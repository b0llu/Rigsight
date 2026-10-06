using System.Windows;
using System.Windows.Media;
using Rigsight.Models;

namespace Rigsight.Controls;

/// <summary>Shared helpers for turning a <see cref="HistoryBuffer"/> into line and area geometry.</summary>
internal static class ChartGeometry
{
    /// <summary>
    /// Builds the line (and the filled area under it) for samples in [from, to] (which map to the plot's
    /// left and right edges), averaging samples that land in the same horizontal pixel so long windows stay
    /// cheap to draw. The samples are grouped by clock time, not by where they are on screen: a chart that
    /// moves along every second draws the same shape each time (grouped by pixel, readings that jump about
    /// were averaged with different neighbours every second, and their peaks seemed to move). With
    /// <paramref name="groupMs"/>, each group is at least that long (60 000: one point for each minute on the
    /// clock). Samples after <paramref name="until"/> are left out (another source covers them).
    /// </summary>
    public static (StreamGeometry Line, StreamGeometry Fill)? Build(
        HistoryBuffer buffer, long from, long to, Rect plot, double min, double max, Func<double, double> transform, long until = long.MaxValue,
        (long Time, double Value)? joinTo = null, double groupMs = 0)
    {
        if (buffer.Count == 0 || to <= from || max <= min) return null;

        var points = new List<List<Point>>();
        List<Point>? run = null;

        double span = to - from;
        groupMs = GroupMs(from, to, plot, groupMs);
        // From the start of the group the left edge is in (and the sample before it, for a line in from the edge):
        // a group keeps its average while it slides out of view.
        int start = Math.Max(0, buffer.IndexAtOrAfter((long)(Math.Floor(from / groupMs) * groupMs)) - 1);
        long group = long.MinValue;
        double sum = 0, timeSum = 0;
        int n = 0;

        // One point for the group: its average, in the middle of its samples.
        void Flush()
        {
            if (n == 0) return;
            double y = plot.Bottom - (transform(sum / n) - min) / (max - min) * plot.Height;
            run ??= [];
            run.Add(new Point(plot.Left + (timeSum / n) / span * plot.Width, Math.Clamp(y, plot.Top, plot.Bottom)));
            (sum, timeSum, n) = (0, 0, 0);
        }

        for (int i = start; i < buffer.Count; i++)
        {
            long time = buffer.TimeAt(i);
            if (time > until) break;
            double value = buffer.ValueAt(i);
            if (time < from - span) continue;

            if (double.IsNaN(value))
            {
                Flush();
                if (run is { Count: > 0 }) points.Add(run);
                run = null;
                continue;
            }

            long g = (long)Math.Floor(time / groupMs);
            if (g != group)
            {
                Flush();
                group = g;
            }
            sum += value;
            // From the window's start: the sum of a few thousand clock times would lose its last digits.
            timeSum += time - from;
            n++;
        }
        Flush();
        // Continue the line to where the next series starts (the minute history hands over to live readings),
        // so there's no seam between them; not across a real gap such as the PC being off.
        long lastTime = long.MinValue;
        for (int i = Math.Min(buffer.Count, buffer.IndexAtOrAfter(until == long.MaxValue ? long.MaxValue : until + 1)) - 1; i >= 0; i--)
            if (!double.IsNaN(buffer.ValueAt(i))) { lastTime = buffer.TimeAt(i); break; }
        bool joined = false;
        if (joinTo is { } j && run is { Count: > 0 } && !double.IsNaN(j.Value) && j.Time - lastTime <= 150_000)
        {
            joined = true;
            double y = plot.Bottom - (transform(j.Value) - min) / (max - min) * plot.Height;
            run.Add(new Point(plot.Left + (j.Time - from) / span * plot.Width, Math.Clamp(y, plot.Top, plot.Bottom)));
        }
        if (run is { Count: > 0 }) points.Add(run);
        if (points.Count == 0) return null;

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            foreach (var r in points)
            {
                ctx.BeginFigure(r[0], false, false);
                if (r.Count > 1) ctx.PolyLineTo(r.Skip(1).ToList(), true, true);
            }
        }
        line.Freeze();

        var fill = new StreamGeometry();
        using (var ctx = fill.Open())
        {
            foreach (var r in points.Where(r => r.Count > 1))
            {
                ctx.BeginFigure(new Point(r[0].X, plot.Bottom), true, true);
                ctx.PolyLineTo(r, false, true);
                // Where it hands over to the next series, overlap it by a pixel: two anti-aliased edges
                // meeting exactly leave a faint seam.
                if (joined && r == points[^1])
                {
                    ctx.LineTo(new Point(r[^1].X + 1, r[^1].Y), false, true);
                    ctx.LineTo(new Point(r[^1].X + 1, plot.Bottom), false, true);
                }
                else ctx.LineTo(new Point(r[^1].X, plot.Bottom), false, true);
            }
        }
        fill.Freeze();

        return (line, fill);
    }

    /// <summary>
    /// How long each group of samples is: the time one pixel covers, or <paramref name="atLeast"/> if that is
    /// longer. A pixel's time is taken in whole seconds from a second up, so a day still in progress (its span
    /// grows every second) keeps the same groups too.
    /// </summary>
    private static double GroupMs(long from, long to, Rect plot, double atLeast)
    {
        double pixelMs = (to - from) / plot.Width;
        if (pixelMs >= 1000) pixelMs = Math.Ceiling(pixelMs / 1000) * 1000;
        return Math.Max(atLeast, pixelMs);
    }

    /// <summary>
    /// The first point <see cref="Build"/> draws for a buffer shown from its first sample: where the line
    /// before it (older history) is joined to. Null when the buffer is empty or starts with a gap.
    /// </summary>
    public static (long Time, double Value)? FirstPoint(HistoryBuffer buffer, long from, long to, Rect plot, double groupMs = 0)
    {
        if (buffer.Count == 0 || to <= from) return null;
        groupMs = GroupMs(from, to, plot, groupMs);
        long first = buffer.TimeAt(0), group = (long)Math.Floor(first / groupMs);
        double sum = 0, timeSum = 0;
        int n = 0;
        for (int i = 0; i < buffer.Count && (long)Math.Floor(buffer.TimeAt(i) / groupMs) == group; i++)
        {
            double value = buffer.ValueAt(i);
            if (double.IsNaN(value)) break;
            sum += value;
            timeSum += buffer.TimeAt(i) - first;
            n++;
        }
        return n == 0 ? null : (first + (long)(timeSum / n), sum / n);
    }

    /// <summary>Range of finite values in [from, to], or null when there are none.</summary>
    public static (double Min, double Max)? Range(HistoryBuffer buffer, long from, Func<double, double> transform, long to = long.MaxValue)
    {
        double min = double.MaxValue, max = double.MinValue;
        for (int i = buffer.IndexAtOrAfter(from); i < buffer.Count && buffer.TimeAt(i) <= to; i++)
        {
            double v = buffer.ValueAt(i);
            if (double.IsNaN(v)) continue;
            v = transform(v);
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return min <= max ? (min, max) : null;
    }

    public static Brush FadeFill(Color color, double topOpacity)
    {
        var brush = new LinearGradientBrush(
            Color.FromArgb((byte)(255 * topOpacity), color.R, color.G, color.B),
            Color.FromArgb(0, color.R, color.G, color.B),
            90);
        brush.Freeze();
        return brush;
    }
}
