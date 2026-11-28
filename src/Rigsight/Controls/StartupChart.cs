using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Core.Startup;

namespace Rigsight.Controls;

/// <summary>How a figure in a Start-up hover is coloured.</summary>
public enum TipTone { Plain, Warm, Good, Quiet }

/// <summary>A part's row in a start's hover: its dot, name and length, amber when it ran long, with its usual under it then.</summary>
public sealed record StartupTipRow(int Part, string Name, string Value, bool Warm, string? Usual);

/// <summary>A figure in a week's hover's shaded box: a caps label over its value.</summary>
public sealed record StartupTipFact(string Label, string Value, TipTone Tone);

/// <summary>A line in a hover's box of what's behind it: an app's icon (its file) or a glyph, what it says, a figure at the right.</summary>
public sealed record StartupTipItem(string? IconPath, string? Glyph, string Text, string? Right, string Letter = "");

/// <summary>
/// A Start-up hover (approved mock-up 2026-11-27, richTip): a title and a grey line, one big figure with its word, a split bar
/// in the parts' colours, the parts as rows, a shaded box of figures, a box of what's behind it, and what a click does.
/// </summary>
public sealed record StartupTip(string Title, string? Sub, string Big, string BigWord, TipTone BigTone)
{
    public IReadOnlyList<(int Part, double Value)> Split { get; init; } = [];
    public IReadOnlyList<StartupTipRow> Rows { get; init; } = [];
    public IReadOnlyList<StartupTipFact> Facts { get; init; } = [];
    public string? NoteLabel { get; init; }
    public IReadOnlyList<StartupTipItem> Items { get; init; } = [];
    public string? Foot { get; init; }
}

/// <summary>
/// One bar of the Start-up chart: a start (or a week's average on Year), its parts in seconds stacked from the bottom
/// (before Windows, Windows starting, getting ready after sign-in). <see cref="Clock"/> places it on the day (0 to 1) on
/// Day; otherwise bars stand side by side, oldest left.
/// </summary>
public sealed record StartupBar(double? Bios, double? Windows, double? After, StartupTip Tip)
{
    /// <summary>A restart after an update: striped, cut with a break and its figure above when past the top.</summary>
    public bool Update { get; init; }
    /// <summary>Windows didn't record it: a dashed outline, never a 0s bar.</summary>
    public bool Missing { get; init; }
    /// <summary>Nothing in it (a week without starts): no bar, and nothing to open.</summary>
    public bool Empty { get; init; }
    /// <summary>Longer than its usual: an amber dot over it.</summary>
    public bool Longer { get; init; }
    /// <summary>A Fast Startup: an F over it.</summary>
    public bool Fast { get; init; }
    /// <summary>A restart after an update's whole length (for the figure over its cut bar).</summary>
    public double? Total { get; init; }
    public double Clock { get; init; } = -1;
    public string Axis { get; init; } = "";
    public double Height => (Bios ?? 0) + (Windows ?? 0) + (After ?? 0);
}

/// <summary>
/// The Start-up page's chart (approved mock-up 2026-11-27, chart()): one bar per start (per week on Year), the three parts
/// stacked in their colours, a dashed band for the usual of each kind shown, an amber dot over a longer start, an F over a
/// Fast Startup, a restart after an update striped (cut with a break and its figure when past the top), a dashed outline
/// where Windows didn't record a start, and a ring around the opened one. Day places the bars on a 12 AM to 12 AM clock.
/// Hover: the column lit and a rich box beside the bar; a click opens the start (or the week).
/// </summary>
public sealed class StartupChart : FrameworkElement
{
    public static readonly DependencyProperty BarsProperty = Register(nameof(Bars), typeof(IReadOnlyList<StartupBar>), null);
    public static readonly DependencyProperty TopProperty = Register(nameof(Top), typeof(double), 90.0);
    public static readonly DependencyProperty BandsProperty = Register(nameof(Bands), typeof(IReadOnlyList<Usual>), null);
    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(nameof(Selected), typeof(int), typeof(StartupChart),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty IsClockProperty = Register(nameof(IsClock), typeof(bool), false);
    public static readonly DependencyProperty BiosBrushProperty = Register(nameof(BiosBrush), typeof(Brush), Brushes.Pink);
    public static readonly DependencyProperty WindowsBrushProperty = Register(nameof(WindowsBrush), typeof(Brush), Brushes.CornflowerBlue);
    public static readonly DependencyProperty AfterBrushProperty = Register(nameof(AfterBrush), typeof(Brush), Brushes.MediumPurple);
    public static readonly DependencyProperty WarmBrushProperty = Register(nameof(WarmBrush), typeof(Brush), Brushes.Gold);
    public static readonly DependencyProperty GoodBrushProperty = Register(nameof(GoodBrush), typeof(Brush), Brushes.SeaGreen);
    public static readonly DependencyProperty PickCommandProperty = DependencyProperty.Register(nameof(PickCommand), typeof(ICommand), typeof(StartupChart));

    private static DependencyProperty Register(string name, Type type, object? value) =>
        DependencyProperty.Register(name, type, typeof(StartupChart), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));

    public StartupChart()
    {
        SetResourceReference(BiosBrushProperty, "PinkBrush");
        SetResourceReference(WindowsBrushProperty, "CpuBrush");
        SetResourceReference(AfterBrushProperty, "PurpleBrush");
        SetResourceReference(WarmBrushProperty, "WarmBrush");
        SetResourceReference(GoodBrushProperty, "GoodBrush");
        Focusable = false;
    }

    public IReadOnlyList<StartupBar>? Bars { get => (IReadOnlyList<StartupBar>?)GetValue(BarsProperty); set => SetValue(BarsProperty, value); }
    /// <summary>The top of the scale, in seconds (a bar past it is cut).</summary>
    public double Top { get => (double)GetValue(TopProperty); set => SetValue(TopProperty, value); }
    /// <summary>The usual of each kind shown, a dashed band each.</summary>
    public IReadOnlyList<Usual>? Bands { get => (IReadOnlyList<Usual>?)GetValue(BandsProperty); set => SetValue(BandsProperty, value); }
    /// <summary>The opened bar (-1 none), ringed.</summary>
    public int Selected { get => (int)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }
    /// <summary>Day: the bars on a 12 AM to 12 AM clock.</summary>
    public bool IsClock { get => (bool)GetValue(IsClockProperty); set => SetValue(IsClockProperty, value); }
    public Brush BiosBrush { get => (Brush)GetValue(BiosBrushProperty); set => SetValue(BiosBrushProperty, value); }
    public Brush WindowsBrush { get => (Brush)GetValue(WindowsBrushProperty); set => SetValue(WindowsBrushProperty, value); }
    public Brush AfterBrush { get => (Brush)GetValue(AfterBrushProperty); set => SetValue(AfterBrushProperty, value); }
    public Brush WarmBrush { get => (Brush)GetValue(WarmBrushProperty); set => SetValue(WarmBrushProperty, value); }
    public Brush GoodBrush { get => (Brush)GetValue(GoodBrushProperty); set => SetValue(GoodBrushProperty, value); }
    /// <summary>Run with a bar's index when it's clicked.</summary>
    public ICommand? PickCommand { get => (ICommand?)GetValue(PickCommandProperty); set => SetValue(PickCommandProperty, value); }

    /// <summary>A part's colour: 0 before Windows (pink), 1 Windows starting (blue), 2 getting ready after sign-in (violet).</summary>
    public Brush PartBrush(int part) => part switch { 0 => BiosBrush, 1 => WindowsBrush, _ => AfterBrush };

    private const double L = 52, R = 10, T = 34, B = 30, ClockSlot = 40, TipW = 340;
    private int _hover = -1;

    private Rect Plot => new(L, T, Math.Max(1, ActualWidth - L - R), Math.Max(1, ActualHeight - T - B));

    private double Slot(int n) => IsClock ? ClockSlot : Plot.Width / Math.Max(n, 1);

    /// <summary>The left edge of bar <paramref name="i"/>'s slot.</summary>
    private double X(IReadOnlyList<StartupBar> bars, int i) => IsClock ? ClockLefts(bars)[i] : L + Slot(bars.Count) * i;

    private double[]? _lefts;
    private IReadOnlyList<StartupBar>? _leftsFor;
    private double _leftsWidth;

    /// <summary>
    /// Day: each bar at its time on the clock, pushed apart where starts minutes apart would overlap (two starts at 3:09 and
    /// 3:21 PM drew one behind the other), and back in from the right edge.
    /// </summary>
    private double[] ClockLefts(IReadOnlyList<StartupBar> bars)
    {
        double w = Plot.Width;
        if (_lefts is not null && ReferenceEquals(_leftsFor, bars) && _leftsWidth == w) return _lefts;
        int n = bars.Count;
        var x = new double[n];
        for (int i = 0; i < n; i++) x[i] = L + (w - ClockSlot) * Math.Clamp(bars[i].Clock, 0, 1);
        for (int i = 1; i < n; i++) x[i] = Math.Max(x[i], x[i - 1] + ClockSlot);
        if (n > 0 && x[n - 1] > L + w - ClockSlot) x[n - 1] = L + w - ClockSlot;
        for (int i = n - 2; i >= 0; i--) x[i] = Math.Min(x[i], x[i + 1] - ClockSlot);
        for (int i = 0; i < n; i++) x[i] = Math.Max(x[i], L + i * ClockSlot);
        (_lefts, _leftsFor, _leftsWidth) = (x, bars, w);
        return x;
    }

    private int At(Point p)
    {
        var bars = Bars;
        if (bars is not { Count: > 0 } || p.Y < T - 26 || p.Y > ActualHeight - B + 2) return -1;
        double slot = Slot(bars.Count);
        int best = -1;
        double bestGap = double.MaxValue;
        for (int i = 0; i < bars.Count; i++)
        {
            double mid = X(bars, i) + slot / 2, gap = Math.Abs(p.X - mid);
            if (gap <= slot / 2 && gap < bestGap) (best, bestGap) = (i, gap);
        }
        return best;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = At(e.GetPosition(this));
        Cursor = i >= 0 && Bars![i] is { Empty: false } && PickCommand is not null ? Cursors.Hand : null;
        if (i == _hover) return;
        _hover = i;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = -1;
        Cursor = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        int i = At(e.GetPosition(this));
        if (i < 0 || Bars![i].Empty || PickCommand is not { } pick || !pick.CanExecute(i)) return;
        pick.Execute(i);
        e.Handled = true;
    }

    protected override HitTestResult HitTestCore(PointHitTestParameters p) => new PointHitTestResult(this, p.HitPoint);

    private double Y(double seconds) => Plot.Bottom - Plot.Height * Math.Clamp(seconds / Math.Max(Top, 1), 0, 1);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var bars = Bars;
        if (bars is null || ActualWidth < L + R + 40 || ActualHeight < T + B + 30) return;
        var plot = Plot;
        double top = Math.Max(Top, 1), slot = Slot(bars.Count), bw = Math.Min(26, slot * 0.62);

        // The scale: a line at each third of the top.
        var grid = new Pen(ChartPaint.Grid, 1);
        grid.Freeze();
        for (int k = 0; k <= 3; k++)
        {
            double v = top * k / 3, y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            ChartPaint.Text(dc, this, k == 0 ? "0" : StartupWords.PartLength(v), new Point(plot.Left - 8, y), 11, ChartPaint.Label, ChartPaint.Align.Right);
        }
        if (IsClock)
            for (int h = 0; h <= 24; h += 6)
            {
                double x = L + (plot.Width - ClockSlot) * h / 24 + ClockSlot / 2;
                ChartPaint.Text(dc, this, TimeText.At(DateTime.Today.AddHours(h), "h tt"), new Point(x, ActualHeight - 9), 11, ChartPaint.Label, ChartPaint.Align.Center);
                var tick = new Pen(ChartPaint.Grid, 1);
                tick.Freeze();
                dc.DrawLine(tick, new Point(Math.Round(x) + 0.5, plot.Bottom), new Point(Math.Round(x) + 0.5, plot.Bottom + 4));
            }

        // The lit column under the pointer, only where a click opens something.
        if (_hover >= 0 && _hover < bars.Count && !bars[_hover].Empty)
            dc.DrawRoundedRectangle(ChartPaint.Track, null, new Rect(X(bars, _hover) + 1, T - 26, slot - 2, plot.Bottom - T + 28), 6, 6);

        // The usual of each kind: a dashed band across the chart.
        if (Bands is { } bands)
        {
            var bandFill = ChartPaint.Brush(ChartPaint.Res("TextColor", 0xFF), 0.07);
            var bandPen = new Pen(ChartPaint.Brush(ChartPaint.Res("FaintColor", 0x6B), 0.8), 1) { DashStyle = new DashStyle([4, 4], 0) };
            bandPen.Freeze();
            foreach (var u in bands)
            {
                double y1 = Y(u.Hi), y2 = Y(u.Lo);
                dc.DrawRectangle(bandFill, bandPen, new Rect(plot.Left, Math.Round(y1) + 0.5, plot.Width, Math.Max(1, Math.Round(y2 - y1))));
            }
        }

        var hatch = Hatch();
        var outline = new Pen(ChartPaint.Label, 1) { DashStyle = new DashStyle([3, 3], 0) };
        outline.Freeze();
        var ring = new Pen(ChartPaint.Brush(ChartPaint.Res("TextColor", 0xFF), 0.6), 1);
        ring.Freeze();
        // Labels thinned by how many there are to show (Year names only each month's first week: thinning by the bars
        // dropped January to August).
        int named = bars.Where((b, i) => b.Axis.Length > 0 && (i == 0 || bars[i - 1].Axis != b.Axis)).Count();
        int every = Math.Max(1, (int)Math.Ceiling(named / 16.0)), label = 0;
        for (int i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            double x = X(bars, i) + (slot - bw) / 2, cx = x + bw / 2, markTop = plot.Bottom;
            if (b.Empty) { }
            else if (b.Missing)
            {
                double y = Y(top * 0.35);
                dc.DrawRoundedRectangle(null, outline, new Rect(x + 0.5, y + 0.5, bw - 1, plot.Bottom - y - 1), 3, 3);
                markTop = y;
            }
            else if (b.Update)
            {
                double h = Math.Min(b.Height, top), y = Y(h);
                dc.DrawRoundedRectangle(hatch, null, new Rect(x, y, bw, plot.Bottom - y), 3, 3);
                markTop = y;
                if (b.Height > top)
                {
                    // Cut with a break: the bar runs past the top, so its own figure says how long it was.
                    var cut = new Pen(ChartPaint.Backdrop, 3);
                    cut.Freeze();
                    dc.DrawLine(cut, new Point(x - 3, y + 14), new Point(x + bw + 3, y + 8));
                    dc.DrawLine(cut, new Point(x - 3, y + 20), new Point(x + bw + 3, y + 14));
                    ChartPaint.Text(dc, this, StartupWords.Length(b.Total ?? b.Height), new Point(cx, y - 8), 10.5, ChartPaint.Muted, ChartPaint.Align.Center);
                    markTop = y - 20;
                }
            }
            else
            {
                var pieces = new List<(Brush, double)>();
                if (b.Bios is double bios) pieces.Add((BiosBrush, Math.Max(0, plot.Bottom - Y(bios))));
                double acc = b.Bios ?? 0;
                if (b.Windows is double win) { pieces.Add((WindowsBrush, Math.Max(0, Y(acc) - Y(acc + win)))); acc += win; }
                if (b.After is double after) { pieces.Add((AfterBrush, Math.Max(0, Y(acc) - Y(acc + after)))); acc += after; }
                StackedBar.Draw(dc, x, bw, plot.Bottom, pieces);
                markTop = Y(acc);
                if (b.Fast)
                {
                    ChartPaint.Text(dc, this, "F", new Point(cx, markTop - 8), 10, ChartPaint.Label, ChartPaint.Align.Center);
                    markTop -= 18;
                }
                // A longer start: an amber dot, its figure only in the hover and the opened start.
                if (b.Longer)
                {
                    dc.DrawEllipse(WarmBrush, null, new Point(cx, markTop - 10), 4, 4);
                    markTop -= 16;
                }
            }
            // The opened one: a ring with room around the bar and anything over it.
            if (i == Selected && !b.Empty)
            {
                double ry = Math.Max(2, Math.Min(markTop, plot.Bottom) - 10);
                dc.DrawRoundedRectangle(null, ring, new Rect(X(bars, i) + 1, ry, slot - 2, plot.Bottom + 6 - ry), 6, 6);
            }
            // A day with several starts is named once, under its first (three "23 Nov" in a row read as three days).
            bool repeat = i > 0 && bars[i - 1].Axis == b.Axis;
            if (!IsClock && b.Axis.Length > 0 && !repeat && label++ % every == 0)
                ChartPaint.Text(dc, this, b.Axis, new Point(cx, ActualHeight - 9), 11, ChartPaint.Label, ChartPaint.Align.Center);
        }

        if (_hover >= 0 && _hover < bars.Count) DrawTip(dc, bars[_hover].Tip, X(bars, _hover), slot);
    }

    // The stripes of a restart after an update.
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
            g.DrawRectangle(ChartPaint.Brush(ChartPaint.Res("FaintColor", 0x6B), 0.45), null, new Rect(0, 0, 3, 6));
        }
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 6, 6), ViewportUnits = BrushMappingMode.Absolute, Transform = new RotateTransform(45) };
        brush.Freeze();
        return _hatch = brush;
    }

    // ── The hover box ──

    private const double Pad = 13, Inner = TipW - 2 * Pad;

    private void DrawTip(DrawingContext dc, StartupTip tip, double slotLeft, double slot)
    {
        var title = Wrap(ChartPaint.Format(this, tip.Title, 13, ChartPaint.TextBrush, true));
        var sub = tip.Sub is null ? null : Wrap(ChartPaint.Format(this, tip.Sub, 12, ChartPaint.Muted));
        var big = ChartPaint.Format(this, tip.Big, 22, ToneBrush(tip.BigTone, ChartPaint.TextBrush), true);
        var word = tip.BigWord.Length == 0 ? null : ChartPaint.Format(this, tip.BigWord, 12, ChartPaint.Muted);
        if (word is not null) word.MaxTextWidth = Math.Max(20, Inner - big.Width - 6);

        // The parts, each a row: its dot, its name, its length (amber when it ran long) and, then, its usual under it.
        var rows = tip.Rows.Select(r => (Row: r, Name: ChartPaint.Format(this, r.Name, 12.5, ChartPaint.TextBrush),
            Value: ChartPaint.Format(this, r.Value, 12.5, r.Warm ? WarmBrush : ChartPaint.TextBrush, true),
            Usual: r.Usual is null ? null : ChartPaint.Format(this, r.Usual, 11.5, ChartPaint.Faint))).ToList();
        double rowsH = rows.Sum(r => 20 + (r.Usual is null ? 0 : r.Usual.Height)) ;

        const double FactPad = 10, ColGap = 10;
        double colW = (Inner - 2 * FactPad - ColGap) / 2;
        var facts = tip.Facts.Select(f => (L: Wrap(ChartPaint.Format(this, f.Label, 10, ChartPaint.Faint, true), colW),
            V: Wrap(ChartPaint.Format(this, f.Value, 12.5, ToneBrush(f.Tone, ChartPaint.TextBrush)), colW))).ToList();
        var factRows = facts.Chunk(2).Select(r => r.Max(c => c.L.Height + 2 + c.V.Height)).ToList();
        double factsH = factRows.Count == 0 ? 0 : factRows.Sum() + (factRows.Count - 1) * 8 + 2 * 9;

        var noteLabel = tip.NoteLabel is null || tip.Items.Count == 0 ? null : ChartPaint.Format(this, tip.NoteLabel, 10, ChartPaint.Faint, true);
        var items = tip.Items.Select(it =>
        {
            var right = it.Right is null ? null : ChartPaint.Format(this, it.Right, 12.5, ChartPaint.TextBrush, true);
            var text = ChartPaint.Format(this, it.Text, 12.5, ChartPaint.TextBrush);
            text.MaxTextWidth = Math.Max(20, Inner - 2 * FactPad - 30 - (right?.Width + 8 ?? 0));
            return (Item: it, Text: text, Right: right);
        }).ToList();
        double noteH = noteLabel is null ? 0 : 9 + noteLabel.Height + 6 + items.Sum(i => Math.Max(22, i.Text.Height) + 6) - 6 + 9;
        var foot = tip.Foot is null ? null : Wrap(ChartPaint.Format(this, tip.Foot, 11.5, ChartPaint.Faint));

        double h = Pad + title.Height + (sub is null ? 0 : 2 + sub.Height) + 8 + big.Height
            + (tip.Split.Count > 0 ? 10 + 6 : 0) + (rows.Count > 0 ? 9 + rowsH : 0) + (factsH > 0 ? 10 + factsH : 0) + (noteH > 0 ? 8 + noteH : 0)
            + (foot is null ? 0 : 10 + 8 + foot.Height) + Pad - 2;

        // Beside the bar's column (the other side near the right edge), its bottom kept within the chart.
        double x = slotLeft + slot + 12;
        if (x + TipW > ActualWidth) x = slotLeft - 12 - TipW;
        if (x < 0) x = 0;
        double y = Math.Min(T - 26, ActualHeight - h);
        var box = new Rect(Math.Round(x) + 0.5, Math.Round(y) + 0.5, TipW, h);
        dc.DrawRoundedRectangle(ChartPaint.TooltipBg, ChartPaint.TooltipBorder, box, 10, 10);

        double left = box.Left + Pad, right2 = box.Right - Pad, ty = box.Top + Pad;
        dc.DrawText(title, new Point(left, ty)); ty += title.Height;
        if (sub is not null) { ty += 2; dc.DrawText(sub, new Point(left, ty)); ty += sub.Height; }
        ty += 8;
        dc.DrawText(big, new Point(left, ty));
        if (word is not null) dc.DrawText(word, new Point(left + big.Width + 6, ty + big.Baseline - word.Baseline));
        ty += big.Height;
        if (tip.Split.Count > 0)
        {
            ty += 10;
            double all = tip.Split.Sum(p => p.Value), bx = left;
            dc.PushClip(new RectangleGeometry(new Rect(left, ty, Inner, 6), 3, 3));
            foreach (var (part, value) in tip.Split)
            {
                double w = all <= 0 ? 0 : Inner * value / all;
                dc.DrawRectangle(PartBrush(part), null, new Rect(bx, ty, Math.Max(0, w - 2), 6));
                bx += w;
            }
            dc.Pop();
            ty += 6;
        }
        if (rows.Count > 0)
        {
            ty += 9;
            foreach (var (row, name, value, usual) in rows)
            {
                double my = ty + 10;
                dc.DrawEllipse(PartBrush(row.Part), null, new Point(left + 4, my), 4, 4);
                dc.DrawText(name, new Point(left + 16, my - name.Height / 2));
                dc.DrawText(value, new Point(right2 - value.Width, my - value.Height / 2));
                ty += 20;
                if (usual is not null) { dc.DrawText(usual, new Point(left + 16, ty - 3)); ty += usual.Height; }
            }
        }
        if (factsH > 0)
        {
            ty += 10;
            dc.DrawRoundedRectangle(ChartPaint.Track, null, new Rect(left, ty, Inner, factsH), 8, 8);
            double ry = ty + 9;
            for (int r = 0; r < factRows.Count; r++)
            {
                for (int c = 0; c < 2 && r * 2 + c < facts.Count; c++)
                {
                    var (l, v) = facts[r * 2 + c];
                    double cx = left + FactPad + c * (colW + ColGap);
                    dc.DrawText(l, new Point(cx, ry));
                    dc.DrawText(v, new Point(cx, ry + l.Height + 2));
                }
                ry += factRows[r] + 8;
            }
            ty += factsH;
        }
        if (noteLabel is not null)
        {
            ty += 8;
            dc.DrawRoundedRectangle(ChartPaint.Track, null, new Rect(left, ty, Inner, noteH), 8, 8);
            double ny = ty + 9, nx = left + FactPad;
            dc.DrawText(noteLabel, new Point(nx, ny));
            ny += noteLabel.Height + 6;
            foreach (var (item, text, right) in items)
            {
                double rowH = Math.Max(22, text.Height), my = ny + rowH / 2;
                DrawIcon(dc, item, new Rect(nx, my - 11, 22, 22));
                dc.DrawText(text, new Point(nx + 30, my - text.Height / 2));
                if (right is not null) dc.DrawText(right, new Point(left + Inner - FactPad - right.Width, my - right.Height / 2));
                ny += rowH + 6;
            }
            ty += noteH;
        }
        if (foot is not null)
        {
            ty += 10;
            dc.DrawLine(ChartPaint.TooltipBorder, new Point(box.Left + 1, Math.Round(ty) + 0.5), new Point(box.Right - 1, Math.Round(ty) + 0.5));
            ty += 8;
            dc.DrawText(foot, new Point(left, ty));
        }
    }

    // An app's own icon, else a glyph in a soft square, else its first letter.
    private void DrawIcon(DrawingContext dc, StartupTipItem item, Rect at)
    {
        if (item.IconPath is { } path && Services.IconCache.Get(path) is { } image)
        {
            dc.DrawImage(image, new Rect(at.X + 3, at.Y + 3, at.Width - 6, at.Height - 6));
            return;
        }
        dc.DrawRoundedRectangle(ChartPaint.Brush(ChartPaint.Res("CpuColor", 0x80), 0.16), null, at, 6, 6);
        if (item.Glyph is { } glyph)
        {
            var icon = new FormattedText(glyph, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 10,
                ChartPaint.CpuBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(icon, new Point(at.X + (at.Width - icon.Width) / 2, at.Y + (at.Height - icon.Height) / 2));
        }
        else ChartPaint.Text(dc, this, item.Letter, new Point(at.X + at.Width / 2, at.Y + at.Height / 2), 10, ChartPaint.CpuBrush, ChartPaint.Align.Center, bold: true);
    }

    private Brush ToneBrush(TipTone tone, Brush plain) => tone switch { TipTone.Warm => WarmBrush, TipTone.Good => GoodBrush, TipTone.Quiet => ChartPaint.Muted, _ => plain };

    private static FormattedText Wrap(FormattedText text, double width = Inner)
    {
        text.MaxTextWidth = Math.Max(1, width);
        return text;
    }
}

/// <summary>
/// The opened start's wide bar (approved mock-up, startHero): its three parts in their colours, against the dashed box of
/// its usual (scaled to 1m 30s, or the start when it's longer); a restart after an update is striped whole.
/// </summary>
public sealed class StartHeroBar : FrameworkElement
{
    public static readonly DependencyProperty StartProperty = DependencyProperty.Register(nameof(Start), typeof(StartupBar), typeof(StartHeroBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UsualProperty = DependencyProperty.Register(nameof(Usual), typeof(Usual), typeof(StartHeroBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BiosBrushProperty = DependencyProperty.Register(nameof(BiosBrush), typeof(Brush), typeof(StartHeroBar), new FrameworkPropertyMetadata(Brushes.Pink, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty WindowsBrushProperty = DependencyProperty.Register(nameof(WindowsBrush), typeof(Brush), typeof(StartHeroBar), new FrameworkPropertyMetadata(Brushes.CornflowerBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AfterBrushProperty = DependencyProperty.Register(nameof(AfterBrush), typeof(Brush), typeof(StartHeroBar), new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));

    public StartHeroBar()
    {
        SetResourceReference(BiosBrushProperty, "PinkBrush");
        SetResourceReference(WindowsBrushProperty, "CpuBrush");
        SetResourceReference(AfterBrushProperty, "PurpleBrush");
        Height = 26;
    }

    public StartupBar? Start { get => (StartupBar?)GetValue(StartProperty); set => SetValue(StartProperty, value); }
    public Usual? Usual { get => (Usual?)GetValue(UsualProperty); set => SetValue(UsualProperty, value); }
    public Brush BiosBrush { get => (Brush)GetValue(BiosBrushProperty); set => SetValue(BiosBrushProperty, value); }
    public Brush WindowsBrush { get => (Brush)GetValue(WindowsBrushProperty); set => SetValue(WindowsBrushProperty, value); }
    public Brush AfterBrush { get => (Brush)GetValue(AfterBrushProperty); set => SetValue(AfterBrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        if (Start is not { } s || ActualWidth < 20) return;
        double w = ActualWidth, top = 5, h = 16;
        double max = Math.Max(Math.Max(90, s.Height), Usual?.Hi ?? 0);
        if (s.Update)
        {
            var stripes = new DrawingBrush(new GeometryDrawing(ChartPaint.Brush(ChartPaint.Res("FaintColor", 0x6B), 0.45), null, new RectangleGeometry(new Rect(0, 0, 3, 6))))
            { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 6, 6), ViewportUnits = BrushMappingMode.Absolute, Transform = new RotateTransform(45) };
            stripes.Freeze();
            dc.DrawRoundedRectangle(ChartPaint.Track, null, new Rect(0, top, w, h), 4, 4);
            dc.DrawRoundedRectangle(stripes, null, new Rect(0, top, w, h), 4, 4);
            return;
        }
        if (s.Missing)
        {
            var dashed = new Pen(ChartPaint.Label, 1) { DashStyle = new DashStyle([3, 3], 0) };
            dashed.Freeze();
            dc.DrawRoundedRectangle(null, dashed, new Rect(0.5, top + 0.5, w - 1, h - 1), 4, 4);
            return;
        }
        double x = 0;
        foreach (var (value, brush) in new[] { (s.Bios, BiosBrush), (s.Windows, WindowsBrush), (s.After, AfterBrush) })
        {
            if (value is not double v || v <= 0) continue;
            double pw = w * v / max;
            dc.DrawRoundedRectangle(brush, null, new Rect(x, top, Math.Max(0, pw - 3), h), 4, 4);
            x += pw;
        }
        if (Usual is { } u)
        {
            var pen = new Pen(ChartPaint.Brush(ChartPaint.Res("MutedColor", 0x8A)), 1) { DashStyle = new DashStyle([3, 3], 0) };
            pen.Freeze();
            dc.DrawRoundedRectangle(null, pen, new Rect(Math.Round(w * u.Lo / max) + 0.5, 0.5, Math.Max(4, Math.Round(w * (u.Hi - u.Lo) / max)), h + 2 * top - 1), 5, 5);
        }
    }
}
