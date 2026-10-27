using System.Globalization;
using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;

namespace Rigsight.Core.Games;

/// <summary>
/// When a play's stutters bunched up and what else was going on then, in one line (approved mock-up, 2026-10-25): "14 of
/// the 31 stutters came between 3:12 and 3:28 PM, while Steam was using the disk heavily (380 MB/s). The rest were spread
/// out, about one every 9 minutes." From the minutes RigSight already keeps: every app's disk and CPU use, the graphics
/// card's memory, Windows' memory. Only what stood out against the rest of the same play is named; otherwise it says so.
/// </summary>
public static class StutterCauses
{
    private const int WindowSec = 15 * 60, AtLeast = 5;

    /// <param name="app">The game's app number (its minutes are left out of the others').</param>
    public static string? Explain(RigsightDb db, long app, IReadOnlyList<Stutter> stutters, long playFrom, long playTo, RigsightSettings settings)
    {
        if (stutters.Count == 0) return null;
        var times = stutters.Select(s => s.AtMs / 1000).OrderBy(t => t).ToList();
        double playMin = Math.Max(1, (playTo - playFrom) / 60.0);

        // The busiest 15 minutes, if they hold at least 5 stutters and a third of them.
        int best = 0, bestAt = 0;
        for (int i = 0, j = 0; i < times.Count; i++)
        {
            while (times[i] - times[j] > WindowSec) j++;
            if (i - j + 1 > best) { best = i - j + 1; bestAt = j; }
        }
        bool bunched = best >= AtLeast && best >= times.Count / 3.0;
        if (!bunched)
            return times.Count == 1 ? Lang.T("The one stutter was on its own.")
                : Lang.T($"The {times.Count} stutters were spread out, {Every(playMin / times.Count)}.");

        long from = times[bestAt], to = times[bestAt + best - 1];
        string when = Lang.T($"{best} of the {times.Count} stutters came between {TimeText.At(TimeUtil.FromUnix(from), "h:mm")} and {TimeText.At(TimeUtil.FromUnix(to), "h:mm tt")}");
        string cause = Cause(db, app, from - 30, to + 30, playFrom, playTo, settings) ?? Lang.T("nothing else stood out then");
        int rest = times.Count - best;
        double restMin = Math.Max(1, playMin - (to - from) / 60.0);
        string tail = rest switch
        {
            0 => "",
            1 => " " + Lang.T("One more came on its own."),
            _ => " " + Lang.T($"The rest were spread out, {Every(restMin / rest)}."),
        };
        return $"{when}, {cause}.{tail}";
    }

    /// <summary>
    /// How often, in the unit that reads right: 765 stutters in two and a half hours said "about one every 1 minutes"
    /// (2026-10-27), when it was one every 12 seconds.
    /// </summary>
    internal static string Every(double minutesEach) =>
        minutesEach >= 1.5 ? Lang.T($"about one every {minutesEach:0} minutes")
        : minutesEach >= 0.75 ? Lang.T("about one a minute")
        : minutesEach * 60 >= 1.5 ? Lang.T($"about one every {minutesEach * 60:0} seconds")
        : Lang.T("about one a second");

    /// <summary>What stood out in those minutes against the rest of the play: another app on the disk, the card's memory full, another app on the CPU, Windows short of memory.</summary>
    private static string? Cause(RigsightDb db, long game, long from, long to, long playFrom, long playTo, RigsightSettings settings)
    {
        long f = from / 60 * 60, t = (to / 60 + 1) * 60;
        var inside = db.GetAppMinutes(f, t).Where(a => a.App != game).ToList();
        var whole = db.GetAppMinutes(playFrom / 60 * 60, playTo + 60).Where(a => a.App != game && (a.Ts < f || a.Ts >= t)).ToList();
        int minutesIn = Math.Max(1, (int)((t - f) / 60)), minutesOut = Math.Max(1, (int)((playTo - playFrom) / 60) - minutesIn);
        var apps = db.LoadApps().ToDictionary(a => a.Id);
        string Name(long id) => apps.TryGetValue(id, out var a) ? settings.AppNames.TryGetValue(a.Exe, out var alias) ? alias : AppCatalog.KnownName(a.Exe) ?? a.Name : Lang.T("An app");

        // Another app reading or writing a lot, far more than it did in the rest of the play.
        var disk = inside.GroupBy(a => a.App).Select(g => (App: g.Key, Rate: g.Sum(a => a.Disk) / minutesIn))
            .OrderByDescending(x => x.Rate).FirstOrDefault();
        if (disk.Rate >= 50e6)
        {
            double before = whole.Where(a => a.App == disk.App).Sum(a => a.Disk) / minutesOut;
            if (disk.Rate >= 3 * before) return Lang.T($"while {Name(disk.App)} was using the disk heavily ({disk.Rate / 1e6:0} MB/s)");
        }
        var minutes = db.GetMinutes(f, t);
        // The graphics card's memory all but full.
        if (minutes.Where(m => m.VramUsed is not null && m.VramTotal is > 0).Select(m => (m.VramUsed!.Value, m.VramTotal!.Value)).ToList() is { Count: > 0 } vram
            && vram.Max(v => v.Item1 / v.Item2) >= 0.95)
        {
            var top = vram.MaxBy(v => v.Item1 / v.Item2);
            return Lang.T($"while the graphics card's memory was nearly full ({top.Item1 / 1024:0.0} of {top.Item2 / 1024:0} GB)");
        }
        // Another app busy on the CPU, more than in the rest of the play.
        var cpu = inside.GroupBy(a => a.App).Select(g => (App: g.Key, Load: g.Sum(a => a.Cpu) / minutesIn)).OrderByDescending(x => x.Load).FirstOrDefault();
        if (cpu.Load >= 20)
        {
            double before = whole.Where(a => a.App == cpu.App).Sum(a => a.Cpu) / minutesOut;
            if (cpu.Load >= 2 * before) return Lang.T($"while {Name(cpu.App)} was busy in the background ({cpu.Load:0}% of the CPU)");
        }
        // Windows nearly out of memory.
        if (minutes.Any(m => m.CommitPct >= 90)) return Lang.T("while Windows was nearly out of memory");
        return null;
    }
}
