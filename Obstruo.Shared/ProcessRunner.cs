using System.Diagnostics;
using System.Text;

namespace Obstruo.Shared;

/// <summary>
/// Runs a console process to completion, draining stdout and stderr on
/// background threads so a chatty child can never fill the OS pipe buffer and
/// deadlock the caller. Enforces a hard timeout and kills the process (and its
/// tree) if it overruns, so a hung netsh/sc call can never wedge the service.
///
/// Replaces the recurring "RedirectStandardOutput + WaitForExit + read ExitCode"
/// pattern, which both risked the buffer-fill deadlock and threw
/// InvalidOperationException when ExitCode was read on a still-running process.
/// </summary>
public static class ProcessRunner
{
    public readonly record struct Result(bool Exited, int ExitCode, string StdOut, string StdErr)
    {
        /// <summary>True only when the process exited on its own with code 0.</summary>
        public bool Success => Exited && ExitCode == 0;
    }

    public static Result Run(string fileName, string arguments, int timeoutMs = 5_000)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new Result(false, -1, stdout.ToString(), stderr.ToString());
        }

        // Second parameterless wait ensures the async read handlers have flushed.
        process.WaitForExit();

        return new Result(true, process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
