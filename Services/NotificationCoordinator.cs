using System.Text.Json;
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

    /// <summary>At most this many metrics are remembered across restarts.</summary>
    private const int MaxRememberedMetrics = 512;

    /// <summary>A remembered window whose reset passed this long ago is forgotten.</summary>
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromDays(2);

    private static readonly JsonSerializerOptions StateJsonOptions = new() { WriteIndented = false };

    /// <summary>The last reading of each metric and the reset its window had then.</summary>
    internal sealed record MetricMemory(int Used, DateTimeOffset? ResetsAt);

    private readonly Dictionary<string, MetricMemory> _memory;
    private readonly Dictionary<string, DateTimeOffset> _lastAlertAt = new(StringComparer.Ordinal);
    private readonly string? _stateFile;
    private bool _dirty;

    /// <summary>An in-memory coordinator: every metric's first reading is recorded silently.</summary>
    public NotificationCoordinator()
        : this(stateFile: null)
    {
    }

    /// <summary>
    /// A coordinator that remembers, in <paramref name="stateFile"/>, each metric's last reading and
    /// its window's reset. After a restart that reading is the previous observation, so a limit
    /// that crossed a threshold while UsageAI was closed alerts once, one already alerted on stays
    /// quiet, and a clean install (no file yet) still records everything silently.
    /// </summary>
    internal NotificationCoordinator(string? stateFile)
    {
        _stateFile = stateFile;
        _memory = LoadMemory(stateFile);
    }

    public static NotificationCoordinator CreatePersistent() => new(AppPaths.AlertStateFile);

    public IReadOnlyList<UsageAlert> Evaluate(UsageSnapshot snapshot, AppSettings settings, DateTimeOffset now)
    {
        var alerts = new List<UsageAlert>();

        foreach (var metric in snapshot.Metrics.Where(metric => metric.HasQuota))
        {
            var key = $"{snapshot.ProviderId}|{metric.Key}";
            var used = metric.UsedPercent!.Value;
            var isFirstObservation = !_memory.TryGetValue(key, out var memory);
            var current = new MetricMemory(used, metric.ResetsAt);
            if (memory != current)
            {
                _memory[key] = current;
                _dirty = true;
            }

            if (isFirstObservation || !settings.NotificationsEnabled)
            {
                continue;
            }

            var previous = memory!.Used;

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

            // In a window that began since the last reading — typically while UsageAI was closed —
            // every threshold is still to be crossed, so the count starts from zero.
            var baseline = StartedNewWindow(memory, metric) ? 0 : previous;
            var crossed = settings.NotifyAtPercent
                .Where(threshold => baseline < threshold && used >= threshold)
                .DefaultIfEmpty(0)
                .Max();
            if (crossed == 0 || !ShouldSend($"{key}|{crossed}", now))
            {
                continue;
            }

            // Thresholds always fire on used; only the wording follows the quota display, with the
            // body stating the opposite side of the title just as a card's secondary line does.
            var remaining = settings.QuotaDisplay == QuotaDisplay.Remaining;
            var detail = remaining ? metric.DisplaySecondaryFor(QuotaDisplay.Remaining) : metric.DisplayRemaining;
            var reset = UsageFormatting.RelativeReset(metric.ResetsAt, now);
            var message = string.IsNullOrEmpty(reset)
                ? detail
                : $"{detail}, {reset}.";
            alerts.Add(new UsageAlert(
                snapshot.ProviderId,
                remaining
                    ? $"{snapshot.ProviderName}: {metric.Name} {100 - crossed}% left"
                    : $"{snapshot.ProviderName}: {metric.Name} {crossed}% used",
                message,
                crossed >= settings.CriticalPercent ? AlertLevel.Critical : AlertLevel.Warning));
        }

        return alerts;
    }

    /// <summary>
    /// True when the metric's window rolled over since <paramref name="memory"/>. A genuine rollover
    /// moves the reset forward by about a whole window, while a sliding window's reset creeps forward
    /// with every poll, so the reset must jump by at least half the window (an hour when its length
    /// is unknown) to count.
    /// </summary>
    internal static bool StartedNewWindow(MetricMemory memory, UsageMetric metric)
    {
        if (memory.ResetsAt is not { } previousReset || metric.ResetsAt is not { } currentReset)
        {
            return false;
        }

        var minimumJump = metric.DurationMinutes is > 0
            ? TimeSpan.FromMinutes(Math.Max(5, metric.DurationMinutes.Value / 2.0))
            : TimeSpan.FromHours(1);
        return currentReset - previousReset >= minimumJump;
    }

    /// <summary>
    /// Writes the remembered readings when they changed. Called once per refresh, after every
    /// provider is in, so a poll costs at most one small write. A failed write is skipped: alerts
    /// then behave as before for the next start.
    /// </summary>
    public void SaveIfChanged(DateTimeOffset now)
    {
        if (!_dirty || _stateFile is null)
        {
            return;
        }

        foreach (var stale in _memory
                     .Where(entry => entry.Value.ResetsAt is { } resetsAt && now - resetsAt > ForgetAfter)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            _memory.Remove(stale);
        }

        try
        {
            AppPaths.WriteAllTextAtomic(
                _stateFile,
                JsonSerializer.Serialize(_memory.Take(MaxRememberedMetrics).ToDictionary(), StateJsonOptions));
            _dirty = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preferences and alert memory never block a refresh.
        }
    }

    private static Dictionary<string, MetricMemory> LoadMemory(string? stateFile)
    {
        var memory = new Dictionary<string, MetricMemory>(StringComparer.Ordinal);
        if (stateFile is null || !File.Exists(stateFile))
        {
            return memory;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, MetricMemory>>(
                SecureLocalFile.ReadAllText(stateFile),
                StateJsonOptions);
            foreach (var (key, value) in saved ?? [])
            {
                if (memory.Count >= MaxRememberedMetrics)
                {
                    break;
                }

                if (key.Length is > 0 and <= 512 && value is not null)
                {
                    memory[key] = value with { Used = Math.Clamp(value.Used, 0, 100) };
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
        {
            // A damaged file is treated as no memory: every metric is then recorded silently again.
        }

        return memory;
    }

    /// <summary>
    /// Moves one provider key's remembered readings in the saved state to another key, or drops them
    /// when <paramref name="newProviderId"/> is null. Used before any coordinator loads the file, so
    /// a login whose records change key keeps its alert memory.
    /// </summary>
    internal static void RenameInFile(string stateFile, string providerId, string? newProviderId)
    {
        var memory = LoadMemory(stateFile);
        var prefix = $"{providerId}|";
        var moved = memory.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (moved.Length == 0)
        {
            return;
        }

        foreach (var key in moved)
        {
            var value = memory[key];
            memory.Remove(key);
            if (newProviderId is not null)
            {
                memory[$"{newProviderId}|{key[prefix.Length..]}"] = value;
            }
        }

        try
        {
            AppPaths.WriteAllTextAtomic(stateFile, JsonSerializer.Serialize(memory, StateJsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Alert memory is a convenience; the next readings are then recorded silently.
        }
    }

    /// <summary>Forgets a provider's history so hiding and re-showing it does not replay alerts.</summary>
    public void Forget(string providerId)
    {
        var prefix = $"{providerId}|";
        foreach (var key in _memory.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
        {
            _memory.Remove(key);
            _dirty = true;
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
