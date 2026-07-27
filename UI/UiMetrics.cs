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

    /// <summary>Scales a baseline pixel measurement.</summary>
    public int this[int baselinePixels] => (int)Math.Round(baselinePixels * _factor);

    public float Exact(float baselinePixels) => baselinePixels * _factor;

    public Rectangle Rect(int x, int y, int width, int height) =>
        new(this[x], this[y], this[width], this[height]);

    public Padding Pad(int left, int top, int right, int bottom) =>
        new(this[left], this[top], this[right], this[bottom]);
}
