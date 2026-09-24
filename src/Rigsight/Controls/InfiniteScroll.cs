using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Rigsight.Controls;

/// <summary>
/// Infinite scrolling for a page's ScrollViewer: runs <c>LoadMore</c> when the view gets near the bottom, and again
/// if the content added is still too short to fill it. The command decides whether there is anything left.
/// </summary>
public static class InfiniteScroll
{
    /// <summary>How close to the bottom (in pixels) before more is loaded: a few cards ahead, so it's there in time.</summary>
    private const double Threshold = 900;

    public static readonly DependencyProperty LoadMoreProperty = DependencyProperty.RegisterAttached(
        "LoadMore", typeof(ICommand), typeof(InfiniteScroll), new PropertyMetadata(null, OnChanged));

    public static ICommand? GetLoadMore(DependencyObject d) => (ICommand?)d.GetValue(LoadMoreProperty);
    public static void SetLoadMore(DependencyObject d, ICommand? value) => d.SetValue(LoadMoreProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        sv.ScrollChanged -= OnScrollChanged;
        if (e.NewValue is not null) sv.ScrollChanged += OnScrollChanged;
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        // Nothing to scroll yet (still loading), or not near the end.
        if (sv.ExtentHeight <= 0 || sv.ScrollableHeight - sv.VerticalOffset > Threshold) return;
        if (GetLoadMore(sv) is { } cmd && cmd.CanExecute(null)) cmd.Execute(null);
    }
}
