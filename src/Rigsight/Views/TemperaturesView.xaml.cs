using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using Rigsight.ViewModels;

namespace Rigsight.Views;

public partial class TemperaturesView : UserControl
{
    private LiveData? _live;

    public TemperaturesView()
    {
        InitializeComponent();
        // The period's button opens the range menu; a custom range is picked in the same editor as the other pages'.
        ChartPager.LabelCommand = new RelayCommand(OpenRangeMenu);
        // Under the button's right edge, like the pager's calendar was: the controls sit at the right of the card.
        RangeMenu.CustomPopupPlacementCallback = (popup, target, _) =>
            [new CustomPopupPlacement(new Point(target.Width - popup.Width + 8, target.Height + 6), PopupPrimaryAxis.Horizontal)];
        ChartRangeEditor.Applied += (from, to) => _live?.SetChartCustom(from, to);
        DataContextChanged += (_, _) => Watch(DataContext as LiveData);
        Unloaded += (_, _) => Watch(null);
        Loaded += (_, _) => Watch(DataContext as LiveData);
    }

    private void Watch(LiveData? live)
    {
        if (_live == live) return;
        if (_live is not null) _live.ChartRangeEditRequested -= OpenRangeEditor;
        _live = live;
        if (_live is not null) _live.ChartRangeEditRequested += OpenRangeEditor;
    }

    private long _menuClosedAt;

    private void OpenRangeMenu()
    {
        // A click on the button while the menu is open closes it (the click outside it) and must not open it again.
        if (Environment.TickCount64 - _menuClosedAt < 250) return;
        RangeMenu.IsOpen = true;
    }

    private void RangeMenu_Closed(object? sender, EventArgs e) => _menuClosedAt = Environment.TickCount64;

    // Any line closes the menu (its command has run).
    private void RangeMenu_Click(object sender, RoutedEventArgs e) => RangeMenu.IsOpen = false;

    private void OpenRangeEditor()
    {
        if (_live is null) return;
        // After the menu has closed.
        Dispatcher.BeginInvoke(() => ChartRangeEditor.Open(ChartPager, _live.ChartCustomFrom, _live.ChartCustomTo, _live.ChartHistoryStart),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
