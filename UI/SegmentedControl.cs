using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace UsageAI.UI;

/// <summary>
/// A row of mutually exclusive options drawn as one pill, the selected one raised: the
/// dashboard's All / Metered / Important switch, and Settings' short choices. Every option is
/// visible at once, so changing view is one click instead of opening a list. Arrow keys, Home
/// and End move the selection; the control is a single tab stop like a radio group.
/// </summary>
internal sealed class SegmentedControl : Control
{
    private const int SegmentPadding = 12;
    private const int Inset = 2;
    private const int Radius = 6;

    private readonly string[] _options;
    private int _selectedIndex;
    private int _hoverIndex = -1;
    private Font? _font;
    private int _fontDpi;
    private int[]? _widths;

    public SegmentedControl(IReadOnlyList<string> options, int selectedIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Count == 0)
        {
            throw new ArgumentException("A segmented control needs at least one option.", nameof(options));
        }

        _options = options.ToArray();
        _selectedIndex = Math.Clamp(selectedIndex, 0, _options.Length - 1);
        DoubleBuffered = true;
        ResizeRedraw = true;
        TabStop = true;
        SetStyle(ControlStyles.Selectable, true);
        AccessibleRole = AccessibleRole.RadioButton;
        UpdateAccessibility();
    }

    public event EventHandler? SelectedIndexChanged;

    /// <summary>The DPI to draw at instead of the control's own, for previews and DPI tests.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int? DpiOverride { get; init; }

    public IReadOnlyList<string> Options => _options;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, _options.Length - 1);
            if (clamped == _selectedIndex)
            {
                return;
            }

            _selectedIndex = clamped;
            UpdateAccessibility();
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The width that fits every option's label with even padding.</summary>
    public int PreferredWidth => SegmentWidths().Sum() + 2 * Scale()[Inset];

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var target = e.KeyCode switch
        {
            Keys.Left => _selectedIndex - 1,
            Keys.Right => _selectedIndex + 1,
            Keys.Home => 0,
            Keys.End => _options.Length - 1,
            _ => _selectedIndex,
        };
        if (target != _selectedIndex)
        {
            SelectedIndex = target;
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = IndexAt(e.Location);
        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            Cursor = index >= 0 && index != _selectedIndex ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        Focus();
        var index = IndexAt(e.Location);
        if (index >= 0)
        {
            SelectedIndex = index;
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var scale = Scale();
        // BackColor is ambient: the window's, unless a host such as a settings card sets its own.
        var background = BackColor;
        graphics.Clear(background);
        var highContrast = Theme.IsHighContrast;

        var outer = new Rectangle(0, 0, Width - 1, Height - 1);
        DrawingHelpers.FillCard(
            graphics,
            outer,
            highContrast ? SystemColors.Window : Theme.Blend(Theme.Text, background, Theme.IsDark ? 0.04 : 0.05),
            highContrast ? SystemColors.WindowFrame : Theme.Hairline,
            scale.Exact(Radius));

        var font = FontForDpi();
        for (var index = 0; index < _options.Length; index++)
        {
            var segment = SegmentBounds(index);
            var selected = index == _selectedIndex;
            if (selected)
            {
                DrawingHelpers.FillCard(
                    graphics,
                    segment,
                    highContrast ? SystemColors.Highlight : Theme.SurfaceRaised,
                    highContrast ? SystemColors.Highlight : Theme.Hairline,
                    scale.Exact(Radius - Inset));
            }
            else if (index == _hoverIndex)
            {
                using var hover = DrawingHelpers.RoundedRectangle(segment, scale.Exact(Radius - Inset));
                using var brush = new SolidBrush(highContrast ? SystemColors.Window : Theme.Blend(Theme.Text, background, 0.07));
                graphics.FillPath(brush, hover);
            }

            var color = highContrast
                ? selected ? SystemColors.HighlightText : SystemColors.WindowText
                : selected ? Theme.Text : Theme.Muted;
            if (!Enabled)
            {
                color = highContrast ? SystemColors.GrayText : Theme.Faint;
            }

            DrawingHelpers.DrawText(
                graphics,
                _options[index],
                font,
                color,
                segment,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        if (Focused && ShowFocusCues)
        {
            using var focus = DrawingHelpers.RoundedRectangle(SegmentBounds(_selectedIndex), scale.Exact(Radius - Inset));
            using var pen = new Pen(Theme.Accent, Math.Max(1F, scale.Exact(1.5F)));
            graphics.DrawPath(pen, focus);
        }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Segments share the width in proportion to their labels, so a long label is never cut
    /// while a short one sits in a wide slot.
    /// </summary>
    internal Rectangle SegmentBounds(int index)
    {
        var inset = Scale()[Inset];
        var widths = SegmentWidths();
        var available = Math.Max(_options.Length, Width - 2 * inset);
        var total = Math.Max(1, widths.Sum());
        var left = inset;
        for (var i = 0; i < index; i++)
        {
            left += widths[i] * available / total;
        }

        var width = index == _options.Length - 1
            ? Width - inset - left
            : widths[index] * available / total;
        return new Rectangle(left, inset, Math.Max(1, width), Math.Max(1, Height - 2 * inset));
    }

    /// <summary>
    /// Each option's natural width, measured once per font: hover and paint ask for segment
    /// bounds many times, and the labels only change size with the DPI.
    /// </summary>
    private int[] SegmentWidths()
    {
        var font = FontForDpi();
        if (_widths is null)
        {
            var padding = 2 * Scale()[SegmentPadding];
            _widths = _options
                .Select(option => TextRenderer.MeasureText(option, font, Size.Empty, TextFormatFlags.NoPadding).Width + padding)
                .ToArray();
        }

        return _widths;
    }

    private int IndexAt(Point point)
    {
        for (var index = 0; index < _options.Length; index++)
        {
            if (SegmentBounds(index).Contains(point))
            {
                return index;
            }
        }

        return -1;
    }

    private void UpdateAccessibility() =>
        AccessibleDescription = $"{_options[_selectedIndex]} selected, {_selectedIndex + 1} of {_options.Length}. Use the arrow keys to change.";

    private LayoutScale Scale() => DpiOverride is { } dpi ? new LayoutScale(dpi) : new LayoutScale(this);

    private Font FontForDpi()
    {
        var dpi = DpiOverride ?? DeviceDpi;
        if (_font is null || _fontDpi != dpi)
        {
            _font?.Dispose();
            _font = Typography.Text(8.5F, dpi);
            _fontDpi = dpi;
            _widths = null;
        }

        return _font;
    }
}
