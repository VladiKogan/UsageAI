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
        var outputPath = ResolveOutputPath(args);
        var now = DateTimeOffset.Now;
        var settings = new AppSettings();
        Theme.Apply(settings.Theme, settings.WarningPercent, settings.CriticalPercent);

        var states = new[]
        {
            new ProviderStatus(
                "codex",
                "Codex",
                new UsageSnapshot(
                    "Plus",
                    new[]
                    {
                        new UsageMetric("5-hour", UsageMetricKind.Session, 43, now.AddHours(2).AddMinutes(18), 300),
                        new UsageMetric("Weekly", UsageMetricKind.Rolling, 63, now.AddDays(3).AddHours(8), 10_080),
                        new UsageMetric(
                            "Credits",
                            UsageMetricKind.Balance,
                            null,
                            RemainingText: "$12.50",
                            UsageText: "Available account balance"),
                    },
                    now,
                    "codex",
                    "Codex"),
                null,
                false,
                now,
                "codex login",
                new Uri("https://chatgpt.com/codex/settings/usage")),
            new ProviderStatus(
                "claude",
                "Claude Code",
                new UsageSnapshot(
                    "Claude Max 20x",
                    new[]
                    {
                        new UsageMetric("5-hour", UsageMetricKind.Session, 22, now.AddHours(3).AddMinutes(41), 300),
                        new UsageMetric("Weekly", UsageMetricKind.Rolling, 49, now.AddDays(4).AddHours(12), 10_080),
                    },
                    now,
                    "claude",
                    "Claude Code",
                    "developer@example.com"),
                null,
                false,
                now,
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
                            76,
                            now.AddDays(9),
                            null,
                            "24% LEFT",
                            "72 of 300 left"),
                        new UsageMetric(
                            "Chat",
                            UsageMetricKind.Monthly,
                            0,
                            now.AddDays(9),
                            null,
                            "UNLIMITED",
                            "No monthly limit",
                            IsUnlimited: true),
                    },
                    now,
                    "copilot",
                    "GitHub Copilot",
                    "octocat"),
                null,
                false,
                now,
                "copilot",
                new Uri("https://github.com/settings/copilot/features")),
        };

        using var form = new UsagePopupForm(settings);
        form.SetMode(args.Contains("--full", StringComparer.OrdinalIgnoreCase)
            ? DashboardMode.Full
            : DashboardMode.Compact);
        form.SetStates(states, isRefreshing: false, lastRefreshed: now, history: Array.Empty<UsageSample>());
        form.Location = new Point(-10_000, -10_000);
        form.Show();
        Application.DoEvents();
        form.PerformLayout();
        using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(bitmap, form.ClientRectangle);
        bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
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
