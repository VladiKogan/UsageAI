using System.Runtime.InteropServices;

namespace UsageAI.UI;

internal static class WindowThemeHelpers
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_DEFAULT = 0;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);

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

    /// <summary>
    /// Asks Windows 11 to round a borderless window's corners and draw its one-pixel frame in the
    /// theme's hairline, so the popup gets the same silhouette as the system's own flyouts. A
    /// window with a system frame, and every window in High Contrast, is handed back to the
    /// system defaults, so a user's accent-coloured borders are never overridden. Earlier
    /// Windows versions ignore both attributes.
    /// </summary>
    public static void ApplyWindowFrame(Form form)
    {
        if (!OperatingSystem.IsWindows() || !form.IsHandleCreated)
        {
            return;
        }

        try
        {
            var custom = form.FormBorderStyle == FormBorderStyle.None && !Theme.IsHighContrast;
            var corners = custom ? DWMWCP_ROUND : DWMWCP_DEFAULT;
            _ = DwmSetWindowAttribute(form.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
            var border = custom ? ColorRef(Theme.Hairline) : DWMWA_COLOR_DEFAULT;
            _ = DwmSetWindowAttribute(form.Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
        catch
        {
            // Ignore on unsupported OS or DWM errors
        }
    }

    /// <summary>DWM colours are COLORREF values: 0x00BBGGRR.</summary>
    internal static int ColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

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
