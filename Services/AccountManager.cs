using UsageAI.Models;

namespace UsageAI.Services;

internal enum AccountNoticeKind
{
    /// <summary>The first scan found more than one login of a provider: Keep all / Choose….</summary>
    FoundSeveral,

    /// <summary>A later scan found a login that waits for Show / Ignore.</summary>
    NewAccount,

    /// <summary>A known folder is now signed in to someone else.</summary>
    IdentityChanged,
}

/// <summary>A banner the popup shows until it is answered.</summary>
internal sealed record AccountNotice(
    AccountNoticeKind Kind,
    string ProviderId,
    string Message,
    string? UnitKey = null);

/// <summary>What adding a login folder came to.</summary>
internal sealed record AccountAddResult(string? UnitKey, string? Problem)
{
    public bool Added => UnitKey is not null && Problem is null;
}

/// <summary>
/// Keeps <see cref="AppSettings.Accounts"/> in step with the login folders on disk: scans the
/// profile when due, adds folders the user chooses, removes accounts, and queues the banners that
/// tell the user about any of it. It never reads or writes a credential itself.
/// </summary>
internal sealed class AccountManager
{
    private readonly AppSettings _settings;
    private readonly string _userProfile;
    private readonly Func<string, string> _defaultFolder;
    private readonly Func<ProviderAccount, IUsageClient> _createClient;
    private readonly List<AccountNotice> _notices = new();

    public AccountManager(
        AppSettings settings,
        string? userProfile = null,
        Func<string, string>? defaultFolder = null,
        Func<ProviderAccount, IUsageClient>? createClient = null)
    {
        _settings = settings;
        _userProfile = userProfile ?? AccountDiscovery.UserProfile();
        _defaultFolder = defaultFolder ?? (providerId => AccountDiscovery.DefaultFolder(providerId, _userProfile));
        _createClient = createClient ?? CreateClient;

        // Accounts still waiting for an answer get their banner again after a restart.
        foreach (var pending in _settings.Accounts.Where(account => account.Pending))
        {
            _notices.Add(NewAccountNotice(pending));
        }
    }

    /// <summary>Raised when the account list changed, so the clients are rebuilt.</summary>
    public event EventHandler? AccountsChanged;

    /// <summary>Raised when the banners changed.</summary>
    public event EventHandler? NoticesChanged;

    public IReadOnlyList<AccountNotice> Notices => _notices;

    /// <summary>Writes terminal commands for login folders; null where they are not offered (previews, diagnostics).</summary>
    public TerminalCommands? Terminal { get; init; }

    public string UserProfile => _userProfile;

    public string DefaultFolder(string providerId) => _defaultFolder(providerId);

    /// <summary>The provider's default clients followed by one client per extra account folder.</summary>
    public IReadOnlyList<IUsageClient> CreateClients(IEnumerable<IUsageClient> defaultClients) =>
        defaultClients
            .Concat(_settings.Accounts
                .Where(account => !account.IsDefault && account.Path is not null && account.DuplicateOf is null)
                .OrderBy(account => account.Order)
                .Select(_createClient))
            .ToArray();

    private static IUsageClient CreateClient(ProviderAccount account) =>
        account.ProviderId.Equals(AccountDiscovery.ClaudeProviderId, StringComparison.OrdinalIgnoreCase)
            ? new ClaudeCodeUsageClient(account.Path!)
            : new CodexUsageClient(account.Path!);

    /// <summary>Scans when automatic scanning is on and a day has passed since the last scan.</summary>
    public bool ScanIfDue(DateTimeOffset nowUtc)
    {
        if (!_settings.FindAccountsAutomatically && _settings.AccountsInitialized)
        {
            return false;
        }

        if (_settings.AccountsInitialized && !UpdateChecker.IsCheckDue(_settings.LastAccountScanUtc, nowUtc))
        {
            return false;
        }

        Scan(nowUtc);
        return true;
    }

    /// <summary>
    /// Lists the profile once for <c>.claude-*</c> / <c>.codex-*</c> login folders and adds the new
    /// ones. The first scan shows them all (they are the user's own) with one banner per provider
    /// that has several; later ones wait for Show / Ignore unless the user turned that off.
    /// With automatic scanning off, only the first-run bookkeeping happens.
    /// </summary>
    public int Scan(DateTimeOffset nowUtc)
    {
        var firstRun = !_settings.AccountsInitialized;
        var added = new List<ProviderAccount>();
        var excluded = AccountDiscovery.MultiAccountProviders.Select(_defaultFolder).ToArray();
        var found = _settings.FindAccountsAutomatically
            ? AccountDiscovery.Scan(_userProfile, excluded)
            : Array.Empty<FoundLogin>();
        foreach (var login in found)
        {
            var key = ProviderKey.Compose(login.ProviderId, ProviderKey.AccountIdFor(login.Folder));
            if (_settings.FindAccount(key) is not null ||
                _settings.ForgottenAccounts.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var identity = AccountDiscovery.ReadIdentity(login.ProviderId, login.Folder, _userProfile);
            var account = new ProviderAccount
            {
                Id = ProviderKey.AccountIdFor(login.Folder),
                ProviderId = login.ProviderId,
                Source = AccountSource.Found,
                Path = login.Folder,
                Order = _settings.NextAccountOrder(login.ProviderId),
                Identity = identity?.Hash,
                Email = identity?.Email,
                Organization = identity?.OrganizationName,
            };
            account.DuplicateOf = DuplicateOf(login.ProviderId, identity, exceptKey: key);
            account.Pending = !firstRun && _settings.AskBeforeShowingNewAccounts && account.DuplicateOf is null;
            _settings.Accounts.Add(account);
            added.Add(account);
        }

        foreach (var account in added.Where(account => account.Pending))
        {
            _notices.Add(NewAccountNotice(account));
        }

        if (firstRun)
        {
            foreach (var provider in added
                         .Where(account => account.DuplicateOf is null)
                         .GroupBy(account => account.ProviderId, StringComparer.OrdinalIgnoreCase))
            {
                var labels = new List<string>();
                if (DefaultHasLogin(provider.Key))
                {
                    labels.Add(DefaultLabel(provider.Key));
                }

                labels.AddRange(provider.Select(account => account.DisplayLabel));
                if (labels.Count > 1)
                {
                    _notices.Add(new AccountNotice(
                        AccountNoticeKind.FoundSeveral,
                        provider.Key,
                        $"Found {labels.Count} {AccountDiscovery.ProviderName(provider.Key)} accounts: {string.Join(", ", labels)}"));
                }
            }
        }

        var duplicatesChanged = RecheckDuplicates();
        RefreshTerminalCommands();
        _settings.AccountsInitialized = true;
        _settings.LastAccountScanUtc = nowUtc.ToUniversalTime();
        _settings.Save();
        WriteCreatedLoginsList();
        if (added.Count > 0 || duplicatesChanged)
        {
            AccountsChanged?.Invoke(this, EventArgs.Empty);
        }

        NoticesChanged?.Invoke(this, EventArgs.Empty);
        return added.Count;
    }

    /// <summary>
    /// Brings every account's terminal command up to date, on the same startup and daily tick as the
    /// scan. A command that starts the CLI bundled in an editor extension names that extension's
    /// version folder, which an update deletes; this points it at the copy installed now.
    /// </summary>
    public int RefreshTerminalCommands()
    {
        if (Terminal is null)
        {
            return 0;
        }

        return _settings.Accounts
            .Where(account => account is { IsDefault: false, Command: not null, Path: not null })
            .Count(account => Terminal.Refresh(account.ProviderId, account.Path!, account.Command!));
    }

    /// <summary>
    /// Adds a login folder the user chose, or one "Sign in to another account" just signed in to.
    /// A folder whose person is already shown is refused with the label that shows it.
    /// </summary>
    public AccountAddResult AddFolder(string providerId, string folder, AccountSource source, string? label)
    {
        if (AccountDiscovery.ExistingLoginProblem(providerId, folder) is { } problem)
        {
            return new AccountAddResult(null, problem);
        }

        var path = ProviderKey.NormalizeFolder(folder);
        if (path.Equals(_defaultFolder(providerId), StringComparison.OrdinalIgnoreCase))
        {
            return new AccountAddResult(null, $"Already shown as {DefaultLabel(providerId)}: that is the default {AccountDiscovery.ProviderName(providerId)} login.");
        }

        var key = ProviderKey.Compose(providerId, ProviderKey.AccountIdFor(path));
        var identity = AccountDiscovery.ReadIdentity(providerId, path, _userProfile);
        if (_settings.FindAccount(key) is { } existing)
        {
            if (!existing.Hidden && !existing.Pending && existing.DuplicateOf is null)
            {
                return new AccountAddResult(null, $"Already shown as {existing.DisplayLabel}.");
            }

            if (DuplicateOf(providerId, identity, exceptKey: key) is { } shownAs)
            {
                return new AccountAddResult(null, $"Already shown as {shownAs}.");
            }

            existing.Hidden = false;
            existing.Pending = false;
            existing.DuplicateOf = null;
            existing.Label = string.IsNullOrWhiteSpace(label) ? existing.Label : label.Trim();
            _notices.RemoveAll(notice => key.Equals(notice.UnitKey, StringComparison.OrdinalIgnoreCase));
            Commit();
            return new AccountAddResult(key, null);
        }

        if (DuplicateOf(providerId, identity, exceptKey: key) is { } duplicate)
        {
            return new AccountAddResult(null, $"Already shown as {duplicate}.");
        }

        _settings.ForgottenAccounts = _settings.ForgottenAccounts
            .Where(id => !id.Equals(key, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _settings.Accounts.Add(new ProviderAccount
        {
            Id = ProviderKey.AccountIdFor(path),
            ProviderId = providerId.ToLowerInvariant(),
            Source = source,
            Path = path,
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            Order = _settings.NextAccountOrder(providerId),
            Identity = identity?.Hash,
            Email = identity?.Email,
            Organization = identity?.OrganizationName,
        });
        Commit();
        return new AccountAddResult(key, null);
    }

    /// <summary>
    /// Removes an account. A folder UsageAI created is deleted with it (the caller confirms first);
    /// any other folder is only forgotten and never touched. The default login cannot be removed.
    /// </summary>
    public string? Remove(string unitKey, bool deleteCreatedFolder)
    {
        if (_settings.FindAccount(unitKey) is not { IsDefault: false } account)
        {
            return "The default login cannot be removed; hide it instead.";
        }

        if (account.Source == AccountSource.Created && deleteCreatedFolder && account.Path is { } path && Directory.Exists(path))
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return $"UsageAI could not delete {path}. Close any program using it, then try again.";
            }
        }

        _settings.Accounts.Remove(account);
        if (account.Command is { } command)
        {
            Terminal?.Remove(command);
        }

        // Whatever its source, a folder still on disk would be offered again by the next scan if it
        // matches the naming rule, so it is remembered as forgotten until added by hand again.
        if (account.Path is { } remaining && Directory.Exists(remaining))
        {
            Forget(unitKey);
        }

        _notices.RemoveAll(notice => unitKey.Equals(notice.UnitKey, StringComparison.OrdinalIgnoreCase));
        Commit();
        return null;
    }

    /// <summary>Remembers a forgotten key, newest last, dropping the oldest beyond the cap.</summary>
    private void Forget(string unitKey)
    {
        var kept = _settings.ForgottenAccounts
            .Where(id => !id.Equals(unitKey, StringComparison.OrdinalIgnoreCase))
            .Append(unitKey)
            .ToArray();
        _settings.ForgottenAccounts = kept.Length > AppSettings.MaxForgottenAccounts
            ? kept[^AppSettings.MaxForgottenAccounts..]
            : kept;
    }

    /// <summary>
    /// Works out again which accounts repeat a person already shown. The default login comes first,
    /// then the provider's accounts in order; the first folder of a person is shown and the rest are
    /// duplicates. A duplicate is never read, so its folder's identity is read again here: when the
    /// account it repeated is removed or signs in to someone else, it is shown again.
    /// Returns true when anything changed.
    /// </summary>
    public bool RecheckDuplicates()
    {
        var changed = false;
        foreach (var providerId in AccountDiscovery.MultiAccountProviders)
        {
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            if (DefaultIdentityHash(providerId) is { } defaultHash)
            {
                seen[defaultHash] = DefaultLabel(providerId);
            }

            foreach (var account in _settings.Accounts
                         .Where(account => !account.IsDefault && account.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(account => account.Order)
                         .ToArray())
            {
                if (account.DuplicateOf is not null && account.Path is { } path &&
                    AccountDiscovery.ReadIdentity(providerId, path, _userProfile) is { } fresh)
                {
                    account.Identity = fresh.Hash;
                    account.Email = fresh.Email;
                    account.Organization = fresh.OrganizationName;
                }

                string? duplicateOf = null;
                if (account.Identity is { } hash && !seen.TryGetValue(hash, out duplicateOf))
                {
                    seen[hash] = account.DisplayLabel;
                }

                if (account.DuplicateOf == duplicateOf)
                {
                    continue;
                }

                account.DuplicateOf = duplicateOf;
                if (duplicateOf is not null && account.Pending)
                {
                    // Nothing to answer for a person already shown.
                    account.Pending = false;
                    _notices.RemoveAll(notice => account.UnitKey.Equals(notice.UnitKey, StringComparison.OrdinalIgnoreCase));
                }

                changed = true;
            }
        }

        return changed;
    }

    /// <summary>Points an account whose folder moved at its new location.</summary>
    public AccountAddResult Locate(string unitKey, string folder)
    {
        if (_settings.FindAccount(unitKey) is not { IsDefault: false } account)
        {
            return new AccountAddResult(null, "Only an account read from its own folder can be located.");
        }

        var source = account.Source == AccountSource.Created ? AccountSource.Created : AccountSource.Added;
        var label = account.Label;
        _settings.Accounts.Remove(account);
        var result = AddFolder(account.ProviderId, folder, source, label);
        if (!result.Added)
        {
            _settings.Accounts.Add(account);
            return result;
        }

        // The account's terminal command follows it to the new folder, or goes when it cannot.
        // The account has moved either way; a command that could not follow is said, not dropped silently.
        if (account.Command is { } command && _settings.FindAccount(result.UnitKey!) is { Path: { } path } moved)
        {
            var rewritten = Terminal?.Create(moved.ProviderId, path, command, previous: command);
            if (rewritten is { Succeeded: true })
            {
                moved.Command = command;
            }
            else
            {
                Terminal?.Remove(command);
            }

            _settings.Save();
            if (rewritten is { Problem: { } problem })
            {
                return new AccountAddResult(result.UnitKey, $"The account now reads {path}, but its terminal command {command} was deleted. {problem}");
            }
        }

        return result;
    }

    /// <summary>
    /// Gives a login folder a terminal command, such as <c>claude-team</c>, replacing the one it had;
    /// an empty name removes it. Applies at once, like adding or removing an account.
    /// </summary>
    public TerminalCommandResult SetTerminalCommand(string unitKey, string? name)
    {
        if (Terminal is null)
        {
            return new TerminalCommandResult(null, "Terminal commands are not available here.");
        }

        if (_settings.FindAccount(unitKey) is not { IsDefault: false, Path: { } folder } account)
        {
            return new TerminalCommandResult(null, "The default login already starts with the provider's own command.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            if (account.Command is { } old)
            {
                Terminal.Remove(old);
                account.Command = null;
                _settings.Save();
            }

            return new TerminalCommandResult(null, null);
        }

        name = name.Trim();
        if (_settings.Accounts.FirstOrDefault(other =>
                !ReferenceEquals(other, account) &&
                name.Equals(other.Command, StringComparison.OrdinalIgnoreCase)) is { } taken)
        {
            return new TerminalCommandResult(null, $"{taken.DisplayLabel} already uses {name}. Choose another name.");
        }

        var result = Terminal.Create(account.ProviderId, folder, name, account.Command);
        if (result.Succeeded)
        {
            account.Command = result.Command;
            _settings.Save();
        }

        return result;
    }

    /// <summary>Show (true) or Ignore (false) for an account waiting on its banner.</summary>
    public void AnswerNewAccount(string unitKey, bool show)
    {
        if (_settings.FindAccount(unitKey) is { } account)
        {
            account.Pending = false;
            account.Hidden = !show;
        }

        _notices.RemoveAll(notice => unitKey.Equals(notice.UnitKey, StringComparison.OrdinalIgnoreCase));
        Commit();
    }

    public void Dismiss(AccountNotice notice)
    {
        if (notice.Kind == AccountNoticeKind.NewAccount && notice.UnitKey is { } key)
        {
            AnswerNewAccount(key, show: false);
            return;
        }

        _notices.Remove(notice);
        NoticesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Queues the banner for a folder now signed in to someone else.</summary>
    public void NotifyIdentityChanged(string unitKey, string? previous, string current)
    {
        var providerId = ProviderKey.ProviderOf(unitKey);
        var name = AccountDiscovery.ProviderName(providerId);
        var which = ProviderKey.IsDefault(unitKey) ? $"Your default {name} login" : $"The {name} login in {_settings.FindAccount(unitKey)?.Path}";
        _notices.RemoveAll(notice => notice.Kind == AccountNoticeKind.IdentityChanged && unitKey.Equals(notice.UnitKey, StringComparison.OrdinalIgnoreCase));
        _notices.Add(new AccountNotice(
            AccountNoticeKind.IdentityChanged,
            providerId,
            previous is null ? $"{which} is now {current}." : $"{which} is now {current} (was {previous}).",
            unitKey));

        // The person who left may have been the one another folder repeated, or the new one may
        // repeat an account already shown.
        if (RecheckDuplicates())
        {
            Commit();
            return;
        }

        NoticesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Saves a change made to the account list and rebuilds the clients.</summary>
    public void Commit()
    {
        RecheckDuplicates();
        _settings.Save();
        WriteCreatedLoginsList();
        AccountsChanged?.Invoke(this, EventArgs.Empty);
        NoticesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The folders UsageAI created, one per line, which the uninstaller offers to delete. Only
    /// these are ever offered: other login folders are the user's own.
    /// </summary>
    private void WriteCreatedLoginsList()
    {
        if (_settings.IsEphemeral)
        {
            return;
        }

        try
        {
            var created = _settings.Accounts
                .Where(account => account.Source == AccountSource.Created && account.Path is not null)
                .Select(account => account.Path!)
                .ToArray();
            var path = AppPaths.CreatedLoginsFile;
            if (created.Length == 0)
            {
                File.Delete(path);
                return;
            }

            AppPaths.WriteAllTextAtomic(path, string.Join("\r\n", created) + "\r\n");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Only the uninstaller's offer depends on this list.
        }
    }

    /// <summary>The label of an account already shown for the same person, or null.</summary>
    private string? DuplicateOf(string providerId, AccountIdentity? identity, string exceptKey)
    {
        if (identity is null)
        {
            return null;
        }

        var hash = identity.Hash;
        if (!exceptKey.Equals(providerId, StringComparison.OrdinalIgnoreCase) &&
            DefaultIdentityHash(providerId) == hash)
        {
            return DefaultLabel(providerId);
        }

        return _settings.Accounts
            .Where(account => !account.IsDefault &&
                              account.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase) &&
                              !account.UnitKey.Equals(exceptKey, StringComparison.OrdinalIgnoreCase) &&
                              !account.Pending &&
                              account.DuplicateOf is null &&
                              account.Identity == hash)
            .Select(account => account.DisplayLabel)
            .FirstOrDefault();
    }

    private string? DefaultIdentityHash(string providerId) =>
        _settings.FindAccount(providerId)?.Identity ??
        AccountDiscovery.ReadIdentity(providerId, _defaultFolder(providerId), _userProfile)?.Hash;

    private string DefaultLabel(string providerId) =>
        _settings.FindAccount(providerId) is { } account && (account.Label ?? account.Email) is { } known
            ? known
            : AccountDiscovery.ReadIdentity(providerId, _defaultFolder(providerId), _userProfile)?.Email ?? "the default login";

    private bool DefaultHasLogin(string providerId) =>
        File.Exists(Path.Combine(_defaultFolder(providerId), AccountDiscovery.CredentialFileName(providerId)));

    private static AccountNotice NewAccountNotice(ProviderAccount account) => new(
        AccountNoticeKind.NewAccount,
        account.ProviderId,
        $"New {AccountDiscovery.ProviderName(account.ProviderId)} account found: {account.DisplayLabel}",
        account.UnitKey);
}
