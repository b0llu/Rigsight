using System.Text.RegularExpressions;

namespace Rigsight.Core.Parts;

/// <summary>The two kinds of core a hybrid CPU has; null on a CPU with one kind.</summary>
public enum CoreKind { Performance, Efficient }

/// <summary>
/// What a per-core sensor's name says: its core's number as the sensor library counts it, its thread's (a load per thread),
/// and its kind where the name gives it ("P-Core #1", "E-Core #3").
/// </summary>
public readonly record struct CoreName(int Core, int? Thread, CoreKind? Kind);

/// <summary>One core's readings right now: its load (its own, or its threads' average), each thread's and its clock.</summary>
public sealed record CoreNow(CoreKind? Kind, int Core, double? Load, IReadOnlyList<double> Threads, double? Mhz);

/// <summary>A core as the CPU page shows it: its number on the page (1 up, across the groups), load, threads, clock.</summary>
public sealed record CoreShown(int Number, CoreKind? Kind, double Load, IReadOnlyList<double> Threads, double? Mhz, int ThreadCount);

/// <summary>A group of cores on the page: the performance or the efficient cores of a hybrid CPU, or all of them (no kind).</summary>
public sealed record CoreGroup(CoreKind? Kind, IReadOnlyList<CoreShown> Cores);

/// <summary>
/// The CPU page's "Each core right now" (1.23.0, approved mock-up): every core's load, threads and clock from the
/// sensors the app already gets each second (nothing new is recorded), grouped into performance and efficient cores on a
/// hybrid CPU (by the sensor's own name, else by Windows' efficiency class), with one sentence on top.
/// </summary>
public static class CpuCores
{
    // "CPU Core #3", "CPU Core #3 Thread #2", "P-Core #1", "E-Core #2 Thread #1", "Core #4", "CPU Core" (one core).
    private static readonly Regex Load = new(@"^(?:(?<k>[PE])-Core|(?:CPU )?Core) #(?<c>\d+)(?: Thread #(?<t>\d+))?$", RegexOptions.CultureInvariant);
    // "Core #1", "CPU Core #1", "P-Core #2", "E-Core #1" (never "Cores (Average)", "Bus Speed" or "Core #1 VID").
    private static readonly Regex Clock = new(@"^(?:(?<k>[PE])-Core|(?:CPU )?Core) #(?<c>\d+)$", RegexOptions.CultureInvariant);
    // "Core #1 VID", "P-Core #2 VID", "CPU Core #1 VID".
    private static readonly Regex Vid = new(@"^(?:(?<k>[PE])-Core|(?:CPU )?Core) #(?<c>\d+) VID$", RegexOptions.CultureInvariant);

    public static CoreName? LoadName(string name) => Parse(Load, name);
    public static CoreName? ClockName(string name) => Parse(Clock, name);
    public static CoreName? VidName(string name) => Parse(Vid, name);

    private static CoreName? Parse(Regex re, string name)
    {
        var m = re.Match(name);
        if (!m.Success) return null;
        CoreKind? kind = m.Groups["k"].Value switch { "P" => CoreKind.Performance, "E" => CoreKind.Efficient, _ => null };
        return new CoreName(int.Parse(m.Groups["c"].Value), m.Groups["t"].Success ? int.Parse(m.Groups["t"].Value) : null, kind);
    }

    /// <summary>
    /// The cores in their groups, numbered 1 up across them (performance first). The kind comes from the sensor's name;
    /// where no name gives one and Windows lists as many cores as there are, from Windows' efficiency class for the core
    /// at the same place (its highest class is the performance cores). One group without a kind otherwise.
    /// </summary>
    public static List<CoreGroup> Group(IReadOnlyList<CoreNow> cores, IReadOnlyList<CoreTopology> windows)
    {
        if (cores.Count == 0) return [];
        cores = FoldThreads(cores, windows);
        var ordered = cores.OrderBy(c => c.Kind is CoreKind.Efficient ? 1 : 0).ThenBy(c => c.Core).ToList();
        bool named = ordered.Any(c => c.Kind is not null);
        bool fromWindows = !named && windows.Count == ordered.Count && CpuTopology.IsHybrid(windows);
        byte top = windows.Count > 0 ? windows.Max(w => w.EfficiencyClass) : (byte)0;
        var shown = new List<CoreShown>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var c = ordered[i];
            CoreKind? kind = named ? c.Kind ?? CoreKind.Performance
                : fromWindows ? (windows[i].EfficiencyClass == top ? CoreKind.Performance : CoreKind.Efficient) : null;
            int threads = c.Threads.Count > 0 ? c.Threads.Count : windows.Count == ordered.Count ? windows[i].Threads : 1;
            double load = c.Load ?? (c.Threads.Count > 0 ? c.Threads.Average() : 0);
            shown.Add(new CoreShown(0, kind, Math.Clamp(load, 0, 100), c.Threads, c.Mhz, threads));
        }
        // Performance cores first, then efficient; numbered in that order.
        var groups = shown.GroupBy(c => c.Kind).OrderBy(g => g.Key is CoreKind.Efficient ? 1 : 0).ToList();
        int n = 0;
        return [.. groups.Select(g => new CoreGroup(groups.Count > 1 ? g.Key : null, [.. g.Select(c => c with { Number = ++n })]))];
    }

    /// <summary>
    /// On AMD the sensor library names one load per logical processor ("CPU Core #1" to "#16" on an 8-core, 16-thread
    /// Ryzen) while its clocks are per core ("Core #1" to "#8"). When the loads carry no thread number and there are as
    /// many as Windows lists threads, more than it lists cores, they are threads: each Windows core takes the next as many
    /// as it runs (Windows numbers a core's threads one after another), and keeps the clock of the core with its number.
    /// </summary>
    internal static IReadOnlyList<CoreNow> FoldThreads(IReadOnlyList<CoreNow> cores, IReadOnlyList<CoreTopology> windows)
    {
        if (windows.Count == 0 || cores.Count <= windows.Count || cores.Count != windows.Sum(w => w.Threads)) return cores;
        if (cores.Any(c => c.Kind is not null || c.Threads.Count > 0)) return cores;
        var byNumber = cores.OrderBy(c => c.Core).ToList();
        var folded = new List<CoreNow>(windows.Count);
        int at = 0;
        for (int i = 0; i < windows.Count; i++)
        {
            var threads = byNumber.Skip(at).Take(windows[i].Threads).Select(c => c.Load ?? 0).ToList();
            at += windows[i].Threads;
            double? mhz = byNumber.FirstOrDefault(c => c.Core == i + 1)?.Mhz;
            folded.Add(new CoreNow(null, i + 1, null, threads, mhz));
        }
        return folded;
    }

    /// <summary>The busiest core (the first of equals).</summary>
    public static CoreShown? Busiest(IEnumerable<CoreShown> cores) =>
        cores.Aggregate((CoreShown?)null, (best, c) => best is null || Math.Round(c.Load) > Math.Round(best.Load) ? c : best);

    /// <summary>
    /// "Core 2 is the busiest at 64%. 1 of 14 cores is over half busy, and the average is 23%." Over half is past 50%, as
    /// shown (rounded).
    /// </summary>
    public static string Sentence(IReadOnlyList<CoreShown> cores)
    {
        if (cores.Count == 0 || Busiest(cores) is not { } top) return "";
        int all = cores.Count, over = cores.Count(c => Math.Round(c.Load) > 50);
        int busiest = (int)Math.Round(top.Load), average = (int)Math.Round(cores.Average(c => c.Load));
        string first = Lang.T($"Core {top.Number} is the busiest at {busiest}%.");
        string second = over == 0 ? Lang.T($"No core is over half busy, and the average is {average}%.")
            : over == all ? Lang.T($"All {all} cores are over half busy, and the average is {average}%.")
            : over == 1 ? Lang.T($"1 of {all} cores is over half busy, and the average is {average}%.")
            : Lang.T($"{over} of {all} cores are over half busy, and the average is {average}%.");
        return $"{first} {second}";
    }

    /// <summary>"8 cores, 16 threads", or a hybrid's "6 performance cores, 8 efficient cores, 20 threads" (the page's subtitle).</summary>
    public static string Count(IReadOnlyList<CoreTopology> windows)
    {
        if (windows.Count == 0) return "";
        int threads = windows.Sum(c => c.Threads);
        string t = threads == 1 ? Lang.T("1 thread") : Lang.T($"{threads} threads");
        if (!CpuTopology.IsHybrid(windows)) return windows.Count == 1 ? Lang.T($"1 core, {t}") : Lang.T($"{windows.Count} cores, {t}");
        byte top = windows.Max(w => w.EfficiencyClass);
        int p = windows.Count(w => w.EfficiencyClass == top), e = windows.Count - p;
        return Lang.T($"{p} performance cores, {e} efficient cores, {t}");
    }

    /// <summary>
    /// A tile's foot: its clock, "4,784 MHz"; the threads, "2 threads", only without a clock. The tile's bars already show
    /// one bar per thread, and both together were cut off on a tile eight to a row.
    /// </summary>
    public static string Foot(CoreShown c)
    {
        if (c.Mhz is double mhz && mhz > 0) return $"{mhz.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} MHz";
        return c.ThreadCount == 1 ? Lang.T("1 thread") : Lang.T($"{c.ThreadCount} threads");
    }
}
