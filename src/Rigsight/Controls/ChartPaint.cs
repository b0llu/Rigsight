using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// Shared colors and text helpers for the custom-drawn charts. The colors come from the current theme's
/// palette (Themes/Dark.xaml or Light.xaml) and are reloaded by <see cref="Load"/> when the theme changes.
/// </summary>
internal static class ChartPaint
{
    public static Brush Grid { get; private set; } = null!;
    public static Brush Label { get; private set; } = null!;
    public static Brush TextBrush { get; private set; } = null!;
    public static Brush Muted { get; private set; } = null!;
    public static Brush Track { get; private set; } = null!;
    public static Brush TooltipBg { get; private set; } = null!;
    public static Pen TooltipBorder { get; private set; } = null!;
    public static Brush Cursor { get; private set; } = null!;
    public static Brush Backdrop { get; private set; } = null!;
    public static Color Cpu { get; private set; }
    public static Color Gpu { get; private set; }

    // Temperature and severity colors (the light theme has darker ones, so they read on white).
    public static SolidColorBrush Cool { get; private set; } = null!;
    public static SolidColorBrush Good { get; private set; } = null!;
    public static SolidColorBrush Warm { get; private set; } = null!;
    public static SolidColorBrush Orange { get; private set; } = null!;
    public static SolidColorBrush Hot { get; private set; } = null!;
    public static SolidColorBrush Faint { get; private set; } = null!;
    public static SolidColorBrush CpuBrush { get; private set; } = null!;

    static ChartPaint() => Load();

    /// <summary>Reads the colors from the application's resources (the palette of the current theme).</summary>
    public static void Load()
    {
        Grid = Brush(Res("StrokeColor", 0x1F));
        Label = Brush(Res("FaintColor", 0x6B));
        TextBrush = Brush(Res("TextColor", 0xFF));
        Muted = Brush(Res("MutedColor", 0xA3));
        Track = Brush(Res("Surface2Color", 0x16));
        TooltipBg = Brush(Res("TooltipColor", 0x1C));
        TooltipBorder = new Pen(Brush(Res("StrokeColor", 0x1F)), 1);
        TooltipBorder.Freeze();
        Cursor = Brush(Res("TextColor", 0xFF), 0.4);
        Backdrop = Brush(Res("BgColor", 0x00));
        Cpu = Res("CpuColor", 0x80);
        Gpu = Res("GpuColor", 0x80);
        Cool = Brush(Res("CoolColor", 0x80));
        Good = Brush(Res("GoodColor", 0x80));
        Warm = Brush(Res("WarmColor", 0x80));
        Orange = Brush(Res("OrangeColor", 0x80));
        Hot = Brush(Res("HotColor", 0x80));
        Faint = Brush(Res("FaintColor", 0x6B));
        CpuBrush = Brush(Cpu);
    }

    /// <summary>A palette color; the grey <paramref name="fallback"/> when there are no resources (designer).</summary>
    public static Color Res(string key, byte fallback) =>
        Application.Current?.TryFindResource(key) is Color c ? c : Color.FromRgb(fallback, fallback, fallback);

    private static readonly Typeface Font = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public static SolidColorBrush Frozen(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush Brush(Color c, double opacity = 1)
    {
        var brush = new SolidColorBrush(c) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    public static FormattedText Format(Visual v, string text, double size, Brush brush, bool bold = false) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? Bold : Font, size, brush,
            VisualTreeHelper.GetDpi(v).PixelsPerDip);

    public enum Align { Left, Center, Right }

    public static void Text(DrawingContext dc, Visual v, string text, Point anchor, double size, Brush brush,
        Align align = Align.Left, bool bold = false, bool middle = true)
    {
        var ft = Format(v, text, size, brush, bold);
        double x = align switch { Align.Center => anchor.X - ft.Width / 2, Align.Right => anchor.X - ft.Width, _ => anchor.X };
        dc.DrawText(ft, new Point(x, middle ? anchor.Y - ft.Height / 2 : anchor.Y));
    }

    /// <summary>Draws a multi-line info box near <paramref name="at"/>, kept inside <paramref name="bounds"/>.</summary>
    public static void InfoBox(DrawingContext dc, Visual v, IReadOnlyList<(string Text, Brush Brush, bool Bold)> lines, Point at, Rect bounds)
    {
        var texts = lines.Select(l => Format(v, l.Text, 12, l.Brush, l.Bold)).ToList();
        double w = texts.Max(t => t.Width) + 20, h = texts.Sum(t => t.Height) + 14;
        double x = at.X + 12, y = at.Y;
        if (x + w > bounds.Right) x = at.X - w - 12;
        if (x < bounds.Left) x = bounds.Left;
        if (y + h > bounds.Bottom) y = bounds.Bottom - h;
        if (y < bounds.Top) y = bounds.Top;
        dc.DrawRoundedRectangle(TooltipBg, TooltipBorder, new Rect(x + 0.5, y + 0.5, w, h), 8, 8);
        double ty = y + 7;
        foreach (var t in texts)
        {
            dc.DrawText(t, new Point(x + 10, ty));
            ty += t.Height;
        }
    }
}
