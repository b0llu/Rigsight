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
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };
    private WidgetsViewModel? _vm;

    public WidgetsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Watch(DataContext as WidgetsViewModel);
        Unloaded += (_, _) => Watch(null);
        Loaded += (_, _) => Watch(DataContext as WidgetsViewModel);
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
}
