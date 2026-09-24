using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Core.Apps;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;

namespace Rigsight.Controls;

/// <summary>Active hours per day, stacked by app category. Hover a bar for that day's details.</summary>
public sealed class DailyBars : FrameworkElement
{
    private const double Left = 40, Right = 8, Top = 8, AxisHeight = 24;

    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(
        nameof(Days), typeof(IReadOnlyList<DayBucket>), typeof(DailyBars), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<DayBucket>? Days { get => (IReadOnlyList<DayBucket>?)GetValue(DaysProperty); set => SetValue(DaysProperty, value); }

    private int _hover = -1;
    private static readonly Dictionary<AppCategory, Brush> Fills = [];

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var days = Days;
        if (days is null || days.Count == 0) return;
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

    private static Brush Fill(AppCategory c)
    {
        if (!Fills.TryGetValue(c, out var b))
            Fills[c] = b = ChartPaint.Brush((Color)ColorConverter.ConvertFromString(AppCatalog.Color(c)));
        return b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var days = Days;
        if (days is null || days.Count == 0 || w < Left + Right + 40 || h < 60) return;

        var plot = new Rect(Left, Top, w - Left - Right, h - Top - AxisHeight);
        double maxHours = Math.Max(1, Math.Ceiling(days.Max(d => d.ActiveSec) / 3600));
        // About four gridlines at a round step: 1–6 hours for days, tens to hundreds for a year's months.
        double step = new double[] { 1, 2, 4, 6, 10, 20, 25, 50, 100, 200, 250, 500 }.FirstOrDefault(x => maxHours / x <= 5, 1000);
        maxHours = Math.Ceiling(maxHours / step) * step;

        var gridPen = new Pen(ChartPaint.Grid, 1) { DashStyle = new DashStyle([3, 4], 0) };
        for (double hr = 0; hr <= maxHours; hr += step)
        {
            double y = Math.Round(plot.Bottom - plot.Height * hr / maxHours) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            ChartPaint.Text(dc, this, $"{hr:0}h", new Point(plot.Left - 8, y), 11, ChartPaint.Label, ChartPaint.Align.Right);
        }

        double slot = plot.Width / days.Count;
        double barW = Math.Max(3, Math.Min(42, slot * 0.62));
        // A year's report has one bar per month.
        bool monthly = days.Count >= 2 && days[1].Day == days[0].Day.AddMonths(1);
        var current = monthly ? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1) : DateTime.Today;
        int labelEvery = days.Count <= (monthly ? 12 : 7) ? 1 : days.Count <= 16 ? 2 : 3;

        for (int i = 0; i < days.Count; i++)
        {
            var d = days[i];
            double cx = plot.Left + slot * (i + 0.5);
            if (i == _hover)
                dc.DrawRoundedRectangle(ChartPaint.Track, null, new Rect(cx - slot / 2 + 2, plot.Top, slot - 4, plot.Height), 6, 6);

            double y = plot.Bottom;
            foreach (var (cat, sec) in d.ActiveByCategory.OrderByDescending(kv => kv.Value))
            {
                double bh = sec / 3600 / maxHours * plot.Height;
                if (bh < 0.5) continue;
                dc.DrawRectangle(Fill(cat), null, new Rect(cx - barW / 2, y - bh, barW, bh));
                y -= bh;
            }

            // Count from the right, so today (the last bar) always has a label.
            if ((days.Count - 1 - i) % labelEvery == 0)
            {
                string label = monthly ? (days.Count > 12 && d.Day.Month == 1 ? d.Day.ToString("yyyy") : d.Day.ToString("MMM"))
                    : d.Day == DateTime.Today ? "Today" : days.Count <= 7 ? d.Day.ToString("ddd d") : d.Day.Day.ToString();
                var brush = d.Day == current ? ChartPaint.TextBrush : ChartPaint.Label;
                ChartPaint.Text(dc, this, label, new Point(cx, h - AxisHeight / 2), 11, brush, ChartPaint.Align.Center);
            }
        }

        if (_hover >= 0 && _hover < days.Count)
        {
            var d = days[_hover];
            var lines = new List<(string, Brush, bool)>
            {
                (d.Day.ToString(monthly ? "MMMM yyyy" : "dddd, d MMM"), ChartPaint.TextBrush, true),
                (d.ActiveSec > 0 ? $"{Units.Duration(d.ActiveSec)} active" : "No activity", ChartPaint.Muted, false),
            };
            if (d.TopApp is not null) lines.Add(($"Most used: {d.TopApp}", ChartPaint.Muted, false));
            foreach (var (cat, sec) in d.ActiveByCategory.OrderByDescending(kv => kv.Value).Take(4))
                lines.Add(($"{AppCatalog.Label(cat)}  {Units.Duration(sec)}", Fill(cat), false));
            if (d.CpuTempMax is double c) lines.Add(($"CPU peak {Units.TempShort(c)}", ChartPaint.Brush(ChartPaint.Cpu), false));
            if (d.GpuTempMax is double g) lines.Add(($"GPU peak {Units.TempShort(g)}", ChartPaint.Brush(ChartPaint.Gpu), false));
            double cx = plot.Left + slot * (_hover + 0.5);
            ChartPaint.InfoBox(dc, this, lines, new Point(cx + barW / 2, plot.Top), new Rect(0, 0, w, h));
        }
    }
}
