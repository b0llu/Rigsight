using Rigsight.Agent.Network;
using Rigsight.Agent.Sensors;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;

namespace Rigsight.Agent.Tracking;

/// <summary>A fan as read with the sensors: its sensor, its name and hardware (for the fans list), and its speed.</summary>
internal readonly record struct FanReading(string Id, string Name, string Hardware, double? Rpm);

/// <summary>
/// Turns raw samples into history: per-minute system summaries, per-hour per-app usage, and
/// sessions. Everything runs on the agent's sampler thread, and data is written once a minute.
/// </summary>
internal sealed class Tracker(RigsightDb db, AppResolver apps, Func<DateTimeOffset>? clock = null)
{
    // The time now. Tests pass their own clock, to replay hours of use (and midnights) in moments.
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);

    /// <summary>
    /// Coming back to an app within this time continues the same session, so alt-tabbing to Discord
    /// mid-match doesn't split one game into many sessions. The other app gets its own session either way.
    /// </summary>
    private const int SessionBreakSeconds = 10 * 60;

    private sealed class MinuteAcc
    {
        public double Seconds, Active, Idle;
        public readonly Dictionary<long, double> Foreground = [];
        public int N;

        public double CpuTempSum, GpuTempSum, CpuLoadSum, GpuLoadSum, CpuPowerSum, GpuPowerSum, RamSum, CpuClockSum, GpuClockSum;
        public int CpuTempN, GpuTempN, CpuLoadN, GpuLoadN, CpuPowerN, GpuPowerN, RamN, CpuClockN, GpuClockN;
        /// <summary>Each fan's speed over the minute, by sensor (see <see cref="FanReading"/>).</summary>
        public readonly Dictionary<string, FanAcc> Fans = [];
        public double? CpuTempMax, GpuTempMax, GpuHotMax, GpuMemMax, CpuVoltMax, GpuVoltMax;
        public double? CpuTempMin, GpuTempMin;
        public double GpuHotSum, GpuMemSum;
        public int GpuHotN, GpuMemN;
        /// <summary>The app working the CPU / GPU hardest in the minute before this minute's hottest reading (null: none clearly).</summary>
        public AppInfo? CpuApp, GpuApp;
    }

    private sealed class FanAcc
    {
        public required string Name, Hardware;
        public double Sum, Max;
        public int N;
    }

    private sealed class Session
    {
        public required AppInfo App { get; init; }
        public long Start { get; init; }
        public long LastActive { get; set; }
        public double ActiveSec { get; set; }
        public double? CpuMax { get; set; }
        public double? GpuMax { get; set; }
        /// <summary>Its row in the history, once saved (see <see cref="Tracker.SaveSession"/>).</summary>
        public long? Row { get; set; }
    }

    private sealed class TodayState
    {
        public DateTime Day { get; init; }
        public double OnSec, ActiveSec, IdleSec;
        public readonly Dictionary<long, double> AppSec = [];
        public double? CpuPeak, GpuPeak;
        public long? CpuPeakTime, GpuPeakTime;
        /// <summary>The app working the CPU / GPU hardest just before each peak (null when none clearly was); named
        /// and categorised when asked, so a rename or a new category shows at once.</summary>
        public AppInfo? CpuPeakApp, GpuPeakApp;
    }

    private RigsightSettings _settings = new();
    private MinuteAcc _minute = new();
    private long _minuteTs;
    private readonly Dictionary<(long Hour, long App), AppHour> _deltas = [];
    private readonly Dictionary<long, Session> _sessions = [];
    private readonly Dictionary<long, double> _fullscreenHeavySec = [];
    private TodayState _today = new() { Day = (clock?.Invoke() ?? DateTimeOffset.Now).LocalDateTime.Date };
    private DateTime _lastPrune = DateTime.MinValue;

    private AppInfo? _foreground;
    private bool _present;

    // Internet use, by app (see NetTracker): which app each process is, and the bookkeeping.
    private readonly NetApps _netApps = new(apps);
    private readonly NetTracker _net = new(db);

    /// <summary>The internet over the last second: the whole connection's speed and each app's.</summary>
    public NetLive NetNow => _net.Live;
    private double? _lastGpuLoad;

    /// <summary>What each app asked of the CPU and the GPU, sample by sample, over the last <see cref="LoadWindowSeconds"/>.</summary>
    private readonly Queue<LoadSample> _load = new();
    private readonly Dictionary<string, int> _loadPids = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A peak is put down to the app that did the most work in this long before it: heat builds over time.</summary>
    private const int LoadWindowSeconds = 60;

    /// <summary>
    /// One process sample: each app's share of the CPU and of the main GPU (0–100, by exe), everything's CPU added up,
    /// and the GPU's load as its sensor read it then (to tell when the GPU's work can't be put down to the apps seen).
    /// </summary>
    private sealed record LoadSample(long Ts, Dictionary<string, double> Cpu, double CpuAll, Dictionary<string, double> Gpu, double? GpuLoad);

    public ActivityInfo Activity { get; private set; } = new();

    /// <summary>Raised (on the sampler thread) when a session has ended.</summary>
    public event Action<SessionRow, AppInfo>? SessionEnded;

    public void SetSettings(RigsightSettings settings) => _settings = settings;

    public void Initialize() => LoadToday();

    private long NowUnix() => _clock().ToUnixTimeSeconds();
    private DateTime LocalToday => _clock().LocalDateTime.Date;

    /// <summary>Start of the current local hour (as <see cref="TimeUtil.LocalHourStartUnix"/>, on this tracker's clock).</summary>
    private long HourStartUnix()
    {
        var now = _clock();
        var local = now.LocalDateTime;
        return now.ToUnixTimeSeconds() - (local.Minute * 60 + local.Second);
    }

    private bool Paused => _settings.Tracking.IsPaused(NowUnix());

    private bool Excluded(string exe) =>
        _settings.Tracking.ExcludedApps.Any(e => e.Equals(exe, StringComparison.OrdinalIgnoreCase));

    private AppHour Delta(long appId)
    {
        long hour = HourStartUnix();
        if (!_deltas.TryGetValue((hour, appId), out var h))
        {
            h = new AppHour { Ts = hour, AppId = appId };
            _deltas[(hour, appId)] = h;
        }
        return h;
    }

    // ── Samples ───────────────────────────────────────────────────────────

    public void OnActivity(ActivitySample s, double dt)
    {
        long now = NowUnix();
        RollMinute();

        if (Paused)
        {
            _foreground = null;
            Activity = new ActivityInfo { Paused = true };
            return;
        }

        bool fullscreenActive = s.Fullscreen && _settings.Tracking.FullscreenCountsAsActive;
        bool present = !s.Locked && (s.IdleMs < _settings.Tracking.IdleMinutes * 60_000 || fullscreenActive);

        AppInfo? app = null;
        if (s.Exe is not null && !s.Locked && !Excluded(s.Exe))
            app = apps.Get(s.Exe, s.Pid);

        _minute.Seconds += dt;
        _today.OnSec += dt;
        if (present)
        {
            _minute.Active += dt;
            _today.ActiveSec += dt;
        }
        else
        {
            _minute.Idle += dt;
            _today.IdleSec += dt;
        }

        if (app is not null)
        {
            var h = Delta(app.Id);
            if (present)
            {
                h.FgSec += dt;
                _minute.Foreground[app.Id] = _minute.Foreground.GetValueOrDefault(app.Id) + dt;
                _today.AppSec[app.Id] = _today.AppSec.GetValueOrDefault(app.Id) + dt;

                if (!_sessions.TryGetValue(app.Id, out var session))
                {
                    session = new Session { App = app, Start = now - (long)dt };
                    _sessions[app.Id] = session;
                }
                session.ActiveSec += dt;
                session.LastActive = now;

                LearnGame(app, s.Fullscreen, dt);
            }
            else
            {
                h.IdleSec += dt;
            }
        }

        _foreground = app;
        _present = present;
        Activity = BuildActivity(app, present, s.Fullscreen);
    }

    /// <summary>Fullscreen + heavy GPU use for a couple of minutes means it's almost certainly a game.</summary>
    private void LearnGame(AppInfo app, bool fullscreen, double dt)
    {
        if (app.AutoCategory != AppCategory.Other || !fullscreen || _lastGpuLoad is not >= 40) return;
        double sec = _fullscreenHeavySec.GetValueOrDefault(app.Id) + dt;
        _fullscreenHeavySec[app.Id] = sec;
        if (sec >= 120)
        {
            apps.MarkAsGame(app);
            Log.Write("tracker", $"Detected {app.Exe} as a game.");
        }
    }

    private ActivityInfo BuildActivity(AppInfo? app, bool present, bool fullscreen)
    {
        if (app is null) return new ActivityInfo { Present = present, Fullscreen = fullscreen };
        _sessions.TryGetValue(app.Id, out var session);
        return new ActivityInfo
        {
            Exe = app.Exe,
            Name = AppResolver.DisplayName(app, _settings),
            Path = app.Path,
            Category = AppResolver.Category(app, _settings),
            Present = present,
            Fullscreen = fullscreen,
            SessionStart = session?.Start ?? 0,
            SessionActiveSec = session?.ActiveSec ?? 0,
            SessionCpuMax = session?.CpuMax,
            SessionGpuMax = session?.GpuMax,
        };
    }

    public void OnSensors(KeyValues k)
    {
        _lastGpuLoad = k.GpuLoad;
        if (Paused) return;

        var m = _minute;
        m.N++;
        Acc(k.CpuTemp, ref m.CpuTempSum, ref m.CpuTempN);
        Acc(k.GpuTemp, ref m.GpuTempSum, ref m.GpuTempN);
        Acc(k.CpuLoad, ref m.CpuLoadSum, ref m.CpuLoadN);
        Acc(k.GpuLoad, ref m.GpuLoadSum, ref m.GpuLoadN);
        Acc(k.CpuPower, ref m.CpuPowerSum, ref m.CpuPowerN);
        Acc(k.GpuPower, ref m.GpuPowerSum, ref m.GpuPowerN);
        Acc(k.RamUsed, ref m.RamSum, ref m.RamN);
        Acc(k.CpuClock, ref m.CpuClockSum, ref m.CpuClockN);
        Acc(k.GpuClock, ref m.GpuClockSum, ref m.GpuClockN);
        Acc(k.GpuHotSpot, ref m.GpuHotSum, ref m.GpuHotN);
        Acc(k.GpuMemJunction, ref m.GpuMemSum, ref m.GpuMemN);
        // The minute's hottest reading so far: note who was working that part hardest just before it.
        if (k.CpuTemp is double cpuNow && (m.CpuTempMax is null || cpuNow > m.CpuTempMax)) m.CpuApp = Busiest(Load.Cpu);
        if (k.GpuTemp is double gpuNow && (m.GpuTempMax is null || gpuNow > m.GpuTempMax)) m.GpuApp = Busiest(Load.Gpu);
        m.CpuTempMax = Max(m.CpuTempMax, k.CpuTemp);
        m.GpuTempMax = Max(m.GpuTempMax, k.GpuTemp);
        m.CpuTempMin = Min(m.CpuTempMin, k.CpuTemp);
        m.GpuTempMin = Min(m.GpuTempMin, k.GpuTemp);
        m.GpuHotMax = Max(m.GpuHotMax, k.GpuHotSpot);
        m.GpuMemMax = Max(m.GpuMemMax, k.GpuMemJunction);
        m.CpuVoltMax = Max(m.CpuVoltMax, k.CpuVoltage);
        m.GpuVoltMax = Max(m.GpuVoltMax, k.GpuVoltage);

        // Today's peaks: every reading, whether or not anyone is at the PC, as the minutes keep them, so Home says what
        // the reports and the Temperatures page say for today. Put down to the app that was doing the work (the one the
        // minute's row keeps), not the window in front, which often isn't what made the heat.
        if (k.CpuTemp is double c && (_today.CpuPeak is null || c > _today.CpuPeak))
        {
            _today.CpuPeak = c;
            _today.CpuPeakTime = NowUnix();
            _today.CpuPeakApp = m.CpuApp;
        }
        if (k.GpuTemp is double g && (_today.GpuPeak is null || g > _today.GpuPeak))
        {
            _today.GpuPeak = g;
            _today.GpuPeakTime = NowUnix();
            _today.GpuPeakApp = m.GpuApp;
        }

        // Attribute readings to whatever is in front while the user is actually using it.
        var app = _foreground;
        if (app is null || !_present) return;

        var h = Delta(app.Id);
        if (k.CpuTemp is double ct) { h.CpuTempSum += ct; h.CpuTempN++; h.CpuTempMax = Max(h.CpuTempMax, ct); }
        if (k.GpuTemp is double gt) { h.GpuTempSum += gt; h.GpuTempN++; h.GpuTempMax = Max(h.GpuTempMax, gt); }
        if (k.GpuLoad is double gl) { h.GpuLoadSum += gl; h.GpuLoadN++; }
        h.GpuHotMax = Max(h.GpuHotMax, k.GpuHotSpot);
        h.CpuPowerMax = Max(h.CpuPowerMax, k.CpuPower);
        h.GpuPowerMax = Max(h.GpuPowerMax, k.GpuPower);
        h.CpuVoltMax = Max(h.CpuVoltMax, k.CpuVoltage);
        h.GpuVoltMax = Max(h.GpuVoltMax, k.GpuVoltage);

        if (_sessions.TryGetValue(app.Id, out var session))
        {
            session.CpuMax = Max(session.CpuMax, k.CpuTemp);
            session.GpuMax = Max(session.GpuMax, k.GpuTemp);
        }

    }

    private string? NameOf(AppInfo? app) => app is null ? null : AppResolver.DisplayName(app, _settings);

    private enum Load { Cpu, Gpu }

    /// <summary>The least an app must average over the minute (% of the CPU / GPU) to be named for a peak…</summary>
    internal const double MinCpuShare = 5, MinGpuShare = 10;

    /// <summary>…and the least part of all the work there was that it must have done.</summary>
    internal const double MinShareOfAll = 0.3;

    /// <summary>
    /// The app that did clearly the most of the CPU's (or GPU's) work over the last minute, or null when none did: the
    /// load was light, shared out between apps, or (for the GPU) mostly done by something whose use can't be read, a
    /// game with anti-cheat, say. Naming nobody beats naming the wrong app.
    /// </summary>
    private AppInfo? Busiest(Load kind)
    {
        if (_load.Count == 0) return null;
        var total = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in _load)
            foreach (var (exe, share) in kind == Load.Cpu ? sample.Cpu : sample.Gpu)
                total[exe] = total.GetValueOrDefault(exe) + share;
        if (total.Count == 0) return null;

        var ranked = total.OrderByDescending(kv => kv.Value).Take(2).ToList();
        string exeTop = ranked[0].Key;
        double top = ranked[0].Value / _load.Count, second = ranked.Count > 1 ? ranked[1].Value / _load.Count : 0;
        // All the work there was: every process's CPU, or the GPU's own load as its sensor read it (now too, which
        // is all there is just after starting).
        double all = kind == Load.Cpu
            ? _load.Average(s => s.CpuAll)
            : _load.Select(s => s.GpuLoad).Append(_lastGpuLoad).OfType<double>().DefaultIfEmpty(0).Average();
        bool clear = top >= (kind == Load.Cpu ? MinCpuShare : MinGpuShare) && top >= second * 1.5 && top >= all * MinShareOfAll;
        if (!clear || !Nameable(exeTop)) return null;
        return apps.Get(exeTop, _loadPids.GetValueOrDefault(exeTop));
    }

    // Not Windows' own parts without a file ("System", "Memory Compression"), apps left out of tracking, or Rigsight.
    private bool Nameable(string exe) =>
        exe.Contains('.') && !Excluded(exe) && !exe.Equals(RigsightPaths.AgentExe, StringComparison.OrdinalIgnoreCase);

    public void OnProcesses(ProcessSnapshot snapshot, Dictionary<string, WindowState> windows, double dt)
    {
        // An app that is no longer running has finished its session right now (don't wait for the gap).
        foreach (var session in _sessions.Values.ToList())
            if (!snapshot.Apps.ContainsKey(session.App.Exe)) CloseSession(session);

        _netApps.Prune(snapshot.PidToExe);
        if (Paused) return;
        RecordLoad(snapshot);

        // Open-but-not-in-front time.
        foreach (var (exe, state) in windows)
        {
            if (Excluded(exe) || (_foreground is not null && _foreground.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase))) continue;
            var app = apps.Get(exe, snapshot.Apps.TryGetValue(exe, out var u) ? u.FirstPid : 0);
            var h = Delta(app.Id);
            if (state.AnyVisible) h.BgSec += dt;
            else if (state.AnyMinimized) h.MinSec += dt;
        }

        // Resource use of apps that matter (windowed, memory-heavy or busy).
        foreach (var usage in snapshot.Apps.Values)
        {
            if (!usage.Exe.Contains('.') || Excluded(usage.Exe) || usage.Exe.Equals(RigsightPaths.AgentExe, StringComparison.OrdinalIgnoreCase)) continue;
            bool interesting = windows.ContainsKey(usage.Exe) || usage.MemMB >= 150 || usage.Cpu >= 2;
            if (!interesting) continue;

            var app = apps.Get(usage.Exe, usage.FirstPid);
            var h = Delta(app.Id);
            h.CpuSum += usage.Cpu;
            h.CpuN++;
            h.CpuMax = Max(h.CpuMax, usage.Cpu);
            h.MemSum += usage.MemMB;
            h.MemN++;
            h.MemMax = Max(h.MemMax, usage.MemMB);
        }
    }

    /// <summary>Keeps each app's CPU and GPU share from this sample, for <see cref="Busiest"/>.</summary>
    private void RecordLoad(ProcessSnapshot snapshot)
    {
        long now = NowUnix();
        while (_load.Count > 0 && _load.Peek().Ts <= now - LoadWindowSeconds) _load.Dequeue();

        // Only apps doing real work are kept (a handful per sample): the rest can't be the one named anyway.
        var cpu = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double cpuAll = 0;
        foreach (var usage in snapshot.Apps.Values)
        {
            cpuAll += usage.Cpu;
            if (usage.Cpu < 1) continue;
            cpu[usage.Exe] = usage.Cpu;
            _loadPids[usage.Exe] = usage.FirstPid;
        }
        var gpu = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (exe, share) in snapshot.Gpu)
        {
            if (share < 1) continue;
            gpu[exe] = share;
            if (snapshot.Apps.TryGetValue(exe, out var usage)) _loadPids[exe] = usage.FirstPid;
        }
        _load.Enqueue(new LoadSample(now, cpu, Math.Min(100, cpuAll), gpu, _lastGpuLoad));
        if (_loadPids.Count > 500) _loadPids.Clear();
    }

    /// <summary>Every fan's speed, read with the sensors: kept per minute (averaged and the highest).</summary>
    public void OnFans(IReadOnlyList<FanReading> fans)
    {
        if (Paused) return;
        var m = _minute;
        foreach (var f in fans)
        {
            if (f.Rpm is not double rpm || !double.IsFinite(rpm) || rpm < 0) continue;
            if (!m.Fans.TryGetValue(f.Id, out var acc)) m.Fans[f.Id] = acc = new FanAcc { Name = f.Name, Hardware = f.Hardware };
            acc.Sum += rpm;
            acc.N++;
            acc.Max = Math.Max(acc.Max, rpm);
        }
    }

    /// <summary>
    /// What each process sent and received over the last second (<paramref name="seconds"/>), from the network trace;
    /// <paramref name="running"/> names the processes (the last process sample's).
    /// </summary>
    public void OnNetwork(Dictionary<int, NetCounts> byPid, IReadOnlyDictionary<int, string> running, double seconds)
    {
        if (Paused) return;
        var counts = new List<(AppInfo? App, NetCounts Counts)>(byPid.Count);
        foreach (var (pid, c) in byPid)
        {
            if (c.IsEmpty) continue;
            var app = _netApps.Of(pid, running, Excluded);
            // Apps left out of tracking still use the internet: their bytes count in the totals, under no name.
            if (app is not null && Excluded(app.Exe)) app = null;
            counts.Add((app, c));
        }
        bool gameInFront = _foreground is not null && _present && AppResolver.Category(_foreground, _settings) == AppCategory.Game;
        _net.OnSecond(NowUnix(), counts, seconds, _foreground, _present, gameInFront, a => AppResolver.DisplayName(a, _settings));
    }

    /// <summary>A time the internet dropped (see ConnectionWatch).</summary>
    public void OnDrop(NetDrop drop)
    {
        if (Paused) return;
        try { db.InsertNetDrop(drop); }
        catch (Exception ex) { Log.Error("network", ex); }
    }

    // ── Persistence ───────────────────────────────────────────────────────

    private void RollMinute()
    {
        long minute = NowUnix() / 60 * 60;
        if (_minuteTs == 0) _minuteTs = minute;
        if (minute == _minuteTs) return;
        Flush(closeAllSessions: false);
        _minute = new MinuteAcc();
        _net.NewMinute();
        _minuteTs = minute;
    }

    /// <summary>Writes the finished minute and hour deltas, closes stale sessions, and rolls the day.</summary>
    public void Flush(bool closeAllSessions)
    {
        long now = NowUnix();
        bool saved = false;
        try
        {
            using var tx = db.BeginTransaction();
            var m = _minute;
            if (m.Seconds >= 1)
            {
                db.WriteMinute(new SystemMinute
                {
                    Ts = _minuteTs,
                    CpuTemp = Avg(m.CpuTempSum, m.CpuTempN),
                    CpuTempMax = m.CpuTempMax,
                    CpuTempMin = m.CpuTempMin,
                    GpuTemp = Avg(m.GpuTempSum, m.GpuTempN),
                    GpuTempMax = m.GpuTempMax,
                    GpuTempMin = m.GpuTempMin,
                    GpuHotMax = m.GpuHotMax,
                    GpuMemMax = m.GpuMemMax,
                    GpuHotAvg = Avg(m.GpuHotSum, m.GpuHotN),
                    GpuMemAvg = Avg(m.GpuMemSum, m.GpuMemN),
                    CpuLoad = Avg(m.CpuLoadSum, m.CpuLoadN),
                    GpuLoad = Avg(m.GpuLoadSum, m.GpuLoadN),
                    CpuPower = Avg(m.CpuPowerSum, m.CpuPowerN),
                    GpuPower = Avg(m.GpuPowerSum, m.GpuPowerN),
                    CpuVoltMax = m.CpuVoltMax,
                    GpuVoltMax = m.GpuVoltMax,
                    RamUsed = Avg(m.RamSum, m.RamN),
                    FgApp = m.Foreground.Count > 0 ? m.Foreground.MaxBy(kv => kv.Value).Key : null,
                    CpuApp = m.CpuApp?.Id,
                    GpuApp = m.GpuApp?.Id,
                    CpuClock = Avg(m.CpuClockSum, m.CpuClockN),
                    GpuClock = Avg(m.GpuClockSum, m.GpuClockN),
                    ActiveSec = (int)Math.Min(60, Math.Round(m.Active)),
                    IdleSec = (int)Math.Min(60, Math.Round(m.Idle)),
                });
                db.WriteFanMinutes(_minuteTs, [.. m.Fans.Where(f => f.Value.N > 0)
                    .Select(f => (db.FanId(f.Key, f.Value.Name, f.Value.Hardware), (int)Math.Round(f.Value.Sum / f.Value.N), (int)Math.Round(f.Value.Max)))]);
            }

            foreach (var h in _deltas.Values)
                if (!h.IsEmpty) db.AddAppHour(h);

            // The minute's hour, counted back from the minute itself (at an hour's first minute the clock is already past it).
            _net.Write(_minuteTs, _minuteTs - TimeUtil.FromUnix(_minuteTs).Minute * 60, closing: closeAllSessions);

            foreach (var session in _sessions.Values.ToList())
            {
                if (closeAllSessions || now - session.LastActive > SessionBreakSeconds) CloseSession(session);
                // Still going: saved as far as it has got, so a PC that loses power or freezes mid-game keeps the
                // session up to this minute (it used to exist only here until it ended).
                else if (session.ActiveSec > 0) SaveSession(session);
            }

            if (LocalToday != _lastPrune)
            {
                _lastPrune = LocalToday;
                if (_settings.Tracking.KeepHistoryDays > 0)
                    db.Prune(TimeUtil.ToUnix(PruneDay(LocalToday, db.LastMinuteBefore(_minuteTs)).AddDays(-_settings.Tracking.KeepHistoryDays)));
            }
            tx.Commit();
            saved = true;
        }
        catch (Exception ex)
        {
            Log.Error("tracker", ex);
        }

        // The minute itself carries on until it ends (see RollMinute): saved early (Windows signing out), it's written
        // again in full, not with only its last part. App times are added up, so those start again from zero; when the
        // save didn't go through (a full disk, a busy database) nothing of them was written, and they wait for the next.
        if (saved) _deltas.Clear();

        if (LocalToday != _today.Day)
            _today = new TodayState { Day = LocalToday };
    }

    /// <summary>
    /// The day old history is counted back from: today, but no later than the day after the last minute recorded before
    /// this one. Deleting can't be undone and the clock can be wrong: a PC whose date jumps a year ahead (a flat clock
    /// battery, a date set by hand) would otherwise lose a year of history within a minute, and stay without it once
    /// the date was put right. A PC that was simply off for weeks only keeps the old days one day longer.
    /// </summary>
    internal static DateTime PruneDay(DateTime today, long? lastMinuteBefore)
    {
        if (lastMinuteBefore is not long last) return today;
        var dayAfter = TimeUtil.FromUnix(last).Date.AddDays(1);
        return dayAfter < today ? dayAfter : today;
    }

    /// <summary>Ends a session: saves it (however short) and tells listeners (for the summary notification).</summary>
    private void CloseSession(Session session)
    {
        _sessions.Remove(session.App.Id);
        if (session.ActiveSec <= 0) return;
        SessionEnded?.Invoke(SaveSession(session), session.App);
    }

    /// <summary>Writes a session as it stands: its row the first time, the same row from then on.</summary>
    private SessionRow SaveSession(Session session)
    {
        var row = new SessionRow
        {
            AppId = session.App.Id,
            Start = session.Start,
            End = session.LastActive,
            ActiveSec = session.ActiveSec,
            CpuTempMax = session.CpuMax,
            GpuTempMax = session.GpuMax,
            IsGame = AppResolver.Category(session.App, _settings) == AppCategory.Game,
        };
        try { session.Row = db.SaveSession(row, session.Row); }
        catch (Exception ex) { Log.Error("tracker", ex); }
        return row;
    }

    private void LoadToday()
    {
        try
        {
            var appRows = db.LoadApps().ToDictionary(a => a.Id);
            var r = ReportBuilder.BuildRaw(db, ReportRange.Day, LocalToday, LocalToday.AddDays(1), appRows, _settings);
            // The peaks as OnSensors keeps them: the day's hottest minute and the app doing the work then, the same
            // peak the reports show for today.
            var (cpu, gpu) = (r.CpuTempPeak, r.GpuTempPeak);
            _today = new TodayState
            {
                Day = LocalToday,
                OnSec = r.OnSec,
                ActiveSec = r.ActiveSec,
                IdleSec = r.AwaySec,
                CpuPeak = cpu?.Value,
                CpuPeakTime = cpu is null ? null : TimeUtil.ToUnix(cpu.Time),
                CpuPeakApp = ByName(cpu?.App),
                GpuPeak = gpu?.Value,
                GpuPeakTime = gpu is null ? null : TimeUtil.ToUnix(gpu.Time),
                GpuPeakApp = ByName(gpu?.App),
            };
            foreach (var a in r.Apps.Where(a => a.ActiveSec > 0))
                _today.AppSec[a.Id] = a.ActiveSec;
        }
        catch (Exception ex)
        {
            Log.Error("tracker", ex);
        }
    }

    public TodayInfo Today()
    {
        var info = new TodayInfo
        {
            OnSec = _today.OnSec,
            ActiveSec = _today.ActiveSec,
            IdleSec = _today.IdleSec,
            CpuPeak = _today.CpuPeak,
            CpuPeakTime = _today.CpuPeakTime,
            CpuPeakApp = NameOf(_today.CpuPeakApp),
            CpuPeakCategory = CategoryOf(_today.CpuPeakApp),
            GpuPeak = _today.GpuPeak,
            GpuPeakTime = _today.GpuPeakTime,
            GpuPeakApp = NameOf(_today.GpuPeakApp),
            GpuPeakCategory = CategoryOf(_today.GpuPeakApp),
        };
        if (_today.AppSec.Count > 0)
        {
            var (id, sec) = _today.AppSec.MaxBy(kv => kv.Value);
            info.TopApp = NameById(id);
            info.TopAppSec = sec;
        }
        return info;
    }

    private AppCategory? CategoryOf(AppInfo? app) => app is null ? null : AppResolver.Category(app, _settings);

    /// <summary>The app a report names (by its shown name), to continue today's peaks after a restart.</summary>
    private AppInfo? ByName(string? name) =>
        name is null ? null : apps.All.FirstOrDefault(a => AppResolver.DisplayName(a, _settings) == name);

    private string? NameById(long id)
    {
        var app = apps.All.FirstOrDefault(a => a.Id == id);
        return app is null ? null : AppResolver.DisplayName(app, _settings);
    }

    public void ClearHistory()
    {
        db.ClearHistory();
        _minute = new MinuteAcc();
        _deltas.Clear();
        _sessions.Clear();
        _net.Clear();
        _today = new TodayState { Day = LocalToday };
    }

    private static void Acc(double? v, ref double sum, ref int n)
    {
        if (v is double d) { sum += d; n++; }
    }

    private static double? Avg(double sum, int n) => n > 0 ? sum / n : null;
    private static double? Max(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
    private static double? Min(double? a, double? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
}
