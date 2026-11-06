using System.Runtime.InteropServices;

namespace UsageAI.UI;

/// <summary>
/// The checked lists on Settings' page. A native list box keeps every wheel turn over it, even with
/// nothing to scroll, so the page stopped scrolling wherever a list sat under the mouse; this one
/// passes the wheel to <see cref="WheelTarget"/> whenever it cannot scroll that way itself. With
/// <see cref="CheckOnBoxOnly"/>, a click on an entry only selects it and the tick changes only from
/// a click on the box or from Space, so a list whose selection shows details stays a list to browse.
/// </summary>
internal sealed class SettingsCheckList : CheckedListBox
{
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_CHAR = 0x0102;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_MOUSEWHEEL = 0x020A;

    /// <summary>The box's width at 96 DPI plus the few pixels around it that still mean the box.</summary>
    private const int CheckBoxHitWidth = 20;

    private bool _inUserInput;
    private bool _ownToggle;

    /// <summary>Receives the wheel when the list has nothing more to scroll in that direction.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Control? WheelTarget { get; set; }

    /// <summary>When set, clicking an entry's text selects it without changing its tick.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool CheckOnBoxOnly { get; set; }

    /// <summary>True when a point (in client coordinates) is on an entry's check box.</summary>
    internal bool IsOnCheckBox(Point point)
    {
        var index = IndexFromPoint(point);
        if (index == NoMatches)
        {
            return false;
        }

        var bounds = GetItemRectangle(index);
        return point.X < bounds.Left + new LayoutScale(this)[CheckBoxHitWidth];
    }

    /// <summary>True when the list can still scroll up (<paramref name="up"/>) or down.</summary>
    internal bool CanScroll(bool up)
    {
        if (Items.Count == 0)
        {
            return false;
        }

        return up
            ? TopIndex > 0
            : GetItemRectangle(Items.Count - 1).Bottom > ClientSize.Height;
    }

    protected override void OnItemCheck(ItemCheckEventArgs ice)
    {
        // The stock list ticks from inside its own click and key handling; with CheckOnBoxOnly only
        // the toggles this class makes itself get through. Code that sets a tick is not input.
        if (CheckOnBoxOnly && _inUserInput && !_ownToggle)
        {
            ice.NewValue = ice.CurrentValue;
        }

        base.OnItemCheck(ice);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (CheckOnBoxOnly && e.Button == MouseButtons.Left && IsOnCheckBox(e.Location))
        {
            Toggle(IndexFromPoint(e.Location));
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (CheckOnBoxOnly && e.KeyCode == Keys.Space && e.Modifiers == Keys.None && SelectedIndex >= 0)
        {
            Toggle(SelectedIndex);
            e.SuppressKeyPress = true;
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEWHEEL && WheelTarget is { IsHandleCreated: true } target)
        {
            var delta = unchecked((short)((m.WParam.ToInt64() >> 16) & 0xFFFF));
            if (delta != 0 && !CanScroll(up: delta > 0))
            {
                _ = SendMessage(target.Handle, m.Msg, m.WParam, m.LParam);
                return;
            }
        }

        if (m.Msg is WM_LBUTTONDOWN or WM_LBUTTONUP or WM_LBUTTONDBLCLK or WM_KEYDOWN or WM_CHAR)
        {
            var outer = _inUserInput;
            _inUserInput = true;
            try
            {
                base.WndProc(ref m);
            }
            finally
            {
                _inUserInput = outer;
            }

            return;
        }

        base.WndProc(ref m);
    }

    private void Toggle(int index)
    {
        if (index < 0 || index >= Items.Count)
        {
            return;
        }

        _ownToggle = true;
        try
        {
            SetItemChecked(index, !GetItemChecked(index));
        }
        finally
        {
            _ownToggle = false;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
