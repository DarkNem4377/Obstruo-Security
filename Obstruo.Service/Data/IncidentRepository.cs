using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Obstruo.Service.Data;

/// <summary>
/// Reads and writes the Incidents table. Incidents are opened for bypass
/// attempts (VPN/proxy/DoH evasion domains) — the "threats" the dashboard
/// surfaces, as opposed to routine content blocks.
///
/// MVP grouping rule (from the blueprint): each bypass attempt is its own
/// incident. ponytail: real correlation (group by device + time window) is a
/// later enhancement — <see cref="Create"/> is the single seam it would extend.
/// </summary>
public sealed class IncidentRepository
{
    private readonly ObstruoDatabase _db;
    private readonly ILogger<IncidentRepository> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public IncidentRepository(ObstruoDatabase db, ILogger<IncidentRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ── Pure helpers (unit-tested without a database) ─────────────────────────

    /// <summary>Human-readable incident reference from the row id, e.g. INC-0007.</summary>
    public static string FormatRef(long id) => $"INC-{id:D4}";

    /// <summary>Title for a bypass-attempt incident.</summary>
    public static string BuildBypassTitle(string domain) => $"Bypass attempt blocked: {domain}";

    // ── Write (participates in the caller's transaction) ──────────────────────

    /// <summary>
    /// Opens an incident inside the caller's transaction and returns its id. The
    /// human-readable <c>incident_ref</c> is derived from the row id (INC-nnnn)
    /// in a follow-up update so it is stable and unique.
    /// </summary>
    public long Create(
        SqliteConnection conn, SqliteTransaction tx,
        DateTime openedAt, string severity, string title, string deviceName, string? mitre)
    {
        long id;
        using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO Incidents
                    (incident_ref, opened_at, state, severity, title, device_name, mitre)
                VALUES
                    ($ref, $opened, 'Open', $sev, $title, $device, $mitre);
                """;
            // Temporary unique ref; rewritten to INC-{id} once the row id is known.
            insert.Parameters.AddWithValue("$ref", "PENDING-" + Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("$opened", openedAt.ToString("o"));
            insert.Parameters.AddWithValue("$sev", severity);
            insert.Parameters.AddWithValue("$title", title);
            insert.Parameters.AddWithValue("$device", deviceName);
            insert.Parameters.AddWithValue("$mitre", (object?)mitre ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }

        using (var idCmd = conn.CreateCommand())
        {
            idCmd.Transaction = tx;
            idCmd.CommandText = "SELECT last_insert_rowid();";
            id = (long)idCmd.ExecuteScalar()!;
        }

        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = "UPDATE Incidents SET incident_ref = $ref WHERE id = $id;";
            upd.Parameters.AddWithValue("$ref", FormatRef(id));
            upd.Parameters.AddWithValue("$id", id);
            upd.ExecuteNonQuery();
        }

        return id;
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <summary>Most recent incidents, newest first, as JSON for the UI.</summary>
    public string GetRecentJson(int limit = 100)
    {
        try
        {
            using var conn = new SqliteConnection(_db.ConnectionString);
            conn.Open();

            var incidents = new List<IncidentSnapshot>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT incident_ref, opened_at, closed_at, state, severity, title, device_name, mitre
                FROM   Incidents
                ORDER BY opened_at DESC
                LIMIT  $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                incidents.Add(new IncidentSnapshot(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));

            return JsonSerializer.Serialize(incidents, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read incidents");
            return "[]";
        }
    }

    private sealed record IncidentSnapshot(
        string Ref, string OpenedAt, string? ClosedAt, string State,
        string Severity, string Title, string DeviceName, string? Mitre);
}
