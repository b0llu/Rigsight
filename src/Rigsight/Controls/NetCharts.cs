using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;

namespace Rigsight.Controls;

/// <summary>What the network charts share: download and upload colours, and a data axis from 0 in round steps.</summary>
public abstract class NetChartBase : FanChartBase
{
    public static readonly DependencyProperty DownBrushProperty = DependencyProperty.Register(
        nameof(DownBrush), typeof(Brush), typeof(NetChartBase), new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UpBrushProperty = DependencyProperty.Register(
        nameof(UpBrush), typeof(Brush), typeof(NetChartBase), new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));

    protected NetChartBase()
    {
        SetResourceReference(DownBrushProperty, "SkyBrush");
        SetResourceReference(UpBrushProperty, "PurpleBrush");
    }

    public Brush DownBrush { get => (Brush)GetValue(DownBrushProperty); set => SetValue(DownBrushProperty, value); }
    public Brush UpBrush { get => (Brush)GetValue(UpBrushProperty); set => SetValue(UpBrushProperty, value); }

    /// <summary>
    /// A round top for the axis: four steps of a round size (15, 20, 25, 30, 40… in Windows' MB / GB), the smallest
    /// that holds the highest. With steps of only 1, 2 or 5 times a power of ten an hour of 42 GB went on an axis of
    /// 80 GB, half the graph empty above it (the user, 2026-11-05: "plotted way too big on y axis"); now 60 GB.
    /// </summary>
    internal static double NiceTop(double max)
    {
        if (max <= 0) return 4 << 20;
        double step = max / 4;
        double unit = step >= 1L << 30 ? 1L << 30 : step >= 1L << 20 ? 1L << 20 : 1L << 10;
        double v = step / unit;
        double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double m = v / p;
        double nice = Steps.First(s => m <= s + 1e-9) * p;
        return Math.Max(nice * unit, 1) * 4;
    }

    private static readonly double[] Steps = [1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10];

    /// <summary>The plot area, with a dashed grid and labels made by <paramref name="label"/>.</summary>
    protected Rect Grid(DrawingContext dc, double top, Func<double, string> label)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var plot = new Rect(AxisWidth + 12, Top, Math.Max(1, ActualWidth - AxisWidth - 12 - 2), Math.Max(1, ActualHeight - AxisHeight - Top));
        var pen = new Pen(GridBrush, 1) { DashStyle = new DashStyle([3, 4], 0) };
        pen.Freeze();
        for (int i = 0; i <= 4; i++)
        {
            double y = Math.Round(plot.Bottom - plot.Height * i / 4) + 0.5;
            dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
            Label(dc, i == 0 ? "0" : label(top * i / 4), new Point(plot.Left - 8, y), LabelBrush, right: true);
        }
        return plot;
    }
}

/// <summary>How the bars of the usage chart are split.</summary>
public enum NetBarsMode
{
    /// <summary>By app: the few with a colour of their own, then every other app together.</summary>
    App,
    /// <summary>The app in front, and what moved in the background (or with nobody at the PC).</summary>
    Background,
}

/// <summary>What the Network page's graph shows: the internet used, or the line check's ping.</summary>
public enum NetChartView { Usage, Ping }

/// <summary>A drop on the graph: from when to when.</summary>
public sealed record NetDropSpan(DateTime From, DateTime To);

/// <summary>Ping checks turned off or on at <see cref="At"/>: a dashed line on the Ping view with <see cref="Label"/> by it.</summary>
public sealed record NetPingMark(DateTime At, bool On, string Label);

/// <summary>
/// The period's internet use in bars: per hour (a day, a week), day (a month) or month (a year). Each bar is everything
/// that moved in it, download and upload together, split by app or by in front and background. On the Network page
/// (<see cref="View"/> and <see cref="CanZoom"/> set) it also shows the hours a game was played (shaded), what Steam
/// started downloading (a dot), the hours the ping went high (a mark over the bar) and the drops (a band); the Ping view
/// draws the line check instead of the bars, on the same hours. Drag across it to zoom in (as Temperatures' graph does),
/// double-click or the card's button to see all of it again. Click a bar to pick it (the page then lists its apps).
/// Hover: the page shows its own box from <see cref="HoverIndex"/> and <see cref="HoverPoint"/>; anywhere else (a
/// dashboard's tile) the chart draws a plain one.
/// </summary>
public sealed class NetBarsChart : NetChartBase
{
    public static readonly DependencyProperty BinsProperty = DependencyProperty.Register(
        nameof(Bins), typeof(IReadOnlyList<NetBin>), typeof(NetBarsChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((NetBarsChart)d).ResetZoom()));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(ReportRange), typeof(NetBarsChart), new FrameworkPropertyMetadata(ReportRange.Day, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(NetBarsMode), typeof(NetBarsChart), new FrameworkPropertyMetadata(NetBarsMode.App, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ViewProperty = DependencyProperty.Register(
        nameof(View), typeof(NetChartView), typeof(NetBarsChart), new FrameworkPropertyMetadata(NetChartView.Usage, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ColorAppsProperty = DependencyProperty.Register(
        nameof(ColorApps), typeof(IReadOnlyList<long>), typeof(NetBarsChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AppNamesProperty = DependencyProperty.Register(
        nameof(AppNames), typeof(IReadOnlyDictionary<long, string>), typeof(NetBarsChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(
        nameof(Selected), typeof(int), typeof(NetBarsChart),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty DropsProperty = DependencyProperty.Register(
        nameof(Drops), typeof(IReadOnlyList<NetDropSpan>), typeof(NetBarsChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HighPingProperty = DependencyProperty.Register(
        nameof(HighPing), typeof(int), typeof(NetBarsChart), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HoverIndexProperty = DependencyProperty.Register(
        nameof(HoverIndex), typeof(int), typeof(NetBarsChart), new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty HoverPointProperty = DependencyProperty.Register(
        nameof(HoverPoint), typeof(Point), typeof(NetBarsChart), new FrameworkPropertyMetadata(default(Point), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty ZoomedProperty = DependencyProperty.Register(
        nameof(Zoomed), typeof(NetDropSpan), typeof(NetBarsChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((NetBarsChart)d).OnZoomedChanged()));
    public static readonly DependencyProperty CanZoomProperty = DependencyProperty.Register(
        nameof(CanZoom), typeof(bool), typeof(NetBarsChart), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty LinkProperty = DependencyProperty.Register(
        nameof(Link), typeof(IReadOnlyList<NetLinkCell>), typeof(NetBarsChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PingMarksProperty = DependencyProperty.Register(
        nameof(PingMarks), typeof(IReadOnlyList<NetPingMark>), typeof(NetBarsChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public NetBarsChart()
    {
        Cursor = System.Windows.Input.Cursors.Hand;
        SetResourceReference(PingBrushProperty, "CpuBrush");
        SetResourceReference(DropBrushProperty, "WarmBrush");
    }

    public static readonly DependencyProperty PingBrushProperty = DependencyProperty.Register(
        nameof(PingBrush), typeof(Brush), typeof(NetBarsChart), new FrameworkPropertyMetadata(Brushes.CornflowerBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DropBrushProperty = DependencyProperty.Register(
        nameof(DropBrush), typeof(Brush), typeof(NetBarsChart), new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush PingBrush { get => (Brush)GetValue(PingBrushProperty); set => SetValue(PingBrushProperty, value); }
    /// <summary>A drop's band: the one thing on the graph in a warning colour (the internet gone is a problem; using it never is).</summary>
    public Brush DropBrush { get => (Brush)GetValue(DropBrushProperty); set => SetValue(DropBrushProperty, value); }

    public IReadOnlyList<NetBin>? Bins { get => (IReadOnlyList<NetBin>?)GetValue(BinsProperty); set => SetValue(BinsProperty, value); }
    public ReportRange Unit { get => (ReportRange)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public NetBarsMode Mode { get => (NetBarsMode)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public NetChartView View { get => (NetChartView)GetValue(ViewProperty); set => SetValue(ViewProperty, value); }
    /// <summary>The apps with a colour of their own, in the order of <see cref="AppBrushKeys"/>.</summary>
    public IReadOnlyList<long>? ColorApps { get => (IReadOnlyList<long>?)GetValue(ColorAppsProperty); set => SetValue(ColorAppsProperty, value); }
    /// <summary>Each app's name, for the hover box.</summary>
    public IReadOnlyDictionary<long, string>? AppNames { get => (IReadOnlyDictionary<long, string>?)GetValue(AppNamesProperty); set => SetValue(AppNamesProperty, value); }
    /// <summary>The bar picked by a click (-1: none).</summary>
    public int Selected { get => (int)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }
    public IReadOnlyList<NetDropSpan>? Drops { get => (IReadOnlyList<NetDropSpan>?)GetValue(DropsProperty); set => SetValue(DropsProperty, value); }
    /// <summary>The ping (ms) a bar's highest is marked from (0: no marks).</summary>
    public int HighPing { get => (int)GetValue(HighPingProperty); set => SetValue(HighPingProperty, value); }
    /// <summary>The bar under the pointer (-1: none). Bound, the page shows its own hover box and the chart draws none.</summary>
    public int HoverIndex { get => (int)GetValue(HoverIndexProperty); set => SetValue(HoverIndexProperty, value); }
    /// <summary>Where that box goes: beside the pointer, on the side with room (in the chart's own coordinates).</summary>
    public Point HoverPoint { get => (Point)GetValue(HoverPointProperty); set => SetValue(HoverPointProperty, value); }
    /// <summary>The stretch zoomed into (null: all of it). Set to null to zoom out.</summary>
    public NetDropSpan? Zoomed { get => (NetDropSpan?)GetValue(ZoomedProperty); set => SetValue(ZoomedProperty, value); }
    public bool CanZoom { get => (bool)GetValue(CanZoomProperty); set => SetValue(CanZoomProperty, value); }
    /// <summary>
    /// The internet strip under the plot, on the same time line in both views: up, dropped (the warning colour), PC off,
    /// not recorded (see <see cref="NetLinkCell"/>). None: no strip and no room kept for it.
    /// </summary>
    public IReadOnlyList<NetLinkCell>? Link { get => (IReadOnlyList<NetLinkCell>?)GetValue(LinkProperty); set => SetValue(LinkProperty, value); }

    /// <summary>The strip's room under the plot (its cells and the gap above them).</summary>
    private double StripRoom => Link is { Count: > 0 } ? 20 : 0;

    /// <summary>When ping checks were stopped and started again in the period (the Ping view marks each).</summary>
    public IReadOnlyList<NetPingMark>? PingMarks { get => (IReadOnlyList<NetPingMark>?)GetValue(PingMarksProperty); set => SetValue(PingMarksProperty, value); }

    /// <summary>The colours apps are told apart by, in order: none of them one that says how a reading is doing.</summary>
    public static readonly string[] AppBrushKeys = ["CpuBrush", "PinkBrush", "GpuBrush", "PurpleBrush"];
    /// <summary>Every other app together.</summary>
    public const string OtherBrushKey = "FaintBrush";

    /// <summary>The width the page's hover box is given room for when it's placed.</summary>
    public const double HoverBoxWidth = 300;

    private Brush Res(string key) => TryFindResource(key) as Brush ?? MutedBrush;

    private Rect PlotArea => new(AxisWidth + 12, Top + MarkRoom, Math.Max(1, ActualWidth - AxisWidth - 12 - 2), Math.Max(1, ActualHeight - AxisHeight - Top - MarkRoom - StripRoom));

    /// <summary>Room over the plot for the high-ping marks (the page's graph only).</summary>
    private double MarkRoom => CanZoom ? 10 : 0;

    // ── Zoom (the bars' times; a year's months don't zoom) ──

    private readonly ChartZoom _zoom = new();
    private bool _settingZoom;

    private TimeSpan Step => Bins is { Count: > 1 } b ? b[1].Start - b[0].Start : TimeSpan.FromHours(1);
    private bool Zoomable => CanZoom && Unit != ReportRange.Year && Bins is { Count: > ChartZoom.MinPoints };

    private DateTime FullFrom => Bins is { Count: > 0 } b ? b[0].Start : DateTime.MinValue;
    private DateTime FullTo => Bins is { Count: > 0 } b ? b[^1].Start + Step : DateTime.MinValue;

    /// <summary>The first and last bar shown (all of them, or the zoomed stretch's).</summary>
    internal (int First, int Last) Shown()
    {
        int count = Bins?.Count ?? 0;
        if (count == 0) return (0, -1);
        if (Zoomed is not { } z || !Zoomable) return (0, count - 1);
        int first = 0, last = count - 1;
        while (first < count - 1 && Bins![first].Start < z.From) first++;
        while (last > first && Bins![last].Start >= z.To) last--;
        return (first, last);
    }

    public void ResetZoom()
    {
        _zoom.Reset();
        if (Zoomed is not null) SetCurrentValue(ZoomedProperty, null);
        InvalidateVisual();
    }

    private void OnZoomedChanged()
    {
        if (_settingZoom) return;
        if (Zoomed is null) _zoom.Reset();
    }

    /// <summary>The zoomed stretch in words, for the card's header: "Fri 30, 10 AM – Fri 30, 10 PM", "3 Mar – 20 Mar".</summary>
    public static string ZoomWords(NetDropSpan z, TimeSpan step) => ChartZoom.Text(z.From, z.To, step);

    /// <summary>The bar under <paramref name="x"/> (null outside the plot).</summary>
    internal int? BarAt(double x)
    {
        var plot = PlotArea;
        var (first, last) = Shown();
        int count = last - first + 1;
        if (count <= 0 || x < plot.Left || x >= plot.Right) return null;
        return first + Math.Min(count - 1, (int)((x - plot.Left) / (plot.Width / count)));
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!Zoomable) return;
        if (e.ClickCount == 2)
        {
            ResetZoom();
            e.Handled = true;
            return;
        }
        double x = e.GetPosition(this).X;
        var plot = PlotArea;
        if (x < plot.Left || x > plot.Right) return;
        _zoom.Press(x);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        bool dragged = _zoom.Dragging;
        if (_zoom.Drag is not null)
        {
            var plot = PlotArea;
            var (first, last) = Shown();
            DateTime shownFrom = Bins![first].Start, shownTo = Bins[last].Start + Step;
            // A position is a time along the stretch shown; the zoom rounds it out to whole bars.
            DateTime TimeAt(double px) => shownFrom + (shownTo - shownFrom) * Math.Clamp((px - plot.Left) / Math.Max(1, plot.Width), 0, 1);
            if (_zoom.Release(TimeAt, FullFrom, FullTo, Step) && _zoom.Range is { } r)
            {
                _settingZoom = true;
                SetCurrentValue(ZoomedProperty, new NetDropSpan(r.From, r.To));
                _settingZoom = false;
                // The bar picked may now be outside: let go of it.
                if (Selected >= 0 && (Bins![Selected].Start < r.From || Bins[Selected].Start >= r.To)) SetCurrentValue(SelectedProperty, -1);
            }
            ReleaseMouseCapture();
            InvalidateVisual();
        }
        if (!dragged && BarAt(e.GetPosition(this).X) is int i) Pick(i);
    }

    protected override void OnLostMouseCapture(System.Windows.Input.MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_zoom.CancelDrag()) InvalidateVisual();
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var at = e.GetPosition(this);
        var plot = PlotArea;
        _zoom.Move(at.X, plot.Left, plot.Right);
        int index = _zoom.Dragging ? -1 : BarAt(at.X) ?? -1;
        if (HoverIndex != index) SetCurrentValue(HoverIndexProperty, index);
        // Beside the pointer, on the side with room for the box.
        double left = at.X + 16 + HoverBoxWidth > ActualWidth ? at.X - 16 - HoverBoxWidth : at.X + 16;
        SetCurrentValue(HoverPointProperty, new Point(Math.Max(0, left), plot.Top));
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (HoverIndex != -1) SetCurrentValue(HoverIndexProperty, -1);
    }

    /// <summary>A click on bar <paramref name="i"/>: picks it, or lets go of it when it is the one picked. An empty bar
    /// has no apps to list, so clicking one lets go too.</summary>
    internal void Pick(int i)
    {
        if (Bins is not { } bins || i < 0 || i >= bins.Count) return;
        SetCurrentValue(SelectedProperty, i == Selected || bins[i].Total == 0 ? -1 : i);
    }

    /// <summary>A bar's pieces, bottom first, each with its colour: what <see cref="OnRender"/> stacks.</summary>
    internal IReadOnlyList<(Brush Brush, long Bytes)> Pieces(NetBin b)
    {
        if (Mode == NetBarsMode.Background)
        {
            long behind = Math.Min(b.Background, b.Total);
            return [(DownBrush, b.Total - behind), (Faded(DownBrush, 0.45), behind)];
        }
        var pieces = new List<(Brush, long)>();
        long named = 0;
        var colored = ColorApps ?? [];
        for (int i = 0; i < colored.Count && i < AppBrushKeys.Length; i++)
        {
            long bytes = Math.Min(b.Apps.FirstOrDefault(a => a.App == colored[i])?.Total ?? 0, b.Total - named);
            named += bytes;
            pieces.Add((Res(AppBrushKeys[i]), bytes));
        }
        pieces.Add((Res(OtherBrushKey), Math.Max(0, b.Total - named)));
        return pieces;
    }

    /// <summary>
    /// The ping axis's top: a round number just over the highest shown, from 4 ms. It used to start at 40 ms, and a
    /// line of 1-3 ms was a flat stroke along the bottom of an empty graph (the user, 2026-11-04: "why does the ping
    /// chart look so empty"); fitted, a steady line reads as steady and a busy one as busy.
    /// </summary>
    public static double PingTop(double highest) =>
        new[] { 4.0, 8, 10, 20, 40, 60, 80, 120, 160, 200, 300, 400, 600, 800, 1000, 1600, 2000 }
            .FirstOrDefault(s => s >= Math.Max(highest, 1) * 1.05, Math.Ceiling(highest / 1000) * 1000);

    protected override void OnRender(DrawingContext dc)
    {
        var all = Bins ?? [];
        var (first, last) = Shown();
        var bins = all.Count == 0 ? [] : all.Skip(first).Take(last - first + 1).ToList();
        bool ping = View == NetChartView.Ping;
        double top = ping ? PingTop(bins.Select(b => (double)(b.Ping?.Highest ?? 0)).DefaultIfEmpty(0).Max())
            : NiceTop(bins.Select(b => (double)b.Total).DefaultIfEmpty(0).Max());
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var plot = PlotArea;
        DrawGrid(dc, plot, top, ping ? v => $"{v:0.#} ms" : v => Units.Data(v));
        if (bins.Count == 0) return;

        double slot = plot.Width / bins.Count, bar = Math.Min(slot * (bins.Count > 60 ? 0.8 : 0.72), 42);
        double Y(double v) => plot.Bottom - plot.Height * Math.Min(v, top) / top;
        int? hover = HoverX is double hx && !_zoom.Dragging ? BarAt(hx) - first : null;
        int selected = Selected >= first && Selected <= last ? Selected - first : -1;

        // Behind everything: the hours a game was played, and the drops.
        var shade = Faded(LabelBrush, 0.07);
        for (int i = 0; i < bins.Count; i++)
            if (bins[i].Game is not null) dc.DrawRectangle(shade, null, new Rect(plot.Left + i * slot, plot.Top, slot + 0.5, plot.Height));
        if (Drops is { } drops)
        {
            var band = Faded(DropBrush, 0.28);
            var from0 = bins[0].Start;
            var to0 = bins[^1].Start + Step;
            double X(DateTime t) => plot.Left + plot.Width * Math.Clamp((t - from0) / (to0 - from0), 0, 1);
            foreach (var d in drops.Where(d => d.To > from0 && d.From < to0))
                dc.DrawRectangle(band, null, new Rect(X(d.From), plot.Top, Math.Max(2, X(d.To) - X(d.From)), plot.Height));
        }
        if (hover is int h)
            dc.DrawRoundedRectangle(Faded(MutedBrush, 0.12), null, new Rect(plot.Left + h * slot, plot.Top, slot, plot.Height), 4, 4);

        if (ping)
        {
            DrawPingOff(dc, plot, bins, slot);
            DrawPing(dc, plot, bins, slot, Y, selected);
            DrawPingMarks(dc, plot, bins);
        }
        else
            for (int i = 0; i < bins.Count; i++)
            {
                var b = bins[i];
                double x = plot.Left + i * slot + (slot - bar) / 2;
                if (b.Total > 0)
                {
                    // Each app's piece a rounded block of its own, a hairline of the card between them, as Reports' bars
                    // draw them (the user, 2026-11-05: the same rounded pieces on every such graph). With a bar picked,
                    // the others step back.
                    bool back = selected >= 0 && i != selected;
                    if (back) dc.PushOpacity(0.3);
                    double from = 0, radius = bar > 6 ? 2.5 : 1;
                    foreach (var (brush, bytes) in Pieces(b))
                    {
                        if (bytes <= 0) continue;
                        double y1 = Y(from), y2 = Y(from + bytes), gap = from > 0 && y1 - y2 > 3 ? 1.5 : 0;
                        double tall = Math.Max(0.5, y1 - y2 - gap), r = Math.Min(radius, tall / 2);
                        dc.DrawRoundedRectangle(brush, null, new Rect(x, y2, bar, tall), r, r);
                        from += bytes;
                    }
                    if (back) dc.Pop();
                }
                // What Steam started downloading: a dot over the bar.
                if (b.Steam.Count > 0) dc.DrawEllipse(Res("CpuBrush"), new Pen(Res("SurfaceBrush"), 1.5), new Point(x + bar / 2, Y(b.Total) - 7), 3.5, 3.5);
                // The ping went high this hour: a small mark over the plot, in the ping's own colour.
                if (HighPing > 0 && b.Ping is { } p && p.Highest >= HighPing) DrawMark(dc, new Point(plot.Left + i * slot + slot / 2, plot.Top - 3));
                Unrecorded(dc, plot, b, x, bar);
            }

        DrawLink(dc, plot, bins);
        DrawAxis(dc, plot, bins, first, slot);

        // The band of a drag going on.
        if (_zoom.Drag is { } drag && _zoom.Dragging)
        {
            double a = Math.Min(drag.Start, drag.End), w = Math.Abs(drag.End - drag.Start);
            dc.DrawRectangle(Faded(PingBrush, 0.16), new Pen(Faded(PingBrush, 0.55), 1), new Rect(a, plot.Top, w, plot.Height));
        }

        // The plain hover box, where no page shows its own (a dashboard's tile).
        bool pageBox = BindingOperations.IsDataBound(this, HoverIndexProperty);
        if (hover is int k && !pageBox) Hover(dc, plot, plot.Left + k * slot + slot / 2, Title(bins[k].Start), HoverLines(bins[k]));
    }

    private void DrawMark(DrawingContext dc, Point tip)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(tip.X - 4, tip.Y - 5), true, true);
            ctx.LineTo(new Point(tip.X + 4, tip.Y - 5), true, false);
            ctx.LineTo(tip, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(Faded(PingBrush, 0.9), null, g);
    }

    /// <summary>
    /// The line check on the same hours, as SmokePing draws it: the usual ping as a line; behind it a darker band from
    /// the lowest to where most checks fell (nine in ten), and a lighter one up to the highest. A tight band is a steady
    /// line, a tall faint one a busy line. Packet loss is a band of the warning colour, the only one on the graph with drops.
    /// </summary>
    private void DrawPing(DrawingContext dc, Rect plot, List<NetBin> bins, double slot, Func<double, double> y, int selected)
    {
        var all = Faded(PingBrush, 0.13);
        var most = Faded(PingBrush, 0.26);
        var pen = new Pen(PingBrush, 1.7) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        // Packet loss: behind the line, the whole height faintly and a solid strip along the bottom.
        var lossBand = Faded(DropBrush, 0.2);
        for (int k = 0; k < bins.Count; k++)
            if (bins[k].Ping is { } lp && lp.LostPercent >= NetLine.LostWorthSaying)
            {
                double lx = plot.Left + k * slot, lw = Math.Max(2, slot);
                dc.DrawRectangle(lossBand, null, new Rect(lx, plot.Top, lw, plot.Height));
                dc.DrawRectangle(DropBrush, null, new Rect(lx, plot.Bottom - 4, lw, 4));
            }
        void Band(List<(double X, NetLinePing P)> run, Func<NetLinePing, int> high, Brush brush)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(run[0].X, y(high(run[0].P))), true, true);
                foreach (var p in run.Skip(1)) ctx.LineTo(new Point(p.X, y(high(p.P))), true, true);
                foreach (var p in Enumerable.Reverse(run)) ctx.LineTo(new Point(p.X, y(p.P.Low)), true, true);
            }
            area.Freeze();
            dc.DrawGeometry(brush, null, area);
        }
        // Unbroken runs of checked bars: a gap where nothing was checked (the PC off), never a line across it.
        int i = 0;
        while (i < bins.Count)
        {
            if (bins[i].Ping is null) { i++; continue; }
            int start = i;
            while (i < bins.Count && bins[i].Ping is not null) i++;
            var run = Enumerable.Range(start, i - start).Select(k => (X: plot.Left + k * slot + slot / 2, P: bins[k].Ping!)).ToList();
            if (run.Count == 1)
            {
                // One checked hour on its own: its spread as a short bar, the usual a dot on it.
                var (x1, p1) = run[0];
                double w1 = Math.Clamp(slot * 0.5, 3, 10);
                dc.DrawRectangle(all, null, new Rect(x1 - w1 / 2, y(p1.Highest), w1, Math.Max(1, y(p1.Low) - y(p1.Highest))));
                dc.DrawRectangle(most, null, new Rect(x1 - w1 / 2, y(p1.Most), w1, Math.Max(1, y(p1.Low) - y(p1.Most))));
                dc.DrawEllipse(PingBrush, null, new Point(x1, y(p1.Usual)), 2.5, 2.5);
                continue;
            }
            Band(run, p => p.Highest, all);
            Band(run, p => p.Most, most);
            var line = new StreamGeometry();
            using (var ctx = line.Open())
            {
                ctx.BeginFigure(new Point(run[0].X, y(run[0].P.Usual)), false, false);
                foreach (var p in run.Skip(1)) ctx.LineTo(new Point(p.X, y(p.P.Usual)), true, true);
            }
            line.Freeze();
            dc.DrawGeometry(null, pen, line);
        }
        if (selected >= 0)
            dc.DrawRoundedRectangle(Faded(PingBrush, 0.08), new Pen(Faded(PingBrush, 0.7), 1), new Rect(plot.Left + selected * slot + 0.5, plot.Top, Math.Max(2, slot - 1), plot.Height), 3, 3);
    }

    /// <summary>
    /// The internet strip: a thin row of cells under the plot, green while the internet was up, the warning colour for a
    /// drop (the one problem on this graph), dark while the PC was off, grey on but not recorded; "INTERNET" beside it.
    /// </summary>
    private void DrawLink(DrawingContext dc, Rect plot, List<NetBin> bins)
    {
        if (Link is not { Count: > 0 } link || bins.Count == 0) return;
        DateTime from0 = bins[0].Start, to0 = bins[^1].Start + Step;
        double span = (to0 - from0).TotalSeconds, top = plot.Bottom + 8, h = 9;
        double X(DateTime t) => plot.Left + plot.Width * Math.Clamp((t - from0).TotalSeconds / span, 0, 1);
        var up = Faded(Res("GoodBrush"), 0.5);
        var off = Faded(LabelBrush, 0.12);
        var notRecorded = Faded(MutedBrush, 0.45);
        foreach (var c in link)
        {
            if (c.End <= from0 || c.Start >= to0 || c.State == NetLinkState.ToCome) continue;
            double x1 = X(c.Start), x2 = X(c.End), w = x2 - x1;
            var brush = c.State switch { NetLinkState.Dropped => DropBrush, NetLinkState.Off => off, NetLinkState.NotRecorded => notRecorded, _ => up };
            // A gap of a pixel between cells where there's room, none when they're thin (a week's 168 hours).
            double gap = w >= 4 ? 1 : 0;
            dc.DrawRectangle(brush, null, new Rect(x1, top, Math.Max(c.State == NetLinkState.Dropped ? 2 : 0.5, w - gap), h));
        }
        var ft = Text(Lang.T("INTERNET"), LabelBrush, 9);
        dc.DrawText(ft, new Point(Math.Max(0, plot.Left - 8 - ft.Width), top + (h - ft.Height) / 2));
    }

    /// <summary>The hours ping checks were off: hatched, so the gap is never read as the line gone.</summary>
    private void DrawPingOff(DrawingContext dc, Rect plot, List<NetBin> bins, double slot)
    {
        if (!bins.Any(b => b.PingOff)) return;
        var hatch = Hatch();
        for (int i = 0; i < bins.Count;)
        {
            if (!bins[i].PingOff) { i++; continue; }
            int start = i;
            while (i < bins.Count && bins[i].PingOff) i++;
            dc.DrawRectangle(hatch, null, new Rect(plot.Left + start * slot, plot.Top, (i - start) * slot, plot.Height));
        }
    }

    private Brush Hatch()
    {
        var pen = new Pen(Faded(LabelBrush, 0.22), 1.5);
        pen.Freeze();
        var drawing = new GeometryDrawing(null, pen, new LineGeometry(new Point(0, 6), new Point(6, 0)));
        var brush = new DrawingBrush(drawing) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 6, 6), ViewportUnits = BrushMappingMode.Absolute, Stretch = System.Windows.Media.Stretch.None };
        brush.Freeze();
        return brush;
    }

    /// <summary>Each moment ping checks were stopped or started again: a dashed line and a label by it ("Ping checks stopped · Fri 2:14 PM").</summary>
    private void DrawPingMarks(DrawingContext dc, Rect plot, List<NetBin> bins)
    {
        if (PingMarks is not { Count: > 0 } marks) return;
        DateTime from0 = bins[0].Start, to0 = bins[^1].Start + Step;
        var dash = new Pen(MutedBrush, 1) { DashStyle = new DashStyle([3, 3], 0) };
        dash.Freeze();
        var pill = Res("Surface2Brush");
        var edge = new Pen(GridBrush, 1);
        edge.Freeze();
        double lastRight = double.MinValue;
        foreach (var m in marks.Where(m => m.At >= from0 && m.At < to0))
        {
            double x = Math.Round(plot.Left + plot.Width * ((m.At - from0) / (to0 - from0))) + 0.5;
            dc.DrawLine(dash, new Point(x, plot.Top), new Point(x, plot.Bottom));
            // The label beside the line, on the side with room; one that would sit on the last is left off (its line stays).
            var ft = Text(m.Label, LineBrush, 11.5);
            double w = ft.Width + 20, h = 22;
            double left = x + 8 + w > plot.Right ? x - 8 - w : x + 8;
            if (left < lastRight + 6) continue;
            var box = new Rect(left, plot.Top + 4, w, h);
            dc.DrawRoundedRectangle(pill, edge, box, 6, 6);
            dc.DrawText(ft, new Point(box.Left + 10, box.Top + (h - ft.Height) / 2));
            lastRight = box.Right;
        }
    }

    /// <summary>Time that wasn't recorded: a grey line under the bar, and for a bar with nothing a dashed outline in its place.</summary>
    private void Unrecorded(DrawingContext dc, Rect plot, NetBin b, double x, double bar)
    {
        if (b.UnrecordedMinutes < MinUnrecorded) return;
        var grey = Faded(MutedBrush, 0.7);
        dc.DrawRoundedRectangle(grey, null, new Rect(x, plot.Bottom + 2, bar, 3), 1.5, 1.5);
        if (b.Total == 0 && bar > 4)
        {
            var dashed = new Pen(grey, 1) { DashStyle = new DashStyle([3, 3], 0) };
            dashed.Freeze();
            dc.DrawRoundedRectangle(null, dashed, new Rect(x + 0.5, plot.Bottom - 28.5, Math.Max(0, bar - 1), 28), 2.5, 2.5);
        }
    }

    private void DrawGrid(DrawingContext dc, Rect plot, double top, Func<double, string> label)
    {
        var pen = new Pen(GridBrush, 1) { DashStyle = new DashStyle([3, 4], 0) };
        pen.Freeze();
        for (int i = 0; i <= 4; i++)
        {
            double y = Math.Round(plot.Bottom - plot.Height * i / 4) + 0.5;
            dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
            Label(dc, i == 0 ? "0" : label(top * i / 4), new Point(plot.Left - 8, y), LabelBrush, right: true);
        }
    }

    /// <summary>The times under the bars: a week's days, a zoomed stretch's hours (the day's name at midnight), a day's
    /// every third hour, a month's every fifth day, a year's months.</summary>
    private void DrawAxis(DrawingContext dc, Rect plot, List<NetBin> bins, int first, double slot)
    {
        bool hours = Step < TimeSpan.FromDays(1);
        int every = hours && bins.Count > 48 ? 24 : hours ? (bins.Count <= 16 ? 1 : bins.Count <= 30 ? 3 : 6) : Unit == ReportRange.Year ? 1 : bins.Count <= 16 ? 1 : 5;
        for (int i = 0; i < bins.Count; i++)
        {
            var t = bins[i].Start;
            string? text;
            if (hours && bins.Count > 48) text = t.Hour == 0 ? t.ToString("ddd d MMM") : null;
            else if (hours) text = t.Hour % every != 0 ? null : t.Hour == 0 && Unit != ReportRange.Day ? t.ToString("ddd") : t.ToString(TimeText.Fit("h tt"));
            else text = AxisLabel(t, first + i);
            if (text is null) continue;
            bool atStart = hours && bins.Count > 48;
            Label(dc, text, new Point(plot.Left + i * slot + (atStart ? 0 : slot / 2), plot.Bottom + StripRoom + AxisHeight / 2 + 2), LabelBrush, center: !atStart);
        }
    }

    /// <summary>The fewest unrecorded minutes in a bar for it to be marked.</summary>
    public const int MinUnrecorded = 15;

    /// <summary>What hovering a bar says: its total, then its biggest apps or its in front / background split.</summary>
    internal IReadOnlyList<string> HoverLines(NetBin b)
    {
        string? missing = b.UnrecordedMinutes >= MinUnrecorded
            ? Lang.T($"Not recorded for {Units.Duration(b.UnrecordedMinutes * 60)}") : null;
        if (b.Total == 0) return [missing ?? Lang.T("Nothing")];
        var lines = new List<string> { Lang.T($"Total {Units.Data(b.Total)}") };
        if (missing is not null) lines.Add(missing);
        if (Mode == NetBarsMode.Background)
        {
            long behind = Math.Min(b.Background, b.Total), away = Math.Min(b.Away, behind);
            lines.Add(Lang.T($"In front {Units.Data(b.Total - behind)}"));
            lines.Add(Lang.T($"In the background {Units.Data(behind - away)}"));
            if (away > 0) lines.Add(Lang.T($"While you were away {Units.Data(away)}"));
        }
        else
        {
            foreach (var a in b.Apps.Take(5))
                lines.Add($"{(AppNames is { } names && names.TryGetValue(a.App, out var name) ? name : Lang.T("Unknown"))} {Units.Data(a.Total)}");
        }
        return lines;
    }

    private string? AxisLabel(DateTime t, int i) => Unit switch
    {
        ReportRange.Day => i % 3 == 0 ? t.ToString(TimeText.Fit("h tt")) : null,
        ReportRange.Week => t.ToString("ddd"),
        ReportRange.Year => t.ToString("MMM"),
        _ => i % 5 == 0 ? t.ToString("d MMM") : null,
    };

    internal string Title(DateTime t) => Step < TimeSpan.FromDays(1)
        ? (Unit == ReportRange.Day ? "" : t.ToString("ddd d MMM") + " · ") + $"{TimeText.At(t, "h tt")} – {TimeText.At(t.AddHours(1), "h tt")}"
        : Unit switch
        {
            ReportRange.Year => t.ToString("MMMM yyyy"),
            _ => t.ToString("dddd, d MMMM"),
        };
}

/// <summary>The connection's speed over the last minute: download as a filled line, upload as a line. Hover a moment for its speeds.</summary>
public sealed class NetLiveChart : NetChartBase
{
    public static readonly DependencyProperty HistoryProperty = DependencyProperty.Register(
        nameof(History), typeof(IReadOnlyList<NetLive>), typeof(NetLiveChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(long), typeof(NetLiveChart), new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<NetLive>? History { get => (IReadOnlyList<NetLive>?)GetValue(HistoryProperty); set => SetValue(HistoryProperty, value); }
    /// <summary>Bumped each second (the live tick) to redraw.</summary>
    public long Version { get => (long)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var points = History ?? [];
        double top = NiceTop(points.Select(p => Math.Max(p.Down, p.Up)).DefaultIfEmpty(0).Max());
        var plot = Grid(dc, top, v => Units.Speed(v));
        Label(dc, Lang.T("60 seconds"), new Point(plot.Left, plot.Bottom + AxisHeight / 2 + 2), LabelBrush);
        Label(dc, Lang.T("now"), new Point(plot.Right, plot.Bottom + AxisHeight / 2 + 2), LabelBrush, right: true);
        if (points.Count < 2) return;

        long last = points[^1].Time;
        Point At(NetLive p, double v) => new(plot.Right - plot.Width * Math.Clamp(last - p.Time, 0, 59) / 59.0, plot.Bottom - plot.Height * Math.Min(v, top) / top);
        StreamGeometry Line(Func<NetLive, double> value, bool fill)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                var first = At(points[0], value(points[0]));
                ctx.BeginFigure(fill ? new Point(first.X, plot.Bottom) : first, fill, fill);
                if (fill) ctx.LineTo(first, true, false);
                for (int i = 1; i < points.Count; i++) ctx.LineTo(At(points[i], value(points[i])), true, true);
                if (fill) ctx.LineTo(new Point(At(points[^1], 0).X, plot.Bottom), true, false);
            }
            g.Freeze();
            return g;
        }
        dc.DrawGeometry(Faded(DownBrush, 0.14), null, Line(p => p.Down, fill: true));
        dc.DrawGeometry(null, new Pen(DownBrush, 1.6), Line(p => p.Down, fill: false));
        dc.DrawGeometry(null, new Pen(UpBrush, 1.4), Line(p => p.Up, fill: false));

        // Hover: the second nearest the pointer, its speeds marked on both lines.
        if (HoverX is double hx && hx >= plot.Left - 6 && hx <= plot.Right + 6)
        {
            var near = points.MinBy(p => Math.Abs(At(p, 0).X - hx))!;
            var (down, up) = (At(near, near.Down), At(near, near.Up));
            dc.DrawEllipse(UpBrush, null, up, 3.5, 3.5);
            dc.DrawEllipse(DownBrush, null, down, 3.5, 3.5);
            long ago = last - near.Time;
            Hover(dc, plot, down.X, ago <= 0 ? Lang.T("Now") : ago == 1 ? Lang.T("1 second ago") : Lang.T($"{ago} seconds ago"),
                [Lang.T($"Download {Units.Speed(near.Down)}"), Lang.T($"Upload {Units.Speed(near.Up)}")]);
        }
    }
}

/// <summary>
/// An app's small chart in the Network page's list: its use day by day (or month by month), each bar as tall as its
/// share of the app's biggest, in the app's colour; a day not recorded is a dot. Hover a bar for its day and amount.
/// </summary>
public sealed class NetSpark : FrameworkElement
{
    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(
        nameof(Bars), typeof(IReadOnlyList<ViewModels.NetSparkBar>), typeof(NetSpark), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush), typeof(Brush), typeof(NetSpark), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ViewModels.NetSparkBar>? Bars { get => (IReadOnlyList<ViewModels.NetSparkBar>?)GetValue(BarsProperty); set => SetValue(BarsProperty, value); }
    public Brush Brush { get => (Brush)GetValue(BrushProperty); set => SetValue(BrushProperty, value); }

    private readonly System.Windows.Controls.ToolTip _tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
    private int _hover = -1;

    public NetSpark()
    {
        ToolTip = _tip;
        System.Windows.Controls.ToolTipService.SetInitialShowDelay(this, 0);
        System.Windows.Controls.ToolTipService.SetBetweenShowDelay(this, 0);
    }

    private int BarAt(double x) => Bars is { Count: > 0 } bars ? Math.Clamp((int)(x / (ActualWidth / bars.Count)), 0, bars.Count - 1) : -1;

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = BarAt(e.GetPosition(this).X);
        if (i == _hover) return;
        _hover = i;
        _tip.Content = i >= 0 ? Bars![i].Tip : null;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        InvalidateVisual();
    }

    protected override HitTestResult HitTestCore(PointHitTestParameters p) => new PointHitTestResult(this, p.HitPoint);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Bars is not { Count: > 0 } bars) return;
        double slot = ActualWidth / bars.Count, w = Math.Max(1, Math.Min(slot * 0.72, 10)), h = ActualHeight;
        var dim = Brush.Clone();
        dim.Opacity = 0.35;
        dim.Freeze();
        for (int i = 0; i < bars.Count; i++)
        {
            double x = i * slot + (slot - w) / 2;
            var brush = _hover >= 0 && i != _hover ? dim : Brush;
            if (bars[i].Off) dc.DrawRectangle(dim, null, new Rect(x + w / 2 - 1, h - 2, 2, 2));
            else
            {
                double bh = Math.Max(1.5, bars[i].Height * (h - 1));
                dc.DrawRoundedRectangle(brush, null, new Rect(x, h - bh, w, bh), Math.Min(1.5, w / 2), Math.Min(1.5, w / 2));
            }
        }
    }
}

/// <summary>An app row's bar: its download then its upload, each as a share (0–100) of the biggest app's.</summary>
public sealed class BarPair : FrameworkElement
{
    public static readonly DependencyProperty DownShareProperty = DependencyProperty.Register(
        nameof(DownShare), typeof(double), typeof(BarPair), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UpShareProperty = DependencyProperty.Register(
        nameof(UpShare), typeof(double), typeof(BarPair), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DownBrushProperty = NetChartBase.DownBrushProperty.AddOwner(typeof(BarPair),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UpBrushProperty = NetChartBase.UpBrushProperty.AddOwner(typeof(BarPair),
        new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));

    public BarPair()
    {
        SetResourceReference(DownBrushProperty, "SkyBrush");
        SetResourceReference(UpBrushProperty, "PurpleBrush");
    }

    public double DownShare { get => (double)GetValue(DownShareProperty); set => SetValue(DownShareProperty, value); }
    public double UpShare { get => (double)GetValue(UpShareProperty); set => SetValue(UpShareProperty, value); }
    public Brush DownBrush { get => (Brush)GetValue(DownBrushProperty); set => SetValue(DownBrushProperty, value); }
    public Brush UpBrush { get => (Brush)GetValue(UpBrushProperty); set => SetValue(UpBrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        double down = w * Math.Clamp(DownShare, 0, 100) / 100, up = w * Math.Clamp(UpShare, 0, 100) / 100;
        if (down > 0) dc.DrawRoundedRectangle(DownBrush, null, new Rect(0, 0, Math.Max(down, 2), h), h / 2, h / 2);
        if (up > 0) dc.DrawRoundedRectangle(UpBrush, null, new Rect(Math.Min(down, w - 2), 0, Math.Max(up, 2), h), h / 2, h / 2);
    }
}

/// <summary>An app's use split three ways: in front, in the background, and while nobody was there (shares, 0–1).</summary>
public sealed class ShareBar : FrameworkElement
{
    public static readonly DependencyProperty FrontProperty = DependencyProperty.Register(
        nameof(Front), typeof(double), typeof(ShareBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty Background2Property = DependencyProperty.Register(
        nameof(Background2), typeof(double), typeof(ShareBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AwayProperty = DependencyProperty.Register(
        nameof(Away), typeof(double), typeof(ShareBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush), typeof(Brush), typeof(ShareBar), new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public ShareBar() => SetResourceReference(BrushProperty, "SkyBrush");

    public double Front { get => (double)GetValue(FrontProperty); set => SetValue(FrontProperty, value); }
    /// <summary>The background share (named so it doesn't hide FrameworkElement's own properties).</summary>
    public double Background2 { get => (double)GetValue(Background2Property); set => SetValue(Background2Property, value); }
    public double Away { get => (double)GetValue(AwayProperty); set => SetValue(AwayProperty, value); }
    public Brush Brush { get => (Brush)GetValue(BrushProperty); set => SetValue(BrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, x = 0;
        var clip = new RectangleGeometry(new Rect(0, 0, w, h), h / 2, h / 2);
        clip.Freeze();
        dc.PushClip(clip);
        foreach (var (share, opacity) in new[] { (Front, 1.0), (Background2, 0.45), (Away, 0.22) })
        {
            double part = w * Math.Clamp(share, 0, 1);
            if (part <= 0) continue;
            var brush = Brush.Clone();
            brush.Opacity = opacity;
            brush.Freeze();
            dc.DrawRectangle(brush, null, new Rect(x, 0, part, h));
            x += part;
        }
        dc.Pop();
    }
}
