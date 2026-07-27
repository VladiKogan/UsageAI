using System.Runtime.InteropServices;
using UsageAI.Services;

namespace UsageAI.UI;

/// <summary>
/// A hidden top-level window that receives the "show the dashboard" broadcast from a second
/// launch and the global hotkey. It is deliberately not a message-only window, because those
/// do not receive broadcast messages.
/// </summary>
internal sealed class MessageWindow : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x0A51;
    private const uint ModAlt = 0x0001;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const uint KeyU = 0x55;

    private bool _hotkeyRegistered;

    public MessageWindow() =>
        CreateHandle(new CreateParams
        {
            Caption = "UsageAI.MessageWindow",
            X = 0,
            Y = 0,
            Height = 0,
            Width = 0,
        });

    public event EventHandler? ShowRequested;

    public event EventHandler? HotkeyPressed;

    /// <summary>Registers Win+Alt+U. Returns false when another app already owns the chord.</summary>
    public bool TryRegisterHotkey()
    {
        if (_hotkeyRegistered)
        {
            return true;
        }

        _hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, ModWin | ModAlt | ModNoRepeat, KeyU);
        return _hotkeyRegistered;
    }

    public void UnregisterHotkey()
    {
        if (!_hotkeyRegistered)
        {
            return;
        }

        UnregisterHotKey(Handle, HotkeyId);
        _hotkeyRegistered = false;
    }

    public void Dispose()
    {
        UnregisterHotkey();
        DestroyHandle();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == SingleInstance.ShowMessage && SingleInstance.ShowMessage != 0)
        {
            ShowRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
        }

        base.WndProc(ref m);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
}
