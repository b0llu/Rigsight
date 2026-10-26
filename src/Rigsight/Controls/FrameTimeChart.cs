using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Core.Games;

namespace Rigsight.Controls;

/// <summary>
/// The Games page's "Every frame": how long each frame of one play took, from its first to its last (2026-10-25: "track
/// every frame's FPS"). Hundreds of thousands of frames, so each column of pixels draws its frames' spread (the
/// fastest to the slowest of most of them, 95 in 100, faint) and their middle (the line); each stutter is an amber dot at its height, an arrow at
/// the top when taller than the chart. Hover a column for its time, its frames and any stutter in it. Lower is smoother.
/// </summary>
public sealed class FrameTimeChart : FrameworkElement
{
    private const double Left = 44, Right = 14, Top = 30, Bottom = 24;

    public static readonly DependencyProperty FramesProperty = DependencyProperty.Register(
        nameof(Frames), typeof(FrameRun), typeof(FrameTimeChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((FrameTimeChart)d)._columnsFor = -1));

    public static readonly DependencyProperty StuttersProperty = DependencyProperty.Register(
        nameof(Stutters), typeof(IReadOnlyList<Stutter>), typeof(FrameTimeChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public FrameRun? Frames { get => (FrameRun?)GetValue(FramesProperty); set => SetValue(FramesProperty, value); }

    public static readonly DependencyProperty SlowestOneProperty = DependencyProperty.Register(
        nameof(SlowestOne), typeof(double?), typeof(FrameTimeChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The frame time the slowest 1% of frames are above (ms): a dashed line, as CapFrameX marks it.</summary>
    public double? SlowestOne { get => (double?)GetValue(SlowestOneProperty); set => SetValue(SlowestOneProperty, value); }
    public IReadOnlyList<Stutter>? Stutters { get => (IReadOnlyList<Stutter>?)GetValue(StuttersProperty); set => SetValue(StuttersProperty, value); }

    /// <summary>One column of pixels: its frames' fastest, middle, the slowest of most (95 in 100) and the slowest, and how many.</summary>
    private readonly record struct Column(float Min, float Mid, float Most, float Max, int Count, long FromMs, long ToMs);

    private Column[] _columns = [];
    private double _columnsFor = -1;
    private int _hover = -1;

    public FrameTimeChart() => ClipToBounds = true;

    protected override Size MeasureOverride(Size available) => new(double.IsInfinity(available.Width) ? 600 : available.Width, 210);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        int next = p.X >= Left && p.X <= ActualWidth - Right && p.Y >= Top && p.Y <= ActualHeight - Bottom ? (int)(p.X - Left) : -1;
        if (next >= _columns.Length || (next >= 0 && _columns[next].Count == 0)) next = -1;
        if (next != _hover) { _hover = next; InvalidateVisual(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = -1;
        InvalidateVisual();
    }

    /// <summary>The frames split into one column per pixel of the plot, by time.</summary>
    private void Split(FrameRun run, int width)
    {
        _columns = new Column[width];
        long from = run.EndMs[0], to = run.EndMs[^1];
        double span = Math.Max(1, to - from);
        var scratch = new List<float>();
        int i = 0;
        for (int c = 0; c < width; c++)
        {
            long colTo = from + (long)Math.Ceiling(span * (c + 1) / width), colFrom = from + (long)(span * c / width);
            scratch.Clear();
            while (i < run.Count && (run.EndMs[i] <= colTo || c == width - 1)) scratch.Add(run.Ms[i++]);
            if (scratch.Count == 0) { _columns[c] = new Column(0, 0, 0, 0, 0, colFrom, colTo); continue; }
            scratch.Sort();
            _columns[c] = new Column(scratch[0], scratch[scratch.Count / 2], scratch[Math.Min(scratch.Count - 1, (int)(scratch.Count * 0.95))], scratch[^1], scratch.Count, colFrom, colTo);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var run = Frames;
        if (run is null || run.Count < 2 || w < Left + Right + 100) return;
        int width = (int)(w - Left - Right);
        if (_columnsFor != width) { Split(run, width); _columnsFor = width; }

        // The scale: most frames and most stutters (the tallest tenth of stutters are arrows at the top, so one long
        // hitch doesn't flatten the rest), in round steps.
        var most = _columns.Where(c => c.Count > 0).Select(c => c.Most).OrderBy(x => x).ToList();
        double typical = _columns.Where(c => c.Count > 0).Select(c => (double)c.Mid).DefaultIfEmpty(10).Average();
        var tall = (Stutters ?? []).Select(x => x.Ms).OrderBy(x => x).ToList();
        double need = Math.Max(Math.Max(typical * 3, most.Count == 0 ? 20 : most[(int)(most.Count * 0.98)] * 1.5), tall.Count == 0 ? 0 : tall[(int)(tall.Count * 0.9)] * 1.1);
        double hi = new double[] { 20, 40, 60, 80, 100, 160, 200, 300, 400 }.FirstOrDefault(v => v >= need, 400);
        double Y(double ms) => Top + (hi - Math.Min(ms, hi)) * (h - Top - Bottom) / hi;

        var grid = new Pen(ChartPaint.Grid, 1);
        for (int k = 0; k <= 4; k++)
        {
            double v = hi * k / 4, y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(grid, new Point(Left, y), new Point(w - Right, y));
            ChartPaint.Text(dc, this, $"{v:0}", new Point(Left - 8, y), 11, ChartPaint.Label, ChartPaint.Align.Right);
        }
        ChartPaint.Text(dc, this, "ms", new Point(Left - 8, Top - 16), 11, ChartPaint.Label, ChartPaint.Align.Right);

        var accent = ChartPaint.Fps;
        // Each column's spread, faint, then the middle as one line.
        var band = new StreamGeometry();
        using (var g = band.Open())
            for (int c = 0; c < _columns.Length; c++)
            {
                var col = _columns[c];
                if (col.Count == 0) continue;
                double x = Left + c, top = Y(col.Most), bottom = Math.Max(Y(col.Min), top + 1);
                g.BeginFigure(new Point(x, top), true, true);
                g.LineTo(new Point(x + 1, top), false, false);
                g.LineTo(new Point(x + 1, bottom), false, false);
                g.LineTo(new Point(x, bottom), false, false);
            }
        band.Freeze();
        dc.DrawGeometry(ChartPaint.Brush(accent.Color, 0.22), null, band);

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            bool open = false;
            for (int c = 0; c < _columns.Length; c++)
            {
                var col = _columns[c];
                if (col.Count == 0) { open = false; continue; }
                var p = new Point(Left + c + 0.5, Y(col.Mid));
                if (!open) { g.BeginFigure(p, false, false); open = true; }
                else g.LineTo(p, true, true);
            }
        }
        line.Freeze();
        dc.DrawGeometry(null, new Pen(accent, 1.6), line);

        // Where the slowest 1% of frames begin.
        if (SlowestOne is double p99 && p99 < hi)
        {
            double y = Math.Round(Y(p99)) + 0.5;
            dc.DrawLine(new Pen(ChartPaint.Muted, 1.5) { DashStyle = new DashStyle([3, 2.5], 0) }, new Point(Left, y), new Point(w - Right, y));
            var label = ChartPaint.Format(this, Lang.T("slowest 1% of frames above this"), 11, ChartPaint.Muted);
            var at = new Rect(w - Right - label.Width - 8, y - label.Height - 3, label.Width + 8, label.Height);
            dc.DrawRectangle(ChartPaint.Brush(ChartPaint.Res("SurfaceColor", 0x11)), null, at);
            dc.DrawText(label, new Point(at.X + 4, at.Y));
        }

        // Stutters: amber dots at their height; an arrow at the top for the ones off the chart.
        long from = run.EndMs[0], to = run.EndMs[^1];
        double span = Math.Max(1, to - from);
        var warm = ChartPaint.Warm;
        var hole = ChartPaint.Brush(ChartPaint.Res("SurfaceColor", 0x11));
        foreach (var s in Stutters ?? [])
        {
            if (s.AtMs < from || s.AtMs > to) continue;
            double x = Left + (s.AtMs - from) / span * width;
            if (s.Ms >= hi)
            {
                var tri = new StreamGeometry();
                using (var g = tri.Open()) { g.BeginFigure(new Point(x, Top), true, true); g.LineTo(new Point(x - 4, Top + 6), true, false); g.LineTo(new Point(x + 4, Top + 6), true, false); }
                tri.Freeze();
                dc.DrawGeometry(warm, null, tri);
            }
            else dc.DrawEllipse(warm, new Pen(hole, 1), new Point(x, Y(s.Ms)), 2.6, 2.6);
        }

        // Times along the bottom, about one every 120 px.
        int labels = Math.Clamp(width / 120 + 1, 2, 12);
        for (int k = 0; k < labels; k++)
        {
            long at = from + (long)(span * k / (labels - 1));
            var align = k == 0 ? ChartPaint.Align.Left : k == labels - 1 ? ChartPaint.Align.Right : ChartPaint.Align.Center;
            ChartPaint.Text(dc, this, TimeText.At(DateTimeOffset.FromUnixTimeMilliseconds(at).LocalDateTime, "h:mm tt"),
                new Point(Left + width * k / (double)(labels - 1), h - 6), 11, ChartPaint.Label, align);
        }

        if (_hover >= 0 && _hover < _columns.Length)
        {
            var col = _columns[_hover];
            double x = Left + _hover + 0.5;
            dc.DrawLine(new Pen(ChartPaint.Cursor, 1), new Point(x, Top), new Point(x, h - Bottom));
            dc.DrawEllipse(accent, new Pen(hole, 1.5), new Point(x, Y(col.Mid)), 3.2, 3.2);
            var start = DateTimeOffset.FromUnixTimeMilliseconds(col.FromMs).LocalDateTime;
            var lines = new List<(string, Brush, bool)>
            {
                (TimeText.At(start, "h:mm:ss tt"), ChartPaint.TextBrush, true),
                (Lang.T($"{col.Mid:0.0} ms a frame ({1000 / Math.Max(col.Mid, 0.1):0} fps)"), accent, false),
                (Lang.T($"Slowest {col.Max:0.0} ms of {col.Count} frames"), ChartPaint.Muted, false),
            };
            var here = (Stutters ?? []).Where(s => s.AtMs >= col.FromMs && s.AtMs <= col.ToMs).ToList();
            if (here.Count > 0)
            {
                var worst = here.MaxBy(s => s.Ms)!;
                lines.Add((here.Count == 1 ? Lang.T($"One stutter, a frame that took {worst.Ms:0} ms against {worst.UsualMs:0.#} usual") : Lang.T($"{here.Count} stutters, the worst {worst.Ms:0} ms"), warm, false));
            }
            ChartPaint.InfoBox(dc, this, lines, new Point(x, Y(col.Max) - 8), new Rect(0, 0, w, h), above: true);
        }
    }
}
