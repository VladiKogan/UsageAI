using UsageAI.Models;

namespace UsageAI.Services;

/// <summary>A projected exhaustion time derived from recent samples of one metric.</summary>
internal sealed record UsageProjection(double PercentPerHour, DateTimeOffset ExhaustedAt, bool BeforeReset);

/// <summary>
/// Turns recorded history into a burn rate. The estimate deliberately uses only the run of
/// samples since the last reset, so a window rollover cannot flatten the slope.
/// </summary>
internal static class UsageForecast
{
    private const int MinimumSamples = 3;
    private const double MinimumPercentPerHour = 0.05;
    private const double MaximumProjectionHours = 24 * 30;
    private static readonly TimeSpan MinimumSpan = TimeSpan.FromMinutes(20);

    /// <summary>A drop of more than this many points means the window reset between samples.</summary>
    private const int ResetDropPoints = 5;

    public static UsageProjection? Project(
        IReadOnlyList<UsageSample> samples,
        string providerId,
        UsageMetric metric,
        DateTimeOffset now)
    {
        if (!metric.HasQuota)
        {
            return null;
        }

        var used = metric.UsedPercent!.Value;
        if (used >= 100)
        {
            return null;
        }

        var series = samples
            .Where(sample =>
                string.Equals(sample.ProviderId, providerId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(sample.MetricKey, metric.Key, StringComparison.Ordinal))
            .OrderBy(sample => sample.At)
            .ToList();

        var runStart = 0;
        for (var index = 1; index < series.Count; index++)
        {
            if (series[index].UsedPercent < series[index - 1].UsedPercent - ResetDropPoints)
            {
                runStart = index;
            }
        }

        if (runStart > 0)
        {
            series.RemoveRange(0, runStart);
        }

        if (series.Count < MinimumSamples)
        {
            return null;
        }

        var span = series[^1].At - series[0].At;
        if (span < MinimumSpan)
        {
            return null;
        }

        var delta = series[^1].UsedPercent - series[0].UsedPercent;
        if (delta <= 0)
        {
            return null;
        }

        var percentPerHour = delta / span.TotalHours;
        if (percentPerHour < MinimumPercentPerHour)
        {
            return null;
        }

        var hoursRemaining = (100 - used) / percentPerHour;
        if (hoursRemaining > MaximumProjectionHours)
        {
            return null;
        }

        var exhaustedAt = now.AddHours(hoursRemaining);
        var beforeReset = metric.ResetsAt is { } resetsAt && exhaustedAt < resetsAt;
        return new UsageProjection(percentPerHour, exhaustedAt, beforeReset);
    }

    /// <summary>Recent points for one metric, normalised for the sparkline.</summary>
    public static IReadOnlyList<int> Trend(
        IReadOnlyList<UsageSample> samples,
        string providerId,
        UsageMetric metric,
        int maximumPoints = 48)
    {
        var series = samples
            .Where(sample =>
                string.Equals(sample.ProviderId, providerId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(sample.MetricKey, metric.Key, StringComparison.Ordinal))
            .OrderBy(sample => sample.At)
            .Select(sample => sample.UsedPercent)
            .ToArray();

        return series.Length <= maximumPoints
            ? series
            : series[^maximumPoints..];
    }
}
