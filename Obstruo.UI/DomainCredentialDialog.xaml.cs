using Obstruo.Shared.Messages;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace Obstruo.UI;

/// <summary>
/// Generic "domain + PIN" confirmation dialog, shared by every credential-gated
/// domain action (add to whitelist, add custom block, remove entries).
///
/// The caller supplies the action as a delegate that sends the IPC command and
/// returns the response; this dialog owns the input UX, busy state, and error
/// display. A wrong credential is rejected by the service — nothing changes.
/// </summary>
public partial class DomainCredentialDialog : Window
{
    /// <summary>Sends the command; receives (domain, credential).</summary>
    public Func<string, string, Task<CommandResponseMessage>>? Action { get; set; }

    /// <summary>True once the action succeeded and the dialog closed itself.</summary>
    public bool Succeeded { get; private set; }

    private bool _busy;

    private readonly bool _requireDomain;

    /// <summary>
    /// Selected block duration in minutes, or null for a permanent block. Only
    /// meaningful when the dialog was created with <c>showDuration: true</c>.
    /// </summary>
    public int? DurationMinutes =>
        int.TryParse((DurationCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string,
            out var m) && m > 0 ? m : null;

    public DomainCredentialDialog(
        string title, string description, string confirmLabel,
        string? domain = null, bool domainEditable = true, bool requireDomain = true,
        bool showDuration = false)
    {
        InitializeComponent();
        _requireDomain = requireDomain;

        if (showDuration)
        {
            DurationPanel.Visibility = Visibility.Visible;
            // Grow the fixed-size window so the extra row doesn't clip the buttons.
            Height = 496;
            MinHeight = 496;
        }

        TitleText.Text = title.ToUpperInvariant();
        DescriptionText.Text = description;
        ConfirmBtn.Content = confirmLabel;

        if (domain is not null)
            DomainBox.Text = domain;
        DomainBox.IsEnabled = domainEditable;

        if (!requireDomain)
        {
            // Credential-only mode (e.g. viewing the whitelist) — no domain input.
            DomainLabel.Visibility = Visibility.Collapsed;
            DomainBorder.Visibility = Visibility.Collapsed;
        }

        Loaded += (_, _) =>
        {
            if (requireDomain && domainEditable && string.IsNullOrEmpty(DomainBox.Text))
                DomainBox.Focus();
            else
                CredentialBox.Focus();
        };
    }

    private async void CredentialBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await RunActionAsync(); }
    }

    private async void ConfirmBtn_Click(object sender, RoutedEventArgs e)
        => await RunActionAsync();

    private async Task RunActionAsync()
    {
        if (_busy || Action is null) return;

        ErrorText.Visibility = Visibility.Collapsed;

        var domain = DomainBox.Text.Trim();
        if (_requireDomain && string.IsNullOrEmpty(domain))
        {
            ShowError("Enter a domain.");
            return;
        }

        var credential = CredentialBox.Password;
        if (string.IsNullOrEmpty(credential))
        {
            ShowError("Enter your PIN or password to confirm.");
            return;
        }

        _busy = true;
        SetInputsEnabled(false);
        ShowStatus("Working…");

        try
        {
            var response = await Action(domain, credential);

            if (response.Success)
            {
                Succeeded = true;
                Close();
                return;
            }

            CredentialBox.Clear();
            ShowError(response.Error ?? "The action was rejected.");
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

    private void SetInputsEnabled(bool enabled)
    {
        // DomainBox keeps its constructor-time editability when re-enabling.
        CredentialBox.IsEnabled = enabled;
        ConfirmBtn.IsEnabled = enabled;
        CancelBtn.IsEnabled = enabled;
        StatusText.Visibility = enabled ? Visibility.Collapsed : StatusText.Visibility;
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
