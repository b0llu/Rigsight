using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Core.Reports;

namespace Rigsight.Controls;

/// <summary>
/// The Reports page's Temperatures card (2026-11-06, the user: a temperature graph below Active time, "a different
/// box"), on the same columns as the Active time graph above it (the same margins as <see cref="DailyBars"/>), so the
/// heat lines up with what was being done. Each bar of the period (an hour, a day, a month) has a CPU bar and a GPU bar
/// side by side, from its lowest to its highest, with a line across at its average: mock-up Gvq1nXbE8Ubq4W1Q2CHzQb,
/// option A, picked by the user the same evening. Two lines of one figure each looked "very odd and empty": a day ran
/// from 38° to 83° and the card showed one of the three. Bars with nothing recorded are left out. The hover says what
/// was in front and all three figures (see <see cref="HeatWho"/>); the column under the pointer in the graph above is
/// lit here too, and this one's there (<see cref="Linked"/>).
/// </summary>
public sealed class PeriodTemps : FrameworkElement
{
    // As DailyBars, so the columns line up.
    private const double Left = 40, Right = 8, AxisHeight = 24, Top = 10;

    /// <summary>The space between a column's CPU bar and its GPU bar.</summary>
    private const double PairGap = 3;

    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(
        nameof(Days), typeof(IReadOnlyList<DayBucket>), typeof(PeriodTemps), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, e) => ((PeriodTemps)d).DaysChanged(e.OldValue as IReadOnlyList<DayBucket>)));

    public IReadOnlyList<DayBucket>? Days { get => (IReadOnlyList<DayBucket>?)GetValue(DaysProperty); set => SetValue(DaysProperty, value); }

    public static readonly DependencyProperty LinkedProperty = DependencyProperty.Register(
        nameof(Linked), typeof(int), typeof(PeriodTemps), new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The column the pointer is on in the graph above (its <see cref="DailyBars.Hovered"/>): lit here too, without a box. -1 for none.</summary>
    public int Linked { get => (int)GetValue(LinkedProperty); set => SetValue(LinkedProperty, value); }

    private static readonly DependencyPropertyKey HoveredPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Hovered), typeof(int), typeof(PeriodTemps), new PropertyMetadata(-1));
    public static readonly DependencyProperty HoveredProperty = HoveredPropertyKey.DependencyProperty;

    /// <summary>The column under the pointer here, for the graph above to light (-1 for none).</summary>
    public int Hovered => (int)GetValue(HoveredProperty);

    /// <summary>A part's figures for one bar: its lowest, its average and its highest, in °C (null where not recorded).</summary>
    internal readonly record struct Spread(double? Low, double? Avg, double? High)
    {
        public bool Any => Low is not null || Avg is not null || High is not null;
    }

    /// <summary>
    /// A bar's spread: a bar without its lowest kept (read from older daily totals) runs from its average, one without
    /// its highest to its average.
    /// </summary>
    internal static (Spread Cpu, Spread Gpu) SpreadOf(DayBucket d) =>
        (new(d.CpuTempMin ?? d.CpuTempAvg, d.CpuTempAvg, d.CpuTempMax ?? d.CpuTempAvg),
         new(d.GpuTempMin ?? d.GpuTempAvg, d.GpuTempAvg, d.GpuTempMax ?? d.GpuTempAvg));

    /// <summary>
    /// The scale for these figures (already in the person's unit): from the lowest to the highest in whole tens, at
    /// least 30° tall so a quiet period doesn't look like a storm (a shorter one is widened around its middle).
    /// </summary>
    internal static (double Lo, double Hi) ScaleOf(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0) return (30, 90);
        double lo = Math.Floor(values.Min() / 10) * 10, hi = Math.Ceiling(values.Max() / 10) * 10;
        if (hi - lo < 30) { double mid = (hi + lo) / 2; lo = Math.Floor((mid - 15) / 10) * 10; hi = lo + 30; }
        return (lo, hi);
    }

    private readonly ChartTween _tween;
    private (Spread Cpu, Spread Gpu)[]? _was;
    private IReadOnlyList<DayBucket>? _old;
    private (double Lo, double Hi) _wasRange, _rangeDrawn;
    private int _hover = -1;

    public PeriodTemps() => _tween = new ChartTween(this);

    private void DaysChanged(IReadOnlyList<DayBucket>? old)
    {
        var now = Days;
        bool same = old is { Count: > 0 } && now is { Count: > 0 } && old.Count == now.Count
            && (old.Count < 2 || old[1].Day - old[0].Day == now[1].Day - now[0].Day) && !ReferenceEquals(old, now);
        _was = same ? [.. old!.Select(SpreadOf)] : null;
        _wasRange = _rangeDrawn;
        _old = old;
        // Another kind of period: the old bars fade out and the new ones in, with the bars above. The same days read
        // again (coming back, the minute's refresh): the new figures just show.
        _tween.Start(ChartTween.SameDays(old, now) ? ChartTween.Kind.None
            : same ? ChartTween.Kind.Move : old is { Count: > 0 } && now is { Count: > 0 } ? ChartTween.Kind.Swap : ChartTween.Kind.None);
        if (_hover >= (now?.Count ?? 0)) SetHover(-1);
    }

    private void SetHover(int next)
    {
        if (next == _hover) return;
        _hover = next;
        SetValue(HoveredPropertyKey, next);
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Days is not { Count: > 0 } days) return;
        double slot = (ActualWidth - Left - Right) / days.Count;
        int i = (int)Math.Floor((e.GetPosition(this).X - Left) / slot);
        SetHover(i >= 0 && i < days.Count ? i : -1);
    }

    protected override void OnMouseLeave(MouseEventArgs e) => SetHover(-1);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var days = _tween.ShowsOld && _old is { Count: > 0 } old ? old : Days;
        if (days is not { Count: > 0 } || w < Left + Right + 40 || h < 60) return;
        var plot = new Rect(Left, Top, w - Left - Right, h - Top - AxisHeight);
        double slot = plot.Width / days.Count;
        bool hourly = days.Count >= 2 && days[1].Day == days[0].Day.AddHours(1);
        bool monthly = days.Count >= 2 && days[1].Day == days[0].Day.AddMonths(1);

        // Each bar's figures as drawn now: on their way from the old ones while the bars move.
        var spreads = days.Select(SpreadOf).ToArray();
        double? e = _tween.Moving;
        if (e is double k && _was is { } was && was.Length == spreads.Length)
            spreads = [.. spreads.Select((s, i) => (Lerp(was[i].Cpu, s.Cpu, k), Lerp(was[i].Gpu, s.Gpu, k)))];

        // The scale: the period's lowest to its highest, in the person's unit.
        var all = days.Select(SpreadOf).SelectMany(s => new[] { s.Cpu.Low, s.Cpu.High, s.Gpu.Low, s.Gpu.High }).OfType<double>().Select(Units.Temp).ToList();
        var (lo, hi) = ScaleOf(all);
        _rangeDrawn = (lo, hi);
        if (e is double m && _wasRange.Hi > _wasRange.Lo) (lo, hi) = (_wasRange.Lo + (lo - _wasRange.Lo) * m, _wasRange.Hi + (hi - _wasRange.Hi) * m);

        var gridPen = new Pen(ChartPaint.Grid, 1) { DashStyle = new DashStyle([3, 4], 0) };
        gridPen.Freeze();
        double stepDeg = (hi - lo) > 60 ? 20 : 10;
        for (double v = Math.Ceiling(lo / stepDeg) * stepDeg; v <= hi + 0.01; v += stepDeg)
        {
            double y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            ChartPaint.Text(dc, this, $"{v:0}°", new Point(plot.Left - 8, y), 11, ChartPaint.Label, ChartPaint.Align.Right);
        }
        double Y(double shown) => plot.Bottom - (shown - lo) / Math.Max(1, hi - lo) * plot.Height;

        // The column under the pointer, here or in the graph above.
        int lit = _hover >= 0 ? _hover : Linked;
        if (lit >= 0 && lit < days.Count)
            dc.DrawRoundedRectangle(ChartPaint.Track, null, new Rect(plot.Left + slot * lit + 2, plot.Top, Math.Max(0, slot - 4), plot.Height), 6, 6);

        dc.PushOpacity(_tween.Opacity);
        // The CPU's bar left of the column's middle, the GPU's right of it; a part the PC never reported leaves the
        // other in the middle.
        bool hasCpu = days.Any(d => SpreadOf(d).Cpu.Any), hasGpu = days.Any(d => SpreadOf(d).Gpu.Any);
        double bw = Math.Clamp(slot * 0.22, 5, 13);
        var tickEdge = new Pen(ChartPaint.Brush(ChartPaint.Res("SurfaceColor", 0x1A)), 1);
        tickEdge.Freeze();
        var parts = new List<(Func<(Spread Cpu, Spread Gpu), Spread> Of, Color Color, double Dx)>();
        if (hasCpu) parts.Add((s => s.Cpu, ChartPaint.Cpu, hasGpu ? -PairGap / 2 - bw : -bw / 2));
        if (hasGpu) parts.Add((s => s.Gpu, ChartPaint.Gpu, hasCpu ? PairGap / 2 : -bw / 2));
        foreach (var (of, color, dx) in parts)
        {
            var range = ChartPaint.Brush(color, 0.48);
            var rangeLit = ChartPaint.Brush(color, 0.7);
            var mark = ChartPaint.Brush(color);
            for (int i = 0; i < spreads.Length; i++)
            {
                var s = of(spreads[i]);
                double x = plot.Left + slot * (i + 0.5) + dx;
                if (s.Low is double low && s.High is double high)
                {
                    double top = Y(Units.Temp(high)), bottom = Y(Units.Temp(low));
                    double r = Math.Min(4, bw / 2);
                    dc.DrawRoundedRectangle(i == lit ? rangeLit : range, null, new Rect(x, top, bw, Math.Max(2, bottom - top)), r, r);
                }
                if (s.Avg is double avg)
                    dc.DrawRoundedRectangle(mark, tickEdge, new Rect(x - 2.5, Y(Units.Temp(avg)) - 2, bw + 5, 4), 2, 2);
            }
        }
        dc.Pop();

        // The bars' own labels, as on the graph above.
        var now = DateTime.Now;
        var current = monthly ? new DateTime(now.Year, now.Month, 1) : hourly ? now.Date.AddHours(now.Hour) : now.Date;
        int labelEvery = hourly ? 3 : days.Count <= (monthly ? 12 : 7) ? 1 : days.Count <= 16 ? 2 : 3;
        for (int i = 0; i < days.Count; i++)
        {
            var d = days[i];
            bool labelled = hourly ? i % labelEvery == 0 : monthly && days.Count > 12 ? d.Day.Month % 3 == 1 : (days.Count - 1 - i) % labelEvery == 0;
            if (!labelled) continue;
            string label = hourly ? d.Day.ToString(TimeText.Fit("h tt"))
                : monthly ? (days.Count > 12 && d.Day.Month == 1 ? d.Day.ToString("yyyy") : d.Day.ToString("MMM"))
                : d.Day == DateTime.Today ? Lang.T("Today") : days.Count <= 7 ? d.Day.ToString("ddd d") : d.Day.Day.ToString();
            ChartPaint.Text(dc, this, label, new Point(plot.Left + slot * (i + 0.5), h - AxisHeight / 2), 11, d.Day == current ? ChartPaint.TextBrush : ChartPaint.Label, ChartPaint.Align.Center);
        }

        if (_hover >= 0 && _hover < days.Count) DrawHover(dc, days, _hover, plot, slot, hourly, monthly);
    }

    private static double? Lerp(double? a, double? b, double t) => a is double x && b is double y ? x + (y - x) * t : b;

    private static Spread Lerp(Spread a, Spread b, double t) => new(Lerp(a.Low, b.Low, t), Lerp(a.Avg, b.Avg, t), Lerp(a.High, b.High, t));

    /// <summary>
    /// The bar's box: when, what was in front, and the CPU's and GPU's average, highest and lowest. Beside the bar, its bottom at the graph's bottom when it's taller than the graph (it rises over the card
    /// above rather than under the one below).
    /// </summary>
    private void DrawHover(DrawingContext dc, IReadOnlyList<DayBucket> days, int i, Rect plot, double slot, bool hourly, bool monthly)
    {
        var d = days[i];
        double cx = plot.Left + slot * (i + 0.5);

        string title = hourly ? $"{TimeText.At(d.Day, "h tt")} – {TimeText.At(d.Day.AddHours(1), "h tt")}"
            : monthly ? d.Day.ToString("MMMM yyyy") : d.Day.ToString("dddd, d MMMM");
        var who = new HeatWho(d.Front);
        bool any = d.CpuTempAvg is not null || d.GpuTempAvg is not null;
        bool lows = days.Any(b => b.CpuTempMin is not null || b.GpuTempMin is not null);

        // The same rows in every bar's box, a dash where a bar has nothing, and the same width: the box doesn't change
        // size as the pointer moves along (2026-11-06, "the hover popup causes layout shift").
        const double pad = 12, lineH = 20, gap = 14;
        var head = ChartPaint.Format(this, title, 13, ChartPaint.TextBrush, bold: true);
        string[] heads = lows ? [Core.Wording.Average, Core.Wording.Highest, Core.Wording.Lowest] : [Core.Wording.Average, Core.Wording.Highest];
        var rows = new List<(string Name, Brush Dot, double?[] Cells)>();
        if (days.Any(b => b.CpuTempAvg is not null || b.CpuTempMax is not null)) rows.Add((Lang.T("CPU"), ChartPaint.Brush(ChartPaint.Cpu), lows ? [d.CpuTempAvg, d.CpuTempMax, d.CpuTempMin] : [d.CpuTempAvg, d.CpuTempMax]));
        if (days.Any(b => b.GpuTempAvg is not null || b.GpuTempMax is not null)) rows.Add((Lang.T("GPU"), ChartPaint.Brush(ChartPaint.Gpu), lows ? [d.GpuTempAvg, d.GpuTempMax, d.GpuTempMin] : [d.GpuTempAvg, d.GpuTempMax]));
        // A bar with nothing says why in the line of what was in front, where the app would be.
        string? empty = any ? null : d.Day > DateTime.Now ? Lang.T("Still to come") : Core.Wording.NotRecorded;

        var colW = heads.Select(t => Math.Max(ChartPaint.Width(this, t, 11, ChartPaint.Muted), ChartPaint.Width(this, "100.0 °F", 12, ChartPaint.TextBrush, true))).ToArray();
        double nameW = rows.Count == 0 ? 0 : rows.Max(r => ChartPaint.Width(this, r.Name, 12, ChartPaint.TextBrush)) + 14;
        double tableW = rows.Count == 0 ? 0 : nameW + colW.Sum(c => c + gap);
        // The longest title the graph has (a day's name, a month's), not this bar's own.
        double titleW = ChartPaint.Width(this, monthly ? "September 2026" : hourly ? "12 PM – 12 AM" : "Wednesday, 30 September", 13, ChartPaint.TextBrush, bold: true);
        double inner = Math.Max(HeatHover.BoxWidth, Math.Max(titleW, tableW));
        double hBox = pad * 2 + head.Height + 4 + HeatHover.RowHeight + (rows.Count == 0 ? 0 : 8 + lineH + rows.Count * lineH);
        double wBox = inner + pad * 2;

        double x = cx + slot / 2 + 8 + wBox > ActualWidth ? cx - slot / 2 - 8 - wBox : cx + slot / 2 + 8;
        x = Math.Max(0, x);
        double top = Math.Min(plot.Top, plot.Bottom - hBox);
        var box = new Rect(Math.Round(x) + 0.5, Math.Round(top) + 0.5, wBox, hBox);
        dc.DrawRoundedRectangle(ChartPaint.TooltipBg, ChartPaint.TooltipBorder, box, 8, 8);

        double lx = box.Left + pad, ly = box.Top + pad;
        dc.DrawText(head, new Point(lx, ly));
        ly += head.Height + 4;
        ly += HeatHover.DrawFront(dc, this, who, lx, ly, inner, empty);
        if (rows.Count > 0)
        {
            ly += 8;
            double right = box.Right - pad;
            var rights = new double[heads.Length];
            for (int c = heads.Length - 1; c >= 0; c--) rights[c] = c == heads.Length - 1 ? right : rights[c + 1] - colW[c + 1] - gap;
            for (int c = 0; c < heads.Length; c++) ChartPaint.Text(dc, this, heads[c], new Point(rights[c], ly + lineH / 2), 11, ChartPaint.Muted, ChartPaint.Align.Right);
            ly += lineH;
            foreach (var (name, dot, cells) in rows)
            {
                dc.DrawEllipse(dot, null, new Point(lx + 4, ly + lineH / 2), 4, 4);
                ChartPaint.Text(dc, this, name, new Point(lx + 14, ly + lineH / 2), 12, ChartPaint.TextBrush);
                for (int c = 0; c < cells.Length; c++)
                    ChartPaint.Text(dc, this, Units.Format(SensorKind.Temperature, cells[c]), new Point(rights[c], ly + lineH / 2), 12, ChartPaint.TextBrush, ChartPaint.Align.Right, bold: true);
                ly += lineH;
            }
        }
    }
}
