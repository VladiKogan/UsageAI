using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using UsageAI.Models;

namespace UsageAI.Services;

internal static class ClaudeWebUsageClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly HttpClient Client = SecureHttp.CreateClient(RequestTimeout);
    private static readonly Uri AccountEndpoint = new("https://claude.ai/api/account");
    private static readonly Uri OrganizationsEndpoint = new("https://claude.ai/api/organizations");

    public static bool IsConfigured => GetSessionKey() is not null;

    public static async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken)
    {
        var sessionKey = GetSessionKey()
            ?? throw new ClaudeWebUsageException("Claude web session authentication is not configured.");

        try
        {
            using var account = await TryGetJsonAsync(AccountEndpoint, sessionKey, cancellationToken);
            var organizationId = account is null ? null : FindOrganizationId(account.RootElement);
            if (organizationId is null)
            {
                using var organizations = await GetJsonAsync(
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
            var usageEndpoint = new Uri($"https://claude.ai/api/organizations/{escapedOrganizationId}/usage");
            using var usage = await GetJsonAsync(usageEndpoint, sessionKey, cancellationToken);

            var plan = ClaudeCodeUsageClient.FormatPlan(
                account is null ? null : GetString(account.RootElement, "rate_limit_tier"));
            var snapshot = ClaudeCodeUsageClient.ParseSnapshot(usage.RootElement, plan) with
            {
                AccountName = account is null ? null : GetString(account.RootElement, "email_address"),
            };

            var overageEndpoint = new Uri(
                $"https://claude.ai/api/organizations/{escapedOrganizationId}/overage_spend_limit");
            using var overage = await TryGetJsonAsync(overageEndpoint, sessionKey, cancellationToken);
            if (overage is not null)
            {
                snapshot = snapshot with
                {
                    CreditBalance = ClaudeCodeUsageClient.FormatExtraUsageObject(overage.RootElement)
                                    ?? snapshot.CreditBalance,
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
        Uri endpoint,
        string sessionKey,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(endpoint, sessionKey);
        using var response = await Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

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

    private static async Task<JsonDocument?> TryGetJsonAsync(
        Uri endpoint,
        string sessionKey,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetJsonAsync(endpoint, sessionKey, cancellationToken);
        }
        catch (ClaudeWebUsageException)
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

internal sealed class ClaudeWebUsageException : Exception
{
    public ClaudeWebUsageException(string message)
        : base(message)
    {
    }

    public ClaudeWebUsageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
