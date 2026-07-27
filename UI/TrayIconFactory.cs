using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace UsageAI.UI;

/// <summary>
/// Renders the tray icon at the size the shell actually asks for. A fixed 32-pixel bitmap
/// scaled down to a 16-pixel tray slot loses the arc and turns any glyph into mush, so the
/// icon is drawn to measure and the glyph appears only when there is room for it.
/// </summary>
internal static class TrayIconFactory
{
    private const int MinimumGlyphSize = 24;

    public static int PreferredSize
    {
        get
        {
            var size = SystemInformation.SmallIconSize.Width;
            return Math.Clamp(size <= 0 ? 16 : size, 16, 64);
        }
    }

    public static Icon Create(int usedPercent, string glyph = "C", bool hasError = false, int size = 0)
    {
        var pixels = Math.Clamp(size <= 0 ? PreferredSize : size, 16, 64);
        using var bitmap = new Bitmap(pixels, pixels, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            var color = hasError ? Theme.Critical : Theme.ForUsage(usedPercent);
            var ringWidth = Math.Max(2F, pixels * 0.14F);
            var inset = ringWidth / 2F + pixels * 0.06F;
            var diameter = pixels - inset * 2F;
            var circle = new RectangleF(inset, inset, diameter, diameter);

            // A translucent grey track reads on both light and dark taskbars; the previous
            // filled dark disc disappeared against a dark shell.
            using (var track = new Pen(Color.FromArgb(90, 140, 152, 168), ringWidth))
            {
                graphics.DrawEllipse(track, circle);
            }

            using (var arc = new Pen(color, ringWidth)
                   {
                       StartCap = LineCap.Round,
                       EndCap = LineCap.Round,
                   })
            {
                // The arc shows consumption, matching the meters in the popup.
                var sweep = hasError ? 360F : Math.Max(10F, 360F * Math.Clamp(usedPercent, 0, 100) / 100F);
                graphics.DrawArc(arc, circle, -90, sweep);
            }

            var displayedGlyph = hasError ? "!" : glyph;
            if (pixels >= MinimumGlyphSize && !string.IsNullOrEmpty(displayedGlyph))
            {
                using var glyphFont = new Font(
                    FontFamily.GenericSansSerif,
                    pixels * 0.34F,
                    FontStyle.Bold,
                    GraphicsUnit.Pixel);
                using var glyphBrush = new SolidBrush(color);
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                };
                graphics.DrawString(
                    displayedGlyph,
                    glyphFont,
                    glyphBrush,
                    new RectangleF(0, 0, pixels, pixels),
                    format);
            }
        }

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
