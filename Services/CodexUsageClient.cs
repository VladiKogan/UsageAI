using System.Diagnostics;
using System.Text.Json;
using UsageAI.Models;

namespace UsageAI.Services;

internal sealed class CodexUsageClient : IUsageClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private const int MaxProtocolLineCharacters = 131_072;
    private const int MaxProtocolMessagesPerResponse = 512;
    private const int MaxRetainedStandardErrorCharacters = 16_384;

    public string Id => "codex";

    public string DisplayName => "Codex";

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default) =>
        ParseSnapshot(await GetRawUsageAsync(cancellationToken));

    private static async Task<JsonElement> GetRawUsageAsync(CancellationToken cancellationToken = default)
    {
        var codexLaunch = FindCodexLaunch();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        using var process = StartAppServer(codexLaunch);
        var stderrTask = ProcessSecurity.DrainTextAsync(
            process.StandardError,
            MaxRetainedStandardErrorCharacters,
            timeout.Token);

        try
        {
            await WriteMessageAsync(process, new
            {
                id = 1,
                method = "initialize",
                @params = new
                {
                    clientInfo = new { name = "usage-ai", title = "UsageAI", version = AppIdentity.Version },
                    capabilities = new { experimentalApi = true },
                },
            }, timeout.Token);

            await ReadResponseAsync(process, 1, timeout.Token);

            await WriteMessageAsync(process, new { method = "initialized" }, timeout.Token);
            await WriteMessageAsync(process, new { id = 2, method = "account/rateLimits/read", @params = (object?)null }, timeout.Token);

            return await ReadResponseAsync(process, 2, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CodexUsageException("Codex did not return usage data within 20 seconds.");
        }
        catch (CodexUsageException exception)
        {
            var stderr = await TryReadErrorAsync(stderrTask);
            if (stderr.Contains("login", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("auth", StringComparison.OrdinalIgnoreCase))
            {
                throw new CodexUsageException(
                    "Codex is not signed in. Run `codex login`, then refresh.",
                    exception);
            }

            throw;
        }
        catch (Exception exception)
        {
            await TryReadErrorAsync(stderrTask);
            throw new CodexUsageException(
                "Could not read Codex usage. Verify the Codex CLI installation, then refresh.",
                exception);
        }
        finally
        {
            await StopProcessAsync(process);
        }
    }

    private static Process StartAppServer(CodexLaunch launch)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = launch.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        ProcessSecurity.ApplyMinimalEnvironment(
            startInfo,
            "CODEX_HOME",
            "NODE_EXTRA_CA_CERTS",
            "SSL_CERT_DIR",
            "SSL_CERT_FILE");

        if (launch.Script is not null)
        {
            startInfo.ArgumentList.Add(launch.Script);
        }

        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--listen");
        startInfo.ArgumentList.Add("stdio://");

        return Process.Start(startInfo)
            ?? throw new CodexUsageException("Windows could not start the Codex CLI.");
    }

    private static async Task WriteMessageAsync(Process process, object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message);
        await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task<JsonElement> ReadResponseAsync(
        Process process,
        int expectedId,
        CancellationToken cancellationToken)
    {
        for (var messageCount = 0; messageCount < MaxProtocolMessagesPerResponse; messageCount++)
        {
            var line = await ProcessSecurity.ReadBoundedLineAsync(
                process.StandardOutput,
                MaxProtocolLineCharacters,
                cancellationToken);
            if (line is null)
            {
                throw new CodexUsageException("The Codex app-server stopped before it returned usage data.");
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var id) || !MatchesId(id, expectedId))
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var errorMessage)
                        ? errorMessage.GetString()
                        : error.GetRawText();

                    throw new CodexUsageException(ToFriendlyError(message));
                }

                if (!root.TryGetProperty("result", out var result))
                {
                    throw new CodexUsageException("Codex returned a response without usage data.");
                }

                return result.Clone();
            }
        }

        throw new CodexUsageException("The Codex app-server returned too many unrelated messages.");
    }

    private static bool MatchesId(JsonElement id, int expectedId) =>
        (id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var number) && number == expectedId) ||
        (id.ValueKind == JsonValueKind.String &&
         id.GetString() == expectedId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static UsageSnapshot ParseSnapshot(JsonElement result)
    {
        var rateLimits = SelectCodexBucket(result);
        var primary = ParseWindow(rateLimits, "primary", "Primary");
        var secondary = ParseWindow(rateLimits, "secondary", "Secondary");
        var (session, weekly) = ClassifyWindows(primary, secondary);

        var plan = GetString(rateLimits, "planType") ?? "Codex";
        var creditBalance = rateLimits.TryGetProperty("credits", out var credits) &&
                            credits.ValueKind == JsonValueKind.Object
            ? GetString(credits, "balance")
            : null;

        var resetCredits = 0;
        if (result.TryGetProperty("rateLimitResetCredits", out var resetSummary) &&
            resetSummary.ValueKind == JsonValueKind.Object &&
            resetSummary.TryGetProperty("availableCount", out var resetCount))
        {
            resetCount.TryGetInt32(out resetCredits);
        }

        return new UsageSnapshot(
            FormatPlan(plan),
            session,
            weekly,
            creditBalance,
            Math.Max(0, resetCredits),
            DateTimeOffset.Now);
    }

    private static (UsageWindow? Session, UsageWindow? Weekly) ClassifyWindows(
        UsageWindow? primary,
        UsageWindow? secondary)
    {
        var windows = new[] { primary, secondary }.Where(window => window is not null).Cast<UsageWindow>().ToList();
        UsageWindow? session = null;
        UsageWindow? weekly = null;

        foreach (var window in windows)
        {
            if (window.DurationMinutes is >= 1_440)
            {
                weekly ??= window with { Name = FormatWindowName(window.DurationMinutes.Value, "Weekly") };
            }
            else
            {
                session ??= window with { Name = FormatWindowName(window.DurationMinutes, "Session") };
            }
        }

        if (session is null && weekly is null && primary is not null)
        {
            session = primary with { Name = "Session" };
            weekly = secondary is null ? null : secondary with { Name = "Weekly" };
        }

        return (session, weekly);
    }

    private static string FormatWindowName(long? durationMinutes, string fallback)
    {
        if (durationMinutes is null)
        {
            return fallback;
        }

        if (durationMinutes.Value % 10_080 == 0)
        {
            var weeks = durationMinutes.Value / 10_080;
            return weeks == 1 ? "Weekly" : $"{weeks}-week";
        }

        if (durationMinutes.Value % 60 == 0)
        {
            return $"{durationMinutes.Value / 60}-hour";
        }

        return fallback;
    }

    private static JsonElement SelectCodexBucket(JsonElement result)
    {
        if (result.TryGetProperty("rateLimitsByLimitId", out var buckets) &&
            buckets.ValueKind == JsonValueKind.Object)
        {
            if (buckets.TryGetProperty("codex", out var codex) && codex.ValueKind == JsonValueKind.Object)
            {
                return codex;
            }

            foreach (var bucket in buckets.EnumerateObject())
            {
                if (bucket.Value.ValueKind == JsonValueKind.Object)
                {
                    return bucket.Value;
                }
            }
        }

        if (result.TryGetProperty("rateLimits", out var historical) &&
            historical.ValueKind == JsonValueKind.Object)
        {
            return historical;
        }

        throw new CodexUsageException("Codex returned no rate-limit windows. Sign in with `codex login`, then refresh.");
    }

    private static UsageWindow? ParseWindow(JsonElement rateLimits, string propertyName, string name)
    {
        if (!rateLimits.TryGetProperty(propertyName, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!window.TryGetProperty("usedPercent", out var usedElement) || !usedElement.TryGetInt32(out var used))
        {
            return null;
        }

        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("resetsAt", out var resetElement) &&
            resetElement.ValueKind == JsonValueKind.Number &&
            resetElement.TryGetInt64(out var resetSeconds))
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetSeconds).ToLocalTime();
        }

        long? duration = null;
        if (window.TryGetProperty("windowDurationMins", out var durationElement) &&
            durationElement.ValueKind == JsonValueKind.Number &&
            durationElement.TryGetInt64(out var durationMinutes))
        {
            duration = durationMinutes;
        }

        return new UsageWindow(name, Math.Clamp(used, 0, 100), resetsAt, duration);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
    }

    private static string FormatPlan(string plan) => plan.Replace('_', ' ') switch
    {
        "plus" => "Plus",
        "pro" => "Pro",
        "prolite" => "Pro Lite",
        "team" => "Team",
        "business" => "Business",
        "enterprise" => "Enterprise",
        "edu" => "Education",
        "free" => "Free",
        var other => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(other),
    };

    private static string ToFriendlyError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Codex could not read the account rate limits.";
        }

        return message.Contains("login", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("auth", StringComparison.OrdinalIgnoreCase)
            ? "Codex is not signed in. Run `codex login`, then refresh."
            : "Codex could not read the account rate limits.";
    }

    private static CodexLaunch FindCodexLaunch()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_PATH");
        if (IsUsableCommand(configured))
        {
            return CreateLaunch(Path.GetFullPath(configured!));
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            throw new CodexUsageException("CODEX_PATH must be an absolute path to codex.exe or codex.cmd.");
        }

        var candidates = new List<string>();
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var cleanDirectory = directory.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(cleanDirectory))
            {
                continue;
            }

            if (!Path.IsPathFullyQualified(cleanDirectory))
            {
                continue;
            }

            candidates.Add(Path.GetFullPath(Path.Combine(cleanDirectory, "codex.exe")));
            candidates.Add(Path.GetFullPath(Path.Combine(cleanDirectory, "codex.cmd")));
        }

        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm",
            "codex.cmd"));

        var command = candidates.FirstOrDefault(File.Exists)
            ?? throw new CodexUsageException(
                "Codex CLI was not found. Install Codex or set CODEX_PATH to codex.cmd, then refresh.");

        return CreateLaunch(command);
    }

    private static CodexLaunch CreateLaunch(string command)
    {
        if (!command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            return new CodexLaunch(command, null);
        }

        var npmDirectory = Path.GetDirectoryName(command)
            ?? throw new CodexUsageException("The Codex CLI path is invalid.");
        var script = Path.Combine(npmDirectory, "node_modules", "@openai", "codex", "bin", "codex.js");
        if (!File.Exists(script))
        {
            throw new CodexUsageException("The Codex npm launcher is incomplete. Reinstall the Codex CLI, then refresh.");
        }

        var localNode = Path.Combine(npmDirectory, "node.exe");
        var node = File.Exists(localNode)
            ? Path.GetFullPath(localNode)
            : ProcessSecurity.FindAbsoluteExecutableOnPath("node.exe");
        if (node is null)
        {
            throw new CodexUsageException("Node.js was not found, so the Codex npm launcher cannot start.");
        }

        return new CodexLaunch(node, script);
    }

    private static bool IsUsableCommand(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !path.Contains('"') &&
        Path.IsPathFullyQualified(path) &&
        (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) &&
        File.Exists(path);

    private sealed record CodexLaunch(string Executable, string? Script);

    private static async Task<string> TryReadErrorAsync(Task<string> stderrTask)
    {
        try
        {
            return await stderrTask.WaitAsync(TimeSpan.FromMilliseconds(250));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            process.StandardInput.Close();
            ProcessSecurity.TryKill(process);

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // The process may already have exited; there is nothing left to clean up.
        }
    }
}

internal sealed class CodexUsageException : Exception
{
    public CodexUsageException(string message)
        : base(message)
    {
    }

    public CodexUsageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
