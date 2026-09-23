using Rigsight.Agent.Sensors;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;

namespace Rigsight.Agent.Tracking;

/// <summary>
/// Turns raw samples into history: per-minute system summaries, per-hour per-app usage, and
/// sessions. Everything runs on the agent's sampler thread, and data is written once a minute.
/// </summary>
internal sealed class Tracker(RigsightDb db, AppResolver apps)
{
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
        public double CpuTempSum, GpuTempSum, CpuLoadSum, GpuLoadSum, CpuPowerSum, GpuPowerSum, RamSum;
        public int CpuTempN, GpuTempN, CpuLoadN, GpuLoadN, CpuPowerN, GpuPowerN, RamN;
        public double? CpuTempMax, GpuTempMax, GpuHotMax, GpuMemMax, CpuVoltMax, GpuVoltMax;
    }

    private sealed class Session
    {
        public required AppInfo App { get; init; }
        public long Start { get; init; }
        public long LastActive { get; set; }
        public double ActiveSec { get; set; }
        public double? CpuMax { get; set; }
        public double? GpuMax { get; set; }
    }

    private sealed class TodayState
    {
        public DateTime Day { get; init; }
        public double OnSec, ActiveSec, IdleSec;
        public readonly Dictionary<long, double> AppSec = [];
        public double? CpuPeak, GpuPeak;
        public string? CpuPeakApp, GpuPeakApp;
        public AppCategory CpuPeakCategory, GpuPeakCategory;
    }

    private RigsightSettings _settings = new();
    private MinuteAcc _minute = new();
    private long _minuteTs;
    private readonly Dictionary<(long Hour, long App), AppHour> _deltas = [];
    private readonly Dictionary<long, Session> _sessions = [];
    private readonly Dictionary<long, double> _fullscreenHeavySec = [];
    private TodayState _today = new() { Day = DateTime.Today };
    private DateTime _lastPrune = DateTime.MinValue;

    private AppInfo? _foreground;
    private bool _present;
    private double? _lastGpuLoad;

    public ActivityInfo Activity { get; private set; } = new();

    /// <summary>Raised (on the sampler thread) when a session has ended.</summary>
    public event Action<SessionRow, AppInfo>? SessionEnded;

    public void SetSettings(RigsightSettings settings) => _settings = settings;

    public void Initialize() => LoadToday();

    private bool Paused => _settings.Tracking.IsPaused(TimeUtil.NowUnix());

    private bool Excluded(string exe) =>
        _settings.Tracking.ExcludedApps.Any(e => e.Equals(exe, StringComparison.OrdinalIgnoreCase));

    private AppHour Delta(long appId)
    {
        long hour = TimeUtil.ToUnix(TimeUtil.LocalHourStart(DateTime.Now));
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
        long now = TimeUtil.NowUnix();
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
        m.CpuTempMax = Max(m.CpuTempMax, k.CpuTemp);
        m.GpuTempMax = Max(m.GpuTempMax, k.GpuTemp);
        m.GpuHotMax = Max(m.GpuHotMax, k.GpuHotSpot);
        m.GpuMemMax = Max(m.GpuMemMax, k.GpuMemJunction);
        m.CpuVoltMax = Max(m.CpuVoltMax, k.CpuVoltage);
        m.GpuVoltMax = Max(m.GpuVoltMax, k.GpuVoltage);

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

        string name = AppResolver.DisplayName(app, _settings);
        var category = AppResolver.Category(app, _settings);
        if (k.CpuTemp is double c && (_today.CpuPeak is null || c > _today.CpuPeak)) { _today.CpuPeak = c; _today.CpuPeakApp = name; _today.CpuPeakCategory = category; }
        if (k.GpuTemp is double g && (_today.GpuPeak is null || g > _today.GpuPeak)) { _today.GpuPeak = g; _today.GpuPeakApp = name; _today.GpuPeakCategory = category; }
    }

    public void OnProcesses(ProcessSnapshot snapshot, Dictionary<string, WindowState> windows, double dt)
    {
        // An app that is no longer running has finished its session right now (don't wait for the gap).
        foreach (var session in _sessions.Values.ToList())
            if (!snapshot.Apps.ContainsKey(session.App.Exe)) CloseSession(session);

        if (Paused) return;

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

    // ── Persistence ───────────────────────────────────────────────────────

    private void RollMinute()
    {
        long minute = TimeUtil.ToUnix(TimeUtil.LocalMinuteStart(DateTime.Now));
        if (_minuteTs == 0) _minuteTs = minute;
        if (minute == _minuteTs) return;
        Flush(closeAllSessions: false);
        _minuteTs = minute;
    }

    /// <summary>Writes the finished minute and hour deltas, closes stale sessions, and rolls the day.</summary>
    public void Flush(bool closeAllSessions)
    {
        long now = TimeUtil.NowUnix();
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
                    GpuTemp = Avg(m.GpuTempSum, m.GpuTempN),
                    GpuTempMax = m.GpuTempMax,
                    GpuHotMax = m.GpuHotMax,
                    GpuMemMax = m.GpuMemMax,
                    CpuLoad = Avg(m.CpuLoadSum, m.CpuLoadN),
                    GpuLoad = Avg(m.GpuLoadSum, m.GpuLoadN),
                    CpuPower = Avg(m.CpuPowerSum, m.CpuPowerN),
                    GpuPower = Avg(m.GpuPowerSum, m.GpuPowerN),
                    CpuVoltMax = m.CpuVoltMax,
                    GpuVoltMax = m.GpuVoltMax,
                    RamUsed = Avg(m.RamSum, m.RamN),
                    FgApp = m.Foreground.Count > 0 ? m.Foreground.MaxBy(kv => kv.Value).Key : null,
                    ActiveSec = (int)Math.Min(60, Math.Round(m.Active)),
                    IdleSec = (int)Math.Min(60, Math.Round(m.Idle)),
                });
            }

            foreach (var h in _deltas.Values)
                if (!h.IsEmpty) db.AddAppHour(h);

            foreach (var session in _sessions.Values.ToList())
            {
                if (!closeAllSessions && now - session.LastActive <= SessionBreakSeconds) continue;
                CloseSession(session);
            }

            if (DateTime.Today != _lastPrune)
            {
                _lastPrune = DateTime.Today;
                db.Prune(
                    TimeUtil.ToUnix(DateTime.Today.AddDays(-_settings.Tracking.KeepDetailedDays)),
                    TimeUtil.ToUnix(DateTime.Today.AddDays(-_settings.Tracking.KeepHistoryDays)));
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            Log.Error("tracker", ex);
        }

        _minute = new MinuteAcc();
        _deltas.Clear();

        if (DateTime.Today != _today.Day)
            _today = new TodayState { Day = DateTime.Today };
    }

    /// <summary>Ends a session: saves it (however short) and tells listeners (for the summary notification).</summary>
    private void CloseSession(Session session)
    {
        _sessions.Remove(session.App.Id);
        if (session.ActiveSec <= 0) return;

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
        try { db.InsertSession(row); }
        catch (Exception ex) { Log.Error("tracker", ex); }
        SessionEnded?.Invoke(row, session.App);
    }

    private static AppCategory CategoryOf(Report r, string? app) =>
        app is null ? AppCategory.Other : r.Apps.FirstOrDefault(a => a.Name == app)?.Category ?? AppCategory.Other;

    private void LoadToday()
    {
        try
        {
            var appRows = db.LoadApps().ToDictionary(a => a.Id);
            var r = ReportBuilder.BuildRaw(db, ReportRange.Day, DateTime.Today, DateTime.Today.AddDays(1), appRows, _settings);
            _today = new TodayState
            {
                Day = DateTime.Today,
                OnSec = r.OnSec,
                ActiveSec = r.ActiveSec,
                IdleSec = r.AwaySec,
                CpuPeak = r.CpuTempPeak?.Value,
                CpuPeakApp = r.CpuTempPeak?.App,
                CpuPeakCategory = CategoryOf(r, r.CpuTempPeak?.App),
                GpuPeak = r.GpuTempPeak?.Value,
                GpuPeakApp = r.GpuTempPeak?.App,
                GpuPeakCategory = CategoryOf(r, r.GpuTempPeak?.App),
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
            CpuPeakApp = _today.CpuPeakApp,
            CpuPeakCategory = _today.CpuPeakCategory,
            GpuPeak = _today.GpuPeak,
            GpuPeakApp = _today.GpuPeakApp,
            GpuPeakCategory = _today.GpuPeakCategory,
        };
        if (_today.AppSec.Count > 0)
        {
            var (id, sec) = _today.AppSec.MaxBy(kv => kv.Value);
            info.TopApp = NameById(id);
            info.TopAppSec = sec;
        }
        return info;
    }

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
        _today = new TodayState { Day = DateTime.Today };
    }

    private static void Acc(double? v, ref double sum, ref int n)
    {
        if (v is double d) { sum += d; n++; }
    }

    private static double? Avg(double sum, int n) => n > 0 ? sum / n : null;
    private static double? Max(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}
