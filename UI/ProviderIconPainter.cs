using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace UsageAI.UI;

internal static class ProviderIconPainter
{
    private const string FontResourceName = "UsageAI.Resources.usageai-providers.ttf";
    private static readonly Lazy<ProviderIconFont?> IconFont = new(
        LoadIconFont,
        LazyThreadSafetyMode.ExecutionAndPublication);

    internal static bool IsBrandFontAvailable => IconFont.Value is not null;

    /// <summary>
    /// Draws the provider's glyph in its brand colour, scaled so its ink fills
    /// <paramref name="bounds"/>. There is no tile behind it: brand colour identifies the
    /// provider, and every other colour in the UI is left to mean usage status.
    /// </summary>
    public static void Draw(Graphics graphics, Rectangle bounds, string providerId)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var color = Theme.ForProvider(providerId);

        var loadedFont = IconFont.Value;
        if (loadedFont is null)
        {
            DrawFallback(graphics, bounds, color, providerId);
            return;
        }

        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormat.GenericTypographic);
        using var glyphPath = new GraphicsPath();
        glyphPath.AddString(
            GlyphFor(providerId),
            loadedFont.Family,
            (int)FontStyle.Regular,
            Math.Max(1F, bounds.Width),
            Point.Empty,
            format);

        var inkBounds = glyphPath.GetBounds();
        if (inkBounds.Width <= 0 || inkBounds.Height <= 0)
        {
            DrawFallback(graphics, bounds, color, providerId);
            return;
        }

        // The glyphs have different side bearings, so fitting the ink rather than the em box is
        // what makes four different marks read as the same size.
        var fit = Math.Min(bounds.Width / inkBounds.Width, bounds.Height / inkBounds.Height);
        using var transform = new Matrix();
        transform.Translate(bounds.Left + bounds.Width / 2F, bounds.Top + bounds.Height / 2F);
        transform.Scale(fit, fit);
        transform.Translate(-(inkBounds.Left + inkBounds.Width / 2F), -(inkBounds.Top + inkBounds.Height / 2F));
        glyphPath.Transform(transform);
        graphics.FillPath(brush, glyphPath);
        GC.KeepAlive(loadedFont);
    }

    private static string GlyphFor(string providerId) => providerId.ToLowerInvariant() switch
    {
        "claude" => "\uE002",
        "copilot" => "\uE003",
        "gemini" => "\uE004",
        _ => "\uE001",
    };

    private static void DrawFallback(Graphics graphics, Rectangle bounds, Color color, string providerId)
    {
        var label = string.IsNullOrWhiteSpace(providerId) ? "?" : providerId[..1].ToUpperInvariant();
        using var font = new Font(
            FontFamily.GenericSansSerif,
            Math.Max(1F, bounds.Width * 0.75F),
            FontStyle.Bold,
            GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        graphics.DrawString(label, font, brush, bounds, format);
    }

    private static ProviderIconFont? LoadIconFont()
    {
        nint fontMemory = 0;
        PrivateFontCollection? collection = null;
        try
        {
            using var stream = typeof(ProviderIconPainter).Assembly.GetManifestResourceStream(FontResourceName);
            if (stream is null || stream.Length is <= 0 or > 128 * 1024)
            {
                return null;
            }

            var fontBytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(fontBytes);
            fontMemory = Marshal.AllocCoTaskMem(fontBytes.Length);
            Marshal.Copy(fontBytes, 0, fontMemory, fontBytes.Length);

            collection = new PrivateFontCollection();
            collection.AddMemoryFont(fontMemory, fontBytes.Length);
            var family = collection.Families.FirstOrDefault();
            if (family is null)
            {
                collection.Dispose();
                Marshal.FreeCoTaskMem(fontMemory);
                return null;
            }

            // This holder is rooted by the static Lazy for the process lifetime. GDI+ requires
            // both the collection and the backing memory to remain alive while its font is used.
            return new ProviderIconFont(collection, family, fontMemory);
        }
        catch (Exception exception) when (exception is ArgumentException or ExternalException or IOException or NotSupportedException)
        {
            collection?.Dispose();
            if (fontMemory != 0)
            {
                Marshal.FreeCoTaskMem(fontMemory);
            }

            return null;
        }
    }

    private sealed record ProviderIconFont(
        PrivateFontCollection Collection,
        FontFamily Family,
        nint FontMemory);
}
