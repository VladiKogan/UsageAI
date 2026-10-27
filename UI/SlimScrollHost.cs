using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace UsageAI.UI;

/// <summary>
/// Hosts a scrolling panel behind a slim, Windows 11-style scrollbar. The system scrollbar can be
/// themed only light or dark, and its full-width track ran the height of every scrolling window
/// even when the content overflowed by a few pixels. The panel keeps its own scrolling — the
/// wheel, the keyboard, scrolling a focused control into view, and every AutoScrollPosition the
/// windows set — and is simply made wider than the host by the system scrollbar's width, so
/// that scrollbar falls outside the visible area. A thin rounded thumb is drawn over the right
/// edge instead, with no track until it is pointed at, and nothing at all when the content fits.
/// High Contrast shows the system scrollbar as it is.
/// </summary>
internal sealed class SlimScrollHost : Control
{
    /// <summary>The strip at the right edge that the slim scrollbar occupies, at 96 DPI.</summary>
    internal const int GutterWidth = 12;

    private readonly ScrollableControl _target;
    private readonly SlimScrollBar _bar;
    private readonly int? _dpiOverride;
    private (int Offset, int Content, int Viewport) _last;
    private bool _ready;

    public SlimScrollHost(ScrollableControl target, int? dpiOverride = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
        _dpiOverride = dpiOverride;
        target.Dock = DockStyle.None;
        _bar = new SlimScrollBar(this) { Visible = false };
        // Laid out only once built: adding the panel lays out the host, and resizing the panel
        // then runs its owner's handlers before the owner has stored this host.
        SuspendLayout();
        Controls.Add(target);
        Controls.Add(_bar);
        _bar.BringToFront();
        ResumeLayout(performLayout: false);
        _ready = true;
        target.ClientSizeChanged += OnTargetChanged;
        target.Scroll += OnTargetChanged;
        target.MouseWheel += OnTargetChanged;
        target.Layout += OnTargetLayout;
        target.BackColorChanged += OnTargetBackColorChanged;
        Theme.Changed += OnThemeChanged;
        // A catch-all for scrolls no event reports, such as a position set in code: cheap, since
        // it compares three numbers and repaints only when one changed.
        Application.Idle += OnIdle;
    }

    internal ScrollableControl Target => _target;

    internal SlimScrollBar Bar => _bar;

    /// <summary>Whether the system scrollbar is shown instead: in High Contrast it is.</summary>
    internal static bool UsesSystemScrollBar => Theme.IsHighContrast;

    /// <summary>
    /// How much of the host's width a scrolling view gives up: the slim gutter, or the system
    /// scrollbar in High Contrast. Content sized to the host should subtract this while it scrolls.
    /// </summary>
    internal int ScrollInset => UsesSystemScrollBar
        ? SystemInformation.GetVerticalScrollBarWidthForDpi(Dpi)
        : Scale()[GutterWidth];

    /// <summary>
    /// The width content can take without overflowing the panel: the host's width, less the slim
    /// gutter while it scrolls. Until the panel has been widened past the system scrollbar — the
    /// moment scrolling switches on — that scrollbar still takes its full width, and content
    /// sized wider than what is left gained a horizontal scrollbar; the widening lays out again.
    /// </summary>
    internal int ContentWidth(bool scrolling)
    {
        if (!scrolling)
        {
            return Width;
        }

        var native = SystemInformation.GetVerticalScrollBarWidthForDpi(Dpi);
        var widened = !UsesSystemScrollBar && _target.Width >= Width + native;
        return Width - (widened ? ScrollInset : Math.Max(native, ScrollInset));
    }

    internal int Offset => -_target.AutoScrollPosition.Y;

    internal int ContentHeight => _target.DisplayRectangle.Height;

    internal int ViewportHeight => _target.ClientSize.Height;

    internal bool CanScroll => _target.VerticalScroll.Visible && ContentHeight > ViewportHeight;

    /// <summary>Scrolls the panel so <paramref name="offset"/> pixels of content are above the view.</summary>
    internal void ScrollTo(int offset)
    {
        var maximum = Math.Max(0, ContentHeight - ViewportHeight);
        _target.AutoScrollPosition = new Point(-_target.AutoScrollPosition.X, Math.Clamp(offset, 0, maximum));
        Sync();
    }

    /// <summary>Re-reads the panel's scroll state and repaints the bar if it changed.</summary>
    internal void Sync()
    {
        var state = (Offset, ContentHeight, ViewportHeight);
        var visible = !UsesSystemScrollBar && CanScroll;
        if (_bar.Visible != visible)
        {
            _bar.Visible = visible;
        }

        if (state != _last)
        {
            _last = state;
            _bar.Invalidate();
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        Arrange();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.Idle -= OnIdle;
            Theme.Changed -= OnThemeChanged;
            _target.ClientSizeChanged -= OnTargetChanged;
            _target.Scroll -= OnTargetChanged;
            _target.MouseWheel -= OnTargetChanged;
            _target.Layout -= OnTargetLayout;
            _target.BackColorChanged -= OnTargetBackColorChanged;
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Sizes the panel: as wide as the host, plus the system scrollbar's width while it shows,
    /// so that scrollbar sits just past the host's right edge where it is clipped away.
    /// </summary>
    private void Arrange()
    {
        if (!_ready)
        {
            return;
        }

        var hidden = !UsesSystemScrollBar && _target.VerticalScroll.Visible
            ? SystemInformation.GetVerticalScrollBarWidthForDpi(Dpi)
            : 0;
        var bounds = new Rectangle(0, 0, Width + hidden, Height);
        if (_target.Bounds != bounds)
        {
            _target.Bounds = bounds;
        }

        var gutter = Scale()[GutterWidth];
        _bar.Bounds = new Rectangle(Math.Max(0, Width - gutter), 0, gutter, Height);
        Sync();
    }

    private void OnTargetChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed)
        {
            Arrange();
        }
    }

    private void OnTargetLayout(object? sender, LayoutEventArgs e)
    {
        // The system scrollbar appears or goes away as content grows or shrinks; the panel is
        // widened or narrowed after this layout pass rather than in the middle of it.
        if (IsHandleCreated && !IsDisposed)
        {
            BeginInvoke(() =>
            {
                if (!IsDisposed)
                {
                    Arrange();
                }
            });
        }
    }

    private void OnTargetBackColorChanged(object? sender, EventArgs e) => _bar.Invalidate();

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed)
        {
            Arrange();
            _bar.Invalidate();
        }
    }

    private void OnIdle(object? sender, EventArgs e)
    {
        if (!IsDisposed && Visible && IsHandleCreated)
        {
            Sync();
        }
    }

    private int Dpi => _dpiOverride ?? LayoutScale.DpiOf(this);

    private LayoutScale Scale() => new(Dpi);

    /// <summary>
    /// The slim bar itself: a rounded thumb, thin until pointed at, on the host's background, with
    /// a faint track only while hovered. It can be dragged, clicked above or below the thumb to
    /// page, and passes the mouse wheel on to the panel.
    /// </summary>
    internal sealed class SlimScrollBar : Control
    {
        private const int WM_MOUSEWHEEL = 0x020A;
        private readonly SlimScrollHost _host;
        private bool _hover;
        private int? _dragFrom;
        private int _dragOffset;

        public SlimScrollBar(SlimScrollHost host)
        {
            _host = host;
            DoubleBuffered = true;
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            AccessibleRole = AccessibleRole.ScrollBar;
            AccessibleName = "Vertical scroll bar";
        }

        internal bool IsHovered => _hover;

        internal bool IsDragging => _dragFrom is not null;

        /// <summary>The thumb's rectangle within the bar for the host's current scroll state.</summary>
        internal Rectangle ThumbBounds
        {
            get
            {
                var scale = _host.Scale();
                var content = Math.Max(1, _host.ContentHeight);
                var viewport = Math.Max(1, _host.ViewportHeight);
                var inset = scale[3];
                var track = Math.Max(1, Height - 2 * inset);
                var length = Math.Clamp((int)Math.Round(track * (double)viewport / content), scale[24], track);
                var travel = Math.Max(0, track - length);
                var maximum = Math.Max(1, content - viewport);
                var top = inset + (int)Math.Round(travel * Math.Clamp(_host.Offset / (double)maximum, 0, 1));
                var width = _hover || IsDragging ? scale[8] : scale[4];
                return new Rectangle(Width - width - scale[2], top, width, length);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var background = _host.Target.BackColor;
            e.Graphics.Clear(background);
            if (!_host.CanScroll)
            {
                return;
            }

            var scale = _host.Scale();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var thumb = ThumbBounds;
            if (_hover || IsDragging)
            {
                var track = new Rectangle(thumb.Left, scale[3], thumb.Width, Math.Max(1, Height - 2 * scale[3]));
                using var trackBrush = new SolidBrush(Theme.Blend(Theme.Text, background, 0.06));
                using var trackPath = DrawingHelpers.RoundedRectangle(track, track.Width / 2F);
                e.Graphics.FillPath(trackBrush, trackPath);
            }

            var amount = IsDragging ? 0.62 : _hover ? 0.48 : 0.32;
            using var thumbBrush = new SolidBrush(Theme.Blend(Theme.Text, background, amount));
            using var thumbPath = DrawingHelpers.RoundedRectangle(thumb, thumb.Width / 2F);
            e.Graphics.FillPath(thumbBrush, thumbPath);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !_host.CanScroll)
            {
                return;
            }

            var thumb = ThumbBounds;
            if (e.Y >= thumb.Top && e.Y < thumb.Bottom)
            {
                _dragFrom = e.Y;
                _dragOffset = _host.Offset;
                Capture = true;
            }
            else
            {
                // Above or below the thumb pages by one view, as the system scrollbar does.
                var page = Math.Max(1, _host.ViewportHeight - _host.Scale()[24]);
                _host.ScrollTo(_host.Offset + (e.Y < thumb.Top ? -page : page));
            }

            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragFrom is not { } from)
            {
                return;
            }

            var thumb = ThumbBounds;
            var travel = Math.Max(1, Height - 2 * _host.Scale()[3] - thumb.Height);
            var scrollable = Math.Max(0, _host.ContentHeight - _host.ViewportHeight);
            _host.ScrollTo(_dragOffset + (int)Math.Round((e.Y - from) * (double)scrollable / travel));
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragFrom = null;
            Capture = false;
            Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            // The wheel over the bar scrolls the panel exactly as it would over the content.
            if (m.Msg == WM_MOUSEWHEEL && _host.Target.IsHandleCreated)
            {
                _ = SendMessage(_host.Target.Handle, m.Msg, m.WParam, m.LParam);
                _host.Sync();
                return;
            }

            base.WndProc(ref m);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    }
}
