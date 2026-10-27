using UsageAI.Services;
using UsageAI.Models;

namespace UsageAI.UI;

/// <summary>
/// Preferences that used to be compile-time constants: refresh cadence, alert thresholds,
/// theme, history, and which providers appear and in what order.
/// </summary>
internal sealed class SettingsForm : Form
{
    // Indexes match ThemeMode, MetricDisplayMode and QuotaDisplay.
    private static readonly string[] ThemeOptions = { "System", "Dark", "Light" };
    private static readonly string[] MetricOptions = { "All", "Metered", "Important" };
    private static readonly string[] QuotaOptions = { "Used", "Remaining" };
    private static readonly string[] SectionNames = { "General", "Alerts", "Appearance", "Providers", "History", "About" };
    private const int NavWidthBaseline = 176;
    private const string SectionHeaderTag = "section-header";

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
    private readonly TableLayoutPanel _layout;
    private readonly NumericUpDown _refreshInterval;
    private readonly CheckBox _slowWhenHidden;
    private readonly CheckBox _notificationsEnabled;
    private readonly NumericUpDown _firstAlert;
    private readonly NumericUpDown _secondAlert;
    private readonly CheckBox _notifyOnReset;
    private readonly NumericUpDown _warningPercent;
    private readonly NumericUpDown _criticalPercent;
    private readonly SegmentedControl _theme;
    private readonly SegmentedControl _metricDisplayMode;
    private readonly SegmentedControl _quotaDisplay;
    private readonly SectionNav _nav;
    private readonly List<Section> _sections = new();
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
    private readonly FlowLayoutPanel _buttons;
    private readonly Button _saveButton;
    private readonly Button _cancelButton;
    private readonly Func<StartupState> _readStartup;
    private readonly Action<bool> _writeStartup;
    private StartupState _startupState;
    private bool _startupLoadedChecked;
    private bool _isCheckingForUpdates;
    private bool _resourcesDisposed;
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
        IReadOnlyDictionary<string, IReadOnlyList<string>>? popupGroups = null)
    {
        _settings = settings;
        _dpiOverride = dpiOverride;
        _checkForUpdates = checkForUpdates;
        _promptForUpdate = promptForUpdate;
        _readStartup = readStartup ?? (static () => StartupManager.Current);
        _writeStartup = writeStartup ?? StartupManager.SetEnabled;
        var scale = Scale();

        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Night;
        ForeColor = Theme.Text;
        // Resizable, since the page scrolls and a taller window shows more of it; not
        // maximizable, because rows stretched across a whole screen are hard to read.
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        SizeGripStyle = SizeGripStyle.Hide;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        Text = "UsageAI settings";
        AppIcon.Apply(this);
        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, scale[1920], scale[1080]);
        var size = settings.SettingsWindowSize is [var width, var height] ? (width, height) : (700, 640);
        ClientSize = new Size(
            Math.Min(scale[size.Item1], Math.Max(320, workingArea.Width - scale[24])),
            Math.Min(scale[size.Item2], Math.Max(280, workingArea.Height - scale[24])));
        Font = Own(Typography.Text(9F));

        _shell = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            RowCount = 2,
        };
        _shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, scale[NavWidthBaseline]));
        _shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _shell.RowStyles.Add(new RowStyle(SizeType.Absolute, scale[52]));
        Controls.Add(_shell);

        _scrollHost = new Panel
        {
            AutoScroll = true,
            Margin = Padding.Empty,
        };
        _scrollHost.HandleCreated += OnScrollableControlHandleCreated;
        _scrollHost.Scroll += (_, _) => SyncNavToScroll();
        _scrollHost.MouseWheel += (_, _) => SyncNavToScroll();
        // The page scrolls behind a slim scrollbar rather than the system's full-width one.
        _scrollFrame = new SlimScrollHost(_scrollHost, _dpiOverride)
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        _shell.Controls.Add(_scrollFrame, 1, 0);

        // Sections are one long page, so every setting stays findable by scrolling; the list on
        // the left jumps to a section and marks the one being read.
        _nav = new SectionNav(SectionNames)
        {
            Dock = DockStyle.Fill,
            DpiOverride = _dpiOverride,
            Margin = scale.Pad(8, 12, 4, 8),
        };
        _nav.SectionChosen += (_, _) => ScrollToSection(_nav.SelectedIndex);
        _nav.SectionEntered += (_, _) => EnterSection(_nav.SelectedIndex);
        _shell.Controls.Add(_nav, 0, 0);
        _nav.TabIndex = 0;
        _scrollFrame.TabIndex = 1;

        _layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Margin = Padding.Empty,
            Padding = LayoutPadding(scale),
        };
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        _layout.Paint += PaintSectionCards;
        _scrollHost.Controls.Add(_layout);

        _refreshInterval = CreateNumeric(AppSettings.MinimumRefreshMinutes, AppSettings.MaximumRefreshMinutes);
        _slowWhenHidden = CreateCheckBox("Slow down while no window is open");
        _notificationsEnabled = CreateCheckBox("Show tray notifications");
        _firstAlert = CreateNumeric(1, 100);
        _secondAlert = CreateNumeric(1, 100);
        _notifyOnReset = CreateCheckBox("Announce window resets");
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
        _trayProvider.Items.Add(new ProviderEntry(string.Empty, "Automatic"));

        _providers = new CheckedListBox
        {
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
            var entry = new ProviderEntry(provider.Id, provider.DisplayName);
            _providers.Items.Add(
                entry,
                _settings.IsProviderVisible(provider.Id));
            _trayProvider.Items.Add(entry);
        }

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

        AddSection("General");
        AddRow("Refresh every (minutes)", _refreshInterval);
        AddSpan(_slowWhenHidden);
        AddSpan(_startWithWindows);
        AddSpan(_startupHint);
        AddSpan(_hotkeyEnabled);

        AddSection("Alerts");
        AddSpan(_notificationsEnabled);
        AddRow("First alert at (% used)", _firstAlert);
        AddRow("Second alert at (% used)", _secondAlert);
        AddSpan(_notifyOnReset);

        AddSection("Appearance");
        AddRow("Theme", _theme);
        AddRow("Dashboard rows", _metricDisplayMode);
        AddSpan(CreateHint("Metered shows every percentage limit. Important shows the highest session, rolling and monthly limit."));
        AddRow("Show quota as", _quotaDisplay);
        AddSpan(_quotaPreview);
        AddSpan(CreateHint("Remaining shows what is left on meters, headlines, the tray icon and alerts. Colours and alert thresholds still follow usage."));
        AddRow("Warning colour from (% used)", _warningPercent);
        AddRow("Critical colour from (% used)", _criticalPercent);
        AddSpan(_thresholdPreview);

        AddSection("Providers");
        AddRow("Tray icon provider", _trayProvider);
        AddSpan(CreateHint("Automatic follows the connected provider with the highest active primary usage."));
        AddSpan(CreateHint("Tick to show. Drag a provider to reorder it, or use Alt+Up / Alt+Down."));
        AddSpan(_providers);
        AddSpan(CreateOrderButtons(scale));
        AddPopupGroupPickers(providers, popupGroups, scale);

        AddSection("History");
        AddSpan(_historyEnabled);
        AddSpan(_forecastEnabled);
        AddSpan(CreateLinkButton("Delete recorded history", (_, _) =>
        {
            UsageHistoryStore.Clear();
            SnapshotCache.Clear();
        }, destructive: true));

        AddSection("About");
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
        FinishSections();

        _buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = Padding.Empty,
            Padding = scale.Pad(0, 10, 18, 10),
        };
        _buttons.Paint += PaintFooterRule;
        _saveButton = CreateDialogButton("Save", scale, primary: true);
        _saveButton.Click += (_, _) => Apply();
        _cancelButton = CreateDialogButton("Cancel", scale, primary: false);
        _cancelButton.DialogResult = DialogResult.Cancel;
        _buttons.Controls.Add(_saveButton);
        _buttons.Controls.Add(_cancelButton);
        _shell.Controls.Add(_buttons, 0, 1);
        _buttons.TabIndex = 2;
        _shell.SetColumnSpan(_buttons, 2);
        AcceptButton = _saveButton;
        CancelButton = _cancelButton;

        LoadValues();
        Theme.Changed += OnThemeChanged;
        ApplyScaledLayout();
        ApplyThemeColors();
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
        _refreshInterval.Value = Math.Clamp(
            _settings.RefreshIntervalMinutes,
            AppSettings.MinimumRefreshMinutes,
            AppSettings.MaximumRefreshMinutes);
        _slowWhenHidden.Checked = _settings.SlowRefreshWhenHidden;
        _notificationsEnabled.Checked = _settings.NotificationsEnabled;
        _firstAlert.Value = _settings.AlertThreshold(0, 80);
        _secondAlert.Value = _settings.AlertThreshold(1, 95);
        _notifyOnReset.Checked = _settings.NotifyOnReset;
        _warningPercent.Value = _settings.WarningPercent;
        _criticalPercent.Value = _settings.CriticalPercent;
        _theme.SelectedIndex = (int)_settings.Theme;
        _metricDisplayMode.SelectedIndex = (int)_settings.MetricDisplayMode;
        _quotaDisplay.SelectedIndex = (int)_settings.QuotaDisplay;
        _quotaPreview.ShowRemaining = _settings.QuotaDisplay == QuotaDisplay.Remaining;
        UpdateThresholdPreview();
        _historyEnabled.Checked = _settings.HistoryEnabled;
        _forecastEnabled.Checked = _settings.ForecastEnabled;
        _hotkeyEnabled.Checked = _settings.GlobalHotkeyEnabled;
        LoadStartupState();
        _trayProvider.SelectedIndex = 0;
        if (_settings.TrayProviderId is { } trayProviderId)
        {
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
            AddRow($"{displayName} popup rings", picker);
        }

        if (_popupGroupPickers.Count > 0)
        {
            AddSpan(CreateHint("Which model group's limits the popup's rings show."));
        }
    }

    private sealed record PopupGroupPicker(string ProviderId, ComboBox Control, IReadOnlyList<string> Groups);

    private void Apply()
    {
        _settings.RefreshIntervalMinutes = (int)_refreshInterval.Value;
        _settings.SlowRefreshWhenHidden = _slowWhenHidden.Checked;
        _settings.NotificationsEnabled = _notificationsEnabled.Checked;
        _settings.NotifyAtPercent = new[] { (int)_firstAlert.Value, (int)_secondAlert.Value };
        _settings.NotifyOnReset = _notifyOnReset.Checked;
        _settings.WarningPercent = (int)_warningPercent.Value;
        _settings.CriticalPercent = (int)_criticalPercent.Value;
        _settings.Theme = _theme.SelectedIndex switch
        {
            1 => ThemeMode.Dark,
            2 => ThemeMode.Light,
            _ => ThemeMode.System,
        };
        _settings.MetricDisplayMode = _metricDisplayMode.SelectedIndex switch
        {
            1 => MetricDisplayMode.MeteredOnly,
            2 => MetricDisplayMode.ImportantOnly,
            _ => MetricDisplayMode.All,
        };
        _settings.QuotaDisplay = _quotaDisplay.SelectedIndex == 1
            ? QuotaDisplay.Remaining
            : QuotaDisplay.Used;
        _settings.HistoryEnabled = _historyEnabled.Checked;
        _settings.ForecastEnabled = _forecastEnabled.Checked;
        _settings.GlobalHotkeyEnabled = _hotkeyEnabled.Checked;
        _settings.TrayProviderId = _trayProvider.SelectedItem is ProviderEntry { Id.Length: > 0 } trayProvider
            ? trayProvider.Id
            : null;

        var order = new List<string>();
        for (var index = 0; index < _providers.Items.Count; index++)
        {
            var entry = (ProviderEntry)_providers.Items[index]!;
            order.Add(entry.Id);
            _settings.SetProviderVisible(entry.Id, _providers.GetItemChecked(index));
        }

        _settings.ProviderOrder = order.ToArray();
        foreach (var picker in _popupGroupPickers)
        {
            _settings.SetPopupMetricGroup(
                picker.ProviderId,
                picker.Control.SelectedIndex > 0 ? picker.Groups[picker.Control.SelectedIndex - 1] : null);
        }

        _settings.Save();
        if (!TryApplyStartupPreference())
        {
            // Every other preference is already saved, so the dialog still closes; only the one
            // thing that failed is reported, and reopening Settings shows the real startup state.
            PromptDialog.Ask(
                "UsageAI settings",
                "Start with Windows was not changed",
                "Windows did not accept the change. Your other settings are saved; reopen Settings to see whether UsageAI starts with Windows.",
                "OK",
                declineText: null,
                PromptKind.Warning);
        }

        DialogResult = DialogResult.OK;
        Close();
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
        _startWithWindows.Checked = _startupState.WillRun;
        _startupHint.Text = _startupState.Status switch
        {
            StartupStatus.Enabled => "UsageAI starts in the tray when you sign in to Windows.",
            StartupStatus.BlockedByWindows =>
                "Windows Startup Apps is blocking UsageAI. Tick this and save to turn it back on.",
            StartupStatus.PathMismatch =>
                "Startup points at another copy of UsageAI. Tick this and save to use this one.",
            _ => "Adds UsageAI to your sign-in items and starts it in the tray.",
        };
        _startupHint.ForeColor = StartupHintColor();
        _startupLoadedChecked = _startWithWindows.Checked;
    }

    private Color StartupHintColor() =>
        _startupState.Status is StartupStatus.BlockedByWindows or StartupStatus.PathMismatch
            ? Theme.WarningText
            : Theme.Muted;

    /// <summary>
    /// Writes only when the tick differs from the one the dialog opened with, so saving is never
    /// how a startup entry disappears. The broken states read as off, which would otherwise make
    /// an untouched Save delete an entry the person never asked about — a second copy of UsageAI
    /// run once from a download would unregister the installed one. Ticking the box is still what
    /// repairs those states, because enabling rewrites the entry and clears any Startup Apps veto.
    /// </summary>
    private bool TryApplyStartupPreference()
    {
        var desired = _startWithWindows.Checked;
        if (desired == _startupLoadedChecked)
        {
            return true;
        }

        try
        {
            _writeStartup(desired);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private FlowLayoutPanel CreateOrderButtons(LayoutScale scale)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = scale.Pad(0, 0, 0, 6),
        };
        var up = CreateDialogButton("Move up", scale, primary: false);
        up.Click += (_, _) => MoveSelected(-1);
        var down = CreateDialogButton("Move down", scale, primary: false);
        down.Click += (_, _) => MoveSelected(1);
        panel.Controls.Add(up);
        panel.Controls.Add(down);
        return panel;
    }

    private void MoveSelected(int offset)
    {
        var index = _providers.SelectedIndex;
        if (index >= 0)
        {
            MoveProvider(index, index + offset);
        }
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
        EndProviderDrag(cancel: false);
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
        }
    }

    /// <summary>
    /// Alt+Up and Alt+Down give the drag gesture a keyboard equal. The buttons alone only offer
    /// one by tabbing out of the list, which is where the selection context is easiest to lose.
    /// Escape has to be claimed here rather than in a KeyDown handler: this form sets
    /// <see cref="Form.CancelButton"/>, so a list box never sees it and it would close the dialog
    /// and discard every edit in it instead of abandoning the drag.
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

        return base.ProcessCmdKey(ref message, keyData);
    }

    /// <summary>
    /// Starts a section: a heading above a card that holds the section's rows. The card is
    /// painted behind the rows by <see cref="PaintSectionCards"/>, so the rows stay ordinary
    /// controls in one table that scrolls as a page.
    /// </summary>
    private void AddSection(string title)
    {
        var scale = Scale();
        var label = new Label
        {
            AutoSize = true,
            Font = Own(Typography.Display(11F)),
            ForeColor = Theme.Text,
            Margin = scale.Pad(0, _sections.Count == 0 ? 4 : 22, 0, 10),
            Tag = SectionHeaderTag,
            Text = title,
        };
        var row = _layout.RowCount;
        CloseSection(row - 1);
        _layout.Controls.Add(label, 0, row);
        _layout.SetColumnSpan(label, 2);
        _layout.RowCount = row + 1;
        _sections.Add(new Section(label, row + 1));
    }

    private void CloseSection(int lastRow)
    {
        if (_sections.Count > 0 && _sections[^1].LastRow < 0)
        {
            _sections[^1] = _sections[^1] with { LastRow = lastRow };
        }
    }

    private void FinishSections()
    {
        CloseSection(_layout.RowCount - 1);

        // Tabbing or clicking scrolls the page to the focused control without a Scroll event, so
        // the section list re-reads the position once that scroll has happened.
        foreach (Control control in _layout.Controls)
        {
            control.Enter += (_, _) =>
            {
                if (IsHandleCreated)
                {
                    BeginInvoke(SyncNavToScroll);
                }
            };
        }
    }

    private Label CreateHint(string text) => new()
    {
        AutoSize = true,
        ForeColor = Theme.Muted,
        Margin = Scale().Pad(0, 0, 0, 8),
        Tag = HintTag,
        Text = text,
    };

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

    /// <summary>Paints each section's card behind its rows.</summary>
    private void PaintSectionCards(object? sender, PaintEventArgs eventArgs)
    {
        if (_sections.Count == 0)
        {
            return;
        }

        eventArgs.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var scale = Scale();
        var heights = _layout.GetRowHeights();
        var tops = new int[heights.Length + 1];
        tops[0] = _layout.Padding.Top;
        for (var index = 0; index < heights.Length; index++)
        {
            tops[index + 1] = tops[index] + heights[index];
        }

        var inset = scale[CardInset];
        foreach (var section in _sections)
        {
            var last = section.LastRow < 0 ? heights.Length - 1 : section.LastRow;
            if (section.FirstRow > last || last >= heights.Length)
            {
                continue;
            }

            var card = Rectangle.FromLTRB(
                _layout.Padding.Left - inset,
                tops[section.FirstRow] - scale[4],
                _layout.Width - _layout.Padding.Right + inset - 1,
                tops[last + 1] + scale[4]);
            DrawingHelpers.FillCard(eventArgs.Graphics, card, Theme.Surface, Theme.Divider, scale.Exact(8));
        }
    }

    private void PaintFooterRule(object? sender, PaintEventArgs eventArgs)
    {
        using var rule = new Pen(Theme.Divider);
        eventArgs.Graphics.DrawLine(rule, 0, 0, _buttons.Width, 0);
    }

    /// <summary>Scrolls the page so a section's heading sits at the top.</summary>
    private void ScrollToSection(int index)
    {
        if (index < 0 || index >= _sections.Count)
        {
            return;
        }

        _scrollHost.AutoScrollPosition = new Point(0, Math.Max(0, _sections[index].Header.Top - Scale()[8]));
    }

    /// <summary>
    /// Enter on the section list moves focus to the section's first control, so the list takes a
    /// keyboard user somewhere; the arrows only scroll, leaving the focus in the list.
    /// </summary>
    private void EnterSection(int index)
    {
        if (index < 0 || index >= _sections.Count)
        {
            return;
        }

        var section = _sections[index];
        var last = section.LastRow < 0 ? _layout.RowCount - 1 : section.LastRow;
        var target = _layout.Controls
            .Cast<Control>()
            .Where(control => _layout.GetRow(control) >= section.FirstRow && _layout.GetRow(control) <= last)
            .OrderBy(control => _layout.GetRow(control))
            .ThenBy(control => _layout.GetColumn(control))
            .SelectMany(control => control.CanSelect && control.TabStop ? new[] { control } : control.Controls.Cast<Control>())
            .FirstOrDefault(control => control.CanSelect && control.TabStop);
        ScrollToSection(index);
        target?.Focus();
    }

    /// <summary>Marks the section whose heading was last scrolled past, as the reader scrolls.</summary>
    private void SyncNavToScroll()
    {
        if (_sections.Count == 0)
        {
            return;
        }

        var top = -_scrollHost.AutoScrollPosition.Y + Scale()[24];
        var current = 0;
        for (var index = 0; index < _sections.Count; index++)
        {
            if (_sections[index].Header.Top <= top)
            {
                current = index;
            }
        }

        // At the bottom of the page the last sections can never reach the top; the last one wins.
        var bottom = _scrollHost.DisplayRectangle.Height - _scrollHost.ClientSize.Height;
        if (bottom > 0 && -_scrollHost.AutoScrollPosition.Y >= bottom - 1)
        {
            current = _sections.Count - 1;
        }

        _nav.SelectedIndex = current;
    }

    private const int CardInset = 12;
    private const int QuotaPreviewHeight = 40;
    private const int ThresholdPreviewHeight = 48;

    private static Padding LayoutPadding(LayoutScale scale) => scale.Pad(14 + CardInset, 8, 18 + CardInset, 12);

    private sealed record Section(Label Header, int FirstRow, int LastRow = -1);

    private void AddRow(string label, Control control)
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
        var row = _layout.RowCount;
        _layout.Controls.Add(caption, 0, row);
        _layout.Controls.Add(control, 1, row);
        _layout.RowCount = row + 1;
    }

    private void AddSpan(Control control)
    {
        var row = _layout.RowCount;
        _layout.Controls.Add(control, 0, row);
        _layout.SetColumnSpan(control, 2);
        _layout.RowCount = row + 1;
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

    private static Button CreateDialogButton(string text, LayoutScale scale, bool primary)
    {
        var button = new Button
        {
            BackColor = primary ? Theme.Accent : Theme.SurfaceRaised,
            FlatStyle = FlatStyle.Flat,
            ForeColor = primary ? Theme.OnAccent : Theme.Text,
            Margin = scale.Pad(8, 0, 0, 0),
            Size = new Size(scale[104], scale[30]),
            Text = text,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderColor = primary ? Theme.Accent : Theme.Hairline;
        return button;
    }

    /// <summary>Tracks fonts this dialog creates so reopening it cannot leak GDI handles.</summary>
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
        _scrollHost.AutoScrollPosition = new Point(0, scrollY);
        SyncNavToScroll();
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

    /// <summary>
    /// Remembers a size the user chose, however the window closed: it is how they want the
    /// window, not one of the settings Save or Cancel decides on. Stored at 96 DPI. A window
    /// that was only fitted to a smaller screen is not a choice, so it never shrinks the size.
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
        if (_settings.SettingsWindowSize is not [var width, var height] || width != size[0] || height != size[1])
        {
            _settings.SettingsWindowSize = size;
            _settings.Save();
        }
    }

    private void ApplyScaledLayout()
    {
        var scale = Scale();
        var workingArea = Screen.FromPoint(Location).WorkingArea;
        MinimumSize = new Size(
            Math.Min(scale[560], workingArea.Width),
            Math.Min(scale[420], workingArea.Height));
        _shell.RowStyles[1] = new RowStyle(SizeType.Absolute, scale[52]);
        _shell.ColumnStyles[0] = new ColumnStyle(SizeType.Absolute, scale[NavWidthBaseline]);
        _nav.Margin = scale.Pad(8, 12, 4, 8);
        _layout.Padding = LayoutPadding(scale);
        _providers.Height = scale[104];
        _checkForUpdatesButton.MinimumSize = new Size(scale[142], scale[30]);
        _buttons.Padding = scale.Pad(0, 10, 18, 10);
        _saveButton.Size = new Size(scale[104], scale[30]);
        _cancelButton.Size = new Size(scale[104], scale[30]);
        foreach (var numeric in _layout.Controls.OfType<NumericUpDown>())
        {
            numeric.Margin = scale.Pad(0, 8, 0, 8);
            numeric.Width = scale[96];
        }
        foreach (var combo in _layout.Controls.OfType<ComboBox>()) combo.Margin = scale.Pad(0, 4, 0, 4);
        foreach (var toggle in _layout.Controls.OfType<ToggleSwitch>())
        {
            toggle.Margin = scale.Pad(0, 2, 0, 2);
            toggle.Height = toggle.PreferredRowHeight;
        }

        _quotaPreview.Height = scale[QuotaPreviewHeight];
        _thresholdPreview.Height = scale[ThresholdPreviewHeight];
        foreach (var segmented in _layout.Controls.OfType<SegmentedControl>())
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
        _saveButton.BackColor = Theme.Accent;
        _saveButton.ForeColor = Theme.OnAccent;
        _saveButton.FlatAppearance.BorderColor = Theme.Accent;
        _cancelButton.BackColor = Theme.SurfaceRaised;
        _cancelButton.ForeColor = Theme.Text;
        _cancelButton.FlatAppearance.BorderColor = Theme.Hairline;
        _versionLabel.ForeColor = Theme.Text;
        _nav.Invalidate();
        _quotaPreview.Invalidate();
        _thresholdPreview.Invalidate();
        WindowThemeHelpers.ApplyDarkTitleBar(this, Theme.IsDark && !Theme.IsHighContrast);
        WindowThemeHelpers.ApplyDarkScrollbar(_scrollHost, Theme.IsDark && !Theme.IsHighContrast);
        WindowThemeHelpers.ApplyDarkScrollbar(_providers, Theme.IsDark && !Theme.IsHighContrast);
        Invalidate(invalidateChildren: true);
    }

    private void ApplyControlTheme(Control control)
    {
        // Rows sit on the section cards; headings, the section list and the footer on the window.
        var onCard = control.Parent == _layout && !Equals(control.Tag, SectionHeaderTag);
        var surface = onCard ? Theme.Surface : Theme.Night;
        switch (control)
        {
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
            case Button button when button.Parent == _buttons:
                break;
            case Button button:
                button.BackColor = Theme.SurfaceRaised;
                button.ForeColor = Theme.Text;
                button.FlatAppearance.BorderColor = Theme.Hairline;
                break;
            case FlowLayoutPanel panel when panel != _buttons && panel.Parent == _layout:
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

    private sealed record ProviderEntry(string Id, string Name)
    {
        public override string ToString() => Name;
    }
}
