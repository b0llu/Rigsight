using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Rigsight.Services;

namespace Rigsight.Controls;

/// <summary>
/// A button's text and icon go from the muted colour to the full one in step with its shade: under the pointer, and
/// (for one that can be picked: a sidebar item, a segment) when it is picked. Set by a trigger, the text is at full
/// white at once while the shade behind it is still fading in, and on the way out goes dim at once while the shade
/// lingers: the two out of step read as a stutter. The rule for the whole app: wherever a control's shade fades, its
/// text fades with it, through this.
/// </summary>
public static class NavInk
{
    public static readonly DependencyProperty FollowsProperty = DependencyProperty.RegisterAttached(
        "Follows", typeof(bool), typeof(NavInk), new PropertyMetadata(false, OnFollowsChanged));

    public static bool GetFollows(DependencyObject d) => (bool)d.GetValue(FollowsProperty);
    public static void SetFollows(DependencyObject d, bool value) => d.SetValue(FollowsProperty, value);

    /// <summary>The palette brush the text takes when the control is picked ("TextBrush" unless said otherwise).</summary>
    public static readonly DependencyProperty CheckedProperty = DependencyProperty.RegisterAttached(
        "Checked", typeof(string), typeof(NavInk), new PropertyMetadata("TextBrush"));

    public static string GetChecked(DependencyObject d) => (string)d.GetValue(CheckedProperty);
    public static void SetChecked(DependencyObject d, string value) => d.SetValue(CheckedProperty, value);

    /// <summary>The control's own brush (the palette's are shared and frozen: they can't be faded).</summary>
    private static readonly DependencyProperty InkProperty = DependencyProperty.RegisterAttached("Ink", typeof(SolidColorBrush), typeof(NavInk));

    private static void OnFollowsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase item || e.NewValue is not true) return;
        Action themed = () => Apply(item, hovered: false, animate: false);
        item.Loaded += (_, _) =>
        {
            ThemeManager.Changed -= themed;
            ThemeManager.Changed += themed;
            Apply(item, hovered: false, animate: false);
        };
        item.Unloaded += (_, _) => ThemeManager.Changed -= themed;
        if (item is ToggleButton toggle)
        {
            toggle.Checked += (_, _) => Apply(item, hovered: false, animate: true);
            toggle.Unchecked += (_, _) => Apply(item, hovered: false, animate: true);
        }
        item.MouseEnter += (_, _) => Apply(item, hovered: true, animate: true);
        item.MouseLeave += (_, _) => Apply(item, hovered: false, animate: true);
        Apply(item, hovered: false, animate: false);
    }

    /// <summary>The palette brush for how the control stands now: picked, under the pointer, or at rest.</summary>
    private static string KeyOf(ButtonBase item) =>
        item is ToggleButton { IsChecked: true } ? GetChecked(item) : item.IsMouseOver ? "TextBrush" : "MutedBrush";

    /// <param name="hovered">The pointer just came over it: as quick as its shade comes in (the other changes take the slower time).</param>
    private static void Apply(ButtonBase item, bool hovered, bool animate)
    {
        string key = KeyOf(item);
        if (item.TryFindResource(key) is not SolidColorBrush palette) return;
        var target = palette.Color;
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
        // Kept as the brush's own colour once it's there, so the animation isn't held on to: unless the control has
        // since been sent another way (that animation is the one running now).
        fade.Completed += (_, _) =>
        {
            if (KeyOf(item) != key) return;
            ink.BeginAnimation(SolidColorBrush.ColorProperty, null);
            ink.Color = target;
        };
        ink.BeginAnimation(SolidColorBrush.ColorProperty, fade);
    }
}
