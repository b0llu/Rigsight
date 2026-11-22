using Rigsight.Core.Stability;

namespace Rigsight.Core.Drivers;

/// <summary>What the tray's "Something just went wrong" found, said in its notification and kept as a Timeline mark.</summary>
/// <param name="Found">Something stood out (a device, the drives, memory or the CPU).</param>
/// <param name="TimelineText">The mark's line on the Timeline.</param>
public sealed record LookBackResult(bool Found, string Title, string Body, string TimelineText)
{
    /// <summary>What was checked and found, with its numbers, for the agent's log (English, never shown).</summary>
    public string Detail { get; init; } = "";
}

/// <summary>
/// "Something just went wrong" (issue #15, approved mock-up v6, 2026-11-22): the person felt a stutter or a hitch and
/// pressed the tray's item, and the 2 minutes before are looked over. First match wins, in this order:
/// <list type="number">
/// <item>a device whose driver kept a CPU core to itself for much longer than its usual (named, with a week of history, a
/// burst at least <see cref="DriverRules.MuchLonger"/> times its usual and at least <see cref="DriverRules.LongUs"/>): the
/// one furthest past its usual;</item>
/// <item>the drives: their busiest reading at least <see cref="DiskFloor"/> and <see cref="DiskOver"/> times their average
/// over the minutes before (at least <see cref="DiskBeforeLeast"/> readings of them);</item>
/// <item>memory: Windows' promised memory at <see cref="CommitFull"/>% or more;</item>
/// <item>the CPU: at <see cref="CpuFull"/>% or more in at least <see cref="CpuBusyLeast"/> readings, with the app working it
/// hardest where it is known.</item>
/// </list>
/// Temperatures aren't looked at: there's nothing in the 2 minutes to hold them against. Pure: the agent gathers the
/// readings, this decides.
/// </summary>
public static class LookBack
{
    /// <summary>The drives' busiest reading must reach this (bytes a second): 100 MB/s as <see cref="Units.Speed"/> writes it.</summary>
    public const double DiskFloor = 100 * 1_048_576.0;
    public const double DiskOver = 3.0;
    public const int DiskBeforeLeast = 5;
    public const double CommitFull = 95, CpuFull = 95;
    public const int CpuBusyLeast = 3;
    /// <summary>The busiest app is named only when it averaged at least this share of the CPU (%) in the busy readings.</summary>
    public const double AppLeast = 10;

    /// <param name="now">When the item was pressed (local time).</param>
    /// <param name="bursts">The driver bursts of the last <see cref="DriverRules.LookBackSec"/> seconds, or null when the driver
    /// trace isn't running (no admin rights, or not started): then devices aren't checked, and the text says why.</param>
    /// <param name="parts">Which part of the PC each driver is, by its file.</param>
    /// <param name="usualOf">A driver's usual longest burst (µs), null while too soon to say.</param>
    /// <param name="points">The close-up readings of the last few minutes (about every 2 seconds), any order.</param>
    /// <param name="nameOf">An app's name by its id, null when it can't be named.</param>
    public static LookBackResult Check(DateTime now, IReadOnlyList<DriverBurst>? bursts, IReadOnlyDictionary<string, DriverPart> parts,
        Func<string, int?> usualOf, IReadOnlyList<CloseUpPoint> points, Func<long, string?>? nameOf = null)
    {
        long nowMs = new DateTimeOffset(now).ToUnixTimeMilliseconds();
        long fromMs = nowMs - DriverRules.LookBackSec * 1000L;
        var recent = points.Where(p => p.T >= fromMs && p.T <= nowMs).OrderBy(p => p.T).ToList();
        var before = points.Where(p => p.T >= nowMs - CloseUp.RingSec * 1000L && p.T < fromMs).ToList();

        // ── A device ──
        if (bursts is not null)
        {
            DriverPart? best = null;
            int bestUs = 0, bestUsual = 0;
            double bestOver = 0;
            foreach (var group in bursts.Where(b => b.Driver.Length > 0 && b.EndMs >= fromMs && b.EndMs <= nowMs + 1000)
                         .GroupBy(b => b.Driver, StringComparer.OrdinalIgnoreCase))
            {
                if (!parts.TryGetValue(group.Key, out var part) || part.Say is null) continue;
                if (usualOf(group.Key) is not int usual) continue;
                int longest = group.Max(b => b.Us);
                double floor = Math.Max(DriverRules.LongUs, DriverRules.MuchLonger * usual);
                if (longest < floor) continue;
                double over = longest / (double)Math.Max(1, usual);
                if (over <= bestOver) continue;
                (best, bestUs, bestUsual, bestOver) = (part, longest, usual, over);
            }
            if (best is { Say: { } say })
                return new LookBackResult(true, Lang.T($"Found something at {now:h:mm tt}"),
                    Lang.T($"Your {say} was using the CPU for much longer than usual. That can cause stutters."),
                    Lang.T($"You marked a problem. Your {say} was using the CPU for much longer than usual."))
                {
                    Detail = $"device {best.Driver} ({say}) {bestUs} µs at its longest against a usual {bestUsual} µs; {Checked(bursts, recent)}",
                };
        }

        // ── The drives ──
        double? diskTop = recent.Select(p => p.Disk).Where(v => v is double x && double.IsFinite(x)).Max();
        var diskBefore = before.Select(p => p.Disk).OfType<double>().Where(double.IsFinite).ToList();
        if (diskTop is double top && top >= DiskFloor && diskBefore.Count >= DiskBeforeLeast && top >= DiskOver * diskBefore.Average())
        {
            string speed = Units.Speed(top);
            return new LookBackResult(true, Lang.T($"Found something at {now:h:mm tt}"),
                Lang.T($"Your drives were reading and writing {speed}, far more than in the minutes before. That can cause stutters."),
                Lang.T($"You marked a problem. Your drives were reading and writing {speed}, far more than in the minutes before."))
            {
                Detail = $"drives {speed} at their busiest against {Units.Speed(diskBefore.Average())} on average in the {diskBefore.Count} readings before; {Checked(bursts, recent)}",
            };
        }

        // ── Memory ──
        var full = recent.Select(p => p.Commit).OfType<double>().Where(c => c >= CommitFull).ToList();
        if (full.Count > 0)
            return new LookBackResult(true, Lang.T($"Found something at {now:h:mm tt}"),
                Lang.T("Windows was nearly out of memory. That can cause stutters."),
                Lang.T("You marked a problem. Windows was nearly out of memory."))
            {
                Detail = $"memory promised at {full.Max():0}%; {Checked(bursts, recent)}",
            };

        // ── The CPU ──
        var busy = recent.Where(p => p.CpuLoad is double l && l >= CpuFull).ToList();
        if (busy.Count >= CpuBusyLeast)
        {
            string? app = null;
            var hardest = busy.SelectMany(p => p.Apps ?? []).GroupBy(a => a.App)
                .Select(g => (App: g.Key, Cpu: g.Sum(a => a.Cpu) / busy.Count)).OrderByDescending(a => a.Cpu).FirstOrDefault();
            if (hardest.Cpu >= AppLeast && nameOf?.Invoke(hardest.App) is { Length: > 0 } name) app = name;
            return new LookBackResult(true, Lang.T($"Found something at {now:h:mm tt}"),
                app is null ? Lang.T("Your CPU was fully busy.") : Lang.T($"Your CPU was fully busy. The app working it hardest was {app}."),
                app is null ? Lang.T("You marked a problem. Your CPU was fully busy.") : Lang.T($"You marked a problem. Your CPU was fully busy. The app working it hardest was {app}."))
            {
                Detail = $"CPU at {CpuFull:0}% or more in {busy.Count} readings, hardest {(hardest.Cpu > 0 ? $"app {hardest.App} at {hardest.Cpu:0}% ({app ?? "not named"})" : "not known")}; {Checked(bursts, recent)}",
            };
        }

        // ── Nothing ──
        string detail = $"nothing; {Checked(bursts, recent)}, highest CPU {Highest(recent, p => p.CpuLoad)}%, drives {(diskTop is double d ? Units.Speed(d) : "-")}, memory {Highest(recent, p => p.Commit)}%";
        if (recent.Count == 0)
        {
            // Nothing was recorded (tracking paused, the agent just started): "as usual" would be a claim with nothing under it.
            return bursts is null
                ? new LookBackResult(false, Lang.T($"Nothing to check at {now:h:mm tt}"),
                    Lang.T("RigSight wasn't recording in the 2 minutes before, so nothing could be checked."),
                    Lang.T("You marked a problem. RigSight wasn't recording in the 2 minutes before."))
                { Detail = detail }
                : new LookBackResult(false, Lang.T($"Nothing unusual at {now:h:mm tt}"),
                    Lang.T("Your devices were as usual in the 2 minutes before. The CPU, the disk and memory weren't being recorded."),
                    Lang.T("You marked a problem. Nothing unusual was found."))
                { Detail = detail };
        }
        return new LookBackResult(false, Lang.T($"Nothing unusual at {now:h:mm tt}"),
            bursts is null
                ? Lang.T("The CPU, the disk and memory were as usual in the 2 minutes before. Your devices can only be checked with RigSight's background part running as administrator.")
                : Lang.T("Your devices, the CPU, the disk and memory were as usual in the 2 minutes before."),
            Lang.T("You marked a problem. Nothing unusual was found."))
        {
            Detail = detail,
        };
    }

    private static string Checked(IReadOnlyList<DriverBurst>? bursts, List<CloseUpPoint> recent) =>
        (bursts is null ? "devices not checked (no driver trace)" : $"devices checked ({bursts.Count} bursts)") + $", {recent.Count} readings";

    private static string Highest(List<CloseUpPoint> points, Func<CloseUpPoint, double?> value) =>
        points.Select(value).OfType<double>().DefaultIfEmpty(double.NaN).Max() is var v && double.IsFinite(v) ? $"{v:0}" : "-";
}
