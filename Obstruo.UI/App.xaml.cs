using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Obstruo.UI.Auth;
using Obstruo.UI.Ipc;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace Obstruo.UI;

public partial class App : Application
{
    private ServiceProvider? _services;

    // How long the startup flow waits for the pipe connection before declaring
    // the service unreachable. The IpcClient's own connect timeout is 2s with
    // 3s retry gaps, so 8s allows one full retry cycle.
    private const int StartupConnectTimeoutMs = 8_000;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The startup flow has async gaps where no window exists (after the
        // splash closes, before the wizard/auth/dashboard opens). The default
        // OnLastWindowClose would kill the app inside those gaps. Explicit
        // shutdown until the dashboard is up.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var services = new ServiceCollection();
        ConfigureServices(services);
        _services = services.BuildServiceProvider();

        // Windows Settings → Apps → Uninstall routes here (ARP UninstallString).
        // Skips the splash/auth flow and opens the PIN-gated uninstall dialog
        // directly — the credential check still happens in the service.
        if (e.Args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            _ = RunUninstallFlowAsync();
            return;
        }

        RunStartupFlow();
    }

    private async Task RunUninstallFlowAsync()
    {
        try
        {
            var ipc = _services!.GetRequiredService<IpcClient>();
            ipc.Start();

            if (!await WaitForConnectionAsync(ipc, StartupConnectTimeoutMs))
            {
                MessageBox.Show(
                    "Cannot reach the Obstruo service, so uninstall is unavailable.\n\n" +
                    "Check that 'Obstruo Security Service' is running and try again.",
                    "Obstruo Security — Uninstall",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown();
                return;
            }

            var dialog = new UninstallWindow(ipc);
            dialog.ShowDialog();

            // UninstallWindow calls Shutdown() itself on success; reaching here
            // means the user cancelled or the uninstall was rejected.
            Shutdown();
        }
        catch (Exception ex)
        {
            _services?.GetService<ILogger<App>>()?.LogError(ex, "Uninstall flow failed");
            Shutdown();
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(builder =>
        {
            builder.AddDebug();
            // Rolling file log under %LocalAppData%\Obstruo\logs — the UI runs
            // non-elevated, so it cannot write to the hardened ProgramData dir.
            builder.AddProvider(new Obstruo.Shared.Logging.FileLoggerProvider(
                System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Obstruo", "logs"),
                prefix: "ui"));
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        services.AddSingleton<AuthService>();
        services.AddSingleton<RecoveryService>();
        services.AddSingleton<IpcClient>();

        services.AddTransient<AuthWindow>();
        services.AddTransient<SetupWizard>();
        services.AddTransient<MainWindow>();
    }

    // Shows splash first. SplashCompleted fires after the fade animation on the
    // UI thread, so ContinueAfterSplash runs on the dispatcher.
    private void RunStartupFlow()
    {
        var splash = new SplashWindow();
        splash.SplashCompleted += async (_, _) => await ContinueAfterSplashAsync();
        splash.Show();
    }

    /// <summary>
    /// Startup routing (auth refactor B — all auth state lives in the service):
    ///   1. Start the IPC client and wait for the pipe connection.
    ///   2. Ask the service for setup state.
    ///   3. Route: unreachable/unknown → error + exit (fail-closed, never guess);
    ///      not configured → SetupWizard; configured → AuthWindow.
    ///   4. Dashboard.
    /// NOTE: IpcClient.Start() connects a named pipe. It does NOT touch DNS —
    /// the DNS risk is the SERVICE, not this client.
    /// </summary>
    private async Task ContinueAfterSplashAsync()
    {
        try
        {
            var ipc = _services!.GetRequiredService<IpcClient>();
            var auth = _services!.GetRequiredService<AuthService>();

            ipc.Start();

            var connected = await WaitForConnectionAsync(ipc, StartupConnectTimeoutMs);

            SetupState? state = null;
            if (connected)
                state = await auth.GetSetupStateAsync();

            if (state is null)
            {
                // Service unreachable or setup state unknown. Fail closed:
                // never assume "not configured" — a dead service on a fully
                // configured machine would re-trigger the wizard and let
                // anyone overwrite credentials via the bootstrap rule.
                MessageBox.Show(
                    "Cannot reach the Obstruo service.\n\n" +
                    "The service may be stopped or still starting. " +
                    "Check that 'Obstruo Security Service' is running, then launch the dashboard again.",
                    "Obstruo Security — Service Unavailable",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown();
                return;
            }

            if (!state.IsConfigured)
            {
                // Bootstrap credential-setting is gated on an elevated caller in the
                // service (so a standard user can't seize the PIN before setup
                // completes). Relaunch elevated if we aren't already.
                if (!EnsureElevatedForSetup()) { Shutdown(); return; }
                if (!RunSetupWizard())
                {
                    Shutdown();
                    return;
                }
            }
            else
            {
                var authWindow = _services!.GetRequiredService<AuthWindow>();
                authWindow.ShowDialog();

                if (!authWindow.Authenticated)
                {
                    Shutdown();
                    return;
                }

                if (authWindow.RequiresSetup)
                {
                    // Recovery cleared all credentials — setup runs again and, like
                    // first-run, needs elevation to re-establish them.
                    if (!EnsureElevatedForSetup()) { Shutdown(); return; }
                    if (!RunSetupWizard())
                    {
                        Shutdown();
                        return;
                    }
                }
            }

            var dashboard = _services!.GetRequiredService<MainWindow>();
            MainWindow = dashboard;
            dashboard.Show();

            // Dashboard is up — from here on, closing it exits the app.
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
        catch (Exception ex)
        {
            // async void event handler chain — an unhandled exception here
            // would crash the process with no diagnostics. Fail loudly instead.
            _services?.GetService<ILogger<App>>()?.LogError(ex, "Startup flow failed");
            MessageBox.Show(
                $"Obstruo failed to start:\n\n{ex.Message}",
                "Obstruo Security — Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>
    /// Waits for the IpcClient to report a connection, up to timeoutMs.
    /// Returns immediately if already connected. Never throws.
    /// </summary>
    private static async Task<bool> WaitForConnectionAsync(IpcClient ipc, int timeoutMs)
    {
        if (ipc.IsConnected) return true;

        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnChanged(object? _, bool connected)
        {
            if (connected) tcs.TrySetResult(true);
        }

        ipc.ConnectionChanged += OnChanged;
        try
        {
            // Re-check after subscribing — the connection may have completed
            // between the first check and the subscription.
            if (ipc.IsConnected) return true;

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            return completed == tcs.Task;
        }
        finally
        {
            ipc.ConnectionChanged -= OnChanged;
        }
    }

    private bool RunSetupWizard()
    {
        var wizard = _services!.GetRequiredService<SetupWizard>();
        wizard.ShowDialog();
        return wizard.SetupComplete;
    }

    /// <summary>
    /// Ensures the setup wizard runs elevated. If already elevated, returns true.
    /// Otherwise relaunches this app elevated (UAC) and returns false so the caller
    /// shuts down the current non-elevated instance — the elevated instance then
    /// re-runs the flow and, seeing setup incomplete, runs the wizard.
    /// </summary>
    private bool EnsureElevatedForSetup()
    {
        if (IsProcessElevated()) return true;

        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
                throw new InvalidOperationException("Could not resolve the application path.");

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--first-run",
                UseShellExecute = true,
                Verb = "runas"   // triggers the UAC elevation prompt
            });
        }
        catch (Exception ex)
        {
            // Win32Exception 1223 = user declined UAC; anything else = launch failure.
            _services?.GetService<ILogger<App>>()?.LogWarning(ex, "Elevation for setup declined or failed");
            MessageBox.Show(
                "Setting up Obstruo needs administrator approval — this stops a standard " +
                "user from changing your PIN and password.\n\n" +
                "Obstruo will now close. Relaunch it and approve the prompt to finish setup.",
                "Obstruo Security — Setup Requires Administrator",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        return false;
    }

    private static bool IsProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.GetService<IpcClient>()?.Stop();
        _services?.Dispose();
        base.OnExit(e);
    }
}