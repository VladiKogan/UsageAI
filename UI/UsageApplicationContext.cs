using Microsoft.Win32;
using System.Diagnostics;
using UsageAI.Models;
using UsageAI.Services;

namespace UsageAI.UI;

internal sealed class UsageApplicationContext : ApplicationContext
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    private const int MaximumTooltipLength = 127;
    private const int MaximumBalloonTitle = 63;
    private const int MaximumBalloonText = 255;

    private readonly AppSettings _settings;
    private readonly IUsageClient[] _clients;
    private readonly UsageRefreshService _service;
    private readonly ContextMenuStrip _menu;
    private readonly NotifyIcon _trayIcon;
    private readonly UsagePopupForm _popup;
    private readonly MessageWindow _messageWindow;
    private readonly System.Windows.Forms.Timer _tickTimer;
    private readonly System.Windows.Forms.Timer _refreshAnimationTimer;
    private readonly ToolStripMenuItem _startupItem;
    private readonly bool _automaticUpdateChecksEnabled;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SynchronizationContext _syncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
    private Icon? _currentIcon;
    private float _refreshAnimationAngle;
    private bool _isExiting;
    private bool _updateCheckRunning;

    public UsageApplicationContext(IEnumerable<IUsageClient> clients, AppSettings settings)
        : this(clients, settings, showTrayIcon: true, enableAutomaticUpdateChecks: true)
    {
    }

    internal UsageApplicationContext(
        IEnumerable<IUsageClient> clients,
        AppSettings settings,
        bool showTrayIcon,
        bool enableAutomaticUpdateChecks = false)
    {
        _settings = settings;
        _automaticUpdateChecksEnabled = enableAutomaticUpdateChecks;
        _clients = clients.ToArray();
        Theme.Apply(_settings.Theme, _settings.WarningPercent, _settings.CriticalPercent);

        _service = new UsageRefreshService(_clients, _settings);
        _service.Updated += OnServiceUpdated;
        _service.AlertsRaised += OnAlertsRaised;

        _popup = new UsagePopupForm(_settings);
        _popup.RefreshRequested += async (_, _) => await RefreshAsync(force: true);
        _popup.SettingsRequested += (_, _) => OpenSettings();
        PushStateToPopup();

        _menu = new ContextMenuStrip
        {
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Renderer = new ToolStripProfessionalRenderer(new DarkColorTable()),
            ShowImageMargin = false,
            Padding = new Padding(4),
        };
        var openItem = new ToolStripMenuItem("Open")
        {
            Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
        };
        openItem.Click += (_, _) => OpenDashboard();
        _menu.Items.Add(openItem);
        _menu.Items.Add("Refresh", null, async (_, _) => await RefreshAsync(force: true));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        _startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = StartupManager.IsEnabled,
            CheckOnClick = true,
        };
        _startupItem.CheckedChanged += StartupItemOnCheckedChanged;
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => Exit());

        _currentIcon = TrayIconFactory.Create(0, "U");
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
        _messageWindow.ShowRequested += (_, _) => OpenDashboard();
        _messageWindow.HotkeyPressed += (_, _) => ToggleCompactPopup();
        ApplyHotkeySetting();

        _tickTimer = new System.Windows.Forms.Timer
        {
            Interval = (int)TickInterval.TotalMilliseconds,
        };
        _tickTimer.Tick += async (_, _) =>
        {
            await RefreshIfDueAsync();
            await CheckForUpdatesIfDueAsync();
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
        _ = RefreshAsync(force: true);
        _ = CheckForUpdatesIfDueAsync();
    }

    private async Task RefreshIfDueAsync()
    {
        if (_isExiting || !_service.IsDue(DateTimeOffset.Now))
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

    private void OnServiceUpdated(object? sender, EventArgs eventArgs) => RunOnUi(() =>
    {
        if (_isExiting)
        {
            return;
        }

        PushStateToPopup();
        UpdateTray();
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
        _popup.SetStates(_service.Statuses, _service.IsRefreshing, _service.LastRefreshed, _service.History);

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
            ReplaceIcon(TrayIconFactory.Create(100, "!", hasError: true));
            _trayIcon.Text = "UsageAI - no connected providers";
            return;
        }

        var trayStatus = SelectTrayStatus(connected, _settings.TrayProviderId)!;
        var usedPercent = TrayUsedPercent(trayStatus);
        ReplaceIcon(TrayIconFactory.Create(
            usedPercent,
            ProviderGlyph(trayStatus.ProviderId),
            identityColor: Theme.ForProvider(trayStatus.ProviderId)));
        _trayIcon.Text = TrayTooltip(trayStatus);
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
            PushStateToPopup();
            _popup.ShowNearTray(DashboardMode.Full);
        }
    }

    private void OpenSettings()
    {
        using var dialog = new SettingsForm(
            _settings,
            _clients.Select(client => (client.Id, client.DisplayName)).ToArray(),
            UpdateChecker.CheckForUpdateAsync,
            PromptForUpdateAsync);
        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        Theme.Apply(_settings.Theme, _settings.WarningPercent, _settings.CriticalPercent);
        ApplyHotkeySetting();
        _service.ApplySettings();
        PushStateToPopup();
        UpdateTray();
        _ = RefreshAsync(force: false);
        _ = CheckForUpdatesIfDueAsync();
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
        var choice = MessageBox.Show(
            canInstall
                ? $"UsageAI {release.Version} is available. You are running {AppIdentity.Version}.\n\n" +
                  "Download the verified installer and install it now?"
                : $"UsageAI {release.Version} is available. You are running {AppIdentity.Version}.\n\n" +
                  "The automatic installer is unavailable. Open the release page?",
            "UsageAI update available",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button1);
        if (choice != DialogResult.Yes || _isExiting)
        {
            return;
        }

        if (!canInstall)
        {
            OpenReleasePage(release.ReleasePageUrl);
            return;
        }

        _trayIcon.ShowBalloonTip(
            5_000,
            "Downloading UsageAI update",
            $"Downloading and verifying UsageAI {release.Version}...",
            ToolTipIcon.Info);

        try
        {
            var installerPath = await UpdateInstaller.DownloadAndVerifyAsync(release, _shutdown.Token);
            if (!_isExiting)
            {
                UpdateInstaller.Launch(installerPath);
            }
        }
        catch (UpdateInstallException exception)
        {
            if (_isExiting)
            {
                return;
            }

            var openPage = MessageBox.Show(
                $"{exception.Message}\n\nOpen the GitHub release page instead?",
                "UsageAI update",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1);
            if (openPage == DialogResult.Yes)
            {
                OpenReleasePage(release.ReleasePageUrl);
            }
        }
    }

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
            MessageBox.Show(
                "Windows could not open the UsageAI release page.",
                "UsageAI update",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
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
        if (eventArgs.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon)
        {
            RunOnUi(() => _ = RefreshAsync(force: false));
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs eventArgs)
    {
        if (eventArgs.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color) ||
            _settings.Theme != ThemeMode.System)
        {
            return;
        }

        RunOnUi(() =>
        {
            Theme.Reapply(_settings.Theme);
            _menu.BackColor = Theme.Surface;
            _menu.ForeColor = Theme.Text;
            UpdateTray();
        });
    }

    private void RunOnUi(Action action)
    {
        if (SynchronizationContext.Current == _syncContext)
        {
            action();
        }
        else
        {
            _syncContext.Post(_ => action(), null);
        }
    }

    private void StartupItemOnCheckedChanged(object? sender, EventArgs eventArgs)
    {
        try
        {
            StartupManager.SetEnabled(_startupItem.Checked);
        }
        catch (Exception)
        {
            _startupItem.CheckedChanged -= StartupItemOnCheckedChanged;
            _startupItem.Checked = StartupManager.IsEnabled;
            _startupItem.CheckedChanged += StartupItemOnCheckedChanged;
            MessageBox.Show(
                "Windows startup settings could not be updated.",
                "UsageAI",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void Exit()
    {
        _isExiting = true;
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
            Application.Idle -= RefreshOnFirstIdle;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _service.Updated -= OnServiceUpdated;
            _service.AlertsRaised -= OnAlertsRaised;
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

    internal static string TrayTooltip(ProviderStatus status) => Trim(
        $"UsageAI - {ShortProviderName(status.ProviderName)}: " +
        $"{TrayUsedPercent(status)}% used",
        MaximumTooltipLength);

    internal static int TrayUsedPercent(ProviderStatus status) =>
        status.Snapshot?.Primary?.UsedPercent ?? status.Snapshot?.HighestUsedPercent ?? 0;

    /// <summary>
    /// Resolves the provider represented by the tray gauge. An unavailable pinned provider
    /// falls back to the most-consumed connected provider until it reconnects.
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

        return connected
            .Where(status => status.Snapshot is not null)
            .OrderByDescending(TrayUsedPercent)
            .FirstOrDefault();
    }

    private static string ProviderGlyph(string providerId) => providerId.ToLowerInvariant() switch
    {
        "claude" => "A",
        "copilot" => "G",
        _ => "C",
    };

    private static string ShortProviderName(string providerName) =>
        providerName.Equals("GitHub Copilot", StringComparison.OrdinalIgnoreCase) ? "Copilot" : providerName;

    private static string Trim(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..(maximumLength - 3)] + "...";

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Theme.SurfaceRaised;
        public override Color MenuItemBorder => Theme.Hairline;
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
        public override Color SeparatorDark => Theme.Hairline;
        public override Color SeparatorLight => Theme.Hairline;
    }
}
