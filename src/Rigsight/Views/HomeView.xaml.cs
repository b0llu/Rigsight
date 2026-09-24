using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Rigsight.ViewModels;

namespace Rigsight.Views;

public partial class HomeView : UserControl
{
    // History is written once a minute, so "today" is rebuilt at the same pace while Home is on screen.
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _refreshing;

    public HomeView()
    {
        InitializeComponent();
        _refresh.Tick += async (_, _) =>
        {
            if (_refreshing || DataContext is not HomeViewModel vm) return;
            _refreshing = true;
            try { await vm.RefreshAsync(); }
            finally { _refreshing = false; }
        };
        Loaded += (_, _) => _refresh.Start();
        Unloaded += (_, _) => _refresh.Stop();
    }

    private void OpenYesterday_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is ShellViewModel shell)
            shell.Navigate("reports", "yesterday");
    }
}
