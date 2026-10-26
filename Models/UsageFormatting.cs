using System.Globalization;

namespace UsageAI.Models;

/// <summary>
/// Shared wording for reset countdowns and staleness, so the tray tooltip, cards, and
/// notifications never disagree about how long is left.
/// </summary>
internal static class UsageFormatting
{
    public static string RelativeReset(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is null)
        {
            return string.Empty;
        }

        var remaining = resetsAt.Value - now;
        return remaining <= TimeSpan.Zero ? "resetting now" : $"resets in {Countdown(remaining)}";
    }

    /// <summary>
    /// The countdown alone, for labels too narrow for "resets in …": "2d 17h", "3h 11m", "45m",
    /// or "now" once the window has rolled over. Empty when the provider reports no reset.
    /// </summary>
    public static string CompactReset(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is null)
        {
            return string.Empty;
        }

        var remaining = resetsAt.Value - now;
        return remaining <= TimeSpan.Zero ? "now" : Countdown(remaining);
    }

    /// <summary>A positive span as the countdown both reset labels share: "2d 17h", "3h 11m", "45m".</summary>
    private static string Countdown(TimeSpan remaining)
    {
        if (remaining.TotalDays >= 1)
        {
            return $"{(int)remaining.TotalDays}d {remaining.Hours}h";
        }

        return remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m"
            : $"{Math.Max(1, remaining.Minutes)}m";
    }

    /// <summary>The wall-clock reset time, used where the countdown alone is ambiguous.</summary>
    public static string AbsoluteReset(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is null)
        {
            return string.Empty;
        }

        var local = resetsAt.Value.ToLocalTime();
        var time = local.ToString("t", CultureInfo.CurrentCulture);
        return local.Date == now.ToLocalTime().Date
            ? time
            : $"{local.ToString("ddd", CultureInfo.CurrentCulture)} {time}";
    }

    public static string Age(DateTimeOffset when, DateTimeOffset now)
    {
        var elapsed = now - when;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (elapsed.TotalHours < 1)
        {
            return $"{(int)elapsed.TotalMinutes} min ago";
        }

        return elapsed.TotalDays < 1
            ? $"{(int)elapsed.TotalHours}h ago"
            : $"{(int)elapsed.TotalDays}d ago";
    }

    public static string RetryCountdown(DateTimeOffset retryAt, DateTimeOffset now)
    {
        var remaining = retryAt - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "retry due";
        }

        if (remaining.TotalDays >= 1)
        {
            return $"retry in {(int)remaining.TotalDays}d";
        }

        if (remaining.TotalHours >= 1)
        {
            return $"retry in {(int)remaining.TotalHours}h";
        }

        return $"retry in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m";
    }
}
