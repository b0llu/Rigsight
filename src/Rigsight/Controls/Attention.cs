using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// Names a part of a scrolling page (Attention.Section="sensors") so the page can be opened at it: Settings scrolls to the
/// section its view model says needs attention, instead of opening at the top where the user may never scroll to it.
/// </summary>
public static class Attention
{
    public static readonly DependencyProperty SectionProperty =
        DependencyProperty.RegisterAttached("Section", typeof(string), typeof(Attention), new PropertyMetadata(null));

    public static string? GetSection(DependencyObject d) => (string?)d.GetValue(SectionProperty);
    public static void SetSection(DependencyObject d, string? value) => d.SetValue(SectionProperty, value);

    /// <summary>The element named <paramref name="section"/> inside <paramref name="root"/>, or null.</summary>
    public static FrameworkElement? Find(DependencyObject root, string section)
    {
        if (root is FrameworkElement fe && GetSection(fe) == section) return fe;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find(VisualTreeHelper.GetChild(root, i), section) is { } found) return found;
        return null;
    }

    /// <summary>
    /// Scrolls so the section sits in the middle of <paramref name="viewer"/>, with the page around it for context. A section
    /// taller than the view is lined up at the top instead (a little below it), so its beginning is never cut off.
    /// False if it isn't there.
    /// </summary>
    public static bool ScrollTo(ScrollViewer viewer, string section, double margin = 12)
    {
        if (Find(viewer, section) is not { } target || !target.IsVisible) return false;
        viewer.UpdateLayout();
        double offset = CenteredOffset(viewer.VerticalOffset, target.TransformToAncestor(viewer).Transform(new Point(0, 0)).Y,
            target.ActualHeight, viewer.ViewportHeight, margin);
        viewer.ScrollToVerticalOffset(Math.Clamp(offset, 0, viewer.ScrollableHeight));
        return true;
    }

    /// <summary>The scroll offset that centres a section at <paramref name="top"/> (relative to the view) of this height.</summary>
    internal static double CenteredOffset(double current, double top, double height, double viewport, double margin) =>
        height + 2 * margin >= viewport
            ? current + top - margin
            : current + top - (viewport - height) / 2;
}
