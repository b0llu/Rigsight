using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Rigsight.Core.Reports;

namespace Rigsight.Controls;

/// <summary>A hover box's content: a heading, the level (a swatch and its name), label and value rows, and a line under them.</summary>
public sealed record FanStripTip(string Title, FanLevel? Level, IReadOnlyList<(string Label, string Value)> Rows, string? Note = null, string? Sub = null);

/// <summary>A bar of a week, a month (a day) or a year (a month, as its average day): minutes at each level, and its hover.</summary>
public sealed record FanStripBar(DateTime Day, int[] Levels, string Label, bool Current, FanStripTip Tip);

/// <summary>A stretch a fact marks on the strip: minutes from the strip's start (a day), or bars (a period); the ticks dim outside it when <paramref name="Ticks"/>.</summary>
public sealed record FanStripMark(double From, double To, string? Label, bool Ticks = false);

/// <summary>What the strip draws (see <see cref="FanStrip"/>).</summary>
public sealed class FanStripData
{
    /// <summary>A day: minute by minute from <see cref="From"/>; otherwise <see cref="Bars"/>.</summary>
    public bool IsDay { get; init; }
    public DateTime From { get; init; }
    /// <summary>Each minute's level (null: not recorded).</summary>
    public FanLevel?[] Levels { get; init; } = [];
    /// <summary>The minutes the fans started up (or sped up), from <see cref="From"/>.</summary>
    public IReadOnlyList<int> Ticks { get; init; } = [];
    /// <summary>The ticks in the GPU's colour (a card's fans), else the CPU's (a laptop's fan, the fans speeding up).</summary>
    public bool GpuTicks { get; init; }
    /// <summary>The hours labelled under the strip, every this many.</summary>
    public int HourStep { get; init; } = 1;
    public Func<int, FanStripTip>? TipAt { get; init; }
    public IReadOnlyList<FanStripBar> Bars { get; init; } = [];
    /// <summary>What each fact marks, by the fact's id.</summary>
    public IReadOnlyDictionary<string, FanStripMark> Marks { get; init; } = new Dictionary<string, FanStripMark>();
}

/// <summary>
/// The Fans page's strip of the day (approved mock-up 2026-11-14): each minute Silent, Quiet, Busy or Loud, taller and
/// brighter the louder (Sky at rising strength; never a reading's green, amber or red, since the levels aren't good or
/// bad), the grey line alone where nothing was recorded, and a mark above for each time the fans started up. A week, a
/// month or a year: a bar a day (a month) of the four levels. Hover for the minute or bar; a fact hovered under it marks
/// its stretch (<see cref="Mark"/>).
/// </summary>
public sealed class FanStrip : FrameworkElement
{
    /// <summary>The four levels' strength and height (share of the strip), Silent to Loud.</summary>
    internal static readonly double[] Strength = [0.32, 0.55, 0.8, 1], Tall = [0.28, 0.5, 0.74, 1];

    private const double TickTop = 2, TickH = 10, BarTop = 20, Base = 78, LabelY = 98;

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(FanStripData), typeof(FanStrip), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((FanStrip)d).HideTip()));
    public static readonly DependencyProperty MarkProperty = DependencyProperty.Register(
        nameof(Mark), typeof(string), typeof(FanStrip), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SkyProperty = DependencyProperty.Register(nameof(Sky), typeof(Brush), typeof(FanStrip), new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GpuProperty = DependencyProperty.Register(nameof(Gpu), typeof(Brush), typeof(FanStrip), new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CpuProperty = DependencyProperty.Register(nameof(Cpu), typeof(Brush), typeof(FanStrip), new FrameworkPropertyMetadata(Brushes.RoyalBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LineProperty = DependencyProperty.Register(nameof(Line), typeof(Brush), typeof(FanStrip), new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(FanStrip), new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(Brush), typeof(FanStrip), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(Brush), typeof(FanStrip), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public FanStripData? Data { get => (FanStripData?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    /// <summary>The id of the fact hovered under the strip (see <see cref="FanStripData.Marks"/>), or null.</summary>
    public string? Mark { get => (string?)GetValue(MarkProperty); set => SetValue(MarkProperty, value); }
    public Brush Sky { get => (Brush)GetValue(SkyProperty); set => SetValue(SkyProperty, value); }
    public Brush Gpu { get => (Brush)GetValue(GpuProperty); set => SetValue(GpuProperty, value); }
    public Brush Cpu { get => (Brush)GetValue(CpuProperty); set => SetValue(CpuProperty, value); }
    public Brush Line { get => (Brush)GetValue(LineProperty); set => SetValue(LineProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public Brush Label { get => (Brush)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public Brush Text { get => (Brush)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    // Like every chart, it answers the pointer: a box beside it (a window of its own, so it can be taller than the strip).
    private readonly ToolTip _tip = new() { Placement = PlacementMode.Relative, IsHitTestVisible = false };
    private int _hover = -1;

    public FanStrip()
    {
        SetResourceReference(SkyProperty, "SkyBrush");
        SetResourceReference(GpuProperty, "GpuBrush");
        SetResourceReference(CpuProperty, "CpuBrush");
        SetResourceReference(LineProperty, "StrokeBrush");
        SetResourceReference(TrackProperty, "HoverBrush");
        SetResourceReference(LabelProperty, "FaintBrush");
        SetResourceReference(TextProperty, "TextBrush");
        // A page switched away under a resting pointer: the box (a window of its own) mustn't stay open behind it.
        Unloaded += (_, _) => HideTip();
        IsVisibleChanged += (_, _) => { if (!IsVisible) HideTip(); };
    }

    private void HideTip()
    {
        _hover = -1;
        _tip.IsOpen = false;
        InvalidateVisual();
    }

    /// <summary>The minute (a day) or bar under <paramref name="x"/>, or -1.</summary>
    internal int IndexAt(double x)
    {
        if (Data is not { } d || ActualWidth <= 0) return -1;
        int n = d.IsDay ? d.Levels.Length : d.Bars.Count;
        if (n == 0) return -1;
        return Math.Clamp((int)Math.Floor(x / ActualWidth * n), 0, n - 1);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        int i = IndexAt(p.X);
        if (i < 0 || Data is not { } d) return;
        if (i != _hover)
        {
            _hover = i;
            var tip = d.IsDay ? d.TipAt?.Invoke(i) : d.Bars[i].Tip;
            _tip.Content = tip is null ? null : Box(tip);
            InvalidateVisual();
        }
        _tip.PlacementTarget = this;
        _tip.HorizontalOffset = p.X + 14;
        _tip.VerticalOffset = p.Y + 14;
        _tip.IsOpen = IsLoaded && _tip.Content is not null;
    }

    protected override void OnMouseLeave(MouseEventArgs e) => HideTip();

    protected override HitTestResult HitTestCore(PointHitTestParameters p) => new PointHitTestResult(this, p.HitPoint);

    private Brush Level(FanLevel level) => Faded(Sky, Strength[(int)level]);

    private static Brush Faded(Brush b, double opacity)
    {
        var copy = b.Clone();
        copy.Opacity = opacity;
        copy.Freeze();
        return copy;
    }

    /// <summary>The hover box's content, in the app's tooltip: heading, level, rows, then the line under them.</summary>
    private FrameworkElement Box(FanStripTip tip)
    {
        var panel = new StackPanel { MinWidth = 170, MaxWidth = 280 };
        panel.Children.Add(new TextBlock { Text = tip.Title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        if (tip.Sub is { } sub) panel.Children.Add(new TextBlock { Text = sub, Foreground = Muted(), Margin = new Thickness(0, 0, 0, 4) });
        if (tip.Level is FanLevel level)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            row.Children.Add(new Rectangle { Width = 9, Height = 9, RadiusX = 2, RadiusY = 2, Fill = Level(level), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            row.Children.Add(new TextBlock { Text = LevelName(level) });
            panel.Children.Add(row);
        }
        foreach (var (label, value) in tip.Rows)
        {
            var grid = new DockPanel { Margin = new Thickness(0, 1, 0, 1), LastChildFill = true };
            var v = new TextBlock { Text = value, FontWeight = FontWeights.SemiBold, Margin = new Thickness(14, 0, 0, 0) };
            DockPanel.SetDock(v, Dock.Right);
            grid.Children.Add(v);
            grid.Children.Add(new TextBlock { Text = label, Foreground = Muted(), TextTrimming = TextTrimming.CharacterEllipsis });
            panel.Children.Add(grid);
        }
        if (tip.Note is { } note) panel.Children.Add(new TextBlock { Text = note, Foreground = Muted(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        return panel;
    }

    private Brush Muted() => TryFindResource("MutedBrush") as Brush ?? Brushes.Gray;

    /// <summary>A level's name, as the legend and the hover say it.</summary>
    public static string LevelName(FanLevel level) => level switch
    {
        FanLevel.Silent => Core.Lang.T("Silent"),
        FanLevel.Quiet => Core.Lang.T("Quiet"),
        FanLevel.Busy => Core.Lang.T("Busy"),
        _ => Core.Lang.T("Loud"),
    };

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (Data is not { } d || w < 20) return;
        if (d.IsDay) DrawDay(dc, d, w);
        else DrawBars(dc, d, w);
    }

    private void DrawDay(DrawingContext dc, FanStripData d, double w)
    {
        int span = d.Levels.Length;
        if (span == 0) return;
        double X(double m) => w * m / span;
        var gridPen = new Pen(Line, 1);
        gridPen.Freeze();
        int hours = span / 60;
        for (int hh = 0; hh <= hours; hh += d.HourStep)
        {
            double x = Math.Round(X(hh * 60)) + 0.5;
            dc.DrawLine(gridPen, new Point(x, BarTop - 4), new Point(x, Base));
        }
        dc.DrawRoundedRectangle(Track, null, new Rect(0, Base - 2, w, 2), 1, 1);

        // A minute's bar, a hair wider than its slot so neighbours join; one geometry a level.
        double bw = Math.Max(0.9, w / span + 0.35), full = Base - BarTop;
        for (int lv = 0; lv < 4; lv++)
        {
            var g = new StreamGeometry();
            bool any = false;
            using (var ctx = g.Open())
                for (int m = 0; m < span; m++)
                {
                    if (d.Levels[m] is not FanLevel l || (int)l != lv) continue;
                    double x = X(m), bh = full * Tall[lv];
                    ctx.BeginFigure(new Point(x, Base), true, true);
                    ctx.LineTo(new Point(x + bw, Base), false, false);
                    ctx.LineTo(new Point(x + bw, Base - bh), false, false);
                    ctx.LineTo(new Point(x, Base - bh), false, false);
                    any = true;
                }
            if (!any) continue;
            g.Freeze();
            dc.DrawGeometry(Level((FanLevel)lv), null, g);
        }

        // The marks above: each start, dimmed outside a hovered fact's stretch.
        FanStripMark? mark = Mark is { } id && d.Marks.TryGetValue(id, out var mk) ? mk : null;
        var tick = d.GpuTicks ? Gpu : Cpu;
        var dim = Faded(tick, 0.25);
        foreach (int m in d.Ticks)
        {
            bool lit = mark is not { Ticks: true } || (m >= mark.From && m < mark.To);
            dc.DrawRoundedRectangle(lit ? tick : dim, null, new Rect(X(m) - 0.75, TickTop, 1.5, TickH), 0.75, 0.75);
        }

        // A marked stretch's name sits on the hours' line, under it; the hours it would cover step aside while it shows.
        Rect? named = null;
        if (mark is not null && mark.To > mark.From)
            named = DrawMark(dc, X(mark.From), Math.Max(X(mark.To), X(mark.From) + 3), BarTop - 4, Base, mark.Label, w);
        for (int hh = 0; hh <= hours; hh += d.HourStep)
        {
            var align = hh == 0 ? ChartPaint.Align.Left : hh * 60 >= span ? ChartPaint.Align.Right : ChartPaint.Align.Center;
            string label = Core.TimeText.At(d.From.AddHours(hh), "h tt");
            double lw = ChartPaint.Width(this, label, 10.5, Label), x = X(hh * 60);
            double left = align == ChartPaint.Align.Left ? x : align == ChartPaint.Align.Right ? x - lw : x - lw / 2;
            if (named is { } r && left < r.Right + 6 && left + lw > r.Left - 6) continue;
            Axis(dc, label, x, align);
        }

        if (_hover >= 0 && _hover < span)
        {
            double x = Math.Round(X(_hover + 0.5)) + 0.5;
            dc.DrawLine(new Pen(Faded(Text, 0.5), 1), new Point(x, 0), new Point(x, Base));
        }
    }

    private void DrawBars(DrawingContext dc, FanStripData d, double w)
    {
        var bars = d.Bars;
        int n = bars.Count;
        if (n == 0) return;
        const double bottom = 86, room = 78;
        double gap = n > 12 ? 3 : 8, bw = (w - gap * (n - 1)) / n;
        double X(int i) => i * (bw + gap);
        var basePen = new Pen(Track, 1);
        basePen.Freeze();
        dc.DrawLine(basePen, new Point(0, bottom + 0.5), new Point(w, bottom + 0.5));
        double max = Math.Max(1, bars.Max(b => b.Levels.Sum()));
        for (int i = 0; i < n; i++)
        {
            var b = bars[i];
            if (i == _hover) dc.DrawRoundedRectangle(Faded(Text, 0.07), null, new Rect(X(i), 0, bw, bottom), 3, 3);
            // Loud at the bottom, as the mock-up stacks them, Silent on top.
            var pieces = new List<(Brush, double)>();
            for (int k = 3; k >= 0; k--)
                if (b.Levels[k] > 0) pieces.Add((Level((FanLevel)k), b.Levels[k] / max * room));
            StackedBar.Draw(dc, X(i), bw, bottom, pieces);
            if (b.Label.Length > 0) Axis(dc, b.Label, X(i) + bw / 2, ChartPaint.Align.Center, 104, b.Current);
        }
        if (Mark is { } id && d.Marks.TryGetValue(id, out var mark) && mark.From >= 0 && mark.From < n)
        {
            int i = (int)mark.From;
            var pen = new Pen(Faded(Text, 0.6), 1);
            pen.Freeze();
            dc.DrawRoundedRectangle(null, pen, new Rect(X(i) - 2, 2, bw + 4, bottom - 1), 3, 3);
        }
    }

    /// <summary>A label under the strip: an hour, a day, a month (the current one in the text's colour).</summary>
    private void Axis(DrawingContext dc, string text, double x, ChartPaint.Align align, double y = LabelY, bool current = false) =>
        ChartPaint.Text(dc, this, text, new Point(x, y), 10.5, current ? Text : Label, align);

    /// <summary>A fact's stretch: a light box over it, and its name on the hours' line under it; returns where the name went.</summary>
    private Rect? DrawMark(DrawingContext dc, double x1, double x2, double top, double bottom, string? label, double w)
    {
        var pen = new Pen(Faded(Text, 0.55), 1);
        pen.Freeze();
        dc.DrawRoundedRectangle(Faded(Text, 0.08), pen, new Rect(x1, top, x2 - x1, bottom - top), 3, 3);
        if (string.IsNullOrEmpty(label)) return null;
        double width = ChartPaint.Width(this, label, 11, Text);
        double cx = Math.Clamp((x1 + x2) / 2, width / 2, w - width / 2);
        ChartPaint.Text(dc, this, label, new Point(cx, LabelY), 11, Text, ChartPaint.Align.Center);
        return new Rect(cx - width / 2, LabelY - 8, width, 16);
    }
}
