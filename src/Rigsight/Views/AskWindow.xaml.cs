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
        Loaded += (_, _) =>
        {
            Place();
            Hook(true);
            // The conversations built once Ask has drawn, hidden in the column, so neither opening Ask nor its column waits for them.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, PlaceSidebar);
        };
        Unloaded += (_, _) => { Hook(false); CloseRefs(); System.Windows.Media.CompositionTarget.Rendering -= Pull_Frame; };
    }

    private Window? _host;

    /// <summary>
    /// RigSight going to the back closes a paragraph's sources: a popup stays on top of every window, so the list
    /// otherwise sat over the browser one of its links had just opened.
    /// </summary>
    private void Hook(bool on)
    {
        if (_host is not null) _host.Deactivated -= Host_Deactivated;
        _host = on ? Window.GetWindow(this) : null;
        if (_host is not null) _host.Deactivated += Host_Deactivated;
    }

    private void Host_Deactivated(object? sender, EventArgs e) => CloseRefs();

    private void Attach(AskViewModel? vm)
    {
        if (_vm is not null) { _vm.PropertyChanged -= OnVmChanged; _vm.SearchWanted = null; _vm.FocusWanted = null; }
        _vm = vm;
        if (_vm is not null) { _vm.PropertyChanged += OnVmChanged; _vm.SearchWanted = FocusSearch; _vm.FocusWanted = FocusQuestion; }
        Place();
    }

    /// <summary>
    /// The cursor into the question box once it's on screen (the window opened, a new conversation): not while the
    /// first-question window shows, and not taken from a box the person is already typing in (History's search, a rename).
    /// </summary>
    private void FocusQuestion() => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
    {
        if (_vm is null || _vm.ShowFirstQuestion || !Question.IsVisible) return;
        if (Keyboard.FocusedElement is TextBox other && other != Question && IsAncestorOf(other)) return;
        Question.Focus();
    });

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The column's notice comes with every new width (twice the work a frame while resizing): placed again only when it shows or hides.
        if (e.PropertyName is nameof(AskViewModel.IsPoppedOut) or nameof(AskViewModel.IsBig)
            || e.PropertyName is nameof(AskViewModel.ShowHistoryColumn) && HistoryColumn.Width.Value != (_vm!.ShowHistoryColumn ? 260 : 0)) Place();
        if (e.PropertyName is nameof(AskViewModel.ShowHistoryOver)) PlaceSidebar();
        // Another conversation: a list left open in the last one would open again by itself on the way back.
        if (e.PropertyName is nameof(AskViewModel.Current)) CloseRefs();
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
        // In the corner only the free sides pull (top, left); popped out, all of them.
        TopEdge.Visibility = LeftEdge.Visibility = TopLeft.Visibility = Visibility.Visible;
        BottomEdge.Visibility = RightEdge.Visibility = BottomRight.Visibility = TopRight.Visibility = BottomLeft.Visibility =
            stuck ? Visibility.Collapsed : Visibility.Visible;
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
        ColumnHost.Visibility = _vm.ShowHistoryColumn ? Visibility.Visible : Visibility.Hidden;
        if (_vm.ShowHistoryColumn) PlaceSidebar();
    }

    /// <summary>The one list of conversations, built the first time it's needed and then only moved.</summary>
    private ContentControl? _sidebar;

    /// <summary>
    /// The conversations over the chat while they're open there (narrow), in the column otherwise, shown or not: moved,
    /// never built again, so a pull past <see cref="AskViewModel.ColumnFrom"/> only lays out rows that already exist.
    /// </summary>
    private void PlaceSidebar()
    {
        if (_vm is null) return;
        if (_sidebar is null)
        {
            _sidebar = new ContentControl { ContentTemplate = (DataTemplate)FindResource("Sidebar"), Focusable = false, IsTabStop = false, FocusVisualStyle = null };
            _sidebar.SetBinding(ContentProperty, new System.Windows.Data.Binding());
        }
        var host = _vm.ShowHistoryOver ? OverHost : ColumnHost;
        if (host.Child == _sidebar) return;
        if (_sidebar.Parent is Border was) was.Child = null;
        host.Child = _sidebar;
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
    // Every new size lays the whole chat out again (each paragraph re-wraps), so it's done at most once a frame, from
    // where the pointer is against where the pull started: a fast pull sent several moves a frame, each a full layout,
    // and the edge trailed the pointer. Measured from the start, not from the edge's last move, so a skipped move loses nothing.
    private object? _pulling;
    private Point _pullFrom;
    private Size _sizeFrom;
    private Rect _rectFrom;
    private bool _pullDue;

    private void Edge_Start(object sender, DragStartedEventArgs e)
    {
        if (_vm is null) return;
        _pulling = sender;
        _pullFrom = Mouse.GetPosition(Layer);
        _sizeFrom = new Size(Panel.Width, Panel.Height);
        _rectFrom = _vm.Floating;
        _pullDue = false;
        System.Windows.Media.CompositionTarget.Rendering += Pull_Frame;
    }

    private void Edge_Drag(object sender, DragDeltaEventArgs e) => _pullDue = sender == _pulling;

    private void Edge_End(object sender, DragCompletedEventArgs e)
    {
        if (sender != _pulling) return;
        System.Windows.Media.CompositionTarget.Rendering -= Pull_Frame;
        if (_pullDue) Pull();
        _pulling = null;
    }

    private void Pull_Frame(object? sender, EventArgs e)
    {
        if (!_pullDue) return;
        _pullDue = false;
        Pull();
    }

    private void Pull()
    {
        if (_vm is null || _pulling is null) return;
        var room = Room;
        var by = Mouse.GetPosition(Layer) - _pullFrom;
        if (!_vm.IsPoppedOut)
        {
            // The bottom-right corner stays put: pulling up or left grows it.
            double w = _sizeFrom.Width - (_pulling == TopEdge ? 0 : by.X), h = _sizeFrom.Height - (_pulling == LeftEdge ? 0 : by.Y);
            var size = Fit(w, h, room);
            if (_vm.IsBig) _vm.StuckBig = size; else _vm.StuckSmall = size;
        }
        else
        {
            // The side pulled moves and the one across from it stays put, kept on the page.
            var r = _rectFrom;
            bool left = _pulling == LeftEdge || _pulling == TopLeft || _pulling == BottomLeft;
            bool right = _pulling == RightEdge || _pulling == TopRight || _pulling == BottomRight;
            bool top = _pulling == TopEdge || _pulling == TopLeft || _pulling == TopRight;
            bool bottom = _pulling == BottomEdge || _pulling == BottomLeft || _pulling == BottomRight;
            double w = left ? Math.Min(r.Width - by.X, r.Right) : right ? Math.Min(r.Width + by.X, room.Width - r.X) : r.Width;
            double h = top ? Math.Min(r.Height - by.Y, r.Bottom) : bottom ? Math.Min(r.Height + by.Y, room.Height - r.Y) : r.Height;
            var size = Fit(w, h, room);
            double x = left ? Math.Max(0, r.Right - size.Width) : r.X, y = top ? Math.Max(0, r.Bottom - size.Height) : r.Y;
            _vm.Floating = new Rect(x, y, size.Width, size.Height);
        }
        Place();
    }

    // ── Moving by the top strip (over the chat, or the sidebar's top) ────
    // Smooth like the edges (2026-10-27, "the drag feels laggy"): the panel and its shadow follow the pointer by a shared
    // move (a render transform, no layout) at most once a frame, on whole pixels; the real place (Margin) is set once,
    // when it's let go. Every mouse move used to set the Margin, a layout pass each, several a frame with a fast mouse.
    private UIElement? _dragBy;
    private readonly System.Windows.Media.TranslateTransform _drag = new();
    private bool _moveDue;
    private Point _moveTo;

    private void Bar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || sender is not UIElement by || e.OriginalSource is DependencyObject d && FindButton(d, by)) return;
        _dragFrom = e.GetPosition(Layer);
        _panelFrom = new Point(Panel.Margin.Left, Panel.Margin.Top);
        _moveTo = _panelFrom;
        _moveDue = false;
        (_drag.X, _drag.Y) = (0, 0);
        Panel.RenderTransform = _drag;
        Shade.RenderTransform = _drag;
        _dragBy = by;
        by.CaptureMouse();
        System.Windows.Media.CompositionTarget.Rendering += Move_Frame;
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
        _moveDue = true;
    }

    private void Move_Frame(object? sender, EventArgs e)
    {
        if (!_moveDue || _vm is null || _dragFrom is not { } from) return;
        _moveDue = false;
        var by = Mouse.GetPosition(Layer) - from;
        var room = Room;
        double x = Math.Round(Math.Clamp(_panelFrom.X + by.X, 0, Math.Max(0, room.Width - Panel.Width)));
        double y = Math.Round(Math.Clamp(_panelFrom.Y + by.Y, 0, Math.Max(0, room.Height - Panel.Height)));
        _moveTo = new Point(x, y);
        // From where the panel is laid out now (it moved once, popping out at its first pull).
        _drag.X = x - Panel.Margin.Left;
        _drag.Y = y - Panel.Margin.Top;
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
        System.Windows.Media.CompositionTarget.Rendering -= Move_Frame;
        if (_moveDue) Move_Frame(null, EventArgs.Empty);
        // Let go: the place it was dragged to becomes its real one, in the same frame as the move is taken away.
        if (_vm is { IsPoppedOut: true }) _vm.Floating = new Rect(_moveTo.X, _moveTo.Y, Panel.Width, Panel.Height);
        Panel.Margin = new Thickness(_moveTo.X, _moveTo.Y, 0, 0);
        (_drag.X, _drag.Y) = (0, 0);
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
    // It opens once the pointer rests on the pill a moment (a pointer only passing over it opened it, 2026-10-24), and
    // stays while the pointer moves into it; a moment after it leaves both, it closes. With one list open, the next pill's
    // opens at once, as tooltips do. A press opens it straight away. One list at a time; scrolling the chat closes it.
    private AskRefs? _openRefs;
    private System.Windows.Threading.DispatcherTimer? _refsClose;
    private System.Windows.Threading.DispatcherTimer? _refsOpen;
    private AskRefs? _refsDue;

    /// <summary>How long the pointer rests on a pill before its list opens (about a tooltip's wait).</summary>
    private static readonly TimeSpan RefsOpenAfter = TimeSpan.FromMilliseconds(400);

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

    private void RefPill_Enter(object sender, MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AskRefs refs) return;
        if (_openRefs is not null) { ShowRefs(refs); return; }
        _refsDue = refs;
        if (_refsOpen is null)
        {
            _refsOpen = new System.Windows.Threading.DispatcherTimer { Interval = RefsOpenAfter };
            _refsOpen.Tick += (_, _) => { _refsOpen.Stop(); if (_refsDue is { } due) ShowRefs(due); _refsDue = null; };
        }
        _refsOpen.Stop();
        _refsOpen.Start();
    }

    private void RefPill_Leave(object sender, MouseEventArgs e)
    {
        // Only passing over: it never opens.
        _refsOpen?.Stop();
        _refsDue = null;
        LeaveRefs();
    }
    private void RefPill_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AskRefs refs) return;
        _refsOpen?.Stop();
        _refsDue = null;
        // A second press (keyboard, a tap) closes what the first opened.
        if (refs.IsOpen && !((UIElement)sender).IsMouseOver) CloseRefs(); else ShowRefs(refs);
    }
    private void RefList_Enter(object sender, MouseEventArgs e) => _refsClose?.Stop();
    private void RefList_Leave(object sender, MouseEventArgs e) => LeaveRefs();
    /// <summary>A source opened: the list goes first (its command then opens the page or the site).</summary>
    private void RefItem_Click(object sender, RoutedEventArgs e) { _refsClose?.Stop(); CloseRefs(); }

    /// <summary>
    /// Under the pill, or when that runs off the screen, above it with the list's bottom (its shadow's clear room) on
    /// the pill's top, so the pointer goes straight from the pill into the list and the list never covers the pill.
    /// The numbers come in screen pixels; the offset is the XAML's, already scaled.
    /// </summary>
    public static readonly CustomPopupPlacementCallback RefsPlacement = (popup, target, offset) =>
    [
        new CustomPopupPlacement(new Point(offset.X, target.Height + offset.Y), PopupPrimaryAxis.Horizontal),
        new CustomPopupPlacement(new Point(offset.X, -popup.Height), PopupPrimaryAxis.Horizontal),
    ];

    /// <summary>Enter sends the question; Shift+Enter starts a new line.</summary>
    private void Question_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || _vm is null) return;
        e.Handled = true;
        if (_vm.SendCommand.CanExecute(null)) _vm.SendCommand.Execute(null);
    }
}
