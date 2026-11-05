using UsageAI.Models;

namespace UsageAI.Services;

/// <summary>A projected exhaustion time derived from recent samples of one metric.</summary>
internal sealed record UsageProjection(double PercentPerHour, DateTimeOffset ExhaustedAt, bool BeforeReset);

/// <summary>
/// How a limit's use compares with the time its window has run: <see cref="Reserve"/> is the
/// percentage of the window elapsed minus the percentage used, so a positive reserve is use behind
/// the clock and a negative one is use ahead of it.
/// </summary>
internal sealed record UsagePace(int ElapsedPercent, int Reserve)
{
    /// <summary>Within this many points either way, use is simply on pace.</summary>
    public const int OnPaceBand = 3;

    public string Text => Reserve switch
    {
        > OnPaceBand => $"{Reserve}% under pace",
        < -OnPaceBand => $"{-Reserve}% over pace",
        _ => "On pace",
    };

    public bool IsOverPace => Reserve < -OnPaceBand;
}

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

    /// <summary>Before this share of a window has passed, its average says too little to project.</summary>
    private const double MinimumElapsedFraction = 0.05;

    /// <summary>
    /// Passed instead of history when the forecast setting is off, so no projection or pace is
    /// shown at all. An ordinary empty list means only that no history was recorded, and then the
    /// window's own average still gives an estimate.
    /// </summary>
    public static IReadOnlyList<UsageSample> Disabled { get; } = new List<UsageSample>().AsReadOnly();

    public static bool IsDisabled(IReadOnlyList<UsageSample> samples) => ReferenceEquals(samples, Disabled);

    /// <summary>
    /// The best projection available: the burn rate from recorded samples when there are enough of
    /// them, else the window's own average since it began. Enough history that shows use has
    /// stopped or slowed to nothing is an answer — no run-out — and is not overridden by the
    /// average. Null when the forecast is off.
    /// </summary>
    public static UsageProjection? Estimate(
        IReadOnlyList<UsageSample> samples,
        string providerId,
        UsageMetric metric,
        DateTimeOffset now)
    {
        if (IsDisabled(samples))
        {
            return null;
        }

        var (projection, sufficient) = ProjectFromHistory(samples, providerId, metric, now);
        return sufficient ? projection : ProjectFromWindow(metric, now);
    }

    /// <summary>How far through its window a metric is, as a fraction, or null when unknown.</summary>
    private static double? ElapsedFraction(UsageMetric metric, DateTimeOffset now)
    {
        if (metric.ResetsAt is not { } resetsAt || metric.DurationMinutes is not > 0)
        {
            return null;
        }

        var duration = TimeSpan.FromMinutes(metric.DurationMinutes.Value);
        var elapsed = duration - (resetsAt - now);
        return elapsed <= TimeSpan.Zero || elapsed > duration ? null : elapsed / duration;
    }

    /// <summary>
    /// A projection from the window's own average: use so far divided by the time the window has
    /// run. It needs no history, so it works from the first reading, but only once
    /// <see cref="MinimumElapsedFraction"/> of the window has passed and while something is used
    /// and something is left.
    /// </summary>
    public static UsageProjection? ProjectFromWindow(UsageMetric metric, DateTimeOffset now)
    {
        if (!metric.HasQuota ||
            metric.UsedPercent is not (> 0 and < 100) ||
            ElapsedFraction(metric, now) is not { } fraction ||
            fraction < MinimumElapsedFraction)
        {
            return null;
        }

        var used = metric.UsedPercent.Value;
        var elapsedHours = fraction * metric.DurationMinutes!.Value / 60.0;
        var percentPerHour = used / elapsedHours;
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
        return new UsageProjection(percentPerHour, exhaustedAt, exhaustedAt < metric.ResetsAt!.Value);
    }

    /// <summary>
    /// The pace of a metered window: elapsed share minus used share. Null when the forecast is off,
    /// for an unmetered or full limit, without a reset time and window length, or before
    /// <see cref="MinimumElapsedFraction"/> of the window has passed.
    /// </summary>
    public static UsagePace? Pace(IReadOnlyList<UsageSample> samples, UsageMetric metric, DateTimeOffset now)
    {
        if (IsDisabled(samples) ||
            !metric.HasQuota ||
            metric.UsedPercent is not { } used ||
            used >= 100 ||
            ElapsedFraction(metric, now) is not { } fraction ||
            fraction < MinimumElapsedFraction)
        {
            return null;
        }

        var elapsedPercent = (int)Math.Round(fraction * 100, MidpointRounding.AwayFromZero);
        return new UsagePace(elapsedPercent, elapsedPercent - used);
    }

    public static UsageProjection? Project(
        IReadOnlyList<UsageSample> samples,
        string providerId,
        UsageMetric metric,
        DateTimeOffset now) =>
        ProjectFromHistory(samples, providerId, metric, now).Projection;

    /// <summary>
    /// The history burn-rate projection, and whether there was enough history (samples and span
    /// since the last reset) for its answer — including "no run-out" — to count.
    /// </summary>
    private static (UsageProjection? Projection, bool Sufficient) ProjectFromHistory(
        IReadOnlyList<UsageSample> samples,
        string providerId,
        UsageMetric metric,
        DateTimeOffset now)
    {
        if (!metric.HasQuota)
        {
            return (null, false);
        }

        var used = metric.UsedPercent!.Value;
        if (used >= 100)
        {
            return (null, false);
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
            return (null, false);
        }

        var span = series[^1].At - series[0].At;
        if (span < MinimumSpan)
        {
            return (null, false);
        }

        // From here the history is enough to answer, and "use has stopped" is an answer too.
        var delta = series[^1].UsedPercent - series[0].UsedPercent;
        if (delta <= 0)
        {
            return (null, true);
        }

        var percentPerHour = delta / span.TotalHours;
        if (percentPerHour < MinimumPercentPerHour)
        {
            return (null, true);
        }

        var hoursRemaining = (100 - used) / percentPerHour;
        if (hoursRemaining > MaximumProjectionHours)
        {
            return (null, true);
        }

        var exhaustedAt = now.AddHours(hoursRemaining);
        var beforeReset = metric.ResetsAt is { } resetsAt && exhaustedAt < resetsAt;
        return (new UsageProjection(percentPerHour, exhaustedAt, beforeReset), true);
    }
}
