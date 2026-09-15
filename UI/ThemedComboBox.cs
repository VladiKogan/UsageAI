namespace UsageAI.UI;

/// <summary>
/// A native drop-down list with owner-drawn items and chrome. WinForms otherwise lets Windows
/// paint the arrow button with a light system colour even when the rest of the app is dark.
/// </summary>
internal sealed class ThemedComboBox : ComboBox
{
    private const int WmPaint = 0x000F;
    private const int WmPrint = 0x0317;
    private const int WmPrintClient = 0x0318;
    private bool _pointerInside;

    public ThemedComboBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        IntegralHeight = false;
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyItemHeight();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyItemHeight();
        Invalidate();
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Bounds.Width <= 0 || e.Bounds.Height <= 0)
        {
            return;
        }

        var isClosedSelection = (e.State & DrawItemState.ComboBoxEdit) != 0;
        var isSelected = !isClosedSelection && (e.State & DrawItemState.Selected) != 0;
        var background = isSelected ? Theme.Accent : Theme.SurfaceRaised;
        var foreground = !Enabled
            ? Theme.Muted
            : isSelected ? Theme.OnAccent : Theme.Text;
        using (var brush = new SolidBrush(background))
        {
            e.Graphics.FillRectangle(brush, e.Bounds);
        }

        if (e.Index >= 0 && e.Index < Items.Count)
        {
            var scale = new LayoutScale(this);
            var textBounds = Rectangle.Inflate(e.Bounds, -scale[8], 0);
            TextRenderer.DrawText(
                e.Graphics,
                GetItemText(Items[e.Index]),
                Font,
                textBounds,
                foreground,
                TextFormatFlags.Left |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix);
        }

        if (isSelected && (e.State & DrawItemState.Focus) != 0)
        {
            using var focus = new Pen(Theme.OnAccent) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            e.Graphics.DrawRectangle(focus, Rectangle.Inflate(e.Bounds, -2, -2));
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _pointerInside = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _pointerInside = false;
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnDropDown(EventArgs e)
    {
        base.OnDropDown(e);
        Invalidate();
    }

    protected override void OnDropDownClosed(EventArgs e)
    {
        base.OnDropDownClosed(e);
        Invalidate();
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg is WmPaint or WmPrint or WmPrintClient)
        {
            PaintChrome(m);
        }
    }

    private void ApplyItemHeight()
    {
        var scale = new LayoutScale(this);
        ItemHeight = Math.Max(Font.Height + scale[8], scale[24]);
        DropDownHeight = ItemHeight * Math.Max(1, Math.Min(Items.Count, 8)) + scale[2];
    }

    private void PaintChrome(Message message)
    {
        if (Width <= 1 || Height <= 1 || !IsHandleCreated)
        {
            return;
        }

        if (message.Msg is WmPrint or WmPrintClient && message.WParam != IntPtr.Zero)
        {
            using var printGraphics = Graphics.FromHdc(message.WParam);
            DrawChrome(printGraphics);
            return;
        }

        using var graphics = CreateGraphics();
        DrawChrome(graphics);
    }

    private void DrawChrome(Graphics graphics)
    {
        var scale = new LayoutScale(this);
        var buttonWidth = Math.Min(Width, Math.Max(scale[26], SystemInformation.VerticalScrollBarWidth));
        var buttonBounds = new Rectangle(Width - buttonWidth, 0, buttonWidth, Height);
        var buttonColor = _pointerInside && !Theme.IsHighContrast
            ? Theme.Blend(Theme.Text, Theme.SurfaceRaised, 0.08)
            : Theme.SurfaceRaised;
        using (var background = new SolidBrush(buttonColor))
        {
            graphics.FillRectangle(background, buttonBounds);
        }

        var borderColor = Focused || DroppedDown ? Theme.Accent : Theme.Hairline;
        using (var border = new Pen(borderColor))
        {
            graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            graphics.DrawLine(border, buttonBounds.Left, 1, buttonBounds.Left, Height - 2);
        }

        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var centerX = buttonBounds.Left + buttonBounds.Width / 2F;
        var centerY = Height / 2F;
        var halfWidth = Math.Max(scale.Exact(3.5F), 2.5F);
        var drop = Math.Max(scale.Exact(2F), 1.5F);
        using var arrow = new Pen(Enabled ? Theme.Text : Theme.Muted, Math.Max(scale.Exact(1.4F), 1F))
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
        };
        graphics.DrawLines(
            arrow,
            new[]
            {
                new PointF(centerX - halfWidth, centerY - drop / 2F),
                new PointF(centerX, centerY + drop),
                new PointF(centerX + halfWidth, centerY - drop / 2F),
            });
    }
}
