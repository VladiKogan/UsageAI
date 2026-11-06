using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using UsageAI.Models;
using UsageAI.Services;
using UsageAI.UI;

namespace UsageAI.Tests;

/// <summary>
/// Several logins per provider: keys and labels, discovery and identity, the account manager's
/// banners, per-folder clients, the refresh service keeping accounts apart, the official-CLI
/// sign-in, and the Settings page and popup banner that manage them.
/// </summary>
internal static class AccountTests
{
    private static readonly string[] KeyringEntries = { "{}" };

    public static Task TestAccountKeysAndSettingsAsync()
    {
        // The default login keeps the bare provider id, so a single-account profile is unchanged.
        Equal("claude", ProviderKey.Compose("claude", "default"));
        Equal("claude@1a2b3c4d", ProviderKey.Compose("claude", "1a2b3c4d"));
        Equal("claude", ProviderKey.ProviderOf("claude@1a2b3c4d"));
        Equal("1a2b3c4d", ProviderKey.AccountOf("claude@1a2b3c4d"));
        Equal("default", ProviderKey.AccountOf("codex"));
        True(ProviderKey.IsDefault("gemini"));
        False(ProviderKey.IsDefault("codex@00ff00ff"));

        // One folder always gets one id, whatever the case or trailing separator.
        var id = ProviderKey.AccountIdFor(@"C:\Users\Someone\.claude-work");
        Equal(8, id.Length);
        Equal(id, ProviderKey.AccountIdFor(@"c:\users\someone\.CLAUDE-WORK\"));
        Equal(@"C:\", ProviderKey.NormalizeFolder(@"C:\"));
        Equal(@"C:\a\b", ProviderKey.NormalizeFolder(@"C:\a\b\\"));

        // Labels: the name, else the email, else the folder; two alike add the organisation, else a number.
        var named = new ProviderAccount { ProviderId = "claude", Id = "aaaaaaaa", Path = @"C:\x\.claude-work", Label = "Team", Email = "a@x.com" };
        var emailed = new ProviderAccount { ProviderId = "claude", Id = "bbbbbbbb", Path = @"C:\x\.claude-b", Email = "a@x.com" };
        var foldered = new ProviderAccount { ProviderId = "claude", Id = "cccccccc", Path = @"C:\x\.claude-c" };
        Equal("Team", named.DisplayLabel);
        Equal("a@x.com", emailed.DisplayLabel);
        Equal(".claude-c", foldered.DisplayLabel);
        Equal("Default", new ProviderAccount { ProviderId = "claude" }.DisplayLabel);
        var sameEmail = AccountLabels.Distinct(new (string, ProviderAccount?)[]
        {
            ("claude", new ProviderAccount { ProviderId = "claude", Email = "a@x.com", Organization = "Personal" }),
            ("claude@bbbbbbbb", emailed.Clone().With(account => account.Organization = "Acme")),
        }).ToDictionary(entry => entry.Key, entry => entry.Label);
        Equal("a@x.com · Personal", sameEmail["claude"]);
        Equal("a@x.com · Acme", sameEmail["claude@bbbbbbbb"]);
        var numbered = AccountLabels.Distinct(new (string, ProviderAccount?)[]
        {
            ("claude", new ProviderAccount { ProviderId = "claude", Email = "a@x.com" }),
            ("claude@bbbbbbbb", emailed),
            ("claude@cccccccc", null),
        }).ToDictionary(entry => entry.Key, entry => entry.Label);
        Equal("a@x.com", numbered["claude"]);
        Equal("a@x.com · Account 2", numbered["claude@bbbbbbbb"]);
        Equal("Default", numbered["claude@cccccccc"]);

        // A hand-edited file cannot point a key at another folder or add a provider without accounts.
        var folder = Path.Combine(Path.GetTempPath(), "UsageAI.AccountTests", ".claude-work");
        var settings = new AppSettings();
        settings.Accounts.Add(new ProviderAccount { ProviderId = "CLAUDE", Id = "deadbeef", Source = AccountSource.Found, Path = folder, Label = new string('x', 90), Identity = "not hex!" });
        settings.Accounts.Add(new ProviderAccount { ProviderId = "claude", Id = "11111111", Source = AccountSource.Added, Path = "relative\\folder" });
        settings.Accounts.Add(new ProviderAccount { ProviderId = "claude", Id = "22222222", Source = AccountSource.Added, Path = @"\\server\share\.claude" });
        settings.Accounts.Add(new ProviderAccount { ProviderId = "gemini", Id = "33333333", Source = AccountSource.Added, Path = folder });
        settings.Accounts.Add(new ProviderAccount { ProviderId = "codex", Id = "anything", Source = AccountSource.Default, Path = folder, Pending = true });
        settings.Accounts.Add(new ProviderAccount { ProviderId = "claude", Id = "duplicate", Source = AccountSource.Found, Path = folder });
        settings.Accounts.Add(new ProviderAccount { ProviderId = "claude", Source = AccountSource.Added, Path = folder + "-2", Label = "no id" });
        settings.ForgottenAccounts = new[] { "claude@abc", "bad key!", "claude@abc" };
        settings.LastAccountScanUtc = DateTimeOffset.UtcNow.AddDays(3);
        settings.Save();
        var loaded = AppSettings.Load();
        Equal(3, loaded.Accounts.Count);
        Equal(ProviderKey.AccountIdFor(folder + "-2"), loaded.Accounts[2].Id);
        Equal("no id", loaded.Accounts[2].Label);
        loaded.Accounts.RemoveAt(2);
        var work = loaded.Accounts[0];
        Equal("claude", work.ProviderId);
        Equal(ProviderKey.AccountIdFor(folder), work.Id);
        Equal(null, work.Label);
        Equal(null, work.Identity);
        var codexDefault = loaded.Accounts[1];
        Equal("default", codexDefault.Id);
        Equal(null, codexDefault.Path);
        False(codexDefault.Pending);
        Equal("claude@abc", string.Join(',', loaded.ForgottenAccounts));
        Equal(null, loaded.LastAccountScanUtc);

        // Visibility and order: a provider's accounts stay together, the default first.
        var key = work.UnitKey;
        True(loaded.IsUnitVisible(key));
        work.Pending = true;
        False(loaded.IsUnitVisible(key));
        work.Pending = false;
        work.DuplicateOf = "someone";
        False(loaded.IsUnitVisible(key));
        work.DuplicateOf = null;
        loaded.SetProviderVisible("claude", false);
        False(loaded.IsUnitVisible(key));
        False(loaded.IsUnitVisible("claude"));
        loaded.SetProviderVisible("claude", true);
        True(loaded.IsUnitVisible("claude@unknown0"));
        loaded.ProviderOrder = new[] { "codex", "claude" };
        Equal($"codex|claude|{key}", string.Join('|', loaded.OrderUnits(new[] { key, "claude", "codex" })));
        work.Order = 0;
        loaded.EnsureAccount("claude").Order = 5;
        Equal($"{key}|claude", string.Join('|', loaded.OrderUnits(new[] { "claude", key })));
        True(loaded.AccountAlertsEnabled("copilot"));
        work.Alerts = false;
        False(loaded.AccountAlertsEnabled(key));
        Equal(6, loaded.NextAccountOrder("claude"));
        return Task.CompletedTask;
    }

    public static Task TestAccountDiscoveryAndIdentityAsync()
    {
        var profile = NewDirectory("profile");
        try
        {
            var claudeWork = Login(profile, ".claude-work", ".credentials.json");
            File.WriteAllText(Path.Combine(claudeWork, ".claude.json"), ClaudeJson("work@example.com", "org-1", "Acme"));
            Login(profile, ".claude_side", ".credentials.json");
            Directory.CreateDirectory(Path.Combine(profile, ".claude-empty"));
            Login(profile, ".clauder", ".credentials.json");
            Login(profile, ".claude-", ".credentials.json");
            var codexTeam = Login(profile, ".codex-team", "auth.json");
            File.WriteAllText(Path.Combine(codexTeam, "auth.json"), CodexAuth("team@example.com", "ws-1"));
            Login(profile, ".codex_two", "auth.json");
            var defaultClaude = Login(profile, ".claude", ".credentials.json");
            File.WriteAllText(Path.Combine(profile, ".claude.json"), ClaudeJson("me@example.com", "org-0", "Personal"));

            // One listing: the naming rule, a credential file, and never the default folder.
            var found = AccountDiscovery.Scan(profile, new[] { defaultClaude, Path.Combine(profile, ".codex") });
            Equal(".claude-work|.claude_side|.codex-team|.codex_two", string.Join('|', found
                .Select(login => Path.GetFileName(login.Folder))
                .Order(StringComparer.Ordinal)));
            Equal("codex", found.Single(login => login.Folder.EndsWith(".codex-team", StringComparison.Ordinal)).ProviderId);
            Equal(0, AccountDiscovery.Scan(Path.Combine(profile, "missing"), Array.Empty<string>()).Count);
            Equal(null, AccountDiscovery.ProviderForFolderName(".claude"));
            Equal("codex", AccountDiscovery.ProviderForFolderName(".CODEX-Work"));

            // Who is signed in, from local files only.
            var work = AccountDiscovery.ReadIdentity("claude", claudeWork, profile)!;
            Equal("work@example.com", work.Email);
            Equal("Acme", work.OrganizationName);
            Equal(work.Hash, AccountDiscovery.ReadIdentity("claude", claudeWork, profile)!.Hash);
            var environment = SaveEnvironment("CLAUDE_CONFIG_DIR", "CODEX_HOME");
            try
            {
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
                Environment.SetEnvironmentVariable("CODEX_HOME", null);
                Equal("me@example.com", AccountDiscovery.ReadIdentity("claude", defaultClaude, profile)!.Email);
                Equal(Path.Combine(profile, ".claude.json"), AccountDiscovery.ClaudeIdentityFile(defaultClaude, profile));
                Equal(ProviderKey.NormalizeFolder(Path.Combine(profile, ".codex")), AccountDiscovery.DefaultFolder("codex", profile));
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", claudeWork);
                Equal(claudeWork, AccountDiscovery.DefaultFolder("claude", profile));
                // Pointed elsewhere, ~\.claude reads its own profile when it has one, else the one beside it.
                Equal(Path.Combine(profile, ".claude.json"), AccountDiscovery.ClaudeIdentityFile(defaultClaude, profile));
                File.WriteAllText(Path.Combine(defaultClaude, ".claude.json"), "{}");
                Equal(Path.Combine(defaultClaude, ".claude.json"), AccountDiscovery.ClaudeIdentityFile(defaultClaude, profile));
            }
            finally
            {
                RestoreEnvironment(environment);
            }

            var team = AccountDiscovery.ReadIdentity("codex", codexTeam, profile)!;
            Equal("team@example.com", team.Email);
            Equal("ws-1", team.OrganizationId);
            NotEqual(work.Hash, team.Hash);
            Equal(null, AccountDiscovery.ReadIdentity("codex", Path.Combine(profile, ".codex_two"), profile));
            File.WriteAllText(Path.Combine(profile, ".codex_two", "auth.json"), """{"tokens":{"account_id":"acct-9"}}""");
            Equal("acct-9", AccountDiscovery.ReadIdentity("codex", Path.Combine(profile, ".codex_two"), profile)!.OrganizationId);
            File.WriteAllText(Path.Combine(profile, ".codex_two", "auth.json"), "not json");
            Equal(null, AccountDiscovery.ReadIdentity("codex", Path.Combine(profile, ".codex_two"), profile));
            Equal(null, AccountDiscovery.ReadIdentity("claude", Path.Combine(profile, ".claude_side"), profile));
            File.WriteAllText(Path.Combine(profile, ".claude_side", ".claude.json"), """{"oauthAccount":{"emailAddress":""}}""");
            Equal(null, AccountDiscovery.ReadIdentity("claude", Path.Combine(profile, ".claude_side"), profile));
            Equal(null, AccountDiscovery.JwtPayload("a.b"));
            Equal(null, AccountDiscovery.JwtPayload("a.!!!.c"));
            Equal(null, AccountDiscovery.JwtPayload($"a.{Base64Url("[1]")}.c"));

            // Folder checks: absolute, local, and holding a login (or empty, for a new sign-in).
            Equal("Choose a folder.", AccountDiscovery.PathProblem(" "));
            True(AccountDiscovery.PathProblem(@"\\server\share")!.Contains("network", StringComparison.Ordinal));
            True(AccountDiscovery.PathProblem("relative")!.Contains("full path", StringComparison.Ordinal));
            Equal(null, AccountDiscovery.ExistingLoginProblem("claude", claudeWork));
            True(AccountDiscovery.ExistingLoginProblem("claude", Path.Combine(profile, ".claude-empty"))!.Contains(".credentials.json", StringComparison.Ordinal));
            True(AccountDiscovery.ExistingLoginProblem("codex", Path.Combine(profile, "nope"))!.Contains("does not exist", StringComparison.Ordinal));
            Equal(AccountDiscovery.NewFolderState.Usable, AccountDiscovery.CheckNewLoginFolder("claude", Path.Combine(profile, "new")).State);
            Equal(AccountDiscovery.NewFolderState.Usable, AccountDiscovery.CheckNewLoginFolder("claude", Path.Combine(profile, ".claude-empty")).State);
            Equal(AccountDiscovery.NewFolderState.HoldsLogin, AccountDiscovery.CheckNewLoginFolder("claude", claudeWork).State);
            Equal(AccountDiscovery.NewFolderState.NotEmpty, AccountDiscovery.CheckNewLoginFolder("codex", claudeWork).State);
            Equal(AccountDiscovery.NewFolderState.Invalid, AccountDiscovery.CheckNewLoginFolder("codex", "relative").State);
            Equal(AccountDiscovery.NewFolderState.Invalid, AccountDiscovery.CheckNewLoginFolder("codex", Path.Combine(profile, ".claude.json")).State);

            // A new sign-in's folder comes from its name, numbered while taken.
            Equal(Path.Combine(profile, ".claude-work-2"), AccountDiscovery.SuggestFolder("claude", "Work", profile));
            Equal(Path.Combine(profile, ".codex-my-team"), AccountDiscovery.SuggestFolder("codex", "  My  Team! ", profile));
            Equal(Path.Combine(profile, ".claude-account-2"), AccountDiscovery.SuggestFolder("claude", null, profile));
            Equal(40, AccountDiscovery.Slug(new string('a', 60)).Length);
            Equal(string.Empty, AccountDiscovery.Slug("!!!"));
            Equal("$env:CODEX_HOME='C:\\it''s'; codex login", AccountCommands.SignIn("codex", "C:\\it's"));
            Equal("$env:CLAUDE_CONFIG_DIR='C:\\x'; claude", AccountCommands.SignIn("claude", "C:\\x"));
        }
        finally
        {
            TryDelete(profile);
        }

        return Task.CompletedTask;
    }

    public static Task TestAccountManagerAsync()
    {
        var profile = NewDirectory("manager");
        try
        {
            var defaultClaude = Login(profile, ".claude", ".credentials.json");
            File.WriteAllText(Path.Combine(profile, ".claude.json"), ClaudeJson("me@example.com", "org-0", "Personal"));
            var work = Login(profile, ".claude-work", ".credentials.json");
            File.WriteAllText(Path.Combine(work, ".claude.json"), ClaudeJson("work@example.com", "org-1", "Acme"));
            var settings = new AppSettings { IsEphemeral = true };
            var created = new List<string>();
            var manager = new AccountManager(
                settings,
                profile,
                providerId => Path.Combine(profile, providerId == "claude" ? ".claude" : ".codex"),
                account =>
                {
                    created.Add(account.UnitKey);
                    return new StubClient(account.UnitKey);
                });
            var changes = 0;
            manager.AccountsChanged += (_, _) => changes++;

            // The first scan shows what it finds, with one banner naming every login.
            True(manager.ScanIfDue(DateTimeOffset.UtcNow));
            var workKey = ProviderKey.Compose("claude", ProviderKey.AccountIdFor(work));
            var account = settings.FindAccount(workKey)!;
            False(account.Pending);
            Equal(AccountSource.Found, account.Source);
            Equal("work@example.com", account.Email);
            Equal(1, changes);
            var banner = manager.Notices.Single();
            Equal(AccountNoticeKind.FoundSeveral, banner.Kind);
            Equal("Found 2 Claude accounts: me@example.com, work@example.com", banner.Message);
            manager.Dismiss(banner);
            Equal(0, manager.Notices.Count);
            True(settings.AccountsInitialized);
            False(manager.ScanIfDue(DateTimeOffset.UtcNow.AddHours(1)));

            // Clients: the defaults, then one per extra folder.
            var clients = manager.CreateClients(new IUsageClient[] { new StubClient("claude") });
            Equal($"claude|{workKey}", string.Join('|', clients.Select(client => client.Id)));

            // A later find waits for Show / Ignore, and comes back after a restart while unanswered.
            var side = Login(profile, ".claude-side", ".credentials.json");
            File.WriteAllText(Path.Combine(side, ".claude.json"), ClaudeJson("side@example.com", "org-2", "Side"));
            True(manager.ScanIfDue(DateTimeOffset.UtcNow.AddDays(2)));
            var sideKey = ProviderKey.Compose("claude", ProviderKey.AccountIdFor(side));
            True(settings.FindAccount(sideKey)!.Pending);
            False(settings.IsUnitVisible(sideKey));
            Equal("New Claude account found: side@example.com", manager.Notices.Single().Message);
            Equal(sideKey, new AccountManager(settings, profile).Notices.Single().UnitKey);
            manager.AnswerNewAccount(sideKey, show: true);
            True(settings.IsUnitVisible(sideKey));
            Equal(0, manager.Notices.Count);

            // The same person in another folder is a duplicate and stays out of the tiles.
            var copy = Login(profile, ".claude-copy", ".credentials.json");
            File.WriteAllText(Path.Combine(copy, ".claude.json"), ClaudeJson("WORK@example.com", "org-1", "Acme"));
            manager.Scan(DateTimeOffset.UtcNow);
            var copyAccount = settings.FindAccount(ProviderKey.Compose("claude", ProviderKey.AccountIdFor(copy)))!;
            Equal("work@example.com", copyAccount.DuplicateOf);
            False(copyAccount.Pending);
            False(manager.CreateClients(Array.Empty<IUsageClient>()).Any(client => client.Id == copyAccount.UnitKey));

            // Adding by folder: refused for the default login, a repeat or a missing login.
            True(manager.AddFolder("claude", defaultClaude, AccountSource.Added, null).Problem!.StartsWith("Already shown as me@example.com", StringComparison.Ordinal));
            Equal("Already shown as work@example.com.", manager.AddFolder("claude", work, AccountSource.Added, null).Problem);
            Equal("Already shown as work@example.com.", manager.AddFolder("claude", copy, AccountSource.Added, null).Problem);
            True(manager.AddFolder("claude", Path.Combine(profile, "nothing"), AccountSource.Added, null).Problem is not null);
            var elsewhere = Login(NewDirectory("elsewhere"), "login", ".credentials.json");
            var added = manager.AddFolder("claude", elsewhere, AccountSource.Created, " Lab ");
            True(added.Added);
            Equal("Lab", settings.FindAccount(added.UnitKey!)!.Label);
            Equal(AccountSource.Created, settings.FindAccount(added.UnitKey!)!.Source);

            // A hidden account added again by folder is shown again.
            settings.FindAccount(sideKey)!.Hidden = true;
            True(manager.AddFolder("claude", side, AccountSource.Added, "Side gig").Added);
            Equal("Side gig", settings.FindAccount(sideKey)!.Label);

            // Locate moves an account; a bad folder keeps it where it was.
            Equal("That folder does not exist.", manager.Locate(added.UnitKey!, Path.Combine(profile, "gone")).Problem);
            True(settings.FindAccount(added.UnitKey!) is not null);
            Equal("Only an account read from its own folder can be located.", manager.Locate("claude", elsewhere).Problem);

            // Remove deletes only a folder UsageAI created; forgetting a found one leaves it and is remembered.
            Equal("The default login cannot be removed; hide it instead.", manager.Remove("claude", deleteCreatedFolder: true));
            Equal(null, manager.Remove(added.UnitKey!, deleteCreatedFolder: true));
            False(Directory.Exists(elsewhere));
            Equal(null, manager.Remove(workKey, deleteCreatedFolder: true));
            True(Directory.Exists(work));
            True(settings.ForgottenAccounts.Contains(workKey));
            manager.Scan(DateTimeOffset.UtcNow);
            Equal(null, settings.FindAccount(workKey));

            // A folder now signed in to someone else gets its own banner.
            manager.NotifyIdentityChanged("claude", "me@example.com", "team@example.com");
            Equal("Your default Claude login is now team@example.com (was me@example.com).", manager.Notices.Single().Message);
            manager.NotifyIdentityChanged(sideKey, null, "x@example.com");
            True(manager.Notices[^1].Message.EndsWith("is now x@example.com.", StringComparison.Ordinal));
            manager.Dismiss(manager.Notices[0]);
            manager.Dismiss(manager.Notices[0]);
            Equal(0, manager.Notices.Count);

            // Automatic scanning off: no listing after the first run.
            settings.FindAccountsAutomatically = false;
            False(manager.ScanIfDue(DateTimeOffset.UtcNow.AddDays(9)));
            Login(profile, ".claude-late", ".credentials.json");
            Equal(0, manager.Scan(DateTimeOffset.UtcNow));
        }
        finally
        {
            TryDelete(profile);
        }

        // Created folders are listed for the uninstaller, and the list goes when none are left.
        var persistentProfile = NewDirectory("created");
        try
        {
            var settings = new AppSettings();
            var manager = new AccountManager(settings, persistentProfile, providerId => Path.Combine(persistentProfile, "." + providerId));
            var folder = Login(persistentProfile, "made-by-usageai", "auth.json");
            var key = manager.AddFolder("codex", folder, AccountSource.Created, null).UnitKey!;
            Equal(ProviderKey.NormalizeFolder(folder), File.ReadAllText(AppPaths.CreatedLoginsFile).Trim());
            manager.Remove(key, deleteCreatedFolder: false);
            True(Directory.Exists(folder));
            False(File.Exists(AppPaths.CreatedLoginsFile));
            Equal(2, manager.CreateClients(UsageAI.Program.CreateUsageClients().Take(2)).Count);
        }
        finally
        {
            TryDelete(persistentProfile);
        }

        return Task.CompletedTask;
    }

    public static async Task TestAccountClientsReadOwnFolderAsync()
    {
        var environment = SaveEnvironment(
            "USAGEAI_CLAUDE_OAUTH_TOKEN",
            "USAGEAI_CLAUDE_SESSION_KEY",
            "CLAUDE_CONFIG_DIR",
            "CLAUDE_PATH",
            "CODEX_HOME");
        var root = NewDirectory("clients");
        try
        {
            var defaultFolder = Path.Combine(root, "default");
            Directory.CreateDirectory(defaultFolder);
            var account = Path.Combine(root, ".claude-team");
            Directory.CreateDirectory(account);
            Environment.SetEnvironmentVariable("USAGEAI_CLAUDE_OAUTH_TOKEN", "default-only-token");
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", defaultFolder);
            Environment.SetEnvironmentVariable("CLAUDE_PATH", Environment.ProcessPath);

            var credentials =
                "{\"claudeAiOauth\":{\"accessToken\":\"expired-team-token\",\"refreshToken\":\"r\",\"expiresAt\":" +
                DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds() +
                ",\"scopes\":[\"user:profile\"],\"subscriptionType\":\"max\"}}";
            File.WriteAllText(Path.Combine(account, ".credentials.json"), credentials);
            var tokens = new List<string>();
            using var http = new HttpClient(new Handler(request =>
            {
                tokens.Add($"{request.RequestUri!.Host} {request.Headers.Authorization}");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"five_hour":{"utilization":40}}""", Encoding.UTF8, "application/json"),
                };
            }));
            var keyringRead = false;
            var client = new ClaudeCodeUsageClient(
                http,
                () =>
                {
                    keyringRead = true;
                    return KeyringEntries;
                },
                cancellationToken => ClaudeCodeUsageClient.RunClaudeAuthRecoveryAsync(account, cancellationToken),
                account);
            var key = ProviderKey.Compose("claude", ProviderKey.AccountIdFor(account));
            Equal(key, client.Id);
            Equal("claude", client.ProviderId);
            Equal(ProviderKey.NormalizeFolder(account), client.LoginFolder);
            True(client.SignInCommand.StartsWith("$env:CLAUDE_CONFIG_DIR=", StringComparison.Ordinal));

            // The expired token is refreshed by the CLI in this folder alone, never through the
            // default login's folder, the environment token, or the Credential Manager.
            var snapshot = await client.GetUsageAsync();
            Equal(key, snapshot.ProviderId);
            Equal(40, snapshot.Primary!.UsedPercent);
            Equal("api.anthropic.com Bearer owner-refreshed-access", tokens.Single());
            False(keyringRead);
            False(File.Exists(Path.Combine(defaultFolder, ".credentials.json")));

            // A folder with no login, or one that is gone, says where it looked.
            File.Delete(Path.Combine(account, ".credentials.json"));
            var notSignedIn = await ThrowsAsync<ClaudeCodeUsageException>(() => new ClaudeCodeUsageClient(http, null, _ => Task.FromResult(false), account).GetUsageAsync());
            True(notSignedIn.Message.Contains("not signed in at", StringComparison.Ordinal));
            var gone = await ThrowsAsync<ClaudeCodeUsageException>(() => new ClaudeCodeUsageClient(Path.Combine(root, "gone")).GetUsageAsync());
            True(gone.Message.StartsWith("Claude login not found at", StringComparison.Ordinal));
            Equal("claude", new ClaudeCodeUsageClient().Id);
            Equal("claude", new ClaudeCodeUsageClient().SignInCommand);
            Equal(ProviderKey.NormalizeFolder(defaultFolder), new ClaudeCodeUsageClient().LoginFolder);

            // Codex: the folder's own auth.json goes to chatgpt.com, and the snapshot carries the key.
            var codexHome = Path.Combine(root, ".codex-team");
            Directory.CreateDirectory(codexHome);
            File.WriteAllText(Path.Combine(codexHome, "auth.json"), CodexAuth("team@example.com", "ws-1", accessToken: "team-access-token"));
            var codexRequests = new List<string>();
            using var codexHttp = new HttpClient(new Handler(request =>
            {
                codexRequests.Add($"{request.RequestUri!.Host} {request.Headers.Authorization}");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":10,"limit_window_seconds":18000,"reset_after_seconds":60}}}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            }));
            var codex = new CodexUsageClient(
                codexHttp,
                () => CodexUsageClient.ReadHttpCredentials(codexHome, DateTimeOffset.UtcNow),
                _ => throw new InvalidOperationException("The app-server must not start."),
                codexHome);
            var codexKey = ProviderKey.Compose("codex", ProviderKey.AccountIdFor(codexHome));
            Equal(codexKey, codex.Id);
            Equal(codexKey, (await codex.GetUsageAsync()).ProviderId);
            Equal("chatgpt.com Bearer team-access-token", codexRequests.Single());
            True(codex.SignInCommand.EndsWith("; codex login", StringComparison.Ordinal));
            Equal(ProviderKey.NormalizeFolder(codexHome), codex.LoginFolder);
            var missing = await ThrowsAsync<CodexUsageException>(() => new CodexUsageClient(Path.Combine(root, "gone")).GetUsageAsync());
            True(missing.Message.StartsWith("Codex login not found at", StringComparison.Ordinal));
            Equal("codex", new CodexUsageClient().Id);

            // The official logins are started with the folder variable set to the new folder.
            var claudeLogin = ClaudeCodeUsageClient.CreateLoginStartInfo(account)!;
            Equal(account, claudeLogin.Environment["CLAUDE_CONFIG_DIR"]);
            Equal("auth|login|--claudeai", string.Join('|', claudeLogin.ArgumentList));
        }
        finally
        {
            RestoreEnvironment(environment);
            TryDelete(root);
        }
    }

    public static async Task TestRefreshServiceKeepsAccountsApartAsync()
    {
        var root = NewDirectory("service");
        try
        {
            var folder = Path.Combine(root, ".claude-team");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, ".claude.json"), ClaudeJson("team@example.com", "org-1", "Acme"));
            var defaultFolder = Path.Combine(root, ".claude");
            Directory.CreateDirectory(defaultFolder);
            File.WriteAllText(Path.Combine(defaultFolder, ".claude.json"), ClaudeJson("me@example.com", "org-0", "Personal"));
            var settings = new AppSettings { IsEphemeral = true, NotifyAtPercent = new[] { 50 } };
            var teamKey = ProviderKey.Compose("claude", ProviderKey.AccountIdFor(folder));
            settings.Accounts.Add(new ProviderAccount { ProviderId = "claude", Id = ProviderKey.AccountIdFor(folder), Source = AccountSource.Found, Path = folder, Order = 1 });
            var primary = new AccountStub("claude", defaultFolder) { Used = 10 };
            var team = new AccountStub(teamKey, folder) { Used = 20 };
            using var service = new UsageRefreshService(new IUsageClient[] { primary }, settings);
            var changes = new List<AccountIdentityChangedEventArgs>();
            var alerts = new List<UsageAlert>();
            service.AccountIdentityChanged += (_, eventArgs) => changes.Add(eventArgs);
            service.AlertsRaised += (_, eventArgs) => alerts.AddRange(eventArgs.Alerts);

            // One account: the plain provider name, as before.
            await service.RefreshAsync(force: true, anyWindowVisible: false);
            Equal("Claude Code", service.Statuses.Single().ProviderName);
            Equal(null, service.Statuses.Single().AccountLabel);
            Equal("me@example.com", settings.FindAccount("claude")!.Email);
            Equal(0, changes.Count);

            // A second account joins due at once; both tiles carry their label.
            service.ReplaceClients(new IUsageClient[] { primary, team });
            True(service.IsDue(DateTimeOffset.Now));
            Equal(2, service.AllStatuses.Count);
            await service.RefreshDueAsync(anyWindowVisible: false);
            Equal($"Claude Code · me@example.com|Claude Code · team@example.com", string.Join('|', service.Statuses.Select(status => status.ProviderName)));
            Equal(20, service.Statuses[1].Snapshot!.Primary!.UsedPercent);
            Equal("claude", service.Statuses[1].Kind);

            // Alerts name the account; an account with alerts off stays quiet.
            team.Used = 60;
            primary.Used = 70;
            settings.FindAccount("claude")!.Alerts = false;
            await service.RefreshAsync(force: true, anyWindowVisible: false);
            Equal("Claude · team@example.com: 5-hour 50% used", alerts.Single().Title);

            // The folder signed in to someone else: announced, and its history starts afresh.
            team.Used = 33;
            File.WriteAllText(Path.Combine(folder, ".claude.json"), ClaudeJson("other@example.com", "org-9", "Else"));
            await service.RefreshAsync(force: true, anyWindowVisible: false);
            Equal(teamKey, changes.Single().UnitKey);
            Equal("team@example.com", changes.Single().Previous);
            Equal("other@example.com", changes.Single().Current);
            False(service.History.Any(sample => sample.ProviderId == teamKey && sample.UsedPercent != 33));
            True(service.History.Any(sample => sample.ProviderId == teamKey && sample.UsedPercent == 33));

            // Hidden, it drops out of the tiles and the labels; removed, it is forgotten.
            settings.FindAccount(teamKey)!.Hidden = true;
            Equal("Claude Code", service.Statuses.Single().ProviderName);
            service.ReplaceClients(new IUsageClient[] { primary });
            Equal(1, service.AllStatuses.Count);
            Throws<ArgumentException>(() => service.ReplaceClients(Array.Empty<IUsageClient>()));

            // The tray follows a default login unless an account is picked.
            var statuses = new[]
            {
                new ProviderStatus(teamKey, "Claude Code · team", Snapshot(teamKey, 90), null, false),
                new ProviderStatus("codex", "Codex", Snapshot("codex", 30), null, false),
            };
            Equal("codex", UsageApplicationContext.SelectTrayStatus(statuses, null)!.ProviderId);
            Equal(teamKey, UsageApplicationContext.SelectTrayStatus(statuses, teamKey)!.ProviderId);
            Equal(teamKey, UsageApplicationContext.SelectTrayStatus(statuses.Take(1).ToArray(), null)!.ProviderId);
            Equal("UsageAI - Claude · team: 90% used", UsageApplicationContext.TrayTooltip(statuses[0] with { AccountLabel = "team" }));
        }
        finally
        {
            TryDelete(root);
        }
    }

    public static async Task TestAccountSignInAsync()
    {
        var root = NewDirectory("login");
        try
        {
            ProcessStartInfo Fake(string variable, string folder, params string[] extra)
            {
                var info = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                };
                ProcessSecurity.ApplyMinimalEnvironment(info);
                info.Environment[variable] = folder;
                info.ArgumentList.Add("usageai-fake-login");
                foreach (var argument in extra)
                {
                    info.ArgumentList.Add(argument);
                }

                return info;
            }

            // Signed in: the link the CLI printed is offered, and the credential file is there.
            var folder = Path.Combine(root, ".claude-new");
            Uri? link = null;
            var result = await AccountLogin.RunAsync("claude", folder, found => link = found, CancellationToken.None, createStartInfo: path => Fake("CLAUDE_CONFIG_DIR", path));
            Equal(AccountLoginOutcome.SignedIn, result.Outcome);
            Equal("https://example.com/oauth/authorize?state=fake", link!.AbsoluteUri);
            True(File.Exists(Path.Combine(folder, ".credentials.json")));

            // Signing the same folder in again waits for a freshly written login.
            var again = await AccountLogin.RunAsync("claude", folder, null, CancellationToken.None, createStartInfo: path => Fake("CLAUDE_CONFIG_DIR", path));
            Equal(AccountLoginOutcome.SignedIn, again.Outcome);

            // Cancelled or timed out: the folder this attempt made is removed again.
            var cancelled = Path.Combine(root, ".codex-cancelled");
            using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(700)))
            {
                var outcome = await AccountLogin.RunAsync("codex", cancelled, null, cancel.Token, createStartInfo: path => Fake("CODEX_HOME", path, "hang"));
                Equal(AccountLoginOutcome.Cancelled, outcome.Outcome);
            }

            False(Directory.Exists(cancelled));
            var slow = Path.Combine(root, ".codex-slow");
            var timedOut = await AccountLogin.RunAsync("codex", slow, null, CancellationToken.None, TimeSpan.FromMilliseconds(700), path => Fake("CODEX_HOME", path, "hang"));
            Equal(AccountLoginOutcome.TimedOut, timedOut.Outcome);
            False(Directory.Exists(slow));

            // No CLI, or a CLI that exits without signing in.
            var missing = await AccountLogin.RunAsync("claude", Path.Combine(root, "x"), null, CancellationToken.None, createStartInfo: _ => null);
            Equal(AccountLoginOutcome.Failed, missing.Outcome);
            True(missing.Message!.Contains("CLI was not found", StringComparison.Ordinal));
            var exits = Path.Combine(root, ".claude-exits");
            var failed = await AccountLogin.RunAsync("claude", exits, null, CancellationToken.None, createStartInfo: path => Fake("CLAUDE_CONFIG_DIR", path, "exit"));
            Equal(AccountLoginOutcome.Failed, failed.Outcome);
            False(Directory.Exists(exits));
        }
        finally
        {
            TryDelete(root);
        }
    }

    public static async Task TestAccountSettingsAndBannerAsync()
    {
        var profile = NewDirectory("ui");
        try
        {
            var work = Login(profile, ".claude-work", ".credentials.json");
            File.WriteAllText(Path.Combine(work, ".claude.json"), ClaudeJson("work@example.com", "org-1", "Acme"));
            var created = Login(profile, ".codex-lab", "auth.json");
            var settings = new AppSettings { IsEphemeral = true, AccountsInitialized = true };
            var manager = new AccountManager(settings, profile, providerId => Path.Combine(profile, "." + providerId), account => new StubClient(account.UnitKey));
            var workKey = manager.AddFolder("claude", work, AccountSource.Found, null).UnitKey!;
            var labKey = manager.AddFolder("codex", created, AccountSource.Created, "lab").UnitKey!;
            settings.FindAccount(labKey)!.Pending = true;

            using var dialog = new SettingsForm(
                settings,
                new[] { ("codex", "Codex"), ("claude", "Claude Code") },
                _ => Task.FromResult(new UpdateCheckResult(true, null)),
                _ => Task.CompletedTask,
                dpiOverride: 96,
                readStartup: static () => new StartupState(StartupStatus.NotRegistered, null),
                writeStartup: _ => { },
                accounts: new SettingsAccountActions(null, manager),
                accountStatuses: new[] { new ProviderStatus(workKey, "Claude Code", Snapshot(workKey, 5), "Claude login has expired.", false) },
                trayChoices: new[] { ("claude", "Claude Code · me"), (workKey, "Claude Code · work@example.com") });
            dialog.Show();
            Application.DoEvents();
            dialog.ShowSection(SettingsForm.AccountsSection);

            var list = Field<CheckedListBox>(dialog, "_accountList");
            var items = list.Items.Cast<object>().Select(item => item.ToString()!).ToArray();
            Equal(4, items.Length);
            True(items.Any(item => item.StartsWith("Claude · work@example.com — Expired", StringComparison.Ordinal)));
            True(items.Any(item => item.StartsWith("Codex · lab — New", StringComparison.Ordinal)));
            True(items.Any(item => item.StartsWith("Claude · Default login", StringComparison.Ordinal)));
            var tray = Field<ComboBox>(dialog, "_trayProvider");
            True(tray.Items.Cast<object>().Any(item => item.ToString() == "Claude Code · work@example.com"));

            // Selecting an account shows where it is read from; edits wait for Save.
            list.SelectedIndex = Array.FindIndex(items, item => item.StartsWith("Claude · work", StringComparison.Ordinal));
            Application.DoEvents();
            True(Field<Label>(dialog, "_accountDetails").Text.Contains("Found in", StringComparison.Ordinal));
            Equal("Forget", Field<Button>(dialog, "_removeAccount").Text);
            Field<ToggleSwitch>(dialog, "_accountAlerts").Checked = false;
            list.SetItemChecked(list.SelectedIndex, false);
            Invoke(dialog, "MoveSelectedAccount", -1);
            True(settings.FindAccount(workKey)!.Alerts);

            list.SelectedIndex = Array.FindIndex(list.Items.Cast<object>().Select(item => item.ToString()!).ToArray(), item => item.StartsWith("Codex · lab", StringComparison.Ordinal));
            Application.DoEvents();
            Equal("Remove", Field<Button>(dialog, "_removeAccount").Text);
            True(Field<Label>(dialog, "_accountDetails").Text.Contains("Created by UsageAI", StringComparison.Ordinal));
            list.SetItemChecked(list.SelectedIndex, true);
            Field<ToggleSwitch>(dialog, "_askBeforeShowing").Checked = false;
            Field<Button>(dialog, "_saveButton").PerformClick();
            Application.DoEvents();

            var saved = settings.FindAccount(workKey)!;
            False(saved.Alerts);
            True(saved.Hidden);
            Equal(0, saved.Order);
            Equal(1, settings.FindAccount("claude")!.Order);
            False(settings.FindAccount(labKey)!.Pending);
            True(settings.IsUnitVisible(labKey));
            False(settings.AskBeforeShowingNewAccounts);

            // The popup's banner shows the first notice and reports the answer.
            manager.NotifyIdentityChanged("claude", null, "team@example.com");
            using var popup = new UsagePopupForm(settings, 96) { StaysOpenWhenInactive = true };
            AccountNoticeEventArgs? answered = null;
            popup.NoticeAnswered += (_, eventArgs) => answered = eventArgs;
            popup.SetNotices(manager.Notices);
            popup.ShowNearTray(DashboardMode.Compact);
            Application.DoEvents();
            Equal(AccountNoticeKind.IdentityChanged, popup.ShownNotice!.Kind);
            var banner = Field<AccountNoticeBanner>(popup, "_banner");
            var buttons = banner.Controls.OfType<Button>().ToArray();
            True(buttons.Any(button => button.Text == "Sign the old account in again…"));
            buttons.Single(button => button.Text == "Dismiss").PerformClick();
            False(answered!.Primary);
            using (var bitmap = new Bitmap(banner.Width, Math.Max(1, banner.Height)))
            {
                banner.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            }

            popup.SetNotices(Array.Empty<AccountNotice>());
            Equal(null, popup.ShownNotice);
            popup.SetNotices(new[]
            {
                new AccountNotice(AccountNoticeKind.NewAccount, "codex", "New Codex account found: x", "codex@00000000"),
                new AccountNotice(AccountNoticeKind.FoundSeveral, "claude", "Found 2 Claude accounts: a, b"),
            });
            True(banner.Controls.OfType<Label>().Single().Text.EndsWith("(+1 more)", StringComparison.Ordinal));
            buttons.Single(button => button.Text == "Show").PerformClick();
            True(answered!.Primary);
            popup.CloseForExit();
            dialog.Close();
            await Task.CompletedTask;
        }
        finally
        {
            TryDelete(profile);
        }
    }

    public static async Task TestApplicationContextAccountsAsync()
    {
        var profile = NewDirectory("context");
        try
        {
            var work = Login(profile, ".claude-work", ".credentials.json");
            File.WriteAllText(Path.Combine(work, ".claude.json"), ClaudeJson("work@example.com", "org-1", "Acme"));
            Login(profile, ".claude", ".credentials.json");
            File.WriteAllText(Path.Combine(profile, ".claude.json"), ClaudeJson("me@example.com", "org-0", "Personal"));
            var settings = new AppSettings { IsEphemeral = true };
            var manager = new AccountManager(settings, profile, providerId => Path.Combine(profile, "." + providerId), account => new StubClient(account.UnitKey));
            using var context = new UsageApplicationContext(
                new IUsageClient[] { new StubClient("claude") },
                settings,
                showTrayIcon: false,
                accounts: manager);

            // The startup scan finds the second login, which joins the clients and the banner.
            Invoke(context, "ScanAccountsIfDue", true);
            Application.DoEvents();
            var workKey = ProviderKey.Compose("claude", ProviderKey.AccountIdFor(work));
            Equal(2, Field<IUsageClient[]>(context, "_clients").Length);
            var popup = Field<UsagePopupForm>(context, "_popup");
            Equal(AccountNoticeKind.FoundSeveral, manager.Notices.Single().Kind);
            context.AnswerNotice(manager.Notices.Single(), primary: true);
            Equal(0, manager.Notices.Count);

            // Show / Ignore on a later find.
            var side = Login(profile, ".claude-side", ".credentials.json");
            manager.Scan(DateTimeOffset.UtcNow);
            Application.DoEvents();
            var sideKey = ProviderKey.Compose("claude", ProviderKey.AccountIdFor(side));
            context.AnswerNotice(manager.Notices.Single(), primary: false);
            True(settings.FindAccount(sideKey)!.Hidden);

            // Removing an account drops its client.
            manager.Remove(workKey, deleteCreatedFolder: false);
            Application.DoEvents();
            Equal(2, Field<IUsageClient[]>(context, "_clients").Length);
            False(Field<IUsageClient[]>(context, "_clients").Any(client => client.Id == workKey));
            Invoke(context, "ScanAccountsIfDue", false);

            // The identity-change banner reaches the popup.
            manager.NotifyIdentityChanged("claude", "me@example.com", "team@example.com");
            Application.DoEvents();
            Equal(AccountNoticeKind.IdentityChanged, manager.Notices.Single().Kind);
            True(UsageApplicationContext.PollingInputs(settings).Contains(sideKey.ToUpperInvariant(), StringComparison.Ordinal));
            await Task.CompletedTask;
        }
        finally
        {
            TryDelete(profile);
        }
    }

    public static Task TestAddAccountDialogAsync()
    {
        var profile = NewDirectory("dialog");
        try
        {
            var existing = Login(profile, ".claude-old", ".credentials.json");
            var settings = new AppSettings { IsEphemeral = true, AccountsInitialized = true };
            var manager = new AccountManager(settings, profile, providerId => Path.Combine(profile, "." + providerId), account => new StubClient(account.UnitKey));

            using (var dialog = new AddAccountForm(manager, "codex", dpiOverride: 96))
            {
                dialog.Show();
                Application.DoEvents();
                var folder = Field<TextBox>(dialog, "_folder");
                var name = Field<TextBox>(dialog, "_name");
                var primary = Field<Button>(dialog, "_primary");
                var status = Field<Label>(dialog, "_status");

                // Sign in proposes a folder from the name, and refuses a folder that cannot be used.
                Equal(Path.Combine(profile, ".codex-account-2"), folder.Text);
                name.Text = "Team";
                Equal(Path.Combine(profile, ".codex-team"), folder.Text);
                True(Field<Label>(dialog, "_command").Text.Contains("$env:CODEX_HOME=", StringComparison.Ordinal));
                Equal("Sign in", primary.Text);
                folder.Text = "relative";
                False(primary.Enabled);
                True(status.Text.Contains("full path", StringComparison.Ordinal));

                // A folder that already holds a login is used instead of signed in over.
                Field<SegmentedControl>(dialog, "_provider").SelectedIndex = 0;
                folder.Text = existing;
                Equal("Use this login instead", primary.Text);
                primary.PerformClick();
                Application.DoEvents();
                Equal(DialogResult.OK, dialog.DialogResult);
                Equal(ProviderKey.Compose("claude", ProviderKey.AccountIdFor(existing)), dialog.AddedKey);
            }

            using (var dialog = new AddAccountForm(manager, "claude", dpiOverride: 96))
            {
                dialog.Show();
                Field<SegmentedControl>(dialog, "_way").SelectedIndex = 1;
                Application.DoEvents();
                var folder = Field<TextBox>(dialog, "_folder");
                Equal(string.Empty, folder.Text);
                Equal("Add", Field<Button>(dialog, "_primary").Text);
                folder.Text = existing;
                Field<Button>(dialog, "_primary").PerformClick();
                True(Field<Label>(dialog, "_status").Text.StartsWith("Already shown as", StringComparison.Ordinal));
                Field<Button>(dialog, "_cancel").PerformClick();
                Equal(DialogResult.Cancel, dialog.DialogResult);
            }

            // Re-authenticating fixes the provider and folder.
            var account = settings.FindAccount(ProviderKey.Compose("claude", ProviderKey.AccountIdFor(existing)))!;
            using (var dialog = new AddAccountForm(manager, reauthenticate: account, dpiOverride: 96))
            {
                dialog.Show();
                Application.DoEvents();
                Equal("Sign in again", Field<Button>(dialog, "_primary").Text);
                True(Field<TextBox>(dialog, "_folder").ReadOnly);
                False(Field<SegmentedControl>(dialog, "_provider").Enabled);
                Invoke(dialog, "ShowLink", new Uri("https://example.com/x"));
                Invoke(dialog, "SetBusy", true);
                Invoke(dialog, "SetBusy", false);
            }

            Equal(null, TextPromptResultOrNull());
        }
        finally
        {
            TryDelete(profile);
        }

        return Task.CompletedTask;
    }

    /// <summary>The rename prompt, cancelled at once.</summary>
    private static string? TextPromptResultOrNull()
    {
        string? result = "unset";
        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            foreach (Form form in Application.OpenForms.Cast<Form>().ToArray())
            {
                if (form is TextPrompt prompt)
                {
                    prompt.DialogResult = DialogResult.Cancel;
                }
            }
        };
        timer.Start();
        result = TextPrompt.Ask(null, "Rename account", "Name:", "x");
        return result;
    }

    public static Task TestAccountDiagnosticsAsync()
    {
        var account = new ProviderAccount { ProviderId = "claude", Id = "1a2b3c4d", Source = AccountSource.Found, Path = @"C:\x\.claude-work", Email = "work@example.com" };
        var json = UsageAI.Program.DiagnosticJson(Snapshot("claude@1a2b3c4d", 5), Array.Empty<ProviderAttempt>(), account, new StubClient("claude@1a2b3c4d"));
        using var document = JsonDocument.Parse(json);
        var node = document.RootElement.GetProperty("Account");
        Equal("1a2b3c4d", node.GetProperty("Id").GetString());
        Equal("Found", node.GetProperty("Source").GetString());
        Equal(@"C:\x\.claude-work", node.GetProperty("Path").GetString());
        Equal("work@example.com", node.GetProperty("Label").GetString());
        False(json.Contains("token", StringComparison.OrdinalIgnoreCase));

        // A default login alone keeps today's shape.
        using var plain = JsonDocument.Parse(UsageAI.Program.DiagnosticJson(Snapshot("claude", 5), Array.Empty<ProviderAttempt>(), null, new StubClient("claude")));
        False(plain.RootElement.TryGetProperty("Account", out _));
        True(InvokeStatic<bool>(typeof(UsageAI.Program), "MatchesAccount", new StubClient("claude@1a2b3c4d"), account, "WORK@example.com"));
        True(InvokeStatic<bool>(typeof(UsageAI.Program), "MatchesAccount", new StubClient("claude@1a2b3c4d"), null, "1a2b3c4d"));
        False(InvokeStatic<bool>(typeof(UsageAI.Program), "MatchesAccount", new StubClient("claude"), null, "someone"));
        return Task.CompletedTask;
    }

    private static UsageSnapshot Snapshot(string key, int used) => new(
        "Pro",
        new[] { new UsageMetric("5-hour", UsageMetricKind.Session, used, DateTimeOffset.Now.AddHours(2), 300) },
        DateTimeOffset.Now,
        key,
        "Claude Code");

    private static ProviderAccount With(this ProviderAccount account, Action<ProviderAccount> change)
    {
        change(account);
        return account;
    }

    private sealed class StubClient(string id) : IUsageClient
    {
        public string Id => id;

        public string DisplayName => "Claude Code";

        public string SignInCommand => "claude";

        public Uri AccountUrl { get; } = new("https://claude.ai/settings/usage");

        public Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot(id, 1));
    }

    private sealed class AccountStub(string id, string folder) : IAccountUsageClient
    {
        public int Used { get; set; }

        public string Id => id;

        public string ProviderId => ProviderKey.ProviderOf(id);

        public string AccountId => ProviderKey.AccountOf(id);

        public string LoginFolder => folder;

        public string DisplayName => "Claude Code";

        public string SignInCommand => "claude";

        public Uri AccountUrl { get; } = new("https://claude.ai/settings/usage");

        public Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot("claude", Used));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static string ClaudeJson(string email, string organization, string organizationName) =>
        JsonSerializer.Serialize(new { oauthAccount = new { emailAddress = email, organizationUuid = organization, organizationName } });

    private static string CodexAuth(string email, string workspace, string accessToken = "a.b.c")
    {
        var payload = Base64Url(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["email"] = email,
            ["https://api.openai.com/auth"] = new { chatgpt_account_id = workspace },
        }));
        return JsonSerializer.Serialize(new { tokens = new { id_token = $"h.{payload}.s", access_token = accessToken, account_id = workspace } });
    }

    private static string Base64Url(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string NewDirectory(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "UsageAI.AccountTests", $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Login(string parent, string name, string credentialFile)
    {
        var folder = Path.Combine(parent, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, credentialFile), "{}");
        return ProviderKey.NormalizeFolder(folder);
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Temp folders are swept by the system.
        }
    }

    private static Dictionary<string, string?> SaveEnvironment(params string[] names) =>
        names.ToDictionary(name => name, Environment.GetEnvironmentVariable);

    private static void RestoreEnvironment(Dictionary<string, string?> saved)
    {
        foreach (var (name, value) in saved)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static T Field<T>(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing field {name}.");
        return (T)field.GetValue(target)!;
    }

    private static void Invoke(object target, string name, params object[] arguments)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing method {name}.");
        method.Invoke(target, arguments);
    }

    private static T InvokeStatic<T>(Type type, string name, params object?[] arguments)
    {
        var method = type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing method {name}.");
        return (T)method.Invoke(null, arguments)!;
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

    private static void True(bool condition, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Expected true: {expression}");
        }
    }

    private static void False(bool condition, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? expression = null) =>
        True(!condition, $"not ({expression})");

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private static void NotEqual<T>(T unexpected, T actual)
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
        {
            throw new InvalidOperationException($"Did not expect '{actual}'.");
        }
    }
}
