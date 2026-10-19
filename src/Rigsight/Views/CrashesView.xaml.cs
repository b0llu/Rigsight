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
    /// list has fewer rows than fit, it ends with the list. Its rows scroll inside it, and then the box ends halfway
    /// through a row, so it shows there is more under it (a box ending exactly on a row's edge looked like the whole list).
    /// </summary>
    private const double ListMinHeight = 560;

    private void FitList()
    {
        if (Scroller.ViewportHeight <= 0 || !ListCard.IsVisible) return;
        // Where the box starts, measured in the page's own content (the same wherever the page is scrolled to).
        double above = ListCard.TransformToAncestor(Layout).Transform(new Point(0, 0)).Y;
        double room = Math.Floor(Scroller.ViewportHeight - Layout.Margin.Top - Layout.Margin.Bottom - above);
        double height = HalfRow(Math.Max(ListMinHeight, room));
        if (double.IsNaN(ListCard.MaxHeight) || Math.Abs(ListCard.MaxHeight - height) > 0.5) ListCard.MaxHeight = height;
    }

    /// <summary>
    /// The box's height moved so its foot falls in the middle of a row, when the rows don't all fit: the row cut there
    /// is the sign to scroll. Shorter where it can be; longer only to stay above ListMinHeight.
    /// </summary>
    private double HalfRow(double height)
    {
        if (!ListScroller.IsVisible || ListItems.Items.Count == 0 || ListScroller.ActualHeight <= 0) return height;
        double chrome = ListCard.ActualHeight - ListScroller.ActualHeight;
        double view = height - chrome;
        if (chrome <= 0 || ListItems.ActualHeight <= view + 1) return height;
        var rows = new List<(double Top, double Height)>();
        for (int i = 0; i < ListItems.Items.Count; i++)
        {
            if (ListItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement row || row.ActualHeight <= 0) continue;
            rows.Add((row.TransformToAncestor(ListItems).Transform(new Point(0, 0)).Y, row.ActualHeight));
        }
        int at = rows.FindIndex(r => r.Top + r.Height > view);
        if (at < 0) return height;
        var (top, h) = rows[at];
        if (view >= top + h * 0.3 && view <= top + h * 0.7) return height; // already cut through the middle
        double cut = top + h * 0.5;
        if (cut + chrome < ListMinHeight && at + 1 < rows.Count) cut = rows[at + 1].Top + rows[at + 1].Height * 0.5;
        return Math.Floor(cut + chrome);
    }

    /// <summary>The wheel moves a long row of tabs sideways.</summary>
    private void Tabs_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (TabScroller.ScrollableWidth <= 0) return;
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }
}
