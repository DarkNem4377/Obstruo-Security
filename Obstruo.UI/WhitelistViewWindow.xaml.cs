using Obstruo.Shared.Enums;
using Obstruo.UI.Ipc;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Obstruo.UI;

/// <summary>
/// Read-and-remove view of the allow-list. Opened from the dashboard's
/// whitelist card after a credential-gated GetWhitelist succeeds; the verified
/// credential is kept for the window's lifetime so Remove works without
/// re-prompting, and is dropped when the window closes.
/// </summary>
public partial class WhitelistViewWindow : Window
{
    private sealed record Entry(string Domain, string AddedAt, string? ExpiresAt, string? Reason);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IpcClient _ipc;
    private string _credential;

    public WhitelistViewWindow(IpcClient ipc, string credential, string initialJson)
    {
        InitializeComponent();
        _ipc = ipc;
        _credential = credential;

        Closed += (_, _) => _credential = string.Empty;

        Render(initialJson);
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    private void Render(string json)
    {
        List<Entry> entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<Entry>>(json, _jsonOptions) ?? [];
        }
        catch (JsonException)
        {
            SummaryText.Text = "Could not read the whitelist from the service.";
            return;
        }

        EntriesPanel.Children.Clear();

        SummaryText.Text = entries.Count switch
        {
            0 => "The whitelist is empty — no domains override the blocklist.",
            1 => "1 domain is whitelisted.",
            _ => $"{entries.Count} domains are whitelisted."
        };

        foreach (var entry in entries)
            EntriesPanel.Children.Add(BuildRow(entry));
    }

    private Border BuildRow(Entry entry)
    {
        var grid = new Grid { Margin = new Thickness(14, 10, 10, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textStack = new StackPanel();
        textStack.Children.Add(new TextBlock
        {
            Text = entry.Domain,
            FontFamily = new FontFamily("Cascadia Code"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)new BrushConverter().ConvertFromString("#d8dcf0")!,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var meta = BuildMetaLine(entry);
        if (meta.Length > 0)
        {
            textStack.Children.Add(new TextBlock
            {
                Text = meta,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                Foreground = (Brush)new BrushConverter().ConvertFromString("#8B8BA7")!,
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        Grid.SetColumn(textStack, 0);
        grid.Children.Add(textStack);

        var removeBtn = new Button
        {
            Content = "Remove",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)new BrushConverter().ConvertFromString("#f0a0a0")!,
            Background = (Brush)new BrushConverter().ConvertFromString("#1a0508")!,
            BorderBrush = (Brush)new BrushConverter().ConvertFromString("#402028")!,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6, 12, 6),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = entry.Domain
        };
        removeBtn.Click += RemoveBtn_Click;
        Grid.SetColumn(removeBtn, 1);
        grid.Children.Add(removeBtn);

        return new Border
        {
            Background = (Brush)new BrushConverter().ConvertFromString("#12121E")!,
            BorderBrush = (Brush)new BrushConverter().ConvertFromString("#1E1E32")!,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Margin = new Thickness(8, 4, 8, 4),
            Child = grid
        };
    }

    private static string BuildMetaLine(Entry entry)
    {
        var parts = new List<string>();

        if (DateTime.TryParse(entry.AddedAt, null, DateTimeStyles.RoundtripKind, out var added))
            parts.Add($"added {added.ToLocalTime():d MMM yyyy}");

        if (entry.ExpiresAt is not null &&
            DateTime.TryParse(entry.ExpiresAt, null, DateTimeStyles.RoundtripKind, out var expires))
            parts.Add($"expires {expires.ToLocalTime():d MMM yyyy HH:mm}");
        else
            parts.Add("permanent");

        if (!string.IsNullOrWhiteSpace(entry.Reason))
            parts.Add(entry.Reason.Trim());

        return string.Join("  ·  ", parts);
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    private async void RemoveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string domain } btn || _credential.Length == 0)
            return;

        btn.IsEnabled = false;
        try
        {
            var response = await _ipc.SendCommandAndWaitAsync(
                ServiceCommand.RemoveWhitelist,
                payload: new Dictionary<string, string> { ["domain"] = domain },
                credential: _credential);

            if (!response.Success)
            {
                SummaryText.Text = response.Error ?? "Remove failed.";
                btn.IsEnabled = true;
                return;
            }

            // Re-fetch so the list always reflects the service's truth.
            var snapshot = await _ipc.SendCommandAndWaitAsync(
                ServiceCommand.GetWhitelist, credential: _credential);
            if (snapshot.Success && snapshot.Data is not null)
                Render(snapshot.Data);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        {
            SummaryText.Text = "Cannot reach the Obstruo service. It may be stopped.";
            btn.IsEnabled = true;
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void DragBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DragMove();
}
