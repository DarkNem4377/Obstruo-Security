using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Obstruo.Shared.Enums;
using Obstruo.Shared.Messages;
using System.Net.NetworkInformation;

namespace Obstruo.Service.Dns;

/// <summary>
/// Monitors Windows DNS registry keys on all active adapters.
/// If anything changes DNS away from 127.0.0.1 externally,
/// reverts it immediately and broadcasts a tamper alert over IPC.
/// Runs on a dedicated background thread — never blocks DNS.
/// </summary>
public sealed class TamperDetector : IDisposable
{
    private readonly DnsSettingsManager _dnsSettings;
    private readonly IpcServer _ipcServer;
    private readonly ILogger<TamperDetector> _logger;

    private Thread? _watchThread;
    private CancellationTokenSource? _cts;
    private bool _started;
    private bool _disposed;

    // ── Sticky protection-state tracking ──────────────────────────────────────
    // On tamper we flip the broadcast protection state to Tampered; it stays that
    // way (visible on the dashboard) and auto-clears back to Active after a quiet
    // cooldown with no further tampering. Self-healing — no dismiss action needed.
    private bool _tampered;
    private DateTime _lastTamperUtc;
    private static readonly TimeSpan StickyClearAfter = TimeSpan.FromMinutes(5);

    private const int PollIntervalMs = 3000;

    private const string RegistryInterfacesPath =
        @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

    private const string RegistryInterfacesPathV6 =
        @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces";

    public TamperDetector(
        DnsSettingsManager dnsSettings,
        IpcServer ipcServer,
        ILogger<TamperDetector> logger)
    {
        _dnsSettings = dnsSettings;
        _ipcServer = ipcServer;
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void Start()
    {
        if (_started) return;
        _started = true;

        _cts = new CancellationTokenSource();

        _watchThread = new Thread(() => WatchLoop(_cts.Token))
        {
            Name = "Obstruo-TamperDetector",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };

        _watchThread.Start();
        _logger.LogInformation("Tamper detector started — polling every {Interval}ms", PollIntervalMs);
    }

    public void Stop()
    {
        if (!_started || _disposed) return;
        _disposed = true;
        _cts?.Cancel();

        // Join the watch thread before returning. The uninstall path relies on
        // this: if a CheckAndRevert() cycle is still in flight when DNS is
        // restored, it would re-pin 127.0.0.1 and leave the machine with no
        // resolver after the service is gone. Bounded so a stuck cycle can't
        // hang shutdown.
        _watchThread?.Join(TimeSpan.FromSeconds(5));

        _logger.LogInformation("Tamper detector stopped");
    }

    // ── Watch loop ────────────────────────────────────────────────────────────

    private void WatchLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                CheckAndRevert();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tamper detector poll cycle threw unexpectedly");
            }

            try
            {
                Task.Delay(PollIntervalMs, token).Wait(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // ── Core check ────────────────────────────────────────────────────────────

    private void CheckAndRevert()
    {
        var tamperedAdapters = new List<string>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;

            var props = nic.GetIPProperties();

            // IPv4 OR IPv6 gateway — must match DnsSettingsManager's adapter
            // filter, or an IPv6-only adapter would be pinned but never watched
            // (or vice versa).
            var hasGateway = props.GatewayAddresses.Count > 0;

            if (!hasGateway)
                continue;

            // Tampered if EITHER the IPv4 or the IPv6 DNS was moved off loopback.
            // Watching only IPv4 leaves an IPv6 resolver as an open bypass.
            if (!IsDnsSetToLocalhost(nic.Id) || !IsIpv6DnsSetToLocalhost(nic.Id))
                tamperedAdapters.Add(nic.Name);
        }

        if (tamperedAdapters.Count == 0)
        {
            // Quiet cycle — auto-clear the sticky Tampered state once enough time
            // has passed since the last detected tampering.
            if (_tampered && DateTime.UtcNow - _lastTamperUtc >= StickyClearAfter)
            {
                _tampered = false;
                _ipcServer.SetProtectionState(ProtectionState.Active);
                _logger.LogInformation(
                    "No tampering for {Minutes} min — protection state cleared to Active",
                    StickyClearAfter.TotalMinutes);
            }
            return;
        }

        _lastTamperUtc = DateTime.UtcNow;

        var adaptersText = string.Join(", ", tamperedAdapters);

        _logger.LogCritical(
            "[TAMPER DETECTED] DNS was changed externally on adapter(s): {Adapters}. " +
            "Reverting to 127.0.0.1 immediately.",
            adaptersText);

        // Broadcast alert over IPC before reverting so the UI sees what happened
        _ipcServer.BroadcastAlert(new AlertMessage
        {
            Timestamp = DateTime.UtcNow.ToString("O"),
            AlertType = AlertType.TamperDetected,
            Severity = Severity.Critical,
            Message = $"DNS tampered on adapter(s): {adaptersText}. Reverting to 127.0.0.1 immediately."
        });

        _dnsSettings.SetDnsToLocalhost();

        // Flip the broadcast protection state to Tampered on the first detection
        // of an episode. Stays sticky until StickyClearAfter of quiet (handled
        // above). Only fire the transition once so we don't spam StatusUpdates
        // while an attacker is actively fighting the revert every few seconds.
        if (!_tampered)
        {
            _tampered = true;
            _ipcServer.SetProtectionState(ProtectionState.Tampered);
        }

        _logger.LogWarning(
            "[TAMPER REVERTED] DNS restored to 127.0.0.1 on {Count} adapter(s)",
            tamperedAdapters.Count);
    }

    // ── Registry check ────────────────────────────────────────────────────────

    private static bool IsDnsSetToLocalhost(string adapterId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"{RegistryInterfacesPath}\{adapterId}");

            if (key == null)
                return true;

            var nameServer = key.GetValue("NameServer") as string ?? "";

            if (string.IsNullOrWhiteSpace(nameServer))
                return false;

            var servers = nameServer
                .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

            // EVERY configured server must be loopback. A secondary like
            // "127.0.0.1,8.8.8.8" is tampering: Windows fails over to the public
            // resolver whenever the proxy doesn't answer, so an unchecked
            // secondary is a persistent, silent bypass channel.
            return servers.Length > 0 && servers.All(s => s.Trim() == "127.0.0.1");
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// True if the adapter's IPv6 DNS is unset (DHCP/RA) or pinned entirely to ::1.
    /// A static IPv6 NameServer with ANY non-::1 entry counts as tamper — a
    /// secondary public resolver is an open bypass just like on IPv4.
    /// Missing key or empty value = DHCP-managed = not tampered (nothing to bypass;
    /// SetDnsToLocalhost pins it to ::1 on the next revert anyway).
    /// </summary>
    private static bool IsIpv6DnsSetToLocalhost(string adapterId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"{RegistryInterfacesPathV6}\{adapterId}");

            if (key == null)
                return true;

            var nameServer = key.GetValue("NameServer") as string ?? "";

            if (string.IsNullOrWhiteSpace(nameServer))
                return true;

            var servers = nameServer
                .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

            return servers.Length > 0 && servers.All(s => s.Trim() == "::1");
        }
        catch
        {
            return true;
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}