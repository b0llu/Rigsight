using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Core.Apps;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;

namespace Rigsight.Controls;

/// <summary>
/// Active hours per day, stacked by app category. Each bar says its total over it where there's room, and a dashed line
/// marks the average day (the days the PC was used), so a long or short day reads without hovering (issue #9,
/// 2026-11-05; mock-up BWEhGhSQvDsiPkBQ8Tn6M6 option A). Hover a bar for that day's details.
/// </summary>
public sealed class DailyBars : FrameworkElement
{
    private const double Left = 40, Right = 8, AxisHeight = 24;
    /// <summary>Room over the plot: for the totals over the bars.</summary>
    private const double Top = 22;
    /// <summary>The narrowest slot a bar's total is written over (a month's 31 bars are too narrow; their hover says it).</summary>
    public const double TotalsFrom = 44;

    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(
        nameof(Days), typeof(IReadOnlyList<DayBucket>), typeof(DailyBars), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<DayBucket>? Days { get => (IReadOnlyList<DayBucket>?)GetValue(DaysProperty); set => SetValue(DaysProperty, value); }

    public static readonly DependencyProperty OnlyProperty = DependencyProperty.Register(
        nameof(Only), typeof(IReadOnlyList<AppCategory>), typeof(DailyBars), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>The kinds picked in Where the time went (null: all): the rest step back, and each bar's total is theirs.</summary>
    public IReadOnlyList<AppCategory>? Only { get => (IReadOnlyList<AppCategory>?)GetValue(OnlyProperty); set => SetValue(OnlyProperty, value); }

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

    // The fills are the current theme's: a picked accent turns the categories into its shades.
    private static int _fillsTheme = -1;

    private static Brush Fill(AppCategory c)
    {
        if (_fillsTheme != Services.ThemeManager.Version)
        {
            Fills.Clear();
            _fillsTheme = Services.ThemeManager.Version;
        }
        if (!Fills.TryGetValue(c, out var b))
            Fills[c] = b = ChartPaint.Brush((Color)ColorConverter.ConvertFromString(Services.ThemeManager.CategoryHex(c)));
        return b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var days = Days;
        if (days is null || days.Count == 0 || w < Left + Right + 40 || h < 60) return;

        var plot = new Rect(Left, Top, w - Left - Right, h - Top - AxisHeight);
        // Bars per hour (one day), per day, or per month (a year), told apart by their spacing.
        bool hourly = days.Count >= 2 && days[1].Day == days[0].Day.AddHours(1);
        double maxSec = days.Max(d => d.ActiveSec);
        // Up to an hour at most (always, per hour): the axis counts minutes. Otherwise hours, about four gridlines
        // at a round step (1–6 hours for days, tens to hundreds for a year's months).
        bool minutes = hourly || maxSec <= 3600;
        double unitSec = minutes ? 60 : 3600;
        double maxUnits = Math.Max(1, Math.Ceiling(maxSec / unitSec));
        double step = minutes
            ? new double[] { 5, 10, 15, 20, 30 }.FirstOrDefault(x => maxUnits / x <= 4, 30)
            : new double[] { 1, 2, 4, 6, 10, 20, 25, 50, 100, 200, 250, 500 }.FirstOrDefault(x => maxUnits / x <= 5, 1000);
        maxUnits = Math.Max(step, Math.Ceiling(maxUnits / step) * step);
        double maxHours = maxUnits * unitSec / 3600;

        var gridPen = new Pen(ChartPaint.Grid, 1) { DashStyle = new DashStyle([3, 4], 0) };
        for (double u = 0; u <= maxUnits; u += step)
        {
            double y = Math.Round(plot.Bottom - plot.Height * u / maxUnits) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            ChartPaint.Text(dc, this, minutes ? $"{u:0}m" : $"{u:0}h", new Point(plot.Left - 8, y), 11, ChartPaint.Label, ChartPaint.Align.Right);
        }

        double slot = plot.Width / days.Count;
        double barW = Math.Max(3, Math.Min(42, slot * 0.62));
        bool totals = slot >= TotalsFrom;
        // A year's report has one bar per month.
        bool monthly = days.Count >= 2 && days[1].Day == days[0].Day.AddMonths(1);
        var now = DateTime.Now;
        var current = monthly ? new DateTime(now.Year, now.Month, 1) : hourly ? now.Date.AddHours(now.Hour) : now.Date;
        int labelEvery = hourly ? 3 : days.Count <= (monthly ? 12 : 7) ? 1 : days.Count <= 16 ? 2 : 3;

        for (int i = 0; i < days.Count; i++)
        {
            var d = days[i];
            double cx = plot.Left + slot * (i + 0.5);
            if (i == _hover)
                dc.DrawRoundedRectangle(ChartPaint.Track, null, new Rect(cx - slot / 2 + 2, plot.Top, Math.Max(0, slot - 4), plot.Height), 6, 6);

            // One plain bar, each kind's piece joined square to the next and only the top rounded (StackedBar).
            double y = plot.Bottom;
            var only = Only;
            var pieces = new List<(Brush, double)>();
            // A kind picked: its pieces first (at the bottom, where they compare day to day), the rest faded over them.
            foreach (var (cat, sec) in d.ActiveByCategory.OrderBy(kv => only is null || only.Contains(kv.Key) ? 0 : 1).ThenByDescending(kv => kv.Value))
            {
                double bh = sec / 3600 / maxHours * plot.Height;
                if (bh < 0.5) continue;
                Brush fill = Fill(cat);
                if (only is not null && !only.Contains(cat))
                {
                    fill = fill.Clone();
                    fill.Opacity = 0.22;
                    fill.Freeze();
                }
                pieces.Add((fill, bh));
                y -= bh;
            }
            StackedBar.Draw(dc, cx - barW / 2, barW, plot.Bottom, pieces);
            // Its total over it (the picked kind's alone, when one is).
            double shownSec = only is null ? d.ActiveSec : d.ActiveByCategory.Where(kv => only.Contains(kv.Key)).Sum(kv => kv.Value);
            if (totals && shownSec >= 60)
                ChartPaint.Text(dc, this, Units.Duration(shownSec), new Point(cx, y - 9), 11, i == _hover || only is not null ? ChartPaint.TextBrush : ChartPaint.Muted, ChartPaint.Align.Center);

            // Hours every three from midnight; more than a year of months at each quarter (January as its year);
            // otherwise count from the right, so today (the last bar) always has a label.
            bool labelled = hourly ? i % labelEvery == 0
                : monthly && days.Count > 12 ? d.Day.Month % 3 == 1
                : (days.Count - 1 - i) % labelEvery == 0;
            if (labelled)
            {
                string label = hourly ? d.Day.ToString(TimeText.Fit("h tt"))
                    : monthly ? (days.Count > 12 && d.Day.Month == 1 ? d.Day.ToString("yyyy") : d.Day.ToString("MMM"))
                    : d.Day == DateTime.Today ? Lang.T("Today") : days.Count <= 7 ? d.Day.ToString("ddd d") : d.Day.Day.ToString();
                var brush = d.Day == current ? ChartPaint.TextBrush : ChartPaint.Label;
                ChartPaint.Text(dc, this, label, new Point(cx, h - AxisHeight / 2), 11, brush, ChartPaint.Align.Center);
            }
        }

        // The average day, as a dashed line with its figure at the right (not for a year's months: they aren't days).
        if (Only is null && !hourly && !monthly && AverageDay(days, now) is double avg && avg / 3600 < maxHours)
        {
            double y = Math.Round(plot.Bottom - plot.Height * avg / 3600 / maxHours) + 0.5;
            var avgPen = new Pen(ChartPaint.Muted, 1.2) { DashStyle = new DashStyle([5, 4], 0) };
            avgPen.Freeze();
            dc.DrawLine(avgPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var text = Lang.T($"Average day {Units.Duration(avg)}");
            var ft = ChartPaint.Format(this, text, 11, ChartPaint.TextBrush);
            var pill = new Rect(plot.Right - ft.Width - 16, y - ft.Height - 6, ft.Width + 12, ft.Height + 4);
            dc.DrawRoundedRectangle(ChartPaint.Track, null, pill, 5, 5);
            ChartPaint.Text(dc, this, text, new Point(pill.Left + 6, pill.Top + pill.Height / 2), 11, ChartPaint.TextBrush, ChartPaint.Align.Left);
        }

        if (_hover >= 0 && _hover < days.Count)
        {
            var d = days[_hover];
            var lines = new List<(string, Brush, bool)>
            {
                (hourly ? $"{TimeText.At(d.Day, "h tt")} – {TimeText.At(d.Day.AddHours(1), "h tt")}" : d.Day.ToString(monthly ? "MMMM yyyy" : "dddd, d MMM"), ChartPaint.TextBrush, true),
                (d.ActiveSec > 0 ? Lang.T($"{Units.Duration(d.ActiveSec)} active") : Lang.T("No activity"), ChartPaint.Muted, false),
            };
            if (d.TopApp is not null) lines.Add((Lang.T($"Most used: {d.TopApp}"), ChartPaint.Muted, false));
            if (!hourly && !monthly && AverageDay(days, now) is double avgDay && d.ActiveSec > 0 && d.Day < now.Date)
                lines.Add((d.ActiveSec >= avgDay ? Lang.T($"{Units.Duration(d.ActiveSec - avgDay)} more than the average day") : Lang.T($"{Units.Duration(avgDay - d.ActiveSec)} less than the average day"), ChartPaint.Muted, false));
            foreach (var (cat, sec) in d.ActiveByCategory.OrderByDescending(kv => kv.Value).Take(4))
                lines.Add(($"{AppCatalog.Label(cat)}  {Units.Duration(sec)}", Fill(cat), false));
            if (d.CpuTempMax is double c) lines.Add((Lang.T($"CPU peak {Units.TempShort(c)}"), ChartPaint.Brush(ChartPaint.Cpu), false));
            if (d.GpuTempMax is double g) lines.Add((Lang.T($"GPU peak {Units.TempShort(g)}"), ChartPaint.Brush(ChartPaint.Gpu), false));
            double cx = plot.Left + slot * (_hover + 0.5);
            ChartPaint.InfoBox(dc, this, lines, new Point(cx + barW / 2, plot.Top), new Rect(0, 0, w, h));
        }
    }

    /// <summary>
    /// The average active time of the days the PC was used, today left out while it's going on (half a day would pull it
    /// down). Null with fewer than two such days: one day is its own average.
    /// </summary>
    internal static double? AverageDay(IReadOnlyList<DayBucket> days, DateTime now)
    {
        var used = days.Where(d => d.ActiveSec >= 60 && d.Day < now.Date).Select(d => d.ActiveSec).ToList();
        return used.Count >= 2 ? used.Average() : null;
    }
}
