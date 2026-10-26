using System.Diagnostics;
using System.Drawing.Drawing2D;
using UsageAI.Models;
using UsageAI.Services;

namespace UsageAI.UI;

internal enum DashboardMode
{
    Compact,
    Full,
}

/// <summary>The limit the popup headline names, with its forecast when one exists.</summary>
internal sealed record PopupHeadline(ProviderStatus Status, UsageMetric Metric, UsageProjection? Projection);

internal sealed class UsagePopupForm : Form
{
    private const int CompactWidthBaseline = 384;
    private const int FullWidthBaseline = 620;
    // A title row plus two lines of headline: the sentence naming the busiest limit, its value
    // and when it runs out does not fit one line at popup width.
    private const int HeaderRowBaseline = 68;
    private const int FooterRowBaseline = 46;
    private const int DashboardColumns = 2;
    private const int DashboardRows = 2;
    private const int CompactColumns = 2;
    private const int TileGap = 8;

    private readonly AppSettings _settings;
    private readonly int? _dpiOverride;
    private readonly TableLayoutPanel _shell;
    private readonly TableLayoutPanel _header;
    private readonly FlowLayoutPanel _content;
    private readonly Label _titleLabel;
    private readonly Label _summaryLabel;
    private readonly Label _updatedLabel;
    private readonly ComboBox _metricMode;
    private readonly Button _detailsButton;
    private readonly IconButton _refreshButton;
    private readonly IconButton _settingsButton;
    private readonly TableLayoutPanel _footer;
    private IReadOnlyList<ProviderStatus> _states = Array.Empty<ProviderStatus>();
    private IReadOnlyList<UsageSample> _history = Array.Empty<UsageSample>();
    private DashboardMode _mode = DashboardMode.Compact;
    private bool _allowClose;
    private bool _isRefreshing;
    private DateTimeOffset? _lastRefreshed;
    private Rectangle? _dashboardBounds;
    private bool _dashboardWasShown;
    private bool _updatingCardWidths;
    private bool _compactNeedsScroll;
    private PopupHeadline? _headline;
    private readonly System.Windows.Forms.Timer _uiTickTimer;
    private readonly ThemedToolTip _tileToolTip = new();
    private readonly System.Windows.Forms.Timer _refreshAnimationTimer;
    private float _refreshAnimationAngle;

    public UsagePopupForm(AppSettings settings, int? dpiOverride = null)
    {
        _settings = settings;
        _dpiOverride = dpiOverride;
        // Every measurement in this window is scaled explicitly, so WinForms auto-scaling is
        // left off rather than applied on top of it.
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Night;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "UsagePopup";
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "UsageAI";
        TopMost = true;
        ResizeRedraw = true;
        KeyPreview = true;

        var scale = Scale();
        ClientSize = new Size(scale[CompactWidthBaseline], scale[236]);

        _shell = new TableLayoutPanel
        {
            BackColor = Theme.Night,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = ShellPadding(scale),
            RowCount = 3,
        };
        _shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _shell.RowStyles.Add(new RowStyle(SizeType.Absolute, scale[HeaderRowBaseline]));
        _shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _shell.RowStyles.Add(new RowStyle(SizeType.Absolute, scale[FooterRowBaseline]));
        Controls.Add(_shell);

        _titleLabel = new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Font = Typography.Display(12F),
            ForeColor = Theme.Text,
            Margin = Padding.Empty,
            Text = "Usage",
            TextAlign = ContentAlignment.MiddleLeft,
        };

        // The headline answers the popup's one question - which limit needs attention - in a
        // sentence, so it is read before any ring.
        _summaryLabel = new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Font = Typography.Text(8.3F),
            ForeColor = Theme.TextSecondary,
            Margin = Padding.Empty,
            Text = "Checking your accounts…",
            TextAlign = ContentAlignment.TopLeft,
        };
        _refreshButton = new IconButton(IconGlyph.Refresh, "Refresh")
        {
            Dock = DockStyle.Fill,
            DpiOverride = _dpiOverride,
            Margin = Padding.Empty,
        };
        _refreshButton.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        _settingsButton = new IconButton(IconGlyph.Settings, "Settings")
        {
            Dock = DockStyle.Fill,
            DpiOverride = _dpiOverride,
            Margin = Padding.Empty,
        };
        _settingsButton.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        _header = new TableLayoutPanel
        {
            ColumnCount = 3,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            RowCount = 2,
        };
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, scale[32]));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, scale[32]));
        _header.RowStyles.Add(new RowStyle(SizeType.Absolute, scale[32]));
        _header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _header.Controls.Add(_titleLabel, 0, 0);
        _header.Controls.Add(_refreshButton, 1, 0);
        _header.Controls.Add(_settingsButton, 2, 0);
        _header.Controls.Add(_summaryLabel, 0, 1);
        _header.SetColumnSpan(_summaryLabel, 3);
        _shell.Controls.Add(_header, 0, 0);

        // Both modes lay out in rows of two; SetMode returns early for the starting mode, so the
        // flow is set here rather than only there.
        _content = new FlowLayoutPanel
        {
            AutoScroll = false,
            BackColor = Theme.Night,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            WrapContents = true,
        };
        _content.HandleCreated += OnContentHandleCreated;
        _content.SizeChanged += (_, _) => RefreshDashboardLayout();
        _shell.Controls.Add(_content, 0, 1);

        _updatedLabel = new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Font = Typography.Text(8F),
            ForeColor = Theme.Muted,
            Margin = scale.Pad(2, 8, 8, 0),
            Text = "Checking accounts...",
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _metricMode = new ThemedComboBox
        {
            AccessibleName = "Dashboard metrics",
            BackColor = Theme.SurfaceRaised,
            Dock = DockStyle.Fill,
            Font = Typography.Text(8.5F),
            ForeColor = Theme.Text,
            Margin = scale.Pad(6, 7, 0, 1),
            Visible = false,
        };
        _metricMode.Items.AddRange(new object[] { "All metrics", "Metered only", "Important only" });
        _metricMode.SelectedIndex = (int)_settings.MetricDisplayMode;
        _metricMode.SelectedIndexChanged += (_, _) =>
        {
            if (_metricMode.SelectedIndex < 0)
            {
                return;
            }

            _settings.MetricDisplayMode = (MetricDisplayMode)_metricMode.SelectedIndex;
            _settings.Save();
            RebuildCards();
        };
        _detailsButton = CreateButton("Open dashboard", scale, primary: true);
        _detailsButton.Click += (_, _) => ShowNearTray(DashboardMode.Full);

        _footer = new TableLayoutPanel
        {
            ColumnCount = 3,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            RowCount = 1,
        };
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, scale[132]));
        _footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _footer.Controls.Add(_updatedLabel, 0, 0);
        _footer.Controls.Add(_metricMode, 1, 0);
        _footer.Controls.Add(_detailsButton, 2, 0);
        _shell.Controls.Add(_footer, 0, 2);

        if (_settings.DashboardBounds is { Length: 4 } saved)
        {
            _dashboardBounds = new Rectangle(saved[0], saved[1], saved[2], saved[3]);
        }

        Deactivate += (_, _) =>
        {
            if (_mode == DashboardMode.Compact && !StaysOpenWhenInactive)
            {
                Hide();
            }
        };
        ResizeEnd += (_, _) => SaveDashboardBounds();
        KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode == Keys.Escape)
            {
                Hide();
            }
        };
        Theme.Changed += OnThemeChanged;

        _uiTickTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _uiTickTimer.Tick += (_, _) =>
        {
            if (Visible && !IsDisposed && !Disposing)
            {
                UpdateStatus();
                _content.Invalidate(invalidateChildren: true);
            }
        };

        _refreshAnimationTimer = new System.Windows.Forms.Timer { Interval = 90 };
        _refreshAnimationTimer.Tick += (_, _) =>
        {
            _refreshAnimationAngle = (_refreshAnimationAngle + 24F) % 360F;
            _refreshButton.Rotation = _refreshAnimationAngle;
        };
    }

    public event EventHandler? RefreshRequested;

    public event EventHandler? SettingsRequested;

    public DashboardMode Mode => _mode;

    /// <summary>
    /// Keeps the compact popup visible when it loses focus. Only the interactive preview sets
    /// it, so the window can be inspected while another program has the focus.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool StaysOpenWhenInactive { get; init; }

    public void SetMode(DashboardMode mode)
    {
        if (_mode == mode)
        {
            return;
        }

        if (_mode == DashboardMode.Full)
        {
            SaveDashboardBounds();
        }

        _mode = mode;
        ConfigureWindowPresentation();
        _detailsButton.Visible = mode == DashboardMode.Compact;
        _metricMode.Visible = mode == DashboardMode.Full;
        _content.FlowDirection = FlowDirection.LeftToRight;
        _content.WrapContents = true;

        // Neither mode scrolls by default. The full dashboard is a fixed grid resized to the
        // client area, and the popup sizes itself to its tiles. An automatic scrollbar latched
        // onto freshly built content before the window had grown to fit it, narrowed the client
        // area under the widths just computed, and wrapped every row for the rest of the session.
        // ApplyPreferredSize turns scrolling on only when the popup cannot fit on the screen.
        _content.AutoScroll = false;
        _compactNeedsScroll = false;
        ApplyScaledChrome();
        RebuildCards();
    }

    public void SetStates(
        IReadOnlyList<ProviderStatus> states,
        bool isRefreshing,
        DateTimeOffset? lastRefreshed,
        IReadOnlyList<UsageSample> history)
    {
        _states = states;
        _isRefreshing = isRefreshing;
        _lastRefreshed = lastRefreshed;
        _history = history;

        // The forecast behind the headline changes only with new readings, so it is worked out
        // here once; the five-second tick only re-words the countdown.
        var forecastHistory = _settings.ForecastEnabled && _settings.HistoryEnabled
            ? history
            : Array.Empty<UsageSample>();
        _headline = SelectHeadline(states, forecastHistory, DateTimeOffset.Now);
        UpdateStatus();
        RebuildCards();
    }

    public void ShowNearTray(DashboardMode mode)
    {
        var changedToFull = _mode != DashboardMode.Full && mode == DashboardMode.Full;
        SetMode(mode);
        var cursor = Cursor.Position;
        var screen = Screen.FromPoint(cursor).WorkingArea;

        if (mode == DashboardMode.Full)
        {
            ShowDashboard(screen, changedToFull);
            return;
        }

        ApplyPreferredSize(screen);
        var scale = Scale();
        var x = Math.Clamp(
            cursor.X - Width + scale[24],
            screen.Left + scale[8],
            Math.Max(screen.Left + scale[8], screen.Right - Width - scale[8]));
        var y = Math.Clamp(
            screen.Bottom - Height - scale[8],
            screen.Top + scale[8],
            Math.Max(screen.Top + scale[8], screen.Bottom - Height - scale[8]));
        if (cursor.Y < screen.Top + Height)
        {
            y = screen.Top + scale[8];
        }

        Location = new Point(x, y);
        if (!Visible)
        {
            Show();
        }

        Activate();
    }

    public void CloseForExit()
    {
        SaveDashboardBounds();
        _allowClose = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        if (!_allowClose && eventArgs.CloseReason == CloseReason.UserClosing)
        {
            SaveDashboardBounds();
            if (_mode == DashboardMode.Full && WindowState != FormWindowState.Normal)
            {
                WindowState = FormWindowState.Normal;
            }

            eventArgs.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(eventArgs);
    }

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        if (_mode == DashboardMode.Full &&
            WindowState != FormWindowState.Minimized &&
            _content is not null)
        {
            RefreshDashboardLayout();
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs eventArgs)
    {
        var scrollY = LayoutScale.ScaleBetweenDpis(
            -_content.AutoScrollPosition.Y,
            eventArgs.DeviceDpiOld,
            eventArgs.DeviceDpiNew);
        base.OnDpiChanged(eventArgs);
        ApplyScaledChrome();
        RebuildCards();
        _content.AutoScrollPosition = new Point(0, scrollY);
        var workingArea = Screen.FromRectangle(eventArgs.SuggestedRectangle).WorkingArea;
        if (_mode == DashboardMode.Full)
        {
            ApplyDashboardMinimumSize(workingArea);
            Bounds = FitDashboardToWorkingArea(Bounds, workingArea);
        }
        else
        {
            ApplyPreferredSize(workingArea);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        // Painted even where DWM draws a rounded frame of the same colour: DWM clips the corners
        // away, while previews (DrawToBitmap) and sessions without DWM effects still get an edge.
        using var border = new Pen(Theme.Hairline);
        e.Graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowThemeHelpers.ApplyDarkTitleBar(this, Theme.IsDark && !Theme.IsHighContrast);
        WindowThemeHelpers.ApplyWindowFrame(this);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            _uiTickTimer.Start();
            UpdateStatus();
            _content.Invalidate(invalidateChildren: true);
        }
        else
        {
            _uiTickTimer.Stop();
            StopRefreshAnimation();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopRefreshAnimation();
            _refreshAnimationTimer.Dispose();
            _uiTickTimer.Dispose();
            _tileToolTip.Dispose();
            Theme.Changed -= OnThemeChanged;
        }

        base.Dispose(disposing);
    }

    private static Button CreateButton(string text, LayoutScale scale, bool primary)
    {
        var button = new Button
        {
            BackColor = primary ? Theme.Accent : Theme.SurfaceRaised,
            Cursor = Cursors.Hand,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            Font = Typography.Text(8.5F, FontStyle.Bold),
            ForeColor = primary ? Theme.OnAccent : Theme.Text,
            Margin = scale.Pad(6, 7, 0, 1),
            Text = text,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderColor = primary ? Theme.Accent : Theme.Hairline;
        button.FlatAppearance.MouseDownBackColor = primary
            ? Theme.Blend(Theme.Accent, Theme.Night, 0.75)
            : Theme.Surface;
        button.FlatAppearance.MouseOverBackColor = primary
            ? Theme.Blend(Theme.Accent, Theme.Text, 0.88)
            : Theme.Blend(Theme.SurfaceRaised, Theme.Text, 0.9);
        return button;
    }

    private void OnThemeChanged(object? sender, EventArgs eventArgs)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        ApplyThemeColors();
        RebuildCards();
        Invalidate(invalidateChildren: true);
    }

    private void ApplyThemeColors()
    {
        BackColor = Theme.Night;
        _shell.BackColor = Theme.Night;
        _content.BackColor = Theme.Night;
        _titleLabel.ForeColor = Theme.Text;
        _summaryLabel.ForeColor = Theme.TextSecondary;
        _updatedLabel.ForeColor = Theme.Muted;
        _metricMode.BackColor = Theme.SurfaceRaised;
        _metricMode.ForeColor = Theme.Text;
        _detailsButton.BackColor = Theme.Accent;
        _detailsButton.ForeColor = Theme.OnAccent;
        _detailsButton.FlatAppearance.BorderColor = Theme.Accent;
        _detailsButton.FlatAppearance.MouseDownBackColor = Theme.Blend(Theme.Accent, Theme.Night, 0.75);
        _detailsButton.FlatAppearance.MouseOverBackColor = Theme.Blend(Theme.Accent, Theme.Text, 0.88);
        _refreshButton.Invalidate();
        _settingsButton.Invalidate();
        WindowThemeHelpers.ApplyDarkTitleBar(this, Theme.IsDark && !Theme.IsHighContrast);
        WindowThemeHelpers.ApplyWindowFrame(this);
        WindowThemeHelpers.ApplyDarkScrollbar(_content, Theme.IsDark && !Theme.IsHighContrast);
        UpdateStatus();
    }

    private void ApplyScaledChrome()
    {
        var scale = Scale();
        _shell.Padding = ShellPadding(scale);
        _shell.RowStyles[0] = new RowStyle(SizeType.Absolute, scale[HeaderRowBaseline]);
        _shell.RowStyles[2] = new RowStyle(SizeType.Absolute, scale[FooterRowBaseline]);
        _header.ColumnStyles[1] = new ColumnStyle(SizeType.Absolute, scale[32]);
        _header.ColumnStyles[2] = new ColumnStyle(SizeType.Absolute, scale[32]);
        _header.RowStyles[0] = new RowStyle(SizeType.Absolute, scale[32]);
        _footer.ColumnStyles[1] = new ColumnStyle(SizeType.Absolute, _mode == DashboardMode.Full ? scale[150] : 0);
        _footer.ColumnStyles[2] = new ColumnStyle(SizeType.Absolute, _mode == DashboardMode.Compact ? scale[132] : 0);
    }

    private void RebuildCards()
    {
        _content.SuspendLayout();
        try
        {
            // Copy first: disposing a control removes it from the collection being iterated.
            var previous = _content.Controls.Cast<Control>().ToArray();
            _content.Controls.Clear();
            foreach (var control in previous)
            {
                control.Dispose();
            }

            var visibleStates = _mode == DashboardMode.Full ? _states : CompactStates();
            if (visibleStates.Count == 0)
            {
                _content.Controls.Add(new EmptyProvidersControl
                {
                    Height = Scale()[104],
                    Margin = new Padding(0, 0, 0, 10),
                });
            }
            else
            {
                var expanded = _mode == DashboardMode.Full;
                foreach (var state in visibleStates)
                {
                    if (!expanded)
                    {
                        var tile = new ProviderTile(
                            state,
                            _settings.QuotaDisplay,
                            _settings.PopupMetricGroup(state.ProviderId),
                            _dpiOverride,
                            _tileToolTip);
                        tile.ActionInvoked += OnCardActionInvoked;
                        _content.Controls.Add(tile);
                        continue;
                    }

                    var selectedMetrics = expanded && state.Snapshot is { } snapshot
                        ? UsageMetricSelection.Select(snapshot.Metrics, _settings.MetricDisplayMode)
                        : null;
                    var card = new ProviderUsageCard(
                        state,
                        expanded,
                        _history,
                        _settings.ForecastEnabled && _settings.HistoryEnabled,
                        selectedMetrics,
                        _dpiOverride,
                        _settings.QuotaDisplay);
                    card.ActionInvoked += OnCardActionInvoked;
                    _content.Controls.Add(card);
                }
            }
        }
        finally
        {
            _content.ResumeLayout(performLayout: true);
        }

        UpdateCardWidths();
        if (_mode == DashboardMode.Compact || !_dashboardWasShown)
        {
            ApplyPreferredSize(Screen.FromPoint(Cursor.Position).WorkingArea);
        }
    }

    private void OnCardActionInvoked(object? sender, ProviderCardActionEventArgs eventArgs)
    {
        switch (eventArgs.Action)
        {
            case ProviderCardAction.CopyCommand when eventArgs.Status.SignInCommand is { Length: > 0 } command:
                TryCopy(command);
                break;
            case ProviderCardAction.OpenAccount when eventArgs.Status.AccountUrl is { } url:
                TryOpen(url);
                break;
            case ProviderCardAction.OpenDetails:
                ShowNearTray(DashboardMode.Full);
                break;
            default:
                break;
        }
    }

    private void TryCopy(string value)
    {
        try
        {
            Clipboard.SetText(value);
            _updatedLabel.Text = $"Copied \"{value}\"";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process owns the clipboard; the command is still shown on the card.
            _updatedLabel.Text = "The clipboard was busy; copy the command from the card.";
        }
    }

    private void TryOpen(Uri url)
    {
        if (!url.IsAbsoluteUri || !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _updatedLabel.Text = "Windows could not open the provider page.";
        }
    }

    private ProviderStatus[] CompactStates()
    {
        var connected = _states.Where(state => state.IsConnected).ToArray();
        if (connected.Length > 0)
        {
            return connected;
        }

        return _isRefreshing
            ? _states.Where(state => state.IsLoading).ToArray()
            : Array.Empty<ProviderStatus>();
    }

    private void UpdateStatus()
    {
        _refreshButton.Enabled = !_isRefreshing;
        _refreshButton.AccessibleName = _isRefreshing ? "Refreshing" : "Refresh";
        UpdateRefreshAnimationState();

        _summaryLabel.Text = FormatHeadline(_headline, _states, _isRefreshing, _settings.QuotaDisplay, DateTimeOffset.Now);
        _updatedLabel.Text = RefreshSummary(
            _states,
            _isRefreshing,
            _lastRefreshed,
            DateTimeOffset.Now);
    }

    /// <summary>
    /// The busiest metered limit across connected providers: the one the popup headline names.
    /// Unlike the tray gauge, which follows each provider's primary window, the headline looks
    /// at every limit, so a weekly window at 92% is not hidden behind a 5-hour one at 40%.
    /// </summary>
    internal static (ProviderStatus Status, UsageMetric Metric)? SelectHeadlineMetric(IReadOnlyList<ProviderStatus> states) =>
        states
            .Where(state => state.Snapshot is not null)
            .SelectMany(state => state.Snapshot!.Metrics
                .Where(metric => metric.HasQuota)
                .Select(metric => (Status: state, Metric: metric)))
            .OrderByDescending(entry => entry.Metric.UsedPercent)
            .Select(entry => ((ProviderStatus Status, UsageMetric Metric)?)entry)
            .FirstOrDefault();

    /// <summary>
    /// The limit the popup headline names. A limit forecast to run out before it resets outranks
    /// one that is merely busy, and among those the one that runs out earliest wins; without a
    /// forecast it is the busiest limit of all.
    /// </summary>
    internal static PopupHeadline? SelectHeadline(
        IReadOnlyList<ProviderStatus> states,
        IReadOnlyList<UsageSample> history,
        DateTimeOffset now)
    {
        var runningOut = states
            .Where(state => state.Snapshot is not null)
            .SelectMany(state => state.Snapshot!.Metrics
                .Where(metric => metric.HasQuota)
                .Select(metric => new PopupHeadline(
                    state,
                    metric,
                    UsageForecast.Project(history, state.ProviderId, metric, now))))
            .Where(candidate => candidate.Projection is { BeforeReset: true })
            .OrderBy(candidate => candidate.Projection!.ExhaustedAt)
            .FirstOrDefault();
        if (runningOut is not null)
        {
            return runningOut;
        }

        return SelectHeadlineMetric(states) is { } busiest
            ? new PopupHeadline(busiest.Status, busiest.Metric, null)
            : null;
    }

    /// <summary>
    /// One sentence that answers the popup's question before any ring is read: which limit runs
    /// out first and when, or, without a forecast, which limit is busiest and when it resets.
    /// </summary>
    internal static string HeadlineText(
        IReadOnlyList<ProviderStatus> states,
        bool isRefreshing,
        QuotaDisplay quotaDisplay,
        IReadOnlyList<UsageSample> history,
        DateTimeOffset now) =>
        FormatHeadline(SelectHeadline(states, history, now), states, isRefreshing, quotaDisplay, now);

    private static string FormatHeadline(
        PopupHeadline? headline,
        IReadOnlyList<ProviderStatus> states,
        bool isRefreshing,
        QuotaDisplay quotaDisplay,
        DateTimeOffset now)
    {
        if (headline is null)
        {
            return isRefreshing
                ? "Checking your accounts…"
                : states.Any(state => state.IsConnected)
                    ? "No metered limits reported."
                    : "No connected providers.";
        }

        var (status, metric, projection) = headline;
        var value = metric.DisplayPrimary(quotaDisplay).ToLowerInvariant();
        var provider = ShortName(status.ProviderName);
        if (projection is { BeforeReset: true })
        {
            return $"{provider} runs out first: {metric.Name} at {value}, around {UsageFormatting.AbsoluteReset(projection.ExhaustedAt, now)}.";
        }

        var reset = UsageFormatting.RelativeReset(metric.ResetsAt, now);
        return string.IsNullOrEmpty(reset)
            ? $"Busiest: {provider} {metric.Name} at {value}."
            : $"Busiest: {provider} {metric.Name} at {value}, {reset}.";
    }

    internal static string RefreshSummary(
        IReadOnlyList<ProviderStatus> states,
        bool isRefreshing,
        DateTimeOffset? lastRefreshed,
        DateTimeOffset now)
    {
        if (isRefreshing)
        {
            return "Checking all providers...";
        }

        var staleStates = states.Where(state => state.IsStale).ToArray();
        if (staleStates.Length > 0)
        {
            var noun = staleStates.Length == 1 ? "provider" : "providers";
            var prefix = $"{staleStates.Length} {noun} stale";
            var lastStaleAttempt = staleStates
                .Select(state => state.LastAttemptedAt)
                .Where(attempted => attempted.HasValue)
                .Select(attempted => attempted!.Value)
                .DefaultIfEmpty()
                .Max();
            return lastStaleAttempt == default
                ? prefix
                : $"{prefix} · checked {UsageFormatting.Age(lastStaleAttempt, now)}";
        }

        var latestUpdated = states
            .Select(state => state.LastUpdated ?? state.Snapshot?.FetchedAt)
            .Where(updated => updated.HasValue)
            .Select(updated => updated!.Value)
            .DefaultIfEmpty()
            .Max();
        if (latestUpdated != default)
        {
            return $"Updated {UsageFormatting.Age(latestUpdated, now)}";
        }

        return lastRefreshed is { } checkedAt
            ? $"Checked {UsageFormatting.Age(checkedAt, now)}"
            : "Waiting for first refresh";
    }

    private void UpdateRefreshAnimationState()
    {
        if (!_isRefreshing || !Visible || IsDisposed || Disposing)
        {
            StopRefreshAnimation();
            return;
        }

        if (!_refreshAnimationTimer.Enabled)
        {
            _refreshAnimationAngle = 0F;
            _refreshButton.Rotation = 0F;
            _refreshAnimationTimer.Start();
        }
    }

    private void StopRefreshAnimation()
    {
        _refreshAnimationTimer.Stop();
        _refreshButton.Rotation = 0F;
    }

    private static string ShortName(string providerName) =>
        providerName.Equals("GitHub Copilot", StringComparison.OrdinalIgnoreCase) ? "Copilot" : providerName;

    internal void ApplyPreferredSize(Rectangle workingArea)
    {
        var scale = Scale();
        var width = scale[_mode == DashboardMode.Compact ? CompactWidthBaseline : FullWidthBaseline];
        int contentHeight;
        var tiles = _content.Controls.OfType<ProviderTile>().ToArray();
        if (_mode == DashboardMode.Compact && tiles.Length > 0)
        {
            var rows = (tiles.Length + CompactColumns - 1) / CompactColumns;
            var tileHeight = tiles.Max(tile => tile.NaturalHeight);
            contentHeight = (rows * tileHeight) + (scale[TileGap] * (rows - 1));
        }
        else if (_mode == DashboardMode.Compact || _content.Controls.Count <= 1)
        {
            contentHeight = _content.Controls
                .Cast<Control>()
                .Sum(control => control.Height + control.Margin.Vertical);
        }
        else
        {
            var gap = scale[10];
            var tallestCard = _content.Controls
                .Cast<Control>()
                .Select(control => control is ProviderUsageCard card ? card.NaturalHeight : control.Height)
                .DefaultIfEmpty(scale[104])
                .Max();
            var rows = DashboardRowCount(_content.Controls.Count);
            contentHeight = (rows * tallestCard) + (gap * (rows - 1));
        }

        contentHeight = Math.Max(scale[104], contentHeight);
        var chromeHeight = _shell.Padding.Vertical + scale[HeaderRowBaseline] + scale[FooterRowBaseline];
        var nonClientWidth = Width - ClientSize.Width;
        var nonClientHeight = Height - ClientSize.Height;
        // A comfortable minimum, but never more than the screen: at extreme scaling the minimum
        // alone can exceed the working area, and a window past the screen edge is unreachable.
        var maximumHeight = Math.Min(
            workingArea.Height - nonClientHeight,
            Math.Max(scale[260], workingArea.Height - nonClientHeight - scale[32]));
        var desiredHeight = Math.Min(chromeHeight + contentHeight, maximumHeight);
        if (_mode == DashboardMode.Compact)
        {
            _compactNeedsScroll = chromeHeight + contentHeight > maximumHeight;
            if (_content.AutoScroll != _compactNeedsScroll)
            {
                _content.AutoScroll = _compactNeedsScroll;
            }
        }

        ClientSize = new Size(
            Math.Min(
                width,
                Math.Min(
                    workingArea.Width - nonClientWidth,
                    Math.Max(scale[260], workingArea.Width - nonClientWidth - scale[32]))),
            desiredHeight);
        UpdateCardWidths();
    }

    private void ConfigureWindowPresentation()
    {
        SuspendLayout();
        try
        {
            if (_mode == DashboardMode.Full)
            {
                FormBorderStyle = FormBorderStyle.Sizable;
                MaximizeBox = true;
                MinimizeBox = true;
                ApplyDashboardMinimumSize(Screen.FromPoint(Location).WorkingArea);
                ShowInTaskbar = true;
                Text = "UsageAI Dashboard";
                TopMost = false;
                WindowThemeHelpers.ApplyDarkTitleBar(this, Theme.IsDark && !Theme.IsHighContrast);
                WindowThemeHelpers.ApplyWindowFrame(this);
                WindowThemeHelpers.ApplyDarkScrollbar(_content, Theme.IsDark && !Theme.IsHighContrast);
                return;
            }

            if (WindowState != FormWindowState.Normal)
            {
                WindowState = FormWindowState.Normal;
            }

            MinimumSize = Size.Empty;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            Text = "UsageAI";
            TopMost = true;
            WindowThemeHelpers.ApplyWindowFrame(this);
        }
        finally
        {
            ResumeLayout(performLayout: true);
        }
    }

    private void ShowDashboard(Rectangle workingArea, bool changedToFull)
    {
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        ApplyDashboardMinimumSize(workingArea);
        if (!_dashboardWasShown)
        {
            if (_dashboardBounds is { } restored)
            {
                Bounds = FitDashboardToWorkingArea(restored, workingArea);
            }
            else
            {
                ApplyPreferredSize(workingArea);
                Location = new Point(
                    workingArea.Left + Math.Max(0, (workingArea.Width - Width) / 2),
                    workingArea.Top + Math.Max(0, (workingArea.Height - Height) / 2));
            }

            _dashboardWasShown = true;
            _dashboardBounds = Bounds;
        }
        else if (changedToFull && _dashboardBounds is { } savedBounds)
        {
            Bounds = FitDashboardToWorkingArea(savedBounds, workingArea);
        }

        if (!Visible)
        {
            Show();
        }

        Activate();
        BringToFront();
    }

    private void ApplyDashboardMinimumSize(Rectangle workingArea)
    {
        var scale = Scale();
        MinimumSize = new Size(
            Math.Min(scale[560], workingArea.Width),
            Math.Min(scale[300], workingArea.Height));
    }

    private void SaveDashboardBounds()
    {
        if (_mode != DashboardMode.Full || !_dashboardWasShown)
        {
            return;
        }

        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        _dashboardBounds = bounds;
        var saved = new[] { bounds.X, bounds.Y, bounds.Width, bounds.Height };
        if (_settings.DashboardBounds is { Length: 4 } existing && existing.SequenceEqual(saved))
        {
            return;
        }

        _settings.DashboardBounds = saved;
        _settings.Save();
    }

    private Rectangle FitDashboardToWorkingArea(Rectangle bounds, Rectangle workingArea) =>
        FitToWorkingArea(bounds, workingArea, Scale());

    private static Rectangle FitToWorkingArea(Rectangle bounds, Rectangle workingArea) =>
        FitToWorkingArea(bounds, workingArea, new LayoutScale(LayoutScale.BaselineDpi));

    private static Rectangle FitToWorkingArea(
        Rectangle bounds,
        Rectangle workingArea,
        LayoutScale scale)
    {
        var width = Math.Clamp(bounds.Width, Math.Min(scale[460], workingArea.Width), workingArea.Width);
        var height = Math.Clamp(bounds.Height, Math.Min(scale[360], workingArea.Height), workingArea.Height);
        var x = Math.Clamp(bounds.X, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - width));
        var y = Math.Clamp(bounds.Y, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - height));
        return new Rectangle(x, y, width, height);
    }

    /// <summary>
    /// How many rows the grid needs. Four providers are the 2x2 the dashboard is designed around,
    /// but the column count is what is fixed, not the row count: the panel no longer scrolls, so a
    /// fifth provider has to earn a third row rather than be laid out past the bottom edge where
    /// nothing could reach it. Shorter rows are what the cards already compact for.
    /// </summary>
    private static int DashboardRowCount(int cardCount) =>
        Math.Max(DashboardRows, (cardCount + DashboardColumns - 1) / DashboardColumns);

    private void UpdateCardWidths()
    {
        if (_updatingCardWidths || _content.IsDisposed || Disposing || IsDisposed)
        {
            return;
        }

        _updatingCardWidths = true;
        _content.SuspendLayout();
        try
        {
            var scale = Scale();

            // The dashboard never scrolls, so its client width is the usable width. The popup
            // reserves the scrollbar up front when it will need one, rather than reading a client
            // width that changes underneath it the moment the scrollbar appears.
            var availableWidth = _mode == DashboardMode.Compact
                ? _content.Width - (_compactNeedsScroll
                    ? SystemInformation.GetVerticalScrollBarWidthForDpi(_dpiOverride ?? DeviceDpi)
                    : 0)
                : _content.ClientSize.Width;
            if (availableWidth < scale[100])
            {
                return;
            }

            if (_mode == DashboardMode.Compact)
            {
                // Tiles sit two to a row; anything else (the empty-state notice) spans the row.
                var gap = scale[TileGap];
                var count = _content.Controls.Count;
                var rows = (count + CompactColumns - 1) / CompactColumns;
                // FlowLayoutPanel wraps a row that fits exactly, and a wrapped row adds a scrollbar
                // that keeps it wrapped, so the row keeps a pixel of slack.
                var tileWidth = Math.Max(1, (availableWidth - gap - 1) / CompactColumns);
                for (var i = 0; i < count; i++)
                {
                    var control = _content.Controls[i];
                    var isTile = control is ProviderTile;
                    var column = i % CompactColumns;
                    var margin = isTile
                        ? new Padding(0, 0, column == 0 ? gap : 0, i / CompactColumns == rows - 1 ? 0 : gap)
                        : Padding.Empty;
                    if (control.Margin != margin)
                    {
                        control.Margin = margin;
                    }

                    var width = isTile ? tileWidth : availableWidth;
                    if (control.Width != width)
                    {
                        control.Width = width;
                    }
                }
            }
            else
            {
                var gap = scale[10];
                var cardWidth = Math.Max(1, (availableWidth - gap) / DashboardColumns);
                var count = _content.Controls.Count;
                var rows = DashboardRowCount(count);
                var availableHeight = _content.ClientSize.Height;
                var availableCardHeight = Math.Max(1, (availableHeight - (gap * (rows - 1))) / rows);

                for (var i = 0; i < count; i++)
                {
                    var control = _content.Controls[i];
                    var colIndex = i % DashboardColumns;
                    var rowIndex = i / DashboardColumns;
                    var rightMargin = colIndex == DashboardColumns - 1 ? 0 : gap;
                    var bottomMargin = rowIndex == rows - 1 ? 0 : gap;
                    var margin = new Padding(0, 0, rightMargin, bottomMargin);
                    if (control.Margin != margin)
                    {
                        control.Margin = margin;
                    }

                    if (control.Width != cardWidth || control.Height != availableCardHeight)
                    {
                        control.Size = new Size(cardWidth, availableCardHeight);
                    }
                }
            }
        }
        finally
        {
            try
            {
                _content.ResumeLayout(performLayout: true);
            }
            finally
            {
                _updatingCardWidths = false;
            }
        }
    }

    private void RefreshDashboardLayout()
    {
        if (_content.IsDisposed || Disposing || IsDisposed || _updatingCardWidths)
        {
            return;
        }

        UpdateCardWidths();
        _content.Invalidate(invalidateChildren: true);
        Invalidate();
    }

    private LayoutScale Scale() => _dpiOverride is { } dpi ? new LayoutScale(dpi) : new LayoutScale(this);

    // The popup is a tight flyout; the dashboard window gets more breathing room.
    private Padding ShellPadding(LayoutScale scale) => _mode == DashboardMode.Compact
        ? scale.Pad(12, 10, 12, 8)
        : scale.Pad(16, 14, 16, 12);

    private void OnContentHandleCreated(object? sender, EventArgs eventArgs) =>
        WindowThemeHelpers.ApplyDarkScrollbar(_content, Theme.IsDark && !Theme.IsHighContrast);

    private sealed class EmptyProvidersControl : Control
    {
        private int _fontDpi;
        private Font _titleFont;
        private Font _bodyFont;

        public EmptyProvidersControl()
        {
            _fontDpi = DeviceDpi;
            _titleFont = Typography.Display(9.5F, _fontDpi);
            _bodyFont = Typography.Text(8.3F, _fontDpi);
            DoubleBuffered = true;
            AccessibleName = "No connected providers";
            AccessibleDescription =
                "No providers are connected. Open the dashboard to see each provider's connection details.";
        }

        // Point-sized faces keep the DPI the process started on, so they have to be rebuilt for the
        // monitor this control actually landed on or the text stays at the primary monitor's scale.
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            EnsureFonts();
        }

        protected override void RescaleConstantsForDpi(int deviceDpiOld, int deviceDpiNew)
        {
            base.RescaleConstantsForDpi(deviceDpiOld, deviceDpiNew);
            EnsureFonts();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            EnsureFonts();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var scale = new LayoutScale(this);
            DrawingHelpers.FillCard(
                e.Graphics,
                new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)),
                Theme.Surface,
                Theme.Hairline,
                scale.Exact(12));
            DrawingHelpers.DrawText(
                e.Graphics,
                "No connected providers",
                _titleFont,
                Theme.Text,
                new Rectangle(scale[18], scale[18], Width - scale[36], scale[24]),
                TextFormatFlags.EndEllipsis);
            DrawingHelpers.DrawText(
                e.Graphics,
                "Right-click the tray icon and choose Open to view connection details.",
                _bodyFont,
                Theme.Muted,
                new Rectangle(scale[18], scale[49], Width - scale[36], scale[42]),
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _titleFont.Dispose();
                _bodyFont.Dispose();
            }

            base.Dispose(disposing);
        }

        private void EnsureFonts()
        {
            if (_fontDpi == DeviceDpi)
            {
                return;
            }

            _fontDpi = DeviceDpi;
            _titleFont.Dispose();
            _bodyFont.Dispose();
            _titleFont = Typography.Display(9.5F, _fontDpi);
            _bodyFont = Typography.Text(8.3F, _fontDpi);
        }
    }
}
