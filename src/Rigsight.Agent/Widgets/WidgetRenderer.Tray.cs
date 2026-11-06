using System.Drawing;
using System.Drawing.Drawing2D;
using Rigsight.Core;
using Rigsight.Core.Settings;

#if RIGSIGHT_APP
// Linked into the app for the Taskbar page's preview, in a namespace of its own there (the tests see both assemblies).
namespace Rigsight.TaskbarDrawing;
#else
namespace Rigsight.Agent.Widgets;
#endif

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

    /// <summary>
    /// Temperatures in their colour (as everywhere in Rigsight), everything else in the taskbar's text colour; in
    /// grayscale, temperatures too.
    /// </summary>
    public static Color TrayColor(SensorKind kind, double? value, bool light, bool gray = false)
    {
        // The taskbar's own text and faint text (as the widgets' black and white palettes), so the Taskbar page can draw
        // these too (this file is linked into the app): none of the widgets' settings reach the taskbar.
        var text = light ? Color.Black : Color.White;
        if (value is not double v) return light ? Color.FromArgb(140, 140, 140) : Color.FromArgb(107, 107, 107);
        if (kind != SensorKind.Temperature || gray) return text;
        if (light)
            return v < 45 ? Color.FromArgb(3, 105, 161) : v < 70 ? Color.FromArgb(4, 120, 87) : v < 85 ? Color.FromArgb(180, 83, 9) : Color.FromArgb(185, 28, 28);
        return v < 45 ? Color.FromArgb(56, 189, 248) : v < 70 ? Color.FromArgb(52, 211, 153) : v < 85 ? Color.FromArgb(251, 191, 36) : Color.FromArgb(248, 113, 113);
    }

    /// <summary>A part's words' colour (see <see cref="TrayParts.WordColor"/>): readable at 4.5:1; in grayscale, grey.</summary>
    public static Color TrayWord(TrayPart part, bool light, bool gray = false)
    {
        var (r, g, b) = TrayParts.WordColor(gray ? TrayPart.Other : part, light);
        return Color.FromArgb(r, g, b);
    }

    /// <summary>
    /// An icon's look (<see cref="TrayLook.Level"/> or <see cref="TrayLook.Ring"/>, see <see cref="TrayParts.IconLook"/>),
    /// how full, in its colour: the icon's tile filling up from the bottom, or a ring round its edge.
    /// </summary>
    internal readonly record struct IconLook(TrayLook Look, float Fill, Color Color);

    /// <summary>
    /// One number filling the icon, or two stacked: on a rounded tile in <paramref name="tile"/> (its part's colour, or
    /// one picked, see <see cref="TrayColors.TileAlpha"/>), or beside a bar in the colour of the part they're about
    /// (<paramref name="mark"/>), or alone (the whole width for the digits). Every number stands as tall as two digits
    /// would: a wider one ("184", "1.2k") is drawn narrower first, down to 70%, and only then smaller. Transparent around
    /// the tile or marks, like Windows' own tray icons.
    /// </summary>
    public static Bitmap RenderTrayIcon(IReadOnlyList<(string Text, Color Color)> lines, int size, Color? mark = null, Color? tile = null, IconLook? look = null)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        if (lines.Count == 0) return bmp;

        // The bar: whole pixels (2 at 16 px, 3 at 24, 4 at 32), so it stays crisp, with a pixel's gap to the digits.
        int bar = mark is null || tile is not null || look is not null ? 0 : Math.Max(2, (int)Math.Round(size / 8.0));
        float left = bar > 0 ? bar + 1 : 0;
        if (look is { Look: TrayLook.Level } fillLook)
        {
            // Fill: a faint tile of the reading's colour, filled up from the bottom as far as the reading is, stronger.
            using var shape = RoundedRect(new RectangleF(0, 0, size, size), size * 0.15f);
            using (var faint = new SolidBrush(Color.FromArgb(46, fillLook.Color))) g.FillPath(faint, shape);
            float top = size * (1 - Math.Clamp(fillLook.Fill, 0, 1));
            var state = g.Save();
            g.SetClip(new RectangleF(0, top, size, size - top));
            using (var full = new SolidBrush(Color.FromArgb(150, fillLook.Color))) g.FillPath(full, shape);
            g.Restore(state);
            left = (float)TrayParts.IconInset(size);
        }
        else if (look is { Look: TrayLook.Ring } ringLook)
        {
            // Ring: a track round the inside of the icon, its arc as far round as the reading is, from the top.
            float stroke = Math.Max(2, size / 9f), r = size / 2f - stroke / 2;
            var box = new RectangleF(size / 2f - r, size / 2f - r, 2 * r, 2 * r);
            using (var track = new Pen(Color.FromArgb(60, ringLook.Color), stroke)) g.DrawEllipse(track, box);
            if (ringLook.Fill > 0.005f)
            {
                using var arc = new Pen(ringLook.Color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(arc, box, -90, 360 * Math.Clamp(ringLook.Fill, 0, 1));
            }
            left = stroke + size / 16f;
        }
        else if (tile is { } t)
        {
            // The whole icon, corners rounded; the digits keep a sliver of it on each side.
            using var tileBrush = new SolidBrush(t);
            using var shape = RoundedRect(new RectangleF(0, 0, size, size), size * 0.15f);
            g.FillPath(tileBrush, shape);
            left = (float)TrayParts.IconInset(size);
        }
        else if (mark is { } c)
        {
            using var brush = new SolidBrush(c);
            g.SmoothingMode = SmoothingMode.None;
            g.FillRectangle(brush, 0, 1, bar, size - 2);
            g.SmoothingMode = SmoothingMode.AntiAlias;
        }

        int count = Math.Min(lines.Count, 2);
        // Inside a ring the digits keep to the ring's inside, top and bottom too, so the ring never runs over them.
        // On a tile (or filling up) they keep the same room above and below as at the sides, as a reading in its box does.
        float digitsTop = look is { Look: TrayLook.Ring } || ((tile is not null || look is { Look: TrayLook.Level }) && size > 16) ? left : 0, inside = size - 2 * digitsTop;
        float band = inside / count, width = size - left - (tile is null && look is null ? 0 : left);
        using var family = new FontFamily("Segoe UI");
        // Two digits set the size: a wider reading ("100", "1.2k") shrinks to fit, a narrower one ("7", "–") isn't
        // blown up past it. The digits' box also places every reading on the same line.
        var digits = Outline("88", family);
        float room = band - (count > 1 ? 1 : 0);
        float largest = Math.Min(width / digits.Width, room / digits.Height);
        for (int i = 0; i < count; i++)
        {
            var (text, color) = lines[i];
            using var path = new GraphicsPath();
            path.AddString(text, family, (int)FontStyle.Bold, 100, PointF.Empty, StringFormat.GenericTypographic);
            var b = path.GetBounds();
            if (b.Width <= 0 || b.Height <= 0) continue;
            // Narrower first (people pick a narrow font for a third digit rather than a smaller one), then smaller.
            float narrow = Math.Clamp(width / (b.Width * largest), 0.7f, 1);
            float scale = Math.Min(largest, width / (b.Width * narrow));
            using var m = new Matrix();
            m.Translate(-b.X, -digits.Y, MatrixOrder.Append);
            m.Scale(scale * narrow, scale, MatrixOrder.Append);
            m.Translate(left + (width - b.Width * scale * narrow) / 2, digitsTop + i * band + (band - digits.Height * scale) / 2, MatrixOrder.Append);
            path.Transform(m);
            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }
        return bmp;
    }

    /// <summary>A part's colour mark (see <see cref="TrayParts.Color"/>); in grayscale, every part's is grey.</summary>
    public static Color TrayMark(TrayPart part, bool light, bool gray = false)
    {
        var (r, gr, b) = TrayParts.Color(gray ? TrayPart.Other : part, light);
        return Color.FromArgb(r, gr, b);
    }

    /// <summary>The box <paramref name="text"/> fills, drawn bold at 100 px (the icon scales it down).</summary>
    private static RectangleF Outline(string text, FontFamily family)
    {
        using var path = new GraphicsPath();
        path.AddString(text, family, (int)FontStyle.Bold, 100, PointF.Empty, StringFormat.GenericTypographic);
        return path.GetBounds();
    }
}
