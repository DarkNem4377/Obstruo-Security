using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Obstruo.Service;

/// <summary>
/// Confirms that a connected pipe CLIENT is the installed Obstruo UI, not an
/// arbitrary local process. The ACL lets any authenticated user open the pipe
/// (the non-elevated UI needs that), so without this check any local script
/// could sit on the pipe and read the LogEvent / MetricsUpdate broadcasts —
/// i.e. the browsing/attempt history in plain text.
///
/// The image path of a client can't be forged by a non-admin: the expected path
/// is under Program Files, which is admin-only, so a spoofing process can never
/// match it. This mirrors the UI's PipeServerVerifier in the opposite direction.
/// Broadcasts (and the connect snapshot) go only to verified clients; per-command
/// credential checks still guard every mutation and sensitive read independently.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PipeClientVerifier
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr Pipe, out uint ClientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(
        IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private static readonly string ExpectedUiPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Obstruo", "ui", "Obstruo.UI.exe");

    private static readonly string InstalledRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Obstruo");

    /// <summary>
    /// True if the pipe's client process is the installed Obstruo UI. In a dev
    /// build (this service not running from Program Files\Obstruo) the check is
    /// skipped and returns true, since the production paths won't line up.
    /// Fails closed (returns false) on any error in a real install.
    /// </summary>
    public static bool VerifyClientIsUi(IntPtr pipeHandle, ILogger logger)
    {
        // Only enforce in a real install — dev builds run from bin\Debug.
        if (!AppContext.BaseDirectory.StartsWith(InstalledRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!GetNamedPipeClientProcessId(pipeHandle, out var pid))
        {
            logger.LogWarning("GetNamedPipeClientProcessId failed — treating client as untrusted");
            return false;
        }

        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
        {
            logger.LogWarning("OpenProcess for pipe client PID {Pid} failed — untrusted", pid);
            return false;
        }

        try
        {
            var sb = new StringBuilder(1024);
            var size = (uint)sb.Capacity;
            if (!QueryFullProcessImageName(handle, 0, sb, ref size))
            {
                logger.LogWarning("QueryFullProcessImageName failed — untrusted client");
                return false;
            }

            var actual = sb.ToString();
            var ok = string.Equals(actual, ExpectedUiPath, StringComparison.OrdinalIgnoreCase);
            if (!ok)
                logger.LogWarning(
                    "Pipe client image mismatch — expected '{Expected}', got '{Actual}'. " +
                    "Withholding broadcasts from this connection.",
                    ExpectedUiPath, actual);
            return ok;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
