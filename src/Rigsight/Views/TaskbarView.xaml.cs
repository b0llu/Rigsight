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
        ColorPick.Changed += (number, name) =>
        {
            if (_picking is { } row && DataContext is TaskbarViewModel vm) vm.SetColors(row, number, name);
        };
        ColorPick.CustomMade += (kind, hex) => (DataContext as TaskbarViewModel)?.AddCustomColor(kind, hex);
    }

    /// <summary>A reading's Colors button: its picker, under the button.</summary>
    private void Colors_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskbarSensorRow row } button) return;
        _picking = row;
        ColorPick.Open(button, row.Name, row.IsTemperature, row.NumberColor, row.NameColor, TaskbarViewModel.PartHex(row.Part),
            Lang.T("Default puts back RigSight's colors."), ((TaskbarViewModel)DataContext).NumberCustomColors, ((TaskbarViewModel)DataContext).NameCustomColors);
    }
}
