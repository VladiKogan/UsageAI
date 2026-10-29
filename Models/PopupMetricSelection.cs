namespace UsageAI.Models;

/// <summary>
/// The limits a provider's popup tile draws as rings, and the model group they came from.
/// </summary>
/// <param name="Rings">At most two metered limits, shortest window first.</param>
/// <param name="Group">The model group the rings belong to, or null when the provider has one.</param>
/// <param name="GroupIsAutomatic">True when <paramref name="Group"/> was chosen as the busiest.</param>
/// <param name="Groups">Every model group the provider reports, when it reports more than one.</param>
internal sealed record PopupRings(
    IReadOnlyList<UsageMetric> Rings,
    string? Group,
    bool GroupIsAutomatic,
    IReadOnlyList<string> Groups)
{
    public bool HasGroupChoice => Groups.Count > 1;
}

/// <summary>
/// Chooses what a popup tile shows. A provider can report several model groups — Gemini reports
/// "Gemini Models" and "Claude and GPT models", each with its own 5-hour and weekly window — and a
/// tile has room for two rings, so both rings come from one group: the one the user picked, or
/// else whichever group holds the busiest limit. Within the group the rings follow the dashboard's
/// Important selection (busiest session, then rolling, then monthly limit), so the popup and the
/// Important view never disagree about which limits matter.
/// </summary>
internal static class PopupMetricSelection
{
    public const int MaximumRings = 2;

    /// <summary>
    /// Splits a provider name such as "Claude and GPT models (Weekly)" into its group and window.
    /// A name without a trailing parenthesis has no group and is its own window.
    /// </summary>
    public static (string? Group, string Window) Split(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var trimmed = name.Trim();
        var open = trimmed.LastIndexOf('(');
        if (open <= 0 || !trimmed.EndsWith(')') || open + 2 >= trimmed.Length)
        {
            return (null, trimmed);
        }

        var group = trimmed[..open].Trim();
        var window = trimmed[(open + 1)..^1].Trim();
        return group.Length == 0 || window.Length == 0 ? (null, trimmed) : (group, window);
    }

    /// <summary>The model groups among a provider's metered limits, in reported order.</summary>
    public static IReadOnlyList<string> Groups(IReadOnlyList<UsageMetric> metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var groups = metrics
            .Where(metric => metric.HasQuota)
            .Select(metric => Split(metric.Name).Group)
            .Where(group => group is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return groups.Length > 1 ? groups : Array.Empty<string>();
    }

    public static PopupRings Select(IReadOnlyList<UsageMetric> metrics, string? preferredGroup)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var metered = metrics.Where(metric => metric.HasQuota).ToArray();
        var groups = Groups(metrics);
        string? group = null;
        var automatic = false;
        if (groups.Count > 0)
        {
            group = groups.FirstOrDefault(candidate =>
                string.Equals(candidate, preferredGroup?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (group is null)
            {
                automatic = true;
                group = metered
                    .Where(metric => Split(metric.Name).Group is not null)
                    .OrderByDescending(metric => metric.UsedPercent)
                    .Select(metric => Split(metric.Name).Group)
                    .First();
            }

            metered = metered
                .Where(metric => string.Equals(Split(metric.Name).Group, group, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        IReadOnlyList<UsageMetric> rings = UsageMetricSelection.Select(metered, MetricDisplayMode.ImportantOnly);
        if (rings.Count == 0)
        {
            // Metered limits that are not session, rolling or monthly windows still deserve a ring.
            rings = metered.OrderByDescending(metric => metric.UsedPercent).ToArray();
        }

        return new PopupRings(rings.Take(MaximumRings).ToArray(), group, automatic, groups);
    }

    /// <summary>The short label a ring carries: the window when the name has a group, else the name.</summary>
    public static string RingLabel(UsageMetric metric) => Split(metric.Name).Window;
}
