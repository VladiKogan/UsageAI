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
        if (remaining <= TimeSpan.Zero)
        {
            return "resetting now";
        }

        if (remaining.TotalDays >= 1)
        {
            return $"resets in {(int)remaining.TotalDays}d {remaining.Hours}h";
        }

        if (remaining.TotalHours >= 1)
        {
            return $"resets in {(int)remaining.TotalHours}h {remaining.Minutes}m";
        }

        return $"resets in {Math.Max(1, remaining.Minutes)}m";
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
}
