namespace UsageAI.UI;

/// <summary>
/// Resolves font families through fallback chains. The Segoe UI Variable faces ship with
/// Windows 11 and Cascadia Mono with the Terminal, so a Windows 10 machine would otherwise
/// silently fall back to Microsoft Sans Serif and lose the entire type hierarchy.
/// </summary>
internal static class Typography
{
    private static readonly string[] DisplayStack =
    {
        "Segoe UI Variable Display",
        "Segoe UI Semibold",
        "Segoe UI",
        "Tahoma",
    };

    private static readonly string[] TextStack =
    {
        "Segoe UI Variable Text",
        "Segoe UI",
        "Tahoma",
    };

    private static readonly string[] MonoStack =
    {
        "Cascadia Mono",
        "Consolas",
        "Lucida Console",
        "Courier New",
    };

    private static readonly string DisplayFamily = Resolve(DisplayStack);
    private static readonly string TextFamily = Resolve(TextStack);
    private static readonly string MonoFamily = Resolve(MonoStack);

    /// <summary>Headings. The caller owns the returned font.</summary>
    public static Font Display(float points, FontStyle style = FontStyle.Bold) =>
        Create(DisplayFamily, points, style);

    /// <summary>Body and label text. The caller owns the returned font.</summary>
    public static Font Text(float points, FontStyle style = FontStyle.Regular) =>
        Create(TextFamily, points, style);

    /// <summary>Numerals and utility labels, where consistent digit widths matter.</summary>
    public static Font Mono(float points, FontStyle style = FontStyle.Bold) =>
        Create(MonoFamily, points, style);

    private static Font Create(string familyName, float points, FontStyle style)
    {
        var size = Math.Clamp(points, 5F, 48F);
        try
        {
            return new Font(familyName, size, AvailableStyle(familyName, style), GraphicsUnit.Point);
        }
        catch (ArgumentException)
        {
            // The resolved family disappeared (a font cache reset); the default face still renders.
            return new Font(FontFamily.GenericSansSerif, size, FontStyle.Regular, GraphicsUnit.Point);
        }
    }

    private static FontStyle AvailableStyle(string familyName, FontStyle style)
    {
        try
        {
            using var family = new FontFamily(familyName);
            if (family.IsStyleAvailable(style))
            {
                return style;
            }

            return family.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : FontStyle.Bold;
        }
        catch (ArgumentException)
        {
            return style;
        }
    }

    private static string Resolve(string[] stack)
    {
        foreach (var candidate in stack)
        {
            try
            {
                using var family = new FontFamily(candidate);
                return family.Name;
            }
            catch (ArgumentException)
            {
                // Not installed on this machine; try the next face in the chain.
            }
        }

        using var messageBoxFont = SystemFonts.MessageBoxFont;
        return messageBoxFont?.FontFamily.Name ?? FontFamily.GenericSansSerif.Name;
    }
}
