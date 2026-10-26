using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace UsageAI.UI;

/// <summary>
/// Renders the tray icon at the size the shell actually asks for: the popup's ring, an arc on a
/// track. A fixed 32-pixel bitmap scaled down to a 16-pixel tray slot loses the arc, so the icon
/// is drawn to measure, and an error's "!" appears only where there is room to read it.
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

    /// <summary>
    /// Draws the gauge. Its colour always follows <paramref name="usedPercent"/>; the arc sweeps
    /// to <paramref name="fillPercent"/> when the quota display shows what is left, so the ring
    /// and the tooltip number encode the same quantity.
    /// </summary>
    public static Icon Create(
        int usedPercent,
        bool hasError = false,
        int size = 0,
        Color? identityColor = null,
        int? fillPercent = null)
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
                       Theme.IsHighContrast ? SystemColors.WindowFrame : Color.FromArgb(210, 24, 30, 40),
                       ringWidth + Math.Max(1.5F, pixels * 0.08F)))
            using (var track = new Pen(Theme.IsHighContrast ? SystemColors.WindowText : Color.FromArgb(235, 205, 214, 226), ringWidth))
            {
                graphics.DrawEllipse(trackOutline, circle);
                graphics.DrawEllipse(track, circle);
            }

            // A clean arc on the track, the same ring the popup draws: no translucent pie behind
            // it and no letter inside, which at tray size only blurred the arc.
            var normalizedFill = Math.Clamp(fillPercent ?? usedPercent, 0, 100);
            if (hasError || normalizedFill > 0)
            {
                var sweep = hasError ? 360F : Math.Max(10F, 360F * normalizedFill / 100F);
                using var arc = new Pen(color, ringWidth)
                {
                    StartCap = hasError ? LineCap.Flat : LineCap.Round,
                    EndCap = hasError ? LineCap.Flat : LineCap.Round,
                };
                graphics.DrawArc(arc, circle, -90, sweep);
            }

            if (!hasError && normalizedFill == 0)
            {
                // At 16px there is no room for a readable glyph. A solid identity marker
                // prevents an empty gauge from collapsing visually into the taskbar.
                var markerDiameter = Math.Max(3F, pixels * 0.22F);
                var markerBounds = new RectangleF(
                    (pixels - markerDiameter) / 2F,
                    (pixels - markerDiameter) / 2F,
                    markerDiameter,
                    markerDiameter);
                using var markerOutline = new SolidBrush(Theme.IsHighContrast ? SystemColors.WindowFrame : Color.FromArgb(220, 24, 30, 40));
                using var marker = new SolidBrush(identityColor ?? color);
                graphics.FillEllipse(
                    markerOutline,
                    RectangleF.Inflate(markerBounds, pixels * 0.05F, pixels * 0.05F));
                graphics.FillEllipse(marker, markerBounds);
            }

            // Only an error earns a glyph, and only where there is room to read it.
            var displayedGlyph = hasError ? "!" : string.Empty;
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
                       Theme.IsHighContrast ? SystemColors.WindowFrame : Color.FromArgb(210, 24, 30, 40),
                       ringWidth + Math.Max(1.5F, pixels * 0.08F)))
            using (var track = new Pen(Theme.IsHighContrast ? SystemColors.WindowText : Color.FromArgb(180, 205, 214, 226), ringWidth))
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
