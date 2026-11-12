using System.Drawing.Drawing2D;
using System.Drawing.Text;
using UsageAI.Models;
using UsageAI.Services;

namespace UsageAI.UI;

/// <summary>
/// The dashboard: every provider's limits in one table. Columns are fixed — limit, usage bar,
/// figure, countdown, forecast — so values line up down the page and compare at a glance, and
/// each provider is a group led by its glyph, name, account and status. A provider reporting
/// several model groups gets a sub-heading per group, so a long name such as "Claude and GPT
/// models" is never cut to fit the label column. Row heights do not depend on width, so the
/// table's height is known before it is laid out; narrow windows drop the forecast column
/// rather than squeezing every cell.
/// </summary>
internal sealed class UsageLedger : Control
{
    private const int ColumnsHeight = 32;
    private const int ProviderHeight = 46;
    private const int GroupHeight = 28;
    private const int MetricHeight = 40;
    private const int MessageHeight = 34;
    private const int ActionMessageHeight = 72;
    private const int GapHeight = 14;
    private const int Indent = 28;
    private const int CellGap = 16;
    private const int GlyphSize = 18;
    private const int BarHeight = 6;
    // Low enough that a dashboard restored at the old 620-pixel grid width still shows forecasts.
    private const int PaceColumnMinimumTableWidth = 540;
    private const int UsageDetailMinimumWidth = 220;

    private static Func<bool> _shiftProbe = static () => (ModifierKeys & Keys.Shift) == Keys.Shift;

    private IReadOnlyList<Row> _rows;
    private QuotaDisplay _quotaDisplay;
    private readonly int? _dpiOverride;
    private readonly List<ActionTarget> _actions = new();
    private LedgerFonts _fonts;
    private int _focusIndex = -1;
    private int _hoverIndex = -1;
    private Point? _pressedAt;
    private string? _dragKey;
    private bool _dragging;
    private bool _dragCancelled;
    private CardReorder.Drop? _drop;
    private string? _movingKey;
    private string? _pendingFocusKey;

    public UsageLedger(
        IReadOnlyList<ProviderStatus> states,
        MetricDisplayMode displayMode,
        QuotaDisplay quotaDisplay,
        IReadOnlyList<UsageSample> forecastHistory,
        int? dpiOverride = null)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(forecastHistory);
        _quotaDisplay = quotaDisplay;
        _dpiOverride = dpiOverride;
        _rows = BuildRows(states, displayMode, forecastHistory, DateTimeOffset.Now);
        _fonts = new LedgerFonts(EffectiveDpi);
        DoubleBuffered = true;
        ResizeRedraw = true;
        AccessibleRole = AccessibleRole.Table;
        AccessibleName = "Usage by provider";
        ApplyRows(states);
    }

    /// <summary>
    /// Replaces the table's contents in place. Providers publish one at a time, so the dashboard
    /// updates several times per refresh; rebuilding the control each time dropped keyboard focus
    /// and, in a scrolled window, the scroll position. The focused action is kept by position.
    /// </summary>
    public void SetData(
        IReadOnlyList<ProviderStatus> states,
        MetricDisplayMode displayMode,
        QuotaDisplay quotaDisplay,
        IReadOnlyList<UsageSample> forecastHistory)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(forecastHistory);
        _quotaDisplay = quotaDisplay;
        _rows = BuildRows(states, displayMode, forecastHistory, DateTimeOffset.Now);
        _hoverIndex = -1;
        // The rows a drag was measured against are gone; a keyboard move keeps its provider focused.
        EndDrag(drop: false);
        _pendingFocusKey = _movingKey;
        ApplyRows(states);
        Invalidate();
    }

    private void ApplyRows(IReadOnlyList<ProviderStatus> states)
    {
        TabStop = _rows.Any(row => row.HasAction);
        SetStyle(ControlStyles.Selectable, TabStop);
        AccessibleDescription = Describe(states);
        Height = NaturalHeight;
    }

    public event EventHandler<ProviderCardActionEventArgs>? ActionInvoked;

    /// <summary>
    /// A provider was dragged by its heading row, or moved with Alt+Up / Alt+Down while one of its
    /// links has the focus, to before or after another.
    /// </summary>
    public event EventHandler<CardReorderEventArgs>? ReorderRequested;

    /// <summary>Eases the usage bars toward new values; null draws them where they belong at once.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal MotionTracker? Motion { get; init; }

    /// <summary>The height every row needs; it does not depend on the width.</summary>
    public int NaturalHeight
    {
        get
        {
            var scale = Scale();
            return _rows.Sum(row => scale[row.BaselineHeight]);
        }
    }

    /// <summary>The rows, top to bottom, for tests.</summary>
    internal IReadOnlyList<Row> Rows => _rows;

    /// <summary>The actionable areas of the last paint, top to bottom, for tests.</summary>
    internal IReadOnlyList<ActionTarget> Actions => _actions;

    /// <summary>Whether the forecast column fits at the current width.</summary>
    internal bool ShowsForecastColumn => Width >= Scale()[PaceColumnMinimumTableWidth];

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        ApplyDpiChange();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDpiChange();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyDpiChange();
    }

    protected override void RescaleConstantsForDpi(int deviceDpiOld, int deviceDpiNew)
    {
        base.RescaleConstantsForDpi(deviceDpiOld, deviceDpiNew);
        ApplyDpiChange();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _dragCancelled = false;
        // A provider is picked up by its heading row, anywhere but on its links and buttons.
        if (e.Button == MouseButtons.Left &&
            !_actions.Any(action => action.Bounds.Contains(e.Location)) &&
            HeadingAt(e.Y) is { } key)
        {
            _pressedAt = e.Location;
            _dragKey = key;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        EndDrag(drop: true);
    }

    // Capture goes when the window is deactivated mid-drag (Alt+Tab).
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture)
        {
            EndDrag(drop: false);
        }
    }

    /// <summary>Gives up a drag in progress, as Esc does. False when no provider is being dragged.</summary>
    internal bool CancelDrag()
    {
        if (!_dragging)
        {
            return false;
        }

        // Letting go of the capture ends the drag through OnMouseCaptureChanged; a table that
        // never had it is ended here.
        Capture = false;
        EndDrag(drop: false);
        return true;
    }

    private void EndDrag(bool drop)
    {
        _pressedAt = null;
        if (!_dragging)
        {
            return;
        }

        var blocks = Blocks();
        var request = drop && _drop is { } landing && _dragKey is { } moved && landing.MarkIndex < blocks.Count
            ? new CardReorderEventArgs(moved, blocks[landing.MarkIndex].Key, landing.After)
            : null;
        _dragging = false;
        // A drag given up with the button still down: its release is not a click on a link either.
        _dragCancelled = !drop;
        _drop = null;
        Cursor = Cursors.Default;
        Invalidate();
        DragEnded?.Invoke(this, EventArgs.Empty);
        if (request is not null)
        {
            ReorderRequested?.Invoke(this, request);
        }
    }

    /// <summary>A provider is being dragged; the dashboard holds new readings back until it ends.</summary>
    internal bool IsDragging => _dragging;

    /// <summary>A drag ended, dropped or cancelled; raised before any <see cref="ReorderRequested"/>.</summary>
    internal event EventHandler? DragEnded;

    /// <summary>Each provider key's rows as one block, top to bottom.</summary>
    private List<(string Key, int Top, int Bottom)> Blocks()
    {
        var scale = Scale();
        var blocks = new List<(string Key, int Top, int Bottom)>();
        var top = 0;
        foreach (var row in _rows)
        {
            var bottom = top + scale[row.BaselineHeight];
            if (row.Status is { } status)
            {
                if (blocks.Count > 0 && blocks[^1].Key.Equals(status.ProviderId, StringComparison.OrdinalIgnoreCase))
                {
                    blocks[^1] = (blocks[^1].Key, blocks[^1].Top, bottom);
                }
                else
                {
                    blocks.Add((status.ProviderId, top, bottom));
                }
            }

            top = bottom;
        }

        return blocks;
    }

    /// <summary>The provider key whose heading row is at <paramref name="y"/>, if one is.</summary>
    private string? HeadingAt(int y)
    {
        var scale = Scale();
        var top = 0;
        foreach (var row in _rows)
        {
            var bottom = top + scale[row.BaselineHeight];
            if (y >= top && y < bottom)
            {
                return row.Kind == RowKind.Provider ? row.Status?.ProviderId : null;
            }

            top = bottom;
        }

        return null;
    }

    private void UpdateDrop(int y)
    {
        var blocks = Blocks();
        var keys = blocks.Select(block => block.Key).ToArray();
        // The gap under a provider counts as that provider, and the column headings as the first
        // one: the line is drawn there, so it must not go out when the pointer reaches it.
        // Outside the table there is nothing to drop on.
        var target = y < 0 || y >= Height ? -1 : Math.Max(0, blocks.FindLastIndex(block => y >= block.Top));
        var drop = target < 0 || target >= blocks.Count
            ? null
            : CardReorder.Resolve(
                keys.Length,
                Array.FindIndex(keys, key => key.Equals(_dragKey, StringComparison.OrdinalIgnoreCase)),
                target,
                pastMiddle: y > (blocks[target].Top + blocks[target].Bottom) / 2);
        if (drop?.MarkIndex != _drop?.MarkIndex || drop?.After != _drop?.After)
        {
            Invalidate();
        }

        _drop = drop;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pressedAt is { } pressed && e.Button.HasFlag(MouseButtons.Left))
        {
            if (!_dragging && CardReorder.IsDrag(pressed, e.Location))
            {
                _dragging = true;
                Cursor = Cursors.SizeAll;
                Invalidate();
            }

            if (_dragging)
            {
                UpdateDrop(e.Y);
                return;
            }
        }

        var index = _actions.FindIndex(action => action.Bounds.Contains(e.Location));
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
        if (_hoverIndex >= 0)
        {
            _hoverIndex = -1;
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        // The click of a drag's release (raised before its MouseUp) is not a click on a link.
        if (e.Button != MouseButtons.Left || _dragging || _dragCancelled)
        {
            return;
        }

        var index = _actions.FindIndex(action => action.Bounds.Contains(e.Location));
        if (index >= 0)
        {
            Invoke(_actions[index]);
        }
    }

    /// <summary>
    /// Tab walks the table's links and buttons before leaving it, as it would through separate
    /// controls; past the last one (or before the first) it moves on to the next control.
    /// </summary>
    protected override bool IsInputKey(Keys keyData) => keyData switch
    {
        Keys.Tab => _focusIndex < _actions.Count - 1,
        Keys.Tab | Keys.Shift => _focusIndex > 0,
        Keys.Enter or Keys.Space => _focusIndex >= 0 && _focusIndex < _actions.Count,
        (Keys.Alt | Keys.Up) or (Keys.Alt | Keys.Down) => _focusIndex >= 0 && _focusIndex < _actions.Count,
        _ => base.IsInputKey(keyData),
    };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyData)
        {
            case Keys.Tab when _focusIndex < _actions.Count - 1:
                MoveFocus(_focusIndex + 1);
                e.Handled = true;
                break;
            case Keys.Tab | Keys.Shift when _focusIndex > 0:
                MoveFocus(_focusIndex - 1);
                e.Handled = true;
                break;
            case Keys.Enter or Keys.Space when _focusIndex >= 0 && _focusIndex < _actions.Count:
                Invoke(_actions[_focusIndex]);
                e.Handled = true;
                break;
            case (Keys.Alt | Keys.Up) or (Keys.Alt | Keys.Down) when _focusIndex >= 0 && _focusIndex < _actions.Count:
                MoveFocusedProvider(forward: e.KeyCode == Keys.Down);
                e.Handled = true;
                break;
        }
    }

    private void MoveFocusedProvider(bool forward)
    {
        var keys = Blocks().Select(block => block.Key).ToArray();
        var index = Array.FindIndex(keys, key =>
            key.Equals(_actions[_focusIndex].Status?.ProviderId, StringComparison.OrdinalIgnoreCase));
        var neighbour = CardReorder.Neighbour(keys.Length, index, forward);
        if (neighbour < 0)
        {
            return;
        }

        // SetData, which the move causes, hands the focus back to the moved provider's first link.
        _movingKey = keys[index];
        ReorderRequested?.Invoke(this, new CardReorderEventArgs(keys[index], keys[neighbour], forward));
        _movingKey = null;
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (_actions.Count > 0)
        {
            MoveFocus(_shiftProbe() ? _actions.Count - 1 : 0);
        }
    }

    /// <summary>
    /// Replaces the live Shift-key reading that decides whether focus arriving by Shift+Tab starts
    /// at the last action; null restores it. The test harness pins it, since the real key state
    /// belongs to whoever is at the machine, and a held Shift once failed a Tab-walk check.
    /// </summary>
    internal static void SetShiftProbe(Func<bool>? probe) =>
        _shiftProbe = probe ?? (static () => (ModifierKeys & Keys.Shift) == Keys.Shift);

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        _focusIndex = -1;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        _actions.Clear();
        var scale = Scale();
        var columns = ComputeColumns(scale);
        var top = 0;
        foreach (var row in _rows)
        {
            var height = scale[row.BaselineHeight];
            var bounds = new Rectangle(0, top, Width, height);
            if (bounds.IntersectsWith(e.ClipRectangle) || row.HasAction)
            {
                switch (row.Kind)
                {
                    case RowKind.Columns:
                        DrawColumns(graphics, scale, columns, bounds);
                        break;
                    case RowKind.Provider:
                        DrawProvider(graphics, scale, bounds, row);
                        break;
                    case RowKind.Group:
                        DrawGroup(graphics, scale, bounds, row);
                        break;
                    case RowKind.Metric:
                        DrawMetric(graphics, scale, columns, bounds, row);
                        break;
                    case RowKind.Message:
                        DrawMessage(graphics, scale, bounds, row);
                        break;
                }
            }

            top += height;
        }

        if (Focused && ShowFocusCues && _focusIndex >= 0 && _focusIndex < _actions.Count)
        {
            using var focus = DrawingHelpers.RoundedRectangle(
                Rectangle.Inflate(_actions[_focusIndex].Bounds, scale[3], scale[2]),
                scale.Exact(4));
            using var pen = new Pen(Theme.Accent, Math.Max(1F, scale.Exact(1.5F)));
            graphics.DrawPath(pen, focus);
        }

        if (_dragging)
        {
            DrawDrag(graphics, scale);
        }

        if (_pendingFocusKey is { } moved)
        {
            _pendingFocusKey = null;
            var index = _actions.FindIndex(action =>
                moved.Equals(action.Status?.ProviderId, StringComparison.OrdinalIgnoreCase));
            if (Focused && index >= 0 && index != _focusIndex)
            {
                MoveFocus(index);
            }
        }
    }

    /// <summary>Fades the provider being dragged and draws the line where it would land.</summary>
    private void DrawDrag(Graphics graphics, LayoutScale scale)
    {
        // Whole pixels: an antialiased fill would blur the fade and the line at their edges.
        graphics.SmoothingMode = SmoothingMode.None;
        var blocks = Blocks();
        var thickness = scale[2];
        foreach (var block in blocks.Where(block => block.Key.Equals(_dragKey, StringComparison.OrdinalIgnoreCase)))
        {
            CardReorder.DrawMoving(graphics, new Rectangle(0, block.Top, Width, block.Bottom - block.Top), BackColor, thickness);
        }

        if (_drop is { } drop && drop.MarkIndex < blocks.Count)
        {
            var edge = drop.After ? blocks[drop.MarkIndex].Bottom : blocks[drop.MarkIndex].Top - thickness;
            CardReorder.DrawLanding(graphics, new Rectangle(0, Math.Clamp(edge, 0, Height - thickness), Width, thickness));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fonts.Dispose();
        }

        base.Dispose(disposing);
    }

    private static List<Row> BuildRows(
        IReadOnlyList<ProviderStatus> states,
        MetricDisplayMode displayMode,
        IReadOnlyList<UsageSample> history,
        DateTimeOffset now)
    {
        var rows = new List<Row> { new(RowKind.Columns, ColumnsHeight) };
        if (states.Count == 0)
        {
            rows.Add(new Row(RowKind.Message, MessageHeight, Text: "No providers to show. Turn providers on in Settings › Providers."));
            return rows;
        }

        foreach (var status in states)
        {
            if (rows.Count > 1)
            {
                rows.Add(new Row(RowKind.Gap, GapHeight));
            }

            rows.Add(new Row(
                RowKind.Provider,
                ProviderHeight,
                status,
                HasAction: status.AccountUrl is not null || status.IsStale));

            if (status.Snapshot is not { } snapshot)
            {
                var signIn = !status.IsLoading && !string.IsNullOrWhiteSpace(status.SignInCommand);
                rows.Add(new Row(
                    RowKind.Message,
                    signIn ? ActionMessageHeight : MessageHeight,
                    status,
                    Text: status.IsLoading
                        ? $"Reading {status.ProviderName} usage…"
                        : string.IsNullOrWhiteSpace(status.Error) ? "Not connected. Sign in, then refresh." : status.Error,
                    Tone: status.IsLoading ? Tone.Quiet : Tone.Critical,
                    HasAction: signIn));
                continue;
            }

            if (status.IsStale)
            {
                rows.Add(new Row(RowKind.Message, MessageHeight, status, Text: status.Error, Tone: Tone.Warning));
            }

            var metrics = UsageMetricSelection.Select(snapshot.Metrics, displayMode);
            if (metrics.Count == 0)
            {
                rows.Add(new Row(
                    RowKind.Message,
                    MessageHeight,
                    status,
                    Text: snapshot.Metrics.Count == 0 ? "No usage reported." : "No metered limits reported.",
                    Tone: Tone.Quiet));
                continue;
            }

            // Sub-headings only when there is more than one group to tell apart.
            var grouped = PopupMetricSelection.Groups(metrics).Count > 1;
            string? currentGroup = null;
            foreach (var metric in metrics)
            {
                var (group, window) = PopupMetricSelection.Split(metric.Name);
                if (grouped && group is not null && !string.Equals(group, currentGroup, StringComparison.OrdinalIgnoreCase))
                {
                    currentGroup = group;
                    rows.Add(new Row(RowKind.Group, GroupHeight, status, Label: group));
                }

                rows.Add(new Row(
                    RowKind.Metric,
                    MetricHeight,
                    status,
                    metric,
                    Label: grouped && group is not null ? window : metric.Name,
                    // A stale reading's window may have moved on, so it gets no estimate or pace.
                    Projection: status.IsStale ? null : UsageForecast.Estimate(history, status.ProviderId, metric, now),
                    Pace: status.IsStale ? null : UsageForecast.Pace(history, metric, now),
                    Nested: grouped && group is not null));
            }
        }

        return rows;
    }

    private Columns ComputeColumns(LayoutScale scale)
    {
        var gap = scale[CellGap];
        var label = Math.Clamp(Width * 28 / 100, scale[130], scale[230]);
        // High Contrast puts a textual cue before critical figures, which needs the extra room.
        var used = scale[Theme.IsHighContrast ? 84 : 58];
        var reset = scale[72];
        var pace = ShowsForecastColumn ? scale[118] : 0;
        var gaps = gap * (pace > 0 ? 4 : 3);
        var usage = Math.Max(scale[48], Width - label - used - reset - pace - gaps);
        var usageLeft = label + gap;
        var usedLeft = usageLeft + usage + gap;
        var resetLeft = usedLeft + used + gap;
        var paceLeft = resetLeft + reset + gap;
        return new Columns(0, label, usageLeft, usage, usedLeft, used, resetLeft, reset, paceLeft, pace);
    }

    private void DrawColumns(Graphics graphics, LayoutScale scale, Columns columns, Rectangle row)
    {
        var font = _fonts.Small;
        var color = Theme.Muted;
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        // "Limit" lines up with the limit names beneath it, not with the provider glyphs.
        var labelLeft = columns.LabelLeft + scale[Indent];
        Cell(graphics, "Limit", font, color, new Rectangle(labelLeft, row.Top, Math.Max(1, columns.Label - labelLeft), row.Height), flags);
        Cell(graphics, "Usage", font, color, new Rectangle(columns.UsageLeft, row.Top, columns.Usage, row.Height), flags);
        Cell(graphics, _quotaDisplay == QuotaDisplay.Remaining ? "Left" : "Used", font, color, new Rectangle(columns.UsedLeft, row.Top, columns.Used, row.Height), flags | TextFormatFlags.Right);
        Cell(graphics, "Resets in", font, color, new Rectangle(columns.ResetLeft, row.Top, columns.Reset, row.Height), flags | TextFormatFlags.Right);
        if (columns.Pace > 0)
        {
            Cell(graphics, "At this pace", font, color, new Rectangle(columns.PaceLeft, row.Top, columns.Pace, row.Height), flags);
        }

        using var rule = new Pen(Theme.Hairline);
        graphics.DrawLine(rule, 0, row.Bottom - 1, Width, row.Bottom - 1);
    }

    private void DrawProvider(Graphics graphics, LayoutScale scale, Rectangle row, Row data)
    {
        var status = data.Status!;
        var glyph = new Rectangle(
            (scale[Indent] - scale[GlyphSize]) / 2,
            row.Top + (row.Height - scale[GlyphSize]) / 2,
            scale[GlyphSize],
            scale[GlyphSize]);
        ProviderIconPainter.Draw(graphics, glyph, status.ProviderId);

        // Status on the right is measured first, so the name and account know how much room is
        // left; its Retry link is drawn last so Tab reaches the name before it, in reading order.
        var right = Width;
        var retryBounds = Rectangle.Empty;
        if (status.IsStale)
        {
            var retryWidth = TextRenderer.MeasureText(graphics, "Retry", _fonts.Small).Width;
            retryBounds = new Rectangle(right - retryWidth, row.Top + (row.Height - _fonts.Small.Height) / 2, retryWidth, _fonts.Small.Height);
            right = retryBounds.Left - scale[12];
        }

        var (statusText, statusColor) = StatusFor(status);
        var statusWidth = Math.Min(right / 2, TextRenderer.MeasureText(graphics, statusText, _fonts.Small).Width);
        Cell(
            graphics,
            statusText,
            _fonts.Small,
            statusColor,
            new Rectangle(right - statusWidth, row.Top, statusWidth, row.Height),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        right -= statusWidth + scale[16];

        var nameLeft = scale[Indent];
        var name = status.AccountUrl is null ? status.ProviderName : $"{status.ProviderName} ↗";
        var nameWidth = Math.Min(Math.Max(1, right - nameLeft), TextRenderer.MeasureText(graphics, name, _fonts.Name).Width);
        var nameBounds = new Rectangle(nameLeft, row.Top + (row.Height - _fonts.Name.Height) / 2, nameWidth, _fonts.Name.Height);
        if (status.AccountUrl is not null)
        {
            DrawLink(graphics, name, _fonts.Name, nameBounds, ProviderCardAction.OpenAccount, status, Theme.Text);
        }
        else
        {
            Cell(graphics, name, _fonts.Name, Theme.Text, nameBounds, TextFormatFlags.EndEllipsis);
        }

        var identity = Identity(status);
        var identityLeft = nameBounds.Right + scale[10];
        if (!string.IsNullOrEmpty(identity) && right - identityLeft > scale[24])
        {
            Cell(
                graphics,
                identity,
                _fonts.Small,
                Theme.Muted,
                new Rectangle(identityLeft, row.Top, right - identityLeft, row.Height),
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        if (!retryBounds.IsEmpty)
        {
            DrawLink(graphics, "Retry", _fonts.Small, retryBounds, ProviderCardAction.Refresh, status);
        }
    }

    private void DrawGroup(Graphics graphics, LayoutScale scale, Rectangle row, Row data)
    {
        using (var rule = new Pen(Theme.Divider))
        {
            graphics.DrawLine(rule, scale[Indent], row.Top, Width, row.Top);
        }

        Cell(
            graphics,
            data.Label ?? string.Empty,
            _fonts.Group,
            Theme.TextSecondary,
            new Rectangle(scale[Indent], row.Top, Width - scale[Indent], row.Height),
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void DrawMetric(Graphics graphics, LayoutScale scale, Columns columns, Rectangle row, Row data)
    {
        var status = data.Status!;
        var metric = data.Metric!;
        if (!data.Nested)
        {
            using var rule = new Pen(Theme.Divider);
            graphics.DrawLine(rule, scale[Indent], row.Top, Width, row.Top);
        }

        var dim = status.IsStale;
        var labelLeft = scale[data.Nested ? Indent + 12 : Indent];
        var middle = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        Cell(
            graphics,
            data.Label ?? metric.Name,
            _fonts.Label,
            Theme.TextSecondary,
            new Rectangle(labelLeft, row.Top, Math.Max(1, columns.Label - labelLeft), row.Height),
            middle);

        DrawUsageCell(graphics, scale, new Rectangle(columns.UsageLeft, row.Top, columns.Usage, row.Height), status.ProviderId, metric, dim);

        if (metric.HasQuota)
        {
            var used = metric.UsedPercent!.Value;
            var cue = Theme.IsHighContrast ? Theme.UsageCue(used) : string.Empty;
            Cell(
                graphics,
                $"{cue}{metric.DisplayedPercent(_quotaDisplay)}%",
                _fonts.Figure,
                dim ? Theme.Muted : Theme.TextForUsage(used),
                new Rectangle(columns.UsedLeft, row.Top, columns.Used, row.Height),
                middle | TextFormatFlags.Right);
        }
        else
        {
            Dash(graphics, new Rectangle(columns.UsedLeft, row.Top, columns.Used, row.Height), TextFormatFlags.Right);
        }

        var reset = UsageFormatting.CompactReset(metric.ResetsAt, DateTimeOffset.Now);
        if (reset.Length > 0)
        {
            Cell(graphics, reset, _fonts.Label, Theme.TextSecondary, new Rectangle(columns.ResetLeft, row.Top, columns.Reset, row.Height), middle | TextFormatFlags.Right);
        }
        else
        {
            Dash(graphics, new Rectangle(columns.ResetLeft, row.Top, columns.Reset, row.Height), TextFormatFlags.Right);
        }

        if (columns.Pace <= 0)
        {
            return;
        }

        var pace = new Rectangle(columns.PaceLeft, row.Top, columns.Pace, row.Height);
        if (data.Projection is { BeforeReset: true } projection)
        {
            Cell(
                graphics,
                $"Out by {UsageFormatting.AbsoluteReset(projection.ExhaustedAt, DateTimeOffset.Now, System.Globalization.CultureInfo.CurrentCulture)}",
                _fonts.Small,
                Theme.IsCriticalUsage(metric.UsedPercent ?? 0) ? Theme.CriticalText : Theme.WarningText,
                pace,
                middle);
        }
        else if (data.Pace is { } usagePace)
        {
            // Not running out before the reset, so the column says how use compares with the clock:
            // over pace in the warning colour, on or under pace quietly.
            Cell(graphics, usagePace.Text, _fonts.Small, usagePace.IsOverPace ? Theme.WarningText : Theme.Faint, pace, middle);
        }
        else if (data.Projection is not null)
        {
            Cell(graphics, "Resets first", _fonts.Small, Theme.Faint, pace, middle);
        }
        else
        {
            Dash(graphics, pace, TextFormatFlags.Left);
        }
    }

    private void DrawUsageCell(Graphics graphics, LayoutScale scale, Rectangle cell, string providerId, UsageMetric metric, bool dim)
    {
        var middle = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        if (metric.IsUnlimited)
        {
            Cell(graphics, "Unlimited", _fonts.Small, Theme.Muted, cell, middle);
            return;
        }

        if (!metric.HasQuota)
        {
            // A balance has no percentage to meter: its value, then what it is.
            var value = metric.RemainingText ?? metric.DisplayRemaining;
            var valueWidth = Math.Min(cell.Width, TextRenderer.MeasureText(graphics, value, _fonts.Figure).Width);
            Cell(graphics, value, _fonts.Figure, Theme.Text, new Rectangle(cell.Left, cell.Top, valueWidth, cell.Height), middle);
            if (!string.IsNullOrWhiteSpace(metric.UsageText) && cell.Width - valueWidth > scale[40])
            {
                Cell(
                    graphics,
                    metric.UsageText,
                    _fonts.Small,
                    Theme.Muted,
                    new Rectangle(cell.Left + valueWidth + scale[10], cell.Top, cell.Width - valueWidth - scale[10], cell.Height),
                    middle);
            }

            return;
        }

        var detail = cell.Width >= scale[UsageDetailMinimumWidth] ? SentenceCase(metric.DisplaySecondaryFor(_quotaDisplay)) : string.Empty;
        var detailWidth = detail.Length == 0
            ? 0
            : Math.Min(cell.Width / 2, TextRenderer.MeasureText(graphics, detail, _fonts.Small).Width);
        var barWidth = Math.Max(scale[24], cell.Width - (detailWidth > 0 ? detailWidth + scale[12] : 0));
        var used = metric.UsedPercent!.Value;
        var fill = dim ? Theme.Blend(Theme.ForUsage(used), Theme.Night, 0.45) : Theme.ForUsage(used);
        // Tweened on what was used, so flipping to "left" redraws in place rather than sweeping.
        var key = $"row|{providerId}|{metric.Name}";
        var bar = new Rectangle(cell.Left, cell.Top + (cell.Height - scale[BarHeight]) / 2, barWidth, scale[BarHeight]);
        var area = Rectangle.Inflate(bar, scale[2], scale[2]);
        var shownUsed = Motion?.Value(this, key, Math.Clamp(used, 0, 100), area) ?? used;
        DrawingHelpers.DrawCapacityMeter(
            graphics,
            bar,
            _quotaDisplay == QuotaDisplay.Remaining ? 100 - shownUsed : shownUsed,
            Motion?.Color(this, key, fill, area) ?? fill,
            Theme.Track);
        if (detailWidth > 0)
        {
            Cell(
                graphics,
                detail,
                _fonts.Small,
                Theme.Muted,
                new Rectangle(cell.Right - detailWidth, cell.Top, detailWidth, cell.Height),
                middle | TextFormatFlags.Right);
        }
    }

    private void DrawMessage(Graphics graphics, LayoutScale scale, Rectangle row, Row data)
    {
        var color = data.Tone switch
        {
            Tone.Critical => Theme.CriticalText,
            Tone.Warning => Theme.WarningText,
            _ => Theme.Muted,
        };
        var left = scale[Indent];
        var textHeight = data.HasAction ? row.Height - scale[36] : row.Height;
        Cell(
            graphics,
            data.Text ?? string.Empty,
            _fonts.Small,
            color,
            new Rectangle(left, row.Top, Math.Max(1, Width - left), textHeight),
            TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

        if (!data.HasAction || data.Status?.SignInCommand is not { Length: > 0 } command)
        {
            return;
        }

        var label = $"Copy  {command}";
        var width = Math.Min(Width - left, TextRenderer.MeasureText(graphics, label, _fonts.Small).Width + scale[20]);
        var button = new Rectangle(left, row.Bottom - scale[34], width, scale[28]);
        var hovered = _actions.Count == _hoverIndex;
        DrawingHelpers.FillCard(
            graphics,
            button,
            hovered ? Theme.Blend(Theme.Text, Theme.SurfaceRaised, 0.08) : Theme.SurfaceRaised,
            Theme.Hairline,
            scale.Exact(6));
        Cell(graphics, label, _fonts.Small, Theme.Text, button, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        _actions.Add(new ActionTarget(button, ProviderCardAction.CopyCommand, data.Status));
    }

    private void DrawLink(
        Graphics graphics,
        string text,
        Font font,
        Rectangle bounds,
        ProviderCardAction action,
        ProviderStatus status,
        Color? color = null)
    {
        var hovered = _actions.Count == _hoverIndex;
        Cell(graphics, text, font, color ?? Theme.Signal, bounds, TextFormatFlags.EndEllipsis);
        if (hovered)
        {
            using var underline = new Pen(color ?? Theme.Signal);
            graphics.DrawLine(underline, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
        }

        _actions.Add(new ActionTarget(bounds, action, status));
    }

    private void Dash(Graphics graphics, Rectangle cell, TextFormatFlags alignment) =>
        Cell(graphics, "–", _fonts.Label, Theme.Faint, cell, alignment | TextFormatFlags.VerticalCenter);

    private static void Cell(Graphics graphics, string text, Font font, Color color, Rectangle bounds, TextFormatFlags flags)
    {
        if (bounds.Width > 0 && bounds.Height > 0 && !string.IsNullOrEmpty(text))
        {
            DrawingHelpers.DrawText(graphics, text, font, color, bounds, flags);
        }
    }

    private static (string Text, Color Color) StatusFor(ProviderStatus status)
    {
        var now = DateTimeOffset.Now;
        if (status.IsLoading)
        {
            return ("Refreshing…", Theme.Muted);
        }

        // The provider's own status page explains a stale card better than a retry countdown, so an
        // incident leads; the text names it, so the colour is never the only cue.
        if (status.IncidentSummary is { } incident && status.IsConnected)
        {
            return (status.IsStale ? $"{incident} · stale" : incident, status.IncidentIsOutage ? Theme.CriticalText : Theme.WarningText);
        }

        if (status.IsStale)
        {
            var age = status.LastUpdated is { } updated ? $"Last good {UsageFormatting.Age(updated, now)}" : "Stale";
            return (status.NextRetryAt is { } retryAt ? $"{age} · {UsageFormatting.RetryCountdown(retryAt, now)}" : age, Theme.WarningText);
        }

        if (!status.IsConnected)
        {
            return (status.IncidentSummary is { } down ? $"Not connected · {down}" : "Not connected", Theme.CriticalText);
        }

        return (status.LastUpdated is { } last ? $"Connected · {UsageFormatting.Age(last, now)}" : "Connected", Theme.Faint);
    }

    private static string Identity(ProviderStatus status) => status.Snapshot is not { } snapshot
        ? string.Empty
        : string.IsNullOrWhiteSpace(snapshot.AccountName)
            ? snapshot.Plan
            : $"{snapshot.Plan} · {snapshot.AccountName}";

    /// <summary>Providers' shouted fallbacks ("8% LEFT") read as sentence case in a table.</summary>
    private static string SentenceCase(string text) =>
        text.Any(char.IsLetter) && string.Equals(text, text.ToUpperInvariant(), StringComparison.Ordinal)
            ? text.ToLowerInvariant()
            : text;

    private string Describe(IReadOnlyList<ProviderStatus> states)
    {
        var parts = new List<string>();
        foreach (var status in states)
        {
            var line = new List<string> { status.ProviderName, StatusFor(status).Text };
            foreach (var row in _rows.Where(row => row.Kind == RowKind.Metric && ReferenceEquals(row.Status, status)))
            {
                var metric = row.Metric!;
                var value = metric.HasQuota
                    ? metric.DisplayPrimary(_quotaDisplay).ToLowerInvariant()
                    : metric.IsUnlimited ? "unlimited" : metric.RemainingText ?? metric.DisplayRemaining;
                var reset = UsageFormatting.RelativeReset(metric.ResetsAt, DateTimeOffset.Now);
                line.Add(string.IsNullOrEmpty(reset) ? $"{metric.Name}: {value}" : $"{metric.Name}: {value}, {reset}");
            }

            foreach (var row in _rows.Where(row => row.Kind == RowKind.Message && ReferenceEquals(row.Status, status)))
            {
                line.Add(row.Text ?? string.Empty);
            }

            parts.Add(string.Join(", ", line));
        }

        return string.Join(". ", parts);
    }

    private void Invoke(ActionTarget target) =>
        ActionInvoked?.Invoke(this, new ProviderCardActionEventArgs(target.Status!, target.Action));

    private void MoveFocus(int index)
    {
        _focusIndex = Math.Clamp(index, 0, Math.Max(0, _actions.Count - 1));
        if (_focusIndex < _actions.Count && Parent is ScrollableControl { AutoScroll: true } scroller)
        {
            // Keep the focused action on screen when the dashboard scrolls.
            var bounds = _actions[_focusIndex].Bounds;
            var view = -scroller.AutoScrollPosition.Y;
            var top = Top - scroller.AutoScrollPosition.Y + bounds.Top;
            if (top < view || top + bounds.Height > view + scroller.ClientSize.Height)
            {
                scroller.AutoScrollPosition = new Point(0, Math.Max(0, top - scroller.ClientSize.Height / 3));
            }
        }

        Invalidate();
    }

    private int EffectiveDpi =>
        _dpiOverride ?? LayoutScale.DpiOf(this);

    private LayoutScale Scale() => new(EffectiveDpi);

    private void ApplyDpiChange()
    {
        var dpi = EffectiveDpi;
        if (_fonts.Dpi != dpi)
        {
            _fonts.Dispose();
            _fonts = new LedgerFonts(dpi);
        }

        if (Height != NaturalHeight)
        {
            Height = NaturalHeight;
        }

        Invalidate();
    }

    internal enum RowKind
    {
        Columns,
        Provider,
        Group,
        Metric,
        Message,
        Gap,
    }

    internal enum Tone
    {
        Quiet,
        Warning,
        Critical,
    }

    internal sealed record Row(
        RowKind Kind,
        int BaselineHeight,
        ProviderStatus? Status = null,
        UsageMetric? Metric = null,
        string? Label = null,
        string? Text = null,
        Tone Tone = Tone.Quiet,
        bool HasAction = false,
        UsageProjection? Projection = null,
        bool Nested = false,
        UsagePace? Pace = null);

    internal sealed record ActionTarget(Rectangle Bounds, ProviderCardAction Action, ProviderStatus? Status);

    private readonly record struct Columns(
        int LabelLeft,
        int Label,
        int UsageLeft,
        int Usage,
        int UsedLeft,
        int Used,
        int ResetLeft,
        int Reset,
        int PaceLeft,
        int Pace);

    /// <summary>The table's painting faces, realised in pixels for one monitor's DPI.</summary>
    private sealed class LedgerFonts : IDisposable
    {
        internal LedgerFonts(int dpi)
        {
            Dpi = dpi;
            Name = Typography.Display(10F, dpi);
            Group = Typography.Text(8.5F, dpi, FontStyle.Bold);
            Label = Typography.Text(9F, dpi);
            Small = Typography.Text(8F, dpi);
            Figure = Typography.Figures(10F, dpi);
        }

        internal int Dpi { get; }

        internal Font Name { get; }

        internal Font Group { get; }

        internal Font Label { get; }

        internal Font Small { get; }

        internal Font Figure { get; }

        public void Dispose()
        {
            Name.Dispose();
            Group.Dispose();
            Label.Dispose();
            Small.Dispose();
            Figure.Dispose();
        }
    }
}
