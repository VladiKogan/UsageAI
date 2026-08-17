using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using UsageAI.Models;
using UsageAI.Services;
using UsageAI.UI;

namespace UsageAI.Tests;

internal static class Program
{
    private static readonly string[] ProviderIds = { "codex", "claude", "copilot", "gemini" };

    private static readonly (string Name, Func<Task> Run)[] Tests =
    {
        ("credential input validation", TestCredentialInputAsync),
        ("minimal child environment", TestMinimalChildEnvironmentAsync),
        ("bounded CLI output", TestBoundedCliOutputAsync),
        ("bounded HTTP JSON", TestBoundedHttpJsonAsync),
        ("bounded credential files", TestBoundedCredentialFileAsync),
        ("Claude session-key normalization", TestClaudeSessionKeyAsync),
        ("Claude organization parsing", TestClaudeOrganizationParsingAsync),
        ("Gemini credential ACL and EFS preservation", TestGeminiCredentialAclPreservationAsync),
        ("Gemini credential lost-update protection", TestGeminiCredentialLostUpdateAsync),
        ("cross-process refresh lock", TestCrossProcessLockAsync),
        ("Codex snapshot parsing", TestCodexSnapshotParsingAsync),
        ("Claude snapshot parsing", TestClaudeSnapshotParsingAsync),
        ("Copilot keeps every reported quota", TestCopilotSnapshotParsingAsync),
        ("Gemini snapshot parsing", TestGeminiSnapshotParsingAsync),
        ("official agy output and fallback order", TestAgyFallbackAsync),
        ("Gemini JWT claims extraction", TestGeminiJwtExtractionAsync),
        ("Antigravity candidates stay PID-bound", TestAntigravityCandidateBindingAsync),
        ("settings validation and round trip", TestSettingsAsync),
        ("settings content clears the fixed footer", TestSettingsScrollLayoutAsync),
        ("provider order and visibility", TestProviderOrderAsync),
        ("tray provider selection, tooltip, and empty icon", TestTrayProviderSelectionAsync),
        ("usage history round trip", TestUsageHistoryAsync),
        ("snapshot cache round trip", TestSnapshotCacheAsync),
        ("burn-rate projection", TestForecastAsync),
        ("projection ignores a window reset", TestForecastResetAsync),
        ("alert thresholds and debouncing", TestNotificationsAsync),
        ("release version comparison", TestUpdateComparisonAsync),
        ("Primary metric prioritization in tray and header", TestPrimaryMetricTrayTooltipAsync),
        ("Theme usage fill colors and tray pie icon", TestThemeUsageColorsAsync),
        ("model formatting and provider status states", CoverageExpansionTests.TestModelFormattingAsync),
        ("refresh orchestration, alerts, history, and backoff", CoverageExpansionTests.TestRefreshOrchestrationAsync),
        ("scheduled stale-provider recovery", CoverageExpansionTests.TestScheduledStaleRecoveryAsync),
        ("scheduler advances with no visible providers", CoverageExpansionTests.TestNoVisibleProviderScheduleAsync),
        ("refresh concurrency and shutdown cancellation", CoverageExpansionTests.TestRefreshConcurrencyAsync),
        ("Claude HTTP success and error handling", CoverageExpansionTests.TestClaudeHttpAsync),
        ("Claude delegates refresh ownership to its CLI", CoverageExpansionTests.TestClaudeCredentialsAreReadOnlyAsync),
        ("Copilot HTTP success and error handling", CoverageExpansionTests.TestCopilotHttpAsync),
        ("Gemini OAuth HTTP success and error handling", CoverageExpansionTests.TestGeminiHttpAsync),
        ("Codex app-server protocol and errors", CoverageExpansionTests.TestCodexProtocolAsync),
        ("Claude web fallback HTTP flows", CoverageExpansionTests.TestClaudeWebHttpAsync),
        ("release update HTTP handling", CoverageExpansionTests.TestUpdateCheckerHttpAsync),
        ("verified update installer download", CoverageExpansionTests.TestUpdateInstallerAsync),
        ("update installer rejects unsafe and malformed assets", CoverageExpansionTests.TestUpdateInstallerFailuresAsync),
        ("provider parser edge cases", CoverageExpansionTests.TestProviderParserEdgesAsync),
        ("corrupt local state recovery", CoverageExpansionTests.TestCorruptLocalStateAsync),
        ("security utility edge cases", CoverageExpansionTests.TestSecurityUtilityEdgesAsync),
        ("custom UI rendering paths", CoverageExpansionTests.TestUiRenderingAsync),
        ("application context lifecycle and tray updates", CoverageExpansionTests.TestApplicationContextAsync),
        ("preview and command-line entry points", CoverageExpansionTests.TestPreviewAndEntryPointsAsync),
        ("provider credential refresh and discovery branches", CoverageExpansionTests.TestProviderCredentialBranchesAsync),
        ("Gemini local probe and fallback branches", CoverageExpansionTests.TestGeminiDeepBranchesAsync),
        ("remaining UI interaction branches", CoverageExpansionTests.TestRemainingUiBranchesAsync),
        ("stale and retry presentation edge cases", CoverageExpansionTests.TestStalePresentationEdgesAsync),
        ("bounded stream branches", CoverageExpansionTests.TestLimitedReadStreamBranchesAsync),
        ("credential file and decoding branches", CoverageExpansionTests.TestCredentialFileBranchesAsync),
        ("process and message-window branches", CoverageExpansionTests.TestProcessAndMessageBranchesAsync),
        ("startup registration integration", WindowsIntegrationTests.TestStartupRegistrationAsync),
        ("single-instance window-message contract", WindowsIntegrationTests.TestSingleInstanceMessageAsync),
        ("Windows Credential Manager integration", WindowsIntegrationTests.TestCredentialManagerAsync),
        ("installer elevation launch contract", WindowsIntegrationTests.TestInstallerLaunchAsync),
    };

    private static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "app-server")
        {
            return await RunFakeCodexAppServerAsync();
        }

        if (args.Length >= 2 &&
            args[0] == "auth" &&
            args[1] == "status")
        {
            var configDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(configDirectory) || !Path.IsPathFullyQualified(configDirectory))
            {
                await Console.Out.WriteLineAsync("{\"loggedIn\":false}");
                return 1;
            }

            var credentialPath = Path.Combine(configDirectory, ".credentials.json");
            var refreshed = $$"""
                {
                  "claudeAiOauth":{
                    "accessToken":"owner-refreshed-access",
                    "refreshToken":"owner-refreshed-token",
                    "expiresAt":{{DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeMilliseconds()}},
                    "scopes":["user:profile"],
                    "subscriptionType":"pro"
                  }
                }
                """;
            await File.WriteAllTextAsync(credentialPath, refreshed);
            await Console.Out.WriteLineAsync("{\"loggedIn\":true,\"authMethod\":\"claude.ai\"}");
            return 0;
        }

        if (args.Length >= 2 &&
            args[0] == "auth" &&
            args[1] == "token")
        {
            var fixture = Environment.GetEnvironmentVariable("GH_CONFIG_DIR");
            if (fixture == "usageai-test-gh-failure")
            {
                await Console.Error.WriteLineAsync("synthetic gh failure");
                return 1;
            }

            await Console.Out.WriteLineAsync(fixture ?? string.Empty);
            return 0;
        }

        // Keep every file-touching test inside a scratch directory instead of the real profile.
        Environment.SetEnvironmentVariable(
            "USAGEAI_DATA_DIR",
            Path.Combine(Path.GetTempPath(), "UsageAI.SecurityTests", $"data-{Guid.NewGuid():N}"));

        var selectedTests = Tests.AsEnumerable();
        var filterIndex = Array.IndexOf(args, "--filter");
        if (filterIndex >= 0)
        {
            if (filterIndex + 1 >= args.Length || string.IsNullOrWhiteSpace(args[filterIndex + 1]))
            {
                Console.Error.WriteLine("--filter requires part of a registered check name.");
                return 2;
            }

            var filter = args[filterIndex + 1];
            selectedTests = selectedTests.Where(test =>
                test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        var testsToRun = selectedTests.ToArray();
        if (testsToRun.Length == 0)
        {
            Console.Error.WriteLine("No registered checks matched the requested filter.");
            return 2;
        }

        var failures = 0;
        foreach (var (name, run) in testsToRun)
        {
            try
            {
                await run();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        TryRemoveDataDirectory();
        Console.WriteLine($"{testsToRun.Length - failures}/{testsToRun.Length} checks passed.");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> RunFakeCodexAppServerAsync()
    {
        var scenario = Environment.GetEnvironmentVariable("CODEX_HOME") ?? "success";
        if (scenario.EndsWith("premature-exit", StringComparison.Ordinal))
        {
            return 0;
        }

        if (await Console.In.ReadLineAsync() is null)
        {
            return 2;
        }

        await Console.Out.WriteLineAsync("not-json");
        await Console.Out.WriteLineAsync("""{"id":"1","result":{}}""");
        await Console.Out.FlushAsync();

        // The initialized notification and rate-limit request are separate protocol lines.
        if (await Console.In.ReadLineAsync() is null ||
            await Console.In.ReadLineAsync() is null)
        {
            return 3;
        }

        if (scenario.EndsWith("auth-error", StringComparison.Ordinal))
        {
            await Console.Error.WriteLineAsync("authentication login required");
            await Console.Error.FlushAsync();
            await Console.Out.WriteLineAsync(
                """{"id":2,"error":{"message":"authentication required"}}""");
        }
        else if (scenario.EndsWith("missing-result", StringComparison.Ordinal))
        {
            await Console.Out.WriteLineAsync("""{"id":99,"result":{}}""");
            await Console.Out.WriteLineAsync("""{"id":2}""");
        }
        else if (scenario.EndsWith("generic-error", StringComparison.Ordinal))
        {
            await Console.Out.WriteLineAsync(
                """{"id":2,"error":{"message":"server unavailable"}}""");
        }
        else if (scenario.EndsWith("too-many", StringComparison.Ordinal))
        {
            for (var index = 0; index < 512; index++)
            {
                await Console.Out.WriteLineAsync(
                    $"{{\"id\":{index + 1000},\"result\":{{}}}}");
            }
        }
        else
        {
            await Console.Out.WriteLineAsync("""{"method":"server/notice","params":{}}""");
            await Console.Out.WriteLineAsync(
                """
                {"id":2,"result":{"rateLimitsByLimitId":{"codex":{"planType":"pro","primary":{"usedPercent":44,"windowDurationMins":300},"secondary":{"usedPercent":61,"windowDurationMins":10080}}}}}
                """);
        }

        await Console.Out.FlushAsync();
        return 0;
    }

    private static Task TestCredentialInputAsync()
    {
        Equal("safe-token", CredentialInput.NormalizeToken("  safe-token  "));
        Null(CredentialInput.NormalizeToken("token\r\nInjected: value"));
        Null(CredentialInput.NormalizeToken("token\tvalue"));
        Null(CredentialInput.NormalizeToken("token value"));
        Null(CredentialInput.NormalizeToken("token\u007fvalue"));
        Null(CredentialInput.NormalizeToken(new string('x', CredentialInput.MaxTokenCharacters + 1)));
        return Task.CompletedTask;
    }

    private static Task TestMinimalChildEnvironmentAsync()
    {
        const string secretName = "USAGEAI_SECURITY_TEST_SECRET";
        var original = Environment.GetEnvironmentVariable(secretName);
        try
        {
            Environment.SetEnvironmentVariable(secretName, "must-not-be-inherited");
            var startInfo = new ProcessStartInfo { UseShellExecute = false };
            ProcessSecurity.ApplyMinimalEnvironment(startInfo);

            False(startInfo.Environment.ContainsKey(secretName));
            Equal("1", startInfo.Environment["NO_COLOR"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretName, original);
        }

        return Task.CompletedTask;
    }

    private static async Task TestBoundedCliOutputAsync()
    {
        using var validReader = new StringReader("message\r\nnext");
        Equal("message", await ProcessSecurity.ReadBoundedLineAsync(validReader, 16, CancellationToken.None));

        using var oversizedReader = new StringReader(new string('x', 17));
        await ThrowsAsync<InvalidDataException>(() =>
            ProcessSecurity.ReadBoundedLineAsync(oversizedReader, 16, CancellationToken.None));
    }

    private static async Task TestBoundedHttpJsonAsync()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[SecureHttp.MaxJsonResponseBytes + 1]),
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        await ThrowsAsync<InvalidDataException>(() =>
            SecureHttp.ReadJsonDocumentAsync(response, CancellationToken.None));
    }

    private static Task TestBoundedCredentialFileAsync()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, "large.json");
            File.WriteAllText(path, new string('x', 64));
            Throws<InvalidDataException>(() => SecureLocalFile.ReadAllText(path, maxCharacters: 32));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }

        return Task.CompletedTask;
    }

    private static Task TestClaudeSessionKeyAsync()
    {
        Equal("sk-ant-session", ClaudeWebUsageClient.NormalizeSessionKey("sessionKey=sk-ant-session"));
        Equal("sk-ant-session", ClaudeWebUsageClient.NormalizeSessionKey(" sk-ant-session "));
        Null(ClaudeWebUsageClient.NormalizeSessionKey("sk-ant-session; other=value"));
        Null(ClaudeWebUsageClient.NormalizeSessionKey("sk-ant-session\r\nInjected: value"));
        return Task.CompletedTask;
    }

    private static Task TestClaudeOrganizationParsingAsync()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "memberships": [
                { "uuid": "membership-id", "organization": { "uuid": "organization-id" } }
              ]
            }
            """);
        Equal("organization-id", ClaudeWebUsageClient.FindOrganizationId(document.RootElement));
        return Task.CompletedTask;
    }

    private static Task TestGeminiCredentialAclPreservationAsync()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, "oauth_creds.json");
            File.WriteAllText(path, GeminiCredentialJson("old-access", "refresh-token"));

            var sid = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("The current Windows SID is unavailable.");
            var security = new FileSecurity();
            security.SetOwner(sid);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);

            var efsEnabled = false;
            try
            {
                File.Encrypt(path);
                efsEnabled =
                    (File.GetAttributes(path) & FileAttributes.Encrypted) != 0;
            }
            catch (Exception exception) when (exception is IOException
                                                  or UnauthorizedAccessException
                                                  or PlatformNotSupportedException
                                                  or CryptographicException)
            {
                // EFS is optional on Windows editions and filesystems.
            }

            const AccessControlSections comparedSections =
                AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
            var before = new FileInfo(path).GetAccessControl(comparedSections)
                .GetSecurityDescriptorSddlForm(comparedSections);

            GeminiUsageClient.TryPersistRefreshedCredentials(
                new GeminiUsageClient.GeminiCredentials(
                    "new-access",
                    "refresh-token",
                    "new-id-token",
                    DateTimeOffset.UtcNow.AddHours(1),
                    path));

            var after = new FileInfo(path).GetAccessControl(comparedSections)
                .GetSecurityDescriptorSddlForm(comparedSections);
            Equal(before, after);
            using var updated = JsonDocument.Parse(File.ReadAllText(path));
            Equal("new-access", updated.RootElement.GetProperty("access_token").GetString());
            Equal("new-id-token", updated.RootElement.GetProperty("id_token").GetString());
            Equal("keep-this-value", updated.RootElement.GetProperty("custom").GetString());
            Equal(0, Directory.GetFiles(directory, ".oauth_creds.json.usageai-tmp.*").Length);
            if (efsEnabled)
            {
                True((File.GetAttributes(path) & FileAttributes.Encrypted) != 0);
            }

            Console.WriteLine($"INFO Gemini EFS preservation exercised: {efsEnabled}");
        }
        finally
        {
            DeleteTestDirectory(directory);
        }

        return Task.CompletedTask;
    }

    private static Task TestGeminiCredentialLostUpdateAsync()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, "oauth_creds.json");
            var current = GeminiCredentialJson("other-access", "other-refresh");
            File.WriteAllText(path, current);

            GeminiUsageClient.TryPersistRefreshedCredentials(
                new GeminiUsageClient.GeminiCredentials(
                    "new-access",
                    "stale-refresh",
                    "new-id-token",
                    DateTimeOffset.UtcNow.AddHours(1),
                    path));

            Equal(current, File.ReadAllText(path));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }

        return Task.CompletedTask;
    }

    private static async Task TestCrossProcessLockAsync()
    {
        var directory = CreateTestDirectory();
        try
        {
            var name = $"security-test-{Guid.NewGuid():N}";
            await using (var first = await CrossProcessFileLock.AcquireAsync(
                             name,
                             TimeSpan.FromSeconds(1),
                             CancellationToken.None,
                             directory))
            {
                await ThrowsAsync<TimeoutException>(() => CrossProcessFileLock.AcquireAsync(
                    name,
                    TimeSpan.FromMilliseconds(250),
                    CancellationToken.None,
                    directory));
            }
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    private static Task TestCodexSnapshotParsingAsync()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "rateLimitsByLimitId": {
                "codex": {
                  "planType": "plus",
                  "primary": { "usedPercent": 43, "windowDurationMins": 300 },
                  "secondary": { "usedPercent": 63, "windowDurationMins": 10080 },
                  "credits": { "balance": "$12.50" }
                }
              },
              "rateLimitResetCredits": { "availableCount": 3 }
            }
            """);

        var snapshot = CodexUsageClient.ParseSnapshot(document.RootElement);
        Equal("Plus", snapshot.Plan);
        Equal(4, snapshot.Metrics.Count);
        Equal("5-hour", snapshot.Metrics[0].Name);
        Equal(UsageMetricKind.Session, snapshot.Metrics[0].Kind);
        Equal(43, snapshot.Metrics[0].UsedPercent);
        Equal(57, snapshot.Metrics[0].RemainingPercent);
        Equal("43% USED", snapshot.Metrics[0].DisplayUsed);
        Equal("57% LEFT", snapshot.Metrics[0].DisplaySecondary);
        Equal("Weekly", snapshot.Metrics[1].Name);
        Equal(UsageMetricKind.Rolling, snapshot.Metrics[1].Kind);
        Equal("Credits", snapshot.Metrics[2].Name);
        Equal("$12.50", snapshot.Metrics[2].DisplayRemaining);
        Equal("$12.50", snapshot.Metrics[2].DisplayUsed);
        False(snapshot.Metrics[2].HasQuota);
        Equal("Reset credits", snapshot.Metrics[3].Name);
        Equal(63, snapshot.HighestUsedPercent);
        return Task.CompletedTask;
    }

    private static Task TestClaudeSnapshotParsingAsync()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "five_hour": { "utilization": 22, "resets_at": "2026-07-27T21:00:00Z" },
              "seven_day": { "utilization": 0.49, "resets_at": "2026-07-31T09:00:00Z" },
              "limits": [
                { "kind": "weekly_all", "group": "weekly", "percent": 51, "resets_at": "2026-07-31T09:00:00Z" },
                { "kind": "weekly_opus", "percent": 12 }
              ],
              "extra_usage": {
                "is_enabled": true,
                "used_credits": 410,
                "monthly_credit_limit": 5000,
                "currency": "USD"
              }
            }
            """);

        var snapshot = ClaudeCodeUsageClient.ParseSnapshot(document.RootElement, "Claude Max 20x");
        Equal("Claude Max 20x", snapshot.Plan);
        Equal(4, snapshot.Metrics.Count);
        Equal(22, snapshot.Metrics[0].UsedPercent);
        // The dedicated weekly limit wins over the seven-day utilisation field.
        Equal("Weekly", snapshot.Metrics[1].Name);
        Equal(51, snapshot.Metrics[1].UsedPercent);
        Equal("Weekly Opus", snapshot.Metrics[2].Name);
        Equal(12, snapshot.Metrics[2].UsedPercent);
        Equal("Extra usage", snapshot.Metrics[3].Name);
        Equal("$4.10 / $50.00", snapshot.Metrics[3].DisplayRemaining);
        return Task.CompletedTask;
    }

    private static Task TestCopilotSnapshotParsingAsync()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "copilot_plan": "individual_pro",
              "login": "octocat",
              "quota_reset_date_utc": "2026-08-01T00:00:00Z",
              "quota_snapshots": {
                "premium_interactions": {
                  "entitlement": 300,
                  "remaining": 72,
                  "percent_remaining": 24,
                  "token_based_billing": true
                },
                "chat": { "unlimited": true },
                "completions": { "unlimited": true }
              }
            }
            """);

        var snapshot = GitHubCopilotUsageClient.ParseSnapshot(document.RootElement);
        Equal("Pro", snapshot.Plan);
        Equal("octocat", snapshot.AccountName);

        // Every reported quota survives; the third one used to be dropped.
        Equal(3, snapshot.Metrics.Count);
        Equal("AI credits", snapshot.Metrics[0].Name);
        Equal(76, snapshot.Metrics[0].UsedPercent);
        Equal("76% USED", snapshot.Metrics[0].DisplayUsed);
        Equal("24% LEFT", snapshot.Metrics[0].DisplayRemaining);
        Equal("72 of 300 left", snapshot.Metrics[0].DisplaySecondary);
        True(snapshot.Metrics[1].IsUnlimited);
        False(snapshot.Metrics[1].HasQuota);
        Equal(76, snapshot.HighestUsedPercent);
        return Task.CompletedTask;
    }

    private static Task TestGeminiSnapshotParsingAsync()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "buckets": [
                {
                  "modelId": "gemini-1.5-pro",
                  "remainingFraction": 0.75,
                  "resetTime": "2026-07-28T20:00:00Z"
                },
                {
                  "modelId": "gemini-1.5-flash",
                  "remainingFraction": 0.90,
                  "resetTime": "2026-07-28T20:00:00Z"
                }
              ]
            }
            """);

        var snapshot = GeminiUsageClient.ParseQuotaResponse(
            document.RootElement,
            planFromCodeAssist: "Gemini Code Assist in Google One AI Pro",
            accountEmail: "user@example.com");

        Equal("Gemini Code Assist in Google One AI Pro", snapshot.Plan);
        Equal("Google Gemini", snapshot.ProviderName);
        Equal("gemini", snapshot.ProviderId);
        Equal("user@example.com", snapshot.AccountName);

        Equal(2, snapshot.Metrics.Count);
        Equal("Gemini Pro", snapshot.Metrics[0].Name);
        Equal(25, snapshot.Metrics[0].UsedPercent);
        Equal("25% USED", snapshot.Metrics[0].DisplayUsed);
        Equal("75% LEFT", snapshot.Metrics[0].DisplayRemaining);

        Equal("Gemini Flash", snapshot.Metrics[1].Name);
        Equal(10, snapshot.Metrics[1].UsedPercent);

        using var antigravityDoc = JsonDocument.Parse(
            """
            {
              "userStatus": {
                "email": "dev@example.com",
                "cascadeModelConfigData": {
                  "clientModelConfigs": [
                    {
                      "label": "Gemini 3.6 Flash (High)",
                      "quotaInfo": { "remainingFraction": 0.80, "resetTime": "2026-07-29T18:00:00Z" }
                    },
                    {
                      "label": "Claude Sonnet 4.6 (Thinking)",
                      "quotaInfo": { "resetTime": "2026-07-29T18:00:00Z" }
                    }
                  ]
                }
              }
            }
            """);

        var agSnapshot = GeminiUsageClient.ParseAntigravityUserStatus(antigravityDoc.RootElement);
        NotNull(agSnapshot);
        Equal(2, agSnapshot!.Metrics.Count);
        Equal("Gemini Models", agSnapshot.Metrics[0].Name);
        Equal(20, agSnapshot.Metrics[0].UsedPercent);
        Equal("Claude and GPT models", agSnapshot.Metrics[1].Name);
        Equal(100, agSnapshot.Metrics[1].UsedPercent);

        using var summaryDoc = JsonDocument.Parse(
            """
            {
              "response": {
                "groups": [
                  {
                    "displayName": "Gemini Models",
                    "buckets": [
                      {
                        "bucketId": "gemini_5h",
                        "displayName": "5 hour",
                        "window": "5h",
                        "remainingFraction": 0.70,
                        "resetTime": "2026-07-29T18:00:00Z"
                      },
                      {
                        "bucketId": "gemini_weekly",
                        "displayName": "Weekly",
                        "window": "weekly",
                        "remainingFraction": 0.55,
                        "resetTime": "2026-08-03T18:00:00Z"
                      }
                    ]
                  },
                  {
                    "displayName": "Claude and GPT models",
                    "buckets": [
                      {
                        "bucketId": "claude_gpt_5h",
                        "displayName": "5 hour",
                        "remainingFraction": 0.40,
                        "resetTime": "2026-07-29T18:00:00Z"
                      },
                      {
                        "bucketId": "claude_gpt_weekly",
                        "displayName": "Weekly",
                        "remainingFraction": 0.25,
                        "resetTime": "2026-08-03T18:00:00Z"
                      }
                    ]
                  }
                ]
              }
            }
            """);

        var summaryMetrics = GeminiUsageClient.ParseQuotaSummaryResponse(summaryDoc.RootElement);
        Equal(4, summaryMetrics.Count);
        Equal("Gemini Models (5-hour)", summaryMetrics[0].Name);
        Equal(UsageMetricKind.Session, summaryMetrics[0].Kind);
        Equal(300L, summaryMetrics[0].DurationMinutes);
        Equal(30, summaryMetrics[0].UsedPercent);
        Equal("Gemini Models (Weekly)", summaryMetrics[1].Name);
        Equal(UsageMetricKind.Rolling, summaryMetrics[1].Kind);
        Equal(10_080L, summaryMetrics[1].DurationMinutes);
        Equal(45, summaryMetrics[1].UsedPercent);
        Equal("Claude and GPT models (5-hour)", summaryMetrics[2].Name);
        Equal("Claude and GPT models (Weekly)", summaryMetrics[3].Name);
        Equal(75, summaryMetrics[3].UsedPercent);

        var mergedMetrics = GeminiUsageClient.MergeAntigravityQuotaSummaryMetrics(
            agSnapshot.Metrics,
            summaryMetrics);
        Equal(4, mergedMetrics.Count);
        Equal("Gemini Models (5-hour)", mergedMetrics[0].Name);
        Equal("Gemini Models (Weekly)", mergedMetrics[1].Name);
        Equal("Claude and GPT models (5-hour)", mergedMetrics[2].Name);
        Equal("Claude and GPT models (Weekly)", mergedMetrics[3].Name);

        using var alternateEnvelopeDoc = JsonDocument.Parse(
            """
            {
              "quotaSummary": {
                "groups": [
                  {
                    "displayName": "Gemini Models",
                    "buckets": [
                      {
                        "bucketId": "gemini_weekly",
                        "remaining": { "remainingFraction": 0.90 }
                      }
                    ]
                  }
                ]
              }
            }
            """);
        var alternateMetrics = GeminiUsageClient.ParseQuotaSummaryResponse(alternateEnvelopeDoc.RootElement);
        Equal(1, alternateMetrics.Count);
        Equal("Gemini Models (Weekly)", alternateMetrics[0].Name);
        Equal(10, alternateMetrics[0].UsedPercent);

        return Task.CompletedTask;
    }

    private static async Task TestAgyFallbackAsync()
    {
        var output =
            """
            {
              "type": "result",
              "result": {
                "planName": "Google AI Pro",
                "accountEmail": "agy@example.com",
                "quotaSummary": {
                  "groups": [
                    {
                      "displayName": "Gemini Models",
                      "buckets": [
                        {
                          "bucketId": "gemini_5h",
                          "remaining": { "remainingFraction": 0.72 },
                          "resetTime": "2026-08-13T17:00:00Z"
                        },
                        {
                          "bucketId": "gemini_weekly",
                          "remainingFraction": 0.44
                        }
                      ]
                    }
                  ]
                }
              }
            }
            """;
        var parsed = AgyUsageProbe.ParseOutput(output);
        NotNull(parsed);
        Equal("Google AI Pro", parsed!.Plan);
        Equal("agy@example.com", parsed.AccountName);
        Equal(2, parsed.Metrics.Count);
        Equal(28, parsed.Metrics[0].UsedPercent);
        Equal(56, parsed.Metrics[1].UsedPercent);
        Null(AgyUsageProbe.ParseOutput("not JSON"));

        var expected = new UsageSnapshot(
            "Antigravity",
            new[] { new UsageMetric("Gemini Models", UsageMetricKind.Rolling, 17) },
            DateTimeOffset.Now,
            "gemini",
            "Google Gemini");
        using var http = new HttpClient();
        var client = new GeminiUsageClient(
            http,
            _ => Task.FromResult<UsageSnapshot?>(null),
            _ => Task.FromResult<UsageSnapshot?>(expected));
        Equal(expected, await client.GetUsageAsync());
        Equal("agy", client.SignInCommand);
    }

    private static Task TestGeminiJwtExtractionAsync()
    {
        // Header: {"alg":"RS256","typ":"JWT"} -> eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9
        // Payload: {"email":"dev@example.com","hd":"example.com"} -> eyJlbWFpbCI6ImRldkBleGFtcGxlLmNvbSIsImhkIjoiZXhhbXBsZS5jb20ifQ
        var mockJwt = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJlbWFpbCI6ImRldkBleGFtcGxlLmNvbSIsImhkIjoiZXhhbXBsZS5jb20ifQ.signature";

        var email = GeminiUsageClient.ExtractEmailFromJwt(mockJwt);
        Equal("dev@example.com", email);

        var hd = GeminiUsageClient.ExtractHostedDomainFromJwt(mockJwt);
        Equal("example.com", hd);

        Null(GeminiUsageClient.ExtractEmailFromJwt("invalid.jwt"));
        return Task.CompletedTask;
    }

    private static Task TestAntigravityCandidateBindingAsync()
    {
        var process = new GeminiUsageClient.ProcessInfo(
            "synthetic-primary-token",
            "synthetic-extension-token",
            ExtensionPort: 52_000,
            Pid: 42);

        var candidates = process.GetBoundCandidates(new ushort[] { 51_001, 51_001 });
        Equal(2, candidates.Count);
        True(candidates.All(candidate => candidate.Pid == 42));
        True(candidates.All(candidate => candidate.Port == 51_001));
        True(candidates.Any(candidate => candidate.Token == "synthetic-primary-token"));
        True(candidates.Any(candidate => candidate.Token == "synthetic-extension-token"));
        False(candidates.Any(candidate => candidate.Port == 52_000));
        True(process.IsListeningPortStillOwned(candidates[0], new ushort[] { 51_001 }));
        False(process.IsListeningPortStillOwned(candidates[0], new ushort[] { 52_000 }));
        False(process.IsListeningPortStillOwned(
            new GeminiUsageClient.BoundAntigravityCandidate(43, 51_001, "synthetic-token"),
            new ushort[] { 51_001 }));

        var pidless = process with { Pid = null };
        Equal(0, pidless.GetBoundCandidates(new ushort[] { 51_001 }).Count);
        return Task.CompletedTask;
    }

    private static Task TestSettingsAsync()
    {
        var lastUpdateCheck = DateTimeOffset.UtcNow.AddHours(-2);
        var settings = new AppSettings
        {
            RefreshIntervalMinutes = 999,
            WarningPercent = 95,
            CriticalPercent = 10,
            NotifyAtPercent = new[] { 150, 80, 80, 20 },
            TrayProviderId = " claude ",
            LastUpdateCheckUtc = lastUpdateCheck,
        };
        settings.Save();

        Equal(AppSettings.MaximumRefreshMinutes, settings.RefreshIntervalMinutes);
        Equal(96, settings.CriticalPercent);
        Equal(2, settings.NotifyAtPercent.Length);
        Equal(20, settings.NotifyAtPercent[0]);
        Equal(80, settings.NotifyAtPercent[1]);
        Equal("claude", settings.TrayProviderId);

        var reloaded = AppSettings.Load();
        Equal(AppSettings.MaximumRefreshMinutes, reloaded.RefreshIntervalMinutes);
        Equal(96, reloaded.CriticalPercent);
        Equal(2, reloaded.NotifyAtPercent.Length);
        Equal("claude", reloaded.TrayProviderId);
        Equal(lastUpdateCheck, reloaded.LastUpdateCheckUtc);

        File.Delete(AppPaths.SettingsFile);
        return Task.CompletedTask;
    }

    private static Task TestProviderOrderAsync()
    {
        var settings = new AppSettings
        {
            ProviderOrder = new[] { "copilot", "codex" },
            HiddenProviders = new[] { "claude" },
        };

        var ordered = settings.OrderProviders(ProviderIds);
        Equal(4, ordered.Count);
        Equal("copilot", ordered[0]);
        Equal("codex", ordered[1]);
        Equal("claude", ordered[2]);
        Equal("gemini", ordered[3]);
        False(settings.IsProviderVisible("claude"));
        True(settings.IsProviderVisible("codex"));

        settings.SetProviderVisible("claude", true);
        True(settings.IsProviderVisible("claude"));
        settings.SetProviderVisible("codex", false);
        False(settings.IsProviderVisible("codex"));
        return Task.CompletedTask;
    }

    private static Task TestSettingsScrollLayoutAsync()
    {
        var settings = new AppSettings { TrayProviderId = "claude" };
        using var form = new SettingsForm(
            settings,
            new[]
            {
                ("codex", "Codex"),
                ("claude", "Claude Code"),
                ("copilot", "GitHub Copilot"),
                ("gemini", "Google Gemini"),
            });
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-10_000, -10_000);
        form.Show();
        Application.DoEvents();
        form.PerformLayout();

        var shell = form.Controls.OfType<TableLayoutPanel>().Single();
        shell.PerformLayout();
        Equal(2, shell.RowCount);

        var content = shell.GetControlFromPosition(0, 0) as Panel
            ?? throw new InvalidOperationException("The settings content area is missing.");
        var footer = shell.GetControlFromPosition(0, 1)
            ?? throw new InvalidOperationException("The settings footer is missing.");
        Equal(content.Bottom, footer.Top);
        Equal(shell.ClientSize.Height, footer.Bottom);

        content.PerformLayout();
        if (!content.VerticalScroll.Visible)
        {
            throw new InvalidOperationException(
                $"Expected a vertical scrollbar for {content.DisplayRectangle.Height}px of settings content " +
                $"inside a {content.ClientSize.Height}px viewport.");
        }

        if (content.HorizontalScroll.Visible)
        {
            throw new InvalidOperationException("The settings content unexpectedly requires horizontal scrolling.");
        }

        var settingsTable = content.Controls.OfType<TableLayoutPanel>().Single();
        var trayProvider = settingsTable.Controls
            .OfType<ComboBox>()
            .Single(combo => combo.Items.Cast<object>().Any(item => item.ToString() == "Automatic"));
        Equal("Claude Code", trayProvider.SelectedItem!.ToString());

        var finalControl = settingsTable.Controls
            .Cast<Control>()
            .Single(control => settingsTable.GetRow(control) == settingsTable.RowCount - 1);
        content.AutoScrollPosition = new Point(0, content.VerticalScroll.Maximum);
        Application.DoEvents();
        content.PerformLayout();
        var finalControlBottom = settingsTable.Top + finalControl.Bottom;
        if (finalControlBottom > content.ClientSize.Height)
        {
            throw new InvalidOperationException(
                $"The final settings control ends at {finalControlBottom}px after scrolling, " +
                $"outside the {content.ClientSize.Height}px viewport.");
        }

        trayProvider.SelectedIndex = trayProvider.Items
            .Cast<object>()
            .Select((item, index) => (Item: item, Index: index))
            .Single(entry => entry.Item.ToString() == "GitHub Copilot")
            .Index;
        var save = footer.Controls
            .OfType<Button>()
            .Single(button => button.Text == "Save");
        save.PerformClick();
        Equal("copilot", settings.TrayProviderId);
        return Task.CompletedTask;
    }

    private static Task TestTrayProviderSelectionAsync()
    {
        var now = DateTimeOffset.Now;
        var codex = new ProviderStatus(
            "codex",
            "Codex",
            SampleSnapshot(40, now),
            null,
            false);
        var claude = new ProviderStatus(
            "claude",
            "Claude Code",
            SampleSnapshot(80, now) with
            {
                ProviderId = "claude",
                ProviderName = "Claude Code",
            },
            null,
            false);
        var connected = new[] { codex, claude };

        Equal("claude", UsageApplicationContext.SelectTrayStatus(connected, null)!.ProviderId);
        Equal("codex", UsageApplicationContext.SelectTrayStatus(connected, "CODEX")!.ProviderId);
        Equal("claude", UsageApplicationContext.SelectTrayStatus(connected, "copilot")!.ProviderId);
        Null(UsageApplicationContext.SelectTrayStatus(Array.Empty<ProviderStatus>(), "codex"));
        Equal("UsageAI - Claude Code: 80% used", UsageApplicationContext.TrayTooltip(claude));

        using var emptyIcon = TrayIconFactory.Create(
            0,
            "A",
            size: 16,
            identityColor: Theme.ForProvider("claude"));
        using var emptyBitmap = emptyIcon.ToBitmap();
        var strongPixels = 0;
        for (var y = 0; y < emptyBitmap.Height; y++)
        {
            for (var x = 0; x < emptyBitmap.Width; x++)
            {
                if (emptyBitmap.GetPixel(x, y).A >= 180)
                {
                    strongPixels++;
                }
            }
        }

        True(strongPixels >= 24);
        using var refreshingIcon = TrayIconFactory.CreateRefreshing(90F, size: 16);
        using var refreshingBitmap = refreshingIcon.ToBitmap();
        True(refreshingBitmap.Width == 16 && refreshingBitmap.Height == 16);
        var hasVisibleRefreshPixel = false;
        for (var y = 0; y < refreshingBitmap.Height && !hasVisibleRefreshPixel; y++)
        {
            for (var x = 0; x < refreshingBitmap.Width; x++)
            {
                if (refreshingBitmap.GetPixel(x, y).A > 0)
                {
                    hasVisibleRefreshPixel = true;
                    break;
                }
            }
        }

        True(hasVisibleRefreshPixel);
        return Task.CompletedTask;
    }

    private static Task TestUsageHistoryAsync()
    {
        UsageHistoryStore.Clear();
        var now = DateTimeOffset.Now;
        UsageHistoryStore.Append(new[]
        {
            new UsageSample(now.AddMinutes(-30), "codex", "Session:5-hour", 30),
            new UsageSample(now, "codex", "Session:5-hour", 42),
            new UsageSample(now.AddDays(-30), "codex", "Session:5-hour", 99),
        });

        var loaded = UsageHistoryStore.Load(TimeSpan.FromHours(2));
        Equal(2, loaded.Count);
        Equal(30, loaded[0].UsedPercent);
        Equal(42, loaded[1].UsedPercent);
        Equal("codex", loaded[1].ProviderId);
        Equal("Session:5-hour", loaded[1].MetricKey);

        UsageHistoryStore.Clear();
        Equal(0, UsageHistoryStore.Load(TimeSpan.FromHours(2)).Count);
        return Task.CompletedTask;
    }

    private static Task TestSnapshotCacheAsync()
    {
        var now = DateTimeOffset.Now;
        var snapshot = new UsageSnapshot(
            "Plus",
            new[]
            {
                new UsageMetric("5-hour", UsageMetricKind.Session, 43, now.AddHours(2), 300),
                new UsageMetric("Credits", UsageMetricKind.Balance, null, RemainingText: "$12.50"),
            },
            now,
            "codex",
            "Codex",
            "someone@example.com");

        SnapshotCache.Save(new[] { snapshot });
        var loaded = SnapshotCache.Load();
        Equal(1, loaded.Count);
        Equal("Plus", loaded[0].Plan);
        Equal("someone@example.com", loaded[0].AccountName);
        Equal(2, loaded[0].Metrics.Count);
        Equal(43, loaded[0].Metrics[0].UsedPercent);
        Equal(UsageMetricKind.Session, loaded[0].Metrics[0].Kind);
        Equal("$12.50", loaded[0].Metrics[1].DisplayRemaining);

        SnapshotCache.Clear();
        Equal(0, SnapshotCache.Load().Count);
        return Task.CompletedTask;
    }

    private static Task TestForecastAsync()
    {
        var now = DateTimeOffset.Now;
        var samples = new List<UsageSample>();
        for (var index = 0; index < 6; index++)
        {
            samples.Add(new UsageSample(
                now.AddMinutes(-50 + index * 10),
                "codex",
                "Session:5-hour",
                30 + index * 4));
        }

        var metric = new UsageMetric("5-hour", UsageMetricKind.Session, 50, now.AddHours(4), 300);
        var projection = UsageForecast.Project(samples, "codex", metric, now);
        NotNull(projection);
        True(projection!.PercentPerHour > 20 && projection.PercentPerHour < 28);
        True(projection.BeforeReset);
        True(projection.ExhaustedAt > now && projection.ExhaustedAt < now.AddHours(4));

        var trend = UsageForecast.Trend(samples, "codex", metric);
        Equal(6, trend.Count);
        Equal(50, trend[^1]);
        return Task.CompletedTask;
    }

    private static Task TestForecastResetAsync()
    {
        var now = DateTimeOffset.Now;
        var samples = new List<UsageSample>
        {
            new(now.AddMinutes(-50), "codex", "Session:5-hour", 80),
            new(now.AddMinutes(-40), "codex", "Session:5-hour", 88),
            new(now.AddMinutes(-30), "codex", "Session:5-hour", 95),
            new(now.AddMinutes(-20), "codex", "Session:5-hour", 3),
            new(now.AddMinutes(-10), "codex", "Session:5-hour", 5),
        };

        // Only the run since the reset counts, and it is too short to project from.
        var metric = new UsageMetric("5-hour", UsageMetricKind.Session, 5, now.AddHours(4), 300);
        Null(UsageForecast.Project(samples, "codex", metric, now));
        return Task.CompletedTask;
    }

    private static Task TestNotificationsAsync()
    {
        var coordinator = new NotificationCoordinator();
        var settings = new AppSettings();
        var now = DateTimeOffset.Now;

        // The first observation is recorded silently, so launching at 70% stays quiet.
        Equal(0, coordinator.Evaluate(SampleSnapshot(70, now), settings, now).Count);

        var crossed = coordinator.Evaluate(SampleSnapshot(85, now), settings, now.AddMinutes(5));
        Equal(1, crossed.Count);
        Equal(AlertLevel.Warning, crossed[0].Level);

        // Still above the same threshold: no repeat.
        Equal(0, coordinator.Evaluate(SampleSnapshot(86, now), settings, now.AddMinutes(10)).Count);

        var critical = coordinator.Evaluate(SampleSnapshot(96, now), settings, now.AddMinutes(15));
        Equal(1, critical.Count);
        Equal(AlertLevel.Critical, critical[0].Level);

        var reset = coordinator.Evaluate(SampleSnapshot(4, now), settings, now.AddMinutes(20));
        Equal(1, reset.Count);
        Equal(AlertLevel.Info, reset[0].Level);
        return Task.CompletedTask;
    }

    private static Task TestUpdateComparisonAsync()
    {
        True(UpdateChecker.IsNewer("v0.5.0", "0.4.0"));
        True(UpdateChecker.IsNewer("0.4.1", "0.4.0"));
        False(UpdateChecker.IsNewer("v0.3.0", "0.4.0"));
        False(UpdateChecker.IsNewer("v0.4.0", "0.4.0"));
        False(UpdateChecker.IsNewer("not-a-version", "0.4.0"));
        return Task.CompletedTask;
    }

    private static UsageSnapshot SampleSnapshot(int usedPercent, DateTimeOffset at) =>
        new(
            "Plus",
            new[] { new UsageMetric("5-hour", UsageMetricKind.Session, usedPercent, at.AddHours(3), 300) },
            at,
            "codex",
            "Codex");

    private static string GeminiCredentialJson(string accessToken, string refreshToken) =>
        $$"""
        {
          "access_token": "{{accessToken}}",
          "refresh_token": "{{refreshToken}}",
          "id_token": "old-id-token",
          "expiry_date": 1000,
          "custom": "keep-this-value"
        }
        """;

    private static string CreateTestDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "UsageAI.SecurityTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTestDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        var expectedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UsageAI.SecurityTests"));
        if (!fullPath.StartsWith(expectedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to remove an unexpected test path.");
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
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
            throw new InvalidOperationException("Expected a value, got null.");
        }
    }

    private static Task TestPrimaryMetricTrayTooltipAsync()
    {
        var now = DateTimeOffset.Now;
        var snapshot = new UsageSnapshot(
            "Claude Pro",
            new[]
            {
                new UsageMetric("5-hour", UsageMetricKind.Session, 8, now.AddHours(3), 300),
                new UsageMetric("Weekly", UsageMetricKind.Rolling, 92, now.AddDays(4), 10_080),
            },
            now,
            "claude",
            "Claude Code");
        var status = new ProviderStatus("claude", "Claude Code", snapshot, null, false);
        var competingSnapshot = new UsageSnapshot(
            "Plus",
            new[]
            {
                new UsageMetric("5-hour", UsageMetricKind.Session, 40, now.AddHours(2), 300),
                new UsageMetric("Weekly", UsageMetricKind.Rolling, 40, now.AddDays(3), 10_080),
            },
            now,
            "codex",
            "Codex");
        var competingStatus = new ProviderStatus("codex", "Codex", competingSnapshot, null, false);
        var statuses = new[] { status, competingStatus };

        Equal("UsageAI - Claude Code: 8% used", UsageApplicationContext.TrayTooltip(status));
        Equal(8, UsageApplicationContext.TrayUsedPercent(status));
        Equal("codex", UsageApplicationContext.SelectTrayStatus(statuses, null)!.ProviderId);
        Equal("claude", UsageApplicationContext.SelectTrayStatus(statuses, "claude")!.ProviderId);
        Equal("codex", UsagePopupForm.SelectHeadlineStatus(statuses)!.ProviderId);
        return Task.CompletedTask;
    }

    private static Task TestThemeUsageColorsAsync()
    {
        Equal(Theme.Success, Theme.ForUsage(8));
        Equal(Theme.Success, Theme.ForUsage(45));
        Equal(Theme.Signal, Theme.ForUsage(50));
        Equal(Theme.Signal, Theme.ForUsage(70));
        Equal(Theme.Warning, Theme.ForUsage(75));
        Equal(Theme.Critical, Theme.ForUsage(92));

        using var filledIcon = TrayIconFactory.Create(
            45,
            "A",
            size: 24,
            identityColor: Theme.ForProvider("claude"));
        using var bitmap = filledIcon.ToBitmap();
        True(bitmap.Width == 24 && bitmap.Height == 24);
        return Task.CompletedTask;
    }

    private static void TryRemoveDataDirectory()
    {
        try
        {
            if (Directory.Exists(AppPaths.DataDirectory))
            {
                DeleteTestDirectory(AppPaths.DataDirectory);
            }
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidOperationException)
        {
            // A leftover scratch directory is harmless.
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
