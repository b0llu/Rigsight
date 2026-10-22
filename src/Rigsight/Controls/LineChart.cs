using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Models;

namespace Rigsight.Controls;

/// <summary>
/// Multi-series time chart with a labelled grid, used for temperature history. Recent time comes from each
/// sensor's live buffer (one point a second); anything older from the minute history. From an hour up the
/// whole line is one point for each minute, its average; hovering a minute says its average, highest and
/// lowest. On a shorter window every reading is drawn, and hovering shows the exact time and each value there.
/// The last 7 or 30 days (up to <see cref="Day"/>, today's up to now) are drawn from hours (each hour's average), and
/// hovering says the hour's three; the last 12 months from days, in the same way; a custom range (<see cref="CustomFrom"/>
/// to <see cref="CustomTo"/>) from minutes, hours or days by its length (see <see cref="Core.Reports.ChartRange"/>).
/// From an hour up, dragging across it shows only that stretch on a scale of its own, as finely as its length allows
/// (2 days of the last 7 from minutes, read for it when asked: see <see cref="DetailWanted"/> and <see cref="ChartZoom"/>).
/// </summary>
public sealed class LineChart : FrameworkElement
{
    private const double AxisWidth = 42;
    private const double AxisHeight = 22;
    private const int Rows = 4;

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IEnumerable<ChartSeries>), typeof(LineChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(long), typeof(LineChart), new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WindowSecondsProperty = DependencyProperty.Register(
        nameof(WindowSeconds), typeof(int), typeof(LineChart), new FrameworkPropertyMetadata(300, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>What the lines are: temperatures (the default: in the user's unit, ° labels) or fan speeds (rpm, from 0).</summary>
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(SensorKind), typeof(LineChart), new FrameworkPropertyMetadata(SensorKind.Temperature, FrameworkPropertyMetadataOptions.AffectsRender));

    public SensorKind Kind { get => (SensorKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    /// <summary>
    /// From an hour up the line is one point a minute (an hour, a day on the longer ones): that minute's average
    /// ("Avg", the default), its highest reading ("High") or its lowest ("Low"). A spike of a few seconds is in the
    /// highest and hardly moves the average: a day whose highest was 74° drew a line that never passed 68°.
    /// </summary>
    public static readonly DependencyProperty PlotProperty = DependencyProperty.Register(
        nameof(Plot), typeof(string), typeof(LineChart), new FrameworkPropertyMetadata("Avg", FrameworkPropertyMetadataOptions.AffectsRender));

    public string Plot { get => (string)GetValue(PlotProperty); set => SetValue(PlotProperty, value); }

    /// <summary>
    /// Says why a stretch has nothing recorded (its ends in Unix milliseconds): the PC was off, asleep, went down
    /// without shutting down... Unset, or where it isn't known, the stretch is only said to be not recorded.
    /// </summary>
    public static readonly DependencyProperty GapReasonProperty = DependencyProperty.Register(
        nameof(GapReason), typeof(Func<long, long, Core.Stability.GapReason>), typeof(LineChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Func<long, long, Core.Stability.GapReason>? GapReason { get => (Func<long, long, Core.Stability.GapReason>?)GetValue(GapReasonProperty); set => SetValue(GapReasonProperty, value); }

    /// <summary>What a point is on this window: 1 its highest, -1 its lowest, 0 its average (and always on a short window, where every reading is drawn).</summary>
    private int Pick => !ByMinute ? 0 : Plot == "High" ? 1 : Plot == "Low" ? -1 : 0;

    private bool IsTemp => Kind == SensorKind.Temperature;

    /// <summary>A stored value as drawn: a temperature in the user's unit, anything else as it is.</summary>
    private Func<double, double> Shown => IsTemp ? Units.Temp : static v => v;

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(LineChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(LineChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Typeface LabelFont = new("Segoe UI");
    private static readonly Typeface HoverFont = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static Brush HoverBack => ChartPaint.TooltipBg;
    private static Pen HoverBorder => ChartPaint.TooltipBorder;
    private static Pen HoverLine => _hoverLine?.Brush == ChartPaint.Cursor ? _hoverLine : _hoverLine = Frozen(new Pen(ChartPaint.Cursor, 1));
    private static Pen? _hoverLine;
    private static Brush HoverText => ChartPaint.TextBrush;
    private static Brush HoverMuted => ChartPaint.Muted;

    private double? _hoverX;
    private Pen? _gridPen;

    public LineChart()
    {
        // Theme colors unless a page sets its own.
        SetResourceReference(GridBrushProperty, "StrokeBrush");
        SetResourceReference(LabelBrushProperty, "FaintBrush");
    }

    public IEnumerable<ChartSeries>? Series { get => (IEnumerable<ChartSeries>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public long Version { get => (long)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public int WindowSeconds { get => (int)GetValue(WindowSecondsProperty); set => SetValue(WindowSecondsProperty, value); }

    /// <summary>With <see cref="WindowSeconds"/> 0: the day shown (today: midnight to now; earlier: the whole day, from minute history).</summary>
    public static readonly DependencyProperty DayProperty = DependencyProperty.Register(
        nameof(Day), typeof(DateTime), typeof(LineChart), new FrameworkPropertyMetadata(default(DateTime), FrameworkPropertyMetadataOptions.AffectsRender));

    public DateTime Day { get => (DateTime)GetValue(DayProperty); set => SetValue(DayProperty, value); }

    /// <summary>With <see cref="WindowSeconds"/> -1: the custom range shown, from a whole hour to another.</summary>
    public static readonly DependencyProperty CustomFromProperty = DependencyProperty.Register(
        nameof(CustomFrom), typeof(DateTime), typeof(LineChart), new FrameworkPropertyMetadata(default(DateTime), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CustomToProperty = DependencyProperty.Register(
        nameof(CustomTo), typeof(DateTime), typeof(LineChart), new FrameworkPropertyMetadata(default(DateTime), FrameworkPropertyMetadataOptions.AffectsRender));

    public DateTime CustomFrom { get => (DateTime)GetValue(CustomFromProperty); set => SetValue(CustomFromProperty, value); }
    public DateTime CustomTo { get => (DateTime)GetValue(CustomToProperty); set => SetValue(CustomToProperty, value); }

    // Set per render: an earlier day draws only minute history (the live buffer only covers the last hour).
    private bool _pastDay;
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    // ── Zoom ──

    /// <summary>Drag across the graph to show only that stretch (off by default: a chart on a card without the button back, a dashboard's, stays whole).</summary>
    public static readonly DependencyProperty CanZoomProperty = DependencyProperty.Register(
        nameof(CanZoom), typeof(bool), typeof(LineChart), new FrameworkPropertyMetadata(false, (d, _) => ((LineChart)d).ResetZoom()));

    public bool CanZoom { get => (bool)GetValue(CanZoomProperty); set => SetValue(CanZoomProperty, value); }

    private static readonly DependencyPropertyKey IsZoomedKey = DependencyProperty.RegisterReadOnly(
        nameof(IsZoomed), typeof(bool), typeof(LineChart), new PropertyMetadata(false));
    public static readonly DependencyProperty IsZoomedProperty = IsZoomedKey.DependencyProperty;

    /// <summary>Showing only a stretch: the card shows its range and the button back.</summary>
    public bool IsZoomed => (bool)GetValue(IsZoomedProperty);

    private static readonly DependencyPropertyKey ZoomTextKey = DependencyProperty.RegisterReadOnly(
        nameof(ZoomText), typeof(string), typeof(LineChart), new PropertyMetadata(""));
    public static readonly DependencyProperty ZoomTextProperty = ZoomTextKey.DependencyProperty;

    /// <summary>The stretch shown, in words ("9:30 AM – 11:15 AM", "Mon 5, 3 PM – Wed 7, 9 AM"); empty when it's all shown.</summary>
    public string ZoomText => (string)GetValue(ZoomTextProperty);

    private static readonly DependencyPropertyKey ResetTextKey = DependencyProperty.RegisterReadOnly(
        nameof(ResetText), typeof(string), typeof(LineChart), new PropertyMetadata(ResetTextOf(300)));
    public static readonly DependencyProperty ResetTextProperty = ResetTextKey.DependencyProperty;

    /// <summary>What the button back says: the period picked, in its own words ("Whole day", "Back to 24 hours", "Whole week").</summary>
    public string ResetText => (string)GetValue(ResetTextProperty);

    internal static string ResetTextOf(int windowSeconds) => windowSeconds switch
    {
        0 => Lang.T("Whole day"),
        3600 => Lang.T("Back to 1 hour"),
        21600 => Lang.T("Back to 6 hours"),
        86400 => Lang.T("Back to 24 hours"),
        604800 => Lang.T("All 7 days"),
        2592000 => Lang.T("All 30 days"),
        31536000 => Lang.T("All 12 months"),
        Core.Reports.ChartRange.Custom => Lang.T("Whole range"),
        _ => Lang.T("Back to all of it"),
    };

    /// <summary>Back to the period picked (the card's button).</summary>
    public ICommand ResetZoomCommand => _resetCommand ??= new ActionCommand(ResetZoom);
    private ICommand? _resetCommand;

    private readonly ChartZoom _zoom = new();

    /// <summary>A zoom keeps to the points this window has: under an hour every reading is drawn, and there's nothing to zoom into.</summary>
    private bool Zoomable => CanZoom && ByMinute;

    /// <summary>
    /// How far apart a zoom's points are: as fine as its length allows, as a custom range of that length is drawn
    /// (minutes up to 2 days, hours up to 62, days beyond), never coarser than the range's own. A zoom is at least 15 of
    /// them: 15 minutes into the last 7 days, as into a day. Set when the drag ends; 0 while not zoomed.
    /// </summary>
    private int _zoomStep;

    private TimeSpan ZoomStep => TimeSpan.FromSeconds(_zoom.IsZoomed && _zoomStep > 0 ? _zoomStep : RangeStep);

    private int ZoomStepFor(DateTime a, DateTime b) => Math.Min(RangeStep,
        Core.Reports.ChartRange.StepSeconds(Core.Reports.ChartRange.Custom, a < b ? a : b, a < b ? b : a));

    /// <summary>
    /// A zoom that wants finer points than the range has: the stretch and how far apart, for the page to read (they come
    /// back as each line's <see cref="ChartSeries.Zoomed"/>). Null otherwise. Set by the chart; bind it one way to source.
    /// </summary>
    public static readonly DependencyProperty DetailWantedProperty = DependencyProperty.Register(
        nameof(DetailWanted), typeof((DateTime From, DateTime To, int Step)?), typeof(LineChart), new FrameworkPropertyMetadata(null));

    public (DateTime From, DateTime To, int Step)? DetailWanted
    {
        get => ((DateTime From, DateTime To, int Step)?)GetValue(DetailWantedProperty);
        set => SetValue(DetailWantedProperty, value);
    }

    public void ResetZoom()
    {
        if (_zoom.Reset()) InvalidateVisual();
        SyncZoom();
    }

    private void SyncZoom()
    {
        if (!_zoom.IsZoomed) _zoomStep = 0;
        SetValue(IsZoomedKey, _zoom.IsZoomed);
        SetValue(ZoomTextKey, _zoom.Range is { } r ? ChartZoom.Text(r.From, r.To, ZoomStep) : "");
        (DateTime, DateTime, int)? wanted = _zoom.Range is { } z && _zoomStep > 0 && _zoomStep < RangeStep ? (z.From, z.To, _zoomStep) : null;
        if (DetailWanted != wanted) SetCurrentValue(DetailWantedProperty, wanted);
    }

    // Another period (or day) starts whole.
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == WindowSecondsProperty || e.Property == DayProperty || e.Property == CustomFromProperty || e.Property == CustomToProperty)
        {
            if (e.Property == WindowSecondsProperty) SetValue(ResetTextKey, ResetTextOf(WindowSeconds));
            ResetZoom();
        }
    }

    /// <summary>Where the plot is, and the whole stretch and the one shown (set each render): the drag works in them.</summary>
    private Rect _plotRect;
    private (DateTime From, DateTime To) _full, _shown;

    private DateTime TimeAtX(double x) => _shown.From + (_shown.To - _shown.From) * Math.Clamp((x - _plotRect.Left) / Math.Max(1, _plotRect.Width), 0, 1);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
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
        if (_plotRect.Width <= 0 || x < _plotRect.Left || x > _plotRect.Right) return;
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
        // The period changed under the drag to one that draws every reading: nothing to zoom into.
        if (!Zoomable && _zoom.CancelDrag()) return;
        int step = _zoom.Drag is { } d ? ZoomStepFor(TimeAtX(d.Start), TimeAtX(d.End)) : RangeStep;
        if (_zoom.Release(TimeAtX, _full.From, _full.To, TimeSpan.FromSeconds(step))) _zoomStep = step;
        SyncZoom();
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        // Capture taken away mid-drag (another window came up): the band goes, nothing zooms.
        if (_zoom.CancelDrag()) InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hoverX = e.GetPosition(this).X;
        _zoom.Move(_hoverX.Value, _plotRect.Left, _plotRect.Right);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoverX = null;
        InvalidateVisual();
    }

    // Transparent background so the whole plot receives mouse moves, not just the lines.
    protected override HitTestResult HitTestCore(PointHitTestParameters p) => new PointHitTestResult(this, p.HitPoint);

    /// <summary>
    /// Where a series switches from its minute history to its live buffer. Short windows use live data
    /// only: one averaged point a minute, joined by straight lines, would look like real readings there.
    /// </summary>
    private long LiveStart(ChartSeries s) =>
        _pastDay || Fixed ? long.MaxValue
        : WindowSeconds is > 0 and < 3600 ? long.MinValue : s.Sensor.History.Count > 0 ? s.Sensor.History.FirstTime : long.MaxValue;

    /// <summary>
    /// From an hour up, the live readings are drawn as the minute history is: one point for each minute on the
    /// clock, its average. The line is then the same kind of thing from end to end (a reading that jumps about
    /// from second to second was a smooth line that ended in a jagged patch), and stays as it is while it moves
    /// along; what the minute's readings reached is in the hover box.
    /// </summary>
    private bool ByMinute => WindowSeconds is 0 or >= 3600 or Core.Reports.ChartRange.Custom;

    /// <summary>The last 7 days, 30 days or 12 months, or a custom range: drawn from the history loaded for it alone (the live readings are an hour at most).</summary>
    private bool Fixed => Core.Reports.ChartRange.IsRolling(WindowSeconds) || WindowSeconds == Core.Reports.ChartRange.Custom;

    /// <summary>Drawn from hours or days, not minutes.</summary>
    private bool Long => Fixed && Step > 60;

    /// <summary>How long each point of the older history must stand for on this window: a minute, an hour, or a day (see <see cref="Core.Reports.ChartRange.StepSeconds"/>).</summary>
    private int RangeStep => Fixed ? Core.Reports.ChartRange.StepSeconds(WindowSeconds, CustomFrom, CustomTo) : 60;

    /// <summary>How far apart the points drawn are: the range's, or a zoom's finer ones once they have been read (set each render).</summary>
    private int Step => _drawStep > 0 ? _drawStep : RangeStep;
    private int _drawStep;

    /// <summary>The scale drawn last, bottom and top.</summary>
    private (double Lo, double Hi) _scale;

    /// <summary>
    /// The series' older history, when it is the kind this window draws (minutes, hours for a week or month, days for a
    /// year): for the moment between the range being changed and its history arriving, another kind isn't drawn as if
    /// it were.
    /// </summary>
    private HistoryBuffer? Older(ChartSeries s) => s.StepSeconds == Step ? s.MinutesFor(Pick) : null;

    private double GroupMs => ByMinute ? 60_000 : 0;

    protected override void OnRender(DrawingContext dc)
    {
        // Nothing to drag across until there's a plot with lines (set below).
        _plotRect = default;
        if (ActualWidth < AxisWidth + 20 || ActualHeight < AxisHeight + 20) return;

        var plot = new Rect(AxisWidth, 6, ActualWidth - AxisWidth - 6, ActualHeight - AxisHeight - 6);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var series = Series?.ToList() ?? [];
        _drawStep = 0;

        long to = series.Count == 0 ? 0 : series.Max(s => Math.Max(s.Sensor.History.LastTime, Older(s)?.LastTime ?? 0));
        // WindowSeconds 0 is one calendar day: today from midnight to now, or an earlier day in full.
        bool dayMode = WindowSeconds == 0;
        var day = Day == default ? DateTime.Today : Day.Date;
        _pastDay = dayMode && day < DateTime.Today;
        long from = dayMode ? new DateTimeOffset(day).ToUnixTimeMilliseconds() : to - WindowSeconds * 1000L;
        // The last 7 days, 30 days or 12 months, or a custom range: from its start to its end, or, still going on, to now
        // (no empty days still to come on the right).
        bool custom = WindowSeconds == Core.Reports.ChartRange.Custom;
        var (periodFrom, periodTo) = !Fixed ? default : custom ? (CustomFrom, CustomTo) : Core.Reports.ChartRange.Rolling(WindowSeconds, day);
        if (Fixed)
        {
            if (periodTo <= periodFrom) return;
            (from, to) = (new DateTimeOffset(periodFrom).ToUnixTimeMilliseconds(),
                new DateTimeOffset(Core.Reports.ChartRange.ShownEnd(periodTo, RangeStep, DateTime.Now)).ToUnixTimeMilliseconds());
            if (to <= from) to = from + RangeStep * 1000L;
        }
        if (_pastDay) to = new DateTimeOffset(day.AddDays(1)).ToUnixTimeMilliseconds();
        else if (dayMode && to - from < 300_000) to = from + 300_000;
        int window = (int)((to - from) / 1000);

        // Y range across all series (live and minute history), in display units, snapped to multiples of 10.
        double lo = double.MaxValue, hi = double.MinValue;
        void Widen((double Min, double Max)? r)
        {
            if (r is not { } v) return;
            lo = Math.Min(lo, v.Min);
            hi = Math.Max(hi, v.Max);
        }
        foreach (var s in series)
        {
            if (!Fixed) Widen(ChartGeometry.Range(s.Sensor.History, from, Shown));
            if (from < LiveStart(s) && Older(s) is { } older) Widen(ChartGeometry.Range(older, from, Shown, LiveStart(s)));
        }

        // Zoomed in: only that stretch of the period, across the whole width, as finely as the zoom asked for once its
        // points have been read (until then, the period's own, drawn wider), and on a scale of its own, as a range of
        // that length is: a zoom into the last 7 days looks like the last 6 hours.
        static DateTime Local(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
        _full = (Local(from), Local(to));
        if (lo <= hi && Zoomable && _zoom.IsZoomed)
        {
            var (zoomFrom, zoomTo) = _zoom.Shown(_full.From, _full.To, ZoomStep);
            (from, to) = (Ms(zoomFrom), Ms(zoomTo));
            window = (int)((to - from) / 1000);
            if (_zoomStep < RangeStep && _zoom.Range is { } asked && series.Count > 0
                && series.All(s => s.Zoomed is not null && s.ZoomedFor == (asked.From, asked.To, _zoomStep)))
            {
                series = [.. series.Select(s => s.Zoomed!)];
                _drawStep = _zoomStep;
            }
            double zoomLo = double.MaxValue, zoomHi = double.MinValue;
            foreach (var s in series)
            {
                foreach (var r in new[] { Fixed ? null : ChartGeometry.Range(s.Sensor.History, from, Shown, to),
                             from < LiveStart(s) && Older(s) is { } older ? ChartGeometry.Range(older, from, Shown, Math.Min(LiveStart(s), to)) : null })
                    if (r is { } v) (zoomLo, zoomHi) = (Math.Min(zoomLo, v.Min), Math.Max(zoomHi, v.Max));
            }
            // A stretch with nothing in it keeps the period's scale.
            if (zoomLo <= zoomHi) (lo, hi) = (zoomLo, zoomHi);
        }
        else if (!Zoomable) _zoom.Reset();
        _shown = (Local(from), Local(to));
        // (Not set while drawing: the card's text would be laid out again in the middle of it.)
        if (_zoom.IsZoomed != IsZoomed) Dispatcher.BeginInvoke(SyncZoom);

        if (lo > hi)
        {
            // The period by its own name ("yesterday", "last week", "in August 2026"), as everywhere else.
            string? period = Fixed ? (custom ? PeriodPicker.Words(Core.Reports.ReportRange.Custom, day) : Core.Reports.ChartRange.Words(WindowSeconds, day, DateTime.Today))
                : _pastDay ? PeriodPicker.Words(Core.Reports.ReportRange.Day, day) : null;
            string nothing = period is null ? Lang.T("Collecting data…") : Lang.T($"Not recorded {period}");
            DrawText(dc, nothing, new Point(plot.Left + plot.Width / 2, plot.Top + plot.Height / 2), dpi, center: true);
            return;
        }
        _plotRect = plot;

        if (IsTemp)
        {
            lo = Math.Floor((lo - 2) / 10) * 10;
            hi = Math.Ceiling((hi + 2) / 10) * 10;
            if (hi - lo < 20) hi = lo + 20;
        }
        else
        {
            // Speeds from a standstill up (a fan's changes read as a share of its range, not magnified), on round steps.
            lo = 0;
            double step = new[] { 250.0, 500, 750, 1000, 1250, 1500, 2000, 2500 }.FirstOrDefault(s => s * Rows >= hi * 1.05, 5000);
            hi = step * Rows;
        }
        _scale = (lo, hi);

        // Horizontal grid + Y labels.
        if (_gridPen is null || _gridPen.Brush != GridBrush)
        {
            _gridPen = new Pen(GridBrush, 1) { DashStyle = new DashStyle([3, 4], 0) };
            _gridPen.Freeze();
        }
        var gridPen = _gridPen;
        const int rows = Rows;
        for (int i = 0; i <= rows; i++)
        {
            double v = lo + (hi - lo) * i / rows;
            double y = Math.Round(plot.Bottom - plot.Height * i / rows) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            DrawText(dc, IsTemp ? $"{v:0}°" : v.ToString("N0", CultureInfo.CurrentCulture), new Point(plot.Left - 8, y), dpi, alignRight: true);
        }

        // X labels at "nice" steps: seconds/minutes ago for short windows, clock times for long ones.
        if (_zoom.IsZoomed || custom)
        {
            // A zoomed stretch: round clock times (every 15 minutes, every 2 hours, every day...), as many as fit, none
            // so near an edge that it would run past it.
            var (ticks, every) = ChartZoom.Ticks(_shown.From, _shown.To, plot.Width, 72);
            foreach (var t in ticks)
            {
                double x = plot.Left + plot.Width * (Ms(t) - from) / (to - from);
                if (x - plot.Left < 24 || plot.Right - x < 24) continue;
                DrawText(dc, ChartZoom.TickLabel(t, every), new Point(x, plot.Bottom + 12), dpi, center: true);
            }
        }
        else if (dayMode)
        {
            // Whole hours from midnight ("12 AM", "3 AM"…), as many as fit; today ends at "now".
            double hours = window / 3600.0;
            int hourStep = new[] { 1, 2, 3, 4, 6 }.FirstOrDefault(s => plot.Width * s / Math.Max(hours, 0.1) >= 64, 6);
            if (!_pastDay) DrawText(dc, Lang.T("now"), new Point(plot.Right, plot.Bottom + 12), dpi, center: true);
            for (int hr = 0; hr <= hours; hr += hourStep)
            {
                double x = plot.Left + plot.Width * hr / hours;
                if (!_pastDay && plot.Right - x < 48) break; // leave room for "now"
                // The first label starts at the chart's left edge: centred on the corner, it runs under the lowest temperature.
                DrawText(dc, day.AddHours(hr).ToString(TimeText.Fit("h tt"), CultureInfo.CurrentCulture), new Point(x, plot.Bottom + 12), dpi, center: hr > 0);
            }
        }
        else if (Fixed && Step == 86400)
        {
            // Its months, at their first days ("Jan", "Feb"…), as many as fit; none so near an edge that it runs past it.
            int every = 1;
            while (plot.Width / 12 * every < 44) every++;
            var first = new DateTime(periodFrom.Year, periodFrom.Month, 1);
            if (first < periodFrom) first = first.AddMonths(1);
            int n = 0;
            for (var month = first; Ms(month) < to; month = month.AddMonths(1), n++)
            {
                double x = plot.Left + plot.Width * (Ms(month) - from) / (to - from);
                if (n % every != 0 || plot.Right - x < 16) continue;
                DrawText(dc, month.ToString(month.Month == 1 ? "yyyy" : "MMM", CultureInfo.CurrentCulture), new Point(x, plot.Bottom + 12), dpi, center: x - plot.Left > 16);
            }
        }
        else if (Fixed)
        {
            // Its days, at their midnights ("Mon 5" through 7 days, "1 Oct" every few days of 30), as many as fit.
            double perDay = plot.Width * 86400 / window;
            int every = WindowSeconds == Core.Reports.ChartRange.Week ? 1 : 5;
            while (perDay * every < 56) every++;
            int n = 0;
            for (var midnight = periodFrom; midnight < periodTo; midnight = midnight.AddDays(1), n++)
            {
                double x = plot.Left + plot.Width * (new DateTimeOffset(midnight).ToUnixTimeMilliseconds() - from) / (to - from);
                if (plot.Right - x < 28) break; // a label there would run past the edge
                // The first starts at the chart's left edge (see the day's hours above).
                if (n % every == 0)
                    DrawText(dc, midnight.ToString(every == 1 ? "ddd d" : "d MMM", CultureInfo.CurrentCulture), new Point(x, plot.Bottom + 12), dpi, center: n > 0);
            }
        }
        else
        {
            int step = window switch { <= 60 => 15, <= 300 => 60, <= 900 => 180, <= 3600 => 900, <= 21600 => 3600, _ => 4 * 3600 };
            // On a narrow chart (a small dashboard tile), skip labels until each has room ("12:04 PM" ≈ 50 px).
            while (step < window && plot.Width * step / window < 56) step *= 2;
            for (int t = 0; t <= window; t += step)
            {
                double x = plot.Right - plot.Width * t / window;
                string label = t == 0 ? Lang.T("now")
                    : window > 3600 ? DateTimeOffset.FromUnixTimeMilliseconds(to - t * 1000L).LocalDateTime.ToString(TimeText.Fit("h:mm tt")) // as times are written everywhere else in the app
                    : t < 60 ? $"-{t}s" : $"-{t / 60}m";
                DrawText(dc, label, new Point(x, plot.Bottom + 12), dpi, center: x - plot.Left > 1);
            }
        }

        // Where nothing was recorded: a thin strip along the foot of the chart, so an empty stretch reads as "no readings",
        // not as a PC that stayed cool. (Shaded from top to bottom, the empty stretches were what the eye went to, not the lines.)
        var gaps = GapsShown(series, from, to);
        var marks = new List<(double X, (long From, long To) Gap)>();
        foreach (var (gapFrom, gapTo) in gaps)
        {
            double left = plot.Left + plot.Width * (Math.Max(gapFrom, from) - from) / (to - from), right = plot.Left + plot.Width * (Math.Min(gapTo, to) - from) / (to - from);
            if (right - left >= 1) dc.DrawRectangle(GapFill, null, new Rect(left, plot.Bottom - 3, right - left, 3));
            // Where the PC went down without shutting down (power lost, a blue screen): a small red mark on the strip at
            // that moment, the last one with readings.
            if (gapFrom >= from && GapReason?.Invoke(gapFrom, gapTo) is Core.Stability.GapReason.ShutOff or Core.Stability.GapReason.Crashed)
            {
                double x = Math.Round(left) + 0.5;
                marks.Add((x, (gapFrom, gapTo)));
                dc.DrawGeometry(ChartPaint.Hot, null, Frozen(new PathGeometry([new PathFigure(new Point(x - 5, plot.Bottom),
                    [new LineSegment(new Point(x + 5, plot.Bottom), false), new LineSegment(new Point(x, plot.Bottom - 8), false)], true)])));
            }
        }

        dc.PushClip(new RectangleGeometry(new Rect(plot.Left, plot.Top - 2, plot.Width, plot.Height + 4)));
        foreach (var s in series)
        {
            long liveStart = LiveStart(s);
            if (from < liveStart && Older(s) is { } older)
                Draw(s, older, liveStart, Fixed ? null : ChartGeometry.FirstPoint(s.Sensor.History, from, to, plot, GroupMs, Pick));
            if (!Fixed) Draw(s, s.Sensor.History, groupMs: GroupMs);
        }
        dc.Pop();

        // The band of a drag going on, in place of the hover.
        _zoom.DrawBand(dc, plot);
        if (_zoom.Dragging) return;

        if (_hoverX is double hx && hx >= plot.Left && hx <= plot.Right)
        {
            long at = from + (long)((hx - plot.Left) / plot.Width * (to - from));
            // On a red mark (the pointer on any part of it, also its half before the stretch begins): why it is there.
            if (marks.Where(m => Math.Abs(m.X - hx) <= MarkReach).OrderBy(m => Math.Abs(m.X - hx)).Select(m => ((long From, long To)?)m.Gap).FirstOrDefault() is { } marked)
                DrawGapHover(dc, plot, marked, hx, dpi);
            else if (gaps.FirstOrDefault(g => at >= g.From && at < g.To) is { To: > 0 } gap) DrawGapHover(dc, plot, gap, hx, dpi);
            else DrawHover(dc, plot, series, from, to, lo, hi, hx, dpi);
        }

        void Draw(ChartSeries s, HistoryBuffer buffer, long until = long.MaxValue, (long, double)? joinTo = null, double groupMs = 0)
        {
            if (ChartGeometry.Build(buffer, from, to, plot, lo, hi, Shown, until, joinTo, groupMs, Pick) is not { } g) return;
            dc.DrawGeometry(s.Fill, null, g.Fill);
            dc.DrawGeometry(null, s.LinePen, g.Line);
        }
    }

    /// <summary>How far to either side of a red mark's tip the pointer still counts as on it (the mark is 10 px wide).</summary>
    private const double MarkReach = 7;

    private static Brush GapFill => _gapFill?.Color == ChartPaint.Res("TextColor", 0x80) ? _gapFill
        : _gapFill = Frozen(new SolidColorBrush(ChartPaint.Res("TextColor", 0x80)) { Opacity = 0.28 });
    private static SolidColorBrush? _gapFill;

    /// <summary>
    /// The stretches of the window with nothing recorded (see <see cref="ChartSeries.Gaps"/>): the same for every line,
    /// so from the first one that has the older history this window draws. None on a window of live readings only.
    /// </summary>
    private List<(long From, long To)> GapsShown(List<ChartSeries> series, long from, long to)
    {
        var s = series.FirstOrDefault(x => Older(x) is not null && from < LiveStart(x));
        if (s is null) return [];
        long until = LiveStart(s);
        return [.. s.Gaps.Where(g => g.To > from && g.From < Math.Min(to, until))];
    }

    /// <summary>Over a stretch with nothing recorded: from when to when, and that nothing was (no row of dashes for each line).</summary>
    private void DrawGapHover(DrawingContext dc, Rect plot, (long From, long To) gap, double hx, double dpi)
    {
        dc.DrawLine(HoverLine, new Point(Math.Round(hx) + 0.5, plot.Top), new Point(Math.Round(hx) + 0.5, plot.Bottom));
        DateTime start = DateTimeOffset.FromUnixTimeMilliseconds(gap.From).LocalDateTime, end = DateTimeOffset.FromUnixTimeMilliseconds(gap.To).LocalDateTime;
        string Moment(DateTime t) => Long ? t.ToString(Step == 86400 ? "d MMM" : TimeText.Fit("ddd d MMM, h tt"), CultureInfo.CurrentCulture)
            : (t.Date == DateTime.Today ? "" : t.ToString("ddd ", CultureInfo.CurrentCulture)) + t.ToString(TimeText.Fit("h:mm tt"), CultureInfo.CurrentCulture);
        // Why, where Windows' log says; otherwise only that nothing was.
        var reason = GapReason?.Invoke(gap.From, gap.To) ?? Core.Stability.GapReason.Unknown;
        string? why = Core.Stability.PowerLog.Words(reason);
        bool down = reason is Core.Stability.GapReason.ShutOff or Core.Stability.GapReason.Crashed;
        var title = Text(why ?? Core.Wording.NotRecorded, HoverFont, 12, down ? ChartPaint.Hot : HoverText, dpi);
        string from = Moment(start), until = Moment(end);
        var when = Text(why is null ? Lang.T($"{from} to {until}") : Lang.T($"Not recorded  ·  {from} to {until}"), LabelFont, 12, HoverMuted, dpi);
        const double pad = 10;
        double w = Math.Max(title.Width, when.Width) + pad * 2, h = pad * 2 + title.Height + 4 + when.Height;
        double x = hx + 14 + w > plot.Right ? hx - 14 - w : hx + 14;
        var box = new Rect(Math.Max(plot.Left, x), plot.Top + 4, w, h);
        dc.DrawRoundedRectangle(HoverBack, HoverBorder, box, 8, 8);
        dc.DrawText(title, new Point(box.Left + pad, box.Top + pad));
        dc.DrawText(when, new Point(box.Left + pad, box.Top + pad + title.Height + 4));
    }

    /// <summary>
    /// The stored minutes that share a point of the line with <paramref name="t"/> (the group <see cref="ChartGeometry.Build"/>
    /// puts them in), as one: the average of their averages, the highest of their highest, the lowest of their lowest,
    /// and where the line is for them. Null with one minute or none (the minute's own figures stand).
    /// </summary>
    private (double? Avg, double? High, double? Low, double At)? Group(ChartSeries s, HistoryBuffer buffer, long t, double groupMs)
    {
        if (groupMs <= 60_000) return null;
        long start = (long)(Math.Floor(t / groupMs) * groupMs);
        double sum = 0, drawnSum = 0, drawnHigh = double.MinValue, drawnLow = double.MaxValue;
        double? high = null, low = null;
        int n = 0, averaged = 0;
        for (int i = buffer.IndexAtOrAfter(start); i < buffer.Count && buffer.TimeAt(i) < start + groupMs; i++)
        {
            double drawn = buffer.ValueAt(i);
            if (double.IsNaN(drawn) || s.StatsAt(i) is not { } m) continue;
            n++;
            drawnSum += drawn;
            drawnHigh = Math.Max(drawnHigh, drawn);
            drawnLow = Math.Min(drawnLow, drawn);
            if (m.Avg is { } a) { sum += a; averaged++; }
            if (m.High is { } h) high = Math.Max(high ?? h, h);
            if (m.Low is { } l) low = Math.Min(low ?? l, l);
        }
        if (n < 2) return null;
        return (averaged > 0 ? sum / averaged : null, high, low, Pick > 0 ? drawnHigh : Pick < 0 ? drawnLow : drawnSum / n);
    }

    /// <summary>The average, highest and lowest of the live readings in the minute on the clock that <paramref name="t"/> is in.</summary>
    private static (double Avg, double High, double Low)? LiveMinute(HistoryBuffer buffer, long t)
    {
        long start = t - t % 60_000;
        double sum = 0, high = double.MinValue, low = double.MaxValue;
        int n = 0;
        for (int i = buffer.IndexAtOrAfter(start); i < buffer.Count && buffer.TimeAt(i) < start + 60_000; i++)
        {
            double v = buffer.ValueAt(i);
            if (double.IsNaN(v)) continue;
            sum += v;
            high = Math.Max(high, v);
            low = Math.Min(low, v);
            n++;
        }
        return n == 0 ? null : (sum / n, high, low);
    }

    /// <summary>
    /// A guide line at the pointer, a dot on each series, and a box with the time and values: each reading at
    /// that second on a short window; from an hour up, the minute's average (where the line is), and for
    /// temperatures its highest and lowest too.
    /// </summary>
    private void DrawHover(DrawingContext dc, Rect plot, List<ChartSeries> series, long from, long to, double lo, double hi, double hx, double dpi)
    {
        long t = from + (long)((hx - plot.Left) / plot.Width * (to - from));
        dc.DrawLine(HoverLine, new Point(Math.Round(hx) + 0.5, plot.Top), new Point(Math.Round(hx) + 0.5, plot.Bottom));

        long shownTime = t;
        bool fromMinutes = false;
        bool stats = ByMinute && IsTemp;
        var rows = new List<(ChartSeries Series, double? Value, double? High, double? Low, double? At)>();
        foreach (var s in series)
        {
            // The live buffer covers recent time; before it starts, the minute history.
            bool useMinutes = t < LiveStart(s);
            var buffer = useMinutes ? Older(s) : s.Sensor.History;
            int i = buffer?.NearestIndex(t) ?? -1;
            double? value = null, high = null, low = null, drawn = null;
            // Only a sample near the pointer counts (none across a gap, e.g. while the PC was off).
            if (buffer is not null && i >= 0 && Math.Abs(buffer.TimeAt(i) - t) <= (useMinutes ? s.StepSeconds * 1500L : 5_000) && !double.IsNaN(buffer.ValueAt(i)))
            {
                value = buffer.ValueAt(i);
                // An hour is named by its start ("3 PM" is 3 to 4), a day by its date.
                long time = Long ? buffer.TimeAt(i) - s.StepSeconds * 500L : buffer.TimeAt(i);
                // The dot goes where the line is: the minute's point.
                double at = value.Value;
                if (useMinutes && s.StatsAt(i) is { } kept)
                {
                    (value, high, low) = kept;
                    // On a day, a point of the line stands for a few minutes: the box says all of them (their highest
                    // is in it wherever the pointer is; said for the nearest minute alone, some could never be reached).
                    if (!Long && Group(s, buffer, t, ChartGeometry.GroupMs(from, to, plot, 60_000)) is { } all) (value, high, low, at) = (all.Avg, all.High, all.Low, all.At);
                }
                else if (!useMinutes && ByMinute && LiveMinute(buffer, t) is { } minute)
                {
                    (value, high, low) = minute;
                    at = Pick > 0 ? minute.High : Pick < 0 ? minute.Low : minute.Avg;
                    time = t - t % 60_000;
                }
                if (rows.Count == 0 || rows.All(r => r.Value is null && r.High is null)) shownTime = time;
                fromMinutes |= useMinutes || ByMinute;
                drawn = at;
                double y = plot.Bottom - (Shown(at) - lo) / (hi - lo) * plot.Height;
                dc.DrawEllipse(s.Brush, new Pen(HoverBack, 2), new Point(hx, Math.Clamp(y, plot.Top, plot.Bottom)), 4, 4);
            }
            rows.Add((s, value, high, low, drawn));
        }
        // The box lists the lines as they stand at the pointer, top one first (a line with nothing there goes last):
        // a fixed order had the lowest line's name above the highest's.
        rows = [.. rows.OrderByDescending(r => r.At ?? double.NegativeInfinity)];

        var local = DateTimeOffset.FromUnixTimeMilliseconds(shownTime).LocalDateTime;
        string when = Long ? local.ToString(Step == 86400 ? "ddd d MMM yyyy" : TimeText.Fit("ddd d MMM, h tt"), CultureInfo.CurrentCulture)
            : (local.Date == DateTime.Today ? "" : local.ToString("ddd ", CultureInfo.CurrentCulture)) + local.ToString(fromMinutes ? TimeText.Fit("h:mm tt") : TimeText.Fit("h:mm:ss tt"));

        var title = Text(when, HoverFont, 12, HoverText, dpi);
        // One value a line, or three columns under their names: the minute's average, highest and lowest.
        FormattedText Cell(double? v) => Text(Units.Format(Kind, v), HoverFont, 12, HoverText, dpi);
        var heads = stats ? new[] { Core.Wording.Average, Core.Wording.Highest, Core.Wording.Lowest }.Select(c => Text(c, LabelFont, 11, HoverMuted, dpi)).ToArray() : [];
        var lines = rows.Select(r => (r.Series, Name: Text(r.Series.Label, LabelFont, 12, HoverMuted, dpi),
            Cells: stats ? new[] { Cell(r.Value), Cell(r.High), Cell(r.Low) } : [Cell(r.Value)])).ToList();

        const double pad = 10, dot = 14, gap = 16, lineH = 19;
        double nameW = lines.Count == 0 ? 0 : lines.Max(l => l.Name.Width);
        int columns = stats ? 3 : 1;
        var widths = Enumerable.Range(0, columns)
            .Select(c => Math.Max(stats ? heads[c].Width : 0, lines.Count == 0 ? 0 : lines.Max(l => l.Cells[c].Width))).ToArray();
        double headH = stats ? lineH : 0;
        double w = Math.Max(title.Width, dot + nameW + widths.Sum(cw => gap + cw)) + pad * 2;
        double h = pad * 2 + title.Height + 4 + headH + lines.Count * lineH;

        // Beside the pointer, flipping to the other side near the right edge.
        double x = hx + 14 + w > plot.Right ? hx - 14 - w : hx + 14;
        double y0 = plot.Top + 4;
        var box = new Rect(Math.Max(plot.Left, x), y0, w, h);
        dc.DrawRoundedRectangle(HoverBack, HoverBorder, box, 8, 8);
        dc.DrawText(title, new Point(box.Left + pad, box.Top + pad));
        double ly = box.Top + pad + title.Height + 4;
        // Columns from the right edge, each as wide as its widest text and right-aligned.
        var rights = new double[columns];
        for (int c = columns - 1; c >= 0; c--) rights[c] = c == columns - 1 ? box.Right - pad : rights[c + 1] - widths[c + 1] - gap;
        for (int c = 0; c < heads.Length; c++)
            dc.DrawText(heads[c], new Point(rights[c] - heads[c].Width, ly + (lineH - heads[c].Height) / 2));
        ly += headH;
        foreach (var (s, name, cells) in lines)
        {
            dc.DrawEllipse(s.Brush, null, new Point(box.Left + pad + 4, ly + lineH / 2), 4, 4);
            dc.DrawText(name, new Point(box.Left + pad + dot, ly + (lineH - name.Height) / 2));
            for (int c = 0; c < columns; c++)
                dc.DrawText(cells[c], new Point(rights[c] - cells[c].Width, ly + (lineH - cells[c].Height) / 2));
            ly += lineH;
        }
    }

    private static FormattedText Text(string text, Typeface font, double size, Brush brush, double dpi) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, font, size, brush, dpi);

    private void DrawText(DrawingContext dc, string text, Point anchor, double dpi, bool alignRight = false, bool center = false)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFont, 11, LabelBrush, dpi);
        double x = alignRight ? anchor.X - ft.Width : center ? anchor.X - ft.Width / 2 : anchor.X;
        dc.DrawText(ft, new Point(x, anchor.Y - ft.Height / 2));
    }
}
