using System.Runtime.InteropServices;

namespace WindowsSoundboard.Services;

/// <summary>
/// Detects the one case where global hotkeys silently stop working: the window in
/// front belongs to an elevated process. Windows does not deliver keystrokes aimed
/// at an elevated window to a low-level hook owned by a normal app, so nothing
/// reaches the hook and the board just looks dead.
/// </summary>
public static class ElevationGuard
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationInformation = 20;

    /// <summary>True when the foreground window is elevated and this app is not.</summary>
    public static bool ForegroundIsElevatedAndWeAreNot()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(foreground, out var processId);
        if (processId == 0 || processId == (uint)Environment.ProcessId)
        {
            return false;
        }

        return IsProcessElevated((int)processId) is true &&
               IsProcessElevated(Environment.ProcessId) is false;
    }

    /// <summary>Null when the token could not be read (an inaccessible or protected process).</summary>
    public static bool? IsProcessElevated(int processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        var token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out token))
            {
                return null;
            }

            var elevation = new TokenElevationInfo();
            if (!GetTokenInformation(
                    token,
                    TokenElevationInformation,
                    ref elevation,
                    Marshal.SizeOf<TokenElevationInfo>(),
                    out _))
            {
                return null;
            }

            return elevation.TokenIsElevated != 0;
        }
        finally
        {
            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }

            CloseHandle(process);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevationInfo
    {
        public int TokenIsElevated;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr token,
        int informationClass,
        ref TokenElevationInfo information,
        int length,
        out int returned);
}
