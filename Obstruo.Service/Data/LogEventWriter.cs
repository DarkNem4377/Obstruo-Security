using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Obstruo.Service.Data;

/// <summary>
/// A single DNS block event ready to be written to BlockedEvents.
/// </summary>
public sealed record BlockedEventRecord(
    DateTime Timestamp,
    string Domain,
    int CategoryId,
    string Severity,
    string DeviceName,
    string? SourceProcess = null,
    string? Geo = null,
    string? Mitre = null,
    string ActionTaken = "Blocked",
    // True for bypass attempts — the writer opens an Incident and links this
    // event to it (incident_id). Routine content blocks do not create incidents.
    bool CreatesIncident = false);

/// <summary>
/// Single dedicated writer for BlockedEvents.
///
/// DNS query handler calls Enqueue() — never blocks, never throws.
/// A background loop drains the queue and batch-inserts every 100ms.
///
/// Backpressure rule (from blueprint):
///   If the queue reaches MaxQueueSize, drop the oldest entry.
///   Losing a log event is acceptable. Blocking the DNS thread is not.
/// </summary>
public sealed class LogEventWriter : IDisposable
{
    private const int MaxQueueSize = 1_000;
    private const int FlushIntervalMs = 100;

    private readonly ObstruoDatabase _db;
    private readonly IncidentRepository _incidents;
    private readonly ILogger<LogEventWriter> _logger;
    private readonly ConcurrentQueue<BlockedEventRecord> _queue = new();

    private CancellationTokenSource? _cts;
    private Task? _writerTask;

    public LogEventWriter(ObstruoDatabase db, IncidentRepository incidents, ILogger<LogEventWriter> logger)
    {
        _db = db;
        _incidents = incidents;
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Enqueue a block event. Non-blocking — safe to call from DNS query thread.
    /// </summary>
    public void Enqueue(BlockedEventRecord record)
    {
        // Drop oldest if at capacity — never block the caller
        if (_queue.Count >= MaxQueueSize)
            _queue.TryDequeue(out _);

        _queue.Enqueue(record);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _writerTask = Task.Run(() => WriterLoopAsync(_cts.Token));
        _logger.LogInformation("LogEventWriter started");
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;

        _cts.Cancel();

        if (_writerTask is not null)
            await _writerTask;

        // Final flush — drain anything that arrived during shutdown
        FlushToDb();

        _logger.LogInformation("LogEventWriter stopped — queue drained");
    }

    // ── Background loop ───────────────────────────────────────────────────────

    private async Task WriterLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(FlushIntervalMs, ct);
                FlushToDb();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path — not an error
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LogEventWriter loop crashed unexpectedly");
        }
    }

    // ── DB write ──────────────────────────────────────────────────────────────

    private void FlushToDb()
    {
        if (_queue.IsEmpty) return;

        // Drain the queue into a local batch
        var batch = new List<BlockedEventRecord>();
        while (_queue.TryDequeue(out var record))
            batch.Add(record);

        if (batch.Count == 0) return;

        try
        {
            using var conn = new SqliteConnection(_db.ConnectionString);
            conn.Open();

            using var tx = conn.BeginTransaction();

            foreach (var r in batch)
            {
                // Bypass attempts open an incident (in this same transaction) and
                // link the event to it. Everything else has a null incident_id.
                long? incidentId = r.CreatesIncident
                    ? _incidents.Create(conn, tx, r.Timestamp, r.Severity,
                        IncidentRepository.BuildBypassTitle(r.Domain), r.DeviceName, r.Mitre)
                    : null;

                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO BlockedEvents
                        (timestamp, domain, category_id, severity, device_name,
                         source_process, geo, mitre, incident_id, action_taken)
                    VALUES
                        ($timestamp, $domain, $categoryId, $severity, $deviceName,
                         $sourceProcess, $geo, $mitre, $incidentId, $actionTaken);
                    """;

                cmd.Parameters.AddWithValue("$timestamp", r.Timestamp.ToString("o"));
                cmd.Parameters.AddWithValue("$domain", r.Domain);
                cmd.Parameters.AddWithValue("$categoryId", r.CategoryId);
                cmd.Parameters.AddWithValue("$severity", r.Severity);
                cmd.Parameters.AddWithValue("$deviceName", r.DeviceName);
                cmd.Parameters.AddWithValue("$sourceProcess", (object?)r.SourceProcess ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$geo", (object?)r.Geo ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$mitre", (object?)r.Mitre ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$incidentId", (object?)incidentId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$actionTaken", r.ActionTaken);

                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            _logger.LogDebug("Flushed {Count} log events to BlockedEvents", batch.Count);
        }
        catch (Exception ex)
        {
            // Log and move on — DNS is unaffected, these entries are lost
            _logger.LogError(ex,
                "Failed to flush {Count} log events to DB — entries discarded", batch.Count);
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}