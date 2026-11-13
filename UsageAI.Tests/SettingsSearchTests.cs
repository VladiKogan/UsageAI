using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using UsageAI.Services;
using UsageAI.UI;

namespace UsageAI.Tests;

/// <summary>
/// Settings' search, modelled on the Claude desktop app's: results drop down under the box,
/// grouped by page, a few to a page; choosing one opens its page, focuses the setting, and marks
/// its row with an accent band that fades. Also the page icons it shares with the page list.
/// </summary>
internal static class SettingsSearchTests
{
    private static readonly string[] GeminiGroups = { "Gemini Models", "Claude and GPT models" };

    public static Task TestSearchMatchingAsync()
    {
        using var host = new Control();
        static SettingsSearchEntry Entry(int page, string text, Control target, params string[] keywords) =>
            new(page, text, target, keywords);
        var pages = new[] { "General", "Alerts", "Appearance" };
        var entries = new[]
        {
            Entry(0, "Refresh every (minutes)", host, "interval"),
            Entry(0, "Start with Windows", host, "startup"),
            Entry(1, "Notifications", host, "notify"),
            Entry(1, "Show tray notifications", host),
            Entry(1, "Notifications", host),
            Entry(2, "Theme", host, "dark"),
            Entry(2, "Warning colour", host),
            Entry(2, "Critical colour", host),
            Entry(2, "Dashboard colour", host),
            Entry(2, "Background colour", host),
            Entry(2, "Accent colour", host),
        };

        // Nothing typed, or only spaces, finds nothing and shows nothing.
        Equal(0, SettingsSearch.Find(entries, pages, "  ").Count);

        // Case does not matter; results come grouped under their page, in page order, and a
        // label found twice on one page is listed once.
        var notif = SettingsSearch.Find(entries, pages, "NOTIF");
        Equal(1, notif.Count);
        Equal("Alerts", notif[0].PageName);
        Equal("Notifications|Show tray notifications", string.Join('|', notif[0].Hits.Select(hit => hit.Entry.Text)));
        Equal(0, notif[0].Hits[0].MatchStart);
        Equal(10, notif[0].Hits[1].MatchStart);
        Equal(5, notif[0].Hits[1].MatchLength);
        Equal(-1, notif[0].NameMatchStart);

        // A label starting with the text comes before one containing it, which comes before a
        // keyword hit — the page's own order breaks ties.
        var ranked = SettingsSearch.Find(new[]
        {
            Entry(0, "Show the status", host),
            Entry(0, "Something else", host, "status"),
            Entry(0, "Status page", host),
        }, pages, "status");
        Equal("Status page|Show the status|Something else", string.Join('|', ranked[0].Hits.Select(hit => hit.Entry.Text)));
        Equal(-1, ranked[0].Hits[2].MatchStart);

        // A keyword finds its setting from its start ("dark" finds Theme), and a keyword typed
        // in full with more after it still does; a fragment from its middle does not.
        Equal("Theme", SettingsSearch.Find(entries, pages, "dar").Single().Hits.Single().Entry.Text);
        Equal("Refresh every (minutes)", SettingsSearch.Find(entries, pages, "intervals").Single().Hits.Single().Entry.Text);
        Equal(0, SettingsSearch.Find(entries, pages, "terv").Count);

        // A page whose name matches is listed even when none of its settings do.
        var page = SettingsSearch.Find(entries, pages, "gene").Single();
        Equal("General", page.PageName);
        Equal(0, page.NameMatchStart);
        Equal(4, page.NameMatchLength);
        Equal(0, page.Hits.Count);

        // A page shows four results and counts the rest; expanded, it shows them all.
        var colours = SettingsSearch.Find(entries, pages, "colour").Single();
        Equal(SettingsSearch.PerPage, colours.Hits.Count);
        Equal(1, colours.More);
        var all = SettingsSearch.Find(entries, pages, "colour", new HashSet<int> { 2 }).Single();
        Equal(5, all.Hits.Count);
        Equal(0, all.More);

        // Nothing found is no groups; the results then say so.
        Equal(0, SettingsSearch.Find(entries, pages, "zzqx").Count);

        // The keyword table finds what people call things, and only through its own labels.
        True(SettingsSearch.KeywordsFor("Mode").Contains("dark"));
        True(SettingsSearch.KeywordsFor("Accounts").Contains("login"));
        True(SettingsSearch.KeywordsFor("Start with Windows").Contains("startup"));
        Equal(0, SettingsSearch.KeywordsFor("Nothing like this").Count);
        return Task.CompletedTask;
    }

    public static Task TestSearchInSettingsAsync()
    {
        Theme.Apply(ThemeMode.Dark, 72, 90);
        var settings = new AppSettings { IsEphemeral = true, GlobalHotkeyEnabled = false };
        using var dialog = new SettingsForm(
            settings,
            new[] { ("codex", "Codex"), ("claude", "Claude Code"), ("gemini", "Google Gemini") },
            _ => Task.FromResult(new UpdateCheckResult(true, null)),
            _ => Task.CompletedTask,
            96,
            static () => new StartupState(StartupStatus.NotRegistered, null),
            static _ => { },
            new Dictionary<string, IReadOnlyList<string>> { ["gemini"] = GeminiGroups })
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-10_000, -10_000),
        };
        dialog.Show();
        dialog.Activate();
        Application.DoEvents();
        var box = dialog.SearchBox;
        var results = dialog.SearchResults;

        // The box sits above the page list, and Ctrl+F or Ctrl+E reaches it from anywhere.
        var column = Field<TableLayoutPanel>(dialog, "_navColumn");
        True(column.GetControlFromPosition(0, 0) is SettingsSearchBox);
        True(column.GetControlFromPosition(0, 1) is SectionNav);
        Equal("Search settings", box.AccessibleName);
        True(Command(dialog, Keys.Control | Keys.F));
        True(Command(dialog, Keys.Control | Keys.E));
        False(results.Visible);

        // Typing shows the results under the box, over the page list, with the first row
        // selected: the page's name, then its settings.
        dialog.Search("notif");
        Application.DoEvents();
        True(results.Visible);
        True(results.Top >= box.Parent!.Bottom - 2 || results.Top > 0);
        True(results.Width >= column.Width);
        Equal(0, results.SelectedIndex);
        Equal(
            "Alerts|Notifications in Alerts|Show tray notifications in Alerts",
            string.Join('|', results.Rows.Select(row => row.AccessibleText)));
        var accessible = results.AccessibilityObject;
        Equal(AccessibleRole.List, accessible.Role);
        Equal(3, accessible.GetChildCount());
        Equal("Notifications in Alerts", accessible.GetChild(1)!.Name);
        Equal(AccessibleRole.ListItem, accessible.GetChild(1)!.Role);
        True(accessible.GetChild(0)!.State.HasFlag(AccessibleStates.Selected));
        Equal("Alerts", accessible.GetSelected()!.Name);
        Null(accessible.GetChild(9));
        Draw(results);

        // Down and Up move through the results, stopping at either end, and stay in the box.
        Key(box, Keys.Up);
        Equal(0, results.SelectedIndex);
        Key(box, Keys.Down);
        Key(box, Keys.Down);
        Key(box, Keys.Down);
        Equal(2, results.SelectedIndex);

        // Enter goes to the setting: its page opens, the setting has the focus, the row is marked
        // and the search is cleared.
        Key(box, Keys.Enter);
        Application.DoEvents();
        Equal(1, dialog.CurrentPage);
        Equal(string.Empty, box.Text);
        False(results.Visible);
        True(dialog.IsFlashing);
        True(dialog.FlashStrength() > 0.5);
        var toggle = Field<CheckBox>(dialog, "_notificationsEnabled");
        True(toggle.Focused || !dialog.ContainsFocus);
        Equal(Theme.Blend(Theme.Accent, Theme.Surface, 0.28 * dialog.FlashStrength()).ToArgb(), toggle.BackColor.ToArgb(), tolerance: 0x101010);
        Draw(dialog);

        // Changing page ends the mark and gives the row its colours back.
        dialog.ShowPage(0);
        False(dialog.IsFlashing);
        Equal(Theme.Surface, toggle.BackColor);

        // With Windows' animations off the band does not fade: it holds, then goes.
        Motion.EnabledOverride = static () => false;
        try
        {
            dialog.GoTo(dialog.SearchEntries().First(entry => entry.Text == "Theme"));
            Equal(2, dialog.CurrentPage);
            Equal(1D, dialog.FlashStrength());
            Draw(dialog);
        }
        finally
        {
            Motion.EnabledOverride = null;
        }

        // In High Contrast the row is framed instead, and its controls keep the system colours.
        Theme.SetHighContrastProbe(static () => true);
        try
        {
            Theme.Apply(ThemeMode.Dark, 72, 90);
            dialog.GoTo(dialog.SearchEntries().First(entry => entry.Text == "Show quota as"));
            Draw(dialog);
            dialog.Search("quota");
            Application.DoEvents();
            Draw(results);
            box.Clear();
        }
        finally
        {
            Theme.SetHighContrastProbe(null);
            Theme.Apply(ThemeMode.Dark, 72, 90);
        }

        // The lists are searched too: a provider is found by name and chosen in its list, and
        // the account list the same way.
        var gemini = dialog.SearchEntries().Single(entry => entry.Text == "Google Gemini" && entry.ListIndex >= 0);
        dialog.GoTo(gemini);
        Equal(3, dialog.CurrentPage);
        Equal(gemini.ListIndex, Field<CheckedListBox>(dialog, "_providers").SelectedIndex);
        var account = dialog.SearchEntries().First(entry => entry.Page == 4 && entry.ListIndex >= 0);
        dialog.GoTo(account);
        Equal(4, dialog.CurrentPage);
        Equal(account.ListIndex, Field<CheckedListBox>(dialog, "_accountList").SelectedIndex);
        var pickerRow = dialog.SearchEntries().Single(entry => entry.Text == "Google Gemini popup rings");
        True(pickerRow.Target is ComboBox);

        // Searching the name finds both the provider and its popup rings: the two rows no longer
        // share a label, so neither hides the other.
        var geminiHits = SettingsSearch.Find(dialog.SearchEntries(), SettingsForm.PageNames, "gemini").Single().Hits;
        True(geminiHits.Any(found => found.Entry.ListKey == "gemini"));
        True(geminiHits.Any(found => found.Entry.Text == "Google Gemini popup rings"));

        // A list entry is found again by its key when chosen, wherever the list now holds it; one
        // no longer listed selects nothing rather than whatever took its place.
        var accounts = Field<CheckedListBox>(dialog, "_accountList");
        var codexAccount = dialog.SearchEntries().Single(entry => entry.ListKey == "codex" && entry.Page == 4);
        accounts.SelectedIndex = 0;
        dialog.GoTo(codexAccount with { ListIndex = 0 });
        Equal(codexAccount.ListIndex, accounts.SelectedIndex);
        True(codexAccount.ListIndex != 0);
        accounts.SelectedIndex = 0;
        dialog.GoTo(codexAccount with { ListKey = "codex@00000000" });
        Equal(0, accounts.SelectedIndex);

        // A page row opens the page itself, and "+N more" lists the rest of a page in place.
        dialog.Search("appear");
        Application.DoEvents();
        Equal(SettingsSearchRowKind.Page, results.SelectedRow!.Kind);
        Key(box, Keys.Enter);
        Equal(2, dialog.CurrentPage);
        dialog.Search("o");
        Application.DoEvents();
        var more = results.Rows.First(row => row.Kind == SettingsSearchRowKind.More);
        var morePage = more.Group!.Page;
        var hidden = more.Group.More;
        var before = results.Rows.Count(row => row.Kind == SettingsSearchRowKind.Hit && row.Group!.Page == morePage);
        Equal($"{hidden} more {(hidden == 1 ? "match" : "matches")} in {more.Group.PageName}", more.AccessibleText);
        var moreIndex = results.Rows.ToList().IndexOf(more);
        while (results.SelectedIndex < moreIndex)
        {
            Key(box, Keys.Down);
        }

        Key(box, Keys.Enter);
        Equal(before + hidden, results.Rows.Count(row => row.Kind == SettingsSearchRowKind.Hit && row.Group!.Page == morePage));
        Equal(SettingsSearchRowKind.Hit, results.SelectedRow!.Kind);
        True(results.Visible);
        // The results scroll to the selection when they are longer than the window allows.
        for (var step = 0; step < 40; step++)
        {
            Key(box, Keys.Down);
        }

        Equal(results.Rows.Count - 1, results.SelectedIndex);
        Draw(results);
        InvokeProtected(results, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, 10, 10, 120));
        Draw(results);

        // The mouse chooses a row too.
        dialog.Search("hotkey");
        Application.DoEvents();
        var hit = results.Rows.ToList().FindIndex(row => row.Kind == SettingsSearchRowKind.Hit);
        var bounds = results.RowBounds(hit);
        var centre = new Point(bounds.Left + 20, bounds.Top + (bounds.Height / 2));
        InvokeProtected(results, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, centre.X, centre.Y, 0));
        Draw(results);
        InvokeProtected(results, "OnMouseLeave", EventArgs.Empty);
        InvokeProtected(results, "OnMouseDown", new MouseEventArgs(MouseButtons.Right, 1, centre.X, centre.Y, 0));
        True(results.Visible);
        InvokeProtected(results, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, centre.X, centre.Y, 0));
        Equal(0, dialog.CurrentPage);
        True(dialog.IsFlashing);

        // A screen reader's default action on a result goes there as well.
        dialog.Search("rescan");
        Application.DoEvents();
        results.AccessibilityObject.GetChild(1)!.DoDefaultAction();
        Equal(4, dialog.CurrentPage);

        // Nothing found says so.
        dialog.Search("zzqx");
        Application.DoEvents();
        Equal(SettingsSearchRowKind.Empty, results.Rows.Single().Kind);
        Equal("No matching settings", results.Rows.Single().AccessibleText);
        Equal(-1, results.SelectedIndex);
        Null(results.SelectedRow);
        Key(box, Keys.Down);
        Key(box, Keys.Enter);
        Draw(results);

        // Escape steps back one thing at a time from the box — the results, then the text, then
        // to the page list — and only closes the window from elsewhere.
        True(results.Visible);
        True(DialogKey(dialog, Keys.Escape));
        False(results.Visible);
        Equal("zzqx", box.Text);
        Key(box, Keys.Down);
        True(results.Visible);
        True(DialogKey(dialog, Keys.Escape));
        True(DialogKey(dialog, Keys.Escape));
        Equal(string.Empty, box.Text);
        True(DialogKey(dialog, Keys.Escape));
        False(dialog.IsDisposed);

        // The page list carries the same icons as the results, and draws with them in every theme.
        var nav = Field<SectionNav>(dialog, "_nav");
        Equal(SettingsForm.PageGlyphs.Length, nav.Glyphs!.Count);
        Draw(nav);
        Theme.Apply(ThemeMode.Light, 72, 90);
        Draw(nav);
        Draw(Field<SettingsSearchBox>(dialog, "_search"));
        Theme.Apply(ThemeMode.Dark, 72, 90);
        True(Typography.HasIcons == (Typography.Icons(10F, 96) is not null));
        using (var font = Typography.Icons(10F, 96))
        {
            True(font is null || font.Unit == GraphicsUnit.Pixel);
        }

        dialog.Close();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Settings shows what the app uses: thresholds that saving tidies are shown as saved, and a
    /// tray-icon choice made from the tray menu for an account added since it opened is shown.
    /// </summary>
    public static Task TestSettingsShowsWhatIsSavedAsync()
    {
        var settings = new AppSettings { IsEphemeral = true, GlobalHotkeyEnabled = false, WarningPercent = 72, CriticalPercent = 90 };
        using var dialog = new SettingsForm(
            settings,
            new[] { ("codex", "Codex"), ("claude", "Claude Code") },
            _ => Task.FromResult(new UpdateCheckResult(true, null)),
            _ => Task.CompletedTask,
            96,
            trayChoices: new[] { ("codex", "Codex"), ("claude", "Claude Code") });
        var warning = Field<NumericUpDown>(dialog, "_warningPercent");
        var critical = Field<NumericUpDown>(dialog, "_criticalPercent");

        // Critical cannot be set below warning in the box itself, so what it shows is what is
        // saved; lowering warning first then lets critical follow, in either order.
        Equal(73M, critical.Minimum);
        critical.Text = "60";
        _ = critical.Value;
        Equal(73M, critical.Value);
        warning.Value = 50;
        Equal(51M, critical.Minimum);
        critical.Value = 60;
        dialog.FlushPendingChanges();
        Equal(50, settings.WarningPercent);
        Equal(60, settings.CriticalPercent);
        Equal(60M, critical.Value);

        // Raising warning past critical carries critical along, and both are saved together.
        warning.Value = 95;
        Equal(96M, critical.Value);
        dialog.FlushPendingChanges();
        Equal(95, settings.WarningPercent);
        Equal(96, settings.CriticalPercent);

        // Alert thresholds are sorted when saved, and a repeated one dropped; the boxes show that.
        var first = Field<NumericUpDown>(dialog, "_firstAlert");
        var second = Field<NumericUpDown>(dialog, "_secondAlert");
        first.Value = 97;
        second.Value = 85;
        dialog.FlushPendingChanges();
        Equal("85,97", string.Join(',', settings.NotifyAtPercent));
        Equal(85M, first.Value);
        Equal(97M, second.Value);
        second.Value = 85;
        dialog.FlushPendingChanges();
        Equal("85", string.Join(',', settings.NotifyAtPercent));
        Equal(85M, first.Value);
        Equal(95M, second.Value);

        // An account the tray menu shows now, chosen there, is shown in Settings too.
        var tray = Field<ComboBox>(dialog, "_trayProvider");
        var count = tray.Items.Count;
        settings.TrayProviderId = "claude@1a2b3c4d";
        dialog.ReloadQuickSettings(new[] { ("codex", "Codex"), ("claude", "Claude Code · me"), ("claude@1a2b3c4d", "Claude Code · work") });
        Equal(count + 1, tray.Items.Count);
        Equal("Claude Code · work", tray.SelectedItem!.ToString());
        Equal("Claude Code · me", tray.Items[3]!.ToString());

        // A question whose yes cannot be taken back answers no on Enter, and draws that button as
        // the primary one so the destructive button never looks like the default.
        using (var prompt = new PromptDialog("Delete recorded history", "Delete?", "Cannot be undone.", "Delete", "Cancel", PromptKind.Warning, 96))
        {
            Equal(Theme.Accent, prompt.AcceptControl.BackColor);
            prompt.MakeDeclineDefault();
            True(prompt.DeclineIsDefault);
            True(ReferenceEquals(prompt.DeclineControl, prompt.AcceptButton));
            Equal(Theme.Accent, prompt.DeclineControl!.BackColor);
            Equal(Theme.SurfaceRaised, prompt.AcceptControl.BackColor);
            True(prompt.DeclineControl.Font.Bold);
            False(prompt.AcceptControl.Font.Bold);
        }

        using (var single = new PromptDialog("UsageAI", "Done", "Nothing to choose.", "OK", null))
        {
            single.MakeDeclineDefault();
            False(single.DeclineIsDefault);
        }

        // The same choices again leave the list as it is.
        var item = tray.Items[3];
        dialog.UpdateTrayChoices(new[] { ("codex", "Codex"), ("claude", "Claude Code · me"), ("claude@1a2b3c4d", "Claude Code · work") });
        True(ReferenceEquals(item, tray.Items[3]));
        return Task.CompletedTask;
    }

    private static void Key(TextBox box, Keys key)
    {
        var method = typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(box, new object[] { new KeyEventArgs(key) });
        Application.DoEvents();
    }

    private static bool Command(SettingsForm dialog, Keys keys) => (bool)typeof(SettingsForm)
        .GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(dialog, new object?[] { default(Message), keys })!;

    private static bool DialogKey(SettingsForm dialog, Keys keys)
    {
        dialog.SearchBox.Focus();
        Application.DoEvents();
        return (bool)typeof(SettingsForm)
            .GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dialog, new object[] { keys })!;
    }

    private static void InvokeProtected(Control control, string name, EventArgs eventArgs) =>
        control.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, new object[] { eventArgs });

    private static void Draw(Control control)
    {
        using var bitmap = new Bitmap(Math.Max(1, control.Width), Math.Max(1, control.Height));
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
    }

    private static T Field<T>(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing field {name}.");
        return (T)field.GetValue(target)!;
    }

    private static void True(bool condition, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Expected true: {expression}");
        }
    }

    private static void False(bool condition, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? expression = null) =>
        True(!condition, $"not ({expression})");

    private static void Null(object? value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? expression = null) =>
        True(value is null, $"{expression} is null");

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    /// <summary>Colours compared channel by channel, since the band keeps fading between two reads.</summary>
    private static void Equal(int expectedArgb, int actualArgb, int tolerance)
    {
        var expected = Color.FromArgb(expectedArgb);
        var actual = Color.FromArgb(actualArgb);
        var spread = Color.FromArgb(tolerance);
        if (Math.Abs(expected.R - actual.R) > spread.R || Math.Abs(expected.G - actual.G) > spread.G || Math.Abs(expected.B - actual.B) > spread.B)
        {
            throw new InvalidOperationException($"Expected about {expected}, got {actual}.");
        }
    }
}
