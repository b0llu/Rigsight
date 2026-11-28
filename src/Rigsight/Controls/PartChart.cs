using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core.Parts;

namespace Rigsight.Controls;

/// <summary>A bar of the GPU or CPU chart: the stretch's average (the bar) and highest (a whisker and cap above it), in the unit shown.</summary>
/// <param name="Game">With a game in front (the part's full colour) or not (its soft shade).</param>
public sealed record PartBar(double Average, double? Highest, bool Game);

/// <summary>
/// One stretch of the GPU or CPU chart: an hour (one bar, coloured by whether a game was in front), or a day or a week
/// (a pair: with a game in front | everything else, either missing where it had none). Hatched when not recorded.
/// </summary>
/// <param name="Axis">Its label under the axis, or empty where the axis skips it.</param>
/// <param name="Tip">Its rich hover, made when first shown (so the games' icons are in).</param>
/// <param name="Old">An older part's stretch, from before the last new part (1.23.0): its bars drawn faint.</param>
public sealed record PartChartSlot(string Axis, bool Paired, PartBar? First, PartBar? Second, bool NotRecorded, Func<RichTip> Tip, bool Old = false);

/// <summary>
/// A mark on the GPU or CPU chart (1.23.0, approved mock-up "after-test" section 2), at the left edge of its stretch: a
/// graphics driver update (a dashed line, "◆ Driver 621.24" in the update colour, as the game chart's) or a new part (a
/// solid line, the part before faint before it and the new one after it). A label that doesn't fit takes its short form,
/// or none; the line always stays.
/// </summary>
/// <param name="Slot">The stretch it came in (the line is at its left edge).</param>
/// <param name="Before">A new part: the one before it, said before the line (null: not known).</param>
/// <param name="Tip">Its rich hover, made when first shown.</param>
/// <param name="Click">What a click on it does (a driver: its versions); null: nothing, and no hand.</param>
public sealed record PartChartMark(int Slot, bool NewPart, string Label, string ShortLabel, string? Before, Func<RichTip> Tip, Action? Click);

/// <summary>
/// The GPU and CPU pages' chart (1.23.0, approved mock-up section 10, histChart): a bar an hour on Day, a pair of bars a
/// day on Week, Month and Custom, a pair a week on Year; each bar the average, a whisker with a cap up to the highest.
/// Hover lights the stretch's column and shows its rich hover beside it.
/// </summary>
public sealed class PartChart : FanChartBase
{
    public static readonly DependencyProperty SlotsProperty = DependencyProperty.Register(
        nameof(Slots), typeof(IReadOnlyList<PartChartSlot>), typeof(PartChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MetricProperty = DependencyProperty.Register(
        nameof(Metric), typeof(PartMetric), typeof(PartChart), new FrameworkPropertyMetadata(PartMetric.Temperature, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarksProperty = DependencyProperty.Register(
        nameof(Marks), typeof(IReadOnlyList<PartChartMark>), typeof(PartChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<PartChartSlot>? Slots { get => (IReadOnlyList<PartChartSlot>?)GetValue(SlotsProperty); set => SetValue(SlotsProperty, value); }
    /// <summary>The driver updates and new parts in the period.</summary>
    public IReadOnlyList<PartChartMark>? Marks { get => (IReadOnlyList<PartChartMark>?)GetValue(MarksProperty); set => SetValue(MarksProperty, value); }
    /// <summary>What the bars measure, for the axis's labels.</summary>
    public PartMetric Metric { get => (PartMetric)GetValue(MetricProperty); set => SetValue(MetricProperty, value); }

    /// <summary>How far a stretch's hover may rise above the chart: over its heading, inside its card.</summary>
    private const double RiseAbove = 50;

    /// <summary>The row over the bars the marks' names take, when there are any.</summary>
    private const double MarkRow = 22;

    /// <summary>An older part's bars (the approved mock-up's 35%).</summary>
    private const double OldOpacity = 0.35;

    // Where each mark was drawn (its line and its names), for the pointer; and where the pointer is.
    private readonly List<(Rect Hit, int Index)> _markHits = [];
    private Point? _pointer;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _pointer = e.GetPosition(this);
        int mark = MarkAt(_pointer.Value);
        Cursor = mark >= 0 && Marks is { } marks && mark < marks.Count && marks[mark].Click is not null ? Cursors.Hand : null;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _pointer = null;
        Cursor = null;
        base.OnMouseLeave(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (MarkAt(e.GetPosition(this)) is var i and >= 0 && Marks is { } marks && i < marks.Count && marks[i].Click is { } click)
        {
            click();
            e.Handled = true;
        }
        base.OnMouseLeftButtonUp(e);
    }

    private int MarkAt(Point p)
    {
        // The last drawn first: a new part's line is drawn over a driver's.
        for (int k = _markHits.Count - 1; k >= 0; k--) if (_markHits[k].Hit.Contains(p)) return _markHits[k].Index;
        return -1;
    }

    private string AxisText(double v) => Metric switch
    {
        PartMetric.Temperature => $"{v:0}°",
        PartMetric.Load => $"{v:0}%",
        PartMetric.Power => $"{v:0} W",
        _ => v.ToString("N0", CultureInfo.CurrentCulture),
    };

    /// <summary>The axis: from 0 (a temperature from a round 10 under its lowest average), four round steps to past the highest.</summary>
    private (double Bottom, double Top) Range(IReadOnlyList<PartChartSlot> slots)
    {
        var bars = slots.SelectMany(s => new[] { s.First, s.Second }).OfType<PartBar>().ToList();
        if (bars.Count == 0) return (0, Metric == PartMetric.Load ? 100 : 4);
        double high = bars.Max(b => b.Highest ?? b.Average);
        if (Metric == PartMetric.Load) return (0, 100);
        double bottom = Metric == PartMetric.Temperature ? Math.Max(0, Math.Floor((bars.Min(b => b.Average) - 10) / 10) * 10) : 0;
        return (bottom, bottom + PowerChart.NiceTop(high - bottom));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var slots = Slots;
        if (slots is not { Count: > 0 } || ActualWidth < 60 || ActualHeight < 60) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var (bottom, top) = Range(slots);
        var marks = (Marks ?? []).Where(m => m.Slot >= 0 && m.Slot < slots.Count).ToList();
        double plotTop = Top + (marks.Count > 0 ? MarkRow : 0);
        var plot = new Rect(AxisWidth + 12, plotTop, Math.Max(1, ActualWidth - AxisWidth - 12 - 2), Math.Max(1, ActualHeight - AxisHeight - plotTop));
        var gridPen = new Pen(GridBrush, 1) { DashStyle = new DashStyle([3, 4], 0) };
        gridPen.Freeze();
        for (int i = 0; i <= 4; i++)
        {
            double y = Math.Round(plot.Bottom - plot.Height * i / 4) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            Label(dc, AxisText(bottom + (top - bottom) * i / 4), new Point(plot.Left - 8, y), LabelBrush, right: true);
        }
        double slotW = plot.Width / slots.Count;
        double Y(double v) => plot.Bottom - plot.Height * Math.Clamp((v - bottom) / (top - bottom), 0, 1);
        Brush full = Brush, soft = Faded(Brush, 0.38);
        var whisker = new Pen(Faded(Brush, 0.7), 1);
        whisker.Freeze();
        var softWhisker = new Pen(Faded(Brush, 0.45), 1);
        softWhisker.Freeze();
        var cap = new Pen(full, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        cap.Freeze();
        var softCap = new Pen(soft, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        softCap.Freeze();
        var hatch = Hatch();

        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            double x0 = plot.Left + slotW * i;
            if (s.NotRecorded)
            {
                double hw = Math.Max(2, slotW - 6);
                dc.DrawRoundedRectangle(hatch, null, new Rect(x0 + (slotW - hw) / 2, plot.Top, hw, plot.Height), 3, 3);
                continue;
            }
            if (s.Old) dc.PushOpacity(OldOpacity);
            int n = s.Paired ? 2 : 1;
            double bw = Math.Max(2, Math.Min(s.Paired ? 34 : 30, (slotW - 6) / n - 2)), gap = n > 1 ? Math.Min(3, bw / 3) : 0;
            double total = n * bw + (n - 1) * gap, bx = x0 + (slotW - total) / 2;
            var bars = s.Paired ? new[] { s.First, s.Second } : new[] { s.First };
            for (int k = 0; k < bars.Length; k++)
            {
                if (bars[k] is not { } b) continue;
                double x = bx + k * (bw + gap), ya = Y(b.Average);
                bool game = b.Game;
                if (b.Highest is double h && Y(h) < ya - 1)
                {
                    double yh = Math.Round(Y(h)) + 0.5, cx = Math.Round(x + bw / 2) + 0.5;
                    dc.DrawLine(game ? whisker : softWhisker, new Point(cx, yh), new Point(cx, ya));
                    dc.DrawLine(game ? cap : softCap, new Point(x + 1, yh), new Point(x + bw - 1, yh));
                }
                double r = Math.Min(3, bw / 2);
                if (plot.Bottom - ya >= 1) dc.DrawRoundedRectangle(game ? full : soft, null, new Rect(x, ya, bw, plot.Bottom - ya), r, r);
            }
            if (s.Old) dc.Pop();
        }
        for (int i = 0; i < slots.Count; i++)
            if (slots[i].Axis.Length > 0) Label(dc, slots[i].Axis, new Point(plot.Left + slotW * (i + 0.5), plot.Bottom + AxisHeight / 2 + 2), LabelBrush, center: true);

        DrawMarks(dc, marks, plot, slotW);

        var bounds = new Rect(0, -RiseAbove, ActualWidth, ActualHeight + RiseAbove);
        if (_pointer is { } at && MarkAt(at) is var hm and >= 0 && Marks is { } all && hm < all.Count)
        {
            // A mark: its stretch lit, its hover beside the mark and its names.
            var mark = all[hm];
            var column = new Rect(plot.Left + slotW * Math.Clamp(mark.Slot, 0, slots.Count - 1) + 1, plot.Top, Math.Max(1, slotW - 2), plot.Height);
            RichHover.Lit(dc, column);
            var near = _markHits.Where(h => h.Index == hm).Select(h => h.Hit).Aggregate(column, (a, b) => Rect.Union(a, b));
            RichHover.Draw(dc, this, MarkTip(hm, mark.Tip), near, bounds);
        }
        else if (HoverX is double hx && hx >= plot.Left && hx <= plot.Right)
        {
            int i = Math.Clamp((int)((hx - plot.Left) / slotW), 0, slots.Count - 1);
            var column = new Rect(plot.Left + slotW * i + 1, plot.Top, Math.Max(1, slotW - 2), plot.Height);
            RichHover.Lit(dc, column);
            RichHover.Draw(dc, this, Tip(i, slots[i].Tip), column, bounds);
        }
    }

    /// <summary>
    /// The marks over the bars: a new part's solid line with the part before (faint, before it) and the new one after it,
    /// then each driver's dashed line with its diamond and version, as the game chart's. The new parts' names go first;
    /// a driver's name takes its short form where the long one would run into another, or none, and its line stays.
    /// </summary>
    private void DrawMarks(DrawingContext dc, List<PartChartMark> marks, Rect plot, double slotW)
    {
        _markHits.Clear();
        if (marks.Count == 0) return;
        var all = Marks!;
        double rowMid = plot.Top - MarkRow / 2 - 3, lineTop = plot.Top - 8;
        var taken = new List<(double L, double R)>();
        bool Free(double l, double r) => l >= 0 && r <= ActualWidth && taken.All(t => r + 6 <= t.L || l >= t.R + 6);
        double X(PartChartMark m) => Math.Round(plot.Left + slotW * m.Slot) + 0.5;

        var dashed = new Pen(ChartPaint.Brush(ChartPaint.Res("MutedColor", 0xA3), 0.8), 1) { DashStyle = new DashStyle([3, 3], 0) };
        dashed.Freeze();
        var solid = new Pen(ChartPaint.TextBrush, 1.5);
        solid.Freeze();

        // Drivers' lines first, so a new part's solid line is over one on the same edge.
        foreach (var m in marks.Where(m => !m.NewPart))
        {
            double x = X(m);
            dc.DrawLine(dashed, new Point(x, lineTop), new Point(x, plot.Bottom));
            var diamond = new StreamGeometry();
            using (var g = diamond.Open())
            {
                g.BeginFigure(new Point(x, rowMid - 4.5), true, true);
                g.LineTo(new Point(x + 4.5, rowMid), true, false);
                g.LineTo(new Point(x, rowMid + 4.5), true, false);
                g.LineTo(new Point(x - 4.5, rowMid), true, false);
            }
            diamond.Freeze();
            dc.DrawGeometry(ChartPaint.FpsMarkText, null, diamond);
        }
        foreach (var m in marks.Where(m => m.NewPart))
            dc.DrawLine(solid, new Point(X(m), lineTop), new Point(X(m), plot.Bottom));

        // The names: the new parts' first (they say what every bar before them is), then the drivers', left to right.
        foreach (var m in marks.Where(m => m.NewPart).Concat(marks.Where(m => !m.NewPart)).ToList())
        {
            int index = IndexOf(all, m);
            double x = X(m);
            var hit = new Rect(x - 6, lineTop, 12, plot.Bottom - lineTop);
            if (m.NewPart)
            {
                var name = Fit(x + 8, [ChartPaint.Format(this, m.Label, 11, ChartPaint.TextBrush, bold: true), ChartPaint.Format(this, m.ShortLabel, 11, ChartPaint.TextBrush, bold: true)]);
                if (name is not null)
                {
                    dc.DrawText(name, new Point(x + 8, rowMid - name.Height / 2));
                    hit.Union(new Rect(x + 4, rowMid - 10, name.Width + 8, 20));
                }
                if (m.Before is { Length: > 0 } before)
                {
                    var was = ChartPaint.Format(this, before, 11, ChartPaint.Faint);
                    if (Free(x - 8 - was.Width, x - 8))
                    {
                        taken.Add((x - 8 - was.Width, x - 8));
                        dc.DrawText(was, new Point(x - 8 - was.Width, rowMid - was.Height / 2));
                        hit.Union(new Rect(x - 12 - was.Width, rowMid - 10, was.Width + 8, 20));
                    }
                }
            }
            else
            {
                hit.Union(new Rect(x - 8, rowMid - 9, 16, 18));
                var name = Fit(x + 8, [ChartPaint.Format(this, m.Label, 10.5, ChartPaint.FpsMarkText), ChartPaint.Format(this, m.ShortLabel, 10.5, ChartPaint.FpsMarkText)]);
                if (name is not null)
                {
                    dc.DrawText(name, new Point(x + 8, rowMid - name.Height / 2));
                    hit.Union(new Rect(x - 8, rowMid - 9, 16 + name.Width + 4, 18));
                }
            }
            _markHits.Add((hit, index));
        }

        // The first of the forms that fits at x (and takes its room), or none.
        FormattedText? Fit(double x, FormattedText[] forms)
        {
            foreach (var f in forms)
                if (Free(x, x + f.Width))
                {
                    taken.Add((x, x + f.Width));
                    return f;
                }
            return null;
        }

        static int IndexOf(IReadOnlyList<PartChartMark> list, PartChartMark m)
        {
            for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], m)) return i;
            return -1;
        }
    }

    // The hover of the mark under the pointer, made once while it stays there.
    private (IReadOnlyList<PartChartMark>? Marks, int Index, RichTip? Tip) _markTip;

    private RichTip MarkTip(int i, Func<RichTip> make)
    {
        if (ReferenceEquals(_markTip.Marks, Marks) && _markTip.Index == i && _markTip.Tip is { } made) return made;
        var tip = make();
        _markTip = (Marks, i, tip);
        return tip;
    }

    // The hover of the stretch under the pointer, made once while it stays there.
    private (IReadOnlyList<PartChartSlot>? Slots, int Index, RichTip? Tip) _tip;

    private RichTip Tip(int i, Func<RichTip> make)
    {
        if (ReferenceEquals(_tip.Slots, Slots) && _tip.Index == i && _tip.Tip is { } made) return made;
        var tip = make();
        _tip = (Slots, i, tip);
        return tip;
    }

    // The stripes of a stretch not recorded (as Start-up's).
    private static Brush? _hatch;
    private static int _hatchTheme = -1;

    private static Brush Hatch()
    {
        if (_hatch is not null && _hatchTheme == Services.ThemeManager.Version) return _hatch;
        _hatchTheme = Services.ThemeManager.Version;
        var group = new DrawingGroup();
        using (var g = group.Open())
        {
            g.DrawRectangle(ChartPaint.Track, null, new Rect(0, 0, 6, 6));
            g.DrawRectangle(ChartPaint.Brush(ChartPaint.Res("FaintColor", 0x6B), 0.3), null, new Rect(0, 0, 3, 6));
        }
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 6, 6), ViewportUnits = BrushMappingMode.Absolute, Transform = new RotateTransform(45) };
        brush.Freeze();
        return _hatch = brush;
    }
}
