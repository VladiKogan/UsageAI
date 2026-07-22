using UsageAI.Models;

namespace UsageAI.UI;

internal sealed class QuotaMeterControl : Control
{
    private UsageWindow? _window;

    public string EmptyName { get; init; } = "Limit";

    public QuotaMeterControl()
    {
        DoubleBuffered = true;
        Height = 78;
        Margin = new Padding(0, 0, 0, 10);
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
    }

    public void SetWindow(UsageWindow? window)
    {
        _window = window;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var background = new SolidBrush(Theme.SurfaceRaised);
        using var border = new Pen(Theme.Hairline);
        var card = new Rectangle(0, 0, Width - 1, Height - 1);
        e.Graphics.FillRectangle(background, card);
        e.Graphics.DrawRectangle(border, card);

        if (_window is null)
        {
            DrawText(e.Graphics, EmptyName.ToUpperInvariant(), new Font("Cascadia Mono", 8F, FontStyle.Bold), Theme.Muted, 14, 13);
            DrawText(e.Graphics, "Not reported by Codex", Font, Theme.Muted, 14, 36);
            return;
        }

        var usageColor = Theme.ForUsage(_window.UsedPercent);
        DrawText(e.Graphics, _window.Name.ToUpperInvariant(), new Font("Cascadia Mono", 8F, FontStyle.Bold), Theme.Muted, 14, 11);

        var remainingText = $"{_window.RemainingPercent}% LEFT";
        using var valueFont = new Font("Cascadia Mono", 15F, FontStyle.Bold, GraphicsUnit.Point);
        var valueSize = e.Graphics.MeasureString(remainingText, valueFont);
        DrawText(e.Graphics, remainingText, valueFont, Theme.Text, Width - 14 - valueSize.Width, 7);

        var resetText = FormatReset(_window.ResetsAt);
        DrawText(e.Graphics, $"{_window.UsedPercent}% used", Font, Theme.Muted, 14, 37);
        using var resetFont = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
        var resetSize = e.Graphics.MeasureString(resetText, resetFont);
        DrawText(e.Graphics, resetText, resetFont, Theme.Muted, Width - 14 - resetSize.Width, 38);

        const int trackLeft = 14;
        const int trackBottom = 64;
        var trackWidth = Math.Max(1, Width - 28);
        using var trackBrush = new SolidBrush(Theme.Night);
        using var fillBrush = new SolidBrush(usageColor);
        e.Graphics.FillRectangle(trackBrush, trackLeft, trackBottom, trackWidth, 5);
        e.Graphics.FillRectangle(fillBrush, trackLeft, trackBottom, (int)(trackWidth * (_window.UsedPercent / 100d)), 5);

        using var tickPen = new Pen(Color.FromArgb(105, Theme.Muted));
        for (var index = 1; index < 4; index++)
        {
            var x = trackLeft + trackWidth * index / 4;
            e.Graphics.DrawLine(tickPen, x, trackBottom, x, trackBottom + 5);
        }
    }

    private static void DrawText(Graphics graphics, string text, Font font, Color color, float x, float y)
    {
        using var brush = new SolidBrush(color);
        graphics.DrawString(text, font, brush, x, y);
    }

    private static string FormatReset(DateTimeOffset? resetsAt)
    {
        if (resetsAt is null)
        {
            return "reset time unavailable";
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
}
