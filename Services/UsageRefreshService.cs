using UsageAI.Models;

namespace UsageAI.Services;

internal sealed class UsageAlertEventArgs : EventArgs
{
    public UsageAlertEventArgs(IReadOnlyList<UsageAlert> alerts) => Alerts = alerts;

    public IReadOnlyList<UsageAlert> Alerts { get; }
}

/// <summary>
/// Owns provider polling: which providers are due, how long to back off after a failure,
/// what the last good reading was, and what should be announced. The UI observes it and
/// never talks to a provider client directly.
/// </summary>
internal sealed class UsageRefreshService : IDisposable
{
    private static readonly TimeSpan MinimumBackoff = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(60);

    private readonly IUsageClient[] _clients;
    private readonly AppSettings _settings;
    private readonly NotificationCoordinator _notifications = new();
    private readonly Dictionary<string, ProviderStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _nextAttempt = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UsageSample> _history = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private DateTimeOffset _nextRegularRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastRefreshed;
    private bool _isRefreshing;

    public UsageRefreshService(IEnumerable<IUsageClient> clients, AppSettings settings)
    {
        _clients = clients.ToArray();
        if (_clients.Length == 0)
        {
            throw new ArgumentException("At least one usage provider is required.", nameof(clients));
        }

        _settings = settings;
        if (_settings.HistoryEnabled)
        {
            _history.AddRange(UsageHistoryStore.Load(UsageHistoryStore.Retention));
        }

        var cached = new Dictionary<string, UsageSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in SnapshotCache.Load())
        {
            cached[snapshot.ProviderId] = snapshot;
        }

        foreach (var client in _clients)
        {
            cached.TryGetValue(client.Id, out var snapshot);
            _statuses[client.Id] = new ProviderStatus(
                client.Id,
                client.DisplayName,
                snapshot,
                snapshot is null ? null : "Showing the last saved reading.",
                IsLoading: true,
                snapshot?.FetchedAt,
                client.SignInCommand,
                client.AccountUrl);
        }
    }

    public event EventHandler? Updated;

    public event EventHandler<UsageAlertEventArgs>? AlertsRaised;

    /// <summary>Visible providers in the user's preferred order.</summary>
    public IReadOnlyList<ProviderStatus> Statuses
    {
        get
        {
            var visible = _clients
                .Where(client => _settings.IsProviderVisible(client.Id))
                .Select(client => client.Id)
                .ToArray();
            return _settings.OrderProviders(visible)
                .Select(id => _statuses[id])
                .ToArray();
        }
    }

    public bool IsRefreshing => _isRefreshing;

    public DateTimeOffset? LastRefreshed => _lastRefreshed;

    public IReadOnlyList<UsageSample> History => _history;

    public bool IsDue(DateTimeOffset now) =>
        now >= _nextRegularRefresh ||
        _clients
            .Where(client => _settings.IsProviderVisible(client.Id))
            .Any(client => _nextAttempt.TryGetValue(client.Id, out var retryAt) && now >= retryAt);

    /// <summary>
    /// Refreshes visible providers now, respecting provider backoff unless force is set.
    /// Explicit events such as session unlock use this path independently of the scheduler.
    /// </summary>
    public Task RefreshAsync(bool force, bool anyWindowVisible) =>
        RefreshCoreAsync(force, anyWindowVisible, scheduled: false);

    /// <summary>
    /// Runs work selected by the scheduler. A retry-only wake-up fetches only failed providers
    /// whose backoff expired and does not move the independent regular-refresh deadline.
    /// </summary>
    public Task RefreshDueAsync(bool anyWindowVisible) =>
        RefreshCoreAsync(force: false, anyWindowVisible, scheduled: true);

    private async Task RefreshCoreAsync(bool force, bool anyWindowVisible, bool scheduled)
    {
        var entered = await _refreshLock.WaitAsync(0);
        if (!entered)
        {
            return;
        }

        try
        {
            if (_shutdown.IsCancellationRequested)
            {
                return;
            }

            var now = DateTimeOffset.Now;
            var regularRefresh = !scheduled || force || now >= _nextRegularRefresh;
            var due = _clients
                .Where(client => _settings.IsProviderVisible(client.Id))
                .Where(client => IsProviderDue(client.Id, force, regularRefresh, now))
                .ToArray();
            if (due.Length == 0)
            {
                if (regularRefresh)
                {
                    ScheduleRegularRefresh(now, anyWindowVisible);
                }

                return;
            }

            _isRefreshing = true;
            foreach (var client in due)
            {
                _statuses[client.Id] = _statuses[client.Id] with { IsLoading = true };
            }

            Updated?.Invoke(this, EventArgs.Empty);

            var results = await Task.WhenAll(due.Select(client => FetchAsync(client, now)));
            if (_shutdown.IsCancellationRequested)
            {
                return;
            }

            var alerts = new List<UsageAlert>();
            var samples = new List<UsageSample>();
            foreach (var result in results)
            {
                var providerId = result.Status.ProviderId;

                if (result.IsFresh && result.Status.Snapshot is { } snapshot)
                {
                    _failures.Remove(providerId);
                    _nextAttempt.Remove(providerId);
                    _statuses[providerId] = result.Status;
                    alerts.AddRange(_notifications.Evaluate(snapshot, _settings, now));
                    if (_settings.HistoryEnabled)
                    {
                        samples.AddRange(UsageHistoryStore.SamplesFrom(snapshot));
                    }
                }
                else if (result.Failed)
                {
                    var failures = _failures.TryGetValue(providerId, out var count) ? count + 1 : 1;
                    _failures[providerId] = failures;
                    var retryAt = now + (result.ThrottleHint ?? BackoffFor(failures));
                    _nextAttempt[providerId] = retryAt;
                    _statuses[providerId] = result.Status with { NextRetryAt = retryAt };
                }
                else
                {
                    _statuses[providerId] = result.Status;
                }
            }

            if (samples.Count > 0)
            {
                UsageHistoryStore.Append(samples);
                RecordHistory(samples, now);
            }

            var snapshots = _statuses.Values
                .Select(status => status.Snapshot)
                .OfType<UsageSnapshot>()
                .ToArray();
            if (snapshots.Length > 0)
            {
                SnapshotCache.Save(snapshots);
            }

            _lastRefreshed = now;
            if (regularRefresh)
            {
                ScheduleRegularRefresh(now, anyWindowVisible);
            }

            _isRefreshing = false;
            Updated?.Invoke(this, EventArgs.Empty);

            if (alerts.Count > 0)
            {
                AlertsRaised?.Invoke(this, new UsageAlertEventArgs(alerts));
            }
        }
        finally
        {
            _isRefreshing = false;
            _refreshLock.Release();
        }
    }

    /// <summary>Re-applies preferences that change scheduling or provider visibility.</summary>
    public void ApplySettings()
    {
        foreach (var client in _clients.Where(client => !_settings.IsProviderVisible(client.Id)))
        {
            _notifications.Forget(client.Id);
        }

        _nextRegularRefresh = DateTimeOffset.MinValue;
    }

    public void Dispose()
    {
        // These synchronization objects are intentionally not disposed: an in-flight fetch
        // still holds the token and will release the semaphore during shutdown.
        _shutdown.Cancel();
    }

    private bool IsProviderDue(
        string providerId,
        bool force,
        bool regularRefresh,
        DateTimeOffset now)
    {
        if (force)
        {
            return true;
        }

        return _nextAttempt.TryGetValue(providerId, out var retryAt)
            ? now >= retryAt
            : regularRefresh;
    }

    private void ScheduleRegularRefresh(DateTimeOffset now, bool anyWindowVisible) =>
        _nextRegularRefresh = now + _settings.EffectiveRefreshInterval(anyWindowVisible);

    private void RecordHistory(IReadOnlyList<UsageSample> samples, DateTimeOffset now)
    {
        _history.AddRange(samples);
        var cutoff = now - UsageHistoryStore.Retention;
        _history.RemoveAll(sample => sample.At < cutoff);
    }

    private static TimeSpan BackoffFor(int failures)
    {
        var minutes = MinimumBackoff.TotalMinutes * Math.Pow(2, Math.Clamp(failures - 1, 0, 8));
        return TimeSpan.FromMinutes(Math.Min(MaximumBackoff.TotalMinutes, minutes));
    }

    /// <summary>
    /// Never mutates shared state: the caller applies failures serially so concurrent
    /// provider fetches cannot race the backoff bookkeeping.
    /// </summary>
    private async Task<FetchResult> FetchAsync(IUsageClient client, DateTimeOffset attemptedAt)
    {
        var previous = _statuses[client.Id];
        try
        {
            var snapshot = await client.GetUsageAsync(_shutdown.Token);
            return new FetchResult(
                new ProviderStatus(
                    client.Id,
                    client.DisplayName,
                    snapshot,
                    null,
                    IsLoading: false,
                    snapshot.FetchedAt,
                    client.SignInCommand,
                    client.AccountUrl,
                    attemptedAt,
                    NextRetryAt: null),
                IsFresh: true,
                Failed: false,
                ThrottleHint: null);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return new FetchResult(previous with { IsLoading = false }, false, false, null);
        }
        catch (Exception exception)
        {
            var message = exception is CodexUsageException or
                ClaudeCodeUsageException or
                ClaudeWebUsageException or
                GitHubCopilotUsageException or
                GeminiUsageException
                ? exception.Message
                : $"{client.DisplayName} usage is temporarily unavailable.";
            var throttleHint = exception is IThrottledUsageException { RetryAfter.TotalSeconds: > 0 } throttled
                ? throttled.RetryAfter
                : (TimeSpan?)null;

            // The previous snapshot is kept so one failed poll marks the card stale
            // instead of erasing everything the user was looking at.
            return new FetchResult(
                previous with
                {
                    Error = message,
                    IsLoading = false,
                    LastAttemptedAt = attemptedAt,
                },
                IsFresh: false,
                Failed: true,
                throttleHint);
        }
    }

    private sealed record FetchResult(
        ProviderStatus Status,
        bool IsFresh,
        bool Failed,
        TimeSpan? ThrottleHint);
}
