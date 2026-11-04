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

    /// <summary>The primary quota value. It moves in the same direction as the consumption meter.</summary>
    [JsonIgnore]
    public string DisplayUsed => IsUnlimited
        ? "UNLIMITED"
        : UsedPercent is { } used
            ? $"{Math.Clamp(used, 0, 100)}% USED"
            : DisplayRemaining;

    /// <summary>The secondary value describing the capacity still available.</summary>
    [JsonIgnore]
    public string DisplayRemaining =>
        RemainingText ?? (IsUnlimited ? "UNLIMITED" : $"{RemainingPercent}% LEFT");

    /// <summary>
    /// The percentage a meter fills to and its headline states. The two always come from here
    /// together, so in remaining mode the number and the bar flip as one.
    /// </summary>
    public int DisplayedPercent(QuotaDisplay display) =>
        display == QuotaDisplay.Remaining ? RemainingPercent : Math.Clamp(UsedPercent ?? 0, 0, 100);

    /// <summary>The headline value in the chosen quota display.</summary>
    public string DisplayPrimary(QuotaDisplay display) =>
        display == QuotaDisplay.Remaining && HasQuota
            ? $"{RemainingPercent}% LEFT"
            : DisplayUsed;

    /// <summary>
    /// Supporting text in the chosen quota display: the opposite side of the headline, so a row
    /// never repeats the same number twice.
    /// </summary>
    public string DisplaySecondaryFor(QuotaDisplay display) =>
        display == QuotaDisplay.Remaining && HasQuota
            ? UsageText ?? $"{Math.Clamp(UsedPercent!.Value, 0, 100)}% USED"
            : DisplaySecondary;

    [JsonIgnore]
    public string DisplayUsage =>
        UsageText ?? (IsUnlimited
            ? "No limit reported"
            : UsedPercent is { } used
                ? $"{used}% used"
                : "Not reported");

    /// <summary>
    /// Supporting card text: provider-specific quota detail when available, otherwise the
    /// remaining percentage; non-quota metrics retain their descriptive usage text.
    /// </summary>
    [JsonIgnore]
    public string DisplaySecondary => HasQuota
        ? UsageText ?? DisplayRemaining
        : DisplayUsage;

    /// <summary>Stable identity for history samples and alert de-duplication.</summary>
    [JsonIgnore]
    public string Key => $"{Kind}:{Name}";
}

/// <param name="Source">
/// Which retrieval path produced the reading, for providers with more than one, such as Gemini's
/// <c>antigravity-ide</c>, <c>antigravity-extension-hub</c>, <c>usageai-hub</c>, <c>agy-print</c> and
/// <c>gemini-cli</c>. It appears in <c>--diagnose</c> output only and is never shown in the UI.
/// </param>
internal sealed record UsageSnapshot(
    string Plan,
    IReadOnlyList<UsageMetric> Metrics,
    DateTimeOffset FetchedAt,
    string ProviderId = "codex",
    string ProviderName = "Codex",
    string? AccountName = null,
    string? Source = null)
{
    private static readonly JsonSerializerOptions DiagnosticJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The metric shown in the compact view: the first real quota, else the first balance.</summary>
    [JsonIgnore]
    public UsageMetric? Primary =>
        Metrics.FirstOrDefault(metric => metric.HasQuota) ?? (Metrics.Count > 0 ? Metrics[0] : null);

    [JsonIgnore]
    public int HighestUsedPercent => Metrics
        .Where(metric => metric.HasQuota)
        .Select(metric => metric.UsedPercent!.Value)
        .DefaultIfEmpty(0)
        .Max();

    public string ToDiagnosticJson() => JsonSerializer.Serialize(this, DiagnosticJsonOptions);
}
