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

    /// <summary>The overlay's top-left on <paramref name="screen"/> (see <see cref="OverlayPlacement"/>).</summary>
    internal static Point Place(OverlaySettings settings, Rectangle screen, Size size, int gap)
    {
        var (x, y) = OverlayPlacement.Place(OverlayPlacement.AnchorOf(settings), settings.OffsetX, settings.OffsetY,
            screen.Width, screen.Height, size.Width, size.Height, gap, keepOnScreen: settings.SnapPosition);
        return new Point(screen.Left + (int)Math.Round(x), screen.Top + (int)Math.Round(y));
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
        var at = Place(settings, screen, bmp.Size, (int)(16 * dpi / 96f));

        // Hanging off the screen (the user's choice): only the part on it, never onto the screen next to it.
        var visible = Rectangle.Intersect(new Rectangle(at, bmp.Size), screen);
        if (visible.Size == bmp.Size)
            Win32.SetLayeredBitmap(Handle, bmp, at, 255); // opacity is in the bitmap (background and content apart)
        else if (!visible.IsEmpty)
        {
            using var part = bmp.Clone(new Rectangle(visible.X - at.X, visible.Y - at.Y, visible.Width, visible.Height), bmp.PixelFormat);
            Win32.SetLayeredBitmap(Handle, part, visible.Location, 255);
        }
        // Games that switch to fullscreen can end up above us; stay on top without taking focus.
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }
}
