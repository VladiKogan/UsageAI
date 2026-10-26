using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
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
        using var dpiScope = PreviewDpiScope.EnterBaseline();
        UseInvariantCulture();

        var outputPath = ResolveOutputPath(args);
        var previewDpi = ResolveDpi(args);
        var settings = PreviewSettings(args);
        var (states, history, lastRefreshed) = SampleData(DateTimeOffset.Now);

        using var form = new UsagePopupForm(settings, LayoutScale.BaselineDpi);
        var isFull = args.Contains("--full", StringComparer.OrdinalIgnoreCase);
        var mode = isFull ? DashboardMode.Full : DashboardMode.Compact;
        form.ShowNearTray(mode);
        form.SetStates(states, isRefreshing: false, lastRefreshed: lastRefreshed, history: history);
        if (isFull)
        {
            form.FormBorderStyle = FormBorderStyle.None;
            var scale = new LayoutScale(LayoutScale.BaselineDpi);
            form.ClientSize = new Size(scale[940], scale[930]);
        }
        else
        {
            var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            form.ApplyPreferredSize(workingArea);
        }
        form.Location = new Point(-10_000, -10_000);
        Application.DoEvents();
        form.PerformLayout();
        using var baselineBitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        baselineBitmap.SetResolution(LayoutScale.BaselineDpi, LayoutScale.BaselineDpi);
        form.DrawToBitmap(baselineBitmap, form.ClientRectangle);
        SaveScaled(baselineBitmap, outputPath, previewDpi);
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

        form.RefreshRequested += async (_, _) =>
        {
            form.SetStates(states, isRefreshing: true, lastRefreshed: lastRefreshed, history: history);
            await Task.Delay(900);
            if (!form.IsDisposed)
            {
                form.SetStates(states, isRefreshing: false, lastRefreshed: DateTimeOffset.Now, history: history);
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

    private static void SaveScaled(Bitmap baseline, string outputPath, int outputDpi)
    {
        if (outputDpi == LayoutScale.BaselineDpi)
        {
            baseline.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
            return;
        }

        var factor = outputDpi / (float)LayoutScale.BaselineDpi;
        using var scaled = new Bitmap(
            (int)Math.Round(baseline.Width * factor),
            (int)Math.Round(baseline.Height * factor));
        scaled.SetResolution(outputDpi, outputDpi);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.DrawImage(baseline, new Rectangle(Point.Empty, scaled.Size));
        }

        scaled.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
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

    /// <summary>
    /// Preview composition starts from a virtual 96-DPI surface and is then resampled as one image.
    /// This keeps point-sized fonts and pixel geometry in lockstep on every host monitor scale.
    /// </summary>
    private readonly struct PreviewDpiScope : IDisposable
    {
        private static readonly nint DpiAwarenessContextUnaware = -1;
        private readonly nint _previousContext;

        private PreviewDpiScope(nint previousContext) => _previousContext = previousContext;

        public static PreviewDpiScope EnterBaseline() =>
            new(SetThreadDpiAwarenessContext(DpiAwarenessContextUnaware));

        public void Dispose()
        {
            if (_previousContext != 0)
            {
                _ = SetThreadDpiAwarenessContext(_previousContext);
            }
        }

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern nint SetThreadDpiAwarenessContext(nint dpiContext);
    }
}
