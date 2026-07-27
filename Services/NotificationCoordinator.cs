using UsageAI.Models;

namespace UsageAI.Services;

internal enum AlertLevel
{
    Info,
    Warning,
    Critical,
}

internal sealed record UsageAlert(string ProviderId, string Title, string Message, AlertLevel Level);

/// <summary>
/// Decides which usage changes are worth interrupting the user for. It never alerts on the
/// first observation of a metric, so starting UsageAI at 92% used stays quiet, and it rate
/// limits each metric so a value flapping across a threshold cannot spam the tray.
/// </summary>
internal sealed class NotificationCoordinator
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(30);

    /// <summary>A fall of at least this many points means the window rolled over.</summary>
    private const int ResetDropPoints = 20;

    /// <summary>Below this, a reset is not interesting enough to announce.</summary>
    private const int ResetNoticeFloor = 50;

    private readonly Dictionary<string, int> _lastPercent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastAlertAt = new(StringComparer.Ordinal);

    public IReadOnlyList<UsageAlert> Evaluate(UsageSnapshot snapshot, AppSettings settings, DateTimeOffset now)
    {
        var alerts = new List<UsageAlert>();

        foreach (var metric in snapshot.Metrics.Where(metric => metric.HasQuota))
        {
            var key = $"{snapshot.ProviderId}|{metric.Key}";
            var used = metric.UsedPercent!.Value;
            var isFirstObservation = !_lastPercent.TryGetValue(key, out var previous);
            _lastPercent[key] = used;

            if (isFirstObservation || !settings.NotificationsEnabled)
            {
                continue;
            }

            if (settings.NotifyOnReset &&
                previous - used >= ResetDropPoints &&
                previous >= ResetNoticeFloor &&
                ShouldSend($"{key}|reset", now))
            {
                alerts.Add(new UsageAlert(
                    snapshot.ProviderId,
                    $"{snapshot.ProviderName}: {metric.Name} reset",
                    $"{metric.DisplayRemaining} after the window rolled over.",
                    AlertLevel.Info));
                continue;
            }

            var crossed = settings.NotifyAtPercent
                .Where(threshold => previous < threshold && used >= threshold)
                .DefaultIfEmpty(0)
                .Max();
            if (crossed == 0 || !ShouldSend($"{key}|{crossed}", now))
            {
                continue;
            }

            var reset = UsageFormatting.RelativeReset(metric.ResetsAt, now);
            var message = string.IsNullOrEmpty(reset)
                ? metric.DisplayRemaining
                : $"{metric.DisplayRemaining}, {reset}.";
            alerts.Add(new UsageAlert(
                snapshot.ProviderId,
                $"{snapshot.ProviderName}: {metric.Name} {crossed}% used",
                message,
                crossed >= settings.CriticalPercent ? AlertLevel.Critical : AlertLevel.Warning));
        }

        return alerts;
    }

    /// <summary>Forgets a provider's history so hiding and re-showing it does not replay alerts.</summary>
    public void Forget(string providerId)
    {
        var prefix = $"{providerId}|";
        foreach (var key in _lastPercent.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
        {
            _lastPercent.Remove(key);
        }

        foreach (var key in _lastAlertAt.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
        {
            _lastAlertAt.Remove(key);
        }
    }

    private bool ShouldSend(string key, DateTimeOffset now)
    {
        if (_lastAlertAt.TryGetValue(key, out var sentAt) && now - sentAt < MinimumInterval)
        {
            return false;
        }

        _lastAlertAt[key] = now;
        return true;
    }
}
