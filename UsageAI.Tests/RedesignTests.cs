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
