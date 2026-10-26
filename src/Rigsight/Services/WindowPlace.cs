using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Rigsight.Core;

namespace Rigsight.Services;

/// <summary>
/// The main window opens where it was last closed, at the same size, maximized if it was (GitHub issue #3, 2026-10-26).
/// Kept per person in the data folder (window.json), read and written with Windows' own window placement, so a place on
/// a screen that's gone since (a monitor unplugged, the resolution lowered) comes back onto one, and it never opens
/// minimized. With nothing kept, the first window fits the screen: 1400 × 920 is taller than a 1080p screen at 125%.
/// Only the app's own window: the tests build theirs without it.
/// </summary>
public static class WindowPlace
{
    private sealed record Kept(int Left, int Top, int Right, int Bottom, bool Maximized);

    private static string FilePath => Path.Combine(RigsightPaths.DataDir, "window.json");

    public static void Keep(Window window)
    {
        var kept = Load();
        // No screen holds any of it now: the default size, centred, as the first time (decided before it shows: once the
        // window is made, Windows has already placed it).
        if (kept is not null && !OnAScreen(kept))
        {
            Log.Write("window", $"Its last place ({kept.Left},{kept.Top} to {kept.Right},{kept.Bottom}) is on no screen now; opening centred");
            kept = null;
        }
        if (kept is null) FitScreen(window);
        else window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.SourceInitialized += (_, _) => { if (kept is not null) Apply(window, kept); };
        window.Closing += (_, _) => Save(window);
    }

    private static Kept? Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Kept>(File.ReadAllText(FilePath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Write("window", $"Its last place couldn't be read ({ex.GetType().Name}); opening at the default size");
            return null;
        }
    }

    /// <summary>The default size, made smaller where the screen's free area is smaller (never under the window's least size).</summary>
    private static void FitScreen(Window window)
    {
        var area = SystemParameters.WorkArea;
        window.Width = Math.Max(window.MinWidth, Math.Min(window.Width, area.Width * 0.94));
        window.Height = Math.Max(window.MinHeight, Math.Min(window.Height, area.Height * 0.94));
    }

    private static bool OnAScreen(Kept kept)
    {
        var rect = new RECT { Left = kept.Left, Top = kept.Top, Right = kept.Right, Bottom = kept.Bottom };
        return rect.Right - rect.Left >= 200 && rect.Bottom - rect.Top >= 200 && MonitorFromRect(ref rect, MONITOR_DEFAULTTONULL) != IntPtr.Zero;
    }

    private static void Apply(Window window, Kept kept)
    {
        var rect = new RECT { Left = kept.Left, Top = kept.Top, Right = kept.Right, Bottom = kept.Bottom };
        // Bigger than that screen's free area now (its resolution lowered): no bigger than it.
        var monitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(monitor, ref info))
        {
            int w = info.Work.Right - info.Work.Left, h = info.Work.Bottom - info.Work.Top;
            if (rect.Right - rect.Left > w) rect.Right = rect.Left + w;
            if (rect.Bottom - rect.Top > h) rect.Bottom = rect.Top + h;
        }
        var hwnd = new WindowInteropHelper(window).Handle;
        var placement = new WINDOWPLACEMENT { Length = Marshal.SizeOf<WINDOWPLACEMENT>(), ShowCmd = SW_HIDE, NormalPosition = rect };
        SetWindowPlacement(hwnd, ref placement);
        if (kept.Maximized) window.WindowState = WindowState.Maximized;
    }

    private static void Save(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var placement = new WINDOWPLACEMENT { Length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (hwnd == IntPtr.Zero || !GetWindowPlacement(hwnd, ref placement)) return;
        // Closed while minimized: kept as it would come back, maximized or not.
        bool maximized = placement.ShowCmd == SW_SHOWMAXIMIZED || (placement.ShowCmd == SW_SHOWMINIMIZED && (placement.Flags & WPF_RESTORETOMAXIMIZED) != 0);
        var r = placement.NormalPosition;
        try
        {
            Directory.CreateDirectory(RigsightPaths.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Kept(r.Left, r.Top, r.Right, r.Bottom, maximized)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("window", $"Its place couldn't be kept ({ex.GetType().Name}: {ex.Message})");
        }
    }

    // ── Windows ─────────────────────────────────────────────────────────

    private const int SW_HIDE = 0, SW_SHOWMINIMIZED = 2, SW_SHOWMAXIMIZED = 3, WPF_RESTORETOMAXIMIZED = 2;
    private const uint MONITOR_DEFAULTTONULL = 0, MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int Length, Flags, ShowCmd;
        public POINT MinPosition, MaxPosition;
        public RECT NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
