using Rigsight.Agent.Frames;
using Rigsight.Agent.Network;
using Rigsight.Core;
using Rigsight.Core.Data;

namespace Rigsight.Agent;

// RigSight's own frames (2026-11-22): the game in front's frame times from Windows' present events, for players without
// RivaTuner (see PresentTrace, Tracker.OwnFrames.cs). The trace runs only while a game is in front and counted, for that
// game's processes, and stops a little after it leaves; a game coming back within that time keeps it running. Admin
// rights only. Sampler thread.
internal sealed partial class AgentContext
{
    /// <summary>The trace stops this long (seconds) after the last game left the front: an alt-tab doesn't restart it.</summary>
    private const int FramesStopAfterSec = 30;

    /// <summary>No events at all for this long (seconds) with a game in front: the trace has stalled.</summary>
    private const int FramesStallSec = 20;

    /// <summary>No presents from the game's processes for this long (seconds): its processes are looked at again (at most this often).</summary>
    private const int FramesRewatchSec = 30;

    private PresentTrace? _presentTrace;
    private long _framesLeftAt, _framesEventsSeen, _framesQuietSince, _framesRetryAt, _framesPresentsSeen, _framesSilentSince, _framesRewatchedAt;
    private int _framesRestarts;
    private string? _framesExe;

    private static string FramesTraceName => "Rigsight Frames" + RigsightPaths.InstanceSuffix;

    /// <summary>Sampler thread, at start: the tracker reads the trace while it runs (with admin rights).</summary>
    private void StartFrames()
    {
        if (!_isAdmin)
        {
            Log.Write("frames", "No admin rights: games' frame rates come only from RivaTuner");
            return;
        }
        var others = PresentTrace.StopOthers(FramesTraceName);
        if (others.Count > 0) Log.Write("frames", $"Stopped {others.Count} frames trace(s) left by test copies ({string.Join(", ", others)})");
        _tracker.ReadOwnFrames = (pid, into) => _presentTrace is { } t ? t.Take(pid, into) : default;
    }

    /// <summary>
    /// Sampler thread, once a second: the trace started for the game in front (again for another game, or when Windows
    /// ended it), watched for stalls, and stopped <see cref="FramesStopAfterSec"/> after no game is in front.
    /// </summary>
    private void SampleFrames()
    {
        if (!_isAdmin) return;
        long now = TimeUtil.NowUnix();
        var (game, exe) = _tracker.FrontGame;
        if (game == 0)
        {
            if (_presentTrace is null) return;
            if (_framesLeftAt == 0) _framesLeftAt = now;
            else if (now - _framesLeftAt >= FramesStopAfterSec) StopFramesTrace($"no game in front for {FramesStopAfterSec}s");
            return;
        }
        _framesLeftAt = 0;
        if (_presentTrace is { Ended: false } trace && trace.GamePid == game)
        {
            CheckFramesHealth(trace, now);
            return;
        }
        if (_presentTrace is { } old)
            StopFramesTrace(old.Ended ? "it stopped by itself" : $"{exe} is in front now");
        if (now < _framesRetryAt) return;
        StartFramesTrace(game, exe, now);
    }

    private void StartFramesTrace(int game, string? exe, long now)
    {
        List<int> pids;
        try { pids = PresentTimes.Watched(game, [.. InstallWatch.Snapshot().Select(p => (p.Pid, p.Parent, p.Created))]); }
        catch (Exception ex)
        {
            Log.Write("frames", $"The process list couldn't be read ({ex.GetType().Name}: {ex.Message}): watching {exe} alone");
            pids = [game];
        }
        _framesExe = exe;
        _presentTrace = PresentTrace.Start(FramesTraceName, game, pids);
        _framesEventsSeen = _framesPresentsSeen = 0;
        _framesQuietSince = _framesSilentSince = 0;
        _framesRewatchedAt = now;
        if (_presentTrace is null)
        {
            _framesRetryAt = now + TraceWatch.RetrySeconds;
            Log.Write("frames", $"The frames trace for {exe} didn't start: trying again in {TraceWatch.RetrySeconds / 60} minutes");
        }
        else Log.Write("frames", $"Frames trace on for {exe} (process {game}" + (pids.Count > 1 ? $", and {pids.Count - 1} it started: {string.Join(", ", pids.Skip(1))})" : ")"));
    }

    private void StopFramesTrace(string why)
    {
        if (_presentTrace is not { } trace) return;
        long sec = TimeUtil.NowUnix() - trace.StartedAt;
        string chain = trace.Picked is { } c ? $", frames from process {c.Pid}'s {(c.Chain == 0 ? "kernel presents (Vulkan, OpenGL)" : $"swap chain 0x{c.Chain:X}")}" : "";
        Log.Write("frames", $"Frames trace off ({why}): {trace.Presents} presents from {_framesExe} in {sec}s{chain}, {trace.Events} events, {trace.Lost} lost");
        trace.Dispose();
        _presentTrace = null;
        _framesLeftAt = 0;
    }

    /// <summary>
    /// A trace with no events at all for <see cref="FramesStallSec"/> while a game is in front has stalled: started again
    /// (at most 3 times in a row, then every <see cref="TraceWatch.RetrySeconds"/>). A game whose processes present
    /// nothing for <see cref="FramesRewatchSec"/> has them looked at again: its drawing may be in a process started since.
    /// </summary>
    private void CheckFramesHealth(PresentTrace trace, long now)
    {
        long presents = trace.Presents;
        if (presents > _framesPresentsSeen) { _framesPresentsSeen = presents; _framesSilentSince = 0; }
        else if (_framesSilentSince == 0) _framesSilentSince = now;
        else if (now - _framesSilentSince >= FramesRewatchSec && now - _framesRewatchedAt >= FramesRewatchSec)
        {
            _framesRewatchedAt = now;
            List<int> pids;
            try { pids = PresentTimes.Watched(trace.GamePid, [.. InstallWatch.Snapshot().Select(p => (p.Pid, p.Parent, p.Created))]); }
            catch (Exception ex) { Log.Write("frames", $"The process list couldn't be read ({ex.GetType().Name}: {ex.Message})"); pids = [.. trace.Pids]; }
            if (!pids.SequenceEqual(trace.Pids))
            {
                Log.Write("frames", $"Expected presents from {_framesExe}, found none for {now - _framesSilentSince}s ({trace.Events} events from other apps): "
                    + $"watching its processes again ({string.Join(", ", trace.Pids)} before, {string.Join(", ", pids)} now)");
                int game = trace.GamePid;
                string? exe = _framesExe;
                StopFramesTrace("its processes changed");
                StartFramesTrace(game, exe, now);
                return;
            }
        }

        long events = trace.Events;
        if (events > _framesEventsSeen) { _framesEventsSeen = events; _framesQuietSince = 0; _framesRestarts = 0; return; }
        if (_framesQuietSince == 0) { _framesQuietSince = now; return; }
        if (now - _framesQuietSince < FramesStallSec) return;
        if (_framesRestarts >= 3)
        {
            if (now < _framesRetryAt) return;
            _framesRetryAt = now + TraceWatch.RetrySeconds;
            var sessions = NetworkTrace.Running();
            Log.Write("frames", $"Expected present events, found none for {now - _framesQuietSince}s after 3 restarts; trying again in {TraceWatch.RetrySeconds / 60} minutes. Trace sessions running now ({sessions.Count}): {string.Join(", ", sessions)}");
        }
        _framesRestarts++;
        Log.Write("frames", $"Expected present events with {_framesExe} in front, found none for {now - _framesQuietSince}s ({events} since the start): reading them again (restart {_framesRestarts})");
        var others = PresentTrace.StopOthers(FramesTraceName);
        if (others.Count > 0) Log.Write("frames", $"Stopped {others.Count} frames trace(s) left by test copies ({string.Join(", ", others)})");
        int pid = trace.GamePid;
        string? name = _framesExe;
        StopFramesTrace("stalled");
        StartFramesTrace(pid, name, now);
    }
}
