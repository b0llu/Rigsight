using System.Runtime.InteropServices;
using Rigsight.Core;

namespace Rigsight.Agent.Native;

/// <summary>
/// Windows' own word on virtual desktops (IVirtualDesktopManager, documented since Windows 10): whether a window sits on
/// a desktop other than the one shown. Asked only of a window Windows' shell hides, a handful each scan. One object for
/// the agent's life; if Windows can't give it, no window counts as on another desktop (as before).
/// </summary>
internal static class VirtualDesktops
{
    private static IVirtualDesktopManager? _manager;
    private static bool _tried;

    /// <summary>True when <paramref name="hwnd"/> belongs to another virtual desktop than the one on screen.</summary>
    public static bool OnAnotherDesktop(IntPtr hwnd)
    {
        if (Manager() is not { } manager) return false;
        try
        {
            // A window with no desktop of its own (a Store app suspended in the background) isn't on another desktop: it's hidden.
            if (manager.GetWindowDesktopId(hwnd, out var desktop) != 0 || desktop == Guid.Empty) return false;
            return manager.IsWindowOnCurrentVirtualDesktop(hwnd, out bool here) == 0 && !here;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }
    }

    private static IVirtualDesktopManager? Manager()
    {
        if (_tried) return _manager;
        _tried = true;
        try { _manager = (IVirtualDesktopManager)new VirtualDesktopManager(); }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Log.Write("windows", $"Windows' virtual desktop manager couldn't be reached ({ex.Message}): windows on other desktops aren't counted as open");
        }
        return _manager;
    }

    [ComImport, Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
    private class VirtualDesktopManager;

    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop);
        [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
        // MoveWindowToDesktop: not used.
    }
}
