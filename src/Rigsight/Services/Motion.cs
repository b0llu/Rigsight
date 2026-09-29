using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Rigsight.Services;

/// <summary>
/// The app's motion: short, eased, and only on a moment (a card over the window, a hover, a switch), never running on
/// its own, so an open window costs nothing more while it sits there. Only opacity and position move (nothing is laid
/// out again), and every animation lets go of what it animated when it ends, so nothing is held in memory. Off when
/// Windows' "Animation effects" is off (Settings → Accessibility → Visual effects), as Windows' own apps are.
/// Pages themselves appear at once: a fade on every page switch was tried (0.2 s) and got in the way.
/// </summary>
public static class Motion
{
    /// <summary>Whether to animate at all: Windows' own setting for it.</summary>
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    public static readonly IEasingFunction Ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    private static readonly Duration CardDuration = new(TimeSpan.FromMilliseconds(260));

    /// <summary>
    /// The durations the controls' hover and press animations use (Theme.xaml: MotionFast, MotionNormal): set to nothing
    /// when animations are off, so the styles need no conditions of their own. Call once, before any window.
    /// </summary>
    public static void ApplyDurations(ResourceDictionary resources)
    {
        if (Enabled) return;
        // Set where they're defined (Theme.xaml, merged in): its styles find their own dictionary's values first.
        foreach (var dictionary in resources.MergedDictionaries.Append(resources))
            foreach (var key in new[] { "MotionFast", "MotionNormal" })
                if (dictionary.Contains(key)) dictionary[key] = new Duration(TimeSpan.Zero);
    }

    /// <summary>A card over the window opening: fades in and grows from just under its size.</summary>
    public static void PopIn(FrameworkElement element)
    {
        if (!Enabled) return;
        var scale = new ScaleTransform(0.96, 0.96);
        var original = element.RenderTransform;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = scale;
        var grow = new DoubleAnimation(0.96, 1, CardDuration) { EasingFunction = Ease, FillBehavior = FillBehavior.Stop };
        grow.Completed += (_, _) => element.RenderTransform = original;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow.Clone());
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, CardDuration) { EasingFunction = Ease, FillBehavior = FillBehavior.Stop });
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
