using Obstruo.Shared.Enums;
using Obstruo.UI.Ipc;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace Obstruo.UI;

/// <summary>
/// PIN/password-gated emergency pause.
///
/// Sends a single EmergencyStop command with the entered credential. The
/// service verifies it, clamps the duration to its configured maximum,
/// enforces the cooldown, and pauses filtering — then resumes automatically
/// when the window expires. The UI never changes protection state directly;
/// a wrong credential is rejected by the service and nothing changes.
/// </summary>
public partial class EmergencyStopWindow : Window
{
    private readonly IpcClient _ipc;
    private bool _busy;

    public EmergencyStopWindow(IpcClient ipc)
    {
        _ipc = ipc;
        InitializeComponent();
        Loaded += (_, _) => CredentialBox.Focus();
    }

    private async void CredentialBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await TryPauseAsync(); }
    }

    private async void PauseBtn_Click(object sender, RoutedEventArgs e)
        => await TryPauseAsync();

    private async Task TryPauseAsync()
    {
        if (_busy) return;

        ErrorText.Visibility = Visibility.Collapsed;

        var credential = CredentialBox.Password;
        if (string.IsNullOrEmpty(credential))
        {
            ShowError("Enter your PIN or password to confirm.");
            return;
        }

        _busy = true;
        SetInputsEnabled(false);
        ShowStatus("Pausing protection…");

        try
        {
            var response = await _ipc.SendCommandAndWaitAsync(
                ServiceCommand.EmergencyStop,
                credential: credential);

            if (response.Success)
            {
                var minutes = ParseMinutes(response.Data);
                MessageBox.Show(
                    $"Protection is paused for {minutes} minute(s).\n\n" +
                    "Filtering resumes automatically when the time is up, or you can " +
                    "resume early with the Resume Protection button.",
                    "Obstruo — Protection Paused",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Close();
                return;
            }

            // Wrong credential, lockout, or cooldown — nothing changed.
            CredentialBox.Clear();
            ShowError(response.Error ?? "Pause was rejected.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        {
            CredentialBox.Clear();
            ShowError("Cannot reach the Obstruo service. It may be stopped.");
        }
        finally
        {
            _busy = false;
            SetInputsEnabled(true);
        }
    }

    /// <summary>Reads "minutes" from the response Data JSON; "a few" on any parse issue.</summary>
    private static string ParseMinutes(string? data)
    {
        try
        {
            if (data is not null)
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("minutes", out var m))
                    return m.GetInt32().ToString();
            }
        }
        catch { /* fall through */ }
        return "a few";
    }

    private void SetInputsEnabled(bool enabled)
    {
        CredentialBox.IsEnabled = enabled;
        PauseBtn.IsEnabled = enabled;
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
