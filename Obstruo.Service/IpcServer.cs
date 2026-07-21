using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Obstruo.Service.Data;
using Obstruo.Shared.Contracts;
using Obstruo.Shared.Enums;
using Obstruo.Shared.Messages;

namespace Obstruo.Service;

/// <summary>
/// Named pipe server. Accepts UI client connections and provides:
///   - Outbound broadcast: LogEvent, StatusUpdate, MetricsUpdate, Alert, Heartbeat
///   - Inbound commands: CommandMessage → CommandResponseMessage
///
/// Architecture:
///   - One accept loop task continuously listens for new connections.
///   - Each connected client gets two tasks: a read loop and a write loop.
///   - Outbound messages go into a bounded Channel per client (backpressure = drop oldest).
///   - The DNS engine never blocks waiting for IPC — fire and forget into the channel.
///
/// Security:
///   - PipeSecurity ACL restricts connections to Administrators + SYSTEM (FullControl)
///     and AuthenticatedUsers (ReadWrite). AuthenticatedUsers is required because the UI
///     runs non-elevated (asInvoker) — UAC split tokens cause the Administrators SID to
///     be marked deny-only on non-elevated tokens, so an Administrators-only ACL would
///     silently block the UI from connecting.
///   - Because ANY authenticated local process can therefore connect, every mutating
///     command MUST carry a credential (PIN or password) that this class verifies
///     with BCrypt against the stored hash before executing. Read-only commands
///     (GetStatus, GetMetrics, GetBlocklist, GetSetupState) require no credential.
///   - Auth refactor B: the UI never touches the database. All credential storage
///     (SetCredential), verification (VerifyCredential), and recovery
///     (PerformRecovery) happen here. SetCredential is unauthenticated ONLY while
///     setup is incomplete (first-run bootstrap); once configured, it requires a
///     valid credential.
///   - PerformRecovery is atomic: verify recovery code + clear all credentials in
///     one command, one transaction. Recovery guesses share the same escalating
///     lockout as PIN/password guesses.
/// </summary>
public sealed class IpcServer
{
    private const string PipeName = "ObstruoSecurityPipe";
    private const int OutboundQueueCapacity = 500;
    private const int HeartbeatIntervalMs = 5_000;

    // Hard cap on a single inbound command line. Any authenticated local process
    // can connect, so an unbounded ReadLineAsync would let one of them exhaust
    // service memory by never sending a newline. Real commands are well under 4 KB.
    private const int MaxCommandChars = 64 * 1024;

    // Default cadence for MetricsUpdate broadcasts; overridden by the
    // metrics_refresh_seconds Config value at Start().
    private const int DefaultMetricsIntervalSeconds = 30;

    private readonly ILogger<IpcServer> _logger;
    private readonly ObstruoDatabase _db;
    private readonly BlocklistRepository _blocklist;
    private readonly MetricsRepository _metrics;
    private readonly UninstallService _uninstall;
    private readonly Obstruo.Service.Dns.SafeSearchRewriter _safeSearch;
    private readonly IncidentRepository _incidents;
    private readonly LogExporter _exporter;
    private readonly ConcurrentDictionary<Guid, ConnectedClient> _clients = new();

    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private Timer? _heartbeatTimer;
    private Timer? _metricsTimer;
    private bool _stopped;

    // State tracked for heartbeats and status updates
    private long _blockCountTotal;
    private ProtectionState _protectionState = ProtectionState.Active;
    private DateTime _startedAt = DateTime.UtcNow;

    // ── Emergency pause ────────────────────────────────────────────────────────
    // While UtcNow < _pausedUntil the DNS proxy skips blocklist checks.
    // Deliberately NOT persisted: a service restart resumes protection
    // (fail-closed). The cooldown IS persisted (Config: emergency_cooldown_until)
    // so restarting the service can't be used to chain pauses back-to-back.
    private DateTime _pausedUntil = DateTime.MinValue;

    // ── IPC-side credential failure lockout ───────────────────────────────────
    // One pipe-wide policy shared by ALL credential checks (VerifyCredential,
    // PerformRecovery, AddDomain, RemoveDomain, post-setup SetCredential).
    // Protects against a local process brute-forcing the PIN or recovery code.
    // 3 wrong → lockout, odd-minute escalation (1, 3, 5, ...). Resets on success.
    //
    // DELIBERATE TRADE-OFF: the counter is pipe-wide, so a hostile local process
    // can lock the legitimate parent out by spamming wrong PINs (local DoS).
    // Accepted because the alternative — per-connection counters — is trivially
    // reset by reconnecting, which would make the brute-force protection
    // worthless. Availability loses to confidentiality here.
    private readonly object _authLock = new();
    private volatile bool _upstreamHealthy = true;
    private int _failedAttempts;
    private int _lockoutCount;
    private DateTime _lockedUntil = DateTime.MinValue;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public IpcServer(
        ILogger<IpcServer> logger,
        ObstruoDatabase db,
        BlocklistRepository blocklist,
        MetricsRepository metrics,
        UninstallService uninstall,
        Obstruo.Service.Dns.SafeSearchRewriter safeSearch,
        IncidentRepository incidents,
        LogExporter exporter)
    {
        _logger = logger;
        _db = db;
        _blocklist = blocklist;
        _metrics = metrics;
        _uninstall = uninstall;
        _safeSearch = safeSearch;
        _incidents = incidents;
        _exporter = exporter;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  LIFECYCLE
    // ═══════════════════════════════════════════════════════════════════════════

    public void Start()
    {
        if (_cts is not null) return;

        _stopped = false;
        _startedAt = DateTime.UtcNow;
        _cts = new CancellationTokenSource();

        // Restore any persisted lockout so a restart doesn't reset brute-force state.
        LoadLockoutState();

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _heartbeatTimer = new Timer(OnHeartbeatTick, null, HeartbeatIntervalMs, HeartbeatIntervalMs);

        var metricsIntervalMs = ReadMetricsIntervalSeconds() * 1_000;
        _metricsTimer = new Timer(OnMetricsTick, null, metricsIntervalMs, metricsIntervalMs);

        _logger.LogInformation("IPC server started on pipe: {PipeName}", PipeName);
    }

    private int ReadMetricsIntervalSeconds()
    {
        if (int.TryParse(ReadConfigValue("metrics_refresh_seconds"), out var s))
            return Math.Clamp(s, 5, 3_600);
        return DefaultMetricsIntervalSeconds;
    }

    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;

        _heartbeatTimer?.Dispose();
        _metricsTimer?.Dispose();
        _cts?.Cancel();

        foreach (var client in _clients.Values)
            client.Dispose();
        _clients.Clear();

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* expected on cancellation */ }
        }

        _cts?.Dispose();
        _cts = null;

        _logger.LogInformation("IPC server stopped");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  STATE UPDATES  (called by DnsProxyService, TamperDetector, Worker, etc.)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Called by DnsProxyService on every block. Increments the running total.</summary>
    public void IncrementBlockCount()
        => Interlocked.Increment(ref _blockCountTotal);

    /// <summary>
    /// True while an emergency pause is active. Checked by DnsProxyService on
    /// every query — expires on its own once the pause window passes, so even
    /// if the service can't broadcast the state change, filtering resumes.
    /// </summary>
    public bool IsProtectionPaused => DateTime.UtcNow < _pausedUntil;

    /// <summary>Called by Worker when protection state changes. Broadcasts a StatusUpdate immediately.</summary>
    public void SetProtectionState(ProtectionState state)
    {
        _protectionState = state;
        Broadcast(BuildStatus());
    }

    /// <summary>Called by DnsProxyService when the upstream outage threshold is
    /// crossed (false) or forwarding recovers (true). Broadcasts on change so the
    /// dashboard's DNS-health tile tracks reality.</summary>
    public void SetUpstreamHealthy(bool healthy)
    {
        if (_upstreamHealthy == healthy)
            return;
        _upstreamHealthy = healthy;
        Broadcast(BuildStatus());
    }

    private StatusUpdateMessage BuildStatus() => new()
    {
        Timestamp = DateTime.UtcNow.ToString("O"),
        ProtectionState = _protectionState,
        UptimeSeconds = (long)(DateTime.UtcNow - _startedAt).TotalSeconds,
        BlockCount = (int)Interlocked.Read(ref _blockCountTotal),
        ThreatLevel = ThreatLevel.Low,
        UpstreamHealthy = _upstreamHealthy,
        RuleCounts = _blocklist.GetCategoryCounts()
    };

    // ═══════════════════════════════════════════════════════════════════════════
    //  BROADCAST  (called by other services to push events to the UI)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called by DnsProxyService on every DNS block. Increments block count and
    /// pushes the event to all connected UI clients.
    /// </summary>
    public void BroadcastLogEvent(LogEventMessage message)
    {
        IncrementBlockCount();
        Broadcast(message);
    }

    public void BroadcastAlert(AlertMessage message)
        => Broadcast(message);

    public void BroadcastStatusUpdate(StatusUpdateMessage message)
        => Broadcast(message);

    public void BroadcastMetricsUpdate(MetricsUpdateMessage message)
        => Broadcast(message);

    // ─── Internal: enqueue a message to every connected client ────────────────

    private void Broadcast<T>(T message) where T : IObstrouMessage
    {
        if (_clients.IsEmpty) return;

        var json = IpcSerializer.Serialize(message);
        var dead = new List<Guid>();

        foreach (var (id, client) in _clients)
        {
            // Broadcasts (live feed, metrics, heartbeat, alerts) carry domain and
            // status data — only verified UI connections receive them.
            if (!client.Trusted)
                continue;

            if (!client.OutboundQueue.Writer.TryWrite(json))
                dead.Add(id);
        }

        foreach (var id in dead)
        {
            if (_clients.TryRemove(id, out var c))
                c.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  HEARTBEAT
    // ═══════════════════════════════════════════════════════════════════════════

    private void OnHeartbeatTick(object? _)
    {
        // Auto-resume after an emergency pause expires. Runs even with no UI
        // connected — the protection state must reflect reality regardless.
        if (_protectionState == ProtectionState.DisabledTemporary && !IsProtectionPaused)
        {
            _logger.LogWarning("Emergency pause expired — protection resumed");
            SetProtectionState(ProtectionState.Active);
        }

        if (_clients.IsEmpty) return;

        Broadcast(new HeartbeatMessage
        {
            Timestamp = DateTime.UtcNow.ToString("O"),
            ServiceOk = true,
            ProtectionState = _protectionState,
            BlockCountTotal = Interlocked.Read(ref _blockCountTotal)
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  METRICS
    // ═══════════════════════════════════════════════════════════════════════════

    private void OnMetricsTick(object? _)
    {
        if (_clients.IsEmpty) return;

        try
        {
            Broadcast(_metrics.GetCurrentMetrics());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Metrics broadcast failed");
        }

        // Sweep expired whitelist entries and notify — the blueprint requires that
        // temporary allow-list exceptions never lapse silently. Runs only while a
        // UI is connected (guarded above) so the alert is actually delivered.
        try
        {
            var expired = _blocklist.SweepExpiredWhitelist();
            if (expired.Count > 0)
            {
                var shown = string.Join(", ", expired.Take(5));
                var more = expired.Count > 5 ? $" (+{expired.Count - 5} more)" : "";
                BroadcastAlert(new AlertMessage
                {
                    Timestamp = DateTime.UtcNow.ToString("O"),
                    AlertType = AlertType.WhitelistExpired,
                    Severity = Severity.Low,
                    Message = $"Temporary allow-list exception expired and is blocked again: {shown}{more}."
                });
            }

            // Symmetric: expire temporary custom BLOCKS. No alert — a block
            // silently lapsing is the intended, harmless direction.
            _blocklist.SweepExpiredCustomBlocks();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Expiry sweep failed");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ACCEPT LOOP
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(ct);

                var client = new ConnectedClient(pipe);

                // Only the installed UI binary receives broadcasts (live feed,
                // metrics) and the connect snapshot. An untrusted local process
                // can still connect and issue commands — but those are each
                // credential-gated, so it gets no domains or history this way.
                client.Trusted = PipeClientVerifier.VerifyClientIsUi(
                    pipe.SafePipeHandle.DangerousGetHandle(), _logger);

                _clients[client.Id] = client;

                _logger.LogInformation(
                    "Client connected (id={ClientId}, trusted={Trusted}, total={Count})",
                    client.Id, client.Trusted, _clients.Count);

                SendSnapshotTo(client);

                _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                _logger.LogWarning(ex, "IPC accept loop error — retrying in 1s");
                await Task.Delay(1_000, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Pushes the current status + metrics to a freshly connected client so the
    /// dashboard populates immediately instead of waiting for the next
    /// heartbeat/metrics tick.
    /// </summary>
    private void SendSnapshotTo(ConnectedClient client)
    {
        // The snapshot includes metrics (TopDomains) — untrusted connections
        // never receive it.
        if (!client.Trusted)
            return;

        try
        {
            client.OutboundQueue.Writer.TryWrite(IpcSerializer.Serialize(BuildStatus()));
            client.OutboundQueue.Writer.TryWrite(IpcSerializer.Serialize(_metrics.GetCurrentMetrics()));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send connect snapshot to client {ClientId}", client.Id);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  PER-CLIENT HANDLER
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task HandleClientAsync(ConnectedClient client, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var readTask = ReadLoopAsync(client, linkedCts.Token);
        var writeTask = WriteLoopAsync(client, linkedCts.Token);

        await Task.WhenAny(readTask, writeTask);
        await linkedCts.CancelAsync();
        await Task.WhenAll(readTask, writeTask);

        if (_clients.TryRemove(client.Id, out _))
        {
            client.Dispose();
            _logger.LogInformation(
                "UI client disconnected (id={ClientId}, remaining={Count})",
                client.Id, _clients.Count);
        }
    }

    private async Task ReadLoopAsync(ConnectedClient client, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await ReadLineBoundedAsync(client.Reader, MaxCommandChars, ct);
                if (line is null) break;

                if (!IpcSerializer.TryDeserializeCommand(line, out var command) || command is null)
                {
                    _logger.LogWarning(
                        "Malformed command received from client {ClientId} — discarding",
                        client.Id);
                    continue;
                }

                var response = HandleCommand(command, client.Pipe);
                var responseJson = IpcSerializer.Serialize(response);

                // Write the response straight to the pipe rather than onto the
                // lossy DropOldest broadcast channel. Under a block-event flood a
                // queued response could otherwise be evicted before it was sent —
                // the command (e.g. Uninstall) would have executed while the UI
                // saw a timeout. The write-gate serializes against the broadcast
                // write loop so the two never interleave a half-written line.
                await WriteRawAsync(client, responseJson, ct);
            }
        }
        catch (InvalidDataException ex)
        {
            // Oversized line — treat as hostile and drop the client.
            _logger.LogWarning(ex,
                "Client {ClientId} exceeded max command size — disconnecting", client.Id);
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Read loop error for client {ClientId}", client.Id);
        }
        finally
        {
            client.OutboundQueue.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Reads a single newline-delimited line, but throws InvalidDataException once
    /// the line exceeds maxChars — so a client that never sends a newline can't
    /// drive unbounded buffer growth. Returns null on EOF.
    /// </summary>
    private static async Task<string?> ReadLineBoundedAsync(
        StreamReader reader, int maxChars, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new char[1];

        while (true)
        {
            var n = await reader.ReadAsync(buf.AsMemory(0, 1), ct);
            if (n == 0)
                return sb.Length == 0 ? null : sb.ToString(); // EOF

            var c = buf[0];
            if (c == '\n')
                return sb.ToString();
            if (c == '\r')
                continue;

            sb.Append(c);
            if (sb.Length > maxChars)
                throw new InvalidDataException(
                    $"Inbound command exceeded {maxChars} characters without a newline.");
        }
    }

    private async Task WriteLoopAsync(ConnectedClient client, CancellationToken ct)
    {
        try
        {
            await foreach (var json in client.OutboundQueue.Reader.ReadAllAsync(ct))
                await WriteRawAsync(client, json, ct);
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Write loop error for client {ClientId}", client.Id);
        }
    }

    /// <summary>
    /// Writes one framed line to the client's pipe. The per-client write-gate
    /// serializes the broadcast write loop and inline command-response writes so
    /// their bytes never interleave.
    /// </summary>
    private static async Task WriteRawAsync(ConnectedClient client, string json, CancellationToken ct)
    {
        await client.WriteGate.WaitAsync(ct);
        try
        {
            await client.Writer.WriteLineAsync(json.AsMemory(), ct);
            await client.Writer.FlushAsync(ct);
        }
        finally
        {
            client.WriteGate.Release();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  COMMAND HANDLER
    // ═══════════════════════════════════════════════════════════════════════════

    private CommandResponseMessage HandleCommand(CommandMessage command, NamedPipeServerStream pipe)
    {
        _logger.LogInformation(
            "Command received: {CommandType} (requestId={RequestId})",
            command.CommandType, command.RequestId);

        try
        {
            return command.CommandType switch
            {
                ServiceCommand.GetStatus => Ok(command, state: _protectionState),
                ServiceCommand.GetMetrics => HandleGetMetrics(command),
                ServiceCommand.GetBlocklist => HandleGetBlocklist(command),
                ServiceCommand.GetSettings => HandleGetSettings(command),
                ServiceCommand.AddDomain => HandleAddDomain(command),
                ServiceCommand.RemoveDomain => HandleRemoveDomain(command),
                ServiceCommand.EmergencyStop => HandleEmergencyStop(command),
                ServiceCommand.EmergencyResume => HandleEmergencyResume(command),
                ServiceCommand.SyncBlocklist => HandleSyncBlocklist(command),
                ServiceCommand.AddWhitelist => HandleAddWhitelist(command),
                ServiceCommand.RemoveWhitelist => HandleRemoveWhitelist(command),
                ServiceCommand.GetWhitelist => HandleGetWhitelist(command),
                ServiceCommand.GetIncidents => HandleGetIncidents(command),
                ServiceCommand.ExportLogs => HandleExportLogs(command),

                // ── Auth over IPC ─────────────────────────────────────────────
                ServiceCommand.GetSetupState => HandleGetSetupState(command),
                ServiceCommand.SetCredential => HandleSetCredential(command, pipe),
                ServiceCommand.VerifyCredential => HandleVerifyCredential(command),
                ServiceCommand.PerformRecovery => HandlePerformRecovery(command),
                ServiceCommand.Uninstall => HandleUninstall(command),

                ServiceCommand.UpdateConfig => HandleUpdateConfig(command),

                _ => Fail(command, $"Unknown command: {command.CommandType}")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HandleCommand threw for {CommandType}", command.CommandType);
            return Fail(command, "Internal service error. See service logs.");
        }
    }

    private sealed record SettingsSnapshot(
        Dictionary<string, string> Config,
        List<CategoryState> Categories,
        string Version,
        string BuildCommit);

    private sealed record CategoryState(string Name, bool Enabled);

    /// <summary>
    /// Read-only settings snapshot for the Settings screen. Returns only the
    /// keys that UpdateConfig accepts (plus category on/off) — never hashes,
    /// lockout state, or version markers. Build identity (version + commit) is
    /// reported from the RUNNING binary so the dashboard shows exactly which
    /// build is live (finding L3). No credential required.
    /// </summary>
    private CommandResponseMessage HandleGetSettings(CommandMessage command)
    {
        var config = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in ConfigValidators.Keys)
            config[key] = ReadConfigValue(key) ?? string.Empty;

        var categories = _blocklist.GetCategoryStates()
            .Select(c => new CategoryState(c.Name, c.Enabled))
            .ToList();

        var snapshot = new SettingsSnapshot(
            config, categories,
            Obstruo.Shared.ObstruoVersion.Current,
            Obstruo.Shared.ObstruoVersion.CommitHash);
        return Ok(command, data: JsonSerializer.Serialize(snapshot, _jsonOptions));
    }

    private CommandResponseMessage HandleGetMetrics(CommandMessage command)
    {
        // Credential-gated: the snapshot includes TopDomains — the real
        // browsing/attempt history — so an on-demand pull is treated like
        // GetBlocklist, not like the ambient GetStatus. (The periodic
        // MetricsUpdate broadcast that drives the post-auth dashboard is a
        // separate channel; per-connection auth for broadcasts is tracked
        // separately.)
        var authError = VerifyCredential(command.Credential);
        if (authError is not null) return Fail(command, authError);

        return Ok(command, data: IpcSerializer.Serialize(_metrics.GetCurrentMetrics()));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  EMERGENCY PAUSE (PIN/password-gated stop, open resume)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Temporarily disables filtering. Credential-gated like every mutating
    /// command. Duration is clamped to emergency_disable_max_minutes; a
    /// persisted cooldown (emergency_disable_cooldown_minutes, measured from
    /// the end of the pause) prevents chaining pauses back-to-back.
    /// Optional payload: { "minutes": "<requested duration>" }.
    /// </summary>
    private CommandResponseMessage HandleEmergencyStop(CommandMessage command)
    {
        var authError = VerifyCredential(command.Credential);
        if (authError is not null) return Fail(command, authError);

        if (IsProtectionPaused)
            return Fail(command, "Protection is already paused.");

        if (DateTime.TryParse(
                ReadConfigValue("emergency_cooldown_until"), null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var cooldownUntil)
            && DateTime.UtcNow < cooldownUntil)
        {
            var wait = (int)Math.Ceiling((cooldownUntil - DateTime.UtcNow).TotalMinutes);
            return Fail(command,
                $"Emergency pause is on cooldown. Try again in {wait} minute(s).");
        }

        var maxMinutes = ReadPositiveConfigInt("emergency_disable_max_minutes", 15);
        var cooldownMinutes = ReadPositiveConfigInt("emergency_disable_cooldown_minutes", 60);

        var minutes = maxMinutes;
        if (command.Payload is not null &&
            (command.Payload.TryGetValue("minutes", out var requestedStr) ||
             command.Payload.TryGetValue("durationMinutes", out requestedStr)) &&
            int.TryParse(requestedStr, out var requested) && requested > 0)
        {
            minutes = Math.Min(requested, maxMinutes);
        }

        _pausedUntil = DateTime.UtcNow.AddMinutes(minutes);
        WriteConfigValue(
            "emergency_cooldown_until",
            _pausedUntil.AddMinutes(cooldownMinutes).ToString("O"));

        _logger.LogWarning(
            "EMERGENCY STOP — filtering paused for {Minutes} minute(s) (until {Until:O})",
            minutes, _pausedUntil);

        SetProtectionState(ProtectionState.DisabledTemporary);

        return Ok(command, state: ProtectionState.DisabledTemporary,
            data: JsonSerializer.Serialize(
                new { minutes, pausedUntil = _pausedUntil.ToString("O") }, _jsonOptions));
    }

    /// <summary>
    /// Ends an active pause early. Deliberately NOT credential-gated — resuming
    /// only makes the system stricter, so there is nothing to protect.
    /// </summary>
    private CommandResponseMessage HandleEmergencyResume(CommandMessage command)
    {
        if (!IsProtectionPaused && _protectionState != ProtectionState.DisabledTemporary)
            return Fail(command, "Protection is not paused.");

        _pausedUntil = DateTime.MinValue;
        _logger.LogWarning("Emergency resume — filtering re-enabled");
        SetProtectionState(ProtectionState.Active);

        return Ok(command, state: ProtectionState.Active);
    }

    private int ReadPositiveConfigInt(string key, int fallback)
        => int.TryParse(ReadConfigValue(key), out var v) && v > 0 ? v : fallback;

    // ── UpdateConfig ───────────────────────────────────────────────────────────

    // Only these Config keys are writable over IPC — everything else in the
    // Config table (credential hashes, lockout state, schema/version markers)
    // must stay out of reach even with a valid credential.
    // internal (not private) so Obstruo.Tests can pin this allowlist down.
    internal static readonly Dictionary<string, Func<string, bool>> ConfigValidators =
        new(StringComparer.Ordinal)
        {
            ["log_retention_hours"] = v => int.TryParse(v, out var i) && i is > 0 and <= 24 * 365,
            ["cleanup_time"] = v => TimeSpan.TryParse(v, out var t)
                                    && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1),
            ["emergency_disable_max_minutes"] = v => int.TryParse(v, out var i) && i is > 0 and <= 240,
            ["emergency_disable_cooldown_minutes"] = v => int.TryParse(v, out var i) && i is >= 0 and <= 10_080,
            ["metrics_refresh_seconds"] = v => int.TryParse(v, out var i) && i is >= 5 and <= 3_600,
            ["blocklist_url"] = v => v.Length == 0
                                     || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
            ["ui_theme"] = v => v is "dark" or "light",
            ["ui_mask_custom"] = v => v is "0" or "1",
            // LAN DNS filtering for other devices on the network. Off by default
            // (finding I-1) — enabling it opens an inbound :53 rule, so it must be
            // a deliberate opt-in. Takes effect on the next service start.
            ["lan_mode_enabled"] = v => v is "0" or "1",
            // SafeSearch: per-engine on/off, plus YouTube strictness. Changes take
            // effect immediately (HandleUpdateConfig refreshes the rewriter snapshot).
            ["safesearch_google"] = v => v is "0" or "1",
            ["safesearch_youtube"] = v => v is "0" or "1",
            ["safesearch_bing"] = v => v is "0" or "1",
            ["safesearch_youtube_level"] = v => v is "moderate" or "strict",
        };

    /// <summary>
    /// Credential-gated settings write. Payload is a batch of key/value pairs:
    ///   - allowlisted Config keys (validated above), and/or
    ///   - "category:&lt;Name&gt;" = "0"|"1" to disable/enable a block category.
    /// Everything is validated BEFORE anything is written — an invalid entry
    /// rejects the whole batch, so settings never end up half-applied.
    /// </summary>
    private CommandResponseMessage HandleUpdateConfig(CommandMessage command)
    {
        var authError = VerifyCredential(command.Credential);
        if (authError is not null) return Fail(command, authError);

        if (command.Payload is null || command.Payload.Count == 0)
            return Fail(command, "UpdateConfig requires at least one key/value in Payload.");

        const string categoryPrefix = "category:";

        // Validate the whole batch first.
        foreach (var (key, value) in command.Payload)
        {
            if (key.StartsWith(categoryPrefix, StringComparison.Ordinal))
            {
                if (value is not ("0" or "1"))
                    return Fail(command, $"'{key}' must be \"0\" or \"1\".");
            }
            else if (ConfigValidators.TryGetValue(key, out var validate))
            {
                if (!validate(value))
                    return Fail(command, $"Invalid value for '{key}'.");
            }
            else
            {
                return Fail(command, $"'{key}' is not a writable setting.");
            }
        }

        // Apply.
        foreach (var (key, value) in command.Payload)
        {
            if (key.StartsWith(categoryPrefix, StringComparison.Ordinal))
            {
                var (success, error) = _blocklist.SetCategoryEnabled(
                    key[categoryPrefix.Length..], value == "1");
                if (!success) return Fail(command, error ?? "Category update failed.");
            }
            else if (!WriteConfigValue(key, value.Trim()))
            {
                return Fail(command, $"Failed to store '{key}'. See service logs.");
            }
        }

        // Re-arm the metrics timer so a new cadence applies immediately instead
        // of waiting for the next service restart.
        if (command.Payload.ContainsKey("metrics_refresh_seconds"))
        {
            var intervalMs = ReadMetricsIntervalSeconds() * 1_000;
            _metricsTimer?.Change(intervalMs, intervalMs);
        }

        // Refresh the SafeSearch snapshot so engine/level changes take effect on
        // the next query instead of the next service restart.
        if (command.Payload.Keys.Any(k => k.StartsWith("safesearch_", StringComparison.Ordinal)))
            _safeSearch.Refresh();

        _logger.LogInformation("UpdateConfig applied {Count} setting(s): {Keys}",
            command.Payload.Count, string.Join(", ", command.Payload.Keys));

        return Ok(command);
    }

    /// <summary>
    /// Credential-gated allow-list add. Payload: { "domain": "…", optional
    /// "reason": "…", optional "expiresMinutes": "60" }. A whitelisted domain
    /// (plus subdomains) overrides every blocklist rule until it expires.
    /// </summary>
    private CommandResponseMessage HandleAddWhitelist(CommandMessage command)
    {
        var authError = VerifyCredential(command.Credential);
        if (authError is not null) return Fail(command, authError);

        if (command.Payload is null ||
            !command.Payload.TryGetValue("domain", out var domain) ||
            string.IsNullOrWhiteSpace(domain))
            return Fail(command, "AddWhitelist requires a 'domain' payload value.");

        command.Payload.TryGetValue("reason", out var reason);
        int? expiresMinutes = null;
        if (command.Payload.TryGetValue("expiresMinutes", out var expStr) &&
            int.TryParse(expStr, out var exp) && exp > 0)
            expiresMinutes = exp;

        var (success, error) = _blocklist.AddWhitelistDomain(domain, reason, expiresMinutes);
        return success ? Ok(command) : Fail(command, error ?? "Add failed.");
    }

    /// <summary>
    /// Credential-gated allow-list read. Same gate as GetBlocklist: the entries
    /// reveal the parent's allow decisions, so they are not readable by an
    /// arbitrary local process.
    /// </summary>
    private CommandResponseMessage HandleGetWhitelist(CommandMessage command)
    {
        var authError = VerifyCredential(command.Credential);
        if (authError is not null) return Fail(command, authError);

        return Ok(command, data: _blocklist.GetWhitelistSnapshotJson());
    }

    /// <summary>
    /// Credential-gated incident read. Incidents describe bypass attempts, so
    /// the same gate as the blocklist/whitelist applies.
    /// </summary>
    private CommandResponseMessage HandleGetIncidents(CommandMessage command)
    {
        var authError = VerifyCredential(command.Credential);
        if (authError is not null) return Fail(command, authError);

        return Ok(command, data: _incidents.GetRecentJson());
    }

    /// <summary>
    /// Credential-gated log export. The UI supplies a path (from a Save dialog)
    /// and format; the service writes the file directly. Path is trusted only
    /// because a valid credential is required — the authenticated parent chooses
    /// where their own history goes.
    /// </summary>
    private CommandResponseMessage HandleExportLogs(CommandMessage command)
    {
        var authError = VerifyCredential(command.Credential);
        if (authError is not null) return Fail(command, authError);

        if (command.Payload is null ||
            !command.Payload.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path))
            return Fail(command, "ExportLogs requires a 'path'.");

        command.Payload.TryGetValue("format", out var format);
        format = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ? "json" : "csv";

        var days = 30;
        if (command.Payload.TryGetValue("days", out var d) && int.TryParse(d, out var dv) && dv > 0)
            days = dv;

        try
        {
            var rows = _exporter.ExportToFile(path, format, days);
            return Ok(command, data: $"{{\"rows\":{rows}}}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export failed");
            return Fail(command, "Could not write the export file. Check the location and try again.");
        }
    }

    private CommandResponseMessage HandleRemoveWhitelist(CommandMessage command)
    {
        var authError = VerifyCredential(command.Credential);
        if (authError is not null) return Fail(command, authError);

        if (command.Payload is null ||
            !command.Payload.TryGetValue("domain", out var domain) ||
            string.IsNullOrWhiteSpace(domain))
            return Fail(command, "RemoveWhitelist requires a 'domain' payload value.");

        var (success, error) = _blocklist.RemoveWhitelistDomain(domain);
        return success ? Ok(command) : Fail(command, error ?? "Remove failed.");
    }

    /// <summary>
    /// Credential-gated blocklist sync. Optional payload { "url": "https://…" }
    /// persists a new feed URL before syncing. The fetch runs synchronously —
    /// callers should use a generous request timeout (feed download + apply).
    /// </summary>
    private CommandResponseMessage HandleSyncBlocklist(CommandMessage command)
    {
        var authError = VerifyCredential(command.Credential);
        if (authError is not null) return Fail(command, authError);

        if (command.Payload is not null &&
            command.Payload.TryGetValue("url", out var url) &&
            !string.IsNullOrWhiteSpace(url))
        {
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return Fail(command, "The blocklist URL must use HTTPS.");
            if (!WriteConfigValue("blocklist_url", url.Trim()))
                return Fail(command, "Failed to store the blocklist URL.");
        }

        var (success, error, added) = _blocklist.SyncNow();
        return success
            ? Ok(command, data: JsonSerializer.Serialize(new { added }, _jsonOptions))
            : Fail(command, error ?? "Sync failed.");
    }

    private CommandResponseMessage HandleGetBlocklist(CommandMessage command)
    {
        // Credential-gated: the snapshot includes the parent's custom domains, so
        // it should not be readable by any authenticated local process.
        var authError = VerifyCredential(command.Credential);
        if (authError is not null) return Fail(command, authError);

        var json = _blocklist.GetSnapshotJson();
        return Ok(command, data: json);
    }

    private CommandResponseMessage HandleAddDomain(CommandMessage command)
    {
        var authError = VerifyCredential(command.Credential);
        if (authError is not null) return Fail(command, authError);

        if (command.Payload is null ||
            !command.Payload.TryGetValue("domain", out var domain) ||
            string.IsNullOrWhiteSpace(domain))
            return Fail(command, "AddDomain requires a 'domain' payload value.");

        command.Payload.TryGetValue("category", out var category);

        // Optional temporary block: expiresMinutes > 0 means the block auto-lifts.
        int? expiresMinutes = null;
        if (command.Payload.TryGetValue("expiresMinutes", out var expStr) &&
            int.TryParse(expStr, out var exp) && exp > 0)
            expiresMinutes = exp;

        var (success, error) = _blocklist.AddCustomDomain(domain, category, expiresMinutes);
        return success ? Ok(command) : Fail(command, error ?? "Add failed.");
    }

    private CommandResponseMessage HandleRemoveDomain(CommandMessage command)
    {
        var authError = VerifyCredential(command.Credential);
        if (authError is not null) return Fail(command, authError);

        if (command.Payload is null ||
            !command.Payload.TryGetValue("domain", out var domain) ||
            string.IsNullOrWhiteSpace(domain))
            return Fail(command, "RemoveDomain requires a 'domain' payload value.");

        var (success, error) = _blocklist.RemoveCustomDomain(domain);
        return success ? Ok(command) : Fail(command, error ?? "Remove failed.");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  UNINSTALL (PIN/password-gated)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies the credential, then hands off to UninstallService. This is the
    /// ONLY command that undoes the DNS/DoH lockdown, so it is gated exactly like
    /// AddDomain/RemoveDomain — the ACL lets any authenticated local process open
    /// the pipe, so the credential is the real control. A wrong credential counts
    /// toward the shared lockout and changes nothing on the system.
    /// </summary>
    private CommandResponseMessage HandleUninstall(CommandMessage command)
    {
        var authError = VerifyCredential(command.Credential);
        if (authError is not null) return Fail(command, authError);

        var (success, error) = _uninstall.Run();
        return success ? Ok(command) : Fail(command, error ?? "Uninstall failed.");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  AUTH-OVER-IPC HANDLERS
    // ═══════════════════════════════════════════════════════════════════════════

    private sealed record SetupState(
        bool PinConfigured,
        bool PasswordConfigured,
        bool RecoveryConfigured,
        bool IsConfigured);

    private SetupState ReadSetupState()
    {
        var pin = !string.IsNullOrEmpty(ReadConfigValue("pin_hash"));
        var pwd = !string.IsNullOrEmpty(ReadConfigValue("password_hash"));
        var rec = !string.IsNullOrEmpty(ReadConfigValue("recovery_code_hash"));

        // Both PIN and password are mandatory — recovery code is generated by
        // the wizard but its absence does not gate "configured".
        return new SetupState(pin, pwd, rec, pin && pwd);
    }

    private CommandResponseMessage HandleGetSetupState(CommandMessage command)
    {
        // Read-only — no credential required. Reveals only booleans.
        var state = ReadSetupState();
        return Ok(command, data: JsonSerializer.Serialize(state, _jsonOptions));
    }

    private static readonly HashSet<string> AllowedCredentialKeys = new(StringComparer.Ordinal)
    {
        "pin_hash", "password_hash", "recovery_code_hash"
    };

    private CommandResponseMessage HandleSetCredential(CommandMessage command, NamedPipeServerStream pipe)
    {
        if (command.Payload is null ||
            !command.Payload.TryGetValue("key", out var key) ||
            !command.Payload.TryGetValue("value", out var plaintext))
            return Fail(command, "SetCredential requires 'key' and 'value' payload values.");

        if (!AllowedCredentialKeys.Contains(key))
            return Fail(command, $"'{key}' is not a valid credential key.");

        if (string.IsNullOrEmpty(plaintext))
            return Fail(command, "Credential value cannot be empty.");

        // ── Server-side format validation — UI validation is bypassable ──────
        switch (key)
        {
            case "pin_hash":
                if (plaintext.Length < 6 || plaintext.Length > 8 || !plaintext.All(char.IsDigit))
                    return Fail(command, "PIN must be 6 to 8 digits.");
                break;

            case "password_hash":
                if (plaintext.Length < 6 || !plaintext.All(char.IsLetterOrDigit))
                    return Fail(command, "Password must be alphanumeric, minimum 6 characters.");
                break;

                // recovery_code_hash: generated value, no format rule beyond non-empty.
        }

        // ── Bootstrap rule ────────────────────────────────────────────────────
        // While setup is incomplete, SetCredential carries no stored credential to
        // check against — so instead we require the CALLER to be an elevated
        // administrator. The first-run wizard is launched elevated (by the
        // installer, or via UI self-elevation); a standard-user process — e.g. a
        // child racing to seize the PIN before the parent finishes setup — is not
        // elevated and is rejected. This closes the bootstrap-hijack window.
        //
        // Once configured, changing any credential instead requires a valid
        // existing credential (the wizard saves pin → password → recovery code;
        // the recovery-code save happens after isConfigured flips true, so it
        // carries the new PIN/password as Credential).
        var setupState = ReadSetupState();
        if (setupState.IsConfigured)
        {
            var authError = VerifyCredential(command.Credential);
            if (authError is not null) return Fail(command, authError);
        }
        else if (!IsClientElevatedAdmin(pipe))
        {
            _logger.LogWarning(
                "Bootstrap SetCredential rejected — caller is not an elevated administrator");
            return Fail(command,
                "Initial setup must be run with administrator privileges. " +
                "Relaunch Obstruo and approve the elevation prompt.");
        }

        var hash = BCrypt.Net.BCrypt.HashPassword(plaintext, workFactor: 12);

        if (!WriteConfigValue(key, hash))
            return Fail(command, "Failed to store credential. See service logs.");

        _logger.LogInformation("Credential '{Key}' stored via IPC (bootstrap={Bootstrap})",
            key, !setupState.IsConfigured);

        return Ok(command);
    }

    private sealed record VerifyResult(
        bool LockedOut,
        long RemainingSeconds,
        int AttemptsBeforeLockout);

    private CommandResponseMessage HandleVerifyCredential(CommandMessage command)
    {
        if (string.IsNullOrEmpty(command.Credential))
            return Fail(command, "VerifyCredential requires a credential.");

        var authError = VerifyCredential(command.Credential);

        if (authError is null)
            return Ok(command);

        return FailWithLockoutInfo(command, authError);
    }

    private CommandResponseMessage HandlePerformRecovery(CommandMessage command)
    {
        if (string.IsNullOrEmpty(command.Credential))
            return Fail(command, "PerformRecovery requires the recovery code.");

        // Shared lockout applies — recovery guesses are rate-limited too.
        // If the user is locked out from PIN brute-forcing, recovery waits too.
        var lockoutError = CheckLockout();
        if (lockoutError is not null)
            return FailWithLockoutInfo(command, lockoutError);

        var storedHash = ReadConfigValue("recovery_code_hash");
        if (string.IsNullOrEmpty(storedHash))
            return Fail(command, "No recovery code is configured.");

        if (!VerifyBcrypt(command.Credential, storedHash))
        {
            _logger.LogWarning("Recovery code verification failed via IPC");
            var error = RegisterAuthFailure("Wrong recovery code.");
            return FailWithLockoutInfo(command, error);
        }

        // Code matched — atomically clear ALL credentials in one transaction.
        // No gap between "verified" and "cleared".
        if (!ClearAllCredentials())
            return Fail(command,
                "Recovery code accepted but clearing credentials failed. See service logs.");

        RegisterAuthSuccess();
        _logger.LogWarning(
            "Recovery performed via IPC — pin_hash, password_hash, recovery_code_hash cleared. Setup wizard required.");

        return Ok(command);
    }

    // ── Credential verification (shared by all authenticated commands) ─────────

    /// <summary>
    /// Verifies a plaintext PIN or password against the stored bcrypt hashes.
    /// Either credential type is accepted (any one method grants access).
    /// Returns null on success, or an error message on failure.
    /// Applies the escalating lockout policy on repeated failures.
    /// </summary>
    private string? VerifyCredential(string? credential)
    {
        var lockoutError = CheckLockout();
        if (lockoutError is not null) return lockoutError;

        if (string.IsNullOrEmpty(credential))
            return "This command requires your PIN or password.";

        var pinHash = ReadConfigValue("pin_hash");
        var passwordHash = ReadConfigValue("password_hash");

        if (string.IsNullOrEmpty(pinHash) && string.IsNullOrEmpty(passwordHash))
            return "No PIN or password is configured. Complete setup first.";

        bool match = VerifyBcrypt(credential, pinHash) || VerifyBcrypt(credential, passwordHash);

        if (match)
        {
            RegisterAuthSuccess();
            return null;
        }

        _logger.LogWarning("IPC credential verification failed");
        return RegisterAuthFailure("Wrong PIN or password.");
    }

    // ── Lockout policy (single implementation, shared by ALL auth paths) ──────

    /// <summary>Returns a lockout error message if currently locked out, else null.</summary>
    private string? CheckLockout()
    {
        lock (_authLock)
        {
            if (DateTime.UtcNow < _lockedUntil)
            {
                var remaining = (int)Math.Ceiling((_lockedUntil - DateTime.UtcNow).TotalMinutes);
                return $"Locked out. Try again in {remaining} minute(s).";
            }
        }
        return null;
    }

    /// <summary>
    /// Records one failed attempt. On the 3rd failure, triggers an escalating
    /// lockout (1, 3, 5, ... minutes) and returns the lockout message;
    /// otherwise returns the supplied wrong-credential message.
    /// </summary>
    private string RegisterAuthFailure(string wrongCredentialMessage)
    {
        lock (_authLock)
        {
            _failedAttempts++;
            _logger.LogWarning("IPC auth failure recorded. Attempt {Count}/3", _failedAttempts);

            if (_failedAttempts >= 3)
            {
                _lockoutCount++;
                _failedAttempts = 0;
                var minutes = (2 * _lockoutCount) - 1; // 1, 3, 5, 7 ...
                _lockedUntil = DateTime.UtcNow.AddMinutes(minutes);

                _logger.LogWarning(
                    "IPC credential lockout triggered (event #{Count}) — locked for {Minutes} min",
                    _lockoutCount, minutes);

                PersistLockoutState();
                return $"Too many wrong attempts. Locked out for {minutes} minute(s).";
            }

            PersistLockoutState();
            return wrongCredentialMessage;
        }
    }

    /// <summary>Resets all failure/lockout state after a successful auth.</summary>
    private void RegisterAuthSuccess()
    {
        lock (_authLock)
        {
            _failedAttempts = 0;
            _lockoutCount = 0;
            _lockedUntil = DateTime.MinValue;
            PersistLockoutState();
        }
    }

    // ── Lockout persistence ────────────────────────────────────────────────────
    // The lockout counters live in memory, so without this a service restart
    // (including a crash-triggered auto-restart) would wipe them and hand an
    // attacker a fresh set of attempts. Persist to Config so lockout survives.

    /// <summary>Writes current lockout state to Config. Callers hold _authLock.</summary>
    private void PersistLockoutState()
    {
        WriteConfigValue("lockout_until", _lockedUntil.ToString("O"));
        WriteConfigValue("lockout_failed", _failedAttempts.ToString());
        WriteConfigValue("lockout_count", _lockoutCount.ToString());
    }

    /// <summary>Restores persisted lockout state at startup.</summary>
    private void LoadLockoutState()
    {
        lock (_authLock)
        {
            if (DateTime.TryParse(
                    ReadConfigValue("lockout_until"), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var until))
                _lockedUntil = until;

            if (int.TryParse(ReadConfigValue("lockout_failed"), out var failed))
                _failedAttempts = failed;

            if (int.TryParse(ReadConfigValue("lockout_count"), out var count))
                _lockoutCount = count;

            if (_lockedUntil > DateTime.UtcNow)
                _logger.LogWarning(
                    "Restored active credential lockout — {Seconds}s remaining",
                    (int)(_lockedUntil - DateTime.UtcNow).TotalSeconds);
        }
    }

    /// <summary>BCrypt verify that never throws — malformed/missing hash = no match.</summary>
    private static bool VerifyBcrypt(string plaintext, string? storedHash)
    {
        if (string.IsNullOrEmpty(storedHash)) return false;
        try { return BCrypt.Net.BCrypt.Verify(plaintext, storedHash); }
        catch { return false; }
    }

    /// <summary>
    /// Builds a failure response carrying structured lockout info in Data so
    /// the UI can show a countdown. Used by VerifyCredential and PerformRecovery.
    /// </summary>
    private CommandResponseMessage FailWithLockoutInfo(CommandMessage command, string error)
    {
        long remainingSeconds;
        int attemptsLeft;
        lock (_authLock)
        {
            var remaining = _lockedUntil - DateTime.UtcNow;
            remainingSeconds = remaining > TimeSpan.Zero ? (long)remaining.TotalSeconds : 0;
            attemptsLeft = remainingSeconds > 0 ? 0 : 3 - _failedAttempts;
        }

        var info = new VerifyResult(remainingSeconds > 0, remainingSeconds, attemptsLeft);

        return new CommandResponseMessage
        {
            Timestamp = DateTime.UtcNow.ToString("O"),
            RequestId = command.RequestId,
            Success = false,
            Error = error,
            Data = JsonSerializer.Serialize(info, _jsonOptions)
        };
    }

    // ── Config table access ────────────────────────────────────────────────────

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read Config key '{Key}'", key);
            return null;
        }
    }

    private bool WriteConfigValue(string key, string value)
    {
        try
        {
            using var conn = new SqliteConnection(_db.ConnectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Config (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = $value;
                """;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write Config key '{Key}'", key);
            return false;
        }
    }

    /// <summary>
    /// Deletes pin_hash, password_hash, and recovery_code_hash in ONE
    /// transaction. Either all three go or none do — a partial clear would
    /// leave the system in an inconsistent auth state.
    /// </summary>
    private bool ClearAllCredentials()
    {
        try
        {
            using var conn = new SqliteConnection(_db.ConnectionString);
            conn.Open();

            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM Config
                WHERE key IN ('pin_hash', 'password_hash', 'recovery_code_hash');
                """;
            cmd.Parameters.Clear();
            cmd.ExecuteNonQuery();
            tx.Commit();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear credentials during recovery");
            return false;
        }
    }

    // ── Response helpers ──────────────────────────────────────────────────────

    private static CommandResponseMessage Ok(
        CommandMessage command, ProtectionState? state = null, string? data = null) => new()
        {
            Timestamp = DateTime.UtcNow.ToString("O"),
            RequestId = command.RequestId,
            Success = true,
            UpdatedState = state,
            Data = data
        };

    private static CommandResponseMessage Fail(CommandMessage command, string error) => new()
    {
        Timestamp = DateTime.UtcNow.ToString("O"),
        RequestId = command.RequestId,
        Success = false,
        Error = error
    };

    // ═══════════════════════════════════════════════════════════════════════════
    //  PIPE FACTORY
    //  ACL: SYSTEM + Administrators = FullControl, AuthenticatedUsers = ReadWrite
    //  AuthenticatedUsers is required because the UI runs non-elevated (asInvoker).
    //  UAC split tokens mark the Administrators SID as deny-only on non-elevated
    //  processes, so an Administrators-only Allow ACE would silently block the UI.
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Impersonates the connected client and reports whether its token is an
    /// elevated administrator. Used to gate bootstrap SetCredential. Fails closed
    /// (returns false) if impersonation or the role check throws.
    ///
    /// A standard user, and an admin running non-elevated (UAC split token marks
    /// the Administrators SID deny-only), both return false. Only a genuinely
    /// elevated process returns true.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private bool IsClientElevatedAdmin(NamedPipeServerStream pipe)
    {
        try
        {
            var isElevatedAdmin = false;
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                isElevatedAdmin = new WindowsPrincipal(identity)
                    .IsInRole(WindowsBuiltInRole.Administrator);
            });
            return isElevatedAdmin;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Client elevation check failed — treating as non-elevated");
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();

        // SYSTEM — full control (service runs as LocalSystem)
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // Administrators — full control
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // AuthenticatedUsers — read/write only
        // Required for the non-elevated UI process to connect.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096,
            pipeSecurity: security);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CONNECTED CLIENT  (nested — owns its pipe + channel + reader/writer)
    // ═══════════════════════════════════════════════════════════════════════════

    private sealed class ConnectedClient : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public NamedPipeServerStream Pipe { get; }
        public Channel<string> OutboundQueue { get; }
        public StreamReader Reader { get; }
        public StreamWriter Writer { get; }

        /// <summary>
        /// True once the client process was verified as the installed UI. Only
        /// trusted clients receive broadcasts and the connect snapshot.
        /// </summary>
        public bool Trusted { get; set; }

        // Serializes writes to Writer between the broadcast loop and inline
        // command-response writes, so their bytes never interleave on the pipe.
        public SemaphoreSlim WriteGate { get; } = new(1, 1);

        public ConnectedClient(NamedPipeServerStream pipe)
        {
            Pipe = pipe;

            OutboundQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(OutboundQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

            Reader = new StreamReader(pipe, leaveOpen: true);
            Writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = false };
        }

        public void Dispose()
        {
            OutboundQueue.Writer.TryComplete();
            try { Reader.Dispose(); } catch { /* ignore */ }
            try { Writer.Dispose(); } catch { /* ignore */ }
            try { Pipe.Dispose(); } catch { /* ignore */ }
            try { WriteGate.Dispose(); } catch { /* ignore */ }
        }
    }
}