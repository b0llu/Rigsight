using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rigsight.Core.Settings;

namespace Rigsight.Controls;

/// <summary>Where the overlay is: its anchor and how far it's moved from it (see <see cref="OverlayPlacement"/>).</summary>
public readonly record struct OverlaySpot(int Anchor, double X, double Y);

/// <summary>
/// The overlay's spot, on a picture of the main screen: the overlay's preview at its real size relative to the screen,
/// dragged anywhere (over the edges too, cut off where the screen ends, as on the real one). It pulls onto the nine
/// anchors (corners, edge middles, centre) when it gets close, per axis, so edges line up; a click on an anchor's dot
/// moves it there. Arrow keys nudge it (Shift: further). Saved once, when it's let go.
/// </summary>
public sealed class OverlayScreen : FrameworkElement
{
    public static readonly DependencyProperty PreviewProperty = DependencyProperty.Register(
        nameof(Preview), typeof(ImageSource), typeof(OverlayScreen), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The overlay's size setting: the preview is drawn at twice size 1.</summary>
    public static readonly DependencyProperty OverlayScaleProperty = DependencyProperty.Register(
        nameof(OverlayScale), typeof(double), typeof(OverlayScreen), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SpotProperty = DependencyProperty.Register(
        nameof(Spot), typeof(OverlaySpot), typeof(OverlayScreen),
        new FrameworkPropertyMetadata(default(OverlaySpot), FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public ImageSource? Preview { get => (ImageSource?)GetValue(PreviewProperty); set => SetValue(PreviewProperty, value); }
    public double OverlayScale { get => (double)GetValue(OverlayScaleProperty); set => SetValue(OverlayScaleProperty, value); }
    public OverlaySpot Spot { get => (OverlaySpot)GetValue(SpotProperty); set => SetValue(SpotProperty, value); }

    /// <summary>The overlay's gap from the screen edges at an anchor, as on the real screen (device-independent pixels).</summary>
    public const double Gap = 16;

    /// <summary>How close (on this picture, in its pixels) the overlay has to get to an anchor's line to snap to it.</summary>
    private const double Magnet = 7;

    private static readonly Brush ScreenBrush = Frozen(new LinearGradientBrush(
        [new GradientStop(Color.FromRgb(0x3A, 0x2F, 0x4F), 0), new GradientStop(Color.FromRgb(0x1E, 0x3B, 0x45), 0.55), new GradientStop(Color.FromRgb(0x40, 0x37, 0x2A), 1)],
        new Point(0, 0), new Point(1, 1)));
    private static readonly Brush DotBrush = Frozen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)));
    private static readonly Brush DotOnBrush = Frozen(new SolidColorBrush(Colors.White));
    private static readonly Pen GuidePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), 1) { DashStyle = new DashStyle([4, 4], 0) });
    private static readonly Pen OutlinePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1.2) { DashStyle = new DashStyle([3, 3], 0) });

    private Point? _dragFrom;
    private Point _dragStart;   // the overlay's top-left when the drag started (screen pixels)
    private Point? _dragAt;     // where it is while dragging (screen pixels), before it's saved
    private bool _snappedX, _snappedY;

    public OverlayScreen()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Arrow;
        ToolTip = "Drag the overlay anywhere, even partly off the screen. It snaps to the corners, edges and centre; arrow keys nudge it.";
    }

    private static double ScreenWidth => SystemParameters.PrimaryScreenWidth;
    private static double ScreenHeight => SystemParameters.PrimaryScreenHeight;
    private double Factor => ActualWidth / ScreenWidth;

    /// <summary>The overlay's size on the real screen (device-independent pixels).</summary>
    private Size OverlaySize => Preview is BitmapSource b
        ? new Size(b.PixelWidth / 2.0 * OverlayScale, b.PixelHeight / 2.0 * OverlayScale)
        : new Size(220 * OverlayScale, 110 * OverlayScale);

    private Point TopLeft(OverlaySpot spot)
    {
        var size = OverlaySize;
        var (x, y) = OverlayPlacement.Place(spot.Anchor, spot.X, spot.Y, ScreenWidth, ScreenHeight, size.Width, size.Height, Gap);
        return new Point(x, y);
    }

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? 420 : available.Width;
        return new Size(width, width * ScreenHeight / ScreenWidth);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(RenderSize);
        var screenShape = new RectangleGeometry(bounds, 12, 12);
        dc.PushClip(screenShape);
        dc.DrawRectangle(ScreenBrush, null, bounds);

        var box = Box();

        // The nine anchors: where the overlay's matching corner or middle sits with no offset.
        for (int anchor = 0; anchor < 9; anchor++)
        {
            var dot = AnchorDot(anchor);
            bool on = _dragAt is null && Spot.Anchor == anchor && Spot.X == 0 && Spot.Y == 0;
            dc.DrawEllipse(on ? DotOnBrush : DotBrush, null, dot, on ? 3.5 : 2.5, on ? 3.5 : 2.5);
        }
        // While dragging, a line where it has snapped to an edge or the middle.
        if (_dragAt is not null)
        {
            if (_snappedX) dc.DrawLine(GuidePen, new Point(SnapLine(box.Left, box.Right, bounds.Width), 0), new Point(SnapLine(box.Left, box.Right, bounds.Width), bounds.Height));
            if (_snappedY) dc.DrawLine(GuidePen, new Point(0, SnapLine(box.Top, box.Bottom, bounds.Height)), new Point(bounds.Width, SnapLine(box.Top, box.Bottom, bounds.Height)));
        }

        if (Preview is { } preview) dc.DrawImage(preview, box);
        else dc.DrawRoundedRectangle(DotBrush, null, box, 6, 6);
        if (IsMouseOver || _dragAt is not null || IsKeyboardFocused) dc.DrawRectangle(null, OutlinePen, box);
        dc.Pop();
    }

    /// <summary>Which of the box's edges or middle a snap guide goes through: the one nearest a third of the picture.</summary>
    private static double SnapLine(double start, double end, double length)
    {
        double middle = (start + end) / 2;
        return middle < length / 3 ? start : middle > length * 2 / 3 ? end : middle;
    }

    /// <summary>An anchor's dot on the picture: the overlay's matching corner or middle when it's there with no offset.</summary>
    private Point AnchorDot(int anchor)
    {
        double f = Factor;
        var size = OverlaySize;
        var at = TopLeft(new OverlaySpot(anchor, 0, 0));
        double x = at.X + size.Width * OverlayPlacement.Column(anchor) / 2, y = at.Y + size.Height * OverlayPlacement.Row(anchor) / 2;
        return new Point(x * f, y * f);
    }

    /// <summary>Drawn at least this tall (its pixels), so a small overlay (one line) can be seen and grabbed.</summary>
    private const double MinBoxHeight = 22;

    /// <summary>
    /// The overlay on the picture. A small one is drawn larger, grown from the point it hangs from (its corner, edge
    /// middle or centre, as the real one grows), so that point, and so where it goes, stays exact.
    /// </summary>
    private Rect Box()
    {
        double f = Factor;
        var at = _dragAt ?? TopLeft(Spot);
        var size = OverlaySize;
        var real = new Rect(at.X * f, at.Y * f, size.Width * f, size.Height * f);
        double grow = Math.Clamp(MinBoxHeight / Math.Max(real.Height, 1), 1, 4);
        if (grow <= 1) return real;
        int anchor = _dragAt is { } p
            ? OverlayPlacement.FromPosition(p.X, p.Y, ScreenWidth, ScreenHeight, size.Width, size.Height, Gap).Anchor
            : Spot.Anchor;
        double fx = OverlayPlacement.Column(anchor) / 2.0, fy = OverlayPlacement.Row(anchor) / 2.0;
        double w = real.Width * grow, h = real.Height * grow;
        return new Rect(real.X + real.Width * fx - w * fx, real.Y + real.Height * fy - h * fy, w, h);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        if (Box().Contains(p))
        {
            _dragFrom = p;
            _dragStart = TopLeft(Spot);
            _dragAt = _dragStart;
            CaptureMouse();
            e.Handled = true;
            return;
        }
        // A click on an anchor's dot moves it there.
        for (int anchor = 0; anchor < 9; anchor++)
            if ((AnchorDot(anchor) - p).Length < 9)
            {
                Spot = new OverlaySpot(anchor, 0, 0);
                e.Handled = true;
                return;
            }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        Cursor = _dragFrom is not null || Box().Contains(p) ? Cursors.SizeAll : Cursors.Arrow;
        if (_dragFrom is not { } from) { InvalidateVisual(); return; }
        double f = Factor;
        var size = OverlaySize;
        double x = _dragStart.X + (p.X - from.X) / f, y = _dragStart.Y + (p.Y - from.Y) / f;
        (x, _snappedX) = Snap(x, ScreenWidth, size.Width, f);
        (y, _snappedY) = Snap(y, ScreenHeight, size.Height, f);
        x = Math.Clamp(x, OverlayPlacement.MinVisible - size.Width, ScreenWidth - OverlayPlacement.MinVisible);
        y = Math.Clamp(y, OverlayPlacement.MinVisible - size.Height, ScreenHeight - OverlayPlacement.MinVisible);
        _dragAt = new Point(x, y);
        InvalidateVisual();
    }

    /// <summary>Pulls a start onto the nearest anchor line (the gap in from an edge, or centred) when it's close.</summary>
    private static (double Value, bool Snapped) Snap(double start, double screen, double size, double f)
    {
        foreach (double line in new[] { Gap, (screen - size) / 2, screen - Gap - size })
            if (Math.Abs(start - line) * f < Magnet) return (line, true);
        return (start, false);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragFrom is null) return;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_dragAt is { } at && at != _dragStart) Commit(at);
        _dragFrom = null;
        _dragAt = null;
        _snappedX = _snappedY = false;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e) => InvalidateVisual();
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) => InvalidateVisual();
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) => InvalidateVisual();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.05 : 0.005;
        var (dx, dy) = e.Key switch
        {
            Key.Left => (-step, 0.0),
            Key.Right => (step, 0.0),
            Key.Up => (0.0, -step),
            Key.Down => (0.0, step),
            _ => (0.0, 0.0),
        };
        if (dx == 0 && dy == 0) return;
        var at = TopLeft(Spot);
        var size = OverlaySize;
        Commit(new Point(
            Math.Clamp(at.X + dx * ScreenWidth, OverlayPlacement.MinVisible - size.Width, ScreenWidth - OverlayPlacement.MinVisible),
            Math.Clamp(at.Y + dy * ScreenHeight, OverlayPlacement.MinVisible - size.Height, ScreenHeight - OverlayPlacement.MinVisible)));
        e.Handled = true;
    }

    private void Commit(Point at)
    {
        var size = OverlaySize;
        var (anchor, x, y) = OverlayPlacement.FromPosition(at.X, at.Y, ScreenWidth, ScreenHeight, size.Width, size.Height, Gap);
        Spot = new OverlaySpot(anchor, x, y);
    }

    // Found by screen readers and UI Automation (a bare FrameworkElement has no peer).
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
        new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(this);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
