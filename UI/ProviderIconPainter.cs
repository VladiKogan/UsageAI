using System.Drawing.Drawing2D;

namespace UsageAI.UI;

internal static class ProviderIconPainter
{
    public static void Draw(Graphics graphics, Rectangle bounds, string providerId)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var color = Theme.ForProvider(providerId);

        // A tint that reads on a near-black card is nearly invisible on a white one.
        using var background = new SolidBrush(Color.FromArgb(Theme.IsDark ? 32 : 40, color));
        using var border = new Pen(Color.FromArgb(Theme.IsDark ? 105 : 125, color), 1F);
        using var shape = DrawingHelpers.RoundedRectangle(bounds, bounds.Width * 0.26F);
        graphics.FillPath(background, shape);
        graphics.DrawPath(border, shape);

        var center = new PointF(bounds.Left + bounds.Width / 2F, bounds.Top + bounds.Height / 2F);
        using var pen = new Pen(color, Math.Max(1.7F, bounds.Width / 18F))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        switch (providerId.ToLowerInvariant())
        {
            case "claude":
                DrawClaude(graphics, pen, center, bounds.Width);
                break;
            case "copilot":
                DrawCopilot(graphics, pen, center, bounds.Width);
                break;
            default:
                DrawCodex(graphics, pen, center, bounds.Width);
                break;
        }
    }

    private static void DrawCodex(Graphics graphics, Pen pen, PointF center, float size)
    {
        var outer = size * 0.22F;
        var inner = size * 0.105F;
        var outerPoints = Hexagon(center, outer, -30F);
        var innerPoints = Hexagon(center, inner, -30F);
        graphics.DrawPolygon(pen, outerPoints);
        graphics.DrawPolygon(pen, innerPoints);
        for (var index = 0; index < 6; index++)
        {
            graphics.DrawLine(pen, outerPoints[index], innerPoints[(index + 1) % 6]);
        }
    }

    private static void DrawClaude(Graphics graphics, Pen pen, PointF center, float size)
    {
        var inner = size * 0.075F;
        var outer = size * 0.235F;
        for (var index = 0; index < 8; index++)
        {
            var angle = MathF.PI * index / 4F;
            var start = new PointF(center.X + MathF.Cos(angle) * inner, center.Y + MathF.Sin(angle) * inner);
            var end = new PointF(center.X + MathF.Cos(angle) * outer, center.Y + MathF.Sin(angle) * outer);
            graphics.DrawLine(pen, start, end);
        }
    }

    private static void DrawCopilot(Graphics graphics, Pen pen, PointF center, float size)
    {
        var head = new RectangleF(center.X - size * 0.23F, center.Y - size * 0.16F, size * 0.46F, size * 0.34F);
        using var path = DrawingHelpers.RoundedRectangle(head, size * 0.10F);
        graphics.DrawPath(pen, path);
        graphics.DrawLine(pen, center.X - size * 0.12F, head.Top, center.X - size * 0.18F, head.Top - size * 0.10F);
        graphics.DrawLine(pen, center.X + size * 0.12F, head.Top, center.X + size * 0.18F, head.Top - size * 0.10F);
        using var eyeBrush = new SolidBrush(pen.Color);
        var eye = Math.Max(2F, size * 0.055F);
        graphics.FillEllipse(eyeBrush, center.X - size * 0.12F - eye / 2F, center.Y - eye / 2F, eye, eye);
        graphics.FillEllipse(eyeBrush, center.X + size * 0.12F - eye / 2F, center.Y - eye / 2F, eye, eye);
    }

    private static PointF[] Hexagon(PointF center, float radius, float offsetDegrees) =>
        Enumerable.Range(0, 6)
            .Select(index => (offsetDegrees + index * 60F) * MathF.PI / 180F)
            .Select(angle => new PointF(center.X + MathF.Cos(angle) * radius, center.Y + MathF.Sin(angle) * radius))
            .ToArray();

}
