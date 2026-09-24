using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Models;

namespace Rigsight.Controls;

/// <summary>
/// Multi-series time chart with a labelled grid, used for temperature history. Recent time comes from each
/// sensor's live buffer (one point a second); anything older from the minute history. Hovering shows the
/// exact time and every series' value there.
/// </summary>
public sealed class LineChart : FrameworkElement
{
    private const double AxisWidth = 42;
    private const double AxisHeight = 22;

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IEnumerable<ChartSeries>), typeof(LineChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(long), typeof(LineChart), new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WindowSecondsProperty = DependencyProperty.Register(
        nameof(WindowSeconds), typeof(int), typeof(LineChart), new FrameworkPropertyMetadata(300, FrameworkPropertyMetadataOptions.AffectsRender));

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
        _pastDay ? long.MaxValue
        : WindowSeconds is > 0 and < 3600 ? long.MinValue : s.Sensor.History.Count > 0 ? s.Sensor.History.FirstTime : long.MaxValue;

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < AxisWidth + 20 || ActualHeight < AxisHeight + 20) return;

        var plot = new Rect(AxisWidth, 6, ActualWidth - AxisWidth - 6, ActualHeight - AxisHeight - 6);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var series = Series?.ToList() ?? [];

        long to = series.Count == 0 ? 0 : series.Max(s => Math.Max(s.Sensor.History.LastTime, s.Minutes.LastTime));
        // WindowSeconds 0 is one calendar day: today from midnight to now, or an earlier day in full.
        bool dayMode = WindowSeconds == 0;
        var day = Day == default ? DateTime.Today : Day.Date;
        _pastDay = dayMode && day < DateTime.Today;
        long from = dayMode ? new DateTimeOffset(day).ToUnixTimeMilliseconds() : to - WindowSeconds * 1000L;
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
            Widen(ChartGeometry.Range(s.Sensor.History, from, Units.Temp));
            if (from < LiveStart(s)) Widen(ChartGeometry.Range(s.Minutes, from, Units.Temp, LiveStart(s)));
        }

        if (lo > hi)
        {
            DrawText(dc, _pastDay ? "No temperatures recorded on this day" : "Collecting data…", new Point(plot.Left + plot.Width / 2, plot.Top + plot.Height / 2), dpi, center: true);
            return;
        }

        lo = Math.Floor((lo - 2) / 10) * 10;
        hi = Math.Ceiling((hi + 2) / 10) * 10;
        if (hi - lo < 20) hi = lo + 20;

        // Horizontal grid + Y labels.
        if (_gridPen is null || _gridPen.Brush != GridBrush)
        {
            _gridPen = new Pen(GridBrush, 1) { DashStyle = new DashStyle([3, 4], 0) };
            _gridPen.Freeze();
        }
        var gridPen = _gridPen;
        const int rows = 4;
        for (int i = 0; i <= rows; i++)
        {
            double v = lo + (hi - lo) * i / rows;
            double y = Math.Round(plot.Bottom - plot.Height * i / rows) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            DrawText(dc, $"{v:0}°", new Point(plot.Left - 8, y), dpi, alignRight: true);
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
                DrawText(dc, day.AddHours(hr).ToString("h tt", CultureInfo.CurrentCulture), new Point(x, plot.Bottom + 12), dpi, center: true);
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
                    : window > 3600 ? DateTimeOffset.FromUnixTimeMilliseconds(to - t * 1000L).LocalDateTime.ToString("t", CultureInfo.CurrentCulture)
                    : t < 60 ? $"-{t}s" : $"-{t / 60}m";
                DrawText(dc, label, new Point(x, plot.Bottom + 12), dpi, center: true);
            }
        }

        dc.PushClip(new RectangleGeometry(new Rect(plot.Left, plot.Top - 2, plot.Width, plot.Height + 4)));
        foreach (var s in series)
        {
            long liveStart = LiveStart(s);
            if (from < liveStart)
                Draw(s, s.Minutes, liveStart, s.Sensor.History.Count > 0 ? (s.Sensor.History.TimeAt(0), s.Sensor.History.ValueAt(0)) : null);
            Draw(s, s.Sensor.History);
        }
        dc.Pop();

        if (_hoverX is double hx && hx >= plot.Left && hx <= plot.Right)
            DrawHover(dc, plot, series, from, to, lo, hi, hx, dpi);

        void Draw(ChartSeries s, HistoryBuffer buffer, long until = long.MaxValue, (long, double)? joinTo = null)
        {
            if (ChartGeometry.Build(buffer, from, to, plot, lo, hi, Units.Temp, until, joinTo) is not { } g) return;
            dc.DrawGeometry(s.Fill, null, g.Fill);
            dc.DrawGeometry(null, s.LinePen, g.Line);
        }
    }

    /// <summary>A guide line at the pointer, a dot on each series, and a box with the time and values.</summary>
    private void DrawHover(DrawingContext dc, Rect plot, List<ChartSeries> series, long from, long to, double lo, double hi, double hx, double dpi)
    {
        long t = from + (long)((hx - plot.Left) / plot.Width * (to - from));
        dc.DrawLine(HoverLine, new Point(Math.Round(hx) + 0.5, plot.Top), new Point(Math.Round(hx) + 0.5, plot.Bottom));

        long shownTime = t;
        bool fromMinutes = false;
        var rows = new List<(ChartSeries Series, double? Value)>();
        foreach (var s in series)
        {
            // The live buffer covers recent time; before it starts, the minute history.
            bool useMinutes = t < LiveStart(s);
            var buffer = useMinutes ? s.Minutes : s.Sensor.History;
            int i = buffer.NearestIndex(t);
            double? value = null;
            // Only a sample near the pointer counts (none across a gap, e.g. while the PC was off).
            if (i >= 0 && Math.Abs(buffer.TimeAt(i) - t) <= (useMinutes ? 90_000 : 5_000) && !double.IsNaN(buffer.ValueAt(i)))
            {
                value = buffer.ValueAt(i);
                if (rows.Count == 0 || rows.All(r => r.Value is null)) shownTime = buffer.TimeAt(i);
                fromMinutes |= useMinutes;
                double y = plot.Bottom - (Units.Temp(value.Value) - lo) / (hi - lo) * plot.Height;
                dc.DrawEllipse(s.Brush, new Pen(HoverBack, 2), new Point(hx, Math.Clamp(y, plot.Top, plot.Bottom)), 4, 4);
            }
            rows.Add((s, value));
        }

        var local = DateTimeOffset.FromUnixTimeMilliseconds(shownTime).LocalDateTime;
        string when = (local.Date == DateTime.Today ? "" : local.ToString("ddd ", CultureInfo.CurrentCulture))
            + local.ToString(fromMinutes ? "t" : "T", CultureInfo.CurrentCulture);

        var title = Text(when, HoverFont, 12, HoverText, dpi);
        var lines = rows.Select(r => (r.Series, Name: Text(r.Series.Label, LabelFont, 12, HoverMuted, dpi),
            Value: Text(r.Value is double v ? Units.Format(SensorKind.Temperature, v) : "—", HoverFont, 12, HoverText, dpi))).ToList();

        const double pad = 10, dot = 14, gap = 16, lineH = 19;
        double nameW = lines.Count == 0 ? 0 : lines.Max(l => l.Name.Width);
        double valueW = lines.Count == 0 ? 0 : lines.Max(l => l.Value.Width);
        double w = Math.Max(title.Width, dot + nameW + gap + valueW) + pad * 2;
        double h = pad * 2 + title.Height + 4 + lines.Count * lineH;

        // Beside the pointer, flipping to the other side near the right edge.
        double x = hx + 14 + w > plot.Right ? hx - 14 - w : hx + 14;
        double y0 = plot.Top + 4;
        var box = new Rect(Math.Max(plot.Left, x), y0, w, h);
        dc.DrawRoundedRectangle(HoverBack, HoverBorder, box, 8, 8);
        dc.DrawText(title, new Point(box.Left + pad, box.Top + pad));
        double ly = box.Top + pad + title.Height + 4;
        foreach (var (s, name, value) in lines)
        {
            dc.DrawEllipse(s.Brush, null, new Point(box.Left + pad + 4, ly + lineH / 2), 4, 4);
            dc.DrawText(name, new Point(box.Left + pad + dot, ly + (lineH - name.Height) / 2));
            dc.DrawText(value, new Point(box.Right - pad - value.Width, ly + (lineH - value.Height) / 2));
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
