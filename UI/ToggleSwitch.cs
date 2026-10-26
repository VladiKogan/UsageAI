using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace UsageAI.UI;

/// <summary>
/// An on/off setting drawn as a Windows 11 toggle: the label on the left, the switch on the
/// right edge. It stays a <see cref="CheckBox"/>, so <see cref="CheckBox.Checked"/>, Space to
/// toggle, focus and the screen-reader check-box role are WinForms' own; only the painting is
/// custom. On is the accent fill with a light knob; off is an outline with a muted knob.
/// </summary>
internal sealed class ToggleSwitch : CheckBox
{
    private const int TrackWidth = 40;
    private const int TrackHeight = 20;
    private const int Knob = 12;

    private bool _hovered;

    public ToggleSwitch()
    {
        AutoSize = false;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    /// <summary>The DPI to draw at instead of the control's own, for previews and DPI tests.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int? DpiOverride { get; init; }

    /// <summary>A height that fits the label and the switch with even padding.</summary>
    internal int PreferredRowHeight => Math.Max(Scale()[32], Font.Height + Scale()[12]);

    protected override void OnMouseEnter(EventArgs eventargs)
    {
        base.OnMouseEnter(eventargs);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs eventargs)
    {
        base.OnMouseLeave(eventargs);
        _hovered = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var graphics = pevent.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var background = BackColor == Color.Transparent ? Parent?.BackColor ?? Theme.Night : BackColor;
        graphics.Clear(background);

        var scale = Scale();
        var highContrast = Theme.IsHighContrast;
        var track = new RectangleF(
            Width - scale.Exact(TrackWidth) - 1,
            (Height - scale.Exact(TrackHeight)) / 2F,
            scale.Exact(TrackWidth),
            scale.Exact(TrackHeight));

        DrawingHelpers.DrawText(
            graphics,
            Text,
            Font,
            Enabled ? (highContrast ? SystemColors.WindowText : Theme.Text) : (highContrast ? SystemColors.GrayText : Theme.Faint),
            // Labels draw with a few pixels of built-in padding; matching it lines the toggles'
            // text up with the captions of the rows around them.
            Rectangle.FromLTRB(scale[3], 0, Math.Max(scale[3] + 1, (int)track.Left - scale[12]), Height),
            TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

        using var trackPath = DrawingHelpers.RoundedRectangle(track, track.Height / 2F);
        var on = Checked;
        var accent = highContrast ? SystemColors.Highlight : Theme.Accent;
        if (on)
        {
            using var fill = new SolidBrush(Enabled ? (_hovered ? Theme.Blend(accent, Theme.Text, 0.9) : accent) : Theme.Track);
            graphics.FillPath(fill, trackPath);
        }
        else
        {
            using var outline = new Pen(highContrast ? SystemColors.WindowText : Theme.Muted, Math.Max(1F, scale.Exact(1)));
            if (_hovered && Enabled)
            {
                using var hover = new SolidBrush(Theme.Blend(Theme.Text, background, 0.06));
                graphics.FillPath(hover, trackPath);
            }

            graphics.DrawPath(outline, trackPath);
        }

        var knob = scale.Exact(Knob);
        var knobLeft = on ? track.Right - knob - scale.Exact(4) : track.Left + scale.Exact(4);
        using (var knobBrush = new SolidBrush(on
                   ? (highContrast ? SystemColors.HighlightText : Theme.OnAccent)
                   : (highContrast ? SystemColors.WindowText : Theme.Muted)))
        {
            graphics.FillEllipse(knobBrush, knobLeft, track.Top + (track.Height - knob) / 2F, knob, knob);
        }

        if (Focused && ShowFocusCues)
        {
            using var focus = DrawingHelpers.RoundedRectangle(RectangleF.Inflate(track, scale.Exact(3), scale.Exact(3)), track.Height / 2F + scale.Exact(3));
            using var pen = new Pen(accent, Math.Max(1F, scale.Exact(1.5F)));
            graphics.DrawPath(pen, focus);
        }
    }

    private LayoutScale Scale() => DpiOverride is { } dpi ? new LayoutScale(dpi) : new LayoutScale(this);
}
