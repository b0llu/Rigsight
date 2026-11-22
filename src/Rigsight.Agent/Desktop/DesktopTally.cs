using System.Text;
using System.Text.Json;
using Rigsight.Core.Data;

namespace Rigsight.Agent.Desktop;

/// <summary>
/// One calendar minute of compositor events (see <see cref="DesktopTrace"/>). Events 24 (a missed refresh, with its late
/// time) and 172 (a missed refresh found, with only whether it was induced) are counted apart: whether DWM writes both
/// for the same frame isn't known yet, so the minute's count is the larger of the two (never their sum), and the hourly
/// log line gives both so the probe's log shows how they pair.
/// </summary>
internal sealed class DesktopCounts
{
    /// <summary>Event 24s not induced and induced; event 172s the same.</summary>
    public int Glitch24, Induced24, Detected172, Induced172;
    /// <summary>Event 25s (only when the Scheduling keyword is on, see <see cref="DesktopTrace.ListenForRecoveries"/>).</summary>
    public int Recoveries;
    /// <summary>The late time of the uninduced 24s that gave one: in all, the longest, and how many gave one.</summary>
    public double GlitchMs;
    public double? GlitchMaxMs;
    public int Timed;
    /// <summary>Uninduced 24s whose late time read as more than <see cref="DesktopTally.MaxGlitchMs"/>: counted, their time left out.</summary>
    public int OddTime;
    /// <summary>The 237 and 240 reports, at most <see cref="DesktopTally.MaxReports"/> together; how many more there were.</summary>
    public readonly List<DwmSmooth> Smooth = [];
    public readonly List<DwmVsyncSummary> Vsync = [];
    public int MoreReports;

    /// <summary>Missed refreshes DWM didn't cause on purpose.</summary>
    public int Glitches => Math.Max(Glitch24, Detected172);

    /// <summary>Missed refreshes DWM says it caused (induced).</summary>
    public int Induced => Math.Max(Induced24, Induced172);

    public bool Any => Glitch24 + Induced24 + Detected172 + Induced172 + Recoveries + Smooth.Count + Vsync.Count + MoreReports > 0;

    /// <summary>The minute's row, the app in front most of it given.</summary>
    public DesktopMinute ToRow(long ts, long? frontApp) =>
        new(ts, Glitches, Induced, Timed > 0 ? Math.Round(GlitchMs, 3) : null, GlitchMaxMs is { } max ? Math.Round(max, 3) : null, Recoveries, frontApp,
            DesktopTally.Json(this));
}

/// <summary>
/// Compositor events added into their calendar minute, on the trace's thread (<see cref="Glitch"/> and the others); the
/// sampler takes the minutes that ended (<see cref="TakeBefore"/>). A lock between the two.
/// </summary>
internal sealed class DesktopTally
{
    /// <summary>Reports kept a minute (237 and 240 together): an animation-heavy minute writes a few, never hundreds.</summary>
    internal const int MaxReports = 60;

    /// <summary>A late time past this (ms) is a field read wrong, not a hitch: the hitch counts, its time doesn't.</summary>
    internal const double MaxGlitchMs = 10_000;

    /// <summary>Minutes kept waiting at most (a sampler that stopped taking them mustn't let this grow).</summary>
    private const int MaxMinutes = 120;

    private readonly Lock _gate = new();
    private readonly SortedDictionary<long, DesktopCounts> _minutes = [];

    private DesktopCounts At(long minute)
    {
        if (!_minutes.TryGetValue(minute, out var c))
        {
            if (_minutes.Count >= MaxMinutes) _minutes.Remove(_minutes.Keys.First());
            _minutes[minute] = c = new DesktopCounts();
        }
        return c;
    }

    /// <summary>Event 24: a missed refresh, induced or not, late by <paramref name="ms"/> (null: its time couldn't be read).</summary>
    public void Glitch(long minute, bool induced, double? ms)
    {
        lock (_gate)
        {
            var c = At(minute);
            if (induced) { c.Induced24++; return; }
            c.Glitch24++;
            if (ms is not { } t || t < 0) return;
            if (t > MaxGlitchMs) { c.OddTime++; return; }
            c.GlitchMs += t;
            c.Timed++;
            if (c.GlitchMaxMs is null || t > c.GlitchMaxMs) c.GlitchMaxMs = t;
        }
    }

    /// <summary>Event 172: a missed refresh found, induced or not.</summary>
    public void Detected(long minute, bool induced)
    {
        lock (_gate)
        {
            var c = At(minute);
            if (induced) c.Induced172++;
            else c.Detected172++;
        }
    }

    /// <summary>Event 25: DWM tried to recover from a missed refresh.</summary>
    public void Recovery(long minute)
    {
        lock (_gate) At(minute).Recoveries++;
    }

    /// <summary>Event 237: an animation's smoothness report.</summary>
    public void Smooth(long minute, DwmSmooth s)
    {
        lock (_gate)
        {
            var c = At(minute);
            if (c.Smooth.Count + c.Vsync.Count < MaxReports) c.Smooth.Add(s);
            else c.MoreReports++;
        }
    }

    /// <summary>Event 240: a scenario's refreshes added up.</summary>
    public void VsyncSummary(long minute, DwmVsyncSummary v)
    {
        lock (_gate)
        {
            var c = At(minute);
            if (c.Smooth.Count + c.Vsync.Count < MaxReports) c.Vsync.Add(v);
            else c.MoreReports++;
        }
    }

    /// <summary>Every minute that started before <paramref name="before"/> (Unix seconds), oldest first, taken away.</summary>
    public List<(long Ts, DesktopCounts Counts)> TakeBefore(long before)
    {
        lock (_gate)
        {
            var taken = _minutes.Where(kv => kv.Key < before).Select(kv => (kv.Key, kv.Value)).ToList();
            foreach (var (ts, _) in taken) _minutes.Remove(ts);
            return taken;
        }
    }

    /// <summary>
    /// The minute's reports as compact JSON, null when there were none: an array of 237s as {"s": scenario, "m": percent
    /// of refreshes missed, "a": app id, "f": frames}, 240s as {"e": 240, "s": scenario, "h": total hits, "fps": FPS}, and
    /// {"more": n} last when more came than were kept.
    /// </summary>
    internal static string? Json(DesktopCounts c)
    {
        if (c.Smooth.Count + c.Vsync.Count + c.MoreReports == 0) return null;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartArray();
            foreach (var s in c.Smooth)
            {
                w.WriteStartObject();
                w.WriteString("s", s.Scenario);
                w.WriteNumber("m", s.PercentMissed);
                w.WriteString("a", s.AppId);
                w.WriteNumber("f", s.Frames);
                w.WriteEndObject();
            }
            foreach (var v in c.Vsync)
            {
                w.WriteStartObject();
                w.WriteNumber("e", 240);
                w.WriteString("s", v.Scenario);
                w.WriteNumber("h", v.TotalHits);
                w.WriteNumber("fps", v.Fps);
                w.WriteEndObject();
            }
            if (c.MoreReports > 0)
            {
                w.WriteStartObject();
                w.WriteNumber("more", c.MoreReports);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
