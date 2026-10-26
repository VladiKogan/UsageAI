using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace UsageAI.UI;

internal enum SettingPreviewKind
{
    /// <summary>A small ring and figure showing the quota display: 92% used, or 8% left.</summary>
    QuotaRing,

    /// <summary>The 0–100% scale split into fine, warning and critical at the chosen thresholds.</summary>
    ThresholdBand,
}

/// <summary>
/// A preview drawn beside a setting so the choice shows what it produces instead of needing an
/// explanation: the quota display's ring, or where the status colours change on the scale.
/// </summary>
internal sealed class SettingPreview : Control
{
    private const int SampleUsedPercent = 92;

    private readonly SettingPreviewKind _kind;
    private bool _showRemaining;
    private int _warningPercent = 72;
    private int _criticalPercent = 90;
    private Font? _font;
    private int _fontDpi;

    public SettingPreview(SettingPreviewKind kind)
    {
        _kind = kind;
        DoubleBuffered = true;
        ResizeRedraw = true;
        AccessibleRole = AccessibleRole.Graphic;
        UpdateAccessibility();
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int? DpiOverride { get; init; }

    /// <summary>Whether the quota ring shows what is left rather than what is used.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShowRemaining
    {
        get => _showRemaining;
        set
        {
            if (_showRemaining == value)
            {
                return;
            }

            _showRemaining = value;
            UpdateAccessibility();
            Invalidate();
        }
    }

    public void SetThresholds(int warningPercent, int criticalPercent)
    {
        // The same rule AppSettings and Theme apply, so the preview shows what will be saved.
        _warningPercent = Math.Clamp(warningPercent, 1, 99);
        _criticalPercent = Math.Clamp(criticalPercent, _warningPercent + 1, 100);
        UpdateAccessibility();
        Invalidate();
    }

    protected override Size DefaultSize => new(200, 40);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.Clear(BackColor);
        var scale = DpiOverride is { } dpi ? new LayoutScale(dpi) : new LayoutScale(this);
        if (_kind == SettingPreviewKind.QuotaRing)
        {
            PaintQuotaRing(graphics, scale);
        }
        else
        {
            PaintThresholdBand(graphics, scale);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void PaintQuotaRing(Graphics graphics, LayoutScale scale)
    {
        var size = Math.Min(Height - 2, scale[32]);
        var ring = new RectangleF(1, (Height - size) / 2F, size, size);
        var shown = _showRemaining ? 100 - SampleUsedPercent : SampleUsedPercent;
        DrawingHelpers.DrawRing(graphics, ring, scale.Exact(4), shown, Theme.ForUsage(SampleUsedPercent), Theme.Track);
        DrawingHelpers.DrawText(
            graphics,
            _showRemaining ? $"{shown}% left" : $"{shown}% used",
            FontForDpi(),
            Theme.TextForUsage(SampleUsedPercent),
            Rectangle.FromLTRB((int)ring.Right + scale[10], 0, Width, Height),
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void PaintThresholdBand(Graphics graphics, LayoutScale scale)
    {
        var font = FontForDpi();
        var labelHeight = font.Height;
        var barHeight = scale[8];
        var top = Math.Max(0, (Height - barHeight - labelHeight - scale[4]) / 2);
        var bar = new Rectangle(0, top, Width - 1, barHeight);
        var warningX = bar.Left + bar.Width * _warningPercent / 100;
        var criticalX = bar.Left + bar.Width * _criticalPercent / 100;
        var gap = Math.Max(1, scale[2]);
        Segment(graphics, Rectangle.FromLTRB(bar.Left, bar.Top, warningX - gap, bar.Bottom), Theme.Success);
        Segment(graphics, Rectangle.FromLTRB(warningX, bar.Top, criticalX - gap, bar.Bottom), Theme.Warning);
        Segment(graphics, Rectangle.FromLTRB(criticalX, bar.Top, bar.Right, bar.Bottom), Theme.Critical);

        var labels = bar.Bottom + scale[4];
        var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        DrawingHelpers.DrawText(graphics, "Fine", font, Theme.Muted, Rectangle.FromLTRB(bar.Left, labels, Math.Max(bar.Left + 1, warningX - scale[4]), labels + labelHeight), flags);
        DrawingHelpers.DrawText(graphics, "Warning", font, Theme.Muted, Rectangle.FromLTRB(warningX, labels, Math.Max(warningX + 1, criticalX - scale[4]), labels + labelHeight), flags);
        DrawingHelpers.DrawText(graphics, "Critical", font, Theme.Muted, Rectangle.FromLTRB(criticalX, labels, Math.Max(criticalX + 1, bar.Right), labels + labelHeight), flags);
    }

    private static void Segment(Graphics graphics, Rectangle bounds, Color color)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        using var brush = new SolidBrush(color);
        using var path = DrawingHelpers.RoundedRectangle(bounds, bounds.Height / 2F);
        graphics.FillPath(brush, path);
    }

    private void UpdateAccessibility() =>
        AccessibleDescription = _kind == SettingPreviewKind.QuotaRing
            ? (_showRemaining ? $"Example: {100 - SampleUsedPercent}% left" : $"Example: {SampleUsedPercent}% used")
            : $"Fine below {_warningPercent}%, warning from {_warningPercent}%, critical from {_criticalPercent}%";

    private Font FontForDpi()
    {
        var dpi = DpiOverride ?? DeviceDpi;
        if (_font is null || _fontDpi != dpi)
        {
            _font?.Dispose();
            _font = Typography.Text(8F, dpi);
            _fontDpi = dpi;
        }

        return _font;
    }
}
