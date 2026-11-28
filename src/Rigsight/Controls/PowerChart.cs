using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// One slot of the Power chart: an hour (a day's lines, in watts), a day or a month (bars, in kWh). Null figures were not
/// recorded (the PC off), or the part gives no power reading: left out, never drawn as 0.
/// </summary>
/// <param name="Axis">Its label under the axis, or empty where the axis skips it.</param>
/// <param name="Title">The hover box's title ("Sat 21 Nov", "10 AM").</param>
/// <param name="Lines">The hover box's lines, worked out by the page.</param>
/// <param name="Rich">Since 1.22.0, a day's rich hover (see <see cref="RichHover"/>), made when it's first shown so the apps' icons are in; null: the lines.</param>
public sealed record PowerSlot(string Axis, string Title, double? Cpu, double? Gpu, IReadOnlyList<string> Lines, Func<RichTip>? Rich = null);

/// <summary>
/// The Power page's chart (1.18.0, approved mock-up 2026-11-23). A day is two lines, the CPU's and the GPU's average watts
/// each hour (not stacked: each can be read on its own); a week or a month is a bar a day, a year a bar a month, each the
/// kWh of the CPU under the GPU's. Hover: a guide and a box of the slot's figures and the app most of it went with.
/// </summary>
public sealed class PowerChart : FanChartBase
{
    public static readonly DependencyProperty SlotsProperty = DependencyProperty.Register(
        nameof(Slots), typeof(IReadOnlyList<PowerSlot>), typeof(PowerChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsLinesProperty = DependencyProperty.Register(
        nameof(IsLines), typeof(bool), typeof(PowerChart), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CpuBrushProperty = DependencyProperty.Register(
        nameof(CpuBrush), typeof(Brush), typeof(PowerChart), new FrameworkPropertyMetadata(Brushes.CornflowerBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GpuBrushProperty = DependencyProperty.Register(
        nameof(GpuBrush), typeof(Brush), typeof(PowerChart), new FrameworkPropertyMetadata(Brushes.DarkTurquoise, FrameworkPropertyMetadataOptions.AffectsRender));

    public PowerChart()
    {
        SetResourceReference(CpuBrushProperty, "CpuBrush");
        SetResourceReference(GpuBrushProperty, "GpuBrush");
    }

    public IReadOnlyList<PowerSlot>? Slots { get => (IReadOnlyList<PowerSlot>?)GetValue(SlotsProperty); set => SetValue(SlotsProperty, value); }
    /// <summary>Lines of watts (a day) rather than bars of kWh.</summary>
    public bool IsLines { get => (bool)GetValue(IsLinesProperty); set => SetValue(IsLinesProperty, value); }
    public Brush CpuBrush { get => (Brush)GetValue(CpuBrushProperty); set => SetValue(CpuBrushProperty, value); }
    public Brush GpuBrush { get => (Brush)GetValue(GpuBrushProperty); set => SetValue(GpuBrushProperty, value); }

    /// <summary>A round top: four steps of 1, 1.5, 2, 2.5, 3, 4, 5, 6 or 8 times a power of ten.</summary>
    internal static double NiceTop(double max)
    {
        if (max <= 0) return 4;
        double step = max * 1.05 / 4, p = Math.Pow(10, Math.Floor(Math.Log10(step))), m = step / p;
        double nice = new[] { 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 }.First(s => m <= s + 1e-9);
        return nice * p * 4;
    }

    private static string Axis(double v, bool watts) =>
        watts ? $"{v:0} W" : v >= 10 ? v.ToString("0", CultureInfo.CurrentCulture) : v.ToString("0.#", CultureInfo.CurrentCulture);

    protected override void OnRender(DrawingContext dc)
    {
        var slots = Slots;
        if (slots is not { Count: > 0 } || ActualWidth < 60 || ActualHeight < 60) return;
        bool lines = IsLines;
        double max = lines
            ? slots.Select(s => Math.Max(s.Cpu ?? 0, s.Gpu ?? 0)).Max()
            : slots.Select(s => (s.Cpu ?? 0) + (s.Gpu ?? 0)).Max();
        double top = NiceTop(max);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var plot = new Rect(AxisWidth + 12, Top, Math.Max(1, ActualWidth - AxisWidth - 12 - 2), Math.Max(1, ActualHeight - AxisHeight - Top));
        var gridPen = new Pen(GridBrush, 1) { DashStyle = new DashStyle([3, 4], 0) };
        gridPen.Freeze();
        for (int i = 0; i <= 4; i++)
        {
            double y = Math.Round(plot.Bottom - plot.Height * i / 4) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            Label(dc, i == 0 ? "0" : Axis(top * i / 4, lines), new Point(plot.Left - 8, y), LabelBrush, right: true);
        }
        double slotW = plot.Width / slots.Count;
        double Y(double v) => plot.Bottom - plot.Height * Math.Clamp(v / top, 0, 1);
        double X(int i) => plot.Left + slotW * (i + 0.5);

        if (lines)
        {
            DrawLine(dc, slots, s => s.Cpu, CpuBrush, X, Y);
            DrawLine(dc, slots, s => s.Gpu, GpuBrush, X, Y);
        }
        else
        {
            double barW = Math.Max(2, Math.Min(36, slotW * 0.6));
            for (int i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                var pieces = new List<(Brush, double)>();
                if (s.Cpu is double c && c > 0) pieces.Add((CpuBrush, plot.Bottom - Y(c)));
                if (s.Gpu is double g && g > 0) pieces.Add((GpuBrush, plot.Bottom - Y(g)));
                if (pieces.Count > 0) StackedBar.Draw(dc, X(i) - barW / 2, barW, plot.Bottom, pieces);
            }
        }
        for (int i = 0; i < slots.Count; i++)
            if (slots[i].Axis.Length > 0) Label(dc, slots[i].Axis, new Point(X(i), plot.Bottom + AxisHeight / 2 + 2), LabelBrush, center: true);

        if (HoverX is double hx && hx >= plot.Left && hx <= plot.Right)
        {
            int i = Math.Clamp((int)((hx - plot.Left) / slotW), 0, slots.Count - 1);
            if (!lines && slots[i].Rich is { } rich && (slots[i].Cpu is not null || slots[i].Gpu is not null))
            {
                // A day's own hover (1.22.0): its column lit, the box beside it, inside the chart.
                var column = new Rect(plot.Left + slotW * i + 2, plot.Top, Math.Max(1, slotW - 4), plot.Height);
                RichHover.Lit(dc, column);
                // Inside the chart's card: beside the column, up to over the chart's own heading when a day says a lot.
                RichHover.Draw(dc, this, Tip(i, rich), column, new Rect(0, -RiseAbove, ActualWidth, ActualHeight + RiseAbove));
            }
            else Hover(dc, plot, X(i), slots[i].Title, slots[i].Lines);
        }
    }

    /// <summary>How far a day's hover may rise above the chart: over its heading, inside its card.</summary>
    private const double RiseAbove = 50;

    // The rich hover of the slot under the pointer, made once while it stays there.
    private (IReadOnlyList<PowerSlot>? Slots, int Index, RichTip? Tip) _tip;

    private RichTip Tip(int i, Func<RichTip> make)
    {
        if (ReferenceEquals(_tip.Slots, Slots) && _tip.Index == i && _tip.Tip is { } made) return made;
        var tip = make();
        _tip = (Slots, i, tip);
        return tip;
    }

    // A line through the recorded slots, broken where one wasn't.
    private static void DrawLine(DrawingContext dc, IReadOnlyList<PowerSlot> slots, Func<PowerSlot, double?> value, Brush brush, Func<int, double> x, Func<double, double> y)
    {
        var pen = new Pen(brush, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            bool open = false;
            for (int i = 0; i < slots.Count; i++)
            {
                if (value(slots[i]) is not double v) { open = false; continue; }
                var p = new Point(x(i), y(v));
                if (!open) { g.BeginFigure(p, false, false); open = true; }
                else g.LineTo(p, true, true);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
        // A lone recorded hour still shows: a dot.
        for (int i = 0; i < slots.Count; i++)
            if (value(slots[i]) is double v && (i == 0 || value(slots[i - 1]) is null) && (i == slots.Count - 1 || value(slots[i + 1]) is null))
                dc.DrawEllipse(brush, null, new Point(x(i), y(v)), 2.5, 2.5);
    }
}
