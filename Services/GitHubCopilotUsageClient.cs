using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using UsageAI.Models;

namespace UsageAI.Services;

internal sealed class GitHubCopilotUsageClient : IUsageClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly Uri UserEndpoint = new("https://api.github.com/copilot_internal/user");
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public string Id => "copilot";

    public string DisplayName => "GitHub Copilot";

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        var tokens = await FindTokensAsync(cancellationToken);
        if (tokens.Count == 0)
        {
            throw new GitHubCopilotUsageException(
                "GitHub Copilot is not signed in. Sign in through Copilot or GitHub CLI, then refresh.");
        }

        Exception? lastFailure = null;
        var rejectedTokens = 0;

        foreach (var token in tokens)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, UserEndpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.UserAgent.ParseAdd("UsageAI/0.2.0");

                using var client = new HttpClient { Timeout = RequestTimeout };
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    rejectedTokens++;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new GitHubCopilotUsageException(
                        $"GitHub Copilot returned HTTP {(int)response.StatusCode} while reading usage.");
                }

                await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
                return ParseSnapshot(document.RootElement);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastFailure = new GitHubCopilotUsageException("GitHub Copilot did not return usage data within 15 seconds.");
            }
            catch (GitHubCopilotUsageException exception)
            {
                lastFailure = exception;
            }
            catch (Exception exception)
            {
                lastFailure = exception;
            }
        }

        if (rejectedTokens == tokens.Count)
        {
            throw new GitHubCopilotUsageException(
                "The saved GitHub credentials cannot access Copilot. Sign in to GitHub Copilot again, then refresh.");
        }

        throw new GitHubCopilotUsageException(
            $"Could not read GitHub Copilot usage: {lastFailure?.Message ?? "unknown error"}",
            lastFailure);
    }

    internal static UsageSnapshot ParseSnapshot(JsonElement root)
    {
        var resetAt = ParseReset(root);
        var windows = new List<(int Order, UsageWindow Window)>();

        if (root.TryGetProperty("quota_snapshots", out var snapshots) && snapshots.ValueKind == JsonValueKind.Object)
        {
            AddQuotaWindow(windows, snapshots, "premium_interactions", 0, resetAt);
            AddQuotaWindow(windows, snapshots, "chat", 1, resetAt);
            AddQuotaWindow(windows, snapshots, "completions", 2, resetAt);
        }

        var selected = windows
            .OrderBy(item => item.Window.RemainingText == "UNLIMITED" ? 1 : 0)
            .ThenBy(item => item.Order)
            .Take(2)
            .Select(item => item.Window)
            .ToArray();

        if (selected.Length == 0)
        {
            throw new GitHubCopilotUsageException(
                "GitHub Copilot returned account details without any quota information.");
        }

        var plan = GetString(root, "copilot_plan") ?? GetString(root, "access_type_sku") ?? "Copilot";
        return new UsageSnapshot(
            FormatPlan(plan),
            selected.ElementAtOrDefault(0),
            selected.ElementAtOrDefault(1),
            null,
            0,
            DateTimeOffset.Now,
            "copilot",
            "GitHub Copilot",
            GetString(root, "login"));
    }

    private static void AddQuotaWindow(
        ICollection<(int Order, UsageWindow Window)> windows,
        JsonElement snapshots,
        string propertyName,
        int order,
        DateTimeOffset? fallbackReset)
    {
        if (!snapshots.TryGetProperty(propertyName, out var quota) || quota.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (quota.TryGetProperty("has_quota", out var hasQuota) &&
            hasQuota.ValueKind is JsonValueKind.False)
        {
            return;
        }

        var unlimited = GetBoolean(quota, "unlimited");
        var remainingPercent = GetDouble(quota, "percent_remaining");
        var entitlement = GetDouble(quota, "entitlement");
        var remaining = GetDouble(quota, "quota_remaining") ?? GetDouble(quota, "remaining");

        if (remainingPercent is null && entitlement is > 0 && remaining is not null)
        {
            remainingPercent = remaining.Value / entitlement.Value * 100;
        }

        remainingPercent = Math.Clamp(remainingPercent ?? (unlimited ? 100 : 0), 0, 100);
        var usedPercent = (int)Math.Round(100 - remainingPercent.Value, MidpointRounding.AwayFromZero);
        var resetAt = ParseUnixTime(quota, "quota_reset_at") ?? fallbackReset;
        var name = propertyName switch
        {
            "premium_interactions" when GetBoolean(quota, "token_based_billing") => "AI credits",
            "premium_interactions" => "Premium requests",
            "chat" => "Chat",
            "completions" => "Completions",
            _ => propertyName.Replace('_', ' '),
        };

        var remainingText = unlimited ? "UNLIMITED" : $"{Math.Round(remainingPercent.Value):0}% LEFT";
        var usageText = unlimited
            ? "No monthly limit"
            : entitlement is > 0 && remaining is not null
                ? $"{FormatNumber(remaining.Value)} of {FormatNumber(entitlement.Value)} left"
                : $"{usedPercent}% used";

        windows.Add((order, new UsageWindow(name, usedPercent, resetAt, null, remainingText, usageText)));
    }

    private static DateTimeOffset? ParseReset(JsonElement root)
    {
        foreach (var propertyName in new[] { "quota_reset_date_utc", "quota_reset_date", "limited_user_reset_date" })
        {
            var value = GetString(root, propertyName);
            if (DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return parsed.ToLocalTime();
            }
        }

        return null;
    }

    private static DateTimeOffset? ParseUnixTime(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            !value.TryGetInt64(out var seconds) ||
            seconds <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<string>> FindTokensAsync(CancellationToken cancellationToken)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        AddToken(tokens, Environment.GetEnvironmentVariable("COPILOT_GITHUB_TOKEN"));

        foreach (var path in GetCredentialFileCandidates())
        {
            AddTokensFromFile(tokens, path);
        }

        foreach (var token in WindowsCredentialReader.FindGenericPasswords("copilot-cli"))
        {
            AddToken(tokens, token);
        }

        AddToken(tokens, await TryReadGitHubCliTokenAsync(cancellationToken));
        return tokens.ToArray();
    }

    private static IEnumerable<string> GetCredentialFileCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        yield return Path.Combine(localAppData, "github-copilot", "apps.json");
        yield return Path.Combine(roamingAppData, "GitHub Copilot", "apps.json");
        yield return Path.Combine(userProfile, ".copilot", "config.json");
        yield return Path.Combine(userProfile, ".copilot", "settings.json");
    }

    private static void AddTokensFromFile(ISet<string> tokens, string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream, JsonOptions);
            AddTokensFromElement(tokens, document.RootElement);
        }
        catch (IOException)
        {
            // Another Copilot process may briefly hold the credential file; other sources can still be tried.
        }
        catch (UnauthorizedAccessException)
        {
            // A protected credential store is optional and should not prevent fallback authentication.
        }
        catch (JsonException)
        {
            // Some Copilot-managed files are JSONC or placeholders rather than credential documents.
        }
    }

    private static void AddTokensFromElement(ISet<string> tokens, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("oauth_token") && property.Value.ValueKind == JsonValueKind.String)
                    {
                        AddToken(tokens, property.Value.GetString());
                    }
                    else if (property.NameEquals("copilotTokens") && property.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var savedToken in property.Value.EnumerateObject())
                        {
                            if (savedToken.Value.ValueKind == JsonValueKind.String)
                            {
                                AddToken(tokens, savedToken.Value.GetString());
                            }
                        }
                    }
                    else if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        AddTokensFromElement(tokens, property.Value);
                    }
                }

                break;
        }
    }

    private static async Task<string?> TryReadGitHubCliTokenAsync(CancellationToken cancellationToken)
    {
        var executable = FindOnPath("gh.exe");
        if (executable is null)
        {
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("auth");
            startInfo.ArgumentList.Add("token");
            startInfo.ArgumentList.Add("--hostname");
            startInfo.ArgumentList.Add("github.com");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await errorTask;
            return process.ExitCode == 0 ? (await outputTask).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindOnPath(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim().Trim('"'), fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void AddToken(ISet<string> tokens, string? token)
    {
        if (!string.IsNullOrWhiteSpace(token))
        {
            tokens.Add(token.Trim());
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
    }

    private static double? GetDouble(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetDouble(out var value)
            ? value
            : null;

    private static bool GetBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.True;

    private static string FormatNumber(double value) =>
        value.ToString(Math.Abs(value % 1) < 0.001 ? "N0" : "N1", CultureInfo.CurrentCulture);

    private static string FormatPlan(string plan)
    {
        var normalized = plan.Replace('_', ' ').Trim();
        return normalized.ToLowerInvariant() switch
        {
            "individual" => "Individual",
            "individual pro" => "Pro",
            "business" => "Business",
            "enterprise" => "Enterprise",
            "free" => "Free",
            var other => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(other),
        };
    }
}

internal sealed class GitHubCopilotUsageException : Exception
{
    public GitHubCopilotUsageException(string message)
        : base(message)
    {
    }

    public GitHubCopilotUsageException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
