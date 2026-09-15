namespace UsageAI.UI;

/// <summary>
/// Converts the layout constants used by the custom-painted controls from their 96-DPI
/// baseline to the DPI a control is actually rendering at. Font sizes are in points and
/// already scale, so only pixel geometry goes through here.
/// </summary>
internal readonly struct LayoutScale
{
    public const int BaselineDpi = 96;

    private readonly float _factor;

    public LayoutScale(Control control) => _factor = Math.Clamp(control.DeviceDpi / (float)BaselineDpi, 0.5F, 6F);

    /// <summary>Explicit-DPI seam for deterministic geometry tests and previews.</summary>
    internal LayoutScale(int dpi) => _factor = Math.Clamp(dpi / (float)BaselineDpi, 0.5F, 6F);

    internal float Factor => _factor;

    /// <summary>Preserves a logical pixel offset while moving between monitor DPIs.</summary>
    internal static int ScaleBetweenDpis(int pixels, int oldDpi, int newDpi)
    {
        if (pixels <= 0 || oldDpi <= 0 || newDpi <= 0)
        {
            return Math.Max(0, pixels);
        }

        return (int)Math.Min(
            int.MaxValue,
            Math.Round(pixels * (double)newDpi / oldDpi));
    }

    /// <summary>Scales a baseline pixel measurement.</summary>
    public int this[int baselinePixels] => (int)Math.Round(baselinePixels * _factor);

    public float Exact(float baselinePixels) => baselinePixels * _factor;

    public Rectangle Rect(int x, int y, int width, int height) =>
        new(this[x], this[y], this[width], this[height]);

    public Padding Pad(int left, int top, int right, int bottom) =>
        new(this[left], this[top], this[right], this[bottom]);
}
