using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// A scrolling box inside a scrolling page: once the box can't go further the way the wheel turns (its end, its start,
/// or everything fits), the wheel scrolls the page, as it does anywhere else on it. (A ScrollViewer keeps the wheel to
/// itself even at its ends, so the page stood still until the mouse left the box.) Set on the ScrollViewer itself, or
/// on a list, whose own scrolling box is then the one meant. A box that was itself being scrolled a moment ago holds the
/// wheel for <see cref="Pause"/> after reaching its end, so the page doesn't jump on with the same turn of the wheel that
/// ran the box out.
/// </summary>
public static class WheelChain
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(WheelChain), new PropertyMetadata(false, OnChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement box) return;
        box.PreviewMouseWheel -= OnWheel;
        if (e.NewValue is true) box.PreviewMouseWheel += OnWheel;
    }

    /// <summary>How long after the box last moved under the wheel the page stays still.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(350);

    /// <summary>The time in milliseconds (tests give their own).</summary>
    internal static Func<long> Clock { get; set; } = () => Environment.TickCount64;

    // When the wheel last moved the box itself.
    private static readonly DependencyProperty MovedAtProperty = DependencyProperty.RegisterAttached(
        "MovedAt", typeof(long), typeof(WheelChain), new PropertyMetadata(long.MinValue / 2));

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0) return;
        if ((sender as ScrollViewer ?? Inner((DependencyObject)sender)) is not { } sv) return;
        bool canMove = e.Delta > 0 ? sv.VerticalOffset > 0.5 : sv.VerticalOffset < sv.ScrollableHeight - 0.5;
        long now = Clock();
        if (canMove)
        {
            sv.SetValue(MovedAtProperty, now);
            return;
        }
        if (VisualTreeHelper.GetParent((DependencyObject)sender) is not UIElement parent) return;
        e.Handled = true;
        if (now - (long)sv.GetValue(MovedAtProperty) < Pause.TotalMilliseconds) return;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sv });
    }

    /// <summary>A list's own scrolling box: the first one inside it.</summary>
    private static ScrollViewer? Inner(DependencyObject d)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var child = VisualTreeHelper.GetChild(d, i);
            if (child is ScrollViewer sv) return sv;
            if (Inner(child) is { } deeper) return deeper;
        }
        return null;
    }
}
