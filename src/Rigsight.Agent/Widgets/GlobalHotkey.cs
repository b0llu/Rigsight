using Rigsight.Agent.Native;
using Rigsight.Core.Settings;

namespace Rigsight.Agent.Widgets;

/// <summary>A system-wide keyboard shortcut, delivered to a hidden message-only window. UI thread only.</summary>
internal sealed class GlobalHotkey : NativeWindow, IDisposable
{
    private const int Id = 1;
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private bool _registered;

    public GlobalHotkey() => CreateHandle(new CreateParams { Parent = HWND_MESSAGE });

    public event Action? Pressed;

    /// <summary>Replaces the current shortcut. Returns false if another program already uses it.</summary>
    public bool Register(Hotkey? hotkey)
    {
        Unregister();
        if (hotkey is not { } h) return true;
        _registered = Win32.RegisterHotKey(Handle, Id, (uint)h.Modifiers | Win32.MOD_NOREPEAT, (uint)h.Key);
        return _registered;
    }

    private void Unregister()
    {
        if (!_registered) return;
        Win32.UnregisterHotKey(Handle, Id);
        _registered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32.WM_HOTKEY && (int)m.WParam == Id)
        {
            Pressed?.Invoke();
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }
}
