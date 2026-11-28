using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Rigsight.Core;
using Rigsight.Core.Games;

namespace Rigsight.Controls;

/// <summary>
/// An opened stretch's strip (1.23.0, approved mock-up "1b · An opened stretch, round 2", from CapFrameX's frame times over
/// time and Chrome's long tasks on its timeline): the stretch's own minutes along the bottom with their clock times, each
/// stutter a thin amber spike as tall as its frame, the stretch's usual frame a dashed line. The pointer takes the nearest
/// spike, dims the others and shows its rich hover; a click picks it (a light outline) and brings its row of the list
/// below (<see cref="List"/>) into view. The facts come from Core's <see cref="StretchStrip"/>.
/// </summary>
public sealed class StretchStripChart : FrameworkElement
{
    public static readonly DependencyProperty StripProperty = DependencyProperty.Register(
        nameof(Strip), typeof(StretchStrip), typeof(StretchStripChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TipProperty = DependencyProperty.Register(
        nameof(Tip), typeof(Func<int, RichTip>), typeof(StretchStripChart), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty PickedProperty = DependencyProperty.Register(
        nameof(Picked), typeof(int), typeof(StretchStripChart), new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PickCommandProperty = DependencyProperty.Register(
        nameof(PickCommand), typeof(ICommand), typeof(StretchStripChart), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty ListProperty = DependencyProperty.Register(
        nameof(List), typeof(ItemsControl), typeof(StretchStripChart), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty PerRowProperty = DependencyProperty.Register(
        nameof(PerRow), typeof(int), typeof(StretchStripChart), new FrameworkPropertyMetadata(1));

    /// <summary>The stretch's spikes, usual frame and clock marks.</summary>
    public StretchStrip? Strip { get => (StretchStrip?)GetValue(StripProperty); set => SetValue(StripProperty, value); }
    /// <summary>A spike's rich hover (by its place in the strip's spikes), made when first shown.</summary>
    public Func<int, RichTip>? Tip { get => (Func<int, RichTip>?)GetValue(TipProperty); set => SetValue(TipProperty, value); }
    /// <summary>The spike picked, -1 for none.</summary>
    public int Picked { get => (int)GetValue(PickedProperty); set => SetValue(PickedProperty, value); }
    /// <summary>Run with a spike's place when it's clicked.</summary>
    public ICommand? PickCommand { get => (ICommand?)GetValue(PickCommandProperty); set => SetValue(PickCommandProperty, value); }
    /// <summary>The list under the strip, whose row of a picked spike is brought into view.</summary>
    public ItemsControl? List { get => (ItemsControl?)GetValue(ListProperty); set => SetValue(ListProperty, value); }
    /// <summary>How many stutters a row of <see cref="List"/> holds (three short ones on a play read by the minute).</summary>
    public int PerRow { get => (int)GetValue(PerRowProperty); set => SetValue(PerRowProperty, value); }

    // The mock-up's strip: 8 px either side, 12 over the spikes, 22 under them for the clock times.
    private const double PadX = 8, PadTop = 12, PadBottom = 22;

    /// <summary>How far the hover may rise above the strip (over its caption) and reach below it (over the list, drawn above it).</summary>
    private const double RiseAbove = 44, ReachBelow = 140;

    // The spike the pointer is on, -1 for none.
    private int _hover = -1;

    // The spikes' colour as a resource, so a change of theme draws the strip again (the drawing reads ChartPaint's).
    private static readonly DependencyProperty InkProperty = DependencyProperty.Register(
        "Ink", typeof(Brush), typeof(StretchStripChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public StretchStripChart()
    {
        Cursor = Cursors.Hand;
        Focusable = false;
        SetResourceReference(InkProperty, "WarmBrush");
    }

    private Rect Plot => new(PadX, PadTop, Math.Max(1, ActualWidth - 2 * PadX), Math.Max(1, ActualHeight - PadTop - PadBottom));

    private int SpikeAt(Point p)
    {
        if (Strip is not { Spikes.Count: > 0 } strip) return -1;
        var plot = Plot;
        double pxPerSecond = plot.Width / Math.Max(1, strip.Seconds);
        return strip.Nearest((p.X - plot.Left) / pxPerSecond, pxPerSecond);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int at = SpikeAt(e.GetPosition(this));
        if (at != _hover)
        {
            _hover = at;
            InvalidateVisual();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hover >= 0)
        {
            _hover = -1;
            InvalidateVisual();
        }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        int at = SpikeAt(e.GetPosition(this));
        if (at >= 0 && Strip is { } strip)
        {
            int index = strip.Spikes[at].Index;
            if (PickCommand is { } pick && pick.CanExecute(index)) pick.Execute(index);
            Reveal(index);
            e.Handled = true;
        }
        base.OnMouseLeftButtonUp(e);
    }

    /// <summary>The picked stutter's row brought into view, once the list has its rows (the rest may just have opened).</summary>
    private void Reveal(int index)
    {
        if (List is not { } list) return;
        int row = index / Math.Max(1, PerRow);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (row >= list.Items.Count) return;
            if (Panel(list) is VirtualizingStackPanel panel) panel.BringIndexIntoViewPublic(row);
            list.UpdateLayout();
            (list.ItemContainerGenerator.ContainerFromIndex(row) as FrameworkElement)?.BringIntoView();
        });
    }

    private static VirtualizingStackPanel? Panel(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is VirtualizingStackPanel { IsItemsHost: true } panel) return panel;
            if (Panel(child) is { } found) return found;
        }
        return null;
    }

    // The whole strip takes the pointer, not only its spikes.
    protected override HitTestResult HitTestCore(PointHitTestParameters p) => new PointHitTestResult(this, p.HitPoint);

    protected override void OnRender(DrawingContext dc)
    {
        if (Strip is not { } strip || ActualWidth < 60 || ActualHeight < 40) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var plot = Plot;
        double X(double s) => plot.Left + plot.Width * s / Math.Max(1, strip.Seconds);
        double Y(double ms) => plot.Top + plot.Height * (1 - Math.Clamp(ms / strip.Top, 0, 1));

        // The base line and the clock marks under it.
        var stroke = new Pen(ChartPaint.Grid, 1);
        stroke.Freeze();
        double baseY = Math.Round(plot.Bottom) + 0.5;
        dc.DrawLine(stroke, new Point(plot.Left, baseY), new Point(plot.Right, baseY));
        for (int i = 0; i < strip.Ticks.Count; i++)
        {
            var tick = strip.Ticks[i];
            double x = Math.Round(X(tick.At)) + 0.5;
            dc.DrawLine(stroke, new Point(x, baseY), new Point(x, baseY + 4));
            bool last = i == strip.Ticks.Count - 1 && tick.At >= strip.Seconds - 0.5;
            var align = i == 0 ? ChartPaint.Align.Left : last ? ChartPaint.Align.Right : ChartPaint.Align.Center;
            ChartPaint.Text(dc, this, tick.Label, new Point(x, ActualHeight - 10), 11, ChartPaint.Label, align);
        }

        // The stretch's usual frame: a dashed line, its words over its right end.
        if (strip.Typical > 0)
        {
            var dash = new Pen(ChartPaint.Muted, 1) { DashStyle = new DashStyle([3, 4], 0) };
            dash.Freeze();
            double y = Math.Round(Y(strip.Typical)) + 0.5;
            dc.DrawLine(dash, new Point(plot.Left, y), new Point(plot.Right, y));
            ChartPaint.Text(dc, this, strip.TypicalText, new Point(plot.Right, y - 10), 11, ChartPaint.Muted, ChartPaint.Align.Right);
        }

        // The spikes: the hovered one (or, with none hovered, the picked one) full, the others dimmed.
        double w = StretchStrip.SpikeWidth(strip.Spikes.Count);
        int picked = -1;
        for (int i = 0; i < strip.Spikes.Count; i++) if (strip.Spikes[i].Index == Picked) picked = i;
        int lit = _hover >= 0 ? _hover : picked;
        double dim = _hover >= 0 ? 0.45 : 0.55;
        var warm = ChartPaint.Warm;
        var faded = ChartPaint.Brush(warm.Color, dim);
        Rect? hoverRect = null, pickRect = null;
        for (int i = 0; i < strip.Spikes.Count; i++)
        {
            var s = strip.Spikes[i];
            double x = X(s.At), top = Y(s.Ms);
            var r = new Rect(x - w / 2, top, w, Math.Max(1, plot.Bottom - top));
            dc.DrawRoundedRectangle(lit < 0 || i == lit ? warm : faded, null, r, 1, 1);
            if (i == _hover) hoverRect = r;
            if (i == picked) pickRect = r;
        }
        // The picked one keeps a thin light outline.
        if (pickRect is { } pr)
        {
            var outline = new Pen(ChartPaint.TextBrush, 1.2);
            outline.Freeze();
            dc.DrawRoundedRectangle(null, outline, new Rect(pr.Left - 3, pr.Top - 3, pr.Width + 6, pr.Height + 3), 2, 2);
        }

        if (_hover >= 0 && hoverRect is { } hr && Tip is { } tip)
            RichHover.Draw(dc, this, tip(strip.Spikes[_hover].Index), hr, new Rect(0, -RiseAbove, ActualWidth, ActualHeight + RiseAbove + ReachBelow));
    }
}
