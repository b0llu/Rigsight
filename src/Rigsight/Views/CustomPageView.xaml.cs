using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Controls;
using Rigsight.ViewModels;

namespace Rigsight.Views;

public partial class CustomPageView : UserControl
{
    private TileGrid? _grid;
    private CustomPageViewModel? _page;

    // Drag state
    private FrameworkElement? _frame;
    private TileViewModel? _tile;
    private Point _grab;
    private Point _down;
    private bool _dragging;

    public CustomPageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_page is not null) _page.LayoutChanged -= OnLayoutChanged;
            _page = DataContext as CustomPageViewModel;
            if (_page is not null) _page.LayoutChanged += OnLayoutChanged;
        };
    }

    private void Grid_Loaded(object sender, RoutedEventArgs e) => _grid = (TileGrid)sender;

    private void OnLayoutChanged() => _grid?.InvalidateMeasure();

    // ── Dragging tiles ────────────────────────────────────────────────────

    private void Tile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_page is not { IsEditing: true } || _grid is null) return;
        if (sender is not FrameworkElement frame || frame.DataContext is not TileViewModel { IsPlaceholder: false } tile) return;
        if (IsInsideButton(e.OriginalSource as DependencyObject, frame)) return;

        _frame = frame;
        _tile = tile;
        _grab = e.GetPosition(frame);
        _down = e.GetPosition(_grid);
        _dragging = false;
        frame.CaptureMouse();
        e.Handled = true;
    }

    private void Tile_MouseMove(object sender, MouseEventArgs e)
    {
        if (_tile is null || _grid is null || _page is null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(_grid);

        if (!_dragging)
        {
            // A small wobble isn't a drag.
            if (Math.Abs(p.X - _down.X) < 4 && Math.Abs(p.Y - _down.Y) < 4) return;
            _dragging = true;
            _page.BeginDrag(_tile);
        }

        double width = _frame?.ActualWidth ?? 0;
        var topLeft = new Point(
            Math.Clamp(p.X - _grab.X, 0, Math.Max(0, _grid.ActualWidth - width)),
            Math.Max(0, p.Y - _grab.Y));
        _tile.DragPosition = topLeft;
        var (col, row) = _grid.CellAt(topLeft);
        _page.DragTo(col, row);
        _grid.InvalidateArrange();

        AutoScroll(e.GetPosition(PageScroll));
    }

    /// <summary>Scrolls the page while a tile is dragged near its top or bottom edge.</summary>
    private void AutoScroll(Point inView)
    {
        const double edge = 48, step = 14;
        if (inView.Y > PageScroll.ActualHeight - edge) PageScroll.ScrollToVerticalOffset(PageScroll.VerticalOffset + step);
        else if (inView.Y < edge) PageScroll.ScrollToVerticalOffset(PageScroll.VerticalOffset - step);
    }

    private void Tile_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_tile is null) return;
        e.Handled = true;
        _frame?.ReleaseMouseCapture(); // ends the drag via LostMouseCapture
    }

    private void Tile_LostCapture(object sender, MouseEventArgs e)
    {
        if (_tile is null) return;
        if (_dragging) _page?.EndDrag();
        _tile = null;
        _frame = null;
        _dragging = false;
    }

    private static bool IsInsideButton(DependencyObject? source, DependencyObject stop)
    {
        for (var d = source; d is not null && d != stop; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ButtonBase) return true;
        return false;
    }

    // ── Tile size menu ────────────────────────────────────────────────────

    private void Size_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button || button.DataContext is not TileViewModel tile) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var option in tile.SizeOptions)
            menu.Items.Add(new MenuItem { Header = option.Label, Command = option.Apply, IsEnabled = !option.IsCurrent });
        menu.IsOpen = true;
    }

    // ── Page name ─────────────────────────────────────────────────────────

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
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
