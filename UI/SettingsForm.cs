using UsageAI.Services;

namespace UsageAI.UI;

/// <summary>
/// Preferences that used to be compile-time constants: refresh cadence, alert thresholds,
/// theme, history, and which providers appear and in what order.
/// </summary>
internal sealed class SettingsForm : Form
{
    private static readonly object[] ThemeChoices = { "Follow Windows", "Dark", "Light" };

    private readonly List<Font> _ownedFonts = new();
    private readonly AppSettings _settings;
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
    private readonly ComboBox _trayProvider;
    private readonly CheckBox _historyEnabled;
    private readonly CheckBox _forecastEnabled;
    private readonly CheckBox _hotkeyEnabled;
    private readonly CheckBox _updateCheckEnabled;
    private readonly CheckedListBox _providers;

    public SettingsForm(AppSettings settings, IReadOnlyList<(string Id, string DisplayName)> providers)
    {
        _settings = settings;
        var scale = new LayoutScale(this);

        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Night;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        Text = "UsageAI settings";
        ClientSize = new Size(scale[470], scale[640]);
        Font = Own(Typography.Text(9F));

        var shell = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            RowCount = 2,
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, scale[52]));
        Controls.Add(shell);

        var scrollHost = new Panel
        {
            AutoScroll = true,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        scrollHost.HandleCreated += OnScrollableControlHandleCreated;
        shell.Controls.Add(scrollHost, 0, 0);

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
        scrollHost.Controls.Add(_layout);

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
        _updateCheckEnabled = CreateCheckBox("Check GitHub for new releases");
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
            Text = "Tick to show. Use the buttons to change the order.",
        });
        AddSpan(_providers);
        AddSpan(CreateOrderButtons(scale));

        AddSection("System");
        AddSpan(_hotkeyEnabled);
        AddSpan(_updateCheckEnabled);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = Padding.Empty,
            Padding = scale.Pad(0, 10, 18, 10),
        };
        var save = CreateDialogButton("Save", scale, primary: true);
        save.Click += (_, _) => Apply();
        var cancel = CreateDialogButton("Cancel", scale, primary: false);
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        shell.Controls.Add(buttons, 0, 1);
        AcceptButton = save;
        CancelButton = cancel;

        LoadValues();
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
        _historyEnabled.Checked = _settings.HistoryEnabled;
        _forecastEnabled.Checked = _settings.ForecastEnabled;
        _hotkeyEnabled.Checked = _settings.GlobalHotkeyEnabled;
        _updateCheckEnabled.Checked = _settings.UpdateCheckEnabled;
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
        _settings.HistoryEnabled = _historyEnabled.Checked;
        _settings.ForecastEnabled = _forecastEnabled.Checked;
        _settings.GlobalHotkeyEnabled = _hotkeyEnabled.Checked;
        _settings.UpdateCheckEnabled = _updateCheckEnabled.Checked;
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
        DialogResult = DialogResult.OK;
        Close();
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
        var target = index + offset;
        if (index < 0 || target < 0 || target >= _providers.Items.Count)
        {
            return;
        }

        var item = _providers.Items[index];
        var isChecked = _providers.GetItemChecked(index);
        _providers.Items.RemoveAt(index);
        _providers.Items.Insert(target, item);
        _providers.SetItemChecked(target, isChecked);
        _providers.SelectedIndex = target;
    }

    private void AddSection(string title)
    {
        var scale = new LayoutScale(this);
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
        var scale = new LayoutScale(this);
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
        Margin = new LayoutScale(this).Pad(0, 4, 0, 4),
        Text = text,
    };

    private NumericUpDown CreateNumeric(int minimum, int maximum) => new()
    {
        BackColor = Theme.SurfaceRaised,
        BorderStyle = BorderStyle.FixedSingle,
        Dock = DockStyle.Fill,
        ForeColor = Theme.Text,
        Margin = new LayoutScale(this).Pad(0, 4, 0, 4),
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
            Margin = new LayoutScale(this).Pad(0, 4, 0, 4),
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
        WindowThemeHelpers.ApplyDarkTitleBar(this, Theme.IsDark);
    }

    private static void OnScrollableControlHandleCreated(object? sender, EventArgs eventArgs)
    {
        if (sender is Control control)
        {
            WindowThemeHelpers.ApplyDarkScrollbar(control, Theme.IsDark);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
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
