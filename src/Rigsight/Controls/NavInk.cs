using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Rigsight.Services;

namespace Rigsight.Controls;

/// <summary>
/// A sidebar item's text and icon go from the muted colour to the full one in step with its shade: under the pointer,
/// and when its page is picked. Set by a trigger the text was at full white at once while the shade behind it was still
/// fading in (later still when the page being opened held the window up), and for that moment looked switched on by itself.
/// </summary>
public static class NavInk
{
    public static readonly DependencyProperty FollowsProperty = DependencyProperty.RegisterAttached(
        "Follows", typeof(bool), typeof(NavInk), new PropertyMetadata(false, OnFollowsChanged));

    public static bool GetFollows(DependencyObject d) => (bool)d.GetValue(FollowsProperty);
    public static void SetFollows(DependencyObject d, bool value) => d.SetValue(FollowsProperty, value);

    /// <summary>The item's own brush (the palette's are shared and frozen: they can't be faded).</summary>
    private static readonly DependencyProperty InkProperty = DependencyProperty.RegisterAttached("Ink", typeof(SolidColorBrush), typeof(NavInk));

    private static void OnFollowsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ToggleButton item || e.NewValue is not true) return;
        Action themed = () => Apply(item, hovered: false, animate: false);
        item.Loaded += (_, _) =>
        {
            ThemeManager.Changed -= themed;
            ThemeManager.Changed += themed;
            Apply(item, hovered: false, animate: false);
        };
        item.Unloaded += (_, _) => ThemeManager.Changed -= themed;
        item.Checked += (_, _) => Apply(item, hovered: false, animate: true);
        item.Unchecked += (_, _) => Apply(item, hovered: false, animate: true);
        item.MouseEnter += (_, _) => Apply(item, hovered: true, animate: true);
        item.MouseLeave += (_, _) => Apply(item, hovered: false, animate: true);
        Apply(item, hovered: false, animate: false);
    }

    /// <param name="hovered">The pointer just came over it: as quick as its shade comes in (the other changes take the slower time).</param>
    private static void Apply(ToggleButton item, bool hovered, bool animate)
    {
        bool lit = item.IsChecked == true || item.IsMouseOver;
        if (item.TryFindResource(lit ? "TextColor" : "MutedColor") is not Color target) return;
        if (item.GetValue(InkProperty) is not SolidColorBrush ink)
        {
            ink = new SolidColorBrush(target);
            item.SetValue(InkProperty, ink);
            item.Foreground = ink;
        }
        var time = item.TryFindResource(hovered ? "MotionFast" : "MotionNormal") is Duration { HasTimeSpan: true } d ? d.TimeSpan : TimeSpan.Zero;
        if (!animate || time == TimeSpan.Zero || !item.IsLoaded)
        {
            ink.BeginAnimation(SolidColorBrush.ColorProperty, null);
            ink.Color = target;
            return;
        }
        var fade = new ColorAnimation(target, time) { EasingFunction = hovered ? null : item.TryFindResource("MotionEase") as IEasingFunction };
        // Kept as the brush's own colour once it's there, so the animation isn't held on to: unless the item has since
        // been sent the other way (that animation is the one running now).
        fade.Completed += (_, _) =>
        {
            bool still = item.IsChecked == true || item.IsMouseOver;
            if (still != lit) return;
            ink.BeginAnimation(SolidColorBrush.ColorProperty, null);
            ink.Color = target;
        };
        ink.BeginAnimation(SolidColorBrush.ColorProperty, fade);
    }
}
