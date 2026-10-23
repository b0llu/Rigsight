using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Rigsight.Core;
using Rigsight.ViewModels;

namespace Rigsight.Views;

/// <summary>
/// The Ask window's place and size. Stuck: its bottom-right corner sits 16 px in from the page's, and pulling its top
/// or left edge grows it up and to the left. Popped out: it floats where it was dragged and grows from its bottom and
/// right edges. Dragging the top bar moves it (a stuck one pops out first); let go near the corner, it sticks again.
/// </summary>
public partial class AskWindow : UserControl
{
    private const double Gap = 16, MinWidth_ = 360, MinHeight_ = 380, StickWithin = 90;
    private AskViewModel? _vm;
    private Point? _dragFrom;
    private Point _panelFrom;

    public AskWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as AskViewModel);
        Layer.SizeChanged += (_, _) => Place();
        Loaded += (_, _) => Place();
    }

    private void Attach(AskViewModel? vm)
    {
        if (_vm is not null) { _vm.PropertyChanged -= OnVmChanged; _vm.SearchWanted = null; }
        _vm = vm;
        if (_vm is not null) { _vm.PropertyChanged += OnVmChanged; _vm.SearchWanted = FocusSearch; }
        Place();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AskViewModel.IsPoppedOut) or nameof(AskViewModel.IsBig) or nameof(AskViewModel.ShowHistoryColumn)) Place();
        // Another conversation opened (and again once a saved one has its messages), or a question asked: the chat goes to its end once laid out. An answer coming in
        // leaves it where it is, so it never jumps while someone reads ("don't scroll down when a new answer comes").
        if (e.PropertyName is nameof(AskViewModel.Current) or nameof(AskViewModel.ShowEnd))
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, ToEnd);
    }

    /// <summary>
    /// To the end as it is now, by its number: ScrollToEnd pins the chat to the bottom until someone scrolls, so a streaming
    /// answer dragged it down line by line (the log showed the offset moving exactly as the content grew).
    /// </summary>
    private void ToEnd()
    {
        Chat.UpdateLayout();
        Chat.ScrollToVerticalOffset(Chat.ScrollableHeight);
    }

    private Size Room => new(Math.Max(MinWidth_, Layer.ActualWidth), Math.Max(MinHeight_, Layer.ActualHeight));

    /// <summary>Puts the panel where the view model says, kept inside the page.</summary>
    private void Place()
    {
        if (_vm is null || Layer.ActualWidth <= 0) return;
        var room = Room;
        PopIcon.Text = _vm.IsPoppedOut ? "" : "";
        PopButton.ToolTip = _vm.IsPoppedOut ? Lang.T("Stick to the corner") : Lang.T("Pop out");
        AutomationProperties_Name(PopButton, (string)PopButton.ToolTip);
        SizeIcon.Text = _vm.IsBig ? "" : "";
        SizeButton.ToolTip = _vm.IsBig ? Lang.T("Smaller") : Lang.T("Bigger");
        AutomationProperties_Name(SizeButton, (string)SizeButton.ToolTip);
        bool stuck = !_vm.IsPoppedOut;
        TopEdge.Visibility = LeftEdge.Visibility = TopLeft.Visibility = stuck ? Visibility.Visible : Visibility.Collapsed;
        BottomEdge.Visibility = RightEdge.Visibility = BottomRight.Visibility = stuck ? Visibility.Collapsed : Visibility.Visible;
        if (stuck)
        {
            var size = Fit(_vm.IsBig ? _vm.StuckBig : _vm.StuckSmall, room);
            Panel.Width = size.Width;
            Panel.Height = size.Height;
            Panel.Margin = new Thickness(room.Width - size.Width - Gap, room.Height - size.Height - Gap, 0, 0);
        }
        else
        {
            var r = _vm.Floating.IsEmpty ? new Rect((room.Width - 780) / 2, Math.Max(Gap, (room.Height - 620) / 3), 780, 620) : _vm.Floating;
            var size = Fit(r.Size, room);
            double x = Math.Clamp(r.X, 0, Math.Max(0, room.Width - size.Width)), y = Math.Clamp(r.Y, 0, Math.Max(0, room.Height - size.Height));
            _vm.Floating = new Rect(x, y, size.Width, size.Height);
            Panel.Width = size.Width;
            Panel.Height = size.Height;
            Panel.Margin = new Thickness(x, y, 0, 0);
        }
        // The rail and the column follow the width.
        _vm.PanelWidth = Panel.Width;
        HistoryColumn.Width = new GridLength(_vm.ShowHistoryColumn ? 260 : 0);
    }

    private static void AutomationProperties_Name(DependencyObject element, string name) => System.Windows.Automation.AutomationProperties.SetName(element, name);

    private static Size Fit(Size want, Size room) => Fit(want.Width, want.Height, room);

    /// <summary>
    /// Clamped as plain numbers, before any Size is made: an edge pulled past the other side, or a popped-out window
    /// partly off the page, gives a negative width, and a Size of one throws ("Width and Height must be non-negative",
    /// a tester's crash dialog, 2026-10-22).
    /// </summary>
    private static Size Fit(double width, double height, Size room) =>
        new(Math.Clamp(double.IsNaN(width) ? MinWidth_ : width, MinWidth_, Math.Max(MinWidth_, room.Width - 2 * Gap)),
            Math.Clamp(double.IsNaN(height) ? MinHeight_ : height, MinHeight_, Math.Max(MinHeight_, room.Height - 2 * Gap)));

    // ── Resizing from the edges ──────────────────────────────────────────
    private void Edge_Drag(object sender, DragDeltaEventArgs e)
    {
        if (_vm is null) return;
        var room = Room;
        if (!_vm.IsPoppedOut)
        {
            // The bottom-right corner stays put: pulling up or left grows it.
            double w = Panel.Width - (sender == TopEdge ? 0 : e.HorizontalChange), h = Panel.Height - (sender == LeftEdge ? 0 : e.VerticalChange);
            var size = Fit(w, h, room);
            if (_vm.IsBig) _vm.StuckBig = size; else _vm.StuckSmall = size;
        }
        else
        {
            var r = _vm.Floating;
            double w = r.Width + (sender == BottomEdge ? 0 : e.HorizontalChange), h = r.Height + (sender == RightEdge ? 0 : e.VerticalChange);
            var size = Fit(Math.Min(w, room.Width - r.X), Math.Min(h, room.Height - r.Y), room);
            _vm.Floating = new Rect(r.TopLeft, size);
        }
        Place();
    }

    // ── Moving by the top strip (over the chat, or the sidebar's top) ────
    private UIElement? _dragBy;

    private void Bar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || sender is not UIElement by || e.OriginalSource is DependencyObject d && FindButton(d, by)) return;
        _dragFrom = e.GetPosition(Layer);
        _panelFrom = new Point(Panel.Margin.Left, Panel.Margin.Top);
        _dragBy = by;
        by.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>The strip lies over the chat's top: the wheel still scrolls the chat under it.</summary>
    private void Bar_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        Chat.ScrollToVerticalOffset(Chat.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    private void Bar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_vm is null || _dragFrom is not { } from || e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(Layer);
        var by = now - from;
        if (!_vm.IsPoppedOut)
        {
            if (Math.Abs(by.X) + Math.Abs(by.Y) < 6) return;
            // A stuck window pops out where it is, at its size, and follows the pointer from there.
            _vm.Floating = new Rect(_panelFrom.X, _panelFrom.Y, Panel.Width, Panel.Height);
            _vm.IsPoppedOut = true;
        }
        var room = Room;
        double x = Math.Clamp(_panelFrom.X + by.X, 0, Math.Max(0, room.Width - Panel.Width)), y = Math.Clamp(_panelFrom.Y + by.Y, 0, Math.Max(0, room.Height - Panel.Height));
        _vm.Floating = new Rect(x, y, Panel.Width, Panel.Height);
        Panel.Margin = new Thickness(x, y, 0, 0);
        // Near the corner: an outline shows where it will stick.
        var corner = Fit(_vm.IsBig ? _vm.StuckBig : _vm.StuckSmall, room);
        bool near = room.Width - (x + Panel.Width) < StickWithin && room.Height - (y + Panel.Height) < StickWithin;
        DropSpot.Visibility = near ? Visibility.Visible : Visibility.Collapsed;
        if (near)
        {
            DropSpot.Width = corner.Width;
            DropSpot.Height = corner.Height;
            DropSpot.Margin = new Thickness(room.Width - corner.Width - Gap, room.Height - corner.Height - Gap, 0, 0);
        }
    }

    private void Bar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragFrom is null) return;
        _dragFrom = null;
        _dragBy?.ReleaseMouseCapture();
        _dragBy = null;
        if (DropSpot.Visibility == Visibility.Visible && _vm is not null)
        {
            DropSpot.Visibility = Visibility.Collapsed;
            _vm.IsPoppedOut = false;
        }
        Place();
    }

    private static bool FindButton(DependencyObject d, DependencyObject stop)
    {
        for (var at = d; at is not null && at != stop; at = System.Windows.Media.VisualTreeHelper.GetParent(at))
            if (at is ButtonBase) return true;
        return false;
    }

    /// <summary>The rail's search: the cursor goes into the conversations' search box once it's on screen.</summary>
    private void FocusSearch() => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
    {
        if (Find<TextBox>(Panel, t => t.Name == "HistorySearchBox" && t.IsVisible) is { } box) { box.Focus(); box.SelectAll(); }
    });

    private static T? Find<T>(DependencyObject from, Func<T, bool> wanted) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(from); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(from, i);
            if (child is T found && wanted(found)) return found;
            if (Find(child, wanted) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>A click on the dimmed chat closes the conversations opened over it.</summary>
    private void Dim_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is not null) _vm.IsHistoryOpen = false;
        e.Handled = true;
    }

    // Rename: the box takes the cursor with its text selected; Enter keeps what's typed, Esc puts the title back, and
    // clicking away keeps it.
    private void Rename_Shown(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is TextBox box)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => { box.Focus(); box.SelectAll(); });
    }

    private void Rename_Key(object sender, KeyEventArgs e)
    {
        if (_vm is null || (sender as FrameworkElement)?.DataContext is not AskConversation talk) return;
        if (e.Key is Key.Enter or Key.Escape)
        {
            _vm.FinishRename(talk, keep: e.Key == Key.Enter);
            e.Handled = true;
        }
    }

    private void Rename_Lost(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_vm is not null && (sender as FrameworkElement)?.DataContext is AskConversation talk) _vm.FinishRename(talk, keep: true);
    }

    // ── A paragraph's sources: the list on hover ─────────────────────────
    // It opens as the pointer reaches the pill and stays while the pointer moves into it; a moment after it leaves both,
    // it closes. A tap opens it where there is no pointer. One list at a time; scrolling the chat closes it.
    private AskRefs? _openRefs;
    private System.Windows.Threading.DispatcherTimer? _refsClose;

    private void ShowRefs(AskRefs refs)
    {
        _refsClose?.Stop();
        if (_openRefs is { } other && other != refs) other.IsOpen = false;
        _openRefs = refs;
        refs.IsOpen = true;
        if (_refsClose is null)
        {
            _refsClose = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _refsClose.Tick += (_, _) => { _refsClose.Stop(); CloseRefs(); };
            Chat.ScrollChanged += (_, e) => { if (e.VerticalChange != 0) CloseRefs(); };
        }
    }

    private void CloseRefs()
    {
        if (_openRefs is { } open) open.IsOpen = false;
        _openRefs = null;
    }

    private void LeaveRefs() { _refsClose?.Stop(); _refsClose?.Start(); }

    private void RefPill_Enter(object sender, MouseEventArgs e) { if ((sender as FrameworkElement)?.Tag is AskRefs refs) ShowRefs(refs); }
    private void RefPill_Leave(object sender, MouseEventArgs e) => LeaveRefs();
    private void RefPill_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AskRefs refs) return;
        // A second press (keyboard, a tap) closes what the first opened.
        if (refs.IsOpen && !((UIElement)sender).IsMouseOver) CloseRefs(); else ShowRefs(refs);
    }
    private void RefList_Enter(object sender, MouseEventArgs e) => _refsClose?.Stop();
    private void RefList_Leave(object sender, MouseEventArgs e) => LeaveRefs();

    /// <summary>Enter sends the question; Shift+Enter starts a new line.</summary>
    private void Question_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || _vm is null) return;
        e.Handled = true;
        if (_vm.SendCommand.CanExecute(null)) _vm.SendCommand.Execute(null);
    }
}
