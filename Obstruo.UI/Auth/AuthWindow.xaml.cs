using Microsoft.Extensions.Logging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Obstruo.UI.Auth;

/// <summary>
/// Authentication window — pure IPC client (auth refactor B).
/// All PIN/password/recovery verification happens in the service.
/// Windows Hello remains local (OS-level check, no DB).
///
/// Lockout: the service is the sole authority. This window only mirrors
/// lockout state reported in failure responses (LastLockoutInfo) and runs
/// a local countdown from that snapshot. If the countdown and the service
/// disagree, the service wins — an early attempt just gets rejected again.
///
/// NOTE: There is no way to query lockout state without attempting auth,
/// so a restart during an active lockout shows a normal window until the
/// first attempt. Secure (service enforces), cosmetic only.
/// </summary>
public partial class AuthWindow : Window
{
    private readonly AuthService _auth;
    private readonly RecoveryService _recovery;
    private readonly ILogger<AuthWindow> _logger;

    private DispatcherTimer? _lockoutTimer;

    // UTC time at which the service-reported lockout expires. Computed from
    // LockoutInfo.RemainingSeconds at the moment of the failure response.
    private DateTime? _lockoutEndsUtc;

    // Prevents double-submit while an async verify is in flight.
    private bool _busy;

    // ── Eye toggle state ──────────────────────────────────────────────────────
    private bool _pinRevealed = false;
    private bool _pwdRevealed = false;

    private static readonly SolidColorBrush EyeActiveBrush =
        new(Color.FromRgb(0x7C, 0x3A, 0xED));
    private static readonly SolidColorBrush EyeInactiveBrush =
        new(Color.FromRgb(0x8B, 0x8B, 0xA7));

    // ── Results read by caller after Close() ──────────────────────────────────
    public bool Authenticated { get; private set; }
    public bool RequiresSetup { get; private set; }

    // ─────────────────────────────────────────────────────────────────────────

    public AuthWindow(
        AuthService auth,
        RecoveryService recovery,
        ILogger<AuthWindow> logger)
    {
        _auth = auth;
        _recovery = recovery;
        _logger = logger;
        InitializeComponent();
    }

    // ── Startup ───────────────────────────────────────────────────────────────

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // No load-time lockout check — lockout state is service-side and only
        // surfaces in failure responses. If a lockout is active, the first
        // attempt will reveal it and trigger the lockout UI.

        // Windows Hello is intentionally NOT offered as an unlock method.
        // UserConsentVerifier authenticates the CURRENTLY logged-in Windows user
        // — in the monitored user's own session that is the monitored user, who
        // would then pass with their own biometric and read the dashboard. Only
        // the PIN/password (verified server-side against the parent's secret)
        // proves the right person. To re-enable on a parent-owned machine,
        // restore the availability check and show HelloSection.
        HelloSection.Visibility = Visibility.Collapsed;

        PinBox.Focus();
    }

    // ── Windows Hello ─────────────────────────────────────────────────────────

    private async void HelloBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        HelloBtn.IsEnabled = false;
        HelloErrorText.Visibility = Visibility.Collapsed;

        try
        {
            var result = await _auth.VerifyWindowsHelloAsync();
            HandleResult(result, "Windows Hello", HelloErrorText, lockoutInfo: null);
        }
        finally
        {
            _busy = false;
            HelloBtn.IsEnabled = true;
        }
    }

    // ── PIN ───────────────────────────────────────────────────────────────────

    private async void PinBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await TryPinAsync(); }
    }

    private async void PinBtn_Click(object sender, RoutedEventArgs e)
        => await TryPinAsync();

    private async Task TryPinAsync()
    {
        if (_busy) return;
        _busy = true;
        PinBtn.IsEnabled = false;
        PinErrorText.Visibility = Visibility.Collapsed;

        try
        {
            var result = await _auth.VerifyCredentialAsync(GetPin());
            HandleResult(result, "PIN", PinErrorText, _auth.LastLockoutInfo);
            if (result != AuthResult.Success) ResetPinField();
        }
        finally
        {
            _busy = false;
            PinBtn.IsEnabled = true;
        }
    }

    // ── Password ──────────────────────────────────────────────────────────────

    private async void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await TryPasswordAsync(); }
    }

    private async void PasswordBtn_Click(object sender, RoutedEventArgs e)
        => await TryPasswordAsync();

    private async Task TryPasswordAsync()
    {
        if (_busy) return;
        _busy = true;
        PasswordBtn.IsEnabled = false;
        PasswordErrorText.Visibility = Visibility.Collapsed;

        try
        {
            var result = await _auth.VerifyCredentialAsync(GetPwd());
            HandleResult(result, "Password", PasswordErrorText, _auth.LastLockoutInfo);
            if (result != AuthResult.Success) ResetPwdField();
        }
        finally
        {
            _busy = false;
            PasswordBtn.IsEnabled = true;
        }
    }

    // ── Eye toggle ────────────────────────────────────────────────────────────

    private void PinEye_Click(object sender, RoutedEventArgs e)
    {
        _pinRevealed = !_pinRevealed;
        if (_pinRevealed)
        {
            PinBoxReveal.Text = PinBox.Password;
            PinBox.Visibility = Visibility.Collapsed;
            PinBoxReveal.Visibility = Visibility.Visible;
            PinEyeBtn.Content = "○";
            PinEyeBtn.Foreground = EyeActiveBrush;
            PinBoxReveal.Focus();
            PinBoxReveal.CaretIndex = PinBoxReveal.Text.Length;
        }
        else
        {
            PinBox.Password = PinBoxReveal.Text;
            PinBoxReveal.Visibility = Visibility.Collapsed;
            PinBox.Visibility = Visibility.Visible;
            PinEyeBtn.Content = "◉";
            PinEyeBtn.Foreground = EyeInactiveBrush;
            PinBox.Focus();
        }
    }

    private void PwdEye_Click(object sender, RoutedEventArgs e)
    {
        _pwdRevealed = !_pwdRevealed;
        if (_pwdRevealed)
        {
            PwdBoxReveal.Text = PasswordBox.Password;
            PasswordBox.Visibility = Visibility.Collapsed;
            PwdBoxReveal.Visibility = Visibility.Visible;
            PwdEyeBtn.Content = "○";
            PwdEyeBtn.Foreground = EyeActiveBrush;
            PwdBoxReveal.Focus();
            PwdBoxReveal.CaretIndex = PwdBoxReveal.Text.Length;
        }
        else
        {
            PasswordBox.Password = PwdBoxReveal.Text;
            PwdBoxReveal.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Visible;
            PwdEyeBtn.Content = "◉";
            PwdEyeBtn.Foreground = EyeInactiveBrush;
            PasswordBox.Focus();
        }
    }

    // ── Field value + reset helpers ───────────────────────────────────────────

    private string GetPin() => _pinRevealed ? PinBoxReveal.Text : PinBox.Password;
    private string GetPwd() => _pwdRevealed ? PwdBoxReveal.Text : PasswordBox.Password;

    private void ResetPinField()
    {
        PinBox.Clear();
        PinBoxReveal.Text = "";
        if (_pinRevealed)
        {
            _pinRevealed = false;
            PinBoxReveal.Visibility = Visibility.Collapsed;
            PinBox.Visibility = Visibility.Visible;
            PinEyeBtn.Content = "◉";
            PinEyeBtn.Foreground = EyeInactiveBrush;
        }
        PinBox.Focus();
    }

    private void ResetPwdField()
    {
        PasswordBox.Clear();
        PwdBoxReveal.Text = "";
        if (_pwdRevealed)
        {
            _pwdRevealed = false;
            PwdBoxReveal.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Visible;
            PwdEyeBtn.Content = "◉";
            PwdEyeBtn.Foreground = EyeInactiveBrush;
        }
    }

    // ── Recovery code ─────────────────────────────────────────────────────────

    private void RecoveryToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        var isVisible = RecoverySection.Visibility == Visibility.Visible;
        RecoverySection.Visibility = isVisible ? Visibility.Collapsed : Visibility.Visible;
        if (!isVisible) RecoveryCodeBox.Focus();
    }

    private async void RecoveryCodeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await TryRecoveryAsync(); }
    }

    private async void RecoveryBtn_Click(object sender, RoutedEventArgs e)
        => await TryRecoveryAsync();

    /// <summary>
    /// Recovery is ONE atomic service command — verify + clear all credentials.
    /// On success, no auth methods exist anymore: caller must run SetupWizard.
    /// Wrong codes count toward the same service lockout as PIN/password.
    /// </summary>
    private async Task TryRecoveryAsync()
    {
        if (_busy) return;

        RecoveryErrorText.Visibility = Visibility.Collapsed;
        var input = RecoveryCodeBox.Text.Trim();

        if (string.IsNullOrEmpty(input))
        {
            ShowError(RecoveryErrorText, "Enter your recovery code.");
            return;
        }

        _busy = true;
        RecoveryBtn.IsEnabled = false;

        try
        {
            var result = await _recovery.PerformRecoveryAsync(input);

            switch (result)
            {
                case AuthResult.Success:
                    _logger.LogWarning(
                        "Recovery performed — all credentials cleared, redirecting to setup");
                    Authenticated = true;
                    RequiresSetup = true;
                    Close();
                    break;

                case AuthResult.LockedOut:
                    _logger.LogWarning("Recovery attempt blocked — lockout active");
                    ShowLockout(_recovery.LastLockoutInfo);
                    break;

                case AuthResult.NotConfigured:
                    ShowError(RecoveryErrorText, "No recovery code is configured.");
                    break;

                case AuthResult.ServiceUnavailable:
                    ShowError(RecoveryErrorText,
                        "Cannot reach the Obstruo service. Recovery is unavailable until the service is running.");
                    break;

                default: // WrongCredential
                    _logger.LogWarning("Incorrect recovery code entered");
                    ShowError(RecoveryErrorText, BuildWrongCredentialMessage(
                        "recovery code", _recovery.LastLockoutInfo));
                    RecoveryCodeBox.Clear();
                    break;
            }
        }
        finally
        {
            _busy = false;
            RecoveryBtn.IsEnabled = true;
        }
    }

    // ── Result handler (PIN / Password / Hello) ───────────────────────────────

    private void HandleResult(
        AuthResult result, string method, TextBlock errorTarget, LockoutInfo? lockoutInfo)
    {
        switch (result)
        {
            case AuthResult.Success:
                _logger.LogInformation("{Method} auth succeeded", method);
                Authenticated = true;
                Close();
                break;

            case AuthResult.WrongCredential:
                ShowError(errorTarget, BuildWrongCredentialMessage(method, lockoutInfo));
                break;

            case AuthResult.LockedOut:
                ShowLockout(lockoutInfo);
                break;

            case AuthResult.NotConfigured:
                ShowError(errorTarget, $"{method} is not configured.");
                break;

            case AuthResult.ServiceUnavailable:
                // Fail-closed: no auth possible while the service is down.
                // Inputs stay enabled so the user can retry once it's back.
                ShowError(errorTarget,
                    "Cannot reach the Obstruo service. Authentication is unavailable until the service is running.");
                break;

            case AuthResult.HelloUnavailable:
                HelloSection.Visibility = Visibility.Collapsed;
                break;

            case AuthResult.Cancelled:
                break;
        }
    }

    /// <summary>
    /// Builds the wrong-credential message from the service's lockout snapshot.
    /// AttemptsBeforeLockout comes from the service — no local counters.
    /// </summary>
    private static string BuildWrongCredentialMessage(string method, LockoutInfo? info)
    {
        if (info is null || info.AttemptsBeforeLockout <= 0)
            return $"Incorrect {method}.";

        var plural = info.AttemptsBeforeLockout == 1 ? "attempt" : "attempts";
        return $"Incorrect {method}. {info.AttemptsBeforeLockout} {plural} remaining before lockout.";
    }

    // ── Lockout UI ────────────────────────────────────────────────────────────

    /// <summary>
    /// Activates the lockout UI from a service-reported LockoutInfo snapshot.
    /// The countdown is a local projection of RemainingSeconds — the service
    /// remains the authority and will reject early attempts regardless.
    /// </summary>
    private void ShowLockout(LockoutInfo? info)
    {
        // Defensive: LockedOut result with no parseable info. Fall back to a
        // 60s local countdown — the service still enforces the real duration.
        var seconds = info?.RemainingSeconds > 0 ? info.RemainingSeconds : 60;
        _lockoutEndsUtc = DateTime.UtcNow.AddSeconds(seconds);

        SetInputsEnabled(false);
        LockoutPanel.Visibility = Visibility.Visible;
        UpdateLockoutText();

        _lockoutTimer?.Stop();
        _lockoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _lockoutTimer.Tick += LockoutTimer_Tick;
        _lockoutTimer.Start();
    }

    private void LockoutTimer_Tick(object? sender, EventArgs e)
    {
        if (_lockoutEndsUtc is null || DateTime.UtcNow >= _lockoutEndsUtc)
        {
            _lockoutTimer?.Stop();
            _lockoutEndsUtc = null;
            LockoutPanel.Visibility = Visibility.Collapsed;
            SetInputsEnabled(true);
            ClearAllErrors();
            PinBox.Focus();
            return;
        }
        UpdateLockoutText();
    }

    private void UpdateLockoutText()
    {
        if (_lockoutEndsUtc is null) return;

        var remaining = _lockoutEndsUtc.Value - DateTime.UtcNow;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

        var minutes = (int)remaining.TotalMinutes;
        var seconds = remaining.Seconds;

        LockoutCountdownText.Text = minutes > 0
            ? $"Try again in {minutes}m {seconds:D2}s"
            : $"Try again in {seconds}s";
    }

    private void SetInputsEnabled(bool enabled)
    {
        PinBox.IsEnabled = enabled;
        PinBoxReveal.IsEnabled = enabled;
        PinEyeBtn.IsEnabled = enabled;
        PinBtn.IsEnabled = enabled;
        PasswordBox.IsEnabled = enabled;
        PwdBoxReveal.IsEnabled = enabled;
        PwdEyeBtn.IsEnabled = enabled;
        PasswordBtn.IsEnabled = enabled;
        HelloBtn.IsEnabled = enabled;
        RecoveryCodeBox.IsEnabled = enabled;
        RecoveryBtn.IsEnabled = enabled;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void ShowError(TextBlock target, string message)
    {
        target.Text = message;
        target.Visibility = Visibility.Visible;
    }

    private void ClearAllErrors()
    {
        PinErrorText.Visibility = Visibility.Collapsed;
        PasswordErrorText.Visibility = Visibility.Collapsed;
        HelloErrorText.Visibility = Visibility.Collapsed;
        RecoveryErrorText.Visibility = Visibility.Collapsed;
    }

    // ── Window chrome ─────────────────────────────────────────────────────────

    private void DragBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DragMove();

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
        => Application.Current.Shutdown();

    // ── Cleanup ───────────────────────────────────────────────────────────────

    protected override void OnClosed(EventArgs e)
    {
        _lockoutTimer?.Stop();
        base.OnClosed(e);
    }
}