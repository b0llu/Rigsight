using System.Windows;
using System.Windows.Controls;
using Rigsight.ViewModels;

namespace Rigsight.Views;

/// <summary>The Latency page (issue #15): an opened driver's view starts at its top.</summary>
public partial class LatencyView : UserControl
{
    private LatencyViewModel? _vm;
    private ScrollViewer? _driverScroller;

    public LatencyView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _vm = DataContext as LatencyViewModel;
            if (_vm is not null) _vm.DriverOpened += OnDriverOpened;
        };
        Unloaded += (_, _) =>
        {
            if (_vm is not null) _vm.DriverOpened -= OnDriverOpened;
            _vm = null;
        };
    }

    private void OnDriverOpened() => _driverScroller?.ScrollToTop();

    // The opened driver's view is built the first time one opens (a LazyTab): its scroller is found as it loads.
    private void Driver_Loaded(object sender, RoutedEventArgs e) =>
        _driverScroller ??= (sender as FrameworkElement)?.FindName("DriverScroller") as ScrollViewer;
}
