using System.Windows;
using System.Windows.Controls;

namespace Rigsight.Controls;

/// <summary>Gives its child the width it's offered and a height of that width times <see cref="Ratio"/> (a cover is 2:3, so 1.5).</summary>
public sealed class AspectBox : Decorator
{
    public static readonly DependencyProperty RatioProperty = DependencyProperty.Register(
        nameof(Ratio), typeof(double), typeof(AspectBox), new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Ratio { get => (double)GetValue(RatioProperty); set => SetValue(RatioProperty, value); }

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? 140 : available.Width;
        var size = new Size(w, w * Ratio);
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size final)
    {
        Child?.Arrange(new Rect(new Point(0, 0), new Size(final.Width, final.Width * Ratio)));
        return new Size(final.Width, final.Width * Ratio);
    }
}
