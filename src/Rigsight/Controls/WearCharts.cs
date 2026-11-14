using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core.Reports;

namespace Rigsight.Controls;

/// <summary>
/// The "Over the months" tile's small chart (approved mock-up 2026-11-14): a bar a week, the first weeks' level as a
/// dashed line, the last bar in its own colour (amber when the part moved). The scale spans at least
/// <see cref="MinSpan"/> of the figure's own unit, so a part that holds steady draws flat. It answers the pointer with
/// that week's text, the bar lit and the others dimmed.
/// </summary>
public sealed class WearBars : FrameworkElement
{
    public static readonly DependencyProperty WeeksProperty = DependencyProperty.Register(
        nameof(Weeks), typeof(IReadOnlyList<WearWeek>), typeof(WearBars), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BaseProperty = DependencyProperty.Register(
        nameof(Base), typeof(double?), typeof(WearBars), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MinSpanProperty = DependencyProperty.Register(
        nameof(MinSpan), typeof(double), typeof(WearBars), new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(WearBars), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LastFillProperty = DependencyProperty.Register(
        nameof(LastFill), typeof(Brush), typeof(WearBars), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>What to say under the pointer, a text for each week.</summary>
    public static readonly DependencyProperty TipsProperty = DependencyProperty.Register(
        nameof(Tips), typeof(IReadOnlyList<string>), typeof(WearBars), new PropertyMetadata(null));

    public IReadOnlyList<WearWeek>? Weeks { get => (IReadOnlyList<WearWeek>?)GetValue(WeeksProperty); set => SetValue(WeeksProperty, value); }
    public double? Base { get => (double?)GetValue(BaseProperty); set => SetValue(BaseProperty, value); }
    public double MinSpan { get => (double)GetValue(MinSpanProperty); set => SetValue(MinSpanProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush? LastFill { get => (Brush?)GetValue(LastFillProperty); set => SetValue(LastFillProperty, value); }
    public IReadOnlyList<string>? Tips { get => (IReadOnlyList<string>?)GetValue(TipsProperty); set => SetValue(TipsProperty, value); }

    private readonly ToolTip _tip = new() { Placement = PlacementMode.Relative, IsHitTestVisible = false };
    private int _hover = -1;

    public WearBars()
    {
        Unloaded += (_, _) => HideTip();
        IsVisibleChanged += (_, _) => { if (!IsVisible) HideTip(); };
    }

    private void HideTip()
    {
        _hover = -1;
        _tip.IsOpen = false;
    }

    private const double Gap = 3;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Weeks is not { Count: >= 1 } weeks || ActualWidth < 8) return;
        double x = e.GetPosition(this).X;
        int i = Math.Clamp((int)(x / (ActualWidth / weeks.Count)), 0, weeks.Count - 1);
        if (i == _hover) return;
        _hover = i;
        _tip.Content = Tips is { } tips && i < tips.Count ? tips[i] : null;
        _tip.PlacementTarget = this;
        _tip.HorizontalOffset = Math.Max(0, x - 60);
        _tip.VerticalOffset = -54;
        _tip.IsOpen = IsLoaded && _tip.Content is not null;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        HideTip();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Weeks is not { Count: >= 1 } weeks || ActualWidth < 8 || ActualHeight < 8) return;
        var values = weeks.Select(w => w.Value).ToList();
        double lo = Math.Min(values.Min(), Base ?? values[0]), hi = Math.Max(values.Max(), Base ?? values[0]);
        if (hi - lo < MinSpan) (lo, hi) = ((lo + hi - MinSpan) / 2, (lo + hi + MinSpan) / 2);
        // Bars stand on a floor below the lowest, so a steady part's bars are all a good height (not slivers).
        double HeightOf(double v) => ActualHeight * (0.45 + 0.5 * (v - lo) / (hi - lo));
        var axis = (Brush)Application.Current.FindResource("StrokeBrush");
        dc.DrawRectangle(axis, null, new Rect(0, ActualHeight - 1, ActualWidth, 1));
        double w = (ActualWidth - Gap * (weeks.Count - 1)) / weeks.Count;
        for (int i = 0; i < weeks.Count; i++)
        {
            double h = HeightOf(values[i]);
            var brush = i == weeks.Count - 1 && LastFill is not null ? LastFill : Fill;
            double opacity = _hover < 0 ? (i == weeks.Count - 1 ? 1 : 0.8) : (i == _hover ? 1 : 0.4);
            dc.PushOpacity(opacity);
            var rect = new Rect(i * (w + Gap), ActualHeight - 1 - h, Math.Max(1, w), h);
            dc.DrawRoundedRectangle(brush, null, rect, Math.Min(2, w / 2), Math.Min(2, w / 2));
            dc.Pop();
        }
        if (Base is double b)
        {
            var pen = new Pen((Brush)Application.Current.FindResource("MutedBrush"), 1) { DashStyle = new DashStyle([3, 3], 0) };
            double y = Math.Round(ActualHeight - 1 - HeightOf(b)) + 0.5;
            dc.PushOpacity(0.6);
            dc.DrawLine(pen, new Point(0, y), new Point(ActualWidth, y));
            dc.Pop();
        }
    }
}

/// <summary>
/// The cooling chart on Temperatures (approved mock-up 2026-11-14): a part's weekly figure as a line over its usual band
/// (shaded), its first and last weeks marked and labelled, the last in amber when it left the band. Charts that sit
/// together are given one scale (<see cref="ScaleLow"/>, <see cref="ScaleSpan"/>) so a flat line is truly flat. The
/// pointer picks a week: a guide line, the point lit, and that week's text.
/// </summary>
public sealed class WearLine : FrameworkElement
{
    public static readonly DependencyProperty WeeksProperty = DependencyProperty.Register(
        nameof(Weeks), typeof(IReadOnlyList<WearWeek>), typeof(WearLine), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BandLowProperty = DependencyProperty.Register(
        nameof(BandLow), typeof(double?), typeof(WearLine), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BandHighProperty = DependencyProperty.Register(
        nameof(BandHigh), typeof(double?), typeof(WearLine), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ScaleLowProperty = DependencyProperty.Register(
        nameof(ScaleLow), typeof(double), typeof(WearLine), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ScaleSpanProperty = DependencyProperty.Register(
        nameof(ScaleSpan), typeof(double), typeof(WearLine), new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(WearLine), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MovedProperty = DependencyProperty.Register(
        nameof(Moved), typeof(bool), typeof(WearLine), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FirstLabelProperty = DependencyProperty.Register(
        nameof(FirstLabel), typeof(string), typeof(WearLine), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LastLabelProperty = DependencyProperty.Register(
        nameof(LastLabel), typeof(string), typeof(WearLine), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TipsProperty = DependencyProperty.Register(
        nameof(Tips), typeof(IReadOnlyList<string>), typeof(WearLine), new PropertyMetadata(null));

    public IReadOnlyList<WearWeek>? Weeks { get => (IReadOnlyList<WearWeek>?)GetValue(WeeksProperty); set => SetValue(WeeksProperty, value); }
    public double? BandLow { get => (double?)GetValue(BandLowProperty); set => SetValue(BandLowProperty, value); }
    public double? BandHigh { get => (double?)GetValue(BandHighProperty); set => SetValue(BandHighProperty, value); }
    /// <summary>The scale's bottom (the figure's unit); NaN: from the line itself.</summary>
    public double ScaleLow { get => (double)GetValue(ScaleLowProperty); set => SetValue(ScaleLowProperty, value); }
    public double ScaleSpan { get => (double)GetValue(ScaleSpanProperty); set => SetValue(ScaleSpanProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public bool Moved { get => (bool)GetValue(MovedProperty); set => SetValue(MovedProperty, value); }
    public string FirstLabel { get => (string)GetValue(FirstLabelProperty); set => SetValue(FirstLabelProperty, value); }
    public string LastLabel { get => (string)GetValue(LastLabelProperty); set => SetValue(LastLabelProperty, value); }
    public IReadOnlyList<string>? Tips { get => (IReadOnlyList<string>?)GetValue(TipsProperty); set => SetValue(TipsProperty, value); }

    private readonly ToolTip _tip = new() { Placement = PlacementMode.Relative, IsHitTestVisible = false };
    private int _hover = -1;
    private const double PadX = 8, PadY = 22, Label = 12;

    public WearLine()
    {
        Unloaded += (_, _) => HideTip();
        IsVisibleChanged += (_, _) => { if (!IsVisible) HideTip(); };
    }

    private void HideTip()
    {
        _hover = -1;
        _tip.IsOpen = false;
    }

    private double XOf(int i, int count) => count < 2 ? ActualWidth / 2 : PadX + (ActualWidth - 2 * PadX) * i / (count - 1);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Weeks is not { Count: >= 1 } weeks || ActualWidth < 20) return;
        double x = e.GetPosition(this).X;
        int i = weeks.Count < 2 ? 0 : Math.Clamp((int)Math.Round((x - PadX) / (ActualWidth - 2 * PadX) * (weeks.Count - 1)), 0, weeks.Count - 1);
        if (i == _hover) return;
        _hover = i;
        _tip.Content = Tips is { } tips && i < tips.Count ? tips[i] : null;
        _tip.PlacementTarget = this;
        _tip.HorizontalOffset = Math.Max(0, XOf(i, weeks.Count) - 100);
        _tip.VerticalOffset = -70;
        _tip.IsOpen = IsLoaded && _tip.Content is not null;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        HideTip();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Weeks is not { Count: >= 1 } weeks || ActualWidth < 20 || ActualHeight < 40) return;
        var values = weeks.Select(w => w.Value).ToList();
        double lo = double.IsNaN(ScaleLow) ? Math.Min(values.Min(), BandLow ?? values.Min()) - 1 : ScaleLow;
        double span = Math.Max(ScaleSpan, 1);
        double YOf(double v) => PadY + (ActualHeight - 2 * PadY) * (1 - (v - lo) / span);
        var amber = (Brush)Application.Current.FindResource("WarmBrush");
        var muted = (Brush)Application.Current.FindResource("MutedBrush");
        var faint = (Brush)Application.Current.FindResource("FaintBrush");

        if (BandLow is double bl && BandHigh is double bh)
        {
            double top = YOf(bh), bottom = YOf(bl);
            var shade = Stroke.CloneCurrentValue();
            shade.Opacity = 0.13;
            dc.DrawRectangle(shade, null, new Rect(0, top, ActualWidth, Math.Max(1, bottom - top)));
            var dash = new Pen(Stroke, 1) { DashStyle = new DashStyle([3, 4], 0) };
            dc.PushOpacity(0.35);
            dc.DrawLine(dash, new Point(0, Math.Round(top) + 0.5), new Point(ActualWidth, Math.Round(top) + 0.5));
            dc.DrawLine(dash, new Point(0, Math.Round(bottom) + 0.5), new Point(ActualWidth, Math.Round(bottom) + 0.5));
            dc.Pop();
        }

        var pen = new Pen(Stroke, 2.2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(XOf(0, weeks.Count), YOf(values[0])), false, false);
            for (int i = 1; i < values.Count; i++) g.LineTo(new Point(XOf(i, weeks.Count), YOf(values[i])), true, true);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);

        if (_hover >= 0 && _hover < weeks.Count)
        {
            var guide = new Pen(muted, 1) { DashStyle = new DashStyle([3, 3], 0) };
            double gx = Math.Round(XOf(_hover, weeks.Count)) + 0.5;
            dc.PushOpacity(0.7);
            dc.DrawLine(guide, new Point(gx, 0), new Point(gx, ActualHeight));
            dc.Pop();
            dc.DrawEllipse(Stroke, null, new Point(XOf(_hover, weeks.Count), YOf(values[_hover])), 4.5, 4.5);
        }

        // The first and last weeks, marked and labelled on the line's outside (under a low point, over a high one, as the
        // mock-up has them), clear of the line and the band: the change reads without an axis.
        double Outside(double y) => y > ActualHeight / 2 ? Math.Min(y + Label + 2, ActualHeight - Label / 2) : Math.Max(y - Label - 2, Label / 2);
        var first = new Point(XOf(0, weeks.Count), YOf(values[0]));
        var last = new Point(XOf(weeks.Count - 1, weeks.Count), YOf(values[^1]));
        dc.DrawEllipse(Stroke, null, first, 4, 4);
        dc.DrawEllipse(Moved ? amber : Stroke, null, last, 4.5, 4.5);
        if (FirstLabel.Length > 0)
            ChartPaint.Text(dc, this, FirstLabel, new Point(0, Outside(first.Y)), Label, faint);
        if (LastLabel.Length > 0)
            ChartPaint.Text(dc, this, LastLabel, new Point(ActualWidth, Outside(last.Y)), Label, Moved ? amber : faint, ChartPaint.Align.Right);
    }
}
