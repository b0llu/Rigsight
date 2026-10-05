using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core;

namespace Rigsight.Controls;

/// <summary>
/// What the fan charts share: theme brushes (as the other charts take them), a dashed rpm grid from 0, labels, and a
/// hover guide with a box of what's under the pointer.
/// </summary>
public abstract class FanChartBase : FrameworkElement
{
    protected const double AxisWidth = 44, AxisHeight = 22, Top = 8;

    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush), typeof(Brush), typeof(FanChartBase), new FrameworkPropertyMetadata(Brushes.SeaGreen, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(FanChartBase), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(FanChartBase), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MutedBrushProperty = DependencyProperty.Register(
        nameof(MutedBrush), typeof(Brush), typeof(FanChartBase), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(FanChartBase), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    protected FanChartBase()
    {
        SetResourceReference(GridBrushProperty, "StrokeBrush");
        SetResourceReference(LabelBrushProperty, "FaintBrush");
        SetResourceReference(MutedBrushProperty, "MutedBrush");
        SetResourceReference(LineBrushProperty, "TextBrush");
    }

    /// <summary>The fan's colour (its dots and bars).</summary>
    public Brush Brush { get => (Brush)GetValue(BrushProperty); set => SetValue(BrushProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public Brush MutedBrush { get => (Brush)GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }

    private static readonly Typeface Font = new("Segoe UI");

    /// <summary>A see-through copy of a brush (frozen: charts redraw often).</summary>
    protected static Brush Faded(Brush brush, double opacity)
    {
        var copy = brush.Clone();
        copy.Opacity = opacity;
        copy.Freeze();
        return copy;
    }

    protected FormattedText Text(string text, Brush brush, double size = 11) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected void Label(DrawingContext dc, string text, Point at, Brush brush, bool right = false, bool center = false)
    {
        var ft = Text(text, brush);
        dc.DrawText(ft, new Point(right ? at.X - ft.Width : center ? at.X - ft.Width / 2 : at.X, at.Y - ft.Height / 2));
    }

    /// <summary>A round top for the rpm axis (four steps of 250, 500, 750…), from 0.</summary>
    protected static double RpmTop(double max) =>
        new[] { 250.0, 500, 750, 1000, 1250, 1500, 2000, 2500 }.FirstOrDefault(s => s * 4 >= max * 1.05, 5000) * 4;

    // ── Hover ──

    /// <summary>Where the pointer is over the chart (null: not over it).</summary>
    protected double? HoverX { get; private set; }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        HoverX = e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        HoverX = null;
        InvalidateVisual();
    }

    // The whole chart takes the pointer, not only its marks.
    protected override HitTestResult HitTestCore(PointHitTestParameters p) => new PointHitTestResult(this, p.HitPoint);

    private static Pen? _guide;

    /// <summary>A guide line at <paramref name="x"/> and, beside it (flipping near the right edge), a box: a title and lines.</summary>
    protected void Hover(DrawingContext dc, Rect plot, double x, string title, IReadOnlyList<string> lines)
    {
        if (_guide?.Brush != ChartPaint.Cursor)
        {
            _guide = new Pen(ChartPaint.Cursor, 1);
            _guide.Freeze();
        }
        dc.DrawLine(_guide, new Point(Math.Round(x) + 0.5, plot.Top), new Point(Math.Round(x) + 0.5, plot.Bottom));
        var head = Text(title, ChartPaint.TextBrush, 12);
        var rows = lines.Select(l => Text(l, ChartPaint.Muted, 12)).ToList();
        const double pad = 10, lineH = 18;
        double w = Math.Max(head.Width, rows.Select(r => r.Width).DefaultIfEmpty(0).Max()) + pad * 2;
        double h = pad * 2 + head.Height + 2 + rows.Count * lineH;
        double left = x + 14 + w > plot.Right ? x - 14 - w : x + 14;
        var box = new Rect(Math.Max(plot.Left, left), plot.Top + 4, w, h);
        dc.DrawRoundedRectangle(ChartPaint.TooltipBg, ChartPaint.TooltipBorder, box, 8, 8);
        dc.DrawText(head, new Point(box.Left + pad, box.Top + pad));
        double y = box.Top + pad + head.Height + 2;
        foreach (var r in rows)
        {
            dc.DrawText(r, new Point(box.Left + pad, y + (lineH - r.Height) / 2));
            y += lineH;
        }
    }

    /// <summary>The plot area, with the rpm grid and its labels drawn.</summary>
    protected Rect Plot(DrawingContext dc, double top)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var plot = new Rect(AxisWidth, Top, Math.Max(1, ActualWidth - AxisWidth - 6), Math.Max(1, ActualHeight - AxisHeight - Top));
        var pen = new Pen(GridBrush, 1) { DashStyle = new DashStyle([3, 4], 0) };
        pen.Freeze();
        for (int i = 0; i <= 4; i++)
        {
            double y = Math.Round(plot.Bottom - plot.Height * i / 4) + 0.5;
            dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
            Label(dc, (top * i / 4).ToString("N0", CultureInfo.CurrentCulture), new Point(plot.Left - 8, y), LabelBrush, right: true);
        }
        return plot;
    }
}

/// <summary>A few minutes at much the same temperature and speed, drawn as one dot (stronger the more minutes).</summary>
public sealed record FanDot(double Temp, double Rpm, int Minutes);

/// <summary>A fan's minutes at one degree: its typical speed while turning (the median), and how often it stood still there.</summary>
public sealed record FanTempBin(int Temp, double Rpm, int Minutes, double StillShare);

/// <summary>
/// A fan's speed at each temperature of the chip it follows: its minutes as dots (the last days), its usual curve under
/// load as a line, and the range where it stands still (a card's zero-rpm mode) shaded. Hover a temperature for the
/// typical speed there and how many minutes say so. A fan on a curve shows a rising band; one at a set speed, a flat one.
/// </summary>
public sealed class FanCurveChart : FanChartBase
{
    public static readonly DependencyProperty DotsProperty = DependencyProperty.Register(
        nameof(Dots), typeof(IReadOnlyList<FanDot>), typeof(FanCurveChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BinsProperty = DependencyProperty.Register(
        nameof(Bins), typeof(IReadOnlyList<FanTempBin>), typeof(FanCurveChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LineProperty = DependencyProperty.Register(
        nameof(Line), typeof(IReadOnlyList<Point>), typeof(FanCurveChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LineLabelProperty = DependencyProperty.Register(
        nameof(LineLabel), typeof(string), typeof(FanCurveChart), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SilentBelowProperty = DependencyProperty.Register(
        nameof(SilentBelow), typeof(double?), typeof(FanCurveChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The minutes, gathered into dots (temperature in °C, rpm).</summary>
    public IReadOnlyList<FanDot>? Dots { get => (IReadOnlyList<FanDot>?)GetValue(DotsProperty); set => SetValue(DotsProperty, value); }
    /// <summary>Each degree's typical speed, for the hover.</summary>
    public IReadOnlyList<FanTempBin>? Bins { get => (IReadOnlyList<FanTempBin>?)GetValue(BinsProperty); set => SetValue(BinsProperty, value); }
    /// <summary>The usual speed at each temperature, as a line.</summary>
    public IReadOnlyList<Point>? Line { get => (IReadOnlyList<Point>?)GetValue(LineProperty); set => SetValue(LineProperty, value); }
    public string LineLabel { get => (string)GetValue(LineLabelProperty); set => SetValue(LineLabelProperty, value); }
    /// <summary>Stands still below this temperature (°C); null when it never does.</summary>
    public double? SilentBelow { get => (double?)GetValue(SilentBelowProperty); set => SetValue(SilentBelowProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < AxisWidth + 40 || ActualHeight < AxisHeight + 40) return;
        var dots = Dots ?? [];
        var line = Line ?? [];
        if (dots.Count == 0 && line.Count == 0)
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
            Label(dc, "Collecting data…", new Point(ActualWidth / 2, ActualHeight / 2), LabelBrush, center: true);
            return;
        }
        var temps = dots.Select(d => d.Temp).Concat(line.Select(p => p.X)).ToList();
        double lo = Math.Floor((temps.Min() - 1) / 5) * 5, hi = Math.Ceiling((temps.Max() + 1) / 5) * 5;
        if (hi - lo < 15) hi = lo + 15;
        double top = RpmTop(dots.Select(d => d.Rpm).Concat(line.Select(p => p.Y)).Max());
        var plot = Plot(dc, top);
        double X(double t) => plot.Left + plot.Width * (t - lo) / (hi - lo);
        double Y(double r) => plot.Bottom - plot.Height * Math.Clamp(r, 0, top) / top;

        if (SilentBelow is double silent && silent > lo)
        {
            var zone = new Rect(plot.Left, plot.Top, Math.Max(0, Math.Min(X(silent), plot.Right) - plot.Left), plot.Height);
            dc.DrawRectangle(Faded(Brush, 0.08), null, zone);
            Label(dc, $"silent below ~{Units.TempShort(silent)}", new Point(zone.Left + zone.Width / 2, plot.Top + 10), Brush, center: true);
        }
        for (double t = Math.Ceiling(lo / 10) * 10; t <= hi; t += 10)
            Label(dc, Units.TempShort(t), new Point(X(t), plot.Bottom + 12), LabelBrush, center: true);

        // Dots: stronger where more minutes fell (four shades, made once per render).
        int most = dots.Count == 0 ? 1 : dots.Max(d => d.Minutes);
        var shades = Enumerable.Range(1, 4).Select(i => Faded(Brush, 0.2 + 0.2 * i)).ToArray();
        foreach (var d in dots)
        {
            int shade = Math.Clamp((int)Math.Ceiling(4 * Math.Sqrt(d.Minutes / (double)most)) - 1, 0, 3);
            dc.DrawEllipse(shades[shade], null, new Point(X(d.Temp), Y(d.Rpm)), 2.6, 2.6);
        }

        if (line.Count >= 2)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(X(line[0].X), Y(line[0].Y)), false, false);
                g.PolyLineTo([.. line.Skip(1).Select(p => new Point(X(p.X), Y(p.Y)))], true, true);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(Faded(LineBrush, 0.7), 1.5), geometry);
            var end = line[^1];
            var ft = Text(LineLabel, MutedBrush);
            dc.DrawText(ft, new Point(Math.Clamp(X(end.X) - ft.Width, plot.Left, Math.Max(plot.Left, plot.Right - ft.Width)), Math.Max(plot.Top, Y(end.Y) - ft.Height - 6)));
        }

        // Hover: the nearest degree with minutes.
        if (HoverX is double hx && hx >= plot.Left && hx <= plot.Right && Bins is { Count: > 0 } bins)
        {
            double t = lo + (hx - plot.Left) / plot.Width * (hi - lo);
            var bin = bins.MinBy(b => Math.Abs(b.Temp + 0.5 - t))!;
            if (Math.Abs(bin.Temp + 0.5 - t) <= 2)
            {
                var lines = new List<string> { bin.StillShare >= 0.5 ? "Usually standing still" : $"Typically {Units.Format(SensorKind.Fan, bin.Rpm)}" };
                if (bin.StillShare is > 0.05 and < 0.5) lines.Add($"Still in {bin.StillShare:P0} of them");
                lines.Add($"{bin.Minutes:N0} minute{(bin.Minutes == 1 ? "" : "s")}");
                double x = X(bin.Temp + 0.5);
                if (bin.StillShare < 0.5) dc.DrawEllipse(LineBrush, new Pen(ChartPaint.TooltipBg, 2), new Point(x, Y(bin.Rpm)), 4, 4);
                Hover(dc, plot, x, $"At {Units.TempShort(bin.Temp)}", lines);
            }
        }
    }
}

/// <summary>One day on the 30-day chart: its average speed and its fastest (null bars: nothing recorded that day).
/// A day under an earlier speed setting is drawn faint (the page's figures are of the setting since).</summary>
public sealed record FanDayBar(DateTime Day, double? Average, double? Fastest, bool Earlier = false);

/// <summary>A fan's last 30 days: a bar per day, its average over the fastest it reached, empty days a tick. Hover a day for its numbers.</summary>
public sealed class FanDaysChart : FanChartBase
{
    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(
        nameof(Days), typeof(IReadOnlyList<FanDayBar>), typeof(FanDaysChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GhostBrushProperty = DependencyProperty.Register(
        nameof(GhostBrush), typeof(Brush), typeof(FanDaysChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>A bar a month (the Day of each bar is its month's first): labelled and hovered as months.</summary>
    public static readonly DependencyProperty MonthlyProperty = DependencyProperty.Register(
        nameof(Monthly), typeof(bool), typeof(FanDaysChart), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>What the bar's value is called in the hover ("Average", or "At 70°" for a trend at one temperature).</summary>
    public static readonly DependencyProperty ValueLabelProperty = DependencyProperty.Register(
        nameof(ValueLabel), typeof(string), typeof(FanDaysChart), new FrameworkPropertyMetadata("Average", FrameworkPropertyMetadataOptions.AffectsRender));

    public FanDaysChart() => SetResourceReference(GhostBrushProperty, "HoverBrush");

    public bool Monthly { get => (bool)GetValue(MonthlyProperty); set => SetValue(MonthlyProperty, value); }
    public string ValueLabel { get => (string)GetValue(ValueLabelProperty); set => SetValue(ValueLabelProperty, value); }
    public IReadOnlyList<FanDayBar>? Days { get => (IReadOnlyList<FanDayBar>?)GetValue(DaysProperty); set => SetValue(DaysProperty, value); }
    public Brush GhostBrush { get => (Brush)GetValue(GhostBrushProperty); set => SetValue(GhostBrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var days = Days ?? [];
        if (days.Count == 0 || ActualWidth < AxisWidth + 40 || ActualHeight < AxisHeight + 30) return;
        double top = RpmTop(days.Max(d => d.Fastest ?? d.Average ?? 0));
        var plot = Plot(dc, top);
        double slot = plot.Width / days.Count, bar = Math.Max(2, slot * 0.6);
        double Y(double r) => plot.Bottom - plot.Height * Math.Clamp(r, 0, top) / top;
        int? hovered = HoverX is double hx && hx >= plot.Left && hx < plot.Right ? (int)((hx - plot.Left) / slot) : null;
        for (int i = 0; i < days.Count; i++)
        {
            double x = plot.Left + slot * i + (slot - bar) / 2;
            var d = days[i];
            if (i == hovered) dc.DrawRoundedRectangle(Faded(GhostBrush, 0.5), null, new Rect(plot.Left + slot * i, plot.Top, slot, plot.Height), 4, 4);
            if (d.Average is null && d.Fastest is null)
            {
                dc.DrawRectangle(GridBrush, null, new Rect(x, plot.Bottom - 2, bar, 2));
                continue;
            }
            if (d.Fastest is double f) dc.DrawRoundedRectangle(GhostBrush, null, new Rect(x, Y(f), bar, plot.Bottom - Y(f)), 3, 3);
            if (d.Average is double a) dc.DrawRoundedRectangle(d.Earlier ? Faded(Brush, 0.35) : Brush, null, new Rect(x, Y(a), bar, plot.Bottom - Y(a)), 3, 3);
        }
        var labels = Monthly ? Enumerable.Range(0, days.Count).Where(i => days.Count <= 12 || i % 2 == 0) : new[] { 0, days.Count / 2, days.Count - 1 }.Distinct();
        foreach (int i in labels)
            Label(dc, days[i].Day.ToString(Monthly ? "MMM" : "d MMM", CultureInfo.CurrentCulture), new Point(plot.Left + slot * (i + 0.5), plot.Bottom + 12), LabelBrush, center: true);

        if (hovered is int h && h < days.Count)
        {
            var d = days[h];
            var lines = new List<string>();
            if (d.Average is null && d.Fastest is null) lines.Add("Nothing recorded");
            if (d.Average is not null) lines.Add($"{ValueLabel} {Units.Format(SensorKind.Fan, d.Average)}");
            if (d.Fastest is not null) lines.Add($"Fastest {Units.Format(SensorKind.Fan, d.Fastest)}");
            if (d.Earlier) lines.Add("Before its setting changed");
            Hover(dc, plot, plot.Left + slot * (h + 0.5), d.Day.ToString(Monthly ? "MMMM yyyy" : "ddd d MMM", CultureInfo.CurrentCulture), lines);
        }
    }
}
