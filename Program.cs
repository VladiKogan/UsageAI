using UsageAI.Models;
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
        var accounts = new AccountManager(settings);
        try
        {
            Application.Run(new UsageApplicationContext(accounts, settings, profileExisted, updateResult));
        }
        finally
        {
            instanceMutex.ReleaseMutex();
        }
    }

    private const string HelpText = """
        UsageAI - a Windows tray meter for Codex, Claude Code, GitHub Copilot, and Google Gemini usage.

          UsageAI.exe                       Start the tray application.
          UsageAI.exe --diagnose <provider> [--account <label|id>]
                                             Print one provider's usage as JSON.
                                             Providers: codex, claude, copilot, gemini.
                                             A provider with several accounts prints
                                             an array, default login first.
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
            if (!CreateUsageClients().Any(candidate => candidate.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"Unknown usage provider '{providerId}'. Use 'codex', 'claude', 'copilot', or 'gemini'.");
            }

            // The saved accounts plus a scan of the profile, kept in memory: a diagnostic never
            // changes what the tray app shows.
            var settings = AppSettings.Load();
            var ephemeral = new AppSettings { IsEphemeral = true };
            ephemeral.Accounts.AddRange(settings.Accounts.Select(account => account.Clone()));
            ephemeral.ForgottenAccounts = settings.ForgottenAccounts;
            ephemeral.AccountsInitialized = true;
            ephemeral.AskBeforeShowingNewAccounts = false;
            var manager = new AccountManager(ephemeral);
            manager.Scan(DateTimeOffset.UtcNow);
            var clients = manager.CreateClients(CreateUsageClients())
                .Where(candidate => ProviderKey.ProviderOf(candidate.Id).Equals(providerId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var accountFilter = ValueAfter(args, "--account");
            if (accountFilter is not null)
            {
                clients = clients
                    .Where(candidate => MatchesAccount(candidate, ephemeral.FindAccount(candidate.Id), accountFilter))
                    .Take(1)
                    .ToArray();
                if (clients.Length == 0)
                {
                    throw new ArgumentException($"No {providerId} account matches '{accountFilter}'.");
                }
            }

            var results = new List<string>();
            var failed = false;
            foreach (var client in clients)
            {
                var account = clients.Length > 1 || accountFilter is not null ? ephemeral.FindAccount(client.Id) : null;
                var attempts = ProviderAttempts.Begin();
                try
                {
                    var snapshot = await client.GetUsageAsync();
                    results.Add(DiagnosticJson(snapshot, attempts, account, client));
                }
                catch (Exception exception)
                {
                    // The error stays on stderr as before; the attempts that led to it go to stdout.
                    Console.Error.WriteLine(clients.Length > 1
                        ? $"{client.Id}: {GetSafeDiagnosticError(exception)}"
                        : GetSafeDiagnosticError(exception));
                    if (attempts.Count > 0 || clients.Length > 1)
                    {
                        results.Add(DiagnosticJson(null, attempts, account, client));
                    }

                    failed = true;
                }
                finally
                {
                    ProviderAttempts.End();
                }
            }

            if (results.Count == 1)
            {
                Console.WriteLine(results[0]);
            }
            else if (results.Count > 1)
            {
                Console.WriteLine($"[\n{string.Join(",\n", results)}\n]");
            }

            if (failed)
            {
                Environment.ExitCode = 1;
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
    internal static string DiagnosticJson(
        UsageAI.Models.UsageSnapshot? snapshot,
        IReadOnlyList<ProviderAttempt> attempts,
        UsageAI.Models.ProviderAccount? account = null,
        IUsageClient? client = null)
    {
        var root = snapshot is null
            ? new System.Text.Json.Nodes.JsonObject()
            : System.Text.Json.Nodes.JsonNode.Parse(snapshot.ToDiagnosticJson())!.AsObject();
        if (account is not null || (client is not null && !UsageAI.Models.ProviderKey.IsDefault(client.Id)))
        {
            // Where the reading came from: the folder and who is signed in, never a credential.
            root["Account"] = new System.Text.Json.Nodes.JsonObject
            {
                ["Id"] = client is null ? account?.Id : UsageAI.Models.ProviderKey.AccountOf(client.Id),
                ["Source"] = (account?.Source ?? UsageAI.Models.AccountSource.Default).ToString(),
                ["Path"] = account?.Path ?? (client as IAccountUsageClient)?.LoginFolder,
                ["Label"] = account?.DisplayLabel,
            };
        }

        root["Attempts"] = System.Text.Json.JsonSerializer.SerializeToNode(attempts.ToArray());
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static bool MatchesAccount(IUsageClient client, UsageAI.Models.ProviderAccount? account, string filter) =>
        UsageAI.Models.ProviderKey.AccountOf(client.Id).Equals(filter, StringComparison.OrdinalIgnoreCase) ||
        client.Id.Equals(filter, StringComparison.OrdinalIgnoreCase) ||
        (account is not null &&
         (string.Equals(account.Label, filter, StringComparison.OrdinalIgnoreCase) ||
          string.Equals(account.Email, filter, StringComparison.OrdinalIgnoreCase) ||
          string.Equals(account.DisplayLabel, filter, StringComparison.OrdinalIgnoreCase)));

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

    /// <summary>The default login of every provider; <see cref="AccountManager"/> adds the extra accounts.</summary>
    internal static IUsageClient[] CreateUsageClients() =>
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
