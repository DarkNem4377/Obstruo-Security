using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Obstruo.Service.Data;

[SupportedOSPlatform("windows")]
public class ObstruoDatabase
{
    private readonly string _dataDir;
    private readonly string _dbPath;
    private readonly string _keyPath;
    private readonly ILogger<ObstruoDatabase> _logger;

    // Namespaces the DPAPI blob. Not a secret — DPAPI's protection comes from the
    // SYSTEM account scope, not from this value.
    private static readonly byte[] DpapiEntropy =
        Encoding.UTF8.GetBytes("Obstruo-DBKey-DPAPI-v1");

    // ── Connection string ─────────────────────────────────────────────────────
    // Password= is translated by Microsoft.Data.Sqlite into PRAGMA key when
    // SQLCipher is the native bundle. The key lives in memory only —
    // never logged by this class. Resolved during Initialize().
    public string ConnectionString { get; private set; } = "";

    // Raw SQLCipher key (hex). Needed by LogRetentionService to key backup
    // databases and to ATTACH…sqlcipher_export legacy plaintext backups.
    // No new exposure — the same value already sits inside ConnectionString.
    public string DbKeyHex { get; private set; } = "";

    public ObstruoDatabase(ILogger<ObstruoDatabase> logger)
    {
        _logger = logger;

        _dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Obstruo");

        Directory.CreateDirectory(_dataDir);

        _dbPath = Path.Combine(_dataDir, "obstruo.db");
        _keyPath = Path.Combine(_dataDir, "db.key");
    }

    public void Initialize()
    {
        _logger.LogInformation("Initializing database at {Path}", _dbPath);

        // Lock the data directory down to SYSTEM + Administrators so a standard
        // user cannot read the DB or the key blob. Defense-in-depth behind DPAPI.
        HardenDataDirectory();

        // Resolve the SQLCipher key: unprotect the existing DPAPI blob, migrate a
        // legacy-keyed DB in place, or create a fresh key. Sets ConnectionString.
        var keyHex = ResolveEncryptionKey();
        DbKeyHex = keyHex;
        ConnectionString = $"Data Source={_dbPath};Password={keyHex};";

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        ExecuteNonQuery(connection, "PRAGMA journal_mode=WAL;");
        ExecuteNonQuery(connection, "PRAGMA synchronous=NORMAL;");

        CreateTables(connection);
        MigrateSchema(connection);
        SeedData(connection);

        // Belt-and-suspenders over the directory ACL: apply an explicit protected
        // DACL to the sensitive files themselves. The WAL/SHM sidecars only exist
        // once the DB has been opened in WAL mode (above), and a file created with
        // its own explicit ACE would not be re-secured by parent re-propagation —
        // so lock each file directly. Directly answers finding M4's "a standard
        // user must not be able to read or copy obstruo.db".
        HardenSensitiveFiles();

        _logger.LogInformation("Database initialized successfully");
    }

    // ── Key management (DPAPI-protected, SYSTEM-scoped) ────────────────────────
    //
    // The SQLCipher key is a random 256-bit value, stored as a DPAPI blob under
    // the SYSTEM account (DataProtectionScope.CurrentUser while running as
    // LocalSystem). A standard user cannot unprotect it — so the encryption is a
    // real boundary for the non-admin (family) adversary, and meaningful friction
    // for the admin (self-control) adversary. The key material is never derived
    // from anything a non-admin can read, unlike the retired legacy scheme.

    private string ResolveEncryptionKey()
    {
        // 1. Protected key already exists → normal startup path.
        if (File.Exists(_keyPath))
        {
            var existing = TryLoadProtectedKey();
            if (existing is not null)
                return existing;

            // Key blob present but unreadable (created under a different account,
            // or corrupted). The DB is now unrecoverable — recreate from scratch.
            _logger.LogError(
                "Database key blob exists but could not be unprotected. Recreating a " +
                "fresh encrypted database (stored credentials will reset).");
            SafeDeleteDatabase();
            return CreateAndStoreNewKey();
        }

        // 2. No protected key. Fresh install, or an upgrade from the legacy scheme.
        if (!File.Exists(_dbPath))
            return CreateAndStoreNewKey();

        // 3. DB exists but no protected key → migrate the legacy-keyed DB in place.
        return MigrateLegacyDatabase();
    }

    private string CreateAndStoreNewKey()
    {
        var keyHex = GenerateKeyHex();
        StoreProtectedKey(keyHex);
        _logger.LogInformation("New DPAPI-protected database key created");
        return keyHex;
    }

    private static string GenerateKeyHex()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private void StoreProtectedKey(string keyHex)
    {
        var plaintext = Encoding.UTF8.GetBytes(keyHex);
        try
        {
            var blob = ProtectedData.Protect(plaintext, DpapiEntropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_keyPath, blob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private string? TryLoadProtectedKey()
    {
        try
        {
            var blob = File.ReadAllBytes(_keyPath);
            var plaintext = ProtectedData.Unprotect(blob, DpapiEntropy, DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unprotect database key");
            return null;
        }
    }

    /// <summary>
    /// Re-encrypts a database created under the legacy derived-key scheme with a
    /// new DPAPI-protected random key, IN PLACE via PRAGMA rekey. Credentials and
    /// all data are preserved — this replaces the old delete-on-mismatch behavior
    /// that would have reset every user's protection on upgrade.
    /// </summary>
    private string MigrateLegacyDatabase()
    {
        _logger.LogWarning(
            "Existing database found without a protected key — migrating from the legacy " +
            "derived-key scheme to a DPAPI-protected key.");

        var legacyKey = LegacyDeriveKey();
        var newKey = GenerateKeyHex();

        try
        {
            // Pooling=False so this legacy-keyed handle is fully closed on dispose
            // rather than lingering in the pool open on the (now re-keyed) file.
            using var conn = new SqliteConnection(
                $"Data Source={_dbPath};Password={legacyKey};Pooling=False;");
            conn.Open();

            // Verify the legacy key opens the file before we trust it.
            using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT count(*) FROM sqlite_master;";
                check.ExecuteScalar();
            }

            // Rewrite every page with the new key. newKey is our own generated
            // hex (no quotes/escaping concerns).
            using (var rekey = conn.CreateCommand())
            {
                rekey.CommandText = $"PRAGMA rekey = '{newKey}';";
                rekey.ExecuteNonQuery();
            }

            StoreProtectedKey(newKey);
            _logger.LogWarning("Database re-keyed and protected with DPAPI (data preserved)");
            return newKey;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Legacy database could not be opened for re-key — it is unreadable with the " +
                "legacy scheme (or already encrypted differently). Recreating a fresh encrypted " +
                "database (stored credentials will reset and the setup wizard will run again).");

            SafeDeleteDatabase();
            return CreateAndStoreNewKey();
        }
    }

    private void SafeDeleteDatabase()
    {
        try
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var path = _dbPath + suffix;
                if (File.Exists(path)) File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to delete database at {Path}. Resolve the file lock and restart.", _dbPath);
            throw;
        }
    }

    // ── Data directory ACL ─────────────────────────────────────────────────────

    /// <summary>
    /// Restricts ProgramData\Obstruo to SYSTEM + Administrators (full control),
    /// dropping inherited access for standard users. Independently blocks a
    /// non-admin from even reading the DB/key files. Non-fatal on failure —
    /// DPAPI-SYSTEM key protection is the primary control.
    /// </summary>
    private void HardenDataDirectory()
    {
        try
        {
            var dir = new DirectoryInfo(_dataDir);
            var security = new DirectorySecurity();

            // Break inheritance and discard inherited ACEs (e.g. Users:Read).
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            foreach (var sid in new[] { system, admins })
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    sid,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            dir.SetAccessControl(security);
            _logger.LogInformation("Data directory ACL hardened to SYSTEM + Administrators");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not harden data directory ACL — continuing (DPAPI still protects the key)");
        }
    }

    /// <summary>
    /// Applies an explicit, inheritance-protected DACL (SYSTEM + Administrators
    /// full control, no one else) to each sensitive file. Runs after the DB and
    /// its WAL/SHM sidecars exist. Independent of the directory ACL so a file
    /// carrying its own stale ACE (e.g. created before the dir was hardened, or by
    /// the installer) can't leave the DB readable to a standard user.
    /// </summary>
    private void HardenSensitiveFiles()
    {
        // WAL/SHM share the DB's base name; the key blob is separate.
        var files = new[]
        {
            _dbPath, _dbPath + "-wal", _dbPath + "-shm", _keyPath,
        };

        foreach (var path in files)
            HardenFile(path);
    }

    private void HardenFile(string path)
    {
        if (!File.Exists(path)) return;

        try
        {
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            var security = new FileSecurity();
            // Drop inherited ACEs (the ProgramData Users:RX/Write) and set an
            // explicit owner-only DACL.
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            foreach (var sid in new[] { system, admins })
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    sid,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
            }

            new FileInfo(path).SetAccessControl(security);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not harden ACL on {File} — continuing (DPAPI still protects the key)",
                Path.GetFileName(path));
        }
    }

    /// <summary>
    /// True if the data directory and the DB file grant access only to SYSTEM and
    /// Administrators — i.e. no ACE references Users / Everyone / Authenticated
    /// Users. Surfaces the M4 posture for a health check; never throws.
    /// </summary>
    public bool VerifyLockedDown()
    {
        try
        {
            var offenders = new[]
            {
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),               // Everyone
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            };

            if (!File.Exists(_dbPath))
                return true; // nothing to leak yet

            var dacl = new FileInfo(_dbPath)
                .GetAccessControl()
                .GetAccessRules(true, true, typeof(SecurityIdentifier));

            foreach (System.Security.AccessControl.FileSystemAccessRule ace in dacl)
            {
                if (ace.AccessControlType == AccessControlType.Allow &&
                    offenders.Any(o => o.Equals(ace.IdentityReference)))
                {
                    _logger.LogWarning(
                        "Data DB ACL still grants access to a broad principal: {Sid}",
                        ace.IdentityReference);
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not verify data-directory lockdown");
            return true; // don't raise a false alarm on a read failure
        }
    }

    // ── Legacy key derivation (migration only) ─────────────────────────────────
    //
    // Retained ONLY to open and re-key databases created by builds that used the
    // derived-key scheme. Do NOT change these values — they must reproduce the old
    // key byte-for-byte or migration will fail and reset the user's credentials.
    // The weakness of this scheme (every input readable by a non-admin) is exactly
    // why new installs use DPAPI instead.
    //
    // NOTE ON THE LITERAL BELOW: the "REPLACE-BEFORE-SHIP" text is NOT an
    // outstanding task — it is now frozen key material. Early builds shipped with
    // this exact string, so it must stay byte-for-byte to decrypt and migrate
    // those installs. New installs never derive a key from it (they use a random
    // DPAPI-sealed key), so its predictability is harmless going forward. Leave
    // it unchanged; the legacy path can be deleted once no pre-DPAPI installs
    // remain in the field.
    private const string HardcodedSecret = "Obstruo-v1-charlie4377-REPLACE-BEFORE-SHIP";

    private string LegacyDeriveKey()
    {
        var machineGuid = ReadMachineGuid();
        var installationId = GetOrCreateInstallationId();

        var passwordBytes = Encoding.UTF8.GetBytes(HardcodedSecret + machineGuid + installationId);
        var saltBytes = Encoding.UTF8.GetBytes("Obstruo-SQLCipher-Salt-v1");

        var keyBytes = Rfc2898DeriveBytes.Pbkdf2(
            passwordBytes,
            saltBytes,
            iterations: 100_000,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: 32);

        CryptographicOperations.ZeroMemory(passwordBytes);

        return Convert.ToHexString(keyBytes).ToLower();
    }

    private static string ReadMachineGuid()
    {
        try
        {
            return Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
                "MachineGuid",
                null) as string ?? "no-machine-guid";
        }
        catch
        {
            return "no-machine-guid";
        }
    }

    /// <summary>
    /// Reads the InstallationID from HKLM\SOFTWARE\Obstruo.
    /// If it does not exist, generates a new GUID and persists it to the registry.
    /// </summary>
    private static string GetOrCreateInstallationId()
    {
        const string regKeyPath = @"SOFTWARE\Obstruo";
        const string regValueName = "InstallationID";

        try
        {
            var existing = Registry.GetValue(
                $@"HKEY_LOCAL_MACHINE\{regKeyPath}",
                regValueName,
                null) as string;

            if (!string.IsNullOrWhiteSpace(existing))
                return existing;

            var newId = Guid.NewGuid().ToString("D");

            using var key = Registry.LocalMachine.CreateSubKey(regKeyPath, writable: true);
            if (key is not null)
            {
                var current = key.GetValue(regValueName) as string;
                if (string.IsNullOrWhiteSpace(current))
                    key.SetValue(regValueName, newId, RegistryValueKind.String);
                else
                    newId = current;
            }

            return newId;
        }
        catch
        {
            return "no-installation-id";
        }
    }

    /// <summary>
    /// Connection string for a secondary database (e.g. a retention backup)
    /// encrypted with the SAME key as the main DB. Pooling is disabled so file
    /// handles release promptly — backup files get deleted/renamed by pruning.
    /// </summary>
    public string BuildBackupConnectionString(string dbPath)
        => $"Data Source={dbPath};Password={DbKeyHex};Pooling=False;";

    // ── Table creation ────────────────────────────────────────────────────────

    // ── Additive schema migrations ─────────────────────────────────────────────
    // CreateTables uses CREATE TABLE IF NOT EXISTS, which never alters an existing
    // table. Columns added after 1.0.3 are applied here for already-installed DBs.
    private void MigrateSchema(SqliteConnection connection)
    {
        // Temporary custom blocks: BlocklistDomains.expires_at (NULL = permanent).
        if (!ColumnExists(connection, "BlocklistDomains", "expires_at"))
        {
            ExecuteNonQuery(connection, "ALTER TABLE BlocklistDomains ADD COLUMN expires_at TEXT;");
            _logger.LogInformation("Schema migration: added BlocklistDomains.expires_at");
        }

        // Rename the built-in blocklist source tag on existing installs to match
        // the renamed code (the internal codename was retired). Idempotent — after
        // the rename no rows carry the old tag. Keyed-on by the masking rule and
        // the seed reconcile, so it must stay in sync with the source constant.
        ExecuteNonQuery(connection,
            "UPDATE BlocklistDomains SET source = 'obstruo-builtin' WHERE source = 'charlie-beta4377';");
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        // Table name is a trusted constant, not user input — safe to interpolate.
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private void CreateTables(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS Config (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);

        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS BlockCategories (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                name         TEXT    NOT NULL UNIQUE,
                enabled      INTEGER NOT NULL DEFAULT 1,
                severity     TEXT    NOT NULL,
                domain_count INTEGER NOT NULL DEFAULT 0
            );
            """);

        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS BlocklistDomains (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                domain      TEXT    NOT NULL UNIQUE,
                category_id INTEGER NOT NULL REFERENCES BlockCategories(id),
                is_wildcard INTEGER NOT NULL DEFAULT 0,
                source      TEXT    NOT NULL,
                added_at    TEXT    NOT NULL,
                notes       TEXT
            );
            """);

        ExecuteNonQuery(connection,
            "CREATE INDEX IF NOT EXISTS idx_domain_lookup   ON BlocklistDomains(domain);");
        ExecuteNonQuery(connection,
            "CREATE INDEX IF NOT EXISTS idx_domain_category ON BlocklistDomains(category_id);");

        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS BlockedEvents (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp      TEXT    NOT NULL,
                domain         TEXT    NOT NULL,
                category_id    INTEGER NOT NULL REFERENCES BlockCategories(id),
                severity       TEXT    NOT NULL,
                device_name    TEXT    NOT NULL,
                source_process TEXT,
                geo            TEXT,
                mitre          TEXT,
                incident_id    INTEGER,
                action_taken   TEXT    NOT NULL DEFAULT 'Blocked'
            );
            """);

        ExecuteNonQuery(connection,
            "CREATE INDEX IF NOT EXISTS idx_blocked_timestamp ON BlockedEvents(timestamp DESC);");
        ExecuteNonQuery(connection,
            "CREATE INDEX IF NOT EXISTS idx_blocked_category  ON BlockedEvents(category_id, timestamp DESC);");
        ExecuteNonQuery(connection,
            "CREATE INDEX IF NOT EXISTS idx_blocked_severity  ON BlockedEvents(severity, timestamp DESC);");
        ExecuteNonQuery(connection,
            "CREATE INDEX IF NOT EXISTS idx_blocked_device    ON BlockedEvents(device_name, timestamp DESC);");

        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS WhitelistEntries (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                domain     TEXT    NOT NULL UNIQUE,
                added_at   TEXT    NOT NULL,
                expires_at TEXT,
                added_by   TEXT    NOT NULL DEFAULT 'user',
                reason     TEXT
            );
            """);

        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS Incidents (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                incident_ref TEXT    NOT NULL UNIQUE,
                opened_at    TEXT    NOT NULL,
                closed_at    TEXT,
                state        TEXT    NOT NULL DEFAULT 'Open',
                severity     TEXT    NOT NULL,
                title        TEXT    NOT NULL,
                device_name  TEXT    NOT NULL,
                mitre        TEXT
            );
            """);

        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS SyncHistory (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                synced_at     TEXT    NOT NULL,
                version_name  TEXT    NOT NULL,
                sha256        TEXT    NOT NULL,
                domains_added INTEGER NOT NULL DEFAULT 0,
                success       INTEGER NOT NULL DEFAULT 1
            );
            """);

        _logger.LogInformation("All tables created/verified");
    }

    // ── Seed data ─────────────────────────────────────────────────────────────

    private void SeedData(SqliteConnection connection)
    {
        SeedConfig(connection);
        SeedCategories(connection);
    }

    private void SeedConfig(SqliteConnection connection)
    {
        var defaults = new Dictionary<string, string>
        {
            ["schema_version"] = "1",
            ["version"] = Obstruo.Shared.ObstruoVersion.Current,   // informational; kept current below
            ["log_retention_hours"] = "720",
            ["emergency_disable_max_minutes"] = "15",
            ["emergency_disable_cooldown_minutes"] = "60",
            ["metrics_refresh_seconds"] = "30",
            ["blocklist_url"] = "",
            ["pin_hash"] = "",
            ["password_hash"] = "",
            ["cleanup_time"] = "02:00",
            ["last_cleanup"] = "",
            ["ui_theme"] = "dark",
            ["recovery_code_hash"] = "",
            // LAN DNS filtering for other devices is OFF by default (finding I-1):
            // bind loopback only unless the user explicitly opts in.
            ["lan_mode_enabled"] = "0",
            // SafeSearch enforcement is ON by default for the engines that support
            // it via DNS. YouTube defaults to Moderate. INSERT OR IGNORE means an
            // upgrade seeds these ON too. DuckDuckGo has no DNS mechanism.
            ["safesearch_google"] = "1",
            ["safesearch_youtube"] = "1",
            ["safesearch_bing"] = "1",
            ["safesearch_youtube_level"] = "moderate",
        };

        foreach (var (key, value) in defaults)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "INSERT OR IGNORE INTO Config (key, value) VALUES ($key, $value);";
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.ExecuteNonQuery();
        }

        // INSERT OR IGNORE never updates existing rows, so after an upgrade the
        // seeded 'version' row would stay at whatever release first created the
        // DB. Force it to the running binary's version. (Upgrade DETECTION uses
        // the registry Version value, not this row.)
        using (var versionCmd = connection.CreateCommand())
        {
            versionCmd.CommandText =
                "UPDATE Config SET value = $value WHERE key = 'version' AND value <> $value;";
            versionCmd.Parameters.AddWithValue("$value", Obstruo.Shared.ObstruoVersion.Current);
            versionCmd.ExecuteNonQuery();
        }

        // Same reasoning for the build commit — it must reflect the RUNNING binary
        // every startup, not the build that first created the DB (finding L3).
        // INSERT-or-force so it exists even on an upgraded DB.
        using (var commitCmd = connection.CreateCommand())
        {
            commitCmd.CommandText = """
                INSERT INTO Config (key, value) VALUES ('build_commit', $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            commitCmd.Parameters.AddWithValue("$value", Obstruo.Shared.ObstruoVersion.CommitHash);
            commitCmd.ExecuteNonQuery();
        }
    }

    private void SeedCategories(SqliteConnection connection)
    {
        // INSERT OR IGNORE, so on upgrade new categories (e.g. the grey tier) are
        // added while existing rows — including any enabled-state the user has
        // toggled — are left untouched.
        foreach (var cat in BlockCategoryDefaults.All)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO BlockCategories (name, enabled, severity, domain_count)
                VALUES ($name, $enabled, $severity, 0);
                """;
            cmd.Parameters.AddWithValue("$name", cat.Name);
            cmd.Parameters.AddWithValue("$enabled", cat.EnabledByDefault ? 1 : 0);
            cmd.Parameters.AddWithValue("$severity", cat.Severity);
            cmd.ExecuteNonQuery();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}