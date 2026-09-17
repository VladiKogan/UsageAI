using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;
using UsageAI.Models;
using UsageAI.Services;
using UsageAI.UI;

namespace UsageAI.Tests;

internal static class CoverageExpansionTests
{
    private static readonly int[] SparklineValues = { -10, 25, 120 };
    private static readonly int[] SingleSparklineValue = { 10 };
    private static readonly string[] IconProviderIds = { "codex", "claude", "copilot", "gemini" };
    private static readonly string[] UserProfileScope = { "user:profile" };
    private static readonly string[] CopilotTokenPair = { "token-one", "token-two" };
    private static readonly string[] SingleCopilotToken = { "synthetic-copilot-token" };
    private static readonly ushort[] CandidatePorts = { 51_234, 51_234, 51_235 };
    private static readonly ushort[] OwnedCandidatePort = { 51_234 };
    private static readonly ushort[] UnrelatedCandidatePort = { 1 };
    private static readonly string[] PreviewDpi96Args = { "--render-preview", "preview.png", "--dpi", "96" };
    private static readonly string[] PreviewDpi192Args = { "--dpi", "192" };
    private static readonly string[] PreviewDpi288Args = { "--dpi", "288" };
    private static readonly string[] PreviewDpiInvalidArgs = { "--dpi", "144" };
    private static readonly string[] MissingFontStack = { "UsageAI missing one", "UsageAI missing two" };

    public static Task TestModelFormattingAsync()
    {
        var now = DateTimeOffset.Now;
        Equal(string.Empty, UsageFormatting.RelativeReset(null, now));
        Equal("resetting now", UsageFormatting.RelativeReset(now, now));
        Equal("resets in 2d 3h", UsageFormatting.RelativeReset(now.AddDays(2).AddHours(3), now));
        Equal("resets in 4h 12m", UsageFormatting.RelativeReset(now.AddHours(4).AddMinutes(12), now));
        Equal("resets in 1m", UsageFormatting.RelativeReset(now.AddSeconds(20), now));
        True(UsageFormatting.AbsoluteReset(now.AddHours(2), now).Length > 0);
        True(UsageFormatting.AbsoluteReset(now.AddDays(2), now).Length > 0);
        Equal(string.Empty, UsageFormatting.AbsoluteReset(null, now));
        Equal("just now", UsageFormatting.Age(now.AddSeconds(-10), now));
        Equal("42 min ago", UsageFormatting.Age(now.AddMinutes(-42), now));
        Equal("3h ago", UsageFormatting.Age(now.AddHours(-3), now));
        Equal("2d ago", UsageFormatting.Age(now.AddDays(-2), now));

        var quota = new UsageMetric(
            "Session",
            UsageMetricKind.Session,
            120,
            now.AddHours(1),
            300,
            UsageText: "custom detail");
        Equal(0, quota.RemainingPercent);
        Equal("100% USED", quota.DisplayUsed);
        Equal("custom detail", quota.DisplaySecondary);
        Equal("Session:Session", quota.Key);

        var unlimited = new UsageMetric(
            "Chat",
            UsageMetricKind.Monthly,
            0,
            IsUnlimited: true);
        False(unlimited.HasQuota);
        Equal("UNLIMITED", unlimited.DisplayUsed);
        Equal("UNLIMITED", unlimited.DisplayRemaining);
        Equal("No limit reported", unlimited.DisplayUsage);

        var balance = new UsageMetric(
            "Credits",
            UsageMetricKind.Balance,
            null,
            RemainingText: "$9.50",
            UsageText: "Available balance");
        Equal("$9.50", balance.DisplayUsed);
        Equal("Available balance", balance.DisplaySecondary);

        var snapshot = new UsageSnapshot(
            "Pro",
            new[] { balance, quota, unlimited },
            now,
            "test",
            "Test Provider",
            "person@example.com");
        Equal(quota, snapshot.Primary);
        Equal(120, snapshot.HighestUsedPercent);
        var diagnostic = snapshot.ToDiagnosticJson();
        True(diagnostic.Contains("\"ProviderId\": \"test\"", StringComparison.Ordinal));
        True(diagnostic.Contains("\"Kind\": \"Session\"", StringComparison.Ordinal));

        var emptySnapshot = snapshot with { Metrics = Array.Empty<UsageMetric>() };
        Null(emptySnapshot.Primary);
        Equal(0, emptySnapshot.HighestUsedPercent);

        var loading = new ProviderStatus("test", "Test", null, null, true);
        Equal("Refreshing", loading.StatusText);
        False(loading.IsConnected);

        var disconnected = loading with { IsLoading = false };
        Equal("Not connected", disconnected.StatusText);

        var connected = disconnected with { Snapshot = snapshot };
        Equal("Connected", connected.StatusText);
        True(connected.IsConnected);
        False(connected.IsStale);

        var stale = connected with { Error = "temporary failure" };
        Equal("Stale", stale.StatusText);
        True(stale.IsStale);
        var attemptedAt = now.AddMinutes(-2);
        stale = stale with { LastAttemptedAt = attemptedAt };
        Equal(
            "1 provider stale · checked 2 min ago",
            UsagePopupForm.RefreshSummary(
                new[] { connected, stale },
                isRefreshing: false,
                lastRefreshed: now,
                now));
        Equal(
            "Updated just now",
            UsagePopupForm.RefreshSummary(
                new[] { connected },
                isRefreshing: false,
                lastRefreshed: now,
                now));
        Equal("retry due", UsageFormatting.RetryCountdown(now, now));
        Equal("retry in 2m", UsageFormatting.RetryCountdown(now.AddSeconds(90), now));
        return Task.CompletedTask;
    }

    public static async Task TestRefreshOrchestrationAsync()
    {
        UsageHistoryStore.Clear();
        SnapshotCache.Clear();
        var now = DateTimeOffset.Now;
        SnapshotCache.Save(new[] { Snapshot("alpha", "Alpha", 15, now.AddMinutes(-10)) });

        var alpha = new QueueUsageClient("alpha", "Alpha");
        alpha.Enqueue(Snapshot("alpha", "Alpha", 70, now));
        alpha.Enqueue(Snapshot("alpha", "Alpha", 85, now.AddMinutes(1)));
        alpha.Enqueue(Snapshot("alpha", "Alpha", 5, now.AddMinutes(2)));

        var beta = new QueueUsageClient("beta", "Beta");
        beta.Enqueue(new GeminiUsageException("Beta asked us to wait.")
        {
            RetryAfter = TimeSpan.FromHours(1),
        });
        beta.Enqueue(Snapshot("beta", "Beta", 25, now.AddMinutes(2)));

        var hidden = new QueueUsageClient("hidden", "Hidden");
        hidden.Enqueue(Snapshot("hidden", "Hidden", 50, now));

        var settings = new AppSettings
        {
            RefreshIntervalMinutes = 1,
            SlowRefreshWhenHidden = false,
            HistoryEnabled = true,
            NotificationsEnabled = true,
            NotifyAtPercent = new[] { 80, 95 },
            WarningPercent = 80,
            CriticalPercent = 95,
            HiddenProviders = new[] { "hidden" },
            ProviderOrder = new[] { "beta", "alpha" },
        };

        using var service = new UsageRefreshService(new IUsageClient[] { alpha, beta, hidden }, settings);
        Equal(2, service.Statuses.Count);
        Equal("beta", service.Statuses[0].ProviderId);
        var cachedAlpha = service.Statuses.Single(status => status.ProviderId == "alpha");
        Equal(15, cachedAlpha.Snapshot!.Primary!.UsedPercent);
        Equal("Showing the last saved reading.", cachedAlpha.Error);
        True(service.IsDue(DateTimeOffset.Now));

        var updates = 0;
        var alerts = new List<UsageAlert>();
        service.Updated += (_, _) => updates++;
        service.AlertsRaised += (_, eventArgs) => alerts.AddRange(eventArgs.Alerts);

        await service.RefreshAsync(force: false, anyWindowVisible: true);
        Equal(1, alpha.CallCount);
        Equal(1, beta.CallCount);
        Equal(0, hidden.CallCount);
        False(service.IsRefreshing);
        NotNull(service.LastRefreshed);
        False(service.IsDue(DateTimeOffset.Now));
        Equal(1, service.History.Count);
        Equal("Beta asked us to wait.", service.Statuses[0].Error);
        NotNull(service.Statuses[0].LastAttemptedAt);
        NotNull(service.Statuses[0].NextRetryAt);
        Equal(70, service.Statuses[1].Snapshot!.Primary!.UsedPercent);

        // Successful providers remain eligible, while a throttled provider is skipped.
        await service.RefreshAsync(force: false, anyWindowVisible: false);
        Equal(2, alpha.CallCount);
        Equal(1, beta.CallCount);
        Equal(1, alerts.Count);
        Equal(AlertLevel.Warning, alerts[0].Level);
        Equal(2, service.History.Count);

        // Hiding a provider forgets alert state; showing it again starts from a quiet baseline.
        settings.SetProviderVisible("alpha", false);
        service.ApplySettings();
        Equal(1, service.Statuses.Count);
        True(service.IsDue(DateTimeOffset.Now));
        settings.SetProviderVisible("alpha", true);
        service.ApplySettings();
        await service.RefreshAsync(force: true, anyWindowVisible: true);

        Equal(3, alpha.CallCount);
        Equal(2, beta.CallCount);
        Null(service.Statuses[0].NextRetryAt);
        Equal(1, alerts.Count);
        Equal(4, service.History.Count);
        True(updates >= 6);
        Equal(2, SnapshotCache.Load().Count);

        UsageHistoryStore.Clear();
        SnapshotCache.Clear();
    }

    public static async Task TestScheduledStaleRecoveryAsync()
    {
        UsageHistoryStore.Clear();
        SnapshotCache.Clear();
        try
        {
            var now = DateTimeOffset.Now;
            var healthy = new QueueUsageClient("healthy", "Healthy");
            healthy.Enqueue(Snapshot("healthy", "Healthy", 10, now));
            healthy.Enqueue(Snapshot("healthy", "Healthy", 11, now.AddMinutes(1)));

            var recovering = new QueueUsageClient("recovering", "Recovering");
            recovering.Enqueue(Snapshot("recovering", "Recovering", 20, now));
            recovering.Enqueue(new InvalidOperationException("temporary failure"));
            recovering.Enqueue(Snapshot("recovering", "Recovering", 21, now.AddMinutes(2)));

            var settings = new AppSettings
            {
                RefreshIntervalMinutes = AppSettings.MaximumRefreshMinutes,
                SlowRefreshWhenHidden = false,
                HistoryEnabled = false,
                NotificationsEnabled = false,
            };
            using var service = new UsageRefreshService(
                new IUsageClient[] { healthy, recovering },
                settings);

            await service.RefreshAsync(force: true, anyWindowVisible: false);
            await service.RefreshAsync(force: false, anyWindowVisible: false);

            var stale = service.Statuses.Single(status => status.ProviderId == "recovering");
            True(stale.IsStale);
            NotNull(stale.LastAttemptedAt);
            NotNull(stale.NextRetryAt);
            Equal(2, healthy.CallCount);
            Equal(2, recovering.CallCount);

            var regularRefreshBeforeRetry = GetPrivateFieldValue<DateTimeOffset>(
                service,
                "_nextRegularRefresh");
            var retrySchedule = GetPrivateField<Dictionary<string, DateTimeOffset>>(
                service,
                "_nextAttempt");
            retrySchedule["recovering"] = DateTimeOffset.Now.AddSeconds(-1);

            True(service.IsDue(DateTimeOffset.Now));
            await service.RefreshDueAsync(anyWindowVisible: false);

            Equal(2, healthy.CallCount);
            Equal(3, recovering.CallCount);
            var recovered = service.Statuses.Single(status => status.ProviderId == "recovering");
            False(recovered.IsStale);
            Null(recovered.Error);
            Null(recovered.NextRetryAt);
            Equal(
                regularRefreshBeforeRetry,
                GetPrivateFieldValue<DateTimeOffset>(service, "_nextRegularRefresh"));
            False(service.IsDue(DateTimeOffset.Now));
        }
        finally
        {
            UsageHistoryStore.Clear();
            SnapshotCache.Clear();
        }
    }

    public static async Task TestNoVisibleProviderScheduleAsync()
    {
        var hidden = new QueueUsageClient("hidden", "Hidden");
        hidden.Enqueue(Snapshot("hidden", "Hidden", 25, DateTimeOffset.Now));
        var settings = new AppSettings
        {
            HiddenProviders = new[] { "hidden" },
            RefreshIntervalMinutes = 5,
            SlowRefreshWhenHidden = false,
            HistoryEnabled = false,
            NotificationsEnabled = false,
        };
        using var service = new UsageRefreshService(new[] { hidden }, settings);

        True(service.IsDue(DateTimeOffset.Now));
        await service.RefreshDueAsync(anyWindowVisible: false);

        Equal(0, hidden.CallCount);
        False(service.IsDue(DateTimeOffset.Now));
        False(service.IsRefreshing);
    }

    /// <summary>
    /// A fast provider must reach the UI while a slow one is still in flight, so one slow
    /// provider cannot hold every other card in its loading state for the whole refresh.
    /// </summary>
    public static async Task TestIncrementalProviderPublishingAsync()
    {
        UsageHistoryStore.Clear();
        SnapshotCache.Clear();
        var now = DateTimeOffset.Now;

        var fast = new QueueUsageClient("fast", "Fast");
        fast.Enqueue(Snapshot("fast", "Fast", 40, now));

        var slow = new GatedUsageClient("slow", "Slow", Snapshot("slow", "Slow", 60, now));

        var settings = new AppSettings
        {
            RefreshIntervalMinutes = 1,
            SlowRefreshWhenHidden = false,
            HistoryEnabled = false,
            NotificationsEnabled = false,
        };

        using var service = new UsageRefreshService(new IUsageClient[] { fast, slow }, settings);

        // Completed by the Updated handler the first time the fast card is done while the
        // slow one is still loading. Batching every provider into one update never reaches
        // that state, so a regression fails here as a timeout rather than a silent pass.
        var landedEarly = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var spinnerStillTurning = false;
        service.Updated += (_, _) =>
        {
            var fastStatus = service.Statuses.Single(status => status.ProviderId == "fast");
            var slowStatus = service.Statuses.Single(status => status.ProviderId == "slow");
            if (fastStatus is { IsLoading: false, Snapshot: not null } && slowStatus.IsLoading)
            {
                spinnerStillTurning = service.IsRefreshing;
                landedEarly.TrySetResult();
            }
        };

        var refresh = service.RefreshAsync(force: true, anyWindowVisible: true);
        try
        {
            await landedEarly.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            slow.Release();
        }

        await refresh;
        True(spinnerStillTurning);

        Equal(40, service.Statuses.Single(status => status.ProviderId == "fast").Snapshot!.Primary!.UsedPercent);
        Equal(60, service.Statuses.Single(status => status.ProviderId == "slow").Snapshot!.Primary!.UsedPercent);
        False(service.IsRefreshing);
    }

    /// <summary>
    /// The Antigravity probe reads owner-to-port mapping from the IP Helper API instead of
    /// shelling out, and retries the combination that answered last before the others.
    /// </summary>
    public static Task TestListeningPortLookupAsync()
    {
        Equal(443, ListeningPortTable.ToHostPort(0xBB01));
        Equal(0, ListeningPortTable.ToHostPort(0));

        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var expected = (ushort)((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var ports = ListeningPortTable.ForProcess((uint)Environment.ProcessId);
            True(ports.Contains(expected));
            Equal(ports.Count, ports.Distinct().Count());
        }
        finally
        {
            listener.Stop();
        }

        // A pid that owns nothing must come back empty rather than throw.
        Equal(0, ListeningPortTable.ForProcess(uint.MaxValue).Count);

        // Discovery has to hold up whether or not a language server happens to be running on
        // the machine running the suite, so this asserts the shape rather than the population.
        var live = GeminiUsageClient.ProcessInfo.FindLiveLanguageServers();
        Null(GeminiUsageClient.ProcessInfo.TryServeFromCache(new Dictionary<uint, long> { [uint.MaxValue] = 1 }));

        // A pid whose start time could not be read is never served from the cache, because
        // there is nothing to tell it apart from a recycled id.
        Null(GeminiUsageClient.ProcessInfo.TryServeFromCache(new Dictionary<uint, long> { [uint.MaxValue] = 0 }));

        // A live server the command-line query yields nothing for — another product's language
        // server, or one not yet given a CSRF token — is a known answer, not a cache miss. If it
        // read as a miss, the query this cache exists to avoid would run on every refresh.
        Equal(0, GeminiUsageClient.ProcessInfo.TryServeFromCache(new Dictionary<uint, long>())!.Count);

        var detected = GeminiUsageClient.ProcessInfo.DetectLocalLanguageServerProcesses();
        True(detected.All(info => !string.IsNullOrWhiteSpace(info.CsrfToken)));
        True(detected.Count <= live.Count);

        // The second call is served from the command-line cache and must agree with the first.
        var repeated = GeminiUsageClient.ProcessInfo.DetectLocalLanguageServerProcesses();
        Equal(detected.Count, repeated.Count);

        // Ownership is revalidated against a freshly read table, never the one that built the
        // candidates: a port released between the two reads must not be handed a CSRF token.
        var owner = new GeminiUsageClient.ProcessInfo("primary", "extension", ExtensionPort: 5_002, Pid: 42);
        var released = owner.GetBoundCandidates(new ushort[] { 5_001, 5_002 })
            .Single(candidate => candidate.Port == 5_002 && candidate.Token == "extension");
        False(owner.IsListeningPortStillOwned(released, new ushort[] { 5_001 }));
        True(owner.IsListeningPortStillOwned(released, new ushort[] { 5_001, 5_002 }));

        var first = new GeminiUsageClient.BoundAntigravityCandidate(7, 51_001, "extension");
        var second = new GeminiUsageClient.BoundAntigravityCandidate(7, 51_002, "primary");
        var candidates = new[] { first, second };

        Equal(second, GeminiUsageClient.Prioritize(candidates, second).First());
        Equal(first, GeminiUsageClient.Prioritize(candidates, first).First());
        Equal(first, GeminiUsageClient.Prioritize(candidates, null).First());
        Equal(
            first,
            GeminiUsageClient.Prioritize(
                candidates,
                new GeminiUsageClient.BoundAntigravityCandidate(9, 9_999, "stale")).First());
        return Task.CompletedTask;
    }

    /// <summary>
    /// Once an agy hub is serving, Gemini must go straight to it. The Antigravity IDE probe
    /// ahead of it still reads command lines through PowerShell and reports the same buckets,
    /// so paying for it on every refresh is wasted work. Mirrors the editor extension.
    /// </summary>
    public static async Task TestGeminiPrefersServingHubAsync()
    {
        var hubbed = Snapshot("gemini", "Google Gemini", 21, DateTimeOffset.Now);
        var probed = Snapshot("gemini", "Google Gemini", 78, DateTimeOffset.Now);

        using var http = new HttpClient(new StubHttpHandler((_, _, _) =>
            throw new InvalidOperationException("HTTP should not be reached.")));

        var probeCalls = 0;
        var hubCalls = 0;
        Task<UsageSnapshot?> Probe(CancellationToken _)
        {
            probeCalls++;
            return Task.FromResult<UsageSnapshot?>(probed);
        }

        Task<UsageSnapshot?> Hub(CancellationToken _)
        {
            hubCalls++;
            return Task.FromResult<UsageSnapshot?>(hubbed);
        }

        var serving = new GeminiUsageClient(http, Probe, Hub, () => { }, () => true);
        Equal(hubbed, await serving.GetUsageAsync());
        Equal(0, probeCalls);
        Equal(1, hubCalls);

        // With no hub the probe keeps its place at the front of the chain.
        probeCalls = 0;
        hubCalls = 0;
        var noHub = new GeminiUsageClient(http, Probe, Hub, () => { }, () => false);
        Equal(probed, await noHub.GetUsageAsync());
        Equal(1, probeCalls);
        Equal(0, hubCalls);

        // A hub that claims to be serving but answers nothing must not strand the provider.
        probeCalls = 0;
        hubCalls = 0;
        var emptyHub = new GeminiUsageClient(
            http,
            Probe,
            _ => Task.FromResult<UsageSnapshot?>(null),
            () => { },
            () => true);
        Equal(probed, await emptyHub.GetUsageAsync());
        Equal(1, probeCalls);
        Equal(0, hubCalls);

        // The short cut asks the hub alone. A hub that has stopped answering must fall through to
        // the ordered chain, never reach the full agy probe from here: that one spawns
        // `agy -p /usage`, which is the per-refresh child process the hub exists to avoid, and it
        // would run ahead of the backoff meant to suppress it.
        var fullAgyCalls = 0;
        var hubOnlyCalls = 0;
        var silentHub = new GeminiUsageClient(
            http,
            Probe,
            agyProbe: _ =>
            {
                fullAgyCalls++;
                return Task.FromResult<UsageSnapshot?>(null);
            },
            resetAgyBackoff: () => { },
            hasAgyHub: () => true,
            agyHubProbe: _ =>
            {
                hubOnlyCalls++;
                return Task.FromResult<UsageSnapshot?>(null);
            });

        probeCalls = 0;
        Equal(probed, await silentHub.GetUsageAsync());
        Equal(1, hubOnlyCalls);
        Equal(1, probeCalls);

        // The local probe answered, so the chain stopped before the full agy probe entirely.
        Equal(0, fullAgyCalls);
    }

    /// <summary>
    /// A metered window that has rolled over earns one prompt poll, so the tray stops showing a
    /// spent quota long after it reset. It must happen once per window, and must never override
    /// a provider's failure backoff.
    /// </summary>
    public static async Task TestResetAwareSchedulingAsync()
    {
        UsageHistoryStore.Clear();
        SnapshotCache.Clear();
        var now = DateTimeOffset.Now;

        // Snapshot() puts the reset two hours after the fetch time, so an old fetch time is a
        // window that has already rolled over.
        var alpha = new QueueUsageClient("alpha", "Alpha");
        alpha.Enqueue(Snapshot("alpha", "Alpha", 90, now.AddHours(-3)));
        alpha.Enqueue(Snapshot("alpha", "Alpha", 90, now.AddHours(-3)));
        alpha.Enqueue(Snapshot("alpha", "Alpha", 5, now.AddHours(-2).AddMinutes(-1)));

        var beta = new QueueUsageClient("beta", "Beta");
        beta.Enqueue(Snapshot("beta", "Beta", 80, now.AddHours(-3)));
        beta.Enqueue(new GeminiUsageException("Beta is unavailable."));

        var settings = new AppSettings
        {
            // Far enough out that nothing below can be a regular refresh.
            RefreshIntervalMinutes = 120,
            SlowRefreshWhenHidden = false,
            HistoryEnabled = false,
            NotificationsEnabled = false,
        };

        using var service = new UsageRefreshService(new IUsageClient[] { alpha, beta }, settings);
        await service.RefreshAsync(force: true, anyWindowVisible: true);
        Equal(1, alpha.CallCount);
        Equal(1, beta.CallCount);

        // Both now hold a window whose reset is in the past, so the scheduler wakes for it even
        // though the two-hour regular deadline is nowhere near.
        True(service.IsDue(DateTimeOffset.Now));

        await service.RefreshDueAsync(anyWindowVisible: true);
        Equal(2, alpha.CallCount);
        Equal(2, beta.CallCount);

        // alpha still reports the window it just left, and beta is in failure backoff. Neither
        // may be polled again: one is rate-limited by the handled-reset guard, the other by its
        // own retry schedule.
        False(service.IsDue(DateTimeOffset.Now));
        await service.RefreshDueAsync(anyWindowVisible: true);
        Equal(2, alpha.CallCount);
        Equal(2, beta.CallCount);

        var betaStatus = service.Statuses.Single(status => status.ProviderId == "beta");
        NotNull(betaStatus.NextRetryAt);
        Equal(80, betaStatus.Snapshot!.Primary!.UsedPercent);
    }

    public static async Task TestRefreshConcurrencyAsync()
    {
        UsageHistoryStore.Clear();
        SnapshotCache.Clear();
        var settings = new AppSettings { HistoryEnabled = false };
        var blocking = new BlockingUsageClient("blocking", "Blocking");
        using (var service = new UsageRefreshService(new[] { blocking }, settings))
        {
            var first = service.RefreshAsync(force: true, anyWindowVisible: true);
            await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.RefreshAsync(force: true, anyWindowVisible: true);
            Equal(1, blocking.CallCount);
            True(service.IsRefreshing);
            blocking.Complete(Snapshot("blocking", "Blocking", 40, DateTimeOffset.Now));
            await first;
            False(service.IsRefreshing);
            Equal(40, service.Statuses[0].Snapshot!.Primary!.UsedPercent);
        }

        var cancelling = new CancellingUsageClient();
        var cancellationService = new UsageRefreshService(new[] { cancelling }, settings);
        var refresh = cancellationService.RefreshAsync(force: true, anyWindowVisible: true);
        await cancelling.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellationService.Dispose();
        await refresh;
        False(cancellationService.IsRefreshing);
        await cancellationService.RefreshAsync(force: true, anyWindowVisible: true);
        Equal(1, cancelling.CallCount);
    }

    public static async Task TestClaudeHttpAsync()
    {
        var environment = SaveEnvironment(
            "USAGEAI_CLAUDE_OAUTH_TOKEN",
            "USAGEAI_CLAUDE_OAUTH_SCOPES",
            "USAGEAI_CLAUDE_SESSION_KEY",
            "CLAUDE_AI_SESSION_KEY",
            "CLAUDE_WEB_SESSION_KEY");
        try
        {
            Environment.SetEnvironmentVariable("USAGEAI_CLAUDE_OAUTH_TOKEN", "synthetic-claude-token");
            Environment.SetEnvironmentVariable("USAGEAI_CLAUDE_OAUTH_SCOPES", "user:profile");
            Environment.SetEnvironmentVariable("USAGEAI_CLAUDE_SESSION_KEY", null);
            Environment.SetEnvironmentVariable("CLAUDE_AI_SESSION_KEY", null);
            Environment.SetEnvironmentVariable("CLAUDE_WEB_SESSION_KEY", null);

            var successHandler = new StubHttpHandler((request, _, _) =>
            {
                Equal(HttpMethod.Get, request.Method);
                Equal("Bearer", request.Headers.Authorization?.Scheme);
                True(request.Headers.Contains("anthropic-beta"));
                return Task.FromResult(JsonResponse(
                    HttpStatusCode.OK,
                    """{"five_hour":{"utilization":31},"seven_day":{"utilization":52}}"""));
            });
            using (var http = new HttpClient(successHandler))
            {
                var snapshot = await new ClaudeCodeUsageClient(http).GetUsageAsync();
                Equal("Claude (OAuth)", snapshot.Plan);
                Equal(2, snapshot.Metrics.Count);
                Equal(31, snapshot.Metrics[0].UsedPercent);
            }

            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)))))
            {
                var authProbes = 0;
                var exception = await ThrowsAsync<ClaudeCodeUsageException>(
                    () => new ClaudeCodeUsageClient(
                        http,
                        () => Array.Empty<string>(),
                        _ =>
                        {
                            authProbes++;
                            return Task.FromResult(true);
                        }).GetUsageAsync());
                True(exception.Message.Contains("expired", StringComparison.OrdinalIgnoreCase));
                Equal(0, authProbes);
            }

            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                   {
                       var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                       response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(37));
                       return Task.FromResult(response);
                   })))
            {
                var exception = await ThrowsAsync<ClaudeCodeUsageException>(
                    () => new ClaudeCodeUsageClient(http).GetUsageAsync());
                Equal(TimeSpan.FromSeconds(37), exception.RetryAfter);
            }

            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       Task.FromResult(JsonResponse(HttpStatusCode.OK, "{broken")))))
            {
                var exception = await ThrowsAsync<ClaudeCodeUsageException>(
                    () => new ClaudeCodeUsageClient(http).GetUsageAsync());
                True(exception.InnerException is JsonException);
            }

            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)))))
            {
                var exception = await ThrowsAsync<ClaudeCodeUsageException>(
                    () => new ClaudeCodeUsageClient(http).GetUsageAsync());
                True(exception.Message.Contains("cannot read", StringComparison.OrdinalIgnoreCase));
            }

            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       throw new OperationCanceledException())))
            {
                var exception = await ThrowsAsync<ClaudeCodeUsageException>(
                    () => new ClaudeCodeUsageClient(http).GetUsageAsync());
                True(exception.Message.Contains("15 seconds", StringComparison.OrdinalIgnoreCase));
            }

            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       throw new InvalidOperationException("synthetic transport failure"))))
            {
                var exception = await ThrowsAsync<ClaudeCodeUsageException>(
                    () => new ClaudeCodeUsageClient(http).GetUsageAsync());
                True(exception.InnerException is InvalidOperationException);
            }

            Environment.SetEnvironmentVariable("USAGEAI_CLAUDE_OAUTH_SCOPES", "other:scope");
            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       throw new InvalidOperationException("HTTP should not be reached."))))
            {
                var exception = await ThrowsAsync<ClaudeCodeUsageException>(
                    () => new ClaudeCodeUsageClient(http).GetUsageAsync());
                True(exception.Message.Contains("user:profile", StringComparison.Ordinal));
            }
        }
        finally
        {
            RestoreEnvironment(environment);
        }
    }

    public static async Task TestClaudeCredentialsAreReadOnlyAsync()
    {
        var environment = SaveEnvironment(
            "USAGEAI_CLAUDE_OAUTH_TOKEN",
            "USAGEAI_CLAUDE_SESSION_KEY",
            "CLAUDE_AI_SESSION_KEY",
            "CLAUDE_WEB_SESSION_KEY",
            "CLAUDE_CONFIG_DIR",
            "CLAUDE_PATH");
        var directory = Path.Combine(AppPaths.DataDirectory, $"claude-read-only-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            Environment.SetEnvironmentVariable("USAGEAI_CLAUDE_OAUTH_TOKEN", null);
            Environment.SetEnvironmentVariable("USAGEAI_CLAUDE_SESSION_KEY", null);
            Environment.SetEnvironmentVariable("CLAUDE_AI_SESSION_KEY", null);
            Environment.SetEnvironmentVariable("CLAUDE_WEB_SESSION_KEY", null);
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", directory);
            Environment.SetEnvironmentVariable(
                "CLAUDE_PATH",
                Environment.ProcessPath ?? throw new InvalidOperationException("The test executable path is unavailable."));

            var credentialPath = Path.Combine(directory, ".credentials.json");
            var original = $$"""
                {
                  "claudeAiOauth":{
                    "accessToken":"expired-access",
                    "refreshToken":"shared-refresh-token",
                    "expiresAt":{{DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds()}},
                    "scopes":["user:profile"],
                    "subscriptionType":"pro"
                  }
                }
                """;
            File.WriteAllText(credentialPath, original);

            var requests = 0;
            using var http = new HttpClient(new StubHttpHandler((request, _, _) =>
            {
                requests++;
                False(request.RequestUri!.AbsolutePath.Contains("oauth/token", StringComparison.Ordinal));
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"five_hour":{"utilization":12}}"""));
            }));

            var exception = await ThrowsAsync<ClaudeCodeUsageException>(
                () => new ClaudeCodeUsageClient(
                    http,
                    () => Array.Empty<string>(),
                    _ => Task.FromResult(false)).GetUsageAsync());
            True(exception.Message.Contains("expired", StringComparison.OrdinalIgnoreCase));
            Equal(0, requests);
            Equal(original, File.ReadAllText(credentialPath));

            var snapshot = await new ClaudeCodeUsageClient(
                http,
                () => Array.Empty<string>(),
                ClaudeCodeUsageClient.RunClaudeAuthStatusAsync).GetUsageAsync();
            Equal(12, snapshot.Primary!.UsedPercent);
            Equal(1, requests);

            using var refreshedDocument = JsonDocument.Parse(File.ReadAllText(credentialPath));
            var refreshedOauth = refreshedDocument.RootElement.GetProperty("claudeAiOauth");
            Equal("owner-refreshed-access", refreshedOauth.GetProperty("accessToken").GetString());
            Equal("owner-refreshed-token", refreshedOauth.GetProperty("refreshToken").GetString());

            var freshCredentials = $$"""
                {
                  "claudeAiOauth":{
                    "accessToken":"fresh-access",
                    "refreshToken":"shared-refresh-token",
                    "expiresAt":{{DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeMilliseconds()}},
                    "scopes":["user:profile"],
                    "subscriptionType":"pro"
                  }
                }
                """;
            File.WriteAllText(credentialPath, freshCredentials);

            var recoveryRequests = 0;
            var recoveryProbes = 0;
            using (var recoveryHttp = new HttpClient(new StubHttpHandler((_, _, _) =>
                   {
                       recoveryRequests++;
                       return Task.FromResult(recoveryRequests == 1
                           ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                           : JsonResponse(HttpStatusCode.OK, """{"five_hour":{"utilization":27}}"""));
                   })))
            {
                var recovered = await new ClaudeCodeUsageClient(
                    recoveryHttp,
                    () => Array.Empty<string>(),
                    _ =>
                    {
                        recoveryProbes++;
                        return Task.FromResult(true);
                    }).GetUsageAsync();
                Equal(27, recovered.Primary!.UsedPercent);
                Equal(2, recoveryRequests);
                Equal(1, recoveryProbes);
                Equal(freshCredentials, File.ReadAllText(credentialPath));
            }

            var invalidResponseRequests = 0;
            using (var invalidResponseHttp = new HttpClient(new StubHttpHandler((_, _, _) =>
                   {
                       invalidResponseRequests++;
                       return Task.FromResult(invalidResponseRequests == 1
                           ? JsonResponse(HttpStatusCode.OK, "{broken")
                           : JsonResponse(HttpStatusCode.OK, """{"seven_day":{"utilization":41}}"""));
                   })))
            {
                var recovered = await new ClaudeCodeUsageClient(
                    invalidResponseHttp,
                    () => Array.Empty<string>(),
                    _ => Task.FromResult(true)).GetUsageAsync();
                Equal(41, recovered.Primary!.UsedPercent);
                Equal(2, invalidResponseRequests);
            }

            var failedRecoveryRequests = 0;
            using (var failedRecoveryHttp = new HttpClient(new StubHttpHandler((_, _, _) =>
                   {
                       failedRecoveryRequests++;
                       return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                   })))
            {
                var failedException = await ThrowsAsync<ClaudeCodeUsageException>(() =>
                    new ClaudeCodeUsageClient(
                        failedRecoveryHttp,
                        () => Array.Empty<string>(),
                        _ => Task.FromResult(false)).GetUsageAsync());
                True(failedException.Message.Contains("cannot read", StringComparison.OrdinalIgnoreCase));
                Equal(1, failedRecoveryRequests);
            }

            var boundedRetryRequests = 0;
            var boundedRetryProbes = 0;
            using (var boundedRetryHttp = new HttpClient(new StubHttpHandler((_, _, _) =>
                   {
                       boundedRetryRequests++;
                       return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                   })))
            {
                var boundedException = await ThrowsAsync<ClaudeCodeUsageException>(() =>
                    new ClaudeCodeUsageClient(
                        boundedRetryHttp,
                        () => Array.Empty<string>(),
                        _ =>
                        {
                            boundedRetryProbes++;
                            return Task.FromResult(true);
                        }).GetUsageAsync());
                True(boundedException.Message.Contains("expired", StringComparison.OrdinalIgnoreCase));
                Equal(2, boundedRetryRequests);
                Equal(1, boundedRetryProbes);
            }
        }
        finally
        {
            RestoreEnvironment(environment);
            Directory.Delete(directory, recursive: true);
        }
    }

    public static async Task TestCopilotHttpAsync()
    {
        var environment = SaveEnvironment(
            "COPILOT_GITHUB_TOKEN",
            "USAGEAI_ENABLE_GH_TOKEN_FALLBACK");
        try
        {
            Environment.SetEnvironmentVariable("COPILOT_GITHUB_TOKEN", "synthetic-copilot-token");
            Environment.SetEnvironmentVariable("USAGEAI_ENABLE_GH_TOKEN_FALLBACK", null);

            var successHandler = new StubHttpHandler((request, _, _) =>
            {
                Equal("Bearer", request.Headers.Authorization?.Scheme);
                True(request.Headers.UserAgent.Count > 0);
                return Task.FromResult(JsonResponse(
                    HttpStatusCode.OK,
                    """
                    {
                      "copilot_plan":"business",
                      "login":"coverage-user",
                      "quota_snapshots":{
                        "premium_interactions":{"entitlement":100,"remaining":35}
                      }
                    }
                    """));
            });
            using (var http = new HttpClient(successHandler))
            {
                var client = new GitHubCopilotUsageClient(
                    http,
                    _ => Task.FromResult<IReadOnlyList<string>>(SingleCopilotToken));
                var first = await client.GetUsageAsync();
                var second = await client.GetUsageAsync();
                Equal("Business", first.Plan);
                Equal(65, first.Primary!.UsedPercent);
                Equal(first.Primary.UsedPercent, second.Primary!.UsedPercent);
                Equal(2, successHandler.CallCount);
            }

            var rejectedHandler = new StubHttpHandler((_, _, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
            using (var http = new HttpClient(rejectedHandler))
            {
                var exception = await ThrowsAsync<GitHubCopilotUsageException>(
                    () => new GitHubCopilotUsageClient(
                        http,
                        _ => Task.FromResult<IReadOnlyList<string>>(SingleCopilotToken))
                        .GetUsageAsync());
                True(exception.Message.Contains("cannot access Copilot", StringComparison.Ordinal));
                True(rejectedHandler.CallCount > 0);
            }

            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))))
            {
                var exception = await ThrowsAsync<GitHubCopilotUsageException>(
                    () => new GitHubCopilotUsageClient(
                        http,
                        _ => Task.FromResult<IReadOnlyList<string>>(SingleCopilotToken))
                        .GetUsageAsync());
                True(exception.InnerException is GitHubCopilotUsageException);
            }

            foreach (var failure in new Func<HttpResponseMessage>[]
                     {
                         () => throw new OperationCanceledException(),
                         () => throw new InvalidOperationException("synthetic transport failure"),
                     })
            {
                using var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                    Task.FromResult(failure())));
                var exception = await ThrowsAsync<GitHubCopilotUsageException>(
                    () => new GitHubCopilotUsageClient(
                        http,
                        _ => Task.FromResult<IReadOnlyList<string>>(
                            SingleCopilotToken))
                        .GetUsageAsync());
                True(exception.InnerException is GitHubCopilotUsageException);
            }
        }
        finally
        {
            RestoreEnvironment(environment);
        }
    }

    public static async Task TestGeminiHttpAsync()
    {
        var environment = SaveEnvironment(
            "GEMINI_CONFIG_DIR",
            "GEMINI_CLIENT_ID",
            "GEMINI_CLIENT_SECRET");
        var directory = Path.Combine(AppPaths.DataDirectory, $"gemini-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            Environment.SetEnvironmentVariable("GEMINI_CONFIG_DIR", directory);
            Environment.SetEnvironmentVariable("GEMINI_CLIENT_ID", "synthetic-client-id");
            Environment.SetEnvironmentVariable("GEMINI_CLIENT_SECRET", "synthetic-client-secret");
            WriteGeminiCredentials(directory, "old-access", refreshToken: null, DateTimeOffset.UtcNow.AddHours(1));

            var successHandler = GeminiHandler(HttpStatusCode.OK);
            using (var http = new HttpClient(successHandler))
            {
                var client = new GeminiUsageClient(http, NoLocalGeminiSnapshot);
                var snapshot = await client.GetUsageAsync();
                Equal("Gemini Code Assist Pro", snapshot.Plan);
                Equal(20, snapshot.Primary!.UsedPercent);
                Equal(2, successHandler.CallCount);
            }

            var unauthorizedHandler = GeminiHandler(HttpStatusCode.Unauthorized);
            using (var http = new HttpClient(unauthorizedHandler))
            {
                var exception = await ThrowsAsync<GeminiUsageException>(
                    () => new GeminiUsageClient(http, NoLocalGeminiSnapshot).GetUsageAsync());
                True(exception.Message.Contains("expired", StringComparison.OrdinalIgnoreCase));
            }

            var throttledHandler = GeminiHandler(HttpStatusCode.TooManyRequests);
            using (var http = new HttpClient(throttledHandler))
            {
                var exception = await ThrowsAsync<GeminiUsageException>(
                    () => new GeminiUsageClient(http, NoLocalGeminiSnapshot).GetUsageAsync());
                Equal(TimeSpan.FromSeconds(29), exception.RetryAfter);
            }

            WriteGeminiCredentials(
                directory,
                "expired-access",
                "synthetic-refresh",
                DateTimeOffset.UtcNow.AddMinutes(-10));
            var refreshHandler = new StubHttpHandler((request, _, _) =>
            {
                if (request.RequestUri!.AbsoluteUri.Contains("oauth2.googleapis.com/token", StringComparison.Ordinal))
                {
                    return Task.FromResult(JsonResponse(
                        HttpStatusCode.OK,
                        """{"access_token":"new-access","expires_in":3600}"""));
                }

                if (request.RequestUri.AbsoluteUri.Contains("loadCodeAssist", StringComparison.Ordinal))
                {
                    return Task.FromResult(JsonResponse(
                        HttpStatusCode.OK,
                        """{"currentTier":{"id":"free-tier"}}"""));
                }

                return Task.FromResult(JsonResponse(
                    HttpStatusCode.OK,
                    """{"buckets":[{"modelId":"gemini-flash","remainingFraction":0.65}]}"""));
            });
            using (var http = new HttpClient(refreshHandler))
            {
                var snapshot = await new GeminiUsageClient(http, NoLocalGeminiSnapshot).GetUsageAsync();
                Equal("Free", snapshot.Plan);
                Equal(35, snapshot.Primary!.UsedPercent);
                Equal(3, refreshHandler.CallCount);
            }

            var local = Snapshot("gemini", "Google Gemini", 12, DateTimeOffset.Now);
            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       throw new InvalidOperationException("HTTP should not be reached."))))
            {
                var client = new GeminiUsageClient(http, _ => Task.FromResult<UsageSnapshot?>(local));
                Equal(local, await client.GetUsageAsync());
            }
        }
        finally
        {
            RestoreEnvironment(environment);
            Directory.Delete(directory, recursive: true);
        }
    }

    public static async Task TestCodexProtocolAsync()
    {
        var environment = SaveEnvironment("CODEX_PATH", "CODEX_HOME");
        try
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("The test executable path is unavailable.");
            True(File.Exists(executable));
            True(executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            Environment.SetEnvironmentVariable("CODEX_PATH", executable);

            Environment.SetEnvironmentVariable("CODEX_HOME", "usageai-test-success");
            var snapshot = await new CodexUsageClient().GetUsageAsync();
            Equal("Pro", snapshot.Plan);
            Equal(44, snapshot.Metrics[0].UsedPercent);
            Equal(61, snapshot.Metrics[1].UsedPercent);
            Equal("Codex", new CodexUsageClient().DisplayName);
            Equal("codex login", new CodexUsageClient().SignInCommand);

            Environment.SetEnvironmentVariable("CODEX_HOME", "usageai-test-auth-error");
            var authentication = await ThrowsAsync<CodexUsageException>(
                () => new CodexUsageClient().GetUsageAsync());
            True(authentication.Message.Contains("codex login", StringComparison.OrdinalIgnoreCase));

            Environment.SetEnvironmentVariable("CODEX_HOME", "usageai-test-missing-result");
            var missing = await ThrowsAsync<CodexUsageException>(
                () => new CodexUsageClient().GetUsageAsync());
            True(missing.Message.Contains("without usage data", StringComparison.Ordinal));

            Environment.SetEnvironmentVariable("CODEX_HOME", "usageai-test-premature-exit");
            var stopped = await ThrowsAsync<CodexUsageException>(
                () => new CodexUsageClient().GetUsageAsync());
            True(stopped.Message.Contains("stopped", StringComparison.OrdinalIgnoreCase));

            Environment.SetEnvironmentVariable("CODEX_HOME", "usageai-test-generic-error");
            var generic = await ThrowsAsync<CodexUsageException>(
                () => new CodexUsageClient().GetUsageAsync());
            True(generic.Message.Contains("could not read", StringComparison.OrdinalIgnoreCase));

            Environment.SetEnvironmentVariable("CODEX_HOME", "usageai-test-too-many");
            var excessive = await ThrowsAsync<CodexUsageException>(
                () => new CodexUsageClient().GetUsageAsync());
            True(excessive.Message.Contains("too many", StringComparison.OrdinalIgnoreCase));

            Environment.SetEnvironmentVariable("CODEX_PATH", "relative-codex.exe");
            var invalidPath = await ThrowsAsync<CodexUsageException>(
                () => new CodexUsageClient().GetUsageAsync());
            True(invalidPath.Message.Contains("absolute path", StringComparison.OrdinalIgnoreCase));

            var launchDirectory = Path.Combine(AppPaths.DataDirectory, $"codex-launch-{Guid.NewGuid():N}");
            Directory.CreateDirectory(launchDirectory);
            try
            {
                var command = Path.Combine(launchDirectory, "codex.cmd");
                File.WriteAllText(command, "@echo off");
                Throws<CodexUsageException>(() =>
                    InvokePrivateStatic<object>(
                        typeof(CodexUsageClient),
                        "CreateLaunch",
                        command));

                var script = Path.Combine(
                    launchDirectory,
                    "node_modules",
                    "@openai",
                    "codex",
                    "bin",
                    "codex.js");
                Directory.CreateDirectory(Path.GetDirectoryName(script)!);
                File.WriteAllText(script, "// synthetic");
                var pathBeforeMissingNode = Environment.GetEnvironmentVariable("PATH");
                try
                {
                    Environment.SetEnvironmentVariable("PATH", launchDirectory);
                    Throws<CodexUsageException>(() =>
                        InvokePrivateStatic<object>(
                            typeof(CodexUsageClient),
                            "CreateLaunch",
                            command));
                }
                finally
                {
                    Environment.SetEnvironmentVariable("PATH", pathBeforeMissingNode);
                }

                var node = Path.Combine(launchDirectory, "node.exe");
                File.Copy(executable, node);
                var launch = InvokePrivateStatic<object>(
                    typeof(CodexUsageClient),
                    "CreateLaunch",
                    command);
                Equal(Path.GetFullPath(node), GetProperty<string>(launch, "Executable"));
                Equal(script, GetProperty<string>(launch, "Script"));

                var previousPath = Environment.GetEnvironmentVariable("PATH");
                try
                {
                    Environment.SetEnvironmentVariable("CODEX_PATH", null);
                    Environment.SetEnvironmentVariable("PATH", launchDirectory);
                    var codexExe = Path.Combine(launchDirectory, "codex.exe");
                    File.Copy(executable, codexExe);
                    var discovered = InvokePrivateStatic<object>(
                        typeof(CodexUsageClient),
                        "FindCodexLaunch");
                    Equal(Path.GetFullPath(codexExe), GetProperty<string>(discovered, "Executable"));
                }
                finally
                {
                    Environment.SetEnvironmentVariable("PATH", previousPath);
                }
            }
            finally
            {
                Directory.Delete(launchDirectory, recursive: true);
            }

            Equal(
                string.Empty,
                await InvokePrivateStaticTaskResultAsync<string>(
                    typeof(CodexUsageClient),
                    "TryReadErrorAsync",
                    Task.FromException<string>(
                        new InvalidOperationException("synthetic stderr failure"))));
            using var unstarted = new Process();
            var stopTask = InvokePrivateStatic<object>(
                typeof(CodexUsageClient),
                "StopProcessAsync",
                unstarted) as Task
                ?? throw new InvalidOperationException("StopProcessAsync returned no task.");
            await stopTask;
        }
        finally
        {
            RestoreEnvironment(environment);
        }
    }

    public static async Task TestClaudeWebHttpAsync()
    {
        var environment = SaveEnvironment(
            "USAGEAI_CLAUDE_SESSION_KEY",
            "CLAUDE_AI_SESSION_KEY",
            "CLAUDE_WEB_SESSION_KEY");
        try
        {
            Environment.SetEnvironmentVariable("USAGEAI_CLAUDE_SESSION_KEY", "synthetic-web-session");
            Environment.SetEnvironmentVariable("CLAUDE_AI_SESSION_KEY", null);
            Environment.SetEnvironmentVariable("CLAUDE_WEB_SESSION_KEY", null);

            var successHandler = new StubHttpHandler((request, _, _) =>
            {
                True(request.Headers.TryGetValues("Cookie", out var cookies));
                True(cookies!.Single().StartsWith("sessionKey=", StringComparison.Ordinal));
                True(request.Headers.Contains("anthropic-client-platform"));

                var path = request.RequestUri!.AbsolutePath;
                if (path == "/api/account")
                {
                    return Task.FromResult(JsonResponse(
                        HttpStatusCode.OK,
                        """
                        {
                          "email_address":"web@example.com",
                          "rate_limit_tier":"max_5x",
                          "memberships":[{"organization":{"uuid":"org/id"}}]
                        }
                        """));
                }

                if (path.EndsWith("/usage", StringComparison.Ordinal))
                {
                    True(request.RequestUri.AbsoluteUri.Contains("org%2Fid", StringComparison.OrdinalIgnoreCase));
                    return Task.FromResult(JsonResponse(
                        HttpStatusCode.OK,
                        """{"five_hour":{"utilization":18},"seven_day":{"utilization":42}}"""));
                }

                return Task.FromResult(JsonResponse(
                    HttpStatusCode.OK,
                    """
                    {
                      "is_enabled":true,
                      "used_credits":250,
                      "monthly_credit_limit":1000,
                      "currency":"USD"
                    }
                    """));
            });
            using (var http = new HttpClient(successHandler))
            {
                var snapshot = await ClaudeWebUsageClient.GetUsageAsync(http, CancellationToken.None);
                Equal("Max 5X", snapshot.Plan);
                Equal("web@example.com", snapshot.AccountName);
                Equal(3, snapshot.Metrics.Count);
                Equal(3, successHandler.CallCount);
            }

            var fallbackHandler = new StubHttpHandler((request, _, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/api/account")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                }

                if (path == "/api/organizations")
                {
                    return Task.FromResult(JsonResponse(
                        HttpStatusCode.OK,
                        """[{"uuid":"fallback-org"}]"""));
                }

                if (path.EndsWith("/usage", StringComparison.Ordinal))
                {
                    return Task.FromResult(JsonResponse(
                        HttpStatusCode.OK,
                        """{"five_hour":{"utilization":33}}"""));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });
            using (var http = new HttpClient(fallbackHandler))
            {
                var snapshot = await ClaudeWebUsageClient.GetUsageAsync(http, CancellationToken.None);
                Equal("Claude", snapshot.Plan);
                Equal(1, snapshot.Metrics.Count);
                Equal(4, fallbackHandler.CallCount);
            }

            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)))))
            {
                var exception = await ThrowsAsync<ClaudeWebUsageException>(
                    () => ClaudeWebUsageClient.GetUsageAsync(http, CancellationToken.None));
                True(exception.Message.Contains("no longer valid", StringComparison.Ordinal));
            }

            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       throw new OperationCanceledException())))
            {
                var exception = await ThrowsAsync<ClaudeWebUsageException>(
                    () => ClaudeWebUsageClient.GetUsageAsync(http, CancellationToken.None));
                True(exception.Message.Contains("timed out", StringComparison.Ordinal));
            }

            Environment.SetEnvironmentVariable("USAGEAI_CLAUDE_SESSION_KEY", null);
            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       throw new InvalidOperationException("HTTP should not be reached."))))
            {
                var exception = await ThrowsAsync<ClaudeWebUsageException>(
                    () => ClaudeWebUsageClient.GetUsageAsync(http, CancellationToken.None));
                True(exception.Message.Contains("not configured", StringComparison.Ordinal));
            }
        }
        finally
        {
            RestoreEnvironment(environment);
        }
    }

    public static async Task TestUpdateCheckerHttpAsync()
    {
        var successHandler = new StubHttpHandler((request, _, _) =>
        {
            Equal(HttpMethod.Get, request.Method);
            True(request.Headers.Contains("X-GitHub-Api-Version"));
            return Task.FromResult(JsonResponse(
                HttpStatusCode.OK,
                """
                {
                  "tag_name":"v99.0.0",
                  "html_url":"https://github.com/VladiKogan/UsageAI/releases/tag/v99.0.0",
                  "assets":[
                    {
                      "name":"UsageAI-99.0.0-Setup.exe",
                      "size":123,
                      "browser_download_url":"https://github.com/VladiKogan/UsageAI/releases/download/v99.0.0/UsageAI-99.0.0-Setup.exe"
                    },
                    {
                      "name":"UsageAI-99.0.0-Setup.exe.sha256",
                      "size":100,
                      "browser_download_url":"https://github.com/VladiKogan/UsageAI/releases/download/v99.0.0/UsageAI-99.0.0-Setup.exe.sha256"
                    }
                  ]
                }
                """));
        });
        using (var http = new HttpClient(successHandler))
        {
            var check = await UpdateChecker.CheckForUpdateAsync(http, CancellationToken.None);
            True(check.Succeeded);
            var release = check.Release;
            NotNull(release);
            Equal("v99.0.0", release!.Tag);
            Equal("99.0.0", release.Version);
            Equal("UsageAI-99.0.0-Setup.exe", release.Installer!.Name);
            Equal("UsageAI-99.0.0-Setup.exe.sha256", release.Checksum!.Name);
        }

        using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                   Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"tag_name":"v0.1.0"}""")))))
        {
            var check = await UpdateChecker.CheckForUpdateAsync(http, CancellationToken.None);
            True(check.Succeeded);
            Null(check.Release);
        }

        using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                   Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"other":"value"}""")))))
        {
            var check = await UpdateChecker.CheckForUpdateAsync(http, CancellationToken.None);
            False(check.Succeeded);
            Null(check.Release);
        }

        using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                   Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))))
        {
            False((await UpdateChecker.CheckForUpdateAsync(http, CancellationToken.None)).Succeeded);
        }

        using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                   Task.FromResult(JsonResponse(HttpStatusCode.OK, "{invalid")))))
        {
            False((await UpdateChecker.CheckForUpdateAsync(http, CancellationToken.None)).Succeeded);
        }

        // Well-formed JSON of the wrong shape must report a failed check, not throw: reading a
        // property off a non-object raises InvalidOperationException, which the caller does not
        // catch and which would surface on the UI thread from the timer and the Settings button.
        foreach (var rootless in new[] { "[]", "\"release\"", "123", "{\"tag_name\":[\"v9.9.9\"]}" })
        {
            using var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                Task.FromResult(JsonResponse(HttpStatusCode.OK, rootless))));
            False((await UpdateChecker.CheckForUpdateAsync(http, CancellationToken.None)).Succeeded);
        }

        // A valid release whose asset entries are not objects still reports the release, with no
        // installer to download, rather than failing the whole check.
        foreach (var assets in new[] { "[1]", "[[]]", "[null]", "[\"UsageAI-9.9.9-Setup.exe\"]" })
        {
            using var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                Task.FromResult(JsonResponse(
                    HttpStatusCode.OK,
                    $"{{\"tag_name\":\"v9.9.9\",\"assets\":{assets}}}"))));
            var assetCheck = await UpdateChecker.CheckForUpdateAsync(http, CancellationToken.None);
            True(assetCheck.Succeeded);
            NotNull(assetCheck.Release);
            Null(assetCheck.Release!.Installer);
        }

        using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                   throw new HttpRequestException("synthetic failure"))))
        {
            False((await UpdateChecker.CheckForUpdateAsync(http, CancellationToken.None)).Succeeded);
        }
        using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                   throw new InvalidDataException("synthetic invalid body"))))
        {
            False((await UpdateChecker.CheckForUpdateAsync(http, CancellationToken.None)).Succeeded);
        }
        using (var cancellation = new CancellationTokenSource())
        using (var http = new HttpClient(new StubHttpHandler((_, _, token) =>
                   Task.FromCanceled<HttpResponseMessage>(token))))
        {
            cancellation.Cancel();
            False((await UpdateChecker.CheckForUpdateAsync(http, cancellation.Token)).Succeeded);
        }

        var malformedAssets = JsonSerializer.Serialize(new
        {
            tag_name = "v99.1.0",
            html_url = "http://github.com/VladiKogan/UsageAI/releases/tag/v99.1.0",
            assets = new object[]
            {
                new { },
                new { name = 1, browser_download_url = "unused", size = 1 },
                new { name = "missing-url" },
                new { name = "bad-url-kind", browser_download_url = 1, size = 1 },
                new { name = "missing-size", browser_download_url = "https://github.com/VladiKogan/UsageAI/releases/download/v99.1.0/x" },
                new { name = "bad-size-kind", browser_download_url = "https://github.com/VladiKogan/UsageAI/releases/download/v99.1.0/x", size = "1" },
                new { name = " ", browser_download_url = "https://github.com/VladiKogan/UsageAI/releases/download/v99.1.0/x", size = 1 },
                new { name = new string('x', 129), browser_download_url = "https://github.com/VladiKogan/UsageAI/releases/download/v99.1.0/x", size = 1 },
                new { name = "zero-size", browser_download_url = "https://github.com/VladiKogan/UsageAI/releases/download/v99.1.0/x", size = 0 },
                new { name = "invalid-uri", browser_download_url = "not a URI", size = 1 },
                new { name = "wrong-host", browser_download_url = "https://example.com/VladiKogan/UsageAI/releases/x", size = 1 },
                new { name = "wrong-path", browser_download_url = "https://github.com/other/repository/releases/x", size = 1 },
                new { name = "notes.txt", browser_download_url = "https://github.com/VladiKogan/UsageAI/releases/download/v99.1.0/notes.txt", size = 1 },
            },
        });
        using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                   Task.FromResult(JsonResponse(HttpStatusCode.OK, malformedAssets)))))
        {
            var check = await UpdateChecker.CheckForUpdateAsync(http, CancellationToken.None);
            True(check.Succeeded);
            NotNull(check.Release);
            Null(check.Release!.Installer);
            Null(check.Release.Checksum);
            Equal("https://github.com/VladiKogan/UsageAI/releases/latest", check.Release.ReleasePageUrl.AbsoluteUri);
        }

        foreach (var invalidTag in new[] { "", new string('v', 33), "not-a-version" })
        {
            using var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                Task.FromResult(JsonResponse(
                    HttpStatusCode.OK,
                    JsonSerializer.Serialize(new { tag_name = invalidTag })))));
            var check = await UpdateChecker.CheckForUpdateAsync(http, CancellationToken.None);
            False(check.Succeeded);
            Null(check.Release);
        }
        using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                   Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"tag_name":42}""")))))
        {
            False((await UpdateChecker.CheckForUpdateAsync(http, CancellationToken.None)).Succeeded);
        }

        foreach (var (json, currentVersion, expected) in new[]
                 {
                     ("{}", "1.0.0", (UpdateRelease?)null),
                     ("""{"tag_name":42}""", "1.0.0", null),
                     ("""{"tag_name":" "}""", "1.0.0", null),
                     (JsonSerializer.Serialize(new { tag_name = new string('v', 33) }), "1.0.0", null),
                     ("""{"tag_name":"v1.0.0"}""", "1.0.0", null),
                 })
        {
            using var document = JsonDocument.Parse(json);
            Equal(
                expected,
                InvokePrivateStatic<UpdateRelease?>(
                    typeof(UpdateChecker),
                    "ParseRelease",
                    document.RootElement,
                    currentVersion));
        }

        using (var document = JsonDocument.Parse(
                   """
                   {"tag_name":"v99-beta.1","assets":{},"html_url":"not a URI"}
                   """))
        {
            var prerelease = InvokePrivateStatic<UpdateRelease?>(
                typeof(UpdateChecker),
                "ParseRelease",
                document.RootElement,
                "1.0.0");
            NotNull(prerelease);
            Equal("99.0", prerelease!.Version);
            Null(prerelease.Installer);
            Null(prerelease.Checksum);
        }
        using (var document = JsonDocument.Parse(
                   """
                   {"tag_name":"v99.2.0","html_url":42}
                   """))
        {
            var numericPage = InvokePrivateStatic<UpdateRelease?>(
                typeof(UpdateChecker),
                "ParseRelease",
                document.RootElement,
                "1.0.0");
            NotNull(numericPage);
            Equal("https://github.com/VladiKogan/UsageAI/releases/latest", numericPage!.ReleasePageUrl.AbsoluteUri);
        }

        True(InvokePrivateStatic<bool>(
            typeof(UpdateChecker),
            "IsExpectedGitHubReleaseUri",
            new Uri("https://github.com/VladiKogan/UsageAI/releases/tag/v99.0.0")));
        False(InvokePrivateStatic<bool>(
            typeof(UpdateChecker),
            "IsExpectedGitHubReleaseUri",
            new Uri("http://github.com/VladiKogan/UsageAI/releases/tag/v99.0.0")));
        False(InvokePrivateStatic<bool>(
            typeof(UpdateChecker),
            "IsExpectedGitHubReleaseUri",
            new Uri("https://example.com/VladiKogan/UsageAI/releases/tag/v99.0.0")));
        False(InvokePrivateStatic<bool>(
            typeof(UpdateChecker),
            "IsExpectedGitHubReleaseUri",
            new Uri("https://github.com/other/repository/releases/tag/v99.0.0")));

        True(UpdateChecker.IsNewer("6-beta.1", "5.9.0"));
        False(UpdateChecker.IsNewer("5+build", "5.0"));
        False(UpdateChecker.IsNewer("invalid", "5.0"));
        False(UpdateChecker.IsNewer("6.0", "invalid"));
        Equal(new Version(7, 0), UpdateChecker.ParseVersion("7"));
        Null(UpdateChecker.ParseVersion("not-a-version"));
        var now = DateTimeOffset.UtcNow;
        True(UpdateChecker.IsCheckDue(null, now));
        False(UpdateChecker.IsCheckDue(now.AddHours(-23), now));
        True(UpdateChecker.IsCheckDue(now.AddHours(-24), now));
    }

    public static async Task TestUpdateInstallerAsync()
    {
        var installerName = "UsageAI-99.0.0-Setup.exe";
        var installerBytes = Encoding.UTF8.GetBytes("synthetic verified installer");
        var checksumText =
            $"{Convert.ToHexString(SHA256.HashData(installerBytes)).ToLowerInvariant()}  {installerName}";
        var checksumBytes = Encoding.ASCII.GetBytes(checksumText);
        var release = new UpdateRelease(
            "v99.0.0",
            "99.0.0",
            new Uri("https://github.com/VladiKogan/UsageAI/releases/tag/v99.0.0"),
            new UpdateAsset(
                installerName,
                new Uri($"https://github.com/VladiKogan/UsageAI/releases/download/v99.0.0/{installerName}"),
                installerBytes.Length),
            new UpdateAsset(
                $"{installerName}.sha256",
                new Uri($"https://github.com/VladiKogan/UsageAI/releases/download/v99.0.0/{installerName}.sha256"),
                checksumBytes.Length));

        var handler = new StubHttpHandler((request, call, _) => call switch
        {
            1 => Task.FromResult(RedirectResponse(
                "https://release-assets.githubusercontent.com/usageai/checksum")),
            2 => Task.FromResult(BinaryResponse(checksumBytes)),
            3 => Task.FromResult(RedirectResponse(
                "https://release-assets.githubusercontent.com/usageai/installer")),
            4 => Task.FromResult(BinaryResponse(installerBytes)),
            _ => throw new InvalidOperationException($"Unexpected update request: {request.RequestUri}"),
        });
        var directory = Path.Combine(
            Path.GetTempPath(),
            "UsageAI.UpdateTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            using var http = new HttpClient(handler);
            var installerPath = await UpdateInstaller.DownloadAndVerifyAsync(
                release,
                http,
                directory,
                CancellationToken.None);
            True(File.Exists(installerPath));
            True(File.ReadAllBytes(installerPath).SequenceEqual(installerBytes));
            Equal(4, handler.CallCount);

            var badChecksum = Encoding.ASCII.GetBytes($"{new string('0', 64)}  {installerName}");
            using var badHttp = new HttpClient(new StubHttpHandler((_, call, _) =>
                Task.FromResult(call == 1
                    ? BinaryResponse(badChecksum)
                    : BinaryResponse(installerBytes))));
            var badRelease = release with
            {
                Checksum = release.Checksum! with { Size = badChecksum.Length },
            };
            await ThrowsAsync<UpdateInstallException>(() => UpdateInstaller.DownloadAndVerifyAsync(
                badRelease,
                badHttp,
                directory,
                CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    public static async Task TestUpdateInstallerFailuresAsync()
    {
        var installerName = "UsageAI-99.0.1-Setup.exe";
        var installerBytes = Encoding.UTF8.GetBytes("synthetic installer");
        var checksumBytes = Encoding.ASCII.GetBytes(
            $"{Convert.ToHexString(SHA256.HashData(installerBytes)).ToLowerInvariant()}  {installerName}");
        var release = new UpdateRelease(
            "v99.0.1",
            "99.0.1",
            new Uri("https://github.com/VladiKogan/UsageAI/releases/tag/v99.0.1"),
            new UpdateAsset(
                installerName,
                new Uri($"https://github.com/VladiKogan/UsageAI/releases/download/v99.0.1/{installerName}"),
                installerBytes.Length),
            new UpdateAsset(
                $"{installerName}.sha256",
                new Uri($"https://github.com/VladiKogan/UsageAI/releases/download/v99.0.1/{installerName}.sha256"),
                checksumBytes.Length));
        var directory = Path.Combine(
            Path.GetTempPath(),
            "UsageAI.UpdateFailureTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            using var unusedHttp = new HttpClient(new StubHttpHandler((_, _, _) =>
                throw new InvalidOperationException("No HTTP request was expected.")));
            var missing = await ThrowsAsync<UpdateInstallException>(() =>
                UpdateInstaller.DownloadAndVerifyAsync(
                    release with { Checksum = null },
                    unusedHttp,
                    directory,
                    CancellationToken.None));
            Equal("This release does not include a verifiable Windows installer.", missing.Message);

            var invalidSize = await ThrowsAsync<UpdateInstallException>(() =>
                UpdateInstaller.DownloadAndVerifyAsync(
                    release with { Installer = release.Installer! with { Size = 0 } },
                    unusedHttp,
                    directory,
                    CancellationToken.None));
            Equal("The published update has an unexpected size.", invalidSize.Message);

            var unsafeHandler = new StubHttpHandler((_, _, _) => Task.FromResult(
                RedirectResponse("https://downloads.example.com/installer")));
            using (var unsafeHttp = new HttpClient(unsafeHandler))
            {
                var unsafeRedirect = await ThrowsAsync<UpdateInstallException>(() =>
                    UpdateInstaller.DownloadAndVerifyAsync(
                        release,
                        unsafeHttp,
                        directory,
                        CancellationToken.None));
                Equal("GitHub returned an unsafe update download address.", unsafeRedirect.Message);
                Equal(1, unsafeHandler.CallCount);
            }

            var wrongNameBytes = Encoding.ASCII.GetBytes(
                $"{Convert.ToHexString(SHA256.HashData(installerBytes)).ToLowerInvariant()}  another.exe");
            var wrongNameRelease = release with
            {
                Checksum = release.Checksum! with { Size = wrongNameBytes.Length },
            };
            var wrongNameHandler = new StubHttpHandler((_, _, _) =>
                Task.FromResult(BinaryResponse(wrongNameBytes)));
            using (var wrongNameHttp = new HttpClient(wrongNameHandler))
            {
                var malformed = await ThrowsAsync<UpdateInstallException>(() =>
                    UpdateInstaller.DownloadAndVerifyAsync(
                        wrongNameRelease,
                        wrongNameHttp,
                        directory,
                        CancellationToken.None));
                Equal("The published checksum is invalid.", malformed.Message);
                Equal(1, wrongNameHandler.CallCount);
            }

            using (var failedHttp = new HttpClient(new StubHttpHandler((_, _, _) =>
                       Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)))))
            {
                var notFound = await ThrowsAsync<UpdateInstallException>(() =>
                    UpdateInstaller.DownloadAndVerifyAsync(
                        release,
                        failedHttp,
                        directory,
                        CancellationToken.None));
                Equal("GitHub did not return the requested update file.", notFound.Message);
            }

            var unverifiedPath = Path.Combine(directory, "not-an-installer.txt");
            Directory.CreateDirectory(directory);
            File.WriteAllText(unverifiedPath, "not executable");
            Throws<UpdateInstallException>(() => UpdateInstaller.Launch(unverifiedPath));
            Throws<UpdateInstallException>(() => UpdateInstaller.Launch(
                Path.Combine(directory, "missing.exe")));
            Equal(0, Directory.EnumerateFiles(directory, "*.partial").Count());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    public static Task TestProviderParserEdgesAsync()
    {
        using (var document = JsonDocument.Parse(
                   """
                   {
                     "rateLimitsByLimitId":{
                       "fallback":{
                         "planType":"business",
                         "primary":{"usedPercent":120,"windowDurationMins":120},
                         "secondary":{"usedPercent":-5,"windowDurationMins":20160},
                         "credits":{"balance":17}
                       }
                     },
                     "rateLimitResetCredits":{"availableCount":1}
                   }
                   """))
        {
            var snapshot = CodexUsageClient.ParseSnapshot(document.RootElement);
            Equal("Business", snapshot.Plan);
            Equal("2-hour", snapshot.Metrics[0].Name);
            Equal(100, snapshot.Metrics[0].UsedPercent);
            Equal("2-week", snapshot.Metrics[1].Name);
            Equal(0, snapshot.Metrics[1].UsedPercent);
            Equal("17", snapshot.Metrics[2].DisplayRemaining);
            Equal("Full reset available", snapshot.Metrics[3].DisplayUsage);
        }

        using (var document = JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":25}}}"""))
        {
            var snapshot = CodexUsageClient.ParseSnapshot(document.RootElement);
            Equal("Session", snapshot.Primary!.Name);
            Equal("Codex", snapshot.Plan);
        }

        using (var document = JsonDocument.Parse("""{}"""))
        {
            Throws<CodexUsageException>(() => CodexUsageClient.ParseSnapshot(document.RootElement));
        }
        Equal(
            "Session",
            InvokePrivateStatic<string>(
                typeof(CodexUsageClient),
                "FormatWindowName",
                null,
                "Session"));
        Equal(
            "Fallback",
            InvokePrivateStatic<string>(
                typeof(CodexUsageClient),
                "FormatWindowName",
                90L,
                "Fallback"));
        Equal(
            "Codex could not read the account rate limits.",
            InvokePrivateStatic<string>(
                typeof(CodexUsageClient),
                "ToFriendlyError",
                (object?)null));
        foreach (var plan in new[] { "prolite", "team", "enterprise", "edu" })
        {
            True(InvokePrivateStatic<string>(
                    typeof(CodexUsageClient),
                    "FormatPlan",
                    plan)
                .Length > 0);
        }

        using (var document = JsonDocument.Parse(
                   """
                   {
                     "fiveHour":{"utilization":0.25,"resetsAt":"not-a-date"},
                     "extraUsage":{"isEnabled":false}
                   }
                   """))
        {
            var snapshot = ClaudeCodeUsageClient.ParseSnapshot(document.RootElement);
            Equal(25, snapshot.Primary!.UsedPercent);
            Equal(1, snapshot.Metrics.Count);
        }

        Equal("Max 20X", ClaudeCodeUsageClient.FormatPlan("max_20x"));
        Equal("Claude Pro", ClaudeCodeUsageClient.FormatPlan("pro"));
        Equal("Claude", ClaudeCodeUsageClient.FormatPlan(null));
        using (var document = JsonDocument.Parse("""{}"""))
        {
            Throws<ClaudeCodeUsageException>(
                () => ClaudeCodeUsageClient.ParseSnapshot(document.RootElement));
        }
        using (var document = JsonDocument.Parse(
                   """
                   {
                     "five_hour":{},
                     "limits":[
                       null,
                       {},
                       {"kind":"other","percent":10},
                       {"kind":"weekly_all","group":"daily","percent":20},
                       {"kind":"weekly_all","group":"weekly"},
                       {"kind":"weekly_all","group":"weekly","percent":35}
                     ]
                   }
                   """))
        {
            var snapshot = ClaudeCodeUsageClient.ParseSnapshot(document.RootElement);
            Equal("Weekly", snapshot.Primary!.Name);
            Equal(35, snapshot.Primary.UsedPercent);
        }
        using (var document = JsonDocument.Parse("""{"is_enabled":true,"used_credits":500}"""))
        {
            NotNull(ClaudeCodeUsageClient.CreateExtraUsageMetric(document.RootElement));
        }
        using (var document = JsonDocument.Parse("""{"is_enabled":true}"""))
        {
            Equal("ENABLED", ClaudeCodeUsageClient.FormatExtraUsageObject(document.RootElement));
        }

        using (var document = JsonDocument.Parse(
                   """
                   {
                     "access_type_sku":"enterprise",
                     "quota_reset_date":"invalid",
                     "quota_snapshots":{
                       "premium_interactions":{
                         "entitlement":200,
                         "quota_remaining":50,
                         "quota_reset_at":999999999999
                       },
                       "chat":{"has_quota":false}
                     }
                   }
                   """))
        {
            var snapshot = GitHubCopilotUsageClient.ParseSnapshot(document.RootElement);
            Equal("Enterprise", snapshot.Plan);
            Equal(75, snapshot.Primary!.UsedPercent);
            Null(snapshot.Primary.ResetsAt);
        }

        using (var document = JsonDocument.Parse("""{"quota_snapshots":{"chat":{"has_quota":false}}}"""))
        {
            Throws<GitHubCopilotUsageException>(
                () => GitHubCopilotUsageClient.ParseSnapshot(document.RootElement));
        }

        using (var document = JsonDocument.Parse(
                   """
                   {
                     "buckets":[
                       {"modelId":"custom_model","remainingFraction":1.5},
                       {"modelId":"custom_model","remainingFraction":0.4},
                       null,
                       {"remainingFraction":0.2}
                     ]
                   }
                   """))
        {
            var snapshot = GeminiUsageClient.ParseQuotaResponse(document.RootElement);
            Equal("Custom Model", snapshot.Primary!.Name);
            Equal(60, snapshot.Primary.UsedPercent);
        }

        using (var document = JsonDocument.Parse("""{"response":{}}"""))
        {
            Equal(0, GeminiUsageClient.ParseQuotaSummaryResponse(document.RootElement).Count);
            Null(GeminiUsageClient.ParseAntigravityUserStatus(document.RootElement));
        }

        using (var document = JsonDocument.Parse("""{}"""))
        {
            Throws<GeminiUsageException>(
                () => GeminiUsageClient.ParseQuotaResponse(document.RootElement));
        }

        using (var document = JsonDocument.Parse("""{"buckets":[]}"""))
        {
            Throws<GeminiUsageException>(() => GeminiUsageClient.ParseQuotaResponse(document.RootElement));
        }

        using (var document = JsonDocument.Parse(
                   """
                   {
                     "groups":[
                       null,
                       {"displayName":"","buckets":[]},
                       {"displayName":"Other Models","buckets":[null,{},{"remainingFraction":0.5,"displayName":"Daily"}]}
                     ]
                   }
                   """))
        {
            var metrics = GeminiUsageClient.ParseQuotaSummaryResponse(document.RootElement);
            Equal(1, metrics.Count);
            Equal("Other Models (Daily)", metrics[0].Name);
        }

        foreach (var json in new[]
                 {
                     """{"response":{"groups":[{"displayName":"Gemini Models","buckets":[{"remainingFraction":0.4,"bucketId":"7d"}]}]}}""",
                     """{"response":{"quotaSummary":{"groups":[{"displayName":"Gemini Models","buckets":[{"remainingFraction":0.4,"bucketId":"5h"}]}]}}}""",
                 })
        {
            using var document = JsonDocument.Parse(json);
            Equal(1, GeminiUsageClient.ParseQuotaSummaryResponse(document.RootElement).Count);
        }
        Equal(
            1,
            GeminiUsageClient.MergeAntigravityQuotaSummaryMetrics(
                new[]
                {
                    new UsageMetric("Existing", UsageMetricKind.Session, 20),
                },
                Array.Empty<UsageMetric>())
            .Count);

        var credentials = new GeminiUsageClient.GeminiCredentials(
            "access",
            "refresh",
            null,
            DateTimeOffset.UtcNow.AddMinutes(2),
            null);
        True(credentials.IsExpired);
        var claudeCredentials = new ClaudeCodeUsageClient.ClaudeCredentials(
            "access",
            DateTimeOffset.UtcNow.AddHours(1),
            Array.Empty<string>(),
            "Claude");
        False(claudeCredentials.IsExpired);

        var forecastNow = DateTimeOffset.Now;
        Null(UsageForecast.Project(
            Array.Empty<UsageSample>(),
            "codex",
            new UsageMetric("Balance", UsageMetricKind.Balance, null),
            forecastNow));
        Null(UsageForecast.Project(
            Array.Empty<UsageSample>(),
            "codex",
            new UsageMetric("Session", UsageMetricKind.Session, 100),
            forecastNow));
        var forecastMetric = new UsageMetric("Session", UsageMetricKind.Session, 50);
        Null(UsageForecast.Project(
            ForecastSamples(forecastNow, 10, 20, 30, TimeSpan.FromMinutes(5)),
            "codex",
            forecastMetric,
            forecastNow));
        Null(UsageForecast.Project(
            ForecastSamples(forecastNow, 30, 25, 20, TimeSpan.FromMinutes(30)),
            "codex",
            forecastMetric,
            forecastNow));
        Null(UsageForecast.Project(
            ForecastSamples(forecastNow, 10, 10, 11, TimeSpan.FromHours(15)),
            "codex",
            forecastMetric,
            forecastNow));
        Null(UsageForecast.Project(
            ForecastSamples(forecastNow, 0, 0, 1, TimeSpan.FromHours(5)),
            "codex",
            new UsageMetric("Session", UsageMetricKind.Session, 0),
            forecastNow));
        return Task.CompletedTask;
    }

    public static Task TestCorruptLocalStateAsync()
    {
        AppPaths.EnsureDirectory();
        File.WriteAllText(AppPaths.SettingsFile, "{not-json");
        var defaults = AppSettings.Load();
        Equal(5, defaults.RefreshIntervalMinutes);

        File.WriteAllText(
            AppPaths.SettingsFile,
            """
            {
              "RefreshIntervalMinutes":-10,
              "WarningPercent":99,
              "CriticalPercent":1,
              "NotifyAtPercent":null,
              "HiddenProviders":[null,"claude","CLAUDE",""],
              "ProviderOrder":["gemini","GEMINI"],
              "TrayProviderId":"   ",
              "DashboardBounds":[1,2,3]
            }
            """);
        var repaired = AppSettings.Load();
        Equal(AppSettings.MinimumRefreshMinutes, repaired.RefreshIntervalMinutes);
        Equal(99, repaired.WarningPercent);
        Equal(100, repaired.CriticalPercent);
        Equal(2, repaired.NotifyAtPercent.Length);
        Equal(1, repaired.HiddenProviders.Length);
        Equal(1, repaired.ProviderOrder.Length);
        Null(repaired.TrayProviderId);
        Null(repaired.DashboardBounds);
        Equal(80, repaired.AlertThreshold(0, 12));
        Equal(12, repaired.AlertThreshold(5, 12));
        Equal(TimeSpan.FromMinutes(3), repaired.EffectiveRefreshInterval(anyWindowVisible: false));
        repaired.SlowRefreshWhenHidden = false;
        Equal(TimeSpan.FromMinutes(1), repaired.EffectiveRefreshInterval(anyWindowVisible: false));

        var now = DateTimeOffset.Now;
        File.WriteAllLines(
            AppPaths.HistoryFile,
            new[]
            {
                string.Empty,
                "{bad",
                """{"t":"2026-01-01T00:00:00Z"}""",
                $$"""{"t":"{{now.AddMinutes(-1):O}}","p":"codex","m":"Session:5-hour","u":150}""",
                $$"""{"t":"{{now.AddMinutes(-2):O}}","p":"codex","m":"Session:5-hour","u":-20}""",
            });
        var history = UsageHistoryStore.Load(TimeSpan.FromHours(1));
        Equal(2, history.Count);
        Equal(0, history[0].UsedPercent);
        Equal(100, history[1].UsedPercent);
        UsageHistoryStore.Append(Array.Empty<UsageSample>());

        var recentLine =
            $$"""{"t":"{{now.AddMinutes(-3):O}}","p":"gemini","m":"Rolling:Quota","u":45}""";
        File.WriteAllText(
            AppPaths.HistoryFile,
            new string(' ', 530_000) + Environment.NewLine + recentLine + Environment.NewLine);
        UsageHistoryStore.Append(new[]
        {
            new UsageSample(now, "gemini", "Rolling:Quota", 46),
        });
        True(new FileInfo(AppPaths.HistoryFile).Length < 530_000);
        Equal(2, UsageHistoryStore.Load(TimeSpan.FromHours(1)).Count);

        File.WriteAllText(AppPaths.HistoryFile, new string('x', 4 * 1024 * 1024 + 1));
        Equal(0, UsageHistoryStore.Load(TimeSpan.FromHours(1)).Count);

        File.WriteAllText(AppPaths.SnapshotCacheFile, "{broken");
        Equal(0, SnapshotCache.Load().Count);
        File.WriteAllText(AppPaths.SnapshotCacheFile, "null");
        Equal(0, SnapshotCache.Load().Count);
        SnapshotCache.Save(new[]
        {
            Snapshot("old", "Old", 20, DateTimeOffset.Now.AddDays(-3)),
        });
        Equal(0, SnapshotCache.Load().Count);

        File.Delete(AppPaths.SettingsFile);
        Equal(5, AppSettings.Load().RefreshIntervalMinutes);
        UsageHistoryStore.Clear();
        SnapshotCache.Clear();
        Equal(0, SnapshotCache.Load().Count);
        return Task.CompletedTask;
    }

    public static async Task TestSecurityUtilityEdgesAsync()
    {
        True(AppIdentity.Version.Length > 0);
        True(AppIdentity.UserAgent.StartsWith("UsageAI/", StringComparison.Ordinal));
        using (var client = SecureHttp.CreateClient(TimeSpan.FromSeconds(3)))
        {
            Equal(TimeSpan.FromSeconds(3), client.Timeout);
            Equal(SecureHttp.MaxJsonResponseBytes, client.MaxResponseContentBufferSize);
        }

        using (var response = JsonResponse(HttpStatusCode.OK, """{"ok":true}""", "application/problem+json"))
        using (var document = await SecureHttp.ReadJsonDocumentAsync(response, CancellationToken.None))
        {
            True(document.RootElement.GetProperty("ok").GetBoolean());
        }

        using (var response = new HttpResponseMessage(HttpStatusCode.OK)
               {
                   Content = new StringContent("<html>no</html>", Encoding.UTF8, "text/html"),
               })
        {
            await ThrowsAsync<InvalidDataException>(
                () => SecureHttp.ReadJsonDocumentAsync(response, CancellationToken.None));
        }

        using (var response = new HttpResponseMessage(HttpStatusCode.OK)
               {
                   Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("""{"value":"too long"}"""))),
               })
        {
            await ThrowsAsync<InvalidDataException>(
                () => SecureHttp.ReadJsonDocumentAsync(response, CancellationToken.None, maxBytes: 5));
        }

        var directory = Path.Combine(AppPaths.DataDirectory, $"security-edges-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var textPath = Path.Combine(directory, "text.txt");
            File.WriteAllText(textPath, "small");
            Equal("small", SecureLocalFile.ReadAllText(textPath, 5));
            File.WriteAllText(textPath, new string('x', 20));
            Throws<InvalidDataException>(() => SecureLocalFile.ReadAllText(textPath, 4));

            var executable = Path.Combine(directory, "coverage-tool.exe");
            File.WriteAllBytes(executable, Array.Empty<byte>());
            var previousPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable(
                    "PATH",
                    $"relative{Path.PathSeparator}\"{directory}\"");
                Equal(Path.GetFullPath(executable), ProcessSecurity.FindAbsoluteExecutableOnPath("coverage-tool.exe"));
                Null(ProcessSecurity.FindAbsoluteExecutableOnPath("missing.exe"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", previousPath);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        using (var reader = new StringReader(new string('a', 20)))
        {
            Equal("aaaaa", await ProcessSecurity.DrainTextAsync(reader, 5, CancellationToken.None));
        }

        using (var reader = new StringReader(string.Empty))
        {
            Null(await ProcessSecurity.ReadBoundedLineAsync(reader, 10, CancellationToken.None));
        }

        using (var reader = new StringReader("a\rb\n"))
        {
            Equal("ab", await ProcessSecurity.ReadBoundedLineAsync(reader, 10, CancellationToken.None));
        }

        ProcessSecurity.TryKill(null);
        using (var unstarted = new Process())
        {
            ProcessSecurity.TryKill(unstarted);
        }
        var commandProcessor = Environment.GetEnvironmentVariable("COMSPEC")
            ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using var completed = Process.Start(new ProcessStartInfo
        {
            FileName = commandProcessor,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/c", "exit", "0" },
        }) ?? throw new InvalidOperationException("Could not start a completed-process fixture.");
        await completed.WaitForExitAsync();
        ProcessSecurity.TryKill(completed);
    }

    public static Task TestUiRenderingAsync()
    {
        using (var minimumFont = Typography.Text(1F))
        using (var maximumFont = Typography.Mono(100F))
        using (var displayFont = Typography.Display(9F, FontStyle.Italic))
        {
            Equal(5F, minimumFont.Size);
            Equal(48F, maximumFont.Size);
            Equal(9F, displayFont.Size);
        }
        Equal(
            FontStyle.Italic,
            InvokePrivateStatic<FontStyle>(
                typeof(Typography),
                "AvailableStyle",
                "UsageAI definitely missing font",
                FontStyle.Italic));
        using (var installedFonts = new System.Drawing.Text.InstalledFontCollection())
        {
            var regularOnly = installedFonts.Families.FirstOrDefault(family =>
                family.IsStyleAvailable(FontStyle.Regular) &&
                !family.IsStyleAvailable(FontStyle.Bold));
            if (regularOnly is not null)
            {
                Equal(
                    FontStyle.Regular,
                    InvokePrivateStatic<FontStyle>(
                        typeof(Typography),
                        "AvailableStyle",
                        regularOnly.Name,
                        FontStyle.Bold));
            }
        }
        True(!string.IsNullOrWhiteSpace(InvokePrivateStatic<string>(
            typeof(Typography),
            "Resolve",
            (object)new[] { "UsageAI missing one", FontFamily.GenericSansSerif.Name })));
        True(!string.IsNullOrWhiteSpace(InvokePrivateStatic<string>(
            typeof(Typography),
            "Resolve",
            (object)MissingFontStack)));

        var themeChanges = 0;
        EventHandler handler = (_, _) => themeChanges++;
        Theme.Changed += handler;
        try
        {
            Theme.Apply(ThemeMode.Dark, 70, 90);
            True(Theme.IsDark);
            Theme.Apply(ThemeMode.Light, 70, 90);
            False(Theme.IsDark);
            Theme.Reapply(ThemeMode.Dark);
            True(themeChanges >= 3);
            Equal(Color.Red.ToArgb(), Theme.Blend(Color.Red, Color.Blue, 1).ToArgb());
            Equal(Color.Blue.ToArgb(), Theme.Blend(Color.Red, Color.Blue, 0).ToArgb());
            Equal(Theme.Codex, Theme.ForProvider("unknown"));

            using var bitmap = new Bitmap(760, 520);
            using var graphics = Graphics.FromImage(bitmap);
            using (var square = DrawingHelpers.RoundedRectangle(new Rectangle(0, 0, 20, 20), 0))
            {
                True(square.PointCount > 0);
            }

            using (var rounded = DrawingHelpers.RoundedRectangle(new RectangleF(5, 5, 40, 20), 50))
            {
                True(rounded.PointCount > 4);
            }

            DrawingHelpers.FillCard(graphics, new Rectangle(2, 2, 100, 40), Color.White, Color.Black, 8);
            DrawingHelpers.DrawCapacityMeter(graphics, new Rectangle(5, 50, 120, 8), 0, Color.Red, Color.Gray);
            DrawingHelpers.DrawCapacityMeter(graphics, new Rectangle(5, 65, 120, 8), 1, Color.Red, Color.Gray);
            DrawingHelpers.DrawCapacityMeter(graphics, new Rectangle(5, 80, 120, 8), 150, Color.Red, Color.Gray);
            DrawingHelpers.DrawCapacityMeter(graphics, Rectangle.Empty, 50, Color.Red, Color.Gray);
            DrawingHelpers.DrawBalanceMarker(graphics, new Rectangle(5, 95, 120, 8), Color.Blue);
            DrawingHelpers.DrawBalanceMarker(graphics, Rectangle.Empty, Color.Blue);
            DrawingHelpers.DrawSparkline(graphics, new Rectangle(5, 110, 140, 30), SparklineValues, Color.Green);
            DrawingHelpers.DrawSparkline(graphics, Rectangle.Empty, SingleSparklineValue, Color.Green);
            True(ProviderIconPainter.IsBrandFontAvailable);
            foreach (var provider in IconProviderIds)
            {
                ProviderIconPainter.Draw(graphics, new Rectangle(170, 5, 48, 48), provider);
            }

            var now = DateTimeOffset.Now;
            var snapshot = new UsageSnapshot(
                "Pro",
                new[]
                {
                    new UsageMetric("Session", UsageMetricKind.Session, 92, now.AddHours(2), 300),
                    new UsageMetric("Credits", UsageMetricKind.Balance, null, RemainingText: "$5"),
                    new UsageMetric("Chat", UsageMetricKind.Monthly, 0, IsUnlimited: true),
                },
                now,
                "codex",
                "Codex",
                "person@example.com");
            var history = Enumerable.Range(0, 6)
                .Select(index => new UsageSample(
                    now.AddMinutes(-50 + index * 10),
                    "codex",
                    "Session:Session",
                    30 + index * 10))
                .ToArray();
            var connected = new ProviderStatus(
                "codex",
                "Codex",
                snapshot,
                null,
                false,
                now,
                "codex login",
                new Uri("https://example.com/usage"));
            var stale = connected with { Error = "Temporary provider failure.", LastUpdated = now.AddHours(-2) };
            var disconnected = new ProviderStatus(
                "claude",
                "Claude Code",
                null,
                "Sign in required.",
                false,
                null,
                "claude",
                new Uri("https://example.com/claude"));
            var loading = disconnected with { ProviderId = "gemini", ProviderName = "Google Gemini", IsLoading = true };

            using var compact = new ProviderUsageCard(connected, expanded: false, history, showTrend: false)
            {
                Width = 420,
            };
            DrawControl(compact);

            using var expanded = new ProviderUsageCard(stale, expanded: true, history, showTrend: true)
            {
                Width = 620,
            };
            DrawControl(expanded);
            True(expanded.AccessibleDescription!.Contains("Temporary provider failure.", StringComparison.Ordinal));

            var actionCount = 0;
            using var disconnectedCard = new ProviderUsageCard(
                disconnected,
                expanded: true,
                Array.Empty<UsageSample>(),
                showTrend: false)
            {
                Width = 520,
            };
            disconnectedCard.ActionInvoked += (_, eventArgs) =>
            {
                actionCount++;
                Equal(ProviderCardAction.CopyCommand, eventArgs.Action);
            };
            DrawControl(disconnectedCard);
            InvokeProtected(disconnectedCard, "OnKeyDown", new KeyEventArgs(Keys.Enter));
            if (actionCount != 1)
            {
                throw new InvalidOperationException($"Expected one card action, got {actionCount}.");
            }

            using var loadingCard = new ProviderUsageCard(
                loading,
                expanded: true,
                Array.Empty<UsageSample>(),
                showTrend: false)
            {
                Width = 520,
            };
            DrawControl(loadingCard);

            var settings = new AppSettings
            {
                DashboardBounds = new[] { -10_000, -10_000, 640, 480 },
                ForecastEnabled = true,
            };
            using var popup = new UsagePopupForm(settings)
            {
                Location = new Point(-10_000, -10_000),
            };
            var refreshRequested = 0;
            var settingsRequested = 0;
            popup.RefreshRequested += (_, _) => refreshRequested++;
            popup.SettingsRequested += (_, _) => settingsRequested++;
            popup.SetStates(
                new[] { connected, stale, disconnected, loading },
                isRefreshing: true,
                lastRefreshed: now,
                history);
            popup.SetMode(DashboardMode.Full);
            popup.Show();
            Application.DoEvents();
            DrawControl(popup);

            var readingButton = Descendants(popup)
                .OfType<Button>()
                .Single(button => button.Text == "Reading");
            NotNull(readingButton.Image);

            foreach (var button in Descendants(popup).OfType<Button>())
            {
                if (button.Text is "Refresh" or "Reading")
                {
                    InvokeProtected(button, "OnClick", EventArgs.Empty);
                }
                else if (button.Text == "Settings")
                {
                    InvokeProtected(button, "OnClick", EventArgs.Empty);
                }
            }

            if (refreshRequested != 1 || settingsRequested != 1)
            {
                throw new InvalidOperationException(
                    $"Expected one refresh and settings request, got {refreshRequested} and {settingsRequested}.");
            }
            popup.SetMode(DashboardMode.Compact);
            popup.SetStates(Array.Empty<ProviderStatus>(), false, null, Array.Empty<UsageSample>());
            Null(readingButton.Image);
            Application.DoEvents();
            DrawControl(popup);
            popup.CloseForExit();
        }
        finally
        {
            Theme.Changed -= handler;
            Theme.Apply(ThemeMode.Dark, 72, 90);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The full dashboard always paints a fixed 2x2 grid sized to its client area, so a
    /// scrollbar can never reveal anything. It used to appear anyway: the freshly built cards
    /// are laid out at their natural height before the grid compacts them, and that transient
    /// overflow latched a scrollbar which then stole width from every card.
    /// </summary>
    /// <summary>
    /// The provider list reorders by dragging an entry as well as by the buttons. The gesture is
    /// driven with real mouse messages rather than by invoking the handlers, because the bug it
    /// guards against lives in what the native list box does on button-up: CheckOnClick ticks the
    /// row that is selected by then, which after a drag is the entry that was just moved.
    /// </summary>
    public static Task TestProviderDragReorderAsync()
    {
        var settings = new AppSettings { HiddenProviders = new[] { "copilot" } };
        using var dialog = new SettingsForm(
            settings,
            new[]
            {
                ("codex", "Codex"),
                ("claude", "Claude Code"),
                ("copilot", "GitHub Copilot"),
                ("gemini", "Google Gemini"),
            })
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-10_000, -10_000),
        };
        dialog.Show();
        Application.DoEvents();

        var list = GetPrivateField<CheckedListBox>(dialog, "_providers");
        Equal(4, list.Items.Count);
        var original = ProviderNames(list);
        Equal("Codex", original[0]);
        True(list.GetItemChecked(0));
        False(list.GetItemChecked(2));

        // Press the first row and move onto the third: the entry follows the pointer, so the row
        // the user is looking at is the row that gets dropped.
        DragProvider(dialog, list, 0, 2);
        var reordered = ProviderNames(list);
        Equal("Claude Code", reordered[0]);
        Equal("GitHub Copilot", reordered[1]);
        Equal("Codex", reordered[2]);
        Equal(2, list.SelectedIndex);

        // The whole point of the gesture: the ticks belong to the providers, and a reorder is not
        // a click, so a full press-move-release must leave every one of them exactly as it was.
        True(list.GetItemChecked(2));
        False(list.GetItemChecked(1));
        Equal(3, list.CheckedItems.Count);

        // The drag is over, so a stray move must not keep dragging.
        MoveOver(dialog, list, 0);
        True(reordered.SequenceEqual(ProviderNames(list)));

        // A press that never passes the drag threshold stays a click, and a click still ticks.
        var settled = ProviderNames(list);
        var ticked = list.GetItemChecked(0);
        ClickProvider(list, 0);
        Equal(-1, GetPrivateFieldValue<int>(dialog, "_dragIndex"));
        True(settled.SequenceEqual(ProviderNames(list)));
        Equal(!ticked, list.GetItemChecked(0));
        ClickProvider(list, 0);
        Equal(ticked, list.GetItemChecked(0));

        // Escape abandons a drag in flight. It has to be claimed by ProcessCmdKey: the form sets
        // CancelButton, so a list box never sees the key, and letting it through would close
        // Settings and discard every edit in it.
        Press(list, 0);
        MoveOver(dialog, list, 3);
        Equal("Claude Code", list.Items[3].ToString());
        True(InvokeProcessCmdKey(dialog, Keys.Escape));
        True(settled.SequenceEqual(ProviderNames(list)));
        Equal(-1, GetPrivateFieldValue<int>(dialog, "_dragIndex"));
        Release(list, 0);

        // With no drag running Escape is not ours, and must fall through to the Cancel button.
        False(InvokeProcessCmdKey(dialog, Keys.Escape));

        // A drag that lost the mouse without a button-up must not still be armed: the next press
        // has to start from scratch rather than resume the stale entry.
        SetPrivateField(dialog, "_dragIndex", 1);
        Press(list, 0);
        Equal(-1, GetPrivateFieldValue<int>(dialog, "_dragIndex"));
        Equal(0, GetPrivateFieldValue<int>(dialog, "_dragCandidate"));
        Release(list, 0);

        // Only the left button reorders, so a right-click never grabs a row.
        var centre = ProviderCentre(list, 0);
        InvokePrivate(
            dialog,
            "OnProvidersMouseDown",
            null,
            new MouseEventArgs(MouseButtons.Right, 1, centre.X, centre.Y, 0));
        Equal(-1, GetPrivateFieldValue<int>(dialog, "_dragCandidate"));

        // Neither does pressing the empty strip below the last row.
        InvokePrivate(
            dialog,
            "OnProvidersMouseDown",
            null,
            new MouseEventArgs(MouseButtons.Left, 1, centre.X, list.Height - 2, 0));
        Equal(-1, GetPrivateFieldValue<int>(dialog, "_dragCandidate"));

        // Straying sideways still means the row the cursor is level with. IndexFromPoint rejects
        // anything outside the client rectangle, so without clamping X the entry would jump to
        // the end of the list the moment the pointer left the narrow column.
        var row = ProviderCentre(list, 1);
        Equal(1, InvokePrivate<int>(dialog, "ProviderIndexFromPoint", new Point(-50, row.Y)));
        Equal(1, InvokePrivate<int>(dialog, "ProviderIndexFromPoint", new Point(list.Width + 50, row.Y)));

        // Only a genuine miss above or below the rows falls back to an end.
        Equal(3, InvokePrivate<int>(dialog, "ProviderIndexFromPoint", new Point(4, list.Height * 4)));
        Equal(0, InvokePrivate<int>(dialog, "ProviderIndexFromPoint", new Point(4, -20)));

        // The ends hold: nothing climbs past the top or falls past the bottom, and an entry
        // dropped back where it started is left alone rather than removed and reinserted.
        var ends = ProviderNames(list);
        var last = list.Items.Count - 1;
        InvokePrivate(dialog, "MoveProvider", 0, -1);
        InvokePrivate(dialog, "MoveProvider", last, last + 1);
        InvokePrivate(dialog, "MoveProvider", 1, 1);
        True(ends.SequenceEqual(ProviderNames(list)));

        // With nothing selected there is nothing for the buttons or the shortcut to move.
        list.ClearSelected();
        InvokePrivate(dialog, "MoveSelected", -1);
        True(ends.SequenceEqual(ProviderNames(list)));

        // Alt+Down is the keyboard equal of the drag, and must not fire from anywhere else.
        list.SelectedIndex = 0;
        var head = list.Items[0].ToString();
        dialog.Activate();
        list.Focus();
        Application.DoEvents();
        if (list.Focused)
        {
            True(InvokeProcessCmdKey(dialog, Keys.Alt | Keys.Down));
            Equal(head, list.Items[1].ToString());
            Equal(1, list.SelectedIndex);
            True(InvokeProcessCmdKey(dialog, Keys.Alt | Keys.Up));
            Equal(head, list.Items[0].ToString());
        }

        False(InvokeProcessCmdKey(dialog, Keys.Alt | Keys.Right));

        // Whatever the list shows at the end is what gets saved, ticks included.
        var saved = list.Items.Cast<object>()
            .Select(item => GetProperty<string>(item, "Id"))
            .ToArray();
        InvokePrivate(dialog, "Apply");
        True(saved.SequenceEqual(settings.ProviderOrder));
        False(settings.IsProviderVisible("copilot"));
        True(settings.IsProviderVisible("codex"));

        dialog.Hide();
        return Task.CompletedTask;
    }

    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int MkLButton = 0x0001;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    private static string?[] ProviderNames(CheckedListBox list) =>
        list.Items.Cast<object>().Select(item => item.ToString()).ToArray();

    private static Point ProviderCentre(CheckedListBox list, int index)
    {
        var bounds = list.GetItemRectangle(index);
        return new Point(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height / 2));
    }

    /// <summary>
    /// Posts a real mouse message to the list so the native control does its own work - taking
    /// capture, moving the selection, and ticking on button-up - alongside the form's handlers.
    /// </summary>
    private static void SendMouse(CheckedListBox list, int message, int buttons, Point point)
    {
        var lParam = (IntPtr)((point.Y << 16) | (point.X & 0xFFFF));
        SendMessageW(list.Handle, message, (IntPtr)buttons, lParam);
        Application.DoEvents();
    }

    private static void Press(CheckedListBox list, int index) =>
        SendMouse(list, WmLButtonDown, MkLButton, ProviderCentre(list, index));

    /// <summary>
    /// The move is the one step that cannot be a real message: WinForms builds the MouseMove
    /// arguments from the physical mouse via <see cref="Control.MouseButtons"/> rather than from
    /// the message's own wParam, so a synthetic WM_MOUSEMOVE always reports no button held. The
    /// press and the release stay real, which is what matters - the tick this guards against is
    /// applied by the native list on button-up.
    /// </summary>
    private static void MoveOver(SettingsForm dialog, CheckedListBox list, int index)
    {
        var centre = ProviderCentre(list, index);
        InvokePrivate(
            dialog,
            "OnProvidersMouseMove",
            null,
            new MouseEventArgs(MouseButtons.Left, 0, centre.X, centre.Y, 0));
    }

    private static void Release(CheckedListBox list, int index) =>
        SendMouse(list, WmLButtonUp, 0, ProviderCentre(list, index));

    private static void DragProvider(SettingsForm dialog, CheckedListBox list, int from, int to)
    {
        Press(list, from);
        MoveOver(dialog, list, to);
        Release(list, to);
    }

    /// <summary>A press and release on one row, with no move in between.</summary>
    private static void ClickProvider(CheckedListBox list, int index)
    {
        Press(list, index);
        Release(list, index);
    }

    private static bool InvokeProcessCmdKey(SettingsForm dialog, Keys keyData)
    {
        var method = typeof(SettingsForm).GetMethod(
            "ProcessCmdKey",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing ProcessCmdKey.");
        var arguments = new object?[] { default(Message), keyData };
        return (bool)method.Invoke(dialog, arguments)!;
    }

    public static Task TestDashboardNeverScrollsAsync()
    {
        var now = DateTimeOffset.Now;
        var providers = new[]
        {
            ("codex", "Codex"),
            ("claude", "Claude Code"),
            ("copilot", "GitHub Copilot"),
            ("gemini", "Google Gemini"),
        };
        var states = providers
            .Select((provider, index) => new ProviderStatus(
                provider.Item1,
                provider.Item2,
                Snapshot(provider.Item1, provider.Item2, 20 + index * 10, now),
                null,
                false,
                now))
            .ToArray();

        using var popup = new UsagePopupForm(new AppSettings(), 96)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-10_000, -10_000),
        };
        popup.SetStates(states, false, now, Array.Empty<UsageSample>());
        popup.SetMode(DashboardMode.Full);
        popup.Show();
        Application.DoEvents();

        var content = GetPrivateField<FlowLayoutPanel>(popup, "_content");
        False(content.AutoScroll);

        // The smallest sizes are where the cards overflow their natural height by the most,
        // which is exactly where the old layout gave up and scrolled instead of compacting.
        foreach (var size in new[] { new Size(1_020, 820), new Size(760, 620), new Size(600, 420) })
        {
            popup.ClientSize = size;
            popup.PerformLayout();
            Application.DoEvents();

            False(content.VerticalScroll.Visible);
            False(content.HorizontalScroll.Visible);
            var cards = content.Controls.OfType<ProviderUsageCard>().ToArray();
            Equal(4, cards.Length);
            True(cards.All(card => card.Bottom <= content.ClientSize.Height));

            // No scrollbar means no dead strip: the right column reaches the client edge,
            // give or take the integer division that splits the row into two equal cards.
            True(cards.Max(card => card.Right) >= content.ClientSize.Width - 2);
        }

        popup.SetMode(DashboardMode.Compact);
        Application.DoEvents();
        True(content.AutoScroll);

        popup.CloseForExit();

        // Four providers are the 2x2 the dashboard is designed around, but with no scrollbar a
        // fifth must earn a third row: laid out past the bottom edge it would be unreachable
        // rather than merely awkward, which is what the scrollbar used to paper over.
        foreach (var count in new[] { 5, 6 })
        {
            var extra = Enumerable.Range(0, count)
                .Select(index => new ProviderStatus(
                    $"p{index}",
                    $"Provider {index}",
                    Snapshot($"p{index}", $"Provider {index}", 20 + index, now),
                    null,
                    false,
                    now))
                .ToArray();

            using var wide = new UsagePopupForm(new AppSettings(), 96)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-10_000, -10_000),
            };
            wide.SetStates(extra, false, now, Array.Empty<UsageSample>());
            wide.SetMode(DashboardMode.Full);
            wide.Show();
            wide.ClientSize = new Size(900, 700);
            wide.PerformLayout();
            Application.DoEvents();

            var grid = GetPrivateField<FlowLayoutPanel>(wide, "_content");
            var extraCards = grid.Controls.OfType<ProviderUsageCard>().ToArray();
            Equal(count, extraCards.Length);
            False(grid.VerticalScroll.Visible);
            True(extraCards.All(card => card.Bottom <= grid.ClientSize.Height));
            True(extraCards.All(card => card.Right <= grid.ClientSize.Width));

            // Still two columns, just more rows, and every card the same size as its neighbours.
            Equal(2, extraCards.Select(card => card.Left).Distinct().Count());
            Equal(3, extraCards.Select(card => card.Top).Distinct().Count());
            Equal(1, extraCards.Select(card => card.Size).Distinct().Count());
            wide.CloseForExit();
        }

        return Task.CompletedTask;
    }

    public static Task TestDashboardFixedGridAsync()
    {
        var now = DateTimeOffset.Now;
        var providers = new[]
        {
            ("codex", "Codex"),
            ("claude", "Claude Code"),
            ("copilot", "GitHub Copilot"),
            ("gemini", "Google Gemini"),
        };
        var states = providers
            .Select((provider, index) => new ProviderStatus(
                provider.Item1,
                provider.Item2,
                Snapshot(provider.Item1, provider.Item2, 20 + index * 10, now),
                null,
                false,
                now))
            .ToArray();

        using var popup = new UsagePopupForm(new AppSettings(), 96)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-10_000, -10_000),
        };
        popup.SetStates(states, false, now, Array.Empty<UsageSample>());
        popup.SetMode(DashboardMode.Full);
        popup.Show();
        Application.DoEvents();

        var content = GetPrivateField<FlowLayoutPanel>(popup, "_content");
        popup.ClientSize = new Size(760, 620);
        popup.PerformLayout();
        Application.DoEvents();
        var initialCards = content.Controls.OfType<ProviderUsageCard>().ToArray();
        Equal(4, initialCards.Length);
        Equal(2, initialCards.Select(card => card.Left).Distinct().Count());
        Equal(2, initialCards.Select(card => card.Top).Distinct().Count());
        Equal(1, initialCards.Select(card => card.Width).Distinct().Count());
        Equal(1, initialCards.Select(card => card.Height).Distinct().Count());
        var initialSize = initialCards[0].Size;

        popup.ClientSize = new Size(1_020, 820);
        popup.PerformLayout();
        Application.DoEvents();
        var resizedCards = content.Controls.OfType<ProviderUsageCard>().ToArray();
        Equal(2, resizedCards.Select(card => card.Left).Distinct().Count());
        Equal(2, resizedCards.Select(card => card.Top).Distinct().Count());
        Equal(1, resizedCards.Select(card => card.Width).Distinct().Count());
        Equal(1, resizedCards.Select(card => card.Height).Distinct().Count());
        True(resizedCards[0].Width > initialSize.Width);
        True(resizedCards[0].Height > initialSize.Height);
        var resizedSize = resizedCards[0].Size;

        popup.ClientSize = new Size(600, 420);
        popup.PerformLayout();
        Application.DoEvents();
        var compactedCards = content.Controls.OfType<ProviderUsageCard>().ToArray();
        Equal(2, compactedCards.Select(card => card.Left).Distinct().Count());
        Equal(2, compactedCards.Select(card => card.Top).Distinct().Count());
        Equal(1, compactedCards.Select(card => card.Width).Distinct().Count());
        Equal(1, compactedCards.Select(card => card.Height).Distinct().Count());
        True(compactedCards.All(card => card.Right <= content.ClientSize.Width));
        True(compactedCards.All(card => card.Bottom <= content.ClientSize.Height));
        True(compactedCards[0].Width < resizedSize.Width);
        True(compactedCards[0].Height < resizedSize.Height);
        DrawControl(popup);
        popup.CloseForExit();

        return Task.CompletedTask;
    }

    public static async Task TestApplicationContextAsync()
    {
        var previousSynchronizationContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
        try
        {
        UsageHistoryStore.Clear();
        SnapshotCache.Clear();
        var client = new QueueUsageClient("context", "Context Provider");
        client.Enqueue(Snapshot("context", "Context Provider", 55, DateTimeOffset.Now));
        client.Enqueue(new InvalidOperationException("Synthetic internal detail must not leak."));
        client.Enqueue(Snapshot("context", "Context Provider", 58, DateTimeOffset.Now));
        client.Enqueue(Snapshot("context", "Context Provider", 61, DateTimeOffset.Now));
        client.Enqueue(Snapshot("context", "Context Provider", 64, DateTimeOffset.Now));
        client.Enqueue(Snapshot("context", "Context Provider", 67, DateTimeOffset.Now));
        var settings = new AppSettings
        {
            GlobalHotkeyEnabled = false,
            HistoryEnabled = false,
            NotificationsEnabled = false,
            SlowRefreshWhenHidden = false,
        };

        using var context = new UsageApplicationContext(new[] { client }, settings, showTrayIcon: false);
        var popup = GetPrivateField<UsagePopupForm>(context, "_popup");
        popup.Location = new Point(-10_000, -10_000);

        await InvokePrivateTaskAsync(context, "RefreshAsync", true);
        Application.DoEvents();
        Equal(1, client.CallCount);
        var service = GetPrivateField<UsageRefreshService>(context, "_service");
        Equal(55, service.Statuses[0].Snapshot!.Primary!.UsedPercent);
        InvokePrivate(context, "UpdateTray");
        var tray = GetPrivateField<NotifyIcon>(context, "_trayIcon");
        True(tray.Text.Contains("55% used", StringComparison.Ordinal));

        await InvokePrivateTaskAsync(context, "RefreshAsync", true);
        Application.DoEvents();
        Equal(2, client.CallCount);
        True(service.Statuses[0].IsStale);
        Equal("Context Provider usage is temporarily unavailable.", service.Statuses[0].Error);

        settings.SetProviderVisible("context", false);
        service.ApplySettings();
        InvokePrivate(context, "PushStateToPopup");
        InvokePrivate(context, "UpdateTray");
        True(tray.Text.Contains("no connected providers", StringComparison.Ordinal));

        settings.SetProviderVisible("context", true);
        service.ApplySettings();
        InvokePrivate(context, "RefreshOnFirstIdle", context, EventArgs.Empty);
        await WaitForConditionAsync(() => client.CallCount >= 3);
        Equal(58, service.Statuses[0].Snapshot!.Primary!.UsedPercent);

        popup.Hide();
        InvokePrivate(context, "ToggleCompactPopup");
        Application.DoEvents();
        True(popup.Visible);
        Equal(DashboardMode.Compact, popup.Mode);
        InvokePrivate(context, "ToggleCompactPopup");
        False(popup.Visible);

        InvokePrivate(
            context,
            "TrayIconOnMouseUp",
            null,
            new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
        Application.DoEvents();
        True(popup.Visible);
        popup.Hide();
        InvokePrivate(context, "OpenDashboard");
        Application.DoEvents();
        True(popup.Visible);
        Equal(DashboardMode.Full, popup.Mode);
        popup.Hide();

        settings.NotificationsEnabled = true;
        InvokePrivate(
            context,
            "OnAlertsRaised",
            context,
            new UsageAlertEventArgs(new[]
            {
                new UsageAlert(
                    "context",
                    new string('T', 80),
                    new string('M', 280),
                    AlertLevel.Critical),
                new UsageAlert("context", "Secondary", "Another threshold.", AlertLevel.Warning),
            }));
        Application.DoEvents();

        InvokePrivate(
            context,
            "OnPowerModeChanged",
            context,
            new Microsoft.Win32.PowerModeChangedEventArgs(Microsoft.Win32.PowerModes.Resume));
        await WaitForConditionAsync(() => client.CallCount >= 4);
        Equal(61, service.Statuses[0].Snapshot!.Primary!.UsedPercent);
        InvokePrivate(
            context,
            "OnSessionSwitch",
            context,
            new Microsoft.Win32.SessionSwitchEventArgs(
                Microsoft.Win32.SessionSwitchReason.SessionUnlock));
        await WaitForConditionAsync(() => client.CallCount >= 5);
        Equal(64, service.Statuses[0].Snapshot!.Primary!.UsedPercent);
        SetPrivateField(service, "_nextRegularRefresh", DateTimeOffset.MinValue);
        await InvokePrivateTaskAsync(context, "RefreshIfDueAsync");
        Equal(6, client.CallCount);
        Equal(67, service.Statuses[0].Snapshot!.Primary!.UsedPercent);

        settings.Theme = ThemeMode.System;
        InvokePrivate(
            context,
            "OnUserPreferenceChanged",
            context,
            new Microsoft.Win32.UserPreferenceChangedEventArgs(
                Microsoft.Win32.UserPreferenceCategory.Color));
        Application.DoEvents();

        settings.GlobalHotkeyEnabled = true;
        InvokePrivate(context, "ApplyHotkeySetting");
        settings.GlobalHotkeyEnabled = false;
        InvokePrivate(context, "ApplyHotkeySetting");

        var colorTableType = typeof(UsageApplicationContext).GetNestedType(
            "DarkColorTable",
            BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("DarkColorTable was not found.");
        var colorTable = Activator.CreateInstance(colorTableType)
            ?? throw new InvalidOperationException("DarkColorTable could not be created.");
        foreach (var property in colorTableType.GetProperties(
                     BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.DeclaringType == colorTableType)
            {
                NotNull(property.GetValue(colorTable));
            }
        }

        // Exercise guarded event paths without changing machine settings.
        InvokePrivate(
            context,
            "TrayIconOnMouseUp",
            null,
            new MouseEventArgs(MouseButtons.Right, 1, 0, 0, 0));
        InvokePrivate(
            context,
            "OnPowerModeChanged",
            context,
            new Microsoft.Win32.PowerModeChangedEventArgs(Microsoft.Win32.PowerModes.StatusChange));
        InvokePrivate(
            context,
            "OnSessionSwitch",
            context,
            new Microsoft.Win32.SessionSwitchEventArgs(Microsoft.Win32.SessionSwitchReason.SessionLock));
        InvokePrivate(
            context,
            "OnUserPreferenceChanged",
            context,
            new Microsoft.Win32.UserPreferenceChangedEventArgs(
                Microsoft.Win32.UserPreferenceCategory.Keyboard));
        InvokePrivate(
            context,
            "OnAlertsRaised",
            context,
            new UsageAlertEventArgs(Array.Empty<UsageAlert>()));

        var ranOnUi = false;
        InvokePrivate(context, "RunOnUi", (Action)(() => ranOnUi = true));
        Application.DoEvents();
        True(ranOnUi);

        var postedToUi = false;
        await Task.Run(() =>
            InvokePrivate(context, "RunOnUi", (Action)(() => postedToUi = true)));
        await WaitForConditionAsync(() => postedToUi);

        SetPrivateField(context, "_isExiting", true);
        await InvokePrivateTaskAsync(context, "RefreshAsync", true);
        await InvokePrivateTaskAsync(context, "RefreshIfDueAsync");
        InvokePrivate(context, "ToggleCompactPopup");
        InvokePrivate(context, "OpenDashboard");
        InvokePrivate(context, "OnServiceUpdated", context, EventArgs.Empty);
        Equal(6, client.CallCount);
        SnapshotCache.Clear();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousSynchronizationContext);
        }
    }

    public static async Task TestPreviewAndEntryPointsAsync()
    {
        var directory = Path.Combine(AppPaths.DataDirectory, $"entry-points-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var compactPath = Path.Combine(directory, "compact.png");
            var compact96Path = Path.Combine(directory, "compact-96.png");
            var fullPath = Path.Combine(directory, "full.png");
            PreviewRenderer.Render(new[] { "--render-preview", compactPath });
            PreviewRenderer.Render(new[] { "--render-preview", compact96Path, "--dpi", "96" });
            PreviewRenderer.Render(new[] { "--render-preview", fullPath, "--full" });
            True(new FileInfo(compactPath).Length > 1_000);
            True(new FileInfo(compact96Path).Length > 1_000);
            True(new FileInfo(fullPath).Length > 1_000);
            using (var compact = Image.FromFile(compactPath))
            using (var compact96 = Image.FromFile(compact96Path))
            {
                Equal(compact96.Width * 2, compact.Width);
                Equal(compact96.Height * 2, compact.Height);
            }

            var applicationAssembly = typeof(ProviderStatus).Assembly.Location;
            var applicationExecutable = Path.ChangeExtension(applicationAssembly, ".exe");
            True(File.Exists(applicationExecutable));

            var help = await RunProcessAsync(applicationExecutable, "--help");
            Equal(0, help.ExitCode);
            True(help.StandardOutput.Contains("UsageAI", StringComparison.Ordinal));

            var version = await RunProcessAsync(applicationExecutable, "--version");
            Equal(0, version.ExitCode);
            True(version.StandardOutput.Trim().Length > 0);

            var diagnostic = await RunProcessAsync(
                applicationExecutable,
                "--diagnose",
                "unknown-provider");
            Equal(1, diagnostic.ExitCode);
            True(diagnostic.StandardError.Contains("Unknown usage provider", StringComparison.Ordinal));

            var processPreviewPath = Path.Combine(directory, "process-preview.png");
            var preview = await RunProcessAsync(
                applicationExecutable,
                "--render-preview",
                processPreviewPath,
                "--full");
            Equal(0, preview.ExitCode);
            True(new FileInfo(processPreviewPath).Length > 1_000);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public static async Task TestProviderCredentialBranchesAsync()
    {
        var environment = SaveEnvironment(
            "USAGEAI_CLAUDE_OAUTH_TOKEN",
            "USAGEAI_CLAUDE_OAUTH_SCOPES",
            "USAGEAI_CLAUDE_SESSION_KEY",
            "CLAUDE_AI_SESSION_KEY",
            "CLAUDE_WEB_SESSION_KEY",
            "CLAUDE_CONFIG_DIR",
            "USAGEAI_ENABLE_GH_TOKEN_FALLBACK");
        var directory = Path.Combine(AppPaths.DataDirectory, $"provider-credentials-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            Environment.SetEnvironmentVariable("USAGEAI_CLAUDE_OAUTH_TOKEN", null);
            Environment.SetEnvironmentVariable("USAGEAI_CLAUDE_OAUTH_SCOPES", null);
            Environment.SetEnvironmentVariable("USAGEAI_CLAUDE_SESSION_KEY", null);
            Environment.SetEnvironmentVariable("CLAUDE_AI_SESSION_KEY", null);
            Environment.SetEnvironmentVariable("CLAUDE_WEB_SESSION_KEY", null);
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", directory);

            File.WriteAllText(
                Path.Combine(directory, ".credentials.json"),
                $$"""
                {
                  "claudeAiOauth":{
                    "accessToken":"file-access",
                    "refreshToken":"file-refresh",
                    "expiresAt":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()}},
                    "scopes":["user:profile"],
                    "subscriptionType":"team"
                  }
                }
                """);
            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       Task.FromResult(JsonResponse(
                           HttpStatusCode.OK,
                           """{"five_hour":{"utilization":21}}""")))))
            {
                var client = new ClaudeCodeUsageClient(http);
                Equal("Claude Code", client.DisplayName);
                Equal("claude", client.SignInCommand);
                var snapshot = await client.GetUsageAsync();
                Equal("Claude Team", snapshot.Plan);
                Equal(21, snapshot.Primary!.UsedPercent);
            }

            var flatCredentials = InvokePrivateStatic<ClaudeCodeUsageClient.ClaudeCredentials>(
                typeof(ClaudeCodeUsageClient),
                "ParseCredentials",
                $$"""
                {
                  "accessToken":"flat-access",
                  "refreshToken":"flat-refresh",
                  "expiresAt":{{long.MaxValue}},
                  "scopes":[" user:profile ",null,17],
                  "rateLimitTier":"enterprise"
                }
                """);
            Equal("flat-access", flatCredentials.AccessToken);
            Equal(DateTimeOffset.MinValue, flatCredentials.ExpiresAt);
            Equal("Claude Enterprise", flatCredentials.Plan);
            Equal(1, flatCredentials.Scopes.Count);
            Throws<ClaudeCodeUsageException>(() =>
                InvokePrivateStatic<ClaudeCodeUsageClient.ClaudeCredentials>(
                    typeof(ClaudeCodeUsageClient),
                    "ParseCredentials",
                    """{"refreshToken":"only-refresh"}"""));

            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", "relative-config");
            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       throw new InvalidOperationException("HTTP should not be reached."))))
            {
                var exception = await ThrowsAsync<ClaudeCodeUsageException>(
                    () => new ClaudeCodeUsageClient(http).GetUsageAsync());
                True(exception.Message.Contains("absolute path", StringComparison.OrdinalIgnoreCase));
            }

            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", directory);
            File.WriteAllText(Path.Combine(directory, ".credentials.json"), "{invalid");
            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       throw new InvalidOperationException("HTTP should not be reached."))))
            {
                var exception = await ThrowsAsync<ClaudeCodeUsageException>(
                    () => new ClaudeCodeUsageClient(
                        http,
                        () => Array.Empty<string>()).GetUsageAsync());
                True(exception.Message.Contains("could not be read", StringComparison.OrdinalIgnoreCase));
            }

            File.Delete(Path.Combine(directory, ".credentials.json"));
            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       throw new InvalidOperationException("HTTP should not be reached."))))
            {
                var exception = await ThrowsAsync<ClaudeCodeUsageException>(
                    () => new ClaudeCodeUsageClient(
                        http,
                        () => Array.Empty<string>()).GetUsageAsync());
                True(exception.Message.Contains("not signed in", StringComparison.OrdinalIgnoreCase));
            }

            var keyringCredential =
                $$"""
                {
                  "accessToken":"keyring-access",
                  "expiresAt":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()}},
                  "scopes":["user:profile"],
                  "subscriptionType":"pro"
                }
                """;
            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       Task.FromResult(JsonResponse(
                           HttpStatusCode.OK,
                           """{"five_hour":{"utilization":14}}""")))))
            {
                var snapshot = await new ClaudeCodeUsageClient(
                    http,
                    () => new[] { "{invalid", keyringCredential }).GetUsageAsync();
                Equal("Claude Pro", snapshot.Plan);
                Equal(14, snapshot.Primary!.UsedPercent);
            }

            Equal("Claude Free", ClaudeCodeUsageClient.FormatPlan("free"));
            Equal("Claude Max", ClaudeCodeUsageClient.FormatPlan("max"));
            Equal("Claude Max 5x", ClaudeCodeUsageClient.FormatPlan("claude_max_5x_plan"));
            Equal("Claude Max 20x", ClaudeCodeUsageClient.FormatPlan("claude_max_20x_plan"));

            var tokenFile = Path.Combine(directory, "copilot.json");
            File.WriteAllText(
                tokenFile,
                """
                {
                  "oauth_token":"token-one",
                  "nested":{"copilotTokens":{"first":"token-two","ignored":17}}
                }
                """);
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            InvokePrivateStatic<object?>(
                typeof(GitHubCopilotUsageClient),
                "AddTokensFromFile",
                tokens,
                tokenFile);
            Equal(2, tokens.Count);
            True(tokens.Contains("token-one"));
            True(tokens.Contains("token-two"));

            File.WriteAllText(tokenFile, "{invalid");
            InvokePrivateStatic<object?>(
                typeof(GitHubCopilotUsageClient),
                "AddTokensFromFile",
                tokens,
                tokenFile);
            InvokePrivateStatic<object?>(
                typeof(GitHubCopilotUsageClient),
                "AddTokensFromFile",
                tokens,
                Path.Combine(directory, "missing.json"));

            var copilot = new GitHubCopilotUsageClient(new HttpClient(new StubHttpHandler((_, _, _) =>
                throw new InvalidOperationException("HTTP is not used by this test."))));
            Equal("GitHub Copilot", copilot.DisplayName);
            Equal("copilot", copilot.SignInCommand);
            SetPrivateField<string?>(copilot, "_workingToken", "token-two");
            var prioritized = InvokePrivate<IReadOnlyList<string>>(
                copilot,
                "PrioritizeTokens",
                (object)CopilotTokenPair);
            Equal("token-two", prioritized[0]);
            InvokePrivate(copilot, "RememberRejectedToken", "token-two");
            Null(GetPrivateFieldValue<string?>(copilot, "_workingToken"));

            var rejected = GetPrivateField<HashSet<string>>(copilot, "_rejectedTokens");
            rejected.Add("token-one");
            rejected.Add("token-two");
            var revived = InvokePrivate<IReadOnlyList<string>>(
                copilot,
                "PrioritizeTokens",
                (object)CopilotTokenPair);
            Equal(2, revived.Count);
            Equal(0, rejected.Count);

            Environment.SetEnvironmentVariable("USAGEAI_ENABLE_GH_TOKEN_FALLBACK", "yes");
            True(InvokePrivateStatic<bool>(
                typeof(GitHubCopilotUsageClient),
                "IsGitHubCliFallbackEnabled"));
            Environment.SetEnvironmentVariable("USAGEAI_ENABLE_GH_TOKEN_FALLBACK", "no");
            False(InvokePrivateStatic<bool>(
                typeof(GitHubCopilotUsageClient),
                "IsGitHubCliFallbackEnabled"));

            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       throw new InvalidOperationException("HTTP should not be reached."))))
            {
                var exception = await ThrowsAsync<GitHubCopilotUsageException>(
                    () => new GitHubCopilotUsageClient(
                        http,
                        _ => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()))
                        .GetUsageAsync());
                True(exception.Message.Contains("not signed in", StringComparison.OrdinalIgnoreCase));
            }

            var deterministicHandler = new StubHttpHandler((_, call, _) =>
                Task.FromResult(call == 1
                    ? JsonResponse(
                        HttpStatusCode.OK,
                        """{"quota_snapshots":{"chat":{"percent_remaining":80}}}""")
                    : new HttpResponseMessage(HttpStatusCode.Unauthorized)));
            using (var http = new HttpClient(deterministicHandler))
            {
                var client = new GitHubCopilotUsageClient(
                    http,
                    _ => Task.FromResult<IReadOnlyList<string>>(CopilotTokenPair));
                _ = await client.GetUsageAsync();
                await ThrowsAsync<GitHubCopilotUsageException>(() => client.GetUsageAsync());
                await ThrowsAsync<GitHubCopilotUsageException>(() => client.GetUsageAsync());
                True(deterministicHandler.CallCount >= 5);
            }

            var candidates = InvokePrivateStatic<IEnumerable<string>>(
                    typeof(GitHubCopilotUsageClient),
                    "GetCredentialFileCandidates")
                .ToArray();
            Equal(4, candidates.Length);

            var cappedTokens = new HashSet<string>(
                Enumerable.Range(0, 32).Select(index => $"token-{index}"),
                StringComparer.Ordinal);
            InvokePrivateStatic<object?>(
                typeof(GitHubCopilotUsageClient),
                "AddToken",
                cappedTokens,
                "one-too-many");
            Equal(32, cappedTokens.Count);
            var normalTokens = new HashSet<string>(StringComparer.Ordinal);
            InvokePrivateStatic<object?>(
                typeof(GitHubCopilotUsageClient),
                "AddToken",
                normalTokens,
                " invalid token ");
            Equal(0, normalTokens.Count);

            var ghDirectory = Path.Combine(directory, "fake-gh");
            Directory.CreateDirectory(ghDirectory);
            var fixtureDirectory = Path.GetDirectoryName(
                Environment.ProcessPath
                ?? throw new InvalidOperationException("The test executable is unavailable."))!;
            foreach (var fixtureName in new[]
                     {
                         "UsageAI.Tests.exe",
                         "UsageAI.Tests.dll",
                         "UsageAI.Tests.deps.json",
                         "UsageAI.Tests.runtimeconfig.json",
                         "UsageAI.dll",
                     })
            {
                var source = Path.Combine(fixtureDirectory, fixtureName);
                if (File.Exists(source))
                {
                    File.Copy(
                        source,
                        Path.Combine(
                            ghDirectory,
                            fixtureName == "UsageAI.Tests.exe" ? "gh.exe" : fixtureName));
                }
            }

            var previousPath = Environment.GetEnvironmentVariable("PATH");
            var previousGhConfig = Environment.GetEnvironmentVariable("GH_CONFIG_DIR");
            try
            {
                Environment.SetEnvironmentVariable("PATH", ghDirectory);
                Environment.SetEnvironmentVariable("GH_CONFIG_DIR", "synthetic-gh-token");
                Equal(
                    "synthetic-gh-token",
                    await InvokePrivateStaticTaskResultAsync<string?>(
                        typeof(GitHubCopilotUsageClient),
                        "TryReadGitHubCliTokenAsync",
                        CancellationToken.None));

                Environment.SetEnvironmentVariable("GH_CONFIG_DIR", "usageai-test-gh-failure");
                Null(await InvokePrivateStaticTaskResultAsync<string?>(
                    typeof(GitHubCopilotUsageClient),
                    "TryReadGitHubCliTokenAsync",
                    CancellationToken.None));

                Environment.SetEnvironmentVariable("PATH", directory);
                Null(await InvokePrivateStaticTaskResultAsync<string?>(
                    typeof(GitHubCopilotUsageClient),
                    "TryReadGitHubCliTokenAsync",
                    CancellationToken.None));
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", previousPath);
                Environment.SetEnvironmentVariable("GH_CONFIG_DIR", previousGhConfig);
            }
        }
        finally
        {
            RestoreEnvironment(environment);
            Directory.Delete(directory, recursive: true);
        }
    }

    public static async Task TestGeminiDeepBranchesAsync()
    {
        var environment = SaveEnvironment(
            "GEMINI_CONFIG_DIR",
            "GEMINI_CLIENT_ID",
            "GEMINI_CLIENT_SECRET");
        var directory = Path.Combine(AppPaths.DataDirectory, $"gemini-deep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            Environment.SetEnvironmentVariable("GEMINI_CONFIG_DIR", directory);
            Environment.SetEnvironmentVariable("GEMINI_CLIENT_ID", "test-client");
            Environment.SetEnvironmentVariable("GEMINI_CLIENT_SECRET", "test-secret");
            Equal("agy", new GeminiUsageClient().SignInCommand);

            var environmentCredentials = InvokePrivateStatic<object>(
                typeof(GeminiUsageClient),
                "ResolveOAuthClientCredentials");
            Equal("test-client", GetProperty<string>(environmentCredentials, "ClientId"));

            Environment.SetEnvironmentVariable("GEMINI_CLIENT_ID", null);
            Environment.SetEnvironmentVariable("GEMINI_CLIENT_SECRET", null);
            File.WriteAllText(
                Path.Combine(directory, "client_config.json"),
                """{"client_id":"config-client","client_secret":"config-secret"}""");
            var configCredentials = InvokePrivateStatic<object>(
                typeof(GeminiUsageClient),
                "ResolveOAuthClientCredentials");
            Equal("config-client", GetProperty<string>(configCredentials, "ClientId"));
            True(InvokePrivateStatic<IEnumerable<string>>(
                    typeof(GeminiUsageClient),
                    "GetJsClientSecretCandidates")
                .Any());
            Environment.SetEnvironmentVariable("GEMINI_CLIENT_ID", "test-client");
            Environment.SetEnvironmentVariable("GEMINI_CLIENT_SECRET", "test-secret");

            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       throw new InvalidOperationException("HTTP should not be reached."))))
            {
                var exception = await ThrowsAsync<GeminiUsageException>(
                    () => new GeminiUsageClient(http, NoLocalGeminiSnapshot).GetUsageAsync());
                True(exception.Message.Contains("not signed in", StringComparison.OrdinalIgnoreCase));
            }

            File.WriteAllText(Path.Combine(directory, "oauth_creds.json"), "{invalid");
            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       throw new InvalidOperationException("HTTP should not be reached."))))
            {
                var exception = await ThrowsAsync<GeminiUsageException>(
                    () => new GeminiUsageClient(http, NoLocalGeminiSnapshot).GetUsageAsync());
                True(exception.Message.Contains("not signed in", StringComparison.OrdinalIgnoreCase));
            }

            WriteGeminiCredentials(directory, "valid-access", null, DateTimeOffset.UtcNow.AddHours(1));
            foreach (var status in new[]
                     {
                         HttpStatusCode.Forbidden,
                         HttpStatusCode.InternalServerError,
                     })
            {
                using var http = new HttpClient(GeminiHandler(status));
                await ThrowsAsync<GeminiUsageException>(
                    () => new GeminiUsageClient(http, NoLocalGeminiSnapshot).GetUsageAsync());
            }

            using (var http = new HttpClient(new StubHttpHandler((request, _, _) =>
                   {
                       if (request.RequestUri!.AbsoluteUri.Contains("loadCodeAssist", StringComparison.Ordinal))
                       {
                           return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                       }

                       return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{invalid"));
                   })))
            {
                var exception = await ThrowsAsync<GeminiUsageException>(
                    () => new GeminiUsageClient(http, NoLocalGeminiSnapshot).GetUsageAsync());
                True(exception.InnerException is JsonException);
            }

            using (var http = new HttpClient(new StubHttpHandler((request, _, _) =>
                   {
                       if (request.RequestUri!.AbsoluteUri.Contains("loadCodeAssist", StringComparison.Ordinal))
                       {
                           return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                       }

                       throw new OperationCanceledException();
                   })))
            {
                var exception = await ThrowsAsync<GeminiUsageException>(
                    () => new GeminiUsageClient(http, NoLocalGeminiSnapshot).GetUsageAsync());
                True(exception.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase));
            }

            var expired = new GeminiUsageClient.GeminiCredentials(
                "expired",
                "refresh",
                null,
                DateTimeOffset.UtcNow.AddHours(-1),
                null);
            foreach (var responseFactory in new Func<HttpResponseMessage>[]
                     {
                         () => new HttpResponseMessage(HttpStatusCode.BadRequest),
                         () => JsonResponse(HttpStatusCode.OK, """{"expires_in":3600}"""),
                         () => JsonResponse(HttpStatusCode.OK, "{invalid"),
                     })
            {
                using var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                    Task.FromResult(responseFactory())));
                await ThrowsAsync<GeminiUsageException>(() =>
                    InvokePrivateTaskResultAsync<GeminiUsageClient.GeminiCredentials>(
                        new GeminiUsageClient(http, NoLocalGeminiSnapshot),
                        "RefreshCredentialsAsync",
                        expired,
                        CancellationToken.None));
            }

            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       throw new OperationCanceledException())))
            {
                var exception = await ThrowsAsync<GeminiUsageException>(() =>
                    InvokePrivateTaskResultAsync<GeminiUsageClient.GeminiCredentials>(
                        new GeminiUsageClient(http, NoLocalGeminiSnapshot),
                        "RefreshCredentialsAsync",
                        expired,
                        CancellationToken.None));
                True(exception.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase));
            }

            var sourcePath = Path.Combine(directory, "oauth_creds.json");
            var freshCredentials = new GeminiUsageClient.GeminiCredentials(
                "still-fresh",
                "refresh-value",
                null,
                DateTimeOffset.UtcNow.AddHours(1),
                sourcePath);
            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       throw new InvalidOperationException("HTTP should not be reached."))))
            {
                var client = new GeminiUsageClient(http, NoLocalGeminiSnapshot);
                Equal(
                    freshCredentials,
                    await InvokePrivateTaskResultAsync<GeminiUsageClient.GeminiCredentials>(
                        client,
                        "EnsureFreshCredentialsAsync",
                        freshCredentials,
                        CancellationToken.None));
                SetPrivateField<GeminiUsageClient.GeminiCredentials?>(
                    client,
                    "_refreshedCredentials",
                    freshCredentials);
                Equal(
                    freshCredentials,
                    await InvokePrivateTaskResultAsync<GeminiUsageClient.GeminiCredentials>(
                        client,
                        "EnsureFreshCredentialsAsync",
                        freshCredentials with { AccessToken = null },
                        CancellationToken.None));
            }

            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       throw new InvalidOperationException("HTTP should not be reached."))))
            {
                var exception = await ThrowsAsync<GeminiUsageException>(() =>
                    InvokePrivateTaskResultAsync<GeminiUsageClient.GeminiCredentials>(
                        new GeminiUsageClient(http, NoLocalGeminiSnapshot),
                        "EnsureFreshCredentialsAsync",
                        freshCredentials with
                        {
                            AccessToken = null,
                            RefreshToken = null,
                            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
                        },
                        CancellationToken.None));
                True(exception.Message.Contains("no refresh token", StringComparison.OrdinalIgnoreCase));
            }

            GeminiUsageClient.TryPersistRefreshedCredentials(
                freshCredentials with { SourcePath = null });
            GeminiUsageClient.TryPersistRefreshedCredentials(
                freshCredentials with { SourcePath = Path.Combine(directory, "missing-oauth.json") });
            File.WriteAllText(sourcePath, "{invalid");
            GeminiUsageClient.TryPersistRefreshedCredentials(freshCredentials);

            WriteGeminiCredentials(
                directory,
                "retry-old-access",
                "retry-refresh",
                DateTimeOffset.UtcNow.AddHours(1));
            var retryHandler = new StubHttpHandler((request, call, _) =>
            {
                if (request.RequestUri!.AbsoluteUri.Contains(
                        "loadCodeAssist",
                        StringComparison.Ordinal))
                {
                    return Task.FromResult(
                        new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                if (request.RequestUri.AbsoluteUri.Contains(
                        "oauth2.googleapis.com",
                        StringComparison.Ordinal))
                {
                    return Task.FromResult(JsonResponse(
                        HttpStatusCode.OK,
                        """{"access_token":"retry-fresh-access","expires_in":3600}"""));
                }

                return Task.FromResult(call < 4
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : JsonResponse(
                        HttpStatusCode.OK,
                        """{"buckets":[{"modelId":"gemini-flash","remainingFraction":0.75}]}"""));
            });
            using (var http = new HttpClient(retryHandler))
            {
                var retried = await new GeminiUsageClient(
                    http,
                    NoLocalGeminiSnapshot).GetUsageAsync();
                Equal(25, retried.Primary!.UsedPercent);
                Equal(5, retryHandler.CallCount);
            }

            var userStatusJson =
                """
                {
                  "userStatus":{
                    "email":"local@example.com",
                    "userTier":{"description":"Local Pro"},
                    "cascadeModelConfigData":{
                      "clientModelConfigs":[
                        {"label":"Gemini Pro","quotaInfo":{"remainingFraction":0.8}},
                        {"label":"Gemini Pro duplicate","quotaInfo":{"remainingFraction":0.8}},
                        {"label":"OpenAI model","quotaInfo":{"remainingFraction":0.6}},
                        {"label":"Other model","quotaInfo":{"remainingFraction":0.4}}
                      ]
                    }
                  }
                }
                """;
            var summaryJson =
                """
                {
                  "quotaSummary":{
                    "groups":[
                      {
                        "displayName":"Gemini Models",
                        "buckets":[{"bucketId":"weekly-7d","remainingFraction":0.5}]
                      },
                      {
                        "displayName":"Other Models",
                        "buckets":[{"bucketId":"session-five","remainingFraction":0.7}]
                      }
                    ]
                  }
                }
                """;
            var localHandler = new StubHttpHandler((request, _, _) =>
                Task.FromResult(JsonResponse(
                    HttpStatusCode.OK,
                    request.RequestUri!.AbsolutePath.Contains("GetUserStatus", StringComparison.Ordinal)
                        ? userStatusJson
                        : summaryJson)));
            using (var http = new HttpClient(localHandler))
            {
                var snapshot = await InvokePrivateStaticTaskResultAsync<UsageSnapshot?>(
                    typeof(GeminiUsageClient),
                    "TryQueryAntigravityPortAsync",
                    http,
                    (ushort)51_234,
                    "synthetic-csrf",
                    CancellationToken.None);
                NotNull(snapshot);
                Equal("Local Pro", snapshot!.Plan);
                True(snapshot.Metrics.Count >= 3);
                Equal(2, localHandler.CallCount);
            }

            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))))
            {
                Null(await InvokePrivateStaticTaskResultAsync<UsageSnapshot?>(
                    typeof(GeminiUsageClient),
                    "TryQueryAntigravityPortAsync",
                    http,
                    (ushort)51_234,
                    "synthetic-csrf",
                    CancellationToken.None));
                Equal(
                    0,
                    (await InvokePrivateStaticTaskResultAsync<IReadOnlyList<UsageMetric>>(
                        typeof(GeminiUsageClient),
                        "TryQueryAntigravityQuotaSummaryPortAsync",
                        http,
                        (ushort)51_234,
                        "synthetic-csrf",
                        CancellationToken.None)).Count);
            }

            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}")))))
            {
                Null(await InvokePrivateStaticTaskResultAsync<UsageSnapshot?>(
                    typeof(GeminiUsageClient),
                    "TryQueryAntigravityPortAsync",
                    http,
                    (ushort)51_234,
                    "synthetic-csrf",
                    CancellationToken.None));
            }

            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       throw new InvalidOperationException("synthetic local failure"))))
            {
                Null(await InvokePrivateStaticTaskResultAsync<UsageSnapshot?>(
                    typeof(GeminiUsageClient),
                    "TryQueryAntigravityPortAsync",
                    http,
                    (ushort)51_234,
                    "synthetic-csrf",
                    CancellationToken.None));
                Equal(
                    0,
                    (await InvokePrivateStaticTaskResultAsync<IReadOnlyList<UsageMetric>>(
                        typeof(GeminiUsageClient),
                        "TryQueryAntigravityQuotaSummaryPortAsync",
                        http,
                        (ushort)51_234,
                        "synthetic-csrf",
                        CancellationToken.None)).Count);
            }

            foreach (var (json, expectedPlan) in new[]
                     {
                         ("""{"currentTier":{"id":"standard-tier"}}""", "Paid"),
                         ("""{"currentTier":{"id":"legacy-tier"}}""", "Legacy"),
                         ("""{"currentTier":{"id":"unknown-tier"}}""", (string?)null),
                     })
            {
                using var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                    Task.FromResult(JsonResponse(HttpStatusCode.OK, json))));
                Equal(
                    expectedPlan,
                    await InvokePrivateTaskResultAsync<string?>(
                        new GeminiUsageClient(http, NoLocalGeminiSnapshot),
                        "LoadCodeAssistPlanAsync",
                        "synthetic-access",
                        null,
                        CancellationToken.None));
            }

            using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                       throw new InvalidOperationException("synthetic plan failure"))))
            {
                Null(await InvokePrivateTaskResultAsync<string?>(
                    new GeminiUsageClient(http, NoLocalGeminiSnapshot),
                    "LoadCodeAssistPlanAsync",
                    "synthetic-access",
                    null,
                    CancellationToken.None));
            }

            using (var document = JsonDocument.Parse(
                       """
                       {
                         "userStatus":{
                           "planStatus":{"planInfo":{"planDisplayName":"Workspace Plan"}},
                           "cascadeModelConfigData":{"clientModelConfigs":[
                             null,
                             {"label":""},
                             {"label":"GPT model"},
                             {"label":"Unknown","quotaInfo":{"remainingFraction":0.2}}
                           ]}
                         }
                       }
                       """))
            {
                var snapshot = GeminiUsageClient.ParseAntigravityUserStatus(document.RootElement);
                NotNull(snapshot);
                Equal("Workspace Plan", snapshot!.Plan);
                Equal("Other Models", snapshot.Metrics[0].Name);
            }

            using (var document = JsonDocument.Parse("""{"userStatus":{}}"""))
            {
                Null(GeminiUsageClient.ParseAntigravityUserStatus(document.RootElement));
            }
            Null(GeminiUsageClient.ExtractEmailFromJwt("single-segment"));

            Environment.SetEnvironmentVariable("GEMINI_CONFIG_DIR", "relative-config");
            True(Path.IsPathFullyQualified(InvokePrivateStatic<string>(
                typeof(GeminiUsageClient),
                "GetGeminiConfigDir")));
            Environment.SetEnvironmentVariable("GEMINI_CONFIG_DIR", directory);

            using (var cancellation = new CancellationTokenSource())
            using (var http = new HttpClient(new StubHttpHandler((_, _, token) =>
                       Task.FromCanceled<HttpResponseMessage>(token))))
            {
                cancellation.Cancel();
                await ThrowsAsync<OperationCanceledException>(() =>
                    InvokePrivateStaticTaskResultAsync<IReadOnlyList<UsageMetric>>(
                        typeof(GeminiUsageClient),
                        "TryQueryAntigravityQuotaSummaryPortAsync",
                        http,
                        (ushort)51_234,
                        "synthetic-csrf",
                        cancellation.Token));
            }

            var process = new GeminiUsageClient.ProcessInfo(
                "primary",
                "extension",
                51_234,
                (uint)Environment.ProcessId);
            var boundCandidates = process.GetBoundCandidates(CandidatePorts);
            Equal(4, boundCandidates.Count);
            True(process.IsListeningPortStillOwned(
                boundCandidates[0],
                OwnedCandidatePort));
            False(process.IsListeningPortStillOwned(
                new GeminiUsageClient.BoundAntigravityCandidate(
                    (uint)Environment.ProcessId,
                    1,
                    "primary"),
                OwnedCandidatePort));
            var pidless = process with { Pid = null };
            Equal(0, pidless.GetBoundCandidates(OwnedCandidatePort).Count);
            False(pidless.IsListeningPortStillOwned(
                new GeminiUsageClient.BoundAntigravityCandidate(1, 1, "primary"),
                UnrelatedCandidatePort));

            var reserveArguments = new object?[] { (ushort)0 };
            True(InvokePrivateStatic<bool>(
                typeof(AgyUsageProbe),
                "TryReserveLoopbackPort",
                reserveArguments));
            True((ushort)reserveArguments[0]! > 0);

            Null(await InvokePrivateStaticTaskResultAsync<string?>(
                typeof(AgyUsageProbe),
                "TryReadCompletedOutputAsync",
                new object?[] { null }));
            Equal(
                "completed",
                await InvokePrivateStaticTaskResultAsync<string?>(
                    typeof(AgyUsageProbe),
                    "TryReadCompletedOutputAsync",
                    Task.FromResult("completed")));
            Null(await InvokePrivateStaticTaskResultAsync<string?>(
                typeof(AgyUsageProbe),
                "TryReadCompletedOutputAsync",
                Task.FromException<string>(new InvalidOperationException("synthetic output failure"))));

            var syntheticMetric = new[]
            {
                new UsageMetric("Synthetic", UsageMetricKind.Rolling, 12),
            };
            var defaultAgySnapshot = InvokePrivateStatic<UsageSnapshot>(
                typeof(AgyUsageProbe),
                "CreateSnapshot",
                syntheticMetric,
                " ",
                null);
            Equal("Antigravity", defaultAgySnapshot.Plan);
            Null(defaultAgySnapshot.AccountName);
            var namedAgySnapshot = InvokePrivateStatic<UsageSnapshot>(
                typeof(AgyUsageProbe),
                "CreateSnapshot",
                syntheticMetric,
                "Named Plan",
                "agy@example.com");
            Equal("Named Plan", namedAgySnapshot.Plan);
            Equal("agy@example.com", namedAgySnapshot.AccountName);

            _ = InvokePrivateStatic<object?>(
                typeof(AgyUsageProbe),
                "TryKillOwnedProcesses",
                new[] { int.MaxValue, int.MaxValue, Environment.ProcessId },
                DateTime.UtcNow.AddHours(-1));
            _ = InvokePrivateStatic<object?>(
                typeof(AgyUsageProbe),
                "TryKillOwnedProcesses",
                new[] { Environment.ProcessId },
                DateTime.UtcNow.AddHours(1));

            var portField = typeof(AgyUsageProbe).GetField(
                "_hubPort",
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AgyUsageProbe._hubPort was not found.");
            var tokenField = typeof(AgyUsageProbe).GetField(
                "_hubToken",
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AgyUsageProbe._hubToken was not found.");
            var processField = typeof(AgyUsageProbe).GetField(
                "_hubProcess",
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AgyUsageProbe._hubProcess was not found.");
            var clientField = typeof(AgyUsageProbe).GetField(
                "_hubClient",
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AgyUsageProbe._hubClient was not found.");
            var previousPort = (ushort)portField.GetValue(null)!;
            var previousToken = (string)tokenField.GetValue(null)!;
            var previousProcess = (Process?)processField.GetValue(null);
            var previousClient = (HttpClient?)clientField.GetValue(null);
            try
            {
                portField.SetValue(null, (ushort)51_234);
                tokenField.SetValue(null, "synthetic-token");
                using (var successClient = new HttpClient(new StubHttpHandler((_, _, _) =>
                           Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}")))))
                using (var response = await InvokePrivateStaticTaskResultAsync<JsonDocument?>(
                           typeof(AgyUsageProbe),
                           "TryPostAsync",
                           successClient,
                           "GetUserStatus",
                           "{}",
                           CancellationToken.None))
                {
                    NotNull(response);
                }

                using (var unavailableClient = new HttpClient(new StubHttpHandler((_, _, _) =>
                           Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))))
                {
                    Null(await InvokePrivateStaticTaskResultAsync<JsonDocument?>(
                        typeof(AgyUsageProbe),
                        "TryPostAsync",
                        unavailableClient,
                        "GetUserStatus",
                        "{}",
                        CancellationToken.None));
                }

                using (var failingClient = new HttpClient(new StubHttpHandler((_, _, _) =>
                           throw new InvalidOperationException("synthetic hub failure"))))
                {
                    Null(await InvokePrivateStaticTaskResultAsync<JsonDocument?>(
                        typeof(AgyUsageProbe),
                        "TryPostAsync",
                        failingClient,
                        "GetUserStatus",
                        "{}",
                        CancellationToken.None));
                }

                using var cancellation = new CancellationTokenSource();
                using var cancelledClient = new HttpClient(new StubHttpHandler((_, _, token) =>
                    Task.FromCanceled<HttpResponseMessage>(token)));
                cancellation.Cancel();
                await ThrowsAsync<OperationCanceledException>(() =>
                    InvokePrivateStaticTaskResultAsync<JsonDocument?>(
                        typeof(AgyUsageProbe),
                        "TryPostAsync",
                        cancelledClient,
                        "GetUserStatus",
                        "{}",
                        cancellation.Token));

                const string quotaSummary =
                    """
                    {"groups":[{"displayName":"Gemini Models","buckets":[{"bucketId":"gemini_5h","remainingFraction":0.8}]}]}
                    """;
                using var currentProcess = Process.GetCurrentProcess();
                processField.SetValue(null, currentProcess);

                using (var noSummaryClient = new HttpClient(new StubHttpHandler((_, _, _) =>
                           Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))))
                {
                    clientField.SetValue(null, noSummaryClient);
                    Null(await InvokePrivateStaticTaskResultAsync<UsageSnapshot?>(
                        typeof(AgyUsageProbe),
                        "TryFetchFromHubAsync",
                        CancellationToken.None));
                }

                using (var emptySummaryClient = new HttpClient(new StubHttpHandler((_, _, _) =>
                           Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}")))))
                {
                    clientField.SetValue(null, emptySummaryClient);
                    Null(await InvokePrivateStaticTaskResultAsync<UsageSnapshot?>(
                        typeof(AgyUsageProbe),
                        "TryFetchFromHubAsync",
                        CancellationToken.None));
                }

                using (var summaryOnlyClient = new HttpClient(new StubHttpHandler((request, _, _) =>
                           Task.FromResult(request.RequestUri!.AbsolutePath.Contains(
                                   "RetrieveUserQuotaSummary",
                                   StringComparison.Ordinal)
                               ? JsonResponse(HttpStatusCode.OK, quotaSummary)
                               : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))))
                {
                    clientField.SetValue(null, summaryOnlyClient);
                    var summaryOnly = await InvokePrivateStaticTaskResultAsync<UsageSnapshot?>(
                        typeof(AgyUsageProbe),
                        "TryFetchFromHubAsync",
                        CancellationToken.None);
                    NotNull(summaryOnly);
                    Equal("Antigravity", summaryOnly!.Plan);
                    Equal(20, summaryOnly.Primary!.UsedPercent);
                }

                using (var mergedClient = new HttpClient(new StubHttpHandler((request, _, _) =>
                           Task.FromResult(JsonResponse(
                               HttpStatusCode.OK,
                               request.RequestUri!.AbsolutePath.Contains(
                                       "RetrieveUserQuotaSummary",
                                       StringComparison.Ordinal)
                                   ? quotaSummary
                                   : """
                                     {"userStatus":{"userTier":{"name":"Hub Plan"},"cascadeModelConfigData":{"clientModelConfigs":[{"label":"Gemini Models","quotaInfo":{"remainingFraction":0.6}}]}}}
                                     """)))))
                {
                    clientField.SetValue(null, mergedClient);
                    var merged = await InvokePrivateStaticTaskResultAsync<UsageSnapshot?>(
                        typeof(AgyUsageProbe),
                        "TryFetchFromHubAsync",
                        CancellationToken.None);
                    NotNull(merged);
                    Equal("Hub Plan", merged!.Plan);
                    True(merged.Metrics.Any(metric => metric.Kind == UsageMetricKind.Session));
                }
            }
            finally
            {
                clientField.SetValue(null, previousClient);
                processField.SetValue(null, previousProcess);
                portField.SetValue(null, previousPort);
                tokenField.SetValue(null, previousToken);
            }

            var exceptionWithInner = new GeminiUsageException(
                "synthetic",
                new InvalidOperationException("inner"));
            NotNull(exceptionWithInner.InnerException);
            Equal("Google Gemini", new GeminiUsageClient(
                new HttpClient(new StubHttpHandler((_, _, _) =>
                    throw new InvalidOperationException())),
                NoLocalGeminiSnapshot).DisplayName);
        }
        finally
        {
            RestoreEnvironment(environment);
            Directory.Delete(directory, recursive: true);
        }
    }

    public static async Task TestRemainingUiBranchesAsync()
    {
        var now = DateTimeOffset.Now;
        var disconnected = new ProviderStatus(
            "claude",
            "Claude Code",
            null,
            "Sign in required.",
            false,
            null,
            "claude",
            new Uri("https://example.com/account"));
        using (var card = new ProviderUsageCard(
                   disconnected,
                   expanded: true,
                   Array.Empty<UsageSample>(),
                   showTrend: false)
               {
                   Width = 500,
               })
        {
            var actions = new List<ProviderCardAction>();
            card.ActionInvoked += (_, eventArgs) => actions.Add(eventArgs.Action);
            DrawControl(card);
            var actionBounds = GetPrivateField<Rectangle>(card, "_actionBounds");
            True(actionBounds.Width > 0);
            InvokeProtected(card, "OnMouseMove", new MouseEventArgs(
                MouseButtons.None,
                0,
                actionBounds.Left + 1,
                actionBounds.Top + 1,
                0));
            InvokeProtected(card, "OnMouseDown", new MouseEventArgs(
                MouseButtons.Left,
                1,
                actionBounds.Left + 1,
                actionBounds.Top + 1,
                0));
            InvokeProtected(card, "OnMouseClick", new MouseEventArgs(
                MouseButtons.Left,
                1,
                actionBounds.Left + 1,
                actionBounds.Top + 1,
                0));
            Equal(ProviderCardAction.CopyCommand, actions.Single());
            InvokeProtected(card, "OnMouseClick", new MouseEventArgs(
                MouseButtons.Right,
                1,
                actionBounds.Left + 1,
                actionBounds.Top + 1,
                0));
            Equal(1, actions.Count);
            InvokeProtected(card, "OnKeyDown", new KeyEventArgs(Keys.F2));
            InvokeProtected(card, "OnGotFocus", EventArgs.Empty);
            InvokeProtected(card, "OnLostFocus", EventArgs.Empty);
            InvokeProtected(card, "OnDpiChangedAfterParent", EventArgs.Empty);
            True((bool)(InvokePrivate(card, "IsInputKey", Keys.Enter) ?? false));
            False((bool)(InvokePrivate(card, "IsInputKey", Keys.F2) ?? true));
        }

        using (var tinyCard = new ProviderUsageCard(
                   disconnected,
                   expanded: false,
                   Array.Empty<UsageSample>(),
                   showTrend: false)
               {
                   Size = new Size(30, 10),
               })
        {
            DrawControl(tinyCard);
        }

        var settings = new AppSettings
        {
            DashboardBounds = new[] { -10_000, -10_000, 650, 480 },
        };
        using var popup = new UsagePopupForm(settings)
        {
            Location = new Point(-10_000, -10_000),
        };
        var balanceSnapshot = new UsageSnapshot(
            "Balance",
            new[] { new UsageMetric("Credits", UsageMetricKind.Balance, null, RemainingText: "$3") },
            now,
            "codex",
            "Codex");
        var connected = new ProviderStatus(
            "codex",
            "Codex",
            balanceSnapshot,
            null,
            false,
            now,
            "codex login",
            new Uri("http://not-https.example"));
        using (var connectedCard = new ProviderUsageCard(
                   connected,
                   expanded: true,
                   Array.Empty<UsageSample>(),
                   showTrend: false)
               {
                   Width = 500,
               })
        {
            ProviderCardAction? action = null;
            connectedCard.ActionInvoked += (_, eventArgs) => action = eventArgs.Action;
            DrawControl(connectedCard);
            var linkBounds = GetPrivateField<Rectangle>(connectedCard, "_linkBounds");
            True(linkBounds.Width > 0);
            InvokeProtected(connectedCard, "OnMouseClick", new MouseEventArgs(
                MouseButtons.Left,
                1,
                linkBounds.Left + 1,
                linkBounds.Top + 1,
                0));
            Equal(ProviderCardAction.OpenAccount, action);
            var staleWithoutTime = connected with
            {
                Error = "stale",
                LastUpdated = null,
            };
            using var staleCard = new ProviderUsageCard(
                staleWithoutTime,
                expanded: true,
                Array.Empty<UsageSample>(),
                showTrend: false);
            Equal("stale", InvokePrivate<string?>(
                staleCard,
                "DetailRight",
                (object?)null));
        }
        popup.SetStates(new[] { connected, disconnected }, false, now, Array.Empty<UsageSample>());
        popup.SetMode(DashboardMode.Full);
        popup.SetMode(DashboardMode.Full);
        InvokePrivate(
            popup,
            "ShowDashboard",
            new Rectangle(-10_000, -10_000, 1_200, 900),
            false);
        Application.DoEvents();
        True(popup.Visible);

        var fitted = InvokePrivateStatic<Rectangle>(
            typeof(UsagePopupForm),
            "FitToWorkingArea",
            new Rectangle(-20_000, -20_000, 50, 50),
            new Rectangle(-10_000, -10_000, 1_000, 800));
        True(fitted.Width >= 460);
        True(fitted.Height >= 360);

        Theme.Apply(ThemeMode.Light, 72, 90);
        Application.DoEvents();
        InvokePrivate(
            popup,
            "OnCardActionInvoked",
            popup,
            new ProviderCardActionEventArgs(connected, ProviderCardAction.OpenAccount));
        InvokePrivate(
            popup,
            "OnCardActionInvoked",
            popup,
            new ProviderCardActionEventArgs(connected, ProviderCardAction.None));

        var loading = disconnected with
        {
            ProviderId = "gemini",
            ProviderName = "Google Gemini",
            IsLoading = true,
        };
        popup.SetStates(new[] { loading }, true, null, Array.Empty<UsageSample>());
        popup.SetMode(DashboardMode.Compact);
        popup.ShowNearTray(DashboardMode.Compact);
        Application.DoEvents();
        InvokeProtected(popup, "OnDeactivate", EventArgs.Empty);
        False(popup.Visible);
        popup.ShowNearTray(DashboardMode.Compact);
        InvokeProtected(popup, "OnKeyDown", new KeyEventArgs(Keys.Escape));
        False(popup.Visible);

        popup.SetStates(Array.Empty<ProviderStatus>(), true, null, Array.Empty<UsageSample>());
        InvokePrivate(popup, "UpdateStatus");
        popup.SetStates(Array.Empty<ProviderStatus>(), false, null, Array.Empty<UsageSample>());
        InvokePrivate(popup, "UpdateStatus");
        Null(InvokePrivateStatic<UsageMetric?>(
            typeof(UsagePopupForm),
            "Headline",
            (object?)null));

        popup.SetStates(new[] { connected, disconnected }, false, now, Array.Empty<UsageSample>());
        popup.SetMode(DashboardMode.Full);
        popup.ShowNearTray(DashboardMode.Full);
        Application.DoEvents();
        var timer = GetPrivateField<System.Windows.Forms.Timer>(popup, "_uiTickTimer");
        InvokePrivate(timer, "OnTick", EventArgs.Empty);
        popup.Hide();
        popup.SetMode(DashboardMode.Compact);
        popup.ShowNearTray(DashboardMode.Full);
        Application.DoEvents();

        var content = GetPrivateField<FlowLayoutPanel>(popup, "_content");
        var contentSize = content.Size;
        content.Size = new Size(10, Math.Max(10, content.Height));
        InvokePrivate(popup, "UpdateCardWidths");
        content.Size = contentSize;
        SetPrivateField(popup, "_updatingCardWidths", true);
        InvokePrivate(popup, "UpdateCardWidths");
        SetPrivateField(popup, "_updatingCardWidths", false);

        Equal(DashboardMode.Full, popup.Mode);
        popup.WindowState = FormWindowState.Maximized;
        popup.Close();
        False(popup.Visible);
        popup.CloseForExit();

        using (var centered = new UsagePopupForm(new AppSettings()))
        {
            centered.SetMode(DashboardMode.Full);
            centered.WindowState = FormWindowState.Minimized;
            InvokePrivate(
                centered,
                "ShowDashboard",
                new Rectangle(-8_000, -8_000, 1_000, 700),
                false);
            Application.DoEvents();
            Equal(FormWindowState.Normal, centered.WindowState);
            True(centered.Visible);
            centered.Hide();
            centered.WindowState = FormWindowState.Maximized;
            centered.SetMode(DashboardMode.Compact);
            centered.CloseForExit();
        }

        var disposedPopup = new UsagePopupForm(new AppSettings());
        disposedPopup.Dispose();
        InvokePrivate(disposedPopup, "OnThemeChanged", disposedPopup, EventArgs.Empty);

        var providerEntries = new[]
        {
            ("codex", "Codex"),
            ("claude", "Claude Code"),
        };
        var darkSettings = new AppSettings();
        using (var settingsForm = new SettingsForm(darkSettings, providerEntries))
        {
            var providers = GetPrivateField<CheckedListBox>(settingsForm, "_providers");
            providers.SelectedIndex = 0;
            InvokePrivate(settingsForm, "MoveSelected", -1);
            providers.SelectedIndex = 1;
            InvokePrivate(settingsForm, "MoveSelected", -1);
            InvokePrivate(settingsForm, "MoveSelected", 10);

            var deleteHistory = Descendants(settingsForm)
                .OfType<Button>()
                .Single(button => button.Text == "Delete recorded history");
            InvokeProtected(deleteHistory, "OnClick", EventArgs.Empty);

            GetPrivateField<ComboBox>(settingsForm, "_theme").SelectedIndex = 1;
            InvokePrivate(settingsForm, "Apply");
            Equal(ThemeMode.Dark, darkSettings.Theme);
        }

        var lightSettings = new AppSettings();
        using (var settingsForm = new SettingsForm(lightSettings, providerEntries))
        {
            GetPrivateField<ComboBox>(settingsForm, "_theme").SelectedIndex = 2;
            InvokePrivate(settingsForm, "Apply");
            Equal(ThemeMode.Light, lightSettings.Theme);
        }

        var offeredRelease = new UpdateRelease(
            "v99.0.0",
            "99.0.0",
            new Uri("https://github.com/VladiKogan/UsageAI/releases/tag/v99.0.0"),
            null,
            null);
        var updateResults = new Queue<UpdateCheckResult>(new[]
        {
            new UpdateCheckResult(true, null),
            new UpdateCheckResult(false, null),
            new UpdateCheckResult(true, offeredRelease),
        });
        UpdateRelease? promptedRelease = null;
        var updateSettings = new AppSettings();
        using (var settingsForm = new SettingsForm(
                   updateSettings,
                   providerEntries,
                   _ => Task.FromResult(updateResults.Dequeue()),
                   release =>
                   {
                       promptedRelease = release;
                       return Task.CompletedTask;
                   }))
        {
            var version = Descendants(settingsForm)
                .OfType<Label>()
                .Single(label => label.Text == $"v{AppIdentity.Version}");
            Equal(Theme.Signal, version.ForeColor);

            await InvokePrivateTaskAsync(settingsForm, "CheckForUpdatesAsync");
            var status = GetPrivateField<Label>(settingsForm, "_updateStatus");
            True(status.Text.Contains("up to date", StringComparison.Ordinal));
            Equal(Theme.Success, status.ForeColor);

            await InvokePrivateTaskAsync(settingsForm, "CheckForUpdatesAsync");
            True(status.Text.Contains("Couldn’t check", StringComparison.Ordinal));
            Equal(Theme.Warning, status.ForeColor);

            await InvokePrivateTaskAsync(settingsForm, "CheckForUpdatesAsync");
            True(status.Text.Contains("99.0.0", StringComparison.Ordinal));
            Equal(offeredRelease, promptedRelease);
            NotNull(updateSettings.LastUpdateCheckUtc);
        }

        Theme.Apply(ThemeMode.Dark, 72, 90);
    }

    public static Task TestStalePresentationEdgesAsync()
    {
        var now = DateTimeOffset.Now;
        var snapshot = Snapshot("fixture", "Fixture", 64, now.AddHours(-3));
        var staleOne = new ProviderStatus(
            "fixture-one",
            "Fixture One",
            snapshot,
            "Temporary failure.",
            IsLoading: false,
            LastUpdated: now.AddHours(-3),
            SignInCommand: "fixture login",
            AccountUrl: new Uri("https://example.com/fixture-one"),
            LastAttemptedAt: now.AddMinutes(-5),
            NextRetryAt: now.AddHours(2));
        var staleTwo = staleOne with
        {
            ProviderId = "fixture-two",
            ProviderName = "Fixture Two",
            LastAttemptedAt = now.AddMinutes(-2),
        };

        Equal(
            "Checking all providers...",
            UsagePopupForm.RefreshSummary(new[] { staleOne }, true, now, now));
        Equal(
            "2 providers stale · checked 2 min ago",
            UsagePopupForm.RefreshSummary(new[] { staleOne, staleTwo }, false, now, now));
        Equal(
            "1 provider stale",
            UsagePopupForm.RefreshSummary(
                new[] { staleOne with { LastAttemptedAt = null } },
                false,
                now,
                now));
        Equal(
            "Checked 3 min ago",
            UsagePopupForm.RefreshSummary(Array.Empty<ProviderStatus>(), false, now.AddMinutes(-3), now));
        Equal(
            "Waiting for first refresh",
            UsagePopupForm.RefreshSummary(Array.Empty<ProviderStatus>(), false, null, now));
        Equal("retry in 1d", UsageFormatting.RetryCountdown(now.AddHours(25), now));
        Equal("retry in 2h", UsageFormatting.RetryCountdown(now.AddHours(2.5), now));

        using var card = new ProviderUsageCard(staleOne, expanded: true, Array.Empty<UsageSample>(), showTrend: false);
        var detail = InvokePrivate<string>(card, "DetailRight", snapshot.Primary);
        True(detail.StartsWith("retry in ", StringComparison.Ordinal));
        True(card.AccessibleDescription?.Contains("Last successful update", StringComparison.Ordinal) == true);
        True(card.AccessibleDescription?.Contains("retry in ", StringComparison.Ordinal) == true);
        return Task.CompletedTask;
    }

    public static Task TestLimitedReadStreamBranchesAsync()
    {
        var limitedType = typeof(SecureHttp).GetNestedType(
            "LimitedReadStream",
            BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LimitedReadStream was not found.");
        using (var inner = new MemoryStream(Encoding.UTF8.GetBytes("abcd")))
        using (var limited = (Stream)(Activator.CreateInstance(
                   limitedType,
                   BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                   binder: null,
                   args: new object[] { inner, 8L },
                   culture: null)
               ?? throw new InvalidOperationException("Could not create LimitedReadStream.")))
        {
            True(limited.CanRead);
            False(limited.CanSeek);
            False(limited.CanWrite);
            Throws<NotSupportedException>(() => _ = limited.Length);
            Throws<NotSupportedException>(() => _ = limited.Position);
            Throws<NotSupportedException>(() => limited.Position = 0);
            var bytes = new byte[2];
            Equal(2, limited.Read(bytes, 0, bytes.Length));
            Equal(2, limited.Read(bytes.AsSpan()));
            Throws<NotSupportedException>(limited.Flush);
            Throws<NotSupportedException>(() => limited.Seek(0, SeekOrigin.Begin));
            Throws<NotSupportedException>(() => limited.SetLength(0));
            Throws<NotSupportedException>(() => limited.Write(bytes, 0, bytes.Length));
        }

        using (var inner = new MemoryStream(Encoding.UTF8.GetBytes("abcd")))
        using (var limited = (Stream)(Activator.CreateInstance(
                   limitedType,
                   BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                   binder: null,
                   args: new object[] { inner, 3L },
                   culture: null)
               ?? throw new InvalidOperationException("Could not create LimitedReadStream.")))
        {
            var bytes = new byte[4];
            Throws<InvalidDataException>(() => limited.ReadExactly(bytes));
        }

        return Task.CompletedTask;
    }

    public static Task TestCredentialFileBranchesAsync()
    {
        var directory = Path.Combine(AppPaths.DataDirectory, $"low-level-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var credential = Path.Combine(directory, "credential.json");
            File.WriteAllText(credential, "original");
            Throws<IOException>(() => SecureLocalFile.ReplaceTextPreservingMetadata(
                credential,
                "different",
                "replacement"));
            Equal("original", File.ReadAllText(credential));

            var utf8 = InvokePrivateStatic<string>(
                typeof(WindowsCredentialReader),
                "DecodePassword",
                Encoding.UTF8.GetBytes("plain\0"));
            Equal("plain", utf8);
            var utf16 = InvokePrivateStatic<string>(
                typeof(WindowsCredentialReader),
                "DecodePassword",
                Encoding.Unicode.GetBytes("wide\0"));
            Equal("wide", utf16);
            Equal(
                0,
                WindowsCredentialReader.FindGenericPasswords(
                    $"UsageAI-nonexistent-{Guid.NewGuid():N}").Count);
            Equal(
                0,
                WindowsCredentialReader.FindKeyringPasswords(
                    $"UsageAI-nonexistent-{Guid.NewGuid():N}").Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    public static async Task TestProcessAndMessageBranchesAsync()
    {
        var commandProcessor = Environment.GetEnvironmentVariable("COMSPEC")
            ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using (var active = Process.Start(new ProcessStartInfo
               {
                   FileName = commandProcessor,
                   UseShellExecute = false,
                   CreateNoWindow = true,
                   ArgumentList = { "/c", "ping", "127.0.0.1", "-n", "30" },
               }) ?? throw new InvalidOperationException("Could not start the kill fixture."))
        {
            ProcessSecurity.TryKill(active);
            await active.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            True(active.HasExited);
        }

        using (var window = new MessageWindow())
        {
            var showCount = 0;
            var hotkeyCount = 0;
            window.ShowRequested += (_, _) => showCount++;
            window.HotkeyPressed += (_, _) => hotkeyCount++;
            window.UnregisterHotkey();
            InvokeWindowMessage(window, SingleInstance.ShowMessage, IntPtr.Zero);
            InvokeWindowMessage(window, 0x0312, new IntPtr(0x0A51));
            Equal(1, showCount);
            Equal(1, hotkeyCount);
        }
    }

    private static UsageSample[] ForecastSamples(
        DateTimeOffset now,
        int first,
        int second,
        int third,
        TimeSpan interval) =>
        new[]
        {
            new UsageSample(now - interval - interval, "codex", "Session:Session", first),
            new UsageSample(now - interval, "codex", "Session:Session", second),
            new UsageSample(now, "codex", "Session:Session", third),
        };

    private static UsageSnapshot Snapshot(
        string providerId,
        string providerName,
        int usedPercent,
        DateTimeOffset fetchedAt) =>
        new(
            "Test",
            new[]
            {
                new UsageMetric(
                    "Session",
                    UsageMetricKind.Session,
                    usedPercent,
                    fetchedAt.AddHours(2),
                    300),
            },
            fetchedAt,
            providerId,
            providerName);

    private static StubHttpHandler GeminiHandler(HttpStatusCode quotaStatus) =>
        new((request, _, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("loadCodeAssist", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(
                    HttpStatusCode.OK,
                    """{"paidTier":{"name":"Gemini Code Assist Pro"}}"""));
            }

            var response = quotaStatus == HttpStatusCode.OK
                ? JsonResponse(
                    HttpStatusCode.OK,
                    """{"buckets":[{"modelId":"gemini-pro","remainingFraction":0.8}]}""")
                : new HttpResponseMessage(quotaStatus);
            if (quotaStatus == HttpStatusCode.TooManyRequests)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(29));
            }

            return Task.FromResult(response);
        });

    private static Task<UsageSnapshot?> NoLocalGeminiSnapshot(CancellationToken cancellationToken) =>
        Task.FromResult<UsageSnapshot?>(null);

    private static void WriteGeminiCredentials(
        string directory,
        string accessToken,
        string? refreshToken,
        DateTimeOffset expiresAt)
    {
        var refreshProperty = refreshToken is null
            ? string.Empty
            : $",\"refresh_token\":\"{refreshToken}\"";
        File.WriteAllText(
            Path.Combine(directory, "oauth_creds.json"),
            $$"""{"access_token":"{{accessToken}}"{{refreshProperty}},"expiry_date":{{expiresAt.ToUnixTimeMilliseconds()}}}""");
    }

    private static HttpResponseMessage JsonResponse(
        HttpStatusCode statusCode,
        string json,
        string mediaType = "application/json") =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, mediaType),
        };

    private static HttpResponseMessage BinaryResponse(byte[] contents) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(contents),
        };

    private static HttpResponseMessage RedirectResponse(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private static Dictionary<string, string?> SaveEnvironment(params string[] names) =>
        names.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);

    private static void RestoreEnvironment(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (name, value) in values)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public static Task TestDashboardMetricModeUiAsync()
    {
        var now = DateTimeOffset.Now;
        var metrics = new UsageMetric[]
        {
            new("Credits", UsageMetricKind.Balance, null, RemainingText: "$7"),
            new("Session lower", UsageMetricKind.Session, 30, now.AddHours(1)),
            new("Rolling", UsageMetricKind.Rolling, 81, now.AddDays(2)),
            new("Session highest", UsageMetricKind.Session, 74, now.AddHours(2)),
            new("Monthly unlimited", UsageMetricKind.Monthly, 100, IsUnlimited: true),
            new("Monthly", UsageMetricKind.Monthly, 62, now.AddDays(8)),
        };
        var snapshot = new UsageSnapshot("Pro", metrics, now, "gemini", "Google Gemini");
        var status = new ProviderStatus("gemini", "Google Gemini", snapshot, null, false);
        var settings = new AppSettings
        {
            MetricDisplayMode = MetricDisplayMode.ImportantOnly,
            GlobalHotkeyEnabled = false,
        };

        using var popup = new UsagePopupForm(settings, 96);
        popup.StartPosition = FormStartPosition.Manual;
        popup.Location = new Point(-10_000, -10_000);
        popup.SetStates(new[] { status }, false, now, Array.Empty<UsageSample>());
        popup.Show();
        Application.DoEvents();
        var modeControl = GetPrivateField<ComboBox>(popup, "_metricMode");
        False(modeControl.Visible);
        var content = GetPrivateField<FlowLayoutPanel>(popup, "_content");
        var compactCard = content.Controls.OfType<ProviderUsageCard>().Single();
        Equal(6, GetPrivateField<IReadOnlyList<UsageMetric>>(compactCard, "_metrics").Count);

        popup.SetMode(DashboardMode.Full);
        True(modeControl.Visible);
        True(modeControl is ThemedComboBox);
        Equal(DrawMode.OwnerDrawFixed, modeControl.DrawMode);
        using (var pickerBitmap = new Bitmap(modeControl.Width, modeControl.Height))
        {
            modeControl.DrawToBitmap(pickerBitmap, modeControl.ClientRectangle);
            False(pickerBitmap.GetPixel(modeControl.Width - 4, 4).ToArgb() == Color.White.ToArgb());
        }

        InvokeProtected(modeControl, "OnMouseEnter", EventArgs.Empty);
        InvokeProtected(modeControl, "OnMouseLeave", EventArgs.Empty);
        InvokeProtected(modeControl, "OnGotFocus", EventArgs.Empty);
        InvokeProtected(modeControl, "OnLostFocus", EventArgs.Empty);
        InvokeProtected(modeControl, "OnDropDown", EventArgs.Empty);
        InvokeProtected(modeControl, "OnDropDownClosed", EventArgs.Empty);
        InvokeProtected(modeControl, "OnDpiChangedAfterParent", EventArgs.Empty);
        using (var itemBitmap = new Bitmap(180, 30))
        using (var itemGraphics = Graphics.FromImage(itemBitmap))
        {
            InvokeProtected(
                modeControl,
                "OnDrawItem",
                new DrawItemEventArgs(
                    itemGraphics,
                    modeControl.Font,
                    new Rectangle(0, 0, itemBitmap.Width, itemBitmap.Height),
                    0,
                    DrawItemState.Selected | DrawItemState.Focus));
            modeControl.Enabled = false;
            InvokeProtected(
                modeControl,
                "OnDrawItem",
                new DrawItemEventArgs(
                    itemGraphics,
                    modeControl.Font,
                    new Rectangle(0, 0, itemBitmap.Width, itemBitmap.Height),
                    0,
                    DrawItemState.ComboBoxEdit));
            InvokeProtected(
                modeControl,
                "OnDrawItem",
                new DrawItemEventArgs(
                    itemGraphics,
                    modeControl.Font,
                    Rectangle.Empty,
                    -1,
                    DrawItemState.None));
            modeControl.Enabled = true;
        }

        var importantCard = content.Controls.OfType<ProviderUsageCard>().Single();
        Equal(
            "Session highest|Rolling|Monthly",
            string.Join('|', GetPrivateField<IReadOnlyList<UsageMetric>>(importantCard, "_metrics").Select(metric => metric.Name)));
        Equal(6, snapshot.Metrics.Count);
        True(ReferenceEquals(metrics, snapshot.Metrics));

        modeControl.SelectedIndex = 1;
        Equal(MetricDisplayMode.MeteredOnly, settings.MetricDisplayMode);
        var meteredCard = content.Controls.OfType<ProviderUsageCard>().Single();
        Equal(
            "Session lower|Rolling|Session highest|Monthly",
            string.Join('|', GetPrivateField<IReadOnlyList<UsageMetric>>(meteredCard, "_metrics").Select(metric => metric.Name)));
        Equal(MetricDisplayMode.MeteredOnly, AppSettings.Load().MetricDisplayMode);

        popup.SetMode(DashboardMode.Compact);
        False(modeControl.Visible);
        Equal(6, GetPrivateField<IReadOnlyList<UsageMetric>>(
            content.Controls.OfType<ProviderUsageCard>().Single(), "_metrics").Count);

        var balanceSnapshot = snapshot with { Metrics = new[] { metrics[0] } };
        var balanceStatus = status with { Snapshot = balanceSnapshot };
        using var emptyCard = new ProviderUsageCard(
            balanceStatus,
            expanded: true,
            Array.Empty<UsageSample>(),
            showTrend: false,
            metrics: UsageMetricSelection.Select(balanceSnapshot.Metrics, MetricDisplayMode.MeteredOnly),
            dpiOverride: 96)
        {
            Width = 520,
        };
        Equal(0, GetPrivateField<IReadOnlyList<UsageMetric>>(emptyCard, "_metrics").Count);
        True(emptyCard.AccessibleDescription!.Contains("No metered limits reported", StringComparison.Ordinal));
        DrawControl(emptyCard);

        var settingsCopy = new AppSettings();
        using var dialog = new SettingsForm(
            settingsCopy,
            new[] { ("gemini", "Google Gemini") },
            _ => Task.FromResult(new UpdateCheckResult(true, null)),
            _ => Task.CompletedTask,
            96);
        var settingsMode = GetPrivateField<ComboBox>(dialog, "_metricDisplayMode");
        Equal(0, settingsMode.SelectedIndex);
        settingsMode.SelectedIndex = 2;
        InvokePrivate(dialog, "Apply");
        Equal(MetricDisplayMode.ImportantOnly, settingsCopy.MetricDisplayMode);

        AppPaths.WriteAllTextAtomic(
            AppPaths.SettingsFile,
            """{"RefreshIntervalMinutes":12,"MetricDisplayMode":999}""");
        var numericInvalid = AppSettings.Load();
        Equal(12, numericInvalid.RefreshIntervalMinutes);
        Equal(MetricDisplayMode.All, numericInvalid.MetricDisplayMode);
        AppPaths.WriteAllTextAtomic(
            AppPaths.SettingsFile,
            """{"RefreshIntervalMinutes":12,"MetricDisplayMode":"future"}""");
        var stringInvalid = AppSettings.Load();
        Equal(5, stringInvalid.RefreshIntervalMinutes);
        Equal(MetricDisplayMode.All, stringInvalid.MetricDisplayMode);
        popup.Hide();
        return Task.CompletedTask;
    }

    public static Task TestReleaseNotesBoundariesAsync()
    {
        var manyBullets = new StringBuilder("## [2.0.0] - 2026-09-15\n### Added\n");
        for (var index = 0; index < 40; index++)
        {
            manyBullets.AppendLine(CultureInfo.InvariantCulture, $"- Bullet {index}");
        }
        var cappedBullets = ReleaseNotes.Parse(manyBullets.ToString());
        Equal(32, cappedBullets.Single().Sections.Single().Bullets.Count);

        var manyVersions = new StringBuilder();
        for (var index = 0; index < 140; index++)
        {
            manyVersions.AppendLine(CultureInfo.InvariantCulture, $"## [{index + 1}.0.0] - 2026-09-15");
            manyVersions.AppendLine("### Fixed");
            manyVersions.AppendLine("- Bounded entry");
        }
        Equal(128, ReleaseNotes.Parse(manyVersions.ToString()).Count);
        Equal(0, ReleaseNotes.LoadBundled(typeof(string).Assembly).Count);
        Equal(0, ReleaseNotes.Parse("## [not-a-version]\n### Added\n- ignored").Count);
        Equal(0, ReleaseNotes.Parse("## [1.0.0] unexpected\n### Added\n- ignored").Count);

        var longBullet = ReleaseNotes.Parse(
            "## [1.0.0]\n### Added\n- " + new string('x', 2_000));
        Equal(1_000, longBullet.Single().Sections.Single().Bullets.Single().Length);

        var literal = ReleaseNotes.Parse("""
            ## [3.0.0] - 2026-09-15
            ### Security
            - Keep <b>literal HTML</b> and **Markdown** as text.
            """);
        Equal("2026-09-15", literal.Single().Date!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var summary = new WhatsNewSummary(literal, "3.0.0", 0);
        using (var rendered = new WhatsNewForm(summary, 96)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-10_000, -10_000),
        })
        {
            rendered.Show();
            Application.DoEvents();
            var document = GetPrivateField<RichTextBox>(rendered, "_content").Text;
            // Angle brackets are never markup here, so the HTML survives verbatim.
            True(document.Contains("<b>literal HTML</b>", StringComparison.Ordinal));
            True(document.Contains("SECURITY", StringComparison.Ordinal));
            True(document.Contains("UsageAI 3.0.0", StringComparison.Ordinal));
            // The strong delimiters become a bold face, so the word is left without them.
            True(document.Contains("and Markdown as text.", StringComparison.Ordinal));
            False(document.Contains("**", StringComparison.Ordinal));
            rendered.Hide();
        }

        Equal(0, ReleaseNotes.SplitInline(string.Empty).Count);
        var spans = ReleaseNotes.SplitInline("run `codex.cmd` then **stop**.");
        Equal(5, spans.Count);
        Equal(ReleaseNoteRunStyle.Normal, spans[0].Style);
        Equal("run ", spans[0].Text);
        Equal(ReleaseNoteRunStyle.Code, spans[1].Style);
        Equal("codex.cmd", spans[1].Text);
        Equal(ReleaseNoteRunStyle.Strong, spans[3].Style);
        Equal("stop", spans[3].Text);
        Equal(".", spans[4].Text);
        foreach (var unmatched in new[] { "unpaired ` tick", "empty `` and ****", "a * b ** c" })
        {
            var literalOnly = ReleaseNotes.SplitInline(unmatched);
            Equal(1, literalOnly.Count);
            Equal(unmatched, literalOnly[0].Text);
            Equal(ReleaseNoteRunStyle.Normal, literalOnly[0].Style);
        }

        var manySkipped = ReleaseNotes.ForUpgrade(
            ReleaseNotes.Parse(string.Join(
                Environment.NewLine,
                Enumerable.Range(1, 6).SelectMany(major => new[]
                {
                    $"## [{major}.0.0] - 2026-09-15",
                    "### Added",
                    $"- Entry {major}",
                }))),
            "6.0.0",
            new Version(1, 0, 0));
        Equal(3, manySkipped.Releases.Count);
        Equal(2, manySkipped.SkippedCount);
        True(manySkipped.HasOlderSkipped);

        var invalidCurrent = ReleaseNotes.ForUpgrade(literal, "invalid", previousVersion: null);
        Equal(0, invalidCurrent.Releases.Count);
        var invalidSettings = new AppSettings { LastRunVersion = "1.0.0" };
        Null(UpgradeNotice.Create(invalidSettings, true, "invalid", literal).Pending);
        Equal("1.0.0", invalidSettings.LastRunVersion);

        var longVersion = new AppSettings { LastRunVersion = new string('1', 64) };
        longVersion.Save();
        Equal(string.Empty, longVersion.LastRunVersion);
        var notice = UpgradeNotice.Create(new AppSettings(), true, "3.0.0", literal);
        NotNull(notice.Pending);
        notice.MarkDisplayed("3.0.0");
        notice.MarkDisplayed("4.0.0");
        Equal("3.0.0", AppSettings.Load().LastRunVersion);
        return Task.CompletedTask;
    }

    public static Task TestWhatsNewLifecycleAsync()
    {
        if (File.Exists(AppPaths.SettingsFile))
        {
            File.Delete(AppPaths.SettingsFile);
        }

        var now = DateTimeOffset.Now;
        var client = new QueueUsageClient("codex", "Codex");
        client.Enqueue(Snapshot("codex", "Codex", 25, now));
        var settings = new AppSettings { GlobalHotkeyEnabled = false };
        using var context = new UsageApplicationContext(
            new[] { client },
            settings,
            showTrayIcon: false,
            enableAutomaticUpdateChecks: false,
            profileExisted: true);
        Equal(0, Application.OpenForms.OfType<WhatsNewForm>().Count());
        Null(settings.LastRunVersion);

        var seen = new HashSet<Form>();
        using var closer = new System.Windows.Forms.Timer { Interval = 10 };
        closer.Tick += (_, _) =>
        {
            foreach (var form in Application.OpenForms.OfType<WhatsNewForm>().ToArray())
            {
                seen.Add(form);
                form.Close();
            }
        };
        closer.Start();
        InvokePrivate(context, "ToggleCompactPopup");
        closer.Stop();
        Equal(1, seen.Count);
        Equal(AppIdentity.Version, settings.LastRunVersion);
        Null(GetPrivateField<UpgradeNotice>(context, "_upgradeNotice").Pending);

        InvokePrivate(context, "ToggleCompactPopup");
        Equal(1, seen.Count);

        using (var dialog = new SettingsForm(settings, new[] { ("codex", "Codex") }))
        {
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(-10_000, -10_000);
            dialog.Show();
            Application.DoEvents();
            var whatsNew = Descendants(dialog).OfType<Button>().Single(button => button.Text == "What's new");
            closer.Start();
            whatsNew.PerformClick();
            closer.Stop();
            Equal(2, seen.Count);
            Equal(AppIdentity.Version, settings.LastRunVersion);
            dialog.Hide();
        }

        var cleanClient = new QueueUsageClient("codex", "Codex");
        cleanClient.Enqueue(Snapshot("codex", "Codex", 20, now));
        var cleanSettings = new AppSettings { GlobalHotkeyEnabled = false };
        using var cleanContext = new UsageApplicationContext(
            new[] { cleanClient },
            cleanSettings,
            showTrayIcon: false,
            enableAutomaticUpdateChecks: false,
            profileExisted: false);
        Null(GetPrivateField<UpgradeNotice>(cleanContext, "_upgradeNotice").Pending);
        Equal(AppIdentity.Version, cleanSettings.LastRunVersion);
        return Task.CompletedTask;
    }

    public static Task TestRuntimeHighContrastAsync()
    {
        var highContrast = false;
        Theme.SetHighContrastProbe(() => highContrast);
        try
        {
            Theme.Apply(ThemeMode.Light, 72, 90);
            var settings = new AppSettings { GlobalHotkeyEnabled = false };
            using var popup = new UsagePopupForm(settings, 96);
            using var dialog = new SettingsForm(settings, new[] { ("codex", "Codex") });
            using var whatsNew = new WhatsNewForm(
                new WhatsNewSummary(Array.Empty<ReleaseNotesVersion>(), AppIdentity.Version, 0),
                96);

            highContrast = true;
            Theme.Reapply(ThemeMode.Light);
            True(Theme.IsHighContrast);
            Equal(SystemColors.Window, popup.BackColor);
            Equal(SystemColors.Window, dialog.BackColor);
            Equal(SystemColors.Window, whatsNew.BackColor);
            Equal(SystemColors.Control, GetPrivateField<ComboBox>(dialog, "_theme").BackColor);
            Equal(SystemColors.Window, GetPrivateField<RichTextBox>(whatsNew, "_content").BackColor);
            Equal(SystemColors.Highlight, GetPrivateField<Button>(whatsNew, "_close").BackColor);
            Equal(SystemColors.HighlightText, GetPrivateField<Button>(whatsNew, "_close").ForeColor);
            Equal(SystemColors.WindowText, Theme.ForUsage(95));
            Equal(SystemColors.WindowText, Theme.ForProvider("gemini"));
            Equal(Color.Red, Theme.Blend(Color.Red, Color.Blue, 0.5));

            using var icon = TrayIconFactory.Create(92, "G", size: 24, identityColor: Theme.Gemini);
            using var iconBitmap = icon.ToBitmap();
            True(iconBitmap.Width == 24 && iconBitmap.Height == 24);
            DrawControl(popup);
            DrawControl(dialog);
            DrawControl(whatsNew);

            var contextClient = new QueueUsageClient("codex", "Codex");
            contextClient.Enqueue(Snapshot("codex", "Codex", 20, DateTimeOffset.Now));
            using var context = new UsageApplicationContext(
                new[] { contextClient },
                new AppSettings { GlobalHotkeyEnabled = false, LastRunVersion = AppIdentity.Version },
                showTrayIcon: false,
                enableAutomaticUpdateChecks: false,
                profileExisted: true);
            var menu = GetPrivateField<ContextMenuStrip>(context, "_menu");
            True(menu.Renderer is ToolStripSystemRenderer);

            highContrast = false;
            InvokePrivate(
                context,
                "OnUserPreferenceChanged",
                new object(),
                new UserPreferenceChangedEventArgs(UserPreferenceCategory.Accessibility));
            False(Theme.IsHighContrast);
            True(menu.Renderer is ToolStripProfessionalRenderer);
            Equal(Theme.Night, popup.BackColor);
            Equal(Theme.Night, dialog.BackColor);
            Equal(Theme.Night, whatsNew.BackColor);
        }
        finally
        {
            Theme.SetHighContrastProbe(null);
            Theme.Apply(ThemeMode.Dark, 72, 90);
        }
        return Task.CompletedTask;
    }

    public static Task TestExtremeDpiGeometryAsync()
    {
        Equal(96, InvokePrivateStatic<int>(
            typeof(PreviewRenderer),
            "ResolveDpi",
            (object)PreviewDpi96Args));
        Equal(192, InvokePrivateStatic<int>(
            typeof(PreviewRenderer),
            "ResolveDpi",
            (object)PreviewDpi192Args));
        Equal(288, InvokePrivateStatic<int>(
            typeof(PreviewRenderer),
            "ResolveDpi",
            (object)PreviewDpi288Args));
        Equal(192, InvokePrivateStatic<int>(
            typeof(PreviewRenderer),
            "ResolveDpi",
            (object)PreviewDpiInvalidArgs));

        var now = DateTimeOffset.Now;
        var snapshot = new UsageSnapshot(
            "A long representative provider plan",
            new UsageMetric[]
            {
                new("Session limit with a long label", UsageMetricKind.Session, 85, now.AddHours(2)),
                new("Monthly", UsageMetricKind.Monthly, 45, now.AddDays(10)),
            },
            now,
            "codex",
            "Codex");
        var status = new ProviderStatus("codex", "Codex", snapshot, null, false);
        var previousCardHeight = 0;

        foreach (var dpi in new[] { 96, 192, 288 })
        {
            var scale = new LayoutScale(dpi);
            using var card = new ProviderUsageCard(
                status,
                expanded: true,
                Array.Empty<UsageSample>(),
                showTrend: false,
                dpiOverride: dpi)
            {
                Width = scale[520],
            };
            Equal(scale[46] + scale[8] + 2 * scale[54], card.NaturalHeight);
            True(card.NaturalHeight > previousCardHeight);
            previousCardHeight = card.NaturalHeight;
            DrawControl(card);

            var disconnected = new ProviderStatus(
                "claude",
                "Claude Code",
                null,
                "Sign in required",
                false,
                SignInCommand: "claude");
            using var actionCard = new ProviderUsageCard(
                disconnected,
                expanded: true,
                Array.Empty<UsageSample>(),
                showTrend: false,
                dpiOverride: dpi)
            {
                Width = scale[520],
            };
            DrawControl(actionCard);
            var actionBounds = GetPrivateField<Rectangle>(actionCard, "_actionBounds");
            True(actionBounds.Width > 0 && actionBounds.Height > 0);
            True(actionCard.ClientRectangle.Contains(actionBounds));
            Equal(
                ProviderCardAction.CopyCommand,
                InvokePrivate<ProviderCardAction>(actionCard, "HitTest", new Point(
                    actionBounds.Left + actionBounds.Width / 2,
                    actionBounds.Top + actionBounds.Height / 2)));

            using var popup = new UsagePopupForm(new AppSettings(), dpi)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-10_000, -10_000),
            };
            popup.SetMode(DashboardMode.Full);
            popup.SetStates(new[] { status }, false, now, Array.Empty<UsageSample>());
            var popupArea = new Rectangle(0, 0, 1024, 768);
            InvokePrivate(popup, "ShowDashboard", popupArea, false);
            Application.DoEvents();
            True(popup.Width <= popupArea.Width && popup.Height <= popupArea.Height);
            AssertVisibleActionsInBounds(popup);
            DrawControl(popup);
            popup.Hide();

            using var settings = new SettingsForm(
                new AppSettings(),
                new[] { ("codex", "Codex"), ("gemini", "Google Gemini") },
                _ => Task.FromResult(new UpdateCheckResult(true, null)),
                _ => Task.CompletedTask,
                dpi)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-10_000, -10_000),
            };
            settings.Show();
            Application.DoEvents();
            var settingsArea = Screen.FromControl(settings).WorkingArea;
            True(settings.Width <= settingsArea.Width && settings.Height <= settingsArea.Height);
            var settingsPadding = GetPrivateField<TableLayoutPanel>(settings, "_layout").Padding;
            var settingsSize = settings.ClientSize;
            InvokePrivate(settings, "ApplyScaledLayout");
            Equal(settingsPadding, GetPrivateField<TableLayoutPanel>(settings, "_layout").Padding);
            Equal(settingsSize, settings.ClientSize);
            AssertVisibleActionsInBounds(settings);
            var scrollHost = GetPrivateField<Panel>(settings, "_scrollHost");
            True(scrollHost.DisplayRectangle.Height >= scrollHost.ClientSize.Height);
            DrawControl(settings);
            settings.Hide();

            using var notes = new WhatsNewForm(
                new WhatsNewSummary(Array.Empty<ReleaseNotesVersion>(), "9.9.9", 0),
                dpi)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-10_000, -10_000),
            };
            notes.Show();
            Application.DoEvents();
            var notesArea = Screen.FromControl(notes).WorkingArea;
            True(notes.Width <= notesArea.Width && notes.Height <= notesArea.Height);
            var notesPadding = GetPrivateField<TableLayoutPanel>(notes, "_shell").Padding;
            var notesSize = notes.ClientSize;
            InvokePrivate(notes, "ApplyScaledLayout");
            Equal(notesPadding, GetPrivateField<TableLayoutPanel>(notes, "_shell").Padding);
            Equal(notesSize, notes.ClientSize);
            AssertVisibleActionsInBounds(notes);
            DrawControl(notes);
            notes.Hide();
        }

        return Task.CompletedTask;
    }

    private static void AssertVisibleActionsInBounds(Control root)
    {
        foreach (var control in Descendants(root).Where(control => control.Visible && control is Button or ComboBox))
        {
            True(control.Width > 0 && control.Height > 0);
            True(control.Left >= 0 && control.Top >= 0);
            True(control.Right <= control.Parent!.ClientSize.Width + control.Parent.Padding.Right);
            True(control.Bottom <= control.Parent.ClientSize.Height + control.Parent.Padding.Bottom);
        }
    }

    private static void DrawControl(Control control)
    {
        if (control.Width <= 0)
        {
            control.Width = 300;
        }

        if (control.Height <= 0)
        {
            control.Height = 200;
        }

        control.CreateControl();
        using var bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static void InvokeProtected(Control control, string name, EventArgs eventArgs)
    {
        var method = control.GetType().GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing method {name}.");
        method.Invoke(control, new object[] { eventArgs });
    }

    private static object? InvokePrivate(object target, string name, params object?[] arguments)
    {
        var methods = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(method => method.Name == name && method.GetParameters().Length == arguments.Length)
            .ToArray();
        var method = methods.Length == 1
            ? methods[0]
            : throw new InvalidOperationException(
                $"Expected one {name} overload with {arguments.Length} parameters, found {methods.Length}.");
        try
        {
            return method.Invoke(target, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static T InvokePrivate<T>(object target, string name, params object?[] arguments) =>
        (T)(InvokePrivate(target, name, arguments)
            ?? throw new InvalidOperationException($"{name} returned null."));

    private static T InvokePrivateStatic<T>(
        Type type,
        string name,
        params object?[] arguments)
    {
        var methods = type
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(method => method.Name == name && method.GetParameters().Length == arguments.Length)
            .ToArray();
        var method = methods.Length == 1
            ? methods[0]
            : throw new InvalidOperationException(
                $"Expected one static {name} overload with {arguments.Length} parameters, found {methods.Length}.");
        try
        {
            var result = method.Invoke(null, arguments);
            return result is null ? default! : (T)result;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static async Task InvokePrivateTaskAsync(
        object target,
        string name,
        params object?[] arguments)
    {
        var result = InvokePrivate(target, name, arguments);
        if (result is not Task task)
        {
            throw new InvalidOperationException($"{name} did not return a Task.");
        }

        await task;
    }

    private static async Task<T> InvokePrivateTaskResultAsync<T>(
        object target,
        string name,
        params object?[] arguments)
    {
        var result = InvokePrivate(target, name, arguments);
        if (result is not Task task)
        {
            throw new InvalidOperationException($"{name} did not return a Task.");
        }

        await task;
        var resultProperty = task.GetType().GetProperty("Result")
            ?? throw new InvalidOperationException($"{name} returned no result.");
        return (T)resultProperty.GetValue(task)!;
    }

    private static async Task<T> InvokePrivateStaticTaskResultAsync<T>(
        Type type,
        string name,
        params object?[] arguments)
    {
        var result = InvokePrivateStatic<object?>(type, name, arguments);
        if (result is not Task task)
        {
            throw new InvalidOperationException($"{name} did not return a Task.");
        }

        await task;
        var resultProperty = task.GetType().GetProperty("Result")
            ?? throw new InvalidOperationException($"{name} returned no result.");
        return (T)resultProperty.GetValue(task)!;
    }

    private static T GetPrivateField<T>(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing field {name}.");
        return (T)(field.GetValue(target)
            ?? throw new InvalidOperationException($"Field {name} is null."));
    }

    private static T GetPrivateFieldValue<T>(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing field {name}.");
        return (T)field.GetValue(target)!;
    }

    private static T GetProperty<T>(object target, string name)
    {
        var property = target.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing property {name}.");
        return (T)(property.GetValue(target)
            ?? throw new InvalidOperationException($"Property {name} is null."));
    }

    private static void SetPrivateField<T>(object target, string name, T value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing field {name}.");
        field.SetValue(target, value);
    }

    private static void InvokeWindowMessage(MessageWindow window, int messageId, IntPtr wParam)
    {
        var message = Message.Create(window.Handle, messageId, wParam, IntPtr.Zero);
        var method = typeof(MessageWindow).GetMethod(
            "WndProc",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MessageWindow.WndProc was not found.");
        var arguments = new object[] { message };
        method.Invoke(window, arguments);
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunProcessAsync(
        string executable,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {executable}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return (process.ExitCode, await output, await error);
    }

    /// <summary>A provider that stays in flight until the test lets it finish.</summary>
    private sealed class GatedUsageClient : IUsageClient
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly UsageSnapshot _snapshot;

        public GatedUsageClient(string id, string displayName, UsageSnapshot snapshot)
        {
            Id = id;
            DisplayName = displayName;
            _snapshot = snapshot;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public string SignInCommand => $"{Id} login";

        public Uri AccountUrl { get; } = new("https://example.com/account");

        public void Release() => _release.TrySetResult();

        public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
        {
            await _release.Task.WaitAsync(cancellationToken);
            return _snapshot;
        }
    }

    private sealed class QueueUsageClient : IUsageClient
    {
        private readonly Queue<object> _results = new();

        public QueueUsageClient(string id, string displayName)
        {
            Id = id;
            DisplayName = displayName;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public string SignInCommand => $"{Id} login";

        public Uri AccountUrl { get; } = new("https://example.com/account");

        public int CallCount { get; private set; }

        public void Enqueue(UsageSnapshot snapshot) => _results.Enqueue(snapshot);

        public void Enqueue(Exception exception) => _results.Enqueue(exception);

        public Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            if (_results.Count == 0)
            {
                throw new InvalidOperationException($"No queued result for {Id}.");
            }

            var result = _results.Dequeue();
            return result is Exception exception
                ? Task.FromException<UsageSnapshot>(exception)
                : Task.FromResult((UsageSnapshot)result);
        }
    }

    private sealed class BlockingUsageClient : IUsageClient
    {
        private readonly TaskCompletionSource<UsageSnapshot> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingUsageClient(string id, string displayName)
        {
            Id = id;
            DisplayName = displayName;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public string SignInCommand => "blocking login";

        public Uri AccountUrl { get; } = new("https://example.com/blocking");

        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            Started.TrySetResult(true);
            cancellationToken.Register(() => _completion.TrySetCanceled(cancellationToken));
            return _completion.Task;
        }

        public void Complete(UsageSnapshot snapshot) => _completion.TrySetResult(snapshot);
    }

    private sealed class CancellingUsageClient : IUsageClient
    {
        public string Id => "cancelling";

        public string DisplayName => "Cancelling";

        public string SignInCommand => "cancel login";

        public Uri AccountUrl { get; } = new("https://example.com/cancel");

        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation should end the delay.");
        }
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> _send;

        public StubHttpHandler(
            Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> send) =>
            _send = send;

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return _send(request, CallCount, cancellationToken);
        }
    }

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) =>
            callback(state);
    }

    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected UI condition was not reached.");
            }

            Application.DoEvents();
            await Task.Delay(10);
        }
    }

    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void True(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    private static void False(bool value) => True(!value);

    private static void Null(object? value)
    {
        if (value is not null)
        {
            throw new InvalidOperationException($"Expected null, got '{value}'.");
        }
    }

    private static void NotNull(object? value)
    {
        if (value is null)
        {
            throw new InvalidOperationException("Expected a value.");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }
}
