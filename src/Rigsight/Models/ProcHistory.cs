using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;

namespace Rigsight.Models;

/// <summary>A fact about an app: what it is and its figure ("Usual memory", "520 MB"), and what its hover says, if anything.</summary>
public sealed record ProcFact(string Label, string Value, string? Tip = null);

/// <summary>The stretch the box under an app shows its CPU and memory over: since 1.22.0 also days picked (Custom).</summary>
public enum ProcPeriod { Today, Week, Month, Custom }

/// <summary>
/// The box between an app's row and its processes on the Processes page: a slim line of what is on record about it,
/// which opens into its CPU and its memory as two charts beside every fact there is, over today (hour by hour) or the
/// last 7 or 30 days (day by day). A fact with nothing recorded is left out.
/// </summary>
public sealed partial class ProcHistory : ObservableObject, Views.IDayRange
{
    /// <summary>The history is being read: until then the line says only what the running app itself tells.</summary>
    [ObservableProperty] private bool _isLoading = true;

    /// <summary>Every fact, on the line at the top of the box.</summary>
    [ObservableProperty] private IReadOnlyList<ProcFact> _facts = [];

    /// <summary>Its memory in each hour of today (MB), the hour now as it is at this moment; null where not recorded.</summary>
    [ObservableProperty] private IReadOnlyList<double?> _hours = [];

    /// <summary>Its usual memory (MB), the dashed line across the chart; null with too little recorded.</summary>
    [ObservableProperty] private double? _usualMB;

    /// <summary>There is a memory chart to draw: two hours or more today, or a day recorded in the days picked.</summary>
    [ObservableProperty] private bool _hasChart;

    /// <summary>Today, the last 7 days or the last 30 days: both charts and the CPU time fact follow it.</summary>
    [ObservableProperty] private ProcPeriod _period = ProcPeriod.Today;

    /// <summary>CPU time is on record at all (the period switch shows only then).</summary>
    [ObservableProperty] private bool _hasCpu;

    /// <summary>The charts are days as bars (the last 7 or 30 days), not today's hours as a line.</summary>
    [ObservableProperty] private bool _isDays;

    /// <summary>The memory chart as it shows for the period: MB per hour or per day, its hovers (null: the chart's own) and its usual.</summary>
    [ObservableProperty] private IReadOnlyList<double?> _memValues = [];
    [ObservableProperty] private IReadOnlyList<string?>? _memTips;
    [ObservableProperty] private double? _memUsual;
    [ObservableProperty] private string _memCaption = "";

    /// <summary>The CPU chart as it shows for the period: % of the CPU per hour, or CPU time per day (seconds), with its hovers and usual.</summary>
    [ObservableProperty] private IReadOnlyList<double?> _cpuValues = [];
    [ObservableProperty] private IReadOnlyList<string?>? _cpuTips;
    [ObservableProperty] private double? _cpuUsual;
    [ObservableProperty] private string _cpuCaption = "";
    [ObservableProperty] private bool _hasCpuChart;

    /// <summary>Today's figure now at the right of each chart's caption, as the row above says it ("3.1%", "1.1 GB"); empty over days.</summary>
    [ObservableProperty] private string _cpuNow = "";
    [ObservableProperty] private string _memNow = "";
    public bool HasCpuNow => CpuNow.Length > 0;
    public bool HasMemNow => MemNow.Length > 0;
    partial void OnCpuNowChanged(string value) => OnPropertyChanged(nameof(HasCpuNow));
    partial void OnMemNowChanged(string value) => OnPropertyChanged(nameof(HasMemNow));

    /// <summary>The CPU chart's figures: a share of the CPU each hour today, CPU time each day over days.</summary>
    public Controls.HourLineScale CpuScale => IsDays ? Controls.HourLineScale.Seconds : Controls.HourLineScale.Percent;
    partial void OnIsDaysChanged(bool value) => OnPropertyChanged(nameof(CpuScale));

    /// <summary>The words under the charts, at the left and the right ("12 AM" and "Now", or the first day and "Today").</summary>
    [ObservableProperty] private string _axisFrom = "";
    [ObservableProperty] private string _axisTo = "";

    /// <summary>When it was read (the box reads again when opened a while later).</summary>
    public DateTime ReadAt { get; private set; }

    // What it was last filled from, for a period picked afterwards.
    private ProcRow? _row;
    private AppPast? _past;

    partial void OnPeriodChanged(ProcPeriod value)
    {
        if (_row is not null) Fill(_row, _past, DateTime.Now, IsLoading);
    }

    // ── Custom: days picked in the shared calendar (1.22.0) ──

    /// <summary>The first day there is history for (the calendar's first day); null before it's known.</summary>
    public DateTime? FirstDay { get; set; }

    /// <summary>The days picked, both in it (the last 7 days until some are).</summary>
    public DateTime CustomFrom { get; private set; } = DateTime.Today.AddDays(-6);
    public DateTime CustomTo { get; private set; } = DateTime.Today;

    /// <summary>Reads the app over days picked (set by the page with the app's history).</summary>
    public Func<DateTime, DateTime, Task<AppRange?>>? ReadRange { get; set; }

    // The days picked as read, or null while they're being read.
    private AppRange? _range;
    private int _rangeReads;

    /// <summary>Days picked in the calendar: the charts and the period facts follow them, read back as far as the history goes.</summary>
    public void PickCustom(DateTime a, DateTime b)
    {
        (CustomFrom, CustomTo) = a <= b ? (a.Date, b.Date) : (b.Date, a.Date);
        _range = null;
        _ = ReadCustomAsync();
    }

    private async Task ReadCustomAsync()
    {
        int read = ++_rangeReads;
        bool loading = IsLoading;
        var before = Period == ProcPeriod.Custom ? ProcPeriod.Today : Period;
        IsLoading = true;
        if (Period == ProcPeriod.Custom) OnPropertyChanged(nameof(Period));
        else Period = ProcPeriod.Custom;
        AppRange? range = null;
        try { if (ReadRange is { } reader) range = await reader(CustomFrom, CustomTo); }
        catch (Exception ex) { Log.Error("processes", ex); }
        if (read != _rangeReads) return;
        _range = range;
        IsLoading = loading;
        // Nothing could be read (the history couldn't be opened): back to the period it showed.
        if (range is null) Period = before;
        else if (_row is not null) Fill(_row, _past, DateTime.Now, loading);
    }

    /// <summary>The calendar closed without days picked: the segments show the period as it is again.</summary>
    public void KeepPeriod() => OnPropertyChanged(nameof(Period));

    /// <summary>What the running app says by itself, before (or without) anything read from the history.</summary>
    public void Fill(ProcRow row, DateTime now) => Fill(row, null, now, loading: IsLoading);

    public void Fill(ProcRow row, AppPast? past, DateTime now, bool loading = false)
    {
        _row = row;
        _past = past;
        // Days just picked, still being read: the box stays as it is under the loader until they come.
        if (Period == ProcPeriod.Custom && _range is null && past is { Days.Count: > 0 })
        {
            IsLoading = true;
            return;
        }
        var facts = new List<ProcFact>();
        void Add(string label, string value, string? tip = null) => facts.Add(new(label, value, tip));

        if (row.Started > 0) Add(Lang.T("Running since"), Since(TimeUtil.FromUnix(row.Started), now));
        // The middle half of its days, as the Memory page says it; one figure where that's all that's known.
        if (past?.UsualMB is { } usual) Add(Lang.T("Usual memory"), past.MemUsual?.Text ?? Units.Megabytes(usual));
        bool custom = Period == ProcPeriod.Custom && past is { Days.Count: > 0 };
        if (custom) { if (_range is { } r && RangeCpuFact(row, r) is { } rangeFact) facts.Add(rangeFact); }
        else if (past is { Days.Count: > 0 } && CpuFact(row, past, Period, now) is { } cpuFact) facts.Add(cpuFact);
        if (!custom && past is not null && EnergyFact(row, Energy, past.Days.Count > 0 ? Period : ProcPeriod.Today) is { } energyFact) facts.Add(energyFact);
        if (past?.FrontSec is { } front) Add(Lang.T("In front today"), Time(front));
        if (past?.BackSec is { } back) Add(Lang.T("In the background today"), Time(back));
        if (past?.NetBytes is { } net) Add(Lang.T("Internet today"), Units.Data(net));
        if (custom && _range is { } picked) Add(Lang.T($"Crashes, {RangeWords(picked.From, picked.To)}"), picked.Crashes == 0 ? Lang.T("None") : picked.Crashes.ToString(CultureInfo.CurrentCulture));
        else if (past is not null && !custom) Add(Lang.T("Crashes (30 days)"), past.Crashes == 0 ? Lang.T("None") : past.Crashes.ToString(CultureInfo.CurrentCulture));

        // The hour now is what the app holds at this moment, as the list above says it.
        var hours = past?.Hours.ToList() ?? [];
        if (hours.Count > 0 && hours.Any(h => h is not null)) hours[^1] = row.MemMB;

        Facts = Kept.Or(Facts, facts);
        Hours = Kept.Or(Hours, hours);
        UsualMB = past?.UsualMB;
        HasCpu = past is { Days.Count: > 0 };
        var period = HasCpu ? Period : ProcPeriod.Today;
        IsDays = period != ProcPeriod.Today;

        if (period == ProcPeriod.Custom)
        {
            // While the days picked are read, the charts stay as they were under the loader.
            if (_range is { } range) FillRange(range, now);
        }
        else if (!IsDays)
        {
            MemValues = Kept.Or(MemValues, hours);
            MemTips = null;
            MemUsual = past?.UsualMB;
            MemCaption = Lang.T("MEMORY TODAY");
            HasChart = hours.Count(h => h is not null) >= 2;
            var cpu = past?.CpuHours.ToList() ?? [];
            CpuValues = Kept.Or(CpuValues, cpu);
            CpuTips = past is null ? null : HourTips(past, now);
            CpuUsual = past?.CpuUsualPct;
            CpuCaption = Lang.T("CPU TODAY");
            HasCpuChart = cpu.Count(h => h is not null) >= 2;
            AxisFrom = Lang.T("12 AM");
            AxisTo = Lang.T("Now");
            CpuNow = HasCpuChart ? row.CpuText : "";
            MemNow = HasChart ? row.MemText : "";
        }
        else
        {
            int n = period == ProcPeriod.Week ? 7 : AppPast.UsualDays;
            var days = past!.Days.Skip(Math.Max(0, past.Days.Count - n)).ToList();
            MemValues = Kept.Or(MemValues, days.Select(d => d.Recorded ? d.MemMb ?? 0 : (double?)null).ToList());
            MemTips = [.. days.Select(d => DayTip(d, n, d.MemMb is { } mb ? Lang.T($"{DayName(d.Day, n)}, {Units.Megabytes(mb)} on average") : null))];
            MemUsual = past.UsualMB;
            HasChart = days.Any(d => d.Recorded);
            CpuValues = Kept.Or(CpuValues, days.Select(d => d.Recorded ? d.CpuMs / 1000.0 : (double?)null).ToList());
            CpuTips = [.. days.Select(d => DayTip(d, n, d.Ran ? Lang.T($"{DayName(d.Day, n)}, {CpuTime(d.CpuMs / 1000.0)} of CPU time") : null))];
            CpuUsual = past.CpuUsualDayMs is double ms ? ms / 1000.0 : null;
            HasCpuChart = HasChart;
            (CpuNow, MemNow) = ("", "");
            if (period == ProcPeriod.Week)
            {
                MemCaption = Lang.T("MEMORY, LAST 7 DAYS");
                CpuCaption = Lang.T("CPU TIME, LAST 7 DAYS");
            }
            else
            {
                MemCaption = Lang.T("MEMORY, LAST 30 DAYS");
                CpuCaption = Lang.T("CPU TIME, LAST 30 DAYS");
            }
            AxisFrom = DayName(days[0].Day, n);
            AxisTo = Lang.T("Today");
        }
        IsLoading = loading;
        if (past is not null) ReadAt = now;
    }

    /// <summary>
    /// Days picked (Custom): bars by the hour, day or month (see <see cref="AppRange.StepOf"/>), captions naming the days
    /// ("CPU TIME, THU 12 NOV – WED 18 NOV"), the dashed line its usual over the same length of time before.
    /// </summary>
    private void FillRange(AppRange range, DateTime now)
    {
        var points = range.Points;
        MemValues = Kept.Or(MemValues, points.Select(p => p.Recorded ? p.MemMb ?? 0 : (double?)null).ToList());
        MemTips = [.. points.Select(p => PointTip(p, range.Step, p.MemMb is { } mb ? Lang.T($"{PointName(p.Start, range.Step)}, {Units.Megabytes(mb)} on average") : null))];
        MemUsual = range.MemUsualMb;
        HasChart = range.AnyRecorded;
        CpuValues = Kept.Or(CpuValues, points.Select(p => p.Recorded ? p.CpuMs / 1000.0 : (double?)null).ToList());
        CpuTips = [.. points.Select(p => PointTip(p, range.Step, p.CpuMs > 0 ? Lang.T($"{PointName(p.Start, range.Step)}, {CpuTime(p.CpuMs / 1000.0)} of CPU time") : null))];
        CpuUsual = range.CpuUsualMs is double ms ? ms / 1000.0 : null;
        HasCpuChart = HasChart;
        (CpuNow, MemNow) = ("", "");
        string name = RangeCaption(range.From, range.To);
        CpuCaption = Lang.T($"CPU TIME, {name}");
        MemCaption = Lang.T($"MEMORY, {name}");
        (AxisFrom, AxisTo) = Axis(range.From, range.To, range.Step, now);
    }

    /// <summary>
    /// The days picked as a chart's caption names them, in capitals as captions are: "THU 12 NOV – WED 18 NOV", one day
    /// alone "THU 12 NOV", with the year when it isn't this one (as Report.CustomTitle).
    /// </summary>
    internal static string RangeCaption(DateTime from, DateTime to)
    {
        var culture = CultureInfo.CurrentCulture;
        bool years = from.Year != DateTime.Today.Year || to.Year != DateTime.Today.Year;
        string Day(DateTime d) => d.ToString(TimeText.Fit(years ? "ddd d MMM yyyy" : "ddd d MMM"), culture);
        return (from.Date == to.Date ? Day(from) : $"{Day(from)} – {Day(to)}").ToUpper(culture);
    }

    /// <summary>The days picked as a fact says them: "12 Nov to 18 Nov", one day alone "12 Nov" (with the year when it isn't this one).</summary>
    internal static string RangeWords(DateTime from, DateTime to)
    {
        var culture = CultureInfo.CurrentCulture;
        bool years = from.Year != DateTime.Today.Year || to.Year != DateTime.Today.Year;
        string Day(DateTime d) => d.ToString(years ? "d MMM yyyy" : "d MMM", culture);
        return from.Date == to.Date ? Day(from) : Lang.T($"{Day(from)} to {Day(to)}");
    }

    /// <summary>The words under the charts: the first bar's and the last's ("Thu 12 Nov" and "Today", "Nov 2025" and "Today").</summary>
    internal static (string From, string To) Axis(DateTime from, DateTime to, AppRangeStep step, DateTime now)
    {
        var culture = CultureInfo.CurrentCulture;
        string fmt = step == AppRangeStep.Months ? TimeText.Fit("MMM yyyy") : TimeText.Fit("ddd d MMM");
        string first = from.ToString(fmt, culture);
        string last = to.Date == now.Date ? Lang.T("Today") : to.ToString(fmt, culture);
        return (first, last);
    }

    /// <summary>A bar's name in its hover: "Thu 12 Nov, 2 PM" (an hour), "Thu 12 Nov" (a day), "November 2025" (a month).</summary>
    internal static string PointName(DateTime start, AppRangeStep step)
    {
        var culture = CultureInfo.CurrentCulture;
        return step switch
        {
            AppRangeStep.Hours => $"{start.ToString(TimeText.Fit("ddd d MMM"), culture)}, {start.ToString(TimeText.Fit("h tt"), culture)}",
            AppRangeStep.Days => start.ToString(TimeText.Fit("ddd d MMM"), culture),
            _ => TimeText.At(start, "MMMM yyyy", culture),
        };
    }

    /// <summary>A bar's hover: what it did, or that it didn't run, or that nothing was recorded then.</summary>
    private static string PointTip(AppRangePoint point, AppRangeStep step, string? did) =>
        !point.Recorded ? Lang.T($"{PointName(point.Start, step)}, not recorded")
        : did ?? Lang.T($"{PointName(point.Start, step)}, not running");

    /// <summary>"CPU time, 12 Nov to 18 Nov 1h 05m", its hover saying what that means for this PC, as the other periods'.</summary>
    internal static ProcFact? RangeCpuFact(ProcRow row, AppRange range)
    {
        if (!range.AnyRecorded) return null;
        double cpuSec = range.CpuMs / 1000.0;
        string time = CpuTime(cpuSec), name = row.Name, words = RangeWords(range.From, range.To);
        string span = range.From == range.To ? Lang.T($"on {words}") : Lang.T($"from {words}");
        string said = row.Count > 1
            ? Lang.T($"All of {name}'s {row.Count} processes together kept one CPU core busy for {time} {span}.")
            : Lang.T($"{name} kept one CPU core busy for {time} {span}.");
        if (range.RunSec > 0)
        {
            int threads = Environment.ProcessorCount;
            string pct = AppLoad.Pct(cpuSec / (range.RunSec * threads) * 100);
            said += " " + Lang.T($"Your CPU has {threads} threads, so that's {pct}% of what it could do in that time.");
        }
        return new ProcFact(Lang.T($"CPU time, {words}"), time, said);
    }

    /// <summary>
    /// "CPU time today 38m", its hover saying what that means for this PC: "All of Discord's 6 processes together kept
    /// one CPU core busy for 38m today. Your CPU has 16 threads, so that's 0.4% of what it could do in that time."
    /// </summary>
    internal static ProcFact? CpuFact(ProcRow row, AppPast past, ProcPeriod period, DateTime now)
    {
        int n = period switch { ProcPeriod.Week => 7, ProcPeriod.Month => AppPast.UsualDays, _ => 1 };
        var days = past.Days.Skip(Math.Max(0, past.Days.Count - n)).ToList();
        if (!days.Any(d => d.Recorded)) return null;
        double cpuSec = days.Sum(d => d.CpuMs) / 1000.0;
        // Over the time it ran while RigSight recorded, which its CPU time is of too.
        double runSec = days.Sum(d => d.RunSec);
        string time = CpuTime(cpuSec), name = row.Name;
        string label, said;
        switch (period)
        {
            case ProcPeriod.Week:
                label = Lang.T("CPU time, last 7 days");
                said = row.Count > 1
                    ? Lang.T($"All of {name}'s {row.Count} processes together kept one CPU core busy for {time} over the last 7 days.")
                    : Lang.T($"{name} kept one CPU core busy for {time} over the last 7 days.");
                break;
            case ProcPeriod.Month:
                label = Lang.T("CPU time, last 30 days");
                said = row.Count > 1
                    ? Lang.T($"All of {name}'s {row.Count} processes together kept one CPU core busy for {time} over the last 30 days.")
                    : Lang.T($"{name} kept one CPU core busy for {time} over the last 30 days.");
                break;
            default:
                label = Lang.T("CPU time today");
                said = row.Count > 1
                    ? Lang.T($"All of {name}'s {row.Count} processes together kept one CPU core busy for {time} today.")
                    : Lang.T($"{name} kept one CPU core busy for {time} today.");
                break;
        }
        if (runSec > 0)
        {
            int threads = past.Threads;
            string pct = AppLoad.Pct(cpuSec / (runSec * threads) * 100);
            said += " " + Lang.T($"Your CPU has {threads} threads, so that's {pct}% of what it could do in that time.");
        }
        return new ProcFact(label, time, said);
    }

    /// <summary>
    /// The CPU and GPU energy in the minutes the app was in front or busy, today, the last 7 days and the last 30 days
    /// (1.18.0, issue #12's power per app): set before <see cref="Fill(ProcRow, AppPast?, DateTime, bool)"/>.
    /// </summary>
    public (double? Today, double? Week, double? Month) Energy { get; set; }

    /// <summary>"CPU + GPU energy while busy today 0.21 kWh": the PC's energy in its busy minutes, never the app's own watts.</summary>
    internal static ProcFact? EnergyFact(ProcRow row, (double? Today, double? Week, double? Month) energy, ProcPeriod period)
    {
        var (wh, label, when) = period switch
        {
            ProcPeriod.Week => (energy.Week, Lang.T("CPU + GPU energy while busy, last 7 days"), Lang.T("over the last 7 days")),
            ProcPeriod.Month => (energy.Month, Lang.T("CPU + GPU energy while busy, last 30 days"), Lang.T("over the last 30 days")),
            _ => (energy.Today, Lang.T("CPU + GPU energy while busy today"), Lang.T("today")),
        };
        if (wh is not double w || w < 1) return null;
        string kwh = Core.Reports.PowerReport.Kwh(w);
        return new ProcFact(label, kwh,
            Lang.T($"The CPU and GPU used {kwh} {when} in the minutes {row.Name} was in front or busy. Other apps busy at the same time count too, so apps don't add up to the total."));
    }

    /// <summary>A CPU time as the page says it: "38m", "2h 05m", "40s", or "Under a second".</summary>
    internal static string CpuTime(double seconds) => seconds < 1 ? Lang.T("Under a second") : Units.Duration(seconds);

    /// <summary>"2 PM to 3 PM, 3.4% of the CPU on average, 2m 02s of CPU time"; the hour now runs to now.</summary>
    internal static IReadOnlyList<string?> HourTips(AppPast past, DateTime now)
    {
        var culture = CultureInfo.CurrentCulture;
        string fmt = TimeText.Fit("h tt");
        var tips = new string?[past.CpuHourMs.Length];
        for (int h = 0; h < tips.Length; h++)
        {
            string from = now.Date.AddHours(h).ToString(fmt, culture), to = now.Date.AddHours(h + 1).ToString(fmt, culture);
            bool last = h == tips.Length - 1;
            tips[h] = past.CpuHourMs[h] switch
            {
                null => last ? Lang.T($"{from} to now, not recorded") : Lang.T($"{from} to {to}, not recorded"),
                0 => last ? Lang.T($"{from} to now, no CPU time") : Lang.T($"{from} to {to}, no CPU time"),
                long ms => last
                    ? Lang.T($"{from} to now, {AppLoad.Pct(past.CpuPct(h) ?? 0)}% of the CPU on average, {Units.DurationExact(ms / 1000.0)} of CPU time")
                    : Lang.T($"{from} to {to}, {AppLoad.Pct(past.CpuPct(h) ?? 0)}% of the CPU on average, {Units.DurationExact(ms / 1000.0)} of CPU time"),
            };
        }
        return tips;
    }

    /// <summary>A day's hover: what it did, or that it didn't run, or that nothing was recorded that day.</summary>
    private static string DayTip(AppPastDay day, int days, string? did) =>
        !day.Recorded ? Lang.T($"{DayName(day.Day, days)}, not recorded")
        : did ?? Lang.T($"{DayName(day.Day, days)}, not running");

    /// <summary>"Fri" over a week; "Fri 14 Nov" over a month, where a weekday comes round more than once.</summary>
    private static string DayName(DateTime day, int days) =>
        day.ToString(TimeText.Fit(days <= 7 ? "ddd" : "ddd d MMM"), CultureInfo.CurrentCulture);

    /// <summary>"9:10 AM" today, "Yesterday, 7:40 PM", or with its date.</summary>
    internal static string Since(DateTime started, DateTime now)
    {
        var culture = CultureInfo.CurrentCulture;
        int days = (int)(now.Date - started.Date).TotalDays;
        return days <= 0 ? started.ToString(TimeText.Fit("h:mm tt"), culture)
            : days == 1 ? Lang.T($"Yesterday, {started.ToString(TimeText.Fit("h:mm tt"), culture)}")
            : started.ToString(TimeText.Fit("d MMM, h:mm tt"), culture);
    }

    /// <summary>"5h 40m", "22m", "Under a minute"; "None" for no time at all.</summary>
    internal static string Time(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 1) return Lang.T("None");
        int minutes = (int)(seconds / 60);
        return minutes < 1 ? Lang.T("Under a minute") : Units.Duration(minutes * 60);
    }
}
