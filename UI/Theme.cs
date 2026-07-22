namespace UsageAI.UI;

internal static class Theme
{
    public static readonly Color Night = Color.FromArgb(10, 14, 20);
    public static readonly Color Surface = Color.FromArgb(17, 24, 33);
    public static readonly Color SurfaceRaised = Color.FromArgb(23, 32, 44);
    public static readonly Color Hairline = Color.FromArgb(42, 55, 70);
    public static readonly Color Track = Color.FromArgb(8, 12, 18);
    public static readonly Color Text = Color.FromArgb(244, 247, 250);
    public static readonly Color Muted = Color.FromArgb(139, 153, 169);
    public static readonly Color Signal = Color.FromArgb(103, 204, 232);
    public static readonly Color Success = Color.FromArgb(105, 211, 164);
    public static readonly Color Warning = Color.FromArgb(240, 177, 91);
    public static readonly Color Critical = Color.FromArgb(239, 107, 119);

    public static readonly Color Codex = Color.FromArgb(110, 211, 180);
    public static readonly Color Claude = Color.FromArgb(222, 155, 112);
    public static readonly Color Copilot = Color.FromArgb(157, 169, 255);

    public static Color ForUsage(int usedPercent) => usedPercent switch
    {
        >= 90 => Critical,
        >= 72 => Warning,
        _ => Signal,
    };

    public static Color ForProvider(string providerId) => providerId.ToLowerInvariant() switch
    {
        "claude" => Claude,
        "copilot" => Copilot,
        _ => Codex,
    };
}
