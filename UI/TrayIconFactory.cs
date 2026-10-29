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

        return ToIcon(bitmap);
    }

    /// <summary>
    /// Draws the UsageAI logo — a ring on a dark tile — with its tank filled to
    /// <paramref name="fillPercent"/>. The water keeps the logo's cyan until
    /// <paramref name="usedPercent"/> crosses the warning or critical threshold, and from there
    /// the ring takes the same colour, so a spent quota shown as remaining (an empty tank) still
    /// reads as critical. The tile is dark whatever the app theme, so the colours are the dark
    /// palette's. Geometry matches scripts\render-logo.ps1 in its 48-unit tile, with a flat
    /// waterline at every size, since a wave would read as a second level.
    /// </summary>
    public static Icon CreateTank(int usedPercent, int fillPercent, int size = 0)
    {
        var pixels = Math.Clamp(size <= 0 ? PreferredSize : size, 16, 64);
        var small = pixels <= 24;
        var ringRadius = small ? 15F : 14.5F;
        var ringWidth = small ? 4.6F : 3.4F;
        var waterRadius = small ? 10.2F : 10.6F;
        var highContrast = Theme.IsHighContrast;
        Color? status = highContrast ? null :
            Theme.IsCriticalUsage(usedPercent) ? Theme.DarkCritical :
            Theme.IsWarningUsage(usedPercent) ? Theme.DarkWarning :
            null;
        var water = highContrast ? SystemColors.Highlight : status ?? Theme.DarkSignal;
        var ringColor = highContrast ? SystemColors.WindowText : status ?? Color.FromArgb(241, 241, 239);

        using var bitmap = new Bitmap(pixels, pixels, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);
            graphics.ScaleTransform(pixels / 48F, pixels / 48F);

            using (var tile = DrawingHelpers.RoundedRectangle(new RectangleF(0, 0, 48, 48), 10.5F))
            using (var tileBrush = new SolidBrush(highContrast ? SystemColors.Window : Color.FromArgb(31, 31, 31)))
            {
                graphics.FillPath(tileBrush, tile);
            }

            using (var ring = new Pen(ringColor, ringWidth))
            {
                graphics.DrawEllipse(ring, 24 - ringRadius, 24 - ringRadius, ringRadius * 2, ringRadius * 2);
            }

            var level = Math.Clamp(fillPercent, 0, 100) / 100F;
            if (level > 0)
            {
                // The water is the circle below its waterline: an arc closed by the chord. A clip
                // region would do the same unsmoothed, and a full tank came out an octagon.
                using var tank = new GraphicsPath();
                var bounds = new RectangleF(24 - waterRadius, 24 - waterRadius, waterRadius * 2, waterRadius * 2);
                if (level >= 1)
                {
                    tank.AddEllipse(bounds);
                }
                else
                {
                    var drop = waterRadius - waterRadius * 2 * level;
                    var theta = (float)(Math.Asin(drop / waterRadius) * 180 / Math.PI);
                    tank.AddArc(bounds, theta, 180 - 2 * theta);
                    tank.CloseFigure();
                }

                using var waterBrush = new SolidBrush(water);
                graphics.FillPath(waterBrush, tank);
            }
        }

        return ToIcon(bitmap);
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

        return ToIcon(bitmap);
    }

    private static Icon ToIcon(Bitmap bitmap)
    {
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
