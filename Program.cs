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

        if (HasFlag(args, "--render-preview") || HasFlag(args, "--preview-window") || HasFlag(args, "--preview-tray"))
        {
            // Previews open real windows, and some of their buttons write local state (Settings'
            // Delete recorded history). Point them at a throwaway folder before anything reads
            // AppPaths, so a preview can never touch an installed copy's settings or history,
            // and delete that folder when the preview ends.
            PreviewRenderer.IsolateDataDirectory();
            try
            {
                if (HasFlag(args, "--render-preview"))
                {
                    PreviewRenderer.Render(args);
                }
                else if (HasFlag(args, "--preview-window"))
                {
                    PreviewRenderer.ShowWindow(args);
                }
                else
                {
                    PreviewRenderer.ShowTray(args);
                }
            }
            finally
            {
                PreviewRenderer.DeleteIsolatedDataDirectory();
            }

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

        // Inno Setup uses Windows Restart Manager to close this process while replacing the
        // executable. Registration lets it reopen the updated build when installation finishes.
        _ = ApplicationRestart.TryRegister();

        var profileExisted = Directory.Exists(AppPaths.DataDirectory);
        var settings = AppSettings.Load();
        // An in-app update leaves a note for the copy that starts after it, and its download.
        var updateResult = UpdateHandoffStore.Take(AppIdentity.Version);
        UpdateHandoffStore.CleanUpInstallers();
        try
        {
            Application.Run(new UsageApplicationContext(CreateUsageClients(), settings, profileExisted, updateResult));
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
          UsageAI.exe --render-preview [path] [--full] [--dpi 96|192|288]
                                    [--quota-display used|remaining] [--theme light|dark]
                                             Render a preview image of the popup, or of
                                             the dashboard with --full.
          UsageAI.exe --preview-window popup|dashboard|settings|whatsnew|prompt|warning|menu|update [--theme light|dark]
                                             Open that window with sample data
                                             (--states mixed for loading, stale and
                                             signed-out providers; update plays an
                                             in-app update, --states failed fails it),
                                             without a tray icon or provider access.
          UsageAI.exe --preview-tray [--theme light|dark] [--quota-display used|remaining]
                                             Run the tray app on sample providers. Each
                                             Refresh steps the readings calm, warning,
                                             critical, so the tray icon can be filmed.
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

            var attempts = ProviderAttempts.Begin();
            try
            {
                var snapshot = await client.GetUsageAsync();
                Console.WriteLine(DiagnosticJson(snapshot, attempts));
            }
            catch (Exception exception)
            {
                // The error stays on stderr as before; the attempts that led to it go to stdout.
                Console.Error.WriteLine(GetSafeDiagnosticError(exception));
                if (attempts.Count > 0)
                {
                    Console.WriteLine(DiagnosticJson(null, attempts));
                }

                Environment.ExitCode = 1;
            }
            finally
            {
                ProviderAttempts.End();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(GetSafeDiagnosticError(exception));
            Environment.ExitCode = 1;
        }
    }

    /// <summary>
    /// The snapshot's diagnostic JSON with an <c>Attempts</c> array added: every source the provider
    /// tried, in order, with a fixed-vocabulary outcome and its duration.
    /// </summary>
    internal static string DiagnosticJson(UsageAI.Models.UsageSnapshot? snapshot, IReadOnlyList<ProviderAttempt> attempts)
    {
        var root = snapshot is null
            ? new System.Text.Json.Nodes.JsonObject()
            : System.Text.Json.Nodes.JsonNode.Parse(snapshot.ToDiagnosticJson())!.AsObject();
        root["Attempts"] = System.Text.Json.JsonSerializer.SerializeToNode(attempts.ToArray());
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
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

    private static string GetDiagnosticProviderId(string[] args) =>
        ValueAfter(args, "--diagnose") ?? "codex";

    /// <summary>
    /// The argument following <paramref name="flag"/>, or null when the flag is absent or is
    /// followed by another flag rather than a value.
    /// </summary>
    internal static string? ValueAfter(string[] args, string flag)
    {
        var flagIndex = Array.FindIndex(args, argument => argument.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return flagIndex >= 0 && flagIndex + 1 < args.Length && !args[flagIndex + 1].StartsWith('-')
            ? args[flagIndex + 1]
            : null;
    }
}
