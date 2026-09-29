using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Rigsight.Services;

/// <summary>
/// The app's motion: short, eased, and only on a moment (a page opening, a card over the window, a hover), never running on
/// its own, so an open window costs nothing more while it sits there. Only opacity and position move (nothing is laid
/// out again), and every animation lets go of what it animated when it ends, so nothing is held in memory. Off when
/// Windows' "Animation effects" is off (Settings → Accessibility → Visual effects), as Windows' own apps are.
/// </summary>
public static class Motion
{
    /// <summary>Whether to animate at all: Windows' own setting for it.</summary>
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    public static readonly IEasingFunction Ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    private static readonly Duration PageDuration = new(TimeSpan.FromMilliseconds(200));
    private static readonly Duration CardDuration = new(TimeSpan.FromMilliseconds(260));
    private const double PageRise = 8;

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

    /// <summary>
    /// A page coming into view: the host fades in and rises a little, as one layer. A page seen for the first time is
    /// built and drawn first (that takes a moment and holds the window, and a fade run meanwhile would be over before
    /// anything showed): it stays hidden until the window is idle again, then fades in. (Its cards one after another was
    /// tried: fading many chart cards at once cost Reports' first opening half a second of CPU, measured.)
    /// </summary>
    public static void PageIn(FrameworkElement host, FrameworkElement page)
    {
        if (!Enabled) return;
        if (page.IsLoaded)
        {
            Fade(host);
            return;
        }
        host.Opacity = 0;
        host.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => Fade(host));
    }

    private static void Fade(FrameworkElement host)
    {
        if (host.RenderTransform is not TranslateTransform rise || rise.IsFrozen)
            host.RenderTransform = rise = new TranslateTransform();
        // The page is drawn live through the fade (about 40 ms more CPU on a page switch, measured). Drawing it once into
        // a picture (BitmapCache) saved that but kept about 15 MB more memory for good: not worth it.
        // From a value, and FillBehavior.Stop: at the end the animations let go and the host is back to its own values
        // (fully shown: set here, under the animation, so a hidden host never stays hidden).
        host.Opacity = 1;
        host.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, PageDuration) { EasingFunction = Ease, FillBehavior = FillBehavior.Stop });
        rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(PageRise, 0, PageDuration) { EasingFunction = Ease, FillBehavior = FillBehavior.Stop });
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
