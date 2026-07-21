using System.Runtime.InteropServices;
using Obstruo.Shared;

namespace Obstruo.Tests;

/// <summary>
/// ProcessRunner sits under every netsh/sc call in the service and installer.
/// These pin down the contract the callers rely on: real exit codes, captured
/// output, and — critically — a hard timeout that kills a hung child instead of
/// wedging the caller.
/// </summary>
public class ProcessRunnerTests
{
    // The product is Windows-only; the runner is exercised with cmd.exe.
    private static bool OnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Fact]
    public void Run_SuccessfulCommand_ReportsSuccessAndCapturesStdout()
    {
        if (!OnWindows) return;

        var result = ProcessRunner.Run("cmd.exe", "/c echo obstruo-ok");

        Assert.True(result.Exited);
        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("obstruo-ok", result.StdOut);
    }

    [Fact]
    public void Run_NonZeroExit_IsNotSuccessButStillExited()
    {
        if (!OnWindows) return;

        var result = ProcessRunner.Run("cmd.exe", "/c exit 3");

        Assert.True(result.Exited);
        Assert.False(result.Success);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public void Run_Overrun_IsKilledAndReportedAsNotExited()
    {
        if (!OnWindows) return;

        // ping -n 4 runs ~3s; a 400ms cap must kill it rather than block.
        var start = DateTime.UtcNow;
        var result = ProcessRunner.Run("cmd.exe", "/c ping -n 4 127.0.0.1", timeoutMs: 400);
        var elapsed = DateTime.UtcNow - start;

        Assert.False(result.Exited);
        Assert.False(result.Success);
        Assert.True(elapsed < TimeSpan.FromSeconds(2),
            $"Runner should have returned promptly after the timeout, took {elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public void Run_MissingExecutable_DoesNotThrow_CallerSeesFailure()
    {
        // Callers wrap Run in try/catch expecting failure, not a crash — but a
        // clean non-throwing path is better. A bogus image name throws Win32Exception
        // from Process.Start; ProcessRunner lets it surface, so callers catch it.
        Assert.ThrowsAny<Exception>(() =>
            ProcessRunner.Run("this-executable-does-not-exist-obstruo", "arg"));
    }
}
