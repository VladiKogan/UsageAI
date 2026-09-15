namespace UsageAI.Models;

/// <summary>
/// Pure dashboard metric selection shared in contract with the editor extension.
/// Provider order is retained, including stable tie-breaking within a metric kind.
/// </summary>
internal static class UsageMetricSelection
{
    private static readonly UsageMetricKind[] ImportantKinds =
    {
        UsageMetricKind.Session,
        UsageMetricKind.Rolling,
        UsageMetricKind.Monthly,
    };

    public static IReadOnlyList<UsageMetric> Select(
        IReadOnlyList<UsageMetric> metrics,
        MetricDisplayMode mode)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        return mode switch
        {
            MetricDisplayMode.All => metrics,
            MetricDisplayMode.MeteredOnly => metrics.Where(metric => metric.HasQuota).ToArray(),
            MetricDisplayMode.ImportantOnly => ImportantKinds
                .Select(kind => metrics
                    .Where(metric => metric.Kind == kind && metric.HasQuota)
                    .OrderByDescending(metric => metric.UsedPercent)
                    .FirstOrDefault())
                .Where(metric => metric is not null)
                .Cast<UsageMetric>()
                .ToArray(),
            _ => metrics,
        };
    }
}
