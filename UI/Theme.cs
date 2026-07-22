namespace UsageAI.UI;

internal static class Theme
{
    public static readonly Color Night = Color.FromArgb(17, 22, 30);
    public static readonly Color Surface = Color.FromArgb(25, 32, 42);
    public static readonly Color SurfaceRaised = Color.FromArgb(32, 41, 53);
    public static readonly Color Hairline = Color.FromArgb(53, 65, 80);
    public static readonly Color Text = Color.FromArgb(241, 245, 249);
    public static readonly Color Muted = Color.FromArgb(145, 158, 174);
    public static readonly Color Signal = Color.FromArgb(85, 188, 216);
    public static readonly Color Warning = Color.FromArgb(241, 174, 85);
    public static readonly Color Critical = Color.FromArgb(238, 105, 113);

    public static Color ForUsage(int usedPercent) => usedPercent switch
    {
        >= 90 => Critical,
        >= 72 => Warning,
        _ => Signal,
    };
}
