using System.Drawing;
using System.Runtime.InteropServices;
using Rigsight.Agent.Native;
using Rigsight.Core.Settings;

namespace Rigsight.Agent.Widgets;

/// <summary>
/// The overlay window: always on top, never takes focus and lets every click through, so it can sit
/// over a game without getting in the way. It follows the screen of whatever is in front.
/// </summary>
internal sealed class OverlayForm : Form
{
    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Text = "Rigsight overlay";
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TRANSPARENT;
            return cp;
        }
    }

    public void Redraw(OverlaySettings settings, WidgetData? data)
    {
        if (!IsHandleCreated) return;

        // Draw on the monitor the game (or whatever is in front) is on.
        IntPtr monitor = Win32.MonitorFromWindow(Win32.GetForegroundWindow(), 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        Rectangle screen = Win32.GetMonitorInfo(monitor, ref info)
            ? Rectangle.FromLTRB(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom)
            : Screen.PrimaryScreen!.Bounds;
        float dpi = Win32.GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 ? dpiX : DeviceDpi;

        using var bmp = WidgetRenderer.RenderOverlay(settings, data, dpi / 96f * (float)settings.Scale);
        int margin = (int)(16 * dpi / 96f);
        bool right = settings.Corner is OverlayCorner.TopRight or OverlayCorner.BottomRight;
        bool bottom = settings.Corner is OverlayCorner.BottomLeft or OverlayCorner.BottomRight;
        var at = new Point(
            right ? screen.Right - bmp.Width - margin : screen.Left + margin,
            bottom ? screen.Bottom - bmp.Height - margin : screen.Top + margin);

        Win32.SetLayeredBitmap(Handle, bmp, at, (byte)Math.Round(settings.Opacity * 255));
        // Games that switch to fullscreen can end up above us; stay on top without taking focus.
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }
}
