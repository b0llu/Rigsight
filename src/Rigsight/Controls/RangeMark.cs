using System.Windows;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// A sensor's place on its range today, for the Temperatures page's Motherboard list (mock-up 2026-11-14): a thin track
/// for the range, and a short upright mark where the reading is now (0 at the day's lowest, 100 at its highest).
/// </summary>
public sealed class RangeMark : FrameworkElement
{
    public static readonly DependencyProperty ShareProperty = DependencyProperty.Register(
        nameof(Share), typeof(double), typeof(RangeMark), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MarkProperty = DependencyProperty.Register(
        nameof(Mark), typeof(Brush), typeof(RangeMark), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Share { get => (double)GetValue(ShareProperty); set => SetValue(ShareProperty, value); }
    public Brush Mark { get => (Brush)GetValue(MarkProperty); set => SetValue(MarkProperty, value); }

    private const double Track = 6, MarkWidth = 3, MarkHeight = 12;

    protected override Size MeasureOverride(Size available) => new(0, MarkHeight);

    protected override void OnRender(DrawingContext dc)
    {
        // The whole box takes the pointer, so its tooltip opens anywhere over it, not only on the thin track.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (ActualWidth < MarkWidth) return;
        double mid = ActualHeight / 2;
        var track = (Brush)Application.Current.FindResource("StrokeBrush");
        dc.DrawRoundedRectangle(track, null, new Rect(0, mid - Track / 2, ActualWidth, Track), Track / 2, Track / 2);
        double x = Math.Clamp(Share, 0, 100) / 100 * (ActualWidth - MarkWidth);
        dc.DrawRoundedRectangle(Mark, null, new Rect(x, mid - MarkHeight / 2, MarkWidth, MarkHeight), 1.5, 1.5);
    }
}
