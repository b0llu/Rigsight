using System.Windows;
using System.Windows.Media;
using Rigsight.Models;

namespace Rigsight.Controls;

/// <summary>Tiny auto-scaled line chart of a sensor's recent history.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(HistoryBuffer), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(long), typeof(Sparkline), new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((Sparkline)d)._fill = null));

    public static readonly DependencyProperty WindowSecondsProperty = DependencyProperty.Register(
        nameof(WindowSeconds), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(60.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Fixed lower bound (e.g. 0 for load). When null the range auto-fits the data.</summary>
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double?), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double?), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private Brush? _fill;

    public HistoryBuffer? Source { get => (HistoryBuffer?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public long Version { get => (long)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double WindowSeconds { get => (double)GetValue(WindowSecondsProperty); set => SetValue(WindowSecondsProperty, value); }
    public double? Minimum { get => (double?)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double? Maximum { get => (double?)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent background so the whole area is hit-testable for tooltips.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        var buffer = Source;
        if (buffer is null || buffer.Count < 2 || ActualWidth < 4 || ActualHeight < 4) return;

        long to = buffer.LastTime;
        long from = to - (long)(WindowSeconds * 1000);
        var range = ChartGeometry.Range(buffer, from, v => v);
        if (range is null) return;
        var (lo, hi) = range.Value;

        lo = Minimum ?? lo;
        hi = Maximum ?? hi;
        if (hi - lo < 1)
        {
            double mid = (hi + lo) / 2;
            lo = Minimum ?? mid - 0.5;
            hi = Maximum ?? lo + 1;
        }
        double pad = (hi - lo) * 0.12;
        if (Minimum is null) lo -= pad;
        if (Maximum is null) hi += pad;

        var plot = new Rect(0, 1.5, ActualWidth, ActualHeight - 3);
        var geometry = ChartGeometry.Build(buffer, from, to, plot, lo, hi, v => v);
        if (geometry is null) return;
        var (line, area) = geometry.Value;

        var color = Stroke is SolidColorBrush s ? s.Color : Colors.DeepSkyBlue;
        _fill ??= ChartGeometry.FadeFill(color, 0.28);
        dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        dc.DrawGeometry(_fill, null, area);
        dc.DrawGeometry(null, new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round }, line);
        dc.Pop();
    }
}
