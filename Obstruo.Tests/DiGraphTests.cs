using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Obstruo.Service;
using Obstruo.Service.Dns;

namespace Obstruo.Tests;

/// <summary>
/// Regression guard for the v1.0.1 round-2 finding: the service failed to start
/// because of a constructor-injection cycle
/// (TamperDetector -> IpcServer -> UninstallService -> TamperDetector), so the
/// DNS proxy never bound :53 and no filtering happened. The bug was invisible to
/// the compiler and only surfaced at runtime when the host resolved the Worker.
///
/// These tests build the REAL production object graph (via AddObstruoServices)
/// and force full resolution, so any future cycle fails the build instead of a
/// shipped release.
/// </summary>
public class DiGraphTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddObstruoServices();
        // ValidateOnBuild eagerly checks that every registration can be
        // constructed; a DI cycle throws here (InvalidOperationException:
        // "A circular dependency was detected...").
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    [Fact]
    public void ProductionGraph_BuildsWithoutCircularDependency()
    {
        using var provider = BuildProvider();

        // Resolving the hosted service is what the host does at StartAsync — the
        // exact path that threw for v1.0.1. It must now succeed.
        var hosted = provider.GetServices<IHostedService>().ToList();

        Assert.NotEmpty(hosted);
    }

    [Fact]
    public void CycleParticipants_AllResolveToSingletons()
    {
        using var provider = BuildProvider();

        var tamper = provider.GetRequiredService<TamperDetector>();
        var ipc = provider.GetRequiredService<IpcServer>();
        var uninstall = provider.GetRequiredService<UninstallService>();
        var lazyTamper = provider.GetRequiredService<Lazy<TamperDetector>>();

        Assert.NotNull(ipc);
        Assert.NotNull(uninstall);
        // The Lazy indirection must hand back the very same singleton, otherwise
        // uninstall would Stop() a different TamperDetector than the one running.
        Assert.Same(tamper, lazyTamper.Value);
    }
}
