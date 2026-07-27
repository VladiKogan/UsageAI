using System.Runtime.InteropServices;

namespace UsageAI.Services;

/// <summary>
/// Lets a second launch bring the running instance forward instead of exiting silently,
/// which previously looked exactly like the app failing to start.
/// </summary>
internal static class SingleInstance
{
    public const string MutexName = "Local\\UsageAI.TrayApplication";

    private static readonly IntPtr BroadcastWindow = new(0xFFFF);

    /// <summary>A process-wide unique message id derived from the app name.</summary>
    public static int ShowMessage { get; } = RegisterWindowMessage("UsageAI.ShowDashboard.9F1C");

    public static void BroadcastShow()
    {
        if (ShowMessage != 0)
        {
            PostMessage(BroadcastWindow, ShowMessage, IntPtr.Zero, IntPtr.Zero);
        }
    }

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegisterWindowMessage(string message);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam);
}
