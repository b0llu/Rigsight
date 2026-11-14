using System.Windows;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// A bar of two parts in one (Storage's "What reads and writes": read, then written): a track, and on it one filled bar
/// rounded only at its two ends, the first colour up to <see cref="First"/> and the second for <see cref="Second"/> after
/// it, with a straight cut between (two rounded pieces side by side looked like two pills stuck together on a short bar,
/// the user 2026-11-14).
/// </summary>
public sealed class SplitBar : FrameworkElement
{
    public static readonly DependencyProperty FirstProperty = DependencyProperty.Register(
        nameof(First), typeof(double), typeof(SplitBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SecondProperty = DependencyProperty.Register(
        nameof(Second), typeof(double), typeof(SplitBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FirstBrushProperty = DependencyProperty.Register(
        nameof(FirstBrush), typeof(Brush), typeof(SplitBar), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SecondBrushProperty = DependencyProperty.Register(
        nameof(SecondBrush), typeof(Brush), typeof(SplitBar), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(SplitBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The first part's share of the whole track (0 to 1).</summary>
    public double First { get => (double)GetValue(FirstProperty); set => SetValue(FirstProperty, value); }
    /// <summary>The second part's share of the whole track (0 to 1).</summary>
    public double Second { get => (double)GetValue(SecondProperty); set => SetValue(SecondProperty, value); }
    public Brush FirstBrush { get => (Brush)GetValue(FirstBrushProperty); set => SetValue(FirstBrushProperty, value); }
    public Brush SecondBrush { get => (Brush)GetValue(SecondBrushProperty); set => SetValue(SecondBrushProperty, value); }
    public Brush? Track { get => (Brush?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override Size MeasureOverride(Size available) => new(0, 8);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, r = h / 2;
        if (w <= 0 || h <= 0) return;
        if (Track is { } track) dc.DrawRoundedRectangle(track, null, new Rect(0, 0, w, h), r, r);
        double first = Math.Clamp(First, 0, 1) * w, second = Math.Clamp(Second, 0, 1 - Math.Clamp(First, 0, 1)) * w;
        double filled = first + second;
        if (filled <= 0) return;
        // The filled part is one rounded bar: its two colours are cut from it, so only its outer ends are round.
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, filled, h), Math.Min(r, filled / 2), r));
        if (first > 0) dc.DrawRectangle(FirstBrush, null, new Rect(0, 0, first, h));
        if (second > 0) dc.DrawRectangle(SecondBrush, null, new Rect(first, 0, second, h));
        dc.Pop();
    }
}
