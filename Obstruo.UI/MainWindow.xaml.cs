using Microsoft.Extensions.Logging;
using Obstruo.Shared.Enums;
using Obstruo.Shared.Messages;
using Obstruo.UI.Ipc;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Obstruo.UI;

public partial class MainWindow : Window
{
    private readonly IpcClient _ipc;
    private readonly ILogger<MainWindow> _logger;

    private readonly ObservableCollection<LiveFeedItem> _feedItems = new();
    private const int MaxFeedItems = 100;

    // Blocks today (local calendar day). Authoritative value comes from the
    // service's MetricsUpdate (database-backed); incremented locally on each
    // live block event so the headline stays responsive between refreshes.
    private long _blocksToday = 0;

    // Protection currently in emergency pause — drives the Stop/Resume button.
    private bool _isPaused;

    // ── Category counters (today, DB-backed via MetricsUpdate) ───────────────
    private readonly Dictionary<string, int> _categoryCounts = new()
    {
        ["Adult"] = 0,
        ["Malware"] = 0,
        ["Bypass"] = 0,
        ["Chat"] = 0,
        ["Games"] = 0,
    };
    private const double CategoryBarMaxWidth = 180.0;

    // ── Uptime ────────────────────────────────────────────────────────────────
    // Anchored to the SERVICE start time, derived from StatusUpdate.UptimeSeconds
    // (sent on connect and on every state change). Null until the first update —
    // this is protection uptime, not how long the dashboard has been open.
    private DateTime? _serviceStartUtc;
    private DispatcherTimer? _uptimeTimer;

    // ── Threat bar pulse (decoration only — color reflects real state) ───────
    private Storyboard? _threatBarStoryboard;

    // ── Feed filter ───────────────────────────────────────────────────────────
    private ICollectionView? _feedView;
    private string _activeFilter = "All";

    // ─────────────────────────────────────────────────────────────────────────

    public MainWindow(IpcClient ipc, ILogger<MainWindow> logger)
    {
        _ipc = ipc;
        _logger = logger;
        InitializeComponent();
    }

    // ── Startup ───────────────────────────────────────────────────────────────

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            LiveFeed.ItemsSource = _feedItems;
            _feedView = CollectionViewSource.GetDefaultView(_feedItems);
            _feedView.Filter = FilterFeedItem;

            _ipc.HeartbeatReceived += OnHeartbeat;
            _ipc.LogEventReceived += OnLogEvent;
            _ipc.AlertReceived += OnAlert;
            _ipc.StatusUpdateReceived += OnStatusUpdate;
            _ipc.MetricsUpdateReceived += OnMetricsUpdate;
            _ipc.ConnectionChanged += OnConnectionChanged;

            UpdateConnectionUi(_ipc.IsConnected);

            BuildVersionText.Text = $"v{Obstruo.Shared.ObstruoVersion.DisplayVersion}";

            StartUptimeCounter();
            StartThreatBarPulse();

            if (_ipc.IsConnected)
                _ = RefreshSafeSearchTilesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Dashboard load error:\n\n{ex.Message}\n\n{ex.StackTrace}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    // ── Uptime counter ────────────────────────────────────────────────────────

    private void StartUptimeCounter()
    {
        _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uptimeTimer.Tick += (_, _) =>
        {
            if (_serviceStartUtc is null)
            {
                UptimeText.Text = "--:--:--";
                return;
            }

            var t = DateTime.UtcNow - _serviceStartUtc.Value;
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            UptimeText.Text = $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
        };
        _uptimeTimer.Start();
    }

    // ── Threat bar height pulse (decoration; color set by protection state) ──

    private void StartThreatBarPulse()
    {
        var entries = new (Border Bar, double Base)[]
        {
            (TBar1,  5.0),
            (TBar2,  8.0),
            (TBar3, 11.0),
            (TBar4,  8.0),
            (TBar5,  5.0),
        };

        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var halfCycle = new Duration(TimeSpan.FromMilliseconds(1100));

        foreach (var entry in entries)
        {
            var anim = new DoubleAnimation
            {
                From = entry.Base - 1.5,
                To = entry.Base + 1.5,
                Duration = halfCycle,
                AutoReverse = true,
                EasingFunction = ease,
            };
            Storyboard.SetTarget(anim, entry.Bar);
            Storyboard.SetTargetProperty(anim, new PropertyPath(FrameworkElement.HeightProperty));
            sb.Children.Add(anim);
        }

        _threatBarStoryboard = sb;
        sb.Begin();
    }

    // ── Feed filter ───────────────────────────────────────────────────────────

    private bool FilterFeedItem(object obj)
    {
        if (_activeFilter == "All") return true;
        if (obj is not LiveFeedItem item) return false;
        return _activeFilter switch
        {
            "AI" => item.Category.Equals("AIAdult", StringComparison.OrdinalIgnoreCase),
            _ => item.Category.Equals(_activeFilter, StringComparison.OrdinalIgnoreCase),
        };
    }

    private void FilterTag_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is string category)
            SetFilter(category);
    }

    private void SetFilter(string category)
    {
        _activeFilter = category;
        _feedView?.Refresh();
        UpdateFilterTagVisuals(category);
    }

    private void UpdateFilterTagVisuals(string active)
    {
        var activeBg = Rgb(0x3F, 0x2D, 0x9B);
        var inactiveBg = Rgb(0x05, 0x0B, 0x15);
        var activeBorder = Rgb(0x6A, 0x5C, 0xFF);
        var inactiveBorder = Rgb(0x1B, 0x1F, 0x2C);
        var activeFg = Rgb(0xE0, 0xDC, 0xFF);
        var inactiveFg = Rgb(0x78, 0x80, 0xA8);

        void Apply(Border b, TextBlock t, string key)
        {
            bool on = key == active;
            b.Background = on ? activeBg : inactiveBg;
            b.BorderBrush = on ? activeBorder : inactiveBorder;
            t.Foreground = on ? activeFg : inactiveFg;
        }

        Apply(FilterTagAll, FilterTagAllText, "All");
        Apply(FilterTagAdult, FilterTagAdultText, "Adult");
        Apply(FilterTagChat, FilterTagChatText, "Chat");
        Apply(FilterTagGames, FilterTagGamesText, "Games");
        Apply(FilterTagAI, FilterTagAIText, "AI");
        Apply(FilterTagBypass, FilterTagBypassText, "Bypass");
        Apply(FilterTagMalware, FilterTagMalwareText, "Malware");
    }

    // ── Incident modal ────────────────────────────────────────────────────────

    private void LiveFeed_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LiveFeed.SelectedItem is not LiveFeedItem item) return;

        var source = item.Category == "Custom" ? "custom" : "obstruo-builtin";
        var blocked = source == "custom" ? "Custom Rule" : "System Blocklist";

        var detail = new IncidentDetail
        {
            Domain = item.Domain,
            PlainDomain = item.PlainDomain,
            Time = item.Time,
            RelativeTime = item.RelativeTime,
            CategoryLabel = item.CategoryLabel,
            SeverityLabel = item.SeverityLabel,
            DeviceName = string.IsNullOrEmpty(item.DeviceName) ? "Unknown" : item.DeviceName,
            Source = source,
            MitreTag = item.MitreTag,
            BlockedBy = blocked,
            CategoryBadgeBg = item.CategoryBadgeBg,
            CategoryTextBrush = item.CategoryTextBrush,
            SeverityBadgeBg = item.SeverityBadgeBg,
            SeverityTextBrush = item.SeverityTextBrush,
        };

        ShowIncident(detail);
        LiveFeed.SelectedItem = null; // clear so same row can reopen modal
    }

    private IncidentDetail? _currentIncident;

    private void ShowIncident(IncidentDetail d)
    {
        _currentIncident = d;
        ModalDomain.Text = d.Domain;
        ModalTime.Text = d.Time;
        ModalRelativeTime.Text = d.RelativeTime;
        ModalCategoryBadge.Background = d.CategoryBadgeBg;
        ModalCategoryText.Text = d.CategoryLabel;
        ModalCategoryText.Foreground = d.CategoryTextBrush;
        ModalSeverityBadge.Background = d.SeverityBadgeBg;
        ModalSeverityText.Text = d.SeverityLabel;
        ModalSeverityText.Foreground = d.SeverityTextBrush;
        ModalDevice.Text = d.DeviceName;
        ModalSource.Text = d.Source;
        ModalBlockedBy.Text = d.BlockedBy;

        var mitreVisibility = string.IsNullOrEmpty(d.MitreTag)
            ? Visibility.Collapsed : Visibility.Visible;
        ModalMitreBadge.Visibility = mitreVisibility;
        ModalMitreLabel.Visibility = mitreVisibility;
        ModalMitreText.Text = d.MitreTag;

        IncidentModal.Visibility = Visibility.Visible;
    }

    private void CloseModal()
        => IncidentModal.Visibility = Visibility.Collapsed;

    private void ModalBackdrop_Click(object sender, MouseButtonEventArgs e)
        => CloseModal();

    private void ModalCard_SuppressClick(object sender, MouseButtonEventArgs e)
        => e.Handled = true;

    private void ModalCloseBtn_Click(object sender, RoutedEventArgs e)
        => CloseModal();

    private void ModalAction_Click(object sender, MouseButtonEventArgs e)
        => CloseModal();

    private void ModalWhitelist_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var domain = _currentIncident?.PlainDomain;
        if (string.IsNullOrEmpty(domain)) { CloseModal(); return; }

        CloseModal();
        ShowWhitelistDialog(domain, domainEditable: false);
    }

    // ── Whitelist / blocklist actions ─────────────────────────────────────────

    private void WhitelistInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            WhitelistAdd_Click(sender, null!);
        }
    }

    private void WhitelistInput_TextChanged(object sender, TextChangedEventArgs e)
        => WhitelistInputHint.Visibility = string.IsNullOrEmpty(WhitelistInput.Text)
            ? Visibility.Visible : Visibility.Collapsed;

    private void WhitelistAdd_Click(object sender, MouseButtonEventArgs e)
    {
        var domain = WhitelistInput.Text.Trim();
        ShowWhitelistDialog(
            string.IsNullOrEmpty(domain) ? null : domain,
            domainEditable: true);
    }

    private void ShowWhitelistDialog(string? domain, bool domainEditable)
    {
        var dialog = new DomainCredentialDialog(
            title: "Add to Whitelist",
            description: "The domain and all of its subdomains will always be allowed, " +
                         "overriding every blocklist rule. Enter your PIN or password to confirm.",
            confirmLabel: "Add to Whitelist",
            domain: domain,
            domainEditable: domainEditable)
        {
            Owner = this,
            Action = (d, credential) => _ipc.SendCommandAndWaitAsync(
                ServiceCommand.AddWhitelist,
                payload: new Dictionary<string, string> { ["domain"] = d },
                credential: credential),
        };
        dialog.ShowDialog();

        if (dialog.Succeeded)
            WhitelistInput.Clear();
    }

    /// <summary>
    /// Credential-gated whitelist viewer: one PIN prompt fetches the list, and
    /// the verified credential stays with the viewer window so Remove works
    /// without re-prompting.
    /// </summary>
    private void WhitelistView_Click(object sender, MouseButtonEventArgs e)
    {
        string? snapshotJson = null;
        string? verifiedCredential = null;

        var dialog = new DomainCredentialDialog(
            title: "View Whitelist",
            description: "Enter your PIN or password to view the whitelisted domains.",
            confirmLabel: "View Whitelist",
            requireDomain: false)
        {
            Owner = this,
            Action = async (_, credential) =>
            {
                var response = await _ipc.SendCommandAndWaitAsync(
                    ServiceCommand.GetWhitelist, credential: credential);
                if (response.Success)
                {
                    snapshotJson = response.Data;
                    verifiedCredential = credential;
                }
                return response;
            },
        };
        dialog.ShowDialog();

        if (dialog.Succeeded && snapshotJson is not null && verifiedCredential is not null)
        {
            new WhitelistViewWindow(_ipc, verifiedCredential, snapshotJson) { Owner = this }
                .ShowDialog();
        }
    }

    private void BlocklistAdd_Click(object sender, MouseButtonEventArgs e)
    {
        var dialog = new DomainCredentialDialog(
            title: "Block a Domain",
            description: "The domain and all of its subdomains will be blocked. Choose a duration — " +
                         "a temporary block lifts itself automatically. Enter your PIN or password to confirm.",
            confirmLabel: "Block Domain",
            showDuration: true)
        {
            Owner = this,
        };
        // Set Action after construction so the closure can read the dialog's
        // selected duration when the command fires.
        dialog.Action = (d, credential) =>
        {
            var payload = new Dictionary<string, string> { ["domain"] = d };
            if (dialog.DurationMinutes is { } mins)
                payload["expiresMinutes"] = mins.ToString();
            return _ipc.SendCommandAndWaitAsync(
                ServiceCommand.AddDomain, payload: payload, credential: credential);
        };
        dialog.ShowDialog();
    }

    // ── IPC event handlers ────────────────────────────────────────────────────

    private void OnHeartbeat(object? sender, HeartbeatMessage hb)
    {
        // Note: hb.BlockCountTotal is "since service start" — deliberately NOT
        // shown. The headline number is "blocked today" from MetricsUpdate.
        Dispatcher.Invoke(() =>
        {
            UpdateConnectionUi(true);
            UpdateProtectionState(hb.ProtectionState);
        });
    }

    private void OnStatusUpdate(object? sender, StatusUpdateMessage status)
    {
        Dispatcher.Invoke(() =>
        {
            // Anchor uptime to the service's reported value so the counter tracks
            // protection uptime across dashboard opens and reconnects.
            if (status.UptimeSeconds >= 0)
                _serviceStartUtc = DateTime.UtcNow.AddSeconds(-status.UptimeSeconds);

            UpdateProtectionState(status.ProtectionState);
            UpdateDnsHealth(status.UpstreamHealthy);
            UpdateRuleCounts(status.RuleCounts);
        });
    }

    private void UpdateDnsHealth(bool healthy)
    {
        DnsHealthText.Text = healthy
            ? "Healthy — forwarding normally"
            : "Not responding — lookups failing closed";
        DnsHealthText.Foreground = new SolidColorBrush(healthy
            ? Color.FromRgb(0x60, 0xC8, 0x88)
            : Color.FromRgb(0xEF, 0x44, 0x44));
        DnsHealthSub.Text = healthy
            ? "Health of the resolvers Obstruo forwards to"
            : "Network changed or offline — check your connection";
    }

    private void UpdateRuleCounts(Dictionary<string, int>? counts)
    {
        if (counts is null || counts.Count == 0)
            return;   // pre-1.0.3 service — leave the loading placeholders

        var total = counts.Values.Sum();
        TileRulesText.Text = $"{total:N0} rules · Ships with app updates";
        RuleCountText.Text = total.ToString("N0");
        HealthRulesText.Text = $"{total:N0} rules loaded · Updates ship with new versions";
        BlocklistFooterText.Text = $"{total:N0} system rules active · domains masked by design";
        CategoryChips.ItemsSource = counts.Select(kv => $"{kv.Key} ({kv.Value:N0})").ToList();
    }

    private void OnMetricsUpdate(object? sender, MetricsUpdateMessage metrics)
    {
        Dispatcher.Invoke(() => ApplyMetrics(metrics));
    }

    private void OnLogEvent(object? sender, LogEventMessage le)
    {
        Dispatcher.Invoke(() =>
        {
            var source = le.Category == BlockCategory.Custom ? "custom" : "obstruo-builtin";
            var masked = Obstruo.Shared.DomainMasker.MaskBySource(le.Domain, source);

            var (catBg, catFg) = CategoryBadgeColors(le.Category);
            var (sevBg, sevFg) = SeverityBadgeColors(le.Severity);

            var item = new LiveFeedItem
            {
                Time = ParseTimestamp(le.Timestamp),
                RelativeTime = ComputeRelativeTime(le.Timestamp),
                Domain = masked,
                PlainDomain = le.Domain,
                Category = le.Category.ToString(),
                CategoryLabel = le.Category switch
                {
                    BlockCategory.AIAdult => "AI ADULT",
                    BlockCategory.Adult => "ADULT",
                    BlockCategory.Malware => "MALWARE",
                    BlockCategory.Chat => "CHAT",
                    BlockCategory.Games => "GAMES",
                    BlockCategory.Bypass => "BYPASS",
                    BlockCategory.Custom => "CUSTOM",
                    _ => le.Category.ToString().ToUpperInvariant(),
                },
                Severity = le.Severity.ToString(),
                SeverityLabel = le.Severity.ToString().ToUpperInvariant(),
                DeviceName = le.DeviceName,
                MitreTag = le.Mitre ?? "",
                CategoryBadgeBg = catBg,
                CategoryTextBrush = catFg,
                SeverityBadgeBg = sevBg,
                SeverityTextBrush = sevFg,
            };

            _feedItems.Insert(0, item);

            if (_feedItems.Count > MaxFeedItems)
                _feedItems.RemoveAt(_feedItems.Count - 1);

            _blocksToday++;
            TotalBlocksText.Text = _blocksToday.ToString("N0");

            UpdateCategoryStats(le.Category);

            if (EmptyState.Visibility == Visibility.Visible)
            {
                EmptyState.Visibility = Visibility.Collapsed;
                LiveFeed.Visibility = Visibility.Visible;
            }

            FeedCountBadge.Visibility = Visibility.Visible;
            FeedCountText.Text = _feedItems.Count.ToString();
        });
    }

    private void OnAlert(object? sender, AlertMessage alert)
    {
        Dispatcher.Invoke(() => ShowAlert(alert));
    }

    private void OnConnectionChanged(object? sender, bool connected)
    {
        Dispatcher.Invoke(() => UpdateConnectionUi(connected));
        if (connected)
            Dispatcher.Invoke(() => _ = RefreshSafeSearchTilesAsync());
    }

    // ── SafeSearch status tiles ───────────────────────────────────────────────
    // Reflects the live per-engine SafeSearch state from GetSettings (no
    // credential needed). Refreshed on connect; a change made in the Settings
    // window shows on the next reconnect. Purely cosmetic — never disrupts the UI.
    private async Task RefreshSafeSearchTilesAsync()
    {
        try
        {
            var response = await _ipc.SendCommandAndWaitAsync(ServiceCommand.GetSettings);
            if (!response.Success || string.IsNullOrEmpty(response.Data)) return;

            using var doc = System.Text.Json.JsonDocument.Parse(response.Data);
            if (!doc.RootElement.TryGetProperty("config", out var cfg)) return;

            void Set(string key, System.Windows.Shapes.Ellipse dot, TextBlock status)
            {
                var on = cfg.TryGetProperty(key, out var v) && v.GetString() == "1";
                status.Text = on ? "Active" : "Not active";
                status.Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(on ? "#22C55E" : "#3a4060"));
                dot.Fill = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(on ? "#22C55E" : "#2a2e42"));
            }

            Set("safesearch_google", SafeSearchGoogleDot, SafeSearchGoogleStatus);
            Set("safesearch_youtube", SafeSearchYouTubeDot, SafeSearchYouTubeStatus);
            Set("safesearch_bing", SafeSearchBingDot, SafeSearchBingStatus);
        }
        catch { /* cosmetic tile — never break the dashboard over it */ }
    }

    // ── Category stats ────────────────────────────────────────────────────────
    // Authoritative counts arrive via MetricsUpdate (today, from the DB).
    // Live block events increment locally between refreshes for responsiveness.

    /// <summary>
    /// Maps a category label (BlockCategory value or DB category name — the DB
    /// has extra names like Paid/SexChat) to its dashboard bar row.
    /// AIAdult/Paid count under Adult, SexChat under Chat; Custom has no row.
    /// </summary>
    private static string? CategoryBarKey(string category) =>
        category.ToLowerInvariant() switch
        {
            "adult" => "Adult",
            "aiadult" => "Adult",
            "paid" => "Adult",
            "malware" => "Malware",
            "bypass" => "Bypass",
            "chat" => "Chat",
            "sexchat" => "Chat",
            "games" => "Games",
            _ => null,
        };

    private void UpdateCategoryStats(BlockCategory category)
    {
        var key = CategoryBarKey(category.ToString());
        if (key is null) return;

        _categoryCounts[key]++;
        RenderCategoryBars();
    }

    private void RenderCategoryBars()
    {
        var max = Math.Max(1, _categoryCounts.Values.Max());

        void Apply(string k, Border bar, TextBlock count)
        {
            var c = _categoryCounts[k];
            count.Text = c.ToString("N0");
            bar.Width = (double)c / max * CategoryBarMaxWidth;
        }

        Apply("Adult", BarFillAdult, CatCountAdult);
        Apply("Malware", BarFillMalware, CatCountMalware);
        Apply("Bypass", BarFillBypass, CatCountBypass);
        Apply("Chat", BarFillChat, CatCountChat);
        Apply("Games", BarFillGames, CatCountGames);

        BypassAttemptsText.Text = _categoryCounts["Bypass"].ToString("N0");
        BypassBlockedText.Text = _categoryCounts["Bypass"].ToString("N0");
    }

    // ── Metrics (database-backed, pushed by the service) ─────────────────────

    private void ApplyMetrics(MetricsUpdateMessage metrics)
    {
        // Headline numbers
        _blocksToday = metrics.BlocksToday;
        TotalBlocksText.Text = _blocksToday.ToString("N0");
        WeekBlocksText.Text = metrics.BlocksWeek.ToString("N0");

        // Category bars — replace local counters with DB truth
        foreach (var k in _categoryCounts.Keys.ToList())
            _categoryCounts[k] = 0;
        foreach (var cc in metrics.ByCategory)
        {
            var key = CategoryBarKey(cc.Category);
            if (key is not null)
                _categoryCounts[key] += cc.Count;
        }
        RenderCategoryBars();

        // 24-hour activity chart
        var bars = new int[24];
        var any = false;
        foreach (var bar in metrics.HourlyBars)
        {
            if (bar.Hour is >= 0 and < 24)
            {
                bars[bar.Hour] = bar.Count;
                any |= bar.Count > 0;
            }
        }
        HourlyChart.Draw(bars);
        ChartEmptyText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;

        // Top blocked domains — masked with the same rules as the live feed
        if (metrics.TopDomains.Count > 0)
        {
            var rows = new List<TopDomainRow>();
            var rank = 1;
            foreach (var hit in metrics.TopDomains)
            {
                var source = hit.Category.Equals("Custom", StringComparison.OrdinalIgnoreCase)
                    ? "custom" : "obstruo-builtin";
                var (catBg, catFg) = CategoryBadgeColors(ParseCategory(hit.Category));
                rows.Add(new TopDomainRow
                {
                    Rank = rank++.ToString(),
                    Domain = Obstruo.Shared.DomainMasker.MaskBySource(hit.Domain, source),
                    Hits = hit.Hits.ToString("N0"),
                    CategoryLabel = hit.Category.ToUpperInvariant(),
                    CategoryBadgeBg = catBg,
                    CategoryTextBrush = catFg,
                });
            }
            TopDomainsList.ItemsSource = rows;
            TopDomainsList.Visibility = Visibility.Visible;
            TopDomainsEmpty.Visibility = Visibility.Collapsed;
        }
        else
        {
            TopDomainsList.Visibility = Visibility.Collapsed;
            TopDomainsEmpty.Visibility = Visibility.Visible;
        }
    }

    /// <summary>DB category name → BlockCategory for badge coloring.</summary>
    private static BlockCategory ParseCategory(string name) =>
        name.ToLowerInvariant() switch
        {
            "adult" => BlockCategory.Adult,
            "aiadult" => BlockCategory.AIAdult,
            "paid" => BlockCategory.Adult,
            "chat" => BlockCategory.Chat,
            "sexchat" => BlockCategory.Chat,
            "games" => BlockCategory.Games,
            "malware" => BlockCategory.Malware,
            "bypass" => BlockCategory.Bypass,
            _ => BlockCategory.Custom,
        };

    // ── Alert display ─────────────────────────────────────────────────────────

    private void ShowAlert(AlertMessage alert)
    {
        string text;

        switch (alert.AlertType)
        {
            case AlertType.TamperDetected:
                text = "⚠  Tampering detected — DNS settings were modified externally. Obstruo has restored them.";
                break;

            case AlertType.LanIpChanged:
                if (alert.Message.StartsWith("FIRST_RUN:", StringComparison.Ordinal))
                {
                    var ip = alert.Message["FIRST_RUN:".Length..];
                    text = $"🌐  LAN mode active. Set your router's DNS server to: {ip}";
                    SetLanIpDisplay(ip);
                }
                else if (alert.Message.StartsWith("IP_CHANGED:", StringComparison.Ordinal))
                {
                    var parts = alert.Message.Split(':');
                    var newIp = parts.Length >= 3 ? parts[2] : "unknown";
                    text = $"🌐  Your LAN IP changed. Update your router's DNS server to: {newIp}";
                    SetLanIpDisplay(newIp);
                }
                else
                {
                    text = alert.Message;
                }
                break;

            case AlertType.Port53Conflict:
                text = "⚠  Port 53 conflict detected. Check the service logs for details.";
                break;

            case AlertType.ProxyUnresponsive:
                text = "⚠  The DNS filter stopped responding. Obstruo is restarting itself — internet resumes when the filter is back.";
                break;

            default:
                text = alert.Message;
                break;
        }

        AlertText.Text = text;

        var isCritical = alert.Severity == Severity.Critical || alert.Severity == Severity.High;
        AlertBanner.Background = new SolidColorBrush(isCritical
            ? Color.FromRgb(0x1A, 0x06, 0x06)
            : Color.FromRgb(0x1A, 0x12, 0x00));
        AlertBanner.BorderBrush = new SolidColorBrush(isCritical
            ? Color.FromRgb(0xEF, 0x44, 0x44)
            : Color.FromRgb(0xF5, 0x9E, 0x0B));
        AlertText.Foreground = new SolidColorBrush(isCritical
            ? Color.FromRgb(0xEF, 0x44, 0x44)
            : Color.FromRgb(0xF5, 0x9E, 0x0B));

        AlertBanner.Visibility = Visibility.Visible;
    }

    private void DismissAlert_Click(object sender, RoutedEventArgs e)
        => AlertBanner.Visibility = Visibility.Collapsed;

    // ── UI state helpers ──────────────────────────────────────────────────────

    private void UpdateConnectionUi(bool connected)
    {
        ConnectionDot.Fill = new SolidColorBrush(connected
            ? Color.FromRgb(0x10, 0xB9, 0x81)
            : Color.FromRgb(0x6B, 0x72, 0x80));
        ConnectionText.Text = connected
            ? "Service connected"
            : "Connecting to service...";

        if (!connected)
            SetThreatLevel("UNKNOWN", Color.FromRgb(0x88, 0x90, 0xB8));
    }

    private void UpdateProtectionState(ProtectionState state)
    {
        var (label, chipBg, chipFg) = state switch
        {
            ProtectionState.Active => ("Active", Color.FromRgb(0x06, 0x3E, 0x24), Color.FromRgb(0x10, 0xB9, 0x81)),
            ProtectionState.DisabledTemporary => ("Paused", Color.FromRgb(0x3D, 0x2A, 0x00), Color.FromRgb(0xF5, 0x9E, 0x0B)),
            ProtectionState.Tampered => ("Tampered", Color.FromRgb(0x3D, 0x08, 0x08), Color.FromRgb(0xEF, 0x44, 0x44)),
            ProtectionState.Recovering => ("Recovering", Color.FromRgb(0x3D, 0x2A, 0x00), Color.FromRgb(0xF5, 0x9E, 0x0B)),
            ProtectionState.Error => ("Error", Color.FromRgb(0x3D, 0x08, 0x08), Color.FromRgb(0xEF, 0x44, 0x44)),
            _ => ("Unknown", Color.FromRgb(0x1E, 0x1E, 0x32), Color.FromRgb(0x8B, 0x8B, 0xA7)),
        };

        ProtectionStateText.Text = label;
        ProtectionChip.Background = new SolidColorBrush(chipBg);
        ProtectionStateText.Foreground = new SolidColorBrush(chipFg);
        ProtectionDot.Fill = new SolidColorBrush(chipFg);

        // Stop/Resume button follows the pause state
        _isPaused = state == ProtectionState.DisabledTemporary;
        StopBtnText.Text = _isPaused ? "Resume Protection" : "Stop Protection";
        var stopFg = new SolidColorBrush(_isPaused
            ? Color.FromRgb(0x10, 0xB9, 0x81)
            : Color.FromRgb(0xFF, 0x80, 0x80));
        StopBtnText.Foreground = stopFg;
        StopBtnIcon.Foreground = stopFg;
        StopBtn.Background = new SolidColorBrush(_isPaused
            ? Color.FromRgb(0x03, 0x14, 0x0C)
            : Color.FromRgb(0x1A, 0x04, 0x04));
        StopBtn.BorderBrush = new SolidColorBrush(_isPaused
            ? Color.FromRgb(0x1A, 0x40, 0x28)
            : Color.FromRgb(0x7A, 0x1A, 0x1A));

        // Threat level — computed from the same real signal.
        var (level, levelColor) = state switch
        {
            ProtectionState.Active => ("NOMINAL", Color.FromRgb(0x10, 0xB9, 0x81)),
            ProtectionState.DisabledTemporary => ("ELEVATED", Color.FromRgb(0xD4, 0xAA, 0x7C)),
            ProtectionState.Recovering => ("ELEVATED", Color.FromRgb(0xD4, 0xAA, 0x7C)),
            ProtectionState.Tampered => ("CRITICAL", Color.FromRgb(0xEF, 0x44, 0x44)),
            ProtectionState.Error => ("CRITICAL", Color.FromRgb(0xEF, 0x44, 0x44)),
            _ => ("UNKNOWN", Color.FromRgb(0x88, 0x90, 0xB8)),
        };
        SetThreatLevel(level, levelColor);
    }

    private void SetThreatLevel(string label, Color color)
    {
        ThreatLevelText.Text = label;
        var brush = new SolidColorBrush(color);
        ThreatLevelText.Foreground = brush;
        TBar1.Background = brush;
        TBar2.Background = brush;
        TBar3.Background = brush;
        TBar4.Background = brush;
        TBar5.Background = brush;
    }

    private void SetLanIpDisplay(string ip)
    {
        LanIpText.Text = ip;
        LanIpPanel.Visibility = Visibility.Visible;
        LanIpNoneText.Visibility = Visibility.Collapsed;
    }

    // ── Color helpers ─────────────────────────────────────────────────────────

    private static SolidColorBrush Rgb(byte r, byte g, byte b)
        => new(Color.FromRgb(r, g, b));

    private static (Brush bg, Brush fg) CategoryBadgeColors(BlockCategory category) =>
        category switch
        {
            BlockCategory.Adult => (Rgb(0x22, 0x00, 0x18), Rgb(0xD0, 0x60, 0xA0)),
            BlockCategory.AIAdult => (Rgb(0x22, 0x00, 0x18), Rgb(0xD0, 0x60, 0xA0)),
            BlockCategory.Malware => (Rgb(0x1E, 0x00, 0x20), Rgb(0xC0, 0x40, 0xC0)),
            BlockCategory.Chat => (Rgb(0x18, 0x0E, 0x00), Rgb(0xC0, 0x88, 0x40)),
            BlockCategory.Games => (Rgb(0x00, 0x10, 0x28), Rgb(0x60, 0x88, 0xC8)),
            BlockCategory.Bypass => (Rgb(0x18, 0x12, 0x00), Rgb(0xD4, 0xAA, 0x7C)),
            BlockCategory.Custom => (Rgb(0x06, 0x04, 0x1A), Rgb(0xB0, 0xA8, 0xFF)),
            _ => (Rgb(0x1E, 0x1E, 0x32), Rgb(0x88, 0x90, 0xB8)),
        };

    private static (Brush bg, Brush fg) SeverityBadgeColors(Severity severity) =>
        severity switch
        {
            Severity.Critical => (Rgb(0x1E, 0x00, 0x20), Rgb(0xC0, 0x40, 0xC0)),
            Severity.High => (Rgb(0x1E, 0x00, 0x20), Rgb(0xC0, 0x40, 0xC0)),
            Severity.Med => (Rgb(0x1A, 0x10, 0x00), Rgb(0xD4, 0xAA, 0x7C)),
            Severity.Low => (Rgb(0x0A, 0x0F, 0x1A), Rgb(0x88, 0x90, 0xB8)),
            _ => (Rgb(0x0A, 0x0F, 0x1A), Rgb(0x88, 0x90, 0xB8)),
        };

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ParseTimestamp(string iso)
    {
        if (DateTime.TryParse(iso, out var dt))
            return dt.ToLocalTime().ToString("HH:mm:ss");
        return "--:--:--";
    }

    private static string ComputeRelativeTime(string iso)
    {
        if (!DateTime.TryParse(iso, out var dt)) return "";
        var diff = DateTime.Now - dt.ToLocalTime();
        if (diff.TotalSeconds < 60) return "just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        return $"{(int)diff.TotalHours}h ago";
    }

    // ── Window chrome ─────────────────────────────────────────────────────────

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
        }
        else
        {
            if (WindowState == WindowState.Maximized) return; // DragMove from maximized needs restore logic — deferred
            DragMove();
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        => ToggleMaximize();

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            MaxHeight = double.PositiveInfinity;
            MaxWidth = double.PositiveInfinity;
        }
        else
        {
            // Cap to work area so a WindowStyle="None" window doesn't cover the taskbar.
            // Set every time — work area changes with monitor/taskbar configuration.
            var wa = SystemParameters.WorkArea;
            MaxHeight = wa.Height;
            MaxWidth = wa.Width;
            WindowState = WindowState.Maximized;
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
        => Close();

    // ── Emergency stop / resume ──────────────────────────────────────────────

    private async void StopBtn_Click(object sender, MouseButtonEventArgs e)
    {
        if (_isPaused)
        {
            // Resume needs no credential — it only makes the system stricter.
            try
            {
                var response = await _ipc.SendCommandAndWaitAsync(ServiceCommand.EmergencyResume);
                if (!response.Success)
                    MessageBox.Show(response.Error ?? "Resume was rejected.", "Obstruo",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.IO.IOException)
            {
                MessageBox.Show("Cannot reach the Obstruo service.", "Obstruo",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        var dialog = new EmergencyStopWindow(_ipc) { Owner = this };
        dialog.ShowDialog();
    }

    // ── Uninstall ─────────────────────────────────────────────────────────────

    private void UninstallBtn_Click(object sender, MouseButtonEventArgs e)
    {
        var dialog = new UninstallWindow(_ipc) { Owner = this };
        dialog.ShowDialog();
    }

    // ── Settings ──────────────────────────────────────────────────────────────

    private void SettingsBtn_Click(object sender, MouseButtonEventArgs e)
    {
        var dialog = new SettingsWindow(_ipc) { Owner = this };
        dialog.ShowDialog();
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    protected override void OnClosed(EventArgs e)
    {
        _uptimeTimer?.Stop();
        _threatBarStoryboard?.Stop();
        _ipc.HeartbeatReceived -= OnHeartbeat;
        _ipc.LogEventReceived -= OnLogEvent;
        _ipc.AlertReceived -= OnAlert;
        _ipc.StatusUpdateReceived -= OnStatusUpdate;
        _ipc.MetricsUpdateReceived -= OnMetricsUpdate;
        _ipc.ConnectionChanged -= OnConnectionChanged;
        base.OnClosed(e);
    }
}

// ── Live feed item ────────────────────────────────────────────────────────────

public class LiveFeedItem
{
    public string Time { get; set; } = "";
    public string RelativeTime { get; set; } = "";
    public string Domain { get; set; } = "";
    public string PlainDomain { get; set; } = "";   // unmasked — needed for whitelist actions
    public string Category { get; set; } = "";
    public string CategoryLabel { get; set; } = "";
    public string Severity { get; set; } = "";
    public string SeverityLabel { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string MitreTag { get; set; } = "";
    public Brush CategoryBadgeBg { get; set; } = Brushes.Transparent;
    public Brush CategoryTextBrush { get; set; } = Brushes.Gray;
    public Brush SeverityBadgeBg { get; set; } = Brushes.Transparent;
    public Brush SeverityTextBrush { get; set; } = Brushes.Gray;
}

// ── Top blocked domain row ────────────────────────────────────────────────────

public class TopDomainRow
{
    public string Rank { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Hits { get; set; } = "";
    public string CategoryLabel { get; set; } = "";
    public Brush CategoryBadgeBg { get; set; } = Brushes.Transparent;
    public Brush CategoryTextBrush { get; set; } = Brushes.Gray;
}

// ── Incident drilldown data ───────────────────────────────────────────────────

public class IncidentDetail
{
    public string Domain { get; set; } = "";
    public string PlainDomain { get; set; } = "";
    public string Time { get; set; } = "";
    public string RelativeTime { get; set; } = "";
    public string CategoryLabel { get; set; } = "";
    public string SeverityLabel { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Source { get; set; } = "";
    public string MitreTag { get; set; } = "";
    public string BlockedBy { get; set; } = "";
    public Brush CategoryBadgeBg { get; set; } = Brushes.Transparent;
    public Brush CategoryTextBrush { get; set; } = Brushes.Gray;
    public Brush SeverityBadgeBg { get; set; } = Brushes.Transparent;
    public Brush SeverityTextBrush { get; set; } = Brushes.Gray;
}