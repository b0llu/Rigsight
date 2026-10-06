using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Models;

namespace Rigsight.Controls;

/// <summary>Tiny auto-scaled line chart of a sensor's recent history.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(HistoryBuffer), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(long), typeof(Sparkline), new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => { if (((Sparkline)d)._hoverX is not null) ((Sparkline)d).ShowTip(); }));

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

    private Pen? _pen;

    public HistoryBuffer? Source { get => (HistoryBuffer?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public long Version { get => (long)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double WindowSeconds { get => (double)GetValue(WindowSecondsProperty); set => SetValue(WindowSecondsProperty, value); }
    public double? Minimum { get => (double?)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    public static readonly DependencyProperty FitToDataProperty = DependencyProperty.Register(
        nameof(FitToData), typeof(bool), typeof(Sparkline), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Stretch whatever history there is (up to <see cref="WindowSeconds"/>) across the full width, instead of
    /// a fixed window mostly empty until it fills (e.g. drive temperatures, kept for a few hours by the agent).
    /// </summary>
    public bool FitToData { get => (bool)GetValue(FitToDataProperty); set => SetValue(FitToDataProperty, value); }
    public double? Maximum { get => (double?)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    /// <summary>What the line measures, for the reading shown under the pointer (none: the number as it is).</summary>
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(SensorKind?), typeof(Sparkline), new PropertyMetadata(null));

    public SensorKind? Kind { get => (SensorKind?)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    // Like every chart, it answers the pointer: the reading at that moment, and when.
    private readonly ToolTip _tip = new() { Placement = PlacementMode.Relative, IsHitTestVisible = false };
    private double? _hoverX;
    private long _from, _to;

    public Sparkline()
    {
        // A page switched away under a resting pointer takes the chart with it and no "mouse left" is said: the tip
        // (a window of its own) must not be left open behind it.
        Unloaded += (_, _) => HideTip();
        IsVisibleChanged += (_, _) => { if (!IsVisible) HideTip(); };
    }

    private void HideTip()
    {
        _hoverX = null;
        _tip.IsOpen = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hoverX = e.GetPosition(this).X;
        ShowTip();
        InvalidateVisual();
    }

    /// <summary>The reading under the pointer in a tip beside it; kept up as new readings arrive.</summary>
    private void ShowTip()
    {
        if (!IsMouseOver || !IsLoaded || HoverIndex() is not (>= 0 and var i) || _hoverX is not double x)
        {
            _tip.IsOpen = false;
            return;
        }
        _tip.Content = HoverText(i);
        _tip.PlacementTarget = this;
        _tip.HorizontalOffset = x + 12;
        _tip.VerticalOffset = -30;
        _tip.IsOpen = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoverX = null;
        _tip.IsOpen = false;
        InvalidateVisual();
    }

    /// <summary>The sample nearest the pointer (index into <see cref="Source"/>), or -1.</summary>
    private int HoverIndex()
    {
        if (_hoverX is not double x || Source is not { Count: >= 2 } buffer || ActualWidth < 4 || _to <= _from) return -1;
        int i = buffer.NearestIndex(_from + (long)(Math.Clamp(x / ActualWidth, 0, 1) * (_to - _from)));
        return i >= 0 && !double.IsNaN(buffer.ValueAt(i)) ? i : -1;
    }

    /// <summary>"62 °C · 3:04:10 PM" (to the minute on a line of hours).</summary>
    internal string? HoverText(int i)
    {
        if (i < 0 || Source is not { } buffer) return null;
        double v = buffer.ValueAt(i);
        var when = DateTimeOffset.FromUnixTimeMilliseconds(buffer.TimeAt(i)).LocalDateTime;
        return $"{(Kind is { } kind ? Units.Format(kind, v) : v.ToString("0.#"))} · {when.ToString(WindowSeconds > 3600 ? "h:mm tt" : "h:mm:ss tt")}";
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent background so the whole area is hit-testable for tooltips.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        var buffer = Source;
        if (buffer is null || buffer.Count < 2 || ActualWidth < 4 || ActualHeight < 4) return;

        long to = buffer.LastTime;
        long from = to - (long)(WindowSeconds * 1000);
        if (FitToData) from = Math.Min(Math.Max(from, buffer.FirstTime), to - 60_000);
        (_from, _to) = (from, to);
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
        if (_pen is null || _pen.Brush != Stroke)
        {
            _pen = new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round };
            _pen.Freeze();
        }
        dc.DrawGeometry(null, _pen, line);

        // Under the pointer: a dot on the line (the reading beside it is the tip, see ShowTip).
        if (HoverIndex() is >= 0 and var i)
        {
            double x = plot.Left + plot.Width * (buffer.TimeAt(i) - from) / (double)(to - from);
            double y = plot.Bottom - (buffer.ValueAt(i) - lo) / (hi - lo) * plot.Height;
            dc.DrawEllipse(Stroke, null, new Point(Math.Clamp(x, 0, ActualWidth), Math.Clamp(y, plot.Top, plot.Bottom)), 2.5, 2.5);
        }
        dc.Pop();
    }
}
