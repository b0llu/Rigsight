using System.Windows;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// One stacked bar as every graph draws it: one solid bar, its pieces joined square with no gap, the bottom square and
/// only the bar's own top corners rounded. The user, 2026-11-05: "normal bars rather than rounded with gaps"; rounding each piece
/// on its own left notches at every join (as charting tools found too, e.g. CodexBar's fix to the same thing).
/// </summary>
public static class StackedBar
{
    /// <summary>The top corners' radius, at most half the bar's width.</summary>
    public const double Radius = 3;

    /// <summary>
    /// Draws <paramref name="pieces"/> bottom first from <paramref name="bottom"/> up, each as tall as its own height in
    /// pixels. <paramref name="backing"/>, when given, is laid under the whole bar first, so a see-through piece (the
    /// quieter grey of "other apps") shows the card's colour through it and never the grid lines.
    /// </summary>
    public static void Draw(DrawingContext dc, double x, double width, double bottom, IReadOnlyList<(Brush Brush, double Height)> pieces, Brush? backing = null)
    {
        var shown = pieces.Where(p => p.Height > 0).ToList();
        if (shown.Count == 0 || width <= 0) return;
        double total = shown.Sum(p => p.Height);
        double r = Math.Min(Radius, Math.Min(width / 2, total));
        var shape = TopRounded(x, bottom - total, width, total, r);
        if (backing is not null) dc.DrawGeometry(backing, null, shape);
        var (topBrush, topHeight) = shown[^1];
        if (topHeight >= r && Opaque(topBrush))
        {
            // The usual bar: its top piece is tall enough to carry the whole rounding and hides what's under it, so only
            // that piece is rounded and the rest are plain rectangles (no clip: a 10-year strip draws 3,650 of these, and
            // a clip on each cost the gate's 35 ms budget, 2026-11-15).
            double yy = bottom;
            for (int i = 0; i < shown.Count; i++)
            {
                var (brush, height) = shown[i];
                double top = yy - height;
                if (i == shown.Count - 1) dc.DrawGeometry(brush, null, TopRounded(x, top, width, height, r));
                // A hair under the next piece, so no thin line of the card shows between two pieces.
                else dc.DrawRectangle(brush, null, new Rect(x, top - 0.5, width, height + 0.5));
                yy = top;
            }
            return;
        }
        // A sliver or a see-through piece on top: the whole bar is cut to its rounded shape and the pieces drawn square
        // inside it, so the top is round whatever piece ends up there. Rounding only the top piece left square corners
        // here (its radius can't be taller than it; the square piece under a see-through one showed through): the user's
        // Network screenshot, 2026-11-14.
        dc.PushClip(shape);
        double y = bottom;
        for (int i = 0; i < shown.Count; i++)
        {
            var (brush, height) = shown[i];
            double top = y - height;
            // A hair under the next piece, so no thin line of the card shows between two pieces; not under a see-through
            // one, where the overlap would show as a darker line.
            double under = i < shown.Count - 1 && Opaque(shown[i + 1].Brush) ? 0.5 : 0;
            dc.DrawRectangle(brush, null, new Rect(x, top - under, width, height + under));
            y = top;
        }
        dc.Pop();
    }

    private static bool Opaque(Brush brush) => brush.Opacity >= 1 && (brush is not SolidColorBrush solid || solid.Color.A == 255);

    /// <summary>A rectangle with only its two top corners rounded.</summary>
    public static Geometry TopRounded(double x, double y, double width, double height, double r)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(x, y + height), true, true);
            ctx.LineTo(new Point(x, y + r), false, false);
            if (r > 0) ctx.ArcTo(new Point(x + r, y), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(x + width - r, y), false, false);
            if (r > 0) ctx.ArcTo(new Point(x + width, y + r), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(x + width, y + height), false, false);
        }
        g.Freeze();
        return g;
    }
}
