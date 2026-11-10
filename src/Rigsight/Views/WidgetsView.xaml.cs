using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core.Settings;
using Rigsight.ViewModels;

namespace Rigsight.Views;

/// <summary>
/// The Widgets page and its editor (approved mock-up 8Bfo1Ce7KFJs4g1wxHQP1U). The page: the map of the screens, the
/// widgets, the list to add from and the settings for all of them. The editor, a page of its own: tiles dragged and
/// resized on the canvas by whole cells, the keyboard for the picked tile, and the shortcut keys recorded here.
/// </summary>
public partial class WidgetsView : UserControl
{
    private WidgetsViewModel? _vm;

    public WidgetsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Watch(DataContext as WidgetsViewModel);
        Loaded += (_, _) =>
        {
            Watch(DataContext as WidgetsViewModel);
            if (_vm is null) return;
            _vm.PageShown = true;
            _vm.LoadMap();
        };
        Unloaded += (_, _) =>
        {
            if (_vm is not null) _vm.PageShown = false;
            Watch(null);
        };
        Map.Moved += (id, x, y) => _vm?.MoveWidget(id, x, y);
        PreviewKeyDown += RecordShortcut;
    }

    private void Watch(WidgetsViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = vm;
        if (vm is null) return;
        vm.PropertyChanged += OnViewModelChanged;
        SetUpEditor();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WidgetsViewModel.Editor)) return;
        SetUpEditor();
        // The editor opens at its top, and the page comes back where it was.
        EditorScroll.ScrollToTop();
        if (_vm?.Editor is not null) Dispatcher.BeginInvoke(() => CanvasScroll.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The editor draws the widget pixel for pixel at this screen's scale, as wide as the canvas is.</summary>
    private void SetUpEditor()
    {
        if (_vm?.Editor is not { } ed) return;
        ed.ScreenScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (CanvasFrame.ActualWidth > 0) ed.CanvasWidth = CanvasFrame.ActualWidth;
    }

    private void CanvasFrame_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_vm?.Editor is { } ed && e.NewSize.Width > 0) ed.CanvasWidth = e.NewSize.Width;
    }

    // ── Shortcut keys ──

    /// <summary>While a shortcut waits for its keys, the keys pressed are it (Esc stops waiting).</summary>
    private void RecordShortcut(object sender, KeyEventArgs e)
    {
        if (_vm is not { Recording: not null } vm) return;
        e.Handled = true;
        // With Alt held, WPF reports the key as "System".
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { vm.Recording = null; return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None) return;
        var mods = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Ctrl;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;
        vm.Record(KeyInterop.VirtualKeyFromKey(key), mods);
    }

    // ── The editor ──

    /// <summary>Enter keeps the new name, Esc puts the old one back; either way the canvas has the keyboard again.</summary>
    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) NameBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        else if (e.Key == Key.Escape) NameBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        else return;
        CanvasScroll.Focus();
        e.Handled = true;
    }

    /// <summary>Arrow keys move the picked tile a cell; Delete removes it; Ctrl+Z and Ctrl+Y undo and redo.</summary>
    private void Canvas_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm?.Editor is not { } ed) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Left: ed.Nudge(-1, 0); break;
            case Key.Right: ed.Nudge(1, 0); break;
            case Key.Up: ed.Nudge(0, -1); break;
            case Key.Down: ed.Nudge(0, 1); break;
            case Key.Delete or Key.Back when ed.Selected >= 0: ed.RemoveTileAt(ed.Selected); break;
            case Key.Z when ctrl: ed.UndoCommand.Execute(null); break;
            case Key.Y when ctrl: ed.RedoCommand.Execute(null); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>A click on the canvas away from every tile picks none.</summary>
    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        CanvasScroll.Focus();
        if (_vm?.Editor is { } ed && !e.Handled) ed.SelectTile(-1);
    }

    private FrameworkElement? _grip;
    private TileBox? _box;
    private string _mode = "move";
    private Point _from;
    private int _dx, _dy;

    /// <summary>Pressed on a tile (to move it) or on its edge or corner (to resize it): the tile is picked and follows the pointer.</summary>
    private void Tile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not TileBox box || _vm?.Editor is not { } ed) return;
        e.Handled = true;
        CanvasScroll.Focus();
        ed.SelectTile(box.Index);
        (_grip, _box, _mode, _from, _dx, _dy) = (el, box, el.Tag as string ?? "move", e.GetPosition(EditCanvas), 0, 0);
        el.CaptureMouse();
    }

    /// <summary>The tile's outline where it would land, in whole cells.</summary>
    private void Tile_MouseMove(object sender, MouseEventArgs e)
    {
        if (_grip is null || _box is null || sender != _grip || e.LeftButton != MouseButtonState.Pressed || _vm?.Editor is not { } ed) return;
        var p = e.GetPosition(EditCanvas);
        double cw = WidgetTiles.CellWidth * ed.Zoom, ch = WidgetTiles.CellHeight * ed.Zoom;
        int dx = (int)Math.Round((p.X - _from.X) / cw), dy = (int)Math.Round((p.Y - _from.Y) / ch);
        if (_mode == "e") dy = 0;
        if (_mode == "s") dx = 0;
        (_dx, _dy) = (dx, dy);
        double left = _box.Left, top = _box.Top, width = _box.Width, height = _box.Height;
        if (_mode == "move") { left += dx * cw; top += dy * ch; }
        else { width = Math.Max(cw, width + dx * cw); height = Math.Max(ch, height + dy * ch); }
        Canvas.SetLeft(DropOutline, left);
        Canvas.SetTop(DropOutline, top);
        DropOutline.Width = width;
        DropOutline.Height = height;
        DropOutline.Visibility = dx != 0 || dy != 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Let go: the tile moves or resizes by the cells dragged (a tile dropped on another swaps with it where they fit).</summary>
    private void Tile_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_grip is null || sender != _grip) return;
        e.Handled = true;
        _grip.ReleaseMouseCapture();
        DropOutline.Visibility = Visibility.Collapsed;
        var (box, mode, dx, dy) = (_box, _mode, _dx, _dy);
        (_grip, _box) = (null, null);
        if (box is null || _vm?.Editor is not { } ed) return;
        if (mode == "move") ed.MoveTile(box.Index, dx, dy);
        else ed.ResizeTile(box.Index, dx, dy);
    }
}
