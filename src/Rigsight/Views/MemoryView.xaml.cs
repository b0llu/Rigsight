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
        Loaded += (_, _) => SetShown(true);
        Unloaded += (_, _) => SetShown(false);
        Scroller.SizeChanged += (_, _) => FitLiveList();
        Layout.SizeChanged += (_, _) => FitLiveList();
        LiveList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) => AlignHeadings(e.ViewportWidth)));
    }

    // Giving apps that start their usual costs work on every update: only while this page is shown.
    private void SetShown(bool shown)
    {
        if (DataContext is not MemoryViewModel vm) return;
        vm.SetShown(shown);
    }

    /// <summary>What a row keeps clear on its right: its own margin (8) and the padding of its highlight (10).</summary>
    private const double RowInset = 18;

    /// <summary>
    /// The column headings end where the rows' figures end: the rows lose the scrollbar's width (and its gap) when the
    /// list is long enough to scroll, and the headings above the list don't.
    /// </summary>
    private void AlignHeadings(double viewportWidth)
    {
        if (viewportWidth <= 0) return;
        double right = RowInset + Math.Max(0, LiveList.ActualWidth - viewportWidth);
        if (Math.Abs(LiveHead.Margin.Right - right) > 0.1) LiveHead.Margin = new Thickness(0, 0, right, 6);
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
