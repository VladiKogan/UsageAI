using System.Drawing.Drawing2D;

namespace UsageAI.UI;

internal enum IconGlyph
{
    Refresh,
    Settings,
}

/// <summary>
/// A quiet, square button that draws a stroke glyph instead of a label: the popup's header
/// actions. It stays a real <see cref="Button"/>, so keyboard activation, focus and the
/// screen-reader role come from WinForms; only the painting is custom.
/// </summary>
internal sealed class IconButton : Button
{
    private readonly IconGlyph _glyph;
    private bool _hovered;
    private bool _pressed;
    private float _rotation;

    public IconButton(IconGlyph glyph, string accessibleName)
    {
        _glyph = glyph;
        AccessibleName = accessibleName;
        Text = accessibleName;
        Cursor = Cursors.Hand;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    /// <summary>
    /// The DPI to draw at instead of the control's own: previews and DPI tests render the whole
    /// window at one explicit DPI, and the glyph must match the header column it sits in.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int? DpiOverride { get; init; }

    /// <summary>Turns the glyph, in degrees: the refresh arrow spins while a refresh runs.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float Rotation
    {
        get => _rotation;
        set
        {
            if (Math.Abs(_rotation - value) < 0.01F)
            {
                return;
            }

            _rotation = value;
            Invalidate();
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        base.OnMouseDown(mevent);
        _pressed = mevent.Button == MouseButtons.Left;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        base.OnMouseUp(mevent);
        _pressed = false;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var graphics = pevent.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var background = Parent?.BackColor ?? Theme.Night;
        graphics.Clear(background);
        var scale = DpiOverride is { } dpi ? new LayoutScale(dpi) : new LayoutScale(this);
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var highContrast = Theme.IsHighContrast;

        // Hover and press are a background tint only: a scaled press repaints the whole header.
        if (Enabled && (_hovered || _pressed))
        {
            using var hover = DrawingHelpers.RoundedRectangle(bounds, scale.Exact(6));
            // High Contrast has no tints, so hover uses the system highlight pair instead.
            using var brush = new SolidBrush(highContrast
                ? SystemColors.Highlight
                : _pressed
                    ? Theme.Blend(Theme.Text, background, 0.14)
                    : Theme.Blend(Theme.Text, background, 0.08));
            graphics.FillPath(brush, hover);
        }

        if (Focused && ShowFocusCues)
        {
            using var focus = DrawingHelpers.RoundedRectangle(Rectangle.Inflate(bounds, -1, -1), scale.Exact(6));
            using var pen = new Pen(Theme.Accent, Math.Max(1F, scale.Exact(1.5F)));
            graphics.DrawPath(pen, focus);
        }

        // Secondary text rather than the muted tone: the glyphs are thin strokes, and in the muted
        // tone the refresh arrow read as a smudge until hovered.
        var color = Enabled ? Theme.TextSecondary : Theme.Blend(Theme.Muted, background, 0.5);
        if (Enabled && (_hovered || Focused))
        {
            color = Theme.Text;
        }

        if (highContrast)
        {
            color = !Enabled
                ? SystemColors.GrayText
                : _hovered || _pressed ? SystemColors.HighlightText : SystemColors.ControlText;
        }

        var size = scale.Exact(16);
        var glyph = new RectangleF((Width - size) / 2F, (Height - size) / 2F, size, size);
        using var stroke = new Pen(color, Math.Max(1.2F, scale.Exact(1.5F)))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        DrawGlyph(graphics, _glyph, glyph, stroke, _rotation);
    }

    internal static void DrawGlyph(Graphics graphics, IconGlyph glyph, RectangleF box, Pen stroke, float rotation)
    {
        var state = graphics.Save();
        try
        {
            var center = new PointF(box.Left + box.Width / 2F, box.Top + box.Height / 2F);
            if (Math.Abs(rotation) > 0.01F)
            {
                graphics.TranslateTransform(center.X, center.Y);
                graphics.RotateTransform(rotation);
                graphics.TranslateTransform(-center.X, -center.Y);
            }

            // Glyphs are drawn on a 16-unit grid, the same one the mock-ups use.
            float U(float units) => units * box.Width / 16F;
            switch (glyph)
            {
                case IconGlyph.Refresh:
                    // A clockwise arc with a gap at the top right, ending at twelve o'clock in a
                    // solid arrowhead that points on into the gap: the arrow is part of the arc,
                    // not a separate corner beside it, so it reads at tray-header size.
                    var arc = new RectangleF(box.Left + U(2.5F), box.Top + U(2.5F), U(11), U(11));
                    graphics.DrawArc(stroke, arc, -30F, 300F);
                    var end = new PointF(box.Left + U(8), box.Top + U(2.5F));
                    var head = new[]
                    {
                        new PointF(end.X + U(2.6F), end.Y),
                        new PointF(end.X - U(0.6F), end.Y - U(2.3F)),
                        new PointF(end.X - U(0.6F), end.Y + U(2.3F)),
                    };
                    using (var fill = new SolidBrush(stroke.Color))
                    {
                        graphics.FillPolygon(fill, head);
                    }

                    // Outlined with the same round-joined stroke, so its corners match the arc.
                    graphics.DrawPolygon(stroke, head);
                    break;
                case IconGlyph.Settings:
                    graphics.DrawLine(stroke, box.Left + U(2.5F), box.Top + U(4.5F), box.Left + U(9.5F), box.Top + U(4.5F));
                    graphics.DrawLine(stroke, box.Left + U(12.5F), box.Top + U(4.5F), box.Left + U(13.5F), box.Top + U(4.5F));
                    graphics.DrawLine(stroke, box.Left + U(2.5F), box.Top + U(11.5F), box.Left + U(3.5F), box.Top + U(11.5F));
                    graphics.DrawLine(stroke, box.Left + U(6.5F), box.Top + U(11.5F), box.Left + U(13.5F), box.Top + U(11.5F));
                    graphics.DrawEllipse(stroke, box.Left + U(9.5F), box.Top + U(3), U(3), U(3));
                    graphics.DrawEllipse(stroke, box.Left + U(3.5F), box.Top + U(10), U(3), U(3));
                    break;
            }
        }
        finally
        {
            graphics.Restore(state);
        }
    }
}
