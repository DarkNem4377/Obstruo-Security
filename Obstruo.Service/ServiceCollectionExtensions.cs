using Microsoft.Extensions.DependencyInjection;
using Obstruo.Service.Data;
using Obstruo.Service.Dns;

namespace Obstruo.Service;

/// <summary>
/// Central registration of every Obstruo service so the exact object graph the
/// Windows host builds can also be built (and validated) from tests. Keeping this
/// in one place is what lets <c>DiGraphTests</c> catch constructor-injection
/// cycles before they ship as a service that silently fails to start.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddObstruoServices(this IServiceCollection services)
    {
        // ── Database ─────────────────────────────────────────────────────────
        services.AddSingleton<ObstruoDatabase>();

        // ── Data layer ───────────────────────────────────────────────────────
        services.AddSingleton<BlocklistRepository>();
        services.AddSingleton<IncidentRepository>();
        services.AddSingleton<LogExporter>();
        services.AddSingleton<LogEventWriter>();
        services.AddSingleton<LogRetentionService>();
        services.AddSingleton<MetricsRepository>();

        // ── DNS layer ────────────────────────────────────────────────────────
        services.AddSingleton<DnsSettingsManager>();
        services.AddSingleton<Port53Checker>();
        services.AddSingleton<TamperDetector>();
        // Lazy resolver breaks the DI cycle TamperDetector -> IpcServer ->
        // UninstallService -> TamperDetector. UninstallService takes
        // Lazy<TamperDetector>; MS DI does not synthesize Lazy<T>, so register it
        // explicitly (resolves the same singleton on first access).
        services.AddSingleton(sp => new Lazy<TamperDetector>(sp.GetRequiredService<TamperDetector>));
        services.AddSingleton<DoHBlocker>();
        services.AddSingleton<Dns53Firewall>();
        services.AddSingleton<NetworkChangeWatcher>();
        services.AddSingleton<QueryLatencyTracker>();
        services.AddSingleton<DnsBlocklistStore>();
        services.AddSingleton<SafeSearchRewriter>();
        services.AddSingleton<DnsProxyService>();
        services.AddSingleton<LanModeService>();

        // ── Uninstall ────────────────────────────────────────────────────────
        services.AddSingleton<UninstallService>();

        // ── IPC layer ────────────────────────────────────────────────────────
        services.AddSingleton<IpcServer>();

        // ── Runtime health ───────────────────────────────────────────────────
        services.AddSingleton<HealthMonitor>();

        // ── Worker (BackgroundService) ───────────────────────────────────────
        services.AddHostedService<Worker>();

        return services;
    }
}
