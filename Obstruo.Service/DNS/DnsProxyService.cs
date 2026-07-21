using ARSoft.Tools.Net;
using ARSoft.Tools.Net.Dns;
using Microsoft.Extensions.Logging;
using Obstruo.Service.Data;
using Obstruo.Shared.Enums;
using Obstruo.Shared.Messages;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Obstruo.Service.Dns;

/// <summary>
/// Local DNS proxy.
///
/// Bindings (all on port 53):
///   127.0.0.1   — always (loopback, system DNS)
///   ::1         — always attempted; skipped gracefully if IPv6 is disabled on the machine
///   LAN IP      — when LanModeService detects a private IP (e.g. 192.168.x.x)
///                 allows router clients to use Obstruo as their DNS server
///
/// Blocked domains → NXDOMAIN (fail-closed).
/// Allowed domains → forwarded to user's real upstream DNS (shadow mode).
/// Upstream failure → ServerFailure (fail-closed, never fail-open).
/// Every block fires a LogEventMessage over IPC and a BlockedEventRecord into LogEventWriter.
/// Neither call blocks the DNS query thread.
/// </summary>
public sealed class DnsProxyService : IDisposable
{
    private readonly DnsBlocklistStore _blocklist;
    private readonly LogEventWriter _logWriter;
    private readonly DnsSettingsManager _dnsSettings;
    private readonly IpcServer _ipcServer;
    private readonly LanModeService _lanMode;
    private readonly QueryLatencyTracker _latency;
    private readonly SafeSearchRewriter _safeSearch;
    private readonly ILogger<DnsProxyService> _logger;

    private DnsServer? _server;
    private List<DnsClient> _upstreamClients = [];
    private int _preferredUpstream;   // index of the last upstream that answered — tried first
    private bool _started;
    private bool _disposed;

    private static readonly string _deviceName = Environment.MachineName;

    /// <summary>
    /// Name the HealthMonitor probes to prove the proxy is alive. It is answered
    /// locally (see OnQueryReceivedAsync) and never forwarded upstream, so the
    /// probe measures proxy liveness only — not upstream reachability. Without
    /// this, an ordinary internet outage makes the probe time out and the monitor
    /// restart-loops the service.
    /// </summary>
    public const string HealthProbeDomain = "health-probe.obstruo.invalid";

    public DnsProxyService(
        DnsBlocklistStore blocklist,
        LogEventWriter logWriter,
        DnsSettingsManager dnsSettings,
        IpcServer ipcServer,
        LanModeService lanMode,
        QueryLatencyTracker latency,
        SafeSearchRewriter safeSearch,
        ILogger<DnsProxyService> logger)
    {
        _blocklist = blocklist;
        _logWriter = logWriter;
        _dnsSettings = dnsSettings;
        _ipcServer = ipcServer;
        _lanMode = lanMode;
        _latency = latency;
        _safeSearch = safeSearch;
        _logger = logger;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;

        // Shadow mode — upstream resolved after BackupAndSetDns() has already run.
        BuildUpstreamClients("Upstream DNS (shadow mode)");

        // ── Build transport list ───────────────────────────────────────────────
        var transports = new List<IServerTransport>();

        // IPv4 loopback — always
        var loopbackEndpoint = new IPEndPoint(IPAddress.Loopback, 53);
        transports.Add(new UdpServerTransport(loopbackEndpoint));
        transports.Add(new TcpServerTransport(loopbackEndpoint));
        _logger.LogInformation("DNS binding: 127.0.0.1:53");

        // IPv6 loopback — attempted, skipped gracefully if IPv6 disabled
        if (ProbeIPv6Available())
        {
            var ipv6Endpoint = new IPEndPoint(IPAddress.IPv6Loopback, 53);
            transports.Add(new UdpServerTransport(ipv6Endpoint));
            transports.Add(new TcpServerTransport(ipv6Endpoint));
            _logger.LogInformation("DNS binding: [::1]:53");
        }
        else
        {
            _logger.LogWarning("IPv6 not available — skipping [::1]:53 binding (IPv4 only)");
        }

        // LAN IP — when LanModeService detected a private IP
        if (_lanMode.CurrentLanIp is not null)
        {
            var lanEndpoint = new IPEndPoint(IPAddress.Parse(_lanMode.CurrentLanIp), 53);
            transports.Add(new UdpServerTransport(lanEndpoint));
            transports.Add(new TcpServerTransport(lanEndpoint));
            _logger.LogInformation("DNS binding: {LanIp}:53 (LAN mode)", _lanMode.CurrentLanIp);
        }
        else
        {
            _logger.LogWarning("No LAN IP available — LAN DNS binding skipped");
        }

        // ── Start server ──────────────────────────────────────────────────────
        _server = new DnsServer(transports.ToArray());
        _server.QueryReceived += OnQueryReceivedAsync;
        _server.Start();

        _logger.LogInformation("DNS proxy started on {Count} transport(s)", transports.Count);
    }

    private void BuildUpstreamClients(string logContext)
    {
        var upstreamServers = _dnsSettings.GetUpstreamDnsServers();

        // Swap the whole list reference — the query path snapshots it once per
        // query, so in-flight lookups keep a consistent view.
        _upstreamClients = upstreamServers
            .Select(ip => new DnsClient(IPAddress.Parse(ip), 2000))
            .ToList();
        _preferredUpstream = 0;

        _logger.LogInformation("{Context}: {Servers}",
            logContext, string.Join(", ", upstreamServers));
    }

    /// <summary>
    /// Rebuilds the upstream client list from the current DNS backup. Called by
    /// the network-change watcher: the clients were previously built once at
    /// Start, so a network switch could leave the proxy forwarding to resolvers
    /// that no longer exist while the :53 firewall was re-computed around a
    /// fresher set.
    /// </summary>
    public void RefreshUpstreams()
    {
        if (!_started) return;
        BuildUpstreamClients("Upstream DNS refreshed after network change");
    }

    private static double ElapsedMs(long startTimestamp)
        => (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

    private async Task OnQueryReceivedAsync(object sender, QueryReceivedEventArgs args)
    {
        if (args.Query is not DnsMessage query)
            return;

        var response = query.CreateResponseInstance();

        // High-resolution entry stamp — used to record the block-decision latency
        // (finding M1). Only the locally-answered blocked path records; upstream
        // forwarding latency is network-bound, not our SLA.
        var startTs = Stopwatch.GetTimestamp();

        try
        {
            var question = query.Questions.FirstOrDefault();

            if (question == null)
            {
                response.ReturnCode = ReturnCode.ServerFailure;
                args.Response = response;
                return;
            }

            var domain = question.Name.ToString().TrimEnd('.').ToLowerInvariant();

            // ── Health probe short-circuit ────────────────────────────────────
            // Answered locally so the HealthMonitor probe never depends on
            // upstream reachability. NXDOMAIN is a real answer — it proves the
            // proxy is processing queries.
            if (domain == HealthProbeDomain)
            {
                response.ReturnCode = ReturnCode.NxDomain;
                args.Response = response;
                return;
            }

            // ── SafeSearch enforcement ────────────────────────────────────────
            // Rewrite search-engine hostnames to their vendor "force SafeSearch"
            // host via a CNAME (+ the target's resolved addresses). Only A/AAAA
            // queries are rewritten — that is what browsers connect on. Skipped
            // during an emergency pause, like every other control.
            if (!_ipcServer.IsProtectionPaused
                && (question.RecordType == RecordType.A || question.RecordType == RecordType.Aaaa))
            {
                var ssTarget = _safeSearch.TryGetTarget(domain);
                if (ssTarget is not null)
                {
                    await AnswerSafeSearchAsync(question, response, ssTarget);
                    args.Response = response;
                    _logger.LogInformation("[SAFESEARCH] {Domain} → {Target}", domain, ssTarget);
                    return;
                }
            }

            // ── Blocklist check ───────────────────────────────────────────────
            // Skipped entirely during an emergency pause — queries forward
            // upstream unfiltered until the pause window expires.
            var blockResult = _blocklist.IsBlocked(domain);
            if (blockResult.IsBlocked && !_ipcServer.IsProtectionPaused)
            {
                response.ReturnCode = ReturnCode.NxDomain;
                args.Response = response;

                var timestamp = DateTime.UtcNow;
                var category = MapCategory(blockResult.CategoryName);
                var severity = MapSeverity(blockResult.Severity);
                var mitre = MapMitre(category);
                var deviceName = ResolveDeviceName(args.RemoteEndpoint);

                // Push to IPC — non-blocking, safe on DNS thread
                _ipcServer.BroadcastLogEvent(new LogEventMessage
                {
                    Timestamp = timestamp.ToString("O"),
                    Domain = domain,
                    Category = category,
                    Severity = severity,
                    DeviceName = deviceName,
                    Mitre = mitre
                });

                // Enqueue to SQLite log writer — non-blocking, safe on DNS thread
                _logWriter.Enqueue(new BlockedEventRecord(
                    Timestamp: timestamp,
                    Domain: domain,
                    CategoryId: blockResult.CategoryId,
                    Severity: blockResult.Severity,
                    DeviceName: deviceName,
                    Mitre: mitre,
                    // Bypass-category blocks are evasion attempts — open an incident.
                    CreatesIncident: category == BlockCategory.Bypass
                ));

                _logger.LogInformation(
                    "[BLOCKED] {Domain} | cat={Category} sev={Severity}",
                    domain, blockResult.CategoryName, blockResult.Severity);

                _latency.Record(ElapsedMs(startTs));
                return;
            }

            // ── Forward to upstream ───────────────────────────────────────────
            // Start from the last upstream that answered. On a network change the
            // original (now unreachable) resolvers time out once; the working
            // fallback then becomes preferred, so subsequent queries stay fast
            // instead of paying the full timeout chain on every lookup.
            var clients = _upstreamClients;
            var count = clients.Count;
            var start = count > 0 ? _preferredUpstream % count : 0;

            for (int k = 0; k < count; k++)
            {
                var idx = (start + k) % count;
                var client = clients[idx];
                try
                {
                    var upstreamResponse = await client.SendMessageAsync(query);
                    if (upstreamResponse != null)
                    {
                        if (idx != _preferredUpstream)
                            _preferredUpstream = idx;
                        if (Interlocked.Exchange(ref _consecutiveUpstreamOutages, 0) >= OutageAlertThreshold)
                        {
                            _logger.LogInformation("Upstream DNS recovered — resolution restored");
                            _ipcServer.SetUpstreamHealthy(true);
                        }
                        args.Response = upstreamResponse;
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Upstream DNS client failed for {Domain} — trying next", domain);
                }
            }

            // All upstreams failed — fail-closed
            _logger.LogWarning(
                "All upstream DNS servers failed for {Domain} — failing closed", domain);
            RecordUpstreamOutage();
            response.ReturnCode = ReturnCode.ServerFailure;
            args.Response = response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Query handler exception — failing closed");
            response.ReturnCode = ReturnCode.ServerFailure;
            args.Response = response;
        }
    }

    // ── Upstream outage detection ─────────────────────────────────────────────
    // Distinguishes "everything is fine" from "the proxy is up but no upstream
    // answers" — the state a user experiences as Wi-Fi connected, internet dead,
    // with no explanation. After N consecutive all-upstream failures, raise one
    // ServiceError alert so the dashboard says why; reset (and re-arm) on the
    // next successful forward.

    private const int OutageAlertThreshold = 5;
    private int _consecutiveUpstreamOutages;

    private void RecordUpstreamOutage()
    {
        if (Interlocked.Increment(ref _consecutiveUpstreamOutages) != OutageAlertThreshold)
            return;   // alert exactly once per outage

        _logger.LogCritical(
            "Upstream DNS unreachable for {Count} consecutive queries — the proxy is " +
            "healthy but cannot forward. Filtering still fails closed.", OutageAlertThreshold);

        _ipcServer.SetUpstreamHealthy(false);
        _ipcServer.BroadcastAlert(new AlertMessage
        {
            Timestamp = DateTime.UtcNow.ToString("O"),
            AlertType = AlertType.ServiceError,
            Severity = Severity.High,
            Message = "Obstruo is running, but its upstream DNS servers are not responding — " +
                      "this usually means the network changed or is offline. Internet name " +
                      "lookups will fail until an upstream is reachable again."
        });
    }

    // ── SafeSearch ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a SafeSearch response: a CNAME from the queried name to the vendor
    /// force-safe host, plus that host's addresses resolved upstream so the client
    /// needs no extra round-trip. If the upstream lookup fails, the CNAME alone is
    /// returned — a conformant resolver re-queries the target through this proxy.
    /// </summary>
    private async Task AnswerSafeSearchAsync(DnsQuestion question, DnsMessage response, string target)
    {
        var targetName = DomainName.Parse(target);
        response.ReturnCode = ReturnCode.NoError;
        response.AnswerRecords.Add(new CNameRecord(question.Name, 300, targetName));

        var upstreamQuery = new DnsMessage { IsRecursionDesired = true };
        upstreamQuery.Questions.Add(new DnsQuestion(targetName, question.RecordType, RecordClass.INet));

        var clients = _upstreamClients;
        var count = clients.Count;
        var start = count > 0 ? _preferredUpstream % count : 0;
        for (int k = 0; k < count; k++)
        {
            try
            {
                var upstream = await clients[(start + k) % count].SendMessageAsync(upstreamQuery);
                if (upstream is not null && upstream.AnswerRecords.Count > 0)
                {
                    foreach (var rec in upstream.AnswerRecords)
                        response.AnswerRecords.Add(rec);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "SafeSearch: upstream resolve of {Target} failed — trying next", target);
            }
        }
        // Fell through — CNAME-only response; the client re-resolves via this proxy.
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _server?.Stop();
        _server = null;
        _logger.LogInformation("DNS proxy stopped");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    // ── Device attribution ────────────────────────────────────────────────────

    /// <summary>
    /// Attributes a query to a device. Loopback queries come from this machine
    /// (its name); anything else is a LAN client using Obstruo as its router
    /// DNS — attribute it to the client's IP so the per-device story in the
    /// dashboard is real instead of everything showing the Obstruo host.
    /// </summary>
    private static string ResolveDeviceName(System.Net.IPEndPoint? remote)
    {
        var address = remote?.Address;
        if (address is null || IPAddress.IsLoopback(address))
            return _deviceName;

        // Normalize IPv4-mapped IPv6 (::ffff:192.168.1.20 → 192.168.1.20)
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        return address.ToString();
    }

    // ── IPv6 probe ────────────────────────────────────────────────────────────

    /// <summary>
    /// Tests whether IPv6 is usable on this machine by briefly binding ::1 on
    /// an ephemeral port (0). Port 0 is OS-assigned and never conflicts.
    /// Does not open port 53 — just checks if IPv6 sockets work at all.
    /// </summary>
    private bool ProbeIPv6Available()
    {
        try
        {
            using var probe = new Socket(
                AddressFamily.InterNetworkV6,
                SocketType.Dgram,
                ProtocolType.Udp);

            probe.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("IPv6 probe failed: {Message}", ex.Message);
            return false;
        }
    }

    // ── Enum mapping ──────────────────────────────────────────────────────────
    // DB category names → BlockCategory enum.
    // "Paid" and "SexChat" have no direct enum value — mapped to closest equivalent.

    private static BlockCategory MapCategory(string categoryName) =>
        categoryName.ToLowerInvariant() switch
        {
            "adult" => BlockCategory.Adult,
            "paid" => BlockCategory.Adult,   // paid adult content → Adult
            "chat" => BlockCategory.Chat,
            "aiadult" => BlockCategory.AIAdult,
            "sexchat" => BlockCategory.Chat,    // sex chat → Chat
            "games" => BlockCategory.Games,
            "malware" => BlockCategory.Malware,
            "bypass" => BlockCategory.Bypass,
            "custom" => BlockCategory.Custom,
            _ => BlockCategory.Custom    // unknown → Custom (don't mislabel as Adult)
        };

    private static Severity MapSeverity(string severity) =>
        severity.ToLowerInvariant() switch
        {
            "low" => Severity.Low,
            "med" => Severity.Med,
            "high" => Severity.High,
            "critical" => Severity.Critical,
            _ => Severity.High         // unknown → High (safe default)
        };

    // ── MITRE ATT&CK mapping ──────────────────────────────────────────────────
    // Only categories with a legitimate technique get a tag. Everything else
    // is null — no tag is better than a fake tag.
    //
    //   Bypass  → T1090     (Proxy — VPN/proxy filter evasion)
    //   Malware → T1071.004 (Application Layer Protocol: DNS — C2 over DNS)

    private static string? MapMitre(BlockCategory category) =>
        category switch
        {
            BlockCategory.Bypass => "T1090",
            BlockCategory.Malware => "T1071.004",
            _ => null
        };
}