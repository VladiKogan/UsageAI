using System.Drawing.Drawing2D;
using System.Drawing.Text;
using UsageAI.Models;

namespace UsageAI.UI;

/// <summary>
/// One provider in the popup: a glyph and name, then up to two ring gauges side by side — the
/// shortest window first — each with its figure inside and its window and countdown beneath.
/// Rings are the at-a-glance view; the dashboard holds the detail, so choosing a tile opens it.
/// A ring and its figure encode the same quantity (used, or remaining when the quota display
/// asks for it); colour always follows the used percentage.
/// </summary>
internal sealed class ProviderTile : Control
{
    private const int TileHeight = 156;
    private const int Inset = 12;
    private const int HeaderTop = 12;
    private const int HeaderHeight = 20;
    private const int GlyphSize = 16;
    private const int IndicatorSize = 18;
    private const int RingTop = 44;
    private const int RingDiameter = 60;
    private const int RingStroke = 6;
    private const int LabelTop = 110;
    private const int ResetTop = 126;
    private const int ColumnGap = 8;
    private const int Radius = 8;

    private readonly ProviderStatus _status;
    private readonly QuotaDisplay _quotaDisplay;
    private readonly PopupRings? _rings;
    private readonly int? _dpiOverride;
    private readonly ThemedToolTip _toolTip;
    private readonly bool _ownsToolTip;
    private TileFonts _fonts;
    private Rectangle _indicatorBounds = Rectangle.Empty;
    private bool _toolTipVisible;

    public ProviderTile(
        ProviderStatus status,
        QuotaDisplay quotaDisplay = QuotaDisplay.Used,
        string? preferredGroup = null,
        int? dpiOverride = null,
        ThemedToolTip? sharedToolTip = null)
    {
        _status = status;
        _quotaDisplay = quotaDisplay;
        _dpiOverride = dpiOverride;
        _rings = status.Snapshot is { } snapshot
            ? PopupMetricSelection.Select(snapshot.Metrics, preferredGroup)
            : null;
        _fonts = new TileFonts(EffectiveDpi);
        // The popup shares one tooltip among its tiles: they are rebuilt on every provider update,
        // and at most one tooltip is ever visible.
        _ownsToolTip = sharedToolTip is null;
        _toolTip = sharedToolTip ?? new ThemedToolTip();
        DoubleBuffered = true;
        ResizeRedraw = true;
        TabStop = true;
        SetStyle(ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PushButton;
        ApplyAccessibility();
        Height = NaturalHeight;
    }

    public event EventHandler<ProviderCardActionEventArgs>? ActionInvoked;

    public int NaturalHeight => Scale()[TileHeight];

    /// <summary>The rings this tile shows; null until the provider has reported usage.</summary>
    internal PopupRings? Rings => _rings;

    /// <summary>What hovering or focusing the ⓘ (or stale) indicator explains.</summary>
    internal string? IndicatorText => _status.IsStale
        ? StaleText()
        : _rings is { HasGroupChoice: true } ? GroupText(_rings) : null;

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        ApplyDpiChange();
    }

    // Until the handle exists DeviceDpi still reports the start-up DPI, so the tile re-measures
    // here, once it knows which monitor it is really on.
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

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_indicatorBounds.Contains(e.Location) && IndicatorText is { } text)
        {
            ShowIndicatorText(text);
        }
        else
        {
            HideIndicatorText();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        HideIndicatorText();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Focused)
        {
            Focus();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left || _indicatorBounds.Contains(e.Location))
        {
            return;
        }

        ActionInvoked?.Invoke(this, new ProviderCardActionEventArgs(_status, ProviderCardAction.OpenDetails));
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is not (Keys.Enter or Keys.Space))
        {
            return;
        }

        ActionInvoked?.Invoke(this, new ProviderCardActionEventArgs(_status, ProviderCardAction.OpenDetails));
        e.Handled = true;
    }

    // Keyboard users reach the indicator's explanation by focusing the tile.
    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (IndicatorText is { } text && !_indicatorBounds.IsEmpty)
        {
            ShowIndicatorText(text);
        }

        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        HideIndicatorText();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var scale = Scale();
        if (Width < scale[60] || Height < scale[40])
        {
            return;
        }

        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        _indicatorBounds = Rectangle.Empty;
        var tile = new Rectangle(0, 0, Width - 1, Height - 1);
        DrawingHelpers.FillCard(graphics, tile, Theme.Surface, Theme.Divider, scale.Exact(Radius));

        DrawHeader(graphics, scale);
        if (_status.Snapshot is null)
        {
            // The popup lists connected providers, plus loading ones while nothing has connected
            // yet; a signed-out provider's sign-in help lives on the dashboard.
            if (_status.IsLoading)
            {
                DrawLoading(graphics, scale);
            }
            else
            {
                DrawMessage(graphics, scale, _status.Error ?? "Not connected", Theme.Muted);
            }
        }
        else if (_rings is { Rings.Count: > 0 } rings)
        {
            DrawRings(graphics, scale, rings.Rings);
        }
        else
        {
            DrawTextRows(graphics, scale, _status.Snapshot.Metrics);
        }

        if (Focused && ShowFocusCues)
        {
            using var focus = DrawingHelpers.RoundedRectangle(Rectangle.Inflate(tile, -1, -1), scale.Exact(Radius));
            using var pen = new Pen(Theme.Accent, Math.Max(1F, scale.Exact(1.5F)));
            graphics.DrawPath(pen, focus);
        }
    }

    private void DrawHeader(Graphics graphics, LayoutScale scale)
    {
        ProviderIconPainter.Draw(
            graphics,
            scale.Rect(Inset, HeaderTop + 2, GlyphSize, GlyphSize),
            _status.ProviderId);

        var indicator = IndicatorText is null
            ? Rectangle.Empty
            : new Rectangle(
                Width - scale[Inset] - scale[IndicatorSize],
                scale[HeaderTop + 1],
                scale[IndicatorSize],
                scale[IndicatorSize]);
        var nameLeft = scale[Inset + GlyphSize + 8];
        var nameRight = indicator.IsEmpty ? Width - scale[Inset] : indicator.Left - scale[6];
        DrawingHelpers.DrawText(
            graphics,
            _status.ProviderName,
            _fonts.Name,
            Theme.Text,
            new Rectangle(nameLeft, scale[HeaderTop], Math.Max(1, nameRight - nameLeft), scale[HeaderHeight]),
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);

        if (indicator.IsEmpty)
        {
            return;
        }

        _indicatorBounds = Rectangle.Inflate(indicator, scale[3], scale[3]);
        var stale = _status.IsStale;
        var color = stale ? Theme.WarningText : Theme.Muted;
        using (var ring = new Pen(color, Math.Max(1F, scale.Exact(1.2F))))
        {
            graphics.DrawEllipse(ring, RectangleF.Inflate(indicator, -scale.Exact(1), -scale.Exact(1)));
        }

        DrawingHelpers.DrawText(
            graphics,
            stale ? "!" : "i",
            _fonts.Indicator,
            color,
            indicator,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void DrawRings(Graphics graphics, LayoutScale scale, IReadOnlyList<UsageMetric> rings)
    {
        var columnWidth = (Width - 2 * scale[Inset] - scale[ColumnGap]) / 2;
        var dim = _status.IsStale;
        for (var index = 0; index < rings.Count; index++)
        {
            var metric = rings[index];
            var columnLeft = scale[Inset] + index * (columnWidth + scale[ColumnGap]);
            DrawRingColumn(graphics, scale, metric, columnLeft, columnWidth, dim);
        }

        if (rings.Count == 1)
        {
            DrawSideNote(graphics, scale, rings[0], scale[Inset] + columnWidth + scale[ColumnGap], columnWidth);
        }
    }

    private void DrawRingColumn(
        Graphics graphics,
        LayoutScale scale,
        UsageMetric metric,
        int columnLeft,
        int columnWidth,
        bool dim)
    {
        var diameter = scale.Exact(RingDiameter);
        var circle = new RectangleF(
            columnLeft + (columnWidth - diameter) / 2F,
            scale[RingTop],
            diameter,
            diameter);
        var used = metric.UsedPercent!.Value;
        var fill = Theme.ForUsage(used);
        var track = Theme.Track;
        if (dim)
        {
            // Stale values stay readable but visibly older than fresh ones.
            fill = Theme.Blend(fill, Theme.Surface, 0.45);
        }

        DrawingHelpers.DrawRing(
            graphics,
            circle,
            scale.Exact(RingStroke),
            metric.DisplayedPercent(_quotaDisplay),
            fill,
            track);

        var figure = $"{(Theme.IsHighContrast ? Theme.UsageCue(used).Trim() : string.Empty)}{metric.DisplayedPercent(_quotaDisplay)}%";
        var figureHeight = _fonts.Figure.Height;
        var captionHeight = _fonts.Caption.Height;
        var stackTop = (int)Math.Round(circle.Top + (circle.Height - figureHeight - captionHeight) / 2F);
        var inner = new Rectangle((int)circle.Left, stackTop, (int)circle.Width, figureHeight);
        DrawingHelpers.DrawText(
            graphics,
            figure,
            _fonts.Figure,
            dim ? Theme.Muted : Theme.TextForUsage(used),
            inner,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.NoClipping);
        DrawingHelpers.DrawText(
            graphics,
            _quotaDisplay == QuotaDisplay.Remaining ? "left" : "used",
            _fonts.Caption,
            Theme.Faint,
            new Rectangle(inner.Left, inner.Bottom - scale[1], inner.Width, captionHeight),
            TextFormatFlags.HorizontalCenter);

        DrawingHelpers.DrawText(
            graphics,
            PopupMetricSelection.RingLabel(metric),
            _fonts.Label,
            Theme.TextSecondary,
            new Rectangle(columnLeft, scale[LabelTop], columnWidth, _fonts.Label.Height),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
        DrawingHelpers.DrawText(
            graphics,
            UsageFormatting.CompactReset(metric.ResetsAt, DateTimeOffset.Now),
            _fonts.Small,
            Theme.Faint,
            new Rectangle(columnLeft, scale[ResetTop], columnWidth, _fonts.Small.Height),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
    }

    /// <summary>
    /// A one-ring provider spends the free column on the figure a ring cannot show: an account
    /// balance (Codex credits), or how much of the allowance is left (Copilot's AI credits).
    /// </summary>
    private void DrawSideNote(Graphics graphics, LayoutScale scale, UsageMetric ring, int left, int width)
    {
        var balance = _status.Snapshot!.Metrics.FirstOrDefault(metric => !metric.HasQuota && !metric.IsUnlimited);
        var middle = scale[RingTop] + scale[RingDiameter] / 2;
        if (balance is not null)
        {
            var labelHeight = _fonts.Small.Height;
            var figureHeight = _fonts.SideFigure.Height;
            var top = middle - (labelHeight + figureHeight) / 2;
            DrawingHelpers.DrawText(
                graphics,
                balance.Name,
                _fonts.Small,
                Theme.Muted,
                new Rectangle(left, top, width, labelHeight),
                TextFormatFlags.EndEllipsis);
            DrawingHelpers.DrawText(
                graphics,
                balance.RemainingText ?? balance.DisplayRemaining,
                _fonts.SideFigure,
                Theme.Text,
                new Rectangle(left, top + labelHeight, width, figureHeight),
                TextFormatFlags.EndEllipsis);
            return;
        }

        if (string.IsNullOrWhiteSpace(ring.UsageText))
        {
            return;
        }

        var height = _fonts.Small.Height * 3;
        DrawingHelpers.DrawText(
            graphics,
            ring.UsageText,
            _fonts.Small,
            Theme.Muted,
            new Rectangle(left, middle - height / 2, width, height),
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
    }

    /// <summary>A provider reporting only balances or unlimited allowances has nothing to ring.</summary>
    private void DrawTextRows(Graphics graphics, LayoutScale scale, IReadOnlyList<UsageMetric> metrics)
    {
        if (metrics.Count == 0)
        {
            DrawMessage(graphics, scale, "No metered limits reported", Theme.Muted);
            return;
        }

        var top = scale[RingTop];
        foreach (var metric in metrics.Take(3))
        {
            var value = metric.IsUnlimited ? "Unlimited" : metric.RemainingText ?? metric.DisplayRemaining;
            var valueWidth = TextRenderer.MeasureText(graphics, value, _fonts.SideFigure).Width;
            var rowHeight = _fonts.SideFigure.Height;
            DrawingHelpers.DrawText(
                graphics,
                metric.Name,
                _fonts.Label,
                Theme.TextSecondary,
                new Rectangle(scale[Inset], top, Math.Max(1, Width - 2 * scale[Inset] - valueWidth - scale[6]), rowHeight),
                TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
            DrawingHelpers.DrawText(
                graphics,
                value,
                _fonts.SideFigure,
                metric.IsUnlimited ? Theme.Muted : Theme.Text,
                new Rectangle(Width - scale[Inset] - valueWidth, top, valueWidth, rowHeight),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            top += rowHeight + scale[8];
        }
    }

    private void DrawLoading(Graphics graphics, LayoutScale scale)
    {
        // The loading state takes the final layout: two empty tracks where the rings will land,
        // with no shimmer or animation that would compete with the tiles that have data.
        var columnWidth = (Width - 2 * scale[Inset] - scale[ColumnGap]) / 2;
        var diameter = scale.Exact(RingDiameter);
        for (var index = 0; index < PopupMetricSelection.MaximumRings; index++)
        {
            var columnLeft = scale[Inset] + index * (columnWidth + scale[ColumnGap]);
            var circle = new RectangleF(columnLeft + (columnWidth - diameter) / 2F, scale[RingTop], diameter, diameter);
            DrawingHelpers.DrawRing(graphics, circle, scale.Exact(RingStroke), 0, Theme.Track, Theme.Track);
        }

        DrawingHelpers.DrawText(
            graphics,
            "Checking usage…",
            _fonts.Small,
            Theme.Faint,
            new Rectangle(scale[Inset], scale[LabelTop + 6], Width - 2 * scale[Inset], _fonts.Small.Height),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
    }

    private void DrawMessage(Graphics graphics, LayoutScale scale, string message, Color color) =>
        DrawingHelpers.DrawText(
            graphics,
            message,
            _fonts.Small,
            color,
            new Rectangle(scale[Inset], scale[RingTop], Width - 2 * scale[Inset], Height - scale[RingTop] - scale[Inset]),
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

    private void ShowIndicatorText(string text)
    {
        if (_toolTipVisible || !IsHandleCreated)
        {
            return;
        }

        _toolTipVisible = true;
        _toolTip.ShowRightAligned(text, this, Rectangle.Inflate(_indicatorBounds, 0, Scale()[4]));
    }

    private void HideIndicatorText()
    {
        if (!_toolTipVisible)
        {
            return;
        }

        _toolTipVisible = false;
        if (IsHandleCreated)
        {
            _toolTip.Hide(this);
        }
    }

    private string StaleText()
    {
        var age = _status.LastUpdated is { } updated
            ? $"Last good reading {UsageFormatting.Age(updated, DateTimeOffset.Now)}."
            : "These values are from an earlier reading.";
        var retry = _status.NextRetryAt is { } retryAt
            ? $" Next try: {UsageFormatting.RetryCountdown(retryAt, DateTimeOffset.Now)}."
            : string.Empty;
        return string.IsNullOrWhiteSpace(_status.Error)
            ? age + retry
            : $"{age}{retry}\n{_status.Error}";
    }

    private string GroupText(PopupRings rings)
    {
        var lines = new List<string>
        {
            rings.GroupIsAutomatic ? $"{rings.Group} (closest to its limit)" : rings.Group ?? string.Empty,
        };
        lines.AddRange(rings.Rings.Select(metric =>
        {
            var reset = UsageFormatting.RelativeReset(metric.ResetsAt, DateTimeOffset.Now);
            var value = metric.DisplayPrimary(_quotaDisplay).ToLowerInvariant();
            return string.IsNullOrEmpty(reset)
                ? $"{PopupMetricSelection.RingLabel(metric)} · {value}"
                : $"{PopupMetricSelection.RingLabel(metric)} · {value} · {reset}";
        }));
        lines.Add("Choose which models show in Settings › Providers.");
        return string.Join('\n', lines);
    }

    private void ApplyAccessibility()
    {
        AccessibleName = $"{_status.ProviderName} usage";
        var parts = new List<string> { _status.StatusText };
        if (_status.Snapshot is { } snapshot)
        {
            parts.Add(snapshot.Plan);
            foreach (var metric in _rings?.Rings ?? Array.Empty<UsageMetric>())
            {
                var reset = UsageFormatting.RelativeReset(metric.ResetsAt, DateTimeOffset.Now);
                var value = metric.DisplayPrimary(_quotaDisplay).ToLowerInvariant();
                parts.Add(string.IsNullOrEmpty(reset) ? $"{metric.Name}: {value}" : $"{metric.Name}: {value}, {reset}");
            }
        }

        if (IndicatorText is { } indicator)
        {
            parts.Add(indicator.Replace('\n', ' '));
        }
        else if (!string.IsNullOrWhiteSpace(_status.Error))
        {
            parts.Add(_status.Error);
        }

        parts.Add("Press Enter to open the dashboard");
        AccessibleDescription = string.Join(". ", parts);
    }

    private int EffectiveDpi =>
        _dpiOverride ?? (IsHandleCreated ? DeviceDpi : Parent?.DeviceDpi ?? DeviceDpi);

    private LayoutScale Scale() => new(EffectiveDpi);

    private void ApplyDpiChange()
    {
        var dpi = EffectiveDpi;
        if (_fonts.Dpi != dpi)
        {
            _fonts.Dispose();
            _fonts = new TileFonts(dpi);
        }

        if (Height != NaturalHeight)
        {
            Height = NaturalHeight;
        }

        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            HideIndicatorText();
            if (_ownsToolTip)
            {
                _toolTip.Dispose();
            }

            _fonts.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>The tile's painting faces, realised in pixels for one monitor's DPI.</summary>
    private sealed class TileFonts : IDisposable
    {
        internal TileFonts(int dpi)
        {
            Dpi = dpi;
            Name = Typography.Display(9.5F, dpi);
            Figure = Typography.Figures(11F, dpi);
            SideFigure = Typography.Figures(10F, dpi);
            Caption = Typography.Text(6.5F, dpi);
            Label = Typography.Text(8F, dpi);
            Small = Typography.Text(7.5F, dpi);
            Indicator = Typography.Text(7F, dpi, FontStyle.Bold);
        }

        internal int Dpi { get; }

        internal Font Name { get; }

        internal Font Figure { get; }

        internal Font SideFigure { get; }

        internal Font Caption { get; }

        internal Font Label { get; }

        internal Font Small { get; }

        internal Font Indicator { get; }

        public void Dispose()
        {
            Name.Dispose();
            Figure.Dispose();
            SideFigure.Dispose();
            Caption.Dispose();
            Label.Dispose();
            Small.Dispose();
            Indicator.Dispose();
        }
    }
}
