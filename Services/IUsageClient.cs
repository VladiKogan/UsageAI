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
