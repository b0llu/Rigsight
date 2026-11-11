using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.ViewModels;

namespace Rigsight.Controls;

/// <summary>
/// The Widgets page's map of the screens: each screen to scale, as Windows has them side by side, with the widgets that
/// are on drawn where they are. A widget dragged here moves on the desktop too, pulled onto the screen's edges and other
/// widgets as on the desktop (Alt: placed freely); a click picks it. Beside the list (approved mock-up
/// UckKnwokm5kF7JjdjHZpqd), the picked widget is outlined and the others are dimmed.
/// </summary>
public sealed class WidgetScreens : FrameworkElement
{
    public static readonly DependencyProperty ScreensProperty = DependencyProperty.Register(nameof(Screens), typeof(List<ScreenChoice.Monitor>),
        typeof(WidgetScreens), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SpotsProperty = DependencyProperty.Register(nameof(Spots), typeof(List<WidgetSpot>),
        typeof(WidgetScreens), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SnapProperty = DependencyProperty.Register(nameof(Snap), typeof(bool), typeof(WidgetScreens), new PropertyMetadata(true));

    public static readonly DependencyProperty PickedProperty = DependencyProperty.Register(nameof(Picked), typeof(string), typeof(WidgetScreens),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty MapHeightProperty = DependencyProperty.Register(nameof(MapHeight), typeof(double), typeof(WidgetScreens),
        new FrameworkPropertyMetadata(300.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The picked widget's identifier: outlined, the others dimmed; a click on another picks it.</summary>
    public string? Picked { get => (string?)GetValue(PickedProperty); set => SetValue(PickedProperty, value); }

    /// <summary>How tall the screens are drawn at most.</summary>
    public double MapHeight { get => (double)GetValue(MapHeightProperty); set => SetValue(MapHeightProperty, value); }

    public List<ScreenChoice.Monitor>? Screens { get => (List<ScreenChoice.Monitor>?)GetValue(ScreensProperty); set => SetValue(ScreensProperty, value); }
    public List<WidgetSpot>? Spots { get => (List<WidgetSpot>?)GetValue(SpotsProperty); set => SetValue(SpotsProperty, value); }
    public bool Snap { get => (bool)GetValue(SnapProperty); set => SetValue(SnapProperty, value); }

    /// <summary>A widget dropped somewhere new: its identifier and its panel's new top left, in screen pixels.</summary>
    public event Action<string, int, int>? Moved;

    private const double LabelRoom = 30;

    private (double Scale, double OffsetX, double OffsetY, int MinX, int MinY) Frame(double width)
    {
        var screens = Screens;
        if (screens is not { Count: > 0 }) return (0, 0, 0, 0, 0);
        int minX = screens.Min(s => s.Left), minY = screens.Min(s => s.Top), maxX = screens.Max(s => s.Left + s.Width), maxY = screens.Max(s => s.Top + s.Height);
        double scale = Math.Min((width - 8) / (maxX - minX), MapHeight / (maxY - minY));
        return (scale, (width - (maxX - minX) * scale) / 2, 4, minX, minY);
    }

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? 800 : available.Width;
        var screens = Screens;
        if (screens is not { Count: > 0 }) return new Size(width, 0);
        var f = Frame(width);
        int minY = screens.Min(s => s.Top), maxY = screens.Max(s => s.Top + s.Height);
        return new Size(width, (maxY - minY) * f.Scale + 8 + LabelRoom);
    }

    private string? _dragging;
    private Point _from;
    private int _x0, _y0, _dx, _dy;
    private (double X, double Y)? _guide;

    private Rect SpotRect(WidgetSpot s, (double Scale, double OffsetX, double OffsetY, int MinX, int MinY) f, int dx = 0, int dy = 0) =>
        new(f.OffsetX + (s.X + dx - f.MinX) * f.Scale, f.OffsetY + (s.Y + dy - f.MinY) * f.Scale, Math.Max(3, s.Width * f.Scale), Math.Max(3, s.Height * f.Scale));

    protected override void OnRender(DrawingContext dc)
    {
        var screens = Screens;
        if (screens is not { Count: > 0 }) return;
        var f = Frame(ActualWidth);
        var line = (Brush)FindResource("StrokeBrush");
        var muted = (Brush)FindResource("MutedBrush");
        var faint = (Brush)FindResource("FaintBrush");
        var wallpaper = new LinearGradientBrush(Color.FromRgb(0x2A, 0x33, 0x50), Color.FromRgb(0x1B, 0x3A, 0x36), 45);
        var taskbar = new SolidColorBrush(Color.FromArgb(235, 28, 28, 28));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var s in screens)
        {
            var r = new Rect(f.OffsetX + (s.Left - f.MinX) * f.Scale, f.OffsetY + (s.Top - f.MinY) * f.Scale, s.Width * f.Scale, s.Height * f.Scale);
            dc.DrawRoundedRectangle(wallpaper, new Pen(line, 1), r, 6, 6);
            double bar = Math.Max(4, 48 * s.Scale * f.Scale);
            dc.PushClip(new RectangleGeometry(r, 6, 6));
            dc.DrawRectangle(taskbar, null, new Rect(r.Left, r.Bottom - bar, r.Width, bar));
            dc.Pop();
            string name = s.IsMain ? Lang.T("Main screen") : Lang.T($"Screen {ScreenChoice.NumberOf(s.Device)}");
            var label = new FormattedText(name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12.5, muted, dpi);
            var size = new FormattedText($"  {s.Width} × {s.Height}", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11.5, faint, dpi);
            // A small screen on a small map keeps its name and leaves its size out.
            bool both = label.Width + size.Width <= r.Width + 8;
            double lx = r.Left + (r.Width - label.Width - (both ? size.Width : 0)) / 2, ly = r.Bottom + 8;
            dc.DrawText(label, new Point(lx, ly));
            if (both) dc.DrawText(size, new Point(lx + label.Width, ly + 1));
        }
        foreach (var spot in Spots ?? [])
        {
            bool dragged = spot.Id == _dragging, picked = spot.Id == Picked;
            var r = SpotRect(spot, f, dragged ? _dx : 0, dragged ? _dy : 0);
            bool dim = Picked is not null && !picked && !dragged;
            if (dim) dc.PushOpacity(0.4);
            if (spot.Picture is { } pic) dc.DrawImage(pic, r);
            else dc.DrawRoundedRectangle(Brushes.Black, null, r, 3, 3);
            if (dim) dc.Pop();
            if (picked || dragged) dc.DrawRoundedRectangle(null, new Pen(Brushes.White, 1.5), Rect.Inflate(r, 2, 2), 4, 4);
        }
        if (_guide is { } g)
        {
            var white = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1);
            if (!double.IsNaN(g.X)) dc.DrawLine(white, new Point(g.X, 0), new Point(g.X, ActualHeight - LabelRoom));
            if (!double.IsNaN(g.Y)) dc.DrawLine(white, new Point(0, g.Y), new Point(ActualWidth, g.Y));
        }
    }

    /// <summary>The middle of a widget on the map, in the map's own coordinates (for tests that drag it).</summary>
    internal Point? CentreOf(string id)
    {
        if (Spots?.FirstOrDefault(s => s.Id == id) is not { } spot) return null;
        var r = SpotRect(spot, Frame(ActualWidth));
        return new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
    }

    private WidgetSpot? SpotAt(Point p)
    {
        var f = Frame(ActualWidth);
        return (Spots ?? []).LastOrDefault(s => Rect.Inflate(SpotRect(s, f), 3, 3).Contains(p));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = _dragging is not null ? Cursors.SizeAll : SpotAt(e.GetPosition(this)) is not null ? Cursors.Hand : null;
        if (e.LeftButton == MouseButtonState.Pressed) DragTo(e.GetPosition(this));
    }

    /// <summary>The widget picked up follows the pointer, at <paramref name="p"/> on the map.</summary>
    internal void DragTo(Point p)
    {
        if (_dragging is null || Spots?.FirstOrDefault(s => s.Id == _dragging) is not { } spot) return;
        var f = Frame(ActualWidth);
        int x = _x0 + (int)Math.Round((p.X - _from.X) / f.Scale), y = _y0 + (int)Math.Round((p.Y - _from.Y) / f.Scale);
        _guide = null;
        if (Snap && (Keyboard.Modifiers & ModifierKeys.Alt) == 0) (x, y) = SnapTo(spot, x, y, f);
        (_dx, _dy) = (x - spot.X, y - spot.Y);
        InvalidateVisual();
    }

    /// <summary>Pulled onto the screen's edges and middle and other widgets' edges, 12 screen pixels apart, within 8 pixels of the map.</summary>
    private (int X, int Y) SnapTo(WidgetSpot spot, int x, int y, (double Scale, double OffsetX, double OffsetY, int MinX, int MinY) f)
    {
        var screen = Screens!.FirstOrDefault(s => x + spot.Width / 2 >= s.Left && x + spot.Width / 2 < s.Left + s.Width && y + spot.Height / 2 >= s.Top && y + spot.Height / 2 < s.Top + s.Height);
        if (screen is null) return (x, y);
        int gap = (int)Math.Round(12 * screen.Scale), near = (int)Math.Round(8 / f.Scale), w = spot.Width, h = spot.Height;
        int left = screen.Left, top = screen.Top, right = screen.Left + screen.Width, bottom = screen.Top + screen.Height - (int)Math.Round(48 * screen.Scale);
        var xs = new List<(int At, int Line)> { (left + gap, left + gap), (right - gap - w, right - gap), (left + (right - left - w) / 2, left + (right - left) / 2) };
        var ys = new List<(int At, int Line)> { (top + gap, top + gap), (bottom - gap - h, bottom - gap) };
        foreach (var o in (Spots ?? []).Where(o => o.Id != spot.Id))
        {
            xs.AddRange([(o.X, o.X), (o.X + o.Width - w, o.X + o.Width), (o.X + o.Width + gap, o.X + o.Width + gap), (o.X - gap - w, o.X - gap)]);
            ys.AddRange([(o.Y, o.Y), (o.Y + o.Height - h, o.Y + o.Height), (o.Y + o.Height + gap, o.Y + o.Height + gap), (o.Y - gap - h, o.Y - gap)]);
        }
        var bx = xs.Where(c => Math.Abs(c.At - x) <= near).OrderBy(c => Math.Abs(c.At - x)).Cast<(int, int)?>().FirstOrDefault();
        var by = ys.Where(c => Math.Abs(c.At - y) <= near).OrderBy(c => Math.Abs(c.At - y)).Cast<(int, int)?>().FirstOrDefault();
        _guide = (bx is { } gx ? f.OffsetX + (gx.Item2 - f.MinX) * f.Scale : double.NaN, by is { } gy ? f.OffsetY + (gy.Item2 - f.MinY) * f.Scale : double.NaN);
        return (bx?.Item1 ?? x, by?.Item1 ?? y);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (PickUp(e.GetPosition(this)))
        {
            CaptureMouse();
            e.Handled = true;
        }
    }

    /// <summary>Picks up the widget at <paramref name="p"/> on the map, if there is one there.</summary>
    internal bool PickUp(Point p)
    {
        var spot = SpotAt(p);
        if (spot is not null)
        {
            Picked = spot.Id;
            (_dragging, _from, _x0, _y0, _dx, _dy) = (spot.Id, p, spot.X, spot.Y, 0, 0);
        }
        InvalidateVisual();
        return spot is not null;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging is not null) ReleaseMouseCapture();
        Drop();
    }

    /// <summary>Puts the widget picked up down where it was dragged to: its new spot, on the desktop too.</summary>
    internal void Drop()
    {
        if (_dragging is { } id)
        {
            var spot = Spots?.FirstOrDefault(s => s.Id == id);
            Rigsight.Core.Log.Write("widgets", spot is null ? $"A widget dragged on the map was no longer on it when dropped ({id})"
                : _dx == 0 && _dy == 0 ? $"{spot.Title} picked on the map, not moved"
                : $"{spot.Title} dragged on the map from {spot.X},{spot.Y} to {spot.X + _dx},{spot.Y + _dy}");
            if (spot is not null && (_dx != 0 || _dy != 0)) Moved?.Invoke(id, spot.X + _dx, spot.Y + _dy);
        }
        (_dragging, _guide, _dx, _dy) = (null, null, 0, 0);
        InvalidateVisual();
    }
}
