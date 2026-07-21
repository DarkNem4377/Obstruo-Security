using Microsoft.Extensions.Logging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Obstruo.UI.Auth;

public partial class SetupWizard : Window
{
    private readonly AuthService _auth;
    private readonly RecoveryService _recovery;
    private readonly ILogger<SetupWizard> _logger;

    private int _step = 1;
    private string _generatedCode = string.Empty;

    // Guards against PasswordChanged <-> TextChanged sync feedback loops
    private bool _syncingReveal;

    public bool SetupComplete { get; private set; }

    public SetupWizard(
        AuthService auth,
        RecoveryService recovery,
        ILogger<SetupWizard> logger)
    {
        _auth = auth;
        _recovery = recovery;
        _logger = logger;
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ShowStep(1);
    }

    // ── Navigation ────────────────────────────────────────────────────────────
    //
    // COMMIT MODEL (auth refactor B):
    // Nothing is saved until "Finish Setup" on step 4. Steps 2 and 3 only
    // validate. This is deliberate:
    //   - The service's bootstrap rule closes once pin + password are both
    //     stored. Per-step commits + Back navigation would create states where
    //     re-saving a credential requires authenticating with a credential the
    //     user just changed in the UI.
    //   - Committing all-or-nothing at the end means Back is always free and
    //     a failed save never leaves the wizard "half done" from the user's
    //     point of view — they stay on step 4 and retry.

    private async void NextBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateCurrentStep()) return;

        switch (_step)
        {
            case 4:
                await FinishSetupAsync();
                return;

            case 5:
                // Set flag first, then close.
                SetupComplete = true;
                Close();
                return;

            default:
                AdvanceTo(_step + 1);
                return;
        }
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        AdvanceTo(_step - 1);
    }

    private void AdvanceTo(int step)
    {
        _step = step;
        ShowStep(_step);
    }

    // ── Step display ──────────────────────────────────────────────────────────

    private void ShowStep(int step)
    {
        Step1.Visibility = Visibility.Collapsed;
        Step2.Visibility = Visibility.Collapsed;
        Step3.Visibility = Visibility.Collapsed;
        Step4.Visibility = Visibility.Collapsed;
        Step5.Visibility = Visibility.Collapsed;

        var current = step switch
        {
            1 => Step1,
            2 => Step2,
            3 => Step3,
            4 => Step4,
            5 => Step5,
            _ => Step1
        };
        current.Visibility = Visibility.Visible;

        UpdateDots(step);

        BackBtn.Visibility = step > 1 && step < 5
            ? Visibility.Visible
            : Visibility.Collapsed;

        NextBtn.Content = step switch
        {
            1 => "Begin Setup →",
            2 => "Next →",
            3 => "Next →",
            4 => "Finish Setup",
            5 => "Open Dashboard",
            _ => "Next →"
        };

        if (step == 4 && string.IsNullOrEmpty(_generatedCode))
        {
            _generatedCode = _recovery.GenerateCode();
            RecoveryCodeDisplay.Text = _generatedCode;
        }

        if (step == 4)
            NextBtn.IsEnabled = SavedConfirmCheck.IsChecked == true;

        if (step == 5)
            NextBtn.IsEnabled = true;
    }

    private void UpdateDots(int step)
    {
        var active = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED));
        var inactive = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x32));

        Dot1.Fill = step >= 1 ? active : inactive;
        Dot2.Fill = step >= 2 ? active : inactive;
        Dot3.Fill = step >= 3 ? active : inactive;
        Dot4.Fill = step >= 4 ? active : inactive;
        Dot5.Fill = step >= 5 ? active : inactive;
    }

    // ── Validation ────────────────────────────────────────────────────────────

    private bool ValidateCurrentStep() => _step switch
    {
        1 => true,
        2 => ValidatePin(),
        3 => ValidatePassword(),
        4 => ValidateRecovery(),
        5 => true,
        _ => true
    };

    private bool ValidatePin()
    {
        PinError.Visibility = Visibility.Collapsed;

        var pin = PinEntry.Password;
        var confirm = PinConfirm.Password;

        if (pin.Length < 6 || pin.Length > 8)
        {
            ShowError(PinError, "PIN must be 6 to 8 digits.");
            return false;
        }

        if (!pin.All(char.IsDigit))
        {
            ShowError(PinError, "PIN must contain digits only.");
            return false;
        }

        if (pin != confirm)
        {
            ShowError(PinError, "PINs do not match.");
            ClearRevealField(PinConfirm, PinConfirmPlain);
            return false;
        }

        return true;
    }

    private bool ValidatePassword()
    {
        PwdError.Visibility = Visibility.Collapsed;

        var pwd = PwdEntry.Password;
        var confirm = PwdConfirm.Password;

        // Password is MANDATORY. There is no skip path.
        if (string.IsNullOrEmpty(pwd))
        {
            ShowError(PwdError, "A password is required. It cannot be skipped.");
            return false;
        }

        if (pwd.Length < 6)
        {
            ShowError(PwdError, "Password must be at least 6 characters.");
            return false;
        }

        if (!pwd.All(char.IsLetterOrDigit))
        {
            ShowError(PwdError, "Password must be alphanumeric (letters and digits only).");
            return false;
        }

        if (pwd == PinEntry.Password)
        {
            ShowError(PwdError, "Password cannot be the same as your PIN.");
            return false;
        }

        if (pwd != confirm)
        {
            ShowError(PwdError, "Passwords do not match.");
            ClearRevealField(PwdConfirm, PwdConfirmPlain);
            return false;
        }

        return true;
    }

    private bool ValidateRecovery()
    {
        RecoveryError.Visibility = Visibility.Collapsed;

        if (SavedConfirmCheck.IsChecked != true)
        {
            ShowError(RecoveryError, "You must confirm that you have saved the recovery code.");
            return false;
        }

        return true;
    }

    // ── Finish: commit all three credentials via the service ─────────────────

    /// <summary>
    /// Saves PIN → password → recovery code over IPC, in that order.
    /// The PIN is passed as existingCredential on EVERY call:
    ///   - During first-run bootstrap the service ignores it (setup incomplete).
    ///   - On a retry after a partial failure (e.g. pin + password stored but
    ///     the recovery save failed), bootstrap has closed — the credential is
    ///     then required, and the PIN in the field matches what was stored.
    /// Any failure keeps the wizard on step 4 with the service's error shown.
    /// Step 5 is reached ONLY when all three credentials are confirmed stored.
    /// KNOWN EDGE: if a retry is needed AND the user goes Back and changes the
    /// PIN before retrying, the re-save authenticates with the new PIN against
    /// the old stored hash and fails (counting toward lockout). Recovery from
    /// that state: re-enter the original PIN. Not worth extra machinery.
    /// </summary>
    private async Task FinishSetupAsync()
    {
        RecoveryError.Visibility = Visibility.Collapsed;

        var pin = PinEntry.Password;
        var pwd = PwdEntry.Password;

        NextBtn.IsEnabled = false;
        BackBtn.IsEnabled = false;
        NextBtn.Content = "Saving…";

        try
        {
            // 1. PIN
            var pinResult = await _auth.SaveCredentialAsync("pin_hash", pin, pin);
            if (!pinResult.Success)
            {
                ShowError(RecoveryError, $"Failed to save PIN: {pinResult.Error}");
                _logger.LogWarning("Setup: PIN save failed: {Error}", pinResult.Error);
                return;
            }

            // 2. Password — still bootstrap (pin alone doesn't complete setup)
            var pwdResult = await _auth.SaveCredentialAsync("password_hash", pwd, pin);
            if (!pwdResult.Success)
            {
                ShowError(RecoveryError, $"Failed to save password: {pwdResult.Error}");
                _logger.LogWarning("Setup: password save failed: {Error}", pwdResult.Error);
                return;
            }

            // 3. Recovery code — setup is now complete, bootstrap CLOSED.
            //    The PIN credential is what authorizes this save.
            var recResult = await _recovery.SaveCodeAsync(_generatedCode, pin);
            if (!recResult.Success)
            {
                ShowError(RecoveryError, $"Failed to save recovery code: {recResult.Error}");
                _logger.LogWarning("Setup: recovery code save failed: {Error}", recResult.Error);
                return;
            }

            _logger.LogInformation("Setup complete — all three credentials stored via service");

            // Clear the plaintext from the input controls now that they're stored.
            // (The local pin/pwd strings can't be zeroed — WPF/string limitation —
            // but don't leave the values sitting in the live PasswordBox/TextBox.)
            ClearRevealField(PinEntry, PinEntryPlain);
            ClearRevealField(PinConfirm, PinConfirmPlain);
            ClearRevealField(PwdEntry, PwdEntryPlain);
            ClearRevealField(PwdConfirm, PwdConfirmPlain);

            AdvanceTo(5);
        }
        finally
        {
            BackBtn.IsEnabled = true;
            if (_step == 5)
            {
                NextBtn.IsEnabled = true;
            }
            else
            {
                // Still on step 4 (a save failed) — restore the button so the
                // user can retry once they've addressed the error.
                NextBtn.Content = "Finish Setup";
                NextBtn.IsEnabled = SavedConfirmCheck.IsChecked == true;
            }
        }
    }

    // ── Show/hide (eye) toggles ───────────────────────────────────────────────
    //
    // WPF PasswordBox cannot display its text. Each field is a PasswordBox with
    // a plain TextBox twin stacked on top (collapsed by default). The eye button
    // swaps which one is visible; the two-way sync below keeps the PasswordBox
    // as the single source of truth, so all validation reads .Password safely.

    private void EyeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender == PinEntryEye) ToggleReveal(PinEntry, PinEntryPlain, PinEntryEyeIcon);
        else if (sender == PinConfirmEye) ToggleReveal(PinConfirm, PinConfirmPlain, PinConfirmEyeIcon);
        else if (sender == PwdEntryEye) ToggleReveal(PwdEntry, PwdEntryPlain, PwdEntryEyeIcon);
        else if (sender == PwdConfirmEye) ToggleReveal(PwdConfirm, PwdConfirmPlain, PwdConfirmEyeIcon);
    }

    private void ToggleReveal(PasswordBox pwd, TextBox plain, Path icon)
    {
        var revealing = plain.Visibility != Visibility.Visible;

        _syncingReveal = true;
        if (revealing)
        {
            plain.Text = pwd.Password;
            plain.Visibility = Visibility.Visible;
            pwd.Visibility = Visibility.Collapsed;
            icon.Data = (Geometry)FindResource("EyeOffGeo");
            plain.Focus();
            plain.CaretIndex = plain.Text.Length;
        }
        else
        {
            pwd.Password = plain.Text;
            pwd.Visibility = Visibility.Visible;
            plain.Visibility = Visibility.Collapsed;
            icon.Data = (Geometry)FindResource("EyeGeo");
            pwd.Focus();
        }
        _syncingReveal = false;
    }

    // Typing while revealed → mirror into the PasswordBox (source of truth)
    private void RevealField_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingReveal) return;

        _syncingReveal = true;
        if (sender == PinEntryPlain) PinEntry.Password = PinEntryPlain.Text;
        else if (sender == PinConfirmPlain) PinConfirm.Password = PinConfirmPlain.Text;
        else if (sender == PwdEntryPlain) PwdEntry.Password = PwdEntryPlain.Text;
        else if (sender == PwdConfirmPlain) PwdConfirm.Password = PwdConfirmPlain.Text;
        _syncingReveal = false;
    }

    // Typing while hidden → mirror into the plain twin so a later reveal is current
    private void RevealField_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingReveal) return;

        _syncingReveal = true;
        if (sender == PinEntry) PinEntryPlain.Text = PinEntry.Password;
        else if (sender == PinConfirm) PinConfirmPlain.Text = PinConfirm.Password;
        else if (sender == PwdEntry) PwdEntryPlain.Text = PwdEntry.Password;
        else if (sender == PwdConfirm) PwdConfirmPlain.Text = PwdConfirm.Password;
        _syncingReveal = false;
    }

    private void ClearRevealField(PasswordBox pwd, TextBox plain)
    {
        _syncingReveal = true;
        pwd.Clear();
        plain.Clear();
        _syncingReveal = false;
    }

    // ── Recovery code UI ──────────────────────────────────────────────────────

    private void CopyCodeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_generatedCode)) return;
        try
        {
            Clipboard.SetText(_generatedCode);
            CopyConfirmText.Visibility = Visibility.Visible;
            _logger.LogInformation("Recovery code copied to clipboard");
            ScheduleClipboardClear(_generatedCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to copy recovery code to clipboard");
        }
    }

    /// <summary>
    /// Clears the recovery code from the clipboard after a delay so it doesn't
    /// linger where any process can read it. Only clears if the clipboard still
    /// holds our code (don't clobber whatever the user copied since).
    /// </summary>
    private void ScheduleClipboardClear(string codeToClear)
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                if (Clipboard.ContainsText() && Clipboard.GetText() == codeToClear)
                {
                    Clipboard.Clear();
                    _logger.LogInformation("Recovery code cleared from clipboard after timeout");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Clipboard clear skipped (clipboard busy)");
            }
        };
        timer.Start();
    }

    private void SavedCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_step == 4)
            NextBtn.IsEnabled = SavedConfirmCheck.IsChecked == true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void ShowError(TextBlock target, string message)
    {
        target.Text = message;
        target.Visibility = Visibility.Visible;
    }

    // ── Window chrome ─────────────────────────────────────────────────────────

    private void DragBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => DragMove();
}