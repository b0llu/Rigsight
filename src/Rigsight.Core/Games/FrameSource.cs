using System.Globalization;
using Rigsight.Core.Data;

namespace Rigsight.Core.Games;

/// <summary>Where a minute of a game's frames came from (frame_minute.src; an older row without one is RivaTuner's).</summary>
public enum FrameSource
{
    /// <summary>RivaTuner Statistics Server's frame times: the source whenever it measures the game.</summary>
    RivaTuner = 0,
    /// <summary>RigSight's own, from Windows' present events (2026-11-22): where RivaTuner gave nothing for the game.</summary>
    Own = 1,
}

/// <summary>The log's lines on the two sources, while RigSight's own is checked against RivaTuner's on real play.</summary>
public static class FrameSources
{
    /// <summary>
    /// A minute both sources had frames for: each one's average, 1% low, stutters and frames, side by side (RivaTuner's
    /// is the one kept). Text for the log only.
    /// </summary>
    public static string Compare(string exe, long minuteTs, FrameSpread rtss, int rtssStutters, FrameSpread own, int ownStutters, bool ownLost) =>
        FormattableString.Invariant($"{exe}, minute at {TimeUtil.FromUnix(minuteTs):HH:mm}: RivaTuner {Say(rtss, rtssStutters)}; RigSight's own {Say(own, ownStutters)}")
        + (ownLost ? " (Windows dropped some of its events)" : "")
        + (rtss.Fps is double a && own.Fps is double b && a > 0 ? FormattableString.Invariant($"; own average {(b - a) / a * 100:+0.0;-0.0;0.0}%") : "");

    private static string Say(FrameSpread s, int stutters) =>
        (s.Fps is double f ? f.ToString("0.0", CultureInfo.InvariantCulture) + " fps" : "no fps")
        + ", 1% low " + (s.Low1 is double l ? l.ToString("0.0", CultureInfo.InvariantCulture) : "none")
        + FormattableString.Invariant($", {stutters} stutters, {s.Frames} frames");

    /// <summary>A play's frames, by source: the log's line when it ends.</summary>
    public static string Play(string exe, int rtssMinutes, long rtssFrames, int ownMinutes, long ownFrames, int bothMinutes) =>
        $"{exe}: frames from RivaTuner in {rtssMinutes} min ({rtssFrames} frames), from RigSight's own in {ownMinutes} min ({ownFrames} frames)"
        + (bothMinutes > 0 ? $", both had frames in {bothMinutes} min (RivaTuner's kept)" : "")
        + (rtssMinutes + ownMinutes == 0 ? ": none recorded" : "");
}
