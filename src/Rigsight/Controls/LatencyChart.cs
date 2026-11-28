using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Core.Drivers;

namespace Rigsight.Controls;

/// <summary>
/// One slot of the Latency chart: an hour or a day. <paramref name="Value"/> is µs (a delay) or a count (calls over 1 ms);
/// null was not recorded, never drawn as 0.
/// </summary>
/// <param name="Lost">Windows dropped some readings in it: drawn grey, and its hover says so.</param>
/// <param name="Long">1 ms or more: drawn in full (in <see cref="LatencyChart.LongBrush"/> when set), the rest softer.</param>
/// <param name="Start">When the slot starts and <paramref name="End"/> ends: what a stretch with nothing recorded says in its hover.</param>
/// <param name="Rich">Since 1.22.0, its rich hover (see <see cref="RichHover"/>), made when first shown; null: the lines.</param>
public sealed record LatencyChartSlot(string Axis, string Title, double? Value, bool Lost, bool Long, IReadOnlyList<string> Lines, DateTime Start = default, DateTime End = default,
    Func<RichTip>? Rich = null);

/// <summary>One bar of a live strip: µs, whether it's 1 ms or more, and its hover.</summary>
/// <param name="Rich">Since 1.23.0, its rich hover (see <see cref="RichHover"/>), made when first shown; null: the lines of <paramref name="Tip"/>.</param>
public sealed record LiveBar(double Us, bool Long, string Tip, Func<RichTip>? Rich = null);

/// <summary>
/// The Latency page's chart (issue #15, approved mock-up 2026-11-24): a bar each hour (or day), the longest driver delay,
/// app wake-up delay or calls over 1 ms in it. For delays, a dashed line at 1 ms and a fainter one at Microsoft's 0.1 ms
/// guidance for drivers, each labelled as what it is (never a pass mark). Hover: a guide and the slot's figures.
/// </summary>
public sealed class LatencyChart : FanChartBase
{
    public static readonly DependencyProperty SlotsProperty = DependencyProperty.Register(
        nameof(Slots), typeof(IReadOnlyList<LatencyChartSlot>), typeof(LatencyChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsMsProperty = DependencyProperty.Register(
        nameof(IsMs), typeof(bool), typeof(LatencyChart), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GuidanceProperty = DependencyProperty.Register(
        nameof(Guidance), typeof(bool), typeof(LatencyChart), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(LatencyChart), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LongBrushProperty = DependencyProperty.Register(
        nameof(LongBrush), typeof(Brush), typeof(LatencyChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Bars by level (1.23.0, issue #18 item 12, the owner's "Blue and amber"): a delay of 1 ms or longer
    /// (<see cref="DriverRules.LongUs"/>) in this colour, the amber; the rest in a softer <see cref="BarBrush"/>. Null: one
    /// colour, brighter from 1 ms (the app wake-up delay's violet, and charts of counts).
    /// </summary>
    public Brush? LongBrush { get => (Brush?)GetValue(LongBrushProperty); set => SetValue(LongBrushProperty, value); }

    /// <summary>Why a stretch has nothing recorded (its ends in Unix milliseconds), as on Temperatures: the PC was off, asleep, shut off suddenly.</summary>
    public static readonly DependencyProperty GapReasonProperty = DependencyProperty.Register(
        nameof(GapReason), typeof(Func<long, long, Core.Stability.GapReason>), typeof(LatencyChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Func<long, long, Core.Stability.GapReason>? GapReason { get => (Func<long, long, Core.Stability.GapReason>?)GetValue(GapReasonProperty); set => SetValue(GapReasonProperty, value); }

    /// <summary>
    /// When what the chart shows was first recorded: a stretch before it is "not recorded yet", never put down to what
    /// Windows' log says happened in it (a sudden shut-off days before the wake-up check existed was named for all of it).
    /// </summary>
    public static readonly DependencyProperty RecordedFromProperty = DependencyProperty.Register(
        nameof(RecordedFrom), typeof(DateTime?), typeof(LatencyChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public DateTime? RecordedFrom { get => (DateTime?)GetValue(RecordedFromProperty); set => SetValue(RecordedFromProperty, value); }

    // A run of empty slots that ends before anything was recorded.
    private bool BeforeRecording(IReadOnlyList<LatencyChartSlot> slots, int to) =>
        RecordedFrom is { } first && slots[to - 1].End != default && slots[to - 1].End <= first;

    public static readonly DependencyProperty PlateBrushProperty = DependencyProperty.Register(
        nameof(PlateBrush), typeof(Brush), typeof(LatencyChart), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public LatencyChart()
    {
        SetResourceReference(BarBrushProperty, "TextBrush");
        SetResourceReference(PlateBrushProperty, "SurfaceBrush");
    }

    /// <summary>The card's colour, behind a line's label so it reads over a bar.</summary>
    public Brush PlateBrush { get => (Brush)GetValue(PlateBrushProperty); set => SetValue(PlateBrushProperty, value); }

    public IReadOnlyList<LatencyChartSlot>? Slots { get => (IReadOnlyList<LatencyChartSlot>?)GetValue(SlotsProperty); set => SetValue(SlotsProperty, value); }
    /// <summary>The values are µs, said in ms (else counts).</summary>
    public bool IsMs { get => (bool)GetValue(IsMsProperty); set => SetValue(IsMsProperty, value); }
    /// <summary>Draws Microsoft's 0.1 ms guidance for drivers (driver delays only).</summary>
    public bool Guidance { get => (bool)GetValue(GuidanceProperty); set => SetValue(GuidanceProperty, value); }
    public Brush BarBrush { get => (Brush)GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }

    private static string Axis(double v, bool ms) => !ms ? v.ToString("N0", CultureInfo.CurrentCulture)
        : v == 0 ? "0" : v >= 1000 ? $"{v / 1000:0.#} ms" : $"{v / 1000:0.##} ms";

    protected override void OnRender(DrawingContext dc)
    {
        var slots = Slots;
        if (slots is not { Count: > 0 } || ActualWidth < 60 || ActualHeight < 60) return;
        bool ms = IsMs;
        double max = slots.Max(s => s.Value ?? 0);
        // A delay chart always reaches past 1 ms, so the 1 ms line is in view and a quiet week isn't drawn as if it weren't.
        if (ms && Guidance) max = Math.Max(max, DriverRules.LongUs * 1.2);
        double top = PowerChart.NiceTop(Math.Max(max, ms ? 100 : 4));
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var plot = new Rect(AxisWidth + 12, Top, Math.Max(1, ActualWidth - AxisWidth - 12 - 2), Math.Max(1, ActualHeight - AxisHeight - Top));
        var gridPen = new Pen(GridBrush, 1) { DashStyle = new DashStyle([3, 4], 0) };
        gridPen.Freeze();
        for (int i = 0; i <= 4; i++)
        {
            double y = Math.Round(plot.Bottom - plot.Height * i / 4) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            Label(dc, Axis(top * i / 4, ms), new Point(plot.Left - 8, y), LabelBrush, right: true);
        }
        double slotW = plot.Width / slots.Count;
        double Y(double v) => plot.Bottom - plot.Height * Math.Clamp(v / top, 0, 1);
        double X(int i) => plot.Left + slotW * (i + 0.5);

        // Not recorded, as on Temperatures: a thin strip along the foot of the chart (shaded top to bottom, the empty
        // stretches were what the eye went to), and a small red mark where the PC shut off suddenly or Windows crashed.
        var strip = Faded(LabelBrush, 0.45);
        foreach (var (i, j) in Gaps(slots))
        {
            dc.DrawRectangle(strip, null, new Rect(plot.Left + slotW * i, plot.Bottom - 3, slotW * (j - i), 3));
            if (!BeforeRecording(slots, j) && Reason(slots, i, j) is Core.Stability.GapReason.ShutOff or Core.Stability.GapReason.Crashed)
            {
                double x = Math.Round(plot.Left + slotW * i) + 0.5;
                var mark = new StreamGeometry();
                using (var g = mark.Open())
                {
                    g.BeginFigure(new Point(x - 5, plot.Bottom), true, true);
                    g.LineTo(new Point(x + 5, plot.Bottom), false, false);
                    g.LineTo(new Point(x, plot.Bottom - 8), false, false);
                }
                mark.Freeze();
                dc.DrawGeometry(ChartPaint.Hot, null, mark);
            }
        }

        // The slot under the pointer, when it has its own hover: its column lit behind the bars.
        int? hovered = HoverX is double px && px >= plot.Left && px <= plot.Right ? Math.Clamp((int)((px - plot.Left) / slotW), 0, slots.Count - 1) : null;
        bool rich = hovered is int hr && slots[hr].Rich is not null && slots[hr].Value is not null;
        var lit = hovered is int li ? new Rect(plot.Left + slotW * li, plot.Top, Math.Max(1, slotW), plot.Height) : Rect.Empty;
        if (rich) RichHover.Lit(dc, lit);

        double barW = Math.Max(2, Math.Min(26, slotW * 0.7));
        var soft = Faded(BarBrush, 0.5);
        var lost = Faded(LabelBrush, 0.7);
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].Value is not double v || v <= 0) continue;
            double h = Math.Max(1.5, plot.Bottom - Y(v));
            var brush = slots[i].Lost ? lost : !ms ? BarBrush : slots[i].Long ? LongBrush ?? BarBrush : soft;
            dc.DrawRoundedRectangle(brush, null, new Rect(X(i) - barW / 2, plot.Bottom - h, barW, h), Math.Min(2, barW / 3), Math.Min(2, barW / 3));
        }

        if (ms && Guidance)
        {
            // The 1 ms line in a soft amber (1.22.0, the approved mock-up), its label too.
            Line(dc, plot, Y(DriverRules.LongUs), Faded(ChartPaint.Warm, 0.5), [4, 4], Lang.T("1 ms"), Faded(ChartPaint.Warm, 0.85));
            if (DriverRules.GuidanceDpcUs / top > 0.04)
                Line(dc, plot, Y(DriverRules.GuidanceDpcUs), Faded(LabelBrush, 0.55), [2, 4], Lang.T("Microsoft's aim for drivers, 0.1 ms"));
        }

        for (int i = 0; i < slots.Count; i++)
            if (slots[i].Axis.Length > 0) Label(dc, slots[i].Axis, new Point(X(i), plot.Bottom + AxisHeight / 2 + 2), LabelBrush, center: true);

        if (HoverX is double hx && hx >= plot.Left && hx <= plot.Right)
        {
            int i = Math.Clamp((int)((hx - plot.Left) / slotW), 0, slots.Count - 1);
            // Over a stretch with nothing recorded: why, where Windows' log says, and from when to when (as on Temperatures).
            if (slots[i].Value is null && slots[i].Start != default && Gaps(slots).FirstOrDefault(g => i >= g.From && i < g.To) is { To: > 0 } gap)
            {
                bool hourly = slots[gap.From].End - slots[gap.From].Start < TimeSpan.FromDays(1);
                string Moment(DateTime t) => !hourly ? t.ToString("d MMM", CultureInfo.CurrentCulture)
                    : (t.Date == DateTime.Today ? "" : t.ToString("ddd ", CultureInfo.CurrentCulture)) + TimeText.Time(t);
                string from = Moment(slots[gap.From].Start), until = Moment(slots[gap.To - 1].End);
                if (BeforeRecording(slots, gap.To))
                    Hover(dc, plot, X(i), Lang.T("Not recorded yet"), [Lang.T($"{from} to {until}"), Lang.T($"Recording began on {RecordedFrom!.Value:d MMM}")]);
                else
                {
                    var reason = Reason(slots, gap.From, gap.To);
                    string? why = reason == Core.Stability.GapReason.NotRunning ? null : Core.Stability.PowerLog.Words(reason);
                    Hover(dc, plot, X(i), why ?? Wording.NotRecorded, [why is null ? Lang.T($"{from} to {until}") : Lang.T($"Not recorded  ·  {from} to {until}")]);
                }
            }
            else if (rich && slots[i].Rich is { } make)
                // The hour's own hover beside its column: it may rise over the card's heading, never below the chart.
                RichHover.Draw(dc, this, Tip(i, make), lit, new Rect(0, -RiseAbove, ActualWidth, ActualHeight + RiseAbove));
            else Hover(dc, plot, X(i), slots[i].Title, slots[i].Lines);
        }
    }

    /// <summary>How far a rich hover may rise above the chart, over the card's heading, to fit.</summary>
    private const double RiseAbove = 120;

    // The rich hover of the slot under the pointer, made once while it stays there.
    private (IReadOnlyList<LatencyChartSlot>? Slots, int Index, RichTip? Tip) _tip;

    private RichTip Tip(int i, Func<RichTip> make)
    {
        if (ReferenceEquals(_tip.Slots, Slots) && _tip.Index == i && _tip.Tip is { } made) return made;
        var tip = make();
        _tip = (Slots, i, tip);
        return tip;
    }

    // The runs of slots with nothing recorded: [From, To) by index.
    private static List<(int From, int To)> Gaps(IReadOnlyList<LatencyChartSlot> slots)
    {
        var gaps = new List<(int, int)>();
        for (int i = 0; i < slots.Count;)
        {
            if (slots[i].Value is not null) { i++; continue; }
            int j = i;
            while (j < slots.Count && slots[j].Value is null) j++;
            gaps.Add((i, j));
            i = j;
        }
        return gaps;
    }

    // Why a run of empty slots is empty (unknown without the slots' times or Windows' log).
    private Core.Stability.GapReason Reason(IReadOnlyList<LatencyChartSlot> slots, int from, int to) =>
        GapReason is { } reason && slots[from].Start != default
            ? reason(new DateTimeOffset(slots[from].Start).ToUnixTimeMilliseconds(), new DateTimeOffset(slots[to - 1].End).ToUnixTimeMilliseconds())
            : Core.Stability.GapReason.Unknown;

    // A labelled reference line across the plot, the label right-aligned above it.
    private void Line(DrawingContext dc, Rect plot, double y, Brush brush, double[] dashes, string label, Brush? labelBrush = null)
    {
        var pen = new Pen(brush, 1) { DashStyle = new DashStyle(dashes, 0) };
        pen.Freeze();
        y = Math.Round(y) + 0.5;
        dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
        var text = Text(label, labelBrush ?? LabelBrush);
        var at = new Point(plot.Right - text.Width - 4, y - text.Height - 2);
        dc.DrawRoundedRectangle(PlateBrush, null, new Rect(at.X - 4, at.Y - 1, text.Width + 8, text.Height + 2), 3, 3);
        dc.DrawText(text, at);
    }
}

/// <summary>
/// The Latency page's live strip (issue #15): the last 2 minutes as bars (driver delay, each 2 seconds) or a line (app
/// wake-up delay, each second), with a dashed 1 ms line on the bars and "2 minutes ago" and "Now" under them. Hover: the
/// second and its figure.
/// </summary>
public sealed class LiveStrip : FanChartBase
{
    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(
        nameof(Bars), typeof(IReadOnlyList<LiveBar>), typeof(LiveStrip), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsLineProperty = DependencyProperty.Register(
        nameof(IsLine), typeof(bool), typeof(LiveStrip), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(LiveStrip), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled), typeof(bool), typeof(LiveStrip), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LongBrushProperty = DependencyProperty.Register(
        nameof(LongBrush), typeof(Brush), typeof(LiveStrip), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Bars of 1 ms or longer in this colour (1.23.0, the amber), the rest in a softer <see cref="Stroke"/>; null: the stroke, in full.</summary>
    public Brush? LongBrush { get => (Brush?)GetValue(LongBrushProperty); set => SetValue(LongBrushProperty, value); }

    /// <summary>A line with a soft fill of its colour under it, fading down (1.22.0, the app wake-up delay).</summary>
    public bool Filled { get => (bool)GetValue(FilledProperty); set => SetValue(FilledProperty, value); }

    public IReadOnlyList<LiveBar>? Bars { get => (IReadOnlyList<LiveBar>?)GetValue(BarsProperty); set => SetValue(BarsProperty, value); }
    public bool IsLine { get => (bool)GetValue(IsLineProperty); set => SetValue(IsLineProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var bars = Bars;
        var plot = new Rect(0, 12, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight - 12 - 18));
        Label(dc, Lang.T("2 minutes ago"), new Point(0, plot.Bottom + 10), LabelBrush);
        var now = Text(Lang.T("Now"), LabelBrush);
        dc.DrawText(now, new Point(plot.Right - now.Width, plot.Bottom + 10 - now.Height / 2));
        if (bars is not { Count: > 0 }) return;
        double max = Math.Max(bars.Max(b => b.Us), IsLine ? 100 : DriverRules.LongUs * 1.2);
        double top = PowerChart.NiceTop(max);
        double Y(double v) => plot.Bottom - plot.Height * Math.Clamp(v / top, 0, 1);
        double w = plot.Width / bars.Count;
        // The bar or second under the pointer, its column lit behind the marks when it has a rich hover (1.23.0).
        int? hovered = HoverX is double px && px >= 0 && px <= plot.Right ? Math.Clamp((int)(px / w), 0, bars.Count - 1) : null;
        var column = hovered is int c ? new Rect(w * c, plot.Top, w, plot.Height) : Rect.Empty;
        if (hovered is int lit && bars[lit].Rich is not null) RichHover.Lit(dc, column);
        if (IsLine)
        {
            var pen = new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            if (Filled && Stroke is SolidColorBrush solid) DrawFill(dc, bars, solid.Color, plot, w, Y);
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                bool open = false;
                for (int i = 0; i < bars.Count; i++)
                {
                    if (bars[i].Us <= 0) { open = false; continue; }
                    var p = new Point(w * (i + 0.5), Y(bars[i].Us));
                    if (!open) { g.BeginFigure(p, false, false); open = true; } else g.LineTo(p, true, true);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
            var t = Text(Axis(top), LabelBrush);
            dc.DrawText(t, new Point(plot.Right - t.Width, 0));
        }
        else
        {
            var soft = Faded(Stroke, 0.45);
            for (int i = 0; i < bars.Count; i++)
            {
                if (bars[i].Us <= 0) continue;
                double h = Math.Max(1.5, plot.Bottom - Y(bars[i].Us));
                dc.DrawRoundedRectangle(bars[i].Long ? LongBrush ?? Stroke : soft, null, new Rect(w * i + 1, plot.Bottom - h, Math.Max(1, w - 2), h), 1.5, 1.5);
            }
            var pen = new Pen(Faded(LabelBrush, 0.8), 1) { DashStyle = new DashStyle([3, 3], 0) };
            pen.Freeze();
            double y = Math.Round(Y(DriverRules.LongUs)) + 0.5;
            dc.DrawLine(pen, new Point(0, y), new Point(plot.Right, y));
            var t = Text(Lang.T("1 ms"), LabelBrush);
            dc.DrawText(t, new Point(plot.Right - t.Width, y - t.Height - 1));
        }
        if (hovered is int at)
        {
            if (bars[at].Rich is { } make)
                RichHover.Draw(dc, this, Tip(bars, at, make), column, new Rect(0, -RiseAbove, ActualWidth, ActualHeight + RiseAbove));
            else
            {
                var lines = bars[at].Tip.Split('\n');
                Hover(dc, plot, w * (at + 0.5), lines[0], lines.Skip(1).ToList());
            }
        }
    }

    // The strip sits under the page's big figure: the hover rises over it rather than over the list below, which draws later.
    private const double RiseAbove = 170;

    // The hover of the bar under the pointer, made once while it stays there (the strip redraws every second).
    private (IReadOnlyList<LiveBar>? Bars, int Index, RichTip? Tip) _tip;

    private RichTip Tip(IReadOnlyList<LiveBar> bars, int i, Func<RichTip> make)
    {
        if (ReferenceEquals(_tip.Bars, bars) && _tip.Index == i && _tip.Tip is { } made) return made;
        var tip = make();
        _tip = (bars, i, tip);
        return tip;
    }

    private static string Axis(double us) => us >= 1000 ? $"{us / 1000:0.#} ms" : $"{us / 1000:0.##} ms";

    // Under each run of the line, its colour fading from a third to nothing at the foot.
    private static void DrawFill(DrawingContext dc, IReadOnlyList<LiveBar> bars, Color color, Rect plot, double w, Func<double, double> y)
    {
        var fill = new LinearGradientBrush(Color.FromArgb(90, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B), 90);
        fill.Freeze();
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            int i = 0;
            while (i < bars.Count)
            {
                if (bars[i].Us <= 0) { i++; continue; }
                int start = i;
                g.BeginFigure(new Point(w * (start + 0.5), plot.Bottom), true, true);
                for (; i < bars.Count && bars[i].Us > 0; i++) g.LineTo(new Point(w * (i + 0.5), y(bars[i].Us)), false, false);
                g.LineTo(new Point(w * (i - 0.5), plot.Bottom), false, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(fill, null, geo);
    }
}
