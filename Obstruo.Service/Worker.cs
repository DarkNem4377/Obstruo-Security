using Obstruo.Service.Data;
using Obstruo.Service.Dns;
using Obstruo.Shared.Enums;
using Obstruo.Shared.Messages;

namespace Obstruo.Service;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly ObstruoDatabase _db;
    private readonly BlocklistRepository _blocklist;
    private readonly LogEventWriter _logWriter;
    private readonly LogRetentionService _logRetention;
    private readonly DnsSettingsManager _dnsSettings;
    private readonly Port53Checker _port53;
    private readonly TamperDetector _tamperDetector;
    private readonly DoHBlocker _dohBlocker;
    private readonly Dns53Firewall _dns53;
    private readonly NetworkChangeWatcher _networkWatcher;
    private readonly LanModeService _lanMode;
    private readonly DnsProxyService _dnsProxy;
    private readonly IpcServer _ipcServer;
    private readonly HealthMonitor _healthMonitor;

    public Worker(
        ILogger<Worker> logger,
        ObstruoDatabase db,
        BlocklistRepository blocklist,
        LogEventWriter logWriter,
        LogRetentionService logRetention,
        DnsSettingsManager dnsSettings,
        Port53Checker port53,
        TamperDetector tamperDetector,
        DoHBlocker dohBlocker,
        Dns53Firewall dns53,
        NetworkChangeWatcher networkWatcher,
        LanModeService lanMode,
        DnsProxyService dnsProxy,
        IpcServer ipcServer,
        HealthMonitor healthMonitor)
    {
        _logger = logger;
        _db = db;
        _blocklist = blocklist;
        _logWriter = logWriter;
        _logRetention = logRetention;
        _dnsSettings = dnsSettings;
        _port53 = port53;
        _tamperDetector = tamperDetector;
        _dohBlocker = dohBlocker;
        _dns53 = dns53;
        _networkWatcher = networkWatcher;
        _lanMode = lanMode;
        _dnsProxy = dnsProxy;
        _ipcServer = ipcServer;
        _healthMonitor = healthMonitor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await StartupAndRunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown — the host cancelled the token.
        }
        catch (Exception ex)
        {
            // Round-2 lesson: an unhandled startup exception previously killed the
            // whole host (default BackgroundServiceExceptionBehavior.StopHost) with
            // the only evidence in a log file — while DNS may already be pinned to
            // 127.0.0.1 with nothing listening. Mirror the port-53-conflict posture
            // instead: log loudly, tell the UI, and hold alive fail-closed so SCM
            // doesn't restart-loop and the dashboard can show WHY internet is down.
            _logger.LogCritical(ex,
                "Obstruo Service startup failed — holding fail-closed. " +
                "Internet stays blocked until the cause is fixed and the service restarts.");

            _ipcServer.SetProtectionState(ProtectionState.Error);
            _ipcServer.BroadcastAlert(new AlertMessage
            {
                Timestamp = DateTime.UtcNow.ToString("O"),
                AlertType = AlertType.ServiceError,
                Severity = Severity.Critical,
                Message = "Obstruo failed to start its protection stack and is holding " +
                          "fail-closed. See the service log under ProgramData\\Obstruo\\logs. " +
                          $"Error: {ex.Message}"
            });

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
    }

    private async Task StartupAndRunAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Obstruo Service starting");

        // ── 1. Initialize database (WAL, schema, seed categories/config) ──────
        _db.Initialize();

        // ── 2. Backup original DNS and set system DNS to 127.0.0.1 ────────────
        //       Must happen before DNS proxy starts.
        //       Skips backup if dns_backup.json already exists.
        _dnsSettings.BackupAndSetDns();

        // ── 3. Apply DoH blocking ──────────────────────────────────────────────
        //       Firewall rules + browser registry policies.
        //       Idempotent — safe to apply on every startup.
        _dohBlocker.Apply();

        // Assert the DoT/DoQ port-853 rules actually landed (finding H3). The
        // v1.0.0 audit found zero 853 rules on a build that claimed them; a
        // missing rule set is re-applied inside VerifyDoTRulesPresent().
        if (!_dohBlocker.VerifyDoTRulesPresent())
        {
            _ipcServer.BroadcastAlert(new AlertMessage
            {
                Timestamp = DateTime.UtcNow.ToString("O"),
                AlertType = AlertType.BypassAttempt,
                Severity = Severity.Med,
                Message = "Encrypted-DNS (DoT/DoQ) firewall rules were missing and have been " +
                          "re-applied. If this recurs, another tool may be removing them."
            });
        }

        // ── 3b. Close the classic-DNS :53 bypass (finding H1) ──────────────────
        //        Block outbound UDP+TCP 53 to every remote address except the
        //        service's own upstream. Runs after BackupAndSetDns so the
        //        upstream set is known. Re-applied on each network change by the
        //        watcher below (the upstream can change with the network).
        _dns53.Apply();

        // ── 4. Seed domains if empty, load all into in-memory store ───────────
        _blocklist.InitializeAndLoad();

        // ── 5. Start log event writer (background queue → SQLite) ─────────────
        _logWriter.Start();

        // ── 6. Start log retention service ────────────────────────────────────
        //       Waits until configured cleanup_time, then runs daily.
        //       Lowest thread priority — never competes with DNS.
        _logRetention.Start();

        // ── 7. Start IPC server ────────────────────────────────────────────────
        //       Started before the port-53 check so that if the check fails the
        //       UI can still connect and receive the Port53Conflict alert below.
        //       IPC does not depend on DNS being up. LAN-state broadcasts still
        //       happen later, once LanModeService has run.
        _ipcServer.Start();

        // ── 8. Check port 53 is available before binding ──────────────────────
        //       Stops Windows DNS Client automatically if it is the conflict.
        //       Unknown process → logs error, stays fail-closed, does not start proxy.
        if (!_port53.EnsurePortAvailable())
        {
            _logger.LogCritical(
                "Port 53 conflict could not be resolved. " +
                "DNS proxy will NOT start. " +
                "Internet is blocked (fail-closed) until the conflict is resolved and the service is restarted.");

            // Reflect the real state — the proxy never started, so protection is
            // NOT active. Without this the dashboard would keep showing "Active"
            // while the internet is actually blocked.
            _ipcServer.SetProtectionState(ProtectionState.Error);

            // Tell the UI why the internet is down — the alert banner is the only
            // channel the user has to learn this without reading service logs.
            _ipcServer.BroadcastAlert(new AlertMessage
            {
                Timestamp = DateTime.UtcNow.ToString("O"),
                AlertType = AlertType.Port53Conflict,
                Severity = Severity.Critical,
                Message = "Port 53 is occupied by another process. Obstruo's DNS filter " +
                          "could not start. Internet stays blocked until the conflict is " +
                          "resolved and the service is restarted."
            });

            // Hold here — service stays alive so SCM doesn't restart-loop.
            // DNS is already pointing to 127.0.0.1 with nothing listening = fail-closed.
            await Task.Delay(Timeout.Infinite, stoppingToken);
            return;
        }

        // ── 9. Detect LAN IP, apply firewall rules ─────────────────────────────
        //       Must run before DNS proxy so DnsProxyService.Start() can read
        //       LanModeService.CurrentLanIp and bind on it immediately.
        //       Sets IsFirstRun / HasIpChanged flags for IPC broadcast below.
        _lanMode.Start();

        // ── 10. Start DNS proxy ────────────────────────────────────────────────
        //       Binds 127.0.0.1:53 + [::1]:53 (if IPv6 available)
        //       + LAN IP:53 (if LanModeService detected one).
        _dnsProxy.Start();

        // ── 11. Broadcast LAN mode notifications ───────────────────────────────
        //        Now that IpcServer is up, flush any LAN state changes the UI
        //        needs to act on. UI shows the LAN IP so the user can configure
        //        their router to point DNS at Obstruo.
        //
        //        IsFirstRun  → first time a LAN IP was detected on this machine.
        //                       UI should show the one-time setup notification.
        //        HasIpChanged → LAN IP changed since last startup.
        //                       UI should prompt user to update their router.
        if (_lanMode.CurrentLanIp is not null)
        {
            if (_lanMode.IsFirstRun)
            {
                _logger.LogInformation(
                    "First LAN IP — broadcasting setup notification. IP={Ip}",
                    _lanMode.CurrentLanIp);

                _ipcServer.BroadcastAlert(new AlertMessage
                {
                    AlertType = AlertType.LanIpChanged,
                    Severity = Severity.Low,
                    Message = $"FIRST_RUN:{_lanMode.CurrentLanIp}",
                    Timestamp = DateTime.UtcNow.ToString("O")
                });
            }
            else if (_lanMode.HasIpChanged)
            {
                _logger.LogWarning(
                    "LAN IP changed — broadcasting update notification. Old={Old} New={New}",
                    _lanMode.PreviousLanIp, _lanMode.CurrentLanIp);

                _ipcServer.BroadcastAlert(new AlertMessage
                {
                    AlertType = AlertType.LanIpChanged,
                    Severity = Severity.Low,
                    Message = $"IP_CHANGED:{_lanMode.PreviousLanIp}:{_lanMode.CurrentLanIp}",
                    Timestamp = DateTime.UtcNow.ToString("O")
                });
            }
        }

        // ── 12. Start tamper detector ──────────────────────────────────────────
        //        Watches registry every 3s — reverts any external DNS changes.
        //        Started last so IPC is already up when it fires its first alert.
        _tamperDetector.Start();

        // ── 12b. Start network-change watcher ──────────────────────────────────
        //         Immediately re-pins DNS + re-applies the :53 block when a NIC
        //         comes online, closing the race before the next tamper poll.
        _networkWatcher.Start();

        // ── 13. Start daily blocklist auto-sync ────────────────────────────────
        //        Must come after the DNS proxy is up — the feed host resolves
        //        through it. No-op while blocklist_url is unconfigured.
        _blocklist.StartAutoSync();

        // ── 14. Start runtime health monitor ───────────────────────────────────
        //        Probes the DNS proxy over loopback; a dead proxy triggers a
        //        service restart via SCM recovery. Started last — only makes
        //        sense once the proxy is supposed to be answering.
        _healthMonitor.Start();

        _logger.LogInformation(
            "Obstruo Service running — DNS proxy active, IPC server active, " +
            "tamper detection active, DoH blocked, log retention scheduled, " +
            "LAN mode active (IP={LanIp})",
            _lanMode.CurrentLanIp ?? "none");

        // Hold until host requests stop
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Obstruo Service stopping");

        // Stop the health monitor FIRST — a clean shutdown must not be
        // mistaken for a dead proxy and trigger a restart-exit.
        _healthMonitor.Stop();

        // Stop the network-change watcher before the tamper detector so a
        // late-firing re-pin can't run against a half-torn-down service.
        _networkWatcher.Stop();

        // Stop tamper detector — no more registry polling or alerts
        _tamperDetector.Stop();

        // Stop blocklist auto-sync — no more feed fetches
        _blocklist.StopAutoSync();

        // Stop IPC server — no more UI communication after this
        await _ipcServer.StopAsync();

        // Stop DNS proxy — no new queries processed after this
        _dnsProxy.Stop();

        // Stop LAN mode — logs stop, firewall rules intentionally retained
        _lanMode.Stop();

        // Stop log retention — cancel any pending wait
        _logRetention.Stop();

        // Flush remaining queued log events before exit
        await _logWriter.StopAsync();

        // NOTE: DNS is NOT restored here.
        // System DNS stays on 127.0.0.1 — fail-closed by design.
        // DNS is only restored on clean PIN-confirmed uninstall.

        // NOTE: DoH blocking is NOT removed here.
        // Firewall rules and browser policies stay active.
        // Only removed on clean PIN-confirmed uninstall.

        await base.StopAsync(cancellationToken);
    }
}