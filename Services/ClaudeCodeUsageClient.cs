using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using UsageAI.Models;

namespace UsageAI.Services;

internal sealed class ClaudeCodeUsageClient : IUsageClient
{
    private const string CredentialManagerService = "Claude Code-credentials";
    private const string OAuthBeta = "oauth-2025-04-20";
    private static readonly Uri UsageEndpoint = new("https://api.anthropic.com/api/oauth/usage");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly HttpClient SharedClient = SecureHttp.CreateClient(RequestTimeout);
    private readonly HttpClient _client;
    private readonly Func<IReadOnlyList<string>> _keyringPasswords;

    public ClaudeCodeUsageClient()
        : this(SharedClient, null)
    {
    }

    internal ClaudeCodeUsageClient(
        HttpClient client,
        Func<IReadOnlyList<string>>? keyringPasswords = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _keyringPasswords = keyringPasswords ??
            (() => WindowsCredentialReader.FindKeyringPasswords(CredentialManagerService));
    }

    public string Id => "claude";

    public string DisplayName => "Claude Code";

    public string SignInCommand => "claude";

    public Uri AccountUrl { get; } = new("https://claude.ai/settings/usage");

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        if (ClaudeWebUsageClient.IsConfigured)
        {
            try
            {
                return await ClaudeWebUsageClient.GetUsageAsync(cancellationToken);
            }
            catch (ClaudeWebUsageException)
            {
                // An expired or unavailable opt-in web session falls back to Claude Code OAuth.
            }
        }

        var credentials = LoadCredentials();
        if (credentials.IsExpired)
        {
            throw new ClaudeCodeUsageException(
                "Claude Code's login has expired. Open `claude` so Claude Code can refresh it, then refresh UsageAI.");
        }

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
        request.Headers.UserAgent.ParseAdd(AppIdentity.UserAgent);

        try
        {
            using var response = await _client.SendAsync(
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
                var retryAfter = response.Headers.RetryAfter?.Delta;
                var retryText = retryAfter is { } delta
                    ? $" Try again in about {Math.Max(1, (int)Math.Ceiling(delta.TotalSeconds))} seconds."
                    : string.Empty;
                throw new ClaudeCodeUsageException($"Anthropic rate-limited the usage request.{retryText}")
                {
                    RetryAfter = retryAfter ?? TimeSpan.Zero,
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ClaudeCodeUsageException(
                    $"Claude Code returned HTTP {(int)response.StatusCode} while reading usage.");
            }

            using var document = await SecureHttp.ReadJsonDocumentAsync(response, cancellationToken);
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
            throw new ClaudeCodeUsageException(
                "Could not read Claude Code usage because the provider returned invalid or unavailable data.",
                exception);
        }
    }

    internal static UsageSnapshot ParseSnapshot(JsonElement root, string plan = "Claude")
    {
        var session = ParseWindow(root, "five_hour", "fiveHour", "5-hour", UsageMetricKind.Session, 300);
        var weekly = ParseWeeklyAllLimit(root) ??
                     ParseWindow(root, "seven_day", "sevenDay", "Weekly", UsageMetricKind.Rolling, 10_080);

        if (session is null && weekly is null)
        {
            throw new ClaudeCodeUsageException(
                "Claude Code returned account details without five-hour or weekly usage data.");
        }

        var metrics = new List<UsageMetric>();
        if (session is not null)
        {
            metrics.Add(session);
        }

        if (weekly is not null)
        {
            metrics.Add(weekly);
        }

        var opusWeekly = ParseNamedLimit(root, "weekly_opus", "Weekly Opus");
        if (opusWeekly is not null)
        {
            metrics.Add(opusWeekly);
        }

        var extraUsage = ParseExtraUsage(root);
        if (extraUsage is not null)
        {
            metrics.Add(extraUsage);
        }

        return new UsageSnapshot(
            plan,
            metrics,
            DateTimeOffset.Now,
            "claude",
            "Claude Code");
    }

    private ClaudeCredentials LoadCredentials()
    {
        var environmentToken = CredentialInput.NormalizeToken(
            Environment.GetEnvironmentVariable("USAGEAI_CLAUDE_OAUTH_TOKEN"));
        if (environmentToken is not null)
        {
            var scopes = (Environment.GetEnvironmentVariable("USAGEAI_CLAUDE_OAUTH_SCOPES") ?? "user:profile")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(scope => scope.Length <= 128)
                .Take(32)
                .ToArray();
            return new ClaudeCredentials(
                environmentToken,
                null,
                scopes,
                "Claude (OAuth)");
        }

        Exception? fileError = null;
        var credentialPath = GetCredentialPath();
        if (File.Exists(credentialPath))
        {
            try
            {
                return ParseCredentials(SecureLocalFile.ReadAllText(credentialPath));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ClaudeCodeUsageException)
            {
                fileError = exception;
            }
        }

        foreach (var savedCredential in _keyringPasswords())
        {
            try
            {
                return ParseCredentials(savedCredential);
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

    private static ClaudeCredentials ParseCredentials(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 32,
        });
        var root = document.RootElement;
        var oauth = TryGetObject(root, "claudeAiOauth", out var nested) ? nested : root;
        var accessToken = CredentialInput.NormalizeToken(GetString(oauth, "accessToken"));
        if (accessToken is null)
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
                .Select(value => value!.Trim())
                .Where(value => value.Length <= 128)
                .Take(32)
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
            expiresAt,
            scopes,
            FormatPlan(planSource));
    }

    private static UsageMetric? ParseWindow(
        JsonElement root,
        string snakeCaseName,
        string camelCaseName,
        string displayName,
        UsageMetricKind kind,
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
        return new UsageMetric(
            displayName,
            kind,
            usedPercent,
            ParseTimestamp(GetString(window, "resets_at") ?? GetString(window, "resetsAt")),
            durationMinutes);
    }

    private static UsageMetric? ParseWeeklyAllLimit(JsonElement root) =>
        FindLimit(
            root,
            kind => kind is "weekly_all" or "all_models" or "weekly_models",
            "Weekly",
            requireWeeklyGroup: true);

    private static UsageMetric? ParseNamedLimit(JsonElement root, string limitKind, string displayName) =>
        FindLimit(
            root,
            kind => string.Equals(kind, limitKind, StringComparison.Ordinal),
            displayName,
            requireWeeklyGroup: false);

    private static UsageMetric? FindLimit(
        JsonElement root,
        Func<string, bool> matchesKind,
        string displayName,
        bool requireWeeklyGroup)
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
            if (kind is null || !matchesKind(kind))
            {
                continue;
            }

            var group = GetString(limit, "group");
            if (requireWeeklyGroup && group is not null &&
                !group.Equals("weekly", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var percent = GetDouble(limit, "percent");
            if (percent is null || !double.IsFinite(percent.Value))
            {
                continue;
            }

            return new UsageMetric(
                displayName,
                UsageMetricKind.Rolling,
                (int)Math.Round(Math.Clamp(percent.Value, 0, 100), MidpointRounding.AwayFromZero),
                ParseTimestamp(GetString(limit, "resets_at") ?? GetString(limit, "resetsAt")),
                10_080);
        }

        return null;
    }

    private static UsageMetric? ParseExtraUsage(JsonElement root)
    {
        if (!TryGetObject(root, "extra_usage", out var extraUsage) &&
            !TryGetObject(root, "extraUsage", out extraUsage))
        {
            return null;
        }

        return CreateExtraUsageMetric(extraUsage);
    }

    /// <summary>Turns an extra-usage or overage-spend object into a balance metric.</summary>
    internal static UsageMetric? CreateExtraUsageMetric(JsonElement extraUsage)
    {
        var value = FormatExtraUsageObject(extraUsage);
        return value is null
            ? null
            : new UsageMetric(
                "Extra usage",
                UsageMetricKind.Balance,
                null,
                RemainingText: value,
                UsageText: "Charged beyond the plan limit");
    }

    internal static string? FormatExtraUsageObject(JsonElement extraUsage)
    {
        if (extraUsage.ValueKind != JsonValueKind.Object ||
            !GetBoolean(extraUsage, "is_enabled", "isEnabled"))
        {
            return null;
        }

        var usedCents = GetDouble(extraUsage, "used_credits") ?? GetDouble(extraUsage, "usedCredits");
        var limitCents = GetDouble(extraUsage, "monthly_credit_limit") ??
                         GetDouble(extraUsage, "monthly_limit") ??
                         GetDouble(extraUsage, "monthlyLimit");
        if (usedCents is null)
        {
            return "ENABLED";
        }

        var currency = GetString(extraUsage, "currency") ?? "USD";
        var used = FormatCurrency(usedCents.Value / 100, currency);
        return limitCents is null
            ? used
            : $"{used} / {FormatCurrency(limitCents.Value / 100, currency)}";
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

    internal static string FormatPlan(string? plan)
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
            configuredDirectory = configuredDirectory.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(configuredDirectory))
            {
                throw new ClaudeCodeUsageException("CLAUDE_CONFIG_DIR must be an absolute path.");
            }

            return Path.Combine(Path.GetFullPath(configuredDirectory), ".credentials.json");
        }

        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.GetFullPath(Path.Combine(userProfile, ".claude", ".credentials.json"));
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

    internal sealed record ClaudeCredentials(
        string AccessToken,
        DateTimeOffset? ExpiresAt,
        IReadOnlyList<string> Scopes,
        string Plan)
    {
        public bool IsExpired => ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow;
    }
}

internal sealed class ClaudeCodeUsageException : Exception, IThrottledUsageException
{
    public ClaudeCodeUsageException(string message)
        : base(message)
    {
    }

    public ClaudeCodeUsageException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Set from the provider's Retry-After header; zero when the provider did not say.</summary>
    public TimeSpan RetryAfter { get; init; }
}
