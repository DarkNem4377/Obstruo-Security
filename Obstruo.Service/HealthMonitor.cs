using ARSoft.Tools.Net;
using ARSoft.Tools.Net.Dns;
using Microsoft.Extensions.Logging;
using Obstruo.Service.Dns;
using Obstruo.Shared.Enums;
using Obstruo.Shared.Messages;
using System.Net;

namespace Obstruo.Service;

/// <summary>
/// Runtime self-heal. System DNS points at 127.0.0.1 (fail-closed), so a DNS
/// proxy that silently dies means "no internet" with no visible cause. SCM
/// recovery only reacts to process crashes — a hung or dead proxy inside a
/// live process is invisible to it.
///
/// Every minute this sends a real DNS query to the local proxy. ANY response
/// (including ServerFailure/NXDOMAIN) proves the proxy is answering; only a
/// timeout counts as a failure. After 3 consecutive failures it:
///   1. broadcasts a critical alert + Error state so a connected UI shows why,
///   2. exits the process with a non-zero code — SCM's recovery actions
///      restart the service, which re-runs the full startup sequence
///      (port-53 check, rebind, DoH rules). That restart IS the self-heal.
/// </summary>
public sealed class HealthMonitor : IDisposable
{
    private const int ProbeIntervalMs = 60_000;
    private const int ProbeTimeoutMs = 3_000;
    private const int FailuresBeforeRestart = 3;

    // Answered locally by DnsProxyService (see DnsProxyService.HealthProbeDomain)
    // and never forwarded upstream, so the probe reflects proxy liveness alone.
    // A reserved-TLD name — never cached, clearly ours in logs.
    private static readonly DomainName ProbeName =
        DomainName.Parse(DnsProxyService.HealthProbeDomain);

    private readonly IpcServer _ipcServer;
    private readonly ILogger<HealthMonitor> _logger;

    private Timer? _timer;
    private int _consecutiveFailures;
    private int _probeRunning; // interlocked guard — probes must not overlap

    public HealthMonitor(IpcServer ipcServer, ILogger<HealthMonitor> logger)
    {
        _ipcServer = ipcServer;
        _logger = logger;
    }

    /// <summary>Call only after DnsProxyService.Start() has succeeded.</summary>
    public void Start()
    {
        _timer ??= new Timer(_ => Probe(), null, ProbeIntervalMs, ProbeIntervalMs);
        _logger.LogInformation("Health monitor started (probe every {Sec}s)", ProbeIntervalMs / 1000);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose() => Stop();

    private void Probe()
    {
        if (Interlocked.Exchange(ref _probeRunning, 1) == 1) return;

        try
        {
            var client = new DnsClient(IPAddress.Loopback, ProbeTimeoutMs);
            var response = client.Resolve(ProbeName, RecordType.A);

            if (response is not null)
            {
                if (_consecutiveFailures > 0)
                    _logger.LogInformation("DNS proxy health restored after {Count} failed probe(s)",
                        _consecutiveFailures);
                _consecutiveFailures = 0;
                return;
            }

            OnProbeFailed("no response (timeout)");
        }
        catch (Exception ex)
        {
            OnProbeFailed(ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _probeRunning, 0);
        }
    }

    private void OnProbeFailed(string reason)
    {
        _consecutiveFailures++;
        _logger.LogWarning(
            "DNS proxy health probe failed ({Count}/{Max}): {Reason}",
            _consecutiveFailures, FailuresBeforeRestart, reason);

        if (_consecutiveFailures < FailuresBeforeRestart) return;

        _logger.LogCritical(
            "DNS proxy unresponsive for {Count} consecutive probes — restarting service " +
            "via SCM recovery (exit). Internet is blocked until the proxy is back.",
            _consecutiveFailures);

        try
        {
            _ipcServer.SetProtectionState(ProtectionState.Error);
            _ipcServer.BroadcastAlert(new AlertMessage
            {
                Timestamp = DateTime.UtcNow.ToString("O"),
                AlertType = AlertType.ProxyUnresponsive,
                Severity = Severity.Critical,
                Message = "The DNS filter stopped responding. Obstruo is restarting itself " +
                          "to recover — internet access resumes when the filter is back."
            });
            // Give the broadcast a moment to flush to connected clients.
            Thread.Sleep(500);
        }
        catch { /* nothing must stop the restart */ }

        Environment.Exit(2);
    }
}
