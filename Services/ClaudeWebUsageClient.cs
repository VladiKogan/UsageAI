using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using UsageAI.Models;

namespace UsageAI.Services;

internal static class ClaudeWebUsageClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly HttpClient SharedClient = SecureHttp.CreateClient(RequestTimeout);
    private static readonly Uri AccountEndpoint = new("https://claude.ai/api/account");
    private static readonly Uri OrganizationsEndpoint = new("https://claude.ai/api/organizations");

    public static bool IsConfigured => GetSessionKey() is not null;

    public static Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken) =>
        GetUsageAsync(SharedClient, cancellationToken);

    internal static async Task<UsageSnapshot> GetUsageAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var sessionKey = GetSessionKey()
            ?? throw new ClaudeWebUsageException("Claude web session authentication is not configured.");

        try
        {
            using var account = await TryGetJsonAsync(client, AccountEndpoint, sessionKey, cancellationToken);
            var organizationId = account is null ? null : FindOrganizationId(account.RootElement);
            if (organizationId is null)
            {
                using var organizations = await GetJsonAsync(
                    client,
                    OrganizationsEndpoint,
                    sessionKey,
                    cancellationToken);
                organizationId = FindFirstOrganizationId(organizations.RootElement);
            }

            if (string.IsNullOrWhiteSpace(organizationId))
            {
                throw new ClaudeWebUsageException("Claude did not return an account organization.");
            }

            var escapedOrganizationId = Uri.EscapeDataString(organizationId);
            // Saved limit-reset credits are reported only when asked for; a rejection of the
            // parameter is retried once without it, so usage never depends on it.
            var usageEndpoint = new Uri($"https://claude.ai/api/organizations/{escapedOrganizationId}/usage");
            using var usage =
                await TryGetJsonAsync(client, new Uri($"{usageEndpoint}?cedar_ember=1"), sessionKey, cancellationToken, retryOnly: true) ??
                await GetJsonAsync(client, usageEndpoint, sessionKey, cancellationToken);

            var plan = ClaudeCodeUsageClient.FormatPlan(
                account is null ? null : GetString(account.RootElement, "rate_limit_tier"));
            var snapshot = ClaudeCodeUsageClient.ParseSnapshot(usage.RootElement, plan) with
            {
                AccountName = account is null ? null : GetString(account.RootElement, "email_address"),
            };

            var overageEndpoint = new Uri(
                $"https://claude.ai/api/organizations/{escapedOrganizationId}/overage_spend_limit");
            using var overage = await TryGetJsonAsync(
                client,
                overageEndpoint,
                sessionKey,
                cancellationToken);
            if (overage is not null &&
                ClaudeCodeUsageClient.CreateExtraUsageMetric(overage.RootElement) is { } overageMetric)
            {
                // The dedicated overage endpoint is more current than the usage payload's copy. Only
                // that copy is replaced; reset credits and other balances stay.
                snapshot = snapshot with
                {
                    Metrics = snapshot.Metrics
                        .Where(metric => !string.Equals(metric.Name, overageMetric.Name, StringComparison.Ordinal))
                        .Append(overageMetric)
                        .ToArray(),
                };
            }

            return snapshot;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ClaudeWebUsageException("Claude web usage timed out.");
        }
        catch (ClaudeWebUsageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException)
        {
            throw new ClaudeWebUsageException("Claude web usage returned invalid or unavailable data.", exception);
        }
    }

    private static async Task<JsonDocument> GetJsonAsync(
        HttpClient client,
        Uri endpoint,
        string sessionKey,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(endpoint, sessionKey);
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        // Cloudflare's browser check blocks the request before claude.ai sees the session, so it
        // says nothing about the session key: the key is kept and the card is only marked stale.
        if (IsCloudflareChallenge(response))
        {
            throw new ClaudeWebUsageException(
                "claude.ai asked for a browser check (Cloudflare) instead of answering. The session key is kept; UsageAI will try again later.")
            {
                RetryAfter = CloudflareRetryAfter,
            };
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ||
            (int)response.StatusCode is >= 300 and < 400)
        {
            throw new ClaudeWebUsageException(
                "Claude's saved web session is no longer valid. Refresh USAGEAI_CLAUDE_SESSION_KEY or use Claude Code OAuth.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ClaudeWebUsageException(
                $"Claude web usage returned HTTP {(int)response.StatusCode}.");
        }

        return await SecureHttp.ReadJsonDocumentAsync(response, cancellationToken);
    }

    /// <summary>How long to leave claude.ai alone after its Cloudflare browser check.</summary>
    private static readonly TimeSpan CloudflareRetryAfter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// True when Cloudflare answered with its challenge page: flagged by <c>cf-mitigated</c>, or a
    /// 403/429/503 HTML page from Cloudflare instead of claude.ai's JSON.
    /// </summary>
    internal static bool IsCloudflareChallenge(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("cf-mitigated", out var mitigated) &&
            mitigated.Any(value => value.Contains("challenge", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable &&
               string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase) &&
               response.Headers.Server.Any(server => string.Equals(server.Product?.Name, "cloudflare", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The JSON at <paramref name="endpoint"/>, or null when it fails. With
    /// <paramref name="retryOnly"/> a Cloudflare challenge is not swallowed, because asking again
    /// without the optional parameter would only meet the same challenge.
    /// </summary>
    private static async Task<JsonDocument?> TryGetJsonAsync(
        HttpClient client,
        Uri endpoint,
        string sessionKey,
        CancellationToken cancellationToken,
        bool retryOnly = false)
    {
        try
        {
            return await GetJsonAsync(client, endpoint, sessionKey, cancellationToken);
        }
        catch (ClaudeWebUsageException exception) when (!retryOnly || exception.RetryAfter == TimeSpan.Zero)
        {
            return null;
        }
    }

    private static HttpRequestMessage CreateRequest(Uri endpoint, string sessionKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("Cookie", $"sessionKey={sessionKey}");
        request.Headers.Add("Origin", "https://claude.ai");
        request.Headers.Referrer = new Uri("https://claude.ai/settings/usage");
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/144.0.0.0 Safari/537.36");
        request.Headers.UserAgent.ParseAdd(AppIdentity.UserAgent);
        request.Headers.Add("anthropic-client-platform", "web_claude_ai");
        return request;
    }

    private static string? GetSessionKey()
    {
        foreach (var variableName in new[]
                 {
                     "USAGEAI_CLAUDE_SESSION_KEY",
                     "CLAUDE_AI_SESSION_KEY",
                     "CLAUDE_WEB_SESSION_KEY",
                 })
        {
            var value = NormalizeSessionKey(Environment.GetEnvironmentVariable(variableName));
            if (value is null)
            {
                continue;
            }

            return value;
        }

        return null;
    }

    internal static string? NormalizeSessionKey(string? rawValue)
    {
        var value = CredentialInput.NormalizeToken(rawValue);
        if (value is null)
        {
            return null;
        }

        const string prefix = "sessionKey=";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[prefix.Length..].Trim();
        }

        return value.Length > 0 && !value.Contains(';') ? value : null;
    }

    internal static string? FindOrganizationId(JsonElement account)
    {
        if (!account.TryGetProperty("memberships", out var memberships) ||
            memberships.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var membership in memberships.EnumerateArray())
        {
            if (membership.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (membership.TryGetProperty("organization", out var organization) &&
                organization.ValueKind == JsonValueKind.Object &&
                GetString(organization, "uuid") is { Length: > 0 } nestedId)
            {
                return nestedId;
            }

            if (GetString(membership, "uuid") is { Length: > 0 } membershipId)
            {
                return membershipId;
            }
        }

        return null;
    }

    private static string? FindFirstOrganizationId(JsonElement organizations)
    {
        if (organizations.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var organization in organizations.EnumerateArray())
        {
            if (organization.ValueKind == JsonValueKind.Object &&
                GetString(organization, "uuid") is { Length: > 0 } id)
            {
                return id;
            }
        }

        return null;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim()
            : null;
}

internal sealed class ClaudeWebUsageException : Exception, IThrottledUsageException
{
    /// <summary>How long to wait before asking claude.ai again; zero unless it asked for a pause.</summary>
    public TimeSpan RetryAfter { get; init; }

    public ClaudeWebUsageException(string message)
        : base(message)
    {
    }

    public ClaudeWebUsageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
