using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Core.Apps;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;

namespace Rigsight.Controls;

/// <summary>
/// A 24-hour strip showing which app was in front (colored by category), with the CPU and GPU
/// temperature curves drawn underneath. Hover to see exactly what was happening at any minute.
/// </summary>
public sealed class DayTimeline : FrameworkElement
{
    private const double Left = 40, Right = 8, BandHeight = 26, Gap = 12, AxisHeight = 26;

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<TimelineSegment>), typeof(DayTimeline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TempsProperty = DependencyProperty.Register(
        nameof(Temps), typeof(IReadOnlyList<TempPoint>), typeof(DayTimeline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DayProperty = DependencyProperty.Register(
        nameof(Day), typeof(DateTime), typeof(DayTimeline), new FrameworkPropertyMetadata(DateTime.Today, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<TimelineSegment>? Segments { get => (IReadOnlyList<TimelineSegment>?)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public IReadOnlyList<TempPoint>? Temps { get => (IReadOnlyList<TempPoint>?)GetValue(TempsProperty); set => SetValue(TempsProperty, value); }
    public DateTime Day { get => (DateTime)GetValue(DayProperty); set => SetValue(DayProperty, value); }

    /// <summary>When the data was read (history is saved once a minute, so the last minute or so isn't in it yet).</summary>
    public static readonly DependencyProperty RecordedUntilProperty = DependencyProperty.Register(
        nameof(RecordedUntil), typeof(DateTime?), typeof(DayTimeline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public DateTime? RecordedUntil { get => (DateTime?)GetValue(RecordedUntilProperty); set => SetValue(RecordedUntilProperty, value); }

    private double _hoverX = -1;
    private static readonly Dictionary<AppCategory, (Brush Solid, Brush Faint)> CategoryBrushes = [];

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hoverX = e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoverX = -1;
        InvalidateVisual();
    }

    private static (Brush Solid, Brush Faint) BrushesFor(AppCategory c)
    {
        if (!CategoryBrushes.TryGetValue(c, out var b))
        {
            var color = (Color)ColorConverter.ConvertFromString(AppCatalog.Color(c));
            CategoryBrushes[c] = b = (ChartPaint.Brush(color), ChartPaint.Brush(color, 0.28));
        }
        return b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (w < Left + Right + 50 || h < BandHeight + Gap + AxisHeight + 30) return;

        var day = Day.Date;
        double plotW = w - Left - Right;
        double X(DateTime t) => Left + (t - day).TotalHours / 24 * plotW;
        DateTime TimeAt(double x) => day.AddHours((x - Left) / plotW * 24);

        // Activity band.
        var band = new Rect(Left, 0, plotW, BandHeight);
        dc.DrawRoundedRectangle(ChartPaint.Track, null, band, 6, 6);
        dc.PushClip(new RectangleGeometry(band, 6, 6));
        foreach (var s in Segments ?? [])
        {
            if (s.AppId is null) continue;
            double x0 = X(s.Start), x1 = X(s.End);
            var (solid, faint) = BrushesFor(s.Category);
            dc.DrawRectangle(s.Away ? faint : solid, null, new Rect(x0, 0, Math.Max(1, x1 - x0), BandHeight));
        }
        dc.Pop();

        // Temperature chart.
        var plot = new Rect(Left, BandHeight + Gap, plotW, h - BandHeight - Gap - AxisHeight);
        var temps = Temps ?? [];
        var values = temps.SelectMany(t => new[] { t.Cpu, t.Gpu }).OfType<double>().Select(Units.Temp).ToList();
        double lo = values.Count > 0 ? Math.Floor((values.Min() - 2) / 10) * 10 : 30;
        double hi = values.Count > 0 ? Math.Ceiling((values.Max() + 2) / 10) * 10 : 80;
        if (hi - lo < 20) hi = lo + 20;

        var gridPen = new Pen(ChartPaint.Grid, 1) { DashStyle = new DashStyle([3, 4], 0) };
        for (int i = 0; i <= 3; i++)
        {
            double v = lo + (hi - lo) * i / 3;
            double y = Math.Round(plot.Bottom - plot.Height * i / 3) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            ChartPaint.Text(dc, this, $"{v:0}°", new Point(plot.Left - 8, y), 11, ChartPaint.Label, ChartPaint.Align.Right);
        }

        double Y(double c) => plot.Bottom - (Units.Temp(c) - lo) / (hi - lo) * plot.Height;
        DrawLine(temps, t => t.Cpu, ChartPaint.Cpu);
        DrawLine(temps, t => t.Gpu, ChartPaint.Gpu);

        // Hour labels, the two at the ends kept inside the chart: centred on its corners, midnight would run
        // under the lowest temperature label.
        for (int hr = 0; hr <= 24; hr += 3)
        {
            double x = Left + plotW * hr / 24;
            string label = hr switch { 0 or 24 => "12 AM", 12 => "12 PM", < 12 => $"{hr} AM", _ => $"{hr - 12} PM" };
            var align = hr == 0 ? ChartPaint.Align.Left : hr == 24 ? ChartPaint.Align.Right : ChartPaint.Align.Center;
            ChartPaint.Text(dc, this, label, new Point(x, h - AxisHeight / 2 + 2), 11, ChartPaint.Label, align);
        }

        // Today: a "Now" line, with nothing to say about the time after it.
        var now = DateTime.Now;
        double nowX = X(now);
        bool isToday = now.Date == day;
        if (isToday)
        {
            var nowPen = new Pen(ChartPaint.Label, 1) { DashStyle = new DashStyle([2, 3], 0) };
            dc.DrawLine(nowPen, new Point(Math.Round(nowX) + 0.5, BandHeight + 2), new Point(Math.Round(nowX) + 0.5, plot.Bottom));
            ChartPaint.Text(dc, this, "Now", new Point(nowX + 4, plot.Top + 7), 10.5, ChartPaint.Label);
        }

        // Hover: cursor line and details (not beyond now).
        if (_hoverX >= Left && _hoverX <= Left + plotW && !(isToday && _hoverX > nowX) && day <= now.Date)
        {
            var time = TimeAt(_hoverX);
            dc.DrawLine(new Pen(ChartPaint.Cursor, 1), new Point(_hoverX, 0), new Point(_hoverX, plot.Bottom));
            var seg = (Segments ?? []).FirstOrDefault(s => s.Start <= time && time < s.End);
            var point = temps.Where(t => Math.Abs((t.Time - time).TotalMinutes) <= 1.5).MinBy(t => Math.Abs((t.Time - time).TotalSeconds));

            var lines = new List<(string, Brush, bool)> { (time.ToString("h:mm tt"), ChartPaint.TextBrush, true) };
            if (seg?.App is { } app)
                lines.Add((seg.Away ? $"{app} (away)" : app, BrushesFor(seg.Category).Solid, false));
            else if (point is null)
                // The minute in progress (and any since the data was read) simply isn't saved yet.
                lines.Add((time >= (RecordedUntil ?? now).AddMinutes(-1.5) ? "Not recorded yet" : "PC off or asleep", ChartPaint.Muted, false));
            if (point?.Cpu is double c) lines.Add(($"CPU {Units.TempShort(c)}", ChartPaint.Brush(ChartPaint.Cpu), false));
            if (point?.Gpu is double g) lines.Add(($"GPU {Units.TempShort(g)}", ChartPaint.Brush(ChartPaint.Gpu), false));
            ChartPaint.InfoBox(dc, this, lines, new Point(_hoverX, BandHeight + 4), new Rect(0, 0, w, h));
        }

        void DrawLine(IReadOnlyList<TempPoint> pts, Func<TempPoint, double?> sel, Color color)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                bool open = false;
                DateTime last = DateTime.MinValue;
                foreach (var p in pts)
                {
                    if (sel(p) is not double v) { open = false; continue; }
                    var pt = new Point(X(p.Time), Y(v));
                    if (!open || (p.Time - last).TotalMinutes > 2.5) ctx.BeginFigure(pt, false, false);
                    else ctx.LineTo(pt, true, true);
                    open = true;
                    last = p.Time;
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, new Pen(ChartPaint.Brush(color), 1.6) { LineJoin = PenLineJoin.Round }, geo);
        }
    }
}
