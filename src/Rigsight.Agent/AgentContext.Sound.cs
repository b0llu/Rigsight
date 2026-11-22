using Rigsight.Agent.Network;
using Rigsight.Agent.Sound;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Drivers;
using Rigsight.Core.Sound;

namespace Rigsight.Agent;

// Sound dropouts (2026-11-22): every time the sound runs out of data on its way out, Windows' audio engine logs a glitch,
// read here in a trace of our own (admin rights only). Once a minute the glitches that came are tagged (the app that was
// playing, by its apps-table id; the driver whose long bursts fell in the 50 ms before, from the driver trace's ring) and
// written; an hourly line says what came. The ring of recent heard glitches is read by the tray's "Something just went
// wrong". Sampler thread.
internal sealed partial class AgentContext
{
    /// <summary>Glitches younger than this (ms) wait for the next minute, so the driver bursts just before them have arrived.</summary>
    private const int SoundSettleMs = 5000;

    private SoundTrace? _soundTrace;
    private long _soundMinute, _soundHour, _soundRetryAt, _soundStored, _soundTagged, _soundNamed;

    private static string SoundTraceName => "Rigsight Sound" + RigsightPaths.InstanceSuffix;

    /// <summary>Sampler thread, at start: the trace (with admin rights).</summary>
    private void StartSound()
    {
        if (!_isAdmin)
        {
            Log.Write("sound", "No admin rights: when the sound cuts out isn't recorded");
            return;
        }
        StartSoundTrace();
    }

    private void StartSoundTrace()
    {
        _soundTrace = SoundTrace.Start(SoundTraceName);
        if (_soundTrace is not null) return;
        var others = SoundTrace.StopOthers(SoundTraceName);
        if (others.Count == 0) return;
        Log.Write("sound", $"Stopped {others.Count} sound trace(s) left by test copies ({string.Join(", ", others)}): starting it again");
        _soundTrace = SoundTrace.Start(SoundTraceName);
    }

    /// <summary>
    /// Sampler thread, once a second: at each new calendar minute the glitches that came are tagged and written. A trace
    /// that isn't running is tried again every <see cref="TraceWatch.RetrySeconds"/>; glitches are rare on a
    /// healthy PC, so a quiet trace is never taken for a stalled one.
    /// </summary>
    private void SampleSound()
    {
        if (!_isAdmin) return;
        long now = TimeUtil.NowUnix(), minute = now / 60 * 60;
        if (_soundMinute == 0) { _soundMinute = minute; return; }
        if (minute == _soundMinute) return;
        _soundMinute = minute;

        if (_soundTrace is not { } trace || trace.Ended)
        {
            if (now < _soundRetryAt) return;
            _soundRetryAt = now + TraceWatch.RetrySeconds;
            Log.Write("sound", _soundTrace is null ? "The sound trace isn't running: starting it again" : "The sound trace stopped by itself: starting it again");
            _soundTrace?.Dispose();
            StartSoundTrace();
            return;
        }

        try { FlushSound(trace, now * 1000 - SoundSettleMs); }
        catch (Exception ex) { _errors.Failed("sound", ex); }

        long hour = now / 3600 * 3600;
        if (_soundHour == 0) _soundHour = hour;
        else if (hour != _soundHour)
        {
            _soundHour = hour;
            Log.Write("sound", $"The last hour: {trace.Summary()}; {_soundStored} glitches written, {_soundNamed} heard ones with the app playing, {_soundTagged} with a driver's long bursts just before");
            _soundStored = _soundTagged = _soundNamed = 0;
        }
    }

    /// <summary>The glitches up to <paramref name="untilMs"/>, tagged with the app playing and the driver just before, written.</summary>
    private void FlushSound(SoundTrace trace, long untilMs)
    {
        var (raw, asked) = trace.Take(untilMs);
        if (raw.Count == 0) return;
        if (raw.Count > SoundRules.KeepPerMinute)
        {
            Log.Write("sound", $"Expected at most {SoundRules.KeepPerMinute} glitches a minute, found {raw.Count} ({raw.Count(g => g.Heard)} heard): keeping the first {SoundRules.KeepPerMinute}, heard ones first");
            raw = [.. raw.OrderByDescending(g => g.Heard).ThenBy(g => g.AtMs).Take(SoundRules.KeepPerMinute)];
        }

        // The driver trace's ring holds the last 2 minutes: every glitch here is younger than that.
        var heardAt = raw.Where(g => g.Heard).Select(g => g.AtMs).DefaultIfEmpty(long.MaxValue).Min();
        var bursts = heardAt == long.MaxValue || _driverTrace is not { Ended: false } drivers ? null
            : drivers.Recent(heardAt - SoundRules.WindowMs).Where(b => b.Us >= DriverRules.LongUs).ToList();

        var glitches = new List<SoundGlitch>(raw.Count);
        foreach (var g in raw)
        {
            if (!g.Heard)
            {
                glitches.Add(new SoundGlitch(g.AtMs, g.Kind, false));
                continue;
            }
            long? app = null;
            string? device = null;
            if (SoundSessions.Nearest(asked, g.AtMs) is { } a)
            {
                device = a.Device;
                if (a.Exe is not null)
                {
                    try { app = _apps.Get(a.Exe, a.Pid).Id; }
                    catch (Exception ex) { _errors.Failed("sound", ex); }
                }
            }
            var drv = bursts is { Count: > 0 } ? BurstOverlap.Inside(bursts, g.AtMs - SoundRules.WindowMs, g.AtMs) : null;
            glitches.Add(new SoundGlitch(g.AtMs, g.Kind, true, app, device, g.Frames, drv?.Driver, drv?.Us ?? 0));
            if (app is not null) _soundNamed++;
            if (drv is not null) _soundTagged++;
        }
        _db.WriteSoundGlitches(glitches);
        _soundStored += glitches.Count;
    }
}
