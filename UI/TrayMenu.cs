using UsageAI.Models;
using UsageAI.Services;

namespace UsageAI.UI;

/// <summary>What the tray menu's quick settings show as chosen, read each time the menu opens.</summary>
/// <param name="TrayChoices">The shown providers and accounts the tray icon can follow.</param>
/// <param name="Enabled">False while the Settings window is open, whose OK would undo a change made here.</param>
internal sealed record TrayQuickState(
    IReadOnlyList<(string Id, string Name)> TrayChoices,
    string? TrayProviderId,
    QuotaDisplay QuotaDisplay,
    bool NotificationsEnabled,
    bool Enabled = true);

/// <summary>The settings the tray menu changes without opening Settings.</summary>
internal sealed record TrayQuickSettings(
    Func<TrayQuickState> Read,
    Action<string?> SetTrayProvider,
    Action<QuotaDisplay> SetQuotaDisplay,
    Action<bool> SetNotifications);

/// <summary>
/// Builds the tray icon's context menu: Open dashboard as the bold default, Refresh, the quick
/// settings (what the tray icon shows, quota used or remaining, pausing notifications), Add
/// account, Settings and Exit, painted by <see cref="MenuRenderer"/> with Windows 11's roomier
/// rows and rounded corners. Spacing and type follow the DPI of the monitor the menu opens on.
/// </summary>
internal static class TrayMenu
{
    /// <summary>The menu's text size, the size Windows gives its own menus.</summary>
    internal const float FontPoints = 9F;

    private const string DefaultItemTag = "default";

    public static ContextMenuStrip Create(
        Action open,
        Action refresh,
        Action settings,
        Action exit,
        Action? addAccount = null,
        TrayQuickSettings? quick = null)
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
        if (quick is not null)
        {
            AddQuickSettings(menu, quick);
        }

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

    /// <summary>
    /// Adds the settings changed straight from the menu, then a separator. The choices and their
    /// ticks are rebuilt each time the menu opens, before its rows are measured, since accounts
    /// come and go and Settings changes the same values.
    /// </summary>
    private static void AddQuickSettings(ContextMenuStrip menu, TrayQuickSettings quick)
    {
        var tray = AddSubmenu(menu, "Tray icon shows");
        var quota = AddSubmenu(menu, "Show quota as");
        var notifications = new ToolStripMenuItem();
        // The row does what its label said when the menu opened.
        var notificationsEnabled = true;
        notifications.Click += (_, _) => quick.SetNotifications(!notificationsEnabled);
        menu.Items.Add(notifications);
        menu.Items.Add(new ToolStripSeparator());

        void Fill()
        {
            var state = quick.Read();
            notificationsEnabled = state.NotificationsEnabled;
            var all = IsTrayChoice(state, AppSettings.AllProvidersTrayId);
            ClearItems(tray);
            // A pinned provider that is not shown falls back to Automatic, as the tray itself does.
            AddChoice(
                tray,
                "Automatic",
                !all && !state.TrayChoices.Any(choice => IsTrayChoice(state, choice.Id)),
                () => quick.SetTrayProvider(null));
            AddChoice(tray, "All connected", all, () => quick.SetTrayProvider(AppSettings.AllProvidersTrayId));
            if (state.TrayChoices.Count > 0)
            {
                tray.DropDownItems.Add(new ToolStripSeparator());
            }

            foreach (var (id, name) in state.TrayChoices)
            {
                // An ampersand in an account label is text, not an access key.
                AddChoice(tray, name.Replace("&", "&&", StringComparison.Ordinal), IsTrayChoice(state, id), () => quick.SetTrayProvider(id));
            }

            ClearItems(quota);
            AddChoice(quota, "Used", state.QuotaDisplay == QuotaDisplay.Used, () => quick.SetQuotaDisplay(QuotaDisplay.Used));
            AddChoice(quota, "Remaining", state.QuotaDisplay == QuotaDisplay.Remaining, () => quick.SetQuotaDisplay(QuotaDisplay.Remaining));
            notifications.Text = state.NotificationsEnabled ? "Pause notifications" : "Resume notifications";
            tray.Enabled = quota.Enabled = notifications.Enabled = state.Enabled;
        }

        Fill();
        menu.Opening += (_, _) => Fill();
    }

    private static bool IsTrayChoice(TrayQuickState state, string id) =>
        string.Equals(state.TrayProviderId, id, StringComparison.OrdinalIgnoreCase);

    /// <summary>A row that opens a list of choices, the chosen one ticked in the list's check margin.</summary>
    private static ToolStripMenuItem AddSubmenu(ContextMenuStrip menu, string text)
    {
        var item = new ToolStripMenuItem(text);
        var list = (ToolStripDropDownMenu)item.DropDown;
        list.ShowImageMargin = false;
        list.ShowCheckMargin = true;
        list.HandleCreated += (_, _) => WindowThemeHelpers.ApplyMenuCorners(list);
        list.Disposed += (_, _) => (list.Tag as MenuFonts)?.Dispose();
        menu.Items.Add(item);
        return item;
    }

    private static void AddChoice(ToolStripMenuItem submenu, string text, bool chosen, Action choose)
    {
        var item = new ToolStripMenuItem(text) { Checked = chosen };
        item.Click += (_, _) => choose();
        submenu.DropDownItems.Add(item);
    }

    private static void ClearItems(ToolStripMenuItem submenu)
    {
        var items = submenu.DropDownItems.Cast<ToolStripItem>().ToArray();
        submenu.DropDownItems.Clear();
        foreach (var item in items)
        {
            item.Dispose();
        }
    }

    /// <summary>Repaints the menu in the current palette, or the system's own in High Contrast.</summary>
    public static void ApplyTheme(ContextMenuStrip menu)
    {
        menu.Renderer = Theme.IsHighContrast ? new MenuRenderer.HighContrast() : new MenuRenderer();
        ApplyColors(menu);
    }

    /// <summary>A submenu's list takes its renderer from the menu, but its colours are its own.</summary>
    private static void ApplyColors(ToolStripDropDown menu)
    {
        menu.BackColor = Theme.Surface;
        menu.ForeColor = Theme.Text;
        WindowThemeHelpers.ApplyMenuCorners(menu);
        menu.Invalidate(invalidateChildren: true);
        foreach (var submenu in menu.Items.OfType<ToolStripMenuItem>().Where(item => item.HasDropDown))
        {
            ApplyColors(submenu.DropDown);
        }
    }

    /// <summary>
    /// Sizes the rows for <paramref name="dpi"/>, the monitor the menu is about to open on, and
    /// gives every item one shared face. The two follow different DPIs on purpose: when the menu
    /// lands on a monitor at another scale, WinForms rescales the items' fonts from the menu's
    /// <see cref="Control.DeviceDpi"/> to that monitor's but leaves their padding alone. Fonts made
    /// for the target monitor were therefore scaled twice, and the menu, sized before that, cut
    /// its rows off; fonts made for where the menu is now come out right after the one rescale.
    /// A submenu's list is a window of its own, so it is measured the same way from its own DPI.
    /// </summary>
    internal static void ApplyMetrics(ToolStripDropDown menu, int dpi)
    {
        ApplyFonts(menu, menu.DeviceDpi);
        var scale = new LayoutScale(dpi);
        foreach (ToolStripItem item in menu.Items)
        {
            // Windows 11 menus have roomier rows than the WinForms default.
            item.Padding = item is ToolStripSeparator ? Padding.Empty : scale.Pad(10, 5, 10, 5);
            if (item is ToolStripMenuItem { HasDropDown: true } submenu)
            {
                ApplyMetrics(submenu.DropDown, dpi);
            }
        }
    }

    /// <summary>
    /// Sets the regular and bold faces for <paramref name="dpi"/> on every item, unless they already
    /// carry them. Set on each item, the bold default can never differ in size from its neighbours,
    /// as it did while it alone kept the start-up monitor's font.
    /// </summary>
    private static void ApplyFonts(ToolStripDropDown menu, int dpi)
    {
        var fonts = menu.Tag as MenuFonts;
        var items = menu.Items.Cast<ToolStripItem>().Where(item => item is not ToolStripSeparator).ToArray();
        if (fonts is not null && fonts.Dpi == dpi &&
            items.All(item => ReferenceEquals(item.Font, Equals(item.Tag, DefaultItemTag) ? fonts.Bold : fonts.Regular)))
        {
            return;
        }

        // Faces for this DPI are kept: a submenu's rows are rebuilt on every opening.
        MenuFonts? previous = null;
        if (fonts is null || fonts.Dpi != dpi)
        {
            previous = fonts;
            fonts = new MenuFonts(dpi);
            menu.Tag = fonts;
        }

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
