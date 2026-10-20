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
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = vm;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        Place();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AskViewModel.IsPoppedOut) or nameof(AskViewModel.IsBig) or nameof(AskViewModel.ShowHistoryColumn)) Place();
        // Another conversation, or a message added to this one (IsEmpty is raised for each): the chat follows it down,
        // once the new message has been laid out.
        if (e.PropertyName is nameof(AskViewModel.Current) or nameof(AskViewModel.IsEmpty))
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => Chat.ScrollToEnd());
    }

    private Size Room => new(Math.Max(MinWidth_, Layer.ActualWidth), Math.Max(MinHeight_, Layer.ActualHeight));

    /// <summary>Puts the panel where the view model says, kept inside the page.</summary>
    private void Place()
    {
        if (_vm is null || Layer.ActualWidth <= 0) return;
        var room = Room;
        HistoryColumn.Width = new GridLength(_vm.ShowHistoryColumn ? 240 : 0);
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
    }

    private static void AutomationProperties_Name(DependencyObject element, string name) => System.Windows.Automation.AutomationProperties.SetName(element, name);

    private static Size Fit(Size want, Size room) =>
        new(Math.Clamp(want.Width, MinWidth_, Math.Max(MinWidth_, room.Width - 2 * Gap)), Math.Clamp(want.Height, MinHeight_, Math.Max(MinHeight_, room.Height - 2 * Gap)));

    // ── Resizing from the edges ──────────────────────────────────────────
    private void Edge_Drag(object sender, DragDeltaEventArgs e)
    {
        if (_vm is null) return;
        var room = Room;
        if (!_vm.IsPoppedOut)
        {
            // The bottom-right corner stays put: pulling up or left grows it.
            double w = Panel.Width - (sender == TopEdge ? 0 : e.HorizontalChange), h = Panel.Height - (sender == LeftEdge ? 0 : e.VerticalChange);
            var size = Fit(new Size(w, h), room);
            if (_vm.IsBig) _vm.StuckBig = size; else _vm.StuckSmall = size;
        }
        else
        {
            var r = _vm.Floating;
            double w = r.Width + (sender == BottomEdge ? 0 : e.HorizontalChange), h = r.Height + (sender == RightEdge ? 0 : e.VerticalChange);
            var size = Fit(new Size(Math.Min(w, room.Width - r.X), Math.Min(h, room.Height - r.Y)), room);
            _vm.Floating = new Rect(r.TopLeft, size);
        }
        Place();
    }

    // ── Moving by the top bar ────────────────────────────────────────────
    private void Bar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || e.OriginalSource is DependencyObject d && FindButton(d)) return;
        _dragFrom = e.GetPosition(Layer);
        _panelFrom = new Point(Panel.Margin.Left, Panel.Margin.Top);
        Bar.CaptureMouse();
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
        Bar.ReleaseMouseCapture();
        if (DropSpot.Visibility == Visibility.Visible && _vm is not null)
        {
            DropSpot.Visibility = Visibility.Collapsed;
            _vm.IsPoppedOut = false;
        }
        Place();
    }

    private bool FindButton(DependencyObject d)
    {
        for (var at = d; at is not null && at != Bar; at = System.Windows.Media.VisualTreeHelper.GetParent(at))
            if (at is ButtonBase) return true;
        return false;
    }

    /// <summary>Enter sends the question; Shift+Enter starts a new line.</summary>
    private void Question_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || _vm is null) return;
        e.Handled = true;
        if (_vm.SendCommand.CanExecute(null)) _vm.SendCommand.Execute(null);
    }
}
