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

    private static int _warningPercent = 72;
    private static int _criticalPercent = 90;

    static Theme() => Apply(ThemeMode.System, 72, 90);

    public static event EventHandler? Changed;

    public static bool IsDark { get; private set; } = true;

    /// <summary>Window background.</summary>
    public static Color Night { get; private set; }

    /// <summary>Card background.</summary>
    public static Color Surface { get; private set; }

    /// <summary>Controls and secondary surfaces.</summary>
    public static Color SurfaceRaised { get; private set; }

    public static Color Hairline { get; private set; }

    public static Color Track { get; private set; }

    public static Color Text { get; private set; }

    public static Color Muted { get; private set; }

    public static Color Signal { get; private set; }

    public static Color Success { get; private set; }

    public static Color Warning { get; private set; }

    public static Color Critical { get; private set; }

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

        var dark = mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => IsSystemDark(),
        };

        IsDark = dark;
        if (dark)
        {
            Night = Color.FromArgb(10, 14, 20);
            Surface = Color.FromArgb(17, 24, 33);
            SurfaceRaised = Color.FromArgb(23, 32, 44);
            Hairline = Color.FromArgb(42, 55, 70);
            Track = Color.FromArgb(8, 12, 18);
            Text = Color.FromArgb(244, 247, 250);
            Muted = Color.FromArgb(148, 162, 178);
            Signal = Color.FromArgb(103, 204, 232);
            Success = Color.FromArgb(105, 211, 164);
            Warning = Color.FromArgb(240, 177, 91);
            Critical = Color.FromArgb(239, 107, 119);
            Codex = Color.FromArgb(110, 211, 180);
            Claude = Color.FromArgb(222, 155, 112);
            Copilot = Color.FromArgb(157, 169, 255);
            Gemini = Color.FromArgb(100, 170, 255);
        }
        else
        {
            Night = Color.FromArgb(243, 246, 250);
            Surface = Color.FromArgb(255, 255, 255);
            SurfaceRaised = Color.FromArgb(236, 241, 247);
            Hairline = Color.FromArgb(210, 219, 229);
            Track = Color.FromArgb(224, 231, 240);
            Text = Color.FromArgb(16, 24, 34);
            Muted = Color.FromArgb(88, 100, 116);
            Signal = Color.FromArgb(11, 116, 153);
            Success = Color.FromArgb(21, 122, 81);
            Warning = Color.FromArgb(166, 92, 8);
            Critical = Color.FromArgb(184, 39, 53);
            Codex = Color.FromArgb(13, 122, 96);
            Claude = Color.FromArgb(166, 82, 26);
            Copilot = Color.FromArgb(70, 82, 191);
            Gemini = Color.FromArgb(26, 115, 232);
        }

        Accent = ResolveAccent(dark);
        OnAccent = Luminance(Accent) > 0.55 ? Color.FromArgb(12, 16, 22) : Color.FromArgb(250, 252, 255);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Re-reads the Windows setting; used when the system theme changes at runtime.</summary>
    public static void Reapply(ThemeMode mode) => Apply(mode, _warningPercent, _criticalPercent);

    public static Color ForUsage(int usedPercent) =>
        usedPercent >= _criticalPercent ? Critical :
        usedPercent >= _warningPercent ? Warning :
        usedPercent < 50 ? Success :
        Signal;

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
        var weight = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (int)Math.Round(foreground.R * weight + background.R * (1 - weight)),
            (int)Math.Round(foreground.G * weight + background.G * (1 - weight)),
            (int)Math.Round(foreground.B * weight + background.B * (1 - weight)));
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

    private static Color ResolveAccent(bool dark)
    {
        var fallback = dark ? Color.FromArgb(66, 133, 191) : Color.FromArgb(20, 96, 160);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DwmKey, writable: false);
            if (key?.GetValue("AccentColor") is not int accent)
            {
                return fallback;
            }

            // DWM stores the accent as 0xAABBGGRR.
            var colour = Color.FromArgb(
                accent & 0xFF,
                (accent >> 8) & 0xFF,
                (accent >> 16) & 0xFF);
            var luminance = Luminance(colour);
            return dark switch
            {
                true when luminance < 0.16 => Blend(colour, Color.White, 0.62),
                false when luminance > 0.78 => Blend(colour, Color.Black, 0.68),
                _ => colour,
            };
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return fallback;
        }
    }

    private static double Luminance(Color colour) =>
        (0.2126 * colour.R + 0.7152 * colour.G + 0.0722 * colour.B) / 255d;
}
