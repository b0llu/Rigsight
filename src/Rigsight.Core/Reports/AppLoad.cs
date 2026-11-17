using System.Globalization;
using Rigsight.Core.Data;

namespace Rigsight.Core.Reports;

public enum LoadLevel { Low, Medium, High }

public enum LoadPart { Cpu, Gpu, Memory }

/// <summary>
/// How heavy an app has been on this PC today, in one word (the Processes page's LOAD TODAY): each part as a share of
/// what the PC has, over the time the app ran today, and the word is the highest of the three. CPU is its CPU time over
/// the time it ran times the threads; GPU its busy time over the time it ran; memory its average over the RAM installed.
/// </summary>
public sealed record AppLoad(LoadLevel Level, LoadPart Part, double CpuPct, double? GpuPct, double MemPct, double MemMb, double RunSec, double CpuSec)
{
    /// <summary>The least running today before there is a word: a few minutes of starting up say little about a day.</summary>
    public const double MinRunSec = 10 * 60;

    // Each part's share of the PC (%): Low under the first, High from the second, Medium between. The GPU and memory
    // are given more room than the CPU, whose share is of every thread together.
    public const double CpuLow = 1, CpuHigh = 10;
    public const double GpuLow = 2, GpuHigh = 20;
    public const double MemLow = 3, MemHigh = 15;

    /// <summary>The level of one share against its two marks.</summary>
    public static LoadLevel LevelOf(double pct, double low, double high) => pct >= high ? LoadLevel.High : pct < low ? LoadLevel.Low : LoadLevel.Medium;

    /// <summary>About how many cores it kept busy on average while it ran (its CPU time over the time it ran).</summary>
    public double Cores => RunSec > 0 ? CpuSec / RunSec : 0;

    /// <summary>
    /// The word for an app that ran <paramref name="runSec"/> today, used <paramref name="cpuSec"/> of CPU time and
    /// <paramref name="gpuSec"/> of the GPU (null: not known), and held <paramref name="memMb"/> on average, on a PC with
    /// <paramref name="threads"/> threads and <paramref name="ramMb"/> of memory. Null under <see cref="MinRunSec"/>.
    /// </summary>
    public static AppLoad? Judge(double cpuSec, double? gpuSec, double memMb, double runSec, int threads, double ramMb)
    {
        if (runSec < MinRunSec) return null;
        double cpu = cpuSec / (runSec * Math.Max(1, threads)) * 100;
        double? gpu = gpuSec is double g ? Math.Min(100, g / runSec * 100) : null;
        double mem = ramMb > 0 ? memMb / ramMb * 100 : 0;
        var parts = new (LoadPart Part, LoadLevel Level, double Score)[]
        {
            (LoadPart.Cpu, LevelOf(cpu, CpuLow, CpuHigh), cpu / CpuHigh),
            (LoadPart.Gpu, LevelOf(gpu ?? 0, GpuLow, GpuHigh), (gpu ?? 0) / GpuHigh),
            (LoadPart.Memory, LevelOf(mem, MemLow, MemHigh), mem / MemHigh),
        };
        // The highest level; of two at it, the one furthest towards its High mark is named.
        var top = parts.OrderByDescending(p => p.Level).ThenByDescending(p => p.Score).First();
        return new AppLoad(top.Level, top.Part, cpu, gpu, mem, memMb, runSec, cpuSec);
    }

    /// <summary>The word: High, Medium or Low.</summary>
    public string Word => Words(Level);

    public static string Words(LoadLevel level) => level switch
    {
        LoadLevel.High => Lang.C("load", "High"),
        LoadLevel.Medium => Lang.C("load", "Medium"),
        _ => Lang.C("load", "Low"),
    };

    /// <summary>
    /// What the word comes from, for its hover: "High because of its memory, 2.4 GB on average today, 8% of your 32 GB.
    /// CPU 0.6% on average, over 6h 40m running." <paramref name="ram"/> is the memory installed as the page says it ("32 GB").
    /// </summary>
    public string Hover(string ram)
    {
        string word = Word, run = Units.Duration(RunSec), cpu = Pct(CpuPct), mem = Pct(MemPct);
        string cores = Cores >= 0.9 ? " " + CoresBusy(Cores) : "";
        if (Level == LoadLevel.Low)
            return GpuPct is double low && low > 0
                ? Lang.T($"Low. CPU {cpu}%, GPU {Pct(low)}% and memory {mem}% of your {ram}, on average today.")
                : Lang.T($"Low. CPU {cpu}% and memory {mem}% of your {ram}, on average today.");
        return Part switch
        {
            LoadPart.Memory => Lang.T($"{word} because of its memory, {Units.Megabytes(MemMb)} on average today, {mem}% of your {ram}. CPU {cpu}% on average, over {run} running.") + cores,
            LoadPart.Gpu => Lang.T($"{word} because of its GPU, {Pct(GpuPct ?? 0)}% on average today while it ran."),
            _ => Lang.T($"{word} because of its CPU, {cpu}% on average today, over {run} running.") + cores,
        };
    }

    /// <summary>"That's about one core busy." / "That's about 3 cores busy."</summary>
    internal static string CoresBusy(double cores)
    {
        int n = Math.Max(1, (int)Math.Round(cores, MidpointRounding.AwayFromZero));
        return n == 1 ? Lang.T("That's about one core busy.") : Lang.T($"That's about {n} cores busy.");
    }

    /// <summary>The hover of an app that hasn't run long enough today for a word.</summary>
    public static string TooSoon(double runSec)
    {
        int needs = (int)(MinRunSec / 60), has = (int)(runSec / 60);
        return has <= 0
            ? Lang.T($"Too soon to say. It needs {needs} minutes of running today.")
            : Lang.T($"Too soon to say. It needs {needs} minutes of running today, and has {has} so far.");
    }

    /// <summary>A share as the hovers say it: "0.6", "12", "0" (a share under 0.05% is nothing worth a figure).</summary>
    public static string Pct(double pct) => pct >= 10
        ? pct.ToString("0", CultureInfo.CurrentCulture)
        : pct.ToString("0.#", CultureInfo.CurrentCulture);

    /// <summary>
    /// Every app's use today (by exe) from the day's rows, for the Processes page's word on each: CPU time and GPU time
    /// (seconds), how long it ran (seconds) and its average memory (MB). Empty before any is on record.
    /// </summary>
    public static Dictionary<string, AppDayUse> ReadToday(RigsightDb db, DateTime now)
    {
        var result = new Dictionary<string, AppDayUse>(StringComparer.OrdinalIgnoreCase);
        var rows = db.GetAppCpuDay(TimeUtil.ToUnix(now.Date));
        if (rows.Count == 0) return result;
        var exes = db.LoadApps().ToDictionary(a => a.Id, a => a.Exe);
        foreach (var r in rows)
            if (r.App != AppCpuUse.AllApps && exes.TryGetValue(r.App, out var exe))
                result[exe] = new AppDayUse(r.CpuMs / 1000.0, r.GpuMs / 1000.0, r.RunSec, r.MemMbAvg ?? 0);
        return result;
    }
}

/// <summary>One app's use over today: CPU and GPU time (seconds), how long it ran (seconds), its average memory (MB).</summary>
public readonly record struct AppDayUse(double CpuSec, double GpuSec, double RunSec, double MemMb);
