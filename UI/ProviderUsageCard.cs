using System.Drawing.Drawing2D;
using System.Drawing.Text;
using UsageAI.Models;
using UsageAI.Services;

namespace UsageAI.UI;

internal enum ProviderCardAction
{
    None,
    CopyCommand,
    OpenAccount,
}

internal sealed class ProviderCardActionEventArgs : EventArgs
{
    public ProviderCardActionEventArgs(ProviderStatus status, ProviderCardAction action)
    {
        Status = status;
        Action = action;
    }

    public ProviderStatus Status { get; }

    public ProviderCardAction Action { get; }
}

/// <summary>
/// One provider's card. Compact and expanded modes share the same grammar: a label on the
/// left, the headline value on the right, supporting detail beneath, and a consumption meter
/// across the bottom. The meter fill and the headline both encode what is used, so they
/// never move in opposite directions.
/// </summary>
internal sealed class ProviderUsageCard : Control
{
    private const int CompactHeight = 98;
    private const int HeaderHeight = 46;
    private const int MetricRowHeight = 54;
    private const int MetricRowWithTrendHeight = 72;

    // The natural heights above carry breathing room; these are what the painted stacks actually
    // occupy. A squeezed row falls back to the compressed layout only once the full one genuinely
    // stops fitting, rather than the moment it dips below its preferred height, and the trend block
    // is drawn only when the row it lands in is tall enough to hold it.
    private const int MetricRowMinimumHeight = 50;
    private const int MetricTrendTop = 52;
    private const int MetricTrendHeight = 16;
    private const int ConnectionBlockHeight = 78;
    private const int CardRadius = 12;
    private const int Gutter = 14;

    private readonly bool _expanded;
    private readonly ProviderStatus _status;
    private readonly IReadOnlyList<UsageSample> _history;
    private readonly bool _showTrend;
    private readonly IReadOnlyList<UsageMetric> _metrics;
    private readonly int? _dpiOverride;
    private CardFonts _fonts;
    private int _measuredDpi;
    private Rectangle _actionBounds = Rectangle.Empty;
    private Rectangle _linkBounds = Rectangle.Empty;

    public int NaturalHeight { get; private set; }

    public ProviderUsageCard(
        ProviderStatus status,
        bool expanded,
        IReadOnlyList<UsageSample> history,
        bool showTrend,
        IReadOnlyList<UsageMetric>? metrics = null,
        int? dpiOverride = null)
    {
        _status = status;
        _expanded = expanded;
        _history = history;
        _showTrend = showTrend && expanded;
        _metrics = expanded
            ? metrics ?? status.Snapshot?.Metrics ?? Array.Empty<UsageMetric>()
            : status.Snapshot?.Metrics ?? Array.Empty<UsageMetric>();
        _dpiOverride = dpiOverride;
        _fonts = new CardFonts(EffectiveDpi);
        DoubleBuffered = true;
        ResizeRedraw = true;
        TabStop = expanded;
        SetStyle(ControlStyles.Selectable, expanded);
        Margin = new Padding(0, 0, 0, 10);
        AccessibleRole = AccessibleRole.Grouping;
        ApplyAccessibility();
        ApplyHeight();
    }

    public event EventHandler<ProviderCardActionEventArgs>? ActionInvoked;

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        EnsureFonts();
        ApplyHeight();
    }

    // The handle is the first moment the card knows which monitor it is on: until then
    // Control.DeviceDpi still reports the DPI the process started on. WinForms updates the value
    // here but re-measures nothing the card painted for itself, so without this hook a card
    // realised on a second monitor kept a height measured for the primary one.
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
        Cursor = HitTest(e.Location) == ProviderCardAction.None ? Cursors.Default : Cursors.Hand;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_expanded && !Focused)
        {
            Focus();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        var action = HitTest(e.Location);
        if (action != ProviderCardAction.None)
        {
            ActionInvoked?.Invoke(this, new ProviderCardActionEventArgs(_status, action));
        }
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

        var action = PrimaryAction();
        if (action != ProviderCardAction.None)
        {
            ActionInvoked?.Invoke(this, new ProviderCardActionEventArgs(_status, action));
            e.Handled = true;
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
        if (Width < 40 || Height < 20)
        {
            return;
        }

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        _actionBounds = Rectangle.Empty;
        _linkBounds = Rectangle.Empty;

        var scale = Scale();
        var card = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        var providerColor = Theme.ForProvider(_status.ProviderId);
        var severity = SeverityColor(providerColor);
        var isCritical = IsCritical();

        DrawingHelpers.FillCard(
            e.Graphics,
            card,
            Theme.Surface,
            isCritical ? Theme.Blend(Theme.Critical, Theme.Hairline, 0.55) : Theme.Hairline,
            scale.Exact(CardRadius));

        // The provider rail turns to the severity colour so urgency is visible before reading.
        using (var rail = new SolidBrush(isCritical ? Theme.Critical : providerColor))
        using (var railPath = DrawingHelpers.RoundedRectangle(
                   new Rectangle(0, scale[16], scale[3], Math.Max(scale[12], Height - scale[32])),
                   scale.Exact(1.5F)))
        {
            e.Graphics.FillPath(rail, railPath);
        }

        if (_expanded)
        {
            DrawExpanded(e.Graphics, scale, providerColor, severity);
        }
        else
        {
            DrawCompact(e.Graphics, scale, providerColor, severity);
        }

        if (Focused && _expanded)
        {
            using var focus = new Pen(Theme.Accent, scale.Exact(1.5F)) { DashStyle = DashStyle.Dot };
            using var focusPath = DrawingHelpers.RoundedRectangle(
                Rectangle.Inflate(card, -scale[2], -scale[2]),
                scale.Exact(CardRadius));
            e.Graphics.DrawPath(focus, focusPath);
        }
    }

    private void DrawCompact(Graphics graphics, LayoutScale scale, Color providerColor, Color severity)
    {
        ProviderIconPainter.Draw(graphics, scale.Rect(14, 16, 34, 34), _status.ProviderId);

        var textLeft = scale[58];
        var metric = _status.Snapshot?.Primary;
        var valueText = metric is { HasQuota: true }
            ? Theme.UsageCue(metric.UsedPercent!.Value) + metric.DisplayUsed
            : metric?.DisplayUsed ?? (_status.IsLoading ? "..." : "--");
        var valueWidth = MeasureWidth(graphics, valueText, _fonts.Value);
        var nameWidth = Math.Max(scale[40], Width - textLeft - valueWidth - scale[26]);

        DrawingHelpers.DrawText(
            graphics,
            _status.ProviderName,
            _fonts.Name,
            Theme.Text,
            new Rectangle(textLeft, scale[12], nameWidth, scale[20]),
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);

        var subtitle = _status.Snapshot?.Plan ?? (_status.IsLoading ? "Connecting" : "Not connected");
        DrawingHelpers.DrawText(
            graphics,
            subtitle,
            _fonts.Small,
            _status.IsConnected ? providerColor : Theme.Muted,
            new Rectangle(textLeft, scale[34], nameWidth, scale[16]),
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);

        var valueBounds = new Rectangle(
            Width - scale[Gutter] - valueWidth,
            scale[8],
            valueWidth,
            scale[26]);
        DrawingHelpers.DrawText(graphics, valueText, _fonts.Value, severity, valueBounds, TextFormatFlags.Right);

        if (metric is not null)
        {
            DrawingHelpers.DrawText(
                graphics,
                metric.Name.ToUpperInvariant(),
                _fonts.Utility,
                Theme.Muted,
                new Rectangle(Width - scale[Gutter] - scale[160], scale[36], scale[160], scale[14]),
                TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
        }

        var detailBounds = new Rectangle(scale[14], scale[56], Width - scale[28], scale[16]);
        DrawSplitLine(
            graphics,
            detailBounds,
            DetailLeft(metric),
            DetailRight(metric),
            _fonts.Small,
            Theme.Muted,
            _status.IsStale ? Theme.Warning : Theme.Muted);

        DrawMeter(graphics, metric, new Rectangle(scale[14], scale[78], Width - scale[28], scale[5]), providerColor);
    }

    private void DrawExpanded(Graphics graphics, LayoutScale scale, Color providerColor, Color severity)
    {
        ProviderIconPainter.Draw(graphics, scale.Rect(16, 12, 36, 36), _status.ProviderId);

        var statusText = _status.StatusText;
        var statusColor = statusText switch
        {
            "Connected" => Theme.Success,
            "Refreshing" => Theme.Signal,
            "Stale" => Theme.Warning,
            _ => Theme.Critical,
        };
        var statusWidth = MeasureWidth(graphics, statusText, _fonts.Small) + scale[18];
        var nameWidth = Math.Max(scale[60], Width - scale[64] - statusWidth - scale[24]);

        var nameBounds = new Rectangle(scale[64], scale[10], nameWidth, scale[20]);
        var nameText = _status.AccountUrl is null ? _status.ProviderName : $"{_status.ProviderName}  ↗";
        DrawingHelpers.DrawText(
            graphics,
            nameText,
            _fonts.Name,
            Theme.Text,
            nameBounds,
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        if (_status.AccountUrl is not null)
        {
            _linkBounds = new Rectangle(
                nameBounds.X,
                nameBounds.Y,
                Math.Min(nameBounds.Width, MeasureWidth(graphics, nameText, _fonts.Name)),
                nameBounds.Height);
        }

        DrawingHelpers.DrawText(
            graphics,
            Identity(),
            _fonts.Small,
            _status.IsConnected ? providerColor : Theme.Muted,
            new Rectangle(scale[64], scale[32], nameWidth, scale[16]),
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);

        // Status is stated in words as well as colour, so it survives a colour-vision deficit.
        using (var dot = new SolidBrush(statusColor))
        {
            graphics.FillEllipse(dot, Width - scale[Gutter] - statusWidth, scale[18], scale[6], scale[6]);
        }

        DrawingHelpers.DrawText(
            graphics,
            statusText,
            _fonts.Small,
            statusColor,
            new Rectangle(Width - scale[Gutter] - statusWidth + scale[11], scale[12], statusWidth - scale[11], scale[18]),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        if (_status.LastUpdated is { } updated)
        {
            var age = UsageFormatting.Age(updated, DateTimeOffset.Now);
            DrawingHelpers.DrawText(
                graphics,
                _status.IsStale ? $"Last good {age}" : age,
                _fonts.Small,
                Theme.Muted,
                new Rectangle(Width - scale[Gutter] - scale[120], scale[32], scale[120], scale[16]),
                TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
        }

        var metrics = _metrics;
        if (metrics.Count == 0)
        {
            DrawConnectionBlock(graphics, scale);
            return;
        }

        var top = scale[HeaderHeight];
        var staleFooterHeight = _status.IsStale && Height >= NaturalHeight
            ? scale[20]
            : 0;
        var availableRowsHeight = Math.Max(metrics.Count, Height - top - staleFooterHeight);
        var naturalRowsHeight = metrics.Sum(metric => RowHeight(metric, scale));
        var extraPerRow = Math.Max(0, availableRowsHeight - naturalRowsHeight) / metrics.Count;
        var compressedRowHeight = Math.Max(1, availableRowsHeight / metrics.Count);

        foreach (var metric in metrics)
        {
            var rowHeight = availableRowsHeight < naturalRowsHeight
                ? compressedRowHeight
                : RowHeight(metric, scale) + extraPerRow;
            DrawMetricRow(graphics, scale, metric, top, rowHeight, providerColor, severity);
            top += rowHeight;
        }

        if (_status.IsStale && _status.Error is { Length: > 0 } staleError)
        {
            DrawingHelpers.DrawText(
                graphics,
                staleError,
                _fonts.Small,
                Theme.Warning,
                new Rectangle(scale[18], top + scale[4], Width - scale[36], scale[16]),
                TextFormatFlags.EndEllipsis);
        }
    }

    private void DrawMetricRow(
        Graphics graphics,
        LayoutScale scale,
        UsageMetric metric,
        int top,
        int rowHeight,
        Color providerColor,
        Color severity)
    {
        using (var divider = new Pen(Theme.Hairline))
        {
            graphics.DrawLine(divider, scale[Gutter], top, Width - scale[Gutter], top);
        }

        var valueColor = metric.HasQuota ? Theme.ForUsage(metric.UsedPercent!.Value) : severity;
        var valueText = metric.HasQuota
            ? Theme.UsageCue(metric.UsedPercent!.Value) + metric.DisplayUsed
            : metric.DisplayUsed;
        var valueWidth = MeasureWidth(graphics, valueText, _fonts.Value);
        var baseRowHeight = RowHeight(metric, scale);

        if (!FitsFullLayout(rowHeight, scale))
        {
            DrawCompressedMetricRow(
                graphics,
                scale,
                metric,
                top,
                rowHeight,
                valueText,
                valueWidth,
                valueColor,
                providerColor);
            return;
        }

        var contentOffset = Math.Max(0, (rowHeight - baseRowHeight) / 2);
        var contentTop = top + contentOffset;

        DrawingHelpers.DrawText(
            graphics,
            metric.Name.ToUpperInvariant(),
            _fonts.Utility,
            Theme.Muted,
            new Rectangle(scale[14], contentTop + scale[8], Math.Max(scale[40], Width - scale[28] - valueWidth - scale[8]), scale[16]),
            TextFormatFlags.EndEllipsis);

        DrawingHelpers.DrawText(
            graphics,
            valueText,
            _fonts.Value,
            metric.IsUnlimited ? Theme.Muted : valueColor,
            new Rectangle(Width - scale[14] - valueWidth, contentTop + scale[4], valueWidth, scale[22]),
            TextFormatFlags.Right);

        DrawSplitLine(
            graphics,
            new Rectangle(scale[14], contentTop + scale[26], Width - scale[28], scale[16]),
            SecondaryText(metric),
            UsageFormatting.RelativeReset(metric.ResetsAt, DateTimeOffset.Now),
            _fonts.Small,
            Theme.Muted,
            Theme.Muted);

        DrawMeter(
            graphics,
            metric,
            new Rectangle(scale[14], contentTop + scale[44], Width - scale[28], scale[4]),
            providerColor);

        // The trend is drawn against the space the row was actually given, not the space it asked
        // for. A provider reporting four metered limits is squeezed well below its natural height
        // on a two-row dashboard, and keying off the natural height instead both hid the sparkline
        // from every squeezed row and let a barely-squeezed one paint over the divider below it.
        if (baseRowHeight <= scale[MetricRowHeight] || !FitsTrend(rowHeight, contentOffset, scale))
        {
            return;
        }

        var trendTop = contentTop + scale[MetricTrendTop];
        var sparklineWidth = Math.Clamp((Width - scale[28]) / 3, scale[84], scale[220]);
        var trend = UsageForecast.Trend(_history, _status.ProviderId, metric);
        if (trend.Count >= 2)
        {
            DrawingHelpers.DrawSparkline(
                graphics,
                new Rectangle(Width - scale[14] - sparklineWidth, trendTop, sparklineWidth, scale[MetricTrendHeight]),
                trend,
                valueColor);
        }

        var projection = UsageForecast.Project(_history, _status.ProviderId, metric, DateTimeOffset.Now);
        if (projection is not null)
        {
            var text = projection.BeforeReset
                ? $"At this pace, empty by {UsageFormatting.AbsoluteReset(projection.ExhaustedAt, DateTimeOffset.Now)}"
                : "At this pace, the window resets first";
            DrawingHelpers.DrawText(
                graphics,
                text,
                _fonts.Small,
                projection.BeforeReset ? Theme.Warning : Theme.Muted,
                new Rectangle(scale[14], trendTop + scale[1], Math.Max(scale[40], Width - scale[28] - sparklineWidth - scale[8]), scale[16]),
                TextFormatFlags.EndEllipsis);
        }
    }

    private void DrawCompressedMetricRow(
        Graphics graphics,
        LayoutScale scale,
        UsageMetric metric,
        int top,
        int rowHeight,
        string valueText,
        int valueWidth,
        Color valueColor,
        Color providerColor)
    {
        var horizontalPadding = scale[14];
        var primaryHeight = Math.Min(scale[20], Math.Max(1, rowHeight - scale[6]));
        var primaryTop = top + Math.Max(1, (rowHeight - primaryHeight - scale[14]) / 2);
        DrawingHelpers.DrawText(
            graphics,
            metric.Name.ToUpperInvariant(),
            _fonts.Utility,
            Theme.Muted,
            new Rectangle(
                horizontalPadding,
                primaryTop,
                Math.Max(scale[24], Width - 2 * horizontalPadding - valueWidth - scale[8]),
                primaryHeight),
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        DrawingHelpers.DrawText(
            graphics,
            valueText,
            _fonts.Value,
            metric.IsUnlimited ? Theme.Muted : valueColor,
            new Rectangle(
                Width - horizontalPadding - valueWidth,
                primaryTop,
                valueWidth,
                primaryHeight),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        if (rowHeight >= scale[34])
        {
            DrawSplitLine(
                graphics,
                new Rectangle(
                    horizontalPadding,
                    Math.Min(top + rowHeight - scale[18], primaryTop + primaryHeight),
                    Width - 2 * horizontalPadding,
                    scale[14]),
                SecondaryText(metric),
                UsageFormatting.RelativeReset(metric.ResetsAt, DateTimeOffset.Now),
                _fonts.Small,
                Theme.Muted,
                Theme.Muted);
        }

        DrawMeter(
            graphics,
            metric,
            new Rectangle(
                horizontalPadding,
                Math.Max(top + 1, top + rowHeight - scale[4]),
                Width - 2 * horizontalPadding,
                Math.Max(1, scale[3])),
            providerColor);
    }

    private void DrawConnectionBlock(Graphics graphics, LayoutScale scale)
    {
        var headerBottom = scale[HeaderHeight];
        using (var divider = new Pen(Theme.Hairline))
        {
            graphics.DrawLine(divider, scale[Gutter], headerBottom, Width - scale[Gutter], headerBottom);
        }

        var availableHeight = Math.Max(0, Height - headerBottom);
        var top = headerBottom + Math.Max(0, (availableHeight - scale[ConnectionBlockHeight]) / 2);

        var message = _status.Snapshot is not null && _metrics.Count == 0
            ? "No metered limits reported"
            : _status.IsLoading
            ? $"Reading {_status.ProviderName} usage..."
            : string.IsNullOrWhiteSpace(_status.Error)
                ? "Connect this provider, then refresh."
                : _status.Error;

        DrawingHelpers.DrawText(
            graphics,
            message,
            _fonts.Body,
            _status.IsLoading ? Theme.Muted : Theme.Critical,
            new Rectangle(scale[18], top + scale[10], Width - scale[36], scale[34]),
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

        if (_status.Snapshot is not null || _status.IsLoading || string.IsNullOrWhiteSpace(_status.SignInCommand))
        {
            return;
        }

        var label = $"Copy  {_status.SignInCommand}";
        var width = MeasureWidth(graphics, label, _fonts.Small) + scale[22];
        _actionBounds = new Rectangle(scale[18], top + scale[48], width, scale[24]);
        DrawingHelpers.FillCard(
            graphics,
            _actionBounds,
            Theme.SurfaceRaised,
            Theme.Accent,
            scale.Exact(6));
        DrawingHelpers.DrawText(
            graphics,
            label,
            _fonts.Small,
            Theme.Text,
            _actionBounds,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private static void DrawMeter(Graphics graphics, UsageMetric? metric, Rectangle bounds, Color providerColor)
    {
        if (metric is null)
        {
            DrawingHelpers.DrawCapacityMeter(graphics, bounds, 0, Theme.Muted, Theme.Track);
            return;
        }

        if (metric.IsUnlimited)
        {
            DrawingHelpers.DrawCapacityMeter(graphics, bounds, 0, Theme.Muted, Theme.Track);
            return;
        }

        if (!metric.HasQuota)
        {
            DrawingHelpers.DrawBalanceMarker(graphics, bounds, providerColor);
            return;
        }

        DrawingHelpers.DrawCapacityMeter(
            graphics,
            bounds,
            metric.UsedPercent!.Value,
            Theme.ForUsage(metric.UsedPercent!.Value),
            Theme.Track);
    }

    private static void DrawSplitLine(
        Graphics graphics,
        Rectangle bounds,
        string left,
        string right,
        Font font,
        Color leftColor,
        Color rightColor)
    {
        var rightWidth = 0;
        if (!string.IsNullOrEmpty(right))
        {
            rightWidth = MeasureWidth(graphics, right, font);
            DrawingHelpers.DrawText(
                graphics,
                right,
                font,
                rightColor,
                bounds,
                TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
        }

        if (string.IsNullOrEmpty(left))
        {
            return;
        }

        // The gap is font-relative so it stays proportional at every DPI.
        var available = bounds.Width - rightWidth - (rightWidth > 0 ? font.Height / 2 : 0);
        if (available <= 0)
        {
            return;
        }

        DrawingHelpers.DrawText(
            graphics,
            left,
            font,
            leftColor,
            new Rectangle(bounds.X, bounds.Y, available, bounds.Height),
            TextFormatFlags.EndEllipsis);
    }

    private string DetailLeft(UsageMetric? metric)
    {
        if (metric is not null)
        {
            return SecondaryText(metric);
        }

        return _status.IsLoading
            ? "Checking usage..."
            : _status.Error ?? "No usage reported";
    }

    private string DetailRight(UsageMetric? metric)
    {
        if (_status.IsStale)
        {
            if (_status.NextRetryAt is { } retryAt)
            {
                return UsageFormatting.RetryCountdown(retryAt, DateTimeOffset.Now);
            }

            return _status.LastUpdated is { } updated
                ? $"last good {UsageFormatting.Age(updated, DateTimeOffset.Now)}"
                : "stale";
        }

        return metric is null ? string.Empty : UsageFormatting.RelativeReset(metric.ResetsAt, DateTimeOffset.Now);
    }

    private static string SecondaryText(UsageMetric metric) => metric.DisplaySecondary;

    private string Identity()
    {
        if (_status.Snapshot is not { } snapshot)
        {
            return "Account unavailable";
        }

        return string.IsNullOrWhiteSpace(snapshot.AccountName)
            ? snapshot.Plan
            : $"{snapshot.Plan}  ·  {snapshot.AccountName}";
    }

    private Color SeverityColor(Color providerColor) => _status.Snapshot is { } snapshot
        ? Theme.ForUsage(snapshot.HighestUsedPercent)
        : _status.IsLoading
            ? Theme.Muted
            : providerColor;

    private bool IsCritical() =>
        _status.Snapshot is { } snapshot &&
        Theme.ForUsage(snapshot.HighestUsedPercent) == Theme.Critical;

    private ProviderCardAction HitTest(Point location)
    {
        if (_actionBounds.Contains(location))
        {
            return ProviderCardAction.CopyCommand;
        }

        return _linkBounds.Contains(location) && _status.AccountUrl is not null
            ? ProviderCardAction.OpenAccount
            : ProviderCardAction.None;
    }

    private ProviderCardAction PrimaryAction()
    {
        if (!_status.IsConnected && !string.IsNullOrWhiteSpace(_status.SignInCommand))
        {
            return ProviderCardAction.CopyCommand;
        }

        return _status.AccountUrl is null ? ProviderCardAction.None : ProviderCardAction.OpenAccount;
    }

    private static int MeasureWidth(Graphics graphics, string text, Font font) =>
        string.IsNullOrEmpty(text) ? 0 : TextRenderer.MeasureText(graphics, text, font).Width;

    /// <summary>
    /// Whether a row of <paramref name="rowHeight"/> can still paint the full label, headline,
    /// detail line and meter. The compressed row is the fallback for rows that genuinely cannot,
    /// not for every row that dips below its preferred <see cref="MetricRowHeight"/>: on a
    /// dashboard tall enough for two rows of cards, a provider reporting four metered limits lands
    /// a couple of pixels short and used to lose its detail line for the sake of them.
    /// </summary>
    internal static bool FitsFullLayout(int rowHeight, LayoutScale scale) =>
        rowHeight >= scale[MetricRowMinimumHeight];

    /// <summary>
    /// Whether the sparkline and forecast line land inside the row rather than across the divider
    /// that starts the next one.
    /// </summary>
    internal static bool FitsTrend(int rowHeight, int contentOffset, LayoutScale scale) =>
        contentOffset + scale[MetricTrendTop] + scale[MetricTrendHeight] <= rowHeight;

    private int RowHeight(UsageMetric metric, LayoutScale scale) =>
        _showTrend && metric.HasQuota ? scale[MetricRowWithTrendHeight] : scale[MetricRowHeight];

    /// <summary>Recomputes <see cref="NaturalHeight"/> for the card's current DPI.</summary>
    private void MeasureNaturalHeight()
    {
        var scale = Scale();
        _measuredDpi = EffectiveDpi;
        if (!_expanded)
        {
            NaturalHeight = scale[CompactHeight];
            return;
        }

        var metrics = _metrics;
        if (metrics.Count == 0)
        {
            NaturalHeight = scale[HeaderHeight] + scale[ConnectionBlockHeight];
            return;
        }

        var height = scale[HeaderHeight] + scale[8];
        foreach (var metric in metrics)
        {
            height += RowHeight(metric, scale);
        }

        if (_status.IsStale)
        {
            height += scale[20];
        }

        NaturalHeight = height;
    }

    private void ApplyHeight()
    {
        MeasureNaturalHeight();
        if (!_expanded || _metrics.Count == 0 || Height < NaturalHeight)
        {
            Height = NaturalHeight;
        }
    }

    /// <summary>
    /// The DPI both the pixel geometry and the painting faces resolve against. Before the handle
    /// exists <see cref="Control.DeviceDpi"/> is still the DPI the process started on, so a card
    /// built for a window on a differently scaled monitor would measure itself for the wrong one;
    /// the parent it is being added to already knows the real value.
    /// </summary>
    private int EffectiveDpi =>
        _dpiOverride ?? (IsHandleCreated ? DeviceDpi : Parent?.DeviceDpi ?? DeviceDpi);

    private LayoutScale Scale() => new(EffectiveDpi);

    /// <summary>
    /// Rebuilds the painting faces when the card's DPI has moved out from under them. A point-sized
    /// font is rasterised once at the process's start-up DPI, so reusing one would keep the primary
    /// monitor's text size inside geometry that has already followed the card to another monitor.
    /// </summary>
    private void EnsureFonts()
    {
        var dpi = EffectiveDpi;
        if (_fonts.Dpi == dpi)
        {
            return;
        }

        _fonts.Dispose();
        _fonts = new CardFonts(dpi);
    }

    /// <summary>
    /// Re-resolves the card against a DPI it has not measured itself at yet — which is every hook
    /// WinForms offers for the moment a control learns which monitor it is really on.
    /// </summary>
    private void ApplyDpiChange()
    {
        var dpi = EffectiveDpi;
        if (_fonts.Dpi == dpi && _measuredDpi == dpi)
        {
            return;
        }

        EnsureFonts();
        var previousDpi = _measuredDpi;
        var previousHeight = Height;
        MeasureNaturalHeight();

        // A card in the full dashboard is given its grid cell height, and that grid never scrolls,
        // so taking the natural height back here would lay the bottom row past the client edge and
        // out of reach. Carrying the height the card already had across the DPI change keeps a
        // deliberate squeeze squeezed and still lands a self-measured card on its new natural size.
        Height = _expanded && _metrics.Count > 0
            ? Math.Max(1, LayoutScale.ScaleBetweenDpis(previousHeight, previousDpi, dpi))
            : NaturalHeight;
        Invalidate();
    }

    /// <summary>
    /// Everything on the card is painted, so screen readers would otherwise see an empty
    /// box. The description carries the same facts the pixels do.
    /// </summary>
    private void ApplyAccessibility()
    {
        AccessibleName = $"{_status.ProviderName} usage";
        var parts = new List<string> { _status.StatusText };
        if (_status.Snapshot is { } snapshot)
        {
            parts.Add(snapshot.Plan);
            if (_expanded && _metrics.Count == 0)
            {
                parts.Add("No metered limits reported");
            }
            foreach (var metric in _expanded ? _metrics : snapshot.Metrics)
            {
                var reset = UsageFormatting.RelativeReset(metric.ResetsAt, DateTimeOffset.Now);
                var values = metric.HasQuota
                    ? $"{metric.DisplayUsed}, {metric.DisplaySecondary}"
                    : $"{metric.DisplayRemaining}, {metric.DisplayUsage}";
                parts.Add(string.IsNullOrEmpty(reset)
                    ? $"{metric.Name}: {values}"
                    : $"{metric.Name}: {values}, {reset}");
            }
        }

        if (!string.IsNullOrWhiteSpace(_status.Error))
        {
            parts.Add(_status.Error);
        }

        if (_status.IsStale && _status.LastUpdated is { } lastUpdated)
        {
            parts.Add($"Last successful update {UsageFormatting.Age(lastUpdated, DateTimeOffset.Now)}");
        }

        if (_status.NextRetryAt is { } retryAt)
        {
            parts.Add(UsageFormatting.RetryCountdown(retryAt, DateTimeOffset.Now));
        }

        AccessibleDescription = string.Join(". ", parts);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fonts.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// The card's painting faces, realised for one monitor's DPI. Sized in pixels rather than
    /// points so the text scales with <see cref="LayoutScale"/> instead of with whichever monitor
    /// the process happened to start on.
    /// </summary>
    private sealed class CardFonts : IDisposable
    {
        internal CardFonts(int dpi)
        {
            Dpi = dpi;
            Name = Typography.Display(10F, dpi);
            Body = Typography.Text(8.5F, dpi);
            Small = Typography.Text(7.8F, dpi);
            Utility = Typography.Mono(7.5F, dpi);
            Value = Typography.Mono(13.5F, dpi);
        }

        internal int Dpi { get; }

        internal Font Name { get; }

        internal Font Body { get; }

        internal Font Small { get; }

        internal Font Utility { get; }

        internal Font Value { get; }

        public void Dispose()
        {
            Name.Dispose();
            Body.Dispose();
            Small.Dispose();
            Utility.Dispose();
            Value.Dispose();
        }
    }
}
