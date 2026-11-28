using Rigsight.Core.Stability;

namespace Rigsight.Core.Games;

/// <summary>
/// What the PC was doing in the seconds around a stutter (1.23.0, issue #18), kept with the stutter's row: the highest
/// reading of each in the agent's close-up ring (a reading about every 2 seconds) from <see cref="StutterSnaps.AroundMs"/>
/// before the stutter to as long after it. Null where nothing was read.
/// </summary>
/// <param name="Gpu">The GPU's load, %.</param>
/// <param name="Cpu">The CPU's load, %.</param>
/// <param name="GpuTemp">The GPU's temperature, °C.</param>
/// <param name="VramMb">The GPU's memory in use, MB (every app's together), and <paramref name="VramTotalMb"/> all it has.</param>
/// <param name="Commit">Memory Windows had promised to apps, % of all it can promise.</param>
/// <param name="Disk">Disk use, bytes a second: the game's own when <paramref name="DiskIsGame"/>, else every app's together.</param>
/// <param name="OtherApp">The busiest app on the CPU other than the game (its app number), and its share of the CPU, %.</param>
public sealed record StutterSnap(double? Gpu, double? Cpu, double? GpuTemp, double? VramMb, double? VramTotalMb, double? Commit,
    double? Disk, bool DiskIsGame, long? OtherApp, double? OtherCpu);

/// <summary>
/// Gives each of a minute's stutters the readings around it, from the close-up ring, as the agent writes the minute.
/// Stutters within <see cref="ShareMs"/> of the first of a bunch share one reading (a hitchy second is one moment, not
/// five), worked out once. Nothing is kept in memory: the snapshot goes into the stutter's row.
/// </summary>
public static class StutterSnaps
{
    /// <summary>How far either side of a stutter the ring's readings count, ms (the ring reads every 2 seconds or so).</summary>
    public const long AroundMs = 2500;

    /// <summary>Stutters this close to the first of their bunch share its snapshot, ms.</summary>
    public const long ShareMs = 4000;

    /// <summary>
    /// <paramref name="stutters"/> with their snapshots (null where the ring had nothing near), for the game whose app
    /// number is <paramref name="game"/>; <paramref name="between"/> reads the ring from one time to another (Unix ms).
    /// </summary>
    public static List<Stutter> Attach(IReadOnlyList<Stutter> stutters, long game, Func<long, long, IReadOnlyList<CloseUpPoint>> between)
    {
        var list = stutters.OrderBy(s => s.AtMs).ToList();
        for (int i = 0; i < list.Count;)
        {
            int j = i;
            while (j + 1 < list.Count && list[j + 1].AtMs - list[i].AtMs <= ShareMs) j++;
            var snap = Of(between(list[i].AtMs - AroundMs, list[j].AtMs + AroundMs), game);
            for (int k = i; k <= j; k++) list[k] = list[k] with { Snap = snap };
            i = j + 1;
        }
        return list;
    }

    /// <summary>One snapshot from the ring's readings: the highest of each, the game's own disk use where it was in front.</summary>
    public static StutterSnap? Of(IReadOnlyList<CloseUpPoint> points, long game)
    {
        if (points.Count == 0) return null;
        static double? Max(IEnumerable<double?> values) => values.Where(v => v is double d && double.IsFinite(d)).Max();
        // The GPU's memory at its fullest reading, with the total that reading had.
        var vram = points.Where(p => p.Vram is not null && p.VramTotal is > 0).MaxBy(p => p.Vram!.Value / p.VramTotal!.Value);
        // The game's own disk use while it was in front; else every app's together.
        var own = points.Where(p => p.Front == game && p.FrontDisk is not null).Select(p => p.FrontDisk).ToList();
        double? disk = own.Count > 0 ? Max(own) : Max(points.Select(p => p.Disk));
        // The busiest other app on the CPU, at its busiest reading.
        var other = points.SelectMany(p => p.Apps ?? []).Where(a => a.App != game).MaxBy(a => a.Cpu);
        var snap = new StutterSnap(Max(points.Select(p => p.GpuLoad)), Max(points.Select(p => p.CpuLoad)), Max(points.Select(p => p.GpuTemp)),
            vram?.Vram, vram?.VramTotal, Max(points.Select(p => p.Commit)), disk, own.Count > 0, other?.App, other?.Cpu);
        return snap is { Gpu: null, Cpu: null, GpuTemp: null, VramMb: null, Commit: null, Disk: null, OtherApp: null } ? null : snap;
    }
}
