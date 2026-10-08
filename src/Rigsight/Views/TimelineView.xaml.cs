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
            if (_vm is null) return;
            _vm.JumpRequested += OnJump;
            _vm.Days.CollectionChanged += OnDaysChanged;
            // Coming back to the page: its first days are already laid out, so nothing scrolls or changes to ask for more.
            FillAhead();
        };
        // Leaving the page: back to the newest days, so opening it again lays out one page of them.
        Unloaded += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.JumpRequested -= OnJump;
                _vm.Days.CollectionChanged -= OnDaysChanged;
            }
            // As many as fill the window and the screen ahead of it: fewer, and coming back would build the rest again.
            int keep = 0;
            for (double y = 0; _vm is not null && keep < _vm.Days.Count && y <= Scroller.ViewportHeight + Ahead; keep++)
                y += (List.ItemContainerGenerator.ContainerFromIndex(keep) as FrameworkElement)?.ActualHeight ?? 0;
            Scroller.ScrollToTop();
            _vm?.ShowFirstPage(keep);
            _vm = null;
        };
    }

    private void OnDaysChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => FillAhead();

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
    /// Builds older days while the list ends within a screen of what's in view (or doesn't fill it yet): a few at a time
    /// and only once the window has nothing else to do, so scrolling, a click or a new filter never waits for them.
    /// Asked for whenever that can have changed: the page shown, the list scrolled, days added or replaced.
    /// </summary>
    private void FillAhead()
    {
        if (_vm is null || _morePending || !_vm.HasMore) return;
        _morePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _morePending = false;
            // Laid out by now, so the list's real height is known. The days this adds ask again.
            if (_vm is { HasMore: true } vm && Scroller.ScrollableHeight - Scroller.VerticalOffset <= Ahead) vm.ShowMoreCommand.Execute(null);
        });
    }

    /// <summary>The calendar follows the month at the top of the list.</summary>
    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_vm is null) return;
        FillAhead();
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
