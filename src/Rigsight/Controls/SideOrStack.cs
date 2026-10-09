using System.Windows;
using System.Windows.Controls;

namespace Rigsight.Controls;

/// <summary>
/// Two parts side by side while there is room (the first as wide as it asks, the second the rest), one above the
/// other when the panel is narrower than <see cref="Threshold"/>: a card's gauge and its tiles. At the window's
/// smallest size the tiles beside a gauge were 80 px wide and cut their own readings off ("33.8" with no W).
/// </summary>
public sealed class SideOrStack : Panel
{
    /// <summary>The width from which the two fit side by side.</summary>
    public static readonly DependencyProperty ThresholdProperty = DependencyProperty.Register(nameof(Threshold), typeof(double), typeof(SideOrStack),
        new FrameworkPropertyMetadata(460.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The space between the two, either way.</summary>
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double), typeof(SideOrStack),
        new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Threshold { get => (double)GetValue(ThresholdProperty); set => SetValue(ThresholdProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    /// <summary>Whether a panel this wide puts the second part under the first.</summary>
    internal bool Stacks(double width) => !double.IsInfinity(width) && width < Threshold;

    protected override Size MeasureOverride(Size available)
    {
        if (InternalChildren.Count == 0) return default;
        UIElement first = InternalChildren[0];
        UIElement? second = InternalChildren.Count > 1 ? InternalChildren[1] : null;
        if (Stacks(available.Width))
        {
            first.Measure(new Size(available.Width, double.PositiveInfinity));
            second?.Measure(new Size(available.Width, double.PositiveInfinity));
            double height = first.DesiredSize.Height + (second is null ? 0 : Gap + second.DesiredSize.Height);
            return new Size(Math.Max(first.DesiredSize.Width, second?.DesiredSize.Width ?? 0), height);
        }
        first.Measure(new Size(double.PositiveInfinity, available.Height));
        double rest = Math.Max(0, available.Width - first.DesiredSize.Width - Gap);
        second?.Measure(new Size(rest, available.Height));
        return new Size(first.DesiredSize.Width + (second is null ? 0 : Gap + second.DesiredSize.Width),
            Math.Max(first.DesiredSize.Height, second?.DesiredSize.Height ?? 0));
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (InternalChildren.Count == 0) return final;
        UIElement first = InternalChildren[0];
        UIElement? second = InternalChildren.Count > 1 ? InternalChildren[1] : null;
        if (Stacks(final.Width))
        {
            first.Arrange(new Rect(0, 0, final.Width, first.DesiredSize.Height));
            second?.Arrange(new Rect(0, first.DesiredSize.Height + Gap, final.Width, Math.Max(0, final.Height - first.DesiredSize.Height - Gap)));
            return final;
        }
        double width = Math.Min(first.DesiredSize.Width, final.Width);
        first.Arrange(new Rect(0, 0, width, final.Height));
        second?.Arrange(new Rect(width + Gap, 0, Math.Max(0, final.Width - width - Gap), final.Height));
        return final;
    }
}
