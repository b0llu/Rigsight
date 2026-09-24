using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core.Stability;
using Rigsight.Models;

namespace Rigsight.Controls;

/// <summary>One day on the crash timeline: problems by severity, and what changed on the PC that day.</summary>
public sealed class CrashDay
{
    public DateTime Day { get; init; }
    public int[] Counts { get; } = new int[4]; // indexed by CrashSeverity
    public List<string> Problems { get; } = [];
    public List<SystemChange> Changes { get; } = [];
    public int Total => Counts.Sum();
}

/// <summary>
/// Problems per day as small stacked bars (blue screens red, serious orange, app crashes amber, power loss grey),
/// with blue marks under the days a driver or Windows update was installed. Hover a day for what happened.
/// </summary>
public sealed class CrashStrip : FrameworkElement
{
    private const double Left = 8, Right = 8, Top = 6, MarkHeight = 12, AxisHeight = 20;

    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(
        nameof(Days), typeof(IReadOnlyList<CrashDay>), typeof(CrashStrip), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<CrashDay>? Days { get => (IReadOnlyList<CrashDay>?)GetValue(DaysProperty); set => SetValue(DaysProperty, value); }

    /// <summary>Run with the day (a DateTime) when a day is clicked.</summary>
    public static readonly DependencyProperty DayCommandProperty = DependencyProperty.Register(
        nameof(DayCommand), typeof(ICommand), typeof(CrashStrip));

    public ICommand? DayCommand { get => (ICommand?)GetValue(DayCommandProperty); set => SetValue(DayCommandProperty, value); }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (Days is { } days && _hover >= 0 && _hover < days.Count) DayCommand?.Execute(days[_hover].Day);
    }

    public static SolidColorBrush CriticalBrush => ChartPaint.Hot;
    public static SolidColorBrush SeriousBrush => ChartPaint.Orange;
    public static SolidColorBrush MinorBrush => ChartPaint.Warm;
    public static SolidColorBrush InfoBrush => ChartPaint.Faint;
    private static Brush ChangeBrush => ChartPaint.CpuBrush;
    private static Brush[] Fills => [InfoBrush, MinorBrush, SeriousBrush, CriticalBrush];

    private int _hover = -1;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var days = Days;
        if (days is null || days.Count == 0) return;
        double slot = (ActualWidth - Left - Right) / days.Count;
        int i = (int)Math.Floor((e.GetPosition(this).X - Left) / slot);
        int next = i >= 0 && i < days.Count ? i : -1;
        if (next != _hover) { _hover = next; InvalidateVisual(); }
        Cursor = next >= 0 && DayCommand is not null ? Cursors.Hand : null;
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
        var days = Days;
        if (days is null || days.Count == 0 || w < 80 || h < 50) return;

        var plot = new Rect(Left, Top, w - Left - Right, h - Top - MarkHeight - AxisHeight);
        double slot = plot.Width / days.Count;
        double barW = Math.Max(2, Math.Min(14, slot * 0.6));
        int max = Math.Max(3, days.Max(d => d.Total));

        // Baseline.
        dc.DrawRectangle(ChartPaint.Grid, null, new Rect(plot.Left, plot.Bottom, plot.Width, 1));

        for (int i = 0; i < days.Count; i++)
        {
            var d = days[i];
            double cx = plot.Left + slot * (i + 0.5);
            if (i == _hover)
                dc.DrawRoundedRectangle(ChartPaint.Track, null, new Rect(cx - Math.Max(slot, 8) / 2, plot.Top - 2, Math.Max(slot, 8), plot.Height + MarkHeight + 2), 4, 4);

            // Worst at the bottom, so the colour you notice first is the one that matters.
            double y = plot.Bottom;
            for (int s = Fills.Length - 1; s >= 0; s--)
            {
                if (d.Counts[s] == 0) continue;
                double bh = Math.Max(3, d.Counts[s] / (double)max * plot.Height);
                dc.DrawRoundedRectangle(Fills[s], null, new Rect(cx - barW / 2, y - bh, barW, bh), Math.Min(2, barW / 2), Math.Min(2, barW / 2));
                y -= bh + 1;
            }

            if (d.Changes.Count > 0)
            {
                // A small diamond: something was installed that day.
                double my = plot.Bottom + MarkHeight / 2 + 1, r = 3.5;
                var diamond = new StreamGeometry();
                using (var g = diamond.Open())
                {
                    g.BeginFigure(new Point(cx, my - r), true, true);
                    g.LineTo(new Point(cx + r, my), false, false);
                    g.LineTo(new Point(cx, my + r), false, false);
                    g.LineTo(new Point(cx - r, my), false, false);
                }
                diamond.Freeze();
                dc.DrawGeometry(ChangeBrush, null, diamond);
            }
        }

        // Date labels: every day for short ranges, Mondays for about a month, the 1st of each month up to a year,
        // and each new year beyond that (the first label carries its year too); "Today" always.
        var todayText = ChartPaint.Format(this, "Today", 11, ChartPaint.TextBrush);
        double todayX = Math.Clamp(plot.Left + slot * (days.Count - 0.5) - todayText.Width / 2, 0, w - todayText.Width);
        if (days[^1].Day == DateTime.Today) dc.DrawText(todayText, new Point(todayX, h - AxisHeight + 3));
        else todayX = double.MaxValue;
        double lastRight = double.MinValue;
        for (int i = 0; i < days.Count - 1; i++)
        {
            var day = days[i].Day;
            bool years = days.Count > 400;
            // A new year soon after the start would collide with the first label: label that year instead.
            if (i == 0 && years && days.Take(150).Any(d => d.Day.DayOfYear == 1)) continue;
            if (!(i == 0 || days.Count <= 14 || (days.Count <= 45 ? day.DayOfWeek == DayOfWeek.Monday
                : years ? day.DayOfYear == 1 : day.Day == 1))) continue;
            string text = days.Count <= 14 ? day.ToString("ddd d")
                : years ? (i == 0 ? day.ToString("MMM yyyy") : day.ToString("yyyy"))
                : day.ToString("d MMM");
            var ft = ChartPaint.Format(this, text, 11, ChartPaint.Label);
            double x = Math.Clamp(plot.Left + slot * (i + 0.5) - ft.Width / 2, 0, w - ft.Width);
            if (x < lastRight + 8 || x + ft.Width > todayX - 8) continue;
            dc.DrawText(ft, new Point(x, h - AxisHeight + 3));
            lastRight = x + ft.Width;
        }

        if (_hover >= 0 && _hover < days.Count)
        {
            var d = days[_hover];
            var lines = new List<(string, Brush, bool)> { (d.Day.ToString("dddd, d MMM yyyy"), ChartPaint.TextBrush, true) };
            if (d.Total == 0) lines.Add(("No problems", ChartPaint.Muted, false));
            else if (DayCommand is not null) lines.Add(("Click to see this day", ChartPaint.Label, false));
            foreach (var p in d.Problems.Take(6)) lines.Add((p, ChartPaint.Muted, false));
            if (d.Problems.Count > 6) lines.Add(($"and {d.Problems.Count - 6} more", ChartPaint.Muted, false));
            foreach (var c in d.Changes.Take(4)) lines.Add(($"Installed: {c.Title}", ChangeBrush, false));
            double cx = plot.Left + slot * (_hover + 0.5);
            // The strip is short, so the box may extend above it (over the page header area).
            ChartPaint.InfoBox(dc, this, lines, new Point(cx, plot.Top), new Rect(0, -240, w, h + 240));
        }
    }

    /// <summary>Builds the days from <paramref name="from"/> to today from the problems and changes.</summary>
    /// <summary>One entry per day from <paramref name="from"/> until <paramref name="to"/> (exclusive) or today, whichever is first.</summary>
    public static List<CrashDay> BuildDays(DateTime from, DateTime to, IEnumerable<CrashGroup> groups, IEnumerable<SystemChange> changes)
    {
        var last = to.Date.AddDays(-1) < DateTime.Today ? to.Date.AddDays(-1) : DateTime.Today;
        var list = new List<CrashDay>();
        for (var d = from.Date; d <= last; d = d.AddDays(1)) list.Add(new CrashDay { Day = d });
        CrashDay? DayOf(DateTime t) => t.Date < from.Date || t.Date > last ? null : list[(int)(t.Date - from.Date).TotalDays];

        var rows = groups.SelectMany(g => g.Rows.Select(r => (Row: r, Severity: g.IsIncident ? CrashSeverity.Serious : CrashGroup.SeverityOf(r))));
        foreach (var (r, severity) in rows.OrderBy(x => x.Row.Time))
            if (DayOf(r.Time) is { } day)
            {
                day.Counts[(int)severity]++;
                day.Problems.Add($"{r.Time:h:mm tt}  {r.Title}");
            }
        foreach (var c in changes)
            if (DayOf(c.Time) is { } day) day.Changes.Add(c);
        return list;
    }
}
