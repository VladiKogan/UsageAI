using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Security.AccessControl;
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
    private static readonly string[] ClaudeProfileScope = { "user:profile" };
    private static readonly string[] ProviderIds = { "codex", "claude", "copilot" };

    private static readonly (string Name, Func<Task> Run)[] Tests =
    {
        ("credential input validation", TestCredentialInputAsync),
        ("minimal child environment", TestMinimalChildEnvironmentAsync),
        ("bounded CLI output", TestBoundedCliOutputAsync),
        ("bounded HTTP JSON", TestBoundedHttpJsonAsync),
        ("bounded credential files", TestBoundedCredentialFileAsync),
        ("Claude session-key normalization", TestClaudeSessionKeyAsync),
        ("Claude organization parsing", TestClaudeOrganizationParsingAsync),
        ("Claude credential ACL preservation", TestClaudeCredentialAclPreservationAsync),
        ("Claude credential lost-update protection", TestClaudeCredentialLostUpdateAsync),
        ("cross-process refresh lock", TestCrossProcessLockAsync),
        ("Codex snapshot parsing", TestCodexSnapshotParsingAsync),
        ("Claude snapshot parsing", TestClaudeSnapshotParsingAsync),
        ("Copilot keeps every reported quota", TestCopilotSnapshotParsingAsync),
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
    };

    private static async Task<int> Main()
    {
        // Keep every file-touching test inside a scratch directory instead of the real profile.
        Environment.SetEnvironmentVariable(
            "USAGEAI_DATA_DIR",
            Path.Combine(Path.GetTempPath(), "UsageAI.SecurityTests", $"data-{Guid.NewGuid():N}"));

        var failures = 0;
        foreach (var (name, run) in Tests)
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
        Console.WriteLine($"{Tests.Length - failures}/{Tests.Length} checks passed.");
        return failures == 0 ? 0 : 1;
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

    private static Task TestClaudeCredentialAclPreservationAsync()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, ".credentials.json");
            File.WriteAllText(path, CredentialJson("old-access", "old-refresh"));

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
            const AccessControlSections comparedSections =
                AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
            var before = new FileInfo(path).GetAccessControl(comparedSections)
                .GetSecurityDescriptorSddlForm(comparedSections);

            ClaudeCodeUsageClient.TryPersistRefreshedCredentials(new ClaudeCodeUsageClient.ClaudeCredentials(
                "new-access",
                "new-refresh",
                DateTimeOffset.UtcNow.AddHours(1),
                ClaudeProfileScope,
                "Claude",
                path,
                "old-refresh"));

            var after = new FileInfo(path).GetAccessControl(comparedSections)
                .GetSecurityDescriptorSddlForm(comparedSections);
            Equal(before, after);
            var updated = File.ReadAllText(path);
            True(updated.Contains("new-access", StringComparison.Ordinal));
            True(updated.Contains("new-refresh", StringComparison.Ordinal));
            True(updated.Contains("keep-this-value", StringComparison.Ordinal));
            Equal(0, Directory.GetFiles(directory, ".credentials.json.usageai-tmp.*").Length);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }

        return Task.CompletedTask;
    }

    private static Task TestClaudeCredentialLostUpdateAsync()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, ".credentials.json");
            var original = CredentialJson("other-access", "other-refresh");
            File.WriteAllText(path, original);

            ClaudeCodeUsageClient.TryPersistRefreshedCredentials(new ClaudeCodeUsageClient.ClaudeCredentials(
                "new-access",
                "new-refresh",
                DateTimeOffset.UtcNow.AddHours(1),
                ClaudeProfileScope,
                "Claude",
                path,
                "stale-refresh"));

            Equal(original, File.ReadAllText(path));
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

    private static Task TestSettingsAsync()
    {
        var settings = new AppSettings
        {
            RefreshIntervalMinutes = 999,
            WarningPercent = 95,
            CriticalPercent = 10,
            NotifyAtPercent = new[] { 150, 80, 80, 20 },
            TrayProviderId = " claude ",
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
        Equal(3, ordered.Count);
        Equal("copilot", ordered[0]);
        Equal("codex", ordered[1]);
        Equal("claude", ordered[2]);
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

    private static string CredentialJson(string accessToken, string refreshToken) =>
        $$"""
        {
          "mcpOAuth": { "server": { "accessToken": "keep-this-value" } },
          "claudeAiOauth": {
            "accessToken": "{{accessToken}}",
            "refreshToken": "{{refreshToken}}",
            "expiresAt": 1000,
            "scopes": ["user:profile"],
            "subscriptionType": "max"
          }
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
