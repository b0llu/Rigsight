using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Rigsight.Core;
using Rigsight.ViewModels;

namespace Rigsight.Views;

public partial class TaskbarView : UserControl
{
    private TaskbarSensorRow? _picking;

    public TaskbarView()
    {
        InitializeComponent();
        ColorPick.Changed += (number, name, tile) =>
        {
            if (_picking is { } row && DataContext is TaskbarViewModel vm) vm.SetColors(row, number, name, tile);
        };
        ColorPick.CustomMade += (kind, hex) => (DataContext as TaskbarViewModel)?.AddCustomColor(kind, hex);
        // The preview is the taskbar's own drawing: drawn again each second while the page shows (its numbers, its graphs).
        _redraw.Tick += (_, _) => Redraw();
        Loaded += (_, _) => { WatchWindow(true); Redraw(); _redraw.Start(); };
        Unloaded += (_, _) => { CloseAddList(); WatchWindow(false); _redraw.Stop(); };
        // The page scrolling away from the box (the list stays where it opened): closed. The list's own scrolling isn't the page's.
        PageScroll.ScrollChanged += (_, e) => { if (e.OriginalSource == PageScroll && e.VerticalChange != 0) CloseAddList(); };
    }

    private Window? _window;

    /// <summary>
    /// The preview and pictures drawn again, at the page's own pixels: the screen's scale times the app's zoom (the whole
    /// page is scaled by Ctrl + wheel, so a picture drawn at the taskbar's size would be stretched and smeared).
    /// </summary>
    private void Redraw()
    {
        if (DataContext is not TaskbarViewModel vm) return;
        try
        {
            if (PresentationSource.FromVisual(this) is { CompositionTarget: { } target } && Window.GetWindow(this) is { } window)
            {
                var toWindow = TransformToAncestor(window);
                double zoom = (toWindow.Transform(new Point(100, 0)) - toWindow.Transform(new Point(0, 0))).Length / 100;
                vm.PageScale = zoom * target.TransformToDevice.M11;
                vm.PopupScale = target.TransformToDevice.M11;
            }
        }
        catch (InvalidOperationException) { /* not in the window yet */ }
        vm.Redraw();
    }

    private readonly System.Windows.Threading.DispatcherTimer _redraw = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>A size the taskbar is too short for: Windows' taskbar settings, where "Show smaller taskbar buttons" is.</summary>
    private void OpenTaskbarSettings_Click(object sender, RoutedEventArgs e) => Rigsight.Services.TaskbarBar.OpenSettings();

    /// <summary>The window's clicks, another window coming to the front, and moves close the "Add a reading" list, as the title bar's ask drop's.</summary>
    private void WatchWindow(bool on)
    {
        if (_window is { } old)
        {
            old.PreviewMouseDown -= Window_PreviewMouseDown;
            old.Deactivated -= Window_Away;
            old.LocationChanged -= Window_Away;
            old.StateChanged -= Window_Away;
            _window = null;
        }
        if (!on || Window.GetWindow(this) is not { } window) return;
        _window = window;
        window.PreviewMouseDown += Window_PreviewMouseDown;
        window.Deactivated += Window_Away;
        window.LocationChanged += Window_Away;
        window.StateChanged += Window_Away;
    }

    /// <summary>A click anywhere in the window but the box (the list is a window of its own: its clicks don't come here).</summary>
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (AddPop.IsOpen && !AddSearchText.IsMouseOver) CloseAddList();
    }

    private void Window_Away(object? sender, EventArgs e) => CloseAddList();

    private void CloseAddList() => AddPop.IsOpen = false;

    // ── The floating lists: a reading's looks, and who a spot is paired with ──

    private void ShowChoices(FrameworkElement under, IReadOnlyList<TaskbarChoice> choices, object? barBrush = null)
    {
        ChoiceList.ItemsSource = choices;
        ChoicePop.Tag = barBrush;
        ChoicePop.PlacementTarget = under;
        ChoicePop.IsOpen = true;
    }

    /// <summary>
    /// The wheel over a floating list scrolls that list alone: a list is a window of its own whose events still reach the
    /// page, which scrolled too once the list reached its end.
    /// </summary>
    private void List_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject d || FindScroll(d) is not { } scroll) return;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset - (scroll.CanContentScroll ? Math.Sign(e.Delta) * 3 : e.Delta / 3.0));
        e.Handled = true;
    }

    private static ScrollViewer? FindScroll(DependencyObject d)
    {
        if (d is ScrollViewer s) return s;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindScroll(System.Windows.Media.VisualTreeHelper.GetChild(d, i)) is { } found) return found;
        return null;
    }

    private void Choice_Click(object sender, RoutedEventArgs e)
    {
        ChoicePop.IsOpen = false;
        if (sender is FrameworkElement { DataContext: TaskbarChoice choice }) choice.Pick();
    }

    /// <summary>A reading's look: each it can have, that reading drawn with it.</summary>
    private void Look_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TaskbarSensorRow row } button) ShowChoices(button, row.LookChoices(), row.BarBrush);
    }

    private TaskbarSpot? _pairFor;

    /// <summary>Paired with: every reading of this PC, a search box over them, under the button.</summary>
    private void Partner_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskbarSpot spot } button) return;
        _pairFor = spot;
        PartnerSearch.Text = "";
        FillPartners();
        PartnerPop.PlacementTarget = button;
        PartnerPop.IsOpen = true;
    }

    private void FillPartners()
    {
        if (_pairFor is not { } spot || DataContext is not TaskbarViewModel vm) return;
        var items = vm.PartnerItems(spot, PartnerSearch.Text);
        PartnerList.ItemsSource = items;
        PartnerEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PartnerPop_Opened(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { PartnerSearch.Focus(); Keyboard.Focus(PartnerSearch); },
        System.Windows.Threading.DispatcherPriority.Input);

    private void PartnerSearch_Changed(object sender, TextChangedEventArgs e) => FillPartners();

    private void PartnerSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        PartnerPop.IsOpen = false;
        e.Handled = true;
    }

    private void PartnerPick_Click(object sender, RoutedEventArgs e)
    {
        PartnerPop.IsOpen = false;
        if (sender is FrameworkElement { DataContext: TaskbarPartnerItem { Pick: { } pick } }) pick();
    }

    /// <summary>A reading's Pick button: its number's, name's and tile's colours, under the button.</summary>
    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskbarSensorRow row } button || DataContext is not TaskbarViewModel vm) return;
        _picking = row;
        ColorPick.Open(button, row.Name, row.IsTemperature, row.NumberColor, row.NameColor, row.TileColor, TaskbarViewModel.PartHex(row.Part),
            Lang.T("Default puts back RigSight's colors."), vm.NumberCustomColors, vm.NameCustomColors, vm.TileCustomColors,
            row.ShowsNameColor, row.ShowsTileColor, row.SecondWord.ToUpper(System.Globalization.CultureInfo.CurrentCulture));
    }

    /// <summary>
    /// A click anywhere on a row's head opens or closes it: its edges and the room round its picture too, not only its
    /// words (a plain click on the handle too; the quick remove, the chevron and a drag keep their own).
    /// </summary>
    private void Head_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskbarSpot spot } head || _dragged) { _dragged = false; return; }
        for (var d = e.OriginalSource as DependencyObject; d is not null && d != head; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Primitives.ButtonBase) return;
        spot.ToggleCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>A drag just ended on the handle: the button coming up after it isn't a click.</summary>
    private bool _dragged;

    // ── Moving a spot: drag its handle onto another row (a line shows where it goes), or the arrow keys on the handle ──

    private Point _gripAt;
    private TaskbarSpot? _gripSpot;

    private void Grip_Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskbarSpot spot } grip) return;
        _gripSpot = spot;
        _dragged = false;
        _gripAt = e.GetPosition(this);
        grip.Focus();
    }

    private void Grip_Move(object sender, MouseEventArgs e)
    {
        if (_gripSpot is not { } spot || e.LeftButton != MouseButtonState.Pressed || sender is not DependencyObject grip) return;
        var at = e.GetPosition(this);
        if (Math.Abs(at.Y - _gripAt.Y) < SystemParameters.MinimumVerticalDragDistance && Math.Abs(at.X - _gripAt.X) < SystemParameters.MinimumHorizontalDragDistance) return;
        _gripSpot = null;
        _dragged = true;
        DragDrop.DoDragDrop(grip, new DataObject(typeof(TaskbarSpot), spot), DragDropEffects.Move);
        ClearDropLines();
    }

    private void Grip_Key(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskbarSpot spot } || DataContext is not TaskbarViewModel vm) return;
        int by = e.Key switch { Key.Up => -1, Key.Down => 1, _ => 0 };
        if (by == 0) return;
        e.Handled = true;
        string key = spot.Key;
        vm.MoveSpot(spot, vm.Spots.IndexOf(spot) + by);
        // The list was made again: the handle of the same spot keeps the focus, for the next press.
        Dispatcher.BeginInvoke(() =>
        {
            if (vm.Spots.FirstOrDefault(s => s.Key == key) is { } moved && SpotList.ItemContainerGenerator.ContainerFromItem(moved) is DependencyObject row
                && FindGrip(row) is { } grip) grip.Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static FrameworkElement? FindGrip(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Focusable: true } f && f.Cursor == Cursors.SizeAll) return f;
            if (FindGrip(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>Above or below a row: by which half of it the pointer is in.</summary>
    private static bool Above(FrameworkElement row, DragEventArgs e) => e.GetPosition(row).Y < row.ActualHeight / 2;

    private void Spot_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskbarSpot target } row || e.Data.GetData(typeof(TaskbarSpot)) is not TaskbarSpot dragged || dragged == target)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        bool above = Above(row, e);
        ClearDropLines();
        target.DropAbove = above;
        target.DropBelow = !above;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void Spot_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TaskbarSpot target }) target.DropAbove = target.DropBelow = false;
    }

    private void Spot_Drop(object sender, DragEventArgs e)
    {
        ClearDropLines();
        if (sender is not FrameworkElement { DataContext: TaskbarSpot target } row || e.Data.GetData(typeof(TaskbarSpot)) is not TaskbarSpot dragged
            || dragged == target || DataContext is not TaskbarViewModel vm) return;
        int from = vm.Spots.IndexOf(dragged), at = vm.Spots.IndexOf(target);
        int to = Above(row, e) ? at : at + 1;
        if (from < to) to--; // it leaves its place first
        vm.MoveSpot(dragged, to);
        e.Handled = true;
    }

    private void ClearDropLines()
    {
        if (DataContext is not TaskbarViewModel vm) return;
        foreach (var spot in vm.Spots) spot.DropAbove = spot.DropBelow = false;
    }

    // ── Adding ──

    /// <summary>The "Add a reading" box: its list floats under it while it has the focus or is typed in.</summary>
    private void AddBox_Focus(object sender, RoutedEventArgs e) => AddPop.IsOpen = true;

    /// <summary>A click in the box after its list was closed (Esc, a click elsewhere) while it kept the focus: the list again.</summary>
    private void AddBox_Click(object sender, MouseButtonEventArgs e)
    {
        if (AddSearchText.IsKeyboardFocusWithin) AddPop.IsOpen = true;
    }

    private void AddBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseAddList(); e.Handled = true; }
        else if (e.Key == Key.Tab) CloseAddList(); // on to the next control
        else AddPop.IsOpen = true;
    }
}
