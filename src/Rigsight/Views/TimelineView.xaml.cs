using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Rigsight.ViewModels;

namespace Rigsight.Views;

public partial class TimelineView : UserControl
{
    private TimelineViewModel? _vm;

    public TimelineView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _vm = DataContext as TimelineViewModel;
            if (_vm is not null) _vm.JumpRequested += OnJump;
        };
        // Leaving the page: back to the newest days, so opening it again lays out one page of them.
        Unloaded += (_, _) =>
        {
            if (_vm is not null) _vm.JumpRequested -= OnJump;
            Scroller.ScrollToTop();
            _vm?.ShowFirstPage();
            _vm = null;
        };
    }

    /// <summary>Scrolls to a day picked in the calendar, once its row is laid out (it may just have been built).</summary>
    private void OnJump(TimelineDay? day)
    {
        if (day is null)
        {
            Scroller.ScrollToTop();
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (List.ItemContainerGenerator.ContainerFromItem(day) is not FrameworkElement row) return;
            Scroller.ScrollToVerticalOffset(row.TransformToAncestor(List).Transform(new Point(0, 0)).Y);
        });
    }

    /// <summary>How close to the bottom (in pixels) before older days are built: a screen ahead, so they're there in time.</summary>
    private const double Ahead = 900;
    private bool _morePending;

    /// <summary>
    /// Near the bottom, older days are built: a few at a time and only once the window has nothing else to do, so
    /// scrolling, a click or a new filter never waits for them. The calendar follows the month at the top of the list.
    /// </summary>
    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_vm is null) return;
        if (!_morePending && _vm.HasMore && Scroller.ExtentHeight > 0 && Scroller.ScrollableHeight - Scroller.VerticalOffset <= Ahead)
        {
            _morePending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                _morePending = false;
                _vm?.ShowMoreCommand.Execute(null);
            });
        }
        if (e.VerticalChange == 0) return;
        double y = 0;
        for (int i = 0; i < _vm.Days.Count; i++)
        {
            if (List.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement row) return;
            y += row.ActualHeight;
            if (y > e.VerticalOffset + 8)
            {
                _vm.OnTopDay(_vm.Days[i].Day);
                return;
            }
        }
    }
}

/// <summary>Picks a timeline line's template: plain for most, so a long list stays cheap to build.</summary>
public sealed class TimelineEntryTemplates : DataTemplateSelector
{
    public DataTemplate? Plain { get; set; }
    public DataTemplate? Problem { get; set; }
    public DataTemplate? Rich { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is not TimelineEntry entry ? Plain : entry.IsProblem ? Problem : entry.HasChildren || entry.Effect.Count > 0 ? Rich : Plain;
}
