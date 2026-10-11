using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Models;

namespace Rigsight.Controls;

/// <summary>
/// The last seven days hour by hour (the Temperatures page): a box for each hour, coloured by the CPU's or the GPU's
/// average over it, in the steps the rest of the app colours a temperature by (by the hour's highest reading, a spike
/// of a second when an app opened turned a whole hour of browsing amber). An hour with nothing recorded is an empty
/// box. Like every chart it answers the pointer: the hour, both averages with their highs, and the app in front.
/// </summary>
public sealed class HeatGrid : FrameworkElement
{
    public static readonly DependencyProperty HoursProperty = DependencyProperty.Register(
        nameof(Hours), typeof(IReadOnlyList<HeatHour>), typeof(HeatGrid), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((HeatGrid)d)._byHour = null));

    /// <summary>The last day shown (the bottom row).</summary>
    public static readonly DependencyProperty LastDayProperty = DependencyProperty.Register(
        nameof(LastDay), typeof(DateTime), typeof(HeatGrid), new FrameworkPropertyMetadata(DateTime.Today, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<HeatHour>? Hours { get => (IReadOnlyList<HeatHour>?)GetValue(HoursProperty); set => SetValue(HoursProperty, value); }
    public DateTime LastDay { get => (DateTime)GetValue(LastDayProperty); set => SetValue(LastDayProperty, value); }

    public const int Days = 7;
    private const double Left = 38, Foot = 20, Gap = 2;

    private Dictionary<DateTime, HeatHour>? _byHour;
    private Dictionary<DateTime, HeatHour> ByHour => _byHour ??= (Hours ?? []).GroupBy(h => h.Hour).ToDictionary(g => g.Key, g => g.First());

    private readonly ToolTip _tip = new() { Placement = PlacementMode.Relative, IsHitTestVisible = false };
    private (int Row, int Hour)? _hover;

    public HeatGrid()
    {
        // A page switched away under a resting pointer takes the chart with it and no "mouse left" is said.
        Unloaded += (_, _) => HideTip();
        IsVisibleChanged += (_, _) => { if (!IsVisible) HideTip(); };
    }

    private void HideTip()
    {
        _hover = null;
        _tip.IsOpen = false;
    }

    private DateTime DayOf(int row) => LastDay.Date.AddDays(row - (Days - 1));
    private (double W, double H) Cell => (Math.Max(1, (ActualWidth - Left) / 24), Math.Max(1, (ActualHeight - Foot) / Days));

    /// <summary>The hotter of the two against each one's own steps: 0 cool, 1 fine, 2 warm, 3 hot (null: nothing read).</summary>
    internal static int? Level(HeatHour hour)
    {
        static int Of(double c, double cool, double good, double warm) => c < cool ? 0 : c < good ? 1 : c < warm ? 2 : 3;
        int? cpu = hour.Cpu is double c ? Of(c, 45, 70, 85) : null, gpu = hour.Gpu is double g ? Of(g, 45, 70, 83) : null;
        return cpu is null ? gpu : gpu is null ? cpu : Math.Max(cpu.Value, gpu.Value);
    }

    /// <summary>"Thu 7 PM · CPU 66° (up to 74°), GPU 68° · Apex Legends", or that nothing was recorded then.</summary>
    internal static string TipOf(DateTime hour, HeatHour? read)
    {
        string when = hour.ToString("ddd h tt", CultureInfo.CurrentCulture);
        if (read is null || (read.Cpu is null && read.Gpu is null)) return $"{when} · Nothing recorded";
        // The highest beside the average only where it says more (it rounds to another figure).
        static string? Part(string name, double? average, double? high) => average is not double a ? null
            : high is double h && Units.TempShort(h) != Units.TempShort(a) ? $"{name} {Units.TempShort(a)} (up to {Units.TempShort(h)})" : $"{name} {Units.TempShort(a)}";
        string temps = string.Join(", ", new[] { Part("CPU", read.Cpu, read.CpuHigh), Part("GPU", read.Gpu, read.GpuHigh) }.Where(t => t is not null));
        return read.App is null ? $"{when} · {temps}" : $"{when} · {temps} · {read.App}";
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        var (w, h) = Cell;
        int hour = (int)((p.X - Left) / w), row = (int)(p.Y / h);
        (int, int)? over = p.X >= Left && hour is >= 0 and < 24 && row is >= 0 and < Days ? (row, hour) : null;
        if (over == _hover && _tip.IsOpen == over is not null)
        {
            if (over is not null) (_tip.HorizontalOffset, _tip.VerticalOffset) = (p.X + 14, p.Y - 34);
            return;
        }
        _hover = over;
        if (over is var (r, hr) && IsLoaded)
        {
            var at = DayOf(r).AddHours(hr);
            _tip.Content = TipOf(at, ByHour.GetValueOrDefault(at));
            _tip.PlacementTarget = this;
            (_tip.HorizontalOffset, _tip.VerticalOffset) = (p.X + 14, p.Y - 34);
            _tip.IsOpen = true;
        }
        else _tip.IsOpen = false;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        HideTip();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent background so the whole area answers the pointer.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (ActualWidth < Left + 48 || ActualHeight < Foot + Days * 4) return;
        var (w, h) = Cell;
        var empty = ChartPaint.Track;
        var outline = new Pen(ChartPaint.TextBrush, 1);
        outline.Freeze();
        var steps = new[] { Tinted(ChartPaint.Cool, 0.5), Tinted(ChartPaint.Good, 0.5), Tinted(ChartPaint.Warm, 0.85), Tinted(ChartPaint.Hot, 1) };
        var hours = ByHour;
        for (int row = 0; row < Days; row++)
        {
            var day = DayOf(row);
            ChartPaint.Text(dc, this, day.ToString("ddd", CultureInfo.CurrentCulture), new Point(0, row * h + (h - Gap) / 2), 11, ChartPaint.Label);
            for (int hour = 0; hour < 24; hour++)
            {
                var box = new Rect(Left + hour * w, row * h, Math.Max(1, w - Gap), Math.Max(1, h - Gap));
                var brush = hours.TryGetValue(day.AddHours(hour), out var read) && Level(read) is int level ? steps[level] : empty;
                dc.DrawRoundedRectangle(brush, _hover == (row, hour) ? outline : null, box, 3, 3);
            }
        }
        // The hours along the foot, each at the left of its column.
        foreach (int hour in (int[])[0, 6, 12, 18])
        {
            ChartPaint.Text(dc, this, new DateTime(2000, 1, 1, hour, 0, 0).ToString("h tt", CultureInfo.CurrentCulture),
                new Point(Left + hour * w, ActualHeight - Foot + 4), 11, ChartPaint.Label, middle: false);
        }
    }

    private static Brush Tinted(SolidColorBrush brush, double opacity)
    {
        var tinted = new SolidColorBrush(brush.Color) { Opacity = opacity };
        tinted.Freeze();
        return tinted;
    }
}
