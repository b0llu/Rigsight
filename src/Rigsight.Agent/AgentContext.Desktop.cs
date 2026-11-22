using System.Globalization;
using Rigsight.Agent.Desktop;
using Rigsight.Agent.Network;
using Rigsight.Core;
using Rigsight.Core.Data;

namespace Rigsight.Agent;

// Desktop hitches (a probe, 2026-11-22; nothing is shown from it): frames Windows' compositor had ready and still missed the
// screen's refresh with, from its own events in a private trace (admin rights only). Each calendar minute with any event
// is written with the app in front most of it; an hourly log line adds the hour up. The stored minutes and that log
// decide later whether this becomes a feature. Sampler thread.
internal sealed partial class AgentContext
{
    /// <summary>Seconds into a minute before the one before it is written: the session hands events over within a second.</summary>
    private const int DesktopSettleSec = 3;

    private DesktopTrace? _desktopTrace;
    private long _desktopRetryAt, _desktopLostSeen, _desktopWritten, _desktopChecked;
    private int _desktopLate;
    /// <summary>Seconds each app was in front, by minute (Unix seconds) and app id; and each app's exe, for the log.</summary>
    private readonly Dictionary<long, Dictionary<long, int>> _desktopFront = [];
    private readonly Dictionary<long, string> _desktopExes = [];
    private DesktopHour _desktopHour = new();

    private static string DesktopTraceName => "Rigsight Desktop" + RigsightPaths.InstanceSuffix;

    /// <summary>Sampler thread, at start: the trace, with admin rights.</summary>
    private void StartDesktop()
    {
        if (!_isAdmin)
        {
            Log.Write("desktop", "No admin rights: the compositor's missed refreshes aren't recorded");
            return;
        }
        StartDesktopTrace();
    }

    private void StartDesktopTrace()
    {
        _desktopTrace = DesktopTrace.Start(DesktopTraceName);
        if (_desktopTrace is null)
        {
            var others = DesktopTrace.StopOthers(DesktopTraceName);
            if (others.Count > 0) Log.Write("desktop", $"Stopped {others.Count} desktop trace(s) left by test copies ({string.Join(", ", others)}): trying again next time");
        }
        _desktopLostSeen = 0;
    }

    /// <summary>
    /// Sampler thread, once a second: the app in front counted into this minute; a few seconds into a minute, the minutes
    /// before it written; at a new local hour, the hour's line. A trace Windows ended is started again (at once, then every
    /// <see cref="TraceWatch.RetrySeconds"/>). A trace that sends nothing is left be: a desktop that draws nothing sends nothing.
    /// </summary>
    private void SampleDesktop()
    {
        if (!_isAdmin) return;
        long now = TimeUtil.NowUnix(), minute = now / 60 * 60;
        if (_tracker.Front is { } app)
        {
            if (!_desktopFront.TryGetValue(minute, out var apps)) _desktopFront[minute] = apps = [];
            apps[app.Id] = apps.GetValueOrDefault(app.Id) + 1;
            _desktopExes.TryAdd(app.Id, app.Exe);
        }

        if (_desktopTrace is not { } trace || trace.Ended)
        {
            if (now < _desktopRetryAt) return;
            _desktopRetryAt = now + TraceWatch.RetrySeconds;
            Log.Write("desktop", _desktopTrace is null ? "The desktop trace isn't running: starting it again" : "The desktop trace stopped by itself: starting it again");
            if (_desktopTrace is { } ended) _desktopHour.Add(ended);
            _desktopTrace?.Dispose();
            StartDesktopTrace();
            return;
        }

        if (now - minute >= DesktopSettleSec)
        {
            WriteDesktopMinutes(trace, minute);
            if (_desktopChecked != minute) { _desktopChecked = minute; CheckDesktopLost(trace, minute - 60); }
        }

        long hour = TimeUtil.LocalHourStartUnix();
        if (_desktopHour.Start == 0) _desktopHour.Start = hour;
        else if (hour != _desktopHour.Start)
        {
            LogDesktopHour(trace);
            _desktopHour = new DesktopHour { Start = hour, EventsAtStart = trace.Events };
        }
    }

    /// <summary>Every minute before <paramref name="current"/> with any event, written with the app in front most of it.</summary>
    private void WriteDesktopMinutes(DesktopTrace trace, long current)
    {
        var taken = trace.Tally.TakeBefore(current);
        foreach (var old in _desktopFront.Keys.Where(k => k < current - 120).ToList()) _desktopFront.Remove(old);
        if (taken.Count == 0) return;

        foreach (var (ts, counts) in taken)
        {
            if (ts <= _desktopWritten)
            {
                // Events that came after their minute was written: left out (the row isn't written over with part of it).
                if (++_desktopLate <= 5) Log.Write("desktop", $"Events for the minute from {TimeUtil.FromUnix(ts):HH:mm} came after it was written: left out ({counts.Glitches} missed refreshes)");
                continue;
            }
            if (!counts.Any) continue;
            long? front = _desktopFront.TryGetValue(ts, out var apps) && apps.Count > 0 ? apps.MaxBy(kv => kv.Value).Key : null;
            try
            {
                _db.WriteDesktopMinute(counts.ToRow(ts, front));
                _desktopWritten = ts;
            }
            catch (Exception ex) { _errors.Failed("desktop", ex); }
            _desktopHour.Add(counts, front is { } id ? _desktopExes.GetValueOrDefault(id) : null);
            _desktopFront.Remove(ts);
        }
    }

    /// <summary>Once a minute: events Windows dropped (no buffer for them, or the reader too late) go in the log, marked.</summary>
    private void CheckDesktopLost(DesktopTrace trace, long minute)
    {
        long lost = trace.Lost;
        if (lost <= _desktopLostSeen) return;
        Log.Write("desktop", $"LOST: Windows dropped desktop events by the end of the minute from {TimeUtil.FromUnix(minute):HH:mm} ({lost - _desktopLostSeen} more, {lost} in all): some missed refreshes went uncounted");
        _desktopHour.Lost += lost - _desktopLostSeen;
        _desktopLostSeen = lost;
    }

    private void LogDesktopHour(DesktopTrace trace)
    {
        var h = _desktopHour;
        long events = trace.Events - h.EventsAtStart, lost = h.Lost;
        string from = TimeUtil.FromUnix(h.Start).ToString("HH:mm", CultureInfo.InvariantCulture);
        string tail = $"{events} events{(trace.Filtered ? "" : $" ({trace.Others} of other kinds in all, no filter)")}, {(lost > 0 ? $"{lost} LOST" : "none lost")}"
            + (trace.Short > 0 ? $", {trace.Short} too short to read in all" : "");
        if (h.Minutes == 0)
        {
            Log.Write("desktop", $"Hour from {from}: no compositor events kept; {tail}");
            return;
        }
        static string Ms(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        var line = new System.Text.StringBuilder($"Hour from {from}: {h.GlitchMinutes} minutes with missed refreshes ({h.Minutes} with any event), "
            + $"{h.Glitches} missed (event 24: {h.G24}, 172: {h.D172}), {h.Induced} induced (24: {h.I24}, 172: {h.I172}), "
            + $"{Ms(h.Ms)} ms late in all, longest {(h.Max is { } max ? Ms(max) : "?")} ms, {h.Recoveries} recovery attempts");
        if (h.OddTime > 0) line.Append($", {h.OddTime} late times past {DesktopTally.MaxGlitchMs:0} ms left out");
        if (h.FrontGlitches.Count > 0)
            line.Append("; in front during them: " + string.Join(", ", h.FrontGlitches.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key} {kv.Value}")));
        if (h.Smooth.Count > 0)
            line.Append("; smoothness reports: " + string.Join(", ", h.Smooth.OrderByDescending(kv => kv.Value.N).Take(8)
                .Select(kv => $"\"{kv.Key}\" {kv.Value.N} (missed {Ms((double)kv.Value.PctSum / kv.Value.N)}% on average, {kv.Value.PctMax}% at most)")));
        if (h.Vsync.Count > 0)
            line.Append("; refresh summaries: " + string.Join(", ", h.Vsync.OrderByDescending(kv => kv.Value.N).Take(8)
                .Select(kv => $"\"{kv.Key}\" {kv.Value.N} ({Ms((double)kv.Value.FpsSum / kv.Value.N)} fps on average)")));
        if (h.MoreReports > 0) line.Append($", {h.MoreReports} more reports not kept");
        line.Append($"; {tail}");
        Log.Write("desktop", line.ToString());
    }

    /// <summary>An hour of written minutes, added up for the log line.</summary>
    private sealed class DesktopHour
    {
        public long Start, EventsAtStart, Lost;
        public int Minutes, GlitchMinutes, Glitches, Induced, G24, I24, D172, I172, Recoveries, OddTime, MoreReports;
        public double Ms;
        public double? Max;
        public readonly Dictionary<string, int> FrontGlitches = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, (int N, long PctSum, uint PctMax)> Smooth = [];
        public readonly Dictionary<string, (int N, long FpsSum)> Vsync = [];

        public void Add(DesktopCounts c, string? frontExe)
        {
            Minutes++;
            if (c.Glitches > 0)
            {
                GlitchMinutes++;
                string who = frontExe ?? "(none)";
                FrontGlitches[who] = FrontGlitches.GetValueOrDefault(who) + c.Glitches;
            }
            Glitches += c.Glitches;
            Induced += c.Induced;
            G24 += c.Glitch24;
            I24 += c.Induced24;
            D172 += c.Detected172;
            I172 += c.Induced172;
            Recoveries += c.Recoveries;
            OddTime += c.OddTime;
            MoreReports += c.MoreReports;
            Ms += c.GlitchMs;
            if (c.GlitchMaxMs is { } m && (Max is null || m > Max)) Max = m;
            foreach (var s in c.Smooth)
            {
                var (n, sum, max) = Smooth.GetValueOrDefault(s.Scenario);
                Smooth[s.Scenario] = (n + 1, sum + s.PercentMissed, Math.Max(max, s.PercentMissed));
            }
            foreach (var v in c.Vsync)
            {
                var (n, sum) = Vsync.GetValueOrDefault(v.Scenario);
                Vsync[v.Scenario] = (n + 1, sum + v.Fps);
            }
        }

        /// <summary>A trace that ended: its events so far stay in the hour's count (the next one starts from 0).</summary>
        public void Add(DesktopTrace ended) => EventsAtStart -= ended.Events;
    }
}
