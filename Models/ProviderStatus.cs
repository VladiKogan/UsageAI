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
    Uri? AccountUrl = null,
    DateTimeOffset? LastAttemptedAt = null,
    DateTimeOffset? NextRetryAt = null)
{
    /// <summary>
    /// The incident the provider's own status page reports, such as "Degraded: Elevated errors on
    /// Claude Code", or null when there is none (or incidents are not shown).
    /// </summary>
    public string? IncidentSummary { get; init; }

    /// <summary>True when <see cref="IncidentSummary"/> describes an outage rather than degraded service.</summary>
    public bool IncidentIsOutage { get; init; }

    /// <summary>
    /// The account's label when its provider shows more than one account (then also part of
    /// <see cref="ProviderName"/>), else null.
    /// </summary>
    public string? AccountLabel { get; init; }

    /// <summary>The provider part of <see cref="ProviderId"/>, which may name an account (<c>claude@1a2b3c4d</c>).</summary>
    public string Kind => ProviderKey.ProviderOf(ProviderId);

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
