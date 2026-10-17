using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Core.Reports;

namespace Rigsight.Controls;

/// <summary>
/// "Each time you played": the days before a change and the days since, as two rows on one temperature scale. A dot is
/// a day's average in the compared game, the bar each side's average. When every dot since sits apart from every dot
/// before, it was every time, not one odd evening. Hover a dot for its day.
/// </summary>
public sealed class ChangeChart : FrameworkElement
{
    private const double Left = 64, Right = 14, RowA = 44, RowB = 104, AxisY = 138, Dot = 6;

    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(
        nameof(Days), typeof(IReadOnlyList<ChangeDay>), typeof(ChangeChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The change's day: the days before it are "before", the days after it "since" (its own day is neither).</summary>
    public static readonly DependencyProperty ChangeDayProperty = DependencyProperty.Register(
        nameof(ChangeDay), typeof(DateTime), typeof(ChangeChart), new FrameworkPropertyMetadata(default(DateTime), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BeforeProperty = DependencyProperty.Register(
        nameof(Before), typeof(double?), typeof(ChangeChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AfterProperty = DependencyProperty.Register(
        nameof(After), typeof(double?), typeof(ChangeChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>"Since" while the change is the current one, "After" once another came.</summary>
    public static readonly DependencyProperty AfterNameProperty = DependencyProperty.Register(
        nameof(AfterName), typeof(string), typeof(ChangeChart), new FrameworkPropertyMetadata("Since", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Hotter since: the "since" row in the warm colour; cooler: in the good colour.</summary>
    public static readonly DependencyProperty WorseProperty = DependencyProperty.Register(
        nameof(Worse), typeof(bool), typeof(ChangeChart), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>What a dot is ("GPU in Crimson Desert"), said on hover.</summary>
    public static readonly DependencyProperty TempLabelProperty = DependencyProperty.Register(
        nameof(TempLabel), typeof(string), typeof(ChangeChart), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChangeDay>? Days { get => (IReadOnlyList<ChangeDay>?)GetValue(DaysProperty); set => SetValue(DaysProperty, value); }
    public DateTime ChangeDay { get => (DateTime)GetValue(ChangeDayProperty); set => SetValue(ChangeDayProperty, value); }
    public double? Before { get => (double?)GetValue(BeforeProperty); set => SetValue(BeforeProperty, value); }
    public double? After { get => (double?)GetValue(AfterProperty); set => SetValue(AfterProperty, value); }
    public string AfterName { get => (string)GetValue(AfterNameProperty); set => SetValue(AfterNameProperty, value); }
    public bool Worse { get => (bool)GetValue(WorseProperty); set => SetValue(WorseProperty, value); }
    public string TempLabel { get => (string)GetValue(TempLabelProperty); set => SetValue(TempLabelProperty, value); }

    /// <summary>The days with a reading, each where it's drawn.</summary>
    private readonly List<(ChangeDay Day, Point At)> _dots = [];
    private int _hover = -1;

    protected override Size MeasureOverride(Size available) => new(double.IsInfinity(available.Width) ? 500 : available.Width, 160);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        int next = -1;
        double best = 14 * 14;
        for (int i = 0; i < _dots.Count; i++)
        {
            double dx = _dots[i].At.X - p.X, dy = _dots[i].At.Y - p.Y, d = dx * dx + dy * dy;
            if (d < best) { best = d; next = i; }
        }
        if (next != _hover) { _hover = next; InvalidateVisual(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = -1;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        _dots.Clear();
        var days = Days;
        if (days is null || w < Left + Right + 100) return;
        var read = days.Where(d => d.Temp is not null && d.Day != ChangeDay).ToList();
        if (read.Count == 0) return;

        // The scale: whole fives around every reading and both averages, at least 20° wide so a small difference looks small.
        var all = read.Select(d => d.Temp!.Value).Concat(new[] { Before, After }.OfType<double>()).ToList();
        double lo = Math.Floor((all.Min() - 2) / 5) * 5, hi = Math.Ceiling((all.Max() + 2) / 5) * 5;
        if (hi - lo < 20) { double mid = (lo + hi) / 2; lo = Math.Floor((mid - 10) / 5) * 5; hi = lo + 20; }
        double X(double v) => Left + (v - lo) / (hi - lo) * (w - Left - Right);

        var grid = new Pen(ChartPaint.Grid, 1);
        for (double v = lo; v <= hi + 0.1; v += 5)
        {
            double x = Math.Round(X(v)) + 0.5;
            dc.DrawLine(grid, new Point(x, RowA - 26), new Point(x, RowB + 16));
            ChartPaint.Text(dc, this, Units.TempShort(v), new Point(x, AxisY), 11, ChartPaint.Label, ChartPaint.Align.Center);
        }

        var since = Worse ? ChartPaint.Warm : ChartPaint.Good;
        Row("Before", RowA, read.Where(d => d.Day < ChangeDay).ToList(), Before, ChartPaint.Muted);
        Row(AfterName, RowB, read.Where(d => d.Day > ChangeDay).ToList(), After, since);

        if (_hover >= 0 && _hover < _dots.Count)
        {
            var (d, at) = _dots[_hover];
            var lines = new List<(string, Brush, bool)>
            {
                (d.Day.ToString("dddd, d MMM"), ChartPaint.TextBrush, true),
                ($"{TempLabel}: {Units.TempShort(d.Temp)}", ChartPaint.Muted, false),
            };
            if (d.Minutes > 0) lines.Add(($"Played: {Units.Duration(d.Minutes * 60.0)}", ChartPaint.Muted, false));
            ChartPaint.InfoBox(dc, this, lines, new Point(at.X + 10, at.Y - 10), new Rect(0, 0, w, h));
        }

        void Row(string name, double y, List<ChangeDay> side, double? average, Brush colour)
        {
            ChartPaint.Text(dc, this, name, new Point(0, y), 12, ChartPaint.TextBrush, ChartPaint.Align.Left);
            dc.DrawLine(new Pen(ChartPaint.Track, 2), new Point(X(lo), y), new Point(X(hi), y));
            if (average is double avg)
            {
                double ax = X(avg);
                dc.DrawRoundedRectangle(colour, null, new Rect(ax - 1.5, y - 14, 3, 28), 1.5, 1.5);
                ChartPaint.Text(dc, this, $"{Units.TempShort(avg)} average", new Point(ax, y - 23), 11, colour, ChartPaint.Align.Center);
            }
            var fill = colour.Clone();
            fill.Opacity = 0.55;
            fill.Freeze();
            var pen = new Pen(colour, 1.5);
            foreach (var d in side)
            {
                var at = new Point(X(d.Temp!.Value), y);
                int i = _dots.Count;
                _dots.Add((d, at));
                dc.DrawEllipse(fill, pen, at, i == _hover ? Dot + 1.5 : Dot, i == _hover ? Dot + 1.5 : Dot);
            }
        }
    }
}
