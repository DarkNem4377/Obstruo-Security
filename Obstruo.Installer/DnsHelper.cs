using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Obstruo.Installer;

/// <summary>
/// Self-contained DNS backup and restore logic for the installer.
/// Mirrors DnsSettingsManager from Obstruo.Service — no project reference.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DnsHelper
{
    private static readonly string BackupPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Obstruo",
        "dns_backup.json");

    private const string RegistryInterfacesPath =
        @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

    // ═══════════════════════════════════════════════════════════════════════════
    //  PUBLIC API
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Writes the DNS backup file only. Does NOT change system DNS.
    /// Safe to call before the point of no return.
    /// </summary>
    public static bool BackupDns()
    {
        if (File.Exists(BackupPath)) return true;

        var adapters = GetActiveAdapters();
        if (adapters.Count == 0) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);

        var backup = new DnsBackup
        {
            BackedUpAt = DateTime.UtcNow,
            Adapters = adapters
        };

        var json = JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(BackupPath, json);
        return true;
    }

    /// <summary>
    /// Sets all active adapters to 127.0.0.1. Does NOT write backup.
    /// This is the point of no return — call BackupDns() before this.
    /// </summary>
    public static bool SetDnsToLocalhost()
    {
        var adapters = GetActiveAdapters();
        if (adapters.Count == 0) return false;

        var allSucceeded = true;
        foreach (var adapter in adapters)
        {
            var ok = RunNetsh(
                $"interface ipv4 set dns name=\"{adapter.Name}\" static 127.0.0.1 primary");
            if (!ok) allSucceeded = false;

            // Pin IPv6 DNS to ::1 too — otherwise IPv6 is an open bypass.
            // Best-effort: fails harmlessly if IPv6 is disabled.
            RunNetsh($"interface ipv6 set dns name=\"{adapter.Name}\" static ::1 primary");
        }

        return allSucceeded;
    }

    /// <summary>
    /// Convenience — backup then set. Used when both steps happen together.
    /// </summary>
    public static bool BackupAndSetDns()
    {
        BackupDns();
        return SetDnsToLocalhost();
    }

    /// <summary>
    /// Restores DNS from backup file. Deletes backup on success.
    /// Falls back to DHCP on all active adapters if backup is missing or corrupt.
    /// </summary>
    public static bool RestoreDns()
    {
        if (!File.Exists(BackupPath))
            return RestoreDhcpFallback();

        DnsBackup? backup;

        try
        {
            var json = File.ReadAllText(BackupPath);
            backup = JsonSerializer.Deserialize<DnsBackup>(json);
        }
        catch
        {
            return RestoreDhcpFallback();
        }

        if (backup?.Adapters == null || backup.Adapters.Count == 0)
            return RestoreDhcpFallback();

        var allSucceeded = true;

        foreach (var adapter in backup.Adapters)
        {
            bool ok;

            if (adapter.IsDhcp)
            {
                ok = RunNetsh($"interface ipv4 set dns name=\"{adapter.Name}\" dhcp");
            }
            else if (adapter.DnsServers.Count > 0)
            {
                ok = RunNetsh(
                    $"interface ipv4 set dns name=\"{adapter.Name}\" static {adapter.DnsServers[0]} primary");

                for (int i = 1; i < adapter.DnsServers.Count; i++)
                    RunNetsh(
                        $"interface ipv4 add dns name=\"{adapter.Name}\" addr={adapter.DnsServers[i]} index={i + 1}");
            }
            else
            {
                ok = RunNetsh($"interface ipv4 set dns name=\"{adapter.Name}\" dhcp");
            }

            // Undo the ::1 IPv6 pin — restore IPv6 to DHCP (best-effort).
            RunNetsh($"interface ipv6 set dns name=\"{adapter.Name}\" dhcp");

            if (!ok) allSucceeded = false;
        }

        if (allSucceeded)
            try { File.Delete(BackupPath); } catch { /* best effort */ }

        return allSucceeded;
    }

    /// <summary>
    /// Returns true if at least one active adapter has 127.0.0.1 as DNS.
    /// Used by the installer to verify the DNS change took effect.
    /// </summary>
    public static bool IsDnsSetToLocalhost()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (nic.OperationalStatus != OperationalStatus.Up) continue;

            var dns = nic.GetIPProperties().DnsAddresses
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .ToList();

            if (dns.Contains("127.0.0.1")) return true;
        }

        return false;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  PRIVATE HELPERS
    // ═══════════════════════════════════════════════════════════════════════════

    private static bool RestoreDhcpFallback()
    {
        var allSucceeded = true;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (nic.OperationalStatus != OperationalStatus.Up) continue;

            var ok = RunNetsh($"interface ipv4 set dns name=\"{nic.Name}\" dhcp");
            if (!ok) allSucceeded = false;

            RunNetsh($"interface ipv6 set dns name=\"{nic.Name}\" dhcp");
        }

        return allSucceeded;
    }

    private static List<AdapterInfo> GetActiveAdapters()
    {
        var result = new List<AdapterInfo>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (nic.OperationalStatus != OperationalStatus.Up) continue;

            var props = nic.GetIPProperties();

            var hasGateway = props.GatewayAddresses
                .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork);

            if (!hasGateway) continue;

            // Reject adapter names that could break out of the quoted netsh arg.
            if (string.IsNullOrWhiteSpace(nic.Name) || nic.Name.Contains('"') || nic.Name.Any(char.IsControl))
                continue;

            var dnsServers = props.DnsAddresses
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .ToList();

            result.Add(new AdapterInfo
            {
                Name = nic.Name,
                IsDhcp = IsDhcpDns(nic.Id),
                DnsServers = dnsServers
            });
        }

        return result;
    }

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

    private static bool RunNetsh(string args)
    {
        try
        {
            return Obstruo.Shared.ProcessRunner.Run("netsh", args).Success;
        }
        catch
        {
            return false;
        }
    }
}

// ── Models (mirrored from Obstruo.Service — no project reference) ─────────────

internal sealed class DnsBackup
{
    public DateTime BackedUpAt { get; set; }
    public List<AdapterInfo> Adapters { get; set; } = [];
}

internal sealed class AdapterInfo
{
    public string Name { get; set; } = "";
    public bool IsDhcp { get; set; }
    public List<string> DnsServers { get; set; } = [];
}