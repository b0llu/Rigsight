using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core;

namespace Rigsight.Controls;

/// <summary>One play on the Games page's chart (or one minute of it, see <see cref="FpsChart.ByMinute"/>): its frame rate, 1% low and what the hover box says.</summary>
public sealed record FpsPoint(DateTime Start, DateTime End, double ActiveSec, double Fps, double? Low, double? Gpu, double? Watts, bool PowerCut);

/// <summary>A change between two plays (a graphics driver, the game's update), drawn between them with a short label.</summary>
public sealed record FpsMark(double Between, string Label, string Title, DateTime Time);

/// <summary>
/// The Games page's "How it runs": a play's average frame rate as a line with dots, its 1% low dashed under it with the
/// band between them shaded, a power cut as a hollow grey dot, and labelled dashed lines where a driver or the game
/// was updated. Hover a play for its figures (a box for each line: the average's above it, the 1% low's below its own
/// point), a label for the change. While a game has a single play with frames, the
/// chart shows that play minute by minute instead (<see cref="ByMinute"/>): the same lines, times along the bottom.
/// </summary>
public sealed class FpsChart : FrameworkElement
{
    private const double Left = 44, Right = 14, Top = 34, Bottom = 24;

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<FpsPoint>), typeof(FpsChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarksProperty = DependencyProperty.Register(
        nameof(Marks), typeof(IReadOnlyList<FpsMark>), typeof(FpsChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<FpsPoint>? Points { get => (IReadOnlyList<FpsPoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public IReadOnlyList<FpsMark>? Marks { get => (IReadOnlyList<FpsMark>?)GetValue(MarksProperty); set => SetValue(MarksProperty, value); }

    public static readonly DependencyProperty ByMinuteProperty = DependencyProperty.Register(
        nameof(ByMinute), typeof(bool), typeof(FpsChart), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Each point is a minute of one play: no dots (too many), times along the bottom, the minute's figures on hover.</summary>
    public bool ByMinute { get => (bool)GetValue(ByMinuteProperty); set => SetValue(ByMinuteProperty, value); }

    private int _hover = -1, _hoverMark = -1;
    private readonly List<double> _xs = [];
    private readonly List<Rect> _labels = [];

    public FpsChart() => ClipToBounds = true;

    protected override Size MeasureOverride(Size available) => new(double.IsInfinity(available.Width) ? 600 : available.Width, 230);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        int mark = _labels.FindIndex(r => r.Contains(p));
        int next = -1;
        if (mark < 0 && _xs.Count > 0 && p.Y >= Top && p.Y <= ActualHeight - Bottom)
        {
            double best = double.MaxValue;
            for (int i = 0; i < _xs.Count; i++)
                if (Math.Abs(_xs[i] - p.X) < best) { best = Math.Abs(_xs[i] - p.X); next = i; }
        }
        if (next != _hover || mark != _hoverMark) { _hover = next; _hoverMark = mark; InvalidateVisual(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = _hoverMark = -1;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        _xs.Clear();
        _labels.Clear();
        var pts = Points;
        if (pts is null || pts.Count < 2 || w < Left + Right + 100) return;

        double hi = Math.Ceiling(pts.Max(p => p.Fps) * 1.1 / 10) * 10;
        double lo = Math.Max(0, Math.Floor(pts.Min(p => p.Low ?? p.Fps) * 0.8 / 10) * 10);
        if (hi - lo < 20) hi = lo + 20;
        double X(int i) => Left + i * (w - Left - Right) / (pts.Count - 1);
        double Y(double v) => Top + (hi - v) * (h - Top - Bottom) / (hi - lo);

        var grid = new Pen(ChartPaint.Grid, 1);
        for (int k = 0; k <= 4; k++)
        {
            double v = Math.Round(lo + (hi - lo) * k / 4), y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(grid, new Point(Left, y), new Point(w - Right, y));
            ChartPaint.Text(dc, this, $"{v:0}", new Point(Left - 8, y), 11, ChartPaint.Label, ChartPaint.Align.Right);
        }
        ChartPaint.Text(dc, this, "fps", new Point(Left - 8, Top - 14), 11, ChartPaint.Label, ChartPaint.Align.Right);

        // The frame rate's own blue, or the picked accent (see Accents.Fps).
        var accent = ChartPaint.Fps;
        for (int i = 0; i < pts.Count; i++) _xs.Add(X(i));

        // The band between the average and the 1% low, then the low dashed, then the average.
        var withLow = Enumerable.Range(0, pts.Count).Where(i => pts[i].Low is not null).ToList();
        if (withLow.Count >= 2)
        {
            var band = new StreamGeometry();
            using (var g = band.Open())
            {
                g.BeginFigure(new Point(X(withLow[0]), Y(pts[withLow[0]].Fps)), true, true);
                foreach (var i in withLow.Skip(1)) g.LineTo(new Point(X(i), Y(pts[i].Fps)), true, false);
                foreach (var i in Enumerable.Reverse(withLow)) g.LineTo(new Point(X(i), Y(pts[i].Low!.Value)), true, false);
            }
            band.Freeze();
            dc.DrawGeometry(ChartPaint.Brush(accent.Color, 0.10), null, band);
            var dashed = new Pen(ChartPaint.Muted, 1.6) { DashStyle = new DashStyle([3, 2.5], 0) };
            Line(withLow, i => pts[i].Low!.Value, dashed);
        }

        foreach (var (m, k) in (Marks ?? []).Select((m, k) => (m, k)))
        {
            int before = (int)Math.Floor(m.Between);
            if (before < 0 || before >= pts.Count - 1) continue;
            double x = Math.Round((X(before) + X(before + 1)) / 2) + 0.5;
            dc.DrawLine(new Pen(ChartPaint.Brush(accent.Color, 0.7), 1) { DashStyle = new DashStyle([3, 3], 0) }, new Point(x, Top - 4), new Point(x, h - Bottom));
            var ft = ChartPaint.Format(this, m.Label, 10.5, ChartPaint.FpsMarkText);
            var box = new Rect(x - ft.Width / 2 - 8, Top - 27, ft.Width + 16, 18);
            if (box.Left < 0) box.X = 0;
            if (box.Right > w) box.X = w - box.Width;
            dc.DrawRoundedRectangle(ChartPaint.FpsMark, new Pen(ChartPaint.FpsMarkStroke, 1), box, 5, 5);
            dc.DrawText(ft, new Point(box.X + 8, box.Y + (18 - ft.Height) / 2));
            _labels.Add(box);
        }

        Line(Enumerable.Range(0, pts.Count).ToList(), i => pts[i].Fps, new Pen(accent, 2.2));
        var ring = new Pen(ChartPaint.Muted, 2);
        var hole = ChartPaint.Brush(ChartPaint.Res("SurfaceColor", 0x11));
        bool byMinute = ByMinute;
        for (int i = 0; i < pts.Count && !byMinute; i++)
        {
            var c = new Point(X(i), Y(pts[i].Fps));
            if (pts[i].PowerCut) dc.DrawEllipse(hole, ring, c, 4.5, 4.5);
            else dc.DrawEllipse(accent, new Pen(hole, 1.5), c, 3.2, 3.2);
        }

        // Times (or days) along the bottom, about one every 120 px: the first and last at the ends, the rest centred under
        // their point; a day already written (two plays on one day) isn't written again.
        string when = byMinute ? "h:mm tt" : "ddd d MMM";
        int labels = Math.Clamp((int)((w - Left - Right) / 120) + 1, 2, pts.Count);
        string? written = null;
        for (int k = 0; k < labels; k++)
        {
            int i = (int)Math.Round(k * (pts.Count - 1) / (double)(labels - 1));
            string text = pts[i].Start.ToString(when);
            if (text == written) continue;
            written = text;
            var align = k == 0 ? ChartPaint.Align.Left : k == labels - 1 ? ChartPaint.Align.Right : ChartPaint.Align.Center;
            ChartPaint.Text(dc, this, text, new Point(k == labels - 1 ? w - Right : X(i), h - 6), 11, ChartPaint.Label, align);
        }

        var bounds = new Rect(0, 0, w, h);
        if (_hoverMark >= 0 && Marks is { } marks && _hoverMark < marks.Count)
        {
            var m = marks.Where(x => x.Between >= 0 && x.Between < pts.Count - 1).ElementAtOrDefault(_hoverMark);
            if (m is not null)
                ChartPaint.InfoBox(dc, this, [(m.Title, ChartPaint.TextBrush, true), (m.Time.ToString("ddd d MMM, h:mm tt"), ChartPaint.Muted, false)],
                    new Point(_labels[_hoverMark].Right, _labels[_hoverMark].Bottom), bounds);
        }
        else if (_hover >= 0 && _hover < pts.Count)
        {
            var p = pts[_hover];
            double x = Math.Round(X(_hover)) + 0.5;
            dc.DrawLine(new Pen(ChartPaint.Cursor, 1), new Point(x, Top), new Point(x, h - Bottom));
            if (byMinute) dc.DrawEllipse(accent, new Pen(hole, 1.5), new Point(X(_hover), Y(p.Fps)), 3.2, 3.2);
            // The average's box: when, the frame rate, the graphics card.
            var lines = new List<(string, Brush, bool)>
            {
                (byMinute ? $"{p.Start:ddd d MMM}  {p.Start:h:mm tt}" : Lang.T($"{p.Start:ddd d MMM}  {p.Start:h:mm tt} to {p.End:h:mm tt} · {Units.Duration(p.ActiveSec)}"), ChartPaint.TextBrush, true),
                (Lang.T($"{p.Fps:0} fps average"), accent, false),
            };
            if (p.Gpu is double gpu) lines.Add(($"GPU {Units.TempShort(gpu)}" + (p.Watts is double watts ? $" · {watts:0} W" : ""), ChartPaint.Muted, false));
            if (p.PowerCut) lines.Add((Lang.T("Ended by a power cut"), ChartPaint.Muted, false));
            var top = ChartPaint.InfoBox(dc, this, lines, new Point(x, Y(p.Fps) - 8), bounds, above: true);
            // The 1% low's own box, under its point on the dashed line (never over the average's).
            if (p.Low is double low)
            {
                var at = new Point(X(_hover), Y(low));
                dc.DrawEllipse(ChartPaint.Muted, new Pen(hole, 1.5), at, 3.2, 3.2);
                ChartPaint.InfoBox(dc, this, [(Lang.T($"{low:0} fps 1% low"), ChartPaint.TextBrush, true), (Lang.T($"The slowest frames: {p.Fps - low:0} under the average"), ChartPaint.Muted, false)],
                    new Point(x, at.Y + 8), bounds, minTop: top.Bottom + 6);
            }
        }

        void Line(List<int> idx, Func<int, double> value, Pen pen)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(X(idx[0]), Y(value(idx[0]))), false, false);
                foreach (var i in idx.Skip(1)) g.LineTo(new Point(X(i), Y(value(i))), true, true);
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }
    }
}
