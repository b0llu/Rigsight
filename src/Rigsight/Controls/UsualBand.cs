using System.Windows;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// An app's usual on its Memory bar as a spread (1.23.0 mock-up, Memory): a dashed outline from <see cref="From"/> to
/// <see cref="To"/> (shares of the bar, 0 to 100) with a faint fill, a little taller than the bar so the bar shows
/// through it. A spread that rounds to nothing is drawn a few pixels wide around its point, so it never vanishes.
/// Its brushes come from the palette (set in XAML); nothing on it is clicked.
/// </summary>
public sealed class UsualBand : FrameworkElement
{
    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(
        nameof(From), typeof(double), typeof(UsualBand), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(
        nameof(To), typeof(double), typeof(UsualBand), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(UsualBand), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((UsualBand)d)._pen = null));

    /// <summary>Where the spread starts on the bar (0 to 100).</summary>
    public double From { get => (double)GetValue(FromProperty); set => SetValue(FromProperty, value); }
    /// <summary>Where the spread ends on the bar (0 to 100).</summary>
    public double To { get => (double)GetValue(ToProperty); set => SetValue(ToProperty, value); }
    /// <summary>The outline's colour; the fill is the same, faint.</summary>
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    /// <summary>The narrowest the band is drawn (px).</summary>
    private const double Least = 4;
    private const double FillOpacity = 0.12;
    private Pen? _pen;

    public UsualBand()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    protected override Size MeasureOverride(Size available) => new(0, 14);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0 || Stroke is not { } stroke) return;
        double a = Math.Clamp(Math.Min(From, To), 0, 100) / 100 * w, b = Math.Clamp(Math.Max(From, To), 0, 100) / 100 * w;
        if (b - a < Least)
        {
            double mid = (a + b) / 2;
            a = Math.Clamp(mid - Least / 2, 0, Math.Max(0, w - Least));
            b = Math.Min(w, a + Least);
        }
        // Not frozen: freezing it would freeze the theme's own brush with it.
        _pen ??= new Pen(stroke, 1) { DashStyle = new DashStyle([3, 2], 0) };
        var box = new Rect(a + 0.5, 0.5, Math.Max(1, b - a - 1), h - 1);
        dc.PushOpacity(FillOpacity);
        dc.DrawRoundedRectangle(stroke, null, box, 4, 4);
        dc.Pop();
        dc.DrawRoundedRectangle(null, _pen, box, 4, 4);
    }
}
