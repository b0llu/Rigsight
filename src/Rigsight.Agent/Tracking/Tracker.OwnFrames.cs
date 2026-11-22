using System.Runtime.InteropServices;
using Rigsight.Agent.Frames;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Games;

namespace Rigsight.Agent.Tracking;

// RigSight's own frames (2026-11-22): the game in front's frame times from Windows' present events (see PresentTrace),
// for players without RivaTuner. RivaTuner stays the source whenever it gives frames for the game, and the overlay and
// widgets keep reading it; a minute RivaTuner gave nothing for is written from RigSight's own (frame_minute.src 1). When
// both had frames, RivaTuner's are kept and the two are compared in the log, so real play checks the own source.
internal sealed partial class Tracker
{
    /// <summary>
    /// The game's frames since the last ask from RigSight's own trace (µs each, into the list), or null while it doesn't
    /// run (no admin rights, no game in front long enough). Set by the agent; a test sets its own.
    /// </summary>
    internal Func<int, List<uint>, PresentRead>? ReadOwnFrames { get; set; }

    /// <summary>The game in front and counted, its process and exe (Pid 0: none): what the agent starts its frames trace for.</summary>
    internal (int Pid, string? Exe) FrontGame { get; private set; }

    private readonly List<uint> _ownBuf = [];
    private readonly StutterFinder _ownStutters = new();
    private int _ownStutterPid;

    /// <summary>Each game's play so far, by source: minutes and frames (said in the log when the play ends).</summary>
    private sealed class PlayFrames
    {
        public int RtssMinutes, OwnMinutes, BothMinutes;
        public long RtssFrames, OwnFrames;

        public void Add(FrameSource src, long frames, bool both)
        {
            if (src == FrameSource.Own) { OwnMinutes++; OwnFrames += frames; }
            else { RtssMinutes++; RtssFrames += frames; }
            if (both) BothMinutes++;
        }
    }

    private readonly Dictionary<long, PlayFrames> _playFrames = [];
    private long _framesTalliedTs;

    /// <summary>
    /// RigSight's own frames of the game in front since the last second: into the minute's own spread and through its own
    /// stutter finder, whether or not RivaTuner gave frames too (the minute picks when it's written). Kept for 14 days
    /// only when RivaTuner didn't measure the game this second. A read in which Windows dropped events starts the stutter
    /// count again and finds none, and its minute writes no stutters: a missing present looks like a long frame.
    /// </summary>
    private void TakeOwnFrames(AppInfo app, int pid, bool rtss)
    {
        if (ReadOwnFrames is not { } read) return;
        var (ok, missed, lost, endMs) = read(pid, _ownBuf);
        if (!ok) return;
        if (pid != _ownStutterPid || missed || lost)
        {
            _ownStutterPid = pid;
            _ownStutters.Reset();
            if (missed) return;
        }
        if (_ownBuf.Count == 0) return;
        if (endMs <= 0) endMs = _clock().ToUnixTimeMilliseconds();
        if (!_minute.OwnFrames.TryGetValue(app.Id, out var acc)) _minute.OwnFrames[app.Id] = acc = new FrameAcc();
        foreach (var us in _ownBuf) acc.Spread.Add(us);
        acc.Seconds++;
        var frames = CollectionsMarshal.AsSpan(_ownBuf);
        if (lost) acc.Lost = true;
        else acc.Stutters.AddRange(_ownStutters.Feed(frames, endMs).Take(Math.Max(0, 300 - acc.Stutters.Count)));
        if (!rtss) FrameStore.Append(app.Id, endMs, frames, FramesFolder);
    }

    /// <summary>The own frames of the minute's app in front, with ten seconds of them at least (else null).</summary>
    private static FrameSpread? OwnFrontFrames(MinuteAcc m) =>
        m.Foreground.Count > 0 && m.OwnFrames.TryGetValue(m.Foreground.MaxBy(kv => kv.Value).Key, out var acc)
            && acc.Seconds >= 10 && acc.Spread.TotalUs >= 10_000_000 ? acc.Spread : null;

    /// <summary>
    /// The minute's frames, each game's from one source: RivaTuner's where it had any, else RigSight's own. A minute both
    /// had frames for is compared in the log. Each play's minutes are added up by source once per minute.
    /// </summary>
    private void WriteFrameMinutes(MinuteAcc m)
    {
        bool tally = _framesTalliedTs != _minuteTs;
        _framesTalliedTs = _minuteTs;
        foreach (var (appId, frames) in m.Frames)
        {
            db.WriteFrameMinute(_minuteTs, appId, frames.Spread, WithDrivers(frames.Stutters), FrameSource.RivaTuner);
            if (tally && !frames.Spread.IsEmpty) Tally(appId).Add(FrameSource.RivaTuner, frames.Spread.Frames, both: m.OwnFrames.TryGetValue(appId, out var o) && !o.Spread.IsEmpty);
        }
        foreach (var (appId, own) in m.OwnFrames)
        {
            if (own.Spread.IsEmpty) continue;
            if (m.Frames.TryGetValue(appId, out var rtss) && !rtss.Spread.IsEmpty)
            {
                if (tally) Log.Write("frames", FrameSources.Compare(Exe(appId), _minuteTs, rtss.Spread, rtss.Stutters.Count, own.Spread, own.Stutters.Count, own.Lost));
                continue;
            }
            if (own.Lost && tally)
                Log.Write("frames", $"{Exe(appId)}, minute at {TimeUtil.FromUnix(_minuteTs):HH:mm}: Windows dropped some of the frames trace's events, so its stutters aren't kept ({own.Stutters.Count} found before)");
            db.WriteFrameMinute(_minuteTs, appId, own.Spread, own.Lost ? [] : WithDrivers(own.Stutters), FrameSource.Own);
            if (tally) Tally(appId).Add(FrameSource.Own, own.Spread.Frames, both: false);
        }
    }

    private PlayFrames Tally(long appId)
    {
        if (!_playFrames.TryGetValue(appId, out var t)) _playFrames[appId] = t = new PlayFrames();
        return t;
    }

    private string Exe(long appId) => _sessions.TryGetValue(appId, out var s) ? s.App.Exe : FrontGame.Exe ?? $"app {appId}";

    /// <summary>A game's play ended: which source its frames came from, said once.</summary>
    private void LogPlayFrames(AppInfo app)
    {
        if (!_playFrames.Remove(app.Id, out var t)) return;
        Log.Write("frames", FrameSources.Play(app.Exe, t.RtssMinutes, t.RtssFrames, t.OwnMinutes, t.OwnFrames, t.BothMinutes));
    }
}
