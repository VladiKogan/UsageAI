using System.Drawing.Drawing2D;

namespace UsageAI.UI;

/// <summary>
/// A <see cref="NumericUpDown"/> whose spin buttons follow the app's palette. Windows draws those
/// buttons with the light visual style whatever the theme, so on a dark card they showed as a
/// white block. Everything else — typing, the arrow keys, the mouse wheel, validation and
/// accessibility — stays the standard control's. High Contrast keeps the system's own buttons.
/// </summary>
internal sealed class ThemedNumericUpDown : NumericUpDown
{
    private readonly Control _buttons;
    private int _hoverHalf = -1;
    private bool _pressed;

    public ThemedNumericUpDown()
    {
        // The first child is the spin-button strip; the second is the text box.
        _buttons = Controls[0];
        _buttons.Paint += OnButtonsPaint;
        _buttons.MouseMove += OnButtonsMouseMove;
        _buttons.MouseLeave += OnButtonsMouseLeave;
        _buttons.MouseDown += OnButtonsMouseDown;
        _buttons.MouseUp += OnButtonsMouseUp;
    }

    /// <summary>Which half the pointer is over: 0 the up button, 1 the down button, -1 neither.</summary>
    internal int HoverHalf => _hoverHalf;

    internal Control SpinButtons => _buttons;

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        _buttons.Invalidate();
    }

    // A theme change recolours the field; the buttons take their surface from it.
    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        _buttons.Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _buttons.Paint -= OnButtonsPaint;
            _buttons.MouseMove -= OnButtonsMouseMove;
            _buttons.MouseLeave -= OnButtonsMouseLeave;
            _buttons.MouseDown -= OnButtonsMouseDown;
            _buttons.MouseUp -= OnButtonsMouseUp;
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Paints over the system's buttons once they are drawn: the field's own surface, a quiet
    /// fill under the half being pointed at or pressed, and a chevron in each half.
    /// </summary>
    internal void PaintButtons(Graphics graphics, Rectangle bounds)
    {
        if (Theme.IsHighContrast || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var surface = BackColor;
        using (var background = new SolidBrush(surface))
        {
            graphics.FillRectangle(background, bounds);
        }

        var upper = new Rectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height / 2);
        var lower = Rectangle.FromLTRB(bounds.Left, upper.Bottom, bounds.Right, bounds.Bottom);
        if (_hoverHalf >= 0 && Enabled)
        {
            using var highlight = new SolidBrush(Theme.Blend(Theme.Text, surface, _pressed ? 0.16 : 0.08));
            graphics.FillRectangle(highlight, _hoverHalf == 0 ? upper : lower);
        }

        var scale = new LayoutScale(this);
        var previous = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Enabled ? Theme.TextSecondary : Theme.Faint, Math.Max(1F, scale.Exact(1.4F)))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        DrawChevron(graphics, pen, upper, scale, pointingUp: true);
        DrawChevron(graphics, pen, lower, scale, pointingUp: false);
        graphics.SmoothingMode = previous;
    }

    private static void DrawChevron(Graphics graphics, Pen pen, Rectangle half, LayoutScale scale, bool pointingUp)
    {
        var halfWidth = Math.Min(scale.Exact(3.5F), half.Width / 3F);
        var halfHeight = Math.Min(scale.Exact(2F), half.Height / 3F);
        var centerX = half.Left + half.Width / 2F;
        var centerY = half.Top + half.Height / 2F;
        var tip = pointingUp ? centerY - halfHeight : centerY + halfHeight;
        var ends = pointingUp ? centerY + halfHeight : centerY - halfHeight;
        graphics.DrawLines(pen, new[]
        {
            new PointF(centerX - halfWidth, ends),
            new PointF(centerX, tip),
            new PointF(centerX + halfWidth, ends),
        });
    }

    private void OnButtonsPaint(object? sender, PaintEventArgs e) => PaintButtons(e.Graphics, _buttons.ClientRectangle);

    private void OnButtonsMouseMove(object? sender, MouseEventArgs e)
    {
        var half = e.Y < _buttons.Height / 2 ? 0 : 1;
        if (half != _hoverHalf)
        {
            _hoverHalf = half;
            _buttons.Invalidate();
        }
    }

    private void OnButtonsMouseLeave(object? sender, EventArgs e)
    {
        _hoverHalf = -1;
        _pressed = false;
        _buttons.Invalidate();
    }

    private void OnButtonsMouseDown(object? sender, MouseEventArgs e)
    {
        _pressed = e.Button == MouseButtons.Left;
        _buttons.Invalidate();
    }

    private void OnButtonsMouseUp(object? sender, MouseEventArgs e)
    {
        _pressed = false;
        _buttons.Invalidate();
    }
}
