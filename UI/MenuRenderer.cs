using System.Drawing.Drawing2D;

namespace UsageAI.UI;

/// <summary>
/// Paints the tray menu like a Windows 11 context menu: a flat surface, a rounded highlight
/// inset from the edges, quiet separators, and no image-margin gradient. It still derives from
/// <see cref="ToolStripProfessionalRenderer"/>, so anything it does not paint keeps the
/// standard behaviour. High Contrast does not use it: the menu falls back to the system renderer.
/// </summary>
internal sealed class MenuRenderer : ToolStripProfessionalRenderer
{
    public MenuRenderer()
        : base(new MenuColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Theme.Surface);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (RoundedByWindows)
        {
            // Windows 11 clips the menu to rounded corners and draws its own border along them;
            // a square one on top would show broken corners and a doubled edge.
            return;
        }

        using var pen = new Pen(Theme.Hairline);
        var bounds = e.AffectedBounds;
        e.Graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
    }

    /// <summary>Windows 11 (build 22000) is the first to honour the rounded-corner request.</summary>
    internal static bool RoundedByWindows { get; set; } =
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // No gradient strip down the left edge.
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled)
        {
            return;
        }

        var scale = new LayoutScale(e.ToolStrip?.DeviceDpi ?? LayoutScale.BaselineDpi);
        var bounds = new Rectangle(scale[4], 1, e.Item.Width - scale[8], e.Item.Height - 2);
        var previous = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = DrawingHelpers.RoundedRectangle(bounds, scale.Exact(4));
        using var brush = new SolidBrush(Theme.Blend(Theme.Text, Theme.Surface, Theme.IsDark ? 0.08 : 0.06));
        e.Graphics.FillPath(brush, path);
        e.Graphics.SmoothingMode = previous;
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var scale = new LayoutScale(e.ToolStrip?.DeviceDpi ?? LayoutScale.BaselineDpi);
        var y = e.Item.Height / 2;
        using var pen = new Pen(Theme.Divider);
        e.Graphics.DrawLine(pen, scale[8], y, e.Item.Width - scale[8], y);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Faint;
        base.OnRenderItemText(e);
    }

    private sealed class MenuColorTable : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Theme.SurfaceRaised;

        public override Color MenuItemBorder => Color.Transparent;

        public override Color ToolStripDropDownBackground => Theme.Surface;

        public override Color ImageMarginGradientBegin => Theme.Surface;

        public override Color ImageMarginGradientMiddle => Theme.Surface;

        public override Color ImageMarginGradientEnd => Theme.Surface;

        public override Color SeparatorDark => Theme.Divider;

        public override Color SeparatorLight => Theme.Divider;
    }
}
