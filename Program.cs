using UsageAI.Services;
using UsageAI.UI;

namespace UsageAI;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (HasFlag(args, "--help") || HasFlag(args, "-h") || HasFlag(args, "/?"))
        {
            Console.WriteLine(HelpText);
            return;
        }

        if (HasFlag(args, "--version"))
        {
            Console.WriteLine(AppIdentity.Version);
            return;
        }

        if (HasFlag(args, "--render-preview"))
        {
            PreviewRenderer.Render(args);
            return;
        }

        if (HasFlag(args, "--diagnose"))
        {
            try
            {
                RunDiagnosticsAsync(args).GetAwaiter().GetResult();
            }
            finally
            {
                // A one-shot diagnostic must not leave the shared Antigravity hub running.
                AgyUsageProbe.DisposeHub();
            }

            return;
        }

        using var instanceMutex = new Mutex(
            initiallyOwned: true,
            SingleInstance.MutexName,
            out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Bring the window of the instance that is already running forward, rather than
            // exiting silently and looking like a failed launch.
            SingleInstance.BroadcastShow();
            return;
        }

        var settings = AppSettings.Load();
        try
        {
            Application.Run(new UsageApplicationContext(CreateUsageClients(), settings));
        }
        finally
        {
            instanceMutex.ReleaseMutex();
        }
    }

    private const string HelpText = """
        UsageAI - a Windows tray meter for Codex, Claude Code, GitHub Copilot, and Google Gemini usage.

          UsageAI.exe                       Start the tray application.
          UsageAI.exe --diagnose <provider>  Print one provider's usage as JSON.
                                             Providers: codex, claude, copilot, gemini.
          UsageAI.exe --render-preview [path] [--full]
                                             Render a preview image of the popup.
          UsageAI.exe --version              Print the application version.
          UsageAI.exe --help                 Show this help.

        Settings live in %LOCALAPPDATA%\UsageAI. Diagnostic output contains account
        identity and usage metadata but never provider tokens.
        """;

    private static async Task RunDiagnosticsAsync(string[] args)
    {
        try
        {
            var providerId = GetDiagnosticProviderId(args);
            var client = CreateUsageClients().FirstOrDefault(candidate =>
                candidate.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
            if (client is null)
            {
                throw new ArgumentException($"Unknown usage provider '{providerId}'. Use 'codex', 'claude', 'copilot', or 'gemini'.");
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
        GitHubCopilotUsageException or
        GeminiUsageException => exception.Message,
        _ => "UsageAI could not complete the diagnostic request.",
    };

    private static IUsageClient[] CreateUsageClients() =>
        new IUsageClient[]
        {
            new CodexUsageClient(),
            new ClaudeCodeUsageClient(),
            new GitHubCopilotUsageClient(),
            new GeminiUsageClient(),
        };

    private static bool HasFlag(string[] args, string flag) =>
        args.Contains(flag, StringComparer.OrdinalIgnoreCase);

    private static string GetDiagnosticProviderId(string[] args)
    {
        var flagIndex = Array.FindIndex(args, argument =>
            argument.Equals("--diagnose", StringComparison.OrdinalIgnoreCase));
        return flagIndex >= 0 && flagIndex + 1 < args.Length && !args[flagIndex + 1].StartsWith('-')
            ? args[flagIndex + 1]
            : "codex";
    }
}
