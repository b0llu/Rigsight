using System.Windows.Controls;
using System.Windows.Threading;
using Rigsight.ViewModels;

namespace Rigsight.Views;

public partial class ReportsView : UserControl
{
    // History is written once a minute: a period that includes now is re-read at that pace while it's on screen.
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _refreshing;

    public ReportsView()
    {
        InitializeComponent();
        _refresh.Tick += async (_, _) =>
        {
            if (_refreshing || DataContext is not ReportsViewModel vm || vm.Report is not { } r) return;
            if (!(r.From <= DateTime.Now && DateTime.Now < r.To)) return;
            _refreshing = true;
            try { await vm.LoadAsync(); }
            finally { _refreshing = false; }
        };
        Loaded += (_, _) => _refresh.Start();
        Unloaded += (_, _) => _refresh.Stop();
    }
}
