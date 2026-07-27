namespace UsageAI.Models;

/// <summary>
/// What the UI knows about one provider right now. A failed refresh keeps the previous
/// snapshot and adds an error, so a transient outage marks the card stale instead of
/// emptying the dashboard.
/// </summary>
internal sealed record ProviderStatus(
    string ProviderId,
    string ProviderName,
    UsageSnapshot? Snapshot,
    string? Error,
    bool IsLoading,
    DateTimeOffset? LastUpdated = null,
    string? SignInCommand = null,
    Uri? AccountUrl = null)
{
    public bool IsConnected => Snapshot is not null;

    /// <summary>Connected, but the most recent refresh failed and the values are old.</summary>
    public bool IsStale => Snapshot is not null && !string.IsNullOrWhiteSpace(Error);

    public string StatusText => IsLoading
        ? "Refreshing"
        : IsStale
            ? "Stale"
            : IsConnected
                ? "Connected"
                : "Not connected";
}
