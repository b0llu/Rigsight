using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// A <see cref="Border"/> whose rounded corners are as strong as its straight edges. WPF strokes a thin rounded
/// border with a pen, and along the curve the anti-aliasing spreads each pixel's worth of line over two
/// half-bright pixels, so a 1 px border looks thinner at the corners. Drawn instead as the border colour filled
/// edge to edge with the (opaque) background filled on top, inset by the thickness with a matching smaller radius,
/// the curve keeps the edges' weight. A see-through background (which would let the border colour show through),
/// uneven thickness or uneven corners fall back to the normal drawing.
/// </summary>
public class SmoothBorder : Border
{
    protected override void OnRender(DrawingContext dc)
    {
        var t = BorderThickness;
        var r = CornerRadius;
        double w = t.Left, radius = r.TopLeft;
        bool even = w > 0 && t.Top == w && t.Right == w && t.Bottom == w
            && r.TopRight == radius && r.BottomRight == radius && r.BottomLeft == radius;
        if (!even || BorderBrush is null || Background is not SolidColorBrush { Color.A: 255, Opacity: >= 1 } background)
        {
            base.OnRender(dc);
            return;
        }

        var outer = new Rect(RenderSize);
        if (outer.Width <= 2 * w || outer.Height <= 2 * w)
        {
            base.OnRender(dc);
            return;
        }
        dc.DrawRoundedRectangle(BorderBrush, null, outer, radius, radius);
        double inner = Math.Max(0, radius - w);
        dc.DrawRoundedRectangle(background, null, new Rect(w, w, outer.Width - 2 * w, outer.Height - 2 * w), inner, inner);
    }
}
