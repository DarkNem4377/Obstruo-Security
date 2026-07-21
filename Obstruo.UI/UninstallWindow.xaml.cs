using Obstruo.Shared.Enums;
using Obstruo.UI.Ipc;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Obstruo.UI;

/// <summary>
/// PIN/password-gated uninstall confirmation.
///
/// Sends a single Uninstall command to the service with the entered credential.
/// The service verifies it, tears down protection (DNS restore, DoH removal,
/// firewall cleanup) in-process, and schedules removal of the service + files.
/// The UI never touches the system directly — a wrong credential is rejected by
/// the service and nothing changes.
/// </summary>
public partial class UninstallWindow : Window
{
    // The service runs teardown (several netsh calls) synchronously before it
    // replies, so allow generous headroom over the default 5s request timeout.
    private const int UninstallTimeoutMs = 30_000;

    // Eye icon (visible) / eye-off icon (hidden) — Material Design path data.
    private const string EyeIcon =
        "M12,9A3,3 0 0,1 15,12A3,3 0 0,1 12,15A3,3 0 0,1 9,12A3,3 0 0,1 12,9M12,4.5C17,4.5 21.27,7.61 23,12C21.27,16.39 17,19.5 12,19.5C7,19.5 2.73,16.39 1,12C2.73,7.61 7,4.5 12,4.5Z";
    private const string EyeOffIcon =
        "M11.83,9L15,12.16C15,12.11 15,12.05 15,12A3,3 0 0,0 12,9C11.94,9 11.89,9 11.83,9M7.53,9.8L9.08,11.35C9.03,11.56 9,11.77 9,12A3,3 0 0,0 12,15C12.22,15 12.44,14.97 12.65,14.92L14.2,16.47C13.53,16.8 12.79,17 12,17A5,5 0 0,1 7,12C7,11.21 7.2,10.47 7.53,9.8M2,4.27L4.28,6.55L4.73,7C3.08,8.3 1.78,10 1,12C2.73,16.39 7,19.5 12,19.5C13.55,19.5 15.03,19.2 16.38,18.66L16.81,19.08L19.73,22L21,20.73L3.27,3M12,7A5,5 0 0,1 17,12C17,12.64 16.87,13.26 16.64,13.82L19.57,16.75C21.07,15.5 22.27,13.86 23,12C21.27,7.61 17,4.5 12,4.5C10.6,4.5 9.26,4.75 8,5.2L10.17,7.35C10.74,7.13 11.35,7 12,7Z";

    private readonly IpcClient _ipc;
    private bool _busy;
    private bool _revealed;
    private bool _syncing; // guards against PasswordChanged <-> TextChanged loops

    public UninstallWindow(IpcClient ipc)
    {
        _ipc = ipc;
        InitializeComponent();
        Loaded += (_, _) => CredentialBox.Focus();
    }

    /// <summary>Current credential regardless of which input is visible.</summary>
    private string Credential => _revealed ? CredentialVisibleBox.Text : CredentialBox.Password;

    private void RevealBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        _revealed = !_revealed;

        if (_revealed)
        {
            _syncing = true;
            CredentialVisibleBox.Text = CredentialBox.Password;
            _syncing = false;

            CredentialBox.Visibility = Visibility.Collapsed;
            CredentialVisibleBox.Visibility = Visibility.Visible;
            RevealIcon.Data = Geometry.Parse(EyeOffIcon);
            RevealIcon.Fill = new SolidColorBrush(Color.FromRgb(0x6A, 0x5C, 0xFF));

            CredentialVisibleBox.Focus();
            CredentialVisibleBox.CaretIndex = CredentialVisibleBox.Text.Length;
        }
        else
        {
            _syncing = true;
            CredentialBox.Password = CredentialVisibleBox.Text;
            _syncing = false;

            CredentialVisibleBox.Visibility = Visibility.Collapsed;
            CredentialBox.Visibility = Visibility.Visible;
            RevealIcon.Data = Geometry.Parse(EyeIcon);
            RevealIcon.Fill = new SolidColorBrush(Color.FromRgb(0x68, 0x70, 0xA0));

            CredentialBox.Focus();
        }
    }

    private void CredentialBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || _revealed) return;
        _syncing = true;
        CredentialVisibleBox.Text = CredentialBox.Password;
        _syncing = false;
    }

    private void CredentialVisibleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || !_revealed) return;
        _syncing = true;
        CredentialBox.Password = CredentialVisibleBox.Text;
        _syncing = false;
    }

    private async void CredentialBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await TryUninstallAsync(); }
    }

    private async void UninstallBtn_Click(object sender, RoutedEventArgs e)
        => await TryUninstallAsync();

    private async Task TryUninstallAsync()
    {
        if (_busy) return;

        ErrorText.Visibility = Visibility.Collapsed;

        var credential = Credential;
        if (string.IsNullOrEmpty(credential))
        {
            ShowError("Enter your PIN or password to confirm.");
            return;
        }

        _busy = true;
        SetInputsEnabled(false);
        ShowStatus("Uninstalling — restoring DNS and removing protection…");

        try
        {
            var response = await _ipc.SendCommandAndWaitAsync(
                ServiceCommand.Uninstall,
                credential: credential,
                timeoutMs: UninstallTimeoutMs);

            if (response.Success)
            {
                StatusText.Visibility = Visibility.Collapsed;

                MessageBox.Show(
                    "Obstruo has been uninstalled.\n\n" +
                    "Your original DNS settings have been restored and DNS-over-HTTPS " +
                    "blocking has been removed. The Obstruo service will finish removing " +
                    "itself in the background.",
                    "Obstruo — Uninstalled",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                Application.Current.Shutdown();
                return;
            }

            // Wrong credential, lockout, or teardown error — service left the
            // system unchanged. Let the user read the reason and retry.
            ClearCredential();
            ShowError(response.Error ?? "Uninstall was rejected.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        {
            ClearCredential();
            ShowError("Cannot reach the Obstruo service. It may be stopped — " +
                      "uninstall is unavailable until the service is running.");
        }
        finally
        {
            _busy = false;
            SetInputsEnabled(true);
        }
    }

    private void ClearCredential()
    {
        _syncing = true;
        CredentialBox.Clear();
        CredentialVisibleBox.Clear();
        _syncing = false;
    }

    private void SetInputsEnabled(bool enabled)
    {
        CredentialBox.IsEnabled = enabled;
        CredentialVisibleBox.IsEnabled = enabled;
        RevealBtn.IsEnabled = enabled;
        UninstallBtn.IsEnabled = enabled;
        CancelBtn.IsEnabled = enabled;
    }

    private void ShowError(string message)
    {
        StatusText.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void ShowStatus(string message)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Close();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Close();
    }

    private void DragBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DragMove();
}