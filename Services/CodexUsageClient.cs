using System.Diagnostics;
using System.Runtime.InteropServices;
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

    public string SignInCommand => "codex login";

    public Uri AccountUrl { get; } = new("https://chatgpt.com/codex/settings/usage");

    /// <summary>How long the optional account lookup may take after usage has already arrived.</summary>
    private static readonly TimeSpan AccountReadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>At most this many extra rate-limit buckets (model-specific limits such as Spark) are shown.</summary>
    private const int MaxExtraBuckets = 6;

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        var (rateLimits, account) = await GetRawUsageAsync(cancellationToken);
        return ParseSnapshot(rateLimits, account);
    }

    private static async Task<(JsonElement RateLimits, JsonElement? Account)> GetRawUsageAsync(CancellationToken cancellationToken = default)
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

            var rateLimits = await ReadResponseAsync(process, 2, timeout.Token);
            return (rateLimits, await TryReadAccountAsync(process, timeout.Token));
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
                    "Codex is not signed in. Run `codex login` or sign in through the Codex extension, then refresh.",
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

    /// <summary>
    /// Asks which account is signed in, for the card's account line. It is optional: usage has
    /// already arrived, so an older CLI without <c>account/read</c>, an error, or a slow answer
    /// leaves the account unnamed instead of failing the refresh. <c>refreshToken: false</c>
    /// keeps the lookup from refreshing the login.
    /// </summary>
    private static async Task<JsonElement?> TryReadAccountAsync(Process process, CancellationToken cancellationToken)
    {
        using var accountTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        accountTimeout.CancelAfter(AccountReadTimeout);
        try
        {
            await WriteMessageAsync(
                process,
                new { id = 3, method = "account/read", @params = new { refreshToken = false } },
                accountTimeout.Token);
            return await ReadResponseAsync(process, 3, accountTimeout.Token);
        }
        catch (Exception exception) when (
            exception is CodexUsageException or IOException ||
            (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>
    /// The arguments ahead of <c>app-server</c>: a read-only sandbox and no approval prompts, so the
    /// server UsageAI starts can never be asked to run or approve anything.
    /// </summary>
    internal static readonly string[] AppServerArguments =
        ["-s", "read-only", "-a", "never", "app-server", "--listen", "stdio://"];

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

        foreach (var argument in AppServerArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

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

    internal static UsageSnapshot ParseSnapshot(JsonElement result, JsonElement? account = null)
    {
        var (mainKey, rateLimits) = SelectCodexBucket(result);
        var extraBuckets = ExtraBuckets(result, mainKey);

        // With model-specific buckets such as Spark, every bucket becomes a named group, so the popup
        // offers Codex and Spark as separate ring groups the way it does Gemini's model groups. An
        // account with only the main bucket keeps the plain "5-hour" / "Weekly" names.
        var metrics = new List<UsageMetric>();
        AddWindows(metrics, rateLimits, extraBuckets.Count > 0 ? "Codex" : null);
        foreach (var (group, bucket) in extraBuckets)
        {
            AddWindows(metrics, bucket, group);
        }

        if (rateLimits.TryGetProperty("credits", out var credits) &&
            credits.ValueKind == JsonValueKind.Object &&
            GetString(credits, "balance") is { Length: > 0 } balance)
        {
            metrics.Add(new UsageMetric(
                "Credits",
                UsageMetricKind.Balance,
                null,
                RemainingText: balance,
                UsageText: "Available account balance"));
        }

        var resetCredits = 0;
        if (result.TryGetProperty("rateLimitResetCredits", out var resetSummary) &&
            resetSummary.ValueKind == JsonValueKind.Object &&
            resetSummary.TryGetProperty("availableCount", out var resetCount))
        {
            resetCount.TryGetInt32(out resetCredits);
        }

        if (resetCredits > 0)
        {
            metrics.Add(new UsageMetric(
                "Reset credits",
                UsageMetricKind.Balance,
                null,
                RemainingText: resetCredits.ToString(System.Globalization.CultureInfo.CurrentCulture),
                UsageText: $"Full reset{(resetCredits == 1 ? string.Empty : "s")} available"));
        }

        // The fresh rate-limit response knows the current plan best; the account lookup only names
        // who is signed in.
        var accountDetails = account is { ValueKind: JsonValueKind.Object } accountRoot &&
                             accountRoot.TryGetProperty("account", out var accountElement) &&
                             accountElement.ValueKind == JsonValueKind.Object
            ? accountElement
            : (JsonElement?)null;
        var plan = GetString(rateLimits, "planType") ??
                   (accountDetails is { } details ? GetString(details, "planType") : null) ??
                   "Codex";
        var email = accountDetails is { } signedIn ? GetString(signedIn, "email") : null;
        return new UsageSnapshot(
            FormatPlan(plan),
            metrics,
            DateTimeOffset.Now,
            "codex",
            "Codex",
            AccountName: email is { Length: > 0 and <= 320 } ? email : null);
    }

    private static void AddWindows(List<UsageMetric> metrics, JsonElement bucket, string? group)
    {
        var primary = ParseWindow(bucket, "primary", "Primary");
        var secondary = ParseWindow(bucket, "secondary", "Secondary");
        var (session, weekly) = ClassifyWindows(primary, secondary);
        foreach (var window in new[] { session, weekly })
        {
            if (window is not null)
            {
                metrics.Add(group is null ? window : window with { Name = $"{group} ({window.Name})" });
            }
        }
    }

    /// <summary>
    /// The buckets besides the main one that report at least one window, each named by the label the
    /// server gives it (<c>limitName</c>), else its model slug or a readable form of its id.
    /// </summary>
    private static List<(string Group, JsonElement Bucket)> ExtraBuckets(JsonElement result, string? mainKey)
    {
        var extras = new List<(string Group, JsonElement Bucket)>();
        if (mainKey is null ||
            !result.TryGetProperty("rateLimitsByLimitId", out var buckets) ||
            buckets.ValueKind != JsonValueKind.Object)
        {
            return extras;
        }

        foreach (var bucket in buckets.EnumerateObject())
        {
            if (extras.Count >= MaxExtraBuckets)
            {
                break;
            }

            if (bucket.Name == mainKey ||
                bucket.Value.ValueKind != JsonValueKind.Object ||
                (ParseWindow(bucket.Value, "primary", "Primary") ?? ParseWindow(bucket.Value, "secondary", "Secondary")) is null)
            {
                continue;
            }

            var group = BucketLabel(bucket.Value, bucket.Name);
            if (!extras.Any(existing => string.Equals(existing.Group, group, StringComparison.OrdinalIgnoreCase)))
            {
                extras.Add((group, bucket.Value));
            }
        }

        return extras;
    }

    internal static string BucketLabel(JsonElement bucket, string limitId)
    {
        var label = GetString(bucket, "limitName") ?? GetString(bucket, "normalModelSlug");
        if (string.IsNullOrWhiteSpace(label))
        {
            label = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(
                limitId.Replace('_', ' ').Replace('-', ' ').Trim());
        }

        // A label ends up in a metric name, where a parenthesis would read as the window.
        label = new string(label.Where(character => !char.IsControl(character) && character is not '(' and not ')').ToArray()).Trim();
        return label.Length switch
        {
            0 => "Other limit",
            > 60 => label[..60].TrimEnd(),
            _ => label,
        };
    }

    private static (UsageMetric? Session, UsageMetric? Weekly) ClassifyWindows(
        UsageMetric? primary,
        UsageMetric? secondary)
    {
        var windows = new[] { primary, secondary }.Where(window => window is not null).Cast<UsageMetric>().ToList();
        UsageMetric? session = null;
        UsageMetric? weekly = null;

        foreach (var window in windows)
        {
            if (window.DurationMinutes is >= 1_440)
            {
                weekly ??= window with
                {
                    Name = FormatWindowName(window.DurationMinutes.Value, "Weekly"),
                    Kind = UsageMetricKind.Rolling,
                };
            }
            else
            {
                session ??= window with
                {
                    Name = FormatWindowName(window.DurationMinutes, "Session"),
                    Kind = UsageMetricKind.Session,
                };
            }
        }

        if (session is null && weekly is null && primary is not null)
        {
            session = primary with { Name = "Session", Kind = UsageMetricKind.Session };
            weekly = secondary is null
                ? null
                : secondary with { Name = "Weekly", Kind = UsageMetricKind.Rolling };
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

    /// <summary>The main bucket and its key; the key is null for the older single-bucket shape.</summary>
    private static (string? Key, JsonElement Bucket) SelectCodexBucket(JsonElement result)
    {
        if (result.TryGetProperty("rateLimitsByLimitId", out var buckets) &&
            buckets.ValueKind == JsonValueKind.Object)
        {
            if (buckets.TryGetProperty("codex", out var codex) && codex.ValueKind == JsonValueKind.Object)
            {
                return ("codex", codex);
            }

            foreach (var bucket in buckets.EnumerateObject())
            {
                if (bucket.Value.ValueKind == JsonValueKind.Object)
                {
                    return (bucket.Name, bucket.Value);
                }
            }
        }

        if (result.TryGetProperty("rateLimits", out var historical) &&
            historical.ValueKind == JsonValueKind.Object)
        {
            return (null, historical);
        }

        throw new CodexUsageException("Codex returned no rate-limit windows. Sign in with `codex login`, then refresh.");
    }

    private static UsageMetric? ParseWindow(JsonElement rateLimits, string propertyName, string name)
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

        return new UsageMetric(name, UsageMetricKind.Session, Math.Clamp(used, 0, 100), resetsAt, duration);
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
            ? "Codex is not signed in. Run `codex login` or sign in through the Codex extension, then refresh."
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

        // Last, so a standalone install keeps priority: the binary the Codex editor extension ships
        // for itself, as the editor extension uses.
        var command = candidates.FirstOrDefault(File.Exists)
            ?? EditorExtensionBinaries.FindCodex(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                RuntimeInformation.OSArchitecture)
            ?? throw new CodexUsageException(
                "Codex CLI was not found. Install Codex or the Codex extension, or set CODEX_PATH to codex.cmd, then refresh.");

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
