using UsageAI.Services;
using UsageAI.Models;

namespace UsageAI.UI;

/// <summary>
/// Preferences that used to be compile-time constants: refresh cadence, alert thresholds,
/// theme, history, and which providers appear and in what order.
/// </summary>
internal sealed class SettingsForm : Form
{
    private static readonly object[] ThemeChoices = { "Follow Windows", "Dark", "Light" };
    private static readonly object[] MetricChoices = { "All", "Metered only", "Important only" };

    private readonly List<Font> _ownedFonts = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AppSettings _settings;
    private readonly int? _dpiOverride;
    private readonly Func<CancellationToken, Task<UpdateCheckResult>> _checkForUpdates;
    private readonly Func<UpdateRelease, Task> _promptForUpdate;
    private readonly TableLayoutPanel _shell;
    private readonly Panel _scrollHost;
    private readonly TableLayoutPanel _layout;
    private readonly NumericUpDown _refreshInterval;
    private readonly CheckBox _slowWhenHidden;
    private readonly CheckBox _notificationsEnabled;
    private readonly NumericUpDown _firstAlert;
    private readonly NumericUpDown _secondAlert;
    private readonly CheckBox _notifyOnReset;
    private readonly NumericUpDown _warningPercent;
    private readonly NumericUpDown _criticalPercent;
    private readonly ComboBox _theme;
    private readonly ComboBox _metricDisplayMode;
    private readonly ComboBox _trayProvider;
    private readonly CheckBox _historyEnabled;
    private readonly CheckBox _forecastEnabled;
    private readonly CheckBox _hotkeyEnabled;
    private readonly CheckBox _startWithWindows;
    private readonly Label _startupHint;
    private readonly CheckedListBox _providers;
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
        Action<bool>? writeStartup = null)
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
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        Text = "UsageAI settings";
        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, scale[1920], scale[1080]);
        ClientSize = new Size(
            Math.Min(scale[470], Math.Max(320, workingArea.Width - scale[24])),
            Math.Min(scale[640], Math.Max(280, workingArea.Height - scale[24])));
        Font = Own(Typography.Text(9F));

        _shell = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            RowCount = 2,
        };
        _shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _shell.RowStyles.Add(new RowStyle(SizeType.Absolute, scale[52]));
        Controls.Add(_shell);

        _scrollHost = new Panel
        {
            AutoScroll = true,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        _scrollHost.HandleCreated += OnScrollableControlHandleCreated;
        _shell.Controls.Add(_scrollHost, 0, 0);

        _layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Margin = Padding.Empty,
            Padding = scale.Pad(18, 16, 18, 8),
        };
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
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
        _forecastEnabled = CreateCheckBox("Show trend and burn-rate forecast");
        _hotkeyEnabled = CreateCheckBox("Global hotkey (Win+Alt+U)");
        _startWithWindows = CreateCheckBox("Start with Windows");
        _startupHint = new Label
        {
            AutoSize = true,
            ForeColor = Theme.Muted,
            Margin = scale.Pad(0, 0, 0, 8),
        };
        _theme = new ComboBox
        {
            BackColor = Theme.SurfaceRaised,
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.Text,
            Margin = scale.Pad(0, 4, 0, 4),
        };
        _theme.Items.AddRange(ThemeChoices);

        _metricDisplayMode = new ComboBox
        {
            AccessibleName = "Dashboard metric display mode",
            BackColor = Theme.SurfaceRaised,
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.Text,
            Margin = scale.Pad(0, 4, 0, 4),
        };
        _metricDisplayMode.Items.AddRange(MetricChoices);

        _trayProvider = new ComboBox
        {
            BackColor = Theme.SurfaceRaised,
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.Text,
            Margin = scale.Pad(0, 4, 0, 4),
        };
        _trayProvider.Items.Add(new ProviderEntry(string.Empty, "Automatic"));

        _providers = new CheckedListBox
        {
            BackColor = Theme.SurfaceRaised,
            BorderStyle = BorderStyle.FixedSingle,
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

        AddSection("Refresh");
        AddRow("Refresh every (minutes)", _refreshInterval);
        AddSpan(_slowWhenHidden);

        AddSection("Alerts");
        AddSpan(_notificationsEnabled);
        AddRow("First alert at (% used)", _firstAlert);
        AddRow("Second alert at (% used)", _secondAlert);
        AddSpan(_notifyOnReset);

        AddSection("Appearance");
        AddRow("Theme", _theme);
        AddRow("Dashboard metrics", _metricDisplayMode);
        AddSpan(new Label
        {
            AutoSize = true,
            ForeColor = Theme.Muted,
            Margin = scale.Pad(0, 0, 0, 8),
            Text = "Metered shows every percentage limit. Important shows the highest Session, Rolling, and Monthly limit.",
        });
        AddRow("Warning colour from (% used)", _warningPercent);
        AddRow("Critical colour from (% used)", _criticalPercent);

        AddSection("History");
        AddSpan(_historyEnabled);
        AddSpan(_forecastEnabled);
        AddSpan(CreateLinkButton("Delete recorded history", (_, _) =>
        {
            UsageHistoryStore.Clear();
            SnapshotCache.Clear();
        }));

        AddSection("Providers");
        AddRow("Tray icon provider", _trayProvider);
        AddSpan(new Label
        {
            AutoSize = true,
            ForeColor = Theme.Muted,
            Margin = scale.Pad(0, 0, 0, 8),
            Text = "Automatic follows the connected provider with the highest active primary usage.",
        });
        AddSpan(new Label
        {
            AutoSize = true,
            ForeColor = Theme.Muted,
            Margin = scale.Pad(0, 0, 0, 4),
            Text = "Tick to show. Drag a provider to reorder it, or use Alt+Up / Alt+Down.",
        });
        AddSpan(_providers);
        AddSpan(CreateOrderButtons(scale));

        AddSection("System");
        AddSpan(_startWithWindows);
        AddSpan(_startupHint);
        AddSpan(_hotkeyEnabled);

        AddSection("About");
        _versionLabel = new Label
        {
            AutoSize = true,
            Font = Own(Typography.Mono(9F)),
            ForeColor = Theme.Signal,
            Margin = scale.Pad(0, 8, 0, 4),
            Text = $"v{AppIdentity.Version}",
        };
        AddRow("Installed version", _versionLabel);
        AddSpan(_checkForUpdatesButton);
        AddSpan(CreateLinkButton("What's new", (_, _) => WhatsNewForm.ShowCurrent(this)));
        AddSpan(_updateStatus);

        _buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = Padding.Empty,
            Padding = scale.Pad(0, 10, 18, 10),
        };
        _saveButton = CreateDialogButton("Save", scale, primary: true);
        _saveButton.Click += (_, _) => Apply();
        _cancelButton = CreateDialogButton("Cancel", scale, primary: false);
        _cancelButton.DialogResult = DialogResult.Cancel;
        _buttons.Controls.Add(_saveButton);
        _buttons.Controls.Add(_cancelButton);
        _shell.Controls.Add(_buttons, 0, 1);
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
        _updateStatus.ForeColor = Theme.Muted;
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
                _updateStatus.ForeColor = Theme.Warning;
                _updateStatus.Text = "Couldn’t check for updates. Check your connection and try again.";
                return;
            }

            if (result.Release is null)
            {
                _updateStatus.ForeColor = Theme.Success;
                _updateStatus.Text = $"You’re up to date — v{AppIdentity.Version} is the latest version.";
                return;
            }

            _updateStatus.ForeColor = Theme.Signal;
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
        _settings.Save();
        if (!TryApplyStartupPreference())
        {
            // Every other preference is already saved, so the dialog still closes; only the one
            // thing that failed is reported, and reopening Settings shows the real startup state.
            MessageBox.Show(
                this,
                "Windows startup settings could not be updated.",
                "UsageAI",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
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
            ? Theme.Warning
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

    private void AddSection(string title)
    {
        var scale = Scale();
        var label = new Label
        {
            AutoSize = true,
            Font = Own(Typography.Mono(8F)),
            ForeColor = Theme.Muted,
            Margin = scale.Pad(0, 14, 0, 6),
            Text = title.ToUpperInvariant(),
        };
        var row = _layout.RowCount;
        _layout.Controls.Add(label, 0, row);
        _layout.SetColumnSpan(label, 2);
        _layout.RowCount = row + 1;
    }

    private void AddRow(string label, Control control)
    {
        var scale = Scale();
        var caption = new Label
        {
            AutoSize = true,
            ForeColor = Theme.Text,
            Margin = scale.Pad(0, 8, 8, 4),
            Text = label,
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

    private CheckBox CreateCheckBox(string text) => new()
    {
        AutoSize = true,
        FlatStyle = FlatStyle.Flat,
        ForeColor = Theme.Text,
        Margin = Scale().Pad(0, 4, 0, 4),
        Text = text,
    };

    private NumericUpDown CreateNumeric(int minimum, int maximum) => new()
    {
        BackColor = Theme.SurfaceRaised,
        BorderStyle = BorderStyle.FixedSingle,
        Dock = DockStyle.Fill,
        ForeColor = Theme.Text,
        Margin = Scale().Pad(0, 4, 0, 4),
        Maximum = maximum,
        Minimum = minimum,
    };

    private Button CreateLinkButton(string text, EventHandler onClick)
    {
        var button = new Button
        {
            AutoSize = true,
            BackColor = Theme.Night,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.Signal,
            Margin = Scale().Pad(0, 4, 0, 4),
            Text = text,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += onClick;
        return button;
    }

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
        Bounds = FitToWorkingArea(Bounds, Screen.FromRectangle(eventArgs.SuggestedRectangle).WorkingArea);
        _scrollHost.AutoScrollPosition = new Point(0, scrollY);
    }

    private void ApplyScaledLayout()
    {
        var scale = Scale();
        var workingArea = Screen.FromPoint(Location).WorkingArea;
        MinimumSize = new Size(
            Math.Min(scale[390], workingArea.Width),
            Math.Min(scale[420], workingArea.Height));
        _shell.RowStyles[1] = new RowStyle(SizeType.Absolute, scale[52]);
        _layout.Padding = scale.Pad(18, 16, 18, 8);
        _providers.Height = scale[104];
        _checkForUpdatesButton.MinimumSize = new Size(scale[142], scale[30]);
        _buttons.Padding = scale.Pad(0, 10, 18, 10);
        _saveButton.Size = new Size(scale[104], scale[30]);
        _cancelButton.Size = new Size(scale[104], scale[30]);
        foreach (var numeric in _layout.Controls.OfType<NumericUpDown>()) numeric.Margin = scale.Pad(0, 4, 0, 4);
        foreach (var combo in _layout.Controls.OfType<ComboBox>()) combo.Margin = scale.Pad(0, 4, 0, 4);
        foreach (var checkBox in _layout.Controls.OfType<CheckBox>()) checkBox.Margin = scale.Pad(0, 4, 0, 4);
    }

    private static Rectangle FitToWorkingArea(Rectangle bounds, Rectangle workingArea)
    {
        var width = Math.Min(bounds.Width, workingArea.Width);
        var height = Math.Min(bounds.Height, workingArea.Height);
        var x = Math.Clamp(bounds.X, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - width));
        var y = Math.Clamp(bounds.Y, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - height));
        return new Rectangle(x, y, width, height);
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
        _updateStatus.ForeColor = Theme.Muted;
        _startupHint.ForeColor = StartupHintColor();
        _saveButton.BackColor = Theme.Accent;
        _saveButton.ForeColor = Theme.OnAccent;
        _saveButton.FlatAppearance.BorderColor = Theme.Accent;
        _cancelButton.BackColor = Theme.SurfaceRaised;
        _cancelButton.ForeColor = Theme.Text;
        _cancelButton.FlatAppearance.BorderColor = Theme.Hairline;
        _versionLabel.ForeColor = Theme.Signal;
        WindowThemeHelpers.ApplyDarkTitleBar(this, Theme.IsDark && !Theme.IsHighContrast);
        WindowThemeHelpers.ApplyDarkScrollbar(_scrollHost, Theme.IsDark && !Theme.IsHighContrast);
        WindowThemeHelpers.ApplyDarkScrollbar(_providers, Theme.IsDark && !Theme.IsHighContrast);
        Invalidate(invalidateChildren: true);
    }

    private static void ApplyControlTheme(Control control)
    {
        switch (control)
        {
            case NumericUpDown or ComboBox or CheckedListBox:
                control.BackColor = Theme.SurfaceRaised;
                control.ForeColor = Theme.Text;
                break;
            case CheckBox:
                control.BackColor = Theme.Night;
                control.ForeColor = Theme.Text;
                break;
            case Label label:
                label.BackColor = Theme.Night;
                label.ForeColor = label.Text.Equals(label.Text.ToUpperInvariant(), StringComparison.Ordinal)
                    ? Theme.Muted
                    : Theme.Text;
                break;
            case Button button when button.FlatAppearance.BorderSize == 0:
                button.BackColor = Theme.Night;
                button.ForeColor = Theme.Signal;
                break;
            case Button button:
                button.BackColor = Theme.SurfaceRaised;
                button.ForeColor = Theme.Text;
                button.FlatAppearance.BorderColor = Theme.Hairline;
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
