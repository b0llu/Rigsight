using Rigsight.Core.Data;

namespace Rigsight.Core.Stability;

/// <summary>One thing that stood out in the minutes before a crash, as said above its lanes. <see cref="Warn"/>: in the warning colour.</summary>
public sealed record StoodOut(string Text, bool Warn, bool IsCalm = false);

/// <summary>
/// What stood out in the minutes before a crash, worked out from what each minute recorded: a part getting hot and the
/// app doing the work then (with how much, where that was recorded), a fan speeding up, Windows nearly out of memory,
/// the drives busy, the battery nearly empty, the connection dropping. Then the parts that were recorded and calm, so
/// "nothing unusual" is said for what was looked at and nothing more. Never says what wasn't recorded.
/// </summary>
public static class CaseMinutes
{
    /// <summary>How much a part must have warmed in the minutes before (°C) for it to be said.</summary>
    public const double Climb = 10;

    /// <summary>The last minutes looked at for a climb, and how far before them the part's earlier level is taken from.</summary>
    public const int ClimbMinutes = 6, EarlierMinutes = 10;

    /// <summary>The least share (%) an app must average over a climb to be named for it, and how far ahead of the next it must be.</summary>
    public const double NameShare = 15, AheadBy = 1.5;

    /// <summary>How long before a climb began an app may have opened for the climb to be put down to it starting.</summary>
    public const int OpenedBeforeMinutes = 2;

    /// <param name="opened">Apps that opened in these minutes (by id, Unix seconds): a game starting warms the GPU as it loads.</param>
    /// <param name="power">The PC went off because the power did (see PcUsual.ShutOffOf): a climb as an app was starting is then
    /// said as that, not as a warning. Where the app itself may be why, the climb is the warning it was.</param>
    public static List<StoodOut> Find(IReadOnlyList<SystemMinute> minutes, IReadOnlyList<AppMinute> apps, IReadOnlyList<FanMinute> fans,
        IReadOnlyDictionary<long, string> fanNames, IReadOnlyList<NetDrop> drops, long crash, double? ramTotalGb, Func<long, string> nameOf,
        IReadOnlyList<(long App, long Start)>? opened = null, bool power = false)
    {
        var said = new List<StoodOut>();
        var before = minutes.Where(m => m.Ts <= crash).OrderBy(m => m.Ts).ToList();
        if (before.Count == 0) return said;
        var calm = new List<string>();

        foreach (var part in new[] { Part.Cpu, Part.Gpu })
        {
            if (Climbed(before, part, crash) is { } climb)
            {
                // The power went just after an app opened: its loading warmed the part, and that isn't why the PC went off.
                var starting = power ? opened?.Where(o => o.Start >= climb.Start - OpenedBeforeMinutes * 60 && o.Start <= crash).OrderBy(o => o.Start).FirstOrDefault() : null;
                said.Add(starting is { App: var app, Start: var at } && at > 0
                    ? new StoodOut($"{(part == Part.Cpu ? "CPU" : "GPU")} went from {Units.TempShort(climb.From)} to {Units.TempShort(climb.To)} as {nameOf(app)} was starting at "
                        + $"{TimeUtil.FromUnix(at):h:mm tt}. That's {nameOf(app)} loading; it isn't why the PC went off.", false)
                    : new StoodOut(ClimbText(climb, part, before, apps, crash, nameOf), true));
            }
            else if (before.Any(m => Temp(m, part) is not null))
                calm.Add(part == Part.Cpu ? "CPU temperature" : "GPU temperature");
        }

        // Fans that sped up to well past where they ran before: one line for all of them.
        var faster = new List<(string Name, int From, int To, long Start)>();
        foreach (var fan in fans.Where(f => f.Ts <= crash).GroupBy(f => f.Fan))
        {
            var rows = fan.OrderBy(f => f.Ts).ToList();
            var late = rows.Where(f => f.Ts > crash - ClimbMinutes * 60).ToList();
            var early = rows.Where(f => f.Ts <= crash - ClimbMinutes * 60).ToList();
            if (late.Count == 0 || early.Count < 3) continue;
            int from = (int)Median(early.Select(f => (double)f.RpmAvg)), to = late.Max(f => f.RpmMax);
            if (from <= 0 || to < from * 1.4 || to - from < 300) continue;
            // From the first minute it got halfway there, by the same reading as its top (a minute's average can stay
            // under halfway when it sped up only near the minute's end).
            var start = late.First(f => f.RpmMax >= from + (to - from) / 2);
            faster.Add((fanNames.GetValueOrDefault(fan.Key) ?? "A fan", from, to, start.Ts));
        }
        if (faster.Count == 1)
            said.Add(new StoodOut($"{faster[0].Name} sped up from {faster[0].From:N0} to {faster[0].To:N0} rpm from {TimeUtil.FromUnix(faster[0].Start):h:mm tt}.", false));
        else if (faster.Count > 1)
            said.Add(new StoodOut($"{Wording.JoinAnd([.. faster.Select(x => x.Name)])} sped up from {TimeUtil.FromUnix(faster.Min(x => x.Start)):h:mm tt}, "
                + $"to as much as {faster.Max(x => x.To):N0} rpm.", false));

        // Memory: what Windows had promised to apps (where recorded), and what was in use.
        var lastMinutes = before.Where(m => m.Ts > crash - EarlierMinutes * 60).ToList();
        double? commit = lastMinutes.Max(m => m.CommitPct);
        // A warning is for the minutes leading up to it; what was calm is said for all of them.
        double? used = lastMinutes.Max(m => m.RamUsed), usedAll = before.Max(m => m.RamUsed);
        if (commit >= 90)
        {
            var top = Top(apps, crash, a => a.MemMb);
            said.Add(new StoodOut($"Windows was nearly out of memory: {commit:0}% of what it can promise was promised to apps"
                + (top is { } t ? $", the most to {nameOf(t.App)} ({t.MemMb / 1024:0.0} GB)." : "."), true));
        }
        else if (ramTotalGb is > 0 && used is double u && u / ramTotalGb >= 0.92)
            said.Add(new StoodOut($"Memory was {100 * u / ramTotalGb:0}% full.", true));
        else if (ramTotalGb is > 0 && usedAll is double peak && peak / ramTotalGb >= 0.92)
        {
            // Nearly full earlier on, not in the minutes leading up to it: said, and not as a warning.
            var when = before.Where(m => m.RamUsed is not null).MaxBy(m => m.RamUsed!.Value)!;
            said.Add(new StoodOut($"Memory was {Math.Min(100, 100 * peak / ramTotalGb.Value):0}% full at {TimeUtil.FromUnix(when.Ts):h:mm tt}, "
                + $"{(used is double atEnd ? $"{100 * atEnd / ramTotalGb.Value:0}%" : "less")} by the end.", false));
        }
        else if (usedAll is double inUse)
            calm.Add(ramTotalGb is > 0 ? $"memory ({100 * inUse / ramTotalGb.Value:0}% full at most)" : "memory");

        // The drives: busy for most of the last minutes.
        var drives = lastMinutes.Where(m => m.DiskRate is not null).ToList();
        if (drives.Count > 0)
        {
            const double Busy = 50 * 1_048_576;
            var busy = drives.Where(m => m.DiskRate >= Busy).ToList();
            if (busy.Count * 2 >= drives.Count && busy.Count >= 2)
            {
                var top = Top(apps, crash, a => a.Disk);
                said.Add(new StoodOut($"The drives were busy, about {busy.Average(m => m.DiskRate!.Value) / 1_048_576:0} MB/s"
                    + (top is { } t && t.Disk >= Busy / 2 ? $", mostly {nameOf(t.App)}." : "."), false));
            }
            else calm.Add("the drives");
        }

        // The battery: on it, and nearly empty.
        if (before[^1] is { OnBattery: true, Battery: int charge } && charge <= 10)
            said.Add(new StoodOut($"The PC was running on its battery, at {charge}%.", true));

        // The connection.
        var drop = drops.Where(d => d.End >= before[0].Ts && d.Start <= crash).OrderByDescending(d => d.Start).FirstOrDefault();
        if (drop is not null)
            said.Add(new StoodOut($"The connection dropped at {TimeUtil.FromUnix(drop.Start):h:mm tt}, for {Units.Duration(drop.Seconds)}.", false));
        else calm.Add("the connection");

        if (calm.Count > 0) said.Add(new StoodOut($"Nothing unusual in these minutes: {Wording.JoinAnd(calm)}.", false, IsCalm: true));
        return said;
    }

    private enum Part { Cpu, Gpu }

    private static double? Temp(SystemMinute m, Part part) => part == Part.Cpu ? m.CpuTemp ?? m.CpuTempMax : m.GpuTemp ?? m.GpuTempMax;

    private sealed record Rise(double From, double To, long Start, long End);

    /// <summary>A part that warmed by <see cref="Climb"/> or more in the last minutes, against where it sat just before them.</summary>
    private static Rise? Climbed(List<SystemMinute> minutes, Part part, long crash)
    {
        long edge = crash - ClimbMinutes * 60;
        var late = minutes.Where(m => m.Ts > edge && Temp(m, part) is not null).ToList();
        var early = minutes.Where(m => m.Ts <= edge && m.Ts > edge - EarlierMinutes * 60 && Temp(m, part) is not null).ToList();
        if (late.Count < 2 || early.Count < 3) return null;
        double from = early.Average(m => Temp(m, part)!.Value);
        var peak = late.MaxBy(m => Temp(m, part)!.Value)!;
        double to = Temp(peak, part)!.Value;
        if (to - from < Climb) return null;
        // From the first minute it was clearly on its way up.
        var start = late.First(m => Temp(m, part) >= from + (to - from) / 3);
        return new Rise(from, to, start.Ts, peak.Ts);
    }

    private static string ClimbText(Rise climb, Part part, List<SystemMinute> minutes, IReadOnlyList<AppMinute> apps, long crash, Func<long, string> nameOf)
    {
        string what = part == Part.Cpu ? "CPU" : "GPU";
        string head = Head(climb, part, crash);
        Func<AppMinute, double> share = part == Part.Cpu ? a => a.Cpu : a => a.Gpu;

        // With how much, from the apps each minute kept: the one far ahead of the rest over the climb.
        var during = apps.Where(a => a.Ts >= climb.Start && a.Ts <= crash).ToList();
        int count = minutes.Count(m => m.Ts >= climb.Start && m.Ts <= crash);
        if (during.Count > 0 && count > 0)
        {
            var ranked = during.GroupBy(a => a.App).Select(g => (App: g.Key, Share: g.Sum(share) / count)).OrderByDescending(x => x.Share).ToList();
            var first = ranked[0];
            double second = ranked.Count > 1 ? ranked[1].Share : 0;
            if (first.Share >= NameShare && first.Share >= second * AheadBy)
                return $"{head}, while {nameOf(first.App)} was using about {first.Share:0}% of the {what}.";
            return $"{head}. No one app was doing most of the work.";
        }
        // From before the apps' shares were kept: the app each minute names as working the part hardest, if one does throughout.
        var named = minutes.Where(m => m.Ts >= climb.Start && m.Ts <= crash).Select(m => part == Part.Cpu ? m.CpuApp : m.GpuApp).ToList();
        if (named.Count > 0 && named.GroupBy(x => x).MaxBy(g => g.Count()) is { Key: long app } most && most.Count() * 3 >= named.Count * 2)
            return $"{head}, while {nameOf(app)} was working it hardest.";
        return $"{head}.";
    }

    private static string Head(Rise climb, Part part, long crash)
    {
        int span = Math.Max(1, (int)Math.Round((crash - climb.Start) / 60.0) + 1);
        return $"{(part == Part.Cpu ? "CPU" : "GPU")} went from {Units.TempShort(climb.From)} to {Units.TempShort(climb.To)} in the {span} minutes before the crash";
    }

    /// <summary>The app with the most of something in the last minute before the crash that has apps recorded.</summary>
    private static AppMinute? Top(IReadOnlyList<AppMinute> apps, long crash, Func<AppMinute, double> by)
    {
        var last = apps.Where(a => a.Ts <= crash).Select(a => a.Ts).DefaultIfEmpty(-1).Max();
        return last < 0 ? null : apps.Where(a => a.Ts == last).MaxBy(by);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }
}
