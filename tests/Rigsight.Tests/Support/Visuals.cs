using System.Windows;
using System.Windows.Media;

namespace Rigsight.Tests.Support;

public static class Visuals
{
    /// <summary>Every descendant of <paramref name="root"/> in the visual tree of type <typeparamref name="T"/>, depth first.</summary>
    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }
}
