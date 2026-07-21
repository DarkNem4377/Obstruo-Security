using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;

namespace Obstruo.Service.Dns;

/// <summary>
/// Reacts to NIC changes so a newly-connected adapter cannot win the resolver
/// race with a DHCP-supplied public DNS before it is re-pinned (finding M2). The
/// 3-second tamper poll eventually corrects this, but there is a window between
/// "Ethernet plugged in / Wi-Fi joined" and the next poll where the OS may prefer
/// the fresh adapter's public resolver. This watcher closes that window by
/// re-pinning immediately on the OS network-change signal, and re-applies the
/// :53 firewall block because the chosen upstream can change with the network.
///
/// Windows raises these events in bursts (a single reconnect fires many), so the
/// handler debounces onto a short timer — the burst collapses into one re-pin.
/// </summary>
public sealed class NetworkChangeWatcher : IDisposable
{
    private readonly DnsSettingsManager _dnsSettings;
    private readonly Dns53Firewall _dns53;
    private readonly DnsProxyService _proxy;
    private readonly ILogger<NetworkChangeWatcher> _logger;

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(750);

    private readonly object _gate = new();
    private Timer? _debounceTimer;
    private bool _started;
    private bool _disposed;

    public NetworkChangeWatcher(
        DnsSettingsManager dnsSettings,
        Dns53Firewall dns53,
        DnsProxyService proxy,
        ILogger<NetworkChangeWatcher> logger)
    {
        _dnsSettings = dnsSettings;
        _dns53 = dns53;
        _proxy = proxy;
        _logger = logger;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;

        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        _logger.LogInformation("Network-change watcher started");
    }

    public void Stop()
    {
        if (!_started || _disposed) return;
        _disposed = true;

        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;

        lock (_gate)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        _logger.LogInformation("Network-change watcher stopped");
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_disposed) return;

            // Coalesce the event burst — (re)arm a one-shot timer.
            _debounceTimer ??= new Timer(_ => Repin());
            _debounceTimer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Repin()
    {
        try
        {
            _logger.LogInformation("Network change detected — re-pinning DNS and :53 firewall");
            _dnsSettings.RepinAllAdapters();
            _dns53.Apply();
            // Same upstream set the firewall was just computed around — the
            // proxy's clients were previously frozen at Start, so a network
            // switch could leave it forwarding to resolvers that no longer exist.
            _proxy.RefreshUpstreams();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Re-pin after network change failed");
        }
    }

    public void Dispose() => Stop();
}
