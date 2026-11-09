using System.Diagnostics;
using System.Globalization;
using UsageAI.Models;
using UsageAI.Services;

namespace UsageAI.UI;

/// <summary>What Settings' account page can do beyond editing preferences, supplied by the tray app.</summary>
internal sealed class SettingsAccountActions
{
    private readonly UsageApplicationContext? _context;

    public SettingsAccountActions(UsageApplicationContext? context, AccountManager manager)
    {
        _context = context;
        Manager = manager;
    }

    public AccountManager Manager { get; }

    /// <summary>Opens Add account… (or, with an account, signs that account's folder in again).</summary>
    public void OpenAddAccount(string? providerId = null, ProviderAccount? reauthenticate = null) =>
        _context?.OpenAddAccount(providerId, reauthenticate);
}

/// <summary>Settings → Accounts: every Claude and Codex login, what to show, and the account actions.</summary>
internal sealed partial class SettingsForm
{
    public const string AccountsSection = "Accounts";

    private CheckedListBox _accountList = null!;
    private Label _accountDetails = null!;
    private ToggleSwitch _accountAlerts = null!;
    private ToggleSwitch _findAccounts = null!;
    private ToggleSwitch _askBeforeShowing = null!;
    private Label _lastScan = null!;
    private Button _removeAccount = null!;
    private Button _reauthenticate = null!;
    private Button _locateAccount = null!;
    private Button _openFolder = null!;
    private Button _terminalCommand = null!;
    private SettingsAccountActions? _accountActions;
    private IReadOnlyList<ProviderStatus> _accountStatuses = Array.Empty<ProviderStatus>();

    /// <summary>Edits waiting for Save, keyed by provider key.</summary>
    private readonly Dictionary<string, AccountDraft> _accountDrafts = new(StringComparer.OrdinalIgnoreCase);
    private bool _loadingAccounts;

    private sealed class AccountDraft
    {
        public string? Label { get; set; }

        public bool Shown { get; set; }

        public bool Alerts { get; set; }

        public int Order { get; set; }
    }

    private sealed record AccountEntry(string Key, string Text)
    {
        public override string ToString() => Text;
    }

    private void AddAccountsSection(LayoutScale scale)
    {
        AddSection(AccountsSection);
        AddSpan(CreateHint(
            "Every Claude Code and Codex login UsageAI reads. Tick to show an account in the popup and dashboard; " +
            "a provider with one account keeps its plain tile."));
        // Selecting an account shows its details below, so a click on its name only selects it;
        // the box (or Space) shows or hides it.
        _accountList = new SettingsCheckList
        {
            AccessibleName = "Accounts",
            AccessibleDescription = "Select an account to see its details. Tick its box, or press Space, to show it. Reorder with Move up and Move down.",
            BackColor = Theme.SurfaceRaised,
            BorderStyle = BorderStyle.None,
            CheckOnBoxOnly = true,
            WheelTarget = _scrollHost,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text,
            Height = scale[120],
            HorizontalScrollbar = true,
            IntegralHeight = false,
            Margin = scale.Pad(0, 4, 0, 4),
        };
        _accountList.HandleCreated += OnScrollableControlHandleCreated;
        _accountList.SelectedIndexChanged += (_, _) => ShowSelectedAccount();
        _accountList.ItemCheck += (_, eventArgs) =>
        {
            if (!_loadingAccounts && _accountList.Items[eventArgs.Index] is AccountEntry entry &&
                _accountDrafts.TryGetValue(entry.Key, out var draft))
            {
                draft.Shown = eventArgs.NewValue == CheckState.Checked;
            }
        };
        AddSpan(_accountList);

        _accountDetails = new Label
        {
            AutoSize = true,
            ForeColor = Theme.Muted,
            Margin = scale.Pad(0, 2, 0, 6),
            Tag = HintTag,
            UseMnemonic = false,
        };
        AddSpan(_accountDetails);

        _accountAlerts = CreateCheckBox("Alerts for this account");
        _accountAlerts.CheckedChanged += (_, _) =>
        {
            if (!_loadingAccounts && SelectedAccountKey() is { } key && _accountDrafts.TryGetValue(key, out var draft))
            {
                draft.Alerts = _accountAlerts.Checked;
            }
        };
        AddSpan(_accountAlerts);

        var rowButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = scale.Pad(0, 0, 0, 4),
            WrapContents = true,
        };
        rowButtons.Controls.Add(CreateLinkButton("Rename…", (_, _) => RenameSelectedAccount()));
        rowButtons.Controls.Add(CreateLinkButton("Move up", (_, _) => MoveSelectedAccount(-1)));
        rowButtons.Controls.Add(CreateLinkButton("Move down", (_, _) => MoveSelectedAccount(1)));
        _reauthenticate = CreateLinkButton("Re-authenticate", (_, _) => ReauthenticateSelectedAccount());
        rowButtons.Controls.Add(_reauthenticate);
        _openFolder = CreateLinkButton("Open folder", (_, _) => OpenSelectedAccountFolder());
        rowButtons.Controls.Add(_openFolder);
        _locateAccount = CreateLinkButton("Locate…", (_, _) => LocateSelectedAccount());
        rowButtons.Controls.Add(_locateAccount);
        _terminalCommand = CreateLinkButton("Terminal command…", (_, _) => SetSelectedAccountCommand());
        rowButtons.Controls.Add(_terminalCommand);
        _removeAccount = CreateLinkButton("Remove", (_, _) => RemoveSelectedAccount(), destructive: true);
        rowButtons.Controls.Add(_removeAccount);
        AddSpan(rowButtons);

        var pageButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = scale.Pad(0, 6, 0, 2),
        };
        var add = CreateDialogButton("Add account…", scale, primary: false);
        add.AutoSize = true;
        add.Margin = scale.Pad(0, 0, 8, 0);
        add.Click += (_, _) =>
        {
            _accountActions?.OpenAddAccount();
            ReloadAccounts();
        };
        var rescan = CreateDialogButton("Rescan", scale, primary: false);
        rescan.Margin = Padding.Empty;
        rescan.Click += (_, _) =>
        {
            _accountActions?.Manager.Scan(DateTimeOffset.UtcNow);
            ReloadAccounts();
        };
        add.Enabled = rescan.Enabled = _accountActions is not null;
        pageButtons.Controls.Add(add);
        pageButtons.Controls.Add(rescan);
        AddSpan(pageButtons);

        _lastScan = CreateHint(string.Empty);
        AddSpan(_lastScan);

        _findAccounts = CreateCheckBox("Find logins in my user folder automatically");
        AddSpan(_findAccounts);
        AddSpan(CreateHint("Looks for folders whose name starts with .claude or .codex, at startup and once a day. No other folder is searched."));
        _askBeforeShowing = CreateCheckBox("Ask before showing newly found accounts");
        AddSpan(_askBeforeShowing);
    }

    /// <summary>Scrolls to a section by name, for the account banner's Choose….</summary>
    public void ShowSection(string name)
    {
        var index = Array.FindIndex(SectionNames, section => section.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _nav.SelectedIndex = index;
            ScrollToSection(index);
        }
    }

    private void LoadAccountValues()
    {
        _findAccounts.Checked = _settings.FindAccountsAutomatically;
        _askBeforeShowing.Checked = _settings.AskBeforeShowingNewAccounts;
        ReloadAccounts();
    }

    /// <summary>
    /// Fills the list from the settings, keeping edits not saved yet for accounts still listed. An
    /// action that adds or removes an account (Add, Rescan, Remove, Locate) applies at once, so
    /// the list is read again after it.
    /// </summary>
    private void ReloadAccounts()
    {
        var selected = SelectedAccountKey();
        _loadingAccounts = true;
        try
        {
            _accountList.Items.Clear();
            var keys = AccountKeys();
            foreach (var key in keys.Where(key => !_accountDrafts.ContainsKey(key)))
            {
                var account = _settings.FindAccount(key);
                _accountDrafts[key] = new AccountDraft
                {
                    Label = account?.Label,
                    Shown = account is null || (!account.Hidden && !account.Pending),
                    Alerts = account?.Alerts ?? true,
                    Order = account?.Order ?? (ProviderKey.IsDefault(key) ? 0 : int.MaxValue / 2),
                };
            }

            foreach (var stale in _accountDrafts.Keys.Where(key => !keys.Contains(key, StringComparer.OrdinalIgnoreCase)).ToArray())
            {
                _accountDrafts.Remove(stale);
            }

            foreach (var key in OrderedAccountKeys())
            {
                _accountList.Items.Add(new AccountEntry(key, AccountText(key)), _accountDrafts[key].Shown);
            }

            var index = selected is null ? -1 : OrderedAccountKeys().ToList().FindIndex(key => key.Equals(selected, StringComparison.OrdinalIgnoreCase));
            _accountList.SelectedIndex = index >= 0 ? index : _accountList.Items.Count > 0 ? 0 : -1;
            _lastScan.Text = _settings.LastAccountScanUtc is { } scanned
                ? $"Last checked {scanned.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}."
                : "Not checked yet.";
        }
        finally
        {
            _loadingAccounts = false;
        }

        ShowSelectedAccount();
    }

    /// <summary>Every provider key the page lists: each provider's default login and its other accounts.</summary>
    private List<string> AccountKeys()
    {
        var keys = AccountDiscovery.MultiAccountProviders.ToList();
        keys.AddRange(_settings.Accounts.Where(account => !account.IsDefault).Select(account => account.UnitKey));
        return keys;
    }

    private IEnumerable<string> OrderedAccountKeys() =>
        AccountKeys()
            .OrderBy(key => Array.IndexOf(AccountDiscovery.MultiAccountProviders.ToArray(), ProviderKey.ProviderOf(key)))
            .ThenBy(key => _accountDrafts[key].Order)
            .ThenBy(key => key, StringComparer.OrdinalIgnoreCase);

    private string AccountText(string key)
    {
        var account = _settings.FindAccount(key);
        var draft = _accountDrafts[key];
        var name = AccountDiscovery.ProviderName(ProviderKey.ProviderOf(key));
        var label = !string.IsNullOrWhiteSpace(draft.Label) ? draft.Label
            : account?.Email ?? Path.GetFileName(account?.Path ?? AccountDiscovery.DefaultFolder(ProviderKey.ProviderOf(key)));
        var state = account?.DuplicateOf is { } duplicate ? $"Duplicate of {duplicate}"
            : account?.Pending == true ? "New — not shown yet"
            : StatusText(key);
        return $"{name} · {label} — {state}";
    }

    private string StatusText(string key)
    {
        var status = _accountStatuses.FirstOrDefault(candidate => candidate.ProviderId.Equals(key, StringComparison.OrdinalIgnoreCase));
        return status is null ? "Not read yet"
            : status.Error is { } error && error.Contains("not found at", StringComparison.OrdinalIgnoreCase) ? "Not found"
            : status.Error is { } failure && failure.Contains("expired", StringComparison.OrdinalIgnoreCase) ? "Expired"
            : status.StatusText;
    }

    private string? SelectedAccountKey() =>
        _accountList?.SelectedItem is AccountEntry entry ? entry.Key : null;

    private void ShowSelectedAccount()
    {
        var key = SelectedAccountKey();
        var account = key is null ? null : _settings.FindAccount(key);
        var isFolder = account is { IsDefault: false };
        _removeAccount.Enabled = isFolder && _accountActions is not null;
        _removeAccount.Text = account?.Source == AccountSource.Created ? "Remove" : "Forget";
        _locateAccount.Enabled = isFolder && _accountActions is not null;
        _terminalCommand.Enabled = isFolder && _accountActions?.Manager.Terminal is not null;
        _reauthenticate.Enabled = key is not null;
        _openFolder.Enabled = key is not null;
        if (key is null)
        {
            _accountDetails.Text = string.Empty;
            return;
        }

        _loadingAccounts = true;
        _accountAlerts.Checked = _accountDrafts[key].Alerts;
        _loadingAccounts = false;
        var folder = account?.Path ?? _accountActions?.Manager.DefaultFolder(ProviderKey.ProviderOf(key)) ?? AccountDiscovery.DefaultFolder(ProviderKey.ProviderOf(key));
        var source = account?.Source switch
        {
            AccountSource.Found => $"Found in {ShortenProfile(folder)}",
            AccountSource.Added => $"Added by you: {folder}",
            AccountSource.Created => $"Created by UsageAI: {folder}",
            _ => $"Standard folder: {ShortenProfile(folder)}",
        };
        var status = _accountStatuses.FirstOrDefault(candidate => candidate.ProviderId.Equals(key, StringComparison.OrdinalIgnoreCase));
        var plan = status?.Snapshot?.Plan is { Length: > 0 } planName ? $" · {planName}" : string.Empty;
        var email = account?.Email is { } known ? $"{known}{plan}\n" : plan.Length > 0 ? $"{plan.TrimStart(' ', '·')}\n" : string.Empty;
        var command = account?.Command is { } name ? $"Terminal command: {name}\n" : string.Empty;
        _accountDetails.Text = $"{email}{source}\n{command}{StatusText(key)}{(status?.Error is { } error ? $": {error}" : string.Empty)}";
    }

    /// <summary>
    /// Takes the latest readings while Settings is open, so an account added or signed in again
    /// here reads Connected without reopening the window. Ticks, selection and edits stay.
    /// </summary>
    internal void UpdateAccountStatuses(IReadOnlyList<ProviderStatus> statuses)
    {
        _accountStatuses = statuses;
        if (_accountList is null || _accountList.IsDisposed)
        {
            return;
        }

        _loadingAccounts = true;
        try
        {
            var selected = _accountList.SelectedIndex;
            var top = _accountList.TopIndex;
            for (var index = 0; index < _accountList.Items.Count; index++)
            {
                if (_accountList.Items[index] is not AccountEntry entry || !_accountDrafts.ContainsKey(entry.Key))
                {
                    continue;
                }

                var text = AccountText(entry.Key);
                if (text != entry.Text)
                {
                    var isChecked = _accountList.GetItemChecked(index);
                    _accountList.Items[index] = entry with { Text = text };
                    _accountList.SetItemChecked(index, isChecked);
                }
            }

            _accountList.SelectedIndex = selected;
            _accountList.TopIndex = top;
        }
        finally
        {
            _loadingAccounts = false;
        }

        ShowSelectedAccount();
    }

    private void SetSelectedAccountCommand()
    {
        if (SelectedAccountKey() is not { } key ||
            _accountActions?.Manager is not { Terminal: { } terminal } manager ||
            _settings.FindAccount(key) is not { IsDefault: false, Path: { } folder } account)
        {
            return;
        }

        var providerName = AccountDiscovery.ProviderName(account.ProviderId);
        var label = _accountDrafts.TryGetValue(key, out var draft) && draft.Label is { } drafted ? drafted : account.Label;
        var suggested = account.Command ?? TerminalCommands.Suggest(account.ProviderId, label ?? account.Email?.Split('@')[0], folder);
        var name = TextPrompt.Ask(
            this,
            "Terminal command",
            account.Command is null
                ? $"Command that starts {providerName} with this account:"
                : "Command for this account (clear it to remove):",
            suggested);
        if (name is null)
        {
            return;
        }

        var result = manager.SetTerminalCommand(key, name);
        if (result.Problem is { } problem)
        {
            PromptDialog.Ask("Terminal command", "The command was not saved", problem, "OK", declineText: null, PromptKind.Warning);
        }
        else if (result.Command is { } command)
        {
            var where = result.PathUpdateFailed
                ? $"UsageAI wrote {result.FilePath}, but could not add {terminal.Folder} to your PATH. Add it yourself, then open a new terminal."
                : result.PathChanged
                    ? $"UsageAI wrote {result.FilePath} and added {terminal.Folder} to your PATH."
                    : $"UsageAI wrote {result.FilePath}.";
            PromptDialog.Ask(
                "Terminal command",
                $"Run {command} in a new terminal",
                $"{where}\n\nOpen a new terminal window (one already open keeps its old PATH) and run {command} to use {providerName} with this account. Forget the account here to delete the command.",
                "OK",
                declineText: null);
        }

        ShowSelectedAccount();
    }

    private static string ShortenProfile(string folder) => AccountDiscovery.DisplayFolder(folder);

    private void RenameSelectedAccount()
    {
        if (SelectedAccountKey() is not { } key)
        {
            return;
        }

        var name = TextPrompt.Ask(this, "Rename account", "Name shown on the account's tile:", _accountDrafts[key].Label ?? string.Empty);
        if (name is null)
        {
            return;
        }

        _accountDrafts[key].Label = string.IsNullOrWhiteSpace(name) ? null : name.Trim()[..Math.Min(name.Trim().Length, 64)];
        ReloadAccounts();
    }

    private void MoveSelectedAccount(int offset)
    {
        if (SelectedAccountKey() is not { } key)
        {
            return;
        }

        var siblings = OrderedAccountKeys()
            .Where(other => ProviderKey.ProviderOf(other).Equals(ProviderKey.ProviderOf(key), StringComparison.OrdinalIgnoreCase))
            .ToList();
        var from = siblings.FindIndex(other => other.Equals(key, StringComparison.OrdinalIgnoreCase));
        var to = from + offset;
        if (from < 0 || to < 0 || to >= siblings.Count)
        {
            return;
        }

        (siblings[from], siblings[to]) = (siblings[to], siblings[from]);
        for (var index = 0; index < siblings.Count; index++)
        {
            _accountDrafts[siblings[index]].Order = index;
        }

        ReloadAccounts();
    }

    private void ReauthenticateSelectedAccount()
    {
        if (SelectedAccountKey() is not { } key)
        {
            return;
        }

        var providerId = ProviderKey.ProviderOf(key);
        var account = _settings.FindAccount(key);
        if (account is { Source: AccountSource.Created } && _accountActions is not null)
        {
            _accountActions.OpenAddAccount(providerId, account);
            ReloadAccounts();
            return;
        }

        // A folder UsageAI did not create is the user's: the command signs it in where it is.
        var folder = account?.Path ?? AccountDiscovery.DefaultFolder(providerId);
        var login = providerId == AccountDiscovery.CodexProviderId ? " login" : string.Empty;
        var command = ProviderKey.IsDefault(key) ? (providerId == AccountDiscovery.CodexProviderId ? "codex" : "claude") + login
            : account?.Command is { } own && _accountActions?.Manager.Terminal is { } terminal &&
              terminal.IsOnUserPath() && File.Exists(terminal.FilePath(own)) ? own + login
            : AccountCommands.SignIn(providerId, folder);
        if (PromptDialog.Ask(
                "Re-authenticate",
                $"Sign {AccountDiscovery.ProviderName(providerId)} in again in a terminal",
                $"Run this in PowerShell, then refresh UsageAI:\n\n{command}",
                "Copy command",
                "Close"))
        {
            try
            {
                Clipboard.SetText(command);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // Another program holds the clipboard; the command is still on screen.
            }
        }
    }

    private void OpenSelectedAccountFolder()
    {
        if (SelectedAccountKey() is not { } key)
        {
            return;
        }

        var folder = _settings.FindAccount(key)?.Path ?? AccountDiscovery.DefaultFolder(ProviderKey.ProviderOf(key));
        if (!Directory.Exists(folder))
        {
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Explorer is unavailable; the path is shown in the details.
        }
    }

    private void LocateSelectedAccount()
    {
        if (SelectedAccountKey() is not { } key || _accountActions is null)
        {
            return;
        }

        using var picker = new FolderBrowserDialog
        {
            Description = "Choose the folder this login moved to",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var result = _accountActions.Manager.Locate(key, picker.SelectedPath);
        if (result.Problem is { } problem)
        {
            var heading = result.UnitKey is null ? "That folder cannot be used" : "The terminal command was removed";
            PromptDialog.Ask("Locate account", heading, problem, "OK", declineText: null, PromptKind.Warning);
        }

        ReloadAccounts();
    }

    private void RemoveSelectedAccount()
    {
        if (SelectedAccountKey() is not { } key || _accountActions is null || _settings.FindAccount(key) is not { IsDefault: false } account)
        {
            return;
        }

        var created = account.Source == AccountSource.Created;
        var commandNote = account.Command is { } command ? $"\n\nIts terminal command, {command}, is deleted too." : string.Empty;
        var confirmed = created
            ? PromptDialog.Ask(
                "Remove account",
                $"Remove {account.DisplayLabel} and delete its login?",
                $"UsageAI created {account.Path} for this account. Removing it deletes that folder, and the login saved in it is removed. A terminal using it would need to sign in again.{commandNote}",
                "Remove and delete",
                "Cancel",
                PromptKind.Warning)
            : PromptDialog.Ask(
                "Forget account",
                $"Forget {account.DisplayLabel}?",
                $"UsageAI stops reading {account.Path}. The folder and its login are not touched.{commandNote}",
                "Forget",
                "Cancel");
        if (!confirmed)
        {
            return;
        }

        if (_accountActions.Manager.Remove(key, deleteCreatedFolder: created) is { } problem)
        {
            PromptDialog.Ask("Remove account", "The account was not removed", problem, "OK", declineText: null, PromptKind.Warning);
        }

        ReloadAccounts();
    }

    /// <summary>Writes the page's edits into the settings, as part of Save.</summary>
    private void ApplyAccounts()
    {
        _settings.FindAccountsAutomatically = _findAccounts.Checked;
        _settings.AskBeforeShowingNewAccounts = _askBeforeShowing.Checked;
        foreach (var (key, draft) in _accountDrafts)
        {
            var account = _settings.FindAccount(key);
            var isDefaultUnchanged = account is null && draft.Shown && draft.Alerts && draft.Label is null && draft.Order == 0;
            if (isDefaultUnchanged)
            {
                continue;
            }

            account ??= _settings.EnsureAccount(key);
            if (account.Pending && draft.Shown)
            {
                account.Pending = false;
                _accountActions?.Manager.AnswerNewAccount(key, show: true);
            }

            account.Label = draft.Label;
            var hidden = !draft.Shown && !account.Pending;
            if (hidden != account.Hidden)
            {
                // The user's own choice now; UsageAI no longer shows it on a login appearing.
                account.HiddenUntilSignedIn = false;
            }

            account.Hidden = hidden;
            account.Alerts = draft.Alerts;
            account.Order = Math.Clamp(draft.Order, 0, 1000);
        }
    }
}
