using System.Windows;
using System.Windows.Controls;

namespace Rigsight.Controls;

/// <summary>
/// Two cards side by side in equal halves and of one height, one above the other when the panel is narrower than
/// <see cref="Threshold"/> (each half would be too narrow for its rows). A card that is collapsed leaves the other
/// the whole row: a lone card never stands in half of it beside an empty space.
/// </summary>
public sealed class CardPair : Panel
{
    /// <summary>The width from which the two stand side by side.</summary>
    public static readonly DependencyProperty ThresholdProperty = DependencyProperty.Register(nameof(Threshold), typeof(double), typeof(CardPair),
        new FrameworkPropertyMetadata(900.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The space between the two, either way (the page's gap between cards).</summary>
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double), typeof(CardPair),
        new FrameworkPropertyMetadata(18.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Threshold { get => (double)GetValue(ThresholdProperty); set => SetValue(ThresholdProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    /// <summary>Whether a panel this wide puts the second card under the first.</summary>
    internal bool Stacks(double width) => !double.IsInfinity(width) && width < Threshold;

    private List<UIElement> Shown => [.. InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed)];

    protected override Size MeasureOverride(Size available)
    {
        foreach (UIElement child in InternalChildren)
            if (child.Visibility == Visibility.Collapsed) child.Measure(default);
        var shown = Shown;
        if (shown.Count == 0) return default;
        if (shown.Count == 1 || Stacks(available.Width))
        {
            double height = 0, width = 0;
            foreach (var child in shown)
            {
                child.Measure(new Size(available.Width, double.PositiveInfinity));
                height += child.DesiredSize.Height;
                width = Math.Max(width, child.DesiredSize.Width);
            }
            return new Size(width, height + Gap * (shown.Count - 1));
        }
        double half = double.IsInfinity(available.Width) ? double.PositiveInfinity : Math.Max(0, (available.Width - Gap) / 2);
        shown[0].Measure(new Size(half, double.PositiveInfinity));
        shown[1].Measure(new Size(half, double.PositiveInfinity));
        return new Size(shown[0].DesiredSize.Width + Gap + shown[1].DesiredSize.Width, Math.Max(shown[0].DesiredSize.Height, shown[1].DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        var shown = Shown;
        if (shown.Count == 1 || (shown.Count > 1 && Stacks(final.Width)))
        {
            double y = 0;
            foreach (var child in shown)
            {
                child.Arrange(new Rect(0, y, final.Width, child.DesiredSize.Height));
                y += child.DesiredSize.Height + Gap;
            }
            return final;
        }
        if (shown.Count == 2)
        {
            double half = Math.Max(0, (final.Width - Gap) / 2);
            shown[0].Arrange(new Rect(0, 0, half, final.Height));
            shown[1].Arrange(new Rect(half + Gap, 0, half, final.Height));
        }
        return final;
    }
}
