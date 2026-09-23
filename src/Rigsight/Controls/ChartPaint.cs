using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>Shared colors and text helpers for the custom-drawn charts.</summary>
internal static class ChartPaint
{
    public static readonly Brush Grid = Frozen(0x23, 0x2B, 0x3D);
    public static readonly Brush Label = Frozen(0x5B, 0x64, 0x7A);
    public static readonly Brush TextBrush = Frozen(0xE8, 0xEC, 0xF4);
    public static readonly Brush Muted = Frozen(0x8A, 0x93, 0xA8);
    public static readonly Brush Track = Frozen(0x1B, 0x21, 0x30);
    public static readonly Brush TooltipBg = Frozen(0x23, 0x2A, 0x3C);
    public static readonly Brush Cursor = Frozen(0xE8, 0xEC, 0xF4, 0x60);
    public static readonly Color Cpu = Color.FromRgb(0x5B, 0x8C, 0xFF);
    public static readonly Color Gpu = Color.FromRgb(0x3D, 0xDC, 0x97);

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
        dc.DrawRoundedRectangle(TooltipBg, null, new Rect(x, y, w, h), 8, 8);
        double ty = y + 7;
        foreach (var t in texts)
        {
            dc.DrawText(t, new Point(x + 10, ty));
            ty += t.Height;
        }
    }
}
