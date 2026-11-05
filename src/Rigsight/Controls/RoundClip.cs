using System.Windows;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// Clips an element to a rounded rectangle its own size (a Border's corner radius doesn't clip what's in it): a card's
/// rows can then shade edge to edge on hover, as Windows' settings cards do, without poking out of its round corners.
/// </summary>
public static class RoundClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundClip), new PropertyMetadata(0.0, OnRadiusChanged));

    public static double GetRadius(DependencyObject d) => (double)d.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject d, double value) => d.SetValue(RadiusProperty, value);

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.SizeChanged -= Resize;
        element.SizeChanged += Resize;
        Apply(element);
    }

    private static void Resize(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement element)
    {
        double r = GetRadius(element);
        element.Clip = r <= 0 ? null : new RectangleGeometry(new Rect(0, 0, element.ActualWidth, element.ActualHeight), r, r);
    }
}
