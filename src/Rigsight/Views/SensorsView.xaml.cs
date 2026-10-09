using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Rigsight.Views;

public partial class SensorsView : UserControl
{
    public SensorsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => { if (_live is not null) _live.Revealed -= ScrollTo; };
        Loaded += (_, _) => { if (_live is not null) { _live.Revealed -= ScrollTo; _live.Revealed += ScrollTo; } };
        SizeChanged += (_, e) => Fit(e.NewSize.Width);
    }

    // ── A narrow window: fewer columns, then no pane, never rows cut off at the right ──

    /// <summary>The rows' lowest, highest and average columns (and each card's count of sensors) have room.</summary>
    public static readonly DependencyProperty ShowStatsProperty = DependencyProperty.Register(nameof(ShowStats), typeof(bool), typeof(SensorsView), new PropertyMetadata(true));
    public bool ShowStats { get => (bool)GetValue(ShowStatsProperty); set => SetValue(ShowStatsProperty, value); }

    /// <summary>The rows' last-minute line has room.</summary>
    public static readonly DependencyProperty ShowSparkProperty = DependencyProperty.Register(nameof(ShowSpark), typeof(bool), typeof(SensorsView), new PropertyMetadata(true));
    public bool ShowSpark { get => (bool)GetValue(ShowSparkProperty); set => SetValue(ShowSparkProperty, value); }

    // The page's side margins; the pane with its gap; a row with every column and the list's scrollbar strip; the same
    // without the three figures; the least the list is left beside the pane.
    internal const double Sides = 64, PaneWidth = 320, FullRow = 728, RowWithSpark = 500, LeastList = 420;

    /// <summary>What a page this wide shows: the pane while the list keeps a readable width beside it, and the columns the list then has room for.</summary>
    internal static (bool Pane, bool Stats, bool Spark) Fits(double width)
    {
        bool pane = width - Sides - PaneWidth >= LeastList;
        double list = width - Sides - (pane ? PaneWidth : 0);
        return (pane, list >= FullRow, list >= RowWithSpark);
    }

    private void Fit(double width)
    {
        var (pane, stats, spark) = Fits(width);
        Pane.Visibility = pane ? Visibility.Visible : Visibility.Collapsed;
        (ShowStats, ShowSpark) = (stats, spark);
    }

    // ── A group's header: click to collapse or expand, drag to move the group ──

    private Point? _pressedAt;

    private void Header_MouseDown(object sender, MouseButtonEventArgs e) => _pressedAt = e.GetPosition(this);

    private void Header_Click(object sender, MouseButtonEventArgs e)
    {
        if (_pressedAt is null) return; // the end of a drag, not a click
        _pressedAt = null;
        if (sender is FrameworkElement { DataContext: Models.HardwareNode node } && DataContext is ViewModels.LiveData live)
            live.ToggleExpandedCommand.Execute(node);
    }

    private void Header_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedAt is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var moved = e.GetPosition(this) - start;
        if (Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance * 2 && Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance * 2) return;
        if (sender is not FrameworkElement { DataContext: Models.HardwareNode node } || DataContext is not ViewModels.LiveData live) return;

        _pressedAt = null;
        // Every group shrinks to its header while dragging, so any place is in reach; they open again on drop.
        node.IsBeingMoved = true;
        live.IsReordering = true;
        try { DragDrop.DoDragDrop(SensorList, node, DragDropEffects.Move); }
        finally
        {
            live.IsReordering = false;
            node.IsBeingMoved = false;
        }
    }

    /// <summary>Hovering over another group moves the dragged one there straight away.</summary>
    private void List_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (e.Data.GetData(typeof(Models.HardwareNode)) is not Models.HardwareNode node || DataContext is not ViewModels.LiveData live) return;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is FrameworkElement { DataContext: var row } && live.GroupOf(row) is { } target)
            {
                live.MoveHardware(node, target);
                return;
            }
        }
    }

    private void List_Drop(object sender, DragEventArgs e) => e.Handled = true;

    // ── A sensor's menu: where it is shown (each a tick to switch), copying its value, hiding it ──

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Models.SensorItem item } button || DataContext is not ViewModels.LiveData live) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "Show it on", IsEnabled = false });
        foreach (var place in live.PlacesFor(item))
        {
            var entry = new MenuItem { Header = place.CanToggle ? place.Name : place.Name + " (full)", IsCheckable = true, IsChecked = place.IsShown, IsEnabled = place.CanToggle };
            entry.Click += (_, _) => place.ToggleCommand.Execute(null);
            menu.Items.Add(entry);
        }
        menu.Items.Add(new Separator());
        var copy = new MenuItem { Header = "Copy value" };
        copy.Click += (_, _) => live.Copy(item);
        menu.Items.Add(copy);
        var hide = new MenuItem { Header = item.IsHidden ? "Show in the list" : "Hide" };
        hide.Click += (_, _) => item.ToggleHidden();
        menu.Items.Add(hide);
        menu.IsOpen = true;
    }

    // ── A line of the "Right now" pane was clicked: scroll the list to that sensor's row ──

    private ViewModels.LiveData? _live;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_live is not null) _live.Revealed -= ScrollTo;
        _live = DataContext as ViewModels.LiveData;
        if (_live is not null) _live.Revealed += ScrollTo;
    }

    private void ScrollTo(Models.SensorItem item)
    {
        if (_live is null) return;
        int index = _live.SensorRows.IndexOf(item);
        if (index < 0) return;
        // The list builds only the rows on screen: its panel is asked for the row by its place in the list.
        Dispatcher.BeginInvoke(() =>
        {
            if (Find<VirtualizingStackPanel>(SensorList) is not { } panel) return;
            panel.BringIndexIntoViewPublic(index);
            // That puts the row at the nearest edge (the bottom, coming from above). Once it is built, move it to the
            // middle of the list, where the eye lands.
            Dispatcher.BeginInvoke(() =>
            {
                if (SensorList.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement row
                    || Find<ScrollViewer>(SensorList) is not { } scroll || !row.IsDescendantOf(scroll)) return;
                double top = row.TransformToAncestor(scroll).Transform(new Point(0, 0)).Y;
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + top - (scroll.ViewportHeight - row.ActualHeight) / 2);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (Find<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;

        if (e.Key == Key.Enter)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }
}
