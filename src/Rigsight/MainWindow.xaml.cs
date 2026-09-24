using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rigsight.Services;
using Rigsight.ViewModels;
using Rigsight.Views;

namespace Rigsight;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _vm;
    private readonly Dictionary<string, FrameworkElement> _pages = [];

    public MainWindow(ShellViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        vm.PropertyChanged += OnViewModelChanged;
        vm.ActivateRequested += BringToFront;
        vm.CustomPageDeleted += key =>
        {
            // Unbind the deleted page's view so nothing keeps it (or its tiles) updating.
            if (_pages.Remove(key, out var view)) view.DataContext = null;
        };
        ShowPage(vm.CurrentPage);

        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        ThemeManager.Changed += RebuildPages;
    }

    /// <summary>
    /// After a theme change, build the pages again: the palette follows by itself, but the charts' colors and
    /// converted colors (temperatures, severities) are picked when a page is built.
    /// </summary>
    private void RebuildPages()
    {
        foreach (var view in _pages.Values) view.DataContext = null;
        _pages.Clear();
        PageHost.Content = null;
        ShowPage(_vm.CurrentPage);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentPage)) ShowPage(_vm.CurrentPage);
    }

    /// <summary>Pages are built on first visit, so opening the app only pays for what you look at.</summary>
    private void ShowPage(string page)
    {
        if (!_pages.TryGetValue(page, out var view))
        {
            view = page switch
            {
                "reports" => new ReportsView { DataContext = _vm.ReportsPage },
                "apps" => new AppsView { DataContext = _vm.Apps },
                "crashes" => new CrashesView { DataContext = _vm.Crashes },
                "temperatures" => new TemperaturesView { DataContext = _vm.Live },
                "memory" => new MemoryView { DataContext = _vm.Memory },
                "storage" => new StorageView { DataContext = _vm.Storage },
                "sensors" => new SensorsView { DataContext = _vm.Live },
                "widgets" => new WidgetsView { DataContext = _vm.Widgets },
                "overlay" => new OverlayView { DataContext = _vm.Overlay },
                "settings" => new SettingsView { DataContext = _vm.SettingsPage },
                _ when _vm.FindCustomPage(page) is { } custom => new CustomPageView { DataContext = custom },
                _ => new HomeView { DataContext = _vm.Home },
            };
            _pages[page] = view;
        }
        PageHost.Content = view;
    }

    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }
}
