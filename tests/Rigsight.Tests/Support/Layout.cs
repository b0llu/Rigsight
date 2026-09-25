using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Rigsight.Tests.Support;

/// <summary>Layout checks on a rendered view.</summary>
public static class Layout
{
    /// <summary>
    /// Visible text whose right edge lies beyond <paramref name="root"/>'s (so part of it is cut off). Text inside
    /// something that scrolls sideways on purpose is skipped.
    /// </summary>
    public static List<string> CutOffText(FrameworkElement root)
    {
        var found = new List<string>();
        void Walk(DependencyObject node)
        {
            if (node is ScrollViewer { HorizontalScrollBarVisibility: not ScrollBarVisibility.Disabled } sv && sv.ScrollableWidth > 0) return;
            if (node is UIElement { IsVisible: false }) return;
            if (node is TextBlock { ActualWidth: > 0 } text && !string.IsNullOrWhiteSpace(text.Text))
            {
                var right = text.TransformToAncestor(root).TransformBounds(new Rect(0, 0, text.ActualWidth, text.ActualHeight)).Right;
                if (right > root.ActualWidth + 1) found.Add($"\"{text.Text}\" ends at {right:0} of {root.ActualWidth:0}");
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        return found;
    }
}
