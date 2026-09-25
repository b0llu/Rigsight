using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Rigsight.Views;

public partial class SensorsView : UserControl
{
    public SensorsView() => InitializeComponent();

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
