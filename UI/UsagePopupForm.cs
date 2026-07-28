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

internal sealed class UsagePopupForm : Form
{
    private const int CompactWidthBaseline = 416;
    private const int FullWidthBaseline = 560;
    private const int HeaderRowBaseline = 58;
    private const int FooterRowBaseline = 46;

    private readonly AppSettings _settings;
    private readonly TableLayoutPanel _shell;
    private readonly TableLayoutPanel _header;
    private readonly FlowLayoutPanel _content;
    private readonly Label _titleLabel;
    private readonly Label _subtitleLabel;
    private readonly Label _summaryLabel;
    private readonly Label _updatedLabel;
    private readonly Button _refreshButton;
    private readonly Button _settingsButton;
    private readonly UsageMarkControl _mark;
    private IReadOnlyList<ProviderStatus> _states = Array.Empty<ProviderStatus>();
    private IReadOnlyList<UsageSample> _history = Array.Empty<UsageSample>();
    private DashboardMode _mode = DashboardMode.Compact;
    private bool _allowClose;
    private bool _isRefreshing;
    private DateTimeOffset? _lastRefreshed;
    private Rectangle? _dashboardBounds;
    private bool _dashboardWasShown;
    private bool _layoutRefreshPending;
    private bool _updatingCardWidths;

    public UsagePopupForm(AppSettings settings)
    {
        _settings = settings;
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

        var scale = new LayoutScale(this);
        ClientSize = new Size(scale[CompactWidthBaseline], scale[236]);

        _shell = new TableLayoutPanel
        {
            BackColor = Theme.Night,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = scale.Pad(16, 14, 16, 12),
            RowCount = 3,
        };
        _shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _shell.RowStyles.Add(new RowStyle(SizeType.Absolute, scale[HeaderRowBaseline]));
        _shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _shell.RowStyles.Add(new RowStyle(SizeType.Absolute, scale[FooterRowBaseline]));
        Controls.Add(_shell);

        _mark = new UsageMarkControl
        {
            Dock = DockStyle.Fill,
            Margin = scale.Pad(0, 2, 12, 2),
        };
        _titleLabel = new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Font = Typography.Display(12F),
            ForeColor = Theme.Text,
            Margin = Padding.Empty,
            Text = "Usage at a glance",
            TextAlign = ContentAlignment.BottomLeft,
        };
        _subtitleLabel = new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Font = Typography.Text(8.2F),
            ForeColor = Theme.Muted,
            Margin = Padding.Empty,
            Text = "Your connected AI accounts",
            TextAlign = ContentAlignment.TopLeft,
        };
        _summaryLabel = new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Font = Typography.Mono(7.5F),
            ForeColor = Theme.Muted,
            Margin = Padding.Empty,
            Text = "CHECKING",
            TextAlign = ContentAlignment.MiddleRight,
        };

        _header = new TableLayoutPanel
        {
            ColumnCount = 3,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            RowCount = 2,
        };
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, scale[46]));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, scale[150]));
        _header.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        _header.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        _header.Controls.Add(_mark, 0, 0);
        _header.SetRowSpan(_mark, 2);
        _header.Controls.Add(_titleLabel, 1, 0);
        _header.Controls.Add(_subtitleLabel, 1, 1);
        _header.Controls.Add(_summaryLabel, 2, 0);
        _header.SetRowSpan(_summaryLabel, 2);
        _shell.Controls.Add(_header, 0, 0);

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
        _settingsButton = CreateButton("Settings", scale, primary: false);
        _settingsButton.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        _refreshButton = CreateButton("Refresh", scale, primary: true);
        _refreshButton.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);

        var footer = new TableLayoutPanel
        {
            ColumnCount = 3,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            RowCount = 1,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, scale[86]));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, scale[92]));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        footer.Controls.Add(_updatedLabel, 0, 0);
        footer.Controls.Add(_settingsButton, 1, 0);
        footer.Controls.Add(_refreshButton, 2, 0);
        _shell.Controls.Add(footer, 0, 2);

        if (_settings.DashboardBounds is { Length: 4 } saved)
        {
            _dashboardBounds = new Rectangle(saved[0], saved[1], saved[2], saved[3]);
        }

        Deactivate += (_, _) =>
        {
            if (_mode == DashboardMode.Compact)
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
    }

    public event EventHandler? RefreshRequested;

    public event EventHandler? SettingsRequested;

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
        IReadOnlyList<ProviderStatus> states,
        bool isRefreshing,
        DateTimeOffset? lastRefreshed,
        IReadOnlyList<UsageSample> history)
    {
        _states = states;
        _isRefreshing = isRefreshing;
        _lastRefreshed = lastRefreshed;
        _history = history;
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
        var x = Math.Clamp(cursor.X - Width + 24, screen.Left + 8, Math.Max(screen.Left + 8, screen.Right - Width - 8));
        var y = Math.Clamp(screen.Bottom - Height - 8, screen.Top + 8, Math.Max(screen.Top + 8, screen.Bottom - Height - 8));
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
        base.OnDpiChanged(eventArgs);
        ApplyScaledChrome();
        RebuildCards();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(Theme.Hairline);
        e.Graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
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
        _subtitleLabel.ForeColor = Theme.Muted;
        _updatedLabel.ForeColor = Theme.Muted;
        _refreshButton.BackColor = Theme.Accent;
        _refreshButton.ForeColor = Theme.OnAccent;
        _refreshButton.FlatAppearance.BorderColor = Theme.Accent;
        _settingsButton.BackColor = Theme.SurfaceRaised;
        _settingsButton.ForeColor = Theme.Text;
        _settingsButton.FlatAppearance.BorderColor = Theme.Hairline;
        UpdateStatus();
    }

    private void ApplyScaledChrome()
    {
        var scale = new LayoutScale(this);
        _shell.Padding = scale.Pad(16, 14, 16, 12);
        _shell.RowStyles[0] = new RowStyle(SizeType.Absolute, scale[HeaderRowBaseline]);
        _shell.RowStyles[2] = new RowStyle(SizeType.Absolute, scale[FooterRowBaseline]);
        _header.ColumnStyles[0] = new ColumnStyle(SizeType.Absolute, scale[46]);
        _header.ColumnStyles[2] = new ColumnStyle(SizeType.Absolute, scale[150]);
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
                    Height = new LayoutScale(this)[104],
                    Margin = new Padding(0, 0, 0, 10),
                });
            }
            else
            {
                var expanded = _mode == DashboardMode.Full;
                foreach (var state in visibleStates)
                {
                    var card = new ProviderUsageCard(
                        state,
                        expanded,
                        _history,
                        _settings.ForecastEnabled && _settings.HistoryEnabled);
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
        var connected = _states.Where(state => state.IsConnected).ToArray();
        _refreshButton.Enabled = !_isRefreshing;
        _refreshButton.Text = _isRefreshing ? "Reading" : "Refresh";

        // The most-consumed provider earns the header slot; a connected count is not news.
        var worst = connected
            .Where(state => state.Snapshot is not null)
            .OrderByDescending(state => state.Snapshot!.HighestUsedPercent)
            .FirstOrDefault();
        var headline = worst is null ? null : Headline(worst.Snapshot);
        if (worst is not null && headline is not null)
        {
            _summaryLabel.ForeColor = headline.HasQuota
                ? Theme.ForUsage(headline.UsedPercent!.Value)
                : Theme.Muted;
            _summaryLabel.Text = $"{ShortName(worst.ProviderName)} {headline.DisplayUsed}".ToUpperInvariant();
        }
        else if (_isRefreshing)
        {
            _summaryLabel.ForeColor = Theme.Signal;
            _summaryLabel.Text = "CHECKING";
        }
        else
        {
            _summaryLabel.ForeColor = Theme.Critical;
            _summaryLabel.Text = "NOT CONNECTED";
        }

        var stale = _states.Count(state => state.IsStale);
        _updatedLabel.Text = _isRefreshing
            ? "Checking all providers..."
            : _lastRefreshed is null
                ? "Waiting for first refresh"
                : stale > 0
                    ? $"Updated {UsageFormatting.Age(_lastRefreshed.Value, DateTimeOffset.Now)}, {stale} stale"
                    : $"Updated {UsageFormatting.Age(_lastRefreshed.Value, DateTimeOffset.Now)}";
    }

    /// <summary>The metric that best represents a provider: the most consumed real quota.</summary>
    private static UsageMetric? Headline(UsageSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        var metered = snapshot.Metrics
            .Where(metric => metric.HasQuota)
            .OrderByDescending(metric => metric.UsedPercent!.Value)
            .FirstOrDefault();
        return metered ?? snapshot.Primary;
    }

    private static string ShortName(string providerName) =>
        providerName.Equals("GitHub Copilot", StringComparison.OrdinalIgnoreCase) ? "Copilot" : providerName;

    private void ApplyPreferredSize(Rectangle workingArea)
    {
        var scale = new LayoutScale(this);
        var width = scale[_mode == DashboardMode.Compact ? CompactWidthBaseline : FullWidthBaseline];
        var contentHeight = _content.Controls
            .Cast<Control>()
            .Sum(control => control.Height + control.Margin.Vertical);
        contentHeight = Math.Max(scale[104], contentHeight);
        var chromeHeight = _shell.Padding.Vertical + scale[HeaderRowBaseline] + scale[FooterRowBaseline];
        var nonClientWidth = Width - ClientSize.Width;
        var nonClientHeight = Height - ClientSize.Height;
        var maximumHeight = Math.Max(scale[260], workingArea.Height - nonClientHeight - 32);
        var desiredHeight = Math.Min(chromeHeight + contentHeight, maximumHeight);
        ClientSize = new Size(
            Math.Min(width, Math.Max(scale[260], workingArea.Width - nonClientWidth - 32)),
            desiredHeight);
        UpdateCardWidths();
    }

    private void ConfigureWindowPresentation()
    {
        SuspendLayout();
        try
        {
            var scale = new LayoutScale(this);
            if (_mode == DashboardMode.Full)
            {
                FormBorderStyle = FormBorderStyle.Sizable;
                MaximizeBox = true;
                MinimizeBox = true;
                MinimumSize = new Size(scale[480], scale[360]);
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
            if (_dashboardBounds is { } restored)
            {
                Bounds = FitToWorkingArea(restored, workingArea);
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

    private static Rectangle FitToWorkingArea(Rectangle bounds, Rectangle workingArea)
    {
        var width = Math.Clamp(bounds.Width, Math.Min(460, workingArea.Width), workingArea.Width);
        var height = Math.Clamp(bounds.Height, Math.Min(360, workingArea.Height), workingArea.Height);
        var x = Math.Clamp(bounds.X, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - width));
        var y = Math.Clamp(bounds.Y, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - height));
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
            var scale = new LayoutScale(this);
            var side = Math.Min(Width, Height);
            var box = new Rectangle(0, Math.Max(0, (Height - side) / 2), Math.Max(1, side - 1), Math.Max(1, side - 1));
            DrawingHelpers.FillCard(e.Graphics, box, Theme.SurfaceRaised, Theme.Hairline, scale.Exact(9));

            var barWidth = Math.Max(2, box.Width / 9);
            var baseline = box.Bottom - box.Height / 4;
            using var codex = new SolidBrush(Theme.Codex);
            using var claude = new SolidBrush(Theme.Claude);
            using var copilot = new SolidBrush(Theme.Copilot);
            e.Graphics.FillRectangle(codex, box.Left + box.Width / 4, baseline - box.Height / 2, barWidth, box.Height / 2);
            e.Graphics.FillRectangle(claude, box.Left + box.Width / 2 - barWidth / 2, baseline - box.Height / 3, barWidth, box.Height / 3);
            e.Graphics.FillRectangle(copilot, box.Right - box.Width / 4 - barWidth, baseline - box.Height * 5 / 8, barWidth, box.Height * 5 / 8);
        }
    }

    private sealed class EmptyProvidersControl : Control
    {
        private readonly Font _titleFont = Typography.Display(9.5F);
        private readonly Font _bodyFont = Typography.Text(8.3F);

        public EmptyProvidersControl()
        {
            DoubleBuffered = true;
            AccessibleName = "No connected providers";
            AccessibleDescription =
                "No providers are connected. Open the dashboard to see each provider's connection details.";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
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
    }
}
