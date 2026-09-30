using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Rigsight.Core;

namespace Rigsight.Controls;

/// <summary>What the fan charts share: theme brushes (as the other charts take them), a dashed rpm grid from 0 and labels.</summary>
public abstract class FanChartBase : FrameworkElement
{
    protected const double AxisWidth = 44, AxisHeight = 22, Top = 8;

    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush), typeof(Brush), typeof(FanChartBase), new FrameworkPropertyMetadata(Brushes.SeaGreen, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(FanChartBase), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(FanChartBase), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MutedBrushProperty = DependencyProperty.Register(
        nameof(MutedBrush), typeof(Brush), typeof(FanChartBase), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(FanChartBase), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    protected FanChartBase()
    {
        SetResourceReference(GridBrushProperty, "StrokeBrush");
        SetResourceReference(LabelBrushProperty, "FaintBrush");
        SetResourceReference(MutedBrushProperty, "MutedBrush");
        SetResourceReference(LineBrushProperty, "TextBrush");
    }

    /// <summary>The fan's colour (its dots and bars).</summary>
    public Brush Brush { get => (Brush)GetValue(BrushProperty); set => SetValue(BrushProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public Brush MutedBrush { get => (Brush)GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }

    private static readonly Typeface Font = new("Segoe UI");

    /// <summary>A see-through copy of a brush (frozen: charts redraw often).</summary>
    protected static Brush Faded(Brush brush, double opacity)
    {
        var copy = brush.Clone();
        copy.Opacity = opacity;
        copy.Freeze();
        return copy;
    }

    protected FormattedText Text(string text, Brush brush, double size = 11) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected void Label(DrawingContext dc, string text, Point at, Brush brush, bool right = false, bool center = false)
    {
        var ft = Text(text, brush);
        dc.DrawText(ft, new Point(right ? at.X - ft.Width : center ? at.X - ft.Width / 2 : at.X, at.Y - ft.Height / 2));
    }

    /// <summary>A round top for the rpm axis (four steps of 250, 500, 750…), from 0.</summary>
    protected static double RpmTop(double max) =>
        new[] { 250.0, 500, 750, 1000, 1250, 1500, 2000, 2500 }.FirstOrDefault(s => s * 4 >= max * 1.05, 5000) * 4;

    /// <summary>The plot area, with the rpm grid and its labels drawn.</summary>
    protected Rect Plot(DrawingContext dc, double top)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var plot = new Rect(AxisWidth, Top, Math.Max(1, ActualWidth - AxisWidth - 6), Math.Max(1, ActualHeight - AxisHeight - Top));
        var pen = new Pen(GridBrush, 1) { DashStyle = new DashStyle([3, 4], 0) };
        pen.Freeze();
        for (int i = 0; i <= 4; i++)
        {
            double y = Math.Round(plot.Bottom - plot.Height * i / 4) + 0.5;
            dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
            Label(dc, (top * i / 4).ToString("N0", CultureInfo.CurrentCulture), new Point(plot.Left - 8, y), LabelBrush, right: true);
        }
        return plot;
    }
}

/// <summary>
/// A fan's speed at each temperature of the chip it follows: a dot per minute, its usual curve as a line, and the range
/// where it stands still (a card's zero-rpm mode) shaded. A fan on a curve shows a rising line; one at a set speed, flat.
/// </summary>
public sealed class FanCurveChart : FanChartBase
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<Point>), typeof(FanCurveChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LineProperty = DependencyProperty.Register(
        nameof(Line), typeof(IReadOnlyList<Point>), typeof(FanCurveChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LineLabelProperty = DependencyProperty.Register(
        nameof(LineLabel), typeof(string), typeof(FanCurveChart), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SilentBelowProperty = DependencyProperty.Register(
        nameof(SilentBelow), typeof(double?), typeof(FanCurveChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>(temperature in °C, rpm) for each minute.</summary>
    public IReadOnlyList<Point>? Points { get => (IReadOnlyList<Point>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    /// <summary>The usual speed at each temperature, as a line.</summary>
    public IReadOnlyList<Point>? Line { get => (IReadOnlyList<Point>?)GetValue(LineProperty); set => SetValue(LineProperty, value); }
    public string LineLabel { get => (string)GetValue(LineLabelProperty); set => SetValue(LineLabelProperty, value); }
    /// <summary>Stands still below this temperature (°C); null when it never does.</summary>
    public double? SilentBelow { get => (double?)GetValue(SilentBelowProperty); set => SetValue(SilentBelowProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < AxisWidth + 40 || ActualHeight < AxisHeight + 40) return;
        var points = Points ?? [];
        var line = Line ?? [];
        if (points.Count == 0 && line.Count == 0)
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
            Label(dc, "Collecting data…", new Point(ActualWidth / 2, ActualHeight / 2), LabelBrush, center: true);
            return;
        }
        var all = points.Concat(line).ToList();
        double lo = Math.Floor((all.Min(p => p.X) - 1) / 5) * 5, hi = Math.Ceiling((all.Max(p => p.X) + 1) / 5) * 5;
        if (hi - lo < 15) hi = lo + 15;
        double top = RpmTop(all.Max(p => p.Y));
        var plot = Plot(dc, top);
        double X(double t) => plot.Left + plot.Width * (t - lo) / (hi - lo);
        double Y(double r) => plot.Bottom - plot.Height * Math.Clamp(r, 0, top) / top;

        if (SilentBelow is double silent && silent > lo)
        {
            var zone = new Rect(plot.Left, plot.Top, Math.Max(0, Math.Min(X(silent), plot.Right) - plot.Left), plot.Height);
            dc.DrawRectangle(Faded(Brush, 0.10), null, zone);
            Label(dc, $"silent below ~{Units.TempShort(silent)}", new Point(zone.Left + zone.Width / 2, plot.Top + 10), Brush, center: true);
        }
        for (double t = Math.Ceiling(lo / 10) * 10; t <= hi; t += 10)
            Label(dc, Units.TempShort(t), new Point(X(t), plot.Bottom + 12), LabelBrush, center: true);

        var dot = Faded(Brush, 0.55);
        foreach (var p in points) dc.DrawEllipse(dot, null, new Point(X(p.X), Y(p.Y)), 2.4, 2.4);

        if (line.Count >= 2)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(X(line[0].X), Y(line[0].Y)), false, false);
                g.PolyLineTo([.. line.Skip(1).Select(p => new Point(X(p.X), Y(p.Y)))], true, true);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(Faded(LineBrush, 0.75), 1.5), geometry);
            var end = line[^1];
            var ft = Text(LineLabel, MutedBrush);
            dc.DrawText(ft, new Point(Math.Max(plot.Left, X(end.X) - ft.Width), Math.Max(plot.Top, Y(end.Y) - ft.Height - 4)));
        }
    }
}

/// <summary>One day on the 30-day chart: its average speed and its fastest (null bars: nothing recorded that day).</summary>
public sealed record FanDayBar(DateTime Day, double? Average, double? Fastest);

/// <summary>A fan's last 30 days: a bar per day, its average over the fastest it reached, empty days left as a tick.</summary>
public sealed class FanDaysChart : FanChartBase
{
    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(
        nameof(Days), typeof(IReadOnlyList<FanDayBar>), typeof(FanDaysChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GhostBrushProperty = DependencyProperty.Register(
        nameof(GhostBrush), typeof(Brush), typeof(FanDaysChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public FanDaysChart() => SetResourceReference(GhostBrushProperty, "HoverBrush");

    public IReadOnlyList<FanDayBar>? Days { get => (IReadOnlyList<FanDayBar>?)GetValue(DaysProperty); set => SetValue(DaysProperty, value); }
    public Brush GhostBrush { get => (Brush)GetValue(GhostBrushProperty); set => SetValue(GhostBrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var days = Days ?? [];
        if (days.Count == 0 || ActualWidth < AxisWidth + 40 || ActualHeight < AxisHeight + 30) return;
        double top = RpmTop(days.Max(d => d.Fastest ?? d.Average ?? 0));
        var plot = Plot(dc, top);
        double slot = plot.Width / days.Count, bar = Math.Max(2, slot * 0.6);
        double Y(double r) => plot.Bottom - plot.Height * Math.Clamp(r, 0, top) / top;
        for (int i = 0; i < days.Count; i++)
        {
            double x = plot.Left + slot * i + (slot - bar) / 2;
            var d = days[i];
            if (d.Average is null && d.Fastest is null)
            {
                dc.DrawRectangle(GridBrush, null, new Rect(x, plot.Bottom - 2, bar, 2));
                continue;
            }
            if (d.Fastest is double f) dc.DrawRoundedRectangle(GhostBrush, null, new Rect(x, Y(f), bar, plot.Bottom - Y(f)), 3, 3);
            if (d.Average is double a) dc.DrawRoundedRectangle(Brush, null, new Rect(x, Y(a), bar, plot.Bottom - Y(a)), 3, 3);
        }
        foreach (int i in new[] { 0, days.Count / 2, days.Count - 1 }.Distinct())
            Label(dc, days[i].Day.ToString("d MMM", CultureInfo.CurrentCulture), new Point(plot.Left + slot * (i + 0.5), plot.Bottom + 12), LabelBrush, center: true);
    }
}
