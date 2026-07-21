using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Obstruo.Service.Dns;

/// <summary>
/// Closes the classic-DNS bypass (finding H1): any process that talks plain DNS
/// on UDP/TCP port 53 directly to a public resolver (e.g. <c>nslookup x 8.8.8.8</c>)
/// sidesteps the local filter entirely, because system-DNS pinning only governs
/// apps that use the OS resolver.
///
/// Fix: outbound firewall rules that block UDP <b>and</b> TCP port 53 to every
/// remote address EXCEPT the service's own chosen upstream resolvers. That is
/// the literal goal — "the machine cannot reach any DNS resolver except Obstruo."
///
/// Why an exclusion set rather than allow-Obstruo + block-everyone: Windows
/// Firewall evaluates block rules ahead of allow rules, so a program-scoped
/// allow can never override a broad :53 block. Instead we simply never block the
/// upstream IPs — leaving Obstruo.Service (the only thing that talks to them on
/// :53) able to forward, while everything else on :53 is denied.
///
/// Loopback is exempt from Windows Filtering Platform rules, so apps querying
/// 127.0.0.1:53 (the Obstruo proxy) are unaffected. Obstruo's upstream clients
/// are IPv4-only (DnsSettingsManager collects IPv4 DNS), so all IPv6 :53 is
/// blocked outright.
///
/// Applied on service start (after BackupAndSetDns, so upstreams are known) and
/// on each network change; removed only on clean PIN-confirmed uninstall.
/// </summary>
public sealed class Dns53Firewall
{
    private readonly DnsSettingsManager _dnsSettings;
    private readonly ILogger<Dns53Firewall> _logger;

    internal const string RulePrefix = "Obstruo-BlockDNS53";

    // Every rule name this class manages — the single source of truth for
    // apply, remove, and the health-check count.
    internal static readonly string[] RuleNames =
    [
        $"{RulePrefix}-v4-UDP",
        $"{RulePrefix}-v4-TCP",
        $"{RulePrefix}-v6-UDP",
        $"{RulePrefix}-v6-TCP",
    ];

    public Dns53Firewall(DnsSettingsManager dnsSettings, ILogger<Dns53Firewall> logger)
    {
        _dnsSettings = dnsSettings;
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// (Re)applies the :53 block. Idempotent — each rule is deleted then re-added,
    /// so it is safe to call on every startup and on every network change (the
    /// upstream set can shift when the active network changes).
    /// </summary>
    public void Apply()
    {
        var upstreams = _dnsSettings.GetUpstreamDnsServers();

        // Only IPv4 upstreams are ever used; parse defensively and ignore the
        // rest. If parsing somehow yields nothing, fall back to Cloudflare so we
        // never build an all-inclusive block that would sever Obstruo's own
        // upstream and fail the whole machine closed.
        var allowV4 = upstreams
            .Select(s => IPAddress.TryParse(s, out var ip) ? ip : null)
            .Where(ip => ip is { AddressFamily: AddressFamily.InterNetwork })
            .Select(ip => ip!.ToString())
            .Distinct()
            .ToList();

        if (allowV4.Count == 0)
            allowV4.Add("1.1.1.1");

        var blockRanges = ComputeIpv4Complement(allowV4);
        var remoteIpV4 = string.Join(",", blockRanges);

        AddBlockRule(RuleNames[0], "UDP", remoteIpV4);
        AddBlockRule(RuleNames[1], "TCP", remoteIpV4);

        // IPv6: no upstream is ever IPv6, so block the whole outbound :53 space.
        AddBlockRule(RuleNames[2], "UDP", remoteIp: null);
        AddBlockRule(RuleNames[3], "TCP", remoteIp: null);

        _logger.LogInformation(
            "Classic-DNS :53 block applied — allowed upstream(s): {Allow}; IPv6 :53 blocked entirely",
            string.Join(", ", allowV4));
    }

    /// <summary>Removes every :53 block rule. Clean-uninstall only.</summary>
    public void Remove()
    {
        foreach (var name in RuleNames)
            RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");
        _logger.LogInformation("Classic-DNS :53 block rules removed");
    }

    // ── IPv4 complement ─────────────────────────────────────────────────────────

    /// <summary>
    /// Given the IPv4 addresses to leave reachable, returns the inclusive netsh
    /// <c>remoteip</c> ranges covering the entire 0.0.0.0–255.255.255.255 space
    /// EXCEPT those addresses. Pure and deterministic — unit-tested. Input that
    /// doesn't parse as IPv4 is ignored.
    /// </summary>
    internal static List<string> ComputeIpv4Complement(IEnumerable<string> allowedIps)
    {
        var allowed = allowedIps
            .Select(s => IPAddress.TryParse(s, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
                ? ToUInt32(ip)
                : (uint?)null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .Distinct()
            .OrderBy(v => v)
            .ToList();

        var ranges = new List<string>();
        long cursor = 0;                 // next address not yet covered
        const long max = 0xFFFFFFFFL;    // 255.255.255.255

        foreach (var a in allowed)
        {
            if (a > cursor)
                ranges.Add($"{FromUInt32((uint)cursor)}-{FromUInt32((uint)(a - 1))}");
            cursor = (long)a + 1;        // skip the allowed address itself
        }

        if (cursor <= max)
            ranges.Add($"{FromUInt32((uint)cursor)}-{FromUInt32((uint)max)}");

        return ranges;
    }

    private static uint ToUInt32(IPAddress ip)
    {
        var b = ip.GetAddressBytes(); // network order, big-endian
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static string FromUInt32(uint v)
        => $"{(v >> 24) & 0xFF}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";

    // ── netsh ───────────────────────────────────────────────────────────────────

    private void AddBlockRule(string name, string protocol, string? remoteIp)
    {
        RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");

        var ipClause = string.IsNullOrEmpty(remoteIp) ? "" : $"remoteip={remoteIp} ";
        RunNetsh(
            $"advfirewall firewall add rule " +
            $"name=\"{name}\" " +
            $"dir=out " +
            $"action=block " +
            $"protocol={protocol} " +
            ipClause +
            $"remoteport=53 " +
            $"enable=yes " +
            $"profile=any");
    }

    private void RunNetsh(string args)
    {
        try
        {
            var result = Obstruo.Shared.ProcessRunner.Run("netsh", args);

            if (!result.Exited)
                _logger.LogWarning("netsh timed out and was killed: {Args}", args);
            else if (result.ExitCode != 0 && !args.Contains("delete"))
                _logger.LogWarning("netsh failed (exit {Code}): {Args} | {Error}",
                    result.ExitCode, args, result.StdErr);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run netsh: {Args}", args);
        }
    }
}
