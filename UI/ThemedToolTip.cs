namespace UsageAI.UI;

/// <summary>
/// A tooltip painted in the app's palette. The classic WinForms tooltip is a pale yellow-white
/// box whatever the theme, which on the dark popup read as a stray window. High Contrast keeps
/// the system's own tooltip colours.
/// </summary>
internal sealed class ThemedToolTip : IDisposable
{
    private const int Padding = 8;

    private readonly ToolTip _toolTip;
    private Font? _font;
    private int _fontDpi;
    private string _currentText = string.Empty;

    public ThemedToolTip()
    {
        _toolTip = new ToolTip
        {
            InitialDelay = 250,
            ReshowDelay = 100,
            ShowAlways = true,
            OwnerDraw = true,
        };
        _toolTip.Popup += OnPopup;
        _toolTip.Draw += OnDraw;
    }

    /// <summary>
    /// Shows <paramref name="text"/> beneath <paramref name="target"/> (owner coordinates) with
    /// its right edge on the target's, so a tooltip for an indicator at a window's right edge
    /// opens inward instead of past it. It flips above the target when there is no room below —
    /// the popup sits on the taskbar — and is kept on the target's screen either way.
    /// </summary>
    public void ShowRightAligned(string text, Control owner, Rectangle target)
    {
        // Popup reports no text, so the size is taken from the text being shown now.
        _currentText = text;
        var size = Measure(text, owner.DeviceDpi);
        var screenTarget = owner.RectangleToScreen(target);
        var workingArea = Screen.FromRectangle(screenTarget).WorkingArea;
        var left = Math.Clamp(
            screenTarget.Right - size.Width,
            workingArea.Left,
            Math.Max(workingArea.Left, workingArea.Right - size.Width));
        var top = screenTarget.Bottom + size.Height <= workingArea.Bottom
            ? screenTarget.Bottom
            : Math.Max(workingArea.Top, screenTarget.Top - size.Height);
        var origin = owner.PointToScreen(Point.Empty);
        _toolTip.Show(text, owner, left - origin.X, top - origin.Y);
    }

    public void Hide(Control owner) => _toolTip.Hide(owner);

    public void Dispose()
    {
        _toolTip.Dispose();
        _font?.Dispose();
    }

    private Size Measure(string text, int dpi)
    {
        var font = FontFor(dpi);
        var scale = new LayoutScale(dpi);
        var measured = TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPrefix);
        return new Size(measured.Width + 2 * scale[Padding], measured.Height + 2 * scale[Padding]);
    }

    private Font FontFor(int dpi)
    {
        if (_font is null || _fontDpi != dpi)
        {
            _font?.Dispose();
            _font = Typography.Text(8.5F, dpi);
            _fontDpi = dpi;
        }

        return _font;
    }

    private void OnPopup(object? sender, PopupEventArgs e)
    {
        var dpi = e.AssociatedControl?.DeviceDpi ?? LayoutScale.BaselineDpi;
        e.ToolTipSize = Measure(_currentText, dpi);
    }

    private void OnDraw(object? sender, DrawToolTipEventArgs e)
    {
        var dpi = e.AssociatedControl?.DeviceDpi ?? LayoutScale.BaselineDpi;
        var scale = new LayoutScale(dpi);
        var highContrast = Theme.IsHighContrast;
        var background = highContrast ? SystemColors.Info : Theme.SurfaceRaised;
        var foreground = highContrast ? SystemColors.InfoText : Theme.Text;
        var border = highContrast ? SystemColors.WindowFrame : Theme.Hairline;
        using (var fill = new SolidBrush(background))
        {
            e.Graphics.FillRectangle(fill, e.Bounds);
        }

        using (var pen = new Pen(border))
        {
            e.Graphics.DrawRectangle(pen, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);
        }

        TextRenderer.DrawText(
            e.Graphics,
            e.ToolTipText,
            FontFor(dpi),
            Rectangle.Inflate(e.Bounds, -scale[Padding], -scale[Padding]),
            foreground,
            TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top);
    }
}
