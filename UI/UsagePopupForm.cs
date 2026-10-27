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
    private const int FullWidthBaseline = 820;
    // A title row plus two lines of headline: the sentence naming the busiest limit, its value
    // and when it runs out does not fit one line at popup width.
    private const int HeaderRowBaseline = 68;
    private const int FooterRowBaseline = 46;
    private const int CompactColumns = 2;
    private const int TileGap = 8;

    private static readonly string[] MetricModeOptions = { "All", "Metered", "Important" };

    private readonly AppSettings _settings;
    private readonly int? _dpiOverride;
    private readonly TableLayoutPanel _shell;
    private readonly TableLayoutPanel _header;
    private readonly FlowLayoutPanel _content;
    private readonly SlimScrollHost _contentFrame;
    private readonly Label _titleLabel;
    private readonly Label _summaryLabel;
    private readonly Label _updatedLabel;
    private readonly SegmentedControl _metricMode;
    private readonly Button _detailsButton;
    private readonly IconButton _refreshButton;
    private readonly IconButton _settingsButton;
    private readonly TableLayoutPanel _footer;
    private IReadOnlyList<ProviderStatus> _states = Array.Empty<ProviderStatus>();
    private IReadOnlyList<UsageSample> _history = Array.Empty<UsageSample>();
    private DashboardMode _mode = DashboardMode.Compact;
    // The DPI the chrome and content were last measured for.
    private int _layoutDpi;
    // Where the compact popup was opened from, so a re-measure can place it there again.
    private Point? _trayAnchor;
    private bool _allowClose;
    private bool _isRefreshing;
    private DateTimeOffset? _lastRefreshed;
    private Rectangle? _dashboardBounds;
    private bool _dashboardWasShown;
    private bool _updatingCardWidths;
    private bool _needsScroll;
    private bool _dashboardSizedByUser;
    private Size _sizeAtResizeBegin;
    private PopupHeadline? _headline;
    private readonly System.Windows.Forms.Timer _uiTickTimer;
    private readonly ThemedToolTip _tileToolTip = new();
    // Shared by every tile and the table, which are rebuilt on updates, so a gauge keeps easing
    // from what it showed before the rebuild.
    private readonly MotionTracker _motion = new();
    private readonly System.Windows.Forms.Timer _refreshAnimationTimer;
    private long _refreshAnimationStart;

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
        // The dashboard's row filter sits in the header, every option in view: one click to
        // change, where the dropdown took two.
        _metricMode = new SegmentedControl(MetricModeOptions, (int)_settings.MetricDisplayMode)
        {
            AccessibleName = "Dashboard rows",
            Dock = DockStyle.Fill,
            DpiOverride = _dpiOverride,
            Margin = scale.Pad(8, 2, 8, 2),
            Visible = false,
        };
        _metricMode.SelectedIndexChanged += (_, _) =>
        {
            _settings.MetricDisplayMode = (MetricDisplayMode)_metricMode.SelectedIndex;
            _settings.Save();
            RebuildCards();
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
            ColumnCount = 4,
            Dock = DockStyle.Fill,
            // The shell's right edge is open for the scrollbar; this keeps the window's inset.
            Margin = scale.Pad(0, 0, RightEdge, 0),
            RowCount = 2,
        };
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, scale[32]));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, scale[32]));
        _header.RowStyles.Add(new RowStyle(SizeType.Absolute, scale[32]));
        _header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _header.Controls.Add(_titleLabel, 0, 0);
        _header.Controls.Add(_metricMode, 1, 0);
        _header.Controls.Add(_refreshButton, 2, 0);
        _header.Controls.Add(_settingsButton, 3, 0);
        _header.Controls.Add(_summaryLabel, 0, 1);
        _header.SetColumnSpan(_summaryLabel, 4);
        _shell.Controls.Add(_header, 0, 0);

        // Both modes lay out in rows of two; SetMode returns early for the starting mode, so the
        // flow is set here rather than only there.
        _content = new ContentPanel
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
        // Scrolls behind a slim scrollbar rather than the system's full-width one.
        _contentFrame = new SlimScrollHost(_content, _dpiOverride)
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        _shell.Controls.Add(_contentFrame, 0, 1);

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
        _detailsButton = CreateButton("Open dashboard", scale, primary: true);
        _detailsButton.Click += (_, _) => ShowNearTray(DashboardMode.Full);

        _footer = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            // The shell's right edge is open for the scrollbar; this keeps the window's inset.
            Margin = scale.Pad(0, 0, RightEdge, 0),
            RowCount = 1,
        };
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, scale[132]));
        _footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _footer.Controls.Add(_updatedLabel, 0, 0);
        _footer.Controls.Add(_detailsButton, 1, 0);
        _shell.Controls.Add(_footer, 0, 2);

        if (_settings.DashboardBounds is { Length: 4 } saved)
        {
            _dashboardBounds = new Rectangle(saved[0], saved[1], saved[2], saved[3]);
            _dashboardSizedByUser = _settings.DashboardSizedByUser;
        }

        Deactivate += (_, _) =>
        {
            if (_mode == DashboardMode.Compact && !StaysOpenWhenInactive)
            {
                Hide();
            }
        };
        // ResizeEnd also ends a move, so only a size that actually changed counts as the user
        // sizing the dashboard.
        ResizeBegin += (_, _) => _sizeAtResizeBegin = Size;
        ResizeEnd += (_, _) =>
        {
            if (_mode == DashboardMode.Full && Size != _sizeAtResizeBegin)
            {
                _dashboardSizedByUser = true;
            }

            SaveDashboardBounds();
        };
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

        // The angle follows elapsed time, one turn a second, so a late tick never slows the turn.
        _refreshAnimationTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _refreshAnimationTimer.Tick += (_, _) =>
        {
            // Animation effects or High Contrast can be switched on mid-refresh; the arrow stops.
            if (!Motion.Enabled)
            {
                StopRefreshAnimation();
                return;
            }

            _refreshButton.Rotation = SpinnerAngle(Environment.TickCount64 - _refreshAnimationStart);
        };
    }

    public event EventHandler? RefreshRequested;

    public event EventHandler? SettingsRequested;

    /// <summary>A stale provider's Retry: refresh that provider alone.</summary>
    public event EventHandler<ProviderCardActionEventArgs>? ProviderRefreshRequested;

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

        // Neither mode scrolls by default: the popup sizes itself to its tiles and the dashboard
        // window to its table. An automatic scrollbar latched onto freshly built content before
        // the window had grown to fit it and narrowed the client area under the widths just
        // computed, so scrolling is switched on explicitly, only when the content cannot fit.
        _content.AutoScroll = false;
        _needsScroll = false;
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
        var cursor = Cursor.Position;
        var screen = Screen.FromPoint(cursor).WorkingArea;
        // Mode first: leaving the dashboard saves where the user left it, before anything moves.
        SetMode(mode);

        if (mode == DashboardMode.Full)
        {
            ShowDashboard(screen, changedToFull);
            return;
        }

        _trayAnchor = cursor;
        MoveOntoScreen(screen);
        PlaceNearTray(cursor, screen);
        if (!Visible)
        {
            Show();
        }

        Activate();
    }

    /// <summary>
    /// Puts the hidden window on the screen it is about to open on before anything is measured.
    /// Created or left on a monitor at another scale, it would be sized for that DPI and only
    /// then put here, where Windows rescales a window it creates or moves without reliably
    /// reporting it; a move while hidden reports the change, and the window is measured for it.
    /// </summary>
    private void MoveOntoScreen(Rectangle screen)
    {
        if (Visible || _dpiOverride is not null || screen.Contains(Location))
        {
            return;
        }

        _ = Handle;
        Location = screen.Location;
    }

    /// <summary>
    /// Sizes the compact popup and sets it beside the tray: against the screen edge nearest
    /// <paramref name="anchor"/>, at the bottom, or at the top when the taskbar is there.
    /// </summary>
    private void PlaceNearTray(Point anchor, Rectangle screen)
    {
        ApplyPreferredSize(screen);
        var scale = Scale();
        var x = Math.Clamp(
            anchor.X - Width + scale[24],
            screen.Left + scale[8],
            Math.Max(screen.Left + scale[8], screen.Right - Width - scale[8]));
        var y = Math.Clamp(
            screen.Bottom - Height - scale[8],
            screen.Top + scale[8],
            Math.Max(screen.Top + scale[8], screen.Bottom - Height - scale[8]));
        if (anchor.Y < screen.Top + Height)
        {
            y = screen.Top + scale[8];
        }

        Location = new Point(x, y);
    }

    /// <summary>
    /// For the README preview: the content area in client coordinates and the dashboard's
    /// table, so a table taller than any window can still be drawn whole. Null in the popup.
    /// </summary>
    internal (Rectangle Content, UsageLedger Table)? PreviewTable =>
        _content.Controls.OfType<UsageLedger>().FirstOrDefault() is { } table
            ? (RectangleToClient(_contentFrame.RectangleToScreen(new Rectangle(Point.Empty, _contentFrame.Size))), table)
            : null;

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
        ReapplyDpi(Screen.FromRectangle(eventArgs.SuggestedRectangle).WorkingArea, scrollY);
    }

    /// <summary>
    /// Measures the window again for the DPI it is on now: after a move to a monitor at another
    /// scale, and after being created straight onto one, which Windows reports as no change.
    /// </summary>
    private void ReapplyDpi(Rectangle workingArea, int scrollY)
    {
        ApplyScaledChrome();
        RebuildCards();
        _content.AutoScrollPosition = new Point(0, scrollY);
        if (_mode == DashboardMode.Full)
        {
            ApplyDashboardMinimumSize(workingArea);

            // Until the user sizes the dashboard it fits its table: a window created on one
            // monitor and shown on another was only scaled by Windows, from a height measured at
            // the first monitor's DPI, and cut the last provider off.
            if (!_dashboardSizedByUser)
            {
                ApplyPreferredSize(workingArea);
            }

            Bounds = FitDashboardToWorkingArea(Bounds, workingArea);
        }
        else
        {
            // Grown or shrunk for the new scale, the popup is set beside the tray again: kept
            // where it was, a popup opened at the bottom of the screen ran off it.
            if (_trayAnchor is { } anchor)
            {
                PlaceNearTray(anchor, workingArea);
            }
            else
            {
                ApplyPreferredSize(workingArea);
            }
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

        // Created straight onto a monitor at another scale, the window gets no DPI change and
        // keeps a layout measured at the start-up DPI. It is measured again once creation has
        // finished, when the window's own size and scrolling can be decided too.
        if (_dpiOverride is null && _layoutDpi != 0 && _layoutDpi != DeviceDpi)
        {
            BeginInvoke(() =>
            {
                if (!IsDisposed && !Disposing && _layoutDpi != DeviceDpi)
                {
                    ReapplyDpi(Screen.FromControl(this).WorkingArea, 0);
                }
            });
        }
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
            _motion.Reset();
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
            _motion.Dispose();
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
        // Leaving High Contrast during a refresh starts the arrow again.
        UpdateRefreshAnimationState();
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
        _metricMode.Invalidate();
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
        _layoutDpi = _dpiOverride ?? DeviceDpi;
        var scale = Scale();
        _shell.Padding = ShellPadding(scale);
        var edge = scale.Pad(0, 0, RightEdge, 0);
        _header.Margin = edge;
        _footer.Margin = edge;
        _shell.RowStyles[0] = new RowStyle(SizeType.Absolute, scale[HeaderRowBaseline]);
        _shell.RowStyles[2] = new RowStyle(SizeType.Absolute, scale[FooterRowBaseline]);
        _header.ColumnStyles[1] = new ColumnStyle(
            SizeType.Absolute,
            _mode == DashboardMode.Full ? _metricMode.PreferredWidth + _metricMode.Margin.Horizontal : 0);
        _header.ColumnStyles[2] = new ColumnStyle(SizeType.Absolute, scale[32]);
        _header.ColumnStyles[3] = new ColumnStyle(SizeType.Absolute, scale[32]);
        _header.RowStyles[0] = new RowStyle(SizeType.Absolute, scale[32]);
        _footer.ColumnStyles[1] = new ColumnStyle(SizeType.Absolute, _mode == DashboardMode.Compact ? scale[132] : 0);
    }

    private void RebuildCards()
    {
        // Held until the window has its new size, so the rebuild shows as one finished frame.
        using var redraw = WindowThemeHelpers.SuspendRedraw(_content);
        _content.SuspendLayout();
        try
        {
            var forecastHistory = _settings.ForecastEnabled && _settings.HistoryEnabled
                ? _history
                : Array.Empty<UsageSample>();

            // The dashboard's table is updated in place: it is rebuilt several times a refresh as
            // providers publish, and a new control would drop the user's focus and scroll position.
            if (_mode == DashboardMode.Full &&
                _content.Controls.Count == 1 &&
                _content.Controls[0] is UsageLedger existing)
            {
                existing.SetData(_states, _settings.MetricDisplayMode, _settings.QuotaDisplay, forecastHistory);
            }
            else
            {
                ReplaceContent(forecastHistory);
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
        else
        {
            GrowDashboardToFit();
        }
    }

    private void ReplaceContent(IReadOnlyList<UsageSample> forecastHistory)
    {
        // Copy first: disposing a control removes it from the collection being iterated.
        var previous = _content.Controls.Cast<Control>().ToArray();
        _content.Controls.Clear();
        foreach (var control in previous)
        {
            control.Dispose();
        }

        if (_mode == DashboardMode.Full)
        {
            var ledger = new UsageLedger(
                _states,
                _settings.MetricDisplayMode,
                _settings.QuotaDisplay,
                forecastHistory,
                _dpiOverride)
            {
                Margin = Padding.Empty,
                Motion = _motion,
            };
            ledger.ActionInvoked += OnCardActionInvoked;
            _content.Controls.Add(ledger);
        }
        else
        {
            AddCompactContent();
        }
    }

    /// <summary>
    /// Grows a dashboard the user has not sized when its table outgrows the window: one first
    /// opened while providers were still loading was sized to their one-line placeholders, and
    /// would otherwise keep that height and scroll once the real rows arrived. It never shrinks,
    /// so switching the row filter does not make the window jump.
    /// </summary>
    private void GrowDashboardToFit()
    {
        if (_mode != DashboardMode.Full ||
            _dashboardSizedByUser ||
            WindowState != FormWindowState.Normal ||
            _content.Controls.OfType<UsageLedger>().FirstOrDefault() is not { } ledger ||
            ledger.NaturalHeight <= _content.ClientSize.Height)
        {
            return;
        }

        var workingArea = Screen.FromControl(this).WorkingArea;
        var before = Size;
        ApplyPreferredSize(workingArea);
        if (Height < before.Height || Width < before.Width)
        {
            Size = new Size(Math.Max(Width, before.Width), Math.Max(Height, before.Height));
        }

        Bounds = FitDashboardToWorkingArea(Bounds, workingArea);
    }

    private void AddCompactContent()
    {
        var visibleStates = CompactStates();
        if (visibleStates.Length == 0)
        {
            _content.Controls.Add(new EmptyProvidersControl
            {
                Height = Scale()[104],
                Margin = new Padding(0, 0, 0, 10),
            });
            return;
        }

        foreach (var state in visibleStates)
        {
            var tile = new ProviderTile(
                state,
                _settings.QuotaDisplay,
                _settings.PopupMetricGroup(state.ProviderId),
                _dpiOverride,
                _tileToolTip)
            {
                Motion = _motion,
            };
            tile.ActionInvoked += OnCardActionInvoked;
            _content.Controls.Add(tile);
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
            case ProviderCardAction.Refresh:
                ProviderRefreshRequested?.Invoke(this, eventArgs);
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
            return $"{provider} runs out first: {metric.Name} at {value}, around {UsageFormatting.AbsoluteReset(projection.ExhaustedAt, now, System.Globalization.CultureInfo.CurrentCulture)}.";
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
        // Without motion the arrow holds still; the disabled button, its "Refreshing" name and
        // the status line still say a refresh is running.
        if (!_isRefreshing || !Visible || IsDisposed || Disposing || !Motion.Enabled)
        {
            StopRefreshAnimation();
            return;
        }

        if (!_refreshAnimationTimer.Enabled)
        {
            _refreshAnimationStart = Environment.TickCount64;
            _refreshButton.Rotation = 0F;
            _refreshAnimationTimer.Start();
        }
    }

    internal static float SpinnerAngle(long elapsedMilliseconds) =>
        elapsedMilliseconds <= 0 ? 0F : elapsedMilliseconds % 1000 * 0.36F;

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
        else if (_content.Controls.OfType<UsageLedger>().FirstOrDefault() is { } ledger)
        {
            contentHeight = ledger.NaturalHeight;
        }
        else
        {
            contentHeight = _content.Controls
                .Cast<Control>()
                .Sum(control => control.Height + control.Margin.Vertical);
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
        _needsScroll = chromeHeight + contentHeight > maximumHeight;
        if (_content.AutoScroll != _needsScroll)
        {
            _content.AutoScroll = _needsScroll;
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
        if (_settings.DashboardBounds is { Length: 4 } existing &&
            existing.SequenceEqual(saved) &&
            _settings.DashboardSizedByUser == _dashboardSizedByUser)
        {
            return;
        }

        _settings.DashboardBounds = saved;
        _settings.DashboardSizedByUser = _dashboardSizedByUser;
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

    private void UpdateCardWidths()
    {
        // The frame is created after the panel, whose first resize can arrive before it exists.
        if (_updatingCardWidths || _contentFrame is null || _content.IsDisposed || Disposing || IsDisposed)
        {
            return;
        }

        _updatingCardWidths = true;
        _content.SuspendLayout();
        try
        {
            var scale = Scale();

            // The dashboard is resized by hand, so whether its table needs a scrollbar is decided
            // here from the room it has now. Both modes reserve the scrollbar's width up front
            // rather than reading a client width that changes the moment the scrollbar appears.
            var ledger = _content.Controls.OfType<UsageLedger>().FirstOrDefault();
            if (_mode == DashboardMode.Full && ledger is not null)
            {
                _needsScroll = ledger.NaturalHeight > _content.Height;
                if (_content.AutoScroll != _needsScroll)
                {
                    _content.AutoScroll = _needsScroll;
                }
            }

            // Sized to the visible frame, less the slim scrollbar's gutter while it scrolls. The
            // frame runs to the window's edge, so content keeps the window's inset and the
            // scrollbar sits in that margin.
            var availableWidth = Math.Min(
                _contentFrame.ContentWidth(_needsScroll),
                _contentFrame.Width - scale[RightEdge]);
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
            else if (ledger is not null)
            {
                ledger.Margin = Padding.Empty;
                var size = new Size(availableWidth, ledger.NaturalHeight);
                if (ledger.Size != size)
                {
                    ledger.Size = size;
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

    // The popup is a tight flyout; the dashboard window gets more breathing room. The right edge
    // is open in both, so a scrolling view carries its scrollbar to the window's edge; the
    // header, footer and content keep the same inset themselves (RightEdge).
    private Padding ShellPadding(LayoutScale scale) => _mode == DashboardMode.Compact
        ? scale.Pad(12, 10, 0, 8)
        : scale.Pad(16, 14, 0, 12);

    /// <summary>The right inset of everything but the scrollbar, at 96 DPI.</summary>
    private int RightEdge => _mode == DashboardMode.Compact ? 12 : 16;

    private void OnContentHandleCreated(object? sender, EventArgs eventArgs) =>
        WindowThemeHelpers.ApplyDarkScrollbar(_content, Theme.IsDark && !Theme.IsHighContrast);

    /// <summary>
    /// The content panel. WinForms scrolls a focused child into view by aligning its top, which for
    /// the dashboard's table — taller than the view — undid the table's own scroll to its focused
    /// link. A child that does not fit keeps the current position and scrolls itself.
    /// </summary>
    private sealed class ContentPanel : FlowLayoutPanel
    {
        protected override Point ScrollToControl(Control activeControl) =>
            activeControl.Height > ClientSize.Height
                ? DisplayRectangle.Location
                : base.ScrollToControl(activeControl);
    }

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
