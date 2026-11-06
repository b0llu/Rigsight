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
    public const double TotalsFromHourly = 28;

    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(
        nameof(Days), typeof(IReadOnlyList<DayBucket>), typeof(DailyBars), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, e) => ((DailyBars)d).DaysChanged(e.OldValue as IReadOnlyList<DayBucket>)));

    public IReadOnlyList<DayBucket>? Days { get => (IReadOnlyList<DayBucket>?)GetValue(DaysProperty); set => SetValue(DaysProperty, value); }

    public static readonly DependencyProperty OnlyProperty = DependencyProperty.Register(
        nameof(Only), typeof(IReadOnlyList<AppCategory>), typeof(DailyBars), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>The kinds picked in Where the time went (null: all): the rest step back, and each bar's total is theirs.</summary>
    public IReadOnlyList<AppCategory>? Only { get => (IReadOnlyList<AppCategory>?)GetValue(OnlyProperty); set => SetValue(OnlyProperty, value); }

    public static readonly DependencyProperty UsualProperty = DependencyProperty.Register(
        nameof(Usual), typeof(IReadOnlyList<double>), typeof(DailyBars), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>
    /// Per hour: the usual active seconds in each hour on that weekday (a dashed line across the hours, as the average
    /// day's line on a week), named by <see cref="UsualName"/>. Null for none.
    /// </summary>
    public IReadOnlyList<double>? Usual { get => (IReadOnlyList<double>?)GetValue(UsualProperty); set => SetValue(UsualProperty, value); }

    public static readonly DependencyProperty UsualNameProperty = DependencyProperty.Register(
        nameof(UsualName), typeof(string), typeof(DailyBars), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>What the usual hours are ("Usual Thursday").</summary>
    public string? UsualName { get => (string?)GetValue(UsualNameProperty); set => SetValue(UsualNameProperty, value); }

    public static readonly DependencyProperty UsualDayProperty = DependencyProperty.Register(
        nameof(UsualDay), typeof(DayOfWeek?), typeof(DailyBars), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>The weekday <see cref="Usual"/> is the usual of: the hover names it ("than your usual Friday").</summary>
    public DayOfWeek? UsualDay { get => (DayOfWeek?)GetValue(UsualDayProperty); set => SetValue(UsualDayProperty, value); }

    public static readonly DependencyProperty ShowTempsProperty = DependencyProperty.Register(
        nameof(ShowTemps), typeof(bool), typeof(DailyBars), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>The hover says each bar's CPU and GPU peaks (off where a temperature graph of its own is right under it).</summary>
    public bool ShowTemps { get => (bool)GetValue(ShowTempsProperty); set => SetValue(ShowTempsProperty, value); }

    // ── From one period to the next (issue #11) ──

    /// <summary>What was drawn before the bars changed, each bar's seconds by kind, and the scale they were on.</summary>
    private List<Dictionary<AppCategory, double>>? _was;
    private double _wasMaxHours;
    private double _maxHoursDrawn;
    private readonly ChartTween _tween;

    public DailyBars() => _tween = new ChartTween(this);

    /// <summary>
    /// New bars: as many as before, at the same spacing (another week, another day), each grows or shrinks from where
    /// it was; otherwise (a day's hours to a week's days) the new ones fade in. At once when there were none, or with
    /// Windows' animations off.
    /// </summary>
    private void DaysChanged(IReadOnlyList<DayBucket>? old)
    {
        var now = Days;
        bool same = old is { Count: > 0 } && now is { Count: > 0 } && old.Count == now.Count && Spacing(old) == Spacing(now) && !ReferenceEquals(old, now);
        _was = same ? [.. old!.Select(d => new Dictionary<AppCategory, double>(d.ActiveByCategory))] : null;
        _wasMaxHours = _maxHoursDrawn;
        _tween.Start(same ? ChartTween.Kind.Move : old is { Count: > 0 } ? ChartTween.Kind.Fade : ChartTween.Kind.None);
    }

    /// <summary>Hours, days or months apart (0 with one bar).</summary>
    private static TimeSpan Spacing(IReadOnlyList<DayBucket> days) => days.Count >= 2 ? days[1].Day - days[0].Day : TimeSpan.Zero;

    /// <summary>Bar <paramref name="i"/>'s seconds by kind as drawn now: on their way from the old ones while they move.</summary>
    private IEnumerable<KeyValuePair<AppCategory, double>> Shown(DayBucket d, int i)
    {
        if (_was is not { } was || i >= was.Count || _tween.Moving is not double e) return d.ActiveByCategory;
        var old = was[i];
        return old.Keys.Union(d.ActiveByCategory.Keys).Select(c =>
            new KeyValuePair<AppCategory, double>(c, old.GetValueOrDefault(c) + (d.ActiveByCategory.GetValueOrDefault(c) - old.GetValueOrDefault(c)) * e));
    }

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
        // Per hour, always the whole hour: 0 to 60 minutes, so a full hour reaches the top and a quiet one looks it
        // (issue #9: scaled to the busiest hour, ten minutes looked like a full one). Up to an hour otherwise: minutes.
        // Else hours, about four gridlines at a round step (1–6 hours for days, tens to hundreds for a year's months).
        bool minutes = hourly || maxSec <= 3600;
        double unitSec = minutes ? 60 : 3600;
        double maxUnits = hourly ? 60 : Math.Max(1, Math.Ceiling(maxSec / unitSec));
        double step = hourly ? 15 : minutes
            ? new double[] { 5, 10, 15, 20, 30 }.FirstOrDefault(x => maxUnits / x <= 4, 30)
            : new double[] { 1, 2, 4, 6, 10, 20, 25, 50, 100, 200, 250, 500 }.FirstOrDefault(x => maxUnits / x <= 5, 1000);
        maxUnits = Math.Max(step, Math.Ceiling(maxUnits / step) * step);
        double maxHours = maxUnits * unitSec / 3600;
        _maxHoursDrawn = maxHours;
        // The bars move on the scale they move from: it changes with them.
        if (_tween.Moving is double moving && _wasMaxHours > 0) maxHours = _wasMaxHours + (maxHours - _wasMaxHours) * moving;
        dc.PushOpacity(_tween.Opacity);

        var gridPen = new Pen(ChartPaint.Grid, 1) { DashStyle = new DashStyle([3, 4], 0) };
        for (double u = 0; u <= maxUnits; u += step)
        {
            double y = Math.Round(plot.Bottom - plot.Height * u / maxUnits) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            ChartPaint.Text(dc, this, minutes ? $"{u:0}m" : $"{u:0}h", new Point(plot.Left - 8, y), 11, ChartPaint.Label, ChartPaint.Align.Right);
        }

        double slot = plot.Width / days.Count;
        double barW = Math.Max(3, Math.Min(42, slot * 0.62));
        // An hour's total is short ("38m"): it fits over a narrower bar than a day's ("12h 45m").
        bool totals = slot >= (hourly ? TotalsFromHourly : TotalsFrom);
        // A year's report has one bar per month.
        bool monthly = days.Count >= 2 && days[1].Day == days[0].Day.AddMonths(1);
        var now = DateTime.Now;
        var current = monthly ? new DateTime(now.Year, now.Month, 1) : hourly ? now.Date.AddHours(now.Hour) : now.Date;
        int labelEvery = hourly ? 3 : days.Count <= (monthly ? 12 : 7) ? 1 : days.Count <= 16 ? 2 : 3;

        // What's drawn where (each bar, each total): the average day's label goes where it covers none of it.
        var taken = new List<Rect>();
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
            var shown = Shown(d, i).ToList();
            foreach (var (cat, sec) in shown.OrderBy(kv => only is null || only.Contains(kv.Key) ? 0 : 1).ThenByDescending(kv => kv.Value))
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
            if (y < plot.Bottom) taken.Add(new Rect(cx - barW / 2, y, barW, plot.Bottom - y));
            // Its total over it (the picked kind's alone, when one is).
            double shownSec = only is null ? (_tween.Moving is null ? d.ActiveSec : shown.Sum(kv => kv.Value)) : shown.Where(kv => only.Contains(kv.Key)).Sum(kv => kv.Value);
            if (totals && shownSec >= (hourly ? 600 : 60))
            {
                string total = Units.Duration(shownSec);
                ChartPaint.Text(dc, this, total, new Point(cx, y - 9), 11, i == _hover || only is not null ? ChartPaint.TextBrush : ChartPaint.Muted, ChartPaint.Align.Center);
                double tw = ChartPaint.Format(this, total, 11, ChartPaint.Muted).Width;
                taken.Add(new Rect(cx - tw / 2 - 2, y - 17, tw + 4, 16));
            }

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
            // At the right over its line, unless a bar or a total is there (Thursday's "12h 45m" sat under it, 2026-11-06):
            // then at the left, then under the line at either end.
            double pw = ft.Width + 12, ph = ft.Height + 4;
            var spots = new[]
            {
                new Rect(plot.Right - pw - 4, y - ph - 2, pw, ph), new Rect(plot.Left + 4, y - ph - 2, pw, ph),
                new Rect(plot.Right - pw - 4, y + 3, pw, ph), new Rect(plot.Left + 4, y + 3, pw, ph),
            };
            var pill = spots.FirstOrDefault(r => !taken.Any(t => t.IntersectsWith(r)), spots[0]);
            dc.DrawRoundedRectangle(ChartPaint.Track, null, pill, 5, 5);
            ChartPaint.Text(dc, this, text, new Point(pill.Left + 6, pill.Top + pill.Height / 2), 11, ChartPaint.TextBrush, ChartPaint.Align.Left);
        }

        // Per hour: the usual for that weekday, hour by hour, as a dashed line stepping across the hours.
        if (Only is null && hourly && Usual is { Count: > 0 } usual)
        {
            var usualPen = new Pen(ChartPaint.Muted, 1.2) { DashStyle = new DashStyle([5, 4], 0) };
            usualPen.Freeze();
            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                for (int i = 0; i < days.Count; i++)
                {
                    double sec = Math.Min(3600, usual[days[i].Day.Hour % usual.Count]);
                    double y = Math.Round(plot.Bottom - plot.Height * sec / 3600 / maxHours) + 0.5;
                    var a = new Point(plot.Left + slot * i, y);
                    if (i == 0) g.BeginFigure(a, false, false); else g.LineTo(a, true, false);
                    g.LineTo(new Point(plot.Left + slot * (i + 1), y), true, false);
                }
            }
            line.Freeze();
            dc.DrawGeometry(null, usualPen, line);
            if (UsualName is { Length: > 0 } name)
            {
                // Its name over the line where the line is highest and nothing is drawn.
                var ft = ChartPaint.Format(this, name, 11, ChartPaint.TextBrush);
                double pw = ft.Width + 12, ph = ft.Height + 4;
                var spots = Enumerable.Range(0, days.Count)
                    .Select(i => (i, y: plot.Bottom - plot.Height * Math.Min(3600, usual[days[i].Day.Hour % usual.Count]) / 3600 / maxHours))
                    .Where(p => p.y < plot.Bottom - 4)
                    .Select(p => new Rect(Math.Clamp(plot.Left + slot * p.i, plot.Left, plot.Right - pw), p.y - ph - 3, pw, ph))
                    .Where(r => r.Top >= 0).ToList();
                if (spots.FirstOrDefault(r => !taken.Any(t => t.IntersectsWith(r))) is { Width: > 0 } pill)
                {
                    dc.DrawRoundedRectangle(ChartPaint.Track, null, pill, 5, 5);
                    ChartPaint.Text(dc, this, name, new Point(pill.Left + 6, pill.Top + pill.Height / 2), 11, ChartPaint.TextBrush, ChartPaint.Align.Left);
                }
            }
        }
        dc.Pop();

        if (_hover >= 0 && _hover < days.Count)
            DrawHover(dc, days, _hover, plot, slot, barW, hourly, monthly, now);
    }

    /// <summary>
    /// The bar's box (2026-11-06, mock-up ReK33EVbdKbsoarY7jwYXQ): the time and its total; how it compares with a usual
    /// that's named ("16m more than your usual Friday (33m)"); a thin bar of the split; each kind with a dot, its time
    /// and its share in columns (System, Other and anything under a minute as Everything else, as in Where the time
    /// went); then the apps longest in front with their icons. Its bottom at the graph's when it's taller than the graph
    /// (it rises over the card's heading rather than run under the card below).
    /// </summary>
    private void DrawHover(DrawingContext dc, IReadOnlyList<DayBucket> days, int i, Rect plot, double slot, double barW, bool hourly, bool monthly, DateTime now)
    {
        var d = days[i];
        DateTime end = hourly ? d.Day.AddHours(1) : monthly ? d.Day.AddMonths(1) : d.Day.AddDays(1);
        string title = hourly ? $"{TimeText.At(d.Day, "h tt")} – {TimeText.At(end, "h tt")}" : d.Day.ToString(monthly ? "MMMM yyyy" : "dddd, d MMM");
        string? total = d.ActiveSec >= 60 ? Units.Duration(d.ActiveSec) : null;
        string? note = d.ActiveSec >= 60 ? Against(days, d, hourly, monthly, now, end) : d.Day > now ? Lang.T("Still to come") : Lang.T("No activity");
        var kinds = Kinds(d);
        var front = d.Front.Where(f => f.Sec >= 60).Take(3).ToList();
        var temps = new List<(string Text, Brush Brush)>();
        if (ShowTemps && d.CpuTempMax is double c) temps.Add((Lang.T($"CPU peak {Units.TempShort(c)}"), ChartPaint.Brush(ChartPaint.Cpu)));
        if (ShowTemps && d.GpuTempMax is double g) temps.Add((Lang.T($"GPU peak {Units.TempShort(g)}"), ChartPaint.Brush(ChartPaint.Gpu)));

        const double pad = 12, rowH = 20, appH = 22, icon = 16, timeW = 58, shareW = 40;
        var head = ChartPaint.Format(this, title, 13, ChartPaint.TextBrush, bold: true);
        var sum = total is null ? null : ChartPaint.Format(this, total, 15, ChartPaint.TextBrush, bold: true);
        var noteFt = note is null ? null : ChartPaint.Format(this, note, 12, ChartPaint.Muted);
        string heading = Lang.T("LONGEST IN FRONT");
        double inner = Math.Max(260, Math.Max(head.Width + 16 + (sum?.Width ?? 0), noteFt?.Width ?? 0));
        foreach (var (name, _, _) in kinds) inner = Math.Max(inner, 14 + ChartPaint.Format(this, name, 12, ChartPaint.TextBrush).Width + 8 + timeW + shareW);
        foreach (var f in front) inner = Math.Max(inner, icon + 8 + ChartPaint.Format(this, f.Name, 12, ChartPaint.TextBrush).Width + 8 + timeW);
        inner = Math.Min(inner, 360);
        double hBox = pad * 2 + Math.Max(head.Height, sum?.Height ?? 0) + (noteFt is null ? 0 : noteFt.Height + 3)
            + (kinds.Count == 0 ? 0 : 16 + kinds.Count * rowH) + (front.Count == 0 ? 0 : 10 + rowH + front.Count * appH) + (temps.Count == 0 ? 0 : 6 + temps.Count * rowH);
        double wBox = inner + pad * 2;
        double cx = plot.Left + slot * (i + 0.5);
        double x = cx + barW / 2 + 10 + wBox > ActualWidth ? cx - barW / 2 - 10 - wBox : cx + barW / 2 + 10;
        var box = new Rect(Math.Round(Math.Max(0, x)) + 0.5, Math.Round(Math.Min(plot.Top, ActualHeight - hBox)) + 0.5, wBox, hBox);
        dc.DrawRoundedRectangle(ChartPaint.TooltipBg, ChartPaint.TooltipBorder, box, 8, 8);

        double lx = box.Left + pad, right = box.Right - pad, y = box.Top + pad;
        double lineH = Math.Max(head.Height, sum?.Height ?? 0);
        dc.DrawText(head, new Point(lx, y + (lineH - head.Height) / 2));
        if (sum is not null) dc.DrawText(sum, new Point(right - sum.Width, y + (lineH - sum.Height) / 2));
        y += lineH;
        if (noteFt is not null)
        {
            dc.DrawText(noteFt, new Point(lx, y + 3));
            y += noteFt.Height + 3;
        }
        if (kinds.Count > 0)
        {
            // The split: one thin bar in the graph's colours, a hairline between the kinds.
            y += 8;
            double all = kinds.Sum(k => k.Sec), bx = lx;
            dc.PushClip(new RectangleGeometry(new Rect(lx, y, inner, 6), 3, 3));
            foreach (var (_, sec, brush) in kinds)
            {
                double bw = inner * sec / all;
                dc.DrawRectangle(brush, null, new Rect(bx, y, Math.Max(0, bw - 2), 6));
                bx += bw;
            }
            dc.Pop();
            y += 8;
            foreach (var (name, sec, brush) in kinds)
            {
                double my = y + rowH / 2;
                dc.DrawEllipse(brush, null, new Point(lx + 4, my), 4, 4);
                ChartPaint.Text(dc, this, name, new Point(lx + 14, my), 12, ChartPaint.TextBrush);
                ChartPaint.Text(dc, this, Units.Duration(sec), new Point(right - shareW, my), 12, ChartPaint.TextBrush, ChartPaint.Align.Right, bold: true);
                ChartPaint.Text(dc, this, $"{Math.Round(sec / all * 100):0}%", new Point(right, my), 12, ChartPaint.Muted, ChartPaint.Align.Right);
                y += rowH;
            }
        }
        if (front.Count > 0)
        {
            y += 10;
            ChartPaint.Text(dc, this, heading, new Point(lx, y + rowH / 2), 11, ChartPaint.Muted, bold: true);
            y += rowH;
            foreach (var f in front)
            {
                double my = y + appH / 2;
                if (Services.IconCache.Get(f.Path) is { } img) dc.DrawImage(img, new Rect(lx, my - icon / 2, icon, icon));
                else
                {
                    dc.DrawRoundedRectangle(ChartPaint.Track, null, new Rect(lx, my - icon / 2, icon, icon), 4, 4);
                    ChartPaint.Text(dc, this, f.Name.Length > 0 ? f.Name[..1].ToUpperInvariant() : "?", new Point(lx + icon / 2, my), 10, ChartPaint.Muted, ChartPaint.Align.Center, bold: true);
                }
                var name = ChartPaint.Format(this, f.Name, 12, ChartPaint.TextBrush);
                name.MaxTextWidth = Math.Max(10, inner - icon - 8 - timeW);
                name.MaxLineCount = 1;
                name.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(name, new Point(lx + icon + 8, my - name.Height / 2));
                ChartPaint.Text(dc, this, Units.Duration(f.Sec), new Point(right, my), 12, ChartPaint.Muted, ChartPaint.Align.Right);
                y += appH;
            }
        }
        if (temps.Count > 0)
        {
            y += 6;
            foreach (var (text, brush) in temps)
            {
                ChartPaint.Text(dc, this, text, new Point(lx, y + rowH / 2), 12, brush);
                y += rowH;
            }
        }
    }

    /// <summary>
    /// The bar's kinds, biggest first, as Where the time went has them: System, Other and anything under a minute (or
    /// under a fiftieth of the bar) together as Everything else, at the end.
    /// </summary>
    internal static List<(string Name, double Sec, Brush Brush)> Kinds(DayBucket d)
    {
        double all = d.ActiveByCategory.Values.Sum();
        var shown = new List<(string, double, Brush)>();
        double rest = 0;
        foreach (var (cat, sec) in d.ActiveByCategory.OrderByDescending(kv => kv.Value))
        {
            if (cat is AppCategory.Other or AppCategory.System || sec < 60 || sec < all / 50) rest += sec;
            else shown.Add((AppCatalog.Label(cat), sec, Fill(cat)));
        }
        if (rest >= 60) shown.Add((Lang.T("Everything else"), rest, Fill(AppCategory.System)));
        return shown;
    }

    /// <summary>
    /// How the bar compares, with what it's set against named: an hour with the usual for that weekday ("16m more than
    /// your usual Friday (33m)"), a day with the average day. Only once it's over (an hour or a day still going on
    /// would always be "less"); none for a year's months.
    /// </summary>
    private string? Against(IReadOnlyList<DayBucket> days, DayBucket d, bool hourly, bool monthly, DateTime now, DateTime end)
    {
        if (end > now || monthly) return null;
        if (hourly)
        {
            if (Usual is not { Count: > 0 } usual || UsualDay is not { } weekday) return null;
            double was = Math.Min(3600, usual[d.Day.Hour % usual.Count]);
            if (was < 60) return null;
            string day = Core.Reports.PeriodWords.DayName(weekday), by = Units.Duration(Math.Abs(d.ActiveSec - was)), usualText = Units.Duration(was);
            return Math.Abs(d.ActiveSec - was) < 3 * 60 ? Lang.T($"About the same as your usual {day} ({usualText})")
                : d.ActiveSec > was ? Lang.T($"{by} more than your usual {day} ({usualText})") : Lang.T($"{by} less than your usual {day} ({usualText})");
        }
        if (AverageDay(days, now) is not double avg) return null;
        string diff = Units.Duration(Math.Abs(d.ActiveSec - avg)), avgText = Units.Duration(avg);
        return Math.Abs(d.ActiveSec - avg) < 15 * 60 ? Lang.T($"About the same as the average day ({avgText})")
            : d.ActiveSec > avg ? Lang.T($"{diff} more than the average day ({avgText})") : Lang.T($"{diff} less than the average day ({avgText})");
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
