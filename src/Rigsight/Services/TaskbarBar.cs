using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rigsight.Services;

/// <summary>
/// Windows' taskbar as it is now: its height and width in pixels and its scale (its DPI / 96), so the Taskbar page draws its
/// preview at the real size (a short taskbar, Windows' smaller buttons, draws one line, as the taskbar will); and a
/// drawing of it as an image shown pixel for pixel.
/// </summary>
public static class TaskbarBar
{
    /// <summary>The taskbar's height and width (pixels) and scale; Windows' usual 48 at 100% when it can't be found.</summary>
    public static (int Height, int Width, float Scale) Now()
    {
        try
        {
            IntPtr bar = FindWindow("Shell_TrayWnd", null);
            if (bar != IntPtr.Zero && GetWindowRect(bar, out var r) && r.B - r.T > 0 && r.R - r.L > (r.B - r.T) * 4)
            {
                float scale = GetDpiForWindow(bar) / 96f;
                return (r.B - r.T, r.R - r.L, scale > 0 ? scale : 1);
            }
        }
        catch (Exception) { /* no taskbar to measure: the usual one */ }
        return (48, 1920, 1);
    }

    /// <summary>
    /// A drawing (32-bit, premultiplied) as an image shown pixel for pixel on a screen at <paramref name="scale"/>: its
    /// DPI is the screen's, so WPF neither stretches nor blurs it.
    /// </summary>
    public static BitmapSource ToImage(System.Drawing.Bitmap bmp, float scale)
    {
        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            var image = BitmapSource.Create(bmp.Width, bmp.Height, 96 * scale, 96 * scale, PixelFormats.Pbgra32, null, data.Scan0,
                data.Stride * bmp.Height, data.Stride);
            image.Freeze();
            return image;
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>Opens Windows' taskbar settings (where "Show smaller taskbar buttons" is).</summary>
    public static void OpenSettings()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true }); }
        catch (Exception ex) { Core.Log.Write("taskbar", $"Couldn't open Windows' taskbar settings: {ex.Message}"); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? name);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
