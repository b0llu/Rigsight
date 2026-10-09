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
/// A week or a month (the one <see cref="Day"/> is in, whole, on the calendar) is drawn from hours (each hour's
/// average), and hovering says the hour's three; a year from days, in the same way.
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

    // Set per render: an earlier day draws only minute history (the live buffer only covers the last hour).
    private bool _pastDay;
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hoverX = e.GetPosition(this).X;
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
        _pastDay || Long ? long.MaxValue
        : WindowSeconds is > 0 and < 3600 ? long.MinValue : s.Sensor.History.Count > 0 ? s.Sensor.History.FirstTime : long.MaxValue;

    /// <summary>
    /// From an hour up, the live readings are drawn as the minute history is: one point for each minute on the
    /// clock, its average. The line is then the same kind of thing from end to end (a reading that jumps about
    /// from second to second was a smooth line that ended in a jagged patch), and stays as it is while it moves
    /// along; what the minute's readings reached is in the hover box.
    /// </summary>
    private bool ByMinute => WindowSeconds is 0 or >= 3600;

    /// <summary>A week or a month: drawn from the hours loaded for it alone (the live readings are an hour at most).</summary>
    private bool Long => WindowSeconds > 86400;

    private Core.Reports.ReportRange LongUnit => WindowSeconds switch
    {
        604800 => Core.Reports.ReportRange.Week,
        31536000 => Core.Reports.ReportRange.Year,
        _ => Core.Reports.ReportRange.Month,
    };

    /// <summary>How long each point of the older history must stand for on this window: a minute, an hour, or a day for a year.</summary>
    private int Step => !Long ? 60 : LongUnit == Core.Reports.ReportRange.Year ? 86400 : 3600;

    /// <summary>
    /// The series' older history, when it is the kind this window draws (minutes, hours for a week or month, days for a
    /// year): for the moment between the range being changed and its history arriving, another kind isn't drawn as if
    /// it were.
    /// </summary>
    private HistoryBuffer? Older(ChartSeries s) => s.StepSeconds == Step ? s.MinutesFor(Pick) : null;

    private double GroupMs => ByMinute ? 60_000 : 0;

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < AxisWidth + 20 || ActualHeight < AxisHeight + 20) return;

        var plot = new Rect(AxisWidth, 6, ActualWidth - AxisWidth - 6, ActualHeight - AxisHeight - 6);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var series = Series?.ToList() ?? [];

        long to = series.Count == 0 ? 0 : series.Max(s => Math.Max(s.Sensor.History.LastTime, Older(s)?.LastTime ?? 0));
        // WindowSeconds 0 is one calendar day: today from midnight to now, or an earlier day in full.
        bool dayMode = WindowSeconds == 0;
        var day = Day == default ? DateTime.Today : Day.Date;
        _pastDay = dayMode && day < DateTime.Today;
        long from = dayMode ? new DateTimeOffset(day).ToUnixTimeMilliseconds() : to - WindowSeconds * 1000L;
        // A week (Monday to Sunday) or a month: all of it, from its first midnight, also while it's still in progress
        // (the days to come stay empty), so the same day is in the same place whichever week is looked at.
        var (periodFrom, periodTo) = Long ? Core.Reports.ReportBuilder.Bounds(LongUnit, day) : default;
        if (Long) (from, to) = (new DateTimeOffset(periodFrom).ToUnixTimeMilliseconds(), new DateTimeOffset(periodTo).ToUnixTimeMilliseconds());
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
            if (!Long) Widen(ChartGeometry.Range(s.Sensor.History, from, Shown));
            if (from < LiveStart(s) && Older(s) is { } older) Widen(ChartGeometry.Range(older, from, Shown, LiveStart(s)));
        }

        if (lo > hi)
        {
            string nothing = Long ? (IsTemp ? "No temperatures recorded in this " : "Nothing recorded in this ")
                    + LongUnit switch { Core.Reports.ReportRange.Week => "week", Core.Reports.ReportRange.Year => "year", _ => "month" }
                : _pastDay ? (IsTemp ? "No temperatures recorded on this day" : "Nothing recorded on this day") : "Collecting data…";
            DrawText(dc, nothing, new Point(plot.Left + plot.Width / 2, plot.Top + plot.Height / 2), dpi, center: true);
            return;
        }

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
        if (dayMode)
        {
            // Whole hours from midnight ("12 AM", "3 AM"…), as many as fit; today ends at "now".
            double hours = window / 3600.0;
            int hourStep = new[] { 1, 2, 3, 4, 6 }.FirstOrDefault(s => plot.Width * s / Math.Max(hours, 0.1) >= 64, 6);
            if (!_pastDay) DrawText(dc, "now", new Point(plot.Right, plot.Bottom + 12), dpi, center: true);
            for (int hr = 0; hr <= hours; hr += hourStep)
            {
                double x = plot.Left + plot.Width * hr / hours;
                if (!_pastDay && plot.Right - x < 48) break; // leave room for "now"
                // The first label starts at the chart's left edge: centred on the corner, it runs under the lowest temperature.
                DrawText(dc, day.AddHours(hr).ToString("h tt", CultureInfo.CurrentCulture), new Point(x, plot.Bottom + 12), dpi, center: hr > 0);
            }
        }
        else if (Long && LongUnit == Core.Reports.ReportRange.Year)
        {
            // Its months, at their first days ("Jan", "Feb"…), as many as fit.
            int every = 1;
            while (plot.Width / 12 * every < 44) every++;
            for (int m = 0; m < 12; m += every)
            {
                double x = plot.Left + plot.Width * (new DateTimeOffset(periodFrom.AddMonths(m)).ToUnixTimeMilliseconds() - from) / (to - from);
                DrawText(dc, periodFrom.AddMonths(m).ToString("MMM", CultureInfo.CurrentCulture), new Point(x, plot.Bottom + 12), dpi, center: m > 0);
            }
        }
        else if (Long)
        {
            // Its days, at their midnights ("Mon 5" through a week, "1 Oct" every few days of a month), as many as fit.
            double perDay = plot.Width * 86400 / window;
            int every = LongUnit == Core.Reports.ReportRange.Week ? 1 : 5;
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
                string label = t == 0 ? "now"
                    : window > 3600 ? DateTimeOffset.FromUnixTimeMilliseconds(to - t * 1000L).LocalDateTime.ToString("h:mm tt") // as times are written everywhere else in the app
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
                Draw(s, older, liveStart, Long ? null : ChartGeometry.FirstPoint(s.Sensor.History, from, to, plot, GroupMs, Pick));
            if (!Long) Draw(s, s.Sensor.History, groupMs: GroupMs);
        }
        dc.Pop();

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
        string Moment(DateTime t) => Long ? t.ToString(Step == 86400 ? "d MMM" : "ddd d MMM, h tt", CultureInfo.CurrentCulture)
            : (t.Date == DateTime.Today ? "" : t.ToString("ddd ", CultureInfo.CurrentCulture)) + t.ToString("h:mm tt", CultureInfo.CurrentCulture);
        // Why, where Windows' log says; otherwise only that nothing was.
        var reason = GapReason?.Invoke(gap.From, gap.To) ?? Core.Stability.GapReason.Unknown;
        string? why = Core.Stability.PowerLog.Words(reason);
        bool down = reason is Core.Stability.GapReason.ShutOff or Core.Stability.GapReason.Crashed;
        var title = Text(why ?? "Not recorded", HoverFont, 12, down ? ChartPaint.Hot : HoverText, dpi);
        var when = Text($"{(why is null ? "" : "Not recorded  ·  ")}{Moment(start)} to {Moment(end)}", LabelFont, 12, HoverMuted, dpi);
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
        string when = Long ? local.ToString(Step == 86400 ? "ddd d MMM yyyy" : "ddd d MMM, h tt", CultureInfo.CurrentCulture)
            : (local.Date == DateTime.Today ? "" : local.ToString("ddd ", CultureInfo.CurrentCulture)) + local.ToString(fromMinutes ? "h:mm tt" : "h:mm:ss tt");

        var title = Text(when, HoverFont, 12, HoverText, dpi);
        // One value a line, or three columns under their names: the minute's average, highest and lowest.
        FormattedText Cell(double? v) => Text(Units.Format(Kind, v), HoverFont, 12, HoverText, dpi);
        var heads = stats ? new[] { "Avg", "Highest", "Lowest" }.Select(c => Text(c, LabelFont, 11, HoverMuted, dpi)).ToArray() : [];
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
