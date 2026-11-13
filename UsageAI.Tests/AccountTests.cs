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
            Login(profile, ".claude2", ".credentials.json");
            Login(profile, ".codex.work", "auth.json");
            Login(profile, ".claudx", ".credentials.json");
            Login(profile, ".codex-wrongfile", ".credentials.json");
            var defaultClaude = Login(profile, ".claude", ".credentials.json");
            Login(profile, ".codex", "auth.json");
            File.WriteAllText(Path.Combine(profile, ".claude.json"), ClaudeJson("me@example.com", "org-0", "Personal"));

            // One listing: any name that starts with .claude or .codex and goes on, holding that
            // provider's own credential file, and never the default folders.
            var found = AccountDiscovery.Scan(profile, new[] { defaultClaude, Path.Combine(profile, ".codex") });
            Equal(".claude-|.claude-work|.claude2|.claude_side|.clauder|.codex-team|.codex.work|.codex_two", string.Join('|', found
                .Select(login => Path.GetFileName(login.Folder))
                .Order(StringComparer.Ordinal)));
            Equal("codex", found.Single(login => login.Folder.EndsWith(".codex-team", StringComparison.Ordinal)).ProviderId);
            Equal("codex", found.Single(login => login.Folder.EndsWith(".codex.work", StringComparison.Ordinal)).ProviderId);
            Equal("claude", found.Single(login => login.Folder.EndsWith(".claude2", StringComparison.Ordinal)).ProviderId);
            Equal(0, AccountDiscovery.Scan(Path.Combine(profile, "missing"), Array.Empty<string>()).Count);
            Equal(null, AccountDiscovery.ProviderForFolderName(".claude"));
            Equal(null, AccountDiscovery.ProviderForFolderName(".CODEX"));
            Equal(null, AccountDiscovery.ProviderForFolderName(".claudx"));
            Equal(null, AccountDiscovery.ProviderForFolderName("claude-work"));
            Equal("codex", AccountDiscovery.ProviderForFolderName(".CODEX-Work"));
            Equal("claude", AccountDiscovery.ProviderForFolderName(".ClaudeWork"));

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
                Equal(null, AccountDiscovery.InheritedFolder("claude", profile));
                Equal(0, AccountDiscovery.InheritedLogins(profile).Count);

                // An inherited variable never moves the main login: it stays the standard folder,
                // and the folder the variable names is offered as a login of its own.
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", claudeWork);
                Environment.SetEnvironmentVariable("CODEX_HOME", $"\"{codexTeam}\"");
                Equal(ProviderKey.NormalizeFolder(Path.Combine(profile, ".claude")), AccountDiscovery.DefaultFolder("claude", profile));
                Equal(claudeWork, AccountDiscovery.InheritedFolder("claude", profile));
                Equal(codexTeam, AccountDiscovery.InheritedFolder("codex", profile));
                Equal("claude|codex", string.Join('|', AccountDiscovery.InheritedLogins(profile).Select(login => login.ProviderId)));
                // The variable naming the standard folder itself, a relative path, a share, or a
                // folder without a login is no extra login.
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", defaultClaude + "\\");
                Equal(null, AccountDiscovery.InheritedFolder("claude", profile));
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", "relative");
                Equal(null, AccountDiscovery.InheritedFolder("claude", profile));
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", @"\\server\share\.claude");
                Equal(null, AccountDiscovery.InheritedFolder("claude", profile));
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(profile, ".claude-empty"));
                Equal(Path.Combine(profile, ".claude-empty"), AccountDiscovery.InheritedFolder("claude", profile));
                Equal("codex", AccountDiscovery.InheritedLogins(profile).Single().ProviderId);

                // ~\.claude reads the profile beside it (~\.claude.json), or one inside it when that is the only one.
                Equal(Path.Combine(profile, ".claude.json"), AccountDiscovery.ClaudeIdentityFile(defaultClaude, profile));
                File.WriteAllText(Path.Combine(defaultClaude, ".claude.json"), "{}");
                Equal(Path.Combine(profile, ".claude.json"), AccountDiscovery.ClaudeIdentityFile(defaultClaude, profile));
                File.Move(Path.Combine(profile, ".claude.json"), Path.Combine(profile, ".claude.json.moved"));
                Equal(Path.Combine(defaultClaude, ".claude.json"), AccountDiscovery.ClaudeIdentityFile(defaultClaude, profile));
                File.Move(Path.Combine(profile, ".claude.json.moved"), Path.Combine(profile, ".claude.json"));
                Equal(@"%USERPROFILE%\.claude", AccountDiscovery.DisplayFolder(defaultClaude, profile));
                Equal(@"D:\elsewhere\.claude", AccountDiscovery.DisplayFolder(@"D:\elsewhere\.claude", profile));
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
            Equal(@"The login in %USERPROFILE%\.claude is always read; hide it instead.", manager.Remove("claude", deleteCreatedFolder: true));
            Equal(null, manager.Remove(added.UnitKey!, deleteCreatedFolder: true));
            False(Directory.Exists(elsewhere));
            Equal(null, manager.Remove(workKey, deleteCreatedFolder: true));
            True(Directory.Exists(work));
            True(settings.ForgottenAccounts.Contains(workKey));
            manager.Scan(DateTimeOffset.UtcNow);
            Equal(null, settings.FindAccount(workKey));

            // A folder now signed in to someone else gets its own banner.
            manager.NotifyIdentityChanged("claude", "me@example.com", "team@example.com");
            Equal(@"The Claude login in %USERPROFILE%\.claude is now team@example.com (was me@example.com).", manager.Notices.Single().Message);
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
            // The main login is the standard ~\.claude under the test-redirected profile; an
            // inherited CLAUDE_CONFIG_DIR (set below to another folder) must not move it.
            var defaultFolder = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE")!, ".claude");
            Directory.CreateDirectory(defaultFolder);
            var inheritedFolder = Path.Combine(root, "inherited");
            Directory.CreateDirectory(inheritedFolder);
            var account = Path.Combine(root, ".claude-lab");
            Directory.CreateDirectory(account);
            Environment.SetEnvironmentVariable("USAGEAI_CLAUDE_OAUTH_TOKEN", "default-only-token");
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", inheritedFolder);
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
            False(File.Exists(Path.Combine(inheritedFolder, ".credentials.json")));

            // A folder with no login, or one that is gone, says where it looked.
            File.Delete(Path.Combine(account, ".credentials.json"));
            var notSignedIn = await ThrowsAsync<ClaudeCodeUsageException>(() => new ClaudeCodeUsageClient(http, null, _ => Task.FromResult(false), account).GetUsageAsync());
            True(notSignedIn.Message.Contains("not signed in at", StringComparison.Ordinal));
            var gone = await ThrowsAsync<ClaudeCodeUsageException>(() => new ClaudeCodeUsageClient(Path.Combine(root, "gone")).GetUsageAsync());
            True(gone.Message.StartsWith("Claude login not found at", StringComparison.Ordinal));
            Equal("claude", new ClaudeCodeUsageClient().Id);
            // The variable is inherited here, so the main login's sign-in clears it first.
            Equal("Remove-Item Env:CLAUDE_CONFIG_DIR -ErrorAction Ignore; claude", new ClaudeCodeUsageClient().SignInCommand);
            Equal(
                "Remove-Item Env:CLAUDE_CONFIG_DIR -ErrorAction Ignore; claude",
                AccountCommands.SignInMain("claude", new System.Collections.Hashtable { ["CLAUDE_CONFIG_DIR"] = @"C:\elsewhere" }));
            Equal(
                "Remove-Item Env:CODEX_HOME -ErrorAction Ignore; codex login",
                AccountCommands.SignInMain("codex", new System.Collections.Hashtable { ["CODEX_HOME"] = @"C:\elsewhere" }));
            Equal("codex login", AccountCommands.SignInMain("codex", new System.Collections.Hashtable()));
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
            var folder = Path.Combine(root, ".claude-lab");
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
            // Who is signed in is read again only once the login itself was written, as a sign-in is.
            File.WriteAllText(Path.Combine(folder, ".claude.json"), ClaudeJson("other@example.com", "org-9", "Else"));
            await service.RefreshAsync(force: true, anyWindowVisible: false);
            Equal(0, changes.Count);
            File.WriteAllText(Path.Combine(folder, ".credentials.json"), "{}");
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
            True(items.Any(item => item.StartsWith("Claude · .claude", StringComparison.Ordinal)));
            // The main login carries its label in the tray list, and each account follows its provider.
            var tray = Field<ComboBox>(dialog, "_trayProvider");
            Equal(
                "Automatic|All connected|Codex|Claude Code · me|Claude Code · work@example.com",
                string.Join("|", tray.Items.Cast<object>().Select(item => item.ToString())));
            Equal(
                "Codex|Claude Code",
                string.Join("|", Field<CheckedListBox>(dialog, "_providers").Items.Cast<object>().Select(item => item.ToString())));

            // Selecting an account shows where it is read from; each change is saved as it is made.
            var changes = SettingsChanges.None;
            dialog.SettingsChanged += (_, eventArgs) => changes |= eventArgs.Changes;
            list.SelectedIndex = Array.FindIndex(items, item => item.StartsWith("Claude · work", StringComparison.Ordinal));
            Application.DoEvents();
            True(Field<Label>(dialog, "_accountDetails").Text.Contains("Found in", StringComparison.Ordinal));
            Equal("Forget", Field<Button>(dialog, "_removeAccount").Text);
            Equal(SettingsChanges.None, changes);
            Field<ToggleSwitch>(dialog, "_accountAlerts").Checked = false;
            False(settings.FindAccount(workKey)!.Alerts);
            list.SetItemChecked(list.SelectedIndex, false);
            True(settings.FindAccount(workKey)!.Hidden);
            // Hiding an account changes what is read, so the app rebuilds its clients and polls.
            True(changes.HasFlag(SettingsChanges.Accounts) && changes.HasFlag(SettingsChanges.Polling));
            Invoke(dialog, "MoveSelectedAccount", -1);
            Equal(0, settings.FindAccount(workKey)!.Order);
            Equal(1, settings.FindAccount("claude")!.Order);

            list.SelectedIndex = Array.FindIndex(list.Items.Cast<object>().Select(item => item.ToString()!).ToArray(), item => item.StartsWith("Codex · lab", StringComparison.Ordinal));
            Application.DoEvents();
            Equal("Remove", Field<Button>(dialog, "_removeAccount").Text);
            True(Field<Label>(dialog, "_accountDetails").Text.Contains("Created by UsageAI", StringComparison.Ordinal));
            list.SetItemChecked(list.SelectedIndex, true);
            False(settings.FindAccount(labKey)!.Pending);
            True(settings.IsUnitVisible(labKey));
            Field<ToggleSwitch>(dialog, "_askBeforeShowing").Checked = false;
            False(settings.AskBeforeShowingNewAccounts);
            tray.SelectedIndex = 3;
            Equal("claude", settings.TrayProviderId);

            // Removing an account UsageAI created asks first, with No as Enter's answer.
            SettingsPrompt? asked = null;
            dialog.Confirm = prompt =>
            {
                asked = prompt;
                return false;
            };
            Invoke(dialog, "RemoveSelectedAccount");
            True(asked!.DeclineIsDefault);
            NotNull(settings.FindAccount(labKey));

            // An account change made elsewhere — a scan, the popup's Add account — reaches the
            // list once the event that raised it has finished.
            settings.FindAccount(workKey)!.Label = "Renamed elsewhere";
            dialog.AccountsChangedElsewhere();
            dialog.AccountsChangedElsewhere();
            Application.DoEvents();
            True(list.Items.Cast<object>().Any(item => item.ToString()!.StartsWith("Claude · Renamed elsewhere", StringComparison.Ordinal)));

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

    public static async Task TestAccountReviewFixesAsync()
    {
        var profile = NewDirectory("fixes");
        try
        {
            var defaultClaude = Login(profile, ".claude", ".credentials.json");
            File.WriteAllText(Path.Combine(profile, ".claude.json"), ClaudeJson("me@example.com", "org-0", "Personal"));
            var settings = new AppSettings { IsEphemeral = true, AccountsInitialized = true, AskBeforeShowingNewAccounts = false };
            var manager = new AccountManager(settings, profile, providerId => Path.Combine(profile, "." + providerId), account => new StubClient(account.UnitKey));

            // Forgetting a folder added by hand keeps the scan from offering it again.
            var work = Login(profile, ".claude-work", ".credentials.json");
            File.WriteAllText(Path.Combine(work, ".claude.json"), ClaudeJson("work@example.com", "org-1", "Acme"));
            var workKey = manager.AddFolder("claude", work, AccountSource.Added, null).UnitKey!;
            Equal(null, manager.Remove(workKey, deleteCreatedFolder: true));
            True(Directory.Exists(work));
            Equal(0, manager.Scan(DateTimeOffset.UtcNow));
            Equal(null, settings.FindAccount(workKey));
            Equal(0, manager.Notices.Count);

            // A duplicate is shown again once the account it repeated signs in to someone else.
            var alt = Login(profile, ".claude-alt", ".credentials.json");
            File.WriteAllText(Path.Combine(alt, ".claude.json"), ClaudeJson("me@example.com", "org-0", "Personal"));
            manager.Scan(DateTimeOffset.UtcNow);
            var altKey = ProviderKey.Compose("claude", ProviderKey.AccountIdFor(alt));
            Equal("me@example.com", settings.FindAccount(altKey)!.DuplicateOf);
            False(manager.CreateClients(Array.Empty<IUsageClient>()).Any(client => client.Id == altKey));
            File.WriteAllText(Path.Combine(profile, ".claude.json"), ClaudeJson("new@example.com", "org-5", "New"));
            settings.EnsureAccount("claude").Identity = AccountDiscovery.ReadIdentity("claude", defaultClaude, profile)!.Hash;
            manager.NotifyIdentityChanged("claude", "me@example.com", "new@example.com");
            Equal(null, settings.FindAccount(altKey)!.DuplicateOf);
            True(manager.CreateClients(Array.Empty<IUsageClient>()).Any(client => client.Id == altKey));
            False(manager.RecheckDuplicates());

            // The newest forgotten folders are kept when the list is full.
            var file = new AppSettings
            {
                ForgottenAccounts = Enumerable.Range(0, AppSettings.MaxForgottenAccounts + 5).Select(index => $"claude@{index:x8}").ToArray(),
            };
            file.Save();
            var reloaded = AppSettings.Load().ForgottenAccounts;
            Equal(AppSettings.MaxForgottenAccounts, reloaded.Length);
            Equal($"claude@{AppSettings.MaxForgottenAccounts + 4:x8}", reloaded[^1]);
            Equal("claude@00000005", reloaded[0]);
        }
        finally
        {
            TryDelete(profile);
        }

        // A one-provider retry asked for while another refresh runs is due afterwards, not dropped.
        var slow = new GatedStub("codex", Task.CompletedTask);
        var other = new GatedStub("claude", Task.CompletedTask);
        using var service = new UsageRefreshService(new IUsageClient[] { slow, other }, new AppSettings { IsEphemeral = true });
        await service.RefreshAsync(force: true, anyWindowVisible: false);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        slow.Gate = gate.Task;
        var running = service.RefreshProviderAsync("codex", anyWindowVisible: false);
        await service.RefreshProviderAsync("claude", anyWindowVisible: false);
        Equal(1, other.Calls);
        gate.SetResult();
        await running;
        True(service.IsDue(DateTimeOffset.Now));
        await service.RefreshDueAsync(anyWindowVisible: false);
        Equal(2, other.Calls);

        // The banner re-measures for its window's DPI.
        using var banner = new AccountNoticeBanner(192);
        banner.ApplyScale();
        Equal(new Padding(20, 12, 16, 12), banner.Padding);
    }

    private sealed class GatedStub(string id, Task gate) : IUsageClient
    {
        public Task Gate { get; set; } = gate;

        public int Calls { get; private set; }

        public string Id => id;

        public string DisplayName => id;

        public string SignInCommand => id;

        public Uri AccountUrl { get; } = new("https://example.com/");

        public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            await Gate;
            return Snapshot(id, 1);
        }
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

    public static Task TestTerminalCommandsAsync()
    {
        // Names: the CLI and the account's label, else the folder's own suffix.
        Equal("claude-lab", TerminalCommands.Suggest("claude", "Lab", null));
        Equal("codex-work", TerminalCommands.Suggest("codex", null, @"C:\Users\me\.codex-work"));
        Equal("claude-account-2", TerminalCommands.Suggest("claude", "Claude", null));
        Equal(null, TerminalCommands.NameProblem("claude-lab"));
        True(TerminalCommands.NameProblem("claude")!.Contains("standard folder", StringComparison.Ordinal));
        NotNull(TerminalCommands.NameProblem("bad name"));
        NotNull(TerminalCommands.NameProblem("-x"));
        NotNull(TerminalCommands.NameProblem("con"));
        NotNull(TerminalCommands.NameProblem("claude-x.exe"));
        NotNull(TerminalCommands.NameProblem("claude-x."));
        Equal(null, TerminalCommands.NameProblem("claude-x.2"));
        NotNull(TerminalCommands.NameProblem(" "));

        var root = NewDirectory("terminal");
        try
        {
            var profile = Path.Combine(root, "profile");
            var bin = Path.Combine(root, "bin");
            var team = Login(profile, ".claude-lab", ".credentials.json");
            string? userPath = @"C:\Tools;%SystemRoot%";
            var writes = 0;
            string? cli = "claude";
            var terminal = new TerminalCommands(
                bin,
                () => userPath,
                value => { userPath = value; writes++; },
                name => name == "taken" ? @"C:\elsewhere\taken.cmd" : null,
                _ => cli,
                profile);

            // The file sets the folder (the profile written as %USERPROFILE%) and starts the CLI.
            var made = terminal.Create("claude", team, "claude-lab", previous: null);
            True(made.Succeeded);
            True(made.PathChanged);
            Equal(@$"C:\Tools;%SystemRoot%;{terminal.Folder}", userPath);
            var text = File.ReadAllText(terminal.FilePath("claude-lab"));
            True(text.StartsWith(TerminalCommands.Marker + "\r\n", StringComparison.Ordinal));
            True(text.Contains("set \"CLAUDE_CONFIG_DIR=%USERPROFILE%\\.claude-lab\"\r\n", StringComparison.Ordinal));
            True(text.Contains("\r\ncmd /d /c \"claude %*\"\r\n", StringComparison.Ordinal));

            // cmd never runs a claude.bat sitting in the current folder (a cloned repository) in
            // place of the CLI on the PATH; the setting stays inside setlocal.
            True(text.Contains("setlocal\r\nset \"NoDefaultCurrentDirectoryInExePath=1\"\r\n", StringComparison.Ordinal));

            // The PATH is written once; writing the same command again is fine.
            True(terminal.Create("claude", team, "claude-lab", previous: "claude-lab").Succeeded);
            Equal(1, writes);
            True(terminal.IsOnUserPath());

            // A command elsewhere on the PATH, or a file UsageAI did not write, is never replaced.
            True(terminal.Create("claude", team, "taken", null).Problem!.Contains(@"C:\elsewhere\taken.cmd", StringComparison.Ordinal));
            File.WriteAllText(terminal.FilePath("mine"), "@echo hand-written\r\n");
            NotNull(terminal.Create("claude", team, "mine", null).Problem);
            False(terminal.Remove("mine"));
            True(File.Exists(terminal.FilePath("mine")));

            // Renaming writes the new file and deletes the old one; a rename onto another file is refused.
            True(terminal.Create("claude", team, "claude-work", previous: "claude-lab").Succeeded);
            False(File.Exists(terminal.FilePath("claude-lab")));
            True(terminal.Create("claude", team, "claude-other", previous: null).Succeeded);
            NotNull(terminal.Create("claude", team, "claude-other", previous: "claude-work").Problem);

            // Paths a .cmd file cannot hold, a CLI with an absolute path, and no CLI at all.
            Equal(@"C:\a%%b\.claude-x", terminal.CmdText(@"C:\a%b\.claude-x"));
            Equal(null, terminal.CmdText(@"C:\Логин\.claude-x"));
            NotNull(terminal.Create("codex", @"C:\Логин\.codex-x", "codex-x", null).Problem);
            cli = "\"C:\\Program Files\\codex\\codex.exe\"";
            True(terminal.Create("codex", Path.Combine(root, ".codex-x"), "codex-x", null).Succeeded);
            var codexText = File.ReadAllText(terminal.FilePath("codex-x"));
            True(codexText.Contains("set \"CODEX_HOME=", StringComparison.Ordinal));
            True(codexText.Contains("\r\ncmd /d /c \"\"C:\\Program Files\\codex\\codex.exe\" %*\"\r\n", StringComparison.Ordinal));
            cli = null;
            True(terminal.Create("claude", team, "claude-none", null).Problem!.Contains("CLI was not found", StringComparison.Ordinal));

            // End to end: the file hands the login folder to an npm-style launcher. That launcher is
            // a batch file whose goto trick ends every batch level, which once dropped the folder
            // before the CLI started, so the default login answered. Arguments arrive untouched.
            var launcher = Path.Combine(root, "npm dir", "zzcli.cmd");
            Directory.CreateDirectory(Path.GetDirectoryName(launcher)!);
            // Like npm's, it starts the CLI (a child cmd here) only after the goto has unwound the batch levels.
            File.WriteAllText(launcher, "@ECHO off\r\nSETLOCAL\r\nendLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & cmd /d /c echo HOME=[%%CODEX_HOME%%] ARGS=[%*]\r\n");
            cli = $"\"{launcher}\"";
            var endToEndHome = Path.Combine(profile, ".codex-e2e");
            True(terminal.Create("codex", endToEndHome, "codex-e2e", null).Succeeded);
            var run = new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/d /c \"\"{terminal.FilePath("codex-e2e")}\" exec \"two words\"\"",
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            run.Environment["USERPROFILE"] = profile;
            run.Environment.Remove("CODEX_HOME");
            using (var process = Process.Start(run)!)
            {
                var output = process.StandardOutput.ReadToEnd();
                True(process.WaitForExit(15_000));
                True(output.Contains($"HOME=[{endToEndHome}]", StringComparison.OrdinalIgnoreCase), output);
                True(output.Contains("ARGS=[exec \"two words\"]", StringComparison.Ordinal), output);
            }

            cli = "claude";

            // A PATH that cannot be written still leaves a working file and says so.
            var failing = new TerminalCommands(bin, () => null, _ => throw new UnauthorizedAccessException(), _ => null, _ => "claude", profile);
            var unsaved = failing.Create("claude", team, "claude-nopath", null);
            True(unsaved.Succeeded);
            True(unsaved.PathUpdateFailed);

            // The manager keeps one command per account and deletes it with the account.
            var settings = new AppSettings { IsEphemeral = true, AccountsInitialized = true };
            var manager = new AccountManager(settings, profile, providerId => Path.Combine(profile, "." + providerId), account => new StubClient(account.UnitKey))
            {
                Terminal = terminal,
            };
            var other = Login(profile, ".claude-side", ".credentials.json");
            var teamKey = manager.AddFolder("claude", team, AccountSource.Added, "Lab").UnitKey!;
            var sideKey = manager.AddFolder("claude", other, AccountSource.Found, null).UnitKey!;
            NotNull(manager.SetTerminalCommand("claude", "claude-x").Problem);
            True(manager.SetTerminalCommand(teamKey, "claude-lab").Succeeded);
            Equal("claude-lab", settings.FindAccount(teamKey)!.Command);
            True(manager.SetTerminalCommand(sideKey, "CLAUDE-LAB").Problem!.Contains("Lab already uses", StringComparison.Ordinal));
            True(manager.SetTerminalCommand(sideKey, "claude-side").Succeeded);
            True(manager.SetTerminalCommand(sideKey, " ").Succeeded);
            Equal(null, settings.FindAccount(sideKey)!.Command);
            False(File.Exists(terminal.FilePath("claude-side")));

            // Locating the folder elsewhere rewrites the command for the new path.
            var moved = Login(root, ".claude-moved", ".credentials.json");
            var movedKey = manager.Locate(teamKey, moved).UnitKey!;
            Equal("claude-lab", settings.FindAccount(movedKey)!.Command);
            True(File.ReadAllText(terminal.FilePath("claude-lab")).Contains(moved, StringComparison.Ordinal));

            // A command that cannot follow the folder is deleted, and the move says so.
            cli = null;
            var again = Login(root, ".claude-again", ".credentials.json");
            var relocated = manager.Locate(movedKey, again);
            cli = "claude";
            True(relocated.Problem!.Contains("claude-lab was deleted", StringComparison.Ordinal));
            Equal(null, settings.FindAccount(relocated.UnitKey!)!.Command);
            False(File.Exists(terminal.FilePath("claude-lab")));
            True(manager.SetTerminalCommand(relocated.UnitKey!, "claude-lab").Succeeded);
            Equal(null, manager.Remove(relocated.UnitKey!, deleteCreatedFolder: false));
            False(File.Exists(terminal.FilePath("claude-lab")));
            Equal("Terminal commands are not available here.", new AccountManager(settings, profile).SetTerminalCommand(sideKey, "x").Problem);

            // A command whose CLI moved (an editor extension's version folder after an update) is
            // rewritten by the next scan; one already current, missing or not UsageAI's is left alone.
            True(manager.SetTerminalCommand(sideKey, "claude-side").Succeeded);
            False(terminal.Refresh("claude", other, "claude-side"));
            Equal(0, manager.RefreshTerminalCommands());
            cli = "\"C:\\Users\\me\\.vscode\\extensions\\anthropic.claude-code-2.0.1\\claude.exe\"";
            Equal(0, new AccountManager(settings, profile).RefreshTerminalCommands());
            manager.Scan(DateTimeOffset.UtcNow);
            True(File.ReadAllText(terminal.FilePath("claude-side")).Contains("claude-code-2.0.1", StringComparison.Ordinal));
            cli = null;
            False(terminal.Refresh("claude", other, "claude-side"));
            True(File.ReadAllText(terminal.FilePath("claude-side")).Contains("claude-code-2.0.1", StringComparison.Ordinal));
            cli = "claude";
            False(terminal.Refresh("claude", other, "mine"));
            Equal("@echo hand-written\r\n", File.ReadAllText(terminal.FilePath("mine")));
            False(terminal.Refresh("claude", other, "claude-missing"));
            False(terminal.Refresh("claude", other, "claude"));
            Equal(1, manager.RefreshTerminalCommands());
            True(File.ReadAllText(terminal.FilePath("claude-side")).Contains("\r\ncmd /d /c \"claude %*\"\r\n", StringComparison.Ordinal));

            // The popup's names are decided by provider: Claude Code reads as Claude, GitHub
            // Copilot as Copilot only in the headline, and every other name is left as it is.
            Equal("Claude", ProviderNames.Popup(new ProviderStatus("claude", "Claude Code", null, null, false)));
            Equal("Claude · Lab", ProviderNames.Popup(new ProviderStatus("claude@1a2b3c4d", "Claude Code · Lab", null, null, false) { AccountLabel = "Lab" }));
            Equal("GitHub Copilot", ProviderNames.Popup(new ProviderStatus("copilot", "GitHub Copilot", null, null, false)));
            Equal("Copilot", ProviderNames.Popup(new ProviderStatus("copilot", "GitHub Copilot", null, null, false), headline: true));
            Equal("Codex · Work", ProviderNames.Popup(new ProviderStatus("codex@1a2b3c4d", "Codex · Work", null, null, false) { AccountLabel = "Work" }, headline: true));

            // The real lookups: a command on the PATH (outside UsageAI's own folder) and the CLI to start.
            var environment = SaveEnvironment("PATH", "PATHEXT", "CLAUDE_PATH", "CODEX_PATH");
            try
            {
                var tools = Path.Combine(root, "tools");
                Directory.CreateDirectory(tools);
                File.WriteAllText(Path.Combine(tools, "claude-busy.cmd"), "@echo off\r\n");
                File.WriteAllText(Path.Combine(bin, "claude-ours.cmd"), TerminalCommands.Marker + "\r\n");
                File.WriteAllText(Path.Combine(tools, "codex.cmd"), "@echo off\r\n");
                Environment.SetEnvironmentVariable("PATH", $"{tools};relative;{bin}");
                Environment.SetEnvironmentVariable("PATHEXT", null);
                Environment.SetEnvironmentVariable("CODEX_PATH", null);
                Environment.SetEnvironmentVariable("CLAUDE_PATH", null);
                True(string.Equals(Path.Combine(tools, "claude-busy.cmd"), TerminalCommands.FindCommandOnPath("claude-busy", bin), StringComparison.OrdinalIgnoreCase));
                Equal(null, TerminalCommands.FindCommandOnPath("claude-ours", bin));
                Equal("codex", TerminalCommands.FindCli("codex"));
                var claudeExe = Path.Combine(tools, "claude.exe");
                File.WriteAllText(claudeExe, string.Empty);
                Environment.SetEnvironmentVariable("CLAUDE_PATH", claudeExe);
                Equal($"\"{claudeExe}\"", TerminalCommands.FindCli("claude"));
                Environment.SetEnvironmentVariable("CLAUDE_PATH", null);
                Equal("claude", TerminalCommands.FindCli("claude"));
                Equal(Path.Combine(AppPaths.DataDirectory, "bin"), TerminalCommands.CreateDefault().Folder);
                _ = UserEnvironmentPath.Read();
            }
            finally
            {
                RestoreEnvironment(environment);
            }

            // A saved command that is not a valid name is dropped on load.
            var saved = new AppSettings { IsEphemeral = true };
            saved.Accounts.Add(new ProviderAccount { ProviderId = "claude", Source = AccountSource.Added, Path = team, Command = "claude" });
            saved.Accounts.Add(new ProviderAccount { ProviderId = "claude", Source = AccountSource.Added, Path = other, Command = " claude-ok " });
            Invoke(saved, "Validate");
            Equal(null, saved.Accounts[0].Command);
            Equal("claude-ok", saved.Accounts[1].Command);
        }
        finally
        {
            TryDelete(root);
        }

        return Task.CompletedTask;
    }

    public static async Task TestUserTerminalCommandsAreFoundAsync()
    {
        var root = NewDirectory("own-commands");
        var environment = SaveEnvironment("USAGEAI_TEST_LOGINS");
        try
        {
            var profile = Path.Combine(root, "profile");
            var bin = Path.Combine(root, "bin");
            var tools = Path.Combine(root, "tools");
            var scripts = Path.Combine(root, "scripts");
            Directory.CreateDirectory(tools);
            Directory.CreateDirectory(scripts);
            Directory.CreateDirectory(bin);
            var work = Login(profile, ".claude-work", ".credentials.json");
            var lab = Login(profile, ".codex-lab", "auth.json");
            Environment.SetEnvironmentVariable("USAGEAI_TEST_LOGINS", profile);

            // Quoted and unquoted cmd, a %VAR% path, a .bat, and a PowerShell script all count.
            File.WriteAllText(Path.Combine(tools, "claude-work.cmd"), $"@echo off\r\nsetlocal\r\nset \"CLAUDE_CONFIG_DIR={work}\"\r\nclaude %*\r\n");
            File.WriteAllText(Path.Combine(tools, "claude-work-api.bat"), "@echo off\r\nset ANTHROPIC_API_KEY=not-kept\r\nSET CLAUDE_CONFIG_DIR=%USAGEAI_TEST_LOGINS%\\.claude-work\\\r\nclaude %*\r\n");
            File.WriteAllText(Path.Combine(scripts, "codex-lab.ps1"), "$env:CODEX_HOME = \"$env:USAGEAI_TEST_LOGINS\\.codex-lab\"\r\ncodex @args\r\n");

            // A launcher without setlocal that clears the variable once the CLI returns still counts,
            // and beside the .ps1 of the same name it is one command, not two.
            File.WriteAllText(Path.Combine(scripts, "codex-lab.cmd"), $"set CODEX_HOME={lab}\r\ncodex %*\r\nset CODEX_HOME=\r\n");

            // Not read as commands: UsageAI's own file, a relative folder, an unexpandable variable,
            // a file too large to be a launcher, and other extensions. A folder no account uses is
            // found, but belongs to no account.
            File.WriteAllText(Path.Combine(tools, "claude-ours.cmd"), $"{TerminalCommands.Marker}\r\nset \"CLAUDE_CONFIG_DIR={work}\"\r\n");
            File.WriteAllText(Path.Combine(tools, "claude-else.cmd"), $"set \"CLAUDE_CONFIG_DIR={Path.Combine(root, "elsewhere")}\"\r\n");
            File.WriteAllText(Path.Combine(tools, "claude-relative.cmd"), "set \"CLAUDE_CONFIG_DIR=.claude-work\"\r\n");
            File.WriteAllText(Path.Combine(tools, "claude-here.cmd"), "set \"CLAUDE_CONFIG_DIR=%~dp0.claude-work\"\r\n");
            File.WriteAllText(Path.Combine(tools, "claude-big.cmd"), $"set \"CLAUDE_CONFIG_DIR={work}\"\r\n@rem {new string('x', 20_000)}\r\n");
            File.WriteAllText(Path.Combine(tools, "claude-work.cmdx"), $"set \"CLAUDE_CONFIG_DIR={work}\"\r\n");
            File.WriteAllText(Path.Combine(tools, "claude-work.txt"), $"set \"CLAUDE_CONFIG_DIR={work}\"\r\n");
            File.WriteAllText(Path.Combine(bin, "claude-mine.cmd"), $"set \"CLAUDE_CONFIG_DIR={work}\"\r\n");

            var terminal = new TerminalCommands(
                bin,
                () => null,
                _ => throw new InvalidOperationException("A check must not write the PATH."),
                _ => null,
                _ => "claude",
                profile,
                () => new[] { tools, scripts, bin, Path.Combine(root, "missing") });
            var found = terminal.FindUserCommands();
            Equal(
                "claude-else.cmd|claude-work-api.bat|claude-work.cmd|codex-lab.cmd|codex-lab.ps1",
                string.Join("|", found.Select(command => Path.GetFileName(command.File)).Order(StringComparer.OrdinalIgnoreCase)));
            True(found.All(command => command.Folder is not null));
            True(found.Where(command => command.Name == "codex-lab").All(command => command.ProviderId == "codex"));

            // Shares and mapped network drives are never listed: an unreachable one blocks.
            True(TerminalCommands.IsLocalFolder(tools));
            False(TerminalCommands.IsLocalFolder(@"\\server\tools"));
            False(TerminalCommands.IsLocalFolder("//server/tools"));
            False(TerminalCommands.IsLocalFolder("relative"));

            // The suggested name skips the user's own: offering it would only be refused.
            var yours = new List<string> { "CLAUDE-WORK" };
            Equal("claude-x", TerminalCommands.SuggestFree("claude-x", yours));
            Equal("claude-work-2", TerminalCommands.SuggestFree("claude-work", yours));
            yours.Add("claude-work-2");
            Equal("claude-work-3", TerminalCommands.SuggestFree("claude-work", yours));
            var longName = new string('a', 40);
            Equal(new string('a', 38) + "-2", TerminalCommands.SuggestFree(longName, new[] { longName }));

            // Each account keeps the files for its own folder, the plain name first.
            var settings = new AppSettings { IsEphemeral = true, AccountsInitialized = true };
            var manager = new AccountManager(settings, profile, providerId => Path.Combine(profile, "." + providerId), account => new StubClient(account.UnitKey))
            {
                Terminal = terminal,
            };
            var workKey = manager.AddFolder("claude", work, AccountSource.Added, "Work").UnitKey!;
            var labKey = manager.AddFolder("codex", lab, AccountSource.Added, "Lab").UnitKey!;

            // Only a scan reads the PATH: startup with automatic finding off does not.
            Equal(0, manager.RefreshTerminalCommands());
            Equal(0, settings.FindAccount(workKey)!.UserCommands.Length);
            Equal(0, manager.RefreshTerminalCommands(findUserCommands: true));
            Equal("claude-work|claude-work-api", string.Join("|", settings.FindAccount(workKey)!.UserCommandNames));
            Equal("codex-lab", string.Join("|", settings.FindAccount(labKey)!.UserCommandNames));
            Equal(2, settings.FindAccount(labKey)!.UserCommands.Length);
            Equal(0, settings.FindAccount("claude")?.UserCommands.Length ?? 0);

            // Settings lists them, and forgetting the account leaves them where they are.
            using (var dialog = new SettingsForm(
                       settings,
                       new[] { ("codex", "Codex"), ("claude", "Claude Code") },
                       _ => Task.FromResult(new UpdateCheckResult(true, null)),
                       _ => Task.CompletedTask,
                       dpiOverride: 96,
                       readStartup: static () => new StartupState(StartupStatus.NotRegistered, null),
                       writeStartup: _ => { },
                       accounts: new SettingsAccountActions(null, manager)))
            {
                dialog.Show();
                Application.DoEvents();
                var list = Field<CheckedListBox>(dialog, "_accountList");
                list.SelectedIndex = list.Items.Cast<object>().Select(item => item.ToString()!).ToList()
                    .FindIndex(item => item.StartsWith("Claude · Work", StringComparison.Ordinal));
                Application.DoEvents();
                True(Field<Label>(dialog, "_accountDetails").Text.Contains(
                    $"Your own terminal commands: claude-work, claude-work-api (in {AccountDiscovery.DisplayFolder(tools)})",
                    StringComparison.Ordinal));

                // Two files of one name read as one command.
                list.SelectedIndex = list.Items.Cast<object>().Select(item => item.ToString()!).ToList()
                    .FindIndex(item => item.StartsWith("Codex · Lab", StringComparison.Ordinal));
                Application.DoEvents();
                True(Field<Label>(dialog, "_accountDetails").Text.Contains(
                    $"Your own terminal command: codex-lab (in {AccountDiscovery.DisplayFolder(scripts)})",
                    StringComparison.Ordinal));
                dialog.Close();
            }

            // A file that no longer points there drops out on the next scan.
            File.WriteAllText(Path.Combine(tools, "claude-work.cmd"), "@echo off\r\nclaude %*\r\n");
            manager.Scan(DateTimeOffset.UtcNow);
            Equal("claude-work-api", string.Join("|", settings.FindAccount(workKey)!.UserCommandNames));
            Equal(null, manager.Remove(workKey, deleteCreatedFolder: false));
            True(File.Exists(Path.Combine(tools, "claude-work-api.bat")));

            // Saved entries that are not absolute command files are dropped on load.
            var saved = new AppSettings { IsEphemeral = true };
            saved.Accounts.Add(new ProviderAccount
            {
                ProviderId = "claude",
                Source = AccountSource.Added,
                Path = work,
                UserCommands = new[] { "relative.cmd", Path.Combine(tools, "x.exe"), Path.Combine(tools, "ok.cmd"), Path.Combine(tools, "OK.cmd") },
            });
            saved.Accounts.Add(new ProviderAccount { ProviderId = "claude", UserCommands = new[] { Path.Combine(tools, "main.cmd") } });
            Invoke(saved, "Validate");
            Equal(Path.Combine(tools, "ok.cmd"), string.Join("|", saved.Accounts.Single(account => !account.IsDefault).UserCommands));
            Equal(0, saved.Accounts.Single(account => account.IsDefault).UserCommands.Length);
            await Task.CompletedTask;
        }
        finally
        {
            RestoreEnvironment(environment);
            TryDelete(root);
        }
    }

    public static async Task TestAccountListClicksWheelAndLiveStatusAsync()
    {
        var profile = NewDirectory("list");
        try
        {
            var work = Login(profile, ".claude-work", ".credentials.json");
            var settings = new AppSettings { IsEphemeral = true, AccountsInitialized = true };
            var manager = new AccountManager(settings, profile, providerId => Path.Combine(profile, "." + providerId), account => new StubClient(account.UnitKey));
            var workKey = manager.AddFolder("claude", work, AccountSource.Added, "Work").UnitKey!;
            using var dialog = new SettingsForm(
                settings,
                new[] { ("codex", "Codex"), ("claude", "Claude Code") },
                _ => Task.FromResult(new UpdateCheckResult(true, null)),
                _ => Task.CompletedTask,
                dpiOverride: 96,
                readStartup: static () => new StartupState(StartupStatus.NotRegistered, null),
                writeStartup: _ => { },
                accounts: new SettingsAccountActions(null, manager));
            dialog.Show();
            Application.DoEvents();
            dialog.ShowSection(SettingsForm.AccountsSection);
            Application.DoEvents();

            // The page paints its antialiased section cards off screen: drawn on the window itself
            // they cost a whole frame per scroll step and flashed bare background first.
            var shownPage = Field<Panel>(dialog, "_scrollHost").Controls.OfType<TableLayoutPanel>().Single(candidate => candidate.Visible);
            Equal(SettingsForm.AccountsSection, shownPage.AccessibleName);
            True((bool)typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(shownPage)!);

            var list = Field<CheckedListBox>(dialog, "_accountList");
            var accounts = IsType<SettingsCheckList>(list);
            var index = list.Items.Cast<object>().Select(item => item.ToString()!).ToList().FindIndex(item => item.StartsWith("Claude · Work", StringComparison.Ordinal));
            True(index >= 0);
            True(list.GetItemChecked(index));
            False(Field<Button>(dialog, "_terminalCommand").Enabled);

            // A click on the name selects the account and leaves its tick; a second click too.
            var row = list.GetItemRectangle(index);
            var onName = new Point(row.Left + (row.Width / 2), row.Top + (row.Height / 2));
            Click(list, onName);
            Equal(index, list.SelectedIndex);
            True(list.GetItemChecked(index));
            Click(list, onName);
            True(list.GetItemChecked(index));
            True(Field<Label>(dialog, "_accountDetails").Text.Contains("Added by you", StringComparison.Ordinal));

            // The box itself, and Space, change the tick.
            var onBox = new Point(row.Left + 4, row.Top + (row.Height / 2));
            True(accounts.IsOnCheckBox(onBox));
            False(accounts.IsOnCheckBox(onName));
            Click(list, onBox);
            False(list.GetItemChecked(index));
            SendMessageW(list.Handle, 0x0100, (IntPtr)0x20, (IntPtr)0x00390001);
            SendMessageW(list.Handle, 0x0102, (IntPtr)' ', (IntPtr)0x00390001);
            Application.DoEvents();
            True(list.GetItemChecked(index));

            // With nothing to scroll in the list, the wheel scrolls the page instead — here a page
            // longer than a window squeezed onto a small screen, scrolled part way down.
            var page = Field<Panel>(dialog, "_scrollHost");
            dialog.ClientSize = new Size(dialog.ClientSize.Width, 300);
            Application.DoEvents();
            page.AutoScrollPosition = new Point(0, 40);
            Application.DoEvents();
            False(accounts.CanScroll(up: true));
            var before = page.AutoScrollPosition.Y;
            True(before < 0);
            SendMessageW(list.Handle, 0x020A, (IntPtr)(120 << 16), IntPtr.Zero);
            Application.DoEvents();
            True(page.AutoScrollPosition.Y > before);
            var providers = IsType<SettingsCheckList>(Field<CheckedListBox>(dialog, "_providers"));
            Equal(page, providers.WheelTarget);

            // A reading that lands while Settings is open updates the row in place.
            True(list.Items[index].ToString()!.EndsWith("Not read yet", StringComparison.Ordinal));
            list.SetItemChecked(index, false);
            dialog.UpdateAccountStatuses(new[] { new ProviderStatus(workKey, "Claude Code", Snapshot(workKey, 5), null, false) });
            Equal(index, list.SelectedIndex);
            False(list.GetItemChecked(index));
            False(list.Items[index].ToString()!.EndsWith("Not read yet", StringComparison.Ordinal));
            True(Field<Label>(dialog, "_accountDetails").Text.Contains("Pro", StringComparison.Ordinal));
            dialog.Close();
            await Task.CompletedTask;
        }
        finally
        {
            TryDelete(profile);
        }
    }

    public static Task TestStandardFolderAndMigrationAsync()
    {
        var profile = NewDirectory("standard");
        try
        {
            var standard = Login(profile, ".claude", ".credentials.json");
            File.WriteAllText(Path.Combine(profile, ".claude.json"), ClaudeJson("me@example.com", "org-0", "Personal"));
            var inherited = Login(profile, "elsewhere-claude", ".credentials.json");
            File.WriteAllText(Path.Combine(inherited, ".claude.json"), ClaudeJson("team@example.com", "org-1", "Acme"));
            var teamHash = AccountDiscovery.ReadIdentity("claude", inherited, profile)!.Hash;
            var meHash = AccountDiscovery.ReadIdentity("claude", standard, profile)!.Hash;
            var inheritedKey = ProviderKey.Compose("claude", ProviderKey.AccountIdFor(inherited));
            IReadOnlyList<FoundLogin> inheritedLogins = new[] { new FoundLogin("claude", ProviderKey.NormalizeFolder(inherited)) };
            AccountManager Manager(AppSettings settings) => new(
                settings,
                profile,
                providerId => ProviderKey.NormalizeFolder(Path.Combine(profile, "." + providerId)),
                account => new StubClient(account.UnitKey),
                () => inheritedLogins);

            // The main slot held the inherited folder's person (UsageAI was started with the variable):
            // the folder becomes its own account and takes the slot's records and choices.
            var moves = new List<string>();
            var settings = new AppSettings { IsEphemeral = true, AccountsInitialized = true, TrayProviderId = "claude" };
            var main = settings.EnsureAccount("claude");
            main.Identity = teamHash;
            main.Email = "team@example.com";
            main.Label = "Work";
            main.Alerts = false;
            settings.Accounts.Add(new ProviderAccount
            {
                Id = "11111111",
                ProviderId = "claude",
                Source = AccountSource.Added,
                Path = Path.Combine(profile, "other"),
                Order = 1,
            });
            settings.PopupMetricGroups["claude"] = "Opus";
            var manager = Manager(settings);
            Equal(inheritedKey, manager.MigrateInheritedMainLogins((from, to) => moves.Add($"{from}->{to ?? "drop"}")).Single());
            Equal($"claude->{inheritedKey}", moves.Single());
            var moved = settings.FindAccount(inheritedKey)!;
            Equal(AccountSource.Found, moved.Source);
            Equal("Work", moved.Label);
            False(moved.Alerts);
            Equal(teamHash, moved.Identity);
            Equal(inheritedKey, settings.TrayProviderId);
            Equal("Opus", settings.PopupMetricGroup(inheritedKey));
            Equal(null, settings.PopupMetricGroup("claude"));
            Equal(null, main.Identity);
            Equal(null, main.Label);
            False(main.Hidden);
            False(main.HiddenUntilSignedIn);

            // The login keeps the slot's place; the slot, now a different login, goes after the others.
            Equal(0, moved.Order);
            Equal(2, main.Order);

            // Once moved, nothing is left to move; the next scan sees the folder as already known.
            moves.Clear();
            Equal(0, manager.MigrateInheritedMainLogins((from, to) => moves.Add(from)).Count);
            Equal(0, moves.Count);
            Equal(0, manager.Scan(DateTimeOffset.UtcNow));

            // Nothing moves when the slot holds the standard folder's own person, holds no one yet,
            // or the same person is signed in to both folders.
            foreach (var savedHash in new string?[] { meHash, null })
            {
                var untouched = new AppSettings { IsEphemeral = true, AccountsInitialized = true };
                untouched.EnsureAccount("claude").Identity = savedHash;
                Equal(0, Manager(untouched).MigrateInheritedMainLogins((_, _) => throw new InvalidOperationException("no move expected")).Count);
                Equal(savedHash, untouched.FindAccount("claude")!.Identity);
            }

            File.Copy(Path.Combine(inherited, ".claude.json"), Path.Combine(profile, ".claude.json"), overwrite: true);
            var same = new AppSettings { IsEphemeral = true, AccountsInitialized = true };
            same.EnsureAccount("claude").Identity = teamHash;
            Equal(0, Manager(same).MigrateInheritedMainLogins((_, _) => throw new InvalidOperationException("no move expected")).Count);
            File.WriteAllText(Path.Combine(profile, ".claude.json"), ClaudeJson("me@example.com", "org-0", "Personal"));

            // A folder already an account was the slot's repeat (not polled): the slot's records merge
            // into it, it is shown again and keeps its own label unless the slot had one.
            var known = new AppSettings { IsEphemeral = true, AccountsInitialized = true };
            known.EnsureAccount("claude").Identity = teamHash;
            known.Accounts.Add(new ProviderAccount
            {
                Id = ProviderKey.AccountIdFor(inherited),
                ProviderId = "claude",
                Source = AccountSource.Added,
                Path = ProviderKey.NormalizeFolder(inherited),
                Label = "Team",
                Identity = teamHash,
                Pending = true,
                DuplicateOf = "team@example.com",
            });
            var knownManager = Manager(known);
            True(knownManager.Notices.Any(notice => inheritedKey.Equals(notice.UnitKey, StringComparison.Ordinal)));
            moves.Clear();
            Equal(0, knownManager.MigrateInheritedMainLogins((from, to) => moves.Add($"{from}->{to ?? "drop"}")).Count);
            Equal($"claude->{inheritedKey}", moves.Single());
            Equal(null, known.FindAccount("claude")!.Identity);
            var merged = known.FindAccount(inheritedKey)!;
            Equal("Team", merged.Label);
            Equal(AccountSource.Added, merged.Source);
            False(merged.Pending);
            Equal(null, merged.DuplicateOf);
            True(known.IsUnitVisible(inheritedKey));
            False(knownManager.Notices.Any(notice => inheritedKey.Equals(notice.UnitKey, StringComparison.Ordinal)));

            // A folder the user forgot (typically as the slot's repeat) comes back: the slot is how
            // they were watching it.
            var forgotten = new AppSettings { IsEphemeral = true, AccountsInitialized = true, ForgottenAccounts = new[] { inheritedKey } };
            forgotten.EnsureAccount("claude").Identity = teamHash;
            var forgottenManager = Manager(forgotten);
            False(forgottenManager.HasUnknownInheritedLogin());
            Equal(inheritedKey, forgottenManager.MigrateInheritedMainLogins((_, _) => { }).Single());
            Equal(0, forgotten.ForgottenAccounts.Length);

            // With no login in the standard folder the reset slot stays out of view until one appears.
            File.Delete(Path.Combine(standard, ".credentials.json"));
            var empty = new AppSettings { IsEphemeral = true, AccountsInitialized = true };
            empty.EnsureAccount("claude").Identity = teamHash;
            var emptyManager = Manager(empty);
            var changes = 0;
            emptyManager.AccountsChanged += (_, _) => changes++;
            Equal(1, emptyManager.MigrateInheritedMainLogins((_, _) => { }).Count);
            var emptyMain = empty.FindAccount("claude")!;
            True(emptyMain.Hidden);
            True(emptyMain.HiddenUntilSignedIn);
            False(emptyManager.RevealSignedInMainLogins());
            True(emptyMain.Hidden);
            File.WriteAllText(Path.Combine(standard, ".credentials.json"), "{}");
            True(emptyManager.RevealSignedInMainLogins());
            False(emptyMain.Hidden);
            False(emptyMain.HiddenUntilSignedIn);
            Equal(1, changes);
            False(emptyManager.RevealSignedInMainLogins());

            // Hidden by the user, it stays hidden whatever appears in the folder.
            emptyMain.Hidden = true;
            emptyMain.HiddenUntilSignedIn = true;
            emptyManager.AnswerNewAccount("claude", show: false);
            False(emptyMain.HiddenUntilSignedIn);
            False(emptyManager.RevealSignedInMainLogins());
            True(emptyMain.Hidden);

            // A scan offers an inherited folder like a found one, even with the profile listing off:
            // shown at once on the first run, waiting for Show / Ignore after that.
            var fresh = new AppSettings { IsEphemeral = true, FindAccountsAutomatically = false };
            Equal(1, Manager(fresh).Scan(DateTimeOffset.UtcNow));
            False(fresh.FindAccount(inheritedKey)!.Pending);
            var later = new AppSettings { IsEphemeral = true, AccountsInitialized = true, FindAccountsAutomatically = false };
            var laterManager = Manager(later);
            True(laterManager.HasUnknownInheritedLogin());
            Equal(1, laterManager.Scan(DateTimeOffset.UtcNow));
            False(laterManager.HasUnknownInheritedLogin());
            True(later.FindAccount(inheritedKey)!.Pending);
            Equal(AccountNoticeKind.NewAccount, laterManager.Notices.Single().Kind);

            // The tray app's record mover: history, alert memory and the cached reading follow the key.
            var now = DateTimeOffset.Now;
            UsageHistoryStore.Clear();
            UsageHistoryStore.Append(new[]
            {
                new UsageSample(now.AddMinutes(-5), "claude", "Session:5-hour", 30),
                new UsageSample(now.AddMinutes(-5), "codex", "Session:5-hour", 10),
            });
            File.WriteAllText(AppPaths.AlertStateFile, """{"claude|Session:5-hour":{"Used":30,"ResetsAt":null},"codex|Session:5-hour":{"Used":10,"ResetsAt":null}}""");
            SnapshotCache.Save(new[] { Snapshot("claude", 30), Snapshot("codex", 10) with { ProviderName = "Codex" } });
            AccountManager.MoveStoredRecords("claude", inheritedKey);
            var history = UsageHistoryStore.Load(TimeSpan.FromDays(1));
            Equal($"{inheritedKey}|codex", string.Join('|', history.Select(sample => sample.ProviderId).Order(StringComparer.Ordinal)));
            Equal(30, history.Single(sample => sample.ProviderId == inheritedKey).UsedPercent);
            var alertState = File.ReadAllText(AppPaths.AlertStateFile);
            True(alertState.Contains($"\"{inheritedKey}|Session:5-hour\"", StringComparison.Ordinal));
            False(alertState.Contains("\"claude|", StringComparison.Ordinal));
            True(alertState.Contains("\"codex|Session:5-hour\"", StringComparison.Ordinal));
            Equal($"{inheritedKey}|codex", string.Join('|', SnapshotCache.Load().Select(snapshot => snapshot.ProviderId).Order(StringComparer.Ordinal)));

            // Moving onto a key that has records merges them: the history keeps both, the moved alert
            // memory and cached reading win.
            UsageHistoryStore.Append(new[] { new UsageSample(now.AddMinutes(-1), "claude", "Session:5-hour", 40) });
            File.WriteAllText(AppPaths.AlertStateFile, $$$"""{"{{{inheritedKey}}}|Session:5-hour":{"Used":30,"ResetsAt":null},"claude|Session:5-hour":{"Used":40,"ResetsAt":null}}""");
            SnapshotCache.Save(SnapshotCache.Load().Append(Snapshot("claude", 40)).ToArray());
            AccountManager.MoveStoredRecords("claude", inheritedKey);
            Equal("30|40", string.Join('|', UsageHistoryStore.Load(TimeSpan.FromDays(1)).Where(sample => sample.ProviderId == inheritedKey).Select(sample => sample.UsedPercent)));
            True(File.ReadAllText(AppPaths.AlertStateFile).Contains($"\"{inheritedKey}|Session:5-hour\":{{\"Used\":40", StringComparison.Ordinal));
            Equal(40, SnapshotCache.Load().Single(snapshot => snapshot.ProviderId == inheritedKey).Metrics[0].UsedPercent);

            // Dropping instead removes the key's records; moving a key with nothing stored changes nothing.
            AccountManager.MoveStoredRecords(inheritedKey, null);
            Equal("codex", string.Join('|', UsageHistoryStore.Load(TimeSpan.FromDays(1)).Select(sample => sample.ProviderId)));
            False(File.ReadAllText(AppPaths.AlertStateFile).Contains(inheritedKey, StringComparison.Ordinal));
            Equal("codex", string.Join('|', SnapshotCache.Load().Select(snapshot => snapshot.ProviderId)));
            AccountManager.MoveStoredRecords("gemini", "gemini@00000000");
            Equal("codex", string.Join('|', SnapshotCache.Load().Select(snapshot => snapshot.ProviderId)));
            UsageHistoryStore.Clear();
            SnapshotCache.Clear();
            File.Delete(AppPaths.AlertStateFile);
        }
        finally
        {
            TryDelete(profile);
        }

        return Task.CompletedTask;
    }

    private static void Click(CheckedListBox list, Point point)
    {
        var lParam = (IntPtr)((point.Y << 16) | (point.X & 0xFFFF));
        SendMessageW(list.Handle, 0x0201, (IntPtr)1, lParam);
        SendMessageW(list.Handle, 0x0202, IntPtr.Zero, lParam);
        Application.DoEvents();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

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

    private static void NotNull(object? value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? expression = null) =>
        True(value is not null, $"{expression} is not null");

    private static T IsType<T>(object? value) =>
        value is T typed ? typed : throw new InvalidOperationException($"Expected {typeof(T).Name}, got {value?.GetType().Name ?? "null"}.");

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
