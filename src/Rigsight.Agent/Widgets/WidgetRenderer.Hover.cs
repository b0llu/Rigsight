using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Rigsight.Core;

#if RIGSIGHT_APP
// Linked into the app, which draws widgets with the same code for its Widgets page (see Rigsight.csproj).
namespace Rigsight.TaskbarDrawing;
#else
namespace Rigsight.Agent.Widgets;
#endif

// Hover on the tiles that draw bars or a graph (approved mock-up XcuVN6VYHFqAbEb33kYRYx, 2026-11-12): the pointer snaps to
// the nearest bar or moment, the bar brightens or the graph gets a guide line and a dot on each line, and a small box says
// what's there. Each such tile tells, as it's drawn, where its bars or graph are (HoverAreas), so the window can tell from
// the pointer alone which bar it's on and draws the widget again only when that changes. Only the agent's windows set a
// hover; the app's pictures never do.
internal static partial class WidgetRenderer
{
    /// <summary>
    /// Where a tile's bars or graph are, in the widget's own pixels at size M: <paramref name="Count"/> bars across it, or
    /// points from its left edge to its right (<paramref name="Points"/>), or only the stretches in <paramref name="Spans"/>
    /// (pairs of shares of its width, a drop on a strip). <paramref name="Part"/> tells apart two graphs on one tile.
    /// </summary>
    internal readonly record struct HoverArea(int Tile, int Part, RectangleF Rect, int Count, bool Points, float[]? Spans = null);

    /// <summary>What the pointer is on: a tile's bar or moment, and where the pointer is (widget pixels at size M).</summary>
    internal readonly record struct HoverOn(int Tile, int Part, int Index, PointF At);

    /// <summary>A line of a hover box, with the dot of its series' colour (none for a plain line).</summary>
    internal readonly record struct TipLine(string Text, Color? Dot);

    /// <summary>
    /// The hover box to draw over the widget: its title and lines, the middle of the bar or moment it's about and the
    /// pointer's height (widget pixels at size M), and its colours, from the widget's palette.
    /// </summary>
    internal sealed record HoverTip(string Title, IReadOnlyList<TipLine> Lines, float AnchorX, float PointerY,
        Color Box, Color Border, Color Text, Color Muted, Color Shadow);

    /// <summary>The bars and graphs of the widget last drawn on this thread (see <see cref="Render"/>).</summary>
    [ThreadStatic] internal static List<HoverArea>? HoverAreas;

    /// <summary>What the pointer is on, set by the window before it draws itself; null: no hover (the app, always).</summary>
    [ThreadStatic] internal static HoverOn? Hover;

    /// <summary>The hover box the last drawing on this thread asks for (null: none).</summary>
    [ThreadStatic] internal static HoverTip? Tip;

    /// <summary>The scale the widget is being drawn at (screen pixels to a pixel at size M).</summary>
    [ThreadStatic] private static float _drawScale;

    /// <summary>The bar or moment under a point of an area; null where it's on none (between a strip's drops).</summary>
    internal static int? IndexAt(HoverArea a, PointF at)
    {
        if (a.Count <= 0 || a.Rect.Width <= 0) return null;
        float f = Math.Clamp((at.X - a.Rect.Left) / a.Rect.Width, 0, 1);
        if (a.Spans is { } spans)
        {
            for (int k = 0; k + 1 < spans.Length; k += 2)
                if (f >= spans[k] && f <= spans[k + 1]) return k / 2;
            return null;
        }
        return a.Points ? (int)Math.Round(f * (a.Count - 1)) : Math.Min(a.Count - 1, (int)(f * a.Count));
    }

    /// <summary>What a point of the widget is on, from the areas it was last drawn with.</summary>
    internal static HoverOn? HoverAt(IReadOnlyList<HoverArea> areas, PointF at)
    {
        foreach (var a in areas)
        {
            // A few pixels' grace at the ends: the first and last bars are as easy to point at as the others.
            if (!RectangleF.Inflate(a.Rect, 3, 0).Contains(at)) continue;
            if (IndexAt(a, at) is int i) return new HoverOn(a.Tile, a.Part, i, at);
        }
        return null;
    }

    /// <summary>A rectangle of what's being drawn, in the widget's own pixels (through a tile drawn smaller, see Within).</summary>
    private static RectangleF ToWidget(Graphics g, RectangleF r)
    {
        var pts = new[] { r.Location, new PointF(r.Right, r.Bottom) };
        g.Transform.TransformPoints(pts);
        float s = _drawScale > 0 ? _drawScale : 1;
        return RectangleF.FromLTRB(pts[0].X / s, pts[0].Y / s, pts[1].X / s, pts[1].Y / s);
    }

    /// <summary>
    /// Says where this tile's bars or graph are, and which of them the pointer is on (null: none, or not this tile's).
    /// </summary>
    private static int? HoverIn(Graphics g, RectangleF area, int count, bool points, int part = 0, float[]? spans = null)
    {
        if (count <= 0 || area.Width <= 0) return null;
        if (HoverAreas is { } list && !list.Any(a => a.Tile == _tileIndex && a.Part == part))
            list.Add(new HoverArea(_tileIndex, part, ToWidget(g, area), count, points, spans));
        return Hover is { } h && h.Tile == _tileIndex && h.Part == part ? Math.Min(h.Index, count - 1) : null;
    }

    /// <summary>Asks for the hover box, about the bar or moment at <paramref name="x"/> (the tile's own pixels).</summary>
    private static void SetTip(Graphics g, Palette p, float x, string title, params TipLine[] lines)
    {
        if (Hover is not { } h) return;
        var pts = new[] { new PointF(x, 0) };
        g.Transform.TransformPoints(pts);
        float s = _drawScale > 0 ? _drawScale : 1;
        var (box, border, text, muted, shadow) = TipColors(p);
        Tip = new HoverTip(title, lines, pts[0].X / s, h.At.Y, box, border, text, muted, shadow);
    }

    /// <summary>
    /// The hover box's colours, from the widget's palette: on a dark one a box a little lighter than the widget with a
    /// lighter edge (the mock-up's #1d1f23 and #3a3d44 on the dark theme), on a light one a white-ish box with a soft edge.
    /// </summary>
    private static (Color Box, Color Border, Color Text, Color Muted, Color Shadow) TipColors(Palette p) => p.Light
        ? (Mix(p.Bg, Color.White, 0.6f), Mix(p.Bg, p.Text, 0.16f), p.Text, p.Muted, Color.FromArgb(30, 0, 0, 0))
        : (Mix(p.Bg, p.Text, 0.08f), Mix(p.Bg, p.Text, 0.2f), Mix(p.Bg, p.Text, 0.93f), p.Muted, Color.FromArgb(102, 0, 0, 0));

    private static Color Mix(Color a, Color b, float t) =>
        Color.FromArgb(255, (int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));

    // ── What a hovered bar or moment looks like ──────────────────────────

    /// <summary>The hovered bar's column, brightened (the mock-up's 10% white over it; a touch of black on a light widget).</summary>
    private static void HoverColumn(Graphics g, RectangleF column, Palette p) =>
        FillRound(g, RectangleF.Inflate(column, -1, 0), 4, Color.FromArgb(p.Light ? 16 : 26, p.Text));

    /// <summary>The thin line down a graph at the moment hovered.</summary>
    private static void HoverGuide(Graphics g, float x, float top, float bottom, Palette p)
    {
        using var pen = new Pen(Color.FromArgb(p.Light ? 90 : 115, p.Text), 1);
        g.DrawLine(pen, x, top, x, bottom);
    }

    /// <summary>A dot on a graph's line at the moment hovered: the line's colour, ringed in the widget's.</summary>
    private static void HoverDot(Graphics g, float x, float y, Color color, Palette p)
    {
        if (!float.IsFinite(y)) return;
        var smooth = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var ring = new SolidBrush(Color.FromArgb(255, p.Bg))) g.FillEllipse(ring, x - 4.5f, y - 4.5f, 9, 9);
        using (var dot = new SolidBrush(color)) g.FillEllipse(dot, x - 2.5f, y - 2.5f, 5, 5);
        g.SmoothingMode = smooth;
    }

    // ── Words ─────────────────────────────────────────────────────────────

    /// <summary>How long ago a moment of a graph was: "Now", "12 seconds ago", "2m 05s ago".</summary>
    internal static string AgoText(double seconds)
    {
        long s = (long)Math.Round(Math.Max(0, seconds));
        return s < 1 ? Lang.T("Now") : s == 1 ? Lang.T("1 second ago") : s < 60 ? Lang.T($"{s} seconds ago") : Lang.T($"{Units.DurationExact(s)} ago");
    }

    /// <summary>How long ago point <paramref name="i"/> of <paramref name="n"/> was, with the newest last and the lot <paramref name="span"/> seconds long.</summary>
    private static double AgoOf(int i, int n, double span) => n <= 1 ? 0 : span * (n - 1 - i) / (n - 1);

    /// <summary>The finite point nearest <paramref name="i"/> (a gap in a history has no reading to show).</summary>
    private static int Nearest(float[] values, int i)
    {
        i = Math.Clamp(i, 0, values.Length - 1);
        for (int k = 0; k < values.Length; k++)
        {
            if (i - k >= 0 && float.IsFinite(values[i - k])) return i - k;
            if (i + k < values.Length && float.IsFinite(values[i + k])) return i + k;
        }
        return -1;
    }

    // ── The box ───────────────────────────────────────────────────────────

    private const float TipPadX = 10, TipPadTop = 6, TipPadBottom = 7, TipTitlePx = 11.5f, TipPx = 12.5f, TipDot = 7, TipDotGap = 6, TipRadius = 8;
    private static float TipTitleLine => TipTitlePx * 1.35f;
    private static float TipLineH => TipPx * 1.35f;

    /// <summary>The box's size in the widget's pixels at size M.</summary>
    internal static SizeF TipSize(Graphics g, HoverTip tip)
    {
        float w = W(g, tip.Title, TipTitlePx);
        foreach (var l in tip.Lines) w = Math.Max(w, (l.Dot is null ? 0 : TipDot + TipDotGap) + W(g, l.Text, TipPx));
        return new SizeF(MathF.Ceiling(w + TipPadX * 2), MathF.Ceiling(TipPadTop + TipTitleLine + tip.Lines.Count * TipLineH + TipPadBottom));
    }

    /// <summary>
    /// Where the box goes on a widget <paramref name="widget"/> big (pixels at size M): over the bar or moment it's about,
    /// above the pointer, or under it near the widget's top; always 6 pixels inside the widget.
    /// </summary>
    internal static PointF TipPlace(SizeF tip, SizeF widget, float anchorX, float pointerY)
    {
        float left = Math.Min(Math.Max(6, anchorX - tip.Width / 2), widget.Width - tip.Width - 6);
        float top = pointerY - tip.Height - 12;
        if (top < 6) top = Math.Min(widget.Height - tip.Height - 6, pointerY + 16);
        return new PointF(Math.Max(6, left), Math.Max(6, top));
    }

    /// <summary>
    /// Draws the hover box over a widget drawn at <paramref name="scale"/> whose top left is the graphics' origin, at
    /// <paramref name="opacity"/> (its quick fade in). Drawn over the finished widget, so it's solid whatever the widget's
    /// opacity, and in Segoe UI whatever its font.
    /// </summary>
    internal static void DrawTip(Graphics g, HoverTip tip, float scale, SizeF widget, float opacity = 1)
    {
        var state = g.Save();
        string? font = FontName;
        var shadowText = TextShadow;
        FontName = null;
        TextShadow = Color.Empty;
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.ScaleTransform(scale, scale);
            var size = TipSize(g, tip);
            var at = TipPlace(size, widget, tip.AnchorX, tip.PointerY);
            // On whole screen pixels, so its edge and letters are crisp.
            at = new PointF(MathF.Round(at.X * scale) / scale, MathF.Round(at.Y * scale) / scale);
            var box = new RectangleF(at, size);
            Color A(Color c) => Color.FromArgb((int)Math.Round(c.A * Math.Clamp(opacity, 0, 1)), c);
            // A soft shadow under it (the mock-up's 0 4px 14px), as a few widening rings.
            for (int k = 4; k >= 1; k--)
            {
                using var path = RoundRect(RectangleF.Inflate(new RectangleF(box.X, box.Y + 3, box.Width, box.Height), k * 1.6f, k * 1.6f), TipRadius + k * 1.6f);
                using var b = new SolidBrush(A(Color.FromArgb(tip.Shadow.A / (k + 2), tip.Shadow)));
                g.FillPath(b, path);
            }
            using (var path = RoundRect(box, TipRadius))
            {
                using (var b = new SolidBrush(A(tip.Box))) g.FillPath(b, path);
                using var pen = new Pen(A(tip.Border), 1);
                using var edge = RoundRect(RectangleF.Inflate(box, -0.5f, -0.5f), TipRadius - 0.5f);
                g.DrawPath(pen, edge);
            }
            float x = box.X + TipPadX, y = box.Y + TipPadTop;
            TextLine(g, tip.Title, TipTitlePx, A(tip.Muted), x, y, TipTitleLine);
            y += TipTitleLine;
            foreach (var line in tip.Lines)
            {
                float tx = x;
                if (line.Dot is { } dot)
                {
                    using var b = new SolidBrush(A(dot));
                    g.FillEllipse(b, x, y + (TipLineH - TipDot) / 2, TipDot, TipDot);
                    tx += TipDot + TipDotGap;
                }
                TextLine(g, line.Text, TipPx, A(tip.Text), tx, y, TipLineH);
                y += TipLineH;
            }
        }
        finally
        {
            FontName = font;
            TextShadow = shadowText;
            g.Restore(state);
        }
    }
    // ── A reading drawn as a graph ────────────────────────────────────────

    /// <summary>
    /// The hover on one reading drawn as a line or a filled graph in <paramref name="area"/> (as <see cref="Points"/> lays
    /// it out): a guide and a dot at the moment nearest the pointer, and the box with how long ago and the reading then.
    /// </summary>
    private static void HoverOnGraph(Graphics g, float[] values, RectangleF area, Reading r, Color color, WidgetData d, Palette p, int part = 0)
    {
        var finite = values.Where(float.IsFinite).ToList();
        if (finite.Count < 2 || HoverIn(g, area, values.Length, points: true, part) is not int at) return;
        int k = Nearest(values, at);
        if (k < 0) return;
        float lo = finite.Min(), hi = finite.Max();
        if (hi - lo < 1) { lo -= 0.5f; hi += 0.5f; }
        float x = area.Left + area.Width * k / (values.Length - 1), y = area.Bottom - (values[k] - lo) / (hi - lo) * area.Height;
        HoverGuide(g, x, area.Top, area.Bottom, p);
        HoverDot(g, x, y, color, p);
        SetTip(g, p, x, AgoText(AgoOf(k, values.Length, d.HistorySeconds)), new TipLine($"{r.Label} {Units.Short(r.Kind, values[k])}", color));
    }

    /// <summary>The hover on a reading drawn as bars (as <see cref="Bars"/> lays them out): the bar brightened, and the box.</summary>
    private static void HoverOnBars(Graphics g, float[] values, RectangleF area, Reading r, Color color, WidgetData d, Palette p)
    {
        var finite = values.Where(float.IsFinite).ToArray();
        if (finite.Length < 2) return;
        int most = Math.Max(8, (int)(area.Width / 5));
        int shown = Math.Min(most, finite.Length);
        if (HoverIn(g, area, shown, points: false) is not int at) return;
        float bw = area.Width / shown;
        HoverColumn(g, new RectangleF(area.X + at * bw, area.Y, bw, area.Height), p);
        int k = finite.Length - shown + at;
        SetTip(g, p, area.X + (at + 0.5f) * bw, AgoText(AgoOf(k, finite.Length, d.HistorySeconds)), new TipLine($"{r.Label} {Units.Short(r.Kind, finite[k])}", color));
    }
}
