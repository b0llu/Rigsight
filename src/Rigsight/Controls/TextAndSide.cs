using System.Windows;
using System.Windows.Controls;

namespace Rigsight.Controls;

/// <summary>
/// A line's sentence with something beside it (Home's Explain button and date): side by side while the sentence keeps
/// at least <see cref="MinText"/> of width, the side part under the sentence otherwise. In Home's "What changed lately"
/// at a 1440 by 932 window the Explain button and date left the sentence about 25 px, one word to a line. The
/// opposite of <see cref="SideOrStack"/>, where the first part keeps its own width and the second gets the rest.
/// </summary>
public sealed class TextAndSide : Panel
{
    /// <summary>The least width the sentence keeps beside the side part.</summary>
    public static readonly DependencyProperty MinTextProperty = DependencyProperty.Register(nameof(MinText), typeof(double), typeof(TextAndSide),
        new FrameworkPropertyMetadata(160.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The space between the sentence and the side part, beside or under it.</summary>
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double), typeof(TextAndSide),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinText { get => (double)GetValue(MinTextProperty); set => SetValue(MinTextProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    private bool _stacked;

    /// <summary>Whether the side part sits under the sentence: there is a side part and beside it the sentence would get less than MinText.</summary>
    internal static bool Stacks(double width, double side, double gap, double minText) =>
        side > 0 && !double.IsInfinity(width) && width - side - gap < minText;

    protected override Size MeasureOverride(Size available)
    {
        if (InternalChildren.Count == 0) return default;
        UIElement text = InternalChildren[0];
        UIElement? side = InternalChildren.Count > 1 ? InternalChildren[1] : null;
        side?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double sideWidth = side?.DesiredSize.Width ?? 0;
        _stacked = Stacks(available.Width, sideWidth, Gap, MinText);
        if (_stacked)
        {
            text.Measure(new Size(available.Width, double.PositiveInfinity));
            return new Size(Math.Max(text.DesiredSize.Width, sideWidth), text.DesiredSize.Height + Gap + side!.DesiredSize.Height);
        }
        double gap = sideWidth > 0 ? Gap : 0;
        text.Measure(new Size(Math.Max(0, available.Width - sideWidth - gap), available.Height));
        return new Size(text.DesiredSize.Width + gap + sideWidth, Math.Max(text.DesiredSize.Height, side?.DesiredSize.Height ?? 0));
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (InternalChildren.Count == 0) return final;
        UIElement text = InternalChildren[0];
        UIElement? side = InternalChildren.Count > 1 ? InternalChildren[1] : null;
        double sideWidth = side?.DesiredSize.Width ?? 0;
        if (_stacked)
        {
            text.Arrange(new Rect(0, 0, final.Width, text.DesiredSize.Height));
            side!.Arrange(new Rect(0, text.DesiredSize.Height + Gap, Math.Min(sideWidth, final.Width), side.DesiredSize.Height));
            return final;
        }
        double gap = sideWidth > 0 ? Gap : 0;
        double textWidth = Math.Max(0, final.Width - sideWidth - gap);
        text.Arrange(new Rect(0, 0, textWidth, final.Height));
        side?.Arrange(new Rect(textWidth + gap, 0, sideWidth, final.Height));
        return final;
    }
}
