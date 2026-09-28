using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Rigsight.Agent.Ui;

/// <summary>
/// A notification-area icon with a lasting identity: a GUID made from a key (a reading's ID, or a part) and where
/// Rigsight is installed. Windows remembers by it whether the user dragged the icon out from behind the arrow, so an
/// icon made again (a reading added back, Rigsight restarted, another reading added beside it) comes back where it was.
/// WinForms' NotifyIcon can't give one: its icons are known by a number that changes each time one is made. Clicks come
/// back through <see cref="TrayIconHost"/>. UI thread only.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly TrayIconHost _host;
    private readonly int _id;
    private readonly Guid _guid;
    private IntPtr _icon;
    private string _tip = "";
    private bool _added;

    public TrayIcon(TrayIconHost host, string key, Action<MouseButtons> click)
    {
        _host = host;
        _guid = GuidFor(key, Environment.ProcessPath ?? "");
        _id = host.Register(this, click);
    }

    /// <summary>
    /// The icon's GUID: the same for the same key and install, different between installs (Windows ties an icon's
    /// GUID to the program that first showed it, so the test copy and the installed Rigsight mustn't share them).
    /// </summary>
    internal static Guid GuidFor(string key, string exePath) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes($"Rigsight tray|{exePath.ToLowerInvariant()}|{key}")));

    /// <summary>Shows <paramref name="icon"/> (the icon takes it over and destroys it when replaced) with <paramref name="tip"/>.</summary>
    public void Set(IntPtr icon, string tip)
    {
        var old = _icon;
        _icon = icon;
        _tip = tip;
        Apply();
        if (old != IntPtr.Zero && old != icon) DestroyIcon(old);
    }

    /// <summary>A new hover text, the same picture.</summary>
    public void SetTip(string tip)
    {
        _tip = tip;
        if (_icon != IntPtr.Zero) Apply();
    }

    /// <summary>Puts the icon in the tray, or updates it there (and again after Explorer restarts, see <see cref="TrayIconHost"/>).</summary>
    internal void Apply()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_GUID);
        if (_added && Shell_NotifyIcon(NIM_MODIFY, ref data)) return;
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
        if (!_added)
        {
            // Left over from a Rigsight that didn't close cleanly: take it back.
            var gone = Data(NIF_GUID);
            Shell_NotifyIcon(NIM_DELETE, ref gone);
            _added = Shell_NotifyIcon(NIM_ADD, ref data);
        }
    }

    /// <summary>After Explorer restarts its tray is empty: added again from scratch.</summary>
    internal void Readd()
    {
        _added = false;
        if (_icon != IntPtr.Zero) Apply();
    }

    private NOTIFYICONDATA Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _host.Handle,
        uID = _id,
        uFlags = flags,
        uCallbackMessage = TrayIconHost.CallbackMessage,
        hIcon = _icon,
        szTip = _tip.Length > 127 ? _tip[..127] : _tip,
        guidItem = _guid,
    };

    public void Dispose()
    {
        var data = Data(NIF_GUID);
        if (_added) Shell_NotifyIcon(NIM_DELETE, ref data);
        _added = false;
        _host.Unregister(_id);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _icon = IntPtr.Zero;
    }

    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_GUID = 0x20;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}

/// <summary>
/// The hidden window <see cref="TrayIcon"/>s report clicks to, which also hears when Explorer restarts (a new taskbar,
/// an empty tray) and puts every icon back. A real, never-shown top-level window: only those hear that.
/// </summary>
internal sealed class TrayIconHost : NativeWindow, IDisposable
{
    internal const int CallbackMessage = 0x8000 + 0x51; // WM_APP + a little
    private static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly Dictionary<int, (TrayIcon Icon, Action<MouseButtons> Click)> _icons = [];
    private int _next = 1;

    public TrayIconHost()
    {
        CreateHandle(new CreateParams { Caption = "Rigsight readings" });
        // Rigsight runs as admin, Explorer doesn't: Windows drops its messages to us unless let through.
        ChangeWindowMessageFilterEx(Handle, CallbackMessage, MSGFLT_ALLOW, IntPtr.Zero);
        ChangeWindowMessageFilterEx(Handle, TaskbarCreated, MSGFLT_ALLOW, IntPtr.Zero);
    }

    internal int Register(TrayIcon icon, Action<MouseButtons> click)
    {
        int id = _next++;
        _icons[id] = (icon, click);
        return id;
    }

    internal void Unregister(int id) => _icons.Remove(id);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == CallbackMessage && _icons.TryGetValue((int)m.WParam, out var entry))
        {
            switch ((int)m.LParam & 0xFFFF)
            {
                case WM_LBUTTONUP: entry.Click(MouseButtons.Left); break;
                case WM_RBUTTONUP:
                    SetForegroundWindow(Handle); // so the menu closes when clicking elsewhere
                    entry.Click(MouseButtons.Right);
                    break;
            }
            return;
        }
        if (m.Msg == TaskbarCreated)
            foreach (var (icon, _) in _icons.Values.ToList()) icon.Readd();
        base.WndProc(ref m);
    }

    public void Dispose() => DestroyHandle();

    private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, MSGFLT_ALLOW = 1;

    [DllImport("user32.dll")] private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, int message, int action, IntPtr info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
