using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Obstruo.Service.Dns;
using Obstruo.Shared.Messages;

namespace Obstruo.Service.Data;

/// <summary>
/// Computes dashboard metrics from BlockedEvents.
///
/// All timestamps in the DB are UTC ISO-8601 round-trip strings ("o" format,
/// written by LogEventWriter), so range filters compare lexicographically.
/// "Today" is the local calendar day; hourly bars cover the last 24 hours
/// bucketed by local hour-of-day (matching the chart's 0–23 layout).
///
/// Never throws — on any DB failure it logs and returns an all-zero snapshot
/// so the periodic broadcast and GetMetrics stay alive.
/// </summary>
public sealed class MetricsRepository
{
    private const int TopDomainsLimit = 5;

    private readonly ObstruoDatabase _db;
    private readonly QueryLatencyTracker _latency;
    private readonly ILogger<MetricsRepository> _logger;

    public MetricsRepository(
        ObstruoDatabase db, QueryLatencyTracker latency, ILogger<MetricsRepository> logger)
    {
        _db = db;
        _latency = latency;
        _logger = logger;
    }

    public MetricsUpdateMessage GetCurrentMetrics()
    {
        var nowUtc = DateTime.UtcNow;
        var todayStartUtc = DateTime.Now.Date.ToUniversalTime();
        var weekStartUtc = nowUtc.AddDays(-7);

        var (p50, p95, samples) = _latency.Snapshot();

        try
        {
            using var conn = new SqliteConnection(_db.ConnectionString);
            conn.Open();

            return new MetricsUpdateMessage
            {
                Timestamp = nowUtc.ToString("O"),
                BlocksToday = CountSince(conn, todayStartUtc),
                BlocksWeek = CountSince(conn, weekStartUtc),
                ByCategory = QueryByCategory(conn, todayStartUtc),
                TopDomains = QueryTopDomains(conn, todayStartUtc),
                // Clock-hour chart (0–23). Windowed to the start of the local day
                // so the current-hour bar counts only today — a rolling 24h window
                // would fold the same clock hour from yesterday into it.
                HourlyBars = QueryHourlyBars(conn, todayStartUtc),
                BlockLatencyP50Ms = p50,
                BlockLatencyP95Ms = p95,
                BlockLatencySamples = samples,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute metrics — returning empty snapshot");
            return new MetricsUpdateMessage
            {
                Timestamp = nowUtc.ToString("O"),
                BlocksToday = 0,
                BlocksWeek = 0,
                ByCategory = [],
                TopDomains = [],
                HourlyBars = [],
                BlockLatencyP50Ms = p50,
                BlockLatencyP95Ms = p95,
                BlockLatencySamples = samples,
            };
        }
    }

    private static int CountSince(SqliteConnection conn, DateTime sinceUtc)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM BlockedEvents WHERE timestamp >= $since;";
        cmd.Parameters.AddWithValue("$since", sinceUtc.ToString("o"));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static List<CategoryCount> QueryByCategory(SqliteConnection conn, DateTime sinceUtc)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.name, COUNT(*)
            FROM BlockedEvents e
            JOIN BlockCategories c ON c.id = e.category_id
            WHERE e.timestamp >= $since
            GROUP BY c.name;
            """;
        cmd.Parameters.AddWithValue("$since", sinceUtc.ToString("o"));

        var result = new List<CategoryCount>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new CategoryCount
            {
                Category = reader.GetString(0),
                Count = reader.GetInt32(1),
            });
        }
        return result;
    }

    private static List<DomainHit> QueryTopDomains(SqliteConnection conn, DateTime sinceUtc)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT e.domain, COUNT(*) AS hits, MAX(c.name) AS category
            FROM BlockedEvents e
            JOIN BlockCategories c ON c.id = e.category_id
            WHERE e.timestamp >= $since
            GROUP BY e.domain
            ORDER BY hits DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$since", sinceUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$limit", TopDomainsLimit);

        var result = new List<DomainHit>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new DomainHit
            {
                Domain = reader.GetString(0),
                Hits = reader.GetInt32(1),
                Category = reader.GetString(2),
            });
        }
        return result;
    }

    private static List<HourlyBar> QueryHourlyBars(SqliteConnection conn, DateTime sinceUtc)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT timestamp FROM BlockedEvents WHERE timestamp >= $since;";
        cmd.Parameters.AddWithValue("$since", sinceUtc.ToString("o"));

        var counts = new int[24];
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (DateTime.TryParse(
                    reader.GetString(0), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
            {
                counts[ts.ToLocalTime().Hour]++;
            }
        }

        var result = new List<HourlyBar>(24);
        for (int hour = 0; hour < 24; hour++)
            result.Add(new HourlyBar { Hour = hour, Count = counts[hour] });
        return result;
    }
}
