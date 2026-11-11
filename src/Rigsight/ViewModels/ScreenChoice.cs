using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core;

namespace Rigsight.ViewModels;

/// <summary>
/// A screen on the Overlay page's Look card, when the PC has two or more (approved mock-up BrxrxUeSZAHcqXGrxtt4LC,
/// 2026-11-09): its number as Windows numbers it, its size, the main one and the one games were last played on marked,
/// and its box on the little map of where they sit. Picking it gives the picture its shape.
/// </summary>
public sealed partial class ScreenChoice(OverlayViewModel owner, ScreenChoice.Monitor monitor, double x, double y, double w, double h) : ObservableObject
{
    /// <summary>A screen as Windows has it: its name ("\\.\DISPLAY2"), where it is and how big in pixels, its scale, main or not, games or not.</summary>
    public sealed record Monitor(string Device, int Left, int Top, int Width, int Height, double Scale, bool IsMain, bool IsGames);

    public string Device => monitor.Device;

    /// <summary>Its number as Windows' Display settings show it (from "\\.\DISPLAY2"), else its place in the list.</summary>
    public int Number { get; } = NumberOf(monitor.Device);

    /// <summary>"2560 × 1440", and "main" for the main screen.</summary>
    public string Detail => $"{monitor.Width} × {monitor.Height}" + (monitor.IsMain ? "  ·  " + Lang.T("main") : "");

    public bool IsMain => monitor.IsMain;
    public bool IsGames => monitor.IsGames;

    /// <summary>Its size as WPF measures it (device-independent pixels), for the picture.</summary>
    public double DipWidth => monitor.Width / monitor.Scale;
    public double DipHeight => monitor.Height / monitor.Scale;

    /// <summary>Its box on the little map.</summary>
    public double X { get; } = x;
    public double Y { get; } = y;
    public double W { get; } = w;
    public double H { get; } = h;

    public string Name => Lang.T($"Screen {Number}");

    public bool IsPicked
    {
        get => ReferenceEquals(owner.Picked, this);
        set { if (value) owner.Picked = this; }
    }

    internal void Changed() => OnPropertyChanged(nameof(IsPicked));

    internal static int NumberOf(string device)
    {
        int at = device.LastIndexOf("DISPLAY", StringComparison.OrdinalIgnoreCase);
        return at >= 0 && int.TryParse(device[(at + 7)..], out int n) ? n : 0;
    }

    /// <summary>This PC's screens now, the one called <paramref name="gameScreen"/> marked as where games were last played.</summary>
    internal static List<Monitor> ReadAll(string? gameScreen)
    {
        var list = new List<Monitor>();
        foreach (var s in System.Windows.Forms.Screen.AllScreens)
        {
            var b = s.Bounds;
            double scale = ScaleOf(b);
            list.Add(new Monitor(s.DeviceName, b.Left, b.Top, b.Width, b.Height, scale, s.Primary,
                gameScreen is not null && string.Equals(s.DeviceName, gameScreen, StringComparison.OrdinalIgnoreCase)));
        }
        // In Windows' order, 1 first.
        return [.. list.OrderBy(m => NumberOf(m.Device) is var n and > 0 ? n : int.MaxValue)];
    }

    /// <summary>A screen's own scale (its DPI over 96), as the agent draws a widget on it; 1 where Windows can't say.</summary>
    internal static double ScaleOf(System.Drawing.Rectangle bounds)
    {
        try
        {
            IntPtr monitor = MonitorFromPoint(new POINT { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + bounds.Height / 2 }, 2 /* nearest */);
            if (GetDpiForMonitor(monitor, 0 /* effective */, out uint dpi, out _) == 0 && dpi > 0) return dpi / 96.0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return 1;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
