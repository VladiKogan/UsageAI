using System.Drawing.Drawing2D;

namespace UsageAI.UI;

/// <summary>Painting primitives shared by every custom-drawn control.</summary>
internal static class DrawingHelpers
{
    public static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var maximumRadius = Math.Min(bounds.Width, bounds.Height) / 2F;
        var effective = Math.Clamp(radius, 0F, Math.Max(0F, maximumRadius));
        if (effective <= 0.5F || bounds.Width <= 0 || bounds.Height <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var diameter = effective * 2F;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static GraphicsPath RoundedRectangle(Rectangle bounds, float radius) =>
        RoundedRectangle(new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height), radius);

    public static void FillCard(Graphics graphics, Rectangle bounds, Color fill, Color border, float radius)
    {
        using var path = RoundedRectangle(bounds, radius);
        using var background = new SolidBrush(fill);
        using var pen = new Pen(border);
        graphics.FillPath(background, path);
        graphics.DrawPath(pen, path);
    }

    public static void DrawText(
        Graphics graphics,
        string text,
        Font font,
        Color color,
        Rectangle bounds,
        TextFormatFlags flags) =>
        TextRenderer.DrawText(
            graphics,
            text,
            font,
            bounds,
            color,
            flags | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);

    /// <summary>
    /// Draws a capacity meter. The fill length is the share still available, so the bar and
    /// the headline percentage always move in the same direction.
    /// </summary>
    public static void DrawCapacityMeter(
        Graphics graphics,
        Rectangle bounds,
        int remainingPercent,
        Color fill,
        Color track)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var radius = bounds.Height / 2F;
        using (var trackPath = RoundedRectangle(bounds, radius))
        using (var trackBrush = new SolidBrush(track))
        {
            graphics.FillPath(trackBrush, trackPath);
        }

        var remaining = Math.Clamp(remainingPercent, 0, 100);
        var width = (int)Math.Round(bounds.Width * remaining / 100D);
        if (width <= 0)
        {
            return;
        }

        // Keep a rounded cap legible even when almost nothing is left.
        width = Math.Max(width, bounds.Height);
        width = Math.Min(width, bounds.Width);
        using var fillPath = RoundedRectangle(new Rectangle(bounds.X, bounds.Y, width, bounds.Height), radius);
        using var fillBrush = new SolidBrush(fill);
        graphics.FillPath(fillBrush, fillPath);
    }

    /// <summary>An indeterminate marker for balances, which have no percentage to meter.</summary>
    public static void DrawBalanceMarker(Graphics graphics, Rectangle bounds, Color color)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var width = Math.Min(bounds.Width, bounds.Height * 8);
        using var path = RoundedRectangle(
            new Rectangle(bounds.X, bounds.Y, width, bounds.Height),
            bounds.Height / 2F);
        using var brush = new SolidBrush(Color.FromArgb(150, color));
        graphics.FillPath(brush, path);
    }

    /// <summary>
    /// Plots recent usage percentages. Values are drawn as consumption, so the line rises as
    /// the quota is spent.
    /// </summary>
    public static void DrawSparkline(
        Graphics graphics,
        Rectangle bounds,
        IReadOnlyList<int> values,
        Color color)
    {
        if (values.Count < 2 || bounds.Width <= 2 || bounds.Height <= 2)
        {
            return;
        }

        var points = new PointF[values.Count];
        var step = bounds.Width / (float)(values.Count - 1);
        for (var index = 0; index < values.Count; index++)
        {
            var value = Math.Clamp(values[index], 0, 100) / 100F;
            points[index] = new PointF(
                bounds.Left + step * index,
                bounds.Bottom - value * bounds.Height);
        }

        var previousMode = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(Color.FromArgb(190, color), 1.4F)
               {
                   LineJoin = LineJoin.Round,
                   StartCap = LineCap.Round,
                   EndCap = LineCap.Round,
               })
        {
            graphics.DrawLines(pen, points);
        }

        using (var dot = new SolidBrush(color))
        {
            var last = points[^1];
            graphics.FillEllipse(dot, last.X - 1.8F, last.Y - 1.8F, 3.6F, 3.6F);
        }

        graphics.SmoothingMode = previousMode;
    }
}
