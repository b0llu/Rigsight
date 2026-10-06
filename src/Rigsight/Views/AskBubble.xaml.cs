using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Rigsight.Services;
using Rigsight.ViewModels;

namespace Rigsight.Views;

/// <summary>Riggy's bubble and the chat that opens above it (see <see cref="AskViewModel"/>).</summary>
public partial class AskBubble : UserControl
{
    private AskViewModel? _vm;

    public AskBubble()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (Parent is FrameworkElement host) host.SizeChanged += (_, _) => FitPanel();
            FitPanel();
        };
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.Added -= OnAdded;
                _vm.PropertyChanged -= OnViewModelChanged;
            }
            _vm = DataContext as AskViewModel;
            if (_vm is not null)
            {
                _vm.Added += OnAdded;
                _vm.PropertyChanged += OnViewModelChanged;
            }
        };
    }

    /// <summary>How tall the chat is (its template binds to this: the chat is only there while open).</summary>
    public static readonly DependencyProperty PanelHeightProperty =
        DependencyProperty.Register(nameof(PanelHeight), typeof(double), typeof(AskBubble), new PropertyMetadata(FullHeight));

    public double PanelHeight
    {
        get => (double)GetValue(PanelHeightProperty);
        set => SetValue(PanelHeightProperty, value);
    }

    /// <summary>A named part of the chat, while it is open (null while closed: it doesn't exist then).</summary>
    private T? Part<T>(string name) where T : FrameworkElement
    {
        return Find(PanelHost);

        T? Find(DependencyObject parent)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T match && match.Name == name) return match;
                if (Find(child) is { } deeper) return deeper;
            }
            return null;
        }
    }

    /// <summary>The chat at its full size, and the room it leaves for the window's edge and for Riggy under it.</summary>
    private const double FullHeight = 640, Around = 110;

    /// <summary>The chat is as tall as it likes where the window has room, and no taller than the window where it hasn't.</summary>
    private void FitPanel()
    {
        if (Parent is FrameworkElement host && host.ActualHeight > 0) PanelHeight = Math.Clamp(host.ActualHeight - Around, 360, FullHeight);
    }

    /// <summary>Whether a click landed on the bubble or in the chat (anywhere else closes the chat).</summary>
    public bool Holds(DependencyObject? source)
    {
        for (var d = source; d is not null; d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (ReferenceEquals(d, this)) return true;
        return false;
    }

    /// <summary>
    /// The chat has just been built (it is each time it opens), still unseen: it fades in, with its last lines
    /// in view and the cursor in its box.
    /// </summary>
    private void Panel_Loaded(object sender, RoutedEventArgs e)
    {
        var panel = (FrameworkElement)sender;
        Part<ScrollViewer>("Scroll")?.ScrollToEnd();
        Motion.PopIn(panel);
        panel.Opacity = 1; // what it keeps once the fade lets go (and at once when animations are off)
        Part<TextBox>("Box")?.Focus();
    }

    /// <summary>A new line: the question just asked goes to the top of the view, so its answer reads from its start.</summary>
    private void OnAdded() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        if (Part<ScrollViewer>("Scroll") is not { } Scroll || Part<ItemsControl>("List") is not { } List) return; // closed
        if (_vm is null || _vm.Messages.Count == 0)
        {
            Scroll.ScrollToTop();
            return;
        }
        int lastQuestion = -1;
        for (int i = _vm.Messages.Count - 1; i >= 0 && lastQuestion < 0; i--)
            if (_vm.Messages[i].IsUser) lastQuestion = i;
        if (lastQuestion < 0 || List.ItemContainerGenerator.ContainerFromIndex(lastQuestion) is not FrameworkElement item)
        {
            Scroll.ScrollToEnd();
            return;
        }
        Scroll.UpdateLayout();
        double top = item.TransformToAncestor(Scroll).Transform(new Point(0, 0)).Y;
        // Whatever fits under the question is shown; a long answer starts at its question, not at its end.
        Scroll.ScrollToVerticalOffset(Math.Min(Scroll.VerticalOffset + top - 4, Scroll.ScrollableHeight));
    });

    /// <summary>Enter sends; the arrow keys walk the suggestions; Tab takes one into the box to finish by hand.</summary>
    private void Box_KeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        switch (e.Key)
        {
            case Key.Enter:
                _vm.SendCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Down or Key.Up:
                e.Handled = _vm.MoveSuggestion(e.Key == Key.Down ? 1 : -1);
                break;
            case Key.Tab when _vm.TakeSuggestion():
                if (sender is TextBox box) box.CaretIndex = box.Text.Length;
                e.Handled = true;
                break;
        }
    }

    private void Suggestions_Click(object sender, MouseButtonEventArgs e)
    {
        if (_vm is not null && ((e.OriginalSource as FrameworkElement)?.DataContext ?? (e.OriginalSource as FrameworkContentElement)?.DataContext) is AskSuggestion pick) _vm.SuggestCommand.Execute(pick.Text);
        Part<TextBox>("Box")?.Focus();
    }

    private void Bubble_Hover(object sender, MouseEventArgs e) => UpdatePeek();

    /// <summary>
    /// Out whole and upright while the mouse is on it or the chat is open; otherwise leaning in from the corner, half
    /// out of sight (its eyes stay in view).
    /// </summary>
    private void UpdatePeek()
    {
        // Two places only: leaning in from the corner, a third of it out of sight; and out whole, under the mouse or while
        // the chat is open. The same spot for both of those, so a click that opens or closes the chat doesn't move it.
        bool hidden = !(Bubble.IsMouseOver || _vm is { IsOpen: true });
        Move(Peek, System.Windows.Media.TranslateTransform.XProperty, hidden ? 21 : 3);
        Move(Peek, System.Windows.Media.TranslateTransform.YProperty, hidden ? 23 : 5);
        Move(Lean, System.Windows.Media.RotateTransform.AngleProperty, hidden ? -30 : 0);

        static void Move(System.Windows.Media.Animation.Animatable target, DependencyProperty property, double to)
        {
            if (Motion.Enabled)
                target.BeginAnimation(property, new System.Windows.Media.Animation.DoubleAnimation(to, TimeSpan.FromMilliseconds(220)) { EasingFunction = Motion.Ease });
            else
            {
                target.BeginAnimation(property, null);
                ((DependencyObject)target).SetValue(property, to);
            }
        }
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AskViewModel.IsOpen)) UpdatePeek();
    }
}
