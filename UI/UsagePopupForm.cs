using System.Drawing.Drawing2D;

namespace UsageAI.UI;

internal sealed class UsagePopupForm : Form
{
    private readonly TableLayoutPanel _shell;
    private readonly FlowLayoutPanel _content;
    private readonly Label _titleLabel;
    private readonly Label _subtitleLabel;
    private readonly Label _connectionLabel;
    private readonly Label _updatedLabel;
    private readonly Button _refreshButton;
    private IReadOnlyList<ProviderViewState> _states = Array.Empty<ProviderViewState>();
    private DashboardMode _mode = DashboardMode.Compact;
    private bool _allowClose;
    private bool _isRefreshing;
    private DateTimeOffset? _lastRefreshed;
    private Rectangle? _dashboardBounds;
    private bool _dashboardWasShown;
    private bool _layoutRefreshPending;
    private bool _updatingCardWidths;

    public event EventHandler? RefreshRequested;

    public UsagePopupForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.Night;
        ClientSize = new Size(416, 236);
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

        _shell = new TableLayoutPanel
        {
            BackColor = Theme.Night,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = new Padding(16, 14, 16, 12),
            RowCount = 3,
        };
        _shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        _shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        Controls.Add(_shell);

        var header = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        var mark = new UsageMarkControl
        {
            Location = new Point(0, 4),
            Size = new Size(34, 34),
        };
        _titleLabel = new Label
        {
            AutoEllipsis = true,
            Font = new Font("Segoe UI Variable Display", 12F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Theme.Text,
            Location = new Point(46, 0),
            Size = new Size(220, 26),
            Text = "Usage at a glance",
        };
        _subtitleLabel = new Label
        {
            AutoEllipsis = true,
            Font = new Font("Segoe UI Variable Text", 8.2F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Theme.Muted,
            Location = new Point(47, 26),
            Size = new Size(255, 20),
            Text = "Your connected AI accounts",
        };
        _connectionLabel = new Label
        {
            Dock = DockStyle.Right,
            Font = new Font("Cascadia Mono", 7.5F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Theme.Muted,
            Size = new Size(116, 38),
            Text = "CHECKING",
            TextAlign = ContentAlignment.MiddleRight,
        };
        header.Controls.Add(mark);
        header.Controls.Add(_titleLabel);
        header.Controls.Add(_subtitleLabel);
        header.Controls.Add(_connectionLabel);
        _shell.Controls.Add(header, 0, 0);

        _content = new FlowLayoutPanel
        {
            AutoScroll = true,
            BackColor = Theme.Night,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            WrapContents = false,
        };
        _content.SizeChanged += (_, _) => RefreshDashboardLayout();
        _shell.Controls.Add(_content, 0, 1);

        var footer = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            RowCount = 1,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _updatedLabel = new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Variable Text", 8F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Theme.Muted,
            Margin = new Padding(2, 8, 8, 0),
            Text = "Checking accounts...",
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _refreshButton = new Button
        {
            BackColor = Theme.SurfaceRaised,
            Cursor = Cursors.Hand,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Variable Text", 8.5F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Theme.Text,
            Margin = new Padding(0, 7, 0, 1),
            TabIndex = 0,
            Text = "Refresh",
            UseVisualStyleBackColor = false,
        };
        _refreshButton.FlatAppearance.BorderColor = Theme.Hairline;
        _refreshButton.FlatAppearance.MouseDownBackColor = Theme.Surface;
        _refreshButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(31, 43, 57);
        _refreshButton.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        footer.Controls.Add(_updatedLabel, 0, 0);
        footer.Controls.Add(_refreshButton, 1, 0);
        _shell.Controls.Add(footer, 0, 2);

        Deactivate += (_, _) =>
        {
            if (_mode == DashboardMode.Compact)
            {
                Hide();
            }
        };
        ResizeEnd += (_, _) => SaveDashboardBounds();
        KeyPreview = true;
        KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode == Keys.Escape)
            {
                Hide();
            }
        };
    }

    public DashboardMode Mode => _mode;

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
        _titleLabel.Text = mode == DashboardMode.Compact ? "Usage at a glance" : "Usage dashboard";
        _subtitleLabel.Text = mode == DashboardMode.Compact
            ? "Your connected AI accounts"
            : "All providers and usage metrics";
        RebuildCards();
    }

    public void SetStates(
        IReadOnlyList<ProviderViewState> states,
        bool isRefreshing,
        DateTimeOffset? lastRefreshed)
    {
        _states = states;
        _isRefreshing = isRefreshing;
        _lastRefreshed = lastRefreshed;
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
        var x = Math.Clamp(cursor.X - Width + 24, screen.Left + 8, screen.Right - Width - 8);
        var y = Math.Clamp(screen.Bottom - Height - 8, screen.Top + 8, screen.Bottom - Height - 8);
        if (cursor.Y < screen.Top + Height)
        {
            y = screen.Top + 8;
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

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(Theme.Hairline);
        e.Graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    private void RebuildCards()
    {
        _content.SuspendLayout();
        try
        {
            _content.Controls.Clear();
            var visibleStates = _mode == DashboardMode.Full
                ? _states
                : CompactStates();

            if (visibleStates.Count == 0)
            {
                _content.Controls.Add(new EmptyProvidersControl
                {
                    Height = 104,
                    Margin = new Padding(0, 0, 0, 10),
                });
            }
            else
            {
                foreach (var state in visibleStates)
                {
                    _content.Controls.Add(new ProviderUsageCard(state, _mode == DashboardMode.Full));
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

    private ProviderViewState[] CompactStates()
    {
        var connected = _states.Where(state => state.IsConnected).ToArray();
        if (connected.Length > 0)
        {
            return connected;
        }

        return _isRefreshing
            ? _states.Where(state => state.IsLoading).ToArray()
            : Array.Empty<ProviderViewState>();
    }

    private void UpdateStatus()
    {
        var connected = _states.Count(state => state.IsConnected);
        _connectionLabel.ForeColor = connected > 0 ? Theme.Success : _isRefreshing ? Theme.Signal : Theme.Critical;
        _connectionLabel.Text = _isRefreshing
            ? "REFRESHING"
            : connected == 1
                ? "1 CONNECTED"
                : $"{connected} CONNECTED";
        _refreshButton.Enabled = !_isRefreshing;
        _refreshButton.Text = _isRefreshing ? "Reading..." : "Refresh";
        _updatedLabel.Text = _isRefreshing
            ? "Checking all providers..."
            : _lastRefreshed is null
                ? "Waiting for first refresh"
                : $"Updated {_lastRefreshed.Value:t}";
    }

    private void ApplyPreferredSize(Rectangle workingArea)
    {
        var width = _mode == DashboardMode.Compact ? 416 : 548;
        var contentHeight = _content.Controls.Cast<Control>().Sum(control => control.Height + control.Margin.Vertical);
        contentHeight = Math.Max(104, contentHeight);
        var chromeHeight = _shell.Padding.Vertical + 58 + 46;
        var nonClientWidth = Width - ClientSize.Width;
        var nonClientHeight = Height - ClientSize.Height;
        var maximumHeight = Math.Max(260, workingArea.Height - nonClientHeight - 32);
        var desiredHeight = Math.Min(chromeHeight + contentHeight, maximumHeight);
        ClientSize = new Size(Math.Min(width, workingArea.Width - nonClientWidth - 32), desiredHeight);
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
                MinimumSize = new Size(460, 360);
                ShowInTaskbar = true;
                Text = "UsageAI Dashboard";
                TopMost = false;
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

        if (!_dashboardWasShown)
        {
            ApplyPreferredSize(workingArea);
            Location = new Point(
                workingArea.Left + Math.Max(0, (workingArea.Width - Width) / 2),
                workingArea.Top + Math.Max(0, (workingArea.Height - Height) / 2));
            _dashboardWasShown = true;
            _dashboardBounds = Bounds;
        }
        else if ((changedToFull || !Visible) && _dashboardBounds is { } savedBounds)
        {
            Bounds = FitToWorkingArea(savedBounds, workingArea);
        }

        if (!Visible)
        {
            Show();
        }

        Activate();
        BringToFront();
    }

    private void SaveDashboardBounds()
    {
        if (_mode != DashboardMode.Full || !_dashboardWasShown)
        {
            return;
        }

        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            _dashboardBounds = bounds;
        }
    }

    private static Rectangle FitToWorkingArea(Rectangle bounds, Rectangle workingArea)
    {
        var width = Math.Clamp(bounds.Width, Math.Min(460, workingArea.Width), workingArea.Width);
        var height = Math.Clamp(bounds.Height, Math.Min(360, workingArea.Height), workingArea.Height);
        var x = Math.Clamp(bounds.X, workingArea.Left, workingArea.Right - width);
        var y = Math.Clamp(bounds.Y, workingArea.Top, workingArea.Bottom - height);
        return new Rectangle(x, y, width, height);
    }

    private void UpdateCardWidths()
    {
        if (_updatingCardWidths || _content.IsDisposed)
        {
            return;
        }

        _updatingCardWidths = true;
        try
        {
            var hasVerticalScroll = _content.VerticalScroll.Visible;
            var width = _content.ClientSize.Width - (hasVerticalScroll ? SystemInformation.VerticalScrollBarWidth : 0);
            width = Math.Max(220, width);
            foreach (Control control in _content.Controls)
            {
                if (control.Width != width)
                {
                    control.Width = width;
                }
            }
        }
        finally
        {
            _updatingCardWidths = false;
        }
    }

    private void RefreshDashboardLayout()
    {
        if (_content.IsDisposed || Disposing || IsDisposed)
        {
            return;
        }

        _content.PerformLayout();
        UpdateCardWidths();
        _content.Invalidate(invalidateChildren: true);
        Invalidate();

        if (_layoutRefreshPending || !IsHandleCreated)
        {
            return;
        }

        _layoutRefreshPending = true;
        BeginInvoke((Action)(() =>
        {
            _layoutRefreshPending = false;
            if (_content.IsDisposed || Disposing || IsDisposed)
            {
                return;
            }

            _content.PerformLayout();
            UpdateCardWidths();
            _content.Invalidate(invalidateChildren: true);
            Invalidate();
        }));
    }

    private sealed class UsageMarkControl : Control
    {
        public UsageMarkControl()
        {
            DoubleBuffered = true;
            AccessibleName = "UsageAI";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var background = new SolidBrush(Theme.SurfaceRaised);
            using var border = new Pen(Theme.Hairline);
            using var shape = RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), 9F);
            e.Graphics.FillPath(background, shape);
            e.Graphics.DrawPath(border, shape);
            using var codex = new SolidBrush(Theme.Codex);
            using var claude = new SolidBrush(Theme.Claude);
            using var copilot = new SolidBrush(Theme.Copilot);
            e.Graphics.FillRectangle(codex, 8, 9, 4, 16);
            e.Graphics.FillRectangle(claude, 15, 13, 4, 12);
            e.Graphics.FillRectangle(copilot, 22, 6, 4, 19);
        }
    }

    private sealed class EmptyProvidersControl : Control
    {
        private readonly Font _titleFont = new("Segoe UI Variable Display", 9.5F, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font _bodyFont = new("Segoe UI Variable Text", 8.3F, FontStyle.Regular, GraphicsUnit.Point);

        public EmptyProvidersControl()
        {
            DoubleBuffered = true;
            AccessibleName = "No connected providers";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var background = new SolidBrush(Theme.Surface);
            using var border = new Pen(Theme.Hairline);
            using var shape = RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), 12F);
            e.Graphics.FillPath(background, shape);
            e.Graphics.DrawPath(border, shape);
            TextRenderer.DrawText(e.Graphics, "No connected providers", _titleFont, new Rectangle(18, 18, Width - 36, 24),
                Theme.Text, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, "Right-click the tray icon and choose Open to view connection details.", _bodyFont,
                new Rectangle(18, 49, Width - 36, 42), Theme.Muted,
                TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
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
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, float radius)
    {
        var diameter = radius * 2F;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
