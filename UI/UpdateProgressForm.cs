using System.Drawing.Drawing2D;

namespace UsageAI.UI;

internal enum UpdateStage
{
    Downloading,
    Starting,
    Installing,
    Installed,
    InstalledNotRunning,
    Failed,
}

/// <summary>
/// The in-app update's progress, in place of the installer's own window. The bar fills while the
/// installer downloads and is verified, waits at <see cref="HandoffProgress"/> while Windows
/// closes this copy and installs, and the copy Windows reopens fills the rest and says the update
/// arrived. It sits in the tray corner without taking focus, like the system's own flyouts.
/// </summary>
internal sealed class UpdateProgressForm : Form
{
    /// <summary>Where the closing copy leaves the bar; the reopened copy carries on from it.</summary>
    internal const float HandoffProgress = 0.9F;

    internal static readonly TimeSpan DismissAfter = TimeSpan.FromSeconds(12);

    private const float DownloadStart = 0.04F;
    private const float DownloadEnd = 0.74F;
    private const float StartingProgress = 0.8F;
    private const int WidthBaseline = 360;
    private const int WsExToolWindow = 0x00000080;

    private readonly int? _dpiOverride;
    private readonly MotionTracker _motion = new();
    private readonly Button _primary;
    private readonly Button _close;
    private readonly Font _buttonFont = Typography.Text(9F);
    private readonly Font _primaryFont = Typography.Text(9F, FontStyle.Bold);
    private readonly System.Windows.Forms.Timer _dismissTimer;
    private Font? _titleFont;
    private Font? _statusFont;
    private int _fontDpi;
    private Rectangle _titleBounds;
    private Rectangle _statusBounds;
    private Rectangle _barBounds;
    private Screen? _screen;
    // Wanted rather than read back: a control reports itself hidden while its form is.
    private bool _showPrimary;
    private bool _showClose;

    public UpdateProgressForm(int? dpiOverride = null)
    {
        _dpiOverride = dpiOverride;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Night;
        DoubleBuffered = true;
        Font = _buttonFont;
        FormBorderStyle = FormBorderStyle.None;
        KeyPreview = true;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        WindowThemeHelpers.RevealWhenPainted(this);
        StartPosition = FormStartPosition.Manual;
        Text = "UsageAI update";
        TopMost = true;

        _primary = new Button { Font = _primaryFont, Visible = false };
        _primary.Click += (_, _) => PrimaryRequested?.Invoke(this, EventArgs.Empty);
        _close = new Button { Text = "Close", Visible = false };
        _close.Click += (_, _) => Close();
        Controls.AddRange(new Control[] { _primary, _close });

        _dismissTimer = new System.Windows.Forms.Timer { Interval = (int)DismissAfter.TotalMilliseconds };
        _dismissTimer.Tick += (_, _) =>
        {
            // Never pulled away from under the pointer.
            if (!Bounds.Contains(Cursor.Position))
            {
                Close();
            }
        };

        Theme.Changed += OnThemeChanged;
        ApplyTheme();
        ApplyLayout();
    }

    /// <summary>Raised by the action button: What's new once installed, the release page after a failure.</summary>
    public event EventHandler? PrimaryRequested;

    public UpdateStage Stage { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Status { get; private set; } = string.Empty;

    /// <summary>The share of the bar the current stage fills, 0 to 1.</summary>
    public float Progress { get; private set; }

    /// <summary>The action button's label, or null while it is hidden.</summary>
    public string? PrimaryText => _showPrimary ? _primary.Text : null;

    internal bool ClosesItself => _dismissTimer.Enabled;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            // A tool window stays out of Alt+Tab, as a notification does.
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow;
            return parameters;
        }
    }

    public void ShowDownloading(string version, double fraction)
    {
        // Download progress is posted, so a late report must not undo a later stage.
        if (Stage != UpdateStage.Downloading)
        {
            return;
        }

        var share = Math.Clamp(fraction, 0, 1);
        Apply(
            UpdateStage.Downloading,
            $"Updating to UsageAI {version}",
            $"Downloading… {(int)Math.Floor(share * 100)}%",
            DownloadStart + (DownloadEnd - DownloadStart) * (float)share,
            primaryText: null);
    }

    public void ShowStarting(string version) => Apply(
        UpdateStage.Starting,
        $"Updating to UsageAI {version}",
        "Verified. Waiting for Windows to start the installer…",
        StartingProgress,
        primaryText: null);

    public void ShowInstalling(string version) => Apply(
        UpdateStage.Installing,
        $"Updating to UsageAI {version}",
        "Installing. UsageAI restarts in a moment.",
        HandoffProgress,
        primaryText: null);

    /// <summary>
    /// The reopened copy's confirmation. The bar starts where the closing copy left it and
    /// fills, and the message closes itself unless the pointer rests on it.
    /// </summary>
    public void ShowInstalled(string version, bool offerWhatsNew)
    {
        if (!Visible)
        {
            // The reopened copy's bar is first drawn where the closing copy left it.
            _motion.Value(this, "bar", HandoffProgress);
        }

        Apply(
            UpdateStage.Installed,
            "UsageAI updated",
            $"Version {version} is installed and running.",
            1F,
            offerWhatsNew ? "What's new" : null);
        _dismissTimer.Start();
    }

    /// <summary>Setup succeeded but did not replace this copy — it runs from another folder.</summary>
    public void ShowInstalledNotRunning(string version) => Apply(
        UpdateStage.InstalledNotRunning,
        "Update installed",
        $"UsageAI {version} is installed and starts the next time UsageAI does.",
        1F,
        primaryText: null);

    public void ShowFailed(string version, string reason) => Apply(
        UpdateStage.Failed,
        $"The update to {version} didn't finish",
        reason,
        Math.Max(Progress, DownloadStart),
        "Release page");

    /// <summary>
    /// Shows the window in the corner of the screen the taskbar is on, away from the taskbar,
    /// without taking focus from whatever the user is doing.
    /// </summary>
    public void ShowInTrayCorner()
    {
        _screen = Screen.PrimaryScreen ?? Screen.FromPoint(Cursor.Position);
        if (!Visible && _dpiOverride is null)
        {
            // Measured on the monitor it opens on, as the popup is (see MoveOntoScreen there).
            _ = Handle;
            Location = _screen.WorkingArea.Location;
        }

        ApplyLayout();
        PlaceInCorner();
        if (!Visible)
        {
            Show();
        }
    }

    /// <summary>The window's position for a screen: the corner beside the taskbar's far end.</summary>
    internal static Point CornerFor(Rectangle bounds, Rectangle workingArea, Size size, int margin)
    {
        var x = workingArea.Left > bounds.Left
            ? workingArea.Left + margin
            : workingArea.Right - size.Width - margin;
        var y = workingArea.Top > bounds.Top
            ? workingArea.Top + margin
            : workingArea.Bottom - size.Height - margin;
        return new Point(x, y);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowThemeHelpers.ApplyWindowFrame(this);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyLayout();
        PlaceInCorner();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape && IsFinished)
        {
            Close();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var scale = Scale();
        EnsureFonts();
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var mark = new RectangleF(scale[16], scale[16], scale.Exact(30), scale.Exact(30));
        if (Stage == UpdateStage.Failed)
        {
            PromptDialog.PaintWarningMark(graphics, mark, scale);
        }
        else
        {
            PromptDialog.PaintLogoMark(graphics, mark, scale);
        }

        DrawingHelpers.DrawText(
            graphics,
            Title,
            _titleFont!,
            Theme.Text,
            _titleBounds,
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        DrawingHelpers.DrawText(
            graphics,
            Status,
            _statusFont!,
            Theme.TextSecondary,
            _statusBounds,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

        var shown = _motion.Value(this, "bar", Progress, _barBounds);
        var fill = Stage == UpdateStage.Failed ? Theme.Warning : Theme.Accent;
        DrawingHelpers.DrawCapacityMeter(graphics, _barBounds, shown * 100F, fill, Theme.Track);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnThemeChanged;
            _dismissTimer.Dispose();
            _motion.Dispose();
            _titleFont?.Dispose();
            _statusFont?.Dispose();
            _buttonFont.Dispose();
            _primaryFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private bool IsFinished => Stage is UpdateStage.Installed or UpdateStage.InstalledNotRunning or UpdateStage.Failed;

    private void Apply(UpdateStage stage, string title, string status, float progress, string? primaryText)
    {
        Stage = stage;
        Title = title;
        Status = status;
        Progress = Math.Clamp(progress, 0F, 1F);
        AccessibleName = title;
        AccessibleDescription = status;
        _primary.Text = primaryText ?? string.Empty;
        _showPrimary = primaryText is not null;
        _showClose = IsFinished;
        _primary.Visible = _showPrimary;
        _close.Visible = _showClose;
        ApplyLayout();
        if (Visible)
        {
            PlaceInCorner();
        }

        Invalidate();
    }

    /// <summary>
    /// Sizes the window for its DPI. The width is fixed and every height follows from it, so the
    /// window is measured before it is placed.
    /// </summary>
    private void ApplyLayout()
    {
        var scale = Scale();
        EnsureFonts();
        var width = scale[WidthBaseline];
        var textLeft = scale[60];
        var textWidth = width - textLeft - scale[18];
        var titleHeight = TextRenderer.MeasureText("Ag", _titleFont!).Height;
        _titleBounds = new Rectangle(textLeft, scale[15], textWidth, titleHeight);
        var statusHeight = TextRenderer.MeasureText(
            Status.Length > 0 ? Status : "Ag",
            _statusFont!,
            new Size(textWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Height;
        _statusBounds = new Rectangle(textLeft, _titleBounds.Bottom + scale[3], textWidth, statusHeight);
        _barBounds = new Rectangle(textLeft, _statusBounds.Bottom + scale[10], textWidth, Math.Max(2, scale[4]));
        var height = _barBounds.Bottom + scale[18];
        if (_showPrimary || _showClose)
        {
            var buttonTop = _barBounds.Bottom + scale[16];
            var buttonHeight = scale[28];
            var right = width - scale[18];
            if (_showClose)
            {
                _close.Size = new Size(Math.Max(scale[76], TextRenderer.MeasureText(_close.Text, _close.Font).Width + scale[24]), buttonHeight);
                _close.Location = new Point(right - _close.Width, buttonTop);
                right = _close.Left - scale[8];
            }

            if (_showPrimary)
            {
                _primary.Size = new Size(Math.Max(scale[96], TextRenderer.MeasureText(_primary.Text, _primary.Font).Width + scale[24]), buttonHeight);
                _primary.Location = new Point(right - _primary.Width, buttonTop);
            }

            height = buttonTop + buttonHeight + scale[16];
        }

        ClientSize = new Size(width, Math.Max(height, scale[16] + scale[30] + scale[16]));
    }

    private void PlaceInCorner()
    {
        if (_screen is null || _dpiOverride is not null)
        {
            return;
        }

        Location = CornerFor(_screen.Bounds, _screen.WorkingArea, Size, Scale()[12]);
    }

    /// <summary>The painted fonts, rebuilt whenever the window's DPI changes.</summary>
    private void EnsureFonts()
    {
        var dpi = _dpiOverride ?? LayoutScale.DpiOf(this);
        if (_titleFont is not null && _fontDpi == dpi)
        {
            return;
        }

        _titleFont?.Dispose();
        _statusFont?.Dispose();
        _titleFont = Typography.Display(11F, dpi);
        _statusFont = Typography.Text(9F, dpi);
        _fontDpi = dpi;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed && !Disposing)
        {
            ApplyTheme();
            ApplyLayout();
        }
    }

    private void ApplyTheme()
    {
        BackColor = Theme.Night;
        ForeColor = Theme.Text;
        PromptDialog.StyleButton(_primary, primary: true);
        PromptDialog.StyleButton(_close, primary: false);
        WindowThemeHelpers.ApplyWindowFrame(this);
        Invalidate(invalidateChildren: true);
    }

    private LayoutScale Scale() => _dpiOverride is { } dpi ? new LayoutScale(dpi) : new LayoutScale(this);
}
