namespace UsageAI.UI;

/// <summary>
/// Builds the tray icon's context menu: Open dashboard as the bold default, Refresh, Settings
/// and Exit, painted by <see cref="MenuRenderer"/> with Windows 11's roomier rows and rounded
/// corners. Spacing follows the DPI of the monitor the menu opens on.
/// </summary>
internal static class TrayMenu
{
    public static ContextMenuStrip Create(Action open, Action refresh, Action settings, Action exit)
    {
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
        };
        // The default action is named for what it opens, and bold as Windows marks a default.
        var openItem = new ToolStripMenuItem("Open dashboard") { Font = new Font(menu.Font, FontStyle.Bold) };
        openItem.Click += (_, _) => open();
        menu.Items.Add(openItem);
        menu.Items.Add("Refresh", null, (_, _) => refresh());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => settings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        ApplyTheme(menu);
        ApplyMetrics(menu, menu.DeviceDpi);
        // The menu opens at the cursor, so its spacing is measured for that monitor. Its own
        // DeviceDpi still describes wherever it was shown last, or the start-up monitor.
        menu.Opening += (_, _) => ApplyMetrics(menu, WindowThemeHelpers.MonitorDpiAt(Cursor.Position) ?? menu.DeviceDpi);
        menu.HandleCreated += (_, _) => WindowThemeHelpers.ApplyMenuCorners(menu);
        menu.Disposed += (_, _) => openItem.Font.Dispose();
        return menu;
    }

    /// <summary>Repaints the menu in the current palette, or the system's own in High Contrast.</summary>
    public static void ApplyTheme(ContextMenuStrip menu)
    {
        menu.BackColor = Theme.Surface;
        menu.ForeColor = Theme.Text;
        menu.Renderer = Theme.IsHighContrast ? new ToolStripSystemRenderer() : new MenuRenderer();
        WindowThemeHelpers.ApplyMenuCorners(menu);
        menu.Invalidate(invalidateChildren: true);
    }

    internal static void ApplyMetrics(ContextMenuStrip menu, int dpi)
    {
        var scale = new LayoutScale(dpi);
        foreach (ToolStripItem item in menu.Items)
        {
            // Windows 11 menus have roomier rows than the WinForms default.
            item.Padding = item is ToolStripSeparator ? Padding.Empty : scale.Pad(10, 5, 10, 5);
        }
    }
}
