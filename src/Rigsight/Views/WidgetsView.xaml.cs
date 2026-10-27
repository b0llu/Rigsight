using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Rigsight.ViewModels;

namespace Rigsight.Views;

public partial class WidgetsView : UserControl
{
    private const double EditorWidth = 430;
    // The shared frozen one: an unfrozen ease kept in a static field holds on to the last animation that used it, and
    // through its Completed handler the whole page (Settings stayed in memory after every theme change, 2026-10-27).
    private static readonly IEasingFunction Ease = Services.Motion.Ease;
    private WidgetsViewModel? _vm;

    public WidgetsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Watch(DataContext as WidgetsViewModel);
        // The reading picker's list closes on a click elsewhere on the page or when the window loses focus.
        PreviewMouseDown += ClosePickerOnClickElsewhere;
        Unloaded += (_, _) =>
        {
            Watch(null);
            if (Window.GetWindow(this) is { } w) WeakEventManager<Window, EventArgs>.RemoveHandler(w, nameof(Window.Deactivated), ClosePickerOnDeactivated);
            ClosePickerOnDeactivated(null, EventArgs.Empty);
        };
        Loaded += (_, _) =>
        {
            Watch(DataContext as WidgetsViewModel);
            // Weakly: a page the window held on to by this stayed in memory after it was replaced (a theme change).
            if (Window.GetWindow(this) is { } w)
            {
                WeakEventManager<Window, EventArgs>.RemoveHandler(w, nameof(Window.Deactivated), ClosePickerOnDeactivated);
                WeakEventManager<Window, EventArgs>.AddHandler(w, nameof(Window.Deactivated), ClosePickerOnDeactivated);
            }
        };
    }

    private void Watch(WidgetsViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = vm;
        if (vm is null) return;
        vm.PropertyChanged += OnViewModelChanged;
        ShowEditor(vm.IsEditing, animate: false);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        if (e.PropertyName == nameof(WidgetsViewModel.IsEditing)) ShowEditor(_vm.IsEditing, animate: true);
        // Another widget while it's open: its settings fade in, so the change is seen.
        else if (e.PropertyName == nameof(WidgetsViewModel.Editing) && _vm.IsEditing && EditorPanel.IsVisible)
            EditorContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = Ease });
    }

    /// <summary>
    /// The editor slides in over the page from the right while the page dims a little behind it; closing slides it
    /// back out and takes it (and the dimming) away. The page itself never moves.
    /// </summary>
    private void ShowEditor(bool show, bool animate)
    {
        var duration = TimeSpan.FromMilliseconds(animate ? (show ? 260 : 200) : 0);
        if (show)
        {
            EditorPanel.Visibility = Visibility.Visible;
            EditorScrim.Visibility = Visibility.Visible;
            EditorContent.BeginAnimation(OpacityProperty, null);
        }
        var slide = new DoubleAnimation(show ? 0 : EditorWidth, duration) { EasingFunction = Ease };
        if (!show) slide.Completed += (_, _) =>
        {
            if (_vm is { IsEditing: true }) return; // opened again meanwhile
            EditorPanel.Visibility = Visibility.Collapsed;
            EditorScrim.Visibility = Visibility.Collapsed;
        };
        EditorSlide.BeginAnimation(TranslateTransform.XProperty, slide, HandoffBehavior.SnapshotAndReplace);
        EditorScrim.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 0.3 : 0, duration) { EasingFunction = Ease }, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>A click outside the editor, on the page, closes it.</summary>
    private void EditorScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is { IsEditing: true }) _vm.Editing = null;
        e.Handled = true;
    }

    // ── The built-in row scrolls sideways with the wheel ──

    private double _rowTarget;
    private bool _rowScrolling;

    /// <summary>
    /// The wheel over the built-in row moves it sideways, smoothly; once it can't go further that way (or everything
    /// fits), the wheel scrolls the page as it does anywhere else.
    /// </summary>
    private void BuiltInRow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        double from = _rowScrolling ? _rowTarget : BuiltInRow.HorizontalOffset;
        double to = Math.Clamp(from - e.Delta * 1.4, 0, BuiltInRow.ScrollableWidth);
        if (Math.Abs(to - from) < 0.5)
        {
            PageScroll.ScrollToVerticalOffset(PageScroll.VerticalOffset - e.Delta / 120.0 * 48);
            return;
        }
        _rowTarget = to;
        if (_rowScrolling) return;
        _rowScrolling = true;
        CompositionTarget.Rendering += StepRow;
    }

    private void StepRow(object? sender, EventArgs e)
    {
        double now = BuiltInRow.HorizontalOffset, gap = _rowTarget - now;
        if (Math.Abs(gap) < 0.5 || !IsLoaded)
        {
            BuiltInRow.ScrollToHorizontalOffset(_rowTarget);
            _rowScrolling = false;
            CompositionTarget.Rendering -= StepRow;
            return;
        }
        BuiltInRow.ScrollToHorizontalOffset(now + gap * 0.22);
    }

    // ── The reading picker ──

    private ViewModels.WidgetCard? PickerCard => PickBox.DataContext as ViewModels.WidgetCard;

    /// <summary>Into the field: the list opens (a click there too, when it was closed with Esc while the field kept focus).</summary>
    private void PickBox_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (PickerCard is { } card) card.PickerOpen = true;
    }

    private void PickBox_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PickerCard is { PickerOpen: false } card && PickBox.IsKeyboardFocusWithin) card.PickerOpen = true;
    }

    /// <summary>A key typed opens the list at once (the search itself waits for a pause in the typing).</summary>
    private void PickBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (PickerCard is { PickerOpen: false } card && PickBox.IsKeyboardFocusWithin && PickBox.Text.Length > 0) card.PickerOpen = true;
    }

    /// <summary>
    /// The list closes when it's meant to: the keyboard left both the field and the list, a click elsewhere on the page
    /// (see <see cref="ClosePickerOnClickElsewhere"/>), the window lost focus, or Esc.
    /// </summary>
    private void PickBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject to && (to == PickBox || IsInPicker(to))) return;
        // Focus can pass through nothing on its way into the list: look again once it has landed.
        Dispatcher.BeginInvoke(() =>
        {
            if (PickBox.IsKeyboardFocusWithin || PickPopup.Child?.IsKeyboardFocusWithin == true) return;
            if (PickerCard is { } card) card.PickerOpen = false;
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private bool IsInPicker(DependencyObject o)
    {
        // A Run or a Hyperlink isn't a Visual: asking VisualTreeHelper for its parent throws, so those go by the logical tree.
        for (var d = o; d is not null; d = (d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : null) ?? LogicalTreeHelper.GetParent(d))
            if (d == PickPopup.Child) return true;
        return false;
    }

    /// <summary>
    /// A click on the page outside the field closes the list. A click in the list comes here too (WPF passes a popup's
    /// events up through the page it belongs to) and keeps it open: a chip or a line was what was clicked.
    /// </summary>
    private void ClosePickerOnClickElsewhere(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PickerCard is not { PickerOpen: true } card || PickBox.IsMouseOver) return;
        if (e.OriginalSource is DependencyObject source && IsInPicker(source)) return;
        card.PickerOpen = false;
    }

    private void ClosePickerOnDeactivated(object? sender, EventArgs e)
    {
        if (PickerCard is { PickerOpen: true } card) card.PickerOpen = false;
    }

    private void PickPopup_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape || PickerCard is not { } card) return;
        card.PickerOpen = false;
        PickBox.Focus();
        e.Handled = true;
    }

    /// <summary>
    /// Esc closes the list; Enter adds the first line that can be added (searched first, if the pause hasn't come yet);
    /// Down opens it. Focus stays in the field throughout: the lines don't take it, so adding one doesn't close the list.
    /// </summary>
    private void PickBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (PickerCard is not { } card) return;
        if (e.Key == System.Windows.Input.Key.Escape && card.PickerOpen)
        {
            card.PickerOpen = false;
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Enter)
        {
            card.PickerOpen = true;
            card.PickerText = PickBox.Text;
            if (card.PickerOptions.FirstOrDefault(o => o.CanAdd) is { } first) card.PickOptionCommand.Execute(first);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Down)
        {
            card.PickerOpen = true;
            e.Handled = true;
        }
    }
}
