using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace UsageAI.UI;

/// <summary>
/// The vertical list of Settings' pages — General, Alerts, … — with the one shown marked by a
/// raised fill and an accent pill, the Windows 11 navigation look. Choosing a page raises
/// <see cref="SectionChosen"/> and the host shows it. Up and Down move between pages. Screen
/// readers meet it as a tab list, one tab per page ("Accounts, tab 5 of 6").
/// </summary>
internal sealed class SectionNav : Control
{
    private const int ItemHeight = 36;
    private const int ItemGap = 2;
    private const int Pill = 3;

    private readonly string[] _sections;
    private int _selectedIndex;
    private int _hoverIndex = -1;
    private Font? _font;
    private Font? _iconFont;
    private int _fontDpi;

    /// <summary>
    /// One icon per page, as code points in the Windows icon font, drawn before each name. Left
    /// out when no icon font is installed, so no page is marked with an empty box.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal IReadOnlyList<string>? Glyphs { get; init; }

    private bool ShowsGlyphs => Glyphs is { } glyphs && glyphs.Count == _sections.Length && Typography.HasIcons;

    public SectionNav(IReadOnlyList<string> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        _sections = sections.ToArray();
        DoubleBuffered = true;
        ResizeRedraw = true;
        TabStop = true;
        SetStyle(ControlStyles.Selectable, true);
        AccessibleRole = AccessibleRole.PageTabList;
        AccessibleName = "Settings pages";
        UpdateAccessibility();
    }

    /// <summary>A page was chosen with the mouse or keyboard.</summary>
    public event EventHandler? SectionChosen;

    /// <summary>Enter was pressed on the marked page: move into it.</summary>
    public event EventHandler? SectionEntered;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int? DpiOverride { get; init; }

    public IReadOnlyList<string> Sections => _sections;

    /// <summary>The marked section. Setting it marks without raising <see cref="SectionChosen"/>.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var clamped = _sections.Length == 0 ? 0 : Math.Clamp(value, 0, _sections.Length - 1);
            if (clamped == _selectedIndex)
            {
                return;
            }

            _selectedIndex = clamped;
            UpdateAccessibility();
            Invalidate();
            if (IsHandleCreated)
            {
                AccessibilityNotifyClients(AccessibleEvents.Selection, clamped);
                if (Focused)
                {
                    AccessibilityNotifyClients(AccessibleEvents.Focus, clamped);
                }
            }
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new NavAccessibleObject(this);

    /// <summary>The list as a tab list whose tabs are the pages, so a screen reader says which is shown.</summary>
    private sealed class NavAccessibleObject(SectionNav owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.PageTabList;

        public override int GetChildCount() => owner._sections.Length;

        public override AccessibleObject? GetChild(int index) =>
            index >= 0 && index < owner._sections.Length ? new PageTabAccessibleObject(owner, index, this) : null;

        public override AccessibleObject? GetFocused() => owner.Focused ? GetChild(owner._selectedIndex) : null;

        public override AccessibleObject? GetSelected() => GetChild(owner._selectedIndex);
    }

    private sealed class PageTabAccessibleObject(SectionNav owner, int index, AccessibleObject parent) : AccessibleObject
    {
        public override string Name => owner._sections[index];

        public override AccessibleRole Role => AccessibleRole.PageTab;

        public override AccessibleObject Parent => parent;

        public override string Description => $"Tab {index + 1} of {owner._sections.Length}";

        public override AccessibleStates State =>
            AccessibleStates.Selectable | AccessibleStates.Focusable |
            (index == owner._selectedIndex ? AccessibleStates.Selected : AccessibleStates.None) |
            (index == owner._selectedIndex && owner.Focused ? AccessibleStates.Focused : AccessibleStates.None);

        public override Rectangle Bounds => owner.RectangleToScreen(owner.ItemBounds(index));

        public override string DefaultAction => "Switch";

        public override void DoDefaultAction() => owner.Choose(index);

        public override void Select(AccessibleSelection flags)
        {
            if (flags.HasFlag(AccessibleSelection.TakeSelection) || flags.HasFlag(AccessibleSelection.TakeFocus))
            {
                owner.Choose(index);
            }
        }
    }

    internal Rectangle ItemBounds(int index)
    {
        var scale = Scale();
        return new Rectangle(0, index * (scale[ItemHeight] + scale[ItemGap]), Width, scale[ItemHeight]);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.Enter || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Enter)
        {
            SectionEntered?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        var target = e.KeyCode switch
        {
            Keys.Up => _selectedIndex - 1,
            Keys.Down => _selectedIndex + 1,
            Keys.Home => 0,
            Keys.End => _sections.Length - 1,
            _ => -1,
        };
        if (target >= 0 && target < _sections.Length && target != _selectedIndex)
        {
            Choose(target);
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
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
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
        var index = IndexAt(e.Location);
        if (e.Button == MouseButtons.Left && index >= 0)
        {
            Focus();
            Choose(index);
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
        var background = Parent?.BackColor ?? Theme.Night;
        graphics.Clear(background);
        var scale = Scale();
        var highContrast = Theme.IsHighContrast;
        var font = FontForDpi();
        for (var index = 0; index < _sections.Length; index++)
        {
            var item = ItemBounds(index);
            var selected = index == _selectedIndex;
            if (selected || index == _hoverIndex)
            {
                using var fill = new SolidBrush(highContrast
                    ? (selected ? SystemColors.Highlight : SystemColors.Window)
                    : Theme.Blend(Theme.Text, background, selected ? 0.08 : 0.05));
                using var shape = DrawingHelpers.RoundedRectangle(item, scale.Exact(6));
                graphics.FillPath(fill, shape);
            }

            if (selected && !highContrast)
            {
                using var pill = new SolidBrush(Theme.Accent);
                using var pillShape = DrawingHelpers.RoundedRectangle(
                    new RectangleF(0, item.Top + item.Height * 0.28F, scale.Exact(Pill), item.Height * 0.44F),
                    scale.Exact(Pill) / 2F);
                graphics.FillPath(pill, pillShape);
            }

            var color = highContrast ? (selected ? SystemColors.HighlightText : SystemColors.WindowText) : Theme.Text;
            var textLeft = item.Left + scale[14];
            if (ShowsGlyphs && IconFontForDpi() is { } icons)
            {
                DrawingHelpers.DrawText(
                    graphics,
                    Glyphs![index],
                    icons,
                    color,
                    new Rectangle(textLeft, item.Top, scale[20], item.Height),
                    TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
                textLeft += scale[30];
            }

            DrawingHelpers.DrawText(
                graphics,
                _sections[index],
                font,
                color,
                Rectangle.FromLTRB(textLeft, item.Top, item.Right - scale[6], item.Bottom),
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        if (Focused && ShowFocusCues && _sections.Length > 0)
        {
            using var focus = DrawingHelpers.RoundedRectangle(Rectangle.Inflate(ItemBounds(_selectedIndex), -1, -1), scale.Exact(6));
            using var pen = new Pen(highContrast ? SystemColors.WindowText : Theme.Accent, Math.Max(1F, scale.Exact(1.5F)));
            graphics.DrawPath(pen, focus);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font?.Dispose();
            _iconFont?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Choose(int index)
    {
        SelectedIndex = index;
        SectionChosen?.Invoke(this, EventArgs.Empty);
    }

    private int IndexAt(Point point)
    {
        for (var index = 0; index < _sections.Length; index++)
        {
            if (ItemBounds(index).Contains(point))
            {
                return index;
            }
        }

        return -1;
    }

    private void UpdateAccessibility() =>
        AccessibleDescription = _sections.Length == 0
            ? null
            : $"{_sections[_selectedIndex]}, {_selectedIndex + 1} of {_sections.Length}. Use Up and Down to change pages and Enter to go into one.";

    private LayoutScale Scale() => DpiOverride is { } dpi ? new LayoutScale(dpi) : new LayoutScale(this);

    private Font FontForDpi()
    {
        var dpi = DpiOverride ?? LayoutScale.DpiOf(this);
        if (_font is null || _fontDpi != dpi)
        {
            _font?.Dispose();
            _iconFont?.Dispose();
            _font = Typography.Text(9F, dpi);
            _iconFont = null;
            _fontDpi = dpi;
        }

        return _font;
    }

    /// <summary>The icon face at the same DPI as the names, made on first use.</summary>
    private Font? IconFontForDpi()
    {
        FontForDpi();
        return _iconFont ??= Typography.Icons(10.5F, _fontDpi);
    }
}
