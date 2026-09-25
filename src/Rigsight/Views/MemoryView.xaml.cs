using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Rigsight.Models;
using Rigsight.ViewModels;

namespace Rigsight.Views;

public partial class MemoryView : UserControl
{
    public MemoryView()
    {
        InitializeComponent();
        // Sorting the process list live costs work on every update; only do it while this page is shown.
        Loaded += (_, _) => (DataContext as MemoryViewModel)?.Live.SetProcessSorting(true);
        Unloaded += (_, _) => (DataContext as MemoryViewModel)?.Live.SetProcessSorting(false);
    }

    private void App_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProcRow row }) (DataContext as MemoryViewModel)?.Live.ToggleProcessesCommand.Execute(row);
    }
}
