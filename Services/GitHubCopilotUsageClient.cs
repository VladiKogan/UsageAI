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
    private static readonly HttpClient SharedClient = SecureHttp.CreateClient(RequestTimeout);
    private const int MaxTokenCandidates = 16;
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 32,
    };

    private readonly HashSet<string> _rejectedTokens = new(StringComparer.Ordinal);
    private readonly HttpClient _client;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _tokenProvider;
    private string? _workingToken;

    public GitHubCopilotUsageClient()
        : this(SharedClient, null)
    {
    }

    internal GitHubCopilotUsageClient(
        HttpClient client,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? tokenProvider = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _tokenProvider = tokenProvider ?? FindTokensAsync;
    }

    public string Id => "copilot";

    public string DisplayName => "GitHub Copilot";

    public string SignInCommand => "copilot";

    public Uri AccountUrl { get; } = new("https://github.com/settings/copilot/features");

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        var tokens = await _tokenProvider(cancellationToken);
        if (tokens.Count == 0)
        {
            throw new GitHubCopilotUsageException(
                "GitHub Copilot is not signed in. Sign in through Copilot, provide COPILOT_GITHUB_TOKEN, or explicitly enable the GitHub CLI fallback.");
        }

        Exception? lastFailure = null;
        var rejectedTokens = 0;

        foreach (var token in PrioritizeTokens(tokens))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, EndpointForToken(token));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.UserAgent.ParseAdd(AppIdentity.UserAgent);

                using var response = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    rejectedTokens++;
                    RememberRejectedToken(token);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new GitHubCopilotUsageException(
                        $"GitHub Copilot returned HTTP {(int)response.StatusCode} while reading usage.");
                }

                using var document = await SecureHttp.ReadJsonDocumentAsync(response, cancellationToken);
                _workingToken = token;
                return ParseSnapshot(document.RootElement);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastFailure = new GitHubCopilotUsageException("GitHub Copilot did not return usage data within 15 seconds.");
            }
            catch (GitHubCopilotUsageException exception) when (exception.Message == SubscriptionEndedMessage)
            {
                // GitHub answered for this account; another token would not make the subscription active.
                _workingToken = token;
                throw;
            }
            catch (GitHubCopilotUsageException exception)
            {
                lastFailure = exception;
            }
            catch (Exception exception)
            {
                lastFailure = new GitHubCopilotUsageException(
                    "GitHub Copilot returned invalid or unavailable usage data.",
                    exception);
            }
        }

        if (rejectedTokens == tokens.Count)
        {
            throw new GitHubCopilotUsageException(
                "The saved GitHub credentials cannot access Copilot. Sign in to GitHub Copilot again, then refresh.");
        }

        throw new GitHubCopilotUsageException(
            "Could not read GitHub Copilot usage because the provider returned invalid or unavailable data.",
            lastFailure);
    }

    /// <summary>The message for an account whose Copilot subscription GitHub reports as ended.</summary>
    internal const string SubscriptionEndedMessage =
        "Your GitHub Copilot subscription has ended. Renew it on GitHub, then refresh.";

    internal static UsageSnapshot ParseSnapshot(JsonElement root)
    {
        // GitHub answers an ended subscription with a normal reply and this SKU; without the check
        // it read as "account details without any quota information".
        if (string.Equals(GetString(root, "access_type_sku"), "subscription_ended", StringComparison.OrdinalIgnoreCase))
        {
            throw new GitHubCopilotUsageException(SubscriptionEndedMessage);
        }

        var resetAt = ParseReset(root);
        var windows = new List<(int Order, UsageMetric Window)>();

        if (root.TryGetProperty("quota_snapshots", out var snapshots) && snapshots.ValueKind == JsonValueKind.Object)
        {
            AddQuotaWindow(windows, snapshots, "premium_interactions", 0, resetAt);
            AddQuotaWindow(windows, snapshots, "chat", 1, resetAt);
            AddQuotaWindow(windows, snapshots, "completions", 2, resetAt);
        }

        // Metered quotas lead; unlimited ones are reported but never crowd out a real limit.
        var selected = windows
            .OrderBy(item => item.Window.IsUnlimited ? 1 : 0)
            .ThenBy(item => item.Order)
            .Select(item => item.Window)
            .ToArray();

        if (selected.Length == 0)
        {
            throw new GitHubCopilotUsageException(
                "GitHub Copilot returned account details without any quota information.");
        }

        return new UsageSnapshot(
            ResolvePlan(GetString(root, "copilot_plan"), GetString(root, "access_type_sku")),
            selected,
            DateTimeOffset.Now,
            "copilot",
            "GitHub Copilot",
            GetString(root, "login"));
    }

    private static void AddQuotaWindow(
        List<(int Order, UsageMetric Window)> windows,
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

        windows.Add((order, new UsageMetric(
            name,
            UsageMetricKind.Monthly,
            usedPercent,
            resetAt,
            null,
            remainingText,
            usageText,
            unlimited)));
    }

    /// <summary>
    /// Tries the token that last worked first and skips ones GitHub already rejected, so a
    /// refresh does not replay every discovered credential against GitHub every five minutes.
    /// </summary>
    private List<string> PrioritizeTokens(IReadOnlyList<string> tokens)
    {
        var candidates = tokens.Where(token => !_rejectedTokens.Contains(token)).ToList();
        if (candidates.Count == 0)
        {
            // Every known token was rejected before; a new sign-in may have revived one.
            _rejectedTokens.Clear();
            candidates = tokens.ToList();
        }

        if (_workingToken is { } working && candidates.Remove(working))
        {
            candidates.Insert(0, working);
        }

        return candidates;
    }

    private void RememberRejectedToken(string token)
    {
        _rejectedTokens.Add(token);
        if (string.Equals(_workingToken, token, StringComparison.Ordinal))
        {
            _workingToken = null;
        }
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

        if (IsGitHubCliFallbackEnabled())
        {
            AddToken(tokens, await TryReadGitHubCliTokenAsync(cancellationToken));
        }

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
            var json = SecureLocalFile.ReadAllText(path);
            using var document = JsonDocument.Parse(json, JsonOptions);
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
        catch (InvalidDataException)
        {
            // Ignore oversized or otherwise invalid optional credential files.
        }
    }

    /// <summary>
    /// The host each discovered token belongs to, for tokens of a GitHub Enterprise Cloud
    /// (<c>*.ghe.com</c>) login. Every other token is a github.com token.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> TokenHosts =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Where a token's usage is read: <c>api.github.com</c> for github.com, <c>api.&lt;host&gt;</c>
    /// for a GitHub Enterprise Cloud host (<c>*.ghe.com</c>), and nowhere for any other host, so a
    /// token is only ever sent to the GitHub service that issued it.
    /// </summary>
    internal static Uri? UserEndpointFor(string? host)
    {
        var normalized = NormalizeHost(host);
        if (normalized is null || normalized == "github.com")
        {
            return normalized is null ? null : UserEndpoint;
        }

        return normalized.EndsWith(".ghe.com", StringComparison.Ordinal) &&
               normalized.Length > ".ghe.com".Length
            ? new Uri($"https://api.{normalized}/copilot_internal/user")
            : null;
    }

    private static Uri EndpointForToken(string token) =>
        TokenHosts.TryGetValue(token, out var host) && UserEndpointFor(host) is { } endpoint
            ? endpoint
            : UserEndpoint;

    /// <summary>
    /// The host a credential key names, or null when the key is not a host at all. Only keys that
    /// look like one count: a URL, <c>host:appId</c> (apps.json), or a bare host whose object holds
    /// the token itself (hosts.json), so an ordinary dotted setting name never redirects a token.
    /// </summary>
    internal static string? HostFromKey(string key, bool holdsToken) =>
        key.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        key.Contains(':', StringComparison.Ordinal) ||
        holdsToken
            ? NormalizeHost(key)
            : null;

    /// <summary>A bare lower-case DNS host from a value such as <c>https://octocorp.ghe.com/</c>.</summary>
    internal static string? NormalizeHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var host = value.Trim().ToLowerInvariant();
        foreach (var scheme in new[] { "https://", "http://" })
        {
            if (host.StartsWith(scheme, StringComparison.Ordinal))
            {
                host = host[scheme.Length..];
            }
        }

        var end = host.IndexOfAny(['/', ':', '?', '#']);
        host = end < 0 ? host : host[..end];
        return host.Length is > 3 and <= 253 &&
               host.Contains('.', StringComparison.Ordinal) &&
               host.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.') &&
               !host.StartsWith('.') && !host.EndsWith('.')
            ? host
            : null;
    }

    /// <summary>
    /// Collects tokens, remembering the host each belongs to where the file says so: an
    /// <c>apps.json</c> key is <c>host:appId</c>, a <c>hosts.json</c> key is the host, and a Copilot
    /// CLI <c>copilotTokens</c> key starts with the host URL. A token without a host is github.com's.
    /// </summary>
    private static void AddTokensFromElement(ISet<string> tokens, JsonElement element, string? host = null)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("oauth_token") && property.Value.ValueKind == JsonValueKind.String)
                    {
                        AddToken(tokens, property.Value.GetString(), host);
                    }
                    else if (property.NameEquals("copilotTokens") && property.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var savedToken in property.Value.EnumerateObject())
                        {
                            if (savedToken.Value.ValueKind == JsonValueKind.String)
                            {
                                AddToken(tokens, savedToken.Value.GetString(), HostFromKey(savedToken.Name, holdsToken: true) ?? host);
                            }
                        }
                    }
                    else if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        var holdsToken = property.Value.TryGetProperty("oauth_token", out _);
                        AddTokensFromElement(tokens, property.Value, HostFromKey(property.Name, holdsToken) ?? host);
                    }
                }

                break;
        }
    }

    private static async Task<string?> TryReadGitHubCliTokenAsync(CancellationToken cancellationToken)
    {
        var executable = ProcessSecurity.FindAbsoluteExecutableOnPath("gh.exe");
        if (executable is null)
        {
            return null;
        }

        Process? process = null;
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
            ProcessSecurity.ApplyMinimalEnvironment(
                startInfo,
                "GH_CONFIG_DIR",
                "NODE_EXTRA_CA_CERTS",
                "SSL_CERT_DIR",
                "SSL_CERT_FILE");
            startInfo.ArgumentList.Add("auth");
            startInfo.ArgumentList.Add("token");
            startInfo.ArgumentList.Add("--hostname");
            startInfo.ArgumentList.Add("github.com");

            process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var outputTask = ProcessSecurity.DrainTextAsync(
                process.StandardOutput,
                CredentialInput.MaxTokenCharacters,
                timeout.Token);
            var errorTask = ProcessSecurity.DrainTextAsync(
                process.StandardError,
                4096,
                timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await errorTask;
            return process.ExitCode == 0 ? CredentialInput.NormalizeToken(await outputTask) : null;
        }
        catch
        {
            ProcessSecurity.TryKill(process);
            return null;
        }
        finally
        {
            ProcessSecurity.TryKill(process);
            process?.Dispose();
        }
    }

    private static void AddToken(ISet<string> tokens, string? token, string? host = null)
    {
        if (tokens.Count >= MaxTokenCandidates)
        {
            return;
        }

        var normalized = CredentialInput.NormalizeToken(token);
        if (normalized is null)
        {
            return;
        }

        // A token for a host UsageAI does not read from (such as a self-hosted GitHub Enterprise
        // Server) is left out entirely rather than sent to github.com.
        var tokenHost = NormalizeHost(host) ?? "github.com";
        if (UserEndpointFor(tokenHost) is null)
        {
            return;
        }

        tokens.Add(normalized);
        if (tokenHost != "github.com")
        {
            TokenHosts[normalized] = tokenHost;
        }
    }

    private static bool IsGitHubCliFallbackEnabled()
    {
        var value = Environment.GetEnvironmentVariable("USAGEAI_ENABLE_GH_TOKEN_FALLBACK");
        return value is not null &&
               (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase));
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

    /// <summary>
    /// GitHub reports <c>copilot_plan</c> as "individual" for Free, Pro and Pro+ alike, so a personal
    /// plan is named from <c>access_type_sku</c> when that SKU is one we recognise. Every other plan,
    /// and an unrecognised SKU, keeps the reported plan name.
    /// </summary>
    internal static string ResolvePlan(string? copilotPlan, string? accessTypeSku)
    {
        var individual = copilotPlan is null
            || copilotPlan.Trim().Equals("individual", StringComparison.OrdinalIgnoreCase);
        if (individual && IndividualPlanFromSku(accessTypeSku) is { } fromSku)
        {
            return fromSku;
        }

        return FormatPlan(copilotPlan ?? accessTypeSku ?? "Copilot");
    }

    private static string? IndividualPlanFromSku(string? accessTypeSku)
    {
        var sku = accessTypeSku?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(sku))
        {
            return null;
        }

        if (sku.StartsWith("plus_", StringComparison.Ordinal))
        {
            return "Pro+";
        }

        return sku switch
        {
            "free_limited_copilot" => "Free",
            // Paid monthly or yearly, or granted free to verified teachers, students and maintainers.
            "monthly_subscriber_quota" or "yearly_subscriber_quota" or "free_faculty_quota"
                or "free_educational_quota" or "free_engaged_oss_quota" => "Pro",
            _ => null,
        };
    }

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
