using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Obstruo.Service.Data;

namespace Obstruo.Service;

public class LanModeService
{
    private readonly ObstruoDatabase _db;
    private readonly ILogger<LanModeService> _logger;

    private const string FirewallRuleUdp = "Obstruo-LAN-DNS-UDP-Inbound";
    private const string FirewallRuleTcp = "Obstruo-LAN-DNS-TCP-Inbound";

    // ── Public state (read by Worker.cs after Start() returns) ───────────────

    /// <summary>LAN IP detected on this startup. Null if no private IP found.</summary>
    public string? CurrentLanIp { get; private set; }

    /// <summary>LAN IP from the previous startup, read from Config.</summary>
    public string? PreviousLanIp { get; private set; }

    /// <summary>
    /// True when no lan_ip was stored in Config — first time we have a LAN IP.
    /// Worker.cs uses this to trigger the first-run LAN notification in the UI.
    /// </summary>
    public bool IsFirstRun { get; private set; }

    /// <summary>
    /// True when CurrentLanIp differs from PreviousLanIp (and it is not the first run).
    /// Worker.cs uses this to broadcast AlertType.LanIpChanged after IpcServer is up.
    /// </summary>
    public bool HasIpChanged { get; private set; }

    // ─────────────────────────────────────────────────────────────────────────

    public LanModeService(ObstruoDatabase db, ILogger<LanModeService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public void Start()
    {
        _logger.LogInformation("LanModeService starting");

        // LAN filtering is opt-in (finding I-1). While disabled, Obstruo binds
        // loopback only and opens no inbound :53 rule — the machine is protected
        // but does not act as a DNS server for other devices. CurrentLanIp stays
        // null, so DnsProxyService skips the LAN bind entirely.
        if (!IsLanModeEnabled())
        {
            _logger.LogInformation(
                "LAN DNS mode is disabled (default) — binding loopback only, no inbound rules");

            // Round-2 finding H7: a 1.0.0 install (LAN mode on by default) leaves
            // its Obstruo-LAN-DNS-* inbound allow rules behind after an upgrade.
            // While LAN mode is off there must be no inbound :53 allow at all, so
            // sweep any inherited rules. Idempotent — deleting absent rules is a
            // no-op, so fresh installs pay nothing.
            RemoveFirewallRule(FirewallRuleUdp);
            RemoveFirewallRule(FirewallRuleTcp);
            return;
        }

        CurrentLanIp = DetectLanIp();
        PreviousLanIp = ReadStoredLanIp();

        if (CurrentLanIp is null)
        {
            _logger.LogWarning(
                "No private LAN IP detected. LAN DNS binding and firewall rules will be skipped.");
            return;
        }

        IsFirstRun = string.IsNullOrEmpty(PreviousLanIp);
        HasIpChanged = !IsFirstRun && CurrentLanIp != PreviousLanIp;

        if (IsFirstRun)
            _logger.LogInformation("First LAN IP detected: {Ip}", CurrentLanIp);
        else if (HasIpChanged)
            _logger.LogWarning("LAN IP changed: {Old} → {New}", PreviousLanIp, CurrentLanIp);
        else
            _logger.LogInformation("LAN IP unchanged: {Ip}", CurrentLanIp);

        StoreCurrentLanIp(CurrentLanIp);
        UpdateFirewallRules(CurrentLanIp);

        _logger.LogInformation("LanModeService started. CurrentLanIp={Ip}", CurrentLanIp);
    }

    public void Stop()
    {
        // Firewall rules are intentionally left in place on stop.
        // They are only removed and re-added on the next Start() if the IP changed.
        // Removing them on stop would break LAN DNS for router clients
        // during a transient service restart.
        _logger.LogInformation("LanModeService stopped (firewall rules retained)");
    }

    /// <summary>
    /// Permanently removes the LAN DNS inbound firewall rules.
    /// Called ONLY on PIN-confirmed uninstall — never on a normal stop, where the
    /// rules are deliberately retained across a transient restart.
    /// </summary>
    public void RemoveFirewallRules()
    {
        _logger.LogInformation("Removing LAN DNS firewall rules (uninstall)");
        RemoveFirewallRule(FirewallRuleUdp);
        RemoveFirewallRule(FirewallRuleTcp);
    }

    // ── LAN IP detection ──────────────────────────────────────────────────────

    private string? DetectLanIp()
    {
        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                foreach (var unicast in iface.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    if (!IsPrivateIp(unicast.Address))
                        continue;

                    var ip = unicast.Address.ToString();
                    _logger.LogDebug(
                        "LAN IP candidate: {Ip} on interface {Name}", ip, iface.Name);
                    return ip;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while detecting LAN IP");
        }

        return null;
    }

    private static bool IsPrivateIp(IPAddress address)
    {
        var b = address.GetAddressBytes();

        // 10.0.0.0/8
        if (b[0] == 10)
            return true;

        // 172.16.0.0/12  (172.16 – 172.31)
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            return true;

        // 192.168.0.0/16
        if (b[0] == 192 && b[1] == 168)
            return true;

        return false;
    }

    // ── Subnet helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Derives a /24 subnet string from a LAN IP.
    /// 192.168.1.100 → 192.168.1.0/24
    ///
    /// /24 is correct for virtually all home and small-office networks.
    /// If a network uses a wider mask (/16 etc.), this is conservative —
    /// only hosts on the same /24 can use Obstruo as their DNS server.
    /// That is the safer default.
    /// </summary>
    private static string GetSubnet24(string ip)
    {
        var parts = ip.Split('.');
        return $"{parts[0]}.{parts[1]}.{parts[2]}.0/24";
    }

    // ── Firewall rules ────────────────────────────────────────────────────────

    private void UpdateFirewallRules(string lanIp)
    {
        var subnet = GetSubnet24(lanIp);

        _logger.LogInformation(
            "Updating LAN DNS firewall rules — IP={Ip}, Subnet={Subnet}", lanIp, subnet);

        // Always remove first so a stale rule from a previous IP doesn't linger
        RemoveFirewallRule(FirewallRuleUdp);
        RemoveFirewallRule(FirewallRuleTcp);

        AddFirewallRule(FirewallRuleUdp, "UDP", lanIp, subnet);
        AddFirewallRule(FirewallRuleTcp, "TCP", lanIp, subnet);
    }

    private void RemoveFirewallRule(string name)
    {
        try
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");
            _logger.LogDebug("Removed firewall rule: {Name}", name);
        }
        catch (Exception ex)
        {
            // Not an error — rule simply may not exist yet
            _logger.LogDebug(ex, "Could not remove firewall rule {Name} (may not exist)", name);
        }
    }

    private void AddFirewallRule(string name, string protocol, string localIp, string remoteSubnet)
    {
        try
        {
            // inbound port 53 on the LAN IP only, from the local /24 subnet only.
            // remoteip restriction is important: without it, anyone who can route
            // to this machine could use Obstruo as an open DNS resolver.
            // Scoped to the Private profile only (finding I-1): an inbound DNS
            // opening should never be active while the machine is on a public
            // network. Combined with the localip/remoteip restriction, exposure is
            // limited to the local /24 on a network the user has marked private.
            var args = $"advfirewall firewall add rule " +
                       $"name=\"{name}\" " +
                       $"dir=in " +
                       $"action=allow " +
                       $"protocol={protocol} " +
                       $"localport=53 " +
                       $"localip={localIp} " +
                       $"remoteip={remoteSubnet} " +
                       $"enable=yes " +
                       $"profile=private";

            RunNetsh(args);

            _logger.LogInformation(
                "Firewall rule added: {Name} {Protocol} localip={Ip} remoteip={Subnet}",
                name, protocol, localIp, remoteSubnet);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add firewall rule {Name}", name);
        }
    }

    private static void RunNetsh(string arguments)
        => Obstruo.Shared.ProcessRunner.Run("netsh", arguments, timeoutMs: 10_000);

    // ── Config read / write ───────────────────────────────────────────────────

    /// <summary>
    /// True only when the user has explicitly enabled LAN DNS filtering
    /// (Config lan_mode_enabled = "1"). Any read failure defaults to OFF — the
    /// safe posture is loopback-only.
    /// </summary>
    private bool IsLanModeEnabled()
    {
        try
        {
            using var conn = new SqliteConnection(_db.ConnectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM Config WHERE key = 'lan_mode_enabled';";
            return cmd.ExecuteScalar()?.ToString() == "1";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read lan_mode_enabled — defaulting LAN mode OFF");
            return false;
        }
    }

    private string? ReadStoredLanIp()
    {
        try
        {
            using var conn = new SqliteConnection(_db.ConnectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM Config WHERE key = 'lan_ip';";
            var result = cmd.ExecuteScalar()?.ToString();

            return string.IsNullOrEmpty(result) ? null : result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read stored LAN IP from Config");
            return null;
        }
    }

    private void StoreCurrentLanIp(string lanIp)
    {
        try
        {
            using var conn = new SqliteConnection(_db.ConnectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();

            // UPSERT — creates the row on first run, updates it on IP change.
            // We do not seed lan_ip in ObstruoDatabase because the value is
            // machine-specific and has no valid default.
            cmd.CommandText = """
                INSERT INTO Config (key, value) VALUES ('lan_ip', $ip)
                ON CONFLICT(key) DO UPDATE SET value = $ip;
                """;
            cmd.Parameters.AddWithValue("$ip", lanIp);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store LAN IP in Config");
        }
    }
}