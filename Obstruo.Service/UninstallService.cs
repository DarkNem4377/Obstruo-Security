using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Obstruo.Service.Dns;

namespace Obstruo.Service;

/// <summary>
/// Executes a clean, PIN-confirmed uninstall from inside the service.
///
/// The service runs as LocalSystem, so it already holds every privilege the
/// teardown needs — no separate elevated uninstaller and no UAC prompt. The
/// caller (IpcServer) verifies the PIN/password BEFORE invoking this; a bad
/// credential never reaches here.
///
/// Two phases:
///   1. In-process, synchronous — the security-critical part:
///        - stop tamper detection FIRST (otherwise it would see DNS move off
///          127.0.0.1 and immediately re-pin it, undoing the restore),
///        - restore original DNS (IPv4 + IPv6),
///        - remove DoH blocking (firewall + browser policies),
///        - remove the LAN DNS firewall rules.
///      If this phase succeeds, protection is fully and correctly removed even
///      if the later file cleanup fails.
///   2. Detached, best-effort — cosmetic cleanup:
///        - a small script waits for the IPC reply to flush, stops and deletes
///          the Windows service, then removes install files and registry state.
///      Leftover files are harmless; the service is already neutralized.
/// </summary>
public sealed class UninstallService
{
    private const string ServiceName = "ObstruoService";
    private const string RegKeyPath = @"SOFTWARE\Obstruo";

    private static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Obstruo");

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Obstruo");

    private readonly DnsSettingsManager _dnsSettings;
    private readonly DoHBlocker _dohBlocker;
    private readonly Dns53Firewall _dns53;
    private readonly LanModeService _lanMode;
    // Lazy to break the DI cycle TamperDetector -> IpcServer -> UninstallService
    // -> TamperDetector. Uninstall is a cold path (only Stop(), only on teardown),
    // so deferring resolution here removes the cycle without changing behavior.
    private readonly Lazy<TamperDetector> _tamperDetector;
    private readonly ILogger<UninstallService> _logger;

    public UninstallService(
        DnsSettingsManager dnsSettings,
        DoHBlocker dohBlocker,
        Dns53Firewall dns53,
        LanModeService lanMode,
        Lazy<TamperDetector> tamperDetector,
        ILogger<UninstallService> logger)
    {
        _dnsSettings = dnsSettings;
        _dohBlocker = dohBlocker;
        _dns53 = dns53;
        _lanMode = lanMode;
        _tamperDetector = tamperDetector;
        _logger = logger;
    }

    /// <summary>
    /// Runs the in-process teardown, then schedules the detached cleanup.
    /// Returns (true, null) if protection was successfully removed. The service
    /// process will be stopped shortly afterward by the detached script.
    /// </summary>
    public (bool Success, string? Error) Run()
    {
        _logger.LogWarning("PIN-confirmed uninstall requested — beginning teardown");

        try
        {
            // 1. Stop tamper detection FIRST. If it keeps polling while we restore
            //    DNS, it will treat the restored (non-loopback) DNS as tampering
            //    and re-pin 127.0.0.1 / ::1, defeating the whole uninstall.
            _tamperDetector.Value.Stop();

            // 2. Restore the user's original DNS (IPv4 from backup, IPv6 → DHCP).
            _dnsSettings.RestoreDns();

            // 3. Remove DoH blocking: firewall rules + Chrome/Edge/Firefox policies.
            _dohBlocker.Remove();

            // 3b. Remove the classic-DNS :53 block rules.
            _dns53.Remove();

            // 4. Remove the LAN DNS inbound firewall rules.
            _lanMode.RemoveFirewallRules();

            _logger.LogWarning(
                "Uninstall teardown complete — DNS restored, DoH unblocked, firewall rules removed. " +
                "Scheduling service + file removal.");

            // 5. Schedule detached cleanup (stops/deletes the service, removes files).
            ScheduleCleanup();

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Uninstall teardown failed");
            return (false, "Uninstall failed during teardown. See service logs.");
        }
    }

    /// <summary>
    /// Writes and launches a detached script that finishes the uninstall after
    /// this process has replied to the UI and exited.
    ///
    /// The initial delay gives the IPC success response time to flush to the UI
    /// before `sc stop` tears down the pipe. Everything after the service stop
    /// is best-effort — leftover files never re-enable protection.
    /// </summary>
    private void ScheduleCleanup()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"obstruo-uninstall-{Guid.NewGuid():N}.cmd");

        var script = new StringBuilder();
        script.AppendLine("@echo off");
        // Let the IPC "success" reply reach the UI before we stop the service.
        script.AppendLine("timeout /t 3 /nobreak >nul 2>&1");
        // Stop the service, then wait until SCM reports it STOPPED (max ~30s).
        script.AppendLine($"sc stop {ServiceName} >nul 2>&1");
        script.AppendLine("set /a _tries=0");
        script.AppendLine(":waitloop");
        script.AppendLine($"sc query {ServiceName} | find \"STOPPED\" >nul 2>&1");
        script.AppendLine("if %errorlevel%==0 goto stopped");
        script.AppendLine("set /a _tries+=1");
        script.AppendLine("if %_tries% geq 30 goto stopped");
        script.AppendLine("timeout /t 1 /nobreak >nul 2>&1");
        script.AppendLine("goto waitloop");
        script.AppendLine(":stopped");
        script.AppendLine($"sc delete {ServiceName} >nul 2>&1");
        // Restore the Windows DNS Client if Port53Checker demoted it to manual
        // start when Obstruo claimed port 53. Runs after our service is stopped
        // so port 53 is free for Dnscache to bind again. Only done when the
        // DnscacheDisabled marker says the change was ours.
        if (WasDnscacheDisabledByObstruo())
        {
            script.AppendLine("sc config Dnscache start= auto >nul 2>&1");
            script.AppendLine("sc start Dnscache >nul 2>&1");
        }
        // Best-effort removal — quotes handle the space in "Program Files".
        script.AppendLine($"rmdir /s /q \"{InstallDir}\" >nul 2>&1");
        script.AppendLine($"rmdir /s /q \"{DataDir}\" >nul 2>&1");
        script.AppendLine($"reg delete \"HKLM\\{RegKeyPath}\" /f >nul 2>&1");
        // Remove the Add/Remove Programs entry written by the installer.
        script.AppendLine("reg delete \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Obstruo\" /f >nul 2>&1");
        // Delete the script itself last.
        script.AppendLine("del \"%~f0\" >nul 2>&1");

        File.WriteAllText(scriptPath, script.ToString());

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });

        _logger.LogWarning("Detached uninstall cleanup script launched: {Path}", scriptPath);
    }

    /// <summary>
    /// True if Port53Checker recorded that it demoted the Windows DNS Client
    /// (HKLM\SOFTWARE\Obstruo\DnscacheDisabled = "1").
    /// </summary>
    private static bool WasDnscacheDisabledByObstruo()
    {
        try
        {
            return Registry.GetValue(
                $@"HKEY_LOCAL_MACHINE\{RegKeyPath}", "DnscacheDisabled", null) as string == "1";
        }
        catch
        {
            return false;
        }
    }
}
