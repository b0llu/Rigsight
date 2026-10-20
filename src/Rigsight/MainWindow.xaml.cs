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
        SetUpTitleBar();
        // Charts and lists grow with a taller window (see Roomy).
        // Zoomed, the pages have the window's room divided by the zoom.
        SizeChanged += (_, _) => ApplyZoom();
        // Ctrl + wheel zooms the window, as a browser does (nothing in the app uses Ctrl + wheel for itself).
        PreviewMouseWheel += (_, e) =>
        {
            if (!System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control) || e.Delta == 0) return;
            Zoom(e.Delta > 0 ? 1 : -1);
            e.Handled = true;
        };
        // What's new after an update: once the window is up, so it's the first thing seen, not a popup out of nowhere.
        ContentRendered += (_, _) =>
        {
            _vm.WhatsNew.CheckOnStart();
            // A rewind finished since RigSight was last open: made now, and the biggest shown full screen.
            _ = _vm.Rewind.StartAsync();
            AskForSupportLater(whatsNewShown: _vm.WhatsNew.IsOpen);
        };
        // Listened to weakly: the card outlives any one window (the tests host several).
        PropertyChangedEventManager.AddHandler(vm.WhatsNew, OnWhatsNewOpened, nameof(WhatsNewViewModel.IsOpen));
        PropertyChangedEventManager.AddHandler(vm.Support, OnSupportOpened, nameof(SupportViewModel.IsOpen));
        PropertyChangedEventManager.AddHandler(vm.Processes.Question, OnEndQuestionOpened, nameof(EndQuestionViewModel.IsOpen));
        PropertyChangedEventManager.AddHandler(vm.Update, OnUpdateScreen, nameof(UpdateViewModel.ShowOverlay));
        PreviewKeyDown += (_, e) =>
        {
            // Ctrl + plus or minus zooms, Ctrl + 0 back to 100%; not in a text box (Overlay's shortcut box records Ctrl + keys).
            if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control
                && System.Windows.Input.Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase)
            {
                int? steps = e.Key switch
                {
                    System.Windows.Input.Key.OemPlus or System.Windows.Input.Key.Add => 1,
                    System.Windows.Input.Key.OemMinus or System.Windows.Input.Key.Subtract => -1,
                    System.Windows.Input.Key.D0 or System.Windows.Input.Key.NumPad0 => 0,
                    _ => null,
                };
                if (steps is int n)
                {
                    Zoom(n);
                    e.Handled = true;
                    return;
                }
            }
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
            if (e.Key == System.Windows.Input.Key.Escape && _vm.Support.IsOpen)
            {
                _vm.Support.LaterCommand.Execute(null);
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

    /// <summary>One step in or out (0: back to 100%), kept in the settings, and shown for a moment in the title bar.</summary>
    private void Zoom(int steps)
    {
        double chosen = steps == 0 ? 1 : Core.Settings.AppZoom.Stepped(_vm.Settings.Current.Zoom, steps);
        if (chosen != _vm.Settings.Current.Zoom)
        {
            Core.Log.Write("app", $"Zoom: {_vm.Settings.Current.Zoom:P0} -> {chosen:P0}");
            _vm.Settings.Update(s => s.Zoom = chosen);
        }
        double used = ApplyZoom();
        ZoomText.Text = Core.Lang.T($"Zoom {Math.Round(used * 100):0}%");
        ZoomText.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                new System.Windows.Media.Animation.DiscreteDoubleKeyFrame(1, TimeSpan.Zero),
                new System.Windows.Media.Animation.DiscreteDoubleKeyFrame(1, TimeSpan.FromSeconds(1.2)),
                new System.Windows.Media.Animation.LinearDoubleKeyFrame(0, TimeSpan.FromSeconds(1.6)),
            },
        });
    }

    /// <summary>
    /// The chosen zoom on everything under the title bar, as far as the window has room for it (see AppZoom), and the
    /// pages that grow with the window told the room they really have. Returns the zoom used.
    /// </summary>
    private double ApplyZoom()
    {
        double width = Frame.ActualWidth, height = Frame.ActualHeight - TitleBar.ActualHeight;
        double room = width > 0 && height > 0 ? Math.Min(width / MinWidth, height / (MinHeight - TitleBar.ActualHeight)) : double.NaN;
        double used = Core.Settings.AppZoom.Used(_vm.Settings.Current.Zoom, room);
        if (Body.LayoutTransform is not System.Windows.Media.ScaleTransform current ? used != 1 : current.ScaleX != used)
            Body.LayoutTransform = used == 1 ? System.Windows.Media.Transform.Identity : new System.Windows.Media.ScaleTransform(used, used);
        Controls.Roomy.Fit(double.IsNaN(height) || height <= 0 ? ActualHeight : ActualHeight / used);
        return used;
    }

    /// <summary>
    /// The opening moment for the support card: 20 seconds after the window opened, if it settled on Home with nothing
    /// else over it, and What's new didn't show this time (one card an opening). Whether it may ask at all is
    /// SupportAsk's to say.
    /// </summary>
    private void AskForSupportLater(bool whatsNewShown)
    {
        if (whatsNewShown) return;
        var wait = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        wait.Tick += (_, _) =>
        {
            wait.Stop();
            if (!CalmOnHome()) return;
            _ = _vm.Support.TryAskAsync("opening", stillGood: CalmOnHome);
        };
        wait.Start();
    }

    private bool CalmOnHome() => IsVisible && WindowState != WindowState.Minimized && _vm.CurrentPage == "home"
        && !_vm.WhatsNew.IsOpen && !_vm.Rewind.IsOpen && !_vm.Export.IsOpen && !_vm.Presets.IsOpen
        && !_vm.Processes.Question.IsOpen && !_vm.Update.ShowOverlay;

    /// <summary>The support card fades in over the window, as What's new does.</summary>
    private void OnSupportOpened(object? sender, PropertyChangedEventArgs e)
    {
        if (!_vm.Support.IsOpen || !Motion.Enabled) return;
        SupportLayer.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop });
        Motion.PopIn(SupportCard);
    }

    /// <summary>A click beside the support card: "Maybe later".</summary>
    private void SupportBackdrop_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => _vm.Support.LaterCommand.Execute(null);

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
