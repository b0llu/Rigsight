using System.Runtime.InteropServices;
using Rigsight.Core.Settings;

namespace Rigsight.Core;

/// <summary>
/// The taskbars on other screens than the main one, and which of them show the readings (<see cref="TrayScreens"/>), the
/// same for the agent that draws them and the Taskbar page that offers the choice. Windows 11 gives each other screen a
/// taskbar of its own ("Shell_SecondaryTrayWnd"), unless "Show my taskbar on all displays" is off in its own settings.
/// </summary>
public static class TaskbarScreens
{
    /// <summary>The other screens' taskbars Windows shows now (lying along the bottom or top, wider than tall), in Windows' order.</summary>
    public static List<IntPtr> Others()
    {
        var list = new List<IntPtr>();
        try
        {
            IntPtr h = IntPtr.Zero;
            while (list.Count < 16 && (h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                if (IsWindowVisible(h) && GetWindowRect(h, out var r) && r.R - r.L > (r.B - r.T) * 4) list.Add(h);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Not Windows: no taskbars to find.
        }
        return list;
    }

    /// <summary>How many screens Windows uses now (1 when it won't say).</summary>
    public static int Screens()
    {
        try { return Math.Max(1, GetSystemMetrics(SmCMonitors)); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return 1; }
    }

    /// <summary>
    /// Whether the main taskbar shows the readings: always but with <see cref="TrayScreens.Others"/>, and then too when no
    /// other taskbar is left (a screen unplugged, or Windows showing the taskbar on the main screen only), so they never
    /// show nowhere.
    /// </summary>
    public static bool OnMain(TrayScreens screens, int otherTaskbars) => screens != TrayScreens.Others || otherTaskbars == 0;

    /// <summary>Whether the other screens' taskbars show the readings.</summary>
    public static bool OnOthers(TrayScreens screens) => screens != TrayScreens.Main;

    /// <summary>A choice's name, the same on the Taskbar page and in the readings' right-click menu.</summary>
    public static string Name(TrayScreens screens) => screens switch
    {
        TrayScreens.Every => Lang.T("Every screen"),
        TrayScreens.Others => Lang.T("Other screens"),
        _ => Lang.T("Main screen"),
    };

    /// <summary>Said in the log: which taskbars show the readings, of how many.</summary>
    public static string Describe(TrayScreens screens, int otherTaskbars)
    {
        if (otherTaskbars == 0) return screens == TrayScreens.Main ? "the main taskbar (no other taskbar)" : $"the main taskbar ({screens} asked, no other taskbar)";
        bool main = OnMain(screens, otherTaskbars), others = OnOthers(screens);
        string them = otherTaskbars == 1 ? "the other screen's taskbar" : $"the {otherTaskbars} other screens' taskbars";
        return main && others ? $"the main taskbar and {them}" : others ? them : $"the main taskbar (not {them})";
    }

    private const int SmCMonitors = 80;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? name);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
