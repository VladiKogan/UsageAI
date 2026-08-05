using System.Drawing.Drawing2D;
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
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

        var outputPath = ResolveOutputPath(args);
        var now = DateTimeOffset.Now;
        var settings = new AppSettings();
        Theme.Apply(settings.Theme, settings.WarningPercent, settings.CriticalPercent);

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
                        new UsageMetric("WEEKLY", UsageMetricKind.Rolling, 92, now.AddDays(2).AddHours(18), 10_080),
                        new UsageMetric(
                            "CREDITS",
                            UsageMetricKind.Balance,
                            null,
                            RemainingText: "$12.50",
                            UsageText: "Available account balance"),
                        new UsageMetric(
                            "RESET CREDITS",
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
                        new UsageMetric("5-HOUR", UsageMetricKind.Session, 34, now.AddHours(3).AddMinutes(12), 300),
                        new UsageMetric("WEEKLY", UsageMetricKind.Rolling, 78, now.AddDays(4).AddHours(8), 10_080),
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
                            "AI CREDITS",
                            UsageMetricKind.Monthly,
                            64,
                            now.AddDays(18).AddHours(4),
                            null,
                            UsageText: "540 of 1,500 left"),
                        new UsageMetric(
                            "CHAT",
                            UsageMetricKind.Monthly,
                            0,
                            now.AddDays(18).AddHours(4),
                            null,
                            "UNLIMITED",
                            "No monthly limit",
                            IsUnlimited: true),
                        new UsageMetric(
                            "COMPLETIONS",
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
                        new UsageMetric("GEMINI MODELS (5-HOUR)", UsageMetricKind.Session, 14, now.AddHours(4).AddMinutes(10), 300),
                        new UsageMetric("GEMINI MODELS (WEEKLY)", UsageMetricKind.Rolling, 48, now.AddDays(5).AddHours(14), 10_080),
                        new UsageMetric("CLAUDE AND GPT MODELS (5-HOUR)", UsageMetricKind.Session, 94, now.AddHours(2).AddMinutes(30), 300),
                        new UsageMetric("CLAUDE AND GPT MODELS (WEEKLY)", UsageMetricKind.Rolling, 83, now.AddDays(4).AddHours(18), 10_080),
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
            // Codex WEEKLY (Red)
            new UsageSample(now.AddHours(-6.0), "codex", "Rolling:WEEKLY", 80),
            new UsageSample(now.AddHours(-4.0), "codex", "Rolling:WEEKLY", 84),
            new UsageSample(now.AddHours(-2.0), "codex", "Rolling:WEEKLY", 88),
            new UsageSample(now.AddHours(-1.0), "codex", "Rolling:WEEKLY", 90),
            new UsageSample(lastRefreshed, "codex", "Rolling:WEEKLY", 92),

            // Claude 5-HOUR (Green - showing session reset drops & peaks like screenshot)
            new UsageSample(now.AddHours(-4.5), "claude", "Session:5-HOUR", 20),
            new UsageSample(now.AddHours(-4.0), "claude", "Session:5-HOUR", 35),
            new UsageSample(now.AddHours(-3.5), "claude", "Session:5-HOUR", 48),
            new UsageSample(now.AddHours(-3.0), "claude", "Session:5-HOUR", 12),
            new UsageSample(now.AddHours(-2.5), "claude", "Session:5-HOUR", 28),
            new UsageSample(now.AddHours(-2.0), "claude", "Session:5-HOUR", 42),
            new UsageSample(now.AddHours(-1.5), "claude", "Session:5-HOUR", 15),
            new UsageSample(now.AddHours(-1.0), "claude", "Session:5-HOUR", 25),
            new UsageSample(now.AddHours(-0.5), "claude", "Session:5-HOUR", 30),
            new UsageSample(lastRefreshed, "claude", "Session:5-HOUR", 34),

            // Claude WEEKLY (Amber)
            new UsageSample(now.AddDays(-6.0), "claude", "Rolling:WEEKLY", 15),
            new UsageSample(now.AddDays(-5.0), "claude", "Rolling:WEEKLY", 28),
            new UsageSample(now.AddDays(-4.0), "claude", "Rolling:WEEKLY", 42),
            new UsageSample(now.AddDays(-3.0), "claude", "Rolling:WEEKLY", 58),
            new UsageSample(now.AddDays(-2.0), "claude", "Rolling:WEEKLY", 68),
            new UsageSample(now.AddDays(-1.0), "claude", "Rolling:WEEKLY", 74),
            new UsageSample(lastRefreshed, "claude", "Rolling:WEEKLY", 78),

            // Copilot AI CREDITS (Blue)
            new UsageSample(now.AddDays(-15.0), "copilot", "Monthly:AI CREDITS", 20),
            new UsageSample(now.AddDays(-10.0), "copilot", "Monthly:AI CREDITS", 38),
            new UsageSample(now.AddDays(-5.0), "copilot", "Monthly:AI CREDITS", 52),
            new UsageSample(now.AddDays(-2.0), "copilot", "Monthly:AI CREDITS", 60),
            new UsageSample(lastRefreshed, "copilot", "Monthly:AI CREDITS", 64),

            // Gemini GEMINI MODELS (5-HOUR) (Green)
            new UsageSample(now.AddHours(-4.0), "gemini", "Session:GEMINI MODELS (5-HOUR)", 2),
            new UsageSample(now.AddHours(-3.0), "gemini", "Session:GEMINI MODELS (5-HOUR)", 8),
            new UsageSample(now.AddHours(-2.0), "gemini", "Session:GEMINI MODELS (5-HOUR)", 15),
            new UsageSample(now.AddHours(-1.0), "gemini", "Session:GEMINI MODELS (5-HOUR)", 11),
            new UsageSample(lastRefreshed, "gemini", "Session:GEMINI MODELS (5-HOUR)", 14),

            // Gemini GEMINI MODELS (WEEKLY) (Green)
            new UsageSample(now.AddDays(-6.0), "gemini", "Rolling:GEMINI MODELS (WEEKLY)", 10),
            new UsageSample(now.AddDays(-4.0), "gemini", "Rolling:GEMINI MODELS (WEEKLY)", 24),
            new UsageSample(now.AddDays(-2.0), "gemini", "Rolling:GEMINI MODELS (WEEKLY)", 36),
            new UsageSample(now.AddDays(-1.0), "gemini", "Rolling:GEMINI MODELS (WEEKLY)", 44),
            new UsageSample(lastRefreshed, "gemini", "Rolling:GEMINI MODELS (WEEKLY)", 48),

            // Gemini CLAUDE AND GPT MODELS (5-HOUR) (Red - showing multi-spike session fluctuations)
            new UsageSample(now.AddHours(-4.5), "gemini", "Session:CLAUDE AND GPT MODELS (5-HOUR)", 15),
            new UsageSample(now.AddHours(-4.0), "gemini", "Session:CLAUDE AND GPT MODELS (5-HOUR)", 40),
            new UsageSample(now.AddHours(-3.5), "gemini", "Session:CLAUDE AND GPT MODELS (5-HOUR)", 85),
            new UsageSample(now.AddHours(-3.0), "gemini", "Session:CLAUDE AND GPT MODELS (5-HOUR)", 25),
            new UsageSample(now.AddHours(-2.5), "gemini", "Session:CLAUDE AND GPT MODELS (5-HOUR)", 52),
            new UsageSample(now.AddHours(-2.0), "gemini", "Session:CLAUDE AND GPT MODELS (5-HOUR)", 74),
            new UsageSample(now.AddHours(-1.5), "gemini", "Session:CLAUDE AND GPT MODELS (5-HOUR)", 95),
            new UsageSample(now.AddHours(-1.0), "gemini", "Session:CLAUDE AND GPT MODELS (5-HOUR)", 90),
            new UsageSample(now.AddHours(-0.5), "gemini", "Session:CLAUDE AND GPT MODELS (5-HOUR)", 96),
            new UsageSample(lastRefreshed, "gemini", "Session:CLAUDE AND GPT MODELS (5-HOUR)", 94),

            // Gemini CLAUDE AND GPT MODELS (WEEKLY) (Amber)
            new UsageSample(now.AddDays(-6.0), "gemini", "Rolling:CLAUDE AND GPT MODELS (WEEKLY)", 18),
            new UsageSample(now.AddDays(-5.0), "gemini", "Rolling:CLAUDE AND GPT MODELS (WEEKLY)", 32),
            new UsageSample(now.AddDays(-4.0), "gemini", "Rolling:CLAUDE AND GPT MODELS (WEEKLY)", 46),
            new UsageSample(now.AddDays(-3.0), "gemini", "Rolling:CLAUDE AND GPT MODELS (WEEKLY)", 62),
            new UsageSample(now.AddDays(-2.0), "gemini", "Rolling:CLAUDE AND GPT MODELS (WEEKLY)", 74),
            new UsageSample(now.AddDays(-1.0), "gemini", "Rolling:CLAUDE AND GPT MODELS (WEEKLY)", 80),
            new UsageSample(lastRefreshed, "gemini", "Rolling:CLAUDE AND GPT MODELS (WEEKLY)", 83),
        };

        using var form = new UsagePopupForm(settings);
        var isFull = args.Contains("--full", StringComparer.OrdinalIgnoreCase);
        var mode = isFull ? DashboardMode.Full : DashboardMode.Compact;
        form.ShowNearTray(mode);
        form.SetStates(states, isRefreshing: false, lastRefreshed: lastRefreshed, history: history);
        if (isFull)
        {
            form.FormBorderStyle = FormBorderStyle.None;
            form.ClientSize = new Size(940, 930);
        }
        else
        {
            var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            form.ApplyPreferredSize(workingArea);
        }
        form.Location = new Point(-10_000, -10_000);
        Application.DoEvents();
        form.PerformLayout();
        using var lowResBitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(lowResBitmap, form.ClientRectangle);

        const float scaleFactor = 2F;
        var highWidth = (int)Math.Round(form.ClientSize.Width * scaleFactor);
        var highHeight = (int)Math.Round(form.ClientSize.Height * scaleFactor);

        using var highResBitmap = new Bitmap(highWidth, highHeight);
        using (var graphics = Graphics.FromImage(highResBitmap))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.DrawImage(lowResBitmap, new Rectangle(0, 0, highWidth, highHeight));
        }

        highResBitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
        form.Hide();
    }

    private static string ResolveOutputPath(string[] args)
    {
        var flagIndex = Array.FindIndex(args, argument =>
            argument.Equals("--render-preview", StringComparison.OrdinalIgnoreCase));
        return flagIndex >= 0 &&
               flagIndex + 1 < args.Length &&
               !args[flagIndex + 1].StartsWith('-')
            ? Path.GetFullPath(args[flagIndex + 1])
            : Path.Combine(Environment.CurrentDirectory, "usageai-preview.png");
    }
}
