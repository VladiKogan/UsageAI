using System.Drawing.Drawing2D;

namespace UsageAI.UI;

/// <summary>
/// A compact command button for a settings card: a rounded, raised face with an optional icon
/// from Windows' icon font beside its label. It stays a real <see cref="Button"/>, so keyboard
/// activation, focus and the screen-reader role come from WinForms; only the painting is custom.
/// It paints its own disabled state: a flat button's disabled text is the system's dark grey,
/// which on the dark theme read as black text on a black card rather than as unavailable.
/// </summary>
internal sealed class ActionButton : Button
{
    /// <summary>Icons in the Windows icon font (Segoe Fluent Icons, or MDL2 on Windows 10).</summary>
    internal static class Glyphs
    {
        public const string Up = "";
        public const string Down = "";
        public const string Rename = "";
        public const string SignIn = "";
        public const string Folder = "";
        public const string Locate = "";
        public const string Terminal = "";
        public const string Delete = "";
        public const string AddPerson = "";
        public const string Refresh = "";
    }

    private const float IconPoints = 9.5F;
    private bool _hovered;
    private bool _pressed;
    private Font? _iconFont;
    private int _iconDpi;

    public ActionButton(string text, string? glyph = null, bool destructive = false)
    {
        Text = text;
        Glyph = glyph;
        Destructive = destructive;
        AutoSize = true;
        Cursor = Cursors.Hand;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw,
            true);
    }

    public string? Glyph { get; }

    /// <summary>Drawn in the critical colour: the action removes something.</summary>
    public bool Destructive { get; }

    /// <summary>The DPI to measure and draw at instead of the window's: previews and DPI tests.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int? DpiOverride { get; init; }

    private LayoutScale Scale() => DpiOverride is { } dpi ? new LayoutScale(dpi) : new LayoutScale(this);

    private bool ShowsIcon => Glyph is not null && Typography.HasIcons;

    public override Size GetPreferredSize(Size proposedSize)
    {
        var scale = Scale();
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        var icon = ShowsIcon ? scale[16] + scale[6] : 0;
        return new Size(
            scale[12] + icon + text.Width + scale[12],
            Math.Max(scale[30], text.Height + scale[12]));
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var graphics = pevent.Graphics;
        var scale = Scale();
        var highContrast = Theme.IsHighContrast;
        var back = Parent?.BackColor ?? Theme.Surface;
        // The corners outside the rounded face show the row behind it, in every theme.
        graphics.Clear(back);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var active = Enabled && (_hovered || _pressed);
        Color fill;
        Color border;
        Color ink;
        if (highContrast)
        {
            fill = active ? SystemColors.Highlight : SystemColors.ButtonFace;
            border = Enabled ? SystemColors.ControlText : SystemColors.GrayText;
            ink = !Enabled ? SystemColors.GrayText : active ? SystemColors.HighlightText : SystemColors.ControlText;
        }
        else
        {
            var tint = Destructive ? Theme.CriticalText : Theme.Text;
            fill = !Enabled ? back
                : _pressed ? Theme.Blend(tint, Theme.SurfaceRaised, 0.14)
                : _hovered ? Theme.Blend(tint, Theme.SurfaceRaised, 0.08)
                : Theme.SurfaceRaised;
            border = Enabled && Destructive && _hovered ? Theme.Blend(Theme.CriticalText, Theme.Hairline, 0.5) : Theme.Hairline;
            // Unavailable reads dimmer than any label on the card, and still legible.
            ink = !Enabled ? Theme.Blend(Theme.Muted, back, 0.62)
                : Destructive ? Theme.CriticalText
                : Theme.Text;
        }

        var face = new RectangleF(0.5F, 0.5F, Width - 1F, Height - 1F);
        using (var path = DrawingHelpers.RoundedRectangle(face, scale.Exact(4)))
        {
            using var brush = new SolidBrush(fill);
            graphics.FillPath(brush, path);
            using var pen = new Pen(border, Math.Max(1F, scale.Exact(1)));
            graphics.DrawPath(pen, path);
        }

        if (Focused && ShowFocusCues)
        {
            var ring = RectangleF.Inflate(face, -scale.Exact(2), -scale.Exact(2));
            using var path = DrawingHelpers.RoundedRectangle(ring, scale.Exact(3));
            using var pen = new Pen(highContrast ? SystemColors.ControlText : Theme.Signal, Math.Max(1F, scale.Exact(1.5F)));
            if (highContrast)
            {
                pen.DashStyle = DashStyle.Dot;
            }

            graphics.DrawPath(pen, path);
        }

        var left = scale[12];
        if (ShowsIcon && IconFont() is { } icons)
        {
            DrawingHelpers.DrawText(
                graphics,
                Glyph!,
                icons,
                ink,
                new Rectangle(left, 0, scale[16], Height),
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            left += scale[16] + scale[6];
        }

        DrawingHelpers.DrawText(
            graphics,
            Text,
            Font,
            ink,
            Rectangle.FromLTRB(left, 0, Width - scale[8], Height),
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
            TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    /// <summary>The icon face for the DPI the button is drawn at, made again when that changes.</summary>
    private Font? IconFont()
    {
        var dpi = DpiOverride ?? LayoutScale.DpiOf(this);
        if (_iconFont is null || _iconDpi != dpi)
        {
            _iconFont?.Dispose();
            _iconFont = Typography.Icons(IconPoints, dpi);
            _iconDpi = dpi;
        }

        return _iconFont;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        if (mevent.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }

        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        _hovered = _hovered && Enabled;
        _pressed = false;
        // A disabled button keeps the arrow: the hand promised a click that would not happen.
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        // "Forget" and "Remove" swap on one button; its width follows the label.
        Parent?.PerformLayout(this, nameof(Text));
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _iconFont?.Dispose();
            _iconFont = null;
        }

        base.Dispose(disposing);
    }
}
