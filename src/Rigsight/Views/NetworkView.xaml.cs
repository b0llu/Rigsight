using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Rigsight.ViewModels;

namespace Rigsight.Views;

public partial class NetworkView : UserControl
{
    /// <summary>The graph's height.</summary>
    public const double ChartHeight = 320;

    /// <summary>The Apps and What happened cards' height: side by side at one height, each scrolling inside.</summary>
    public const double ListHeight = 560;

    public NetworkView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is NetworkViewModel old) old.Happenings.CollectionChanged -= OnHappenings;
            if (e.NewValue is NetworkViewModel vm) vm.Happenings.CollectionChanged += OnHappenings;
        };
    }

    private bool _scrollQueued;

    /// <summary>The lines are made anew when a bar is picked: once they're laid out, the first lit one is scrolled to.</summary>
    private void OnHappenings(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_scrollQueued) return;
        _scrollQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ShowPicked);
    }

    /// <summary>
    /// Scrolls What happened, and only it, to its first lit line when that line is out of sight. Not BringIntoView: that
    /// scrolls the page too, and a click on the graph would move the page under the pointer.
    /// </summary>
    private void ShowPicked()
    {
        _scrollQueued = false;
        if (DataContext is not NetworkViewModel vm || vm.Happenings.FirstOrDefault(r => r.IsPicked) is not { } row) return;
        if (HappeningsList.ItemContainerGenerator.ContainerFromItem(row) is not FrameworkElement line || !line.IsVisible) return;
        double top = line.TransformToAncestor(HappeningsList).Transform(new Point(0, 0)).Y;
        double bottom = top + line.ActualHeight, offset = HappeningsScroll.VerticalOffset, seen = HappeningsScroll.ViewportHeight;
        if (top >= offset && bottom <= offset + seen) return;
        // A little of the line before it shows above, so it doesn't sit hard against the card's edge.
        HappeningsScroll.ScrollToVerticalOffset(Math.Max(0, top - 24));
    }
}
