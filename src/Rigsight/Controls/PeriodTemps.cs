using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Core.Reports;

namespace Rigsight.Controls;

/// <summary>
/// The Reports page's Temperatures card (2026-11-06, the user: a temperature graph below Active time, "a different
/// box"): a line each for the CPU and the GPU, a point per bar of the Active time graph above it and on the same
/// columns (the same margins as <see cref="DailyBars"/>), so the heat lines up with what was being done. Each point is
/// the bar's average, highest or lowest (<see cref="Plot"/>, the Temperatures page's remembered choice); hours with
/// nothing recorded are left out. The hover says what was in front and the temperatures (see <see cref="HeatWho"/>).
/// </summary>
public sealed class PeriodTemps : FrameworkElement
{
    // As DailyBars, so the columns line up.
    private const double Left = 40, Right = 8, AxisHeight = 24, Top = 10;

    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(
        nameof(Days), typeof(IReadOnlyList<DayBucket>), typeof(PeriodTemps), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, e) => ((PeriodTemps)d).DaysChanged(e.OldValue as IReadOnlyList<DayBucket>)));

    public IReadOnlyList<DayBucket>? Days { get => (IReadOnlyList<DayBucket>?)GetValue(DaysProperty); set => SetValue(DaysProperty, value); }

    public static readonly DependencyProperty PlotProperty = DependencyProperty.Register(
        nameof(Plot), typeof(string), typeof(PeriodTemps), new FrameworkPropertyMetadata("Avg", FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((PeriodTemps)d).PlotChanged()));

    /// <summary>What each point is: the bar's average ("Avg"), its highest ("High") or its lowest ("Low").</summary>
    public string Plot { get => (string)GetValue(PlotProperty); set => SetValue(PlotProperty, value); }

    private readonly ChartTween _tween;
    private (double? Cpu, double? Gpu)[]? _was;
    private (double Lo, double Hi) _wasRange, _rangeDrawn;
    private int _hover = -1;

    public PeriodTemps() => _tween = new ChartTween(this);

    private void DaysChanged(IReadOnlyList<DayBucket>? old)
    {
        var now = Days;
        bool same = old is { Count: > 0 } && now is { Count: > 0 } && old.Count == now.Count
            && (old.Count < 2 || old[1].Day - old[0].Day == now[1].Day - now[0].Day) && !ReferenceEquals(old, now);
        _was = same ? [.. old!.Select(Point)] : null;
        _wasRange = _rangeDrawn;
        _tween.Start(same ? ChartTween.Kind.Move : old is { Count: > 0 } ? ChartTween.Kind.Fade : ChartTween.Kind.None);
    }

    private void PlotChanged()
    {
        // Average to highest: the lines move to the other values.
        if (Days is not { Count: > 0 } days) return;
        _was = [.. days.Select(d => PointFor(d, _lastPlot))];
        _wasRange = _rangeDrawn;
        _lastPlot = Plot;
        _tween.Start(ChartTween.Kind.Move);
    }

    private string _lastPlot = "Avg";

    private (double? Cpu, double? Gpu) Point(DayBucket d) => PointFor(d, Plot);

    private static (double? Cpu, double? Gpu) PointFor(DayBucket d, string plot) => plot switch
    {
        "High" => (d.CpuTempMax, d.GpuTempMax),
        // A bar without its lowest kept (read from older daily totals) has its average there.
        "Low" => (d.CpuTempMin ?? d.CpuTempAvg, d.GpuTempMin ?? d.GpuTempAvg),
        _ => (d.CpuTempAvg, d.GpuTempAvg),
    };

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Days is not { Count: > 0 } days) return;
        double slot = (ActualWidth - Left - Right) / days.Count;
        int i = (int)Math.Floor((e.GetPosition(this).X - Left) / slot);
        int next = i >= 0 && i < days.Count ? i : -1;
        if (next != _hover) { _hover = next; InvalidateVisual(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = -1;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (Days is not { Count: > 0 } days || w < Left + Right + 40 || h < 60) return;
        var plot = new Rect(Left, Top, w - Left - Right, h - Top - AxisHeight);
        double slot = plot.Width / days.Count;
        bool hourly = days.Count >= 2 && days[1].Day == days[0].Day.AddHours(1);
        bool monthly = days.Count >= 2 && days[1].Day == days[0].Day.AddMonths(1);

        // Each point as drawn now: on its way from the old one while the lines move.
        var points = days.Select(Point).ToArray();
        double? e = _tween.Moving;
        if (e is double k && _was is { } was && was.Length == points.Length)
            points = [.. points.Select((p, i) => (Lerp(was[i].Cpu, p.Cpu, k), Lerp(was[i].Gpu, p.Gpu, k)))];

        // The scale: the period's own values, in tens with room around them (at least 30° tall), in the user's unit.
        var all = days.SelectMany(d => new[] { PointFor(d, Plot).Cpu, PointFor(d, Plot).Gpu }).OfType<double>().Select(Units.Temp).ToList();
        double lo = all.Count == 0 ? 30 : Math.Floor((all.Min() - 4) / 10) * 10, hi = all.Count == 0 ? 90 : Math.Ceiling((all.Max() + 4) / 10) * 10;
        if (hi - lo < 30) { double mid = (hi + lo) / 2; lo = Math.Floor((mid - 15) / 10) * 10; hi = lo + 30; }
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

        if (_hover >= 0 && _hover < days.Count)
            dc.DrawRoundedRectangle(ChartPaint.Track, null, new Rect(plot.Left + slot * _hover + 2, plot.Top, Math.Max(0, slot - 4), plot.Height), 6, 6);

        dc.PushOpacity(_tween.Opacity);
        var cpu = ChartPaint.Brush(ChartPaint.Cpu);
        var gpu = ChartPaint.Brush(ChartPaint.Gpu);
        DrawLine(points.Select(p => p.Item1).ToArray(), cpu);
        DrawLine(points.Select(p => p.Item2).ToArray(), gpu);
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

        if (_hover >= 0 && _hover < days.Count) DrawHover(dc, days, _hover, plot, slot, hourly, monthly, Y);

        // A line through the bars' middles, broken where nothing was recorded; a point on its own is a dot.
        void DrawLine(double?[] values, Brush brush)
        {
            var pen = new Pen(brush, 2) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                bool open = false;
                for (int i = 0; i < values.Length; i++)
                {
                    if (values[i] is not double v) { open = false; continue; }
                    var at = new Point(plot.Left + slot * (i + 0.5), Y(Units.Temp(v)));
                    if (!open) { c.BeginFigure(at, false, false); open = true; } else c.LineTo(at, true, true);
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
            for (int i = 0; i < values.Length; i++)
                if (values[i] is double v && (i == 0 || values[i - 1] is null) && (i == values.Length - 1 || values[i + 1] is null))
                    dc.DrawEllipse(brush, null, new Point(plot.Left + slot * (i + 0.5), Y(Units.Temp(v))), 3, 3);
        }
    }

    private static double? Lerp(double? a, double? b, double t) => a is double x && b is double y ? x + (y - x) * t : b;

    /// <summary>
    /// The bar's box: when, what was in front, and the CPU's and GPU's average, highest and lowest. Beside the bar, its bottom at the graph's bottom when it's taller than the graph (it rises over the card
    /// above rather than under the one below).
    /// </summary>
    private void DrawHover(DrawingContext dc, IReadOnlyList<DayBucket> days, int i, Rect plot, double slot, bool hourly, bool monthly, Func<double, double> y)
    {
        var d = days[i];
        double cx = plot.Left + slot * (i + 0.5);
        foreach (var (v, brush) in new[] { (PointFor(d, Plot).Cpu, ChartPaint.Brush(ChartPaint.Cpu)), (PointFor(d, Plot).Gpu, ChartPaint.Brush(ChartPaint.Gpu)) })
            if (v is double t) dc.DrawEllipse(brush, new Pen(ChartPaint.TooltipBg, 2), new Point(cx, y(Units.Temp(t))), 4, 4);

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

        var colW = heads.Select(t => Math.Max(ChartPaint.Format(this, t, 11, ChartPaint.Muted).Width, ChartPaint.Format(this, "100.0 °F", 12, ChartPaint.TextBrush, true).Width)).ToArray();
        double nameW = rows.Count == 0 ? 0 : rows.Max(r => ChartPaint.Format(this, r.Name, 12, ChartPaint.TextBrush).Width) + 14;
        double tableW = rows.Count == 0 ? 0 : nameW + colW.Sum(c => c + gap);
        // The longest title the graph has (a day's name, a month's), not this bar's own.
        double titleW = ChartPaint.Format(this, monthly ? "September 2026" : hourly ? "12 PM – 12 AM" : "Wednesday, 30 September", 13, ChartPaint.TextBrush, bold: true).Width;
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
