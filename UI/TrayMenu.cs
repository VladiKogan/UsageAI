namespace UsageAI.UI;

/// <summary>
/// Builds the tray icon's context menu: Open dashboard as the bold default, Refresh, Add account,
/// Settings and Exit, painted by <see cref="MenuRenderer"/> with Windows 11's roomier rows and
/// rounded corners. Spacing and type follow the DPI of the monitor the menu opens on.
/// </summary>
internal static class TrayMenu
{
    /// <summary>The menu's text size, the size Windows gives its own menus.</summary>
    internal const float FontPoints = 9F;

    private const string DefaultItemTag = "default";

    public static ContextMenuStrip Create(Action open, Action refresh, Action settings, Action exit, Action? addAccount = null)
    {
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
        };
        // The default action is named for what it opens, and bold as Windows marks a default.
        var openItem = new ToolStripMenuItem("Open dashboard") { Tag = DefaultItemTag };
        openItem.Click += (_, _) => open();
        menu.Items.Add(openItem);
        menu.Items.Add("Refresh", null, (_, _) => refresh());
        menu.Items.Add(new ToolStripSeparator());
        if (addAccount is not null)
        {
            menu.Items.Add("Add account…", null, (_, _) => addAccount());
        }

        menu.Items.Add("Settings…", null, (_, _) => settings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        ApplyTheme(menu);
        ApplyMetrics(menu, menu.DeviceDpi);
        // The menu opens at the cursor, so its spacing is measured for that monitor. Its own
        // DeviceDpi still describes wherever it was shown last, or the start-up monitor.
        menu.Opening += (_, _) => ApplyMetrics(menu, WindowThemeHelpers.MonitorDpiAt(Cursor.Position) ?? menu.DeviceDpi);
        menu.HandleCreated += (_, _) => WindowThemeHelpers.ApplyMenuCorners(menu);
        menu.Disposed += (_, _) => (menu.Tag as MenuFonts)?.Dispose();
        return menu;
    }

    /// <summary>Repaints the menu in the current palette, or the system's own in High Contrast.</summary>
    public static void ApplyTheme(ContextMenuStrip menu)
    {
        menu.BackColor = Theme.Surface;
        menu.ForeColor = Theme.Text;
        menu.Renderer = Theme.IsHighContrast ? new MenuRenderer.HighContrast() : new MenuRenderer();
        WindowThemeHelpers.ApplyMenuCorners(menu);
        menu.Invalidate(invalidateChildren: true);
    }

    /// <summary>
    /// Sizes the rows for <paramref name="dpi"/>, the monitor the menu is about to open on, and
    /// gives every item one shared face. The two follow different DPIs on purpose: when the menu
    /// lands on a monitor at another scale, WinForms rescales the items' fonts from the menu's
    /// <see cref="Control.DeviceDpi"/> to that monitor's but leaves their padding alone. Fonts made
    /// for the target monitor were therefore scaled twice, and the menu, sized before that, cut
    /// its rows off; fonts made for where the menu is now come out right after the one rescale.
    /// </summary>
    internal static void ApplyMetrics(ContextMenuStrip menu, int dpi)
    {
        ApplyFonts(menu, menu.DeviceDpi);
        var scale = new LayoutScale(dpi);
        foreach (ToolStripItem item in menu.Items)
        {
            // Windows 11 menus have roomier rows than the WinForms default.
            item.Padding = item is ToolStripSeparator ? Padding.Empty : scale.Pad(10, 5, 10, 5);
        }
    }

    /// <summary>
    /// Sets the regular and bold faces for <paramref name="dpi"/> on every item, unless they already
    /// carry them. Set on each item, the bold default can never differ in size from its neighbours,
    /// as it did while it alone kept the start-up monitor's font.
    /// </summary>
    private static void ApplyFonts(ContextMenuStrip menu, int dpi)
    {
        var fonts = menu.Tag as MenuFonts;
        var items = menu.Items.Cast<ToolStripItem>().Where(item => item is not ToolStripSeparator).ToArray();
        if (fonts is not null && fonts.Dpi == dpi &&
            items.All(item => ReferenceEquals(item.Font, Equals(item.Tag, DefaultItemTag) ? fonts.Bold : fonts.Regular)))
        {
            return;
        }

        var previous = fonts;
        fonts = new MenuFonts(dpi);
        menu.Tag = fonts;
        foreach (var item in items)
        {
            item.Font = Equals(item.Tag, DefaultItemTag) ? fonts.Bold : fonts.Regular;
        }

        previous?.Dispose();
    }

    /// <summary>The menu's two faces for one DPI, owned by the menu.</summary>
    private sealed class MenuFonts(int dpi) : IDisposable
    {
        public int Dpi { get; } = dpi;

        public Font Regular { get; } = Typography.Text(FontPoints, dpi);

        public Font Bold { get; } = Typography.Text(FontPoints, dpi, FontStyle.Bold);

        public void Dispose()
        {
            Regular.Dispose();
            Bold.Dispose();
        }
    }
}
