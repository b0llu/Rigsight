using System.Windows;
using System.Windows.Controls;

namespace Rigsight.Controls;

/// <summary>
/// Asks for no height of its own and gives its child the height its row gets from the others. A list beside a fixed
/// column (Network's Using it now beside the speeds and chart) fills that column's height and scrolls past it, but never
/// makes the card taller: the card kept growing a few pixels the moment the list started to scroll, moving the whole page.
/// </summary>
public sealed class TakeRowHeight : Decorator
{
    // The height the row gave last time: the child is measured at it, so a scroll box inside knows where its view ends.
    private double _height = double.PositiveInfinity;

    protected override Size MeasureOverride(Size available)
    {
        if (Child is null) return default;
        Child.Measure(new Size(available.Width, _height));
        return new Size(Child.DesiredSize.Width, 0);
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (Math.Abs(final.Height - _height) > 0.5)
        {
            // One more pass at the real height; it settles because this box's own height never changes the row's.
            _height = final.Height;
            InvalidateMeasure();
        }
        Child?.Arrange(new Rect(final));
        return final;
    }
}
