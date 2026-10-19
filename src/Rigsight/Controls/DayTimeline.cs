using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Core.Apps;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;

namespace Rigsight.Controls;

/// <summary>
/// A strip showing which app was in front (colored by category), with the CPU and GPU temperature curves drawn
/// underneath: a day (24 hours from midnight), or any span up to two days (<see cref="End"/>), such as an evening
/// that ran past midnight. Hover to see exactly what was happening at any minute. Drag across it to show only that
/// stretch (the strip with it), double-click to see all of it again (see <see cref="ChartZoom"/>).
/// </summary>
public sealed class DayTimeline : FrameworkElement
{
    private const double Left = 40, Right = 8, BandHeight = 26, Gap = 12, AxisHeight = 26;

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<TimelineSegment>), typeof(DayTimeline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TempsProperty = DependencyProperty.Register(
        nameof(Temps), typeof(IReadOnlyList<TempPoint>), typeof(DayTimeline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DayProperty = DependencyProperty.Register(
        nameof(Day), typeof(DateTime), typeof(DayTimeline), new FrameworkPropertyMetadata(DateTime.Today, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<TimelineSegment>? Segments { get => (IReadOnlyList<TimelineSegment>?)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public IReadOnlyList<TempPoint>? Temps { get => (IReadOnlyList<TempPoint>?)GetValue(TempsProperty); set => SetValue(TempsProperty, value); }
    public DateTime Day { get => (DateTime)GetValue(DayProperty); set => SetValue(DayProperty, value); }

    /// <summary>
    /// What the curves are drawn from, as on Temperatures (the same choice): each minute's average ("Avg", the default),
    /// its highest ("High") or its lowest ("Low").
    /// </summary>
    public static readonly DependencyProperty PlotProperty = DependencyProperty.Register(
        nameof(Plot), typeof(string), typeof(DayTimeline), new FrameworkPropertyMetadata("Avg", FrameworkPropertyMetadataOptions.AffectsRender));

    public string Plot { get => (string)GetValue(PlotProperty); set => SetValue(PlotProperty, value); }

    /// <summary>Where the strip ends: null for a whole day (24 hours from <see cref="Day"/>'s midnight), else it runs from
    /// <see cref="Day"/> itself (a date and time) to here.</summary>
    public static readonly DependencyProperty EndProperty = DependencyProperty.Register(
        nameof(End), typeof(DateTime?), typeof(DayTimeline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public DateTime? End { get => (DateTime?)GetValue(EndProperty); set => SetValue(EndProperty, value); }

    /// <summary>The span shown: a whole day, or <see cref="Day"/> to <see cref="End"/>.</summary>
    private (DateTime Start, DateTime End) Span() =>
        End is { } end && end > Day ? (Day, end) : (Day.Date, Day.Date.AddDays(1));

    /// <summary>When the data was read (history is saved once a minute, so the last minute or so isn't in it yet).</summary>
    public static readonly DependencyProperty RecordedUntilProperty = DependencyProperty.Register(
        nameof(RecordedUntil), typeof(DateTime?), typeof(DayTimeline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public DateTime? RecordedUntil { get => (DateTime?)GetValue(RecordedUntilProperty); set => SetValue(RecordedUntilProperty, value); }

    // ── Zoom ──

    /// <summary>Drag across the graph to show only that stretch (off by default: a timeline on a card without the button back stays whole).</summary>
    public static readonly DependencyProperty CanZoomProperty = DependencyProperty.Register(
        nameof(CanZoom), typeof(bool), typeof(DayTimeline), new FrameworkPropertyMetadata(false, (d, _) => ((DayTimeline)d).ResetZoom()));

    public bool CanZoom { get => (bool)GetValue(CanZoomProperty); set => SetValue(CanZoomProperty, value); }

    private static readonly DependencyPropertyKey IsZoomedKey = DependencyProperty.RegisterReadOnly(
        nameof(IsZoomed), typeof(bool), typeof(DayTimeline), new PropertyMetadata(false));
    public static readonly DependencyProperty IsZoomedProperty = IsZoomedKey.DependencyProperty;

    /// <summary>Showing only a stretch: the card shows its range and the button back.</summary>
    public bool IsZoomed => (bool)GetValue(IsZoomedProperty);

    private static readonly DependencyPropertyKey ZoomTextKey = DependencyProperty.RegisterReadOnly(
        nameof(ZoomText), typeof(string), typeof(DayTimeline), new PropertyMetadata(""));
    public static readonly DependencyProperty ZoomTextProperty = ZoomTextKey.DependencyProperty;

    /// <summary>The stretch shown, in words ("9:30 AM – 11:15 AM"); empty when it's all shown.</summary>
    public string ZoomText => (string)GetValue(ZoomTextProperty);

    private static readonly DependencyPropertyKey ResetTextKey = DependencyProperty.RegisterReadOnly(
        nameof(ResetText), typeof(string), typeof(DayTimeline), new PropertyMetadata(Lang.T("Whole day")));
    public static readonly DependencyProperty ResetTextProperty = ResetTextKey.DependencyProperty;

    /// <summary>What the button back says: the whole day, or the whole of a custom range.</summary>
    public string ResetText => (string)GetValue(ResetTextProperty);

    /// <summary>Back to all of it (the card's button).</summary>
    public ICommand ResetZoomCommand => _resetCommand ??= new ActionCommand(ResetZoom);
    private ICommand? _resetCommand;

    private readonly ChartZoom _zoom = new();

    /// <summary>A point of the curves is a minute.</summary>
    private static readonly TimeSpan Step = TimeSpan.FromMinutes(1);

    public void ResetZoom()
    {
        if (_zoom.Reset()) InvalidateVisual();
        SyncZoom();
    }

    private void SyncZoom()
    {
        SetValue(IsZoomedKey, _zoom.IsZoomed);
        SetValue(ZoomTextKey, _zoom.Range is { } r ? ChartZoom.Text(r.From, r.To, Step) : "");
    }

    // Another day (or range) starts whole.
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == DayProperty || e.Property == EndProperty)
        {
            SetValue(ResetTextKey, End is { } end && end > Day ? Lang.T("Whole range") : Lang.T("Whole day"));
            ResetZoom();
        }
    }

    /// <summary>Where the plot is across, and the stretch it shows (set each render): the drag works in them.</summary>
    private double _plotLeft = Left, _plotWidth;
    private (DateTime From, DateTime To) _shown;

    private DateTime TimeAtX(double x) => _shown.From + (_shown.To - _shown.From) * Math.Clamp((x - _plotLeft) / Math.Max(1, _plotWidth), 0, 1);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!CanZoom) return;
        if (e.ClickCount == 2)
        {
            ResetZoom();
            e.Handled = true;
            return;
        }
        double x = e.GetPosition(this).X;
        if (_plotWidth <= 0 || x < _plotLeft || x > _plotLeft + _plotWidth) return;
        _zoom.Press(x);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_zoom.Drag is null) return;
        FinishDrag();
        ReleaseMouseCapture();
    }

    /// <summary>The drag ended: zoom to it (or, a press that hardly moved, nothing).</summary>
    internal void FinishDrag()
    {
        var (from, to) = Span();
        _zoom.Release(TimeAtX, from, to, Step);
        SyncZoom();
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        // Capture taken away mid-drag (another window came up): the band goes, nothing zooms.
        if (_zoom.CancelDrag()) InvalidateVisual();
    }

    private double _hoverX = -1;
    private static readonly Dictionary<AppCategory, (Brush Solid, Brush Faint)> CategoryBrushes = [];

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hoverX = e.GetPosition(this).X;
        _zoom.Move(_hoverX, _plotLeft, _plotLeft + _plotWidth);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoverX = -1;
        InvalidateVisual();
    }

    // The brushes are the current theme's: a picked accent turns the categories into its shades.
    private static int _brushesTheme = -1;

    private static (Brush Solid, Brush Faint) BrushesFor(AppCategory c)
    {
        if (_brushesTheme != Services.ThemeManager.Version)
        {
            CategoryBrushes.Clear();
            _brushesTheme = Services.ThemeManager.Version;
        }
        if (!CategoryBrushes.TryGetValue(c, out var b))
        {
            var color = (Color)ColorConverter.ConvertFromString(Services.ThemeManager.CategoryHex(c));
            CategoryBrushes[c] = b = (ChartPaint.Brush(color), ChartPaint.Brush(color, 0.28));
        }
        return b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (w < Left + Right + 50 || h < BandHeight + Gap + AxisHeight + 30) return;

        var (dayStart, dayEnd) = Span();
        double plotW = w - Left - Right;
        // Zoomed in: only that stretch, drawn across the whole width.
        var (start, end) = _shown = _zoom.Shown(dayStart, dayEnd, Step);
        // (Not set while drawing: the card's text would be laid out again in the middle of it.)
        if (_zoom.IsZoomed != IsZoomed) Dispatcher.BeginInvoke(SyncZoom);
        (_plotLeft, _plotWidth) = (Left, plotW);
        double hours = (end - start).TotalHours;
        double X(DateTime t) => Left + (t - start).TotalHours / hours * plotW;
        DateTime TimeAt(double x) => start.AddHours((x - Left) / plotW * hours);

        // Activity band.
        var band = new Rect(Left, 0, plotW, BandHeight);
        dc.DrawRoundedRectangle(ChartPaint.Track, null, band, 6, 6);
        dc.PushClip(new RectangleGeometry(band, 6, 6));
        foreach (var s in Segments ?? [])
        {
            if (s.AppId is null) continue;
            double x0 = X(s.Start), x1 = X(s.End);
            var (solid, faint) = BrushesFor(s.Category);
            dc.DrawRectangle(s.Away ? faint : solid, null, new Rect(x0, 0, Math.Max(1, x1 - x0), BandHeight));
        }
        dc.Pop();

        // Temperature chart.
        var plot = new Rect(Left, BandHeight + Gap, plotW, h - BandHeight - Gap - AxisHeight);
        var temps = Temps ?? [];
        string pick = Plot;
        // The scale is the whole day's (of the line picked), zoomed or not: a stretch keeps its place against the rest.
        var values = temps.SelectMany(t => new[] { t.CpuFor(pick), t.GpuFor(pick) }).OfType<double>().Where(double.IsFinite).Select(Units.Temp).ToList();
        double lo = values.Count > 0 ? Math.Floor((values.Min() - 2) / 10) * 10 : 30;
        double hi = values.Count > 0 ? Math.Ceiling((values.Max() + 2) / 10) * 10 : 80;
        if (hi - lo < 20) hi = lo + 20;

        var gridPen = new Pen(ChartPaint.Grid, 1) { DashStyle = new DashStyle([3, 4], 0) };
        for (int i = 0; i <= 3; i++)
        {
            double v = lo + (hi - lo) * i / 3;
            double y = Math.Round(plot.Bottom - plot.Height * i / 3) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            ChartPaint.Text(dc, this, $"{v:0}°", new Point(plot.Left - 8, y), 11, ChartPaint.Label, ChartPaint.Align.Right);
        }

        double Y(double c) => plot.Bottom - (Units.Temp(c) - lo) / (hi - lo) * plot.Height;
        // Zoomed, the minutes either side run past the plot's edges: cut there.
        dc.PushClip(new RectangleGeometry(new Rect(plot.Left, plot.Top - 2, plot.Width, plot.Height + 4)));
        DrawLine(temps, t => t.CpuFor(pick), ChartPaint.Cpu);
        DrawLine(temps, t => t.GpuFor(pick), ChartPaint.Gpu);
        dc.Pop();

        // Hour labels, the two at the ends kept inside the chart: centred on its corners, midnight would run
        // under the lowest temperature label.
        // Every 3 hours of the clock (every 6 over a longer span), plus the two ends.
        int step = hours > 30 ? 6 : 3;
        static string HourLabel(DateTime t) => t.Minute != 0 ? ChartZoom.Clock(t) : t.Hour switch { 0 => "12 AM", 12 => "12 PM", < 12 => $"{t.Hour} AM", _ => $"{t.Hour - 12} PM" };
        var ticks = new List<DateTime> { start };
        if (_zoom.IsZoomed)
        {
            // A zoomed stretch: round clock times (every 15 minutes, every hour...), as many as fit, and its two ends.
            var (between, every) = ChartZoom.Ticks(start, end, plotW, 72);
            ticks.AddRange(between.Where(t => t > start && t - start >= every / 2 && end - t >= every / 2));
        }
        else
            for (var t = start.Date.AddHours(Math.Ceiling(start.TimeOfDay.TotalHours / step) * step); t < end; t = t.AddHours(step))
                if (t > start && (t - start).TotalHours >= step / 2.0 && (end - t).TotalHours >= step / 2.0) ticks.Add(t);
        ticks.Add(end);
        foreach (var t in ticks)
        {
            var align = t == start ? ChartPaint.Align.Left : t == end ? ChartPaint.Align.Right : ChartPaint.Align.Center;
            ChartPaint.Text(dc, this, HourLabel(t), new Point(X(t), h - AxisHeight / 2 + 2), 11, ChartPaint.Label, align);
        }

        // Today: a "Now" line, with nothing to say about the time after it.
        var now = DateTime.Now;
        double nowX = X(now);
        bool isToday = start <= now && now < end;
        if (isToday)
        {
            var nowPen = new Pen(ChartPaint.Label, 1) { DashStyle = new DashStyle([2, 3], 0) };
            dc.DrawLine(nowPen, new Point(Math.Round(nowX) + 0.5, BandHeight + 2), new Point(Math.Round(nowX) + 0.5, plot.Bottom));
            ChartPaint.Text(dc, this, Lang.T("Now"), new Point(nowX + 4, plot.Top + 7), 10.5, ChartPaint.Label);
        }

        // Hover: cursor line and details (not beyond now).
        // The band of a drag going on, in place of the hover.
        _zoom.DrawBand(dc, new Rect(Left, 0, plotW, plot.Bottom));
        if (_zoom.Dragging) return;
        if (_hoverX >= Left && _hoverX <= Left + plotW && !(isToday && _hoverX > nowX) && start <= now)
        {
            var time = TimeAt(_hoverX);
            dc.DrawLine(new Pen(ChartPaint.Cursor, 1), new Point(_hoverX, 0), new Point(_hoverX, plot.Bottom));
            var seg = (Segments ?? []).FirstOrDefault(s => s.Start <= time && time < s.End);
            var point = temps.Where(t => Math.Abs((t.Time - time).TotalMinutes) <= 1.5).MinBy(t => Math.Abs((t.Time - time).TotalSeconds));

            var lines = new List<(string, Brush, bool)> { (time.ToString("h:mm tt"), ChartPaint.TextBrush, true) };
            if (seg?.App is { } app)
                lines.Add((seg.Away ? Lang.T($"{app} (away)") : app, BrushesFor(seg.Category).Solid, false));
            else if (point is null)
                // The minute in progress (and any since the data was read) simply isn't saved yet.
                lines.Add((time >= (RecordedUntil ?? now).AddMinutes(-1.5) ? Lang.T("Not recorded yet") : Wording.NotRecorded, ChartPaint.Muted, false));
            // The values the lines are drawn from (the minute's average, highest or lowest, as picked).
            if (point?.CpuFor(pick) is double c) lines.Add(($"CPU {Units.TempShort(c)}", ChartPaint.Brush(ChartPaint.Cpu), false));
            if (point?.GpuFor(pick) is double g) lines.Add(($"GPU {Units.TempShort(g)}", ChartPaint.Brush(ChartPaint.Gpu), false));
            ChartPaint.InfoBox(dc, this, lines, new Point(_hoverX, BandHeight + 4), new Rect(0, 0, w, h));
        }

        void DrawLine(IReadOnlyList<TempPoint> pts, Func<TempPoint, double?> sel, Color color)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                bool open = false;
                DateTime last = DateTime.MinValue;
                foreach (var p in pts)
                {
                    if (sel(p) is not double v || !double.IsFinite(v)) { open = false; continue; }
                    var pt = new Point(X(p.Time), Y(v));
                    if (!open || (p.Time - last).TotalMinutes > 2.5) ctx.BeginFigure(pt, false, false);
                    else ctx.LineTo(pt, true, true);
                    open = true;
                    last = p.Time;
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, new Pen(ChartPaint.Brush(color), 1.6) { LineJoin = PenLineJoin.Round }, geo);
        }
    }
}
