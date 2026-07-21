using Obstruo.Shared.Enums;
using Obstruo.UI.Ipc;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Obstruo.UI;

/// <summary>
/// Settings screen. Reads current values with the read-only GetSettings command
/// and writes them back with the credential-gated UpdateConfig command (and
/// SyncBlocklist for the feed). All persistence and validation happen in the
/// service — this window only collects input and shows the result.
/// </summary>
public partial class SettingsWindow : Window
{
    // Feed download + apply runs synchronously in the service, so give the
    // sync request generous headroom over the default 5s command timeout.
    private const int SyncTimeoutMs = 90_000;

    private readonly IpcClient _ipc;
    private bool _busy;

    private readonly ObservableCollection<CategoryToggle> _categories = new();

    public SettingsWindow(IpcClient ipc)
    {
        _ipc = ipc;
        InitializeComponent();
        CategoriesList.ItemsSource = _categories;
        Loaded += async (_, _) => await LoadAsync();
    }

    // ── Load ────────────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        try
        {
            var response = await _ipc.SendCommandAndWaitAsync(ServiceCommand.GetSettings);
            if (!response.Success || string.IsNullOrEmpty(response.Data))
            {
                ShowError(response.Error ?? "Could not load settings from the service.");
                SetBusy(true); // keep inputs disabled — nothing to edit
                return;
            }

            var snapshot = JsonSerializer.Deserialize<SettingsSnapshot>(response.Data, _json);
            if (snapshot is null)
            {
                ShowError("The service returned settings in an unexpected format.");
                SetBusy(true);
                return;
            }

            ApplySnapshot(snapshot);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        {
            ShowError("Cannot reach the Obstruo service. It may be stopped.");
            SetBusy(true);
        }
    }

    private void ApplySnapshot(SettingsSnapshot snapshot)
    {
        var c = snapshot.Config ?? new Dictionary<string, string>();

        RetentionBox.Text  = Get(c, "log_retention_hours", "720");
        CleanupTimeBox.Text = Get(c, "cleanup_time", "02:00");
        MaxPauseBox.Text   = Get(c, "emergency_disable_max_minutes", "15");
        CooldownBox.Text   = Get(c, "emergency_disable_cooldown_minutes", "60");
        MetricsBox.Text    = Get(c, "metrics_refresh_seconds", "30");
        BlocklistUrlBox.Text = Get(c, "blocklist_url", "");

        var theme = Get(c, "ui_theme", "dark");
        ThemeCombo.SelectedIndex = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        MaskCustomCheck.IsChecked = Get(c, "ui_mask_custom", "0") == "1";
        LanModeCheck.IsChecked = Get(c, "lan_mode_enabled", "0") == "1";

        // SafeSearch (defaults ON; DuckDuckGo has no DNS mechanism, so it is absent).
        SafeSearchGoogleCheck.IsChecked = Get(c, "safesearch_google", "1") == "1";
        SafeSearchYouTubeCheck.IsChecked = Get(c, "safesearch_youtube", "1") == "1";
        SafeSearchBingCheck.IsChecked = Get(c, "safesearch_bing", "1") == "1";
        YouTubeLevelCombo.SelectedIndex =
            string.Equals(Get(c, "safesearch_youtube_level", "moderate"), "strict",
                StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        _categories.Clear();
        foreach (var cat in snapshot.Categories ?? new List<CategoryDto>())
            _categories.Add(new CategoryToggle { Name = cat.Name, Enabled = cat.Enabled });

        // Build identity from the running service (finding L3) — lets the user (or
        // support) confirm exactly which build is live.
        var version = string.IsNullOrWhiteSpace(snapshot.Version) ? "?" : snapshot.Version;
        var commit = string.IsNullOrWhiteSpace(snapshot.BuildCommit) ? "unknown" : snapshot.BuildCommit;
        BuildInfoText.Text = commit == "unknown"
            ? $"Obstruo Security v{version}"
            : $"Obstruo Security v{version} · build {commit}";
    }

    private static string Get(Dictionary<string, string> c, string key, string fallback)
        => c.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;

    // ── Save ────────────────────────────────────────────────────────────────

    private async void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ClearMessages();

        var credential = CredentialBox.Password;
        if (string.IsNullOrEmpty(credential))
        {
            ShowError("Enter your PIN or password to save.");
            return;
        }

        if (!TryBuildConfigPayload(out var payload, out var validationError))
        {
            ShowError(validationError!);
            return;
        }

        SetBusy(true);
        ShowStatus("Saving…");

        try
        {
            var response = await _ipc.SendCommandAndWaitAsync(
                ServiceCommand.UpdateConfig, payload: payload, credential: credential);

            if (response.Success)
            {
                ShowStatus("Settings saved.");
                CredentialBox.Clear();
            }
            else
            {
                ShowError(response.Error ?? "The service rejected the settings.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        {
            ShowError("Cannot reach the Obstruo service. It may be stopped.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Builds the UpdateConfig payload. Light client-side checks give fast
    /// feedback; the service re-validates every value authoritatively.
    /// </summary>
    private bool TryBuildConfigPayload(out Dictionary<string, string> payload, out string? error)
    {
        payload = new Dictionary<string, string>(StringComparer.Ordinal);
        error = null;

        if (!IsIntInRange(RetentionBox.Text, 1, 24 * 365))
        { error = "Retention must be a whole number of hours (1–8760)."; return false; }
        if (!TimeSpan.TryParse(CleanupTimeBox.Text.Trim(), out var t) || t < TimeSpan.Zero || t >= TimeSpan.FromDays(1))
        { error = "Cleanup time must be a valid time of day (HH:MM)."; return false; }
        if (!IsIntInRange(MaxPauseBox.Text, 1, 240))
        { error = "Max pause must be 1–240 minutes."; return false; }
        if (!IsIntInRange(CooldownBox.Text, 0, 10_080))
        { error = "Cooldown must be 0–10080 minutes."; return false; }
        if (!IsIntInRange(MetricsBox.Text, 5, 3_600))
        { error = "Metrics refresh must be 5–3600 seconds."; return false; }

        var url = BlocklistUrlBox.Text.Trim();
        if (url.Length > 0 && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        { error = "The blocklist URL must start with https:// (or be empty)."; return false; }

        payload["log_retention_hours"] = RetentionBox.Text.Trim();
        payload["cleanup_time"] = CleanupTimeBox.Text.Trim();
        payload["emergency_disable_max_minutes"] = MaxPauseBox.Text.Trim();
        payload["emergency_disable_cooldown_minutes"] = CooldownBox.Text.Trim();
        payload["metrics_refresh_seconds"] = MetricsBox.Text.Trim();
        payload["blocklist_url"] = url;
        payload["ui_theme"] = SelectedTheme();
        payload["ui_mask_custom"] = MaskCustomCheck.IsChecked == true ? "1" : "0";
        payload["lan_mode_enabled"] = LanModeCheck.IsChecked == true ? "1" : "0";
        payload["safesearch_google"] = SafeSearchGoogleCheck.IsChecked == true ? "1" : "0";
        payload["safesearch_youtube"] = SafeSearchYouTubeCheck.IsChecked == true ? "1" : "0";
        payload["safesearch_bing"] = SafeSearchBingCheck.IsChecked == true ? "1" : "0";
        payload["safesearch_youtube_level"] = SelectedYouTubeLevel();

        foreach (var cat in _categories)
            payload[$"category:{cat.Name}"] = cat.Enabled ? "1" : "0";

        return true;
    }

    // ── Sync blocklist feed ───────────────────────────────────────────────────

    private async void SyncBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ClearMessages();

        var credential = CredentialBox.Password;
        if (string.IsNullOrEmpty(credential))
        {
            ShowError("Enter your PIN or password to sync.");
            return;
        }

        var url = BlocklistUrlBox.Text.Trim();
        if (url.Length == 0)
        {
            ShowError("Enter an HTTPS feed URL first.");
            return;
        }
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            ShowError("The blocklist URL must start with https://.");
            return;
        }

        SetBusy(true);
        ShowStatus("Downloading and applying the feed… this can take a moment.");

        try
        {
            var response = await _ipc.SendCommandAndWaitAsync(
                ServiceCommand.SyncBlocklist,
                payload: new Dictionary<string, string> { ["url"] = url },
                credential: credential,
                timeoutMs: SyncTimeoutMs);

            if (response.Success)
                ShowStatus($"Sync complete. {ParseAdded(response.Data)} domain change(s).");
            else
                ShowError(response.Error ?? "Sync was rejected.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        {
            ShowError("Cannot reach the Obstruo service. It may be stopped.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string ParseAdded(string? data)
    {
        try
        {
            if (data is not null)
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("added", out var a))
                    return a.GetInt32().ToString("N0");
            }
        }
        catch { /* fall through */ }
        return "0";
    }

    // ── Export ────────────────────────────────────────────────────────────────

    private async void ExportBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ClearMessages();

        var credential = CredentialBox.Password;
        if (string.IsNullOrEmpty(credential))
        {
            ShowError("Enter your PIN or password to export.");
            return;
        }

        var json = string.Equals(
            (ExportFormatCombo.SelectedItem as ComboBoxItem)?.Content as string,
            "JSON", StringComparison.OrdinalIgnoreCase);
        var format = json ? "json" : "csv";

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"obstruo-activity-{DateTime.Now:yyyyMMdd}.{format}",
            DefaultExt = format,
            Filter = json ? "JSON file (*.json)|*.json" : "CSV file (*.csv)|*.csv",
        };
        if (dialog.ShowDialog(this) != true) return;

        SetBusy(true);
        ShowStatus("Exporting…");
        try
        {
            var response = await _ipc.SendCommandAndWaitAsync(
                ServiceCommand.ExportLogs,
                payload: new Dictionary<string, string>
                {
                    ["path"] = dialog.FileName,
                    ["format"] = format,
                    ["days"] = "30",
                },
                credential: credential,
                timeoutMs: 30_000);

            if (response.Success)
            {
                CredentialBox.Clear();
                ShowStatus($"Exported {ParseRows(response.Data)} event(s) to {dialog.FileName}.");
            }
            else
            {
                ShowError(response.Error ?? "The service rejected the export.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        {
            ShowError("Cannot reach the Obstruo service. It may be stopped.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string ParseRows(string? data)
    {
        try
        {
            if (data is not null)
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("rows", out var r))
                    return r.GetInt32().ToString("N0");
            }
        }
        catch { /* fall through */ }
        return "0";
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private string SelectedTheme()
        => (ThemeCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "dark";

    private string SelectedYouTubeLevel()
        => string.Equals(
            (YouTubeLevelCombo.SelectedItem as ComboBoxItem)?.Content as string,
            "Strict", StringComparison.OrdinalIgnoreCase) ? "strict" : "moderate";

    private static bool IsIntInRange(string text, int min, int max)
        => int.TryParse(text?.Trim(), out var v) && v >= min && v <= max;

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SaveBtn.IsEnabled = !busy;
        SyncBtn.IsEnabled = !busy;
        ExportBtn.IsEnabled = !busy;
        CredentialBox.IsEnabled = !busy;
    }

    private void ClearMessages()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
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

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ── DTOs ────────────────────────────────────────────────────────────────

    private sealed record SettingsSnapshot(
        Dictionary<string, string>? Config,
        List<CategoryDto>? Categories,
        string? Version,
        string? BuildCommit);

    private sealed record CategoryDto(string Name, bool Enabled);

    /// <summary>Bindable row for the categories list (two-way IsChecked).</summary>
    public sealed class CategoryToggle
    {
        public string Name { get; set; } = "";
        public bool Enabled { get; set; }
    }
}
