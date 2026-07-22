using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UsageAI.Models;

namespace UsageAI.Services;

internal sealed class ClaudeCodeUsageClient : IUsageClient
{
    private const string CredentialManagerService = "Claude Code-credentials";
    private const string OAuthBeta = "oauth-2025-04-20";
    private const string OAuthClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private static readonly Uri UsageEndpoint = new("https://api.anthropic.com/api/oauth/usage");
    private static readonly Uri TokenEndpoint = new("https://platform.claude.com/v1/oauth/token");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private ClaudeCredentials? _refreshedCredentials;

    public string Id => "claude";

    public string DisplayName => "Claude Code";

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        var credentials = LoadCredentials();
        credentials = await EnsureFreshCredentialsAsync(credentials, cancellationToken);

        if (credentials.Scopes.Count > 0 &&
            !credentials.Scopes.Contains("user:profile", StringComparer.Ordinal))
        {
            throw new ClaudeCodeUsageException(
                "Claude Code's OAuth token is missing the user:profile scope. Run `claude` to sign in again.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("anthropic-beta", OAuthBeta);
        request.Headers.UserAgent.ParseAdd("UsageAI/0.1.0");

        try
        {
            using var client = new HttpClient { Timeout = RequestTimeout };
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new ClaudeCodeUsageException(
                    "Claude Code's login has expired. Run `claude` to sign in again, then refresh.");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new ClaudeCodeUsageException(
                    "Claude Code's login cannot read account usage. Run `claude` to sign in again.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryText = response.Headers.RetryAfter?.Delta is { } retryAfter
                    ? $" Try again in about {Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))} seconds."
                    : string.Empty;
                throw new ClaudeCodeUsageException($"Anthropic rate-limited the usage request.{retryText}");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ClaudeCodeUsageException(
                    $"Claude Code returned HTTP {(int)response.StatusCode} while reading usage.");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
            return ParseSnapshot(document.RootElement, credentials.Plan);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ClaudeCodeUsageException("Claude Code did not return usage data within 15 seconds.");
        }
        catch (ClaudeCodeUsageException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ClaudeCodeUsageException($"Could not read Claude Code usage: {exception.Message}", exception);
        }
    }

    internal static UsageSnapshot ParseSnapshot(JsonElement root, string plan = "Claude")
    {
        var session = ParseWindow(root, "five_hour", "fiveHour", "5-hour", 300);
        var weekly = ParseWeeklyAllLimit(root) ??
                     ParseWindow(root, "seven_day", "sevenDay", "Weekly", 10_080);

        if (session is null && weekly is null)
        {
            throw new ClaudeCodeUsageException(
                "Claude Code returned account details without five-hour or weekly usage data.");
        }

        return new UsageSnapshot(
            plan,
            session,
            weekly,
            FormatExtraUsage(root),
            0,
            DateTimeOffset.Now,
            "claude",
            "Claude Code");
    }

    private ClaudeCredentials LoadCredentials()
    {
        var environmentToken = Environment.GetEnvironmentVariable("USAGEAI_CLAUDE_OAUTH_TOKEN");
        if (!string.IsNullOrWhiteSpace(environmentToken))
        {
            environmentToken = environmentToken.Trim();
            var scopes = (Environment.GetEnvironmentVariable("USAGEAI_CLAUDE_OAUTH_SCOPES") ?? "user:profile")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new ClaudeCredentials(
                environmentToken,
                null,
                null,
                scopes,
                "Claude (OAuth)",
                null,
                environmentToken);
        }

        Exception? fileError = null;
        var credentialPath = GetCredentialPath();
        if (File.Exists(credentialPath))
        {
            try
            {
                return ParseCredentials(File.ReadAllText(credentialPath), credentialPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ClaudeCodeUsageException)
            {
                fileError = exception;
            }
        }

        foreach (var savedCredential in WindowsCredentialReader.FindKeyringPasswords(CredentialManagerService))
        {
            try
            {
                return ParseCredentials(savedCredential, null);
            }
            catch (Exception exception) when (exception is JsonException or ClaudeCodeUsageException)
            {
                fileError ??= exception;
            }
        }

        if (fileError is not null)
        {
            throw new ClaudeCodeUsageException(
                "Claude Code's saved login could not be read. Run `claude` to sign in again.",
                fileError);
        }

        throw new ClaudeCodeUsageException(
            "Claude Code is not signed in. Run `claude` to sign in, then refresh.");
    }

    private async Task<ClaudeCredentials> EnsureFreshCredentialsAsync(
        ClaudeCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (_refreshedCredentials is { } cached &&
            cached.SourceCredentialToken == credentials.SourceCredentialToken &&
            cached.ExpiresAt is { } cachedExpiry &&
            cachedExpiry > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            credentials = cached;
        }

        if (!credentials.IsExpired)
        {
            return credentials;
        }

        if (credentials.SourcePath is { } sourcePath && File.Exists(sourcePath))
        {
            try
            {
                var diskCredentials = ParseCredentials(File.ReadAllText(sourcePath), sourcePath);
                if (!diskCredentials.IsExpired)
                {
                    return diskCredentials;
                }

                credentials = diskCredentials;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // The already-loaded refresh token can still be used if Claude Code briefly holds the file.
            }
        }

        if (string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            throw new ClaudeCodeUsageException(
                "Claude Code's login has expired. Run `claude` to sign in again, then refresh.");
        }

        var refreshed = await RefreshCredentialsAsync(credentials, cancellationToken);
        _refreshedCredentials = refreshed;
        if (credentials.SourcePath is not null)
        {
            TryPersistRefreshedCredentials(refreshed);
        }

        return refreshed;
    }

    private static async Task<ClaudeCredentials> RefreshCredentialsAsync(
        ClaudeCredentials credentials,
        CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credentials.RefreshToken,
            ["client_id"] = OAuthClientId,
        };
        if (credentials.Scopes.Count > 0)
        {
            body["scope"] = string.Join(' ', credentials.Scopes);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("anthropic-beta", OAuthBeta);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        try
        {
            using var client = new HttpClient { Timeout = RequestTimeout };
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new ClaudeCodeUsageException(
                    "Claude Code's login could not be refreshed. Run `claude` to sign in again.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var accessToken = GetString(root, "access_token")?.Trim();
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new ClaudeCodeUsageException("Anthropic returned an empty OAuth access token.");
            }

            var refreshToken = GetString(root, "refresh_token")?.Trim();
            var expiresIn = GetDouble(root, "expires_in") ?? 3_600;
            var scopes = (GetString(root, "scope") ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return credentials with
            {
                AccessToken = accessToken,
                RefreshToken = string.IsNullOrWhiteSpace(refreshToken) ? credentials.RefreshToken : refreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn)),
                Scopes = scopes.Length == 0 ? credentials.Scopes : scopes,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ClaudeCodeUsageException("Claude Code's login refresh timed out.");
        }
        catch (ClaudeCodeUsageException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ClaudeCodeUsageException(
                $"Claude Code's login could not be refreshed: {exception.Message}",
                exception);
        }
    }

    private static ClaudeCredentials ParseCredentials(string json, string? sourcePath)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        var root = document.RootElement;
        var oauth = TryGetObject(root, "claudeAiOauth", out var nested) ? nested : root;
        var accessToken = GetString(oauth, "accessToken")?.Trim();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ClaudeCodeUsageException(
                "Claude Code's saved login has no OAuth access token. Run `claude` to sign in again.");
        }

        var scopes = Array.Empty<string>();
        if (oauth.TryGetProperty("scopes", out var scopeElement) && scopeElement.ValueKind == JsonValueKind.Array)
        {
            scopes = scopeElement.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToArray();
        }

        DateTimeOffset? expiresAt = null;
        if (GetDouble(oauth, "expiresAt") is { } expiresAtMilliseconds)
        {
            try
            {
                expiresAt = DateTimeOffset.FromUnixTimeMilliseconds((long)expiresAtMilliseconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                expiresAt = DateTimeOffset.MinValue;
            }
        }

        var planSource = GetString(oauth, "subscriptionType") ?? GetString(oauth, "rateLimitTier");
        return new ClaudeCredentials(
            accessToken,
            GetString(oauth, "refreshToken")?.Trim(),
            expiresAt,
            scopes,
            FormatPlan(planSource),
            sourcePath,
            GetString(oauth, "refreshToken")?.Trim() ?? accessToken);
    }

    private static void TryPersistRefreshedCredentials(ClaudeCredentials credentials)
    {
        var path = credentials.SourcePath;
        if (path is null || !File.Exists(path))
        {
            return;
        }

        string? temporaryPath = null;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            var oauth = root?["claudeAiOauth"] as JsonObject;
            if (root is null || oauth is null)
            {
                return;
            }

            oauth["accessToken"] = credentials.AccessToken;
            if (!string.IsNullOrWhiteSpace(credentials.RefreshToken))
            {
                oauth["refreshToken"] = credentials.RefreshToken;
            }

            if (credentials.ExpiresAt is { } expiresAt)
            {
                oauth["expiresAt"] = expiresAt.ToUnixTimeMilliseconds();
            }

            if (credentials.Scopes.Count > 0)
            {
                oauth["scopes"] = new JsonArray(
                    credentials.Scopes.Select(scope => JsonValue.Create(scope)).ToArray());
            }

            temporaryPath = Path.Combine(
                Path.GetDirectoryName(path)!,
                $".credentials.json.usageai-tmp.{Environment.ProcessId}.{Guid.NewGuid():N}");
            File.WriteAllText(temporaryPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, path, true);
            temporaryPath = null;
        }
        catch (IOException)
        {
            // The refreshed token remains cached for this UsageAI process.
        }
        catch (UnauthorizedAccessException)
        {
            // The refreshed token remains cached for this UsageAI process.
        }
        catch (JsonException)
        {
            // Do not replace a credential file that changed to an unexpected format.
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // A failed best-effort cleanup must not hide valid usage data.
                }
            }
        }
    }

    private static UsageWindow? ParseWindow(
        JsonElement root,
        string snakeCaseName,
        string camelCaseName,
        string displayName,
        long durationMinutes)
    {
        if (!TryGetObject(root, snakeCaseName, out var window) &&
            !TryGetObject(root, camelCaseName, out window))
        {
            return null;
        }

        var utilization = GetDouble(window, "utilization");
        if (utilization is null || !double.IsFinite(utilization.Value))
        {
            return null;
        }

        var usedPercent = NormalizeUtilization(utilization.Value);
        return new UsageWindow(
            displayName,
            usedPercent,
            ParseTimestamp(GetString(window, "resets_at") ?? GetString(window, "resetsAt")),
            durationMinutes);
    }

    private static UsageWindow? ParseWeeklyAllLimit(JsonElement root)
    {
        if (!root.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var limit in limits.EnumerateArray())
        {
            if (limit.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var kind = GetString(limit, "kind");
            var group = GetString(limit, "group");
            if (kind is not ("weekly_all" or "all_models" or "weekly_models") ||
                group is not null && !group.Equals("weekly", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var percent = GetDouble(limit, "percent");
            if (percent is null || !double.IsFinite(percent.Value))
            {
                continue;
            }

            return new UsageWindow(
                "Weekly",
                (int)Math.Round(Math.Clamp(percent.Value, 0, 100), MidpointRounding.AwayFromZero),
                ParseTimestamp(GetString(limit, "resets_at") ?? GetString(limit, "resetsAt")),
                10_080);
        }

        return null;
    }

    private static string? FormatExtraUsage(JsonElement root)
    {
        if ((!TryGetObject(root, "extra_usage", out var extraUsage) &&
             !TryGetObject(root, "extraUsage", out extraUsage)) ||
            !GetBoolean(extraUsage, "is_enabled", "isEnabled"))
        {
            return null;
        }

        var usedCents = GetDouble(extraUsage, "used_credits") ?? GetDouble(extraUsage, "usedCredits");
        var limitCents = GetDouble(extraUsage, "monthly_limit") ?? GetDouble(extraUsage, "monthlyLimit");
        if (usedCents is null)
        {
            return "Extra usage enabled";
        }

        var currency = GetString(extraUsage, "currency") ?? "USD";
        var used = FormatCurrency(usedCents.Value / 100, currency);
        return limitCents is null
            ? $"Extra usage  {used}"
            : $"Extra usage  {used} / {FormatCurrency(limitCents.Value / 100, currency)}";
    }

    private static int NormalizeUtilization(double utilization)
    {
        var percent = utilization > 0 && utilization <= 1 ? utilization * 100 : utilization;
        return (int)Math.Round(Math.Clamp(percent, 0, 100), MidpointRounding.AwayFromZero);
    }

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.ToLocalTime()
            : null;

    private static string FormatPlan(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan))
        {
            return "Claude";
        }

        var normalized = plan.Trim().ToLowerInvariant();
        if (normalized.Contains("claude_max_5x") || normalized.Contains("claude_max_5"))
        {
            return "Claude Max 5x";
        }

        if (normalized.Contains("claude_max_20x") || normalized.Contains("claude_max_20"))
        {
            return "Claude Max 20x";
        }

        return normalized switch
        {
            "free" => "Claude Free",
            "pro" or "claude_pro" => "Claude Pro",
            "max" => "Claude Max",
            "team" => "Claude Team",
            "enterprise" => "Claude Enterprise",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(plan.Replace('_', ' ').Trim()),
        };
    }

    private static string FormatCurrency(double value, string currency) =>
        currency.Equals("USD", StringComparison.OrdinalIgnoreCase)
            ? value.ToString("C2", CultureInfo.GetCultureInfo("en-US"))
            : $"{value:N2} {currency.ToUpperInvariant()}";

    private static string GetCredentialPath()
    {
        var configuredDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return Path.Combine(configuredDirectory.Trim().Trim('"'), ".credentials.json");
        }

        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(userProfile, ".claude", ".credentials.json");
    }

    private static bool TryGetObject(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value) && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
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

    private static bool GetBoolean(JsonElement element, string snakeCaseName, string camelCaseName) =>
        element.TryGetProperty(snakeCaseName, out var property) && property.ValueKind == JsonValueKind.True ||
        element.TryGetProperty(camelCaseName, out property) && property.ValueKind == JsonValueKind.True;

    private sealed record ClaudeCredentials(
        string AccessToken,
        string? RefreshToken,
        DateTimeOffset? ExpiresAt,
        IReadOnlyList<string> Scopes,
        string Plan,
        string? SourcePath,
        string SourceCredentialToken)
    {
        public bool IsExpired => ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow.AddMinutes(5);
    }
}

internal sealed class ClaudeCodeUsageException : Exception
{
    public ClaudeCodeUsageException(string message)
        : base(message)
    {
    }

    public ClaudeCodeUsageException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
