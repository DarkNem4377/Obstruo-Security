using Microsoft.Extensions.Logging;
using Obstruo.Shared.Contracts;
using Obstruo.Shared.Enums;
using Obstruo.Shared.Messages;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;

namespace Obstruo.UI.Ipc;

/// <summary>
/// Named pipe client — connects to the Obstruo service IPC server.
/// Deserializes incoming NDJSON messages and raises typed events.
/// Automatically reconnects when the connection is lost (service restart, etc.).
///
/// All events are raised on a background thread.
/// UI consumers must dispatch to the UI thread themselves:
///   Application.Current.Dispatcher.Invoke(() => { ... });
///
/// Request/response: SendCommandAndWaitAsync sends a CommandMessage and awaits
/// the CommandResponseMessage with the matching RequestId. Pending requests are
/// failed immediately when the connection drops.
/// </summary>
public sealed class IpcClient : IDisposable
{
    private const string PipeName = "ObstruoSecurityPipe";
    private const int ReconnectDelayMs = 3_000;
    private const int ConnectTimeoutMs = 2_000;
    private const int DefaultRequestTimeoutMs = 5_000;

    private readonly ILogger<IpcClient> _logger;

    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    // ── Pending request/response waiters ─────────────────────────────────────
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandResponseMessage>>
        _pendingRequests = new();

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fires every ~5 seconds with service health, protection state, and total block count.
    /// Primary driver for the status chip and headline metrics in the dashboard.
    /// </summary>
    public event EventHandler<HeartbeatMessage>? HeartbeatReceived;

    /// <summary>
    /// Fires on every blocked domain. Primary driver for the live feed.
    /// </summary>
    public event EventHandler<LogEventMessage>? LogEventReceived;

    /// <summary>
    /// Fires on service alerts: tamper detected, LAN IP changed, port 53 conflict, etc.
    /// </summary>
    public event EventHandler<AlertMessage>? AlertReceived;

    /// <summary>
    /// Fires when the service pushes a protection-state change (connect snapshot,
    /// emergency pause/resume, tamper, error). Faster than waiting for the next
    /// heartbeat.
    /// </summary>
    public event EventHandler<StatusUpdateMessage>? StatusUpdateReceived;

    /// <summary>
    /// Fires with database-backed dashboard metrics (today/week totals, category
    /// breakdown, top domains, hourly bars). Sent on connect and every
    /// metrics_refresh_seconds thereafter.
    /// </summary>
    public event EventHandler<MetricsUpdateMessage>? MetricsUpdateReceived;

    /// <summary>
    /// Fires when the service responds to a command. Also fires for responses
    /// consumed by SendCommandAndWaitAsync — waiters are completed first.
    /// </summary>
    public event EventHandler<CommandResponseMessage>? CommandResponseReceived;

    /// <summary>
    /// Fires when the connection state changes.
    /// true  = connected and actively reading from the pipe.
    /// false = disconnected — reconnect loop is running in the background.
    /// </summary>
    public event EventHandler<bool>? ConnectionChanged;

    /// <summary>Current connection state. Set before ConnectionChanged fires.</summary>
    public bool IsConnected { get; private set; }

    // ─────────────────────────────────────────────────────────────────────────

    public IpcClient(ILogger<IpcClient> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Starts the background connect + read loop.
    /// Safe to call once. Subsequent calls are no-ops.
    /// </summary>
    public void Start()
    {
        if (_cts is not null) return;

        _cts = new CancellationTokenSource();
        _readLoop = Task.Run(() => RunAsync(_cts.Token));

        _logger.LogInformation("IpcClient started");
    }

    /// <summary>
    /// Stops the background loop and closes the pipe.
    /// </summary>
    public void Stop()
    {
        if (_cts is null) return;

        _cts.Cancel();
        _cts.Dispose();
        _cts = null;

        FailAllPending("IPC client stopped.");
        ClosePipe();
        _logger.LogInformation("IpcClient stopped");
    }

    // ── Request/response ──────────────────────────────────────────────────────

    /// <summary>
    /// Sends a command and awaits the response with the matching RequestId.
    /// Throws InvalidOperationException if not connected.
    /// Throws TimeoutException if no response arrives within timeoutMs.
    /// Throws IOException if the connection drops while waiting.
    /// </summary>
    public async Task<CommandResponseMessage> SendCommandAndWaitAsync(
        ServiceCommand commandType,
        Dictionary<string, string>? payload = null,
        string? credential = null,
        int timeoutMs = DefaultRequestTimeoutMs)
    {
        if (!IsConnected)
            throw new InvalidOperationException(
                "Not connected to the Obstruo service. Is the service running?");

        var requestId = Guid.NewGuid().ToString("N");

        var command = new CommandMessage
        {
            Timestamp = DateTime.UtcNow.ToString("O"),
            RequestId = requestId,
            CommandType = commandType,
            Payload = payload,
            Credential = credential
        };

        var tcs = new TaskCompletionSource<CommandResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pendingRequests.TryAdd(requestId, tcs))
            throw new InvalidOperationException("RequestId collision — retry the command.");

        try
        {
            await SendCommandAsync(command);

            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            await using var registration = timeoutCts.Token.Register(
                () => tcs.TrySetException(new TimeoutException(
                    $"No response from service within {timeoutMs}ms for {commandType}.")));

            return await tcs.Task;
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    private void FailAllPending(string reason)
    {
        foreach (var (id, tcs) in _pendingRequests)
        {
            tcs.TrySetException(new IOException(reason));
            _pendingRequests.TryRemove(id, out _);
        }
    }

    // ── Background loop ───────────────────────────────────────────────────────

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAsync(ct);

                if (ct.IsCancellationRequested)
                    break;

                await ReadLoopAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "IPC read loop error — will reconnect in {Delay}ms", ReconnectDelayMs);
            }
            finally
            {
                SetConnected(false);
                FailAllPending("Connection to the Obstruo service was lost.");
                ClosePipe();
            }

            try { await Task.Delay(ReconnectDelayMs, ct); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("IpcClient background loop exited");
    }

    // ── Connect ───────────────────────────────────────────────────────────────

    private async Task ConnectAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _logger.LogDebug("Attempting pipe connection to '{PipeName}'", PipeName);

                _pipe = new NamedPipeClientStream(
                    serverName: ".",
                    pipeName: PipeName,
                    direction: PipeDirection.InOut,
                    options: PipeOptions.Asynchronous);

                await _pipe.ConnectAsync(ConnectTimeoutMs, ct);

                // Verify the server is the real installed service before trusting
                // it with anything — defeats named-pipe squatting / PIN capture.
                if (!PipeServerVerifier.VerifyServerIsService(
                        _pipe.SafePipeHandle.DangerousGetHandle(), _logger))
                {
                    _logger.LogError(
                        "Pipe server failed identity verification — closing (possible spoof)");
                    ClosePipe();
                    try { await Task.Delay(ReconnectDelayMs, ct); }
                    catch (OperationCanceledException) { return; }
                    continue;
                }

                // StreamWriter used for outbound commands only.
                // Inbound reading uses a separate StreamReader in ReadLoopAsync.
                _writer = new StreamWriter(_pipe) { AutoFlush = true };

                SetConnected(true);
                _logger.LogInformation("Connected to Obstruo service pipe");
                return;
            }
            catch (TimeoutException)
            {
                _logger.LogDebug(
                    "Pipe connect timed out — service may not be running yet. Retrying...");
                ClosePipe();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    "Pipe connect failed ({Message}) — retrying in {Delay}ms",
                    ex.Message, ReconnectDelayMs);
                ClosePipe();
            }

            try { await Task.Delay(ReconnectDelayMs, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    // ── Read loop ─────────────────────────────────────────────────────────────

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        // StreamReader is local — pipe lifetime is managed by ClosePipe().
        // Do NOT dispose the reader here; it would close the underlying pipe
        // before the write path (_writer) is done with it.
        var reader = new StreamReader(_pipe!);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);

            // Null = server closed its end of the pipe
            if (line is null)
            {
                _logger.LogInformation("Service closed the pipe — reconnecting");
                return;
            }

            if (string.IsNullOrWhiteSpace(line))
                continue;

            DispatchMessage(line);
        }
    }

    // ── Message dispatch ──────────────────────────────────────────────────────

    private void DispatchMessage(string json)
    {
        if (!IpcSerializer.TryDeserialize(json, out var message) || message is null)
        {
            _logger.LogWarning("Could not deserialize IPC message: {Json}", json);
            return;
        }

        try
        {
            switch (message)
            {
                case HeartbeatMessage hb:
                    HeartbeatReceived?.Invoke(this, hb);
                    break;

                case LogEventMessage le:
                    LogEventReceived?.Invoke(this, le);
                    break;

                case AlertMessage alert:
                    AlertReceived?.Invoke(this, alert);
                    break;

                case StatusUpdateMessage status:
                    StatusUpdateReceived?.Invoke(this, status);
                    break;

                case MetricsUpdateMessage metrics:
                    MetricsUpdateReceived?.Invoke(this, metrics);
                    break;

                case CommandResponseMessage cr:
                    // Complete the matching request waiter FIRST, then raise
                    // the public event for any passive listeners.
                    if (_pendingRequests.TryRemove(cr.RequestId, out var tcs))
                        tcs.TrySetResult(cr);

                    CommandResponseReceived?.Invoke(this, cr);
                    break;

                default:
                    _logger.LogDebug(
                        "No handler registered for message type {Type}",
                        message.GetType().Name);
                    break;
            }
        }
        catch (Exception ex)
        {
            // A throwing event handler must never kill the read loop.
            _logger.LogError(ex,
                "Event handler threw for message type {Type}", message.GetType().Name);
        }
    }

    // ── Outbound ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a command to the service.
    /// Thread-safe. Silently drops the message if not currently connected.
    /// </summary>
    public async Task SendCommandAsync(CommandMessage command)
    {
        if (!IsConnected || _writer is null)
        {
            _logger.LogDebug("SendCommandAsync called while disconnected — dropping command");
            return;
        }

        var line = IpcSerializer.Serialize(command) + IpcSerializer.MessageDelimiter;

        await _writeLock.WaitAsync();
        try
        {
            await _writer.WriteAsync(line);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send command — pipe may be broken");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetConnected(bool connected)
    {
        if (IsConnected == connected) return;
        IsConnected = connected;

        try { ConnectionChanged?.Invoke(this, connected); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ConnectionChanged handler threw");
        }
    }

    private void ClosePipe()
    {
        try { _writer?.Dispose(); } catch { /* ignore */ }
        try { _pipe?.Dispose(); } catch { /* ignore */ }
        _writer = null;
        _pipe = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        FailAllPending("IPC client disposed.");
        _writeLock.Dispose();
    }
}