using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// A grouped hover's note in a shaded box of its own, under the facts (driver activity, approved mock-up v6,
/// 2026-11-21): a small caps <see cref="Label"/> and the answer in bright <see cref="Value"/> ("POSSIBLE CAUSE" /
/// "Your Ethernet adapter") with the evidence as a quieter <see cref="Sentence"/>; or, with no value, the sentence
/// alone in full colour (a minute's line); <see cref="Detail"/> small and grey under it (the device and its file).
/// <see cref="Quiet"/> greys the sentence even without a value (a minute whose readings were dropped).
/// </summary>
public readonly record struct HoverNote(string? Label, string? Value, string Sentence, string? Detail = null, bool Quiet = false);

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
    /// <summary>A light shade over what came after a crash, on its lanes.</summary>
    public static Brush After { get; private set; } = null!;
    public static Color Cpu { get; private set; }
    public static Color Gpu { get; private set; }

    // Temperature and severity colors (the light theme has darker ones, so they read on white).
    public static SolidColorBrush Cool { get; private set; } = null!;
    public static SolidColorBrush Good { get; private set; } = null!;
    public static SolidColorBrush Warm { get; private set; } = null!;
    public static SolidColorBrush Orange { get; private set; } = null!;
    public static SolidColorBrush Hot { get; private set; } = null!;
    public static SolidColorBrush ReadCool { get; private set; } = null!;
    public static SolidColorBrush ReadGood { get; private set; } = null!;
    public static SolidColorBrush ReadWarm { get; private set; } = null!;
    public static SolidColorBrush ReadHot { get; private set; } = null!;
    public static SolidColorBrush Faint { get; private set; } = null!;
    public static SolidColorBrush CpuBrush { get; private set; } = null!;
    /// <summary>The Games page's frame rate and its chart's update markers (see <see cref="Services.Accents.Fps"/>).</summary>
    public static SolidColorBrush Fps { get; private set; } = null!;
    public static SolidColorBrush FpsMark { get; private set; } = null!;
    public static SolidColorBrush FpsMarkStroke { get; private set; } = null!;
    public static SolidColorBrush FpsMarkText { get; private set; } = null!;

    static ChartPaint() => Load();

    /// <summary>Reads the colors from the application's resources (the palette of the current theme).</summary>
    public static void Load()
    {
        Forget();
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
        After = Brush(Res("TextColor", 0xFF), 0.035);
        Cpu = Res("CpuColor", 0x80);
        Gpu = Res("GpuColor", 0x80);
        Cool = Brush(Res("CoolColor", 0x80));
        Good = Brush(Res("GoodColor", 0x80));
        Warm = Brush(Res("WarmColor", 0x80));
        Orange = Brush(Res("OrangeColor", 0x80));
        Hot = Brush(Res("HotColor", 0x80));
        // A reading's colours by how it is doing: the same four, or shades of the accent if the person chose that.
        // Warnings (a crash's mark, a gap in a chart) stay on the four above.
        (ReadCool, ReadGood, ReadWarm, ReadHot) = (Cool, Good, Warm, Hot);
        if (Services.ThemeManager.ReadingAccent is { } accent)
        {
            var shades = Services.Accents.ReadingShades(accent, Res("TextColor", 0xFF), Res("BgColor", 0x00));
            (ReadCool, ReadGood, ReadWarm, ReadHot) = (Brush(shades.Cool), Brush(shades.Good), Brush(shades.Warm), Brush(shades.Hot));
        }
        Faint = Brush(Res("FaintColor", 0x6B));
        CpuBrush = Brush(Cpu);
        Fps = Brush(Res("FpsColor", 0x80));
        FpsMark = Brush(Res("FpsMarkColor", 0x22));
        FpsMarkStroke = Brush(Res("FpsMarkStrokeColor", 0x3A));
        FpsMarkText = Brush(Res("FpsMarkTextColor", 0xC0));
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

    /// <summary>A new piece of text, for a chart that sets its width or trimming itself (see <see cref="Text"/> to just draw one).</summary>
    public static FormattedText Format(Visual v, string text, double size, Brush brush, bool bold = false) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? Bold : Font, size, brush,
            VisualTreeHelper.GetDpi(v).PixelsPerDip);

    public enum Align { Left, Center, Right }

    public static void Text(DrawingContext dc, Visual v, string text, Point anchor, double size, Brush brush,
        Align align = Align.Left, bool bold = false, bool middle = true)
    {
        var key = Key(v, text, size, brush, bold);
        var ft = Lay(key);
        double x = align switch { Align.Center => anchor.X - ft.Width / 2, Align.Right => anchor.X - ft.Width, _ => anchor.X };
        var at = new Point(x, middle ? anchor.Y - ft.Height / 2 : anchor.Y);
        // Drawn again where it was drawn before: the same drawing. A drawing is only ever shown at its own place: one
        // shown at two places in a frame is drawn by WPF as moving text, softer (2026-11-08, a bar total that two bars
        // had, "59m", came out dimmer than the others).
        var placed = (key, at.X, at.Y);
        var drawn = _drawn ??= [];
        if (!drawn.TryGetValue(placed, out var drawing))
        {
            if (drawn.Count >= LaidMost) drawn.Clear();
            var group = new DrawingGroup();
            using (var gc = group.Open()) gc.DrawText(ft, at);
            // Frozen only when its brush already is: freezing the drawing would freeze a chart's own brush with it.
            if (brush.IsFrozen && group.CanFreeze) group.Freeze();
            drawn[placed] = drawing = group;
        }
        dc.DrawDrawing(drawing);
    }

    /// <summary>How wide a piece of text is, as <see cref="Text"/> would draw it.</summary>
    public static double Width(Visual v, string text, double size, Brush brush, bool bold = false) => Lay(Key(v, text, size, brush, bold)).Width;

    // A chart's text, laid out once (and drawn again as it was where it stays put). The charts draw every label on every
    // frame of a switch's motion, and laying each one out again every time (and the garbage that left) was most of what
    // changing a Reports period cost (2026-11-08: ChartPaint.Text 266 ms of the window's 1,364 over four switches,
    // garbage collection 226). The laid-out text is only ever drawn here, never handed out, so nothing changes it.
    private sealed record TextKey(string Text, double Size, Brush Brush, bool Bold, double PixelsPerDip);
    [ThreadStatic] private static Dictionary<TextKey, FormattedText>? _laid;
    [ThreadStatic] private static Dictionary<(TextKey, double, double), Drawing>? _drawn;
    // Labels, totals and the numbers a count passes through, and where they stood: the most a busy page has, with room.
    private const int LaidMost = 4000;

    private static TextKey Key(Visual v, string text, double size, Brush brush, bool bold) =>
        new(text, size, brush, bold, VisualTreeHelper.GetDpi(v).PixelsPerDip);

    private static FormattedText Lay(TextKey key)
    {
        var laid = _laid ??= [];
        if (laid.TryGetValue(key, out var ft)) return ft;
        if (laid.Count >= LaidMost) laid.Clear();
        ft = new FormattedText(key.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, key.Bold ? Bold : Font, key.Size, key.Brush, key.PixelsPerDip);
        _ = ft.Width; // laid out now, once
        return laid[key] = ft;
    }

    /// <summary>Text laid out for one theme isn't the next one's (its brushes are new).</summary>
    public static void Forget()
    {
        _laid?.Clear();
        _drawn?.Clear();
    }

    /// <summary>Draws a multi-line info box near <paramref name="at"/>, kept inside <paramref name="bounds"/>.</summary>
    /// <summary>
    /// A hover box beside <paramref name="at"/>: its top there, or its bottom there with <paramref name="above"/>; kept
    /// inside <paramref name="bounds"/>, at least <paramref name="minTop"/> down. Returns where it was drawn.
    /// </summary>
    public static Rect InfoBox(DrawingContext dc, Visual v, IReadOnlyList<(string Text, Brush Brush, bool Bold)> lines, Point at, Rect bounds, bool above = false, double minTop = double.NegativeInfinity)
    {
        // Room to read (the user, 2026-11-20: the hovers looked cramped): 12 px in from the sides, 10 from the top and
        // bottom, 3 between lines, and a little more under a bold first line, which is the box's title.
        var texts = lines.Select(l => Format(v, l.Text, 12, l.Brush, l.Bold)).ToList();
        double gaps = Math.Max(0, texts.Count - 1) * LineGap + (lines.Count > 1 && lines[0].Bold ? TitleGap - LineGap : 0);
        double w = texts.Max(t => t.Width) + 2 * PadX, h = texts.Sum(t => t.Height) + gaps + 2 * PadY;
        var (x, y) = Place(at, w, h, bounds, above, minTop);
        dc.DrawRoundedRectangle(TooltipBg, TooltipBorder, new Rect(x + 0.5, y + 0.5, w, h), Corner, Corner);
        double ty = y + PadY;
        for (int i = 0; i < texts.Count; i++)
        {
            dc.DrawText(texts[i], new Point(x + PadX, ty));
            ty += texts[i].Height + (i == 0 && lines[0].Bold && texts.Count > 1 ? TitleGap : LineGap);
        }
        return new Rect(x, y, w, h);
    }

    private const double PadX = 12, PadY = 10, LineGap = 3, TitleGap = 6, Corner = 10;

    /// <summary>Where a hover box of <paramref name="w"/> by <paramref name="h"/> goes: beside <paramref name="at"/>, kept inside <paramref name="bounds"/>.</summary>
    private static (double X, double Y) Place(Point at, double w, double h, Rect bounds, bool above, double minTop)
    {
        double x = at.X + 12, y = above ? at.Y - h : Math.Max(at.Y, minTop);
        if (x + w > bounds.Right) x = at.X - w - 12;
        if (x < bounds.Left) x = bounds.Left;
        if (y + h > bounds.Bottom) y = bounds.Bottom - h;
        if (y < bounds.Top) y = bounds.Top;
        return (x, y);
    }

    /// <summary>A grouped hover's facts: a caps label over its value, two to a row; <see cref="Warm"/> when it needs a look.</summary>
    public readonly record struct HoverFact(string Label, string Value, Brush? Brush = null);


    /// <summary>
    /// A hover with more to say (a play on the Games page): a title and a grey line under it, one big figure with its
    /// word, the rest as label and value pairs in a shaded box, then an update's line in the update colour and, under a thin line,
    /// what a click does. Drawn like the Network page's hour hover (the user, 2026-11-20, approved mock-up).
    /// </summary>
    public static Rect GroupedBox(DrawingContext dc, Visual v, string title, string? sub, string big, string bigWord, Brush bigBrush,
        IReadOnlyList<HoverFact> facts, string? mark, string? foot, Point at, Rect bounds, bool above = false, HoverNote? note = null)
    {
        const double Width = 268, FactsPad = 10, ColGap = 10, RowGap = 8;
        // Every line wraps inside the box rather than running past its edge: "2:31 PM to 8:06 PM · 5h 17m · ended by a
        // power cut" was wider than the box (the user, 2026-11-21), and Spanish is longer still.
        double inner = Width - 2 * PadX;
        var t = Wrap(Format(v, title, 13, TextBrush, true), inner);
        var s = sub is null ? null : Wrap(Format(v, sub, 12, Muted), inner);
        var b = Format(v, big, 22, bigBrush, true);
        var bw = Format(v, bigWord, 12, Muted);
        double colW = (inner - 2 * FactsPad - ColGap) / 2;
        var cells = facts.Select(f => (L: Wrap(Format(v, f.Label.ToUpper(CultureInfo.CurrentCulture), 10.5, Faint, true), colW), V: Wrap(Format(v, f.Value, 12.5, f.Brush ?? TextBrush), colW))).ToList();
        var rows = cells.Chunk(2).Select(r => r.Max(c => c.L.Height + 1 + c.V.Height)).ToList();
        double factsH = rows.Count == 0 ? 0 : rows.Sum() + (rows.Count - 1) * RowGap + 2 * 8;
        var m = mark is null ? null : Wrap(Format(v, mark, 12, FpsMarkText), inner);
        var f = foot is null ? null : Wrap(Format(v, foot, 11.5, Faint), inner);
        double noteW = inner - 2 * FactsPad;
        var nl = note is { Label: { } nlabel } ? Wrap(Format(v, nlabel.ToUpper(CultureInfo.CurrentCulture), 10.5, Faint, true), noteW) : null;
        var nv = note is { Value: { } nvalue } ? Wrap(Format(v, nvalue, 12.5, TextBrush, true), noteW) : null;
        var ns = note is { } n0 ? Wrap(Format(v, n0.Sentence, nv is null ? 12.5 : 12, nv is null && !n0.Quiet ? TextBrush : Muted), noteW) : null;
        var nd = note is { Detail: { } ndetail } ? Wrap(Format(v, ndetail, 11, Faint), noteW) : null;
        double noteH = ns is null ? 0 : 8 + (nl is null ? 0 : nl.Height + 1) + (nv is null ? 0 : nv.Height + 3) + ns.Height + (nd is null ? 0 : 3 + nd.Height) + 8;

        double h = PadY + t.Height + (s is null ? 0 : 2 + s.Height) + 8 + b.Height + (factsH > 0 ? 8 + factsH : 0) + (noteH > 0 ? 8 + noteH : 0)
            + (m is null ? 0 : 9 + m.Height) + (f is null ? 0 : 9 + 1 + 7 + f.Height) + PadY;
        var (x, y) = Place(at, Width, h, bounds, above, double.NegativeInfinity);
        dc.DrawRoundedRectangle(TooltipBg, TooltipBorder, new Rect(x + 0.5, y + 0.5, Width, h), Corner, Corner);

        double left = x + PadX, ty = y + PadY;
        dc.DrawText(t, new Point(left, ty)); ty += t.Height;
        if (s is not null) { ty += 2; dc.DrawText(s, new Point(left, ty)); ty += s.Height; }
        ty += 8;
        dc.DrawText(b, new Point(left, ty));
        dc.DrawText(bw, new Point(left + b.Width + 5, ty + b.Baseline - bw.Baseline));
        ty += b.Height;
        if (factsH > 0)
        {
            ty += 8;
            dc.DrawRoundedRectangle(Track, null, new Rect(left, ty, Width - 2 * PadX, factsH), 7, 7);
            double ry = ty + 8;
            for (int r = 0; r < rows.Count; r++)
            {
                for (int c = 0; c < 2 && r * 2 + c < cells.Count; c++)
                {
                    var (l, val) = cells[r * 2 + c];
                    double cx = left + FactsPad + c * (colW + ColGap);
                    dc.DrawText(l, new Point(cx, ry));
                    dc.DrawText(val, new Point(cx, ry + l.Height + 1));
                }
                ry += rows[r] + RowGap;
            }
            ty += factsH;
        }
        if (ns is not null)
        {
            ty += 8;
            dc.DrawRoundedRectangle(Track, null, new Rect(left, ty, Width - 2 * PadX, noteH), 7, 7);
            double nx = left + FactsPad, ny = ty + 8;
            if (nl is not null) { dc.DrawText(nl, new Point(nx, ny)); ny += nl.Height + 1; }
            if (nv is not null) { dc.DrawText(nv, new Point(nx, ny)); ny += nv.Height + 3; }
            dc.DrawText(ns, new Point(nx, ny)); ny += ns.Height;
            if (nd is not null) dc.DrawText(nd, new Point(nx, ny + 3));
            ty += noteH;
        }
        if (m is not null)
        {
            // On the box's left edge like every other line, no dot in front (the user, 2026-11-22).
            ty += 9;
            dc.DrawText(m, new Point(left, ty));
            ty += m.Height;
        }
        if (f is not null)
        {
            ty += 9;
            dc.DrawLine(TooltipBorder, new Point(x + 1, Math.Round(ty) + 0.5), new Point(x + Width - 1, Math.Round(ty) + 0.5));
            ty += 1 + 7;
            dc.DrawText(f, new Point(left, ty));
        }
        return new Rect(x, y, Width, h);

        static FormattedText Wrap(FormattedText text, double width)
        {
            text.MaxTextWidth = Math.Max(1, width);
            return text;
        }
    }
}
