using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Rigsight.Views;

public partial class CrashesView : UserControl
{
    public CrashesView()
    {
        InitializeComponent();
        // Leaving the page: back to the newest crashes, so opening it again lays out one page of rows, not every row
        // scrolled into view (a hundred and more froze the window on each visit).
        Unloaded += (_, _) =>
        {
            Scroller.ScrollToTop();
            ListScroller.ScrollToTop();
            (DataContext as ViewModels.CrashesViewModel)?.ShowFirstPage();
            // …and back to the list: a crash left open would be what "Crashes" in the sidebar shows next time.
            (DataContext as ViewModels.CrashesViewModel)?.CloseCaseCommand.Execute(null);
        };
        // A crash opened is its own page: it starts at its top, and the list keeps its place for the way back.
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ViewModels.CrashesViewModel old) old.PropertyChanged -= OnChanged;
            if (e.NewValue is ViewModels.CrashesViewModel vm) vm.PropertyChanged += OnChanged;
        };
        Scroller.SizeChanged += (_, _) => FitList();
        Layout.SizeChanged += (_, _) => FitList();
        // The cards above come and go with the period (the timeline, what they have in common) without the page's
        // width or the window changing: the box is fitted again once each layout has settled.
        ListCard.SizeChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FitList);
        IsVisibleChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FitList);
    }

    private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ViewModels.CrashesViewModel.Case)) return;
        CaseScroller.ScrollToTop();
        // The tab of the time shown, where the row has more times than fit across.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if ((sender as ViewModels.CrashesViewModel)?.CaseTabs.FirstOrDefault(t => t.IsSelected) is { } tab)
                (Tabs.ItemContainerGenerator.ContainerFromItem(tab) as FrameworkElement)?.BringIntoView();
        });
    }

    /// <summary>
    /// The list's box, by the user's rule: never shorter than ListMinHeight; where the window has more room than that
    /// under the cards above, it reaches down to the window's foot (so the page itself needn't scroll); and where the
    /// list has fewer rows than fit, it ends with the list. Its rows scroll inside it.
    /// </summary>
    private const double ListMinHeight = 440;

    private void FitList()
    {
        if (Scroller.ViewportHeight <= 0 || !ListCard.IsVisible) return;
        // Where the box starts, measured in the page's own content (the same wherever the page is scrolled to).
        double above = ListCard.TransformToAncestor(Layout).Transform(new Point(0, 0)).Y;
        double room = Math.Floor(Scroller.ViewportHeight - Layout.Margin.Top - Layout.Margin.Bottom - above);
        double height = Math.Max(ListMinHeight, room);
        if (double.IsNaN(ListCard.MaxHeight) || Math.Abs(ListCard.MaxHeight - height) > 0.5) ListCard.MaxHeight = height;
    }

    /// <summary>The wheel moves a long row of tabs sideways.</summary>
    private void Tabs_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (TabScroller.ScrollableWidth <= 0) return;
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }
}
