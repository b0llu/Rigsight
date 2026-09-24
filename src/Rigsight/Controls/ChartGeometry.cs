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
    /// cheap to draw. Samples after <paramref name="until"/> are left out (another source covers them).
    /// </summary>
    public static (StreamGeometry Line, StreamGeometry Fill)? Build(
        HistoryBuffer buffer, long from, long to, Rect plot, double min, double max, Func<double, double> transform, long until = long.MaxValue,
        (long Time, double Value)? joinTo = null)
    {
        if (buffer.Count == 0 || to <= from || max <= min) return null;

        var points = new List<List<Point>>();
        List<Point>? run = null;

        double span = to - from;
        int start = Math.Max(0, buffer.IndexAtOrAfter(from) - 1);
        int bucketX = int.MinValue;
        double bucketSum = 0;
        int bucketN = 0;
        double bucketXPos = 0;

        void Flush()
        {
            if (bucketN == 0) return;
            double v = transform(bucketSum / bucketN);
            double y = plot.Bottom - (v - min) / (max - min) * plot.Height;
            run ??= [];
            run.Add(new Point(bucketXPos, Math.Clamp(y, plot.Top, plot.Bottom)));
            bucketN = 0;
            bucketSum = 0;
        }

        for (int i = start; i < buffer.Count; i++)
        {
            if (buffer.TimeAt(i) > until) break;
            double value = buffer.ValueAt(i);
            double x = plot.Left + (buffer.TimeAt(i) - from) / span * plot.Width;
            if (x < plot.Left - plot.Width) continue;

            if (double.IsNaN(value))
            {
                Flush();
                if (run is { Count: > 0 }) points.Add(run);
                run = null;
                bucketX = int.MinValue;
                continue;
            }

            int px = (int)Math.Floor(x);
            if (px != bucketX)
            {
                Flush();
                bucketX = px;
            }
            bucketSum += value;
            bucketN++;
            bucketXPos = x;
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
