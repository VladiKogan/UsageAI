using UsageAI.Models;

namespace UsageAI.Services;

internal interface IUsageClient
{
    string Id { get; }

    string DisplayName { get; }

    /// <summary>The command a user runs to connect this provider, offered on error cards.</summary>
    string SignInCommand { get; }

    /// <summary>The provider's own usage page, opened from the dashboard.</summary>
    Uri AccountUrl { get; }

    Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Implemented by provider clients that negatively cache an expensive recovery path, so a refresh
/// the user asked for clears that cache instead of serving a degraded reading until it expires.
/// </summary>
internal interface IForcedRefreshAware
{
    void OnForcedRefresh();
}

/// <summary>
/// Implemented by provider exceptions that know when the provider will accept another
/// request, so the refresh schedule can honour it instead of guessing.
/// </summary>
internal interface IThrottledUsageException
{
    TimeSpan RetryAfter { get; }
}

/// <summary>
/// A client that reads one login of a provider that can have several (Claude Code, Codex). Its
/// <see cref="IUsageClient.Id"/> is the provider key: the bare provider id for the default login,
/// else <c>provider@account</c>.
/// </summary>
internal interface IAccountUsageClient : IUsageClient
{
    string ProviderId { get; }

    string AccountId { get; }

    /// <summary>The folder the login lives in, read for who is signed in.</summary>
    string LoginFolder { get; }
}
