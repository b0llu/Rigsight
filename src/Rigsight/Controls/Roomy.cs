using System.Windows;

namespace Rigsight.Controls;

/// <summary>
/// A chart or a list whose height is set for the window's usual size and grows with a taller window:
/// <c>ctl:Roomy.Height="230"</c> is 230 at the usual 920 px and up to 1.6 times that maximised on a large screen,
/// where a chart stayed a 230 px strip across 2,400 px with half the window empty under the page. Never smaller than
/// the height given. <c>ctl:Roomy.MaxHeight</c> does the same for a box that scrolls.
/// </summary>
public static class Roomy
{
    /// <summary>The window height the pages' heights were set for.</summary>
    internal const double UsualHeight = 920;
    internal const double Most = 1.6;

    /// <summary>How much taller than given things are right now (1 to <see cref="Most"/>).</summary>
    public static double Factor { get; private set; } = 1;

    /// <summary>The factor for a window this tall, in steps of a twentieth so dragging the window's edge doesn't lay every page out on each pixel.</summary>
    internal static double FactorFor(double windowHeight) =>
        double.IsNaN(windowHeight) ? 1 : Math.Clamp(Math.Floor(windowHeight / UsualHeight * 20) / 20, 1, Most);

    private static readonly List<WeakReference<FrameworkElement>> Elements = [];

    /// <summary>The window's height changed: everything that grows with it is sized again.</summary>
    public static void Fit(double windowHeight)
    {
        double factor = FactorFor(windowHeight);
        if (factor == Factor) return;
        Factor = factor;
        Elements.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var weak in Elements)
            if (weak.TryGetTarget(out var element)) Apply(element);
    }

    public static readonly DependencyProperty HeightProperty = DependencyProperty.RegisterAttached("Height", typeof(double), typeof(Roomy),
        new PropertyMetadata(double.NaN, OnChanged));
    public static readonly DependencyProperty MaxHeightProperty = DependencyProperty.RegisterAttached("MaxHeight", typeof(double), typeof(Roomy),
        new PropertyMetadata(double.NaN, OnChanged));

    public static double GetHeight(DependencyObject o) => (double)o.GetValue(HeightProperty);
    public static void SetHeight(DependencyObject o, double value) => o.SetValue(HeightProperty, value);
    public static double GetMaxHeight(DependencyObject o) => (double)o.GetValue(MaxHeightProperty);
    public static void SetMaxHeight(DependencyObject o, double value) => o.SetValue(MaxHeightProperty, value);

    private static void OnChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not FrameworkElement element) return;
        if (!Elements.Exists(w => w.TryGetTarget(out var known) && ReferenceEquals(known, element))) Elements.Add(new WeakReference<FrameworkElement>(element));
        Apply(element);
    }

    private static void Apply(FrameworkElement element)
    {
        if (GetHeight(element) is var height && !double.IsNaN(height)) element.Height = Math.Round(height * Factor);
        if (GetMaxHeight(element) is var most && !double.IsNaN(most)) element.MaxHeight = Math.Round(most * Factor);
    }
}
