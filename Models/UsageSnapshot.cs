using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsageAI.Models;

/// <summary>
/// Describes what a metric measures so the UI can order, forecast, and alert on it
/// without knowing which provider produced it.
/// </summary>
internal enum UsageMetricKind
{
    /// <summary>A short rolling window, such as a five-hour session limit.</summary>
    Session,

    /// <summary>A longer rolling window, such as a weekly limit.</summary>
    Rolling,

    /// <summary>A quota that refills on a billing or calendar cycle.</summary>
    Monthly,

    /// <summary>A balance or allowance without a percentage, such as credits.</summary>
    Balance,
}

/// <summary>
/// One provider-reported measurement. Providers report as many of these as they expose,
/// so a provider is never limited to a fixed pair of windows.
/// </summary>
internal sealed record UsageMetric(
    string Name,
    UsageMetricKind Kind,
    int? UsedPercent,
    DateTimeOffset? ResetsAt = null,
    long? DurationMinutes = null,
    string? RemainingText = null,
    string? UsageText = null,
    bool IsUnlimited = false)
{
    /// <summary>True when the metric carries a usable percentage that can be metered and forecast.</summary>
    [JsonIgnore]
    public bool HasQuota => UsedPercent is not null && !IsUnlimited;

    [JsonIgnore]
    public int RemainingPercent => Math.Clamp(100 - (UsedPercent ?? 0), 0, 100);

    /// <summary>The headline value. Both this and the meter fill encode remaining capacity.</summary>
    [JsonIgnore]
    public string DisplayRemaining =>
        RemainingText ?? (IsUnlimited ? "UNLIMITED" : $"{RemainingPercent}% LEFT");

    [JsonIgnore]
    public string DisplayUsage =>
        UsageText ?? (IsUnlimited
            ? "No limit reported"
            : UsedPercent is { } used
                ? $"{used}% used"
                : "Not reported");

    /// <summary>Stable identity for history samples and alert de-duplication.</summary>
    [JsonIgnore]
    public string Key => $"{Kind}:{Name}";
}

internal sealed record UsageSnapshot(
    string Plan,
    IReadOnlyList<UsageMetric> Metrics,
    DateTimeOffset FetchedAt,
    string ProviderId = "codex",
    string ProviderName = "Codex",
    string? AccountName = null)
{
    private static readonly JsonSerializerOptions DiagnosticJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The metric shown in the compact view: the first real quota, else the first balance.</summary>
    [JsonIgnore]
    public UsageMetric? Primary =>
        Metrics.FirstOrDefault(metric => metric.HasQuota) ?? Metrics.FirstOrDefault();

    [JsonIgnore]
    public int HighestUsedPercent => Metrics
        .Where(metric => metric.HasQuota)
        .Select(metric => metric.UsedPercent!.Value)
        .DefaultIfEmpty(0)
        .Max();

    public string ToDiagnosticJson() => JsonSerializer.Serialize(this, DiagnosticJsonOptions);
}
