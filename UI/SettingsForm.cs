using UsageAI.Services;
using UsageAI.Models;

namespace UsageAI.UI;

/// <summary>What a change made in Settings asks of the app beyond being saved.</summary>
[Flags]
internal enum SettingsChanges
{
    None = 0,

    /// <summary>Redraw the popup, dashboard and tray icon from the cached readings.</summary>
    Display = 1,

    /// <summary>The theme or its warning and critical colours changed.</summary>
    Theme = 2,

    /// <summary>The global hotkey was turned on or off.</summary>
    Hotkey = 4,

    /// <summary>Which providers are polled, or how often, may have changed.</summary>
    Polling = 8,

    /// <summary>Which accounts are read changed, so the clients are built again.</summary>
    Accounts = 16,

    /// <summary>
    /// An account is shown that was not, so it is read once the changes settle. Said on its own
    /// when the account manager already rebuilt the clients for it.
    /// </summary>
    AccountShown = 32,
}

internal sealed class SettingsChangedEventArgs(SettingsChanges changes) : EventArgs
{
    public SettingsChanges Changes { get; } = changes;
}

/// <summary>A question Settings asks before a change it cannot take back.</summary>
internal sealed record SettingsPrompt(
    string Caption,
    string Title,
    string Body,
    string AcceptText,
    string? DeclineText,
    PromptKind Kind = PromptKind.Question,
    bool DeclineIsDefault = false);

/// <summary>
/// Preferences that used to be compile-time constants: refresh cadence, alert thresholds,
/// theme, history, and which providers appear and in what order. One page per subject, chosen in
/// the list on the left; every change is saved as it is made, so there is no Save or Cancel.
/// </summary>
internal sealed partial class SettingsForm : Form
{
    // Indexes match ThemeMode, MetricDisplayMode and QuotaDisplay.
    private static readonly string[] ThemeOptions = { "System", "Dark", "Light" };
    private static readonly string[] MetricOptions = { "All", "Metered", "Important" };
    private static readonly string[] QuotaOptions = { "Used", "Remaining" };
    internal static readonly string[] PageNames = { "General", "Alerts", "Appearance", "Providers", AccountsSection, "About" };
    private const int NavWidthBaseline = 176;
    private const string SectionHeaderTag = "section-header";
    private const int DefaultWidth = 700;
    private const int DefaultHeight = 640;

    /// <summary>How long a number box waits after its last change before saving it.</summary>
    internal const int NumberCommitDelayMilliseconds = 600;

    private readonly List<Font> _ownedFonts = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AppSettings _settings;
    private readonly int? _dpiOverride;
    private Size _sizeAtResizeBegin;
    private bool _resizedByUser;
    private readonly Func<CancellationToken, Task<UpdateCheckResult>> _checkForUpdates;
    private readonly Func<UpdateRelease, Task> _promptForUpdate;
    private readonly TableLayoutPanel _shell;
    private readonly Panel _scrollHost;
    private readonly SlimScrollHost _scrollFrame;
    private readonly List<SettingsPage> _pages = new();
    private SettingsPage? _building;
    private int _currentPage = -1;
    private readonly NumericUpDown _refreshInterval;
    private readonly CheckBox _slowWhenHidden;
    private readonly CheckBox _notificationsEnabled;
    private readonly NumericUpDown _firstAlert;
    private readonly NumericUpDown _secondAlert;
    private readonly CheckBox _notifyOnReset;
    private readonly CheckBox _showIncidents;
    private readonly NumericUpDown _warningPercent;
    private readonly NumericUpDown _criticalPercent;
    private readonly SegmentedControl _theme;
    private readonly SegmentedControl _metricDisplayMode;
    private readonly SegmentedControl _quotaDisplay;
    private readonly SectionNav _nav;
    private readonly TableLayoutPanel _navColumn;
    private const int SearchRowHeight = 50;
    private readonly SettingPreview _quotaPreview;
    private readonly SettingPreview _thresholdPreview;
    private UpdateTone _updateTone = UpdateTone.Neutral;
    private readonly ComboBox _trayProvider;
    private readonly CheckBox _historyEnabled;
    private readonly CheckBox _forecastEnabled;
    private readonly CheckBox _hotkeyEnabled;
    private readonly CheckBox _startWithWindows;
    private readonly Label _startupHint;
    private readonly CheckedListBox _providers;
    private readonly List<PopupGroupPicker> _popupGroupPickers = new();
    private readonly Button _checkForUpdatesButton;
    private readonly Label _updateStatus;
    private readonly Label _versionLabel;
    private readonly Func<StartupState> _readStartup;
    private readonly Action<bool> _writeStartup;
    private readonly System.Windows.Forms.Timer _numberTimer;
    private readonly Dictionary<NumericUpDown, (Action Write, SettingsChanges Changes)> _pendingNumbers = new();
    private StartupState _startupState;
    private bool _startupLoadedChecked;
    private bool _startupFailed;
    private bool _isCheckingForUpdates;
    private bool _resourcesDisposed;
    private bool _loading;
    private int _dragCandidate = -1;
    private int _dragIndex = -1;
    private Point _dragOrigin;
    private bool _movingProvider;
    private HashSet<string> _dragChecks = new(StringComparer.OrdinalIgnoreCase);
    private List<ProviderEntry> _dragOrder = new();

    public SettingsForm(AppSettings settings, IReadOnlyList<(string Id, string DisplayName)> providers)
        : this(
            settings,
            providers,
            UpdateChecker.CheckForUpdateAsync,
            static _ => Task.CompletedTask,
            dpiOverride: null)
    {
    }

    internal SettingsForm(
        AppSettings settings,
        IReadOnlyList<(string Id, string DisplayName)> providers,
        Func<CancellationToken, Task<UpdateCheckResult>> checkForUpdates,
        Func<UpdateRelease, Task> promptForUpdate,
        int? dpiOverride = null,
        Func<StartupState>? readStartup = null,
        Action<bool>? writeStartup = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? popupGroups = null,
        SettingsAccountActions? accounts = null,
        IReadOnlyList<ProviderStatus>? accountStatuses = null,
        IReadOnlyList<(string Id, string DisplayName)>? trayChoices = null)
    {
        _settings = settings;
        _accountActions = accounts;
        _accountStatuses = accountStatuses ?? Array.Empty<ProviderStatus>();
        _dpiOverride = dpiOverride;
        _checkForUpdates = checkForUpdates;
        _promptForUpdate = promptForUpdate;
        _readStartup = readStartup ?? (static () => StartupManager.Current);
        _writeStartup = writeStartup ?? StartupManager.SetEnabled;
        Confirm = prompt => PromptDialog.Ask(
            this,
            prompt.Caption,
            prompt.Title,
            prompt.Body,
            prompt.AcceptText,
            prompt.DeclineText,
            prompt.Kind,
            prompt.DeclineIsDefault);
        var scale = Scale();

        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Night;
        ForeColor = Theme.Text;
        // A window of its own rather than a dialog: every change applies as it is made, so there is
        // nothing to confirm, and the tray app shows it with no owner, so it is never hidden along
        // with the popup it was opened from (0.19.0–0.19.1). Its taskbar button and minimize box
        // keep it findable once other windows cover it. Resizable, since a page can be longer than
        // the window; not maximizable, because rows stretched across a whole screen are hard to read.
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = true;
        SizeGripStyle = SizeGripStyle.Hide;
        ShowInTaskbar = true;
        WindowThemeHelpers.RevealWhenPainted(this);
        StartPosition = FormStartPosition.CenterScreen;
        Text = "UsageAI settings";
        AppIcon.Apply(this);
        _builtFactor = scale.Factor;
        _baselineSize = settings.SettingsPageSize is [var width, var height] ? new Size(width, height) : new Size(DefaultWidth, DefaultHeight);
        SizeToBaseline(Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, scale[1920], scale[1080]));
        Font = Own(Typography.Text(9F));

        _shell = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            RowCount = 1,
        };
        _shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, scale[NavWidthBaseline]));
        _shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(_shell);

        _scrollHost = new Panel
        {
            AutoScroll = true,
            Margin = Padding.Empty,
        };
        _scrollHost.HandleCreated += OnScrollableControlHandleCreated;
        // A page longer than the window scrolls behind a slim scrollbar rather than the system's.
        _scrollFrame = new SlimScrollHost(_scrollHost, _dpiOverride)
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        _shell.Controls.Add(_scrollFrame, 1, 0);

        // One page at a time; the list on the left chooses it and marks the one shown, under the
        // search box that finds a setting on any of them.
        _navColumn = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            RowCount = 2,
        };
        _navColumn.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _navColumn.RowStyles.Add(new RowStyle(SizeType.Absolute, scale[SearchRowHeight]));
        _navColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        CreateSearch(scale);
        _nav = new SectionNav(PageNames)
        {
            Dock = DockStyle.Fill,
            DpiOverride = _dpiOverride,
            Glyphs = PageGlyphs,
            Margin = scale.Pad(8, 6, 4, 8),
        };
        _nav.SectionChosen += (_, _) => ShowPage(_nav.SelectedIndex);
        _nav.SectionEntered += (_, _) => EnterPage(_nav.SelectedIndex);
        _navColumn.Controls.Add(_search, 0, 0);
        _navColumn.Controls.Add(_nav, 0, 1);
        _shell.Controls.Add(_navColumn, 0, 0);
        _search.TabIndex = 0;
        _nav.TabIndex = 1;
        _navColumn.TabIndex = 0;
        _scrollFrame.TabIndex = 1;
        // Opens with the page list focused, as before the search box sat above it: the box is
        // one Shift+Tab or Ctrl+F away, and an empty box under focus read as a prompt to type.
        ActiveControl = _nav;

        _numberTimer = new System.Windows.Forms.Timer { Interval = NumberCommitDelayMilliseconds };
        _numberTimer.Tick += (_, _) => FlushPendingChanges();

        _refreshInterval = CreateNumeric(AppSettings.MinimumRefreshMinutes, AppSettings.MaximumRefreshMinutes);
        _slowWhenHidden = CreateCheckBox("Slow down while no window is open");
        _notificationsEnabled = CreateCheckBox("Show tray notifications");
        _firstAlert = CreateNumeric(1, 100);
        _secondAlert = CreateNumeric(1, 100);
        _notifyOnReset = CreateCheckBox("Announce window resets");
        _showIncidents = CreateCheckBox("Show provider incidents from their status pages");
        _warningPercent = CreateNumeric(1, 99);
        _criticalPercent = CreateNumeric(2, 100);
        _historyEnabled = CreateCheckBox("Record usage history on this machine");
        _forecastEnabled = CreateCheckBox("Show burn-rate forecast");
        _hotkeyEnabled = CreateCheckBox("Global hotkey (Win+Alt+U)");
        _startWithWindows = CreateCheckBox("Start with Windows");
        _startupHint = new Label
        {
            AutoSize = true,
            ForeColor = Theme.Muted,
            Margin = scale.Pad(0, 0, 0, 8),
        };
        // Short choices are segmented controls: every option visible, one click to change.
        _theme = CreateSegmented(ThemeOptions, "Theme");
        _metricDisplayMode = CreateSegmented(MetricOptions, "Dashboard metric display mode");
        _quotaDisplay = CreateSegmented(QuotaOptions, "Quota display");

        // Each preview shows what its setting produces, so the choice needs no explanation.
        _quotaPreview = new SettingPreview(SettingPreviewKind.QuotaRing)
        {
            AccessibleName = "Quota display preview",
            Dock = DockStyle.Fill,
            DpiOverride = _dpiOverride,
            Height = scale[QuotaPreviewHeight],
            Margin = scale.Pad(0, 0, 0, 6),
        };
        _quotaDisplay.SelectedIndexChanged += (_, _) => _quotaPreview.ShowRemaining = _quotaDisplay.SelectedIndex == 1;
        _thresholdPreview = new SettingPreview(SettingPreviewKind.ThresholdBand)
        {
            AccessibleName = "Status colour preview",
            Dock = DockStyle.Fill,
            DpiOverride = _dpiOverride,
            Height = scale[ThresholdPreviewHeight],
            Margin = scale.Pad(0, 0, 0, 8),
        };
        // Critical starts at least a point above warning, the rule the saved setting follows, so
        // the boxes never hold a pair that would be saved as something else.
        _warningPercent.ValueChanged += (_, _) =>
            _criticalPercent.Minimum = Math.Min(_criticalPercent.Maximum, _warningPercent.Value + 1);
        _warningPercent.ValueChanged += (_, _) => UpdateThresholdPreview();
        _criticalPercent.ValueChanged += (_, _) => UpdateThresholdPreview();

        _trayProvider = new ThemedComboBox
        {
            AccessibleName = "Tray icon provider",
            BackColor = Theme.SurfaceRaised,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text,
            Margin = scale.Pad(0, 4, 0, 4),
        };

        _providers = new SettingsCheckList
        {
            WheelTarget = _scrollHost,
            BackColor = Theme.SurfaceRaised,
            BorderStyle = BorderStyle.None,
            CheckOnClick = true,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text,
            Height = scale[104],
            IntegralHeight = false,
            Margin = scale.Pad(0, 4, 0, 4),
        };
        _providers.HandleCreated += OnScrollableControlHandleCreated;
        _providers.AccessibleDescription =
            "Tick a provider to show it. Reorder with Alt+Up and Alt+Down, or drag an entry.";
        _providers.MouseDown += OnProvidersMouseDown;
        _providers.MouseMove += OnProvidersMouseMove;
        _providers.MouseUp += OnProvidersMouseUp;
        _providers.ItemCheck += OnProvidersItemCheck;
        foreach (var id in _settings.OrderProviders(providers.Select(provider => provider.Id).ToArray()))
        {
            var provider = providers.First(candidate =>
                candidate.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            _providers.Items.Add(
                new ProviderEntry(provider.Id, provider.DisplayName),
                _settings.IsProviderVisible(provider.Id));
            _trayProviders.Add((provider.Id, provider.DisplayName));
        }

        _committedProviderOrder = ProviderIdsInList();
        FillTrayChoices(trayChoices ?? Array.Empty<(string Id, string DisplayName)>());

        _checkForUpdatesButton = CreateDialogButton("Check for updates", scale, primary: false);
        _checkForUpdatesButton.AutoSize = true;
        _checkForUpdatesButton.MinimumSize = new Size(scale[142], scale[30]);
        _checkForUpdatesButton.Margin = scale.Pad(0, 4, 0, 4);
        _checkForUpdatesButton.Click += async (_, _) => await CheckForUpdatesAsync();

        _updateStatus = new Label
        {
            AutoSize = true,
            ForeColor = Theme.Muted,
            Margin = scale.Pad(0, 2, 0, 8),
            Text = "Updates are also checked automatically once a day.",
        };

        AddPage("General");
        AddSection("Refresh");
        AddRow("Refresh every (minutes)", _refreshInterval);
        AddSpan(_slowWhenHidden);
        AddSection("Startup");
        AddSpan(_startWithWindows);
        AddSpan(_startupHint);
        AddSpan(_hotkeyEnabled);
        AddSection("History");
        AddSpan(_historyEnabled);
        AddSpan(_forecastEnabled);
        AddSpan(CreateLinkButton("Delete recorded history", (_, _) => DeleteRecordedHistory(), destructive: true));

        AddPage("Alerts");
        AddSection("Notifications");
        AddSpan(_notificationsEnabled);
        AddRow("First alert at (% used)", _firstAlert);
        AddRow("Second alert at (% used)", _secondAlert);
        AddSpan(_notifyOnReset);
        AddSection("Provider status");
        AddSpan(_showIncidents);

        AddPage("Appearance");
        AddSection("Theme");
        AddRow("Mode", _theme);
        AddSection("Quota");
        AddRow("Show quota as", _quotaDisplay);
        AddSpan(_quotaPreview);
        AddSpan(CreateHint("Remaining shows what is left on meters, headlines, the tray icon and alerts. Colours and alert thresholds still follow usage."));
        AddSection("Colours");
        AddRow("Warning colour from (% used)", _warningPercent);
        AddRow("Critical colour from (% used)", _criticalPercent);
        AddSpan(_thresholdPreview);
        AddSection("Dashboard");
        AddRow("Dashboard rows", _metricDisplayMode);
        AddSpan(CreateHint("Metered shows every percentage limit. Important shows the highest session, rolling and monthly limit."));
        AddSection("Tray icon");
        AddRow("Shows", _trayProvider);
        AddSpan(CreateHint("The tray icon's tank fills with the chosen provider's or account's usage. Automatic follows the connected provider with the highest active primary usage. All connected fills it to the busiest limit and lists every provider when you point at it.", belowRow: true));

        AddPage("Providers");
        AddSection("Shown providers");
        AddSpan(CreateHint("Tick to show. Drag a provider to reorder it, or use Alt+Up / Alt+Down."));
        AddSpan(_providers);
        AddSpan(CreateOrderButtons(scale));
        AddPopupGroupPickers(trayChoices is { Count: > 0 } ? trayChoices : providers, popupGroups, scale);

        AddAccountsSection(scale);

        AddPage("About");
        AddSection("UsageAI");
        _versionLabel = new Label
        {
            AutoSize = true,
            Font = Own(Typography.Figures(10F)),
            ForeColor = Theme.Text,
            Margin = scale.Pad(0, 8, 0, 4),
            Text = $"v{AppIdentity.Version}",
        };
        AddRow("Installed version", _versionLabel);
        AddSpan(_checkForUpdatesButton);
        AddSpan(CreateLinkButton("What's new", (_, _) => WhatsNewForm.ShowCurrent(this)));
        AddSpan(_updateStatus);
        AddSection("Support the project");
        AddSpan(CreateHint("UsageAI is free and open source. If it helps you, a star on GitHub helps others find it."));
        AddSpan(CreateSupportLinks(scale));
        FinishPages();

        LoadValues();
        WireChanges();
        ShowPage(0);
        Theme.Changed += OnThemeChanged;
        ApplyScaledLayout();
        ApplyThemeColors();
    }

    /// <summary>A change was saved; the app redraws, re-themes or polls as it asks.</summary>
    public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;

    /// <summary>Asks a question owned by this window. Replaceable so checks never show a dialog.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<SettingsPrompt, bool> Confirm { get; set; }

    /// <summary>The page on screen, as an index into <see cref="PageNames"/>.</summary>
    internal int CurrentPage => _currentPage;

    private sealed class SettingsPage(string name, BufferedTableLayoutPanel layout)
    {
        public string Name { get; } = name;

        public BufferedTableLayoutPanel Layout { get; } = layout;

        public List<Section> Sections { get; } = new();
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_isCheckingForUpdates)
        {
            return;
        }

        _isCheckingForUpdates = true;
        _checkForUpdatesButton.Enabled = false;
        _checkForUpdatesButton.Text = "Checking...";
        SetUpdateTone(UpdateTone.Neutral);
        _updateStatus.Text = "Contacting GitHub releases...";

        try
        {
            var result = await _checkForUpdates(_lifetime.Token);
            if (_lifetime.IsCancellationRequested || IsDisposed)
            {
                return;
            }

            _settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
            _settings.Save();

            if (!result.Succeeded)
            {
                SetUpdateTone(UpdateTone.Warning);
                _updateStatus.Text = "Couldn’t check for updates. Check your connection and try again.";
                return;
            }

            if (result.Release is null)
            {
                SetUpdateTone(UpdateTone.Success);
                _updateStatus.Text = $"You’re up to date — v{AppIdentity.Version} is the latest version.";
                return;
            }

            SetUpdateTone(UpdateTone.Available);
            _updateStatus.Text = $"Version {result.Release.Version} is available.";
            await _promptForUpdate(result.Release);
        }
        finally
        {
            _isCheckingForUpdates = false;
            if (!IsDisposed)
            {
                _checkForUpdatesButton.Enabled = true;
                _checkForUpdatesButton.Text = "Check for updates";
            }
        }
    }

    private enum UpdateTone
    {
        Neutral,
        Warning,
        Success,
        Available,
    }

    private void SetUpdateTone(UpdateTone tone)
    {
        _updateTone = tone;
        _updateStatus.ForeColor = UpdateToneColor(tone);
    }

    private static Color UpdateToneColor(UpdateTone tone) => tone switch
    {
        UpdateTone.Warning => Theme.WarningText,
        UpdateTone.Success => Theme.Success,
        UpdateTone.Available => Theme.Signal,
        _ => Theme.Muted,
    };

    private void LoadValues()
    {
        _loading = true;
        try
        {
            _refreshInterval.Value = Math.Clamp(
                _settings.RefreshIntervalMinutes,
                AppSettings.MinimumRefreshMinutes,
                AppSettings.MaximumRefreshMinutes);
            _slowWhenHidden.Checked = _settings.SlowRefreshWhenHidden;
            _firstAlert.Value = _settings.AlertThreshold(0, 80);
            _secondAlert.Value = _settings.AlertThreshold(1, 95);
            _notifyOnReset.Checked = _settings.NotifyOnReset;
            _showIncidents.Checked = _settings.ShowProviderIncidents;
            _warningPercent.Value = _settings.WarningPercent;
            _criticalPercent.Value = _settings.CriticalPercent;
            _theme.SelectedIndex = (int)_settings.Theme;
            _metricDisplayMode.SelectedIndex = (int)_settings.MetricDisplayMode;
            UpdateThresholdPreview();
            _historyEnabled.Checked = _settings.HistoryEnabled;
            _forecastEnabled.Checked = _settings.ForecastEnabled;
            _hotkeyEnabled.Checked = _settings.GlobalHotkeyEnabled;
            LoadStartupState();
            LoadAccountValues();
            LoadQuickValues();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// The values the tray menu's quick settings also change: one loader for opening the window
    /// and for a change made from the tray while it is open, so the two never drift apart.
    /// </summary>
    private void LoadQuickValues()
    {
        _notificationsEnabled.Checked = _settings.NotificationsEnabled;
        _quotaDisplay.SelectedIndex = (int)_settings.QuotaDisplay;
        _quotaPreview.ShowRemaining = _settings.QuotaDisplay == QuotaDisplay.Remaining;
        SelectTrayProvider();
    }

    private void SelectTrayProvider()
    {
        _trayProvider.SelectedIndex = 0;
        if (_settings.TrayProviderId is not { } trayProviderId)
        {
            return;
        }

        for (var index = 1; index < _trayProvider.Items.Count; index++)
        {
            var entry = (ProviderEntry)_trayProvider.Items[index]!;
            if (entry.Id.Equals(trayProviderId, StringComparison.OrdinalIgnoreCase))
            {
                _trayProvider.SelectedIndex = index;
                break;
            }
        }
    }

    /// <summary>
    /// Shows the values the tray menu's quick settings change — what the tray icon shows, quota
    /// used or remaining, notifications — after one changed while this window was open, so the
    /// window never shows, or later saves, a value that is no longer the setting.
    /// </summary>
    internal void ReloadQuickSettings(IReadOnlyList<(string Id, string DisplayName)>? trayChoices = null)
    {
        if (trayChoices is not null)
        {
            UpdateTrayChoices(trayChoices);
        }

        _loading = true;
        try
        {
            LoadQuickValues();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Saves each control's value as it changes. Each writes only its own setting, so a change
    /// made elsewhere while the window is open — the tray menu, an account found by a scan — is
    /// never overwritten with what the window showed when it opened.
    /// </summary>
    private void WireChanges()
    {
        WireNumber(_refreshInterval, () => _settings.RefreshIntervalMinutes = (int)_refreshInterval.Value, SettingsChanges.Polling);
        WireToggle(_slowWhenHidden, on => _settings.SlowRefreshWhenHidden = on, SettingsChanges.Polling);
        WireToggle(_notificationsEnabled, on => _settings.NotificationsEnabled = on, SettingsChanges.Display);
        WireNumber(_firstAlert, WriteAlertThresholds, SettingsChanges.None);
        WireNumber(_secondAlert, WriteAlertThresholds, SettingsChanges.None);
        WireToggle(_notifyOnReset, on => _settings.NotifyOnReset = on, SettingsChanges.None);
        WireToggle(_showIncidents, on => _settings.ShowProviderIncidents = on, SettingsChanges.Display);
        WireNumber(_warningPercent, WriteColourThresholds, SettingsChanges.Theme | SettingsChanges.Display);
        WireNumber(_criticalPercent, WriteColourThresholds, SettingsChanges.Theme | SettingsChanges.Display);
        _theme.SelectedIndexChanged += (_, _) => Write(
            () => _settings.Theme = _theme.SelectedIndex switch
            {
                1 => ThemeMode.Dark,
                2 => ThemeMode.Light,
                _ => ThemeMode.System,
            },
            SettingsChanges.Theme | SettingsChanges.Display);
        _metricDisplayMode.SelectedIndexChanged += (_, _) => Write(
            () => _settings.MetricDisplayMode = _metricDisplayMode.SelectedIndex switch
            {
                1 => MetricDisplayMode.MeteredOnly,
                2 => MetricDisplayMode.ImportantOnly,
                _ => MetricDisplayMode.All,
            },
            SettingsChanges.Display);
        _quotaDisplay.SelectedIndexChanged += (_, _) => Write(
            () => _settings.QuotaDisplay = _quotaDisplay.SelectedIndex == 1 ? QuotaDisplay.Remaining : QuotaDisplay.Used,
            SettingsChanges.Display);
        WireToggle(_historyEnabled, on => _settings.HistoryEnabled = on, SettingsChanges.Display);
        WireToggle(_forecastEnabled, on => _settings.ForecastEnabled = on, SettingsChanges.Display);
        WireToggle(_hotkeyEnabled, on => _settings.GlobalHotkeyEnabled = on, SettingsChanges.Hotkey);
        _startWithWindows.CheckedChanged += (_, _) =>
        {
            if (!_loading)
            {
                ApplyStartupPreference();
            }
        };
        _trayProvider.SelectedIndexChanged += (_, _) => Write(
            () => _settings.TrayProviderId = _trayProvider.SelectedItem is ProviderEntry { Id.Length: > 0 } trayProvider
                ? trayProvider.Id
                : null,
            SettingsChanges.Display);
        _providers.ItemCheck += OnProviderTicked;
        foreach (var picker in _popupGroupPickers)
        {
            picker.Control.SelectedIndexChanged += (_, _) => Write(
                () => _settings.SetPopupMetricGroup(
                    picker.ProviderId,
                    picker.Control.SelectedIndex > 0 ? picker.Groups[picker.Control.SelectedIndex - 1] : null),
                SettingsChanges.Display);
        }

        WireAccountChanges();
    }

    private void WriteAlertThresholds() =>
        _settings.NotifyAtPercent = new[] { (int)_firstAlert.Value, (int)_secondAlert.Value };

    /// <summary>
    /// Writes warning and critical together, as the old Save did. Written one at a time, a pair
    /// passing through an order the saved setting does not allow (critical below warning) was
    /// clamped on the way and stayed clamped.
    /// </summary>
    private void WriteColourThresholds()
    {
        _settings.WarningPercent = (int)_warningPercent.Value;
        _settings.CriticalPercent = (int)_criticalPercent.Value;
    }

    /// <summary>
    /// Shows the numbers just saved as the app now has them. Saving tidies them — alert
    /// thresholds are sorted and a repeated one dropped — so a pair is shown again whole. Only
    /// the boxes saved and their partners are touched, never one still being typed in.
    /// </summary>
    private void ShowSavedNumbers(IReadOnlyCollection<NumericUpDown> saved)
    {
        _loading = true;
        try
        {
            if (saved.Contains(_firstAlert) || saved.Contains(_secondAlert))
            {
                _firstAlert.Value = _settings.AlertThreshold(0, 80);
                _secondAlert.Value = _settings.AlertThreshold(1, 95);
            }

            if (saved.Contains(_warningPercent) || saved.Contains(_criticalPercent))
            {
                _warningPercent.Value = _settings.WarningPercent;
                _criticalPercent.Value = Math.Max(_criticalPercent.Minimum, _settings.CriticalPercent);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void WireToggle(CheckBox toggle, Action<bool> write, SettingsChanges changes) =>
        toggle.CheckedChanged += (_, _) => Write(() => write(toggle.Checked), changes);

    /// <summary>
    /// A number box saves once it settles: on Enter, on leaving it, or a moment after its last
    /// step, so holding an arrow or clicking it several times is one save and, for the refresh
    /// interval, one poll. Reading <see cref="NumericUpDown.Value"/> first takes in a number still
    /// being typed.
    /// </summary>
    private void WireNumber(NumericUpDown number, Action write, SettingsChanges changes)
    {
        number.ValueChanged += (_, _) =>
        {
            if (_loading)
            {
                return;
            }

            _pendingNumbers[number] = (write, changes);
            _numberTimer.Stop();
            _numberTimer.Start();
        };
        number.Leave += (_, _) => SettleNumber(number);
        number.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode == Keys.Enter)
            {
                SettleNumber(number);
            }
        };
    }

    private void SettleNumber(NumericUpDown number)
    {
        _ = number.Value;
        FlushPendingChanges();
    }

    /// <summary>Saves any number still waiting out its pause, as the window closes or a check asks.</summary>
    internal void FlushPendingChanges()
    {
        _numberTimer.Stop();
        if (_pendingNumbers.Count == 0)
        {
            return;
        }

        var changes = SettingsChanges.None;
        foreach (var (write, kind) in _pendingNumbers.Values)
        {
            write();
            changes |= kind;
        }

        var saved = _pendingNumbers.Keys.ToArray();
        _pendingNumbers.Clear();
        Commit(changes);
        ShowSavedNumbers(saved);
    }

    private void Write(Action write, SettingsChanges changes)
    {
        if (_loading)
        {
            return;
        }

        write();
        Commit(changes);
    }

    private void Commit(SettingsChanges changes)
    {
        _settings.Save();
        SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(changes));
    }

    /// <summary>
    /// Reads the effective startup state rather than the presence of the registry entry, so the
    /// tick means "Windows will really launch this copy". A registration that Startup Apps has
    /// vetoed, or that points at a copy of UsageAI which is no longer the one running, reads as
    /// off and says why.
    /// </summary>
    private void LoadStartupState()
    {
        _startupState = _readStartup();
        var wasLoading = _loading;
        _loading = true;
        _startWithWindows.Checked = _startupState.WillRun;
        _loading = wasLoading;
        _startupFailed = false;
        _startupHint.Text = _startupState.Status switch
        {
            StartupStatus.Enabled => "UsageAI starts in the tray when you sign in to Windows.",
            StartupStatus.BlockedByWindows =>
                "Windows Startup Apps is blocking UsageAI. Turn this on to allow it again.",
            StartupStatus.PathMismatch =>
                "Startup points at another copy of UsageAI. Turn this on to use this one.",
            _ => "Adds UsageAI to your sign-in items and starts it in the tray.",
        };
        _startupHint.ForeColor = StartupHintColor();
        _startupLoadedChecked = _startWithWindows.Checked;
    }

    private Color StartupHintColor() =>
        _startupFailed || _startupState.Status is StartupStatus.BlockedByWindows or StartupStatus.PathMismatch
            ? Theme.WarningText
            : Theme.Muted;

    /// <summary>
    /// Writes the startup entry as the switch is flipped. It writes only when the switch differs
    /// from the state it last read, so a broken state that reads as off is never deleted by
    /// accident; turning the switch on is still what repairs one, since enabling rewrites the
    /// entry and clears any Startup Apps veto. If Windows refuses, the switch goes back and the
    /// hint under it says so.
    /// </summary>
    private void ApplyStartupPreference()
    {
        var desired = _startWithWindows.Checked;
        if (desired == _startupLoadedChecked)
        {
            return;
        }

        try
        {
            _writeStartup(desired);
            LoadStartupState();
        }
        catch (Exception)
        {
            _loading = true;
            _startWithWindows.Checked = _startupLoadedChecked;
            _loading = false;
            _startupFailed = true;
            _startupHint.Text = "Windows did not accept the change. Try again, or check Startup apps in Windows Settings.";
            _startupHint.ForeColor = StartupHintColor();
        }
    }

    private void DeleteRecordedHistory()
    {
        if (!Confirm(new SettingsPrompt(
                "Delete recorded history",
                "Delete all recorded usage history?",
                "The usage history and saved readings on this computer are deleted. Forecasts start again from new readings. This cannot be undone.",
                "Delete",
                "Cancel",
                PromptKind.Warning,
                DeclineIsDefault: true)))
        {
            return;
        }

        UsageHistoryStore.Clear();
        SnapshotCache.Clear();
    }

    /// <summary>
    /// One picker per provider that reports more than one model group, choosing which group the
    /// popup's rings show. Groups come from the provider's last reading, so a provider that has
    /// not reported yet has no picker until it does.
    /// </summary>
    private void AddPopupGroupPickers(
        IReadOnlyList<(string Id, string DisplayName)> providers,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? popupGroups,
        LayoutScale scale)
    {
        if (popupGroups is null)
        {
            return;
        }

        foreach (var (id, displayName) in providers)
        {
            if (!popupGroups.TryGetValue(id, out var groups) || groups.Count < 2)
            {
                continue;
            }

            if (_popupGroupPickers.Count == 0)
            {
                AddSection("Popup rings");
            }

            var picker = new ThemedComboBox
            {
                AccessibleName = $"{displayName} popup rings",
                BackColor = Theme.SurfaceRaised,
                Dock = DockStyle.Fill,
                ForeColor = Theme.Text,
                Margin = scale.Pad(0, 4, 0, 4),
            };
            picker.Items.Add("Closest to its limit");
            foreach (var group in groups)
            {
                picker.Items.Add(group);
            }

            // Item 0 is automatic; a pinned group the provider no longer reports selects it too.
            var pinned = _settings.PopupMetricGroup(id);
            picker.SelectedIndex = groups
                .Select((group, index) => (group, index))
                .Where(entry => string.Equals(entry.group, pinned, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.index + 1)
                .FirstOrDefault();
            _popupGroupPickers.Add(new PopupGroupPicker(id, picker, groups));
            // Found as "Google Gemini popup rings": the bare name is the provider list's entry,
            // and two results of one name on a page would be shown as one.
            AddRow(displayName, picker, picker.AccessibleName);
        }

        if (_popupGroupPickers.Count > 0)
        {
            AddSpan(CreateHint("Which model group's limits the popup's rings show.", belowRow: true));
        }
    }

    private sealed record PopupGroupPicker(string ProviderId, ComboBox Control, IReadOnlyList<string> Groups);

    /// <summary>
    /// About's two ways to give back: star the repository, which opens its page (the star is
    /// given there), and report a problem, which opens a new issue started with the versions.
    /// No count is fetched and nothing is asked unprompted.
    /// </summary>
    private FlowLayoutPanel CreateSupportLinks(LayoutScale scale)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = scale.Pad(0, 0, 0, 4),
            WrapContents = true,
        };
        var star = CreateLinkButton("★ Star on GitHub", (_, _) => Browser.Open(ProjectLinks.Repository));
        star.AccessibleName = "Star UsageAI on GitHub";
        var report = CreateLinkButton(
            "Report a problem",
            (_, _) => Browser.Open(ProjectLinks.NewIssue(AppIdentity.Version, ProjectLinks.WindowsVersion)));
        report.AccessibleName = "Report a problem on GitHub";
        panel.Controls.Add(star);
        panel.Controls.Add(report);
        return panel;
    }

    private FlowLayoutPanel CreateOrderButtons(LayoutScale scale)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = scale.Pad(0, 0, 0, 6),
        };
        // Spaced on the right, unlike a footer's right-aligned buttons: a left margin indented the
        // row, and in a window squeezed onto a small screen neither button fit beside it.
        var up = CreateDialogButton("Move up", scale, primary: false);
        up.Margin = scale.Pad(0, 0, 8, 0);
        up.Click += (_, _) => MoveSelected(-1);
        var down = CreateDialogButton("Move down", scale, primary: false);
        down.Margin = Padding.Empty;
        down.Click += (_, _) => MoveSelected(1);
        panel.Controls.Add(up);
        panel.Controls.Add(down);
        return panel;
    }

    private void MoveSelected(int offset)
    {
        var index = _providers.SelectedIndex;
        if (index < 0 || index + offset < 0 || index + offset >= _providers.Items.Count || !ConfirmReplaceCardOrder())
        {
            return;
        }

        MoveProvider(index, index + offset);
        CommitProviderOrder();
    }

    /// <summary>The Providers list's order as last saved, to tell whether it changed.</summary>
    private string[] _committedProviderOrder;

    private string[] ProviderIdsInList() =>
        _providers.Items.Cast<ProviderEntry>().Select(entry => entry.Id).ToArray();

    private void CommitProviderOrder()
    {
        var order = ProviderIdsInList();
        if (order.SequenceEqual(_committedProviderOrder, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _committedProviderOrder = order;
        _settings.ProviderOrder = order;
        // An order chosen here replaces one made by dragging cards, which this list cannot show.
        _settings.UnitOrder = Array.Empty<string>();
        Commit(SettingsChanges.Display);
    }

    /// <summary>
    /// Reordering providers or accounts here sorts every card by provider again, which loses an
    /// order made by dragging cards in the popup. With no Cancel to take it back, the first
    /// reorder asks; once the dragged order is gone, there is nothing left to ask about.
    /// </summary>
    private bool ConfirmReplaceCardOrder() =>
        _settings.UnitOrder.Length == 0 ||
        Confirm(new SettingsPrompt(
            "UsageAI settings",
            "Replace the card order you arranged by dragging?",
            "You dragged cards in the popup or dashboard into an order of their own. Reordering here sorts every card by provider again, in the order of these lists.",
            "Replace",
            "Keep my order"));

    private void OnProviderTicked(object? sender, ItemCheckEventArgs eventArgs)
    {
        if (_loading || _movingProvider || eventArgs.NewValue == eventArgs.CurrentValue ||
            _providers.Items[eventArgs.Index] is not ProviderEntry entry)
        {
            return;
        }

        _settings.SetProviderVisible(entry.Id, eventArgs.NewValue == CheckState.Checked);
        Commit(SettingsChanges.Polling | SettingsChanges.Display);
    }

    /// <summary>
    /// The one place the list is reordered, so the buttons, the keyboard shortcut, and a drag all
    /// move an entry the same way: the tick travels with its provider and stays selected.
    /// </summary>
    private void MoveProvider(int from, int to)
    {
        var count = _providers.Items.Count;
        if (from == to || from < 0 || from >= count || to < 0 || to >= count)
        {
            return;
        }

        var item = _providers.Items[from];
        var isChecked = _providers.GetItemChecked(from);
        _providers.BeginUpdate();
        _movingProvider = true;
        try
        {
            _providers.Items.RemoveAt(from);
            _providers.Items.Insert(to, item);
            _providers.SetItemChecked(to, isChecked);
            _providers.SelectedIndex = to;
        }
        finally
        {
            _movingProvider = false;
            _providers.EndUpdate();
        }

        if (_dragIndex >= 0)
        {
            _dragIndex = to;
        }
    }

    /// <summary>
    /// The nearest entry to a point, so a drag that strays past the first or last row still drops
    /// at that end instead of being ignored.
    /// </summary>
    private int ProviderIndexFromPoint(Point point)
    {
        var count = _providers.Items.Count;
        if (count == 0)
        {
            return -1;
        }

        // IndexFromPoint rejects any point outside the client rectangle, horizontally included,
        // and the list holds the mouse for the whole drag. A cursor that strays a few pixels to
        // either side of this narrow list still means the row it is level with, so only a genuine
        // above-or-below miss may fall back to an end.
        var bounds = _providers.ClientRectangle;
        var x = Math.Clamp(point.X, bounds.Left, Math.Max(bounds.Left, bounds.Right - 1));
        var index = _providers.IndexFromPoint(new Point(x, point.Y));
        return index != ListBox.NoMatches ? index : point.Y <= bounds.Top ? 0 : count - 1;
    }

    private void OnProvidersMouseDown(object? sender, MouseEventArgs eventArgs)
    {
        // A drag can lose the mouse without ever seeing a button-up: Alt+Tab, a system dialog, a
        // locked workstation. Clear it here or the next press would resume the stale entry with
        // no threshold to pass and no snapshot behind it.
        EndProviderDrag(cancel: false);
        _dragCandidate = -1;
        if (eventArgs.Button != MouseButtons.Left)
        {
            return;
        }

        var index = _providers.IndexFromPoint(eventArgs.Location);
        if (index == ListBox.NoMatches)
        {
            return;
        }

        // Snapshot before anything moves, so a cancelled drag can put the original order and its
        // ticks back.
        _dragCandidate = index;
        _dragOrigin = eventArgs.Location;
        _dragOrder = _providers.Items.Cast<ProviderEntry>().ToList();
        _dragChecks = _providers.CheckedItems
            .Cast<ProviderEntry>()
            .Select(entry => entry.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reordering rides on the mouse capture the list already takes, not on OLE drag-and-drop.
    /// Registering a drop target needs an STA thread and buys nothing here: the entry never
    /// leaves its own list, and a <see cref="CheckedListBox"/> rejects every owner-draw mode, so
    /// moving the row under the cursor is the clearest insertion feedback available anyway.
    /// </summary>
    private void OnProvidersMouseMove(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != MouseButtons.Left)
        {
            return;
        }

        if (_dragIndex < 0)
        {
            // Wait for the system drag threshold so a click that wobbles still ticks the box.
            var threshold = SystemInformation.DragSize;
            if (_dragCandidate < 0 ||
                (Math.Abs(eventArgs.X - _dragOrigin.X) < threshold.Width &&
                    Math.Abs(eventArgs.Y - _dragOrigin.Y) < threshold.Height))
            {
                return;
            }

            _dragIndex = _dragCandidate;
            _dragCandidate = -1;
            _providers.Cursor = Cursors.SizeNS;
        }

        MoveProvider(_dragIndex, ProviderIndexFromPoint(eventArgs.Location));
    }

    private void OnProvidersMouseUp(object? sender, MouseEventArgs eventArgs)
    {
        _dragCandidate = -1;
        var dropped = _dragIndex >= 0;
        EndProviderDrag(cancel: false);
        if (dropped && IsHandleCreated)
        {
            // After the list has finished with the button-up: a question asked from inside it
            // would open while the list still holds the mouse.
            BeginInvoke(CommitDraggedProviderOrder);
        }
    }

    /// <summary>A drop saves the new order, or puts the old one back if the dragged card order is kept.</summary>
    internal void CommitDraggedProviderOrder()
    {
        if (IsDisposed || ProviderIdsInList().SequenceEqual(_committedProviderOrder, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        if (!ConfirmReplaceCardOrder())
        {
            RestoreProviderOrder();
            return;
        }

        CommitProviderOrder();
    }

    /// <summary>
    /// CheckOnClick ticks the box on button-<em>up</em>, against whichever row is selected by
    /// then, which after a drag is the entry that was just moved. Left alone, every reorder would
    /// quietly show or hide a provider. The move's own tick transfer is let through.
    /// </summary>
    private void OnProvidersItemCheck(object? sender, ItemCheckEventArgs eventArgs)
    {
        if (_dragIndex >= 0 && !_movingProvider)
        {
            eventArgs.NewValue = eventArgs.CurrentValue;
        }
    }

    private void EndProviderDrag(bool cancel)
    {
        if (_dragIndex < 0)
        {
            return;
        }

        _dragIndex = -1;
        _providers.Cursor = Cursors.Default;
        if (cancel)
        {
            RestoreProviderOrder();
        }
    }

    private void RestoreProviderOrder()
    {
        if (_dragOrder.Count != _providers.Items.Count)
        {
            return;
        }

        _loading = true;
        _providers.BeginUpdate();
        try
        {
            _providers.Items.Clear();
            foreach (var entry in _dragOrder)
            {
                _providers.Items.Add(entry, _dragChecks.Contains(entry.Id));
            }
        }
        finally
        {
            _providers.EndUpdate();
            _loading = false;
        }
    }

    /// <summary>
    /// Alt+Up and Alt+Down give the drag gesture a keyboard equal. The buttons alone only offer
    /// one by tabbing out of the list, which is where the selection context is easiest to lose.
    /// Escape abandons a drag before it can close the window. Ctrl+Tab and Ctrl+Page Down move
    /// to the next page, with Shift or Page Up the previous one, from anywhere in the window.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Escape && _dragIndex >= 0)
        {
            EndProviderDrag(cancel: true);
            return true;
        }

        if (_providers.Focused && keyData is (Keys.Alt | Keys.Up) or (Keys.Alt | Keys.Down))
        {
            MoveSelected(keyData == (Keys.Alt | Keys.Up) ? -1 : 1);
            return true;
        }

        if (keyData is (Keys.Control | Keys.F) or (Keys.Control | Keys.E))
        {
            FocusSearch();
            return true;
        }

        if (_pages.Count > 0)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.Tab:
                case Keys.Control | Keys.PageDown:
                    ShowPage((_currentPage + 1) % _pages.Count);
                    return true;
                case Keys.Control | Keys.Shift | Keys.Tab:
                case Keys.Control | Keys.PageUp:
                    ShowPage((_currentPage + _pages.Count - 1) % _pages.Count);
                    return true;
            }
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    /// <summary>
    /// Escape closes the window, as Cancel did; every change is already saved. It is handled as a
    /// dialog key so an open drop-down list closes first, as with a form's CancelButton. In the
    /// search box it steps back through the search instead.
    /// </summary>
    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            if (!EscapeSearch())
            {
                Close();
            }

            return true;
        }

        return base.ProcessDialogKey(keyData);
    }

    /// <summary>Starts a page: a table of cards shown when its name is chosen in the list.</summary>
    private void AddPage(string name)
    {
        CloseSection();
        var layout = new BufferedTableLayoutPanel
        {
            AccessibleName = name,
            AccessibleRole = AccessibleRole.PropertyPage,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Margin = Padding.Empty,
            Padding = LayoutPadding(Scale()),
            Visible = false,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        layout.Paint += PaintSectionCards;
        _scrollHost.Controls.Add(layout);
        _building = new SettingsPage(name, layout);
        _pages.Add(_building);
    }

    private BufferedTableLayoutPanel PageLayout => _building!.Layout;

    /// <summary>
    /// Starts a card on the page being built: a heading above a card that holds its rows. The
    /// card is painted behind the rows by <see cref="PaintSectionCards"/>, so the rows stay
    /// ordinary controls in one table.
    /// </summary>
    private void AddSection(string title)
    {
        var scale = Scale();
        var page = _building!;
        var label = new Label
        {
            AutoSize = true,
            Font = Own(Typography.Display(11F)),
            ForeColor = Theme.Text,
            Margin = scale.Pad(0, page.Sections.Count == 0 ? 4 : 22, 0, 10),
            Tag = SectionHeaderTag,
            Text = title,
        };
        var row = PageLayout.RowCount;
        CloseSection();
        PageLayout.Controls.Add(label, 0, row);
        PageLayout.SetColumnSpan(label, 2);
        PageLayout.RowCount = row + 1;
        page.Sections.Add(new Section(label, row + 1));
        Index(title, label);
    }

    private void CloseSection()
    {
        if (_building is { Sections.Count: > 0 } page && page.Sections[^1].LastRow < 0)
        {
            page.Sections[^1] = page.Sections[^1] with { LastRow = page.Layout.RowCount - 1 };
        }
    }

    private void FinishPages()
    {
        CloseSection();
        _building = null;
    }

    /// <summary>
    /// Shows one page and marks it in the list. The others stay built, hidden, with every value
    /// they hold, so going back to one finds it as it was left.
    /// </summary>
    internal void ShowPage(int index)
    {
        if (index < 0 || index >= _pages.Count)
        {
            return;
        }

        EndFlash();
        using (WindowThemeHelpers.SuspendRedraw(_scrollHost))
        {
            for (var page = 0; page < _pages.Count; page++)
            {
                _pages[page].Layout.Visible = page == index;
            }

            _currentPage = index;
            _nav.SelectedIndex = index;
            _scrollHost.AutoScrollPosition = Point.Empty;
        }

        _scrollFrame.Sync();
    }

    /// <summary>Shows a page by name, for the account banner's Choose… and reopening the window.</summary>
    public void ShowSection(string name)
    {
        var index = Array.FindIndex(PageNames, page => page.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            ShowPage(index);
        }
    }

    /// <summary>
    /// Enter on the page list moves focus to the page's first control, so the list takes a
    /// keyboard user somewhere; the arrows only change the page, leaving the focus in the list.
    /// </summary>
    private void EnterPage(int index)
    {
        if (index < 0 || index >= _pages.Count)
        {
            return;
        }

        ShowPage(index);
        var layout = _pages[index].Layout;
        var target = layout.Controls
            .Cast<Control>()
            .OrderBy(control => layout.GetRow(control))
            .ThenBy(control => layout.GetColumn(control))
            .SelectMany(control => control.CanSelect && control.TabStop ? new[] { control } : control.Controls.Cast<Control>())
            .FirstOrDefault(control => control.CanSelect && control.TabStop);
        target?.Focus();
    }

    /// <summary>
    /// The height that shows the longest page whole at the window's current width, so no page
    /// scrolls in a window opened at its natural size.
    /// </summary>
    internal int TallestPageHeight()
    {
        var width = Math.Max(1, _scrollFrame.Width);
        return _pages.Max(page => page.Layout.GetPreferredSize(new Size(width, 0)).Height);
    }

    /// <summary>The scale the constructor sized the window and its rows for.</summary>
    private readonly float _builtFactor;

    /// <summary>The remembered or default client size, at 96 DPI.</summary>
    private readonly Size _baselineSize;

    private void SizeToBaseline(Rectangle workingArea)
    {
        var scale = Scale();
        ClientSize = new Size(
            Math.Min(scale[_baselineSize.Width], Math.Max(320, workingArea.Width - scale[24])),
            Math.Min(scale[_baselineSize.Height], Math.Max(280, workingArea.Height - scale[24])));
    }

    /// <summary>
    /// Settles the window's size once it has a handle and so a real DPI. Centring follows the
    /// mouse, so the window can be created on a monitor at another scale, and Windows sends no
    /// DPI change for a window created there: the text came out at that monitor's scale while
    /// the window, its columns and its rows kept the start-up one, clipping labels. Sized again
    /// at its own DPI, it then fits its tallest page unless a size was remembered.
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var rescaled = Math.Abs(Scale().Factor - _builtFactor) > 0.001F;
        if (rescaled)
        {
            ApplyScaledLayout();
            SizeToBaseline(Screen.FromControl(this).WorkingArea);
        }

        if (_settings.SettingsPageSize is null)
        {
            FitToTallestPage();
        }

        if ((rescaled || _settings.SettingsPageSize is null) && StartPosition == FormStartPosition.CenterScreen)
        {
            CenterOnOwnScreen();
        }
    }

    /// <summary>Sizes a window with no remembered size to its tallest page, kept on the working area.</summary>
    private void FitToTallestPage()
    {
        var scale = Scale();
        var workingArea = Screen.FromControl(this).WorkingArea;
        var frame = Height - ClientSize.Height;
        var height = Math.Min(TallestPageHeight(), workingArea.Height - frame - scale[24]);
        ClientSize = new Size(ClientSize.Width, Math.Max(height, MinimumSize.Height - frame));
    }

    /// <summary>
    /// Centres the window again after a resize, on the monitor it is on. Form.CenterToScreen
    /// would pick the monitor under the mouse, which can be one at another scale.
    /// </summary>
    private void CenterOnOwnScreen()
    {
        var area = Screen.FromControl(this).WorkingArea;
        Location = new Point(
            area.Left + Math.Max(0, (area.Width - Width) / 2),
            area.Top + Math.Max(0, (area.Height - Height) / 2));
    }

    /// <summary>
    /// Muted explanatory text. A hint under a row's control (<paramref name="belowRow"/>) keeps a
    /// gap from it, or a dropdown's bottom edge sits right on the text's first line.
    /// </summary>
    private Label CreateHint(string text, bool belowRow = false)
    {
        var hint = new Label
        {
            AutoSize = true,
            ForeColor = Theme.Muted,
            Margin = Scale().Pad(0, belowRow ? 6 : 0, 0, 8),
            Tag = HintTag,
            Text = text,
        };
        if (belowRow)
        {
            _rowHints.Add(hint);
        }

        return hint;
    }

    /// <summary>Hints under a row's control, whose gap is scaled again on a DPI change.</summary>
    private readonly List<Label> _rowHints = new();

    private const string HintTag = "hint";

    private SegmentedControl CreateSegmented(IReadOnlyList<string> options, string accessibleName) =>
        new(options)
        {
            AccessibleName = accessibleName,
            DpiOverride = _dpiOverride,
            Dock = DockStyle.Fill,
            Margin = Scale().Pad(0, 4, 0, 4),
            MinimumSize = new Size(0, Scale()[32]),
        };

    private void UpdateThresholdPreview() =>
        _thresholdPreview.SetThresholds((int)_warningPercent.Value, (int)_criticalPercent.Value);

    /// <summary>Paints each card of a page behind its rows.</summary>
    private void PaintSectionCards(object? sender, PaintEventArgs eventArgs)
    {
        if (_pages.FirstOrDefault(candidate => ReferenceEquals(candidate.Layout, sender)) is not { Sections.Count: > 0 } page)
        {
            return;
        }

        var layout = page.Layout;
        eventArgs.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var scale = Scale();
        var heights = layout.GetRowHeights();
        var tops = new int[heights.Length + 1];
        tops[0] = layout.Padding.Top;
        for (var index = 0; index < heights.Length; index++)
        {
            tops[index + 1] = tops[index] + heights[index];
        }

        var inset = scale[CardInset];
        foreach (var section in page.Sections)
        {
            var last = section.LastRow < 0 ? heights.Length - 1 : section.LastRow;
            if (section.FirstRow > last || last >= heights.Length)
            {
                continue;
            }

            var card = Rectangle.FromLTRB(
                layout.Padding.Left - inset,
                tops[section.FirstRow] - scale[4],
                layout.Width - layout.Padding.Right + inset - 1,
                tops[last + 1] + scale[4]);
            DrawingHelpers.FillCard(eventArgs.Graphics, card, Theme.Surface, Theme.Divider, scale.Exact(8));
        }

        PaintFlash(eventArgs.Graphics, layout, tops);
    }

    private const int CardInset = 12;
    private const int QuotaPreviewHeight = 40;
    private const int ThresholdPreviewHeight = 48;

    private static Padding LayoutPadding(LayoutScale scale) => scale.Pad(14 + CardInset, 8, 18 + CardInset, 12);

    private sealed record Section(Label Header, int FirstRow, int LastRow = -1);

    /// <summary>
    /// Adds a caption and its control. The search finds it by the caption, or by
    /// <paramref name="searchText"/> where the caption alone would read as something else.
    /// </summary>
    private void AddRow(string label, Control control, string? searchText = null)
    {
        var scale = Scale();
        // The caption fills its cell and centres vertically, so it lines up with a control of
        // any height beside it.
        var caption = new Label
        {
            AutoEllipsis = true,
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text,
            Margin = scale.Pad(0, 0, 8, 0),
            Text = label,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var row = PageLayout.RowCount;
        PageLayout.Controls.Add(caption, 0, row);
        PageLayout.Controls.Add(control, 1, row);
        PageLayout.RowCount = row + 1;
        Index(searchText ?? label, control);
    }

    /// <summary>
    /// Adds a row across both columns. A switch or button is found by the search under its own
    /// text, and so is each button in a row of them; hints and other text are not.
    /// </summary>
    private void AddSpan(Control control)
    {
        var row = PageLayout.RowCount;
        PageLayout.Controls.Add(control, 0, row);
        PageLayout.SetColumnSpan(control, 2);
        PageLayout.RowCount = row + 1;
        switch (control)
        {
            case CheckBox or Button:
                Index(control.Text, control);
                break;
            case FlowLayoutPanel buttons:
                foreach (var button in buttons.Controls.OfType<Button>())
                {
                    Index(button.Text, button);
                }

                break;
        }
    }

    private ToggleSwitch CreateCheckBox(string text) => new()
    {
        AccessibleName = text,
        Dock = DockStyle.Fill,
        DpiOverride = _dpiOverride,
        Height = Scale()[34],
        Margin = Scale().Pad(0, 2, 0, 2),
        Text = text,
    };

    // A compact, borderless stepper at the right edge, where the switches and choices sit.
    private ThemedNumericUpDown CreateNumeric(int minimum, int maximum) => new()
    {
        Anchor = AnchorStyles.Right,
        BackColor = Theme.SurfaceRaised,
        BorderStyle = BorderStyle.None,
        ForeColor = Theme.Text,
        Margin = Scale().Pad(0, 8, 0, 8),
        Maximum = maximum,
        Minimum = minimum,
        TextAlign = HorizontalAlignment.Center,
        Width = Scale()[96],
    };

    private Button CreateLinkButton(string text, EventHandler onClick, bool destructive = false)
    {
        var button = new Button
        {
            AutoSize = true,
            BackColor = Theme.Surface,
            Cursor = Cursors.Hand,
            FlatStyle = FlatStyle.Flat,
            ForeColor = destructive ? Theme.CriticalText : Theme.Signal,
            Margin = Scale().Pad(0, 4, 0, 4),
            Tag = destructive ? DestructiveTag : null,
            Text = text,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Theme.Blend(Theme.Text, Theme.Surface, 0.06);
        button.FlatAppearance.MouseDownBackColor = Theme.Blend(Theme.Text, Theme.Surface, 0.1);
        button.Click += onClick;
        return button;
    }

    private const string DestructiveTag = "destructive";

    /// <summary>
    /// A bordered button. It grows to its text over a minimum that <see cref="ApplyScaledLayout"/>
    /// sets again at every scale: sized once in pixels, it kept the start-up monitor's size while
    /// its text took the new one, and "Move down" lost its second word at 200%.
    /// </summary>
    private static Button CreateDialogButton(string text, LayoutScale scale, bool primary)
    {
        var button = new Button
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = primary ? Theme.Accent : Theme.SurfaceRaised,
            FlatStyle = FlatStyle.Flat,
            ForeColor = primary ? Theme.OnAccent : Theme.Text,
            Margin = scale.Pad(8, 0, 0, 0),
            MinimumSize = new Size(scale[DialogButtonWidth], scale[DialogButtonHeight]),
            Padding = scale.Pad(8, 0, 8, 0),
            Tag = DialogButtonTag,
            Text = text,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderColor = primary ? Theme.Accent : Theme.Hairline;
        return button;
    }

    private const string DialogButtonTag = "dialog-button";
    private const int DialogButtonWidth = 104;
    private const int DialogButtonHeight = 30;

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

    /// <summary>Tracks fonts this window creates so reopening it cannot leak GDI handles.</summary>
    private Font Own(Font font)
    {
        _ownedFonts.Add(font);
        return font;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowThemeHelpers.ApplyDarkTitleBar(this, Theme.IsDark && !Theme.IsHighContrast);
    }

    private static void OnScrollableControlHandleCreated(object? sender, EventArgs eventArgs)
    {
        if (sender is Control control)
        {
            WindowThemeHelpers.ApplyDarkScrollbar(control, Theme.IsDark && !Theme.IsHighContrast);
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs eventArgs)
    {
        var scrollY = LayoutScale.ScaleBetweenDpis(
            -_scrollHost.AutoScrollPosition.Y,
            eventArgs.DeviceDpiOld,
            eventArgs.DeviceDpiNew);
        base.OnDpiChanged(eventArgs);
        ApplyScaledLayout();
        Bounds = WindowBounds.FitToWorkingArea(Bounds, Screen.FromRectangle(eventArgs.SuggestedRectangle).WorkingArea);
        // Wrapped text was measured while the window still had its old width; measured again at
        // the new one, a one-line hint stops keeping the height of two.
        foreach (var page in _pages)
        {
            page.Layout.PerformLayout();
        }

        _scrollHost.AutoScrollPosition = new Point(0, scrollY);
        _scrollFrame.Sync();
        if (_results.Visible)
        {
            PlaceResults();
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_results is { Visible: true })
        {
            PlaceResults();
        }
    }

    protected override void OnResizeBegin(EventArgs e)
    {
        base.OnResizeBegin(e);
        _sizeAtResizeBegin = Size;
    }

    // A drag that only moved the window leaves its size alone and is not a size choice.
    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        _resizedByUser |= Size != _sizeAtResizeBegin;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // A number typed but not yet settled is still the user's choice.
        foreach (var number in AllPageControls().OfType<NumericUpDown>())
        {
            _ = number.Value;
        }

        FlushPendingChanges();
        base.OnFormClosing(e);
    }

    /// <summary>
    /// Remembers a size the user chose. Stored at 96 DPI. A window that was only fitted to a
    /// smaller screen, or to its tallest page, is not a choice, so it never sets the size.
    /// </summary>
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        if (!_resizedByUser)
        {
            return;
        }

        var dpi = _dpiOverride ?? DeviceDpi;
        int[] size =
        [
            LayoutScale.ScaleBetweenDpis(ClientSize.Width, dpi, LayoutScale.BaselineDpi),
            LayoutScale.ScaleBetweenDpis(ClientSize.Height, dpi, LayoutScale.BaselineDpi),
        ];
        if (_settings.SettingsPageSize is not [var width, var height] || width != size[0] || height != size[1])
        {
            _settings.SettingsPageSize = size;
            _settings.Save();
        }
    }

    private IEnumerable<Control> AllPageControls() =>
        _pages.SelectMany(page => page.Layout.Controls.Cast<Control>());

    private void ApplyScaledLayout()
    {
        var scale = Scale();
        var workingArea = Screen.FromPoint(Location).WorkingArea;
        MinimumSize = new Size(
            Math.Min(scale[560], workingArea.Width),
            Math.Min(scale[420], workingArea.Height));
        _shell.ColumnStyles[0] = new ColumnStyle(SizeType.Absolute, scale[NavWidthBaseline]);
        _navColumn.RowStyles[0] = new RowStyle(SizeType.Absolute, scale[SearchRowHeight]);
        _search.Margin = scale.Pad(8, 12, 4, 4);
        _nav.Margin = scale.Pad(8, 6, 4, 8);
        foreach (var page in _pages)
        {
            page.Layout.Padding = LayoutPadding(scale);
        }

        _providers.Height = scale[104];
        _accountList.Height = scale[160];
        foreach (var button in _pages.SelectMany(page => Descendants(page.Layout)).OfType<Button>().Where(button => Equals(button.Tag, DialogButtonTag)))
        {
            button.MinimumSize = new Size(scale[DialogButtonWidth], scale[DialogButtonHeight]);
            button.Padding = scale.Pad(8, 0, 8, 0);
        }

        _checkForUpdatesButton.MinimumSize = new Size(scale[142], scale[30]);
        var controls = AllPageControls().ToArray();
        foreach (var numeric in controls.OfType<NumericUpDown>())
        {
            numeric.Margin = scale.Pad(0, 8, 0, 8);
            numeric.Width = scale[96];
        }

        foreach (var combo in controls.OfType<ComboBox>())
        {
            combo.Margin = scale.Pad(0, 4, 0, 4);
        }

        foreach (var hint in _rowHints)
        {
            hint.Margin = scale.Pad(0, 6, 0, 8);
        }

        foreach (var toggle in controls.OfType<ToggleSwitch>())
        {
            toggle.Margin = scale.Pad(0, 2, 0, 2);
            toggle.Height = toggle.PreferredRowHeight;
        }

        _quotaPreview.Height = scale[QuotaPreviewHeight];
        _thresholdPreview.Height = scale[ThresholdPreviewHeight];
        foreach (var segmented in controls.OfType<SegmentedControl>())
        {
            segmented.Margin = scale.Pad(0, 4, 0, 4);
            segmented.MinimumSize = new Size(0, scale[32]);
        }
    }

    private void OnThemeChanged(object? sender, EventArgs eventArgs)
    {
        if (!IsDisposed && !Disposing) ApplyThemeColors();
    }

    private void ApplyThemeColors()
    {
        BackColor = Theme.Night;
        ForeColor = Theme.Text;
        ApplyControlTheme(_shell);
        _updateStatus.ForeColor = UpdateToneColor(_updateTone);
        _startupHint.ForeColor = StartupHintColor();
        _versionLabel.ForeColor = Theme.Text;
        _nav.Invalidate();
        _results.Invalidate();
        _quotaPreview.Invalidate();
        _thresholdPreview.Invalidate();
        WindowThemeHelpers.ApplyDarkTitleBar(this, Theme.IsDark && !Theme.IsHighContrast);
        WindowThemeHelpers.ApplyDarkScrollbar(_scrollHost, Theme.IsDark && !Theme.IsHighContrast);
        WindowThemeHelpers.ApplyDarkScrollbar(_providers, Theme.IsDark && !Theme.IsHighContrast);
        WindowThemeHelpers.ApplyDarkScrollbar(_accountList, Theme.IsDark && !Theme.IsHighContrast);
        Invalidate(invalidateChildren: true);
    }

    private void ApplyControlTheme(Control control)
    {
        // Rows sit on the cards; headings and the page list on the window.
        var onCard = control.Parent is BufferedTableLayoutPanel && !Equals(control.Tag, SectionHeaderTag);
        var surface = onCard ? Theme.Surface : Theme.Night;
        switch (control)
        {
            case SettingsSearchBox search:
                search.ApplyColors();
                return;
            case NumericUpDown numeric:
                numeric.BackColor = Theme.SurfaceRaised;
                numeric.ForeColor = Theme.Text;
                numeric.BorderStyle = Theme.IsHighContrast ? BorderStyle.FixedSingle : BorderStyle.None;
                break;
            case CheckedListBox list:
                list.BackColor = Theme.SurfaceRaised;
                list.ForeColor = Theme.Text;
                list.BorderStyle = Theme.IsHighContrast ? BorderStyle.FixedSingle : BorderStyle.None;
                break;
            case ComboBox:
                control.BackColor = Theme.SurfaceRaised;
                control.ForeColor = Theme.Text;
                break;
            case ToggleSwitch or SegmentedControl or SettingPreview:
                control.BackColor = surface;
                control.ForeColor = Theme.Text;
                break;
            case Label label when Equals(label.Tag, SectionHeaderTag):
                label.BackColor = Theme.Night;
                label.ForeColor = Theme.Text;
                break;
            case Label label when ReferenceEquals(label, _startupHint):
                label.BackColor = surface;
                label.ForeColor = StartupHintColor();
                break;
            case Label label:
                label.BackColor = surface;
                label.ForeColor = Equals(label.Tag, HintTag) ? Theme.Muted : Theme.Text;
                break;
            case Button button when button.FlatAppearance.BorderSize == 0:
                button.BackColor = Theme.Surface;
                button.ForeColor = Equals(button.Tag, DestructiveTag) ? Theme.CriticalText : Theme.Signal;
                button.FlatAppearance.MouseOverBackColor = Theme.Blend(Theme.Text, Theme.Surface, 0.06);
                button.FlatAppearance.MouseDownBackColor = Theme.Blend(Theme.Text, Theme.Surface, 0.1);
                break;
            case Button button:
                button.BackColor = Theme.SurfaceRaised;
                button.ForeColor = Theme.Text;
                button.FlatAppearance.BorderColor = Theme.Hairline;
                break;
            case FlowLayoutPanel panel when panel.Parent is BufferedTableLayoutPanel:
                panel.BackColor = Theme.Surface;
                break;
            default:
                control.BackColor = Theme.Night;
                control.ForeColor = Theme.Text;
                break;
        }

        foreach (Control child in control.Controls)
        {
            ApplyControlTheme(child);
        }
    }

    private LayoutScale Scale() => _dpiOverride is { } dpi ? new LayoutScale(dpi) : new LayoutScale(this);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_resourcesDisposed)
        {
            _resourcesDisposed = true;
            Theme.Changed -= OnThemeChanged;
            _numberTimer.Dispose();
            _flashTimer.Dispose();
            _lifetime.Cancel();
            _lifetime.Dispose();
            foreach (var font in _ownedFonts)
            {
                font.Dispose();
            }

            _ownedFonts.Clear();
        }

        base.Dispose(disposing);
    }

    /// <summary>The providers the tray-icon list offers, in the order Settings opened with.</summary>
    private readonly List<(string Id, string DisplayName)> _trayProviders = new();

    /// <summary>The shown providers and accounts the tray-icon list was last filled from.</summary>
    private (string Id, string DisplayName)[] _trayChoices = Array.Empty<(string Id, string DisplayName)>();

    /// <summary>
    /// Fills the tray-icon list: Automatic, All connected, then each provider with its shown
    /// accounts, then any account whose provider is not listed. Filled again when the shown
    /// accounts change while the window is open, so a choice made from the tray menu for an
    /// account added since can be shown.
    /// </summary>
    private void FillTrayChoices(IReadOnlyList<(string Id, string DisplayName)> choices)
    {
        _trayChoices = choices.ToArray();
        _trayProvider.BeginUpdate();
        try
        {
            _trayProvider.Items.Clear();
            _trayProvider.Items.Add(new ProviderEntry(string.Empty, "Automatic"));
            _trayProvider.Items.Add(new ProviderEntry(AppSettings.AllProvidersTrayId, "All connected"));
            foreach (var (id, displayName) in _trayProviders)
            {
                AddTrayChoices(id, displayName, _trayChoices);
            }

            foreach (var (id, displayName) in _trayChoices.Where(choice =>
                !ProviderKey.IsDefault(choice.Id) &&
                !_trayProviders.Any(provider => provider.Id.Equals(ProviderKey.ProviderOf(choice.Id), StringComparison.OrdinalIgnoreCase))))
            {
                _trayProvider.Items.Add(new ProviderEntry(id, displayName));
            }
        }
        finally
        {
            _trayProvider.EndUpdate();
        }
    }

    /// <summary>
    /// Takes the shown providers and accounts as they are now, refilling the tray-icon list only
    /// when they changed, and keeps the saved choice selected.
    /// </summary>
    internal void UpdateTrayChoices(IReadOnlyList<(string Id, string DisplayName)> choices)
    {
        if (choices.SequenceEqual(_trayChoices))
        {
            return;
        }

        _loading = true;
        try
        {
            FillTrayChoices(choices);
            SelectTrayProvider();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Adds a provider's tray choices: its main login, then each other shown account. While the
    /// provider shows more than one account the main login carries its label too ("Claude Code ·
    /// me@example.com"), as in the popup, so it is never mistaken for the provider as a whole.
    /// </summary>
    private void AddTrayChoices(
        string providerId,
        string providerName,
        IReadOnlyList<(string Id, string DisplayName)> choices)
    {
        var main = choices.FirstOrDefault(choice => choice.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        _trayProvider.Items.Add(new ProviderEntry(providerId, main.DisplayName ?? providerName));
        foreach (var (id, displayName) in choices.Where(choice =>
            !ProviderKey.IsDefault(choice.Id) &&
            ProviderKey.ProviderOf(choice.Id).Equals(providerId, StringComparison.OrdinalIgnoreCase)))
        {
            _trayProvider.Items.Add(new ProviderEntry(id, displayName));
        }
    }

    private sealed record ProviderEntry(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>A <see cref="TableLayoutPanel"/> that paints through an off-screen buffer.</summary>
    private sealed class BufferedTableLayoutPanel : TableLayoutPanel
    {
        public BufferedTableLayoutPanel() => DoubleBuffered = true;
    }
}
