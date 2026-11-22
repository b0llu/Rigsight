namespace Rigsight.Core.Drivers;

/// <summary>
/// A driver's own time in one call, with whatever ran inside it on the same core taken out. Windows times each deferred
/// procedure call (DPC) and interrupt service routine (ISR) from its start to its end, so when an interrupt arrives in
/// the middle of a DPC the DPC's time includes the interrupt's: put down to the DPC's driver, it would blame a network
/// card for what the mouse did. The inner call ends first, so Windows logs it first; each core keeps its last few calls,
/// and a call that ends later and wholly contains one of them has that one's time taken off its own.
/// </summary>
/// <remarks>
/// Only the same core: calls on other cores run side by side, never inside each other. A call that contained others
/// takes their place in the core's list (they are part of it now), so a DPC holding an ISR that held another ISR has the
/// outer ISR's whole time taken off, once, not the inner one's twice. Times are in the trace's clock ticks.
/// </remarks>
public sealed class ExclusiveTime
{
    /// <summary>
    /// The calls each core remembers. A DPC that ran a long time can have had more interrupts inside it than this; the
    /// oldest are forgotten, and their time stays in the DPC's (it then reads a little long, never short).
    /// </summary>
    public const int Depth = 16;

    private long[] _starts = [], _ends = [];
    private int[] _next = [];

    /// <param name="cpus">Cores to make room for; a core past them makes room as it comes (a PC's cores don't change while it runs).</param>
    public ExclusiveTime(int cpus = 0) => Grow(cpus);

    /// <summary>The call's own ticks, start to end less the calls before it on <paramref name="cpu"/> that lie wholly inside it; the call is remembered.</summary>
    public long Take(int cpu, long start, long end)
    {
        if (cpu < 0) return Math.Max(0, end - start);
        if (cpu >= _next.Length) Grow(cpu + 1);
        int at = cpu * Depth;
        return Take(_starts.AsSpan(at, Depth), _ends.AsSpan(at, Depth), ref _next[cpu], start, end);
    }

    /// <summary>
    /// The pure part, over one core's remembered calls: the new call's own ticks. The calls it contains are forgotten and
    /// the new one is remembered in the first slot they freed, or else over the oldest. An empty slot has its end at 0.
    /// </summary>
    public static long Take(Span<long> starts, Span<long> ends, ref int next, long start, long end)
    {
        if (end <= start) return 0;
        long inner = 0;
        int free = -1;
        for (int i = 0; i < starts.Length; i++)
        {
            long s = starts[i], e = ends[i];
            if (e == 0 || s < start || e > end) continue;
            inner += e - s;
            ends[i] = 0;
            if (free < 0) free = i;
        }
        if (free < 0)
        {
            free = next;
            next = (next + 1) % starts.Length;
        }
        starts[free] = start;
        ends[free] = end;
        return Math.Max(0, end - start - inner);
    }

    private void Grow(int cpus)
    {
        if (cpus <= _next.Length) return;
        Array.Resize(ref _starts, cpus * Depth);
        Array.Resize(ref _ends, cpus * Depth);
        Array.Resize(ref _next, cpus);
    }
}
