using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace UsageAI.UI;

/// <summary>
/// Settings' search field: a rounded box with a magnifier and a "Search" placeholder around a
/// borderless text box, which keeps the focus while the results below it are browsed.
/// </summary>
internal sealed class SettingsSearchBox : Control
{
    /// <summary>The magnifier in the Windows icon font.</summary>
    internal const string MagnifierGlyph = "";

    private const int EmSetCueBanner = 0x1501;
    private readonly TextBox _box;
    private Font? _iconFont;
    private int _iconDpi;

    public SettingsSearchBox()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
            true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        _box = new TextBox
        {
            AccessibleName = "Search settings",
            AccessibleDescription = "Type to find a setting. Up and Down choose a result, Enter goes to it, and Escape closes the results.",
            BorderStyle = BorderStyle.None,
            TabIndex = 0,
        };
        _box.HandleCreated += (_, _) => SendMessage(_box.Handle, EmSetCueBanner, (IntPtr)1, "Search");
        _box.GotFocus += (_, _) => Invalidate();
        _box.LostFocus += (_, _) => Invalidate();
        Controls.Add(_box);
        ApplyColors();
    }

    /// <summary>The text box the search is typed into.</summary>
    internal TextBox Box => _box;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int? DpiOverride { get; init; }

    /// <summary>Takes the palette again, after a theme change or High Contrast switching.</summary>
    internal void ApplyColors()
    {
        var highContrast = Theme.IsHighContrast;
        BackColor = highContrast ? SystemColors.Window : Theme.Night;
        _box.BackColor = highContrast ? SystemColors.Window : Theme.SurfaceRaised;
        _box.ForeColor = highContrast ? SystemColors.WindowText : Theme.Text;
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var scale = Scale();
        var left = scale[34];
        var height = _box.PreferredHeight;
        _box.SetBounds(left, Math.Max(0, (Height - height) / 2), Math.Max(1, Width - left - scale[10]), height);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _box.Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = Scale();
        var highContrast = Theme.IsHighContrast;
        graphics.Clear(highContrast ? SystemColors.Window : Parent?.BackColor ?? Theme.Night);
        var field = new RectangleF(0.5F, 0.5F, Width - 1.5F, Height - 1.5F);
        using (var shape = DrawingHelpers.RoundedRectangle(field, scale.Exact(6)))
        {
            using var fill = new SolidBrush(_box.BackColor);
            graphics.FillPath(fill, shape);
            var focused = _box.Focused;
            using var border = new Pen(
                highContrast ? SystemColors.WindowText : focused ? Theme.Accent : Theme.Hairline,
                focused ? Math.Max(1F, scale.Exact(1.5F)) : 1F);
            graphics.DrawPath(border, shape);
        }

        if (IconFont() is { } icons)
        {
            DrawingHelpers.DrawText(
                graphics,
                MagnifierGlyph,
                icons,
                highContrast ? SystemColors.WindowText : Theme.Muted,
                new Rectangle(scale[10], 0, scale[18], Height),
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        PerformLayout();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _iconFont?.Dispose();
        }

        base.Dispose(disposing);
    }

    private Font? IconFont()
    {
        var dpi = DpiOverride ?? LayoutScale.DpiOf(this);
        if (_iconFont is null || _iconDpi != dpi)
        {
            _iconFont?.Dispose();
            _iconFont = Typography.Icons(9.5F, dpi);
            _iconDpi = dpi;
        }

        return _iconFont;
    }

    private LayoutScale Scale() => DpiOverride is { } dpi ? new LayoutScale(dpi) : new LayoutScale(this);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, string lParam);
}

/// <summary>What a row of the search results is.</summary>
internal enum SettingsSearchRowKind
{
    /// <summary>A page's name, heading the results on it; choosing it opens the page.</summary>
    Page,

    /// <summary>A setting, card heading or list entry that matched.</summary>
    Hit,

    /// <summary>"+N more", which lists the rest of a page's results in place.</summary>
    More,

    /// <summary>"No matching settings".</summary>
    Empty,
}

internal sealed record SettingsSearchRow(SettingsSearchRowKind Kind, SettingsSearchGroup? Group, SettingsSearchHit? Hit)
{
    /// <summary>What a screen reader says for the row, as the Claude desktop app's results do.</summary>
    public string AccessibleText => Kind switch
    {
        SettingsSearchRowKind.Page => Group!.PageName,
        SettingsSearchRowKind.Hit => $"{Hit!.Entry.Text} in {Group!.PageName}",
        SettingsSearchRowKind.More => $"{Group!.More} more {(Group.More == 1 ? "match" : "matches")} in {Group.PageName}",
        _ => "No matching settings",
    };
}

internal sealed class SettingsSearchRowEventArgs(SettingsSearchRow row) : EventArgs
{
    public SettingsSearchRow Row { get; } = row;
}

/// <summary>
/// The results under Settings' search box, laid over the page list and the page: each page with
/// results under its name, a few to a page, the typed letters in the accent colour. The text box
/// keeps the focus, so the arrows and Enter are passed in by the window; the mouse works here.
/// </summary>
internal sealed class SettingsSearchResults : Control
{
    private const int RowHeight = 34;
    private const int EmptyHeight = 96;
    private const int Inset = 6;

    private IReadOnlyList<SettingsSearchRow> _rows = Array.Empty<SettingsSearchRow>();
    private int _selected = -1;
    private int _hover = -1;
    private int _top;
    private Font? _font;
    private Font? _underlined;
    private Font? _iconFont;
    private Font? _emptyIcon;
    private int _fontDpi;

    public SettingsSearchResults()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
            true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Visible = false;
        AccessibleName = "Search results";
        AccessibleRole = AccessibleRole.List;
    }

    /// <summary>A row was chosen with the mouse.</summary>
    public event EventHandler<SettingsSearchRowEventArgs>? Chosen;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int? DpiOverride { get; init; }

    /// <summary>The page icons, as in the page list.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal IReadOnlyList<string>? Glyphs { get; init; }

    internal IReadOnlyList<SettingsSearchRow> Rows => _rows;

    internal int SelectedIndex => _selected;

    internal SettingsSearchRow? SelectedRow => _selected >= 0 && _selected < _rows.Count ? _rows[_selected] : null;

    /// <summary>
    /// Shows <paramref name="groups"/>, or "No matching settings" for none, with the first row
    /// selected, as typing does. <paramref name="select"/> picks another row instead, such as the
    /// first one a "+N more" just revealed.
    /// </summary>
    internal void SetResults(IReadOnlyList<SettingsSearchGroup> groups, int select = 0)
    {
        var rows = new List<SettingsSearchRow>();
        foreach (var group in groups)
        {
            rows.Add(new SettingsSearchRow(SettingsSearchRowKind.Page, group, null));
            rows.AddRange(group.Hits.Select(hit => new SettingsSearchRow(SettingsSearchRowKind.Hit, group, hit)));
            if (group.More > 0)
            {
                rows.Add(new SettingsSearchRow(SettingsSearchRowKind.More, group, null));
            }
        }

        if (rows.Count == 0)
        {
            rows.Add(new SettingsSearchRow(SettingsSearchRowKind.Empty, null, null));
        }

        _rows = rows;
        _hover = -1;
        _top = 0;
        _selected = rows[0].Kind == SettingsSearchRowKind.Empty ? -1 : Math.Clamp(select, 0, rows.Count - 1);
        EnsureVisible();
        Invalidate();
        NotifySelection();
    }

    /// <summary>Moves the selection by <paramref name="step"/> rows, stopping at either end.</summary>
    internal void MoveSelection(int step)
    {
        if (_selected < 0)
        {
            return;
        }

        var target = Math.Clamp(_selected + step, 0, _rows.Count - 1);
        if (target == _selected)
        {
            return;
        }

        _selected = target;
        EnsureVisible();
        Invalidate();
        NotifySelection();
    }

    /// <summary>The height that shows every row, or as many as fit in <paramref name="maximum"/>.</summary>
    internal int PreferredHeight(int maximum)
    {
        var scale = Scale();
        var content = _rows.Count == 1 && _rows[0].Kind == SettingsSearchRowKind.Empty
            ? scale[EmptyHeight]
            : _rows.Count * scale[RowHeight];
        return Math.Max(scale[RowHeight] + (2 * scale[Inset]), Math.Min(maximum, content + (2 * scale[Inset])));
    }

    private int VisibleRows => Math.Max(1, (Height - (2 * Scale()[Inset])) / Math.Max(1, Scale()[RowHeight]));

    private void EnsureVisible()
    {
        if (_selected < 0)
        {
            return;
        }

        if (_selected < _top)
        {
            _top = _selected;
        }
        else if (_selected >= _top + VisibleRows)
        {
            _top = _selected - VisibleRows + 1;
        }
    }

    internal Rectangle RowBounds(int index)
    {
        var scale = Scale();
        return new Rectangle(
            scale[Inset],
            scale[Inset] + ((index - _top) * scale[RowHeight]),
            Width - (2 * scale[Inset]),
            scale[RowHeight]);
    }

    private int RowAt(Point point)
    {
        for (var index = _top; index < _rows.Count && index < _top + VisibleRows; index++)
        {
            if (RowBounds(index).Contains(point))
            {
                return _rows[index].Kind == SettingsSearchRowKind.Empty ? -1 : index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Clips the window to its rounded shape. It lies over the page list and the cards, so
    /// corners painted in any one colour showed as dark notches over a card.
    /// </summary>
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        using var path = DrawingHelpers.RoundedRectangle(new RectangleF(0, 0, Width, Height), Scale().Exact(8));
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var row = RowAt(e.Location);
        if (row != _hover)
        {
            _hover = row;
            Cursor = row >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var row = RowAt(e.Location);
        if (e.Button == MouseButtons.Left && row >= 0)
        {
            _selected = row;
            Invalidate();
            Chosen?.Invoke(this, new SettingsSearchRowEventArgs(_rows[row]));
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var top = Math.Clamp(_top - Math.Sign(e.Delta) * 3, 0, Math.Max(0, _rows.Count - VisibleRows));
        if (top != _top)
        {
            _top = top;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = Scale();
        var highContrast = Theme.IsHighContrast;
        var surface = highContrast ? SystemColors.Window : Theme.SurfaceRaised;
        graphics.Clear(surface);
        using (var shape = DrawingHelpers.RoundedRectangle(new RectangleF(0.5F, 0.5F, Width - 1.5F, Height - 1.5F), scale.Exact(8)))
        {
            using var fill = new SolidBrush(surface);
            graphics.FillPath(fill, shape);
            using var border = new Pen(highContrast ? SystemColors.WindowText : Theme.Hairline);
            graphics.DrawPath(border, shape);
        }

        FontsForDpi();
        if (_rows is [{ Kind: SettingsSearchRowKind.Empty }])
        {
            PaintEmpty(graphics, scale, highContrast);
            return;
        }

        var clip = graphics.Clip;
        graphics.SetClip(new Rectangle(0, scale[Inset], Width, Height - (2 * scale[Inset])));
        for (var index = _top; index < _rows.Count && index < _top + VisibleRows + 1; index++)
        {
            PaintRow(graphics, index, scale, highContrast, surface);
        }

        graphics.Clip = clip;
    }

    private void PaintEmpty(Graphics graphics, LayoutScale scale, bool highContrast)
    {
        var muted = highContrast ? SystemColors.WindowText : Theme.Muted;
        if (_emptyIcon is not null)
        {
            DrawingHelpers.DrawText(
                graphics,
                SettingsSearchBox.MagnifierGlyph,
                _emptyIcon,
                muted,
                new Rectangle(0, scale[16], Width, scale[32]),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        DrawingHelpers.DrawText(
            graphics,
            "No matching settings",
            _font!,
            muted,
            new Rectangle(0, scale[54], Width, scale[24]),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void PaintRow(Graphics graphics, int index, LayoutScale scale, bool highContrast, Color surface)
    {
        var row = _rows[index];
        var bounds = RowBounds(index);
        var selected = index == _selected;
        if (selected || index == _hover)
        {
            using var fill = new SolidBrush(highContrast
                ? (selected ? SystemColors.Highlight : SystemColors.Window)
                : Theme.Blend(Theme.Text, surface, selected ? 0.1 : 0.05));
            using var shape = DrawingHelpers.RoundedRectangle(bounds, scale.Exact(6));
            graphics.FillPath(fill, shape);
        }

        var text = highContrast ? (selected ? SystemColors.HighlightText : SystemColors.WindowText) : Theme.Text;
        var match = highContrast ? text : Theme.Accent;
        var left = bounds.Left + scale[10];
        var nameLeft = left + (ShowsGlyphs ? scale[30] : 0);
        switch (row.Kind)
        {
            case SettingsSearchRowKind.Page:
                if (ShowsGlyphs && _iconFont is not null)
                {
                    DrawingHelpers.DrawText(
                        graphics,
                        Glyphs![row.Group!.Page],
                        _iconFont,
                        text,
                        new Rectangle(left, bounds.Top, scale[20], bounds.Height),
                        TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
                }

                DrawMatched(graphics, row.Group!.PageName, row.Group.NameMatchStart, row.Group.NameMatchLength, nameLeft, bounds, text, match, highContrast);
                break;
            case SettingsSearchRowKind.Hit:
                DrawMatched(graphics, row.Hit!.Entry.Text, row.Hit.MatchStart, row.Hit.MatchLength, nameLeft, bounds, text, match, highContrast);
                break;
            case SettingsSearchRowKind.More:
                DrawingHelpers.DrawText(
                    graphics,
                    $"+{row.Group!.More} more",
                    _font!,
                    highContrast ? text : Theme.Muted,
                    Rectangle.FromLTRB(nameLeft, bounds.Top, bounds.Right - scale[8], bounds.Bottom),
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                break;
        }
    }

    /// <summary>
    /// Draws a label with the typed letters in the accent colour, or underlined in High Contrast,
    /// where colour alone must not carry meaning. A label too long for the row is drawn whole with
    /// an ellipsis instead.
    /// </summary>
    private void DrawMatched(
        Graphics graphics,
        string label,
        int start,
        int length,
        int left,
        Rectangle bounds,
        Color text,
        Color match,
        bool highContrast)
    {
        const TextFormatFlags Flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        var font = _font!;
        var available = bounds.Right - Scale()[8] - left;
        var whole = TextRenderer.MeasureText(graphics, label, font, Size.Empty, Flags).Width;
        if (start < 0 || length <= 0 || start + length > label.Length || whole > available)
        {
            DrawingHelpers.DrawText(graphics, label, font, text, Rectangle.FromLTRB(left, bounds.Top, bounds.Right - Scale()[8], bounds.Bottom), Flags | TextFormatFlags.EndEllipsis);
            return;
        }

        var parts = new[] { label[..start], label.Substring(start, length), label[(start + length)..] };
        var x = left;
        for (var part = 0; part < parts.Length; part++)
        {
            if (parts[part].Length == 0)
            {
                continue;
            }

            var partFont = part == 1 && highContrast ? _underlined! : font;
            var width = TextRenderer.MeasureText(graphics, parts[part], partFont, Size.Empty, Flags).Width;
            DrawingHelpers.DrawText(graphics, parts[part], partFont, part == 1 ? match : text, new Rectangle(x, bounds.Top, width + 1, bounds.Height), Flags);
            x += width;
        }
    }

    private bool ShowsGlyphs => Glyphs is not null && Typography.HasIcons;

    private void FontsForDpi()
    {
        var dpi = DpiOverride ?? LayoutScale.DpiOf(this);
        if (_font is not null && _fontDpi == dpi)
        {
            return;
        }

        DisposeFonts();
        _font = Typography.Text(9F, dpi);
        _underlined = Typography.Text(9F, dpi, FontStyle.Underline);
        _iconFont = Typography.Icons(10.5F, dpi);
        _emptyIcon = Typography.Icons(16F, dpi);
        _fontDpi = dpi;
    }

    private void DisposeFonts()
    {
        _font?.Dispose();
        _underlined?.Dispose();
        _iconFont?.Dispose();
        _emptyIcon?.Dispose();
        _font = _underlined = _iconFont = _emptyIcon = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeFonts();
        }

        base.Dispose(disposing);
    }

    /// <summary>Tells a screen reader which result is selected, though the focus stays in the box.</summary>
    private void NotifySelection()
    {
        if (IsHandleCreated && _selected >= 0)
        {
            AccessibilityNotifyClients(AccessibleEvents.Selection, _selected);
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ResultsAccessibleObject(this);

    private LayoutScale Scale() => DpiOverride is { } dpi ? new LayoutScale(dpi) : new LayoutScale(this);

    private sealed class ResultsAccessibleObject(SettingsSearchResults owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.List;

        public override int GetChildCount() => owner._rows.Count;

        public override AccessibleObject? GetChild(int index) =>
            index >= 0 && index < owner._rows.Count ? new RowAccessibleObject(owner, index, this) : null;

        public override AccessibleObject? GetSelected() => owner._selected >= 0 ? GetChild(owner._selected) : null;
    }

    private sealed class RowAccessibleObject(SettingsSearchResults owner, int index, AccessibleObject parent) : AccessibleObject
    {
        public override string Name => owner._rows[index].AccessibleText;

        public override AccessibleRole Role => AccessibleRole.ListItem;

        public override AccessibleObject Parent => parent;

        public override AccessibleStates State =>
            AccessibleStates.Selectable | (index == owner._selected ? AccessibleStates.Selected : AccessibleStates.None);

        public override Rectangle Bounds => owner.RectangleToScreen(owner.RowBounds(index));

        public override string DefaultAction => "Go to";

        public override void DoDefaultAction() =>
            owner.Chosen?.Invoke(owner, new SettingsSearchRowEventArgs(owner._rows[index]));
    }
}
