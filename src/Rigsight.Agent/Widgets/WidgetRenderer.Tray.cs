using System.Drawing;
using System.Drawing.Drawing2D;
using Rigsight.Core;

namespace Rigsight.Agent.Widgets;

/// <summary>A reading in the taskbar: one number (or two, stacked) drawn as a notification-area icon.</summary>
internal static partial class WidgetRenderer
{
    /// <summary>The reading as the taskbar shows it (see <see cref="Units.TrayText"/>).</summary>
    public static string TrayText(SensorKind kind, double? value) => Units.TrayText(kind, value);

    /// <summary>
    /// Whether the taskbar is light (Windows' own mode, separate from apps'): the numbers are drawn dark on it.
    /// Read at most every few seconds.
    /// </summary>
    internal static bool TaskbarIsLight()
    {
        long now = Environment.TickCount64;
        if (now - _taskbarLightChecked < 3000) return _taskbarLight;
        _taskbarLightChecked = now;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            _taskbarLight = key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch { _taskbarLight = false; }
        return _taskbarLight;
    }

    private static bool _taskbarLight;
    private static long _taskbarLightChecked = long.MinValue / 2;

    /// <summary>Temperatures in their colour (as everywhere in Rigsight), everything else in the taskbar's text colour.</summary>
    public static Color TrayColor(SensorKind kind, double? value, bool light)
    {
        var p = light ? LightPalette : DarkPalette;
        return kind == SensorKind.Temperature ? TempColor(value, p) : value is null ? p.Faint : p.Text;
    }

    /// <summary>
    /// One number filling the icon, or two stacked (the combined icon). Each is drawn as large as fits the width: a
    /// two-digit temperature fills it, "1.2k" gets smaller. Transparent around the digits, like Windows' own tray icons.
    /// </summary>
    public static Bitmap RenderTrayIcon(IReadOnlyList<(string Text, Color Color)> lines, int size)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        if (lines.Count == 0) return bmp;

        int count = Math.Min(lines.Count, 2);
        float band = size / (float)count;
        using var family = new FontFamily("Segoe UI");
        // Two digits set the size: a wider reading ("100", "1.2k") shrinks to fit, a narrower one ("7", "–") isn't
        // blown up past it. The digits' box also places every reading on the same line.
        var digits = Outline("88", family);
        float room = band - (count > 1 ? 1 : 0);
        float largest = Math.Min(size / digits.Width, room / digits.Height);
        for (int i = 0; i < count; i++)
        {
            var (text, color) = lines[i];
            using var path = new GraphicsPath();
            path.AddString(text, family, (int)FontStyle.Bold, 100, PointF.Empty, StringFormat.GenericTypographic);
            var b = path.GetBounds();
            if (b.Width <= 0 || b.Height <= 0) continue;
            float scale = Math.Min(largest, size / b.Width);
            using var m = new Matrix();
            m.Translate(-b.X, -digits.Y, MatrixOrder.Append);
            m.Scale(scale, scale, MatrixOrder.Append);
            m.Translate((size - b.Width * scale) / 2, i * band + (band - digits.Height * scale) / 2, MatrixOrder.Append);
            path.Transform(m);
            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }
        return bmp;
    }

    /// <summary>The box <paramref name="text"/> fills, drawn bold at 100 px (the icon scales it down).</summary>
    private static RectangleF Outline(string text, FontFamily family)
    {
        using var path = new GraphicsPath();
        path.AddString(text, family, (int)FontStyle.Bold, 100, PointF.Empty, StringFormat.GenericTypographic);
        return path.GetBounds();
    }
}
