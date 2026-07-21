using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Obstruo.Service.Dns;

namespace Obstruo.Service.Data;

/// <summary>
/// Seeds the Obstruo built-in domain list into BlocklistDomains — on first run
/// and again (idempotently) whenever SeedListVersion is newer than the DB's
/// stored seed_version, so seed additions reach existing installs on upgrade —
/// then loads all enabled domains into the in-memory DnsBlocklistStore.
///
/// Also provides the blocklist operations behind IPC commands:
///   GetSnapshotJson()      → GetBlocklist
///   AddCustomDomain(...)   → AddDomain   (credential check happens in IpcServer)
///   RemoveCustomDomain(...)→ RemoveDomain (credential check happens in IpcServer)
///   SyncNow()              → SyncBlocklist (credential check happens in IpcServer)
///
/// Sync feed format (HTTPS, JSON):
///   { "versionName": "obstruo-2026-07",
///     "domains": [ { "domain": "example.com", "category": "Adult", "wildcard": false }, … ] }
/// Synced rows carry source='sync' and are fully reconciled on each sync:
/// rows no longer in the feed are removed, new rows added, changed categories
/// updated. Seed (obstruo-builtin) and user (custom) rows are never touched.
/// </summary>
public sealed class BlocklistRepository : IDisposable
{
    // Feeds larger than this are rejected outright — a corrupt or hostile feed
    // must not balloon the DB or memory.
    private const int MaxFeedBytes = 64 * 1024 * 1024;
    private const int MaxFeedDomains = 5_000_000;

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly ObstruoDatabase _db;
    private readonly DnsBlocklistStore _store;
    private readonly ILogger<BlocklistRepository> _logger;

    private Timer? _syncTimer;
    private readonly object _syncLock = new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public BlocklistRepository(
        ObstruoDatabase db,
        DnsBlocklistStore store,
        ILogger<BlocklistRepository> logger)
    {
        _db = db;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Seeds domains if the table is empty, then loads everything into the store.
    /// Call once at service startup after ObstruoDatabase.Initialize().
    /// </summary>
    public void InitializeAndLoad()
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        conn.Open();

        EnsureSeedCurrent(conn);
        LoadIntoStore(conn);
        LoadWhitelistIntoStore(conn);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  IPC OPERATIONS
    // ═══════════════════════════════════════════════════════════════════════════

    // ── Snapshot DTOs (serialized into CommandResponseMessage.Data) ───────────

    private sealed record CategorySnapshot(string Name, int Count);
    private sealed record DomainSnapshot(string Domain, string Category, string Source);
    private sealed record BlocklistSnapshot(
        List<CategorySnapshot> Categories,
        List<DomainSnapshot> Domains);

    /// <summary>Live rule counts per enabled category, largest first — for the
    /// dashboard's passive displays (StatusUpdate), so the UI never shows stale
    /// hardcoded numbers.</summary>
    public Dictionary<string, int> GetCategoryCounts()
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT bc.name, COUNT(bd.id)
            FROM   BlockCategories bc
            LEFT JOIN BlocklistDomains bd ON bd.category_id = bc.id
            WHERE  bc.enabled = 1
            GROUP BY bc.id, bc.name
            ORDER BY COUNT(bd.id) DESC;
            """;
        var counts = new Dictionary<string, int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            counts[reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }

    /// <summary>
    /// Returns the full blocklist as JSON for CommandResponseMessage.Data.
    /// Counts are computed live from BlocklistDomains — not the cached
    /// BlockCategories.domain_count column.
    /// Domains are returned in PLAIN TEXT — masking is a UI display concern.
    /// </summary>
    public string GetSnapshotJson()
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        conn.Open();

        var categories = new List<CategorySnapshot>();
        using (var catCmd = conn.CreateCommand())
        {
            catCmd.CommandText = """
                SELECT bc.name, COUNT(bd.id)
                FROM   BlockCategories bc
                LEFT JOIN BlocklistDomains bd ON bd.category_id = bc.id
                WHERE  bc.enabled = 1
                GROUP BY bc.id, bc.name
                ORDER BY COUNT(bd.id) DESC;
                """;
            using var reader = catCmd.ExecuteReader();
            while (reader.Read())
                categories.Add(new CategorySnapshot(reader.GetString(0), reader.GetInt32(1)));
        }

        var domains = new List<DomainSnapshot>();
        using (var domCmd = conn.CreateCommand())
        {
            domCmd.CommandText = """
                SELECT bd.domain, bc.name, bd.source
                FROM   BlocklistDomains bd
                JOIN   BlockCategories  bc ON bc.id = bd.category_id
                WHERE  bc.enabled = 1
                ORDER BY bd.source DESC, bd.domain ASC;
                """;
            using var reader = domCmd.ExecuteReader();
            while (reader.Read())
                domains.Add(new DomainSnapshot(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return JsonSerializer.Serialize(new BlocklistSnapshot(categories, domains), _jsonOptions);
    }

    private sealed record WhitelistEntrySnapshot(
        string Domain, string AddedAt, string? ExpiresAt, string? Reason);

    /// <summary>
    /// Read model for GetWhitelist: every live (non-expired) allow-list entry
    /// with its added date, optional expiry, and optional reason. Same
    /// credential gate as GetBlocklist — enforced by the IpcServer caller.
    /// </summary>
    public string GetWhitelistSnapshotJson()
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        conn.Open();

        var entries = new List<WhitelistEntrySnapshot>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT domain, added_at, expires_at, reason
                FROM   WhitelistEntries
                WHERE  expires_at IS NULL OR expires_at >= $now
                ORDER BY domain ASC;
                """;
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                entries.Add(new WhitelistEntrySnapshot(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return JsonSerializer.Serialize(entries, _jsonOptions);
    }

    /// <summary>
    /// Deletes whitelist entries whose expiry has passed and returns the domains
    /// removed. Enforcement is already immediate at query time (the in-memory
    /// store ignores expired entries); this cleans the table and lets the caller
    /// notify the user, so temporary exceptions never lapse silently.
    /// ponytail: the in-memory store is not reloaded here — it already drops
    /// expired entries by expiry check, so a stale row costs nothing until the
    /// next full reload on add/sync.
    /// </summary>
    public List<string> SweepExpiredWhitelist()
    {
        var removed = new List<string>();
        try
        {
            using var conn = new SqliteConnection(_db.ConnectionString);
            conn.Open();

            var now = DateTime.UtcNow.ToString("o");
            using (var select = conn.CreateCommand())
            {
                select.CommandText =
                    "SELECT domain FROM WhitelistEntries WHERE expires_at IS NOT NULL AND expires_at < $now;";
                select.Parameters.AddWithValue("$now", now);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                    removed.Add(reader.GetString(0));
            }

            if (removed.Count > 0)
            {
                using var delete = conn.CreateCommand();
                delete.CommandText =
                    "DELETE FROM WhitelistEntries WHERE expires_at IS NOT NULL AND expires_at < $now;";
                delete.Parameters.AddWithValue("$now", now);
                delete.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Whitelist expiry sweep failed");
        }
        return removed;
    }

    /// <summary>
    /// Deletes temporary custom blocks whose expiry has passed and removes them
    /// from the live store, so a time-limited block stops enforcing. Returns the
    /// domains removed. Only 'custom' rows can be temporary — seed rows are never
    /// touched. Symmetric to <see cref="SweepExpiredWhitelist"/>.
    /// </summary>
    public List<string> SweepExpiredCustomBlocks()
    {
        var removed = new List<string>();
        try
        {
            using var conn = new SqliteConnection(_db.ConnectionString);
            conn.Open();

            var now = DateTime.UtcNow.ToString("o");
            using (var select = conn.CreateCommand())
            {
                select.CommandText =
                    "SELECT domain FROM BlocklistDomains " +
                    "WHERE source = 'custom' AND expires_at IS NOT NULL AND expires_at < $now;";
                select.Parameters.AddWithValue("$now", now);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                    removed.Add(reader.GetString(0));
            }

            if (removed.Count > 0)
            {
                using (var delete = conn.CreateCommand())
                {
                    delete.CommandText =
                        "DELETE FROM BlocklistDomains " +
                        "WHERE source = 'custom' AND expires_at IS NOT NULL AND expires_at < $now;";
                    delete.Parameters.AddWithValue("$now", now);
                    delete.ExecuteNonQuery();
                }
                foreach (var domain in removed)
                    _store.RemoveDomain(domain);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Custom-block expiry sweep failed");
        }
        return removed;
    }

    /// <summary>
    /// Adds a user domain with source='custom' and pushes it into the live
    /// in-memory store immediately — the block takes effect on the next DNS query.
    /// Caller (IpcServer) is responsible for credential verification.
    /// </summary>
    public (bool Success, string? Error) AddCustomDomain(
        string rawDomain, string? categoryName, int? expiresMinutes = null)
    {
        var domain = NormalizeDomain(rawDomain);

        if (!IsValidDomain(domain))
            return (false, $"'{rawDomain}' is not a valid domain.");

        try
        {
            using var conn = new SqliteConnection(_db.ConnectionString);
            conn.Open();

            var categories = GetCategoryIds(conn);
            var catName = !string.IsNullOrWhiteSpace(categoryName) && categories.ContainsKey(categoryName)
                ? categoryName
                : "Custom";
            var categoryId = categories[catName];
            var severity = GetCategorySeverity(conn, categoryId);

            // Temporary block: NULL expiry means permanent. A periodic sweep
            // (SweepExpiredCustomBlocks) removes it after expiry, and LoadIntoStore
            // filters it out on restart.
            DateTime? expiresAt = expiresMinutes is > 0
                ? DateTime.UtcNow.AddMinutes(expiresMinutes.Value)
                : null;

            using var tx = conn.BeginTransaction();

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO BlocklistDomains
                        (domain, category_id, is_wildcard, source, added_at, expires_at)
                    VALUES
                        ($domain, $categoryId, $isWildcard, 'custom', $addedAt, $expiresAt);
                    """;
                cmd.Parameters.AddWithValue("$domain", domain);
                cmd.Parameters.AddWithValue("$categoryId", categoryId);
                cmd.Parameters.AddWithValue("$isWildcard", domain.StartsWith("*.") ? 1 : 0);
                cmd.Parameters.AddWithValue("$addedAt", DateTime.UtcNow.ToString("o"));
                cmd.Parameters.AddWithValue("$expiresAt", (object?)expiresAt?.ToString("o") ?? DBNull.Value);

                try
                {
                    cmd.ExecuteNonQuery();
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // UNIQUE constraint
                {
                    return (false, $"'{domain}' is already on the blocklist.");
                }
            }

            RefreshCategoryCounts(conn, tx);
            tx.Commit();

            // Live store — block takes effect immediately, no restart needed.
            // Only push it in if its category is enabled; otherwise the store
            // would disagree with the DB (which LoadIntoStore filters on
            // enabled = 1) until the next full reload.
            if (IsCategoryEnabled(conn, categoryId))
                _store.AddDomain(domain, categoryId, catName, severity);

            _logger.LogInformation("Custom domain added via IPC: {Domain} ({Category})", domain, catName);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddCustomDomain failed for {Domain}", domain);
            return (false, "Database error while adding domain. See service logs.");
        }
    }

    /// <summary>
    /// Removes a domain ONLY if source='custom'. System (obstruo-builtin)
    /// entries are never deletable — enforced here at the data layer, not
    /// just in the UI. Removes from the live store on success.
    /// </summary>
    public (bool Success, string? Error) RemoveCustomDomain(string rawDomain)
    {
        var domain = NormalizeDomain(rawDomain);

        if (string.IsNullOrEmpty(domain))
            return (false, "No domain provided.");

        try
        {
            using var conn = new SqliteConnection(_db.ConnectionString);
            conn.Open();

            using var tx = conn.BeginTransaction();

            int affected;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    DELETE FROM BlocklistDomains
                    WHERE domain = $domain AND source = 'custom';
                    """;
                cmd.Parameters.AddWithValue("$domain", domain);
                affected = cmd.ExecuteNonQuery();
            }

            if (affected == 0)
            {
                tx.Rollback();
                return (false,
                    $"'{domain}' was not removed — it is either not on the list " +
                    "or is a protected system entry.");
            }

            RefreshCategoryCounts(conn, tx);
            tx.Commit();

            _store.RemoveDomain(domain);

            _logger.LogInformation("Custom domain removed via IPC: {Domain}", domain);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RemoveCustomDomain failed for {Domain}", domain);
            return (false, "Database error while removing domain. See service logs.");
        }
    }

    /// <summary>Category name → enabled flag, for the Settings screen.</summary>
    public List<(string Name, bool Enabled)> GetCategoryStates()
    {
        var result = new List<(string, bool)>();
        try
        {
            using var conn = new SqliteConnection(_db.ConnectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name, enabled FROM BlockCategories ORDER BY name;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add((reader.GetString(0), reader.GetInt32(1) == 1));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetCategoryStates failed");
        }
        return result;
    }

    /// <summary>
    /// Enables/disables a whole category and reloads the live store so the
    /// change applies to the next DNS query. Used by UpdateConfig
    /// ("category:&lt;Name&gt;" keys). Caller handles credential verification.
    /// </summary>
    public (bool Success, string? Error) SetCategoryEnabled(string categoryName, bool enabled)
    {
        try
        {
            using var conn = new SqliteConnection(_db.ConnectionString);
            conn.Open();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "UPDATE BlockCategories SET enabled = $enabled WHERE name = $name;";
                cmd.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$name", categoryName);

                if (cmd.ExecuteNonQuery() == 0)
                    return (false, $"Unknown category '{categoryName}'.");
            }

            LoadIntoStore(conn);
            // Re-sweep the allow-list: enabling a category can make an existing
            // whitelist entry conflict with the system blocklist.
            LoadWhitelistIntoStore(conn);

            _logger.LogWarning("Category '{Category}' {State} via IPC",
                categoryName, enabled ? "ENABLED" : "DISABLED");
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SetCategoryEnabled failed for {Category}", categoryName);
            return (false, "Database error while toggling category. See service logs.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  WHITELIST
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Adds an allow-list entry. Whitelisted domains (and their subdomains)
    /// are never blocked, overriding every blocklist rule. Optional expiry in
    /// minutes for temporary exceptions. Takes effect immediately.
    /// Caller (IpcServer) is responsible for credential verification.
    /// </summary>
    public (bool Success, string? Error) AddWhitelistDomain(
        string rawDomain, string? reason = null, int? expiresMinutes = null)
    {
        var domain = NormalizeDomain(rawDomain);

        if (!IsValidDomain(domain) || domain.StartsWith("*."))
            return (false, $"'{rawDomain}' is not a valid domain (wildcards are implicit — " +
                           "a whitelisted domain always covers its subdomains).");

        // Guard: a domain the system blocks can never enter the allow-list.
        // Two probes — (1) would the filter block this exact name (same walk as
        // live queries: parents, wildcards, brand-family), (2) does any blocked
        // entry live beneath it (whitelisting a parent would shelter it).
        var probe = _store.ProbeSystemBlock(domain);
        if (probe.IsBlocked)
            return (false, $"'{domain}' matches the blocklist " +
                           $"({probe.CategoryName} category) and cannot be whitelisted. " +
                           "If this is a custom entry you added, remove it from the " +
                           "blocklist instead.");

        if (_store.HasBlockedDescendant(domain, out var blockedChild))
            return (false, $"'{domain}' cannot be whitelisted: it would also unblock " +
                           $"'{blockedChild}', which is on the blocklist (a whitelisted " +
                           "domain always covers its subdomains).");

        DateTime? expiresAt = expiresMinutes is > 0
            ? DateTime.UtcNow.AddMinutes(expiresMinutes.Value)
            : null;

        try
        {
            using var conn = new SqliteConnection(_db.ConnectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO WhitelistEntries (domain, added_at, expires_at, added_by, reason)
                VALUES ($domain, $addedAt, $expiresAt, 'user', $reason)
                ON CONFLICT(domain) DO UPDATE SET
                    expires_at = excluded.expires_at,
                    reason     = excluded.reason;
                """;
            cmd.Parameters.AddWithValue("$domain", domain);
            cmd.Parameters.AddWithValue("$addedAt", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$expiresAt",
                (object?)expiresAt?.ToString("o") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
            cmd.ExecuteNonQuery();

            _store.AddWhitelistEntry(domain, expiresAt);

            _logger.LogInformation(
                "Whitelist entry added via IPC: {Domain} (expires: {Expires})",
                domain, expiresAt?.ToString("o") ?? "never");
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddWhitelistDomain failed for {Domain}", domain);
            return (false, "Database error while adding whitelist entry. See service logs.");
        }
    }

    public (bool Success, string? Error) RemoveWhitelistDomain(string rawDomain)
    {
        var domain = NormalizeDomain(rawDomain);
        if (string.IsNullOrEmpty(domain))
            return (false, "No domain provided.");

        try
        {
            using var conn = new SqliteConnection(_db.ConnectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM WhitelistEntries WHERE domain = $domain;";
            cmd.Parameters.AddWithValue("$domain", domain);
            var affected = cmd.ExecuteNonQuery();

            if (affected == 0)
                return (false, $"'{domain}' is not on the whitelist.");

            _store.RemoveWhitelistEntry(domain);

            _logger.LogInformation("Whitelist entry removed via IPC: {Domain}", domain);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RemoveWhitelistDomain failed for {Domain}", domain);
            return (false, "Database error while removing whitelist entry. See service logs.");
        }
    }

    /// <summary>
    /// Loads non-expired whitelist entries into the live store and clears out
    /// rows that expired before this startup.
    /// </summary>
    private void LoadWhitelistIntoStore(SqliteConnection conn)
    {
        var nowStr = DateTime.UtcNow.ToString("o");

        using (var purge = conn.CreateCommand())
        {
            purge.CommandText =
                "DELETE FROM WhitelistEntries WHERE expires_at IS NOT NULL AND expires_at < $now;";
            purge.Parameters.AddWithValue("$now", nowStr);
            var purged = purge.ExecuteNonQuery();
            if (purged > 0)
                _logger.LogInformation("Purged {Count} expired whitelist entries", purged);
        }

        var entries = new List<(string, DateTime?)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT domain, expires_at FROM WhitelistEntries;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                DateTime? expiresAt = null;
                if (!reader.IsDBNull(1) &&
                    DateTime.TryParse(
                        reader.GetString(1), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var exp))
                    expiresAt = exp;

                entries.Add((reader.GetString(0), expiresAt));
            }
        }

        // Sweep entries the system blocklist now covers — rows written before
        // the add-time guard existed, or newly caught after a category was
        // enabled. The blocklist always wins over the allow-list; runs after
        // LoadIntoStore so the store's blocklist is current.
        var conflicted = entries
            .Where(e => _store.ProbeSystemBlock(e.Item1).IsBlocked ||
                        _store.HasBlockedDescendant(e.Item1, out _))
            .Select(e => e.Item1)
            .ToList();
        if (conflicted.Count > 0)
        {
            using var drop = conn.CreateCommand();
            drop.CommandText = "DELETE FROM WhitelistEntries WHERE domain = $domain;";
            var p = drop.CreateParameter();
            p.ParameterName = "$domain";
            drop.Parameters.Add(p);
            foreach (var domain in conflicted)
            {
                p.Value = domain;
                drop.ExecuteNonQuery();
                _logger.LogWarning(
                    "Whitelist entry '{Domain}' removed — it matches the system blocklist", domain);
            }
            entries.RemoveAll(e => conflicted.Contains(e.Item1));
        }

        _store.LoadWhitelist(entries);
        _logger.LogInformation("Loaded {Count} whitelist entries into store", entries.Count);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  BLOCKLIST SYNC
    // ═══════════════════════════════════════════════════════════════════════════

    private sealed record FeedDomain(string Domain, string? Category, bool Wildcard);
    private sealed record BlocklistFeed(string? VersionName, List<FeedDomain>? Domains);

    private static readonly JsonSerializerOptions _feedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Starts the daily auto-sync. First attempt runs a few minutes after
    /// startup (the DNS proxy must be up first — the feed host resolves
    /// through it). No-op every tick while blocklist_url is unset.
    /// </summary>
    public void StartAutoSync()
    {
        _syncTimer ??= new Timer(
            _ => AutoSyncTick(), null,
            TimeSpan.FromMinutes(5), TimeSpan.FromHours(24));
        _logger.LogInformation("Blocklist auto-sync scheduled (daily)");
    }

    public void StopAutoSync()
    {
        _syncTimer?.Dispose();
        _syncTimer = null;
    }

    public void Dispose() => StopAutoSync();

    private void AutoSyncTick()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ReadConfigValue("blocklist_url")))
                return;

            var (success, error, added) = SyncNow();
            if (success)
                _logger.LogInformation("Auto-sync complete — {Added} domain change(s)", added);
            else
                _logger.LogWarning("Auto-sync failed: {Error}", error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-sync tick threw");
        }
    }

    /// <summary>
    /// Fetches the configured feed and reconciles the sync-sourced blocklist
    /// rows against it. Returns the net row change. Serialized — concurrent
    /// calls (manual + timer) run one at a time.
    /// Caller (IpcServer) is responsible for credential verification.
    /// </summary>
    public (bool Success, string? Error, int Added) SyncNow()
    {
        lock (_syncLock)
        {
            return SyncNowCore();
        }
    }

    private (bool Success, string? Error, int Added) SyncNowCore()
    {
        var url = ReadConfigValue("blocklist_url");
        if (string.IsNullOrWhiteSpace(url))
            return (false, "No blocklist URL is configured (Config key: blocklist_url).", 0);

        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return (false, "The blocklist URL must use HTTPS.", 0);

        // ── Fetch ─────────────────────────────────────────────────────────────
        // Streamed with a hard byte cap so a hostile or misconfigured feed host
        // cannot make the SYSTEM service allocate unbounded memory. The cap is
        // enforced during the copy, not after a full in-memory download.
        byte[] payload;
        try
        {
            payload = DownloadCapped(url, MaxFeedBytes);
        }
        catch (FeedTooLargeException)
        {
            return (false, "Blocklist feed exceeds the size limit.", 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Blocklist feed fetch failed: {Url}", url);
            return (false, "Could not download the blocklist feed. Check the URL and connectivity.", 0);
        }

        var sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        if (sha256 == GetLastSuccessfulSyncSha())
        {
            _logger.LogInformation("Blocklist feed unchanged (sha256 match) — nothing to do");
            return (true, null, 0);
        }

        // ── Parse + validate ──────────────────────────────────────────────────
        BlocklistFeed? feed;
        try
        {
            feed = JsonSerializer.Deserialize<BlocklistFeed>(payload, _feedJsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Blocklist feed is not valid JSON");
            return (false, "The blocklist feed is not valid JSON.", 0);
        }

        if (feed?.Domains is null || feed.Domains.Count == 0)
            return (false, "The blocklist feed contains no domains.", 0);
        if (feed.Domains.Count > MaxFeedDomains)
            return (false, "Blocklist feed exceeds the domain-count limit.", 0);

        try
        {
            var added = ReconcileSyncedDomains(feed, sha256);
            return (true, null, added);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Blocklist sync failed while applying the feed");
            WriteSyncHistory(feed.VersionName ?? "unknown", sha256, 0, success: false);
            return (false, "Database error while applying the feed. See service logs.", 0);
        }
    }

    private sealed class FeedTooLargeException : Exception;

    /// <summary>
    /// Downloads a URL into memory, aborting as soon as more than
    /// <paramref name="maxBytes"/> have arrived. Rejects early on an oversized
    /// Content-Length header before reading any body.
    /// </summary>
    private static byte[] DownloadCapped(string url, int maxBytes)
    {
        using var response = _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
            .GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is long declared && declared > maxBytes)
            throw new FeedTooLargeException();

        using var stream = response.Content.ReadAsStream();
        using var buffer = new MemoryStream();

        var chunk = new byte[81_920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new FeedTooLargeException();
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private int ReconcileSyncedDomains(BlocklistFeed feed, string sha256)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        conn.Open();

        var categories = GetCategoryIds(conn);
        var now = DateTime.UtcNow.ToString("o");

        int before = CountSyncRows(conn);
        int skipped = 0;

        using (var tx = conn.BeginTransaction())
        {
            // Temp table holding the incoming feed for the removal diff.
            using (var create = conn.CreateCommand())
            {
                create.Transaction = tx;
                create.CommandText = """
                    CREATE TEMP TABLE IF NOT EXISTS sync_feed (domain TEXT PRIMARY KEY);
                    DELETE FROM sync_feed;
                    """;
                create.ExecuteNonQuery();
            }

            foreach (var entry in feed.Domains!)
            {
                var domain = NormalizeDomain(entry.Domain);
                if (entry.Wildcard && !domain.StartsWith("*."))
                    domain = "*." + domain;

                if (!IsValidDomain(domain) ||
                    entry.Category is null ||
                    !categories.TryGetValue(entry.Category, out var categoryId))
                {
                    skipped++;
                    continue;
                }

                using (var mark = conn.CreateCommand())
                {
                    mark.Transaction = tx;
                    mark.CommandText = "INSERT OR IGNORE INTO sync_feed (domain) VALUES ($d);";
                    mark.Parameters.AddWithValue("$d", domain);
                    mark.ExecuteNonQuery();
                }

                // Insert as source='sync'; if the row exists we only update it
                // when it is ALREADY a sync row — seed/custom rows keep priority.
                using var upsert = conn.CreateCommand();
                upsert.Transaction = tx;
                upsert.CommandText = """
                    INSERT INTO BlocklistDomains
                        (domain, category_id, is_wildcard, source, added_at)
                    VALUES ($domain, $categoryId, $isWildcard, 'sync', $addedAt)
                    ON CONFLICT(domain) DO UPDATE SET
                        category_id = excluded.category_id,
                        is_wildcard = excluded.is_wildcard
                    WHERE BlocklistDomains.source = 'sync';
                    """;
                upsert.Parameters.AddWithValue("$domain", domain);
                upsert.Parameters.AddWithValue("$categoryId", categoryId);
                upsert.Parameters.AddWithValue("$isWildcard", domain.StartsWith("*.") ? 1 : 0);
                upsert.Parameters.AddWithValue("$addedAt", now);
                upsert.ExecuteNonQuery();
            }

            // Remove sync rows the feed no longer lists.
            using (var prune = conn.CreateCommand())
            {
                prune.Transaction = tx;
                prune.CommandText = """
                    DELETE FROM BlocklistDomains
                    WHERE source = 'sync'
                      AND domain NOT IN (SELECT domain FROM sync_feed);
                    """;
                prune.ExecuteNonQuery();
            }

            RefreshCategoryCounts(conn, tx);
            tx.Commit();
        }

        int after = CountSyncRows(conn);
        var net = after - before;

        // Reload the live store from the DB so removals take effect too.
        LoadIntoStore(conn);

        WriteSyncHistory(feed.VersionName ?? "unknown", sha256, net, success: true);

        _logger.LogInformation(
            "Blocklist sync applied — version={Version} net={Net} skipped={Skipped} totalSyncRows={Total}",
            feed.VersionName ?? "unknown", net, skipped, after);

        return net;
    }

    private static int CountSyncRows(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM BlocklistDomains WHERE source = 'sync';";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private string? GetLastSuccessfulSyncSha()
    {
        try
        {
            using var conn = new SqliteConnection(_db.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT sha256 FROM SyncHistory
                WHERE success = 1 AND version_name != 'log-retention-cleanup'
                ORDER BY id DESC LIMIT 1;
                """;
            return cmd.ExecuteScalar() as string;
        }
        catch
        {
            return null;
        }
    }

    private void WriteSyncHistory(string versionName, string sha256, int added, bool success)
    {
        try
        {
            using var conn = new SqliteConnection(_db.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO SyncHistory (synced_at, version_name, sha256, domains_added, success)
                VALUES ($at, $version, $sha, $added, $success);
                """;
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$version", versionName);
            cmd.Parameters.AddWithValue("$sha", sha256);
            cmd.Parameters.AddWithValue("$added", added);
            cmd.Parameters.AddWithValue("$success", success ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write SyncHistory record");
        }
    }

    private string? ReadConfigValue(string key)
    {
        try
        {
            using var conn = new SqliteConnection(_db.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM Config WHERE key = $key;";
            cmd.Parameters.AddWithValue("$key", key);
            var result = cmd.ExecuteScalar()?.ToString();
            return string.IsNullOrEmpty(result) ? null : result;
        }
        catch
        {
            return null;
        }
    }

    // ── Validation / helpers ──────────────────────────────────────────────────

    private static string NormalizeDomain(string? raw)
        => (raw ?? string.Empty).Trim().ToLowerInvariant().TrimEnd('.');

    private static bool IsValidDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return false;

        // Allow wildcard prefix — validate the remainder as a hostname
        var host = domain.StartsWith("*.") ? domain[2..] : domain;

        if (!host.Contains('.')) return false; // require at least one dot (no bare TLDs)
        return Uri.CheckHostName(host) == UriHostNameType.Dns;
    }

    private static bool IsCategoryEnabled(SqliteConnection conn, int categoryId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT enabled FROM BlockCategories WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", categoryId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) == 1;
    }

    private static string GetCategorySeverity(SqliteConnection conn, int categoryId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT severity FROM BlockCategories WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", categoryId);
        return cmd.ExecuteScalar()?.ToString() ?? "Med";
    }

    private static void RefreshCategoryCounts(SqliteConnection conn, SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE BlockCategories
            SET domain_count = (
                SELECT COUNT(*) FROM BlocklistDomains
                WHERE category_id = BlockCategories.id
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  SEED + LOAD (unchanged)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Bump whenever the seed gains entries. Installs whose DB stores an older
    /// (or no) seed_version re-run the idempotent seed on startup, so blocklist
    /// additions ship with a release instead of only reaching new DBs.
    /// v4: the full curated list (blocklist-seed.txt, ~5,900 domains) is now the
    /// seed, unioned with the hardcoded curated set.
    /// </summary>
    internal const int SeedListVersion = 4;

    private void EnsureSeedCurrent(SqliteConnection conn)
    {
        int stored = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT value FROM Config WHERE key = 'seed_version';";
            _ = int.TryParse(cmd.ExecuteScalar()?.ToString(), out stored);
        }

        if (stored >= SeedListVersion)
        {
            _logger.LogInformation(
                "Seed list is current (seed_version {Version})", stored);
            return;
        }

        _logger.LogInformation(
            "Seeding Obstruo built-in domain list (seed_version {Stored} → {Current})...",
            stored, SeedListVersion);
        var categories = GetCategoryIds(conn);
        SeedDomains(conn, categories);

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO Config (key, value) VALUES ('seed_version', $v)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            cmd.Parameters.AddWithValue("$v", SeedListVersion.ToString());
            cmd.ExecuteNonQuery();
        }
    }

    private static Dictionary<string, int> GetCategoryIds(SqliteConnection conn)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name FROM BlockCategories;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(1)] = reader.GetInt32(0);
        return result;
    }

    private void SeedDomains(SqliteConnection conn, Dictionary<string, int> cats)
    {
        var now = DateTime.UtcNow.ToString("o");
        int seeded = 0;

        using var tx = conn.BeginTransaction();

        // One prepared statement reused across all ~6,000 rows — creating a fresh
        // command per row made the full-list seed needlessly slow.
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO BlocklistDomains
                    (domain, category_id, is_wildcard, source, added_at)
                VALUES
                    ($domain, $categoryId, $isWildcard, 'obstruo-builtin', $addedAt);
                """;
            var pDomain = cmd.Parameters.Add("$domain", Microsoft.Data.Sqlite.SqliteType.Text);
            var pCat = cmd.Parameters.Add("$categoryId", Microsoft.Data.Sqlite.SqliteType.Integer);
            var pWild = cmd.Parameters.Add("$isWildcard", Microsoft.Data.Sqlite.SqliteType.Integer);
            cmd.Parameters.AddWithValue("$addedAt", now);

            foreach (var (domain, categoryId, isWildcard) in AllSeedEntries(cats))
            {
                pDomain.Value = domain.ToLowerInvariant().Trim();
                pCat.Value = categoryId;
                pWild.Value = isWildcard ? 1 : 0;
                seeded += cmd.ExecuteNonQuery(); // INSERT OR IGNORE → 0 when the row already exists
            }
        }

        // Refresh cached domain counts per category
        using var updateCmd = conn.CreateCommand();
        updateCmd.Transaction = tx;
        updateCmd.CommandText = """
            UPDATE BlockCategories
            SET domain_count = (
                SELECT COUNT(*) FROM BlocklistDomains
                WHERE category_id = BlockCategories.id
            );
            """;
        updateCmd.ExecuteNonQuery();

        tx.Commit();
        _logger.LogInformation("Seeded {Count} new domains (curated list)", seeded);
    }

    private void LoadIntoStore(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT bd.domain, bd.category_id, bd.is_wildcard, bc.name, bc.severity
            FROM   BlocklistDomains bd
            JOIN   BlockCategories  bc ON bc.id = bd.category_id
            WHERE  bc.enabled = 1
              AND  (bd.expires_at IS NULL OR bd.expires_at >= $now);
            """;
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));

        var entries = new List<DomainEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var domain = reader.GetString(0);
            var categoryId = reader.GetInt32(1);
            var isWildcard = reader.GetInt32(2) == 1;
            var categoryName = reader.GetString(3);
            var severity = reader.GetString(4);

            // Wildcard entries are stored as "*.example.com" in the domain column.
            // Pass directly — DnsBlocklistStore.Normalize handles the prefix.
            entries.Add(new DomainEntry(domain, categoryId, categoryName, severity));
        }

        _store.LoadDomains(entries);
        _logger.LogInformation(
            "Loaded {Count} domain rules into blocklist store", entries.Count);
    }

    // ── Obstruo built-in seed data ────────────────────────────────────────────

    internal static IEnumerable<(string Domain, int CategoryId, bool IsWildcard)>
        GetSeedDomains(Dictionary<string, int> cats)
    {
        int adult = cats["Adult"];
        int paid = cats["Paid"];
        int chat = cats["Chat"];
        int aiAdult = cats["AIAdult"];
        int sexChat = cats["SexChat"];
        int games = cats["Games"];

        // ── Adult — free sites ────────────────────────────────────────────
        var adultDomains = new[]
        {
            "analvids.com", "xvideos.com", "xhamster.com", "pornhub.com",
            "porntrex.com", "xnxx.com", "youjizz.com", "pornone.com",
            "theyarehuge.com", "youporn.com", "tnaflix.com", "redtube.com",
            "tube8.com", "perfectgirls.net", "spankbang.com", "handjobhub.com",
            "yespornpleasexxx.com", "swapsmut.com", "seemyporn.com", "yespornplease.com",
            "lobstertube.com", "cliphunter.com", "16honeys.com", "mypornhere.com",
            "pornrewind.com", "anyporn.com", "pornerbros.com", "tubedupe.com",
            "pornoxo.com", "porngem.com", "hclips.com", "beeg.com",
            "hdzog.com", "gotporn.com", "xfreehd.com", "xbabe.com",
            "drtuber.com", "largehdtube.com", "teenpornvideo.xxx", "anysex.com",
            "lesbianpornvideos.com", "megatube.xxx", "foxporns.com", "pornhd.com",
            "porndig.com", "fucd.com", "sxyprn.com", "yourdailypornvideos.com",
            "sexu.com", "xmoviesforyou.com", "fux.com", "luxuretv.com",
            "bravoporn.com", "slutload.com", "xxxymovies.com", "lesbian8.com",
            "nuvid.com", "hqporner.com", "3movs.com", "worldsex.com",
            "letmejerk.com", "letsjerk.to", "veporno.net", "dobbyporn.com",
            "pussyspace.com", "taxi69.com", "tubxporn.com", "pornktube.porn",
            "bobs-tube.com", "bubbaporn.com", "iporntv.net", "spicybigtits.com",
            "tubepornclassic.com", "sextvx.com", "porn00.org", "joysporn.com",
            "go.porn", "plusone8.com", "xxvideoss.org", "kum.com",
            "netpornsex.net", "pornvibe.org", "apetube.com", "pornovideoshub.com",
            "pornrox.com", "pornmaki.com", "watchxxxfreeinhd.com", "rec-tube.com",
            "likuoo.video", "pornky.tv", "pornobae.com", "porn7.xxx",
            "freeomovie.com", "fapality.com", "gonzoxxxmovies.com", "fullxxxmovies.net",
            "porn4days.org", "kowalskypage.com", "tubexclips.com", "porndish.com",
            "porn555.com", "tubsexer.com", "pornhdvideos.net", "mobilepornmovies.com",
            "videolucah.mobi", "pornsexer.com", "hotntubes.com", "porntitan.com",
            "faapy.com", "sexvid.xxx", "pornhd3x.tv", "freeuseporn.com",
            "pornmz.com", "pornhat.com", "ok.xxx", "pornhex.com",
            "xxxfiles.com", "hardcorepost.com", "jizzbunker.com", "dirtytube.com",
            "tube2017.com", "nonktube.com", "imzog.com", "porno800.com",
            "nesaporn.com", "collectionofbestporn.com", "absoluporn.com", "porn300.com",
            "porndroids.com", "freshporno.net", "bustybus.com", "netfapx.com",
            "smutr.com", "familysinners.com", "familystrokes.com", "badmilfs.com",
            "spyfam.com", "family.xxx", "pervmom.com", "familypornhd.com",
            "mysislovesme.com", "stepsiblings.com", "stepsiblingscaught.com", "filthyfamily.com",
            "sexteachermom.com", "brattysis.com", "outofthefamily.com", "daughterswap.com",
            "familylust.com", "momsbangteens.com", "dadcrush.com", "porndoepremium.com",
            "milfzr.com", "familyporn.tv", "tabooflix.com", "tabootube.xxx",
            "mothersontube.com", "familyporntv.com", "motherfuckerxxx.com",
            "motherdaughterexchangeclub.com",

            // seed v2 — leak set from the 2026-07 v1.0.0 block tests
            // (reports 1/4/5; grey-tier policy entries deliberately excluded)
            "4tube.com", "8muses.com", "adultfriendfinder.com", "analdin.com",
            "ancensored.com", "babestube.com", "bellesa.co", "boyfriendtv.com",
            "celebjihad.com", "cityheaven.net", "crazyshit.com", "cumshots.com",
            "daftsex.com", "desixnxx2.net", "dirtypornvids.com", "efukt.com",
            "e-hentai.org", "eporner.com", "erome.com", "escort-advisor.com",
            "fapster.com", "freevideo.cz", "gayboystube.com", "gaymaletube.com",
            "hdsex.org", "heavy-r.com", "hotmovs.com", "iafd.com",
            "ice-gay.com", "iceporn.com", "imagefap.com", "iwank.tv",
            "ixxx.com", "katestube.com", "keezmovies.com", "kompoz.me",
            "literotica.com", "lsl.com", "muchohentai.com", "nudevista.com",
            "oral-amateure.com", "pichunter.com", "planetsuzy.org", "porn.biz",
            "porn.com", "porndoe.com", "pornhub.org", "pornmd.com",
            "pornolab.net", "pornpics.com", "pornq.com", "porzo.com",
            "sankakucomplex.com", "sex.com", "shameless.com", "shooshtime.com",
            "sleazyneasy.com", "softcore69.com", "spankwire.com", "sunporno.com",
            "super.cz", "thefappening.pro", "theporndude.com", "theync.com",
            "thisav.com", "thisvid.com", "thumbzilla.com", "topescortbabes.com",
            "tt1069.com", "tubegalore.com", "tukif.com", "txxx.com",
            "upornia.com", "vercomicsporno.com", "videosdemadurasx.com",
            "videospornogratisx.net", "vikiporn.com", "vipergirls.to", "vjav.com",
            "vkmag.com", "vporn.com", "xhamster.desi", "xhamster9.com",
            "xvidzz.com", "ypmate.com",
        };
        foreach (var d in adultDomains)
            yield return (d, adult, false);

        // ── Paid — subscription adult sites ───────────────────────────────
        var paidDomains = new[]
        {
            "onlyfans.com", "fansly.com", "sheer.com", "brazzers.com",
            "momsteachsex.com", "realitykings.com", "rawrides.tv", "spicevids.com",
            "mofos.com", "bangbros.com", "adulttime.com", "faphouse.com",
            "sweetsinner.com", "nookies.com", "teamskeet.com", "sexyhub.com",
            "filthykings.com", "1adultpassword.com", "newsensations.com", "playboy.tv",
            "xvideos.red", "twistys.com", "gilfaf.com", "babesnetwork.com",
            "mylf.com", "thepovgod.com", "hookuphotshot.com", "21sextreme.com",
            "hustler.com", "nubilefilms.com", "videobox.com", "tiny4k.com",
            "lilhumpers.com", "tugpass.com", "oopsie.com", "bang.com",
            "julesjordan.com", "jayspov.net", "ftvgirls.com", "propertysex.com",
            "passion-hd.com", "cherrypimps.com", "swallowed.com", "dogfartnetwork.com",
            "pornmegaload.com", "pornprosnetwork.com", "ddfnetwork.com", "lubed.com",
            "povd.com", "lethalhardcore.com", "slim4k.com", "littlecaprice-dreams.com",
            "gamelink.com",

            // seed v2 — leak set from the 2026-07 v1.0.0 block tests
            "adultdvdempire.com", "blacked.com", "brazzersnetwork.com",
            "caribbeancom.com", "duga.jp", "evilangel.com", "kink.com",
            "manyvids.com", "naughtyamerica.com", "pornhubpremium.com",
            "r18.com", "xhamsterpremium.com",
        };
        foreach (var d in paidDomains)
            yield return (d, paid, false);

        // ── Chat — live cam sites ─────────────────────────────────────────
        var chatDomains = new[]
        {
            "stripchat.com", "chaturbate.com", "bongacams.com", "livejasmin.com",
            "camsoda.com", "mrporngeeklive.com", "imlive.com", "myfreecams.com",
            "cam4.com", "jerkmate.com", "livesex9.com", "babestation.tv",

            // seed v2 — leak set from the 2026-07 v1.0.0 block tests
            "cam4.es", "cameraprive.com", "jasmin.com", "pornhublive.com",
            "reallifecam.com", "redtubelive.com", "xhamsterlive.com",
        };
        foreach (var d in chatDomains)
            yield return (d, chat, false);

        // ── AIAdult — AI-generated adult content ──────────────────────────
        var aiDomains = new[]
        {
            "gptgirlfriend.online", "candy.ai", "swipey.ai", "clothoff.app",
            "fantasygf.ai", "trynectar.ai", "rolemantic.ai", "porn.ai",
            "x-pictures.io", "seduced.com", "crush.to", "thotchat.ai",
            "aichattings.com", "promptchan.com", "aihentaichat.com", "pornworks.ai",
            "herahaven.ai", "nudify-ai.top", "secretdesires.ai", "animegenius.live3d.io",
            "deepmode.ai", "nsfw.tools", "charfriend.com", "imgnai.com",
            "pornworksai.com", "secrets.ai", "kupid.ai", "tingo.ai",
            "undressai.tools", "soulgen.net", "ehentai.ai", "dreamgf.ai",
        };
        foreach (var d in aiDomains)
            yield return (d, aiAdult, false);

        // ── SexChat — sex chat rooms ──────────────────────────────────────
        var sexChatDomains = new[]
        {
            "pgcams.tv", "rabbitcams.sex", "camfall.com", "sextpanther.com",
            "mydirtyhobby.com", "skyprivate.com", "arousr.com", "slutroulette.com",
            "chatroulette.com", "dirtyroulette.com", "chatrandom.com", "isexychat.com",
            "chatzozo.com", "bestsexcam.com", "omevideo.com",
        };
        foreach (var d in sexChatDomains)
            yield return (d, sexChat, false);

        // ── Games — adult/porn games ──────────────────────────────────────
        var gamesDomains = new[]
        {
            "cityofsin3d.com", "dreamsexworld.com", "sexworld3d.com", "nutaku.net",
            "sexselector.com", "aixxxgames.com", "grandbangauto.com", "my3dgirlfriends.com",
            "adultworld3d.com", "redlightcenter.com", "piratejessica.com", "princessofarda.com",
            "virtuallust3d.com", "hentaisex3d.com", "lust-goddess.com", "3dgirlz.com",
            "townofsins.com", "transpornstarharem.com", "gameoflust2.com", "3dxchat.com",
            "chathouse3d.com", "pornstarharem.com", "bootyheroes.com", "hornyvilla.com",
            "comixharem.com", "lifeselector.com", "dirtyleague.com", "3dsexvilla.com",
            "mnfclub.com", "cuntwars.com", "hentaiheroes.com", "hentaiclicker.com",
            "faptitans.com", "porngames.tv", "gamcore.com", "smutstone.com",
            "adultgamesworld.com", "mysexgames.com", "wetpussygames.com", "adultgamescollector.com",
            "hornygamer.com", "sexyfuckgames.com", "fap-nation.com", "adultgameson.com",
            "comdotgame.com", "naughtymachinima.com", "pinkgames.com", "mycandygames.com",
            "playpornogames.com", "meetandfuckgames.com", "mamba-games.com", "fenoxo.com",
            "playforceone.com", "gamesofdesire.com", "69games.xxx", "adultgames.to",
            "onlyhgames.com", "summertimesaga.com", "dikgames.com",
        };
        foreach (var d in gamesDomains)
            yield return (d, games, false);

        // ── Grey tier (M6) — seeded into OFF-by-default policy categories ──────
        // These leaked only relative to the tester's broad list; they are
        // adult-adjacent or pure policy calls, so they ship as opt-in toggles
        // rather than hard blocks. LoadIntoStore filters on category.enabled, so
        // they do nothing until the user turns the category on. Guarded by
        // TryGetValue so a partial category set (e.g. in a unit test) is tolerated.
        foreach (var (domain, category) in GreyTier)
        {
            if (cats.TryGetValue(category, out var catId))
                yield return (domain, catId, false);
        }
    }

    /// <summary>
    /// Grey-tier domain → opt-in category. Also the exclusion set for the bulk
    /// embedded import (the curated list files these under Adult, which would
    /// make them hard blocks and undo the opt-in tier).
    /// </summary>
    private static readonly (string Domain, string Category)[] GreyTier =
    [
        ("okcupid.com",        "Dating"),
        ("joyclub.de",         "Dating"),
        ("4chan.org",          "Forums"),
        ("urbandictionary.com","SoftContent"),
        ("cosmopolitan.com",   "SoftContent"),
        ("videa.hu",           "SoftContent"),
        ("girlsgogames.com",   "CasualGames"),
        ("dmm.com",            "JPStore"),
        ("dlsite.com",         "JPStore"),
    ];

    private static readonly HashSet<string> GreyTierDomains =
        GreyTier.Select(g => g.Domain).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // ── Curated list (embedded resource) ──────────────────────────────────────

    /// <summary>
    /// The full seed = the hardcoded curated set (broad category coverage,
    /// including the modern AI-porn / game domains the embedded list lacks)
    /// UNIONed with the embedded curated list (~5,900 domains, mostly Adult).
    /// The hardcoded set is yielded first so its category wins on any overlap;
    /// INSERT OR IGNORE dedupes the rest. Grey-tier domains are only ever yielded
    /// under their opt-in category, never from the bulk Adult import.
    /// </summary>
    internal static IEnumerable<(string Domain, int CategoryId, bool IsWildcard)>
        AllSeedEntries(Dictionary<string, int> cats)
    {
        foreach (var entry in GetSeedDomains(cats))
            yield return entry;

        foreach (var (domain, category) in LoadEmbeddedSeed())
        {
            if (GreyTierDomains.Contains(domain)) continue;      // stays opt-in
            if (cats.TryGetValue(category, out var catId))
                yield return (domain, catId, false);
        }
    }

    /// <summary>
    /// Streams (domain, categoryName) from the embedded blocklist-seed.txt.
    /// Lines are bare domains; category comes from the nearest preceding header
    /// of the form "# ── Adult (5870) ──…". Unknown/headerless lines are skipped.
    /// </summary>
    internal static IEnumerable<(string Domain, string Category)> LoadEmbeddedSeed()
    {
        var asm = typeof(BlocklistRepository).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("blocklist-seed.txt", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null) yield break;

        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null) yield break;
        using var reader = new StreamReader(stream);

        string? category = null;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('#'))
            {
                var m = Regex.Match(line, @"([A-Za-z]+)\s*\(\d+\)");
                if (m.Success) category = m.Groups[1].Value;
                continue;
            }

            if (category is not null)
                yield return (line.ToLowerInvariant(), category);
        }
    }
}