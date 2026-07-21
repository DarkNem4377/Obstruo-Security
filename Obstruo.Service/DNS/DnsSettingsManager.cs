using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Obstruo.Service.Dns;

public class DnsSettingsManager
{
    private readonly string _backupPath;
    private readonly ILogger<DnsSettingsManager> _logger;

    private const string RegistryInterfacesPath =
        @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

    public bool HasBackup => File.Exists(_backupPath);

    public DnsSettingsManager(ILogger<DnsSettingsManager> logger)
    {
        _logger = logger;
        _backupPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Obstruo",
            "dns_backup.json");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void BackupAndSetDns()
    {
        if (!HasBackup)
        {
            var adapters = GetActiveAdapters();

            if (adapters.Count == 0)
            {
                _logger.LogWarning("No active network adapters found — skipping DNS backup");
            }
            else
            {
                var backup = new DnsBackup
                {
                    BackedUpAt = DateTime.UtcNow,
                    Adapters = adapters
                };

                var json = JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_backupPath, json);
                _logger.LogInformation("DNS backup saved — {Count} adapter(s)", adapters.Count);
            }
        }
        else
        {
            _logger.LogInformation("DNS backup already exists — skipping backup step");

            // Capture any adapter that has appeared since the backup was written
            // (new Wi-Fi network, dock, Ethernet plugged in). Without this it
            // would be pinned to 127.0.0.1 below but never recorded, so uninstall
            // could not restore it and would leave it pointing at a dead resolver.
            MergeNewAdaptersIntoBackup();
        }

        SetDnsToLocalhost();
    }

    /// <summary>
    /// Adds active adapters that are not yet in the backup and still carry their
    /// original (non-Obstruo) DNS. Adapters already pinned to loopback are
    /// skipped — capturing 127.0.0.1 as an "original" would be worse than useless.
    /// </summary>
    private void MergeNewAdaptersIntoBackup()
    {
        try
        {
            var json = File.ReadAllText(_backupPath);
            var backup = JsonSerializer.Deserialize<DnsBackup>(json);
            if (backup?.Adapters is null) return;

            var known = backup.Adapters
                .Select(a => a.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var added = 0;
            foreach (var adapter in GetActiveAdapters())
            {
                if (known.Contains(adapter.Name))
                    continue;

                // Skip an adapter that is already pinned to loopback only — its
                // real DNS is unknown, so DHCP-on-restore (the default) is best.
                var loopbackOnly = !adapter.IsDhcp
                    && adapter.DnsServers.Count > 0
                    && adapter.DnsServers.All(s => s == "127.0.0.1");
                if (loopbackOnly)
                    continue;

                backup.Adapters.Add(adapter);
                added++;
                _logger.LogInformation(
                    "New adapter captured into DNS backup: {Name}", adapter.Name);
            }

            if (added > 0)
            {
                var updated = JsonSerializer.Serialize(
                    backup, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_backupPath, updated);
                _logger.LogInformation("DNS backup updated — {Count} new adapter(s)", added);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not merge new adapters into the DNS backup");
        }
    }

    public void SetDnsToLocalhost()
    {
        // Pin EVERY adapter, not just the connected/gateway ones (findings M2/M3):
        //   - a disconnected Ethernet still carried 8.8.8.8/8.8.4.4 and would win
        //     the resolver race the moment it came up, before any re-pin;
        //   - a virtual adapter (VirtualBox host-only) advertised a site-local
        //     fec0:: IPv6 resolver that the OS could still use.
        // Pinning them all up front closes both — a NIC can never come online
        // carrying a non-Obstruo resolver.
        var adapterNames = GetAllPinnableAdapterNames();

        if (adapterNames.Count == 0)
        {
            _logger.LogWarning("No pinnable adapters found when setting DNS to localhost");
            return;
        }

        foreach (var name in adapterNames)
        {
            RunNetsh($"interface ipv4 set dns name=\"{name}\" static 127.0.0.1 primary");

            // Pin IPv6 DNS to ::1 as well. Without this, a client (or a virtual
            // adapter's RA) can point IPv6 DNS at a public/site-local resolver and
            // resolve every blocked domain over IPv6, bypassing the filter. `set
            // dns static ::1 primary` also drops any competing server already on
            // the adapter (the fec0:: case). Fails harmlessly if IPv6 is disabled.
            RunNetsh($"interface ipv6 set dns name=\"{name}\" static ::1 primary");

            _logger.LogInformation("DNS set to 127.0.0.1 / ::1 on adapter: {Name}", name);
        }
    }

    /// <summary>
    /// Re-applies the local-resolver pin after a network change: captures any
    /// newly-appeared adapter into the backup (so uninstall can still restore it)
    /// then re-pins every adapter. Cheap enough to call on each NetworkChange
    /// event — <see cref="NetworkChangeWatcher"/> debounces the firehose.
    /// </summary>
    public void RepinAllAdapters()
    {
        if (HasBackup)
            MergeNewAdaptersIntoBackup();
        SetDnsToLocalhost();
    }

    public void RestoreDns()
    {
        if (!HasBackup)
        {
            _logger.LogError("No DNS backup found — cannot restore. Manual DNS fix required.");
            return;
        }

        DnsBackup? backup;

        try
        {
            var json = File.ReadAllText(_backupPath);
            backup = JsonSerializer.Deserialize<DnsBackup>(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DNS backup file is corrupt — cannot restore. Manual DNS fix required.");
            return;
        }

        if (backup?.Adapters == null || backup.Adapters.Count == 0)
        {
            _logger.LogError("DNS backup contains no adapter entries — cannot restore");
            return;
        }

        foreach (var adapter in backup.Adapters)
        {
            if (adapter.IsDhcp)
            {
                RunNetsh($"interface ipv4 set dns name=\"{adapter.Name}\" dhcp");
                _logger.LogInformation("Restored {Name} to DHCP DNS", adapter.Name);
            }
            else if (adapter.DnsServers.Count > 0)
            {
                RunNetsh($"interface ipv4 set dns name=\"{adapter.Name}\" static {adapter.DnsServers[0]} primary");

                for (int i = 1; i < adapter.DnsServers.Count; i++)
                {
                    RunNetsh($"interface ipv4 add dns name=\"{adapter.Name}\" addr={adapter.DnsServers[i]} index={i + 1}");
                }

                _logger.LogInformation("Restored {Name} to static DNS: {Servers}",
                    adapter.Name, string.Join(", ", adapter.DnsServers));
            }
            else
            {
                _logger.LogWarning("Adapter {Name} had no DNS servers in backup — skipping", adapter.Name);
            }

            // The backup only records IPv4 DNS, so IPv6 is restored to DHCP —
            // correct for the overwhelming majority of setups (IPv6 DNS is almost
            // always router/RA-assigned). This undoes the ::1 pin from
            // SetDnsToLocalhost so resolution is not left broken over IPv6.
            RunNetsh($"interface ipv6 set dns name=\"{adapter.Name}\" dhcp");
        }

        // SetDnsToLocalhost pins EVERY adapter (incl. disconnected/virtual ones
        // that were never in the backup). Those would be left stranded on a dead
        // 127.0.0.1 / ::1 resolver after uninstall. Reset any adapter still
        // pinned to loopback that the backup didn't cover back to DHCP.
        var restored = backup.Adapters
            .Select(a => a.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in GetAllPinnableAdapterNames())
        {
            if (restored.Contains(name))
                continue;

            RunNetsh($"interface ipv4 set dns name=\"{name}\" dhcp");
            RunNetsh($"interface ipv6 set dns name=\"{name}\" dhcp");
            _logger.LogInformation(
                "Reset non-backed-up adapter {Name} to DHCP DNS (was pinned to loopback)", name);
        }

        File.Delete(_backupPath);
        _logger.LogInformation("DNS restored successfully. Backup deleted.");
    }

    /// <summary>
    /// Returns upstream DNS servers from backup for shadow mode.
    /// Always appends 1.1.1.1 as a guaranteed fallback after shadow servers.
    /// Falls back to 1.1.1.1 only if backup missing or unreadable.
    /// </summary>
    public List<string> GetUpstreamDnsServers()
    {
        const string fallback = "1.1.1.1";

        if (!HasBackup)
        {
            _logger.LogWarning("No DNS backup — falling back to Cloudflare upstream");
            return [fallback];
        }

        try
        {
            var json = File.ReadAllText(_backupPath);
            var backup = JsonSerializer.Deserialize<DnsBackup>(json);

            // Union across ALL backed-up adapters, first-seen order. Taking only
            // the first adapter's servers left the proxy with resolvers that may
            // be unreachable on the currently-active network (e.g. an Ethernet
            // backup while on Wi-Fi) — the "Wi-Fi says connected but nothing
            // loads" failure, softened only by the Cloudflare fallback.
            var servers = new List<string>();
            foreach (var adapter in backup?.Adapters ?? [])
            {
                foreach (var s in adapter.DnsServers)
                {
                    if (s != "127.0.0.1" && !servers.Contains(s))
                        servers.Add(s);
                }
            }

            if (servers.Count > 0)
            {
                // Append Cloudflare as fallback if not already in list
                if (!servers.Contains(fallback))
                    servers.Add(fallback);

                _logger.LogInformation("Shadow mode upstream: {Servers}",
                    string.Join(", ", servers));

                return servers;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read DNS backup for upstream — falling back to Cloudflare");
        }

        return [fallback];
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private List<AdapterDnsInfo> GetActiveAdapters()
    {
        var result = new List<AdapterDnsInfo>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;

            var props = nic.GetIPProperties();

            // Any default gateway qualifies — IPv4 or IPv6. Requiring an IPv4
            // gateway would silently leave an IPv6-only adapter unpinned and
            // unwatched, i.e. no protection at all on an IPv6-only network.
            // (netsh ipv4 set dns fails harmlessly on such an adapter; the
            // ipv6 pin is the one that matters there.)
            var hasGateway = props.GatewayAddresses.Count > 0;

            if (!hasGateway)
                continue;

            // Adapter names are interpolated into netsh command strings. A name
            // containing a double-quote could break out of the quoted argument.
            // Renaming an adapter requires admin, so this is low-risk self-harm,
            // but reject such names defensively rather than run a broken command.
            if (!IsSafeAdapterName(nic.Name))
            {
                _logger.LogWarning(
                    "Skipping adapter with unsafe name (contains quote/control char): {Name}", nic.Name);
                continue;
            }

            var dnsServers = props.DnsAddresses
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .ToList();

            result.Add(new AdapterDnsInfo
            {
                Name = nic.Name,
                Description = nic.Description,
                IsDhcp = IsDhcpDns(nic.Id),
                DnsServers = dnsServers
            });
        }

        return result;
    }

    /// <summary>
    /// Names of every adapter that should be pinned to the local resolver —
    /// every non-loopback, non-tunnel adapter regardless of connection status or
    /// gateway. This is deliberately broader than <see cref="GetActiveAdapters"/>
    /// (which governs backup/upstream and must stay connected-only): a
    /// disconnected or virtual adapter that still carries a public resolver is a
    /// live bypass the moment it comes up (findings M2/M3).
    /// </summary>
    private List<string> GetAllPinnableAdapterNames()
    {
        var names = new List<string>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            if (!IsSafeAdapterName(nic.Name))
            {
                _logger.LogWarning(
                    "Skipping adapter with unsafe name (contains quote/control char): {Name}", nic.Name);
                continue;
            }

            names.Add(nic.Name);
        }

        return names;
    }

    /// <summary>Rejects adapter names containing a double-quote or control chars.</summary>
    private static bool IsSafeAdapterName(string name)
        => !string.IsNullOrWhiteSpace(name)
           && !name.Contains('"')
           && !name.Any(char.IsControl);

    private static bool IsDhcpDns(string adapterId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"{RegistryInterfacesPath}\{adapterId}");

            var nameServer = key?.GetValue("NameServer") as string;
            return string.IsNullOrWhiteSpace(nameServer);
        }
        catch
        {
            return true;
        }
    }

    private void RunNetsh(string args)
    {
        try
        {
            var result = Obstruo.Shared.ProcessRunner.Run("netsh", args);

            if (!result.Exited)
                _logger.LogWarning("netsh timed out and was killed: {Args}", args);
            else if (result.ExitCode != 0)
                _logger.LogWarning("netsh failed (exit {Code}): {Args} | {Error}",
                    result.ExitCode, args, result.StdErr);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run netsh: {Args}", args);
        }
    }
}

// ── Backup models ─────────────────────────────────────────────────────────────

public class DnsBackup
{
    public DateTime BackedUpAt { get; set; }
    public List<AdapterDnsInfo> Adapters { get; set; } = [];
}

public class AdapterDnsInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsDhcp { get; set; }
    public List<string> DnsServers { get; set; } = [];
}