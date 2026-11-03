using System.Windows;
using System.Windows.Controls;
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
        Loaded += (_, _) => WatchWindow(true);
        Unloaded += (_, _) => { CloseAddList(); WatchWindow(false); };
        // The page scrolling away from the box (the list stays where it opened): closed. The list's own scrolling isn't the page's.
        PageScroll.ScrollChanged += (_, e) => { if (e.OriginalSource == PageScroll && e.VerticalChange != 0) CloseAddList(); };
    }

    private Window? _window;

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
    private void Window_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (AddPop.IsOpen && !AddSearchText.IsMouseOver) CloseAddList();
    }

    private void Window_Away(object? sender, EventArgs e) => CloseAddList();

    private void CloseAddList() => AddPop.IsOpen = false;

    /// <summary>A reading's Colors button: its picker, under the button.</summary>
    private void Colors_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskbarSensorRow row } button || DataContext is not TaskbarViewModel vm) return;
        _picking = row;
        ColorPick.Open(button, row.Name, row.IsTemperature, row.NumberColor, row.NameColor, row.TileColor, TaskbarViewModel.PartHex(row.Part),
            Lang.T("Default puts back RigSight's colors."), vm.NumberCustomColors, vm.NameCustomColors, vm.TileCustomColors);
    }

    /// <summary>A reading's Look button: its picker floats under it (and closes on a pick).</summary>
    private void Look_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskbarSensorRow row } button) return;
        LookPop.DataContext = row;
        LookPop.PlacementTarget = button;
        LookPop.IsOpen = true;
    }

    private void LookChoice_Click(object sender, RoutedEventArgs e) => LookPop.IsOpen = false;

    /// <summary>The "Add a reading" box: its list floats under it while it has the focus or is typed in.</summary>
    private void AddBox_Focus(object sender, RoutedEventArgs e) => AddPop.IsOpen = true;

    /// <summary>A click in the box after its list was closed (Esc, a click elsewhere) while it kept the focus: the list again.</summary>
    private void AddBox_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (AddSearchText.IsKeyboardFocusWithin) AddPop.IsOpen = true;
    }

    private void AddBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) { CloseAddList(); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Tab) CloseAddList(); // on to the next control
        else AddPop.IsOpen = true;
    }
}
