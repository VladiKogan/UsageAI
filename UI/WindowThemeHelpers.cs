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

    /// <summary>
    /// Keeps <paramref name="form"/> off the screen until it has painted. Windows shows a new
    /// window before it has drawn anything, so the dark windows opened as a white sheet (about
    /// 50 ms for Settings) and then as white blocks for each child control. The window is
    /// cloaked whenever its handle is created or it is hidden — it still exists and paints, it is
    /// just not composed onto the screen — and revealed once, after showing, every pending paint
    /// of it and its children is done. That also covers a handle recreated while the window is
    /// on screen (the popup turning into the dashboard), whose new window would otherwise
    /// appear unpainted where the popup was before moving.
    /// </summary>
    public static PaintedReveal RevealWhenPainted(Form form) => new(form);

    private const int DWMWA_CLOAK = 13;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr state);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    /// <summary>
    /// A window <paramref name="form"/> owns is on screen: a dialog, a message box, a tooltip.
    /// Owned windows share their owner's thread, so only that thread's windows are looked at.
    /// </summary>
    internal static bool OwnsVisibleWindow(Form form)
    {
        if (!OperatingSystem.IsWindows() || !form.IsHandleCreated)
        {
            return false;
        }

        var owner = form.Handle;
        var found = false;
        _ = EnumThreadWindows(
            GetWindowThreadProcessId(owner, out _),
            (window, _) =>
            {
                found = GetWindow(window, GW_OWNER) == owner && IsWindowVisible(window);
                return !found;
            },
            IntPtr.Zero);
        return found;
    }

    private const uint GW_OWNER = 4;

    /// <summary>
    /// Uncloaks the hidden window that owns <paramref name="form"/>: one cloaked when it hid
    /// would cloak <paramref name="form"/> with it. Hidden, it shows nothing either way, and it is
    /// cloaked again as it is next shown.
    /// </summary>
    private static void UncloakHiddenOwner(Form form)
    {
        if (!OperatingSystem.IsWindows() || !form.IsHandleCreated)
        {
            return;
        }

        var owner = GetWindow(form.Handle, GW_OWNER);
        if (owner != IntPtr.Zero && !IsWindowVisible(owner))
        {
            _ = SetCloak(owner, cloaked: false);
        }
    }

    internal static bool SetCloak(Control control, bool cloaked) =>
        OperatingSystem.IsWindows() && control.IsHandleCreated && SetCloak(control.Handle, cloaked);

    private static bool SetCloak(IntPtr window, bool cloaked)
    {
        try
        {
            var value = cloaked ? 1 : 0;
            return DwmSetWindowAttribute(window, DWMWA_CLOAK, ref value, sizeof(int)) == 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Cloaks a form until it has painted; see <see cref="RevealWhenPainted"/>.</summary>
    internal sealed class PaintedReveal
    {
        private readonly Form _form;
        private bool _pending;

        public PaintedReveal(Form form)
        {
            _form = form;
            form.HandleCreated += OnHandleCreated;
            form.VisibleChanged += OnVisibleChanged;
        }

        /// <summary>
        /// Takes a window that is on screen off it until it has painted again, for a change that
        /// reshapes it in steps (a new frame, a new size, a new place) the user should not watch.
        /// </summary>
        public void HideUntilPainted()
        {
            SetCloak(_form, cloaked: true);
            Schedule();
        }

        private void OnHandleCreated(object? sender, EventArgs eventArgs)
        {
            // A reveal queued on a handle that was just replaced may never run; this one stands in.
            _pending = false;
            SetCloak(_form, cloaked: true);
            Schedule();
        }

        private void OnVisibleChanged(object? sender, EventArgs eventArgs)
        {
            // DWM cloaks a window's owned windows with it, so a form that owns one on screen is
            // never cloaked: the compact popup hides when the Settings window it owns (opened
            // from it, ShowDialog takes the active window as owner) takes the focus, and cloaking
            // it left Settings modal and invisible. Cloaking again when shown, which WinForms
            // reports from WM_SHOWWINDOW before the window appears, covers a form left uncloaked.
            if (!OwnsVisibleWindow(_form))
            {
                SetCloak(_form, cloaked: true);
            }

            if (_form.Visible)
            {
                Schedule();
            }
        }

        // Queued rather than run now, so whatever the caller does after showing — sizing,
        // moving, filling the window — is finished before it appears.
        private void Schedule()
        {
            if (_pending || !_form.Visible || !_form.IsHandleCreated || _form.IsDisposed)
            {
                return;
            }

            _pending = true;
            _form.BeginInvoke(Reveal);
        }

        private void Reveal()
        {
            _pending = false;
            if (_form.IsDisposed || !_form.IsHandleCreated || !_form.Visible)
            {
                // Hidden again before it was revealed: it stays cloaked until it is next shown.
                return;
            }

            const uint RDW_UPDATENOW = 0x0100, RDW_ALLCHILDREN = 0x0080, RDW_ERASENOW = 0x0200;
            _ = RedrawWindow(_form.Handle, IntPtr.Zero, IntPtr.Zero, RDW_ERASENOW | RDW_UPDATENOW | RDW_ALLCHILDREN);
            // The owner may have hidden, and been cloaked, before this window counted as on screen.
            UncloakHiddenOwner(_form);
            SetCloak(_form, cloaked: false);
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
