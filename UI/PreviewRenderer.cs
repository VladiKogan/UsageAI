using System.Globalization;
using System.Threading;
using UsageAI.Models;
using UsageAI.Services;

namespace UsageAI.UI;

/// <summary>
/// Renders the popup with representative data so the README screenshot can be regenerated
/// without connecting real accounts. Kept out of the application entry point because it is
/// a development tool, not part of the tray app's behaviour.
/// </summary>
internal static class PreviewRenderer
{
    public static void Render(string[] args)
    {
        // Creating the form installs WinForms' synchronization context on this thread. Left in
        // place, a caller that awaits afterwards posts its continuation to a message loop that
        // nobody runs, so the thread is handed back as it was found.
        var callerContext = SynchronizationContext.Current;
        try
        {
            RenderCore(args);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(callerContext);
        }
    }

    private static void RenderCore(string[] args)
    {
        UseInvariantCulture();

        var outputPath = ResolveOutputPath(args);
        var previewDpi = ResolveDpi(args);
        var settings = PreviewSettings(args);
        var (states, history, lastRefreshed) = SampleData(DateTimeOffset.Now);

        // Drawn at the requested DPI itself rather than at 96 and enlarged: the controls paint
        // with fonts realised for an exact DPI, which a 96-DPI surface inside a scaled session
        // inflated past their boxes, and the enlargement blurred every edge.
        using var form = new UsagePopupForm(settings, previewDpi);
        var isFull = args.Contains("--full", StringComparer.OrdinalIgnoreCase);
        var mode = isFull ? DashboardMode.Full : DashboardMode.Compact;
        form.ShowNearTray(mode);
        form.SetStates(states, isRefreshing: false, lastRefreshed: lastRefreshed, history: history);
        ScaleControlFonts(form, previewDpi);
        if (isFull)
        {
            form.FormBorderStyle = FormBorderStyle.None;
        }

        // Sized from the content on a canvas larger than any screen, so the image never
        // inherits this machine's screen size; the dashboard at its natural width, which
        // keeps the forecast column.
        var scale = new LayoutScale(previewDpi);
        form.ApplyPreferredSize(new Rectangle(0, 0, scale[4000], scale[4000]));
        if (isFull)
        {
            form.ClientSize = new Size(scale[940], form.ClientSize.Height);
        }

        form.Location = new Point(-30_000, -30_000);
        Application.DoEvents();
        form.PerformLayout();
        using var window = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(window, form.ClientRectangle);
        using var image = ComposeWholeTable(form, window);
        image.SetResolution(previewDpi, previewDpi);
        image.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
        form.Hide();
    }

    /// <summary>
    /// Opens the real popup or dashboard with the sample data at the monitor's own DPI, so a
    /// change can be tried by hand without a tray icon, provider credentials, or the
    /// single-instance lock of an installed copy. Closing or hiding the window ends the process.
    /// </summary>
    public static void ShowWindow(string[] args)
    {
        UseInvariantCulture();
        var settings = PreviewSettings(args);
        var window = Program.ValueAfter(args, "--preview-window")?.ToLowerInvariant();
        switch (window)
        {
            case "settings":
                ShowSettings(settings);
                return;
            case "whatsnew":
                ShowWhatsNew();
                return;
            case "prompt":
                PromptDialog.Ask(
                    "UsageAI update",
                    "UsageAI 9.9.9 is available",
                    $"You are running {AppIdentity.Version}. Download the verified installer and install it now?",
                    "Install update",
                    "Later");
                return;
            case "warning":
                PromptDialog.Ask(
                    "UsageAI update",
                    "The update could not be installed",
                    "The installer's signature did not match. The GitHub release page has the installer to download by hand.",
                    "Open release page",
                    "Close",
                    PromptKind.Warning);
                return;
            case "menu":
                ShowMenu();
                return;
        }

        var mode = window == "dashboard" ? DashboardMode.Full : DashboardMode.Compact;

        using var context = new ApplicationContext();
        using var form = new UsagePopupForm(settings) { StaysOpenWhenInactive = true };
        var (states, history, lastRefreshed) = SampleData(DateTimeOffset.Now);
        if (string.Equals(Program.ValueAfter(args, "--states"), "mixed", StringComparison.OrdinalIgnoreCase))
        {
            states = MixedStates(states, DateTimeOffset.Now);
        }

        // Each refresh alternates between the sample and busier readings, so the gauges have
        // somewhere to move and some cross a status threshold.
        var shown = states;
        form.RefreshRequested += async (_, _) =>
        {
            form.SetStates(shown, isRefreshing: true, lastRefreshed: lastRefreshed, history: history);
            await Task.Delay(900);
            shown = ReferenceEquals(shown, states) ? Busier(states) : states;
            if (!form.IsDisposed)
            {
                form.SetStates(shown, isRefreshing: false, lastRefreshed: DateTimeOffset.Now, history: history);
            }
        };
        form.VisibleChanged += (_, _) =>
        {
            if (!form.Visible)
            {
                context.ExitThread();
            }
        };
        // States first, as the tray app does, so the window sizes itself to real content.
        form.SetStates(states, isRefreshing: false, lastRefreshed: lastRefreshed, history: history);
        form.ShowNearTray(mode);
        Application.Run(context);
    }

    /// <summary>
    /// Runs the real tray app on the sample providers — tray icon, tooltip, popup, dashboard,
    /// Settings and alerts — with no provider access, update check or startup registration. The
    /// tray starts on All connected, and each Refresh the user asks for steps the readings to the
    /// next <see cref="PreviewTrayStages"/> stage, so the tank can be filmed filling and changing
    /// colour; automatic polls and the refreshes after sleep or unlock leave the stage alone. It
    /// never answers a relaunch, which belongs to an installed copy. Exit in the tray menu ends it.
    /// </summary>
    public static void ShowTray(string[] args)
    {
        UseInvariantCulture();
        var settings = PreviewSettings(args);
        settings.TrayProviderId = AppSettings.AllProvidersTrayId;
        var stages = new PreviewTrayStages(settings);
        var (states, _, _) = SampleData(DateTimeOffset.Now);

        // Staggered answers, so the spinner turns and providers land one at a time as they do live.
        var clients = states
            .Select((state, index) => new PreviewUsageClient(state, stages, TimeSpan.FromMilliseconds(400 + 300 * index)))
            .ToArray();
        using var context = new UsageApplicationContext(
            clients,
            settings,
            showTrayIcon: true,
            checkForUpdates: static _ => Task.FromResult(new UpdateCheckResult(true, null)),
            readStartup: static () => new StartupState(StartupStatus.Enabled, null),
            writeStartup: static _ => { },
            answerRelaunch: false);
        context.UserRefreshRequested += (_, _) => stages.Advance();
        Application.Run(context);
    }

    /// <summary>The busiest metered limit in the sample data, which the tray stages scale from.</summary>
    internal static int BusiestSample(IReadOnlyList<ProviderStatus> states) => states
        .Where(state => state.Snapshot is not null)
        .SelectMany(state => state.Snapshot!.Metrics)
        .Where(metric => metric.HasQuota)
        .Max(metric => metric.UsedPercent!.Value);

    /// <summary>
    /// <paramref name="snapshot"/> with every metered limit at <paramref name="share"/> of its
    /// sample reading, up to 100%. A changed limit drops its usage text, which stated the
    /// sample's figures.
    /// </summary>
    internal static UsageSnapshot Scale(UsageSnapshot snapshot, double share, DateTimeOffset fetchedAt) => snapshot with
    {
        FetchedAt = fetchedAt,
        Metrics = snapshot.Metrics
            .Select(metric =>
            {
                if (!metric.HasQuota)
                {
                    return metric;
                }

                var scaled = Math.Clamp((int)Math.Round(metric.UsedPercent!.Value * share), 0, 100);
                return scaled == metric.UsedPercent ? metric : metric with { UsedPercent = scaled, UsageText = null };
            })
            .ToArray(),
    };

    /// <summary>
    /// The What's New window as an upgrade across up to the two newest bundled releases shows it:
    /// from the release before them, or from nothing when there are only two.
    /// </summary>
    private static void ShowWhatsNew()
    {
        var releases = ReleaseNotes.LoadBundled();
        var summary = releases.Count == 0
            ? new WhatsNewSummary(releases, AppIdentity.Version, 0)
            : ReleaseNotes.ForUpgrade(
                releases,
                releases[0].DisplayVersion,
                releases.Count > 2 ? releases[2].Version : new Version(0, 0));
        using var form = new WhatsNewForm(summary);
        Application.Run(form);
    }

    /// <summary>
    /// The tray menu, opened at the cursor as the tray would open it. Choosing an item or
    /// dismissing the menu ends the process.
    /// </summary>
    private static void ShowMenu()
    {
        using var context = new ApplicationContext();
        using var menu = TrayMenu.Create(
            open: context.ExitThread,
            refresh: context.ExitThread,
            settings: context.ExitThread,
            exit: context.ExitThread);
        menu.Closed += (_, _) => context.ExitThread();
        menu.Show(Cursor.Position);
        Application.Run(context);
    }

    /// <summary>
    /// The real Settings dialog with the sample providers' model groups. Startup registration and
    /// the update check are stubbed, so the preview never touches the Run key or the network.
    /// </summary>
    private static void ShowSettings(AppSettings settings)
    {
        var (states, _, _) = SampleData(DateTimeOffset.Now);
        using var dialog = new SettingsForm(
            settings,
            states.Select(state => (state.ProviderId, state.ProviderName)).ToArray(),
            static _ => Task.FromResult(new UpdateCheckResult(true, null)),
            static _ => Task.CompletedTask,
            readStartup: static () => new StartupState(StartupStatus.Enabled, null),
            writeStartup: static _ => { },
            popupGroups: UsageApplicationContext.PopupGroups(states));
        Application.Run(dialog);
    }

    /// <summary>
    /// Redirects <see cref="AppPaths"/> to a fresh folder under the temp directory. It must run
    /// before anything reads <see cref="AppPaths.DataDirectory"/>, which resolves once.
    /// </summary>
    internal static void IsolateDataDirectory() =>
        Environment.SetEnvironmentVariable(
            "USAGEAI_DATA_DIR",
            Path.Combine(Path.GetTempPath(), "UsageAI.Preview", Guid.NewGuid().ToString("N")));

    private static AppSettings PreviewSettings(string[] args)
    {
        var theme = Program.ValueAfter(args, "--theme")?.ToLowerInvariant() switch
        {
            "light" => ThemeMode.Light,
            "dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        var settings = new AppSettings { QuotaDisplay = ResolveQuotaDisplay(args), Theme = theme, IsEphemeral = true };
        Theme.Apply(settings.Theme, settings.WarningPercent, settings.CriticalPercent);
        return settings;
    }

    private static void UseInvariantCulture()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
    }

    /// <summary>The same providers with every metered limit 17 points busier, capped at 100%.</summary>
    internal static ProviderStatus[] Busier(ProviderStatus[] states) => states
        .Select(state => state.Snapshot is not { } snapshot
            ? state
            : state with
            {
                Snapshot = snapshot with
                {
                    Metrics = snapshot.Metrics
                        .Select(metric => metric.HasQuota ? metric with { UsedPercent = Math.Min(100, metric.UsedPercent!.Value + 17) } : metric)
                        .ToArray(),
                },
            })
        .ToArray();

    /// <summary>
    /// The sample providers in every non-happy state the windows must handle: signed out with a
    /// copyable sign-in command, still loading, stale with an error, and connected with only
    /// unlimited allowances. The popup lists connected providers only, so it shows the last two;
    /// the dashboard shows all four.
    /// </summary>
    internal static ProviderStatus[] MixedStates(ProviderStatus[] states, DateTimeOffset now)
    {
        var codex = states[0] with
        {
            Snapshot = null,
            Error = "Codex CLI was not found. Install Codex or sign in through the Codex editor extension, then refresh.",
            LastUpdated = null,
        };
        var claude = states[1] with { Snapshot = null, Error = null, IsLoading = true, LastUpdated = null };
        var copilot = states[2] with
        {
            Error = "GitHub did not respond. UsageAI will try again.",
            LastUpdated = now.AddHours(-2),
            NextRetryAt = now.AddMinutes(4),
        };
        var unlimited = states[3].Snapshot! with
        {
            Metrics = states[2].Snapshot!.Metrics.Where(metric => metric.IsUnlimited).ToArray(),
        };
        var gemini = states[3] with { Snapshot = unlimited, ProviderId = "gemini" };
        return new[] { codex, claude, copilot, gemini };
    }

    /// <summary>
    /// Representative provider states and history for previews: one limit at each severity, a
    /// balance, unlimited metrics, and a provider with two model groups.
    /// </summary>
    internal static (ProviderStatus[] States, UsageSample[] History, DateTimeOffset LastRefreshed) SampleData(DateTimeOffset now)
    {
        var lastRefreshed = now.AddMinutes(-7);

        var states = new[]
        {
            new ProviderStatus(
                "codex",
                "Codex",
                new UsageSnapshot(
                    "Plus",
                    new[]
                    {
                        new UsageMetric("Weekly", UsageMetricKind.Rolling, 92, now.AddDays(2).AddHours(18), 10_080),
                        new UsageMetric(
                            "Credits",
                            UsageMetricKind.Balance,
                            null,
                            RemainingText: "$12.50",
                            UsageText: "Available account balance"),
                        new UsageMetric(
                            "Reset credits",
                            UsageMetricKind.Balance,
                            null,
                            RemainingText: "1",
                            UsageText: "Full reset available"),
                    },
                    lastRefreshed,
                    "codex",
                    "Codex"),
                null,
                false,
                lastRefreshed,
                "codex login",
                new Uri("https://chatgpt.com/codex/settings/usage")),

            new ProviderStatus(
                "claude",
                "Claude Code",
                new UsageSnapshot(
                    "Claude Pro",
                    new[]
                    {
                        new UsageMetric("5-hour", UsageMetricKind.Session, 34, now.AddHours(3).AddMinutes(12), 300),
                        new UsageMetric("Weekly", UsageMetricKind.Rolling, 78, now.AddDays(4).AddHours(8), 10_080),
                    },
                    lastRefreshed,
                    "claude",
                    "Claude Code"),
                null,
                false,
                lastRefreshed,
                "claude",
                new Uri("https://claude.ai/settings/usage")),

            new ProviderStatus(
                "copilot",
                "GitHub Copilot",
                new UsageSnapshot(
                    "Pro",
                    new[]
                    {
                        new UsageMetric(
                            "AI credits",
                            UsageMetricKind.Monthly,
                            64,
                            now.AddDays(18).AddHours(4),
                            null,
                            UsageText: "540 of 1,500 left"),
                        new UsageMetric(
                            "Chat",
                            UsageMetricKind.Monthly,
                            0,
                            now.AddDays(18).AddHours(4),
                            null,
                            "UNLIMITED",
                            "No monthly limit",
                            IsUnlimited: true),
                        new UsageMetric(
                            "Completions",
                            UsageMetricKind.Monthly,
                            0,
                            now.AddDays(18).AddHours(4),
                            null,
                            "UNLIMITED",
                            "No monthly limit",
                            IsUnlimited: true),
                    },
                    lastRefreshed,
                    "copilot",
                    "GitHub Copilot",
                    "octocat"),
                null,
                false,
                lastRefreshed,
                "copilot",
                new Uri("https://github.com/settings/copilot/features")),

            new ProviderStatus(
                "gemini",
                "Google Gemini",
                new UsageSnapshot(
                    "Google AI Pro",
                    new[]
                    {
                        new UsageMetric("Gemini Models (5-hour)", UsageMetricKind.Session, 14, now.AddHours(4).AddMinutes(10), 300),
                        new UsageMetric("Gemini Models (Weekly)", UsageMetricKind.Rolling, 48, now.AddDays(5).AddHours(14), 10_080),
                        new UsageMetric("Claude and GPT models (5-hour)", UsageMetricKind.Session, 94, now.AddHours(2).AddMinutes(30), 300),
                        new UsageMetric("Claude and GPT models (Weekly)", UsageMetricKind.Rolling, 83, now.AddDays(4).AddHours(18), 10_080),
                    },
                    lastRefreshed,
                    "gemini",
                    "Google Gemini",
                    AccountName: "user@example.com"),
                null,
                false,
                lastRefreshed,
                "gemini",
                new Uri("https://aistudio.google.com/")),
        };

        var history = new[]
        {
            new UsageSample(now.AddHours(-6.0), "codex", "Rolling:Weekly", 80),
            new UsageSample(now.AddHours(-4.0), "codex", "Rolling:Weekly", 84),
            new UsageSample(now.AddHours(-2.0), "codex", "Rolling:Weekly", 88),
            new UsageSample(now.AddHours(-1.0), "codex", "Rolling:Weekly", 90),
            new UsageSample(lastRefreshed, "codex", "Rolling:Weekly", 92),

            new UsageSample(now.AddHours(-4.5), "claude", "Session:5-hour", 20),
            new UsageSample(now.AddHours(-4.0), "claude", "Session:5-hour", 35),
            new UsageSample(now.AddHours(-3.5), "claude", "Session:5-hour", 48),
            new UsageSample(now.AddHours(-3.0), "claude", "Session:5-hour", 12),
            new UsageSample(now.AddHours(-2.5), "claude", "Session:5-hour", 28),
            new UsageSample(now.AddHours(-2.0), "claude", "Session:5-hour", 42),
            new UsageSample(now.AddHours(-1.5), "claude", "Session:5-hour", 15),
            new UsageSample(now.AddHours(-1.0), "claude", "Session:5-hour", 25),
            new UsageSample(now.AddHours(-0.5), "claude", "Session:5-hour", 30),
            new UsageSample(lastRefreshed, "claude", "Session:5-hour", 34),

            new UsageSample(now.AddDays(-6.0), "claude", "Rolling:Weekly", 15),
            new UsageSample(now.AddDays(-5.0), "claude", "Rolling:Weekly", 28),
            new UsageSample(now.AddDays(-4.0), "claude", "Rolling:Weekly", 42),
            new UsageSample(now.AddDays(-3.0), "claude", "Rolling:Weekly", 58),
            new UsageSample(now.AddDays(-2.0), "claude", "Rolling:Weekly", 68),
            new UsageSample(now.AddDays(-1.0), "claude", "Rolling:Weekly", 74),
            new UsageSample(lastRefreshed, "claude", "Rolling:Weekly", 78),

            new UsageSample(now.AddDays(-15.0), "copilot", "Monthly:AI credits", 20),
            new UsageSample(now.AddDays(-10.0), "copilot", "Monthly:AI credits", 38),
            new UsageSample(now.AddDays(-5.0), "copilot", "Monthly:AI credits", 52),
            new UsageSample(now.AddDays(-2.0), "copilot", "Monthly:AI credits", 60),
            new UsageSample(lastRefreshed, "copilot", "Monthly:AI credits", 64),

            new UsageSample(now.AddHours(-4.0), "gemini", "Session:Gemini Models (5-hour)", 2),
            new UsageSample(now.AddHours(-3.0), "gemini", "Session:Gemini Models (5-hour)", 8),
            new UsageSample(now.AddHours(-2.0), "gemini", "Session:Gemini Models (5-hour)", 15),
            new UsageSample(now.AddHours(-1.0), "gemini", "Session:Gemini Models (5-hour)", 11),
            new UsageSample(lastRefreshed, "gemini", "Session:Gemini Models (5-hour)", 14),

            new UsageSample(now.AddDays(-6.0), "gemini", "Rolling:Gemini Models (Weekly)", 10),
            new UsageSample(now.AddDays(-4.0), "gemini", "Rolling:Gemini Models (Weekly)", 24),
            new UsageSample(now.AddDays(-2.0), "gemini", "Rolling:Gemini Models (Weekly)", 36),
            new UsageSample(now.AddDays(-1.0), "gemini", "Rolling:Gemini Models (Weekly)", 44),
            new UsageSample(lastRefreshed, "gemini", "Rolling:Gemini Models (Weekly)", 48),

            new UsageSample(now.AddHours(-4.5), "gemini", "Session:Claude and GPT models (5-hour)", 15),
            new UsageSample(now.AddHours(-4.0), "gemini", "Session:Claude and GPT models (5-hour)", 40),
            new UsageSample(now.AddHours(-3.5), "gemini", "Session:Claude and GPT models (5-hour)", 85),
            new UsageSample(now.AddHours(-3.0), "gemini", "Session:Claude and GPT models (5-hour)", 25),
            new UsageSample(now.AddHours(-2.5), "gemini", "Session:Claude and GPT models (5-hour)", 52),
            new UsageSample(now.AddHours(-2.0), "gemini", "Session:Claude and GPT models (5-hour)", 74),
            new UsageSample(now.AddHours(-1.5), "gemini", "Session:Claude and GPT models (5-hour)", 95),
            new UsageSample(now.AddHours(-1.0), "gemini", "Session:Claude and GPT models (5-hour)", 90),
            new UsageSample(now.AddHours(-0.5), "gemini", "Session:Claude and GPT models (5-hour)", 96),
            new UsageSample(lastRefreshed, "gemini", "Session:Claude and GPT models (5-hour)", 94),

            new UsageSample(now.AddDays(-6.0), "gemini", "Rolling:Claude and GPT models (Weekly)", 18),
            new UsageSample(now.AddDays(-5.0), "gemini", "Rolling:Claude and GPT models (Weekly)", 32),
            new UsageSample(now.AddDays(-4.0), "gemini", "Rolling:Claude and GPT models (Weekly)", 46),
            new UsageSample(now.AddDays(-3.0), "gemini", "Rolling:Claude and GPT models (Weekly)", 62),
            new UsageSample(now.AddDays(-2.0), "gemini", "Rolling:Claude and GPT models (Weekly)", 74),
            new UsageSample(now.AddDays(-1.0), "gemini", "Rolling:Claude and GPT models (Weekly)", 80),
            new UsageSample(lastRefreshed, "gemini", "Rolling:Claude and GPT models (Weekly)", 83),
        };

        return (states, history, lastRefreshed);
    }

    /// <summary>
    /// Windows will not make a window taller than the screen, so a dashboard whose table is
    /// taller is drawn in two parts: the window for its header and footer, and the table on its
    /// own at its full height, put where the content area was.
    /// </summary>
    private static Bitmap ComposeWholeTable(UsagePopupForm form, Bitmap window)
    {
        if (form.PreviewTable is not ({ } content, { } table) || table.NaturalHeight <= content.Height)
        {
            return new Bitmap(window);
        }

        // Detached first: in the window the layout keeps it narrowed for the scrollbar.
        // It keeps the panel's background, which it otherwise inherited.
        table.BackColor = table.Parent?.BackColor ?? table.BackColor;
        table.Parent?.Controls.Remove(table);
        // As wide as the window laid it out, which leaves the dashboard's right margin.
        table.Size = new Size(table.Width, table.NaturalHeight);
        table.CreateControl();
        using var tableImage = new Bitmap(table.Width, table.Height);
        table.DrawToBitmap(tableImage, new Rectangle(Point.Empty, tableImage.Size));
        var extra = table.Height - content.Height;
        var image = new Bitmap(window.Width, window.Height + extra);
        using var graphics = Graphics.FromImage(image);
        var above = new Rectangle(0, 0, window.Width, content.Top);
        var below = Rectangle.FromLTRB(0, content.Bottom, window.Width, window.Height);
        graphics.DrawImage(window, above, above, GraphicsUnit.Pixel);
        // The window's sides beside the content area: its background and border.
        graphics.DrawImage(window, new Rectangle(0, content.Top, window.Width, table.Height), new Rectangle(0, content.Top, window.Width, 1), GraphicsUnit.Pixel);
        graphics.DrawImage(tableImage, content.Left, content.Top, tableImage.Width, tableImage.Height);
        graphics.DrawImage(window, new Rectangle(0, content.Bottom + extra, window.Width, below.Height), below, GraphicsUnit.Pixel);
        return image;
    }

    /// <summary>
    /// Labels and buttons keep point-sized fonts, which WinForms enlarges for a real monitor's
    /// DPI. Drawn off screen at another DPI nothing enlarges them, so each font a control sets
    /// itself is scaled here from the DPI GDI realises points at to the preview's.
    /// </summary>
    private static void ScaleControlFonts(Control root, int previewDpi)
    {
        using var screen = Graphics.FromHwnd(IntPtr.Zero);
        var factor = previewDpi / screen.DpiY;
        if (Math.Abs(factor - 1F) < 0.01F)
        {
            return;
        }

        void Scale(Control control)
        {
            // An inherited font is the parent's own object; it follows the parent's change.
            if (control.Parent is null || !ReferenceEquals(control.Font, control.Parent.Font))
            {
                var font = control.Font;
                if (font.Unit != GraphicsUnit.Pixel)
                {
                    control.Font = new Font(font.FontFamily, font.SizeInPoints * factor, font.Style, GraphicsUnit.Point);
                }
            }

            foreach (Control child in control.Controls)
            {
                Scale(child);
            }
        }

        Scale(root);
    }

    private static string ResolveOutputPath(string[] args) =>
        Program.ValueAfter(args, "--render-preview") is { } path
            ? Path.GetFullPath(path)
            : Path.Combine(Environment.CurrentDirectory, "usageai-preview.png");

    private static int ResolveDpi(string[] args) =>
        int.TryParse(Program.ValueAfter(args, "--dpi"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dpi) &&
        dpi is 96 or 192 or 288
            ? dpi
            : 192;

    private static QuotaDisplay ResolveQuotaDisplay(string[] args) =>
        string.Equals(Program.ValueAfter(args, "--quota-display"), "remaining", StringComparison.OrdinalIgnoreCase)
            ? QuotaDisplay.Remaining
            : QuotaDisplay.Used;
}

/// <summary>
/// The tray preview's script: the stage every sample provider reads at, which only a Refresh the
/// user asks for advances. Each stage puts the busiest sample limit at a target taken from the
/// current thresholds: calm, between warning and critical, then past critical. Thresholds changed
/// in the preview's Settings therefore still produce all three states.
/// </summary>
internal sealed class PreviewTrayStages
{
    public const int Count = 3;

    private readonly AppSettings _settings;

    public PreviewTrayStages(AppSettings settings) => _settings = settings;

    public int Stage { get; private set; }

    /// <summary>The busiest limit's reading at the current stage.</summary>
    public int Target => Targets(_settings.WarningPercent, _settings.CriticalPercent)[Stage];

    public void Advance() => Stage = (Stage + 1) % Count;

    /// <summary>Settings keep critical above warning, so the middle target lands between them.</summary>
    internal static int[] Targets(int warningPercent, int criticalPercent) => new[]
    {
        Math.Max(1, warningPercent / 2),
        (warningPercent + criticalPercent) / 2,
        Math.Min(100, Math.Max(criticalPercent + 4, 94)),
    };
}

/// <summary>
/// One sample provider for the tray preview, reading at the current <see cref="PreviewTrayStages"/>
/// stage. The sample is rebuilt on every poll, so its reset times stay ahead of the clock however
/// long the preview runs.
/// </summary>
internal sealed class PreviewUsageClient : IUsageClient
{
    private readonly ProviderStatus _sample;
    private readonly PreviewTrayStages _stages;
    private readonly TimeSpan _latency;

    public PreviewUsageClient(ProviderStatus sample, PreviewTrayStages stages, TimeSpan latency)
    {
        _sample = sample;
        _stages = stages;
        _latency = latency;
    }

    public string Id => _sample.ProviderId;

    public string DisplayName => _sample.ProviderName;

    public string SignInCommand => _sample.SignInCommand ?? _sample.ProviderId;

    public Uri AccountUrl => _sample.AccountUrl ?? new Uri("https://example.com/");

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.Now;
        var (states, _, _) = PreviewRenderer.SampleData(now);
        var sample = states.First(state => state.ProviderId.Equals(Id, StringComparison.OrdinalIgnoreCase));
        var share = (double)_stages.Target / PreviewRenderer.BusiestSample(states);
        return PreviewRenderer.Scale(sample.Snapshot!, share, now);
    }
}
