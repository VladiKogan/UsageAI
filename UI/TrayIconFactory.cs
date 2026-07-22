using System.Runtime.InteropServices;

namespace UsageAI.UI;

internal static class TrayIconFactory
{
    public static Icon Create(int usedPercent, string glyph = "C", bool hasError = false)
    {
        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var baseBrush = new SolidBrush(Theme.SurfaceRaised);
        using var ring = new Pen(Theme.Hairline, 3.5F);
        graphics.FillEllipse(baseBrush, 4, 4, 24, 24);
        graphics.DrawEllipse(ring, 5.5F, 5.5F, 21, 21);

        var color = hasError ? Theme.Critical : Theme.ForUsage(usedPercent);
        using var arc = new Pen(color, 4F) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        var sweep = hasError ? 360F : Math.Max(12F, 360F * Math.Clamp(usedPercent, 0, 100) / 100F);
        graphics.DrawArc(arc, 5.5F, 5.5F, 21, 21, -90, sweep);

        using var glyphFont = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Pixel);
        using var glyphBrush = new SolidBrush(Theme.Text);
        var displayedGlyph = hasError ? "!" : glyph;
        var glyphSize = graphics.MeasureString(displayedGlyph, glyphFont);
        graphics.DrawString(displayedGlyph, glyphFont, glyphBrush, 16 - glyphSize.Width / 2, 16 - glyphSize.Height / 2);

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool DestroyIcon(IntPtr handle);
}
