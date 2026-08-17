using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
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
                var exception = await ThrowsAsync<ClaudeCodeUsageException>(
                    () => new ClaudeCodeUsageClient(http).GetUsageAsync());
                True(exception.Message.Contains("expired", StringComparison.OrdinalIgnoreCase));
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
            var release = await UpdateChecker.FindNewerReleaseAsync(http, CancellationToken.None);
            NotNull(release);
            Equal("v99.0.0", release!.Tag);
            Equal("99.0.0", release.Version);
            Equal("UsageAI-99.0.0-Setup.exe", release.Installer!.Name);
            Equal("UsageAI-99.0.0-Setup.exe.sha256", release.Checksum!.Name);
        }

        using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                   Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"tag_name":"v0.1.0"}""")))))
        {
            Null(await UpdateChecker.FindNewerReleaseAsync(http, CancellationToken.None));
        }

        using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                   Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"other":"value"}""")))))
        {
            Null(await UpdateChecker.FindNewerReleaseAsync(http, CancellationToken.None));
        }

        using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                   Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))))
        {
            Null(await UpdateChecker.FindNewerReleaseAsync(http, CancellationToken.None));
        }

        using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                   Task.FromResult(JsonResponse(HttpStatusCode.OK, "{invalid")))))
        {
            Null(await UpdateChecker.FindNewerReleaseAsync(http, CancellationToken.None));
        }

        using (var http = new HttpClient(new StubHttpHandler((_, _, _) =>
                   throw new HttpRequestException("synthetic failure"))))
        {
            Null(await UpdateChecker.FindNewerReleaseAsync(http, CancellationToken.None));
        }

        True(UpdateChecker.IsNewer("6-beta.1", "5.9.0"));
        False(UpdateChecker.IsNewer("5+build", "5.0"));
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
            var fullPath = Path.Combine(directory, "full.png");
            PreviewRenderer.Render(new[] { "--render-preview", compactPath });
            PreviewRenderer.Render(new[] { "--render-preview", fullPath, "--full" });
            True(new FileInfo(compactPath).Length > 1_000);
            True(new FileInfo(fullPath).Length > 1_000);

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

    public static Task TestRemainingUiBranchesAsync()
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

        Theme.Apply(ThemeMode.Dark, 72, 90);
        return Task.CompletedTask;
    }

    public static Task TestStalePresentationEdgesAsync()
    {
        var now = new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
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
