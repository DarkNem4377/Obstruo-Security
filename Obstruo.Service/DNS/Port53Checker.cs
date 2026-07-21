using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Obstruo.Service.Dns;

public class Port53Checker
{
    private readonly ILogger<Port53Checker> _logger;

    // Windows DNS Client service name in SCM
    private const string WindowsDnsClientService = "Dnscache";

    public Port53Checker(ILogger<Port53Checker> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Checks port 53 and resolves any conflict before the DNS proxy binds.
    /// Returns true if port is clear and safe to bind.
    /// Returns false if conflict could not be resolved — caller must not start DNS proxy.
    /// </summary>
    public bool EnsurePortAvailable()
    {
        if (!IsPort53Occupied())
        {
            _logger.LogInformation("Port 53 is free — safe to bind");
            return true;
        }

        _logger.LogWarning("Port 53 is occupied — identifying process");

        var occupyingProcess = GetProcessOccupyingPort53();

        if (occupyingProcess == null)
        {
            // Port is occupied but we can't identify who — try to bind anyway
            // Some cases (e.g. IPv6 only listener) show as occupied but UDP 127.0.0.1 is still free
            _logger.LogWarning("Could not identify process on port 53 — attempting bind anyway");
            return true;
        }

        _logger.LogWarning("Port 53 occupied by: {Name} (PID {Pid})",
            occupyingProcess.ProcessName, occupyingProcess.Id);

        // ── Known safe case: Windows DNS Client ───────────────────────────
        if (IsWindowsDnsClient(occupyingProcess))
        {
            _logger.LogInformation("Windows DNS Client (Dnscache) detected — disabling");
            return DisableWindowsDnsClient();
        }

        // ── Unknown process ───────────────────────────────────────────────
        _logger.LogError(
            "Port 53 is occupied by unknown process: {Name} (PID {Pid}). " +
            "Obstruo cannot start until this process releases port 53. " +
            "Please stop '{Name}' manually and restart Obstruo.",
            occupyingProcess.ProcessName,
            occupyingProcess.Id,
            occupyingProcess.ProcessName);

        return false;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static bool IsPort53Occupied()
    {
        try
        {
            // Try binding UDP 127.0.0.1:53 — the exact endpoint we need
            using var udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
            udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, 53));
            return false; // Bind succeeded — port is free
        }
        catch (SocketException)
        {
            return true; // Bind failed — port is occupied
        }
    }

    private static Process? GetProcessOccupyingPort53()
    {
        try
        {
            // Use netstat to find PID owning port 53
            var output = Obstruo.Shared.ProcessRunner.Run("netstat", "-ano -p UDP").StdOut;

            foreach (var line in output.Split('\n'))
            {
                // Match lines containing :53 as local address
                if (!line.Contains(":53 ") && !line.Contains(":53\t"))
                    continue;

                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                    continue;

                // PID is last column in netstat -ano output
                if (!int.TryParse(parts[^1], out var pid))
                    continue;

                if (pid <= 0)
                    continue;

                return Process.GetProcessById(pid);
            }
        }
        catch (Exception)
        {
            // netstat failed or process already exited — not fatal
        }

        return null;
    }

    private static bool IsWindowsDnsClient(Process process)
    {
        // PID 0 = System Idle, PID 4 = System — not DNS client
        if (process.Id <= 4)
            return false;

        // Only svchost.exe hosts Dnscache. Anything else on port 53 is a
        // third-party resolver (Acrylic, pihole-on-Windows, dnscrypt, …) that
        // we must never stop on our own.
        if (!process.ProcessName.Equals("svchost", StringComparison.OrdinalIgnoreCase))
            return false;

        // Confirm THIS pid is the svchost instance hosting Dnscache — not some
        // other svchost (e.g. ICS/SharedAccess, which also binds UDP 53 for the
        // mobile hotspot). Stopping the wrong service leaves port 53 occupied and
        // the machine fail-closed. sc queryex reports the hosting process PID.
        var dnscachePid = GetServicePid(WindowsDnsClientService);
        return dnscachePid is not null && dnscachePid == process.Id;
    }

    /// <summary>
    /// Returns the PID hosting the given service, or null if it can't be
    /// determined (service stopped, not present, or query failed).
    /// </summary>
    private static int? GetServicePid(string serviceName)
    {
        try
        {
            var output = Obstruo.Shared.ProcessRunner.Run("sc", $"queryex {serviceName}", timeoutMs: 3_000).StdOut;

            // Only trust the PID when the service is actually RUNNING — a stopped
            // service reports PID 0.
            if (!output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
                return null;

            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("PID", StringComparison.OrdinalIgnoreCase))
                    continue;

                var colon = trimmed.IndexOf(':');
                if (colon < 0) continue;

                if (int.TryParse(trimmed[(colon + 1)..].Trim(), out var pid) && pid > 0)
                    return pid;
            }
        }
        catch
        {
            // fall through
        }

        return null;
    }

    private bool DisableWindowsDnsClient()
    {
        try
        {
            // Stop the service
            RunSc("stop Dnscache");

            // Set to Manual so it doesn't restart on reboot
            // We do NOT set Disabled — that breaks some Windows features
            RunSc("config Dnscache start= demand");

            // Record that WE changed Dnscache so the uninstall cleanup knows to
            // restore it to auto-start. Without this marker a clean uninstall
            // would leave the machine permanently without the Windows DNS Client.
            MarkDnscacheDisabled();

            // Wait up to 5 seconds for port to release
            for (int i = 0; i < 10; i++)
            {
                Thread.Sleep(500);
                if (!IsPort53Occupied())
                {
                    _logger.LogInformation("Windows DNS Client stopped — port 53 is now free");
                    return true;
                }
            }

            _logger.LogError("Windows DNS Client stopped but port 53 still occupied after 5s");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disable Windows DNS Client");
            return false;
        }
    }

    /// <summary>
    /// Writes HKLM\SOFTWARE\Obstruo\DnscacheDisabled = "1". Read by
    /// UninstallService so the detached cleanup script restores Dnscache to
    /// auto-start — but only when Obstruo was the one that demoted it.
    /// </summary>
    private void MarkDnscacheDisabled()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Obstruo", writable: true);
            key?.SetValue("DnscacheDisabled", "1", RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record DnscacheDisabled marker");
        }
    }

    private void RunSc(string args)
    {
        var result = Obstruo.Shared.ProcessRunner.Run("sc", args);

        if (!result.Exited)
            _logger.LogWarning("sc command timed out and was killed: sc {Args}", args);
        else if (result.ExitCode != 0)
            _logger.LogWarning("sc command failed (exit {Code}): sc {Args} | {Error}",
                result.ExitCode, args, result.StdErr);
    }
}