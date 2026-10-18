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
        // Charts and lists grow with a taller window (see Roomy).
        SizeChanged += (_, e) => Controls.Roomy.Fit(e.NewSize.Height);
        // What's new after an update: once the window is up, so it's the first thing seen, not a popup out of nowhere.
        ContentRendered += (_, _) => _vm.WhatsNew.CheckOnStart();
        // Listened to weakly: the card outlives any one window (the tests host several).
        PropertyChangedEventManager.AddHandler(vm.WhatsNew, OnWhatsNewOpened, nameof(WhatsNewViewModel.IsOpen));
        PropertyChangedEventManager.AddHandler(vm.Processes.Question, OnEndQuestionOpened, nameof(EndQuestionViewModel.IsOpen));
        PropertyChangedEventManager.AddHandler(vm.Update, OnUpdateScreen, nameof(UpdateViewModel.ShowOverlay));
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && _vm.Processes.Question.IsOpen)
            {
                _vm.Processes.Question.Cancel();
                e.Handled = true;
                return;
            }
            if (e.Key == System.Windows.Input.Key.Escape && _vm.Export.IsOpen)
            {
                _vm.Export.CloseCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (e.Key == System.Windows.Input.Key.Escape && _vm.Presets.IsOpen)
            {
                _vm.Presets.CloseCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (e.Key == System.Windows.Input.Key.Escape && _vm.WhatsNew.IsOpen)
            {
                _vm.WhatsNew.CloseCommand.Execute(null);
                e.Handled = true;
                return;
            }
        };
        // Nothing is refreshed while the window is minimized: catch up as it comes back, not at the next minute.
        StateChanged += (_, _) => { if (WindowState != WindowState.Minimized) _ = _vm.RefreshOnRestoreAsync(); };
        ThemeManager.Changed += RebuildPages;
        // Closing ends the app, except where another window takes over (the tests host several in turn).
        Closed += (_, _) => ThemeManager.Changed -= RebuildPages;
        Closing += (_, _) => _vm.WindowClosing();
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

    /// <summary>The What's new card fades in over the window when it opens.</summary>
    private void OnWhatsNewOpened(object? sender, PropertyChangedEventArgs e)
    {
        if (!_vm.WhatsNew.IsOpen || !Motion.Enabled) return;
        WhatsNewLayer.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop });
        Motion.PopIn(WhatsNewCardHost);
    }

    /// <summary>
    /// While "Updating RigSight" is up, nothing of the window behind it may show through: the sidebar and the page are
    /// hidden (a scrollbar fading out was seen drawn over it), and the Settings menu, a window of its own, is closed (it
    /// stayed over the screen when the update was started from it). Back as they were if the update didn't install.
    /// </summary>
    private void OnUpdateScreen(object? sender, PropertyChangedEventArgs e)
    {
        bool up = _vm.Update.ShowOverlay;
        SidebarHost.Visibility = PageHost.Visibility = up ? Visibility.Hidden : Visibility.Visible;
        if (up) SettingsRow.IsChecked = false;
    }

    /// <summary>
    /// The question before ending a part of Windows takes the keyboard while it is up: Enter is its button in front,
    /// Esc cancels, and the list behind it no longer answers the keys.
    /// </summary>
    private void OnEndQuestionOpened(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm.Processes.Question.IsOpen) Dispatcher.BeginInvoke(() => EndQuestionLayer.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// A line of the Settings menu was picked: the menu goes away as its command runs. Not for the update line: the
    /// menu stays open and that line becomes the answer ("Checking…", then "Up to date" with a tick).
    /// </summary>
    /// <summary>
    /// "Crashes" pressed in the sidebar while a crash is open on that page: back to the list (the page is already the
    /// one shown, so nothing else would happen, and the list is what the sidebar's name promises).
    /// </summary>
    private void NavClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ViewModels.NavEntry { Key: "crashes" } && _vm.CurrentPage == "crashes")
            _vm.Crashes.CloseCaseCommand.Execute(null);
        // …and for an opened game: the sidebar's Games goes back to the library.
        if ((sender as FrameworkElement)?.DataContext is ViewModels.NavEntry { Key: "games" } && _vm.CurrentPage == "games")
            _vm.Games.ShowLibrary();
        // The same for an opened change: the sidebar's Timeline goes back to the list.
        if ((sender as FrameworkElement)?.DataContext is ViewModels.NavEntry { Key: "timeline" } && _vm.CurrentPage == "timeline")
            _vm.Timeline.CloseChangeCommand.Execute(null);
    }

    private void SettingsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, MenuUpdate)) ShowUpdateAnswer(true);
        else SettingsRow.IsChecked = false;
    }

    /// <summary>The next time the menu opens its update line asks again: the answer shows only from a press until the menu closes.</summary>
    private void SettingsMenu_Closed(object? sender, EventArgs e) => ShowUpdateAnswer(false);

    private void ShowUpdateAnswer(bool answer)
    {
        MenuUpdateText.Visibility = answer ? Visibility.Collapsed : Visibility.Visible;
        MenuUpdateAnswer.Visibility = answer ? Visibility.Visible : Visibility.Collapsed;
        // The line's icon (its Tag) follows the answer too: a tick once it is up to date.
        if (answer) MenuUpdate.SetBinding(TagProperty, new System.Windows.Data.Binding("Update.AnswerIcon"));
        else
        {
            System.Windows.Data.BindingOperations.ClearBinding(MenuUpdate, TagProperty);
            MenuUpdate.Tag = ViewModels.UpdateViewModel.CheckIcon;
        }
    }

    private void EndQuestionBackdrop_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => _vm.Processes.Question.Cancel();

    private void ExportBackdrop_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => _vm.Export.CloseCommand.Execute(null);

    private void PresetsBackdrop_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => _vm.Presets.CloseCommand.Execute(null);

    private void WhatsNewBackdrop_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => _vm.WhatsNew.CloseCommand.Execute(null);

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentPage)) ShowPage(_vm.CurrentPage);
    }

    /// <summary>
    /// Pages are built on first visit, so opening the app only pays for what you look at.
    /// </summary>
    private void ShowPage(string page)
    {
        // The page on screen, for the log: a crash that ends the app at once (a stack overflow) can write nothing itself,
        // and the agent's line about it then has this to go with it.
        Core.Log.Write("app", $"Showing {page}");
        if (!_pages.TryGetValue(page, out var view))
        {
            view = page switch
            {
                "reports" => new ReportsView { DataContext = _vm.ReportsPage },
                "apps" => new AppsView { DataContext = _vm.Apps },
                "games" => new GamesView { DataContext = _vm.Games },
                "crashes" => new CrashesView { DataContext = _vm.Crashes },
                "timeline" => new TimelineView { DataContext = _vm.Timeline },
                "temperatures" => new TemperaturesView { DataContext = _vm.Live },
                "fans" => new FansView { DataContext = _vm.Fans },
                "network" => new NetworkView { DataContext = _vm.Network },
                "memory" => new MemoryView { DataContext = _vm.Memory },
                "processes" => new ProcessesView { DataContext = _vm.Processes },
                "storage" => new StorageView { DataContext = _vm.Storage },
                "sensors" => new SensorsView { DataContext = _vm.Live },
                "widgets" => new WidgetsView { DataContext = _vm.Widgets },
                "overlay" => new OverlayView { DataContext = _vm.Overlay },
                "taskbar" => new TaskbarView { DataContext = _vm.Taskbar },
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
