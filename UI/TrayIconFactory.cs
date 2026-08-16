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

    public static Icon Create(
        int usedPercent,
        string glyph = "C",
        bool hasError = false,
        int size = 0,
        Color? identityColor = null)
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

            // A dark outer edge plus a light inner track keeps the empty ring visible on
            // both light and dark taskbars, regardless of the UsageAI theme.
            using (var trackOutline = new Pen(
                       Color.FromArgb(210, 24, 30, 40),
                       ringWidth + Math.Max(1.5F, pixels * 0.08F)))
            using (var track = new Pen(Color.FromArgb(235, 205, 214, 226), ringWidth))
            {
                graphics.DrawEllipse(trackOutline, circle);
                graphics.DrawEllipse(track, circle);
            }

            var normalizedUsage = Math.Clamp(usedPercent, 0, 100);
            if (hasError || normalizedUsage > 0)
            {
                var sweep = hasError ? 360F : Math.Max(10F, 360F * normalizedUsage / 100F);
                var fillAlpha = Math.Clamp((int)(55 + normalizedUsage * 0.85), 55, 160);
                using (var fillBrush = new SolidBrush(Color.FromArgb(fillAlpha, color)))
                {
                    graphics.FillPie(fillBrush, circle.X, circle.Y, circle.Width, circle.Height, -90, sweep);
                }

                using var arc = new Pen(color, ringWidth)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                };
                graphics.DrawArc(arc, circle, -90, sweep);
            }

            if (!hasError && normalizedUsage == 0)
            {
                // At 16px there is no room for a readable glyph. A solid identity marker
                // prevents an empty gauge from collapsing visually into the taskbar.
                var markerDiameter = Math.Max(3F, pixels * 0.22F);
                var markerBounds = new RectangleF(
                    (pixels - markerDiameter) / 2F,
                    (pixels - markerDiameter) / 2F,
                    markerDiameter,
                    markerDiameter);
                using var markerOutline = new SolidBrush(Color.FromArgb(220, 24, 30, 40));
                using var marker = new SolidBrush(identityColor ?? color);
                graphics.FillEllipse(
                    markerOutline,
                    RectangleF.Inflate(markerBounds, pixels * 0.05F, pixels * 0.05F));
                graphics.FillEllipse(marker, markerBounds);
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

    public static Icon CreateRefreshing(float angleDegrees, int size = 0)
    {
        var pixels = Math.Clamp(size <= 0 ? PreferredSize : size, 16, 64);
        using var bitmap = new Bitmap(pixels, pixels, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            var ringWidth = Math.Max(2F, pixels * 0.14F);
            var inset = ringWidth / 2F + pixels * 0.06F;
            var circle = new RectangleF(inset, inset, pixels - inset * 2F, pixels - inset * 2F);
            using (var outline = new Pen(
                       Color.FromArgb(210, 24, 30, 40),
                       ringWidth + Math.Max(1.5F, pixels * 0.08F)))
            using (var track = new Pen(Color.FromArgb(180, 205, 214, 226), ringWidth))
            using (var arc = new Pen(Theme.Signal, ringWidth)
                   {
                       StartCap = LineCap.Round,
                       EndCap = LineCap.Round,
                   })
            {
                graphics.DrawEllipse(outline, circle);
                graphics.DrawEllipse(track, circle);
                graphics.DrawArc(arc, circle, angleDegrees, 250F);
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
