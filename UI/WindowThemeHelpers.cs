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
    private const int DWMWCP_ROUNDSMALL = 3;
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

    /// <summary>
    /// Rounds a context menu's corners the way Windows 11 rounds its own menus (the small
    /// radius). High Contrast keeps square system menus.
    /// </summary>
    public static void ApplyMenuCorners(Control menu)
    {
        if (!OperatingSystem.IsWindows() || !menu.IsHandleCreated)
        {
            return;
        }

        try
        {
            var corners = Theme.IsHighContrast ? DWMWCP_DEFAULT : DWMWCP_ROUNDSMALL;
            _ = DwmSetWindowAttribute(menu.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
        }
        catch
        {
            // Ignore on unsupported OS or DWM errors
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>
    /// The effective DPI of the monitor holding <paramref name="point"/> (physical pixels), for a
    /// window about to open there whose own <see cref="Control.DeviceDpi"/> still describes
    /// wherever it was last shown. Null when Windows cannot say.
    /// </summary>
    public static int? MonitorDpiAt(Point point)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            const uint MONITOR_DEFAULTTONEAREST = 2;
            const int MDT_EFFECTIVE_DPI = 0;
            var monitor = MonitorFromPoint(new NativePoint { X = point.X, Y = point.Y }, MONITOR_DEFAULTTONEAREST);
            return monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 && dpi > 0
                ? (int)dpi
                : null;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr window, IntPtr updateRect, IntPtr updateRegion, uint flags);

    /// <summary>
    /// Stops <paramref name="control"/> painting until the returned scope is disposed, then
    /// repaints it and its children once. Replacing child controls otherwise paints the empty
    /// panel between removing the old ones and adding the new, which flashes. A hidden control is
    /// left alone: turning redraw back on would also make it visible.
    /// </summary>
    public static IDisposable SuspendRedraw(Control control)
    {
        if (!OperatingSystem.IsWindows() || !control.IsHandleCreated || !control.Visible)
        {
            return RedrawScope.None;
        }

        const int WM_SETREDRAW = 0x000B;
        _ = SendMessage(control.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        return new RedrawScope(control);
    }

    private sealed class RedrawScope : IDisposable
    {
        public static readonly RedrawScope None = new(null);

        private Control? _control;

        public RedrawScope(Control? control) => _control = control;

        public void Dispose()
        {
            if (_control is not { IsDisposed: false, IsHandleCreated: true } control)
            {
                _control = null;
                return;
            }

            _control = null;
            const int WM_SETREDRAW = 0x000B;
            const uint RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004, RDW_ALLCHILDREN = 0x0080, RDW_FRAME = 0x0400;
            _ = SendMessage(control.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
            _ = RedrawWindow(control.Handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_FRAME);
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
