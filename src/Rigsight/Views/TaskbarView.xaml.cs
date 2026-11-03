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
    }

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

    private void AddBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) { AddPop.IsOpen = false; e.Handled = true; }
        else AddPop.IsOpen = true;
    }
}
