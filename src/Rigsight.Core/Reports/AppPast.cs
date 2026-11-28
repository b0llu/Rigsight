using Rigsight.Core.Data;

namespace Rigsight.Core.Reports;

/// <summary>
/// What is on record about one running app, for the box under its row on the Processes page. Anything not recorded
/// stays null (today's hour-by-hour memory is kept only for apps that had a window, held 150 MB or used 2% of the
/// processor; its usual counts every second it ran), and the page leaves that fact out.
/// </summary>
public sealed class AppPast
{
    /// <summary>How many days back the usual is taken over.</summary>
    public const int UsualDays = 30;

    /// <summary>The fewest days with memory on record for there to be a usual.</summary>
    public const int MinUsualDays = 3;

    /// <summary>Its memory in each hour of today (MB), from midnight to the hour now; null where it wasn't recorded.</summary>
    public double?[] Hours { get; init; } = [];

    /// <summary>
    /// Its usual memory over the days before today as a spread, the middle half of its days over every second it ran (see
    /// <see cref="MemoryUsual"/>); null with too few of them.
    /// </summary>
    public MemoryUsual? MemUsual { get; init; }

    /// <summary>Its usual memory as one figure (MB), the middle of <see cref="MemUsual"/>; null with too few days.</summary>
    public double? UsualMB { get => _usualMB ?? MemUsual?.Middle; init => _usualMB = value; }
    private readonly double? _usualMB;

    /// <summary>Seconds in front today (in use, or with the user away), and open behind something else or minimized.</summary>
    public double? FrontSec { get; init; }
    public double? BackSec { get; init; }

    /// <summary>Bytes it moved over the network today.</summary>
    public long? NetBytes { get; init; }

    /// <summary>Its crashes and hangs over the last <see cref="UsualDays"/> days.</summary>
    public int Crashes { get; init; }

    public bool HasChart => Hours.Count(h => h is not null) >= 2;

    /// <summary>
    /// Its CPU time in each hour of today (ms), from midnight to the hour now; null where nothing was recorded (the PC
    /// off, or before CPU time was kept). Empty when there is no CPU time on record at all.
    /// </summary>
    public long?[] CpuHourMs { get; init; } = [];

    /// <summary>Seconds gone in the hour now, which its CPU share is taken over.</summary>
    public double HourNowSec { get; init; } = 3600;

    /// <summary>The PC's logical processors (threads): the whole CPU, as a share of which an app's CPU time is said.</summary>
    public int Threads { get; init; } = Environment.ProcessorCount;

    /// <summary>Its usual CPU share in an hour it used the CPU (%), over the days before today; null with too few of them.</summary>
    public double? CpuUsualPct { get; init; }

    /// <summary>
    /// The <see cref="UsualDays"/> days up to today, oldest first: its CPU time and its memory each day. Empty when there
    /// is no CPU time on record at all.
    /// </summary>
    public IReadOnlyList<AppPastDay> Days { get; init; } = [];

    /// <summary>Its usual CPU time on a day it ran (ms), over the days before today; null with too few of them.</summary>
    public double? CpuUsualDayMs { get; init; }

    /// <summary>Its CPU share in an hour of today (%): the hour now over the part of it gone so far.</summary>
    public double? CpuPct(int hour) => hour >= 0 && hour < CpuHourMs.Length && CpuHourMs[hour] is long ms
        ? ms / 1000.0 / ((hour == CpuHourMs.Length - 1 ? HourNowSec : 3600) * Threads) * 100 : null;

    /// <summary>Its CPU share (%) in each hour of today, for the chart; null where nothing was recorded.</summary>
    public double?[] CpuHours => [.. Enumerable.Range(0, CpuHourMs.Length).Select(CpuPct)];

    public static AppPast Read(RigsightDb db, string exe, DateTime now)
    {
        var today = now.Date;
        long from = TimeUtil.ToUnix(today), to = TimeUtil.ToUnix(today.AddDays(1));
        int crashes = db.GetCrashes(TimeUtil.ToUnix(today.AddDays(-UsualDays)), to).Count(c => c.AppExe.Equals(exe, StringComparison.OrdinalIgnoreCase));
        if (db.LoadApps().FirstOrDefault(a => a.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)) is not { } app) return new AppPast { Crashes = crashes };
        var cpu = ReadCpu(db, app.Id, now);

        var hours = new double?[now.Hour + 1];
        double front = 0, back = 0;
        bool any = false;
        foreach (var h in db.GetAppHoursOf(app.Id, from, to))
        {
            any = true;
            front += h.FgSec + h.IdleSec;
            back += h.BgSec + h.MinSec;
            int at = TimeUtil.FromUnix(h.Ts).Hour;
            if (h.MemN > 0 && at < hours.Length) hours[at] = h.MemSum / h.MemN;
        }

        // The same usual as the Memory page's (MemoryPast.SpreadOf), for this one app.
        var usual = MemoryUsual.ForApp(db, app.Id, now);

        long net = 0;
        bool anyNet = false;
        foreach (var u in db.GetNetAppDays(from, to))
        {
            if (u.App != app.Id) continue;
            anyNet = true;
            net += u.Down + u.Up;
        }

        return new AppPast
        {
            Hours = hours,
            MemUsual = usual,
            FrontSec = any ? front : null,
            BackSec = any ? back : null,
            NetBytes = anyNet ? net : null,
            Crashes = crashes,
            CpuHourMs = cpu.Hours,
            HourNowSec = cpu.HourNowSec,
            CpuUsualPct = cpu.UsualPct,
            Days = cpu.Days,
            CpuUsualDayMs = cpu.UsualDayMs,
        };
    }

    private sealed record Cpu(long?[] Hours, double HourNowSec, double? UsualPct, IReadOnlyList<AppPastDay> Days, double? UsualDayMs);

    /// <summary>
    /// Its CPU time from the tables that keep it: an hour or a day is recorded when every process's total has a row
    /// there (the agent was running), and one the app has no row in is one it didn't use the CPU in (an hour) or didn't
    /// run at all (a day).
    /// </summary>
    private static Cpu ReadCpu(RigsightDb db, long app, DateTime now)
    {
        var today = now.Date;
        double hourNow = Math.Max(60, (now - now.Date.AddHours(now.Hour)).TotalSeconds);
        long first = TimeUtil.ToUnix(today.AddDays(-(UsualDays - 1))), from = TimeUtil.ToUnix(today), to = TimeUtil.ToUnix(today.AddDays(1));
        var allDays = db.GetAppCpuDays(AppCpuUse.AllApps, TimeUtil.ToUnix(today.AddDays(-UsualDays)), to).Select(d => d.Ts).ToHashSet();
        if (allDays.Count == 0) return new([], hourNow, null, [], null);

        // Today, hour by hour.
        var hours = new long?[now.Hour + 1];
        foreach (var h in db.GetAppCpuHours(AppCpuUse.AllApps, from, to))
            if (TimeUtil.FromUnix(h.Ts).Hour is var at && at < hours.Length) hours[at] = 0;
        foreach (var h in db.GetAppCpuHours(app, from, to))
            if (TimeUtil.FromUnix(h.Ts).Hour is var at && at < hours.Length) hours[at] = h.CpuMs;

        // The usual hour: the hours it used the CPU in, over the days before today.
        int threads = Environment.ProcessorCount;
        long usualMs = 0, usualHours = 0;
        var usualDays = new HashSet<DateTime>();
        foreach (var h in db.GetAppCpuHours(app, TimeUtil.ToUnix(today.AddDays(-UsualDays)), from))
        {
            if (h.CpuMs <= 0) continue;
            usualMs += h.CpuMs;
            usualHours++;
            usualDays.Add(TimeUtil.FromUnix(h.Ts).Date);
        }
        double? usualPct = usualDays.Count >= MinUsualDays ? usualMs / 1000.0 / (usualHours * 3600.0 * threads) * 100 : null;

        // Day by day, the last UsualDays up to today.
        var mine = db.GetAppCpuDays(app, first, to).ToDictionary(d => d.Ts);
        var days = new List<AppPastDay>(UsualDays);
        for (var day = today.AddDays(-(UsualDays - 1)); day <= today; day = day.AddDays(1))
        {
            long ts = TimeUtil.ToUnix(day);
            days.Add(mine.TryGetValue(ts, out var d)
                ? new AppPastDay(day, true, d.CpuMs, d.MemMbAvg, d.RunSec, d.GpuMs)
                : new AppPastDay(day, allDays.Contains(ts), 0, null, 0, 0));
        }
        var before = db.GetAppCpuDays(app, TimeUtil.ToUnix(today.AddDays(-UsualDays)), from).Where(d => d.RunSec > 0).ToList();
        double? usualDay = before.Count >= MinUsualDays ? before.Average(d => (double)d.CpuMs) : null;
        return new(hours, hourNow, usualPct, days, usualDay);
    }
}

/// <summary>
/// One day of an app on the Processes page's bars: whether the day was recorded at all, and if so its CPU time (ms), its
/// average memory while it ran (MB; null when it didn't run), how long it ran (seconds) and its GPU time (ms).
/// </summary>
public sealed record AppPastDay(DateTime Day, bool Recorded, long CpuMs, double? MemMb, double RunSec, long GpuMs)
{
    public bool Ran => RunSec > 0 || CpuMs > 0;
}
