using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core;

namespace Rigsight.Views;

public partial class SensorsView : UserControl
{
    public SensorsView()
    {
        InitializeComponent();
        ((Controls.Proxy)Resources["View"]).Data = this;
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) =>
        {
            if (_live is not null) _live.Revealed -= ScrollTo;
            // Left while scrolling: the lines are drawn again when the page is back.
            _scrollRest?.Stop();
            SensorList.ClearValue(Controls.Sparkline.HoldProperty);
        };
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
    // The tag and name columns together keep the 188 these were set with (see Controls.KindTag.NameMinWidth).
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

    /// <summary>A device card has nothing to open: a press and release there is no drag, and nothing more.</summary>
    private void Card_MouseUp(object sender, MouseButtonEventArgs e) => _pressedAt = null;

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

        // Every item has an icon (each place its page's from the sidebar), so their words line up, as in the Apps and
        // Processes menus; a place the sensor is on has a tick at its right instead of Windows' tick, which went where
        // the icon is.
        static MenuItem Item(object header, string glyph) => new()
        {
            Header = header,
            Icon = new TextBlock { Text = glyph, Style = (Style)Application.Current.FindResource("Icon"), FontSize = Controls.IconSize.S14 },
            FocusVisualStyle = null,
        };

        // The heading too (the eye, as on each row of the list): without one, its words stood left of everything under it.
        var heading = Item(Lang.T("Show it on"), "");
        heading.IsEnabled = false;
        menu.Items.Add(heading);
        foreach (var place in live.PlacesFor(item))
        {
            var header = new DockPanel { LastChildFill = true, MinWidth = 150 };
            if (place.IsShown)
            {
                var tick = new TextBlock { Text = "", Style = (Style)Application.Current.FindResource("Icon"), FontSize = Controls.IconSize.S12, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                tick.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                DockPanel.SetDock(tick, Dock.Right);
                header.Children.Add(tick);
            }
            header.Children.Add(new TextBlock { Text = place.CanToggle ? place.Name : Lang.T($"{place.Name} (full)") });
            var entry = Item(header, place.Glyph);
            entry.IsEnabled = place.CanToggle;
            AutomationProperties.SetName(entry, place.IsShown ? Lang.T($"{place.Name}, on") : place.Name);
            entry.Click += (_, _) => place.ToggleCommand.Execute(null);
            menu.Items.Add(entry);
        }
        menu.Items.Add(new Separator());
        var copy = Item(Lang.T("Copy value"), "");
        copy.Click += (_, _) => live.Copy(item);
        menu.Items.Add(copy);
        var hide = Item(item.IsHidden ? Lang.T("Show in the list") : Lang.T("Hide"), item.IsHidden ? "" : "");
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
        // A device's battery is inside its card (1.22.0): the card is the row to bring into view.
        if (index < 0) index = _live.SensorRows.FindIndex(r => r is Models.DeviceCard c && c.Battery == item);
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

    // ── Renaming a sensor: the box is put in the row only while renaming (1.23.0) ──

    /// <summary>
    /// A click on a sensor's name: the rename box takes the name's place, focused, its caret at the end. Enter or a click
    /// elsewhere keeps the new name, Escape keeps the old one; either way the plain name comes back. Each row used to
    /// carry a box of its own, built with the row, on every row of a long list.
    /// </summary>
    private void Name_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: Models.SensorItem item, Parent: Grid row } shown) return;
        e.Handled = true;
        var box = new TextBox { Style = (Style)FindResource("InlineEdit"), Text = item.Label, ToolTip = item.NameTip };
        Grid.SetColumn(box, Grid.GetColumn(shown));
        bool done = false;
        void Finish(bool keep)
        {
            if (done) return;
            done = true;
            if (keep) item.Label = box.Text;
            row.Children.Remove(box);
            shown.Visibility = Visibility.Visible;
            row.DataContextChanged -= Handed;
        }
        // The row handed another sensor (scrolled away and reused) while renaming: the rename so far is kept for its own sensor.
        void Handed(object? s, DependencyPropertyChangedEventArgs a) => Finish(keep: true);
        box.KeyDown += (_, k) =>
        {
            if (k.Key is not (Key.Enter or Key.Escape)) return;
            k.Handled = true;
            Finish(keep: k.Key == Key.Enter);
            Keyboard.ClearFocus();
        };
        box.LostKeyboardFocus += (_, _) => Finish(keep: true);
        row.DataContextChanged += Handed;
        shown.Visibility = Visibility.Hidden;
        row.Children.Add(box);
        box.Focus();
        box.CaretIndex = box.Text.Length;
    }

    // ── Scrolling: the last-minute lines wait until the list stops (1.23.0) ──

    /// <summary>How long after the list last moved its lines are drawn again.</summary>
    private static readonly TimeSpan ScrollRest = TimeSpan.FromMilliseconds(150);
    private System.Windows.Threading.DispatcherTimer? _scrollRest;

    /// <summary>
    /// The list moved: its rows' lines hold (see <see cref="Controls.Sparkline.HoldProperty"/>) until it has been still
    /// for <see cref="ScrollRest"/>, then each one that missed a reading or came into view is drawn once. Readings keep
    /// being kept meanwhile. Only a move counts: rows added or a card folded change the list's height, not its place.
    /// </summary>
    private void List_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0) return;
        if (!Controls.Sparkline.GetHold(SensorList)) Controls.Sparkline.SetHold(SensorList, true);
        if (_scrollRest is null)
        {
            _scrollRest = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = ScrollRest };
            _scrollRest.Tick += (_, _) =>
            {
                _scrollRest!.Stop();
                SensorList.ClearValue(Controls.Sparkline.HoldProperty);
            };
        }
        _scrollRest.Stop();
        _scrollRest.Start();
    }
}
