using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Diagnostics;

namespace Obstruo.Service.Dns;

/// <summary>
/// Two-layer encrypted-DNS blocking:
/// Layer 1 — Windows Firewall rules:
///   - outbound TCP 443 AND UDP 443 (HTTP/3/QUIC) to known DoH provider IPs,
///   - outbound TCP 853 (DNS-over-TLS) and UDP 853 (DNS-over-QUIC) to ANY IP —
///     port 853 is IANA-assigned to encrypted DNS, so a global block breaks
///     nothing else.
/// Layer 2 — Browser registry policies disabling DoH in Chrome, Edge, and Firefox.
/// Applied on service start. Removed on clean uninstall.
/// </summary>
public sealed class DoHBlocker
{
    private readonly ILogger<DoHBlocker> _logger;

    // Firewall rule name prefixes — used to identify and remove Obstruo rules on uninstall
    private const string FirewallRulePrefix = "Obstruo-BlockDoH";
    private const string DoTRulePrefix = "Obstruo-BlockDoT";

    // Known DoH provider IPs to block outbound HTTPS (port 443) to
    private static readonly string[] DoHProviderIps =
    [
        // Cloudflare
        "1.1.1.1",
        "1.0.0.1",
        "2606:4700:4700::1111",
        "2606:4700:4700::1001",

        // Google
        "8.8.8.8",
        "8.8.4.4",
        "2001:4860:4860::8888",
        "2001:4860:4860::8844",

        // NextDNS
        "45.90.28.0",
        "45.90.30.0",

        // Quad9
        "9.9.9.9",
        "149.112.112.112",

        // AdGuard
        "94.140.14.14",
        "94.140.15.15",

        // OpenDNS
        "208.67.222.222",
        "208.67.220.220",

        // Mullvad
        "194.242.2.2",
        "194.242.2.3",
        "2a07:e340::2",
        "2a07:e340::3",

        // ControlD
        "76.76.2.0",
        "76.76.10.0",

        // DNS.SB
        "185.222.222.222",
        "45.11.45.11",
    ];

    public DoHBlocker(ILogger<DoHBlocker> logger)
    {
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies both DoH blocking layers.
    /// Safe to call on every startup — firewall rules use unique names and are idempotent.
    /// </summary>
    public void Apply()
    {
        _logger.LogInformation("Applying DoH blocking");
        ApplyFirewallRules();
        ApplyBrowserPolicies();
        _logger.LogInformation("DoH blocking applied");
    }

    /// <summary>
    /// Removes all DoH blocking. Called ONLY on clean PIN-confirmed uninstall.
    /// </summary>
    public void Remove()
    {
        _logger.LogInformation("Removing DoH blocking");
        RemoveFirewallRules();
        RemoveBrowserPolicies();
        _logger.LogInformation("DoH blocking removed");
    }

    /// <summary>
    /// Startup/health assertion (finding H3): confirms the DoT/DoQ port-853
    /// outbound block rules are actually present in the firewall. The v1.0.0
    /// audit found zero 853 rules on a build that was supposed to ship them, so
    /// their presence must be asserted, not assumed. If either is missing the
    /// full rule set is re-applied and <c>false</c> is returned so the caller can
    /// surface it. Rule presence is read back from the firewall itself.
    /// </summary>
    public bool VerifyDoTRulesPresent()
    {
        var present = FirewallRuleExists($"{DoTRulePrefix}-TCP")
                   && FirewallRuleExists($"{DoTRulePrefix}-UDP");

        if (present)
        {
            _logger.LogInformation("DoT/DoQ port-853 firewall rules verified present");
            return true;
        }

        _logger.LogWarning(
            "DoT/DoQ port-853 firewall rules MISSING — re-applying DoH/DoT block rules");
        ApplyFirewallRules();
        return false;
    }

    /// <summary>
    /// True if a firewall rule with the exact name exists. Uses the netsh show
    /// exit code (0 = found, 1 = "No rules match").
    /// </summary>
    private static bool FirewallRuleExists(string name)
    {
        var result = Obstruo.Shared.ProcessRunner.Run(
            "netsh", $"advfirewall firewall show rule name=\"{name}\"");
        return result.Exited && result.ExitCode == 0;
    }

    // ── Layer 1: Firewall rules ───────────────────────────────────────────────

    private void ApplyFirewallRules()
    {
        foreach (var ip in DoHProviderIps)
        {
            // Remove the legacy single-rule name from pre-QUIC builds so an
            // upgrade doesn't leave duplicate rules behind.
            RunNetsh($"advfirewall firewall delete rule name=\"{LegacyRuleName(ip)}\"");

            // DoH over HTTP/2 (TCP 443) and HTTP/3/QUIC (UDP 443)
            AddBlockRule(RuleName(ip, "TCP"), "TCP", 443, ip);
            AddBlockRule(RuleName(ip, "UDP"), "UDP", 443, ip);

            _logger.LogInformation(
                "Firewall rules added: block outbound TCP+UDP 443 to {Ip}", ip);
        }

        // DoT (TCP 853) and DoQ (UDP 853) to any resolver — not just the known
        // list. Port 853 carries nothing but encrypted DNS.
        AddBlockRule($"{DoTRulePrefix}-TCP", "TCP", 853, remoteIp: null);
        AddBlockRule($"{DoTRulePrefix}-UDP", "UDP", 853, remoteIp: null);
        _logger.LogInformation("Firewall rules added: block outbound TCP+UDP 853 to any IP");
    }

    private void RemoveFirewallRules()
    {
        foreach (var ip in DoHProviderIps)
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{LegacyRuleName(ip)}\"");
            RunNetsh($"advfirewall firewall delete rule name=\"{RuleName(ip, "TCP")}\"");
            RunNetsh($"advfirewall firewall delete rule name=\"{RuleName(ip, "UDP")}\"");
            _logger.LogInformation("Firewall rules removed for {Ip}", ip);
        }

        RunNetsh($"advfirewall firewall delete rule name=\"{DoTRulePrefix}-TCP\"");
        RunNetsh($"advfirewall firewall delete rule name=\"{DoTRulePrefix}-UDP\"");
        _logger.LogInformation("DoT/DoQ port-853 firewall rules removed");
    }

    private static string RuleName(string ip, string protocol)
        => $"{FirewallRulePrefix}-{ip.Replace(":", "_")}-{protocol}";

    /// <summary>Rule name used by builds that only blocked TCP 443.</summary>
    private static string LegacyRuleName(string ip)
        => $"{FirewallRulePrefix}-{ip.Replace(":", "_")}";

    /// <summary>
    /// Adds one outbound block rule (idempotent — deletes any same-named rule
    /// first). remoteIp null = applies to every remote address.
    /// </summary>
    private void AddBlockRule(string name, string protocol, int port, string? remoteIp)
    {
        RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");

        var ipClause = remoteIp is null ? "" : $"remoteip={remoteIp} ";
        RunNetsh(
            $"advfirewall firewall add rule " +
            $"name=\"{name}\" " +
            $"dir=out " +
            $"action=block " +
            $"protocol={protocol} " +
            ipClause +
            $"remoteport={port} " +
            $"enable=yes " +
            $"profile=any");
    }

    // ── Layer 2: Browser policies ─────────────────────────────────────────────

    private void ApplyBrowserPolicies()
    {
        ApplyChromePolicies();
        ApplyEdgePolicies();
        ApplyFirefoxPolicy();
    }

    private void RemoveBrowserPolicies()
    {
        RemoveChromePolicies();
        RemoveEdgePolicies();
        RemoveFirefoxPolicy();
    }

    // Chrome
    private void ApplyChromePolicies()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Policies\Google\Chrome");

            key.SetValue("DnsOverHttpsMode", "off", RegistryValueKind.String);
            _logger.LogInformation("Chrome DoH policy applied");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply Chrome DoH policy — Chrome may not be installed");
        }
    }

    private void RemoveChromePolicies()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Policies\Google\Chrome", writable: true);

            key?.DeleteValue("DnsOverHttpsMode", throwOnMissingValue: false);
            _logger.LogInformation("Chrome DoH policy removed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove Chrome DoH policy");
        }
    }

    // Edge
    private void ApplyEdgePolicies()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Policies\Microsoft\Edge");

            key.SetValue("DnsOverHttpsMode", "off", RegistryValueKind.String);
            _logger.LogInformation("Edge DoH policy applied");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply Edge DoH policy");
        }
    }

    private void RemoveEdgePolicies()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft\Edge", writable: true);

            key?.DeleteValue("DnsOverHttpsMode", throwOnMissingValue: false);
            _logger.LogInformation("Edge DoH policy removed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove Edge DoH policy");
        }
    }

    // Firefox — uses a JSON enterprise policy file. Both the 64-bit
    // (Program Files) and 32-bit (Program Files (x86)) install locations are
    // covered; a 32-bit Firefox reading a policy we only wrote for 64-bit would
    // be an open DoH bypass.
    private static IEnumerable<string> FirefoxPolicyDirs()
    {
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                 })
        {
            var root = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(root))
                yield return Path.Combine(root, "Mozilla Firefox", "distribution");
        }
    }

    private void ApplyFirefoxPolicy()
    {
        foreach (var firefoxPolicyDir in FirefoxPolicyDirs())
        {
            try
            {
                var firefoxPolicyPath = Path.Combine(firefoxPolicyDir, "policies.json");

                Directory.CreateDirectory(firefoxPolicyDir);

                // Merge with existing policy file if present — don't overwrite other policies
                var policy = LoadExistingFirefoxPolicy(firefoxPolicyPath);
                policy["DNSOverHTTPS"] = new { Enabled = false, Locked = true };

                var json = System.Text.Json.JsonSerializer.Serialize(
                    new { policies = policy },
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

                File.WriteAllText(firefoxPolicyPath, json);
                _logger.LogInformation("Firefox DoH policy applied at {Path}", firefoxPolicyPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to apply Firefox DoH policy at {Dir} — Firefox may not be installed there",
                    firefoxPolicyDir);
            }
        }
    }

    private void RemoveFirefoxPolicy()
    {
        foreach (var firefoxPolicyDir in FirefoxPolicyDirs())
        {
            try
            {
                var firefoxPolicyPath = Path.Combine(firefoxPolicyDir, "policies.json");

                if (!File.Exists(firefoxPolicyPath))
                    continue;

                var policy = LoadExistingFirefoxPolicy(firefoxPolicyPath);
                policy.Remove("DNSOverHTTPS");

                if (policy.Count == 0)
                {
                    File.Delete(firefoxPolicyPath);
                }
                else
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(
                        new { policies = policy },
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(firefoxPolicyPath, json);
                }

                _logger.LogInformation("Firefox DoH policy removed from {Path}", firefoxPolicyPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove Firefox DoH policy at {Dir}", firefoxPolicyDir);
            }
        }
    }

    private static Dictionary<string, object> LoadExistingFirefoxPolicy(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<string, object>();

        try
        {
            var json = File.ReadAllText(path);
            var doc = System.Text.Json.JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("policies", out var policiesEl))
            {
                // Deserialize existing policies into a mutable dict
                return System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, object>>(policiesEl.GetRawText())
                    ?? new Dictionary<string, object>();
            }
        }
        catch
        {
            // Corrupt policy file — start fresh
        }

        return new Dictionary<string, object>();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void RunNetsh(string args)
    {
        try
        {
            var result = Obstruo.Shared.ProcessRunner.Run("netsh", args);

            if (!result.Exited)
                _logger.LogWarning("netsh timed out and was killed: {Args}", args);
            // Exit code 1 on delete when rule doesn't exist — not an error
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