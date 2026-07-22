using UsageAI.Services;
using UsageAI.UI;

namespace UsageAI;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Contains("--render-preview", StringComparer.OrdinalIgnoreCase))
        {
            RenderPreview(args);
            return;
        }

        if (args.Contains("--diagnose", StringComparer.OrdinalIgnoreCase))
        {
            RunDiagnosticsAsync().GetAwaiter().GetResult();
            return;
        }

        using var instanceMutex = new Mutex(
            initiallyOwned: true,
            "Local\\UsageAI.TrayApplication",
            out var isFirstInstance);
        if (!isFirstInstance)
        {
            return;
        }

        try
        {
            Application.Run(new UsageApplicationContext(CreateUsageClients()));
        }
        finally
        {
            instanceMutex.ReleaseMutex();
        }
    }

    private static async Task RunDiagnosticsAsync()
    {
        try
        {
            var providerId = GetDiagnosticProviderId(Environment.GetCommandLineArgs());
            var client = CreateUsageClients().FirstOrDefault(candidate =>
                candidate.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
            if (client is null)
            {
                throw new ArgumentException($"Unknown usage provider '{providerId}'. Use 'codex', 'claude', or 'copilot'.");
            }

            var snapshot = await client.GetUsageAsync();
            Console.WriteLine(snapshot.ToDiagnosticJson());
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(GetSafeDiagnosticError(exception));
            Environment.ExitCode = 1;
        }
    }

    private static string GetSafeDiagnosticError(Exception exception) => exception switch
    {
        ArgumentException or
        CodexUsageException or
        ClaudeCodeUsageException or
        ClaudeWebUsageException or
        GitHubCopilotUsageException => exception.Message,
        _ => "UsageAI could not complete the diagnostic request.",
    };

    private static IUsageClient[] CreateUsageClients() =>
        new IUsageClient[]
        {
            new CodexUsageClient(),
            new ClaudeCodeUsageClient(),
            new GitHubCopilotUsageClient(),
        };

    private static string GetDiagnosticProviderId(string[] args)
    {
        var flagIndex = Array.FindIndex(args, argument =>
            argument.Equals("--diagnose", StringComparison.OrdinalIgnoreCase));
        return flagIndex >= 0 && flagIndex + 1 < args.Length && !args[flagIndex + 1].StartsWith('-')
            ? args[flagIndex + 1]
            : UsageProviderSettings.SelectedProviderId;
    }

    private static void RenderPreview(string[] args)
    {
        var flagIndex = Array.FindIndex(args, argument =>
            argument.Equals("--render-preview", StringComparison.OrdinalIgnoreCase));
        var outputPath = flagIndex >= 0 &&
                         flagIndex + 1 < args.Length &&
                         !args[flagIndex + 1].StartsWith('-')
            ? Path.GetFullPath(args[flagIndex + 1])
            : Path.Combine(Environment.CurrentDirectory, "usageai-preview.png");

        var now = DateTimeOffset.Now;
        var states = new UI.ProviderViewState[]
        {
            new(
                "codex",
                "Codex",
                new Models.UsageSnapshot(
                    "Plus",
                    new Models.UsageWindow("5-hour", 43, now.AddHours(2).AddMinutes(18), 300),
                    new Models.UsageWindow("Weekly", 63, now.AddDays(3).AddHours(8), 10_080),
                    "$12.50",
                    3,
                    now),
                null,
                false),
            new(
                "claude",
                "Claude Code",
                new Models.UsageSnapshot(
                    "Max",
                    new Models.UsageWindow("5-hour", 22, now.AddHours(3).AddMinutes(41), 300),
                    new Models.UsageWindow("Weekly", 49, now.AddDays(4).AddHours(12), 10_080),
                    "$4.10",
                    0,
                    now,
                    "claude",
                    "Claude Code"),
                null,
                false),
            new(
                "copilot",
                "GitHub Copilot",
                new Models.UsageSnapshot(
                    "Pro",
                    new Models.UsageWindow("AI credits", 76, now.AddDays(9), null, "24% LEFT", "72 of 300 left"),
                    new Models.UsageWindow("Chat", 0, now.AddDays(9), null, "UNLIMITED", "No monthly limit"),
                    null,
                    0,
                    now,
                    "copilot",
                    "GitHub Copilot",
                    "octocat"),
                null,
                false),
        };

        using var form = new UsagePopupForm();
        form.SetStates(states, isRefreshing: false, lastRefreshed: now);
        form.SetMode(args.Contains("--full", StringComparer.OrdinalIgnoreCase)
            ? UI.DashboardMode.Full
            : UI.DashboardMode.Compact);
        form.Location = new Point(-10_000, -10_000);
        form.Show();
        Application.DoEvents();
        form.PerformLayout();
        using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(bitmap, form.ClientRectangle);
        bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
        form.Hide();
    }
}
