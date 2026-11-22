namespace Rigsight.Core.Sound;

/// <summary>What one of Windows' audio events is to RigSight (see <see cref="SoundRules.Classify"/>).</summary>
public enum SoundClass
{
    /// <summary>Kept for the logs only (counted by its event ID).</summary>
    Other,
    /// <summary>The sound ran out of data on its way out: what people hear as a crackle or a cut. Counted for people.</summary>
    Heard,
    /// <summary>Something in the chain was late (an app, the engine's pump, a driver's request): kept as supporting evidence, never counted as a dropout.</summary>
    Late,
    /// <summary>A sound stream or endpoint being made: a heard glitch just after one is the stream starting, not a dropout.</summary>
    StreamStart,
}

/// <summary>
/// The shared facts of "sound dropouts" (2026-11-22): Windows' audio engine logs a glitch event (Microsoft-Windows-Audio,
/// its GlitchDetection and Informational channels) each time the sound it sends out runs out of data, and RigSight keeps
/// them and holds them against the driver bursts it already measures (see Drivers). People read "your sound cut out".
/// </summary>
public static class SoundRules
{
    /// <summary>A heard glitch is matched with driver bursts in the <see cref="WindowMs"/> before it (ms): the audio engine's buffers are about 10 ms, a few of them make the slack it had.</summary>
    public const int WindowMs = 50;

    /// <summary>Dropouts further apart than this start a new stretch (ms).</summary>
    public const long StretchGapMs = 5 * 60_000;

    /// <summary>Heard glitches this soon after a stream was made, or after the trace started, are a stream starting, not a dropout (ms).</summary>
    public const int SettleMs = 1000;

    /// <summary>Which apps are playing is asked at most this often while glitches come (ms).</summary>
    public const int AttributeEveryMs = 2000;

    /// <summary>A glitch takes the answer to "which app was playing" asked nearest to it, within this (ms).</summary>
    public const int AttributeWithinMs = 3000;

    /// <summary>
    /// Heard glitches closer than this to the one before (ms) are the same cut: a device starved for a while logs one
    /// event per engine pass (about every 10 ms), and the engine's own glitch event can come with the device's.
    /// </summary>
    public const int OneCutMs = 100;

    /// <summary>A stretch shorter than this (ms) may make its case with one burst of matches (see SoundDropouts.Decide).</summary>
    public const long ShortStretchMs = 10 * 60_000;

    /// <summary>The ring of recent heard glitches (for the tray's look back) keeps at most this many.</summary>
    public const int RingMax = 2000;

    /// <summary>At most this many glitches are written a minute (heard first); a storm past it is logged, not stored.</summary>
    public const int KeepPerMinute = 1000;

    /// <summary>Event IDs (Microsoft-Windows-Audio).</summary>
    public const int ThresholdExceeded = 19, ServerStarved = 33, IoctlTooLong = 37, EndGlitch = 41, DeviceStarved = 48,
        PumpMissed = 118, CreateStream = 127, EndpointInstance = 133, AeGlitch = 142, AeDrop = 143;

    /// <summary>
    /// The only events let through when the provider is enabled (its Informational and Performance channels also log an
    /// event per engine pass, about 100 a second, which this leaves out): 19, 26 to 49, 118, 127 to 134, 142, 143.
    /// </summary>
    public static readonly IReadOnlyList<ushort> FilterIds =
    [
        ThresholdExceeded,
        .. Enumerable.Range(26, 49 - 26 + 1).Select(i => (ushort)i),
        PumpMissed,
        .. Enumerable.Range(127, 134 - 127 + 1).Select(i => (ushort)i),
        AeGlitch, AeDrop,
    ];

    /// <summary>
    /// Heard: 48 (a render device starved, KS endpoint) and 142 (the audio engine's own glitch). Late: 33 (an app didn't
    /// hand the engine its sound in time), 118 (the engine's pump missed its deadline), 37 (a driver request took too
    /// long). Stream starts: 127 to 132 (a stream being made). The rest are logged by ID only.
    /// </summary>
    public static SoundClass Classify(int id) => id switch
    {
        DeviceStarved or AeGlitch => SoundClass.Heard,
        ServerStarved or PumpMissed or IoctlTooLong => SoundClass.Late,
        >= 127 and <= 132 => SoundClass.StreamStart,
        _ => SoundClass.Other,
    };
}

/// <summary>
/// Whether a heard glitch is a stream settling (left out) rather than a dropout: within <see cref="SoundRules.SettleMs"/>
/// after the trace started or after a stream was made. Fed in the order events come (the trace's thread).
/// </summary>
public sealed class SoundSettle(long startedMs)
{
    private long _streamAt = long.MinValue / 2;

    /// <summary>A stream was made at <paramref name="atMs"/> (Unix ms).</summary>
    public void StreamStarted(long atMs) => _streamAt = Math.Max(_streamAt, atMs);

    /// <summary>A heard glitch at <paramref name="atMs"/> is left out.</summary>
    public bool Settling(long atMs) =>
        atMs - startedMs < SoundRules.SettleMs || (atMs >= _streamAt && atMs - _streamAt < SoundRules.SettleMs);
}

/// <summary>
/// One glitch as kept: when (Unix ms), its event ID, whether it is heard, and for heard ones the app that was playing (its
/// apps-table id), the output it played on, the frames the event names (142: frames missing; 48: frames the device still
/// had), and the driver whose long bursts fell in the <see cref="SoundRules.WindowMs"/> before it with their µs there.
/// </summary>
public sealed record SoundGlitch(long AtMs, int Kind, bool Heard, long? App = null, string? Device = null, long? Frames = null,
    string? Drv = null, int DrvUs = 0);
