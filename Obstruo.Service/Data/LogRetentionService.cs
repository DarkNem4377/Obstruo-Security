using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Obstruo.Service.Data;

/// <summary>
/// Runs a daily cleanup of BlockedEvents based on the user-configured retention period.
/// Cleanup flow:
///   1. Export records older than retention window to backup DB.
///   2. Delete exported records from main DB.
///   3. Remove records from backup DB beyond double the retention window,
///      and delete whole backup files older than that window.
///   4. Write result to SyncHistory.
/// Runs on a dedicated background thread. Never touches DNS.
///
/// Backups are SQLCipher-encrypted with the SAME key as the main database —
/// the browsing history must never be weaker-protected in the backup than in
/// the source. Plaintext backups written by older builds are encrypted in
/// place (ATTACH + sqlcipher_export) the first time they are touched.
/// </summary>
public sealed class LogRetentionService : IDisposable
{
    private readonly ObstruoDatabase _db;
    private readonly ILogger<LogRetentionService> _logger;

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private bool _started;
    private bool _disposed;

    private static readonly string BackupDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Obstruo", "backups");

    public LogRetentionService(
        ObstruoDatabase db,
        ILogger<LogRetentionService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void Start()
    {
        if (_started) return;
        _started = true;

        Directory.CreateDirectory(BackupDir);

        _cts = new CancellationTokenSource();

        _thread = new Thread(() => RunLoop(_cts.Token))
        {
            Name = "Obstruo-LogRetention",
            IsBackground = true,
            Priority = ThreadPriority.Lowest
        };

        _thread.Start();
        _logger.LogInformation("Log retention service started");
    }

    public void Stop()
    {
        if (!_started || _disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _logger.LogInformation("Log retention service stopped");
    }

    // ── Main loop ─────────────────────────────────────────────────────────────

    private void RunLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var waitMs = GetMsUntilNextCleanup();
                _logger.LogInformation(
                    "Next log cleanup in {Minutes} minutes", waitMs / 60000);

                Task.Delay((int)waitMs, token).Wait(token);

                if (!token.IsCancellationRequested)
                    RunCleanup();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Log retention cycle threw unexpectedly — will retry next scheduled time");
            }
        }
    }

    // ── Cleanup logic ─────────────────────────────────────────────────────────

    private void RunCleanup()
    {
        _logger.LogInformation("Log retention cleanup starting");

        var retentionHours = GetRetentionHours();
        var cutoff = DateTime.UtcNow.AddHours(-retentionHours);
        var backupCutoff = DateTime.UtcNow.AddHours(-retentionHours * 2);
        var backupPath = Path.Combine(BackupDir, $"backup_{DateTime.UtcNow:yyyy-MM-dd}.db");
        var backupConnStr = _db.BuildBackupConnectionString(backupPath);

        int exported = 0;
        int deleted = 0;
        int pruned = 0;

        try
        {
            EnsureBackupEncrypted(backupPath, backupConnStr);
            exported = ExportToBackup(cutoff, backupConnStr);
            deleted = DeleteFromMain(cutoff);
            pruned = PruneBackup(backupCutoff, backupPath, backupConnStr);
            pruned += PruneOldBackupFiles(backupCutoff);
            WriteHistory(exported, deleted, pruned);
            UpdateLastCleanup();

            _logger.LogInformation(
                "Log retention cleanup complete — exported={Exported} deleted={Deleted} pruned={Pruned}",
                exported, deleted, pruned);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Log retention cleanup failed");
        }
    }

    private int ExportToBackup(DateTime cutoff, string backupConnStr)
    {
        var cutoffStr = cutoff.ToString("o");
        var count = 0;

        EnsureBackupSchema(backupConnStr);

        using var mainConn = new SqliteConnection(_db.ConnectionString);
        using var backupConn = new SqliteConnection(backupConnStr);
        mainConn.Open();
        backupConn.Open();

        using var selectCmd = mainConn.CreateCommand();
        selectCmd.CommandText = """
            SELECT id, timestamp, domain, category_id, severity,
                   device_name, source_process, geo, mitre, incident_id, action_taken
            FROM BlockedEvents
            WHERE timestamp < $cutoff
            ORDER BY timestamp ASC;
            """;
        selectCmd.Parameters.AddWithValue("$cutoff", cutoffStr);

        using var reader = selectCmd.ExecuteReader();
        using var tx = backupConn.BeginTransaction();

        while (reader.Read())
        {
            using var insertCmd = backupConn.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT OR IGNORE INTO BlockedEvents
                    (id, timestamp, domain, category_id, severity,
                     device_name, source_process, geo, mitre, incident_id, action_taken)
                VALUES
                    ($id, $timestamp, $domain, $category_id, $severity,
                     $device_name, $source_process, $geo, $mitre, $incident_id, $action_taken);
                """;

            insertCmd.Parameters.AddWithValue("$id", reader.GetInt64(0));
            insertCmd.Parameters.AddWithValue("$timestamp", reader.GetString(1));
            insertCmd.Parameters.AddWithValue("$domain", reader.GetString(2));
            insertCmd.Parameters.AddWithValue("$category_id", reader.GetInt64(3));
            insertCmd.Parameters.AddWithValue("$severity", reader.GetString(4));
            insertCmd.Parameters.AddWithValue("$device_name", reader.GetString(5));
            insertCmd.Parameters.AddWithValue("$source_process", reader.IsDBNull(6) ? DBNull.Value : reader.GetString(6));
            insertCmd.Parameters.AddWithValue("$geo", reader.IsDBNull(7) ? DBNull.Value : reader.GetString(7));
            insertCmd.Parameters.AddWithValue("$mitre", reader.IsDBNull(8) ? DBNull.Value : reader.GetString(8));
            insertCmd.Parameters.AddWithValue("$incident_id", reader.IsDBNull(9) ? DBNull.Value : reader.GetInt64(9));
            insertCmd.Parameters.AddWithValue("$action_taken", reader.GetString(10));
            insertCmd.ExecuteNonQuery();
            count++;
        }

        tx.Commit();
        _logger.LogInformation("Exported {Count} records to backup", count);
        return count;
    }

    private int DeleteFromMain(DateTime cutoff)
    {
        var cutoffStr = cutoff.ToString("o");

        using var conn = new SqliteConnection(_db.ConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM BlockedEvents WHERE timestamp < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", cutoffStr);

        var deleted = cmd.ExecuteNonQuery();
        _logger.LogInformation("Deleted {Count} records from main DB", deleted);
        return deleted;
    }

    private int PruneBackup(DateTime backupCutoff, string backupPath, string backupConnStr)
    {
        if (!File.Exists(backupPath))
            return 0;

        var cutoffStr = backupCutoff.ToString("o");

        using var conn = new SqliteConnection(backupConnStr);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM BlockedEvents WHERE timestamp < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", cutoffStr);

        var pruned = cmd.ExecuteNonQuery();
        _logger.LogInformation("Pruned {Count} records from backup DB", pruned);
        return pruned;
    }

    /// <summary>
    /// Deletes whole backup files whose date (from the backup_yyyy-MM-dd.db
    /// filename) is older than the double-retention window. Without this,
    /// one file per cleanup day accumulates forever.
    /// </summary>
    private int PruneOldBackupFiles(DateTime backupCutoff)
    {
        var removed = 0;

        foreach (var file in Directory.GetFiles(BackupDir, "backup_*.db"))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            var datePart = stem["backup_".Length..];

            if (!DateTime.TryParseExact(
                    datePart, "yyyy-MM-dd", null,
                    System.Globalization.DateTimeStyles.AssumeUniversal |
                    System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var fileDate))
                continue;

            // The file only holds records exported on that date, which are all
            // older than the retention window already — so once the file date
            // passes the double window, everything inside is expired.
            if (fileDate >= backupCutoff.Date)
                continue;

            try
            {
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    var p = file + suffix;
                    if (File.Exists(p)) File.Delete(p);
                }
                removed++;
                _logger.LogInformation("Deleted expired backup file {File}", file);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete expired backup file {File}", file);
            }
        }

        return removed;
    }

    // ── Backup encryption ─────────────────────────────────────────────────────

    /// <summary>
    /// Guarantees the backup file at <paramref name="backupPath"/> is readable
    /// with the main DB key before cleanup writes to it. Three cases:
    ///   - opens with the key (or doesn't exist yet) → nothing to do;
    ///   - opens WITHOUT a key → plaintext backup from an older build →
    ///     encrypt it in place via ATTACH + sqlcipher_export;
    ///   - opens neither way → corrupt/foreign → moved aside so cleanup
    ///     can start a fresh encrypted file.
    /// </summary>
    private void EnsureBackupEncrypted(string backupPath, string backupConnStr)
    {
        if (!File.Exists(backupPath)) return;
        if (CanOpen(backupConnStr)) return;

        var plainConnStr = $"Data Source={backupPath};Pooling=False;";
        if (CanOpen(plainConnStr))
        {
            _logger.LogWarning(
                "Backup {File} is plaintext (pre-encryption build) — encrypting in place",
                backupPath);
            EncryptLegacyBackup(backupPath, plainConnStr);
            return;
        }

        var quarantine = backupPath + ".corrupt";
        _logger.LogError(
            "Backup {File} is unreadable with and without the key — moving to {Quarantine}",
            backupPath, quarantine);
        if (File.Exists(quarantine)) File.Delete(quarantine);
        File.Move(backupPath, quarantine);
    }

    private static bool CanOpen(string connStr)
    {
        try
        {
            using var conn = new SqliteConnection(connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master;";
            cmd.ExecuteScalar();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Rewrites a plaintext SQLite backup as a SQLCipher database keyed with
    /// the main DB key: export into a temp encrypted file, then swap it in.
    /// The plaintext original is deleted only after the export succeeds.
    /// </summary>
    private void EncryptLegacyBackup(string backupPath, string plainConnStr)
    {
        var tempPath = backupPath + ".enc";
        if (File.Exists(tempPath)) File.Delete(tempPath);

        using (var conn = new SqliteConnection(plainConnStr))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            // Key and path are service-generated (hex key, date-stamped path
            // under ProgramData) — no untrusted input reaches this SQL.
            cmd.CommandText = $"""
                ATTACH DATABASE '{tempPath.Replace("'", "''")}' AS enc KEY '{_db.DbKeyHex}';
                SELECT sqlcipher_export('enc');
                DETACH DATABASE enc;
                """;
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        File.Delete(backupPath);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var p = backupPath + suffix;
            if (File.Exists(p)) File.Delete(p);
        }
        File.Move(tempPath, backupPath);

        _logger.LogInformation("Legacy plaintext backup encrypted: {File}", backupPath);
    }

    private void WriteHistory(int exported, int deleted, int pruned)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO SyncHistory (synced_at, version_name, sha256, domains_added, success)
            VALUES ($synced_at, $version_name, $sha256, $domains_added, $success);
            """;

        cmd.Parameters.AddWithValue("$synced_at", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$version_name", "log-retention-cleanup");
        cmd.Parameters.AddWithValue("$sha256", $"exported={exported};deleted={deleted};pruned={pruned}");
        cmd.Parameters.AddWithValue("$domains_added", 0);
        cmd.Parameters.AddWithValue("$success", 1);
        cmd.ExecuteNonQuery();
    }

    private void UpdateLastCleanup()
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Config (key, value) VALUES ('last_cleanup', $value)
            ON CONFLICT(key) DO UPDATE SET value = $value;
            """;
        cmd.Parameters.AddWithValue("$value", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private void EnsureBackupSchema(string backupConnStr)
    {
        using var conn = new SqliteConnection(backupConnStr);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;

            CREATE TABLE IF NOT EXISTS BlockedEvents (
                id             INTEGER PRIMARY KEY,
                timestamp      TEXT    NOT NULL,
                domain         TEXT    NOT NULL,
                category_id    INTEGER NOT NULL,
                severity       TEXT    NOT NULL,
                device_name    TEXT    NOT NULL,
                source_process TEXT,
                geo            TEXT,
                mitre          TEXT,
                incident_id    INTEGER,
                action_taken   TEXT    NOT NULL DEFAULT 'Blocked'
            );

            CREATE INDEX IF NOT EXISTS idx_backup_timestamp ON BlockedEvents(timestamp DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    // ── Config helpers ────────────────────────────────────────────────────────

    private int GetRetentionHours()
    {
        try
        {
            using var conn = new SqliteConnection(_db.ConnectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM Config WHERE key = 'log_retention_hours';";
            var val = cmd.ExecuteScalar() as string;

            if (int.TryParse(val, out var hours) && hours > 0)
                return hours;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read retention hours — defaulting to 720 (30 days)");
        }

        return 720;
    }

    private long GetMsUntilNextCleanup()
    {
        try
        {
            using var conn = new SqliteConnection(_db.ConnectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM Config WHERE key = 'cleanup_time';";
            var val = cmd.ExecuteScalar() as string ?? "02:00";

            if (!TimeSpan.TryParse(val, out var cleanupTime))
                cleanupTime = new TimeSpan(2, 0, 0);

            var now = DateTime.Now;
            var next = DateTime.Today.Add(cleanupTime);

            if (next <= now)
                next = next.AddDays(1);

            return (long)(next - now).TotalMilliseconds;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read cleanup time — defaulting to 24h from now");
            return (long)TimeSpan.FromHours(24).TotalMilliseconds;
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}