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
        Scroller.SizeChanged += (_, _) => FitLiveList();
        Layout.SizeChanged += (_, _) => FitLiveList();
    }

    /// <summary>
    /// The live list fills what the window has left under the cards above it, as tall as the window allows, and stops
    /// shrinking at its MinHeight: a smaller window scrolls the page instead of squeezing the list to a few rows.
    /// </summary>
    private void FitLiveList()
    {
        double above = Layout.RowDefinitions[0].ActualHeight + Layout.RowDefinitions[1].ActualHeight;
        double room = Math.Floor(Scroller.ActualHeight - Layout.Margin.Top - Layout.Margin.Bottom - LiveCard.Margin.Top - above);
        double height = Math.Max(LiveCard.MinHeight, room);
        if (LiveCard.Height != height) LiveCard.Height = height;
    }

    private void App_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProcRow row }) (DataContext as MemoryViewModel)?.Live.ToggleProcessesCommand.Execute(row);
    }
}
