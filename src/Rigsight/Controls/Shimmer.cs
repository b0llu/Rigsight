using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Rigsight.Controls;

/// <summary>
/// A soft band of light that sweeps across a button every few seconds (the plan to pick on the Plans screen, the person's
/// call 2026-10-24: "a slight shimmer so it gets attraction"). Set <c>ctl:Shimmer.Sweep="True"</c> on the band itself: it
/// runs only while the band is on screen and Windows' animation effects are on, so a closed screen costs nothing.
/// </summary>
public static class Shimmer
{
    public static readonly DependencyProperty SweepProperty = DependencyProperty.RegisterAttached("Sweep", typeof(bool), typeof(Shimmer),
        new PropertyMetadata(false, OnSweepChanged));

    public static bool GetSweep(DependencyObject d) => (bool)d.GetValue(SweepProperty);
    public static void SetSweep(DependencyObject d, bool value) => d.SetValue(SweepProperty, value);

    private static readonly TimeSpan Across = TimeSpan.FromSeconds(1.4), Every = TimeSpan.FromSeconds(4.2);

    private static void OnSweepChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement band) return;
        band.IsVisibleChanged -= Changed;
        band.SizeChanged -= Resized;
        if (e.NewValue is true)
        {
            band.IsVisibleChanged += Changed;
            band.SizeChanged += Resized;
            Update(band);
        }
        else Stop(band);
    }

    private static void Changed(object sender, DependencyPropertyChangedEventArgs e) => Update((FrameworkElement)sender);
    private static void Resized(object sender, SizeChangedEventArgs e) { if (e.WidthChanged) Update((FrameworkElement)sender); }

    private static void Update(FrameworkElement band)
    {
        if (!band.IsVisible || !Services.Motion.Enabled || band.Parent is not FrameworkElement host || host.ActualWidth <= 0)
        {
            Stop(band);
            return;
        }
        if (band.RenderTransform is not TranslateTransform move || move.IsFrozen) band.RenderTransform = move = new TranslateTransform();
        // From just off the left edge to just off the right one, then still until the next sweep.
        double from = -band.ActualWidth - 10, to = host.ActualWidth + 10;
        var sweep = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(1.2) };
        sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        sweep.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(Across), new SineEase { EasingMode = EasingMode.EaseInOut }));
        sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(to, KeyTime.FromTimeSpan(Every)));
        move.X = from;
        move.BeginAnimation(TranslateTransform.XProperty, sweep);
    }

    private static void Stop(FrameworkElement band)
    {
        if (band.RenderTransform is TranslateTransform { IsFrozen: false } move)
        {
            move.BeginAnimation(TranslateTransform.XProperty, null);
            move.X = -10_000;
        }
    }
}
