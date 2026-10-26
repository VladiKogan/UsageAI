using Microsoft.Win32;
using UsageAI.Services;

namespace UsageAI.UI;

/// <summary>
/// The resolved palette. Colours are read as properties rather than constants so the app can
/// follow the Windows light/dark setting and the user's accent colour without rebuilding the UI.
/// </summary>
internal static class Theme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string DwmKey = @"Software\Microsoft\Windows\DWM";
    private const string AccentKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

    // AccentPalette holds eight RGBA entries from lightest to darkest; Windows 11 draws accent
    // fills with Light2 in dark mode and Dark1 in light mode.
    private const int AccentLight2 = 1;
    private const int AccentDark1 = 4;

    private static int _warningPercent = 72;
    private static int _criticalPercent = 90;
    private static Func<bool> _highContrastProbe = static () => SystemInformation.HighContrast;

    static Theme() => Apply(ThemeMode.System, 72, 90);

    public static event EventHandler? Changed;

    public static bool IsDark { get; private set; } = true;

    public static bool IsHighContrast { get; private set; }

    /// <summary>Window background.</summary>
    public static Color Night { get; private set; }

    /// <summary>Card background.</summary>
    public static Color Surface { get; private set; }

    /// <summary>Controls and secondary surfaces.</summary>
    public static Color SurfaceRaised { get; private set; }

    /// <summary>Window and card outlines.</summary>
    public static Color Hairline { get; private set; }

    /// <summary>Rules between rows inside one surface; quieter than <see cref="Hairline"/>.</summary>
    public static Color Divider { get; private set; }

    public static Color Track { get; private set; }

    public static Color Text { get; private set; }

    /// <summary>Row labels and secondary values: one step below <see cref="Text"/>.</summary>
    public static Color TextSecondary { get; private set; }

    public static Color Muted { get; private set; }

    /// <summary>Timestamps and hints: the quietest text that still meets 4.5:1 on its surface.</summary>
    public static Color Faint { get; private set; }

    public static Color Signal { get; private set; }

    /// <summary>The meter colour for a limit that is fine.</summary>
    public static Color Success { get; private set; }

    public static Color Warning { get; private set; }

    public static Color Critical { get; private set; }

    /// <summary><see cref="Warning"/> as text, adjusted so a figure stays legible on its surface.</summary>
    public static Color WarningText { get; private set; }

    /// <summary><see cref="Critical"/> as text, adjusted so a figure stays legible on its surface.</summary>
    public static Color CriticalText { get; private set; }

    public static Color Codex { get; private set; }

    public static Color Claude { get; private set; }

    public static Color Copilot { get; private set; }

    public static Color Gemini { get; private set; }

    /// <summary>The Windows accent colour, adjusted for legibility on the current background.</summary>
    public static Color Accent { get; private set; }

    /// <summary>Text drawn on top of <see cref="Accent"/>.</summary>
    public static Color OnAccent { get; private set; }

    public static void Apply(ThemeMode mode, int warningPercent, int criticalPercent)
    {
        _warningPercent = Math.Clamp(warningPercent, 1, 99);
        _criticalPercent = Math.Clamp(criticalPercent, _warningPercent + 1, 100);

        IsHighContrast = ProbeHighContrast();
        var dark = mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => IsSystemDark(),
        };

        IsDark = dark;
        if (IsHighContrast)
        {
            Night = SystemColors.Window;
            Surface = SystemColors.Window;
            SurfaceRaised = SystemColors.Control;
            Hairline = SystemColors.WindowFrame;
            Divider = SystemColors.WindowFrame;
            Track = SystemColors.ControlDark;
            Text = SystemColors.WindowText;
            TextSecondary = SystemColors.WindowText;
            Muted = SystemColors.GrayText;
            Faint = SystemColors.GrayText;
            Signal = SystemColors.Highlight;
            Success = SystemColors.WindowText;
            Warning = SystemColors.WindowText;
            Critical = SystemColors.WindowText;
            WarningText = SystemColors.WindowText;
            CriticalText = SystemColors.WindowText;
            Codex = SystemColors.WindowText;
            Claude = SystemColors.WindowText;
            Copilot = SystemColors.WindowText;
            Gemini = SystemColors.WindowText;
            Accent = SystemColors.Highlight;
            OnAccent = SystemColors.HighlightText;
        }
        else if (dark)
        {
            // Neutral charcoal rather than navy: elevation comes from lightness alone, and colour
            // is reserved for data, so a limit at risk is the brightest thing in the window.
            Night = Color.FromArgb(31, 31, 31);
            Surface = Color.FromArgb(43, 43, 43);
            SurfaceRaised = Color.FromArgb(51, 51, 51);
            Hairline = Color.FromArgb(58, 58, 58);
            Divider = Color.FromArgb(51, 51, 51);
            Track = Color.FromArgb(58, 58, 58);
            Text = Color.FromArgb(241, 241, 239);
            TextSecondary = Color.FromArgb(214, 214, 210);
            Muted = Color.FromArgb(165, 166, 161);
            Faint = Color.FromArgb(150, 151, 146);
            Signal = Color.FromArgb(96, 205, 255);
            Success = Color.FromArgb(143, 176, 142);
            Warning = Color.FromArgb(232, 165, 75);
            Critical = Color.FromArgb(240, 96, 90);
            WarningText = Color.FromArgb(242, 191, 114);
            CriticalText = Color.FromArgb(255, 138, 132);
            Codex = Color.FromArgb(110, 211, 180);
            Claude = Color.FromArgb(222, 155, 112);
            Copilot = Color.FromArgb(157, 169, 255);
            Gemini = Color.FromArgb(100, 170, 255);
        }
        else
        {
            Night = Color.FromArgb(255, 255, 255);
            Surface = Color.FromArgb(247, 248, 250);
            SurfaceRaised = Color.FromArgb(241, 242, 244);
            Hairline = Color.FromArgb(223, 225, 229);
            Divider = Color.FromArgb(236, 238, 241);
            Track = Color.FromArgb(232, 234, 238);
            Text = Color.FromArgb(17, 19, 24);
            TextSecondary = Color.FromArgb(43, 47, 54);
            Muted = Color.FromArgb(95, 99, 104);
            Faint = Color.FromArgb(109, 113, 118);
            Signal = Color.FromArgb(0, 103, 192);
            Success = Color.FromArgb(79, 122, 78);
            Warning = Color.FromArgb(217, 119, 6);
            Critical = Color.FromArgb(196, 36, 42);
            WarningText = Color.FromArgb(146, 64, 14);
            CriticalText = Color.FromArgb(179, 20, 28);
            Codex = Color.FromArgb(13, 122, 96);
            Claude = Color.FromArgb(166, 82, 26);
            Copilot = Color.FromArgb(70, 82, 191);
            Gemini = Color.FromArgb(26, 115, 232);
        }

        if (!IsHighContrast)
        {
            Accent = ResolveAccent(dark);
            OnAccent = ContrastRatio(Accent, DarkOnAccent) >= ContrastRatio(Accent, LightOnAccent)
                ? DarkOnAccent
                : LightOnAccent;
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Re-reads the Windows setting; used when the system theme changes at runtime.</summary>
    public static void Reapply(ThemeMode mode) => Apply(mode, _warningPercent, _criticalPercent);

    /// <summary>
    /// The meter colour for a usage level. Everything below the warning threshold is one calm
    /// colour: a hue per band read as a rainbow, and only a limit at risk should draw the eye.
    /// </summary>
    public static Color ForUsage(int usedPercent) =>
        usedPercent >= _criticalPercent ? Critical :
        usedPercent >= _warningPercent ? Warning :
        Success;

    /// <summary>
    /// The text colour for a usage figure. A fine limit reads in the ordinary text colour, so
    /// colour on a number always means the limit needs attention.
    /// </summary>
    public static Color TextForUsage(int usedPercent) =>
        usedPercent >= _criticalPercent ? CriticalText :
        usedPercent >= _warningPercent ? WarningText :
        Text;

    public static Color ForProvider(string providerId) => providerId.ToLowerInvariant() switch
    {
        "claude" => Claude,
        "copilot" => Copilot,
        "gemini" => Gemini,
        _ => Codex,
    };

    /// <summary>Blends <paramref name="foreground"/> onto <paramref name="background"/>.</summary>
    public static Color Blend(Color foreground, Color background, double amount)
    {
        if (IsHighContrast)
        {
            return foreground;
        }

        var weight = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (int)Math.Round(foreground.R * weight + background.R * (1 - weight)),
            (int)Math.Round(foreground.G * weight + background.G * (1 - weight)),
            (int)Math.Round(foreground.B * weight + background.B * (1 - weight)));
    }

    /// <summary>
    /// Whether a usage level is past the critical threshold. Callers ask this rather than
    /// comparing colours, because High Contrast gives every status the same colour.
    /// </summary>
    public static bool IsCriticalUsage(int usedPercent) => usedPercent >= _criticalPercent;

    public static string UsageCue(int usedPercent) =>
        usedPercent >= _criticalPercent ? "!! " :
        usedPercent >= _warningPercent ? "! " :
        string.Empty;

    internal static void SetHighContrastProbe(Func<bool>? probe)
    {
        _highContrastProbe = probe ?? (static () => SystemInformation.HighContrast);
    }

    private static bool ProbeHighContrast()
    {
        try
        {
            return _highContrastProbe();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: false);
            // Dark is the default when Windows does not report a preference.
            return key?.GetValue("AppsUseLightTheme") is not int appsUseLightTheme || appsUseLightTheme == 0;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    private static Color ResolveAccent(bool dark) => LegibleAccent(ReadAccent(dark), dark);

    private static Color ReadAccent(bool dark)
    {
        var fallback = dark ? Color.FromArgb(96, 205, 255) : Color.FromArgb(0, 103, 192);
        try
        {
            // Windows 11 derives its own light and dark accent shades from the user's choice;
            // using the same entries keeps UsageAI's buttons the colour Windows' own controls are.
            using (var accentKey = Registry.CurrentUser.OpenSubKey(AccentKey, writable: false))
            {
                if (accentKey?.GetValue("AccentPalette") is byte[] { Length: >= 32 } palette)
                {
                    var index = dark ? AccentLight2 : AccentDark1;
                    return Color.FromArgb(palette[index * 4], palette[index * 4 + 1], palette[index * 4 + 2]);
                }
            }

            using var key = Registry.CurrentUser.OpenSubKey(DwmKey, writable: false);

            // DWM stores the accent as 0xAABBGGRR.
            return key?.GetValue("AccentColor") is int accent
                ? Color.FromArgb(accent & 0xFF, (accent >> 8) & 0xFF, (accent >> 16) & 0xFF)
                : fallback;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// Moves an accent towards white on dark backgrounds, or towards black on light ones, until it
    /// stands out from the window (3:1, the bar for a filled control) and some label colour reads
    /// on it at 4.5:1. Windows' own palette entries usually pass untouched; a pale yellow or a
    /// deep navy accent would otherwise give an invisible button or an unreadable label.
    /// </summary>
    internal static Color LegibleAccent(Color accent, bool dark)
    {
        var background = dark ? Night : Color.White;
        var target = dark ? Color.White : Color.Black;
        var candidate = accent;
        for (var step = 1; step <= 10 && !IsLegibleAccent(candidate, background); step++)
        {
            candidate = BlendColors(target, accent, step / 10d);
        }

        return candidate;
    }

    private static bool IsLegibleAccent(Color accent, Color background) =>
        ContrastRatio(accent, background) >= 3 &&
        Math.Max(ContrastRatio(accent, DarkOnAccent), ContrastRatio(accent, LightOnAccent)) >= 4.5;

    private static readonly Color DarkOnAccent = Color.FromArgb(12, 16, 22);
    private static readonly Color LightOnAccent = Color.FromArgb(250, 252, 255);

    /// <summary>The WCAG contrast ratio between two opaque colours.</summary>
    internal static double ContrastRatio(Color first, Color second)
    {
        var a = RelativeLuminance(first);
        var b = RelativeLuminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double RelativeLuminance(Color colour)
    {
        static double Channel(byte value)
        {
            var c = value / 255d;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(colour.R) + 0.7152 * Channel(colour.G) + 0.0722 * Channel(colour.B);
    }

    private static Color BlendColors(Color foreground, Color background, double amount)
    {
        var weight = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (int)Math.Round(foreground.R * weight + background.R * (1 - weight)),
            (int)Math.Round(foreground.G * weight + background.G * (1 - weight)),
            (int)Math.Round(foreground.B * weight + background.B * (1 - weight)));
    }
}
