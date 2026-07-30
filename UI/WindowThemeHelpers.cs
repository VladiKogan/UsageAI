using System.Runtime.InteropServices;

namespace UsageAI.UI;

internal static class WindowThemeHelpers
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;

    [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string? pszSubIdList);

    public static void ApplyDarkTitleBar(Form form, bool isDark)
    {
        if (!OperatingSystem.IsWindows() || !form.IsHandleCreated)
        {
            return;
        }

        try
        {
            int useDarkMode = isDark ? 1 : 0;
            var result = DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
            if (result != 0)
            {
                _ = DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useDarkMode, sizeof(int));
            }
        }
        catch
        {
            // Ignore on unsupported OS or DWM errors
        }
    }

    public static void ApplyDarkScrollbar(Control control, bool isDark)
    {
        if (!OperatingSystem.IsWindows() || !control.IsHandleCreated)
        {
            return;
        }

        try
        {
            _ = SetWindowTheme(control.Handle, isDark ? "DarkMode_Explorer" : "Explorer", null);
        }
        catch
        {
            // Ignore on unsupported OS
        }
    }
}
