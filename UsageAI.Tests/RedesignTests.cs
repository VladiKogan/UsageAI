using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using UsageAI.Models;
using UsageAI.Services;
using UsageAI.UI;

namespace UsageAI.Tests;

/// <summary>Checks for the redesigned popup: ring selection, its setting, tiles and their parts.</summary>
internal static class RedesignTests
{
    private static readonly int[] TileDpis = { 96, 192, 288 };
    private static readonly string[] ShippedProviders = { "codex", "claude", "copilot", "gemini" };
    private static readonly int[] RestoredBounds = { 0, 0, 900, 700 };
    private static readonly string[] SectionList = { "General", "Alerts", "History" };
    private static readonly int[] AutoSavedBounds = { 0, 0, 620, 600 };

    public static Task TestPopupRingSelectionAsync()
    {
        Equal(("Claude and GPT models", "Weekly"), PopupMetricSelection.Split("Claude and GPT models (Weekly)"));
        Equal(("Gemini Models", "Limit 1"), PopupMetricSelection.Split(" Gemini Models (Limit 1) "));
        Equal(((string?)null, "Weekly"), PopupMetricSelection.Split("Weekly"));
        Equal(((string?)null, "(5-hour)"), PopupMetricSelection.Split("(5-hour)"));
        Equal(((string?)null, "Odd ()"), PopupMetricSelection.Split("Odd ()"));
        Equal("Weekly", PopupMetricSelection.RingLabel(Metric("Claude and GPT models (Weekly)", UsageMetricKind.Rolling, 1)));

        var gemini = new[]
        {
            Metric("Gemini Models (5-hour)", UsageMetricKind.Session, 14),
            Metric("Gemini Models (Weekly)", UsageMetricKind.Rolling, 48),
            Metric("Claude and GPT models (5-hour)", UsageMetricKind.Session, 94),
            Metric("Claude and GPT models (Weekly)", UsageMetricKind.Rolling, 83),
        };
        Equal("Gemini Models|Claude and GPT models", string.Join('|', PopupMetricSelection.Groups(gemini)));

        // Automatic: the group holding the busiest limit, shortest window first.
        var automatic = PopupMetricSelection.Select(gemini, null);
        True(automatic.HasGroupChoice);
        True(automatic.GroupIsAutomatic);
        Equal("Claude and GPT models", automatic.Group);
        Equal("Claude and GPT models (5-hour)|Claude and GPT models (Weekly)", Names(automatic.Rings));

        // A pinned group wins, matched without regard to case or stray spaces.
        var pinned = PopupMetricSelection.Select(gemini, "  gemini models ");
        False(pinned.GroupIsAutomatic);
        Equal("Gemini Models", pinned.Group);
        Equal("Gemini Models (5-hour)|Gemini Models (Weekly)", Names(pinned.Rings));

        // A pinned group the provider stopped reporting falls back to automatic.
        True(PopupMetricSelection.Select(gemini, "Retired models").GroupIsAutomatic);

        // One group is no choice at all: the older "(Limit n)" names all share a group.
        var legacy = new[]
        {
            Metric("Gemini Models (Limit 1)", UsageMetricKind.Rolling, 10),
            Metric("Gemini Models (Limit 2)", UsageMetricKind.Rolling, 60),
        };
        Equal(0, PopupMetricSelection.Groups(legacy).Count);
        var legacyRings = PopupMetricSelection.Select(legacy, null);
        False(legacyRings.HasGroupChoice);
        Null(legacyRings.Group);
        Equal("Gemini Models (Limit 2)", Names(legacyRings.Rings));

        // Without groups the rings are the Important selection: busiest session, then rolling,
        // then monthly, two at most, and balances and unlimited allowances never ring.
        var mixed = new[]
        {
            new UsageMetric("Credits", UsageMetricKind.Balance, null, RemainingText: "$7"),
            Metric("Monthly", UsageMetricKind.Monthly, 99),
            Metric("Weekly", UsageMetricKind.Rolling, 20),
            Metric("Session low", UsageMetricKind.Session, 5),
            Metric("Session high", UsageMetricKind.Session, 50),
            new UsageMetric("Chat", UsageMetricKind.Monthly, 0, IsUnlimited: true),
        };
        Equal("Session high|Weekly", Names(PopupMetricSelection.Select(mixed, null).Rings));

        // Metered limits outside the three windows still ring, busiest first.
        var balances = new[]
        {
            Metric("Pool A", UsageMetricKind.Balance, 10),
            Metric("Pool B", UsageMetricKind.Balance, 70),
            Metric("Pool C", UsageMetricKind.Balance, 40),
        };
        Equal("Pool B|Pool C", Names(PopupMetricSelection.Select(balances, null).Rings));
        Equal(0, PopupMetricSelection.Select(Array.Empty<UsageMetric>(), "x").Rings.Count);
        return Task.CompletedTask;
    }

    public static Task TestPopupGroupSettingAsync()
    {
        var settings = new AppSettings();
        Null(settings.PopupMetricGroup("gemini"));
        settings.SetPopupMetricGroup("gemini", "  Claude and GPT models ");
        Equal("Claude and GPT models", settings.PopupMetricGroup("GEMINI"));
        settings.Save();
        Equal("Claude and GPT models", AppSettings.Load().PopupMetricGroup("Gemini"));

        settings.SetPopupMetricGroup("gemini", null);
        Null(settings.PopupMetricGroup("gemini"));
        settings.SetPopupMetricGroup("gemini", "   ");
        Null(settings.PopupMetricGroup("gemini"));

        // A hand-edited file keeps only usable entries, and an explicit null is an empty map.
        AppPaths.WriteAllTextAtomic(
            AppPaths.SettingsFile,
            """{"PopupMetricGroups":{"GEMINI":"Gemini Models","claude":"","   ":"x","codex":"__LONG__"}}"""
                .Replace("__LONG__", new string('x', 129), StringComparison.Ordinal));
        var loaded = AppSettings.Load();
        Equal("Gemini Models", loaded.PopupMetricGroup("gemini"));
        Equal(1, loaded.PopupMetricGroups.Count);
        AppPaths.WriteAllTextAtomic(AppPaths.SettingsFile, """{"PopupMetricGroups":null}""");
        Equal(0, AppSettings.Load().PopupMetricGroups.Count);
        return Task.CompletedTask;
    }

    public static Task TestCompactResetAndHeadlineAsync()
    {
        var now = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        Equal(string.Empty, UsageFormatting.CompactReset(null, now));
        Equal("now", UsageFormatting.CompactReset(now.AddMinutes(-1), now));
        Equal("2d 17h", UsageFormatting.CompactReset(now.AddDays(2).AddHours(17).AddMinutes(5), now));
        Equal("3h 11m", UsageFormatting.CompactReset(now.AddHours(3).AddMinutes(11).AddSeconds(30), now));
        Equal("45m", UsageFormatting.CompactReset(now.AddMinutes(45).AddSeconds(30), now));

        // A steep enough history turns the headline into a forecast.
        var weekly = Metric("Weekly", UsageMetricKind.Rolling, 80) with { ResetsAt = now.AddDays(3) };
        var codex = Status("codex", "Codex", weekly);
        var history = new[]
        {
            new UsageSample(now.AddHours(-3), "codex", weekly.Key, 50),
            new UsageSample(now.AddHours(-2), "codex", weekly.Key, 60),
            new UsageSample(now.AddHours(-1), "codex", weekly.Key, 70),
            new UsageSample(now, "codex", weekly.Key, 80),
        };
        var forecast = UsagePopupForm.HeadlineText(new[] { codex }, false, QuotaDisplay.Used, history, now);
        True(forecast.StartsWith("Codex runs out first: Weekly at 80% used, around ", StringComparison.Ordinal));

        // A limit forecast to run out outranks a busier one that will reset in time.
        var busyButSafe = Status("claude", "Claude Code", Metric("Weekly", UsageMetricKind.Rolling, 95) with { ResetsAt = now.AddHours(1) });
        var headline = UsagePopupForm.SelectHeadline(new[] { busyButSafe, codex }, history, now)!;
        Equal("codex", headline.Status.ProviderId);
        True(headline.Projection is { BeforeReset: true });
        Equal("claude", UsagePopupForm.SelectHeadline(new[] { busyButSafe, codex }, Array.Empty<UsageSample>(), now)!.Status.ProviderId);
        Null(UsagePopupForm.SelectHeadline(Array.Empty<ProviderStatus>(), history, now));

        // Without one it names the busiest limit and its reset; Copilot gets its short name.
        var copilot = Status("copilot", "GitHub Copilot", Metric("AI credits", UsageMetricKind.Monthly, 64) with { ResetsAt = now.AddDays(18) });
        Equal(
            "Busiest: Copilot AI credits at 36% left, resets in 18d 0h.",
            UsagePopupForm.HeadlineText(new[] { copilot }, false, QuotaDisplay.Remaining, Array.Empty<UsageSample>(), now));
        Equal(
            "Busiest: Codex Weekly at 80% used.",
            UsagePopupForm.HeadlineText(
                new[] { Status("codex", "Codex", Metric("Weekly", UsageMetricKind.Rolling, 80)) },
                false,
                QuotaDisplay.Used,
                Array.Empty<UsageSample>(),
                now));
        return Task.CompletedTask;
    }

    public static Task TestProviderTileStatesAsync()
    {
        var now = DateTimeOffset.Now;
        foreach (var dpi in TileDpis)
        {
            var scale = new LayoutScale(dpi);

            var claude = Status(
                "claude",
                "Claude Code",
                Metric("5-hour", UsageMetricKind.Session, 34) with { ResetsAt = now.AddHours(3) },
                Metric("Weekly", UsageMetricKind.Rolling, 78) with { ResetsAt = now.AddDays(4) });
            using (var tile = Tile(claude, dpi))
            {
                Equal(scale[156], tile.NaturalHeight);
                Equal(2, tile.Rings!.Rings.Count);
                Null(tile.IndicatorText);
                True(tile.AccessibleDescription!.Contains("5-hour: 34% used", StringComparison.Ordinal));
                True(tile.AccessibleDescription.Contains("Press Enter to open the dashboard", StringComparison.Ordinal));
                Draw(tile);
                Equal(Rectangle.Empty, Field<Rectangle>(tile, "_indicatorBounds"));
                Equal(ProviderCardAction.OpenDetails, Click(tile, new Point(tile.Width / 2, tile.Height / 2)));
                Equal(ProviderCardAction.OpenDetails, Press(tile, Keys.Enter));
                Equal(ProviderCardAction.None, Press(tile, Keys.A));
            }

            using (var remaining = Tile(claude, dpi, QuotaDisplay.Remaining))
            {
                True(remaining.AccessibleDescription!.Contains("5-hour: 66% left", StringComparison.Ordinal));
                Draw(remaining);
            }

            // Two model groups: the ⓘ indicator explains which one the rings show.
            var gemini = Status(
                "gemini",
                "Google Gemini",
                Metric("Gemini Models (5-hour)", UsageMetricKind.Session, 14),
                Metric("Claude and GPT models (5-hour)", UsageMetricKind.Session, 94) with { ResetsAt = now.AddHours(2) },
                Metric("Claude and GPT models (Weekly)", UsageMetricKind.Rolling, 83));
            using (var grouped = Tile(gemini, dpi))
            {
                var text = grouped.IndicatorText!;
                True(text.StartsWith("Claude and GPT models (closest to its limit)", StringComparison.Ordinal));
                True(text.Contains("5-hour · 94% used · resets in", StringComparison.Ordinal));
                True(text.Contains("Weekly · 83% used", StringComparison.Ordinal));
                True(text.EndsWith("Settings › Providers.", StringComparison.Ordinal));
                Draw(grouped);
                var indicator = Field<Rectangle>(grouped, "_indicatorBounds");
                True(grouped.ClientRectangle.Contains(indicator));

                // Choosing the indicator explains instead of opening the dashboard.
                var centre = new Point(indicator.Left + indicator.Width / 2, indicator.Top + indicator.Height / 2);
                Equal(ProviderCardAction.None, Click(grouped, centre));
                Move(grouped, centre);
                True(Field<bool>(grouped, "_toolTipVisible"));
                Move(grouped, new Point(1, grouped.Height - 2));
                False(Field<bool>(grouped, "_toolTipVisible"));
                Invoke(grouped, "OnGotFocus", EventArgs.Empty);
                True(Field<bool>(grouped, "_toolTipVisible"));
                Invoke(grouped, "OnLostFocus", EventArgs.Empty);
                False(Field<bool>(grouped, "_toolTipVisible"));
                Invoke(grouped, "OnMouseLeave", EventArgs.Empty);
            }

            using (var pinned = new ProviderTile(gemini, QuotaDisplay.Used, "Gemini Models", dpi) { Width = scale[176] })
            {
                True(pinned.IndicatorText!.StartsWith("Gemini Models\n", StringComparison.Ordinal));
            }

            // Stale: rings dim and the indicator turns to a warning that says how old they are.
            var stale = claude with { Error = "Claude did not respond.", LastUpdated = now.AddHours(-2), NextRetryAt = now.AddMinutes(5) };
            using (var staleTile = Tile(stale, dpi))
            {
                var text = staleTile.IndicatorText!;
                True(text.StartsWith("Last good reading 2h ago. Next try: retry in", StringComparison.Ordinal));
                True(text.EndsWith("Claude did not respond.", StringComparison.Ordinal));
                True(staleTile.AccessibleDescription!.Contains("Last good reading", StringComparison.Ordinal));
                Draw(staleTile);
            }

            using (var undated = Tile(claude with { Error = "Timed out", LastUpdated = null }, dpi))
            {
                True(undated.IndicatorText!.StartsWith("These values are from an earlier reading.", StringComparison.Ordinal));
            }

            // One ring: the free column carries a balance, or how much of the allowance is left.
            var codex = Status(
                "codex",
                "Codex",
                Metric("Weekly", UsageMetricKind.Rolling, 92),
                new UsageMetric("Credits", UsageMetricKind.Balance, null, RemainingText: "$12.50"));
            using (var balance = Tile(codex, dpi))
            {
                Equal(1, balance.Rings!.Rings.Count);
                Draw(balance);
            }

            var copilot = Status(
                "copilot",
                "GitHub Copilot",
                Metric("AI credits", UsageMetricKind.Monthly, 64) with { UsageText = "540 of 1,500 left" });
            using (var allowance = Tile(copilot, dpi))
            {
                Draw(allowance);
            }

            using (var bare = Tile(Status("copilot", "GitHub Copilot", Metric("AI credits", UsageMetricKind.Monthly, 64)), dpi))
            {
                Draw(bare);
            }

            // Nothing to ring: unlimited allowances and balances as text rows, or a plain notice.
            var unlimited = Status(
                "copilot",
                "GitHub Copilot",
                new UsageMetric("Chat", UsageMetricKind.Monthly, 0, IsUnlimited: true),
                new UsageMetric("Credits", UsageMetricKind.Balance, null, RemainingText: "$3"));
            using (var rows = Tile(unlimited, dpi))
            {
                Equal(0, rows.Rings!.Rings.Count);
                Draw(rows);
            }

            using (var empty = Tile(Status("codex", "Codex"), dpi))
            {
                Draw(empty);
            }

            using (var loading = Tile(new ProviderStatus("claude", "Claude Code", null, null, true), dpi))
            {
                Null(loading.Rings);
                Draw(loading);
            }

            // The popup never lists a signed-out provider, but a tile handed one still paints its
            // error and sends the user to the dashboard, where the sign-in help lives.
            var signedOut = new ProviderStatus("codex", "Codex", null, "Codex CLI was not found.", false, SignInCommand: "codex login");
            using (var disconnected = Tile(signedOut, dpi))
            {
                True(disconnected.AccessibleDescription!.Contains("Codex CLI was not found.", StringComparison.Ordinal));
                Draw(disconnected);
                Equal(ProviderCardAction.OpenDetails, Click(disconnected, new Point(2, 2)));
                Equal(ProviderCardAction.OpenDetails, Press(disconnected, Keys.Space));
                Equal(ProviderCardAction.None, Click(disconnected, new Point(2, 2), MouseButtons.Right));
            }

            using (var unnamed = Tile(new ProviderStatus("codex", "Codex", null, null, false), dpi))
            {
                Draw(unnamed);
            }
        }

        // High Contrast keeps a textual cue next to every critical figure.
        Theme.SetHighContrastProbe(static () => true);
        try
        {
            Theme.Apply(ThemeMode.Dark, 72, 90);
            using var contrast = Tile(Status("codex", "Codex", Metric("Weekly", UsageMetricKind.Rolling, 95)), 96);
            Draw(contrast);
        }
        finally
        {
            Theme.SetHighContrastProbe(null);
            Theme.Apply(ThemeMode.Dark, 72, 90);
        }

        // A tile too small to paint stays blank rather than throwing.
        using (var tiny = new ProviderTile(Status("codex", "Codex", Metric("Weekly", UsageMetricKind.Rolling, 1)), dpiOverride: 96) { Width = 20, Height = 20 })
        {
            tiny.CreateControl();
            using var bitmap = new Bitmap(20, 20);
            tiny.DrawToBitmap(bitmap, new Rectangle(0, 0, 20, 20));
        }

        return Task.CompletedTask;
    }

    public static Task TestSettingsPopupGroupPickerAsync()
    {
        var now = DateTimeOffset.Now;
        var gemini = Status(
            "gemini",
            "Google Gemini",
            Metric("Gemini Models (5-hour)", UsageMetricKind.Session, 14),
            Metric("Claude and GPT models (5-hour)", UsageMetricKind.Session, 94));
        var codex = Status("codex", "Codex", Metric("Weekly", UsageMetricKind.Rolling, 92));
        var groups = UsageApplicationContext.PopupGroups(new[]
        {
            gemini,
            codex,
            new ProviderStatus("claude", "Claude Code", null, "Signed out", false),
        });
        Equal("Gemini Models|Claude and GPT models", string.Join('|', groups["GEMINI"]));
        Equal(0, groups["codex"].Count);
        False(groups.ContainsKey("claude"));

        var settings = new AppSettings { GlobalHotkeyEnabled = false };
        settings.SetPopupMetricGroup("gemini", "Retired models");
        using (var dialog = Settings(settings, groups))
        {
            var picker = Descendants(dialog).OfType<ComboBox>().Single(box => box.AccessibleName == "Google Gemini popup rings");
            False(Descendants(dialog).OfType<ComboBox>().Any(box => box.AccessibleName == "Codex popup rings"));

            // A pinned group the provider no longer reports reads as automatic.
            Equal(0, picker.SelectedIndex);
            Equal("Closest to its limit|Gemini Models|Claude and GPT models", string.Join('|', picker.Items.Cast<object>()));
            picker.SelectedIndex = 2;
            Save(dialog);
        }

        Equal("Claude and GPT models", settings.PopupMetricGroup("gemini"));
        using (var reopened = Settings(settings, groups))
        {
            var picker = Descendants(reopened).OfType<ComboBox>().Single(box => box.AccessibleName == "Google Gemini popup rings");
            Equal(2, picker.SelectedIndex);
            picker.SelectedIndex = 0;
            Save(reopened);
        }

        Null(settings.PopupMetricGroup("gemini"));

        // Without readings there is nothing to pick from, so no picker appears.
        using (var empty = Settings(settings, null))
        {
            False(Descendants(empty).OfType<ComboBox>().Any(box => box.AccessibleName?.EndsWith("popup rings", StringComparison.Ordinal) == true));
        }

        return Task.CompletedTask;
    }

    public static Task TestRingAndIconDrawingAsync()
    {
        var fill = Color.FromArgb(255, 0, 0);
        var track = Color.FromArgb(0, 0, 255);
        using (var bitmap = new Bitmap(100, 100))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            // Zero draws no arc: an unused limit never looks started.
            graphics.Clear(Color.White);
            DrawingHelpers.DrawRing(graphics, new RectangleF(0, 0, 100, 100), 10, 0, fill, track);
            Equal(track.ToArgb(), bitmap.GetPixel(50, 5).ToArgb());

            // A quarter fills twelve to three o'clock and leaves the left side to the track.
            graphics.Clear(Color.White);
            DrawingHelpers.DrawRing(graphics, new RectangleF(0, 0, 100, 100), 10, 25, fill, track);
            Equal(fill.ToArgb(), bitmap.GetPixel(70, 7).ToArgb());
            Equal(track.ToArgb(), bitmap.GetPixel(5, 50).ToArgb());

            // Full and over-full both close the ring; a circle thinner than its stroke draws nothing.
            graphics.Clear(Color.White);
            DrawingHelpers.DrawRing(graphics, new RectangleF(0, 0, 100, 100), 10, 140, fill, track);
            Equal(fill.ToArgb(), bitmap.GetPixel(5, 50).ToArgb());
            graphics.Clear(Color.White);
            DrawingHelpers.DrawRing(graphics, new RectangleF(0, 0, 8, 8), 10, 50, fill, track);
            Equal(Color.White.ToArgb(), bitmap.GetPixel(4, 4).ToArgb());
        }

        foreach (var glyph in new[] { IconGlyph.Refresh, IconGlyph.Settings })
        {
            using var host = new Panel { Size = new Size(64, 64) };
            using var button = new IconButton(glyph, glyph.ToString()) { Size = new Size(32, 32), DpiOverride = 144 };
            host.Controls.Add(button);
            Equal(glyph.ToString(), button.AccessibleName);
            Draw(button);
            Invoke(button, "OnMouseEnter", EventArgs.Empty);
            Invoke(button, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 4, 4, 0));
            Draw(button);
            Invoke(button, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 4, 4, 0));
            Invoke(button, "OnMouseLeave", EventArgs.Empty);
            button.Rotation = 90F;
            button.Rotation = 90F;
            Equal(90F, button.Rotation);
            Draw(button);
            button.Enabled = false;
            Draw(button);
        }

        // High Contrast hover swaps to the system highlight pair, so the glyph never sits on a
        // fill of its own colour.
        Theme.SetHighContrastProbe(static () => true);
        try
        {
            Theme.Apply(ThemeMode.Dark, 72, 90);
            using var host = new Panel { Size = new Size(64, 64), BackColor = SystemColors.Window };
            using var button = new IconButton(IconGlyph.Settings, "Settings") { Size = new Size(32, 32) };
            host.Controls.Add(button);
            Invoke(button, "OnMouseEnter", EventArgs.Empty);
            button.CreateControl();
            using var bitmap = new Bitmap(32, 32);
            button.DrawToBitmap(bitmap, new Rectangle(0, 0, 32, 32));
            var colours = Enumerable.Range(0, 32)
                .SelectMany(x => Enumerable.Range(0, 32).Select(y => bitmap.GetPixel(x, y).ToArgb()))
                .ToHashSet();
            True(colours.Contains(SystemColors.Highlight.ToArgb()));
            button.Enabled = false;
            Draw(button);
        }
        finally
        {
            Theme.SetHighContrastProbe(null);
            Theme.Apply(ThemeMode.Dark, 72, 90);
        }

        using (var owner = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-10_000, -10_000),
            Size = new Size(300, 200),
            ShowInTaskbar = false,
        })
        using (var toolTip = new ThemedToolTip())
        {
            owner.Show();
            Application.DoEvents();
            toolTip.ShowRightAligned("Line one\nLine two", owner, new Rectangle(260, 10, 20, 20));
            Application.DoEvents();

            // Near the bottom of the screen the tooltip opens above its target instead.
            var bottom = owner.RectangleToClient(new Rectangle(0, Screen.FromControl(owner).WorkingArea.Bottom - 12, 20, 10));
            toolTip.ShowRightAligned(string.Join('\n', Enumerable.Repeat("A long line", 8)), owner, bottom);
            Application.DoEvents();
            toolTip.Hide(owner);
            owner.Hide();
        }

        return Task.CompletedTask;
    }

    public static Task TestUsageLedgerAsync()
    {
        var now = DateTimeOffset.Now;
        var weekly = Metric("Weekly", UsageMetricKind.Rolling, 95) with { ResetsAt = now.AddDays(3) };
        var session = Metric("5-hour", UsageMetricKind.Session, 75) with { ResetsAt = now.AddHours(4) };
        var calm = Metric("Monthly", UsageMetricKind.Monthly, 10) with { ResetsAt = now.AddDays(20) };
        var unreset = Metric("Pool", UsageMetricKind.Rolling, 40);
        var codex = Status("codex", "Codex", weekly, session, calm, unreset) with { AccountUrl = new Uri("https://example.com/codex") };
        var gemini = Status(
            "gemini",
            "Google Gemini",
            Metric("Gemini Models (5-hour)", UsageMetricKind.Session, 14),
            Metric("Gemini Models (Weekly)", UsageMetricKind.Rolling, 48),
            Metric("Claude and GPT models (5-hour)", UsageMetricKind.Session, 94));
        History(now, "codex", weekly, 60, 95, out var weeklyHistory);
        History(now, "codex", session, 70, 75, out var sessionHistory);
        History(now, "codex", calm, 9, 10, out var calmHistory);
        var history = weeklyHistory.Concat(sessionHistory).Concat(calmHistory).ToArray();

        using var table = new UsageLedger(new[] { codex, gemini }, MetricDisplayMode.All, QuotaDisplay.Remaining, history, 96)
        {
            Width = 900,
        };

        // Two model groups give two sub-headings; their limits are labelled by window alone.
        var groupRows = table.Rows.Where(row => row.Kind == UsageLedger.RowKind.Group).Select(row => row.Label).ToArray();
        Equal("Gemini Models|Claude and GPT models", string.Join('|', groupRows));
        Equal("5-hour|Weekly|5-hour", string.Join('|', table.Rows
            .Where(row => row.Kind == UsageLedger.RowKind.Metric && row.Status!.ProviderId == "gemini")
            .Select(row => row.Label)));
        True(table.Rows.Where(row => row.Kind == UsageLedger.RowKind.Metric && row.Status!.ProviderId == "gemini").All(row => row.Nested));

        // A provider with one group, or none, gets no sub-heading.
        False(table.Rows.Any(row => row.Kind == UsageLedger.RowKind.Group && row.Status!.ProviderId == "codex"));

        // Every forecast state: out before reset (critical and warning), resets first, and none.
        var projections = table.Rows
            .Where(row => row.Kind == UsageLedger.RowKind.Metric && row.Status!.ProviderId == "codex")
            .ToDictionary(row => row.Metric!.Name, row => row.Projection);
        True(projections["Weekly"] is { BeforeReset: true });
        True(projections["5-hour"] is not null);
        Null(projections["Pool"]);
        True(table.ShowsForecastColumn);
        Draw(table);

        // Narrow widths drop the forecast column instead of squeezing every cell.
        table.Width = 520;
        False(table.ShowsForecastColumn);
        Draw(table);
        table.Width = 900;

        // Tab walks the actions in reading order and stops at either end; Shift+Tab walks back.
        Draw(table);
        Equal(1, table.Actions.Count);
        Invoke(table, "OnGotFocus", EventArgs.Empty);
        Equal(0, Field<int>(table, "_focusIndex"));
        Equal(false, (bool)typeof(Control).GetMethod("IsInputKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(table, new object[] { Keys.Tab })!);
        Equal(false, (bool)typeof(Control).GetMethod("IsInputKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(table, new object[] { Keys.Tab | Keys.Shift })!);
        var invoked = new List<ProviderCardAction>();
        table.ActionInvoked += (_, args) => invoked.Add(args.Action);
        Invoke(table, "OnKeyDown", new KeyEventArgs(Keys.Space));
        Equal(ProviderCardAction.OpenAccount, invoked.Single());

        var stale = codex with { Error = "Timed out", LastUpdated = now.AddMinutes(-30) };
        using (var walk = new UsageLedger(new[] { stale, gemini with { AccountUrl = new Uri("https://example.com/g") } }, MetricDisplayMode.All, QuotaDisplay.Used, history, 96) { Width = 900 })
        {
            Draw(walk);
            Equal("OpenAccount|Refresh|OpenAccount", string.Join('|', walk.Actions.Select(action => action.Action)));
            Invoke(walk, "OnGotFocus", EventArgs.Empty);
            Invoke(walk, "OnKeyDown", new KeyEventArgs(Keys.Tab));
            Invoke(walk, "OnKeyDown", new KeyEventArgs(Keys.Tab));
            Equal(2, Field<int>(walk, "_focusIndex"));
            Invoke(walk, "OnKeyDown", new KeyEventArgs(Keys.Tab));
            Equal(2, Field<int>(walk, "_focusIndex"));
            Invoke(walk, "OnKeyDown", new KeyEventArgs(Keys.Tab | Keys.Shift));
            Equal(1, Field<int>(walk, "_focusIndex"));
            Draw(walk);
            Invoke(walk, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, walk.Actions[1].Bounds.Left + 1, walk.Actions[1].Bounds.Top + 1, 0));
            Draw(walk);
        }

        // A scrolling host keeps the focused action in view.
        using (var host = new Panel { AutoScroll = true, Size = new Size(900, 120) })
        using (var tall = new UsageLedger(new[] { gemini, stale }, MetricDisplayMode.All, QuotaDisplay.Used, history, 96) { Width = 880 })
        {
            host.Controls.Add(tall);
            host.CreateControl();
            Draw(tall);
            Invoke(tall, "OnGotFocus", EventArgs.Empty);
            Invoke(tall, "OnKeyDown", new KeyEventArgs(Keys.Tab));
            True(-host.AutoScrollPosition.Y > 0);
        }

        // Nothing to show, and High Contrast cues on critical figures.
        using (var empty = new UsageLedger(Array.Empty<ProviderStatus>(), MetricDisplayMode.All, QuotaDisplay.Used, Array.Empty<UsageSample>(), 96) { Width = 600 })
        {
            True(empty.AccessibleDescription is not null);
            False(empty.TabStop);
            Equal("No providers to show. Turn providers on in Settings › Providers.", empty.Rows.Single(row => row.Kind == UsageLedger.RowKind.Message).Text);
            Draw(empty);
        }

        using (var noUsage = new UsageLedger(new[] { Status("copilot", "GitHub Copilot") }, MetricDisplayMode.All, QuotaDisplay.Used, Array.Empty<UsageSample>(), 96) { Width = 600 })
        {
            Equal("No usage reported.", noUsage.Rows.Single(row => row.Kind == UsageLedger.RowKind.Message).Text);
        }

        Theme.SetHighContrastProbe(static () => true);
        try
        {
            Theme.Apply(ThemeMode.Dark, 72, 90);
            using var contrast = new UsageLedger(new[] { codex }, MetricDisplayMode.All, QuotaDisplay.Used, history, 96) { Width = 900 };
            Draw(contrast);
        }
        finally
        {
            Theme.SetHighContrastProbe(null);
            Theme.Apply(ThemeMode.Dark, 72, 90);
        }

        return Task.CompletedTask;
    }

    public static Task TestDashboardRefitsOnDpiChangeAsync()
    {
        // Four providers with three limits each: a table well over 380 pixels tall at 96 DPI.
        var states = ShippedProviders
            .Select(id => Status(
                id,
                id,
                Metric("Weekly", UsageMetricKind.Rolling, 50),
                Metric("5-hour", UsageMetricKind.Session, 20),
                Metric("Monthly", UsageMetricKind.Monthly, 5)))
            .ToArray();

        foreach (var sizedByUser in new[] { false, true })
        {
            using var form = new UsagePopupForm(new AppSettings { IsEphemeral = true }, 96)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-10_000, -10_000),
            };
            form.SetStates(states, false, DateTimeOffset.Now, Array.Empty<UsageSample>());
            form.SetMode(DashboardMode.Full);

            // The way the tray app opens it, so the form knows the dashboard has been shown.
            typeof(UsagePopupForm)
                .GetMethod("ShowDashboard", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(form, new object[] { new Rectangle(-10_000, -10_000, 1_200, 1_000), false });
            Application.DoEvents();
            if (sizedByUser)
            {
                Invoke(form, "OnResizeEnd", EventArgs.Empty);
            }

            // Windows moved the window to a monitor with another DPI and kept a size scaled from
            // one measured before it knew where it would land: too short for the table.
            form.ClientSize = new Size(700, 320);
            DpiChanged(form, new Rectangle(0, 0, 900, 380));
            Application.DoEvents();
            var table = Descendants(form).OfType<UsageLedger>().Single();
            True(table.NaturalHeight > 380);
            if (sizedByUser)
            {
                // A size the user chose is theirs: the window stays short and scrolls.
                True(form.ClientSize.Height <= 380);
            }
            else
            {
                // It grows to fit its table, or to the screen when the screen is the smaller.
                var screen = Screen.FromRectangle(new Rectangle(0, 0, 900, 380)).WorkingArea;
                True(form.ClientSize.Height >= Math.Min(table.NaturalHeight, screen.Height - 200));
            }

            form.Hide();
        }

        return Task.CompletedTask;
    }

    public static async Task TestDashboardKeepsStateAcrossUpdatesAsync()
    {
        // Retry on a stale row refreshes that provider alone, past its backoff.
        var first = new CountingClient("first");
        var second = new CountingClient("second");
        var settings = new AppSettings { IsEphemeral = true, HistoryEnabled = false };
        using (var service = new UsageRefreshService(new IUsageClient[] { first, second }, settings))
        {
            await service.RefreshProviderAsync("SECOND", anyWindowVisible: true);
            Equal(0, first.Calls);
            Equal(1, second.Calls);
            await service.RefreshProviderAsync("missing", anyWindowVisible: true);
            Equal(1, second.Calls);
            await service.RefreshAsync(force: true, anyWindowVisible: true);
            Equal(1, first.Calls);
            Equal(2, second.Calls);
        }

        var now = DateTimeOffset.Now;
        var loading = ShippedProviders.Select(id => new ProviderStatus(id, id, null, null, true)).ToArray();
        var loaded = ShippedProviders
            .Select(id => Status(id, id, Metric("Weekly", UsageMetricKind.Rolling, 50), Metric("5-hour", UsageMetricKind.Session, 20), Metric("Monthly", UsageMetricKind.Monthly, 5)) with
            {
                AccountUrl = new Uri($"https://example.com/{id}"),
            })
            .ToArray();
        using var form = new UsagePopupForm(new AppSettings { IsEphemeral = true }, 96)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-10_000, -10_000),
        };
        var retried = new List<string>();
        form.ProviderRefreshRequested += (_, args) => retried.Add(args.Status.ProviderId);

        // First opened while every provider is still loading: sized to their placeholders.
        form.SetStates(loading, true, null, Array.Empty<UsageSample>());
        form.SetMode(DashboardMode.Full);
        typeof(UsagePopupForm)
            .GetMethod("ShowDashboard", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form, new object[] { new Rectangle(-10_000, -10_000, 1_400, 1_000), false });
        Application.DoEvents();
        var content = Descendants(form).OfType<FlowLayoutPanel>().Single(panel => panel.Controls.OfType<UsageLedger>().Any());
        var table = content.Controls.OfType<UsageLedger>().Single();
        var loadingHeight = form.ClientSize.Height;

        // The data lands: the same table updates in place and the unsized window grows to fit.
        form.SetStates(loaded, false, now, Array.Empty<UsageSample>());
        Application.DoEvents();
        True(ReferenceEquals(table, content.Controls.OfType<UsageLedger>().Single()));
        True(form.ClientSize.Height > loadingHeight);

        // Focus inside the table survives an update.
        Draw(table);
        Invoke(table, "OnGotFocus", EventArgs.Empty);
        Invoke(table, "OnKeyDown", new KeyEventArgs(Keys.Tab));
        Equal(1, Field<int>(table, "_focusIndex"));
        form.SetStates(loaded.Select(state => state with { LastUpdated = now.AddMinutes(1) }).ToArray(), false, now, Array.Empty<UsageSample>());
        Application.DoEvents();
        Equal(1, Field<int>(table, "_focusIndex"));

        // A stale row's Retry names its provider.
        form.SetStates(loaded.Select((state, index) => index == 2 ? state with { Error = "Timed out" } : state).ToArray(), false, now, Array.Empty<UsageSample>());
        Draw(table);
        var retry = table.Actions.Single(action => action.Action == ProviderCardAction.Refresh).Bounds;
        Invoke(table, "OnMouseClick", new MouseEventArgs(MouseButtons.Left, 1, retry.Left + 1, retry.Top + 1, 0));
        Equal("copilot", retried.Single());

        // Moving the window is not sizing it; a size the user changes is, and is remembered.
        Invoke(form, "OnResizeBegin", EventArgs.Empty);
        Invoke(form, "OnResizeEnd", EventArgs.Empty);
        False(Field<bool>(form, "_dashboardSizedByUser"));
        Invoke(form, "OnResizeBegin", EventArgs.Empty);
        form.Size = new Size(form.Width - 40, form.Height - 40);
        Invoke(form, "OnResizeEnd", EventArgs.Empty);
        True(Field<bool>(form, "_dashboardSizedByUser"));
        var saved = Field<AppSettings>(form, "_settings");
        True(saved.DashboardSizedByUser);

        // A sized dashboard keeps its size when more rows arrive; it scrolls instead.
        var sizedHeight = form.ClientSize.Height;
        form.SetStates(loaded.Concat(loaded.Select(state => state with { ProviderId = state.ProviderId + "2" })).ToArray(), false, now, Array.Empty<UsageSample>());
        Application.DoEvents();
        Equal(sizedHeight, form.ClientSize.Height);

        // Focus moving to the tall table does not snap the scroll back to its top.
        content.AutoScrollPosition = new Point(0, 120);
        var scrollToControl = typeof(ScrollableControl).GetMethod("ScrollToControl", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var target = (Point)scrollToControl.Invoke(content, new object[] { table })!;
        Equal(content.DisplayRectangle.Location, target);
        form.Hide();

        // Saved bounds bring back whether the user sized them.
        var restored = new AppSettings { IsEphemeral = true, DashboardBounds = RestoredBounds, DashboardSizedByUser = true };
        using (var reopened = new UsagePopupForm(restored, 96))
        {
            True(Field<bool>(reopened, "_dashboardSizedByUser"));
        }

        using (var autoSaved = new UsagePopupForm(new AppSettings { IsEphemeral = true, DashboardBounds = AutoSavedBounds }, 96))
        {
            False(Field<bool>(autoSaved, "_dashboardSizedByUser"));
        }
    }

    public static Task TestSettingsPageAsync()
    {
        // Previews get a throwaway data folder of their own, never the user's profile.
        var original = Environment.GetEnvironmentVariable("USAGEAI_DATA_DIR");
        try
        {
            PreviewRenderer.IsolateDataDirectory();
            var isolated = Environment.GetEnvironmentVariable("USAGEAI_DATA_DIR")!;
            True(isolated.StartsWith(Path.Combine(Path.GetTempPath(), "UsageAI.Preview"), StringComparison.OrdinalIgnoreCase));
            PreviewRenderer.IsolateDataDirectory();
            False(string.Equals(isolated, Environment.GetEnvironmentVariable("USAGEAI_DATA_DIR"), StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("USAGEAI_DATA_DIR", original);
        }

        // Toggle switches stay check boxes: Checked, Space and the accessible role are WinForms'.
        using (var host = new Panel { Size = new Size(400, 60), BackColor = Theme.Surface })
        using (var toggle = new ToggleSwitch { Text = "Show tray notifications", Size = new Size(380, 34), DpiOverride = 144 })
        {
            host.Controls.Add(toggle);
            True(toggle is CheckBox);
            True(toggle.PreferredRowHeight >= new LayoutScale(144)[32]);
            foreach (var state in new[] { false, true })
            {
                toggle.Checked = state;
                Draw(toggle);
                Invoke(toggle, "OnMouseEnter", EventArgs.Empty);
                Draw(toggle);
                Invoke(toggle, "OnMouseLeave", EventArgs.Empty);
            }

            toggle.Enabled = false;
            Draw(toggle);
            Theme.SetHighContrastProbe(static () => true);
            try
            {
                Theme.Apply(ThemeMode.Dark, 72, 90);
                toggle.Enabled = true;
                Draw(toggle);
            }
            finally
            {
                Theme.SetHighContrastProbe(null);
                Theme.Apply(ThemeMode.Dark, 72, 90);
            }
        }

        // The section list: a click or the arrow keys choose, Home and End jump to the ends.
        using (var nav = new SectionNav(SectionList) { Size = new Size(176, 300), DpiOverride = 96 })
        {
            var chosen = 0;
            nav.SectionChosen += (_, _) => chosen++;
            Draw(nav);
            var second = nav.ItemBounds(1);
            Invoke(nav, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, second.Left + 4, second.Top + 4, 0));
            Draw(nav);
            Invoke(nav, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, second.Left + 4, second.Top + 4, 0));
            Equal(1, nav.SelectedIndex);
            Invoke(nav, "OnKeyDown", new KeyEventArgs(Keys.Down));
            Equal(2, nav.SelectedIndex);
            Invoke(nav, "OnKeyDown", new KeyEventArgs(Keys.End));
            Equal(2, nav.SelectedIndex);
            Invoke(nav, "OnKeyDown", new KeyEventArgs(Keys.Home));
            Equal(0, nav.SelectedIndex);
            Invoke(nav, "OnKeyDown", new KeyEventArgs(Keys.Up));
            Equal(0, nav.SelectedIndex);
            Equal(3, chosen);

            // Enter goes into the section rather than reaching the dialog's Save button.
            var entered = 0;
            nav.SectionEntered += (_, _) => entered++;
            True((bool)typeof(SectionNav).GetMethod("IsInputKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(nav, new object[] { Keys.Enter })!);
            Invoke(nav, "OnKeyDown", new KeyEventArgs(Keys.Enter));
            Equal(1, entered);
            Invoke(nav, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 4, 299, 0));
            Equal(3, chosen);
            nav.SelectedIndex = 9;
            Equal(2, nav.SelectedIndex);
            True(nav.AccessibleDescription!.StartsWith("History, 3 of 3", StringComparison.Ordinal));
            Invoke(nav, "OnGotFocus", EventArgs.Empty);
            Draw(nav);
            Invoke(nav, "OnLostFocus", EventArgs.Empty);
            Invoke(nav, "OnMouseLeave", EventArgs.Empty);
        }

        // Each preview shows what its setting produces.
        using (var ring = new SettingPreview(SettingPreviewKind.QuotaRing) { Size = new Size(200, 40), DpiOverride = 96, BackColor = Theme.Surface })
        {
            Equal("Example: 92% used", ring.AccessibleDescription);
            Draw(ring);
            ring.ShowRemaining = true;
            ring.ShowRemaining = true;
            Equal("Example: 8% left", ring.AccessibleDescription);
            Draw(ring);
        }

        using (var band = new SettingPreview(SettingPreviewKind.ThresholdBand) { Size = new Size(300, 40), DpiOverride = 96 })
        {
            // The same rule the saved setting follows: critical starts at least one point later.
            band.SetThresholds(60, 50);
            Equal("Fine below 60%, warning from 60%, critical from 61%", band.AccessibleDescription);
            band.SetThresholds(72, 90);
            Draw(band);
        }

        // The settings page jumps to a section, and follows the scroll back to the list.
        using var dialog = Settings(new AppSettings { IsEphemeral = true }, null);
        dialog.Show();
        Application.DoEvents();
        var sectionNav = Field<SectionNav>(dialog, "_nav");
        var scrollHost = Field<Panel>(dialog, "_scrollHost");
        Equal("General|Alerts|Appearance|Providers|History|About", string.Join('|', sectionNav.Sections));
        var appearance = 2;
        sectionNav.SelectedIndex = appearance;
        typeof(SettingsForm).GetMethod("ScrollToSection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, new object[] { appearance });
        True(-scrollHost.AutoScrollPosition.Y > 0);
        sectionNav.SelectedIndex = 0;
        typeof(SettingsForm).GetMethod("SyncNavToScroll", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, null);
        Equal(appearance, sectionNav.SelectedIndex);

        // At the bottom of the page the last section is the one marked, though it never reaches the top.
        scrollHost.AutoScrollPosition = new Point(0, scrollHost.DisplayRectangle.Height);
        typeof(SettingsForm).GetMethod("SyncNavToScroll", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, null);
        Equal(sectionNav.Sections.Count - 1, sectionNav.SelectedIndex);
        typeof(SettingsForm).GetMethod("ScrollToSection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, new object[] { 99 });

        // Enter on a section moves the focus to its first control.
        typeof(SettingsForm).GetMethod("EnterSection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, new object[] { 1 });
        True(Field<CheckBox>(dialog, "_notificationsEnabled").Focused);
        typeof(SettingsForm).GetMethod("EnterSection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, new object[] { -1 });

        // The tab order follows the screen: section list, then the page, then the buttons. The
        // page sits in its slim-scrollbar frame, which is what the window orders.
        True(sectionNav.TabIndex < Field<SlimScrollHost>(dialog, "_scrollFrame").TabIndex);

        // A theme change keeps the update status's meaning, and High Contrast restores the
        // borders that are the number fields' only edge on a card.
        typeof(SettingsForm).GetMethod("SetUpdateTone", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dialog, new object[] { Enum.Parse(typeof(SettingsForm).GetNestedType("UpdateTone", BindingFlags.NonPublic)!, "Warning") });
        Theme.SetHighContrastProbe(static () => true);
        try
        {
            Theme.Apply(ThemeMode.Dark, 72, 90);
            Application.DoEvents();
            Equal(Theme.WarningText, Field<Label>(dialog, "_updateStatus").ForeColor);
            Equal(BorderStyle.FixedSingle, Field<NumericUpDown>(dialog, "_refreshInterval").BorderStyle);
        }
        finally
        {
            Theme.SetHighContrastProbe(null);
            Theme.Apply(ThemeMode.Dark, 72, 90);
        }

        Application.DoEvents();
        Equal(BorderStyle.None, Field<NumericUpDown>(dialog, "_refreshInterval").BorderStyle);
        Equal(Theme.WarningText, Field<Label>(dialog, "_updateStatus").ForeColor);

        // Changing the quota display or a threshold updates its preview straight away.
        Field<SegmentedControl>(dialog, "_quotaDisplay").SelectedIndex = 1;
        True(Field<SettingPreview>(dialog, "_quotaPreview").ShowRemaining);
        Field<NumericUpDown>(dialog, "_warningPercent").Value = 60;
        Field<NumericUpDown>(dialog, "_criticalPercent").Value = 90;
        True(Field<SettingPreview>(dialog, "_thresholdPreview").AccessibleDescription!.Contains("warning from 60%", StringComparison.Ordinal));
        Draw(dialog);
        dialog.Hide();
        return Task.CompletedTask;
    }

    private sealed class CountingClient : IUsageClient
    {
        public CountingClient(string id) => Id = id;

        public string Id { get; }

        public string DisplayName => Id;

        public string SignInCommand => Id;

        public Uri AccountUrl => new($"https://example.com/{Id}");

        public int Calls { get; private set; }

        public Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new UsageSnapshot("Pro", new[] { Metric("Weekly", UsageMetricKind.Rolling, 10) }, DateTimeOffset.Now, Id, Id));
        }
    }

    /// <summary>Sends <paramref name="form"/> the event Windows raises after a monitor DPI change.</summary>
    private static void DpiChanged(Form form, Rectangle suggested)
    {
        const int WmDpiChanged = 0x02E0;
        var rect = System.Runtime.InteropServices.Marshal.AllocHGlobal(16);
        try
        {
            System.Runtime.InteropServices.Marshal.WriteInt32(rect, 0, suggested.Left);
            System.Runtime.InteropServices.Marshal.WriteInt32(rect, 4, suggested.Top);
            System.Runtime.InteropServices.Marshal.WriteInt32(rect, 8, suggested.Right);
            System.Runtime.InteropServices.Marshal.WriteInt32(rect, 12, suggested.Bottom);
            var dpi = form.DeviceDpi;
            var message = Message.Create(form.Handle, WmDpiChanged, (IntPtr)(dpi | (dpi << 16)), rect);
            var constructor = typeof(DpiChangedEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var args = (DpiChangedEventArgs)constructor.Invoke(new object[] { dpi, message });
            Invoke(form, "OnDpiChanged", args);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(rect);
        }
    }

    private static void History(DateTimeOffset now, string providerId, UsageMetric metric, int from, int to, out UsageSample[] samples) =>
        samples = Enumerable.Range(0, 4)
            .Select(step => new UsageSample(now.AddHours(step - 3), providerId, metric.Key, from + (to - from) * step / 3))
            .ToArray();

    private static UsageMetric Metric(string name, UsageMetricKind kind, int used) => new(name, kind, used);

    private static SettingsForm Settings(AppSettings settings, IReadOnlyDictionary<string, IReadOnlyList<string>>? groups)
    {
        var dialog = new SettingsForm(
            settings,
            new[] { ("codex", "Codex"), ("gemini", "Google Gemini") },
            _ => Task.FromResult(new UpdateCheckResult(true, null)),
            _ => Task.CompletedTask,
            96,
            static () => new StartupState(StartupStatus.NotRegistered, null),
            static _ => { },
            groups)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-10_000, -10_000),
        };
        dialog.CreateControl();
        return dialog;
    }

    private static void Save(SettingsForm dialog)
    {
        var save = Descendants(dialog).OfType<Button>().Single(button => button.Text == "Save");
        Invoke(save, "OnClick", EventArgs.Empty);
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static ProviderStatus Status(string id, string name, params UsageMetric[] metrics) =>
        new(id, name, new UsageSnapshot("Pro", metrics, DateTimeOffset.Now, id, name), null, false, DateTimeOffset.Now);

    private static ProviderTile Tile(ProviderStatus status, int dpi, QuotaDisplay display = QuotaDisplay.Used) =>
        new(status, display, null, dpi) { Width = new LayoutScale(dpi)[176] };

    private static string Names(IEnumerable<UsageMetric> metrics) => string.Join('|', metrics.Select(metric => metric.Name));

    /// <summary>
    /// The secondary surfaces: the themed prompt that replaced the system message boxes, the
    /// Windows 11-style tray menu, the ring-only tray icon, and What's New's sentence-case sections.
    /// </summary>
    public static Task TestPromptMenuAndTrayAsync()
    {
        Theme.Apply(ThemeMode.Dark, 72, 90);
        using (var question = new PromptDialog("UsageAI update", "UsageAI 9.9.9 is available", "You are running 1.0.0. Install it now?", "Install update", "Later", dpiOverride: 96))
        {
            var accept = question.AcceptControl;
            var decline = question.DeclineControl!;
            Equal<IButtonControl?>(accept, question.AcceptButton);
            Equal<IButtonControl?>(decline, question.CancelButton);
            Equal(DialogResult.Yes, accept.DialogResult);
            Equal(DialogResult.No, decline.DialogResult);
            Equal("UsageAI update", question.Text);
            // The primary action sits at the right edge, the way Windows dialogs order them.
            True(decline.Right < accept.Left);
            Equal(question.ClientSize.Width - 24, accept.Right);
            True(accept.Bottom < question.ClientSize.Height);
            Equal(440, question.ClientSize.Width);
            Draw(question);
        }

        // A notice has a single button that both Enter and Escape press, and a failure is marked
        // with a warning sign rather than the app's logo.
        using (var notice = new PromptDialog("UsageAI", "The changelog did not open", "Windows could not open it.", "OK", declineText: null, PromptKind.Warning, dpiOverride: 192))
        {
            Equal(PromptKind.Warning, notice.Kind);
            Null(notice.DeclineControl);
            Equal<IButtonControl?>(notice.AcceptControl, notice.CancelButton);
            Equal(880, notice.ClientSize.Width);
            Draw(notice);
        }

        // High Contrast hands the buttons to the system so they use the user's colours.
        Theme.SetHighContrastProbe(static () => true);
        try
        {
            Theme.Apply(ThemeMode.Dark, 72, 90);
            using var contrast = new PromptDialog("UsageAI", "Title", "Body", "Yes", "No", dpiOverride: 96);
            Equal(FlatStyle.System, contrast.AcceptControl.FlatStyle);
            Equal(FlatStyle.System, contrast.DeclineControl!.FlatStyle);
            Draw(contrast);
            using var contrastWarning = new PromptDialog("UsageAI", "Title", "Body", "OK", null, PromptKind.Warning, dpiOverride: 96);
            Draw(contrastWarning);
        }
        finally
        {
            Theme.SetHighContrastProbe(null);
            Theme.Apply(ThemeMode.Dark, 72, 90);
        }

        Equal("Added", WhatsNewForm.SentenceCase("ADDED"));
        Equal("Security", WhatsNewForm.SentenceCase("security"));
        Equal(string.Empty, WhatsNewForm.SentenceCase(string.Empty));

        // The menu paints a selected item, a disabled item and a divider without falling back.
        using (var menu = new ContextMenuStrip { Renderer = new MenuRenderer() })
        {
            menu.Items.Add(new ToolStripMenuItem("Open dashboard"));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Unavailable") { Enabled = false });
            menu.Items.Add(new ToolStripMenuItem("Exit"));
            menu.CreateControl();
            menu.Items[0].Select();
            using var bitmap = new Bitmap(Math.Max(1, menu.Width), Math.Max(1, menu.Height));
            menu.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            // The selected row carries a tinted fill inside its rounded inset.
            var selected = menu.Items[0].Bounds;
            var highlight = bitmap.GetPixel(selected.Right - 12, selected.Top + selected.Height / 2);
            True(highlight.ToArgb() != Theme.Surface.ToArgb());
            // The inset leaves the row's own corner on the menu surface.
            Equal(Theme.Surface.ToArgb(), bitmap.GetPixel(selected.Left + 1, selected.Top + 1).ToArgb());
        }

        // Where Windows does not round the menu, the renderer draws the hairline border itself.
        var rounded = MenuRenderer.RoundedByWindows;
        try
        {
            MenuRenderer.RoundedByWindows = false;
            using var square = new ContextMenuStrip { Renderer = new MenuRenderer() };
            square.Items.Add(new ToolStripMenuItem("Exit"));
            square.CreateControl();
            using var squareBitmap = new Bitmap(Math.Max(1, square.Width), Math.Max(1, square.Height));
            square.DrawToBitmap(squareBitmap, new Rectangle(Point.Empty, squareBitmap.Size));
            Equal(Theme.Hairline.ToArgb(), squareBitmap.GetPixel(0, square.Height / 2).ToArgb());
        }
        finally
        {
            MenuRenderer.RoundedByWindows = rounded;
        }

        // Row spacing follows the DPI the menu opens at.
        using (var scaled = TrayMenu.Create(static () => { }, static () => { }, static () => { }, static () => { }))
        {
            TrayMenu.ApplyMetrics(scaled, 96);
            Equal(new Padding(10, 5, 10, 5), scaled.Items[0].Padding);
            Equal(Padding.Empty, scaled.Items[2].Padding);
            TrayMenu.ApplyMetrics(scaled, 192);
            Equal(new Padding(20, 10, 20, 10), scaled.Items[0].Padding);
        }

        True(WindowThemeHelpers.MonitorDpiAt(Point.Empty) is >= 96);

        // High Contrast keeps the system menu renderer and square corners.
        Theme.SetHighContrastProbe(static () => true);
        try
        {
            Theme.Apply(ThemeMode.Dark, 72, 90);
            using var contrastMenu = TrayMenu.Create(static () => { }, static () => { }, static () => { }, static () => { });
            True(contrastMenu.Renderer is ToolStripSystemRenderer);
            Equal(6, contrastMenu.Items.Count);
        }
        finally
        {
            Theme.SetHighContrastProbe(null);
            Theme.Apply(ThemeMode.Dark, 72, 90);
        }

        var invoked = new List<string>();
        using (var tray = TrayMenu.Create(() => invoked.Add("open"), () => invoked.Add("refresh"), () => invoked.Add("settings"), () => invoked.Add("exit")))
        {
            True(tray.Renderer is MenuRenderer);
            Equal("Open dashboard", tray.Items[0].Text);
            True(tray.Items[0].Font.Bold);
            True(tray.Items[1] is ToolStripSeparator == false && tray.Items[2] is ToolStripSeparator);
            foreach (ToolStripItem item in tray.Items)
            {
                item.PerformClick();
            }

            Equal("open,refresh,settings,exit", string.Join(',', invoked));
        }

        // The tray ring is a clean arc: the middle stays clear rather than holding a pie or glyph.
        using (var icon = TrayIconFactory.Create(60, size: 32))
        using (var bitmap = icon.ToBitmap())
        {
            Equal(0, bitmap.GetPixel(16, 16).A);
            True(bitmap.GetPixel(26, 9).A > 0);
        }

        // An error is the only state that earns a glyph, and only where it can be read.
        using (var error = TrayIconFactory.Create(100, hasError: true, size: 32))
        using (var errorBitmap = error.ToBitmap())
        {
            True(Enumerable.Range(10, 12).Any(y => errorBitmap.GetPixel(16, y).A > 0));
        }

        using (var smallError = TrayIconFactory.Create(100, hasError: true, size: 16))
        using (var smallBitmap = smallError.ToBitmap())
        {
            Equal(0, smallBitmap.GetPixel(8, 8).A);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gauges ease from what they showed to what they should show, a status colour crossfades,
    /// and motion stops entirely when Windows animations are off or High Contrast is on.
    /// </summary>
    public static Task TestMotionAsync()
    {
        // The curve starts fast, settles gently and never overshoots.
        Equal(0D, Motion.Ease(0));
        Equal(1D, Motion.Ease(1));
        Equal(0D, Motion.Ease(-1));
        Equal(1D, Motion.Ease(2));
        True(Motion.Ease(0.25) > 0.6);
        var previous = 0D;
        for (var step = 1; step <= 20; step++)
        {
            var eased = Motion.Ease(step / 20D);
            True(eased >= previous && eased <= 1);
            previous = eased;
        }

        Equal(0F, UsagePopupForm.SpinnerAngle(0));
        Equal(180F, UsagePopupForm.SpinnerAngle(500));
        Equal(90F, UsagePopupForm.SpinnerAngle(1250));

        var enabled = true;
        Motion.EnabledOverride = () => enabled;
        try
        {
            long now = 10_000;
            using var motion = new MotionTracker(() => now);
            using var owner = new Panel();

            // First sight draws the value at once: opening the popup never replays a sweep.
            Equal(40F, motion.Value(owner, "a", 40));
            False(motion.IsAnimating);

            // A new value eases from the old one and arrives after the duration.
            Equal(40F, motion.Value(owner, "a", 80));
            True(motion.IsAnimating);
            now += MotionTracker.ValueDurationMs / 2;
            var halfway = motion.Value(owner, "a", 80);
            True(halfway > 60 && halfway < 80);

            // Retargeted mid-flight, it carries on from where it is rather than jumping back.
            Equal(halfway, motion.Value(owner, "a", 20));
            now += MotionTracker.ValueDurationMs;
            Equal(20F, motion.Value(owner, "a", 20));

            // Crossing a threshold crossfades the colour over a shorter time.
            Equal(Color.Green.ToArgb(), motion.Color(owner, "c", Color.Green).ToArgb());
            Equal(Color.Green.ToArgb(), motion.Color(owner, "c", Color.Red).ToArgb());
            now += MotionTracker.ColorDurationMs / 2;
            var mixed = motion.Color(owner, "c", Color.Red);
            True(mixed.R > Color.Green.R && mixed.G < Color.Green.G);
            now += MotionTracker.ColorDurationMs;
            Equal(Color.Red.ToArgb(), motion.Color(owner, "c", Color.Red).ToArgb());

            // Each tick repaints the controls still moving; with none left the timer stops.
            motion.Value(owner, "a", 60);
            True(motion.IsAnimating);
            InvokeTick(motion);
            InvokeTick(motion);
            False(motion.IsAnimating);

            // A disposed control is skipped rather than repainted.
            var gone = new Panel();
            motion.Value(gone, "a", 10);
            gone.Dispose();
            InvokeTick(motion);

            // Only the moving gauge's area is repainted; a whole-control request absorbs it.
            motion.Value(owner, "area", 0, new Rectangle(0, 0, 4, 4));
            motion.Value(owner, "area", 50, new Rectangle(0, 0, 4, 4));
            motion.Value(owner, "area2", 0, new Rectangle(10, 10, 4, 4));
            motion.Value(owner, "area2", 50, new Rectangle(10, 10, 4, 4));
            motion.Color(owner, "c", Color.Blue);
            InvokeTick(motion);
            InvokeTick(motion);

            // Hiding the window forgets every gauge: what changed meanwhile is drawn in place.
            motion.Value(owner, "a", 10);
            True(motion.IsAnimating);
            motion.Reset();
            False(motion.IsAnimating);
            Equal(70F, motion.Value(owner, "a", 70));
            False(motion.IsAnimating);

            // Animations off: every change lands at once and nothing keeps ticking.
            InvokeTick(motion);
            enabled = false;
            Equal(90F, motion.Value(owner, "a", 90));
            False(motion.IsAnimating);
            enabled = true;

            // The tiles and the table draw mid-flight values without trouble.
            var status = Status("codex", "Codex", Metric("Weekly", UsageMetricKind.Rolling, 30));
            using (var tile = new ProviderTile(status, dpiOverride: 96) { Motion = motion, Width = 300 })
            {
                Draw(tile);
            }

            var hotter = Status("codex", "Codex", Metric("Weekly", UsageMetricKind.Rolling, 95));
            using (var tile = new ProviderTile(hotter, dpiOverride: 96) { Motion = motion, Width = 300 })
            {
                Draw(tile);
                True(motion.IsAnimating);
            }

            using (var ledger = new UsageLedger(new[] { status }, MetricDisplayMode.All, QuotaDisplay.Used, Array.Empty<UsageSample>(), 96) { Motion = motion, Width = 640 })
            {
                Draw(ledger);
                ledger.SetData(new[] { hotter }, MetricDisplayMode.All, QuotaDisplay.Used, Array.Empty<UsageSample>());
                Draw(ledger);
            }

            // Flipping to "left" is presentation only: the gauges hold what was used, so nothing
            // starts moving.
            motion.Reset();
            using (var used = new ProviderTile(hotter, dpiOverride: 96) { Motion = motion, Width = 300 })
            {
                Draw(used);
            }

            using (var left = new ProviderTile(hotter, QuotaDisplay.Remaining, dpiOverride: 96) { Motion = motion, Width = 300 })
            {
                Draw(left);
                False(motion.IsAnimating);
            }

            using (var leftTable = new UsageLedger(new[] { hotter }, MetricDisplayMode.All, QuotaDisplay.Remaining, Array.Empty<UsageSample>(), 96) { Motion = motion, Width = 640 })
            {
                Draw(leftTable);
            }

            motion.Dispose();
            motion.Dispose();
            Equal(5F, motion.Value(owner, "z", 5));
        }
        finally
        {
            Motion.EnabledOverride = null;
        }

        // High Contrast asks for a still screen whatever Windows' animation setting says.
        Theme.SetHighContrastProbe(static () => true);
        try
        {
            Theme.Apply(ThemeMode.Dark, 72, 90);
            False(Motion.Enabled);
        }
        finally
        {
            Theme.SetHighContrastProbe(null);
            Theme.Apply(ThemeMode.Dark, 72, 90);
        }

        _ = Motion.Enabled;
        return Task.CompletedTask;
    }

    /// <summary>
    /// A control measures at its window's DPI even before it has a handle of its own, and a
    /// suspended repaint resumes once.
    /// </summary>
    public static Task TestWindowDpiAndRedrawAsync()
    {
        using var form = new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-10_000, -10_000),
        };
        var child = new Panel();
        var grandchild = new Panel();
        child.Controls.Add(grandchild);
        form.Controls.Add(child);

        // Before the window exists every control falls back to its own value.
        Equal(grandchild.DeviceDpi, LayoutScale.DpiOf(grandchild));
        form.CreateControl();
        _ = form.Handle;
        Equal(form.DeviceDpi, LayoutScale.DpiOf(grandchild));
        Equal(form.DeviceDpi, LayoutScale.DpiOf(form));
        Equal(new LayoutScale(form.DeviceDpi)[100], new LayoutScale(grandchild)[100]);
        using (var loose = new Panel())
        {
            Equal(loose.DeviceDpi, LayoutScale.DpiOf(loose));
        }

        // Hidden: nothing is suspended, since turning redraw back on would show the window.
        using (WindowThemeHelpers.SuspendRedraw(child))
        {
            False(form.Visible);
        }

        form.Show();
        Application.DoEvents();
        var redraw = WindowThemeHelpers.SuspendRedraw(child);
        child.Controls.Clear();
        child.Controls.Add(new Label { Text = "rebuilt" });
        redraw.Dispose();
        redraw.Dispose();
        Application.DoEvents();
        True(child.Visible);
        form.Hide();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Settings' number fields draw their spin buttons in the app's palette rather than the
    /// system's light style, which showed as a white block on a dark card.
    /// </summary>
    public static Task TestThemedStepperAsync()
    {
        Theme.Apply(ThemeMode.Dark, 72, 90);
        using var host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-10_000, -10_000) };
        using var stepper = new ThemedNumericUpDown { BackColor = Theme.SurfaceRaised, Minimum = 1, Maximum = 99, Value = 10, Width = 96 };
        host.Controls.Add(stepper);
        host.Show();
        Application.DoEvents();

        var buttons = stepper.SpinButtons;
        using (var bitmap = new Bitmap(Math.Max(1, buttons.Width), Math.Max(1, buttons.Height)))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            stepper.PaintButtons(graphics, new Rectangle(Point.Empty, bitmap.Size));
            // The corners are the field's own surface; the chevrons sit in the middle.
            Equal(Theme.SurfaceRaised.ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
            Equal(Theme.SurfaceRaised.ToArgb(), bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1).ToArgb());

            // Pointing at a half shades it, and pressing shades it more.
            Invoke(buttons, stepper, "OnButtonsMouseMove", new MouseEventArgs(MouseButtons.None, 0, 1, 1, 0));
            Equal(0, stepper.HoverHalf);
            stepper.PaintButtons(graphics, new Rectangle(Point.Empty, bitmap.Size));
            var hovered = bitmap.GetPixel(0, 0);
            True(hovered.ToArgb() != Theme.SurfaceRaised.ToArgb());
            Invoke(buttons, stepper, "OnButtonsMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 1, 1, 0));
            stepper.PaintButtons(graphics, new Rectangle(Point.Empty, bitmap.Size));
            True(bitmap.GetPixel(0, 0).ToArgb() != hovered.ToArgb());
            Invoke(buttons, stepper, "OnButtonsMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 1, 1, 0));
            Invoke(buttons, stepper, "OnButtonsMouseMove", new MouseEventArgs(MouseButtons.None, 0, 1, buttons.Height - 1, 0));
            Equal(1, stepper.HoverHalf);
            Invoke(buttons, stepper, "OnButtonsMouseLeave", EventArgs.Empty);
            Equal(-1, stepper.HoverHalf);

            stepper.Enabled = false;
            stepper.PaintButtons(graphics, new Rectangle(Point.Empty, bitmap.Size));
            stepper.Enabled = true;
            stepper.BackColor = Theme.Surface;
            stepper.PaintButtons(graphics, Rectangle.Empty);
        }

        Draw(stepper);

        // High Contrast leaves the system's buttons alone.
        Theme.SetHighContrastProbe(static () => true);
        try
        {
            Theme.Apply(ThemeMode.Dark, 72, 90);
            using var bitmap = new Bitmap(20, 20);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Magenta);
            stepper.PaintButtons(graphics, new Rectangle(0, 0, 20, 20));
            Equal(Color.Magenta.ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
        }
        finally
        {
            Theme.SetHighContrastProbe(null);
            Theme.Apply(ThemeMode.Dark, 72, 90);
        }

        host.Hide();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Settings is resizable but not maximizable, opens at the size it was left at, and stores
    /// that size at 96 DPI however the window closed.
    /// </summary>
    public static Task TestSettingsWindowSizeAsync()
    {
        static SettingsForm Open(AppSettings settings, int dpi) => new(
            settings,
            new[] { ("codex", "Codex") },
            static _ => Task.FromResult(new UpdateCheckResult(true, null)),
            static _ => Task.CompletedTask,
            readStartup: static () => new StartupState(StartupStatus.NotRegistered, null),
            writeStartup: static _ => { },
            dpiOverride: dpi)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-10_000, -10_000),
        };

        // Out-of-range or malformed sizes are dropped, so the default opens instead.
        foreach (var invalid in new[] { new[] { 100, 600 }, new[] { 800, 9000 }, new[] { 800 } })
        {
            new AppSettings { SettingsWindowSize = invalid }.Save();
            Null(AppSettings.Load().SettingsWindowSize);
        }

        var remembered = new AppSettings { GlobalHotkeyEnabled = false, SettingsWindowSize = new[] { 760, 600 } };
        using (var dialog = Open(remembered, 96))
        {
            Equal(FormBorderStyle.Sizable, dialog.FormBorderStyle);
            False(dialog.MaximizeBox);
            Equal(new Size(760, 600), dialog.ClientSize);

            // A drag that only moves the window is not a size choice.
            dialog.Show();
            Invoke(dialog, "OnResizeBegin", EventArgs.Empty);
            Invoke(dialog, "OnResizeEnd", EventArgs.Empty);
            dialog.Close();
        }

        Equal("760,600", string.Join(',', remembered.SettingsWindowSize!));

        // Resized by the user, the size is kept whether Settings was saved or cancelled.
        using (var dialog = Open(remembered, 96))
        {
            dialog.Show();
            Invoke(dialog, "OnResizeBegin", EventArgs.Empty);
            dialog.ClientSize = new Size(820, 700);
            Invoke(dialog, "OnResizeEnd", EventArgs.Empty);
            dialog.Close();
        }

        Equal("820,700", string.Join(',', remembered.SettingsWindowSize!));
        Equal("820,700", string.Join(',', AppSettings.Load().SettingsWindowSize!));

        // It is stored at 96 DPI, so a size chosen at 200% opens the same on any monitor.
        using (var scaled = Open(remembered, 192))
        {
            scaled.Show();
            Invoke(scaled, "OnResizeBegin", EventArgs.Empty);
            scaled.ClientSize = new Size(1200, 1000);
            Invoke(scaled, "OnResizeEnd", EventArgs.Empty);
            scaled.Close();
        }

        Equal("600,500", string.Join(',', remembered.SettingsWindowSize!));

        // A window opened with no saved size uses the default.
        using (var fresh = Open(new AppSettings { GlobalHotkeyEnabled = false }, 96))
        {
            Equal(new Size(700, 640), fresh.ClientSize);
        }

        File.Delete(AppPaths.SettingsFile);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Scrolling views hide the system scrollbar past their right edge and draw a slim one: it
    /// tracks the panel, drags, pages, passes the wheel on, and gives way to the system scrollbar
    /// in High Contrast.
    /// </summary>
    public static Task TestSlimScrollBarAsync()
    {
        Theme.Apply(ThemeMode.Dark, 72, 90);
        using var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-10_000, -10_000), ClientSize = new Size(300, 200) };
        var panel = new Panel { AutoScroll = true, BackColor = Theme.Night };
        panel.Controls.Add(new Panel { Bounds = new Rectangle(0, 0, 200, 1000) });
        using var host = new SlimScrollHost(panel, dpiOverride: 96) { Dock = DockStyle.Fill };
        form.Controls.Add(host);
        form.Show();
        Application.DoEvents();
        host.Sync();

        // The system scrollbar is pushed just past the host's edge, and the slim bar shows.
        var native = SystemInformation.GetVerticalScrollBarWidthForDpi(96);
        True(host.CanScroll);
        Equal(host.Width + native, panel.Width);
        Equal(host.Width, panel.ClientSize.Width);
        True(host.Bar.Visible);
        Equal(SlimScrollHost.GutterWidth, host.ScrollInset);
        Equal(SlimScrollHost.GutterWidth, host.Bar.Width);
        // Content gives up only the slim gutter once the panel is widened, and nothing when it fits.
        Equal(host.Width - SlimScrollHost.GutterWidth, host.ContentWidth(scrolling: true));
        Equal(host.Width, host.ContentWidth(scrolling: false));

        // The thumb follows the panel's position.
        var top = host.Bar.ThumbBounds.Top;
        host.ScrollTo(400);
        Equal(400, host.Offset);
        True(host.Bar.ThumbBounds.Top > top);
        host.ScrollTo(99_999);
        Equal(host.ContentHeight - host.ViewportHeight, host.Offset);
        host.ScrollTo(0);
        Draw(host.Bar);

        // Pointing at it widens the thumb; dragging it scrolls, and clicking below pages.
        var idleWidth = host.Bar.ThumbBounds.Width;
        Invoke(host.Bar, "OnMouseEnter", EventArgs.Empty);
        True(host.Bar.IsHovered && host.Bar.ThumbBounds.Width > idleWidth);
        Draw(host.Bar);
        var thumb = host.Bar.ThumbBounds;
        Invoke(host.Bar, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, thumb.Left + 1, thumb.Top + 2, 0));
        True(host.Bar.IsDragging);
        Invoke(host.Bar, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, thumb.Left + 1, thumb.Top + 40, 0));
        True(host.Offset > 0);
        Draw(host.Bar);
        Invoke(host.Bar, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, thumb.Left + 1, thumb.Top + 40, 0));
        False(host.Bar.IsDragging);
        Invoke(host.Bar, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, 1, 1, 0));
        host.ScrollTo(0);
        Invoke(host.Bar, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 2, host.Bar.Height - 2, 0));
        True(host.Offset > 0);
        var paged = host.Offset;
        Invoke(host.Bar, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 2, 1, 0));
        True(host.Offset < paged);
        Invoke(host.Bar, "OnMouseDown", new MouseEventArgs(MouseButtons.Right, 1, 2, 1, 0));
        Invoke(host.Bar, "OnMouseLeave", EventArgs.Empty);
        False(host.Bar.IsHovered);

        // The wheel over the bar scrolls the panel as it would over the content.
        host.ScrollTo(0);
        SendMessage(host.Bar.Handle, 0x020A, new IntPtr(-120 << 16), IntPtr.Zero);
        True(host.Offset > 0);

        // Content that fits needs no bar at all.
        panel.Controls[0].Height = 50;
        panel.PerformLayout();
        Application.DoEvents();
        host.Sync();
        False(host.CanScroll);
        False(host.Bar.Visible);
        Draw(host.Bar);
        panel.Controls[0].Height = 1000;
        panel.PerformLayout();
        Application.DoEvents();
        host.Sync();

        // High Contrast keeps the system scrollbar where it is, and the slim bar stands aside.
        Theme.SetHighContrastProbe(static () => true);
        try
        {
            Theme.Apply(ThemeMode.Dark, 72, 90);
            Application.DoEvents();
            host.PerformLayout();
            True(SlimScrollHost.UsesSystemScrollBar);
            Equal(host.Width, panel.Width);
            False(host.Bar.Visible);
            Equal(native, host.ScrollInset);
            Equal(host.Width - native, host.ContentWidth(scrolling: true));
        }
        finally
        {
            Theme.SetHighContrastProbe(null);
            Theme.Apply(ThemeMode.Dark, 72, 90);
        }

        form.Hide();

        // What's New's notes are a rich text box, read and scrolled through its own scrollbar.
        var releases = ReleaseNotes.LoadBundled();
        using (var notes = new WhatsNewForm(ReleaseNotes.ForUpgrade(releases, releases[0].DisplayVersion, new Version(0, 0)), 96)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-10_000, -10_000),
        })
        {
            notes.Show();
            notes.ClientSize = new Size(480, 420);
            Application.DoEvents();
            var frame = Field<SlimScrollHost>(notes, "_contentFrame");
            var text = Field<RichTextBox>(notes, "_content");
            frame.PerformLayout();
            Application.DoEvents();
            frame.Sync();
            True(frame.CanScroll);
            True(frame.ContentHeight > frame.ViewportHeight);
            Equal(frame.Width + native, text.Width);
            Equal(0, frame.Offset);
            frame.ScrollTo(120);
            True(frame.Offset > 0);
            frame.ScrollTo(0);
            Equal(0, frame.Offset);
            notes.Hide();
        }

        // Only scrolling panels and rich text boxes have a scroll state to follow.
        var refused = false;
        try
        {
            using var unsupported = new SlimScrollHost(new Label());
        }
        catch (ArgumentException)
        {
            refused = true;
        }

        True(refused);
        return Task.CompletedTask;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    private static void Invoke(Control sender, object target, string name, EventArgs eventArgs)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing method {name}.");
        method.Invoke(target, new object[] { sender, eventArgs });
    }

    private static void InvokeTick(MotionTracker motion)
    {
        var method = typeof(MotionTracker).GetMethod("OnTick", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing OnTick.");
        method.Invoke(motion, new object?[] { null, EventArgs.Empty });
    }

    private static void Draw(Control control)
    {
        control.CreateControl();
        using var bitmap = new Bitmap(Math.Max(1, control.Width), Math.Max(1, control.Height));
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
    }

    private static ProviderCardAction Click(ProviderTile tile, Point point, MouseButtons button = MouseButtons.Left)
    {
        var action = ProviderCardAction.None;
        void Handler(object? sender, ProviderCardActionEventArgs args) => action = args.Action;
        tile.ActionInvoked += Handler;
        Invoke(tile, "OnMouseClick", new MouseEventArgs(button, 1, point.X, point.Y, 0));
        tile.ActionInvoked -= Handler;
        return action;
    }

    private static ProviderCardAction Press(ProviderTile tile, Keys key)
    {
        var action = ProviderCardAction.None;
        void Handler(object? sender, ProviderCardActionEventArgs args) => action = args.Action;
        tile.ActionInvoked += Handler;
        Invoke(tile, "OnKeyDown", new KeyEventArgs(key));
        tile.ActionInvoked -= Handler;
        return action;
    }

    private static void Move(ProviderTile tile, Point point) =>
        Invoke(tile, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, point.X, point.Y, 0));

    private static void Invoke(Control control, string name, EventArgs eventArgs)
    {
        var method = control.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing method {name}.");
        method.Invoke(control, new object[] { eventArgs });
    }

    private static T Field<T>(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing field {name}.");
        return (T)field.GetValue(target)!;
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private static void Null(object? value)
    {
        if (value is not null)
        {
            throw new InvalidOperationException($"Expected null, got '{value}'.");
        }
    }

    private static void True(
        bool value,
        [System.Runtime.CompilerServices.CallerFilePath] string file = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (!value)
        {
            throw new InvalidOperationException($"Expected true at {Path.GetFileName(file)}:{line}.");
        }
    }

    private static void False(
        bool value,
        [System.Runtime.CompilerServices.CallerFilePath] string file = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0) => True(!value, file, line);
}
