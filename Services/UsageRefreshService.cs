using UsageAI.Models;

namespace UsageAI.Services;

internal sealed class UsageAlertEventArgs : EventArgs
{
    public UsageAlertEventArgs(IReadOnlyList<UsageAlert> alerts) => Alerts = alerts;

    public IReadOnlyList<UsageAlert> Alerts { get; }
}

/// <summary>A known login folder that is now signed in to someone else.</summary>
internal sealed class AccountIdentityChangedEventArgs : EventArgs
{
    public AccountIdentityChangedEventArgs(string unitKey, string? previous, string current)
    {
        UnitKey = unitKey;
        Previous = previous;
        Current = current;
    }

    public string UnitKey { get; }

    /// <summary>Who was signed in before (an email), when it was known.</summary>
    public string? Previous { get; }

    /// <summary>Who is signed in now (an email, or the account's label).</summary>
    public string Current { get; }
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

    /// <summary>
    /// How long after a window's reset the follow-up poll runs. Providers publish the reset
    /// instant, not the moment their own counters roll over, so this waits out the skew.
    /// </summary>
    private static readonly TimeSpan ResetSettlingDelay = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Spacing between follow-up polls for the same window. A provider whose counters lag its
    /// own published reset instant gets another chance instead of the tray keeping a spent
    /// quota until the next regular refresh, which the reset poll exists to prevent.
    /// </summary>
    private static readonly TimeSpan ResetRetryInterval = TimeSpan.FromMinutes(1);

    /// <summary>Total polls one window may cause, so a lagging provider cannot be polled forever.</summary>
    private const int MaximumResetAttempts = 3;

    private IUsageClient[] _clients;
    private readonly AppSettings _settings;
    private readonly NotificationCoordinator _notifications = NotificationCoordinator.CreatePersistent();
    private readonly Dictionary<string, ProviderStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _nextAttempt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What each provider has already been polled for after a window rolled over. Bounded and
    /// spaced rather than a single attempt: a provider that still reports the window it just
    /// left has to be asked again, but never in a tight loop.
    /// </summary>
    private readonly Dictionary<string, ResetPoll> _resetPolls = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When each account's identity was last read, and its credential file's write time then.
    /// Claude's <c>.claude.json</c> can be megabytes and is rewritten constantly, so who is signed in
    /// is read again only when the login itself was written (a sign-in to someone else rewrites it)
    /// or after <see cref="IdentityRecheckInterval"/>.
    /// </summary>
    private readonly Dictionary<string, (DateTime CredentialWrittenAt, DateTimeOffset CheckedAt)> _identityChecks =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan IdentityRecheckInterval = TimeSpan.FromHours(6);

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

    /// <summary>Raised when a known login folder turns out to be signed in to a different person.</summary>
    public event EventHandler<AccountIdentityChangedEventArgs>? AccountIdentityChanged;

    /// <summary>
    /// Visible providers in the user's preferred order, a provider's accounts together. When a
    /// provider shows more than one account, each is named "Claude Code · label".
    /// </summary>
    public IReadOnlyList<ProviderStatus> Statuses
    {
        get
        {
            var visible = _clients
                .Where(client => _settings.IsUnitVisible(client.Id))
                .Select(client => client.Id)
                .ToArray();
            var labels = AccountLabels(visible);
            return _settings.OrderUnits(visible)
                .Select(id => labels.TryGetValue(id, out var label)
                    ? _statuses[id] with
                    {
                        ProviderName = $"{_statuses[id].ProviderName} · {label}",
                        AccountLabel = label,
                    }
                    : _statuses[id])
                .ToArray();
        }
    }

    /// <summary>Every client's key, shown or not, for Settings' account list.</summary>
    public IReadOnlyList<ProviderStatus> AllStatuses => _clients.Select(client => _statuses[client.Id]).ToArray();

    /// <summary>
    /// Labels for the accounts of every provider that shows more than one: the user's name, else
    /// the email, else the folder; two alike add the organisation, else "Account 2".
    /// </summary>
    internal IReadOnlyDictionary<string, string> AccountLabels(IReadOnlyList<string> visibleKeys)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in visibleKeys.GroupBy(ProviderKey.ProviderOf, StringComparer.OrdinalIgnoreCase))
        {
            var keys = _settings.OrderUnits(provider.ToArray());
            if (keys.Count < 2)
            {
                continue;
            }

            foreach (var (key, label) in Models.AccountLabels.Distinct(keys.Select(key => (key, _settings.FindAccount(key)))))
            {
                labels[key] = label;
            }
        }

        return labels;
    }

    public bool IsRefreshing => _isRefreshing;

    public DateTimeOffset? LastRefreshed => _lastRefreshed;

    public IReadOnlyList<UsageSample> History => _history;

    public bool IsDue(DateTimeOffset now) =>
        now >= _nextRegularRefresh ||
        _clients
            .Where(client => _settings.IsUnitVisible(client.Id))
            .Any(client => _nextAttempt.TryGetValue(client.Id, out var retryAt)
                ? now >= retryAt
                : PendingResetAt(client.Id, now) is not null);

    /// <summary>
    /// The reset instant a provider is overdue a poll for, or null. A metered window that has
    /// rolled over is worth one prompt poll: otherwise the tray can sit at a spent quota for a
    /// whole refresh interval — up to two hours at the maximum setting — after it actually reset,
    /// and the reset notification arrives just as late.
    /// </summary>
    private DateTimeOffset? PendingResetAt(string providerId, DateTimeOffset now)
    {
        if (!_statuses.TryGetValue(providerId, out var status) || status.Snapshot is not { } snapshot)
        {
            return null;
        }

        _resetPolls.TryGetValue(providerId, out var polled);

        DateTimeOffset? pending = null;
        foreach (var metric in snapshot.Metrics)
        {
            if (!metric.HasQuota || metric.ResetsAt is not { } resetsAt)
            {
                continue;
            }

            if (now < resetsAt + ResetSettlingDelay)
            {
                continue;
            }

            if (polled is not null && !IsAnotherAttemptAllowed(polled, resetsAt, now))
            {
                continue;
            }

            if (pending is null || resetsAt > pending)
            {
                pending = resetsAt;
            }
        }

        return pending;
    }

    /// <summary>
    /// Whether a window already polled for deserves another attempt. A provider whose counters
    /// trail its published reset instant would otherwise spend its one follow-up on a reading
    /// that still shows the spent window, and keep showing it until the next regular refresh.
    /// </summary>
    private static bool IsAnotherAttemptAllowed(ResetPoll polled, DateTimeOffset resetsAt, DateTimeOffset now) =>
        polled.ResetsAt < resetsAt ||
        (polled.ResetsAt == resetsAt &&
         polled.Attempts < MaximumResetAttempts &&
         now >= polled.LastAttemptAt + ResetRetryInterval);

    /// <summary>A provider's follow-up polls for one rolled-over window.</summary>
    private sealed record ResetPoll(DateTimeOffset ResetsAt, int Attempts, DateTimeOffset LastAttemptAt);

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

    /// <summary>
    /// Retries one provider now — the dashboard's Retry on a stale row — past its backoff, leaving
    /// every other provider and the regular refresh deadline on their own schedules.
    /// </summary>
    public Task RefreshProviderAsync(string providerId, bool anyWindowVisible) =>
        RefreshCoreAsync(force: true, anyWindowVisible, scheduled: false, onlyProviderId: providerId);

    private async Task RefreshCoreAsync(bool force, bool anyWindowVisible, bool scheduled, string? onlyProviderId = null)
    {
        var entered = await _refreshLock.WaitAsync(0);
        if (!entered)
        {
            // One provider asked for while another refresh runs — a dashboard Retry, an account just
            // added or signed in again — is due on the next tick rather than dropped.
            if (onlyProviderId is not null && _statuses.ContainsKey(onlyProviderId))
            {
                _nextAttempt[onlyProviderId] = DateTimeOffset.MinValue;
            }

            return;
        }

        try
        {
            if (_shutdown.IsCancellationRequested)
            {
                return;
            }

            var now = DateTimeOffset.Now;
            var regularRefresh = onlyProviderId is null && (!scheduled || force || now >= _nextRegularRefresh);
            var due = _clients
                .Where(client => onlyProviderId is null || client.Id.Equals(onlyProviderId, StringComparison.OrdinalIgnoreCase))
                .Where(client => _settings.IsUnitVisible(client.Id))
                .Where(client => IsProviderDue(client.Id, force, regularRefresh, now))
                .ToArray();
            if (force)
            {
                // The user asked for fresh numbers, so a provider sitting out its own recovery
                // backoff should try that path again rather than repeat a degraded reading.
                foreach (var client in due.OfType<IForcedRefreshAware>())
                {
                    client.OnForcedRefresh();
                }
            }

            // Recorded before fetching, because the fetch replaces the snapshot these are read from.
            foreach (var client in due)
            {
                if (PendingResetAt(client.Id, now) is { } resetAt)
                {
                    var attempts = _resetPolls.TryGetValue(client.Id, out var polled) && polled.ResetsAt == resetAt
                        ? polled.Attempts + 1
                        : 1;
                    _resetPolls[client.Id] = new ResetPoll(resetAt, attempts, now);
                }
            }

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

            // Each provider is applied the moment it lands instead of after the slowest one,
            // so a fast card stops spinning on its own schedule. Completions are still folded
            // in one at a time, which keeps the backoff bookkeeping single-threaded.
            var pending = due.Select(client => FetchAsync(client, now)).ToList();
            var alerts = new List<UsageAlert>();
            var samples = new List<UsageSample>();
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending);
                pending.Remove(finished);
                if (_shutdown.IsCancellationRequested)
                {
                    return;
                }

                ApplyResult(await finished, now, alerts, samples);

                // Recorded before the event, not after the persistence below: the moment the
                // spinner stops, the cards are read together with this timestamp, and a fresh
                // card next to the previous refresh's "last updated" time reads as a bug.
                _lastRefreshed = now;

                // The tray spinner keeps turning while any provider is still outstanding.
                _isRefreshing = pending.Count > 0;
                Updated?.Invoke(this, EventArgs.Empty);
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

            _notifications.SaveIfChanged(now);
            if (_accountsChanged)
            {
                _accountsChanged = false;
                _settings.Save();
            }

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
        foreach (var client in _clients.Where(client => !_settings.IsUnitVisible(client.Id)))
        {
            _notifications.Forget(client.Id);
        }

        _nextRegularRefresh = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Swaps in the clients for the current account list. Clients that stay keep their reading,
    /// backoff and reset bookkeeping; a new one is due at once; a removed one is forgotten.
    /// </summary>
    public void ReplaceClients(IEnumerable<IUsageClient> clients)
    {
        var next = clients.ToArray();
        if (next.Length == 0)
        {
            throw new ArgumentException("At least one usage provider is required.", nameof(clients));
        }

        var keep = new HashSet<string>(next.Select(client => client.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var removed in _clients.Where(client => !keep.Contains(client.Id)).Select(client => client.Id).ToArray())
        {
            _statuses.Remove(removed);
            _failures.Remove(removed);
            _nextAttempt.Remove(removed);
            _resetPolls.Remove(removed);
            _identityChecks.Remove(removed);
            _notifications.Forget(removed);
        }

        foreach (var client in next.Where(client => !_statuses.ContainsKey(client.Id)))
        {
            _statuses[client.Id] = new ProviderStatus(
                client.Id,
                client.DisplayName,
                null,
                null,
                IsLoading: true,
                null,
                client.SignInCommand,
                client.AccountUrl);
        }

        // A new account, or one just shown that was never read, is due on the next tick rather
        // than at the next regular refresh.
        foreach (var client in next.Where(client => _statuses[client.Id].Snapshot is null && !_nextAttempt.ContainsKey(client.Id)))
        {
            _nextAttempt[client.Id] = DateTimeOffset.MinValue;
        }

        _clients = next;
    }

    /// <summary>
    /// Starts a key afresh: its alert memory and recorded history go, so a folder now signed in to
    /// someone else does not inherit the previous person's readings.
    /// </summary>
    public void ForgetHistory(string unitKey)
    {
        _notifications.Forget(unitKey);
        _history.RemoveAll(sample => sample.ProviderId.Equals(unitKey, StringComparison.OrdinalIgnoreCase));
        UsageHistoryStore.RemoveProvider(unitKey);
    }

    public void Dispose()
    {
        // These synchronization objects are intentionally not disposed: an in-flight fetch
        // still holds the token and will release the semaphore during shutdown.
        _shutdown.Cancel();
    }

    /// <summary>
    /// Folds one completed fetch into the shared state. Only ever called from the refresh
    /// loop, one result at a time, so concurrent fetches cannot race the backoff bookkeeping.
    /// </summary>
    private void ApplyResult(
        FetchResult result,
        DateTimeOffset now,
        List<UsageAlert> alerts,
        List<UsageSample> samples)
    {
        var providerId = result.Status.ProviderId;
        if (!_clients.Any(client => client.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase)))
        {
            // The account was removed while its fetch was in flight.
            return;
        }

        if (result.IsFresh && result.Status.Snapshot is { } snapshot)
        {
            _failures.Remove(providerId);
            _nextAttempt.Remove(providerId);
            _statuses[providerId] = result.Status;
            if (result.IdentityStamp is { } stamp)
            {
                _identityChecks[providerId] = (stamp, now);
            }

            if (result.Identity is { } identity)
            {
                ObserveIdentity(providerId, identity);
            }

            var raised = _notifications.Evaluate(snapshot with { ProviderName = AlertName(providerId, snapshot.ProviderName) }, _settings, now);
            if (_settings.AccountAlertsEnabled(providerId))
            {
                alerts.AddRange(raised);
            }
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

    private bool _accountsChanged;

    /// <summary>The credential file's write time when the account's identity is due a read, else null.</summary>
    private DateTime? IdentityCheckDue(IAccountUsageClient client, DateTimeOffset now)
    {
        DateTime written;
        try
        {
            written = File.GetLastWriteTimeUtc(Path.Combine(client.LoginFolder, AccountDiscovery.CredentialFileName(client.ProviderId)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            written = DateTime.MinValue;
        }

        return _identityChecks.TryGetValue(client.Id, out var last) &&
               last.CredentialWrittenAt == written &&
               now - last.CheckedAt < IdentityRecheckInterval
            ? null
            : written;
    }

    /// <summary>
    /// Records who a login folder is signed in to. The first reading is kept silently; a different
    /// person later starts the key afresh and is announced.
    /// </summary>
    private void ObserveIdentity(string unitKey, AccountIdentity identity)
    {
        var hash = identity.Hash;
        var account = _settings.FindAccount(unitKey);
        if (account?.Identity == hash)
        {
            return;
        }

        account ??= _settings.EnsureAccount(unitKey);
        var previous = account.Identity is null ? null : account.Email ?? "another account";
        var changed = account.Identity is not null;
        account.Identity = hash;
        account.Email = identity.Email;
        account.Organization = identity.OrganizationName;
        _accountsChanged = true;
        if (!changed)
        {
            return;
        }

        ForgetHistory(unitKey);
        AccountIdentityChanged?.Invoke(
            this,
            new AccountIdentityChangedEventArgs(unitKey, previous, identity.Email ?? account.DisplayLabel));
    }

    /// <summary>The provider name an alert's title carries: with the account when the provider shows several.</summary>
    private string AlertName(string unitKey, string providerName)
    {
        var visible = _clients.Where(client => _settings.IsUnitVisible(client.Id)).Select(client => client.Id).ToArray();
        return AccountLabels(visible).TryGetValue(unitKey, out var label)
            ? $"{ProviderNames.Short(unitKey, providerName)} · {label}"
            : providerName;
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

        // A provider serving out its failure backoff keeps it; a rolled-over window does not
        // get to bypass the retry schedule.
        return _nextAttempt.TryGetValue(providerId, out var retryAt)
            ? now >= retryAt
            : regularRefresh || PendingResetAt(providerId, now) is not null;
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

        // Decided before the first await, while this still runs with the refresh loop, and recorded
        // by ApplyResult: this method mutates nothing shared.
        var identityCheck = client is IAccountUsageClient candidate ? IdentityCheckDue(candidate, attemptedAt) : null;
        try
        {
            var snapshot = await client.GetUsageAsync(_shutdown.Token) with { ProviderId = client.Id };
            var identity = client is IAccountUsageClient account && identityCheck is not null
                ? await Task.Run(() => AccountDiscovery.ReadIdentity(account.ProviderId, account.LoginFolder))
                : null;
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
                ThrottleHint: null,
                identity,
                identityCheck);
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
        TimeSpan? ThrottleHint,
        AccountIdentity? Identity = null,
        DateTime? IdentityStamp = null);
}
