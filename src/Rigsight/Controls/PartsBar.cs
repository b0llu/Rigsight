using System.Windows;
using System.Windows.Media;
using Rigsight.Models;

namespace Rigsight.Controls;

/// <summary>
/// One bar cut into parts by their weight: an opened app's memory by kind of process (Processes), a period's time by
/// kind of app (Reports). The names and figures are under it; this only shows the proportions.
/// </summary>
public sealed class PartsBar : FrameworkElement
{
    public static readonly DependencyProperty PartsProperty = DependencyProperty.Register(
        nameof(Parts), typeof(System.Collections.IEnumerable), typeof(PartsBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public System.Collections.IEnumerable? Parts { get => (System.Collections.IEnumerable?)GetValue(PartsProperty); set => SetValue(PartsProperty, value); }

    /// <summary>The gap between two parts.</summary>
    private const double Gap = 2;

    protected override void OnRender(DrawingContext dc)
    {
        var parts = Parts?.OfType<IBarPart>().ToList() ?? [];
        if (parts.Count == 0 || ActualWidth < 4 || ActualHeight < 2) return;
        double total = parts.Sum(p => p.Weight);
        if (total <= 0) return;
        double room = ActualWidth - Gap * (parts.Count - 1), x = 0, r = ActualHeight / 2;
        // Rounded at the bar's two ends only: the parts meet square.
        dc.PushClip(new RectangleGeometry(new Rect(RenderSize), r, r));
        foreach (var part in parts)
        {
            double w = Math.Max(0, room * part.Weight / total);
            if (Converters.ResourceBrushConverter.Of(part.Brush) is { } brush) dc.DrawRectangle(brush, null, new Rect(x, 0, w, ActualHeight));
            x += w + Gap;
        }
        dc.Pop();
    }
}
