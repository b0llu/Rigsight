using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Rigsight.Core;
using Rigsight.Core.Games;

namespace Rigsight.Controls;

/// <summary>
/// A point of "How it runs" over a range: one play, or a week of plays over a long range. Its frame rate, 1% low and
/// stutters an hour (plays with frame data), and whether it opens (a play with frame data, or a week).
/// </summary>
public sealed record RunsPoint(DateTime At, DateTime End, double ActiveSec, double Fps, double? Low, double? StuttersAnHour, int Plays, bool IsWeek,
    bool CanOpen, bool PowerCut, double? Gpu, double? Watts);

/// <summary>A driver or the game updated: a flag on a dashed line at its time.</summary>
public sealed record RunsMark(DateTime Time, string Label, string Title);

/// <summary>A minute of one play, from its frames: the true average, 1% low and its stutters.</summary>
public sealed record RunsMinute(DateTime At, double? Fps, double? Low, int Stutters);

/// <summary>
/// The Games page's "How it runs" as one chart (approved mock-up "How it runs, as one chart", 2026-10-27): the plays over
/// the range picked, or one play a minute at a time, in frames a second on the same scale. Solid line the average, dashed
/// the 1% low, the band between them most frames, amber bars along the bottom the stutters (one a play, as tall as its
/// stutters an hour; one tick a stutter inside a play). Switching to a play zooms into its dot, and back out again.
/// Updates are flags with their name beside them only where it fits. The hover finds the nearest point anywhere up and
/// down the chart, so a thin bar never has to be hit.
/// </summary>
public sealed class RunsChart : FrameworkElement
{
    private const double Left = 44, Right = 14, Top = 34, Bottom = 24, Rail = 12;

    public static readonly DependencyProperty PointsProperty = Register<IReadOnlyList<RunsPoint>?>(nameof(Points), null);
    public static readonly DependencyProperty MarksProperty = Register<IReadOnlyList<RunsMark>?>(nameof(Marks), null);
    public static readonly DependencyProperty FromProperty = Register(nameof(From), DateTime.MinValue);
    public static readonly DependencyProperty ToProperty = Register(nameof(To), DateTime.MinValue);
    public static readonly DependencyProperty MinutesProperty = Register<IReadOnlyList<RunsMinute>?>(nameof(Minutes), null);
    public static readonly DependencyProperty StuttersProperty = Register<IReadOnlyList<Stutter>?>(nameof(Stutters), null);
    public static readonly DependencyProperty PlayStartProperty = Register<DateTime?>(nameof(PlayStart), null);
    public static readonly DependencyProperty ShowPlayProperty = DependencyProperty.Register(nameof(ShowPlay), typeof(bool), typeof(RunsChart),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((RunsChart)d).ZoomTo((bool)e.NewValue)));

    /// <summary>0 the plays, 1 the play; between, the zoom from one to the other.</summary>
    private static readonly DependencyProperty ZoomProperty = Register(nameof(Zoom), 0.0);

    private static DependencyProperty Register<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(RunsChart), new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<RunsPoint>? Points { get => (IReadOnlyList<RunsPoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public IReadOnlyList<RunsMark>? Marks { get => (IReadOnlyList<RunsMark>?)GetValue(MarksProperty); set => SetValue(MarksProperty, value); }
    /// <summary>The range the plays are drawn over.</summary>
    public DateTime From { get => (DateTime)GetValue(FromProperty); set => SetValue(FromProperty, value); }
    public DateTime To { get => (DateTime)GetValue(ToProperty); set => SetValue(ToProperty, value); }
    /// <summary>The play shown: its minutes and stutters, and when it started (its dot is where the zoom starts).</summary>
    public IReadOnlyList<RunsMinute>? Minutes { get => (IReadOnlyList<RunsMinute>?)GetValue(MinutesProperty); set => SetValue(MinutesProperty, value); }
    public IReadOnlyList<Stutter>? Stutters { get => (IReadOnlyList<Stutter>?)GetValue(StuttersProperty); set => SetValue(StuttersProperty, value); }
    public DateTime? PlayStart { get => (DateTime?)GetValue(PlayStartProperty); set => SetValue(PlayStartProperty, value); }
    public bool ShowPlay { get => (bool)GetValue(ShowPlayProperty); set => SetValue(ShowPlayProperty, value); }
    private double Zoom => (double)GetValue(ZoomProperty);

    /// <summary>A play with frame data, or a week, was clicked.</summary>
    public event Action<RunsPoint>? Opened;

    private int _hover = -1, _hoverMinute = -1, _hoverMark = -1;
    private readonly List<double> _xs = [];
    private readonly List<double> _minuteXs = [];
    private readonly List<(Rect Hit, RunsMark Mark)> _flags = [];

    public RunsChart()
    {
        ClipToBounds = true;
        // Drawn already shown (a page opened on a play): no zoom to watch.
        Loaded += (_, _) => SetValue(ZoomProperty, ShowPlay ? 1.0 : 0.0);
    }

    protected override Size MeasureOverride(Size available) => new(double.IsInfinity(available.Width) ? 600 : available.Width, 250);

    private void ZoomTo(bool play)
    {
        _hover = _hoverMinute = _hoverMark = -1;
        if (!IsLoaded) { SetValue(ZoomProperty, play ? 1.0 : 0.0); return; }
        BeginAnimation(ZoomProperty, new DoubleAnimation(play ? 1 : 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        double z = Zoom;
        int hover = -1, minute = -1, mark = -1;
        if (z <= 0.001)
        {
            mark = _flags.FindIndex(f => f.Hit.Contains(p));
            if (mark < 0) hover = Nearest(_xs, p.X);
        }
        else if (z >= 0.999) minute = Nearest(_minuteXs, p.X);
        Cursor = hover >= 0 && Points is { } pts && hover < pts.Count && pts[hover].CanOpen ? Cursors.Hand : null;
        if (hover != _hover || minute != _hoverMinute || mark != _hoverMark) { (_hover, _hoverMinute, _hoverMark) = (hover, minute, mark); InvalidateVisual(); }
    }

    private static int Nearest(List<double> xs, double x)
    {
        int best = -1;
        for (int i = 0; i < xs.Count; i++) if (best < 0 || Math.Abs(xs[i] - x) < Math.Abs(xs[best] - x)) best = i;
        return best;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = _hoverMinute = _hoverMark = -1;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (Zoom <= 0.001 && _hover >= 0 && Points is { } pts && _hover < pts.Count && pts[_hover].CanOpen) Opened?.Invoke(pts[_hover]);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        _xs.Clear();
        _minuteXs.Clear();
        _flags.Clear();
        if (w < Left + Right + 100) return;
        var pts = Points ?? [];
        var mins = (Minutes ?? []).Where(m => m.Fps is not null).ToList();

        // One scale for both views, so a play's line sits where its dot was.
        var values = pts.SelectMany(p => new[] { p.Fps, p.Low ?? p.Fps }).Concat(mins.SelectMany(m => new[] { m.Fps!.Value, m.Low ?? m.Fps!.Value })).ToList();
        if (values.Count == 0) values = [60, 120];
        double hi = Math.Ceiling(values.Max() * 1.08 / 10) * 10;
        double lo = Math.Max(0, Math.Floor(values.Min() * 0.85 / 10) * 10);
        if (hi - lo < 20) hi = lo + 20;
        double railBase = h - Bottom, plotBottom = railBase - Rail;
        double Y(double v) => Top + (hi - v) * (plotBottom - Top) / (hi - lo);

        var grid = new Pen(ChartPaint.Grid, 1);
        for (int k = 0; k <= 4; k++)
        {
            double v = Math.Round(lo + (hi - lo) * k / 4), y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(grid, new Point(Left, y), new Point(w - Right, y));
            ChartPaint.Text(dc, this, $"{v:0}", new Point(Left - 8, y), 11, ChartPaint.Label, ChartPaint.Align.Right);
        }
        ChartPaint.Text(dc, this, "fps", new Point(Left - 8, Top - 14), 11, ChartPaint.Label, ChartPaint.Align.Right);
        dc.DrawLine(grid, new Point(Left, Math.Round(railBase) + 0.5), new Point(w - Right, Math.Round(railBase) + 0.5));

        var accent = ChartPaint.Fps;
        var hole = ChartPaint.Brush(ChartPaint.Res("SurfaceColor", 0x11));
        var warm = ChartPaint.Warm;
        double from = From.Ticks, to = Math.Max(To.Ticks, From.Ticks + TimeSpan.TicksPerHour);
        double X(DateTime t) => Left + (t.Ticks - from) / (to - from) * (w - Left - Right);
        for (int i = 0; i < pts.Count; i++) _xs.Add(X(pts[i].At));

        // Where the zoom grows from: the play's own dot (or the right edge when it's outside the range).
        int origin = PlayStart is { } start ? pts.ToList().FindIndex(p => !p.IsWeek && p.At == start) : -1;
        if (origin < 0 && PlayStart is { } s2) origin = pts.ToList().FindIndex(p => p.IsWeek && s2 >= p.End.AddDays(-7) && s2 < p.End);
        var o = origin >= 0 ? new Point(_xs[origin], Y(pts[origin].Fps)) : new Point(w - Right, (Top + plotBottom) / 2);
        double z = Zoom, big = Math.Max(8, (w - Left) / 8);

        if (z < 0.999)
        {
            dc.PushTransform(new ScaleTransform(1 + z * (big - 1), 1, o.X, o.Y));
            dc.PushOpacity(1 - z);
            DrawPlays();
            dc.Pop();
            dc.Pop();
        }
        if (z > 0.001 && mins.Count >= 2)
        {
            double sc = 1 / big + (1 - 1 / big) * z;
            dc.PushTransform(new ScaleTransform(sc, 1, o.X, o.Y));
            dc.PushOpacity(z);
            DrawPlay();
            dc.Pop();
            dc.Pop();
        }

        var bounds = new Rect(0, 0, w, h);
        if (z <= 0.001 && _hoverMark >= 0 && _hoverMark < _flags.Count)
        {
            var (hit, m) = _flags[_hoverMark];
            ChartPaint.InfoBox(dc, this, [(m.Title, ChartPaint.TextBrush, true), (m.Time.ToString(TimeText.Fit("ddd d MMM, h:mm tt")), ChartPaint.Muted, false)],
                new Point(hit.Right, hit.Bottom), bounds);
        }
        else if (z <= 0.001 && _hover >= 0 && _hover < pts.Count) HoverPoint(pts[_hover], _xs[_hover]);
        else if (z >= 0.999 && _hoverMinute >= 0 && _hoverMinute < mins.Count) HoverMinute(mins[_hoverMinute], _minuteXs[_hoverMinute]);

        void DrawPlays()
        {
            if (pts.Count == 0)
            {
                ChartPaint.Text(dc, this, Lang.T("No plays with a frame rate in this range"), new Point((Left + w - Right) / 2, (Top + plotBottom) / 2), 12.5, ChartPaint.Muted, ChartPaint.Align.Center);
            }
            // The lines break where nothing was played (2026-10-27: a line straight from the 21st to the 26th read as
            // plays in between): more than two days between plays, or a week without any.
            var gap = TimeSpan.FromDays(pts.Any(p => p.IsWeek) ? 8 : 2);
            foreach (var run in Runs(pts.Count, i => pts[i].At, gap))
            {
                var withLow = run.Where(i => pts[i].Low is not null).ToList();
                if (withLow.Count >= 2)
                {
                    Band(withLow.Select(i => new Point(_xs[i], Y(pts[i].Fps))).ToList(), withLow.Select(i => new Point(_xs[i], Y(pts[i].Low!.Value))).ToList());
                    Line(withLow.Select(i => new Point(_xs[i], Y(pts[i].Low!.Value))).ToList(), new Pen(ChartPaint.Muted, 1.6) { DashStyle = new DashStyle([3, 2.5], 0) });
                }
                Line(run.Select(i => new Point(_xs[i], Y(pts[i].Fps))).ToList(), new Pen(accent, 2.2));
            }

            // Updates: a flag on its dashed line; its name beside it only where it fits before the next one.
            var marks = (Marks ?? []).Where(m => m.Time >= From && m.Time <= To).OrderBy(m => m.Time).ToList();
            double free = double.MinValue;
            var flagPen = new Pen(ChartPaint.Brush(accent.Color, 0.7), 1) { DashStyle = new DashStyle([3, 3], 0) };
            for (int k = 0; k < marks.Count; k++)
            {
                double x = Math.Round(X(marks[k].Time)) + 0.5;
                dc.DrawLine(flagPen, new Point(x, Top - 8), new Point(x, railBase));
                var diamond = new StreamGeometry();
                using (var g = diamond.Open())
                {
                    g.BeginFigure(new Point(x, Top - 18), true, true);
                    g.LineTo(new Point(x + 4.5, Top - 13.5), true, false);
                    g.LineTo(new Point(x, Top - 9), true, false);
                    g.LineTo(new Point(x - 4.5, Top - 13.5), true, false);
                }
                diamond.Freeze();
                dc.DrawGeometry(ChartPaint.FpsMarkText, null, diamond);
                var hit = new Rect(x - 8, Top - 22, 16, 18);
                var ft = ChartPaint.Format(this, marks[k].Label, 10.5, ChartPaint.FpsMarkText);
                double next = k + 1 < marks.Count ? X(marks[k + 1].Time) - 10 : w - Right;
                if (x + 8 > free && x + 8 + ft.Width < next)
                {
                    dc.DrawText(ft, new Point(x + 8, Top - 13.5 - ft.Height / 2));
                    hit = new Rect(x - 8, Top - 22, 16 + ft.Width + 4, 18);
                    free = x + 8 + ft.Width + 6;
                }
                _flags.Add((hit, marks[k]));
            }

            // Stutters, one bar a play (or week) with frame data, as tall as its stutters an hour; then the dots.
            double barW = pts.Any(p => p.IsWeek) ? Math.Clamp((w - Left - Right) / Math.Max(1, pts.Count) / 2, 4, 10) : 6;
            for (int i = 0; i < pts.Count; i++)
                if (pts[i].StuttersAnHour is double rate)
                {
                    double bh = 3 + Math.Min(1, rate / 400) * (Rail - 2);
                    dc.DrawRoundedRectangle(warm, null, new Rect(_xs[i] - barW / 2, railBase - bh, barW, bh), 1, 1);
                }
            var ring = new Pen(ChartPaint.Brush(accent.Color, 0.5), 1.5);
            var cut = new Pen(ChartPaint.Muted, 2);
            for (int i = 0; i < pts.Count; i++)
            {
                var c = new Point(_xs[i], Y(pts[i].Fps));
                if (pts[i].CanOpen && !pts[i].IsWeek) dc.DrawEllipse(null, ring, c, 7, 7);
                if (pts[i].PowerCut) dc.DrawEllipse(hole, cut, c, 4.5, 4.5);
                else { double r = pts[i].IsWeek ? 2.8 : 3.2; dc.DrawEllipse(accent, new Pen(hole, 1.5), c, r, r); }
            }
            DateLabels();
        }

        void DateLabels()
        {
            double span = (to - from) / TimeSpan.TicksPerDay, room = w - Left - Right;
            var ticks = new List<(DateTime At, string Text)>();
            if (span > 62)
            {
                int step = new[] { 1, 2, 3, 6 }.FirstOrDefault(s => room / (span / 30.4 / s) >= 56, 12);
                for (var d = new DateTime(From.Year, From.Month, 1).AddMonths(1); d < To; d = d.AddMonths(step)) ticks.Add((d, d.ToString(d.Month == 1 ? "MMM yyyy" : "MMM")));
            }
            else
            {
                int step = new[] { 1, 2, 5, 7, 10, 14 }.FirstOrDefault(s => room / (span / s) >= (s == 1 ? 96 : 64), 30);
                string fmt = step == 1 ? "ddd d MMM" : "d MMM";
                for (var d = To.Date; d >= From.Date; d = d.AddDays(-step))
                    if (d.AddHours(12) >= From && d.AddHours(12) <= To) ticks.Insert(0, (d.AddHours(12), d.ToString(fmt)));
            }
            foreach (var (at, text) in ticks)
            {
                double x = Math.Clamp(X(at), Left + 28, w - Right - 28);
                ChartPaint.Text(dc, this, text, new Point(x, h - 6), 11, ChartPaint.Label, ChartPaint.Align.Center);
            }
        }

        void DrawPlay()
        {
            DateTime a = mins[0].At, b = mins[^1].At.AddMinutes(1);
            double span = Math.Max(1, (b - a).Ticks);
            double Xm(DateTime t) => Left + (t - a).Ticks / span * (w - Left - Right);
            foreach (var m in mins) _minuteXs.Add(Xm(m.At.AddSeconds(30)));
            var top = mins.Select((m, i) => new Point(_minuteXs[i], Y(m.Fps!.Value))).ToList();
            // Broken where the play has no frames for over two minutes (the game behind other windows, say).
            foreach (var run in Runs(mins.Count, i => mins[i].At, TimeSpan.FromMinutes(2.5)))
            {
                var withLow = run.Where(i => mins[i].Low is not null).ToList();
                if (withLow.Count >= 2)
                {
                    Band(withLow.Select(i => top[i]).ToList(), withLow.Select(i => new Point(_minuteXs[i], Y(mins[i].Low!.Value))).ToList());
                    Line(withLow.Select(i => new Point(_minuteXs[i], Y(mins[i].Low!.Value))).ToList(), new Pen(ChartPaint.Muted, 1.3) { DashStyle = new DashStyle([3, 2.5], 0) });
                }
                Line(run.Select(i => top[i]).ToList(), new Pen(accent, 1.7));
            }
            long aMs = new DateTimeOffset(a).ToUnixTimeMilliseconds();
            var tick = ChartPaint.Brush(warm.Color, 0.8);
            foreach (var s in Stutters ?? [])
            {
                double x = Left + (s.AtMs - aMs) / (span / TimeSpan.TicksPerMillisecond) * (w - Left - Right);
                if (x < Left || x > w - Right) continue;
                double bh = 4 + Math.Min(1, Math.Max(0, s.Ms - 20) / 80) * (Rail - 2);
                dc.DrawRectangle(tick, null, new Rect(x - 0.6, railBase - bh, 1.2, bh));
            }
            int labels = Math.Clamp((int)((w - Left - Right) / 120) + 1, 2, 12);
            for (int k = 0; k < labels; k++)
            {
                var at = a + TimeSpan.FromTicks((long)(span * k / (labels - 1)));
                var align = k == 0 ? ChartPaint.Align.Left : k == labels - 1 ? ChartPaint.Align.Right : ChartPaint.Align.Center;
                ChartPaint.Text(dc, this, TimeText.At(at, "h:mm tt"), new Point(Left + (w - Left - Right) * k / (labels - 1), h - 6), 11, ChartPaint.Label, align);
            }
        }

        void HoverPoint(RunsPoint p, double x0)
        {
            double x = Math.Round(x0) + 0.5;
            dc.DrawLine(new Pen(ChartPaint.Cursor, 1), new Point(x, Top), new Point(x, railBase));
            var lines = new List<(string, Brush, bool)>
            {
                (p.IsWeek ? Lang.T($"Week of {p.End.AddDays(-7):d MMM} · {(p.Plays == 1 ? Lang.T("1 play") : Lang.T($"{p.Plays} plays"))}")
                          : Lang.T($"{p.At:ddd d MMM}  {p.At:h:mm tt} to {p.End:h:mm tt} · {Units.Duration(p.ActiveSec)}"), ChartPaint.TextBrush, true),
                (Lang.T($"{p.Fps:0} fps average"), accent, false),
            };
            if (p.Low is double low) lines.Add((Lang.T($"{low:0} fps 1% low"), ChartPaint.Muted, false));
            if (p.StuttersAnHour is double rate) lines.Add((rate < 0.5 ? Lang.T("No stutters") : Lang.T($"{rate:0} stutters an hour"), warm, false));
            if (p.Gpu is double gpu && !p.IsWeek) lines.Add(($"GPU {Units.TempShort(gpu)}" + (p.Watts is double watts ? $" · {watts:0} W" : ""), ChartPaint.Muted, false));
            if (p.PowerCut) lines.Add((Lang.T("Ended by a power cut"), ChartPaint.Muted, false));
            // An update shortly before: what may explain a change.
            if ((Marks ?? []).Where(m => m.Time <= p.End && m.Time > p.At.AddDays(-7)).MaxBy(m => m.Time) is { } m)
                lines.Add((Lang.T($"{m.Time:d MMM}: {m.Title}"), ChartPaint.FpsMarkText, false));
            if (p.CanOpen) lines.Add((p.IsWeek ? Lang.T("Click to see this week") : Lang.T("Click to see this play"), ChartPaint.Muted, false));
            dc.DrawEllipse(accent, new Pen(hole, 1.5), new Point(x0, Y(p.Fps)), 4, 4);
            ChartPaint.InfoBox(dc, this, lines, new Point(x, Y(p.Fps) - 10), bounds, above: true);
        }

        void HoverMinute(RunsMinute m, double x0)
        {
            double x = Math.Round(x0) + 0.5;
            dc.DrawLine(new Pen(ChartPaint.Cursor, 1), new Point(x, Top), new Point(x, railBase));
            var lines = new List<(string, Brush, bool)>
            {
                (TimeText.At(m.At, "h:mm tt"), ChartPaint.TextBrush, true),
                (Lang.T($"{m.Fps:0} fps average"), accent, false),
            };
            if (m.Low is double low) lines.Add((Lang.T($"{low:0} fps 1% low"), ChartPaint.Muted, false));
            lines.Add((m.Stutters switch { 0 => Lang.T("No stutters in this minute"), 1 => Lang.T("1 stutter in this minute"), _ => Lang.T($"{m.Stutters} stutters in this minute") },
                m.Stutters > 0 ? warm : ChartPaint.Muted, false));
            dc.DrawEllipse(accent, new Pen(hole, 1.5), new Point(x0, Y(m.Fps!.Value)), 3.5, 3.5);
            ChartPaint.InfoBox(dc, this, lines, new Point(x, Y(m.Fps!.Value) - 10), bounds, above: true);
        }

        // The points split where more than gap lies between one and the next.
        static List<List<int>> Runs(int count, Func<int, DateTime> at, TimeSpan gap)
        {
            var runs = new List<List<int>>();
            for (int i = 0; i < count; i++)
            {
                if (i == 0 || at(i) - at(i - 1) > gap) runs.Add([]);
                runs[^1].Add(i);
            }
            return runs;
        }

        void Band(List<Point> top, List<Point> bottom)
        {
            var band = new StreamGeometry();
            using (var g = band.Open())
            {
                g.BeginFigure(top[0], true, true);
                foreach (var p in top.Skip(1)) g.LineTo(p, true, false);
                foreach (var p in Enumerable.Reverse(bottom)) g.LineTo(p, true, false);
            }
            band.Freeze();
            dc.DrawGeometry(ChartPaint.Brush(accent.Color, 0.10), null, band);
        }

        void Line(List<Point> points, Pen pen)
        {
            if (points.Count < 2) return;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(points[0], false, false);
                foreach (var p in points.Skip(1)) g.LineTo(p, true, true);
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }
    }
}
