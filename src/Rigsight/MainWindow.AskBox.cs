using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Rigsight;

/// <summary>
/// The title bar's ask box and its drop (approved mock-up, 2026-10-27): a click or the Ask shortcut grows the box down
/// into a panel to type in; Esc, a click away or the window going to the back shrinks it back into the box, keeping what's
/// typed. The grow is the user's own pick over a plain fade; with Windows set to show fewer animations it only fades.
/// </summary>
public partial class MainWindow
{
    private const double DropWidth = 640;
    private bool _dropClosing;

    private void SetUpAskBox()
    {
        _vm.Ask.BarOpenRequested += () => Dispatcher.BeginInvoke(OpenFromBox, System.Windows.Threading.DispatcherPriority.Input);
        // Hangs from the box: its top on the box's, centred on it, from the sizes Windows gives as they are on screen (2026-10-27:
        // worked out by hand from the zoom, it was only centred at 100%).
        AskDrop.CustomPopupPlacementCallback = DropPlacement;
        PreviewMouseDown += (_, e) => { if (AskDrop.IsOpen && !AskDropRoot.IsMouseOver && !AskBox.IsMouseOver) CloseDrop(); };
        Deactivated += (_, _) => { if (AskDrop.IsOpen) CloseDrop(instant: true); };
        LocationChanged += (_, _) => { if (AskDrop.IsOpen) CloseDrop(instant: true); };
        StateChanged += (_, _) => { if (AskDrop.IsOpen) CloseDrop(instant: true); };
        AskDropRoot.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; CloseDrop(); } };
        // Ctrl+K in RigSight: the box, as a command bar opens in most apps; the shortcut from anywhere is Settings' own.
        InputBindings.Add(new KeyBinding(new CommunityToolkit.Mvvm.Input.RelayCommand(OpenFromBox), Key.K, ModifierKeys.Control));
        // Readied once the window has settled, so the first click doesn't wait for it (2026-10-27: the first opening lagged).
        Loaded += (_, _) =>
        {
            var later = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(2) };
            later.Tick += (_, _) => { later.Stop(); WarmDrop(); };
            later.Start();
        };
        // Start asking in the drop: it becomes the box to type in, the cursor in it.
        _vm.Ask.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModels.AskViewModel.ShowFirstQuestion) && !_vm.Ask.ShowFirstQuestion && AskDrop.IsOpen)
                Dispatcher.BeginInvoke(() => AskDropText.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        };
        // A question asked or a chat opened from the panel: it goes back into the box as Ask opens.
        _vm.Ask.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ViewModels.AskViewModel.IsOpen) && _vm.Ask.IsOpen && AskDrop.IsOpen) CloseDrop(); };
    }

    /// <summary>
    /// Centred on the box with its top on the box's: both sizes as drawn on screen (zoom, Windows' scaling and the drop's
    /// shadow room included), so it's the middle whatever the zoom. The drop's sides have the same room, so its middle is
    /// the drawn panel's.
    /// </summary>
    internal static CustomPopupPlacement[] DropPlacement(Size popup, Size target, Point offset) =>
        [new CustomPopupPlacement(new Point((target.Width - popup.Width) / 2, 0), PopupPrimaryAxis.None)];

    /// <summary>
    /// A zoom change while it's open: opened again in place, at once, so it's measured and placed at the new zoom (2026-10-27:
    /// an open popup kept the size it had, unlike one opened after the change). What's typed stays.
    /// </summary>
    private void PlaceDrop()
    {
        if (!AskDrop.IsOpen || _dropClosing) return;
        AskDrop.IsOpen = false;
        AskDropRoot.InvalidateMeasure();
        AskDrop.IsOpen = true;
        Dispatcher.BeginInvoke(() => AskDropText.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void AskBox_Click(object sender, RoutedEventArgs e) => OpenFromBox();

    /// <summary>What the box (or the shortcut) does: Plans with no questions left, else the drop, Ask open or not (it starts a new chat).</summary>
    private void OpenFromBox()
    {
        var ask = _vm.Ask;
        if (!ask.IsEnabled) return;
        if (!ask.InTopBar) { ask.IsOpen = true; return; }
        if (ask.BarLocked) { ask.SeePlansCommand.Execute(null); return; }
        if (!AskDrop.IsOpen) OpenDrop();
        else AskDropText.Focus();
    }

    /// <summary>
    /// The drop's first opening done out of sight: its window made, its contents built and laid out, its code compiled,
    /// and then closed again, all invisible and without taking the keyboard. A real first opening was slow with all of it.
    /// </summary>
    private void WarmDrop()
    {
        _vm.Ask.ReadyBar();
        if (AskDrop.IsOpen || !IsVisible || WindowState == WindowState.Minimized || !_vm.Ask.InTopBar) return;
        AskDropRoot.Opacity = 0;
        AskDropRoot.IsHitTestVisible = false;
        AskDrop.IsOpen = true;
        AskDropRoot.UpdateLayout();
        Dispatcher.BeginInvoke(() =>
        {
            if (!_dropClosing && AskDropRoot.Opacity == 0) AskDrop.IsOpen = false;
            AskDropRoot.Opacity = 1;
            AskDropRoot.IsHitTestVisible = true;
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OpenDrop()
    {
        _dropClosing = false;
        _vm.Ask.ReadyBar();
        // Seen and pressable, even when a click came in while it was being readied out of sight.
        AskDropRoot.Opacity = 1;
        AskDropRoot.IsHitTestVisible = true;
        PlaceDrop();
        AskDropScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        AskDropScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        AskDropRoot.BeginAnimation(OpacityProperty, null);
        AskDropContent.BeginAnimation(OpacityProperty, null);
        AskDropContent.Opacity = 0;
        AskDrop.IsOpen = true;
        AskBox.Opacity = 0;
        AskDropRoot.UpdateLayout();
        bool grow = SystemParameters.ClientAreaAnimation;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (grow)
        {
            // From the box's own size to the panel's, hanging from its top; the contents come in once it has grown.
            double sx = Math.Clamp(AskBox.ActualWidth / DropWidth, 0.1, 1), sy = Math.Clamp(AskBox.ActualHeight / Math.Max(1, AskDropRoot.ActualHeight), 0.05, 1);
            AskDropScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(sx, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            AskDropScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(sy, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            AskDropContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { BeginTime = TimeSpan.FromMilliseconds(90) });
        }
        else
        {
            AskDropContent.Opacity = 1;
            AskDropRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        }
        Dispatcher.BeginInvoke(() => { if (!_vm.Ask.ShowFirstQuestion) { AskDropText.Focus(); AskDropText.CaretIndex = AskDropText.Text.Length; } }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Back into the box, keeping what's typed (the box shows it); at once when the window moved or went to the back.</summary>
    private void CloseDrop(bool instant = false)
    {
        if (!AskDrop.IsOpen || _dropClosing) return;
        void Done() { AskDrop.IsOpen = false; AskBox.Opacity = 1; _dropClosing = false; }
        if (instant || !SystemParameters.ClientAreaAnimation)
        {
            if (instant) { Done(); return; }
            _dropClosing = true;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
            fade.Completed += (_, _) => { Done(); AskDropRoot.BeginAnimation(OpacityProperty, null); AskDropRoot.Opacity = 1; };
            AskDropRoot.BeginAnimation(OpacityProperty, fade);
            return;
        }
        _dropClosing = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        double sx = Math.Clamp(AskBox.ActualWidth / DropWidth, 0.1, 1), sy = Math.Clamp(AskBox.ActualHeight / Math.Max(1, AskDropRoot.ActualHeight), 0.05, 1);
        AskDropContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(80)));
        var shrink = new DoubleAnimation(sy, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease, BeginTime = TimeSpan.FromMilliseconds(40) };
        shrink.Completed += (_, _) => Done();
        AskDropScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(sx, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease, BeginTime = TimeSpan.FromMilliseconds(40) });
        AskDropScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, shrink);
    }

    /// <summary>Not now on the first-question step in the drop: back into the box, nothing agreed.</summary>
    private void AskDropNotNow_Click(object sender, RoutedEventArgs e) => CloseDrop();

    /// <summary>Settings › Ask RigSight before agreeing (which pages it may read, which AI).</summary>
    private void AskDropSettings_Click(object sender, RoutedEventArgs e)
    {
        CloseDrop(instant: true);
        _vm.Ask.SettingsCommand.Execute(null);
    }

    /// <summary>Enter asks, Shift+Enter a new line, ↓ into the questions when nothing is typed.</summary>
    private void AskDropText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            if (_vm.Ask.AskFromBarCommand.CanExecute(null)) _vm.Ask.AskFromBarCommand.Execute(null);
        }
        else if (e.Key == Key.Down && AskDropText.Text.Length == 0)
        {
            e.Handled = true;
            AskDropList.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }
    }
}
