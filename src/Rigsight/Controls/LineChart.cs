using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Models;

namespace Rigsight.Controls;

/// <summary>Multi-series time chart with a labelled grid, used for temperature history.</summary>
public sealed class LineChart : FrameworkElement
{
    private const double AxisWidth = 42;
    private const double AxisHeight = 22;

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IEnumerable<ChartSeries>), typeof(LineChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(long), typeof(LineChart), new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WindowSecondsProperty = DependencyProperty.Register(
        nameof(WindowSeconds), typeof(int), typeof(LineChart), new FrameworkPropertyMetadata(300, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(LineChart), new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x23, 0x2B, 0x3D)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(LineChart), new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x5B, 0x64, 0x7A)), FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Typeface LabelFont = new("Segoe UI");

    public IEnumerable<ChartSeries>? Series { get => (IEnumerable<ChartSeries>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public long Version { get => (long)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public int WindowSeconds { get => (int)GetValue(WindowSecondsProperty); set => SetValue(WindowSecondsProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < AxisWidth + 20 || ActualHeight < AxisHeight + 20) return;

        var plot = new Rect(AxisWidth, 6, ActualWidth - AxisWidth - 6, ActualHeight - AxisHeight - 6);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var series = Series?.ToList() ?? [];

        long to = series.Count == 0 ? 0 : series.Max(s => s.Sensor.History.LastTime);
        long windowMs = WindowSeconds * 1000L;
        long from = to - windowMs;

        // Y range across all series, in display units, snapped to multiples of 10.
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var s in series)
        {
            if (ChartGeometry.Range(s.Sensor.History, from, Units.Temp) is { } r)
            {
                lo = Math.Min(lo, r.Min);
                hi = Math.Max(hi, r.Max);
            }
        }

        if (lo > hi)
        {
            DrawText(dc, "Collecting data…", new Point(plot.Left + plot.Width / 2, plot.Top + plot.Height / 2), dpi, center: true);
            return;
        }

        lo = Math.Floor((lo - 2) / 10) * 10;
        hi = Math.Ceiling((hi + 2) / 10) * 10;
        if (hi - lo < 20) hi = lo + 20;

        // Horizontal grid + Y labels.
        var gridPen = new Pen(GridBrush, 1) { DashStyle = new DashStyle([3, 4], 0) };
        const int rows = 4;
        for (int i = 0; i <= rows; i++)
        {
            double v = lo + (hi - lo) * i / rows;
            double y = Math.Round(plot.Bottom - plot.Height * i / rows) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            DrawText(dc, $"{v:0}°", new Point(plot.Left - 8, y), dpi, alignRight: true);
        }

        // X labels at "nice" steps.
        int step = WindowSeconds switch { <= 60 => 15, <= 300 => 60, <= 900 => 180, _ => 900 };
        for (int t = 0; t <= WindowSeconds; t += step)
        {
            double x = plot.Right - plot.Width * t / WindowSeconds;
            string label = t == 0 ? "now" : t < 60 ? $"-{t}s" : $"-{t / 60}m";
            DrawText(dc, label, new Point(x, plot.Bottom + 12), dpi, center: true);
        }

        dc.PushClip(new RectangleGeometry(new Rect(plot.Left, plot.Top - 2, plot.Width, plot.Height + 4)));
        foreach (var s in series)
        {
            if (ChartGeometry.Build(s.Sensor.History, from, to, plot, lo, hi, Units.Temp) is not { } g) continue;
            dc.DrawGeometry(ChartGeometry.FadeFill(s.Color, 0.10), null, g.Fill);
            dc.DrawGeometry(null, new Pen(s.Brush, 2) { LineJoin = PenLineJoin.Round }, g.Line);
        }
        dc.Pop();
    }

    private void DrawText(DrawingContext dc, string text, Point anchor, double dpi, bool alignRight = false, bool center = false)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFont, 11, LabelBrush, dpi);
        double x = alignRight ? anchor.X - ft.Width : center ? anchor.X - ft.Width / 2 : anchor.X;
        dc.DrawText(ft, new Point(x, anchor.Y - ft.Height / 2));
    }
}
