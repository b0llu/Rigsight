namespace Rigsight.Core.Drivers;

/// <summary>Which driver's long bursts fell inside a stutter's frame, and how much of their time did.</summary>
public static class BurstOverlap
{
    /// <summary>
    /// The driver with the most of its own time inside <paramref name="fromMs"/>..<paramref name="toMs"/> (Unix ms, the
    /// frame from its start to its end), counting only bursts of <see cref="DriverRules.LongUs"/> or more from a driver
    /// that was found ("" is an address no loaded driver held). Null when none fell inside. Only time inside the frame
    /// counts: a burst that ended just before the frame began is context, not overlap.
    /// </summary>
    public static (string Driver, int Us)? Inside(IReadOnlyList<DriverBurst> bursts, double fromMs, double toMs)
    {
        Dictionary<string, double>? sums = null;
        foreach (var b in bursts)
        {
            if (b.Us < DriverRules.LongUs || b.Driver.Length == 0) continue;
            double overlap = Math.Min(b.EndMs, toMs) - Math.Max(b.StartMs, fromMs);
            if (overlap <= 0) continue;
            sums ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            sums[b.Driver] = sums.GetValueOrDefault(b.Driver) + overlap;
        }
        if (sums is null) return null;
        var top = sums.MaxBy(kv => kv.Value);
        return (top.Key, (int)Math.Round(top.Value * 1000));
    }
}
