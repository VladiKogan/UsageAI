using System.Drawing.Drawing2D;
using System.Drawing.Text;
using UsageAI.Models;

namespace UsageAI.UI;

internal sealed class ProviderUsageCard : Control
{
    private const int CompactHeight = 92;
    private const int ExpandedHeaderHeight = 62;
    private const int MetricHeight = 68;
    private readonly Font _nameFont = new("Segoe UI Variable Display", 10F, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _bodyFont = new("Segoe UI Variable Text", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _smallFont = new("Segoe UI Variable Text", 7.8F, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _utilityFont = new("Cascadia Mono", 7.5F, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _valueFont = new("Cascadia Mono", 13.5F, FontStyle.Bold, GraphicsUnit.Point);
    private readonly bool _expanded;
    private ProviderViewState _state;

    public ProviderUsageCard(ProviderViewState state, bool expanded)
    {
        _state = state;
        _expanded = expanded;
        DoubleBuffered = true;
        Margin = new Padding(0, 0, 0, 10);
        Height = expanded ? CalculateExpandedHeight(state) : CompactHeight;
        AccessibleName = $"{state.ProviderName} usage";
        AccessibleRole = AccessibleRole.Grouping;
    }

    public void UpdateState(ProviderViewState state)
    {
        _state = state;
        Height = _expanded ? CalculateExpandedHeight(state) : CompactHeight;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var card = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using var cardPath = RoundedRectangle(card, 12F);
        using var background = new SolidBrush(Theme.Surface);
        using var border = new Pen(Theme.Hairline);
        e.Graphics.FillPath(background, cardPath);
        e.Graphics.DrawPath(border, cardPath);

        var providerColor = Theme.ForProvider(_state.ProviderId);
        using var rail = new SolidBrush(providerColor);
        using var railPath = RoundedRectangle(new Rectangle(0, 16, 3, Math.Max(12, Height - 32)), 1.5F);
        e.Graphics.FillPath(rail, railPath);

        if (_expanded)
        {
            DrawExpanded(e.Graphics, providerColor);
        }
        else
        {
            DrawCompact(e.Graphics, providerColor);
        }
    }

    private void DrawCompact(Graphics graphics, Color providerColor)
    {
        ProviderIconPainter.Draw(graphics, new Rectangle(16, 17, 38, 38), _state.ProviderId);
        DrawText(graphics, _state.ProviderName, _nameFont, Theme.Text, new Rectangle(66, 15, 134, 22),
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);

        var plan = _state.Snapshot?.Plan ?? (_state.IsLoading ? "Connecting" : "Not connected");
        DrawText(graphics, plan, _smallFont, _state.IsConnected ? providerColor : Theme.Muted,
            new Rectangle(66, 38, 134, 18), TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);

        var metric = PreferredMetric(_state.Snapshot);
        if (metric is null)
        {
            DrawText(graphics, _state.IsLoading ? "READING" : "NO METRIC", _utilityFont, Theme.Muted,
                new Rectangle(204, 15, Width - 220, 17), TextFormatFlags.Right);
            DrawText(graphics, _state.IsLoading ? "Checking usage..." : "No usage reported", _bodyFont, Theme.Text,
                new Rectangle(190, 35, Width - 206, 22), TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
            return;
        }

        DrawText(graphics, metric.Name.ToUpperInvariant(), _utilityFont, Theme.Muted,
            new Rectangle(204, 13, Width - 220, 17), TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
        DrawText(graphics, metric.Value, _valueFont, Theme.Text,
            new Rectangle(194, 28, Width - 210, 24), TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
        DrawText(graphics, metric.Detail, _smallFont, Theme.Muted,
            new Rectangle(190, 54, Width - 206, 18), TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
        DrawMeter(graphics, metric, 66, Height - 12, Width - 82, 4, providerColor);
    }

    private void DrawExpanded(Graphics graphics, Color providerColor)
    {
        ProviderIconPainter.Draw(graphics, new Rectangle(16, 13, 38, 38), _state.ProviderId);
        DrawText(graphics, _state.ProviderName, _nameFont, Theme.Text, new Rectangle(66, 11, Width - 190, 22),
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);

        var identity = ProviderIdentity(_state.Snapshot);
        DrawText(graphics, identity, _smallFont, providerColor, new Rectangle(66, 34, Width - 190, 18),
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);

        var statusText = _state.IsLoading
            ? "Refreshing"
            : _state.Snapshot is not null && string.IsNullOrWhiteSpace(_state.Error)
                ? "Connected"
                : _state.Snapshot is not null
                    ? "Update failed"
                    : "Not connected";
        var statusColor = statusText == "Connected" ? Theme.Success : statusText == "Refreshing" ? Theme.Signal : Theme.Critical;
        using var dot = new SolidBrush(statusColor);
        graphics.FillEllipse(dot, Width - 116, 22, 6, 6);
        DrawText(graphics, statusText, _smallFont, statusColor, new Rectangle(Width - 104, 14, 88, 22),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        if (_state.Snapshot is null)
        {
            DrawConnectionState(graphics);
            return;
        }

        var metrics = AllMetrics(_state.Snapshot);
        if (metrics.Count == 0)
        {
            DrawText(graphics, "No usage metrics were reported for this account.", _bodyFont, Theme.Muted,
                new Rectangle(18, ExpandedHeaderHeight + 12, Width - 36, MetricHeight - 20),
                TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter);
            return;
        }

        for (var index = 0; index < metrics.Count; index++)
        {
            DrawMetricRow(graphics, metrics[index], ExpandedHeaderHeight + index * MetricHeight, providerColor);
        }
    }

    private void DrawConnectionState(Graphics graphics)
    {
        var top = ExpandedHeaderHeight;
        using var divider = new Pen(Theme.Hairline);
        graphics.DrawLine(divider, 16, top, Width - 16, top);
        var message = _state.IsLoading
            ? $"Reading {_state.ProviderName} usage..."
            : string.IsNullOrWhiteSpace(_state.Error)
                ? "Connect this provider, then refresh the dashboard."
                : _state.Error;
        DrawText(graphics, message, _bodyFont, Theme.Muted, new Rectangle(18, top + 12, Width - 36, Height - top - 20),
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
    }

    private void DrawMetricRow(Graphics graphics, MetricItem metric, int top, Color providerColor)
    {
        using var divider = new Pen(Theme.Hairline);
        graphics.DrawLine(divider, 16, top, Width - 16, top);
        DrawText(graphics, metric.Name.ToUpperInvariant(), _utilityFont, Theme.Muted,
            new Rectangle(18, top + 10, Width / 2, 18), TextFormatFlags.EndEllipsis);
        DrawText(graphics, metric.Value, _valueFont, Theme.Text,
            new Rectangle(Width / 2, top + 7, Width / 2 - 18, 24), TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
        DrawText(graphics, metric.Detail, _smallFont, Theme.Muted,
            new Rectangle(18, top + 34, Width - 36, 17), TextFormatFlags.EndEllipsis);
        DrawMeter(graphics, metric, 18, top + 55, Width - 36, 4, providerColor);
    }

    private static void DrawMeter(Graphics graphics, MetricItem metric, int left, int top, int width, int height, Color providerColor)
    {
        if (metric.UsedPercent is null)
        {
            using var creditBrush = new SolidBrush(Color.FromArgb(165, providerColor));
            graphics.FillRectangle(creditBrush, left, top, Math.Min(width, 34), height);
            return;
        }

        using var track = new SolidBrush(Theme.Track);
        using var fill = new SolidBrush(Theme.ForUsage(metric.UsedPercent.Value));
        graphics.FillRectangle(track, left, top, Math.Max(1, width), height);
        graphics.FillRectangle(fill, left, top, (int)(Math.Max(1, width) * metric.UsedPercent.Value / 100D), height);
    }

    private static MetricItem? PreferredMetric(UsageSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        if (snapshot.Session is not null)
        {
            return FromWindow(snapshot.Session);
        }

        if (snapshot.Weekly is not null)
        {
            return FromWindow(snapshot.Weekly);
        }

        return FromCredits(snapshot);
    }

    private static List<MetricItem> AllMetrics(UsageSnapshot snapshot)
    {
        var metrics = new List<MetricItem>();
        if (snapshot.Session is not null)
        {
            metrics.Add(FromWindow(snapshot.Session));
        }

        if (snapshot.Weekly is not null)
        {
            metrics.Add(FromWindow(snapshot.Weekly));
        }

        var credits = FromCredits(snapshot);
        if (credits is not null)
        {
            metrics.Add(credits);
        }

        return metrics;
    }

    private static MetricItem FromWindow(UsageWindow window)
    {
        var reset = FormatReset(window.ResetsAt);
        var detail = string.IsNullOrWhiteSpace(reset) ? window.DisplayUsage : $"{window.DisplayUsage}  -  {reset}";
        return new MetricItem(window.Name, window.DisplayRemaining, detail, window.UsedPercent);
    }

    private static MetricItem? FromCredits(UsageSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.CreditBalance))
        {
            var detail = snapshot.AvailableResetCredits > 0
                ? $"Balance  -  {snapshot.AvailableResetCredits} full reset{(snapshot.AvailableResetCredits == 1 ? string.Empty : "s")} available"
                : "Available account balance";
            return new MetricItem("Credits", snapshot.CreditBalance, detail, null);
        }

        return snapshot.AvailableResetCredits > 0
            ? new MetricItem(
                "Reset credits",
                snapshot.AvailableResetCredits.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"Full reset{(snapshot.AvailableResetCredits == 1 ? string.Empty : "s")} available",
                null)
            : null;
    }

    private static string ProviderIdentity(UsageSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return "Account unavailable";
        }

        return string.IsNullOrWhiteSpace(snapshot.AccountName)
            ? snapshot.Plan
            : $"{snapshot.Plan}  -  {snapshot.AccountName}";
    }

    private static string FormatReset(DateTimeOffset? resetsAt)
    {
        if (resetsAt is null)
        {
            return string.Empty;
        }

        var remaining = resetsAt.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            return "resetting now";
        }

        if (remaining.TotalDays >= 1)
        {
            return $"resets in {(int)remaining.TotalDays}d {remaining.Hours}h";
        }

        if (remaining.TotalHours >= 1)
        {
            return $"resets in {(int)remaining.TotalHours}h {remaining.Minutes}m";
        }

        return $"resets in {Math.Max(1, remaining.Minutes)}m";
    }

    private static int CalculateExpandedHeight(ProviderViewState state)
    {
        if (state.Snapshot is null)
        {
            return 122;
        }

        return ExpandedHeaderHeight + Math.Max(1, AllMetrics(state.Snapshot).Count) * MetricHeight + 8;
    }

    private static void DrawText(Graphics graphics, string text, Font font, Color color, Rectangle bounds, TextFormatFlags flags) =>
        TextRenderer.DrawText(graphics, text, font, bounds, color, flags | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);

    private static GraphicsPath RoundedRectangle(Rectangle bounds, float radius)
    {
        var diameter = radius * 2F;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
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

    private sealed record MetricItem(string Name, string Value, string Detail, int? UsedPercent);
}
