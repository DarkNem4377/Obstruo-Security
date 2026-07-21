using Microsoft.Extensions.Logging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Obstruo.UI.Ipc;

/// <summary>
/// Verifies that a connected named pipe is actually served by the installed
/// Obstruo service, not by an impostor. Without this, a non-admin process that
/// creates a pipe with our name during a service-down window could impersonate
/// the service and capture the PIN/password the user types.
///
/// The check reads the pipe server's PID and confirms its image path is exactly
/// the installed service exe. A non-admin can't place a binary at that path
/// (Program Files is admin-only), so a spoofing process can never match.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PipeServerVerifier
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr Pipe, out uint ServerProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(
        IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private static readonly string ExpectedServicePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Obstruo", "service", "Obstruo.Service.exe");

    private static readonly string InstalledRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Obstruo");

    /// <summary>
    /// True if the pipe's server process is the installed Obstruo service. In a
    /// dev build (this UI not running from Program Files\Obstruo) the check is
    /// skipped and returns true, since the production paths won't line up.
    /// </summary>
    public static bool VerifyServerIsService(IntPtr pipeHandle, ILogger logger)
    {
        // Only enforce in a real install — dev builds run from bin\Debug.
        if (!AppContext.BaseDirectory.StartsWith(InstalledRoot, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("Pipe server verification skipped (not a Program Files install)");
            return true;
        }

        if (!GetNamedPipeServerProcessId(pipeHandle, out var pid))
        {
            logger.LogWarning("GetNamedPipeServerProcessId failed — refusing pipe");
            return false;
        }

        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
        {
            logger.LogWarning("OpenProcess for pipe server PID {Pid} failed — refusing pipe", pid);
            return false;
        }

        try
        {
            var sb = new StringBuilder(1024);
            var size = (uint)sb.Capacity;
            if (!QueryFullProcessImageName(handle, 0, sb, ref size))
            {
                logger.LogWarning("QueryFullProcessImageName failed — refusing pipe");
                return false;
            }

            var actual = sb.ToString();
            var ok = string.Equals(actual, ExpectedServicePath, StringComparison.OrdinalIgnoreCase);
            if (!ok)
                logger.LogWarning(
                    "Pipe server image mismatch — expected '{Expected}', got '{Actual}'. " +
                    "Possible spoof — refusing to send credentials.",
                    ExpectedServicePath, actual);
            return ok;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
