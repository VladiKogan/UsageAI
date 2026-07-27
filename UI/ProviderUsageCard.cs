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
/// left, the headline value on the right, supporting detail beneath, and a capacity meter
/// across the bottom. The meter fill and the headline both encode what is left, so they
/// never move in opposite directions.
/// </summary>
internal sealed class ProviderUsageCard : Control
{
    private const int CompactHeight = 98;
    private const int HeaderHeight = 58;
    private const int MetricRowHeight = 72;
    private const int MetricRowWithTrendHeight = 92;
    private const int ConnectionBlockHeight = 86;
    private const int CardRadius = 12;
    private const int Gutter = 16;

    private readonly Font _nameFont = Typography.Display(10F);
    private readonly Font _bodyFont = Typography.Text(8.5F);
    private readonly Font _smallFont = Typography.Text(7.8F);
    private readonly Font _utilityFont = Typography.Mono(7.5F);
    private readonly Font _valueFont = Typography.Mono(13.5F);
    private readonly bool _expanded;
    private readonly ProviderStatus _status;
    private readonly IReadOnlyList<UsageSample> _history;
    private readonly bool _showTrend;
    private Rectangle _actionBounds = Rectangle.Empty;
    private Rectangle _linkBounds = Rectangle.Empty;

    public ProviderUsageCard(
        ProviderStatus status,
        bool expanded,
        IReadOnlyList<UsageSample> history,
        bool showTrend)
    {
        _status = status;
        _expanded = expanded;
        _history = history;
        _showTrend = showTrend && expanded;
        DoubleBuffered = true;
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
        ApplyHeight();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyHeight();
        Invalidate();
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
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        _actionBounds = Rectangle.Empty;
        _linkBounds = Rectangle.Empty;

        var scale = new LayoutScale(this);
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
        var valueText = metric?.DisplayRemaining ?? (_status.IsLoading ? "..." : "--");
        var valueWidth = MeasureWidth(graphics, valueText, _valueFont);
        var nameWidth = Math.Max(scale[40], Width - textLeft - valueWidth - scale[26]);

        DrawingHelpers.DrawText(
            graphics,
            _status.ProviderName,
            _nameFont,
            Theme.Text,
            new Rectangle(textLeft, scale[12], nameWidth, scale[20]),
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);

        var subtitle = _status.Snapshot?.Plan ?? (_status.IsLoading ? "Connecting" : "Not connected");
        DrawingHelpers.DrawText(
            graphics,
            subtitle,
            _smallFont,
            _status.IsConnected ? providerColor : Theme.Muted,
            new Rectangle(textLeft, scale[34], nameWidth, scale[16]),
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);

        var valueBounds = new Rectangle(
            Width - scale[Gutter] - valueWidth,
            scale[8],
            valueWidth,
            scale[26]);
        DrawingHelpers.DrawText(graphics, valueText, _valueFont, severity, valueBounds, TextFormatFlags.Right);

        if (metric is not null)
        {
            DrawingHelpers.DrawText(
                graphics,
                metric.Name.ToUpperInvariant(),
                _utilityFont,
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
            _smallFont,
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
        var statusWidth = MeasureWidth(graphics, statusText, _smallFont) + scale[18];
        var nameWidth = Math.Max(scale[60], Width - scale[64] - statusWidth - scale[24]);

        var nameBounds = new Rectangle(scale[64], scale[10], nameWidth, scale[20]);
        var nameText = _status.AccountUrl is null ? _status.ProviderName : $"{_status.ProviderName}  ↗";
        DrawingHelpers.DrawText(
            graphics,
            nameText,
            _nameFont,
            Theme.Text,
            nameBounds,
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        if (_status.AccountUrl is not null)
        {
            _linkBounds = new Rectangle(
                nameBounds.X,
                nameBounds.Y,
                Math.Min(nameBounds.Width, MeasureWidth(graphics, nameText, _nameFont)),
                nameBounds.Height);
        }

        DrawingHelpers.DrawText(
            graphics,
            Identity(),
            _smallFont,
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
            _smallFont,
            statusColor,
            new Rectangle(Width - scale[Gutter] - statusWidth + scale[11], scale[12], statusWidth - scale[11], scale[18]),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        if (_status.LastUpdated is { } updated)
        {
            DrawingHelpers.DrawText(
                graphics,
                UsageFormatting.Age(updated, DateTimeOffset.Now),
                _smallFont,
                Theme.Muted,
                new Rectangle(Width - scale[Gutter] - scale[120], scale[32], scale[120], scale[16]),
                TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
        }

        var metrics = _status.Snapshot?.Metrics ?? Array.Empty<UsageMetric>();
        if (metrics.Count == 0)
        {
            DrawConnectionBlock(graphics, scale);
            return;
        }

        var top = scale[HeaderHeight];
        foreach (var metric in metrics)
        {
            var rowHeight = RowHeight(metric, scale);
            DrawMetricRow(graphics, scale, metric, top, rowHeight, providerColor, severity);
            top += rowHeight;
        }

        if (_status.IsStale && _status.Error is { Length: > 0 } staleError)
        {
            DrawingHelpers.DrawText(
                graphics,
                staleError,
                _smallFont,
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
        var valueText = metric.DisplayRemaining;
        var valueWidth = MeasureWidth(graphics, valueText, _valueFont);

        DrawingHelpers.DrawText(
            graphics,
            metric.Name.ToUpperInvariant(),
            _utilityFont,
            Theme.Muted,
            new Rectangle(scale[18], top + scale[11], Math.Max(scale[40], Width - scale[36] - valueWidth - scale[8]), scale[16]),
            TextFormatFlags.EndEllipsis);

        DrawingHelpers.DrawText(
            graphics,
            valueText,
            _valueFont,
            metric.IsUnlimited ? Theme.Muted : valueColor,
            new Rectangle(Width - scale[18] - valueWidth, top + scale[6], valueWidth, scale[24]),
            TextFormatFlags.Right);

        DrawSplitLine(
            graphics,
            new Rectangle(scale[18], top + scale[32], Width - scale[36], scale[16]),
            metric.DisplayUsage,
            UsageFormatting.RelativeReset(metric.ResetsAt, DateTimeOffset.Now),
            _smallFont,
            Theme.Muted,
            Theme.Muted);

        DrawMeter(
            graphics,
            metric,
            new Rectangle(scale[18], top + scale[52], Width - scale[36], scale[5]),
            providerColor);

        if (rowHeight <= scale[MetricRowHeight])
        {
            return;
        }

        var trendTop = top + scale[62];
        var trend = UsageForecast.Trend(_history, _status.ProviderId, metric);
        if (trend.Count >= 2)
        {
            DrawingHelpers.DrawSparkline(
                graphics,
                new Rectangle(Width - scale[18] - scale[96], trendTop, scale[96], scale[18]),
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
                _smallFont,
                projection.BeforeReset ? Theme.Warning : Theme.Muted,
                new Rectangle(scale[18], trendTop + scale[2], Width - scale[36] - scale[104], scale[16]),
                TextFormatFlags.EndEllipsis);
        }
    }

    private void DrawConnectionBlock(Graphics graphics, LayoutScale scale)
    {
        var top = scale[HeaderHeight];
        using (var divider = new Pen(Theme.Hairline))
        {
            graphics.DrawLine(divider, scale[Gutter], top, Width - scale[Gutter], top);
        }

        var message = _status.IsLoading
            ? $"Reading {_status.ProviderName} usage..."
            : string.IsNullOrWhiteSpace(_status.Error)
                ? "Connect this provider, then refresh."
                : _status.Error;

        DrawingHelpers.DrawText(
            graphics,
            message,
            _bodyFont,
            _status.IsLoading ? Theme.Muted : Theme.Critical,
            new Rectangle(scale[18], top + scale[10], Width - scale[36], scale[34]),
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

        if (_status.IsLoading || string.IsNullOrWhiteSpace(_status.SignInCommand))
        {
            return;
        }

        var label = $"Copy  {_status.SignInCommand}";
        var width = MeasureWidth(graphics, label, _smallFont) + scale[22];
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
            _smallFont,
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
            DrawingHelpers.DrawCapacityMeter(graphics, bounds, 100, Theme.Blend(Theme.Muted, Theme.Track, 0.6), Theme.Track);
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
            metric.RemainingPercent,
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
            return metric.DisplayUsage;
        }

        return _status.IsLoading
            ? "Checking usage..."
            : _status.Error ?? "No usage reported";
    }

    private string DetailRight(UsageMetric? metric)
    {
        if (_status.IsStale)
        {
            return _status.LastUpdated is { } updated
                ? $"stale, {UsageFormatting.Age(updated, DateTimeOffset.Now)}"
                : "stale";
        }

        return metric is null ? string.Empty : UsageFormatting.RelativeReset(metric.ResetsAt, DateTimeOffset.Now);
    }

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

    private int RowHeight(UsageMetric metric, LayoutScale scale) =>
        _showTrend && metric.HasQuota ? scale[MetricRowWithTrendHeight] : scale[MetricRowHeight];

    private void ApplyHeight()
    {
        var scale = new LayoutScale(this);
        if (!_expanded)
        {
            Height = scale[CompactHeight];
            return;
        }

        var metrics = _status.Snapshot?.Metrics ?? Array.Empty<UsageMetric>();
        if (metrics.Count == 0)
        {
            Height = scale[HeaderHeight] + scale[ConnectionBlockHeight];
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

        Height = height;
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
            foreach (var metric in snapshot.Metrics)
            {
                var reset = UsageFormatting.RelativeReset(metric.ResetsAt, DateTimeOffset.Now);
                parts.Add(string.IsNullOrEmpty(reset)
                    ? $"{metric.Name}: {metric.DisplayRemaining}, {metric.DisplayUsage}"
                    : $"{metric.Name}: {metric.DisplayRemaining}, {metric.DisplayUsage}, {reset}");
            }
        }

        if (!string.IsNullOrWhiteSpace(_status.Error))
        {
            parts.Add(_status.Error);
        }

        AccessibleDescription = string.Join(". ", parts);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _nameFont.Dispose();
            _bodyFont.Dispose();
            _smallFont.Dispose();
            _utilityFont.Dispose();
            _valueFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
