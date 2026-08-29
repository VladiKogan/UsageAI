using System.Runtime.InteropServices;

namespace UsageAI.Services;

/// <summary>
/// Registers the tray process with Windows Restart Manager so an installer that closes it can
/// reopen the updated executable after replacing its files.
/// </summary>
internal static class ApplicationRestart
{
    private const int RestartNoCrash = 0x1;
    private const int RestartNoHang = 0x2;
    private const int RestartNoReboot = 0x8;
    internal const int UpdateRestartFlags = RestartNoCrash | RestartNoHang | RestartNoReboot;

    public static bool TryRegister() => TryRegister(RegisterApplicationRestart);

    internal static bool TryRegister(Func<string, int, int> registerApplicationRestart) =>
        registerApplicationRestart(string.Empty, UpdateRestartFlags) >= 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegisterApplicationRestart(string commandLine, int flags);
}
