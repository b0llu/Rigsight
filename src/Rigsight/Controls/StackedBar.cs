using System.Windows;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// One stacked bar as every graph draws it: one solid bar, its pieces joined square with no gap, the bottom square and
/// only the top corners rounded. The user, 2026-11-05: "normal bars rather than rounded with gaps"; rounding each piece
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
        if (backing is not null) dc.DrawGeometry(backing, null, TopRounded(x, bottom - total, width, total, r));
        double y = bottom;
        for (int i = 0; i < shown.Count; i++)
        {
            var (brush, height) = shown[i];
            double top = y - height;
            if (i == shown.Count - 1) dc.DrawGeometry(brush, null, TopRounded(x, top, width, height, Math.Min(r, height)));
            // A hair under the next piece, so no thin line of the card shows between two pieces.
            else dc.DrawRectangle(brush, null, new Rect(x, top - 0.5, width, height + 0.5));
            y = top;
        }
    }

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
