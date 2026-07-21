using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Obstruo.Installer;

// ── Progress types ─────────────────────────────────────────────────────────────

internal enum InstallStatus
{
    Running,
    StepComplete,
    Warning,
    Failed,
    Complete,
    Cancelled
}

internal sealed record InstallProgress(
    int Step,
    int TotalSteps,
    string Message,
    InstallStatus Status);

// ── Payload locator ────────────────────────────────────────────────────────────

/// <summary>
/// Locates source binaries for the install operation.
///
/// Release mode: looks for a payload\ folder next to the installer exe,
/// structured as payload\service\, payload\ui\, payload\watchdog\.
///
/// Debug mode: walks up 4 directories from the installer exe to find the
/// solution root, then locates each project's debug output directory.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class InstallPayload
{
    public string ServiceDir { get; init; } = "";
    public string UiDir { get; init; } = "";
    public string WatchdogDir { get; init; } = "";

    public string ServiceExe => Path.Combine(ServiceDir, "Obstruo.Service.exe");
    public string UiExe => Path.Combine(UiDir, "Obstruo.UI.exe");
    public string WatchdogExe => Path.Combine(WatchdogDir, "Obstruo.Watchdog.exe");

    public bool IsValid =>
        Directory.Exists(ServiceDir) && File.Exists(ServiceExe) &&
        Directory.Exists(UiDir) && File.Exists(UiExe) &&
        Directory.Exists(WatchdogDir) && File.Exists(WatchdogExe);

    public static InstallPayload? Locate()
    {
        var baseDir = AppContext.BaseDirectory;

        // ── Release: payload\ folder bundled with installer ───────────────────
        var release = new InstallPayload
        {
            ServiceDir = Path.Combine(baseDir, "payload", "service"),
            UiDir = Path.Combine(baseDir, "payload", "ui"),
            WatchdogDir = Path.Combine(baseDir, "payload", "watchdog")
        };
        if (release.IsValid) return release;

        // ── Debug: sibling project bin\Debug\ outputs ─────────────────────────
        // Walk up: <tfm> → Debug → bin → Obstruo.Installer → [solution]
        var solutionDir = baseDir;
        for (int i = 0; i < 4; i++)
            solutionDir = Path.GetDirectoryName(solutionDir) ?? solutionDir;

        // Probe for whichever TFM directory actually contains the exe instead
        // of hardcoding framework monikers that silently break on TFM bumps.
        var debug = new InstallPayload
        {
            ServiceDir = FindDebugOutput(solutionDir, "Obstruo.Service", "Obstruo.Service.exe") ?? "",
            UiDir = FindDebugOutput(solutionDir, "Obstruo.UI", "Obstruo.UI.exe") ?? "",
            WatchdogDir = FindDebugOutput(solutionDir, "Obstruo.Watchdog", "Obstruo.Watchdog.exe") ?? ""
        };
        return debug.IsValid ? debug : null;
    }

    /// <summary>
    /// Returns the newest bin\Debug\&lt;tfm&gt; directory of a project that
    /// contains the given exe, or null.
    /// </summary>
    private static string? FindDebugOutput(string solutionDir, string project, string exeName)
    {
        var debugDir = Path.Combine(solutionDir, project, "bin", "Debug");
        if (!Directory.Exists(debugDir)) return null;

        return Directory.GetDirectories(debugDir)
            .Where(dir => File.Exists(Path.Combine(dir, exeName)))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}

// ── Install engine ─────────────────────────────────────────────────────────────

/// <summary>
/// Executes the full Obstruo install/upgrade sequence with rollback.
///
/// Step 6 (Set DNS to 127.0.0.1) is the point of no return.
/// The PartialInstall registry flag is written immediately before Step 6
/// and cleared only after Step 8 (service verified running).
/// If the process dies between these two points, Obstruo.Watchdog picks it up
/// on the next boot and restores DNS automatically.
///
/// EULA acceptance (version + UTC timestamp) is recorded to the registry
/// during Step 1, immediately after administrator privileges are confirmed
/// and before any system modification. The installer window must not
/// construct this engine unless the user has accepted the EULA.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class InstallEngine
{
    private const string ServiceName = "ObstruoService";
    private const string ServiceDisplay = "Obstruo Security DNS Filter";
    private const string ServiceDesc = "DNS-layer content filtering and parental control.";
    private static readonly string CurrentVersion = Obstruo.Shared.ObstruoVersion.Current;
    private const string RegKeyPath = @"SOFTWARE\Obstruo";
    private const string ArpKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Obstruo";
    private const int TotalSteps = 8;

    private static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Obstruo");

    private static readonly string ServiceInstallDir = Path.Combine(InstallDir, "service");
    private static readonly string UiInstallDir = Path.Combine(InstallDir, "ui");
    private static readonly string WatchdogInstallDir = Path.Combine(InstallDir, "watchdog");

    private readonly IProgress<InstallProgress> _progress;
    private readonly CancellationToken _ct;
    private readonly string? _acceptedEulaVersion;

    private InstallPayload? _payload;
    private bool _pastPointOfNoReturn;

    public InstallEngine(
        IProgress<InstallProgress> progress,
        CancellationToken ct = default,
        string? acceptedEulaVersion = null)
    {
        _progress = progress;
        _ct = ct;
        _acceptedEulaVersion = acceptedEulaVersion;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ENTRY POINT
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<bool> RunAsync()
    {
        try
        {
            return await ExecuteAsync();
        }
        catch (OperationCanceledException)
        {
            Report(0, "Installation cancelled.", InstallStatus.Cancelled);
            if (_pastPointOfNoReturn)
            {
                Report(0, "Restoring DNS settings — please wait...", InstallStatus.Running);
                DnsHelper.RestoreDns();
                ClearPartialInstallFlag();
            }
            return false;
        }
        catch (Exception ex)
        {
            Report(0, $"Unexpected error: {ex.Message}", InstallStatus.Failed);
            if (_pastPointOfNoReturn)
            {
                DnsHelper.RestoreDns();
                ClearPartialInstallFlag();
            }
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  INSTALL SEQUENCE
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task<bool> ExecuteAsync()
    {
        // ── Pre-step: Launch Watchdog ──────────────────────────────────────────
        // Monitors our PID. If we die after the DNS change, it restores DNS.
        // Best-effort — never blocks install if Watchdog can't be found.
        LaunchWatchdog();

        // ── Step 1: Verify admin ───────────────────────────────────────────────
        Report(1, "Verifying administrator privileges...", InstallStatus.Running);

        if (!IsRunningAsAdmin())
        {
            Report(1, "Administrator privileges required. Right-click the installer and select 'Run as administrator'.",
                InstallStatus.Failed);
            return false;
        }

        Report(1, "Administrator privileges confirmed.", InstallStatus.StepComplete);

        // Record EULA acceptance now that we are confirmed elevated, and
        // before any system modification takes place.
        WriteEulaAcceptance();

        _ct.ThrowIfCancellationRequested();

        // ── Step 2: Check existing installation ───────────────────────────────
        Report(2, "Checking for existing installation...", InstallStatus.Running);
        var mode = DetectInstallMode();

        if (mode == InstallMode.SameVersion)
        {
            Report(2, $"Obstruo Security {CurrentVersion} is already installed.",
                InstallStatus.Warning);
            return true;
        }

        if (mode == InstallMode.Upgrade)
        {
            Report(2, "Existing installation detected — performing in-place upgrade.",
                InstallStatus.StepComplete);

            if (!await StopServiceAsync())
            {
                Report(2,
                    "Could not stop the existing Obstruo service. " +
                    "Close the Obstruo dashboard and try again.",
                    InstallStatus.Failed);
                return false;
            }
        }
        else
        {
            Report(2, "Fresh installation.", InstallStatus.StepComplete);
        }

        _ct.ThrowIfCancellationRequested();

        // ── Step 3: Locate and copy files ─────────────────────────────────────
        Report(3, "Locating installation files...", InstallStatus.Running);
        _payload = InstallPayload.Locate();

        if (_payload is null)
        {
            Report(3,
                "Installation files not found. " +
                "Ensure all projects are built before running the installer.",
                InstallStatus.Failed);
            return false;
        }

        Report(3, "Copying files to Program Files\\Obstruo...", InstallStatus.Running);

        if (!CopyFiles(_payload))
        {
            Report(3, "Failed to copy installation files. Check disk space and permissions.",
                InstallStatus.Failed);
            await RollbackAsync(RollbackLevel.FilesOnly, mode);
            return false;
        }

        Report(3, "Files copied successfully.", InstallStatus.StepComplete);
        _ct.ThrowIfCancellationRequested();

        // ── Step 4: Register Windows Service (fresh install only) ─────────────
        Report(4, "Registering Windows Service...", InstallStatus.Running);

        if (mode == InstallMode.Fresh)
        {
            if (!RegisterService())
            {
                Report(4, "Failed to register the Windows Service.", InstallStatus.Failed);
                await RollbackAsync(RollbackLevel.FilesOnly, mode);
                return false;
            }

            ConfigureServiceAutoRestart();
        }

        Report(4, "Service registered.", InstallStatus.StepComplete);
        _ct.ThrowIfCancellationRequested();

        // ── Step 5: Backup DNS settings ───────────────────────────────────────
        Report(5, "Backing up current DNS settings...", InstallStatus.Running);
        DnsHelper.BackupDns();
        Report(5, "DNS settings backed up.", InstallStatus.StepComplete);
        _ct.ThrowIfCancellationRequested();

        // ══════════════════════════════════════════════════════════════════════
        //  POINT OF NO RETURN
        //  Write the PartialInstall flag BEFORE touching DNS.
        //  Cleared only after service is verified running (Step 8).
        // ══════════════════════════════════════════════════════════════════════
        WritePartialInstallFlag();
        _pastPointOfNoReturn = true;

        // ── Step 6: Set DNS to 127.0.0.1 ──────────────────────────────────────
        Report(6, "Enabling DNS protection...", InstallStatus.Running);

        if (!DnsHelper.SetDnsToLocalhost())
        {
            Report(6, "Failed to set system DNS to 127.0.0.1.", InstallStatus.Failed);
            await RollbackAsync(RollbackLevel.Full, mode);
            return false;
        }

        Report(6, "DNS protection enabled.", InstallStatus.StepComplete);

        // ── Step 7: Start service ──────────────────────────────────────────────
        Report(7, "Starting Obstruo service...", InstallStatus.Running);

        if (!await StartServiceAsync())
        {
            Report(7, "Failed to start the Obstruo service.", InstallStatus.Failed);
            await RollbackAsync(RollbackLevel.Full, mode);
            return false;
        }

        Report(7, "Service started.", InstallStatus.StepComplete);

        // ── Step 8: Verify ─────────────────────────────────────────────────────
        Report(8, "Verifying installation...", InstallStatus.Running);

        if (!await VerifyInstallationAsync())
        {
            Report(8, "Service verification failed. The service started but is not responding.",
                InstallStatus.Failed);
            await RollbackAsync(RollbackLevel.Full, mode);
            return false;
        }

        // ── Finalize ───────────────────────────────────────────────────────────
        WriteVersionToRegistry();
        WriteArpEntry();
        ClearPartialInstallFlag();
        _pastPointOfNoReturn = false;

        Report(8, "Obstruo Security installed successfully.", InstallStatus.Complete);

        // UI launch is handled exclusively by the installer window's
        // "Finish Setup" button (with --first-run). The engine does not launch it.
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  INSTALL MODE
    // ═══════════════════════════════════════════════════════════════════════════

    private enum InstallMode { Fresh, Upgrade, SameVersion }

    private static InstallMode DetectInstallMode()
    {
        try
        {
            var installedVersion = Registry.GetValue(
                $@"HKEY_LOCAL_MACHINE\{RegKeyPath}",
                "Version",
                null) as string;

            if (string.IsNullOrWhiteSpace(installedVersion))
                return InstallMode.Fresh;

            return string.Equals(installedVersion, CurrentVersion, StringComparison.Ordinal)
                ? InstallMode.SameVersion
                : InstallMode.Upgrade;
        }
        catch
        {
            return InstallMode.Fresh;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  FILE OPERATIONS
    // ═══════════════════════════════════════════════════════════════════════════

    private static bool CopyFiles(InstallPayload payload)
    {
        try
        {
            CopyDirectory(payload.ServiceDir, ServiceInstallDir);
            CopyDirectory(payload.UiDir, UiInstallDir);
            CopyDirectory(payload.WatchdogDir, WatchdogInstallDir);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);

        foreach (var subDir in Directory.GetDirectories(source))
            CopyDirectory(subDir, Path.Combine(target, Path.GetFileName(subDir)));
    }

    private static void DeleteInstallDir()
    {
        try
        {
            if (Directory.Exists(InstallDir))
                Directory.Delete(InstallDir, recursive: true);
        }
        catch { /* best effort */ }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  SERVICE MANAGEMENT
    // ═══════════════════════════════════════════════════════════════════════════

    private static bool RegisterService()
    {
        var exePath = Path.Combine(ServiceInstallDir, "Obstruo.Service.exe");

        var registered = RunSc(
            $"create {ServiceName} " +
            $"binPath=\"{exePath}\" " +
            $"start=auto " +
            $"DisplayName=\"{ServiceDisplay}\"");

        if (!registered) return false;

        RunSc($"description {ServiceName} \"{ServiceDesc}\"");
        return true;
    }

    private static void ConfigureServiceAutoRestart()
    {
        // Reset failure count after 24h.
        // Actions: restart after 5s → 30s → 60s.
        RunSc($"failure {ServiceName} reset=86400 actions=restart/5000/restart/30000/restart/60000");
    }

    private static async Task<bool> StopServiceAsync()
    {
        RunSc($"stop {ServiceName}");

        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(500);
            if (!IsServiceRunning()) return true;
        }

        return !IsServiceRunning();
    }

    private static async Task<bool> StartServiceAsync()
    {
        RunSc($"start {ServiceName}");

        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            if (IsServiceRunning()) return true;
        }

        return IsServiceRunning();
    }

    private static async Task<bool> VerifyInstallationAsync()
    {
        // Give the service time to fully initialize (DNS bind, DB init)
        await Task.Delay(3_000);

        if (!IsServiceRunning()) return false;
        if (!DnsHelper.IsDnsSetToLocalhost()) return false;

        return true;
    }

    private static bool IsServiceRunning()
    {
        try
        {
            return Obstruo.Shared.ProcessRunner
                .Run("sc.exe", $"query {ServiceName}", timeoutMs: 3_000)
                .StdOut.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteService()
    {
        RunSc($"stop {ServiceName}");
        Thread.Sleep(2_000);
        RunSc($"delete {ServiceName}");
    }

    private static bool RunSc(string args)
    {
        try
        {
            return Obstruo.Shared.ProcessRunner.Run("sc.exe", args, timeoutMs: 10_000).Success;
        }
        catch
        {
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ROLLBACK
    // ═══════════════════════════════════════════════════════════════════════════

    private enum RollbackLevel
    {
        FilesOnly,  // Delete copied files (pre-service-registration failures)
        Full        // Restore DNS + delete service + delete files
    }

    private async Task RollbackAsync(RollbackLevel level, InstallMode mode)
    {
        if (mode == InstallMode.Upgrade)
        {
            // NEVER delete the service or install dir on a failed upgrade — the
            // machine already had a working, PIN-protected Obstruo. Deleting it
            // would strip protection with no credential, and leaving the (already
            // stopped) service down would leave DNS pinned with nothing serving
            // it. Bring the existing service back up; it re-pins DNS and resumes
            // protection. If it won't start, restore DNS so the user still has
            // working internet.
            if (await StartServiceAsync())
            {
                Report(0, "Upgrade failed — the previous Obstruo service was restarted.",
                    InstallStatus.Warning);
            }
            else
            {
                DnsHelper.RestoreDns();
                Report(0,
                    "Upgrade failed and the service could not be restarted — DNS was restored. " +
                    "Reinstall Obstruo to restore protection.",
                    InstallStatus.Warning);
            }

            ClearPartialInstallFlag();
            return;
        }

        // Fresh install rollback — tear everything down.
        if (level == RollbackLevel.Full)
        {
            DnsHelper.RestoreDns();
            DeleteService();
            await Task.Delay(1_000);
        }

        DeleteInstallDir();
        ClearPartialInstallFlag();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  REGISTRY
    // ═══════════════════════════════════════════════════════════════════════════

    // Scheduled task that lets Obstruo.Watchdog restore DNS on the next logon if
    // the whole machine reboots between the point of no return and verification.
    //
    // A scheduled task with /RL HIGHEST is used instead of an HKLM RunOnce entry:
    // RunOnce programs run with the logging-on user's FILTERED (non-elevated)
    // token, so the Watchdog's netsh DNS-restore calls would all fail. The task
    // runs elevated in the installing admin's interactive session, so both the
    // netsh calls and the recovery dialogs work. The Watchdog deletes the task
    // itself after a boot-check run (see Obstruo.Watchdog); a successful install
    // deletes it via ClearPartialInstallFlag.
    private const string RecoveryTaskName = "ObstruoWatchdogRecovery";

    private static void WritePartialInstallFlag()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(RegKeyPath, writable: true);
            key?.SetValue("PartialInstall", "1", RegistryValueKind.String);
        }
        catch { /* best effort */ }

        RegisterBootWatchdog();
    }

    private static void ClearPartialInstallFlag()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegKeyPath, writable: true);
            key?.DeleteValue("PartialInstall", throwOnMissingValue: false);
        }
        catch { /* best effort */ }

        UnregisterBootWatchdog();
    }

    /// <summary>
    /// Registers the installed Watchdog to run elevated at next logon in
    /// boot-check mode (see RecoveryTaskName comment for why a scheduled task
    /// and not RunOnce). A successful install removes the task via
    /// ClearPartialInstallFlag; after a crash the Watchdog removes it itself.
    /// </summary>
    private static void RegisterBootWatchdog()
    {
        try
        {
            var watchdogExe = Path.Combine(WatchdogInstallDir, "Obstruo.Watchdog.exe");
            if (!File.Exists(watchdogExe)) return;

            // \" escapes survive ProcessRunner's argument string — the task
            // action becomes: "C:\...\Obstruo.Watchdog.exe" --boot-check
            Obstruo.Shared.ProcessRunner.Run("schtasks.exe",
                $"/Create /F /TN {RecoveryTaskName} /SC ONLOGON /RL HIGHEST " +
                $"/TR \"\\\"{watchdogExe}\\\" --boot-check\"",
                timeoutMs: 10_000);
        }
        catch { /* best effort — the child-process Watchdog is the primary net */ }
    }

    private static void UnregisterBootWatchdog()
    {
        try
        {
            Obstruo.Shared.ProcessRunner.Run("schtasks.exe",
                $"/Delete /F /TN {RecoveryTaskName}", timeoutMs: 10_000);
        }
        catch { /* best effort */ }
    }

    private static void WriteVersionToRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(RegKeyPath, writable: true);
            key?.SetValue("Version", CurrentVersion, RegistryValueKind.String);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Registers Obstruo in Windows Settings → Apps (Add/Remove Programs).
    /// The UninstallString launches the dashboard with --uninstall, which
    /// routes into the same PIN-gated uninstall flow as the dashboard button —
    /// ARP visibility without an uninstall path that bypasses the credential.
    /// </summary>
    private static void WriteArpEntry()
    {
        try
        {
            var uiExe = Path.Combine(UiInstallDir, "Obstruo.UI.exe");

            using var key = Registry.LocalMachine.CreateSubKey(ArpKeyPath, writable: true);
            if (key is null) return;

            key.SetValue("DisplayName", Obstruo.Shared.ObstruoVersion.ProductName);
            key.SetValue("DisplayVersion", CurrentVersion);
            key.SetValue("Publisher", Obstruo.Shared.ObstruoVersion.Publisher);
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("DisplayIcon", uiExe);
            key.SetValue("UninstallString", $"\"{uiExe}\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            key.SetValue("EstimatedSize", EstimateInstallSizeKb(), RegistryValueKind.DWord);
        }
        catch { /* ARP visibility is best effort — never blocks install */ }
    }

    private static int EstimateInstallSizeKb()
    {
        try
        {
            long bytes = 0;
            foreach (var file in Directory.EnumerateFiles(
                         InstallDir, "*", SearchOption.AllDirectories))
                bytes += new FileInfo(file).Length;
            return (int)Math.Min(bytes / 1024, int.MaxValue);
        }
        catch
        {
            return 0;
        }
    }

    private void WriteEulaAcceptance()
    {
        if (string.IsNullOrWhiteSpace(_acceptedEulaVersion)) return;

        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(RegKeyPath, writable: true);
            key?.SetValue("EulaAcceptedVersion", _acceptedEulaVersion, RegistryValueKind.String);
            key?.SetValue("EulaAcceptedUtc",
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                RegistryValueKind.String);
        }
        catch { /* Acceptance recording is best effort — never blocks install */ }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════════════════════

    private static bool IsRunningAsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void LaunchWatchdog()
    {
        // Release: Watchdog bundled next to installer
        var watchdogExe = Path.Combine(AppContext.BaseDirectory, "Obstruo.Watchdog.exe");

        // Debug: locate from payload
        if (!File.Exists(watchdogExe))
            watchdogExe = InstallPayload.Locate()?.WatchdogExe ?? "";

        if (!File.Exists(watchdogExe)) return;

        try
        {
            Process.Start(new ProcessStartInfo(watchdogExe)
            {
                Arguments = Process.GetCurrentProcess().Id.ToString(),
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch { /* Watchdog is a safety net — never block install */ }
    }

    private void Report(int step, string message, InstallStatus status)
        => _progress.Report(new InstallProgress(step, TotalSteps, message, status));
}