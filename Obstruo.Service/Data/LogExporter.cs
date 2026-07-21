using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Obstruo.Service.Data;

/// <summary>
/// Exports the activity log (BlockedEvents) to CSV or JSON at a caller-chosen
/// path. The service writes the file directly because a full export easily
/// exceeds the 64 KB IPC message cap — the UI only supplies the path and format.
/// Invoked by the credential-gated ExportLogs command, so the same gate as
/// GetBlocklist protects this sensitive history.
/// </summary>
public sealed class LogExporter
{
    private readonly ObstruoDatabase _db;
    private readonly ILogger<LogExporter> _logger;

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public LogExporter(ObstruoDatabase db, ILogger<LogExporter> logger)
    {
        _db = db;
        _logger = logger;
    }

    public sealed record LogRow(
        string Timestamp, string Domain, string Category,
        string Severity, string Device, string? Mitre, string Action);

    /// <summary>
    /// Escapes a field per RFC 4180: wrap in quotes if it contains a comma,
    /// quote, CR or LF, doubling any embedded quote. Pure — unit-tested.
    /// </summary>
    public static string EscapeCsv(string? field)
    {
        field ??= "";
        if (field.IndexOfAny([',', '"', '\n', '\r']) < 0) return field;
        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>
    /// Writes the last <paramref name="days"/> days of blocked events to
    /// <paramref name="path"/> in "csv" or "json". Returns the row count.
    /// Throws on I/O failure — the caller turns that into a command error.
    /// </summary>
    public int ExportToFile(string path, string format, int days)
    {
        var rows = ReadRows(days);

        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(path, JsonSerializer.Serialize(rows, _json), Encoding.UTF8);
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine("timestamp,domain,category,severity,device,mitre,action");
            foreach (var r in rows)
                sb.AppendLine(string.Join(',',
                    EscapeCsv(r.Timestamp), EscapeCsv(r.Domain), EscapeCsv(r.Category),
                    EscapeCsv(r.Severity), EscapeCsv(r.Device), EscapeCsv(r.Mitre), EscapeCsv(r.Action)));
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        _logger.LogInformation("Exported {Rows} log rows to {Path} ({Format})", rows.Count, path, format);
        return rows.Count;
    }

    private List<LogRow> ReadRows(int days)
    {
        var rows = new List<LogRow>();
        using var conn = new SqliteConnection(_db.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT e.timestamp, e.domain, c.name, e.severity, e.device_name, e.mitre, e.action_taken
            FROM   BlockedEvents e
            JOIN   BlockCategories c ON e.category_id = c.id
            WHERE  e.timestamp >= $since
            ORDER BY e.timestamp DESC;
            """;
        cmd.Parameters.AddWithValue("$since", DateTime.UtcNow.AddDays(-days).ToString("o"));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new LogRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6)));
        return rows;
    }
}
