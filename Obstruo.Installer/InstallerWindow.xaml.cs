using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;

namespace Obstruo.Installer;

// ── Step view model ────────────────────────────────────────────────────────────

internal sealed class StepItem
{
    public string Icon { get; set; } = "○";
    public string Label { get; set; } = "";
    public string IconColor { get; set; } = "#FF6B7A99";
    public string LabelColor { get; set; } = "#FF6B7A99";
}

// ── Window code-behind ─────────────────────────────────────────────────────────

public partial class InstallerWindow : Window
{
    private static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Obstruo");
    private const string UiExeName = "Obstruo.UI.exe";

    /// <summary>
    /// Version of the EULA embedded in this installer build (single source:
    /// ObstruoVersion.EulaVersion). Must be bumped whenever Resources\EULA.txt
    /// materially changes, so upgrades can detect that re-acceptance is required.
    /// </summary>
    private static readonly string EulaVersion = Obstruo.Shared.ObstruoVersion.EulaVersion;

    private const string EulaResourceName = "Obstruo.Installer.Resources.EULA.txt";

    private static readonly string[] StepLabels =
    [
        "Verify administrator privileges",
        "Check for existing installation",
        "Copy files to Program Files",
        "Register Windows Service",
        "Back up DNS settings",
        "Enable DNS protection",
        "Start Obstruo service",
        "Verify installation"
    ];

    private readonly ObservableCollection<StepItem> _steps = [];
    private CancellationTokenSource? _cts;
    private bool _installing;
    private bool _finished;
    private bool _eulaAccepted;

    public InstallerWindow()
    {
        InitializeComponent();

        // Populate step list with inactive state
        foreach (var label in StepLabels)
            _steps.Add(new StepItem { Label = label });

        StepList.ItemsSource = _steps;

        LoadEulaText();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  EULA
    // ═══════════════════════════════════════════════════════════════════════════

    private void LoadEulaText()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(EulaResourceName);

            if (stream is null)
            {
                FailEulaLoad();
                return;
            }

            using var reader = new StreamReader(stream);
            TxtEula.Text = reader.ReadToEnd();
        }
        catch
        {
            FailEulaLoad();
        }
    }

    /// <summary>
    /// Fail closed: if the license text cannot be shown, acceptance is
    /// impossible and installation must not proceed.
    /// </summary>
    private void FailEulaLoad()
    {
        TxtEula.Text =
            "ERROR: The License Agreement could not be loaded.\n\n" +
            "This installer appears to be damaged. Installation cannot continue.\n" +
            "Please download a fresh copy of the Obstruo Security installer.";

        ChkAgree.IsEnabled = false;
        BtnAccept.IsEnabled = false;
    }

    private void ChkAgree_Changed(object sender, RoutedEventArgs e)
    {
        BtnAccept.IsEnabled = ChkAgree.IsChecked == true;
    }

    private void BtnAccept_Click(object sender, RoutedEventArgs e)
    {
        if (ChkAgree.IsChecked != true) return;

        _eulaAccepted = true;
        EulaOverlay.Visibility = Visibility.Collapsed;
    }

    private void BtnDecline_Click(object sender, RoutedEventArgs e)
    {
        // Decline = exit. Nothing has been written to the system.
        Application.Current.Shutdown();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  BUTTON HANDLERS
    // ═══════════════════════════════════════════════════════════════════════════

    private async void BtnInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_installing || _finished) return;

        // Defense in depth: the install view is unreachable while the EULA
        // overlay is visible, but never run the engine without acceptance.
        if (!_eulaAccepted) return;

        _installing = true;
        BtnInstall.IsEnabled = false;
        BtnCancel.IsEnabled = true;

        TxtHeading.Text = "Installing...";
        TxtSubheading.Text = "Please do not close this window.";

        _cts = new CancellationTokenSource();

        var progress = new Progress<InstallProgress>(OnProgress);
        var engine = new InstallEngine(progress, _cts.Token, acceptedEulaVersion: EulaVersion);

        var success = await engine.RunAsync();

        _installing = false;
        _finished = true;

        BtnCancel.IsEnabled = false;
        BtnInstall.IsEnabled = false;

        if (success)
        {
            TxtHeading.Text = "Installation Complete";
            TxtSubheading.Text = "Click Finish to set up your PIN and password.";
            BtnInstall.Content = "Finish Setup";
            BtnInstall.IsEnabled = true;
            BtnInstall.Click -= BtnInstall_Click;
            BtnInstall.Click += BtnFinish_Click;
        }
        else
        {
            TxtHeading.Text = "Installation Failed";
            TxtSubheading.Text = "See the log below for details. Your system has been restored.";
            BtnInstall.Content = "Close";
            BtnInstall.IsEnabled = true;
            BtnInstall.Click -= BtnInstall_Click;
            BtnInstall.Click += (_, _) => Application.Current.Shutdown();
        }
    }

    private void BtnFinish_Click(object sender, RoutedEventArgs e)
    {
        // UI is installed to the ui\ subdirectory by InstallEngine.
        var uiDir = Path.Combine(InstallDir, "ui");
        var uiPath = Path.Combine(uiDir, UiExeName);

        if (!File.Exists(uiPath))
        {
            AppendLog($"ERROR: {uiPath} not found. Cannot launch setup wizard.");
            TxtStatus.Text = "✕  Setup wizard not found — launch Obstruo manually from Start Menu.";
            TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x4C, 0x6A));
            return; // Do NOT shut down — leave the log visible so the failure is diagnosable
        }

        try
        {
            // UseShellExecute=false so the wizard inherits this installer's elevated
            // token directly. The service now requires an elevated caller to set the
            // initial PIN/password, and inheriting elevation here avoids a second UAC
            // prompt right after the (already elevated) installer.
            Process.Start(new ProcessStartInfo
            {
                FileName = uiPath,
                WorkingDirectory = uiDir,
                Arguments = "--first-run",
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR launching setup wizard: {ex.Message}");
            TxtStatus.Text = "✕  Failed to launch setup wizard — see log.";
            TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x4C, 0x6A));
            return; // Same: keep the installer open on failure
        }

        Application.Current.Shutdown();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_installing) { Application.Current.Shutdown(); return; }

        BtnCancel.IsEnabled = false;
        TxtStatus.Text = "Cancelling...";
        _cts?.Cancel();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        // Close while the EULA is showing = decline. Nothing written; just exit.
        if (!_eulaAccepted)
        {
            Application.Current.Shutdown();
            return;
        }

        if (_installing)
        {
            var result = MessageBox.Show(
                "Installation is in progress. Cancelling now may leave your system in a " +
                "partial state. The Obstruo Watchdog will restore your DNS settings automatically.\n\n" +
                "Are you sure you want to cancel?",
                "Cancel Installation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;
            _cts?.Cancel();
        }

        Application.Current.Shutdown();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  PROGRESS HANDLER
    // ═══════════════════════════════════════════════════════════════════════════

    private void OnProgress(InstallProgress p)
    {
        // Always called on UI thread via Progress<T>
        AppendLog(p.Message);

        // Update the progress bar
        if (p.Step > 0)
        {
            ProgressBar.Value = p.Step;
            TxtProgressPct.Text = $"{(int)((p.Step / (double)p.TotalSteps) * 100)}%";
        }

        // Update TxtStatus
        TxtStatus.Text = p.Status switch
        {
            InstallStatus.Failed => "✕  " + p.Message,
            InstallStatus.Warning => "⚠  " + p.Message,
            InstallStatus.Complete => "✓  Installation complete",
            InstallStatus.Cancelled => "Installation cancelled",
            _ => ""
        };

        TxtStatus.Foreground = p.Status switch
        {
            InstallStatus.Failed => new SolidColorBrush(Color.FromRgb(0xFF, 0x4C, 0x6A)),
            InstallStatus.Warning => new SolidColorBrush(Color.FromRgb(0xD4, 0xAA, 0x7C)),
            InstallStatus.Complete => new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x84)),
            _ => new SolidColorBrush(Color.FromRgb(0x6B, 0x7A, 0x99))
        };

        // Update step indicators
        if (p.Step < 1 || p.Step > _steps.Count) return;

        var idx = p.Step - 1;

        switch (p.Status)
        {
            case InstallStatus.Running:
                _steps[idx].Icon = "▶";
                _steps[idx].IconColor = "#FF6A5CFF";
                _steps[idx].LabelColor = "#FFE8EAF0";
                break;

            case InstallStatus.StepComplete:
                _steps[idx].Icon = "✓";
                _steps[idx].IconColor = "#FF3DDC84";
                _steps[idx].LabelColor = "#FF3DDC84";
                break;

            case InstallStatus.Warning:
                _steps[idx].Icon = "⚠";
                _steps[idx].IconColor = "#FFD4AA7C";
                _steps[idx].LabelColor = "#FFD4AA7C";
                break;

            case InstallStatus.Failed:
                _steps[idx].Icon = "✕";
                _steps[idx].IconColor = "#FFFF4C6A";
                _steps[idx].LabelColor = "#FFFF4C6A";
                break;
        }

        // Force ItemsControl to refresh — StepItem is not INotifyPropertyChanged
        StepList.ItemsSource = null;
        StepList.ItemsSource = _steps;

        // Auto-scroll log
        LogScroller.ScrollToBottom();
    }

    private void AppendLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var current = TxtLog.Text;
        TxtLog.Text = string.IsNullOrEmpty(current)
            ? $"[{timestamp}]  {message}"
            : $"{current}\n[{timestamp}]  {message}";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  WINDOW DRAG
    // ═══════════════════════════════════════════════════════════════════════════

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
            DragMove();
    }
}