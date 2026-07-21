using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: SupportedOSPlatform("windows")]

namespace Obstruo.Watchdog;

/// <summary>
/// Obstruo Watchdog — launched by the installer at startup.
///
/// Responsibilities:
///   1. Monitor the installer process PID passed as argv[0].
///   2. When the installer exits, check HKLM\SOFTWARE\Obstruo\PartialInstall.
///   3. Flag present  = installer died mid-flight → restore DNS + show recovery UI.
///   4. Flag absent   = installer completed cleanly → exit silently.
///
/// The partial-install flag is written by the installer before the DNS change
/// step (point of no return) and cleared only after the service is verified
/// running. If the installer crashes anywhere between those two points, the
/// Watchdog catches it here.
///
/// The Watchdog runs as WinExe (no console window). It is intentionally
/// dependency-free — no reference to Obstruo.Service or Obstruo.Shared.
/// All DNS restore logic is self-contained via netsh.
/// </summary>
internal static class Program
{
    private const string RegKeyPath = @"SOFTWARE\Obstruo";
    private const string PartialInstall = "PartialInstall";

    private static readonly string BackupPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Obstruo",
        "dns_backup.json");

    private const string BootCheckArg = "--boot-check";

    [STAThread]
    private static int Main(string[] args)
    {
        // ── Boot-recovery mode ────────────────────────────────────────────────
        // Registered as an elevated (/RL HIGHEST) logon scheduled task by the
        // installer before the point of no return. If the MACHINE reboots
        // mid-install (not just the installer process dying), this runs on next
        // logon and checks the flag directly — there is no installer process to
        // wait for. Elevation matters: the netsh DNS-restore calls below fail on
        // a filtered token, which is why this is a scheduled task and not a
        // RunOnce entry. The task re-fires every logon until deleted, so this
        // mode always deletes it after one run.
        var bootCheck = args.Length == 0
            || args[0].Equals(BootCheckArg, StringComparison.OrdinalIgnoreCase);

        if (!bootCheck)
        {
            // ── Parse installer PID ────────────────────────────────────────────
            if (!int.TryParse(args[0], out var installerPid))
            {
                ShowError(
                    "Watchdog launched without a valid installer PID.\n\n" +
                    "This process should only be started by the Obstruo installer.");
                return 1;
            }

            // ── Wait for installer process to exit ─────────────────────────────
            try
            {
                var installer = Process.GetProcessById(installerPid);
                installer.WaitForExit();
            }
            catch (ArgumentException)
            {
                // Process already gone — still need to check the flag
            }
            catch (Exception ex)
            {
                ShowError(
                    $"Watchdog lost track of the installer process (PID {installerPid}).\n\n" +
                    $"Details: {ex.Message}\n\n" +
                    "Checking for partial install state...");
            }
        }

        // ── Check partial-install flag ────────────────────────────────────────
        var isPartialInstall = CheckPartialInstallFlag();

        // A boot-check run was triggered by the logon scheduled task — remove it
        // so it doesn't fire again on every future logon. One recovery attempt
        // per crash, mirroring the old RunOnce semantics.
        if (bootCheck)
            DeleteRecoveryTask();

        if (!isPartialInstall)
        {
            // Clean exit — installer completed successfully. Nothing to do.
            return 0;
        }

        // ── Installer crashed mid-flight — run recovery ───────────────────────
        RunRecovery();
        return 0;
    }

    /// <summary>Best-effort removal of the installer's logon recovery task.</summary>
    private static void DeleteRecoveryTask()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe",
                "/Delete /F /TN ObstruoWatchdogRecovery")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(10_000);
        }
        catch
        {
            // Best effort — a leftover task is annoying, not dangerous.
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  PARTIAL INSTALL FLAG
    // ═══════════════════════════════════════════════════════════════════════════

    private static bool CheckPartialInstallFlag()
    {
        try
        {
            var value = Registry.GetValue(
                $@"HKEY_LOCAL_MACHINE\{RegKeyPath}",
                PartialInstall,
                null) as string;

            return value == "1";
        }
        catch
        {
            // If we can't read the registry, assume clean — don't touch DNS
            return false;
        }
    }

    private static void ClearPartialInstallFlag()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegKeyPath, writable: true);
            key?.DeleteValue(PartialInstall, throwOnMissingValue: false);
        }
        catch
        {
            // Best effort — not fatal
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  RECOVERY
    // ═══════════════════════════════════════════════════════════════════════════

    private static void RunRecovery()
    {
        // Show immediate notice so the user knows something is happening
        MessageBox.Show(
            "The Obstruo installer did not complete successfully.\n\n" +
            "Your internet connection may be affected. " +
            "Obstruo is now attempting to restore your original DNS settings automatically.\n\n" +
            "Click OK to begin recovery.",
            "Obstruo — Installation Recovery",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);

        var restored = RestoreDns();

        if (restored)
        {
            // Clear the flag — recovery succeeded
            ClearPartialInstallFlag();

            MessageBox.Show(
                "DNS settings have been restored successfully.\n\n" +
                "Your internet connection should be working normally again.\n\n" +
                "You may try running the Obstruo installer again when ready.",
                "Obstruo — Recovery Complete",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        else
        {
            MessageBox.Show(
                "Obstruo could not automatically restore your DNS settings.\n\n" +
                "To fix your internet connection manually:\n\n" +
                "1. Open Settings → Network & Internet\n" +
                "2. Click your active network adapter\n" +
                "3. Click Edit under DNS server assignment\n" +
                "4. Switch from Manual to Automatic (DHCP)\n\n" +
                "Or run this in an elevated Command Prompt:\n" +
                "    netsh interface ip set dns name=\"[your adapter]\" dhcp\n\n" +
                "Contact support if you need further assistance.",
                "Obstruo — Recovery Failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  DNS RESTORE
    //  Mirrors DnsSettingsManager.RestoreDns() from Obstruo.Service.
    //  Self-contained — no reference to Service project.
    // ═══════════════════════════════════════════════════════════════════════════

    private static bool RestoreDns()
    {
        if (!File.Exists(BackupPath))
        {
            // No backup — can't restore. Try setting DHCP on all active adapters
            // as a best-effort recovery.
            return RestoreDhcpFallback();
        }

        DnsBackup? backup;

        try
        {
            var json = File.ReadAllText(BackupPath);
            backup = JsonSerializer.Deserialize<DnsBackup>(json);
        }
        catch
        {
            // Backup file corrupt — fall back to DHCP
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
                {
                    RunNetsh(
                        $"interface ipv4 add dns name=\"{adapter.Name}\" addr={adapter.DnsServers[i]} index={i + 1}");
                }
            }
            else
            {
                // No DNS servers in backup for this adapter — set DHCP
                ok = RunNetsh($"interface ipv4 set dns name=\"{adapter.Name}\" dhcp");
            }

            // Undo the ::1 IPv6 pin the service/installer applied — restore to DHCP.
            RunNetsh($"interface ipv6 set dns name=\"{adapter.Name}\" dhcp");

            if (!ok) allSucceeded = false;
        }

        if (allSucceeded)
        {
            try { File.Delete(BackupPath); } catch { /* best effort */ }
        }

        return allSucceeded;
    }

    /// <summary>
    /// Last-resort recovery when no backup exists or backup is unreadable.
    /// Sets all non-loopback active adapters to DHCP DNS.
    /// </summary>
    private static bool RestoreDhcpFallback()
    {
        var allSucceeded = true;

        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                continue;

            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                continue;

            var ok = RunNetsh($"interface ipv4 set dns name=\"{nic.Name}\" dhcp");
            if (!ok) allSucceeded = false;

            RunNetsh($"interface ipv6 set dns name=\"{nic.Name}\" dhcp");
        }

        return allSucceeded;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════════════════════

    private static bool RunNetsh(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)!;
            process.WaitForExit(5_000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void ShowError(string message)
    {
        MessageBox.Show(
            message,
            "Obstruo Watchdog",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}

// ── Backup models (mirrored from Obstruo.Service — no project reference) ─────

internal sealed class DnsBackup
{
    public DateTime BackedUpAt { get; set; }
    public List<AdapterDnsInfo> Adapters { get; set; } = [];
}

internal sealed class AdapterDnsInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsDhcp { get; set; }
    public List<string> DnsServers { get; set; } = [];
}