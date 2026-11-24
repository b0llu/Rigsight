namespace Rigsight.Core.Drivers;

/// <summary>
/// The shared facts of "driver activity" (issue #15, approved mock-up v6, 2026-11-21): how long each device's driver
/// kept a CPU core to itself, read by the agent from Windows' own driver events (deferred procedure calls and interrupt
/// service routines). People never see these words: the screens say a part of the PC "was using the CPU for much longer
/// than usual", only beside stutters the player felt or a moment they marked from the tray.
/// </summary>
public static class DriverRules
{
    /// <summary>A burst this long or longer is kept in the agent's in-memory ring (µs).</summary>
    public const int RingUs = 250;

    /// <summary>A "long" burst: what counts as the part using the CPU for a long time (µs). Tuned after the Phase 0 probe.</summary>
    public const int LongUs = 1000;

    /// <summary>A minute is stored for a driver only when its longest burst reached this (µs) or its total reached <see cref="KeepTotalUs"/>.</summary>
    public const int KeepMinuteUs = 500;

    /// <summary>See <see cref="KeepMinuteUs"/> (µs of the driver's own time in the minute).</summary>
    public const int KeepTotalUs = 5000;

    /// <summary>How far back the tray's "Something just went wrong" looks, and how long the ring keeps bursts (seconds).</summary>
    public const int LookBackSec = 120;

    /// <summary>The ring's cap on bursts, whatever the time: a storm can't grow it without end.</summary>
    public const int RingMax = 20_000;

    /// <summary>"Much longer than usual": at least this many times the part's usual longest burst, and at least <see cref="LongUs"/>.</summary>
    public const double MuchLonger = 3.0;

    /// <summary>Days of history behind a part's usual; fewer and nothing is said about it.</summary>
    public const int UsualDays = 7;

    /// <summary>Where the call-length buckets start (µs): under 250, 250 to 500, 500 to 1,000, 1,000 to 2,000, 2,000 to 4,000, 4,000 or more.</summary>
    public static readonly int[] BucketUs = [250, 500, 1000, 2000, 4000];

    /// <summary>Microsoft's guidance for drivers: a DPC should take no more than this (µs). Drawn on the Latency chart as what it is, never as a pass mark.</summary>
    public const int GuidanceDpcUs = 100;
}

/// <summary>
/// One stretch a driver kept a CPU core to itself: its own time, with any interrupt handled inside it taken out.
/// <paramref name="EndMs"/> is when it ended, Unix milliseconds with a fraction (stutters are matched against frames of
/// 10 to 100 ms, so whole milliseconds would blur them).
/// </summary>
/// <param name="Driver">The driver's file, lower case ("e1r68x64.sys"), or "" when the address matched no loaded driver.</param>
public readonly record struct DriverBurst(double EndMs, int Us, int Cpu, string Driver)
{
    public double StartMs => EndMs - Us / 1000.0;
}

/// <summary>A driver's work over one minute or one hour (<paramref name="Ts"/> is its start, Unix seconds).</summary>
/// <param name="Count">Every call counted, long or short.</param>
/// <param name="TotalUs">Its own time on the CPU, all cores together.</param>
/// <param name="LongestUs">Its longest single burst.</param>
/// <param name="Long">Bursts of <see cref="DriverRules.LongUs"/> or more.</param>
/// <param name="Split">Its DPCs and interrupts apart, and how long its calls took (the Latency page, issue #15); null in
/// what was kept before the split was (it then reads "not kept apart then").</param>
public sealed record DriverTally(long Ts, string Driver, int Count, long TotalUs, int LongestUs, int Long, DriverSplit? Split = null);

/// <summary>
/// A driver's work kept apart by kind (deferred procedure calls, timer DPCs counted with them, and interrupts) and its calls
/// counted by how long they took (<see cref="DriverRules.BucketUs"/>): the Latency page's opened driver (issue #15, approved
/// mock-up 2026-11-24). Threaded DPCs are never counted, as everywhere else.
/// </summary>
/// <param name="Buckets">Calls under 0.25 ms, 0.25 to 0.5, 0.5 to 1, 1 to 2, 2 to 4, and 4 ms or more (six counts).</param>
public sealed record DriverSplit(int DpcCount, long DpcUs, int DpcLongestUs, int IsrCount, long IsrUs, int IsrLongestUs, long[] Buckets)
{
    /// <summary>The two added together, as an hour adds up its minutes.</summary>
    public static DriverSplit? Add(DriverSplit? a, DriverSplit? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        var buckets = new long[DriverRules.BucketUs.Length + 1];
        for (int i = 0; i < buckets.Length; i++) buckets[i] = a.Buckets.ElementAtOrDefault(i) + b.Buckets.ElementAtOrDefault(i);
        return new(a.DpcCount + b.DpcCount, a.DpcUs + b.DpcUs, Math.Max(a.DpcLongestUs, b.DpcLongestUs),
            a.IsrCount + b.IsrCount, a.IsrUs + b.IsrUs, Math.Max(a.IsrLongestUs, b.IsrLongestUs), buckets);
    }

    /// <summary>Which bucket a call of <paramref name="us"/> falls in (0 to 5).</summary>
    public static int BucketOf(int us)
    {
        int i = 0;
        while (i < DriverRules.BucketUs.Length && us >= DriverRules.BucketUs[i]) i++;
        return i;
    }
}

/// <summary>
/// The app wake-up check's figures over a minute or an hour (issue #15, approved mock-up 2026-11-24): how long Windows took
/// to run a waiting RigSight thread once it was woken. Checks are moments apart, so a short pause between two is missed;
/// the rate is kept with the figures, since the highest depends on it.
/// </summary>
/// <param name="Checks">Checks made.</param>
/// <param name="MedianUs">The middle check.</param>
/// <param name="HighestUs">The longest check.</param>
/// <param name="OverMs">Checks of 1 ms or more.</param>
/// <param name="Rate">Checks a second asked for most of the time (8, 64 or 2).</param>
public sealed record WakeTally(long Ts, int Checks, int MedianUs, int HighestUs, int OverMs, int Rate);

/// <summary>One second of the live Latency page (issue #15): the longest driver call in it and whose, and the wake-up checks' longest and middle.</summary>
public sealed record LatencySecond(long Ts, int DriverUs, string Driver, int WakeUs, int WakeMedianUs);

/// <summary>What a driver's work can be put down to, in the words people know from Windows Settings.</summary>
public enum PartKind
{
    /// <summary>Windows itself, its shared layers (network, driver framework), or nothing nameable: never shown.</summary>
    None,
    Ethernet,
    WiFi,
    Gpu,
    /// <summary>One device with its own name, such as a USB headset ("HyperX DuoCast").</summary>
    Device,
}

/// <summary>
/// A driver file and the part of the PC it is, as the agent found it among the devices Windows has. <see cref="Say"/> is
/// what the screens call it, or null when it mustn't be named (then nothing is said at all).
/// </summary>
/// <param name="Model">The device's own name from Windows ("Intel(R) I211 Gigabit Network Connection"), shown small and grey.</param>
public sealed record DriverPart(string Driver, PartKind Kind, string? DeviceName, string? Model)
{
    public string? Say => Kind switch
    {
        PartKind.Ethernet => Lang.T("Ethernet adapter"),
        PartKind.WiFi => Lang.T("Wi-Fi adapter"),
        PartKind.Gpu => "GPU",
        PartKind.Device => DeviceName,
        _ => null,
    };
}
