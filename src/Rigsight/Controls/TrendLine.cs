using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// A small line through a value a day (the Temperatures page's "At rest": a chip's resting temperature over the last
/// month), scaled to its own range so a slow climb shows. A day with nothing breaks the line; a day standing alone is
/// a dot. It answers the pointer with that day's text.
/// </summary>
public sealed class TrendLine : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double?>), typeof(TrendLine), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>What to say under the pointer, a text for each value.</summary>
    public static readonly DependencyProperty TipsProperty = DependencyProperty.Register(
        nameof(Tips), typeof(IReadOnlyList<string>), typeof(TrendLine), new PropertyMetadata(null));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(TrendLine), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double?>? Values { get => (IReadOnlyList<double?>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public IReadOnlyList<string>? Tips { get => (IReadOnlyList<string>?)GetValue(TipsProperty); set => SetValue(TipsProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    /// <summary>The least the scale spans (the values' own unit): a degree up and down isn't drawn as a mountain.</summary>
    private const double MinSpan = 6;

    private readonly ToolTip _tip = new() { Placement = PlacementMode.Relative, IsHitTestVisible = false };
    private int _hover = -1;

    public TrendLine()
    {
        Unloaded += (_, _) => HideTip();
        IsVisibleChanged += (_, _) => { if (!IsVisible) HideTip(); };
    }

    private void HideTip()
    {
        _hover = -1;
        _tip.IsOpen = false;
    }

    private double XOf(int i, int count) => count < 2 ? ActualWidth / 2 : 3 + (ActualWidth - 6) * i / (count - 1);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Values is not { Count: >= 1 } values || ActualWidth < 8) return;
        double x = e.GetPosition(this).X;
        _hover = values.Count < 2 ? 0 : Math.Clamp((int)Math.Round((x - 3) / (ActualWidth - 6) * (values.Count - 1)), 0, values.Count - 1);
        _tip.Content = Tips is { } tips && _hover < tips.Count ? tips[_hover] : null;
        _tip.PlacementTarget = this;
        _tip.HorizontalOffset = x + 12;
        _tip.VerticalOffset = -30;
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
        // Transparent background so the whole area answers the pointer.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Values is not { Count: >= 1 } values || ActualWidth < 8 || ActualHeight < 8) return;
        var have = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        if (have.Count == 0) return;
        double lo = have.Min(), hi = have.Max();
        if (hi - lo < MinSpan) (lo, hi) = ((lo + hi - MinSpan) / 2, (lo + hi + MinSpan) / 2);
        double YOf(double v) => 3 + (ActualHeight - 6) * (1 - (v - lo) / (hi - lo));

        var pen = new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            bool open = false;
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] is not double v)
                {
                    open = false;
                    continue;
                }
                var point = new Point(XOf(i, values.Count), YOf(v));
                if (open) g.LineTo(point, true, true);
                else
                {
                    g.BeginFigure(point, false, false);
                    // A day with none on either side: a dot, or it wouldn't show at all.
                    bool alone = (i == 0 || values[i - 1] is null) && (i == values.Count - 1 || values[i + 1] is null);
                    if (alone) dc.DrawEllipse(Stroke, null, point, 1.8, 1.8);
                    open = true;
                }
            }
        }
        line.Freeze();
        dc.DrawGeometry(null, pen, line);
        if (_hover >= 0 && _hover < values.Count && values[_hover] is double at)
            dc.DrawEllipse(Stroke, null, new Point(XOf(_hover, values.Count), YOf(at)), 3, 3);
    }
}
