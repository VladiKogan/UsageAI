using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using UsageAI.Models;
using UsageAI.Services;

namespace UsageAI.UI;

internal sealed class UsageApplicationContext : ApplicationContext
{
    /// <summary>
    /// How closely a due refresh is honoured. Each tick only asks the service whether anything
    /// is due, so this is cheap; at 30 s a window that had just reset could stay on screen for
    /// half a minute longer than the schedule intended.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);
    private const int MaximumTooltipLength = 127;
    private const int MaximumBalloonTitle = 63;
    private const int MaximumBalloonText = 255;

    private readonly AppSettings _settings;
    private IUsageClient[] _clients;

    /// <summary>The providers' default logins; the account clients are rebuilt around them.</summary>
    private readonly IUsageClient[] _defaultClients;

    /// <summary>Extra logins of Claude and Codex. Null in tests and previews that do not use them.</summary>
    private readonly AccountManager? _accounts;
    private SettingsForm? _openSettings;
    private readonly UsageRefreshService _service;
    private readonly ContextMenuStrip _menu;
    private readonly NotifyIcon _trayIcon;
    private readonly UsagePopupForm _popup;
    private readonly MessageWindow _messageWindow;
    private readonly System.Windows.Forms.Timer _tickTimer;
    private readonly System.Windows.Forms.Timer _refreshAnimationTimer;
    private readonly bool _automaticUpdateChecksEnabled;
    private readonly Func<CancellationToken, Task<UpdateCheckResult>> _checkForUpdates;
    private readonly Func<StartupState>? _readStartup;
    private readonly Action<bool>? _writeStartup;
    private readonly UpgradeNotice _upgradeNotice;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SynchronizationContext _syncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
    private Icon? _currentIcon;
    private float _refreshAnimationAngle;
    private bool _isExiting;
    private bool _disposed;
    private bool _updateCheckRunning;
    private readonly Func<UpdateRelease, IProgress<double>?, CancellationToken, Task<string>> _downloadInstaller;
    private readonly Func<string, Process> _launchInstaller;
    private UpdateHandoffResult? _updateResult;
    private UpdateProgressForm? _updateWindow;

    public UsageApplicationContext(
        AccountManager accounts,
        AppSettings settings,
        bool profileExisted,
        UpdateHandoffResult? updateResult)
        : this(
            Program.CreateUsageClients(),
            settings,
            showTrayIcon: true,
            enableAutomaticUpdateChecks: true,
            profileExisted,
            incidents: new ProviderIncidentService(),
            updateResult: updateResult,
            accounts: accounts)
    {
    }

    /// <summary>
    /// Reads the providers' status pages. Only the installed app gets one; tests and previews leave
    /// it null, so they never reach the network for it.
    /// </summary>
    private readonly ProviderIncidentService? _incidents;

    internal UsageApplicationContext(
        IEnumerable<IUsageClient> clients,
        AppSettings settings,
        bool showTrayIcon,
        bool enableAutomaticUpdateChecks = false,
        bool profileExisted = false,
        Func<CancellationToken, Task<UpdateCheckResult>>? checkForUpdates = null,
        Func<StartupState>? readStartup = null,
        Action<bool>? writeStartup = null,
        bool answerRelaunch = true,
        ProviderIncidentService? incidents = null,
        UpdateHandoffResult? updateResult = null,
        Func<UpdateRelease, IProgress<double>?, CancellationToken, Task<string>>? downloadInstaller = null,
        Func<string, Process>? launchInstaller = null,
        AccountManager? accounts = null)
    {
        _settings = settings;
        _accounts = accounts;
        _updateResult = updateResult;
        _downloadInstaller = downloadInstaller ?? UpdateInstaller.DownloadAndVerifyAsync;
        _launchInstaller = launchInstaller ?? UpdateInstaller.Launch;
        _incidents = incidents;
        if (_incidents is not null)
        {
            _incidents.Changed += (_, _) => RunOnUi(() =>
            {
                UpdateTray();
                PushStateToPopup();
            });
        }

        _automaticUpdateChecksEnabled = enableAutomaticUpdateChecks;
        _checkForUpdates = checkForUpdates ?? UpdateChecker.CheckForUpdateAsync;
        _readStartup = readStartup;
        _writeStartup = writeStartup;
        _defaultClients = clients.ToArray();
        _clients = _accounts?.CreateClients(_defaultClients).ToArray() ?? _defaultClients;
        Theme.Apply(_settings.Theme, _settings.WarningPercent, _settings.CriticalPercent);
        _upgradeNotice = UpgradeNotice.Create(
            _settings,
            profileExisted,
            AppIdentity.Version,
            ReleaseNotes.LoadBundled());

        _service = new UsageRefreshService(_clients, _settings);
        _service.Updated += OnServiceUpdated;
        _service.AlertsRaised += OnAlertsRaised;
        _service.AccountIdentityChanged += OnAccountIdentityChanged;

        _popup = new UsagePopupForm(_settings);
        _popup.RefreshRequested += async (_, _) => await RefreshRequestedByUserAsync();
        _popup.ProviderRefreshRequested += async (_, eventArgs) => await RetryProviderAsync(eventArgs.Status.ProviderId);
        _popup.SettingsRequested += (_, _) => OpenSettings();
        _popup.AddAccountRequested += (_, _) => OpenAddAccount();
        _popup.NoticeAnswered += (_, eventArgs) => AnswerNotice(eventArgs.Notice, eventArgs.Primary);
        PushStateToPopup();

        if (_accounts is not null)
        {
            _accounts.AccountsChanged += OnAccountsChanged;
            _accounts.NoticesChanged += OnNoticesChanged;
            _popup.SetNotices(_accounts.Notices);
        }

        _menu = TrayMenu.Create(
            open: OpenDashboard,
            refresh: () => _ = RefreshRequestedByUserAsync(),
            settings: OpenSettings,
            exit: Exit,
            addAccount: () => OpenAddAccount());

        _currentIcon = TrayIconFactory.Create(0);
        _trayIcon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Icon = _currentIcon,
            Text = "UsageAI - checking connected providers",
            Visible = showTrayIcon,
        };
        _trayIcon.MouseUp += TrayIconOnMouseUp;
        _trayIcon.BalloonTipClicked += (_, _) => OpenDashboard();

        _messageWindow = new MessageWindow();
        if (answerRelaunch)
        {
            _messageWindow.ShowRequested += (_, _) => OpenDashboard();
        }

        _messageWindow.HotkeyPressed += (_, _) => ToggleCompactPopup();
        // Windows closing the app (an installer's Restart Manager, a sign-out, a shutdown) exits
        // as the tray menu's Exit does. Posted, so the teardown runs outside the window procedure.
        _messageWindow.EndSessionRequested += (_, _) => _syncContext.Post(_ => Exit(), null);
        ApplyHotkeySetting();

        _tickTimer = new System.Windows.Forms.Timer
        {
            Interval = (int)TickInterval.TotalMilliseconds,
        };
        _tickTimer.Tick += async (_, _) =>
        {
            // The status pages are checked alongside, not after, a provider refresh that can take seconds.
            var incidentCheck = RefreshIncidentsIfDueAsync();
            ScanAccountsIfDue();
            await RefreshIfDueAsync();
            await CheckForUpdatesIfDueAsync();
            await incidentCheck;
        };
        _tickTimer.Start();

        _refreshAnimationTimer = new System.Windows.Forms.Timer { Interval = 110 };
        _refreshAnimationTimer.Tick += (_, _) =>
        {
            if (_isExiting || !_service.IsRefreshing)
            {
                _refreshAnimationTimer.Stop();
                return;
            }

            _refreshAnimationAngle = (_refreshAnimationAngle + 28F) % 360F;
            UpdateRefreshingTrayIcon();
        };

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        Application.Idle += RefreshOnFirstIdle;
    }

    private void RefreshOnFirstIdle(object? sender, EventArgs eventArgs)
    {
        Application.Idle -= RefreshOnFirstIdle;

        // A login folder may have appeared while UsageAI was closed; found ones join this refresh.
        ScanAccountsIfDue(atStartup: true);
        _ = RefreshAsync(force: true);
        _ = CheckForUpdatesIfDueAsync();
        _ = RefreshIncidentsIfDueAsync();
        ShowUpdateResult();
    }

    /// <summary>
    /// Tells the user an in-app update arrived, or that it did not. The copy that started the
    /// installer left a note, and this copy read it at startup.
    /// </summary>
    private void ShowUpdateResult()
    {
        if (_updateResult is not { } result || _isExiting)
        {
            return;
        }

        _updateResult = null;
        var window = NewUpdateWindow(UpdateChecker.LatestReleasePage);
        if (result.Outcome == UpdateOutcome.Installed)
        {
            window.ShowInstalled(result.Handoff.ToVersion, offerWhatsNew: _upgradeNotice.Pending is not null);
        }
        else
        {
            window.ShowFailed(
                result.Handoff.ToVersion,
                $"UsageAI {result.Handoff.FromVersion} is still installed. The release page has the installer to run by hand.");
        }

        window.ShowInTrayCorner();
    }

    /// <summary>
    /// A fresh progress window. Its button opens What's New once an update is installed, or
    /// <paramref name="releasePage"/> after a failure.
    /// </summary>
    private UpdateProgressForm NewUpdateWindow(Uri releasePage)
    {
        _updateWindow?.Close();
        var window = new UpdateProgressForm();
        window.PrimaryRequested += (_, _) =>
        {
            var failed = window.Stage == UpdateStage.Failed;
            window.Close();
            if (failed)
            {
                OpenReleasePage(releasePage);
            }
            else
            {
                ShowPendingWhatsNew();
            }
        };
        window.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_updateWindow, window))
            {
                _updateWindow = null;
            }

            // The installer that just ran has exited by now, so its download can go.
            _ = Task.Run(UpdateHandoffStore.CleanUpInstallers);
        };
        _updateWindow = window;
        return window;
    }

    /// <summary>The update progress window while one is open, for tests.</summary>
    internal UpdateProgressForm? UpdateWindow => _updateWindow;

    /// <summary>
    /// True while the Windows session is locked. Scheduled polls wait until it is unlocked, which
    /// refreshes at once; nobody can see the tray meanwhile, so polling would only start provider
    /// processes and requests overnight. Alerts that fall due meanwhile arrive with that refresh.
    /// </summary>
    internal bool IsSessionLocked { get; private set; }

    private async Task RefreshIfDueAsync()
    {
        if (_isExiting || IsSessionLocked || !_service.IsDue(DateTimeOffset.Now))
        {
            return;
        }

        await _service.RefreshDueAsync(_popup.Visible);
    }

    private async Task RefreshAsync(bool force)
    {
        if (_isExiting)
        {
            return;
        }

        await _service.RefreshAsync(force, _popup.Visible);
    }

    /// <summary>
    /// Raised when the user asks for a refresh from the popup or the tray menu, before it starts.
    /// Refreshes after sleep, unlock or startup are forced too, so they cannot stand for this.
    /// </summary>
    internal event EventHandler? UserRefreshRequested;

    private Task RefreshRequestedByUserAsync()
    {
        UserRefreshRequested?.Invoke(this, EventArgs.Empty);
        return RefreshAsync(force: true);
    }

    private async Task RetryProviderAsync(string providerId)
    {
        if (_isExiting)
        {
            return;
        }

        await _service.RefreshProviderAsync(providerId, _popup.Visible);
    }

    /// <summary>
    /// Scans the profile for login folders at startup and then once a day, on the same tick as the
    /// update check but with its own timestamp, so an offline update check never skips it. Every
    /// tick also shows a hidden main login whose standard folder now has a login.
    /// </summary>
    private void ScanAccountsIfDue(bool atStartup = false)
    {
        if (_accounts is null || _isExiting)
        {
            return;
        }

        // A main login hidden for having no login in its standard folder comes back once it has
        // one; it was not polled while hidden, so everything is made due.
        if (_accounts.RevealSignedInMainLogins())
        {
            _service.ApplySettings();
        }

        if (atStartup)
        {
            // An inherited CLAUDE_CONFIG_DIR / CODEX_HOME login is offered even with scanning off.
            if (_settings.FindAccountsAutomatically || !_settings.AccountsInitialized ||
                _accounts.HasUnknownInheritedLogin())
            {
                _accounts.Scan(DateTimeOffset.UtcNow);
            }
            else
            {
                // A scan keeps terminal commands current; with scanning off they are checked here.
                _accounts.RefreshTerminalCommands();
            }

            return;
        }

        _accounts.ScanIfDue(DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Rebuilds the clients around the current account list. It starts no refresh itself: a new
    /// account is due, so the caller's own refresh or the next tick reads it, and a refresh started
    /// here would hold the lock and swallow the forced one a caller (startup, Add account) is about
    /// to ask for.
    /// </summary>
    private void OnAccountsChanged(object? sender, EventArgs eventArgs) => RunOnUi(() =>
    {
        if (_isExiting || _accounts is null)
        {
            return;
        }

        var before = _clients.Select(client => client.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _clients = _accounts.CreateClients(_defaultClients).ToArray();
        foreach (var removed in before.Where(id => !_clients.Any(client => client.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
        {
            _service.ForgetHistory(removed);
        }

        _service.ReplaceClients(_clients);
        PushStateToPopup();
        UpdateTray();
    });

    private void OnNoticesChanged(object? sender, EventArgs eventArgs) => RunOnUi(() =>
    {
        if (!_isExiting && _accounts is not null)
        {
            _popup.SetNotices(_accounts.Notices);
        }
    });

    /// <summary>
    /// Raised from inside a refresh as it folds in a reading; posted, so the account list (which may
    /// change as duplicates are worked out again) is not rebuilt under that refresh.
    /// </summary>
    private void OnAccountIdentityChanged(object? sender, AccountIdentityChangedEventArgs eventArgs) =>
        _syncContext.Post(
            _ =>
            {
                if (!_disposed && !_isExiting)
                {
                    _accounts?.NotifyIdentityChanged(eventArgs.UnitKey, eventArgs.Previous, eventArgs.Current);
                }
            },
            null);

    /// <summary>The account banner's answers.</summary>
    internal void AnswerNotice(AccountNotice notice, bool primary)
    {
        if (_accounts is null)
        {
            return;
        }

        switch (notice.Kind)
        {
            case AccountNoticeKind.NewAccount when notice.UnitKey is { } key:
                _accounts.AnswerNewAccount(key, show: primary);
                if (primary)
                {
                    _ = RefreshIfDueAsync();
                }

                break;
            case AccountNoticeKind.FoundSeveral:
                _accounts.Dismiss(notice);
                if (!primary)
                {
                    OpenSettings(SettingsForm.AccountsSection);
                }

                break;
            default:
                _accounts.Dismiss(notice);
                if (primary)
                {
                    OpenAddAccount(notice.ProviderId);
                }

                break;
        }
    }

    /// <summary>
    /// Add account…: signs another account in through the provider's official CLI, or adds a login
    /// folder the user already has. With <paramref name="reauthenticate"/>, signs that account's
    /// own folder in again.
    /// </summary>
    internal void OpenAddAccount(string? providerId = null, ProviderAccount? reauthenticate = null)
    {
        if (_isExiting || _accounts is null)
        {
            return;
        }

        ShowPendingWhatsNew();
        _popup.Hide();
        using var dialog = new AddAccountForm(_accounts, providerId, reauthenticate);
        if (dialog.ShowDialog() == DialogResult.OK && dialog.AddedKey is { } added)
        {
            _ = _service.RefreshProviderAsync(added, _popup.Visible);
        }
    }

    private void OnServiceUpdated(object? sender, EventArgs eventArgs) => RunOnUi(() =>
    {
        if (_isExiting)
        {
            return;
        }

        PushStateToPopup();
        UpdateTray();
        _openSettings?.UpdateAccountStatuses(_service.AllStatuses);
    });

    private void OnAlertsRaised(object? sender, UsageAlertEventArgs eventArgs) => RunOnUi(() =>
    {
        if (_isExiting || eventArgs.Alerts.Count == 0 || !_settings.NotificationsEnabled)
        {
            return;
        }

        var primary = eventArgs.Alerts.OrderByDescending(alert => alert.Level).First();
        var text = eventArgs.Alerts.Count > 1
            ? $"{primary.Message} (+{eventArgs.Alerts.Count - 1} more)"
            : primary.Message;
        _trayIcon.ShowBalloonTip(
            8_000,
            Trim(primary.Title, MaximumBalloonTitle),
            Trim(text, MaximumBalloonText),
            primary.Level switch
            {
                AlertLevel.Critical => ToolTipIcon.Error,
                AlertLevel.Warning => ToolTipIcon.Warning,
                _ => ToolTipIcon.Info,
            });
    });

    private void PushStateToPopup() =>
        _popup.SetStates(WithIncidents(_service.Statuses), _service.IsRefreshing, _service.LastRefreshed, _service.History);

    /// <summary>Attaches each provider's ongoing status-page incident, if any, for the dashboard.</summary>
    private IReadOnlyList<ProviderStatus> WithIncidents(IReadOnlyList<ProviderStatus> statuses) =>
        _incidents is null || _incidents.Current.Count == 0
            ? statuses
            : statuses
                .Select(status => _incidents.For(status.Kind) is { } incident
                    ? status with
                    {
                        IncidentSummary = $"{incident.SeverityLabel}: {incident.Title}",
                        IncidentIsOutage = incident.IsOutage,
                    }
                    : status)
                .ToArray();

    private void UpdateTray()
    {
        if (_service.IsRefreshing)
        {
            if (!_refreshAnimationTimer.Enabled)
            {
                _refreshAnimationAngle = 0F;
                _refreshAnimationTimer.Start();
            }

            UpdateRefreshingTrayIcon();
            return;
        }

        _refreshAnimationTimer.Stop();
        var statuses = _service.Statuses;
        var connected = statuses.Where(status => status.Snapshot is not null).ToArray();
        if (connected.Length == 0)
        {
            ReplaceIcon(TrayIconFactory.Create(100, hasError: true));
            _trayIcon.Text = "UsageAI - no connected providers";
            return;
        }

        if (ShowsAllProviders(_settings))
        {
            var tank = TankLevel(connected, _settings);
            var worst = connected
                .Select(status => _incidents?.For(status.Kind))
                .OfType<ProviderIncident>()
                .MaxBy(incident => incident.Severity);
            ReplaceIcon(TrayIconFactory.CreateTank(tank.UsedPercent, tank.FillPercent, incident: worst?.Severity));
            _trayIcon.Text = WithIncident(TrayTooltipAll(connected, _settings), worst);
            return;
        }

        var trayStatus = SelectTrayStatus(connected, _settings.TrayProviderId)!;
        var usedPercent = TrayUsedPercent(trayStatus);
        var trayIncident = _incidents?.For(trayStatus.Kind);
        ReplaceIcon(TrayIconFactory.Create(
            usedPercent,
            identityColor: Theme.ForProvider(trayStatus.ProviderId),
            fillPercent: TrayDisplayedPercent(trayStatus, _settings.QuotaDisplay) ?? 0,
            incident: trayIncident?.Severity));
        _trayIcon.Text = WithIncident(TrayTooltip(trayStatus, _settings.QuotaDisplay), trayIncident);
    }

    /// <summary>The most of the shell's tooltip length an incident line may take.</summary>
    private const int MaximumIncidentLineLength = 60;

    /// <summary>
    /// Adds a provider's ongoing incident to a tray tooltip as its own line, which also carries the
    /// meaning of the icon's dot where colour cannot (High Contrast). The line goes right after the
    /// first one, capped in length, so the shell's limit trims usage lines from the end — the
    /// all-providers tooltip already fills most of it — and never the incident.
    /// </summary>
    internal static string WithIncident(string tooltip, ProviderIncident? incident)
    {
        if (incident is null)
        {
            return tooltip;
        }

        var line = Trim(
            $"{ShortProviderName(incident.ProviderId)} {incident.SeverityLabel.ToLowerInvariant()}: {incident.Title}",
            MaximumIncidentLineLength);
        var firstBreak = tooltip.IndexOf('\n', StringComparison.Ordinal);
        var combined = firstBreak < 0
            ? $"{tooltip}\n{line}"
            : $"{tooltip[..firstBreak]}\n{line}{tooltip[firstBreak..]}";
        return Trim(combined, MaximumTooltipLength);
    }

    /// <summary>
    /// Checks the providers' status pages when due, or clears incidents when the setting is off.
    /// Like provider polling, it waits while the session is locked; unlocking checks again.
    /// </summary>
    private async Task RefreshIncidentsIfDueAsync()
    {
        if (_incidents is null || _isExiting || IsSessionLocked)
        {
            return;
        }

        try
        {
            await _incidents.RefreshIfDueAsync(_settings.ShowProviderIncidents, DateTimeOffset.UtcNow, _shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // The app is exiting.
        }
    }

    private void UpdateRefreshingTrayIcon()
    {
        ReplaceIcon(TrayIconFactory.CreateRefreshing(_refreshAnimationAngle));
        _trayIcon.Text = "UsageAI - refreshing connected providers";
    }

    private void ReplaceIcon(Icon icon)
    {
        var previous = _currentIcon;
        _currentIcon = icon;
        _trayIcon.Icon = icon;
        previous?.Dispose();
    }

    private void TrayIconOnMouseUp(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left)
        {
            ToggleCompactPopup();
        }
    }

    private void ToggleCompactPopup()
    {
        if (_isExiting)
        {
            return;
        }

        ShowPendingWhatsNew();

        if (_popup.Visible && _popup.Mode == DashboardMode.Compact)
        {
            _popup.Hide();
            return;
        }

        PushStateToPopup();
        _popup.ShowNearTray(DashboardMode.Compact);
    }

    private void OpenDashboard()
    {
        if (!_isExiting)
        {
            ShowPendingWhatsNew();
            PushStateToPopup();
            _popup.ShowNearTray(DashboardMode.Full);
        }
    }

    private void OpenSettings() => OpenSettings(null);

    private void OpenSettings(string? section)
    {
        ShowPendingWhatsNew();
        var pollingBefore = PollingInputs(_settings);
        using var dialog = new SettingsForm(
            _settings,
            _defaultClients.Select(client => (client.Id, client.DisplayName)).ToArray(),
            _checkForUpdates,
            PromptForUpdateAsync,
            readStartup: _readStartup,
            writeStartup: _writeStartup,
            popupGroups: PopupGroups(_service.Statuses),
            accounts: _accounts is null ? null : new SettingsAccountActions(this, _accounts),
            accountStatuses: _service.AllStatuses,
            trayChoices: _service.Statuses.Select(status => (status.ProviderId, status.ProviderName)).ToArray());
        if (section is not null)
        {
            dialog.Shown += (_, _) => dialog.ShowSection(section);
        }

        // Open while a refresh lands (an account just added here is read straight away), so the
        // Accounts page shows the new readings instead of the ones it opened with.
        // Settings can be opened again from the tray while one is open; the outer one gets updates back.
        var outerSettings = _openSettings;
        _openSettings = dialog;
        DialogResult answer;
        try
        {
            answer = dialog.ShowDialog();
        }
        finally
        {
            _openSettings = outerSettings;
        }

        if (answer != DialogResult.OK)
        {
            return;
        }

        var accountsShown = false;
        if (_accounts is not null)
        {
            // An account shown for the first time is due, and read straight after.
            _clients = _accounts.CreateClients(_defaultClients).ToArray();
            _service.ReplaceClients(_clients);
            _popup.SetNotices(_accounts.Notices);
            accountsShown = true;
        }

        // A presentation-only change — theme, colours, metric or quota display — re-renders from
        // the cached snapshots. Only a change to which providers are polled, or how often, polls.
        var pollingChanged = !PollingInputs(_settings).Equals(pollingBefore, StringComparison.Ordinal);
        Theme.Apply(_settings.Theme, _settings.WarningPercent, _settings.CriticalPercent);
        ApplyHotkeySetting();
        if (pollingChanged)
        {
            _service.ApplySettings();
        }

        PushStateToPopup();
        UpdateTray();
        if (pollingChanged)
        {
            _ = RefreshAsync(force: false);
        }
        else if (accountsShown)
        {
            _ = RefreshIfDueAsync();
        }

        _ = CheckForUpdatesIfDueAsync();
        _ = RefreshIncidentsIfDueAsync();
    }

    /// <summary>The model groups each provider reported last, for Settings' popup-ring pickers.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> PopupGroups(IReadOnlyList<ProviderStatus> statuses) =>
        statuses
            .Where(status => status.Snapshot is not null)
            .GroupBy(status => status.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => PopupMetricSelection.Groups(group.First().Snapshot!.Metrics),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>The settings that decide which providers are fetched and how often.</summary>
    internal static string PollingInputs(AppSettings settings) => string.Join(
        '|',
        settings.RefreshIntervalMinutes.ToString(CultureInfo.InvariantCulture),
        settings.SlowRefreshWhenHidden ? "slow" : "steady",
        string.Join(
            ',',
            settings.HiddenProviders
                .Select(id => id.ToUpperInvariant())
                .Order(StringComparer.Ordinal)),
        string.Join(
            ',',
            settings.Accounts
                .Where(account => account.Hidden)
                .Select(account => account.UnitKey.ToUpperInvariant())
                .Order(StringComparer.Ordinal)));

    private void ShowPendingWhatsNew()
    {
        if (_upgradeNotice.Pending is not { } summary || _isExiting)
        {
            return;
        }

        try
        {
            using var form = new WhatsNewForm(summary);
            form.Shown += (_, _) => _upgradeNotice.MarkDisplayed(AppIdentity.Version);
            form.ShowDialog();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Keep the notice queued. A later explicit interaction can try again.
        }
    }

    private void ApplyHotkeySetting()
    {
        if (_settings.GlobalHotkeyEnabled)
        {
            _messageWindow.TryRegisterHotkey();
        }
        else
        {
            _messageWindow.UnregisterHotkey();
        }
    }

    private async Task CheckForUpdatesIfDueAsync()
    {
        var checkedAt = DateTimeOffset.UtcNow;
        if (_isExiting ||
            _updateCheckRunning ||
            !_automaticUpdateChecksEnabled ||
            !UpdateChecker.IsCheckDue(_settings.LastUpdateCheckUtc, checkedAt))
        {
            return;
        }

        _updateCheckRunning = true;
        try
        {
            var release = await UpdateChecker.FindNewerReleaseAsync(_shutdown.Token);
            _settings.LastUpdateCheckUtc = checkedAt;
            _settings.Save();
            if (release is not null && !_isExiting)
            {
                await PromptForUpdateAsync(release);
            }
        }
        finally
        {
            _updateCheckRunning = false;
        }
    }

    private async Task PromptForUpdateAsync(UpdateRelease release)
    {
        var canInstall = release.Installer is not null && release.Checksum is not null;
        var accepted = PromptDialog.Ask(
            "UsageAI update",
            $"UsageAI {release.Version} is available",
            canInstall
                ? $"You are running {AppIdentity.Version}. Download the verified installer and install it now?"
                : $"You are running {AppIdentity.Version}. The automatic installer is unavailable, so the release page has the download.",
            canInstall ? "Install update" : "Open release page",
            "Later");
        if (!accepted || _isExiting)
        {
            return;
        }

        if (!canInstall)
        {
            OpenReleasePage(release.ReleasePageUrl);
            return;
        }

        await InstallUpdateAsync(release);
    }

    /// <summary>
    /// Downloads, verifies and starts the installer, showing each step in the progress window.
    /// Windows then closes this copy and reopens the new one, which finishes the bar.
    /// </summary>
    internal async Task InstallUpdateAsync(UpdateRelease release)
    {
        var window = NewUpdateWindow(release.ReleasePageUrl);
        window.ShowDownloading(release.Version, 0);
        window.ShowInTrayCorner();
        var progress = new Progress<double>(fraction =>
        {
            if (!window.IsDisposed)
            {
                window.ShowDownloading(release.Version, fraction);
            }
        });

        try
        {
            var installerPath = await _downloadInstaller(release, progress, _shutdown.Token);
            if (_isExiting)
            {
                return;
            }

            if (!window.IsDisposed)
            {
                window.ShowStarting(release.Version);
                // Painted now: the elevation prompt holds this thread until it is answered.
                window.Update();
            }

            UpdateHandoffStore.Save(new UpdateHandoff(AppIdentity.Version, release.Version, DateTimeOffset.UtcNow));
            Process installer;
            try
            {
                installer = _launchInstaller(installerPath);
            }
            catch (UpdateInstallException)
            {
                UpdateHandoffStore.Delete();
                throw;
            }

            if (!window.IsDisposed)
            {
                window.ShowInstalling(release.Version);
            }

            await WatchInstallerAsync(installer, window, release.Version);
        }
        catch (UpdateInstallException exception)
        {
            if (_isExiting)
            {
                return;
            }

            if (!window.IsDisposed)
            {
                // Said in the panel already on screen, rather than a dialog over everything.
                window.ShowFailed(release.Version, $"{exception.Message} The release page has the installer to download by hand.");
                return;
            }

            var openPage = PromptDialog.Ask(
                "UsageAI update",
                "The update could not be installed",
                $"{exception.Message} The GitHub release page has the installer to download by hand.",
                "Open release page",
                "Close",
                PromptKind.Warning);
            if (openPage)
            {
                OpenReleasePage(release.ReleasePageUrl);
            }
        }
    }

    /// <summary>
    /// Waits for the installer. Normally Windows closes this copy first, which cancels the wait.
    /// An installer that exits while this copy still runs either failed or installed into a
    /// folder this copy does not run from, and the window says which instead of waiting forever.
    /// </summary>
    private async Task WatchInstallerAsync(Process installer, UpdateProgressForm window, string version)
    {
        int exitCode;
        using (installer)
        {
            try
            {
                await installer.WaitForExitAsync(_shutdown.Token);
                exitCode = installer.ExitCode;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                // Nothing to watch: the restarted copy reports the outcome instead.
                return;
            }
        }

        if (_isExiting)
        {
            return;
        }

        // This copy outlived the installer, so it already knows the outcome, and must not find
        // the note again if it is the one started next.
        UpdateHandoffStore.Delete();
        if (window.IsDisposed)
        {
            return;
        }

        if (exitCode == 0)
        {
            window.ShowInstalledNotRunning(version);
            return;
        }

        window.ShowFailed(version, InstallerFailure(exitCode));
    }

    /// <summary>An Inno Setup exit code in words; the numbers are Setup's documented codes.</summary>
    internal static string InstallerFailure(int exitCode) => exitCode switch
    {
        2 or 5 => "The installer was cancelled. This version is still installed.",
        7 or 8 => "The installer could not prepare the update. This version is still installed.",
        _ => $"The installer stopped with error {exitCode}. This version is still installed.",
    };

    private static void OpenReleasePage(Uri releasePageUrl)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(releasePageUrl.AbsoluteUri)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception)
        {
            PromptDialog.Ask(
                "UsageAI update",
                "The release page did not open",
                "Windows could not open the UsageAI release page in a browser.",
                "OK",
                declineText: null,
                PromptKind.Warning);
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs eventArgs)
    {
        if (eventArgs.Mode == PowerModes.Resume)
        {
            // Values read before sleeping are stale by definition.
            RunOnUi(() => _ = RefreshAsync(force: true));
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs eventArgs)
    {
        var reason = eventArgs.Reason;
        RunOnUi(() => HandleSessionSwitch(reason));
    }

    /// <summary>
    /// Pauses scheduled polling when the session locks and resumes with a refresh when it unlocks.
    /// Runs on the UI thread, where the tick that reads <see cref="IsSessionLocked"/> also runs.
    /// </summary>
    internal void HandleSessionSwitch(SessionSwitchReason reason)
    {
        if (reason == SessionSwitchReason.SessionLock)
        {
            IsSessionLocked = true;
        }
        else if (reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon)
        {
            IsSessionLocked = false;
            _ = RefreshAsync(force: false);
            _ = RefreshIncidentsIfDueAsync();
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs eventArgs)
    {
        if (eventArgs.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility))
        {
            return;
        }

        RunOnUi(() =>
        {
            Theme.Reapply(_settings.Theme);
            ApplyMenuTheme();
            UpdateTray();
        });
    }

    private void ApplyMenuTheme() => TrayMenu.ApplyTheme(_menu);

    private void RunOnUi(Action action)
    {
        if (SynchronizationContext.Current == _syncContext)
        {
            action();
        }
        else
        {
            // A provider can finish after the context is disposed — the app exiting mid-refresh —
            // and its update must not reach the disposed tray icon and popup.
            _syncContext.Post(
                _ =>
                {
                    if (!_disposed)
                    {
                        action();
                    }
                },
                null);
        }
    }

    private void Exit()
    {
        _isExiting = true;
        _updateWindow?.Close();
        Application.Idle -= RefreshOnFirstIdle;
        _shutdown.Cancel();
        _tickTimer.Stop();
        _refreshAnimationTimer.Stop();
        _trayIcon.Visible = false;
        _popup.CloseForExit();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed = true;
            _isExiting = true;
            Application.Idle -= RefreshOnFirstIdle;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _service.Updated -= OnServiceUpdated;
            _service.AlertsRaised -= OnAlertsRaised;
            _service.AccountIdentityChanged -= OnAccountIdentityChanged;
            if (_accounts is not null)
            {
                _accounts.AccountsChanged -= OnAccountsChanged;
                _accounts.NoticesChanged -= OnNoticesChanged;
            }

            _shutdown.Cancel();
            _refreshAnimationTimer.Dispose();
            _tickTimer.Dispose();
            _messageWindow.Dispose();
            _trayIcon.Dispose();
            _menu.Dispose();
            _popup.Dispose();
            _service.Dispose();
            _currentIcon?.Dispose();
            AgyUsageProbe.DisposeHub();
        }

        base.Dispose(disposing);
    }

    internal static string TrayTooltip(ProviderStatus status, QuotaDisplay display = QuotaDisplay.Used) => Trim(
        $"UsageAI - {ShortProviderName(status)}: " +
        (TrayDisplayedPercent(status, display) is { } percent
            ? $"{HighContrastCue(TrayUsedPercent(status))}{percent}% {(display == QuotaDisplay.Remaining ? "left" : "used")}"
            : "no metered limit"),
        MaximumTooltipLength);

    /// <summary>
    /// The "! " or "!! " High Contrast puts before a limit past the warning or critical
    /// threshold, as the popup and dashboard do, since there the tray icon draws every status in
    /// the same system colour.
    /// </summary>
    private static string HighContrastCue(int usedPercent) =>
        Theme.IsHighContrast ? Theme.UsageCue(usedPercent) : string.Empty;

    internal static bool ShowsAllProviders(AppSettings settings) =>
        string.Equals(settings.TrayProviderId, AppSettings.AllProvidersTrayId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The tooltip that lists every connected provider, one line each, with the limits its popup
    /// tile draws ("Codex 10% | 22%"). A provider that would push the text past the shell's limit
    /// is counted in a closing "+N more" line instead of being cut off mid-reading.
    /// </summary>
    internal static string TrayTooltipAll(IReadOnlyList<ProviderStatus> connected, AppSettings settings)
    {
        var lines = connected
            .Where(status => status.Snapshot is not null)
            .Select(status =>
                $"{HighContrastCue(TrayRings(status, settings).Max(metric => metric.UsedPercent) ?? 0)}" +
                $"{ShortProviderName(status)} {TrayReadings(status, settings)}")
            .ToArray();
        var text = "UsageAI";
        for (var index = 0; index < lines.Length; index++)
        {
            var candidate = $"{text}\n{lines[index]}";
            var later = lines.Length - index - 1;
            var reserve = later > 0 ? $"\n+{later} more".Length : 0;
            if (candidate.Length + reserve > MaximumTooltipLength)
            {
                return Trim($"{text}\n+{lines.Length - index} more", MaximumTooltipLength);
            }

            text = candidate;
        }

        return text;
    }

    /// <summary>
    /// How full the logo's tank is when the tray lists every provider: the busiest limit the
    /// tooltip shows sets the colour, and the tank holds that limit's used share — or, when
    /// quotas show as remaining, what is left of it. With no metered limit the tank stays empty
    /// rather than claiming 100% left.
    /// </summary>
    internal static (int UsedPercent, int FillPercent) TankLevel(
        IReadOnlyList<ProviderStatus> connected,
        AppSettings settings)
    {
        var busiest = connected
            .Where(status => status.Snapshot is not null)
            .SelectMany(status => TrayRings(status, settings))
            .Max(metric => metric.UsedPercent);
        if (busiest is not { } used)
        {
            return (0, 0);
        }

        used = Math.Clamp(used, 0, 100);
        return (used, settings.QuotaDisplay == QuotaDisplay.Remaining ? 100 - used : used);
    }

    /// <summary>
    /// The popup tile's rings, so the tooltip, the tank and the popup agree on which limits and
    /// which model group matter.
    /// </summary>
    private static IReadOnlyList<UsageMetric> TrayRings(ProviderStatus status, AppSettings settings) =>
        PopupMetricSelection.Select(status.Snapshot!.Metrics, settings.PopupMetricGroup(status.ProviderId)).Rings;

    /// <summary>
    /// The rings as figures. Remaining mode carries one trailing unit, as the editor Status Bar
    /// does, and a provider without a metered limit says so rather than reading 0% or 100% left.
    /// </summary>
    private static string TrayReadings(ProviderStatus status, AppSettings settings)
    {
        var rings = TrayRings(status, settings);
        if (rings.Count == 0)
        {
            return "no metered limit";
        }

        var readings = string.Join(" | ", rings.Select(metric => $"{metric.DisplayedPercent(settings.QuotaDisplay)}%"));
        return settings.QuotaDisplay == QuotaDisplay.Remaining ? $"{readings} left" : readings;
    }

    /// <summary>Drives tray selection and gauge colour, so both stay on what is used.</summary>
    internal static int TrayUsedPercent(ProviderStatus status) =>
        status.Snapshot?.Primary?.UsedPercent ?? status.Snapshot?.HighestUsedPercent ?? 0;

    /// <summary>
    /// The number the tray tooltip states and the gauge ring fills to. Remaining mode returns
    /// null for a provider without a metered limit rather than claiming it has 100% left.
    /// </summary>
    internal static int? TrayDisplayedPercent(ProviderStatus status, QuotaDisplay display)
    {
        var used = Math.Clamp(TrayUsedPercent(status), 0, 100);
        if (display != QuotaDisplay.Remaining)
        {
            return used;
        }

        return status.Snapshot?.Metrics.Any(metric => metric.HasQuota) == true ? 100 - used : null;
    }

    /// <summary>
    /// Resolves the provider represented by the tray gauge. An unavailable pinned provider
    /// falls back to the most-consumed connected provider until it reconnects. Automatic
    /// follows the providers' default logins, so adding an account never moves the tray; an
    /// account is followed only when the user picks it.
    /// </summary>
    internal static ProviderStatus? SelectTrayStatus(
        IReadOnlyList<ProviderStatus> connected,
        string? preferredProviderId)
    {
        if (!string.IsNullOrWhiteSpace(preferredProviderId))
        {
            var preferred = connected.FirstOrDefault(status =>
                status.Snapshot is not null &&
                status.ProviderId.Equals(preferredProviderId, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
            {
                return preferred;
            }
        }

        var candidates = connected.Where(status => status.Snapshot is not null).ToArray();
        var primaries = candidates.Where(status => ProviderKey.IsDefault(status.ProviderId)).ToArray();
        return (primaries.Length > 0 ? primaries : candidates)
            .OrderByDescending(TrayUsedPercent)
            .FirstOrDefault();
    }

    /// <summary>
    /// The editor Status Bar's labels, so "Claude Code" reads as Claude in both, with the account
    /// when its provider shows more than one.
    /// </summary>
    private static string ShortProviderName(ProviderStatus status) =>
        status.AccountLabel is { } label
            ? $"{ShortProviderName(status.Kind, status.ProviderName)} · {label}"
            : ShortProviderName(status.Kind, status.ProviderName);

    private static string ShortProviderName(string providerId, string? fallback = null) =>
        ProviderNames.Short(providerId, fallback);

    private static string Trim(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..(maximumLength - 3)] + "...";
}
