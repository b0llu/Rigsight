using Rigsight.Agent.Native;

namespace Rigsight.Agent.Tracking;

internal readonly record struct ActivitySample(int Pid, string? Exe, bool Fullscreen, uint IdleMs, bool Locked);

/// <summary>Window state of one app across all its top-level windows.</summary>
internal readonly record struct WindowState(bool AnyVisible, bool AnyMinimized);

/// <summary>Figures out which app is in front, whether it's fullscreen, and which apps have open windows.</summary>
internal sealed class ActivityMonitor
{
    // The desktop and taskbar belong to explorer.exe, but looking at them isn't "using File Explorer".
    private static readonly HashSet<string> ShellClasses = ["WorkerW", "Progman", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland"];

    /// <summary>Parts of Windows (and the windowless Rigsight agent) that are never counted as an app you're using.</summary>
    private static readonly HashSet<string> NotApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "SearchHost.exe", "SearchApp.exe", "StartMenuExperienceHost.exe", "ShellExperienceHost.exe", "ShellHost.exe",
        "TextInputHost.exe", "LockApp.exe", "Rigsight.Agent.exe",
    };
    private Dictionary<int, string> _pidToExe = [];

    // Keep delegates alive while native code may call them.
    private readonly Win32.EnumWindowsProc _enumTop;
    private readonly Win32.EnumWindowsProc _enumChild;
    private Dictionary<string, WindowState>? _scanResult;
    private int _hostPid, _childPid;

    public ActivityMonitor()
    {
        _enumTop = OnTopWindow;
        _enumChild = OnChildWindow;
    }

    public void UpdateProcessMap(Dictionary<int, string> pidToExe) => _pidToExe = pidToExe;

    public ActivitySample Sample()
    {
        uint idle = Win32.IdleMilliseconds();
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return new ActivitySample(0, null, false, idle, true);

        Win32.GetWindowThreadProcessId(hwnd, out int pid);
        var exe = ExeFor(pid);

        // Desktop, taskbar, Start, search: you're at the PC, but not in any app.
        if (ShellClasses.Contains(Win32.ClassName(hwnd)) || (exe is not null && NotApps.Contains(exe) && !exe.Equals("LockApp.exe", StringComparison.OrdinalIgnoreCase)))
            return new ActivitySample(0, null, false, idle, false);

        // UWP apps are hosted by ApplicationFrameHost; the real app owns a child window.
        if (exe is not null && exe.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            _hostPid = pid;
            _childPid = 0;
            Win32.EnumChildWindows(hwnd, _enumChild, IntPtr.Zero);
            if (_childPid != 0)
            {
                pid = _childPid;
                exe = ExeFor(pid) ?? exe;
            }
        }

        bool locked = exe is not null && exe.Equals("LockApp.exe", StringComparison.OrdinalIgnoreCase);
        return new ActivitySample(pid, exe, IsFullscreen(hwnd), idle, locked);
    }

    private string? ExeFor(int pid)
    {
        if (pid <= 4) return null;
        if (_pidToExe.TryGetValue(pid, out var exe)) return exe;
        var path = Win32.ProcessPath(pid);
        if (path is null) return null;
        exe = Path.GetFileName(path);
        _pidToExe[pid] = exe;
        return exe;
    }

    private bool OnChildWindow(IntPtr hwnd, IntPtr _)
    {
        Win32.GetWindowThreadProcessId(hwnd, out int pid);
        if (pid != _hostPid && pid != 0)
        {
            _childPid = pid;
            return false;
        }
        return true;
    }

    private static bool IsFullscreen(IntPtr hwnd)
    {
        if (hwnd == Win32.GetShellWindow() || hwnd == Win32.GetDesktopWindow()) return false;
        if (ShellClasses.Contains(Win32.ClassName(hwnd))) return false;
        if (!Win32.GetWindowRect(hwnd, out var r)) return false;

        var monitor = Win32.MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var mi = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(monitor, ref mi)) return false;
        var m = mi.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    /// <summary>Which apps have visible top-level windows, and whether they are all minimized.</summary>
    public Dictionary<string, WindowState> ScanWindows()
    {
        _scanResult = new Dictionary<string, WindowState>(StringComparer.OrdinalIgnoreCase);
        Win32.EnumWindows(_enumTop, IntPtr.Zero);
        var result = _scanResult;
        _scanResult = null;
        return result;
    }

    private bool OnTopWindow(IntPtr hwnd, IntPtr _)
    {
        if (!Win32.IsWindowVisible(hwnd)) return true;
        if (Win32.GetWindow(hwnd, Win32.GW_OWNER) != IntPtr.Zero) return true;
        if (((long)Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE) & Win32.WS_EX_TOOLWINDOW) != 0) return true;
        if (Win32.GetWindowTextLength(hwnd) == 0) return true;
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

        if (ShellClasses.Contains(Win32.ClassName(hwnd))) return true;
        Win32.GetWindowThreadProcessId(hwnd, out int pid);
        var exe = ExeFor(pid);
        if (exe is null || _scanResult is null || NotApps.Contains(exe)) return true;

        bool minimized = Win32.IsIconic(hwnd);
        var prev = _scanResult.GetValueOrDefault(exe);
        _scanResult[exe] = new WindowState(prev.AnyVisible || !minimized, prev.AnyMinimized || minimized);
        return true;
    }
}
