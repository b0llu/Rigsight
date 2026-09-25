using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Rigsight.Agent.Ipc;
using Rigsight.Agent.Native;
using Rigsight.Agent.Sensors;
using Rigsight.Agent.Tracking;
using Rigsight.Agent.Ui;
using Rigsight.Agent.Widgets;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;
using Rigsight.Core.Updates;

namespace Rigsight.Agent;

/// <summary>
/// Runs the agent. Two threads do the work:
///  • the UI thread (WinForms message loop) owns the tray icon and widget windows;
///  • the sampler thread reads sensors, tracks activity and writes the database.
/// The sampler wakes once a second and does very little each time, so the agent stays near 0% CPU.
/// </summary>
internal sealed class AgentContext : ApplicationContext
{
    private readonly SynchronizationContext _ui;
    private readonly bool _isAdmin;
    private readonly Lock _settingsLock = new();
    private volatile RigsightSettings _settings;

    private readonly SensorHost _sensors = new();
    private readonly KeyHistory _history = new();
    private readonly DailyExtremes _extremes = new();
    private readonly DriveTempHistory _driveHistory = new();
    // Set when an app connects (hello), so the next tick carries every sensor's range, not just changes.
    private volatile bool _sendAllExtremes;
    private readonly EventWaitHandle _quitSignal;
    private RegisteredWaitHandle? _quitWait;
    private readonly RigsightDb _db;
    private readonly AppResolver _apps;
    private readonly Tracker _tracker;
    private readonly ProcessSampler _procSampler = new();
    private readonly ActivityMonitor _activity = new();
    private readonly AlertMonitor _alerts = new();
    private readonly PipeServer _pipe;
    private readonly TrayController _tray;
    private readonly WidgetManager _widgets;
    private readonly OverlayManager _overlay;
    private readonly NotificationCenter _notices;

    private readonly ConcurrentQueue<Action> _samplerWork = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _sampler;
    private volatile bool _stopping;
    private volatile bool _sensorsReady;
    private volatile bool _overlayVisible;
    // Today's totals as last computed on the sampler thread, for the hello sent to a newly connected app.
    private volatile TodayInfo? _lastToday;
    private bool _startupEnabled;
    private string? _pendingPage, _pendingArg;

    // Set RIGSIGHT_PROFILE=1 to log how long each part of the sampler loop takes, once a minute.
    private static readonly bool Profiling = Environment.GetEnvironmentVariable("RIGSIGHT_PROFILE") == "1";
    private readonly Dictionary<string, double> _profile = [];
    private long _profileStart = Stopwatch.GetTimestamp();

    private void Measure(string name, long startTimestamp)
    {
        if (!Profiling) return;
        _profile[name] = _profile.GetValueOrDefault(name) + Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        if (Stopwatch.GetElapsedTime(_profileStart).TotalSeconds >= 60)
        {
            Log.Write("profile", string.Join(", ", _profile.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value:0}ms")));
            _profile.Clear();
            _profileStart = Stopwatch.GetTimestamp();
        }
    }

    public AgentContext(string[] args, bool isAdmin)
    {
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _ui = SynchronizationContext.Current!;
        _isAdmin = isAdmin;

        _settings = SettingsStore.Load();
        Units.Fahrenheit = _settings.UseFahrenheit;

        _db = RigsightDb.OpenWriter();
        _apps = new AppResolver(_db);
        _tracker = new Tracker(_db, _apps);
        _tracker.SetSettings(_settings);
        _tracker.SessionEnded += OnSessionEnded;

        _widgets = new WidgetManager(() => _settings, MutateSettings, () => OpenApp("widgets"));
        _overlay = new OverlayManager(isAdmin);
        _overlay.StateChanged += OnOverlayStateChanged;
        _overlay.CantReachGame += OnOverlayCantReachGame;
        _tray = new TrayController(() => OpenApp(null), _widgets.BuildTrayMenu(), _overlay.TrayItem, PauseFor, Resume,
            () => _settings.Tracking.IsPaused(TimeUtil.NowUnix()), Quit);
        _notices = new NotificationCenter(() => _settings, _tray, OpenApp, n => _overlay.ShowInGame(n, _settings.Alerts.CardSeconds));

        _pipe = new PipeServer(BuildHello, OnUiMessage);
        _pipe.Start();
        _widgets.Apply(_settings);
        _overlay.Apply(_settings.Overlay);
        if (_settings.Overlay.Enabled) _overlay.EnsureRtssRunning();

        // Save before Windows shuts down or signs out (otherwise the process is simply killed, losing
        // the game session in progress), and let the installer ask for a clean stop ("--quit").
        SystemEvents.SessionEnding += OnSessionEnding;
        // A time zone change (e.g. travelling) must move "today" too; .NET caches the zone otherwise.
        SystemEvents.TimeChanged += OnTimeChanged;
        _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, RigsightPaths.AgentQuitEvent);
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitSignal, (_, _) => _ui.Post(_ => Quit(), null), null, Timeout.Infinite, executeOnlyOnce: true);

        // First run: register to start with Windows (the user can turn this off in Settings).
        _startupEnabled = StartupTask.IsEnabled();
        if (!_settings.StartupConfigured && _isAdmin)
        {
            _startupEnabled = StartupTask.Enable();
            MutateSettings(s => s.StartupConfigured = true);
        }
        else if (_startupEnabled && _isAdmin && !StartupTask.PointsHere())
        {
            // Installed somewhere new (or reinstalled): point the task at this copy.
            _startupEnabled = StartupTask.Enable();
        }

        _sampler = new Thread(SamplerLoop) { IsBackground = true, Name = "Sampler", Priority = ThreadPriority.BelowNormal };
        _sampler.Start();

        if (args.Contains("--open")) OpenApp(null);
        if (_isAdmin) ThreadPool.QueueUserWorkItem(_ => UpdateInstaller.Cleanup());
        // Updates: first a minute and a half after starting (Windows is still settling at sign-in), then every
        // six hours; GitHub itself is asked at most once a day.
        _updateTimer = new System.Threading.Timer(_ => _ = RunUpdaterAsync(), null, TimeSpan.FromSeconds(90), TimeSpan.FromHours(6));
    }

    // ── Updates ───────────────────────────────────────────────────────────

    private System.Threading.Timer? _updateTimer;
    private bool _updaterStarted;
    private int _updaterRunning;

    private async Task RunUpdaterAsync()
    {
        if (_stopping || Environment.ProcessPath is not { } exe || Interlocked.Exchange(ref _updaterRunning, 1) == 1) return;
        try
        {
            bool appOpen = _pipe.ClientCount > 0;
            var args = new List<string> { "--update" };
            // Installing a waiting update is for the first run after sign-in, and never under an open window.
            if (!_updaterStarted && _isAdmin && !appOpen) args.Add("--at-startup");
            if (appOpen) args.Add("--no-download"); // the app shows its own download
            _updaterStarted = true;

            using var child = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });
            if (child is null) return;
            await child.WaitForExitAsync();
            int code = child.ExitCode; // read now: the process object is disposed before the UI thread gets to it
            if (code is BackgroundUpdater.Downloaded or BackgroundUpdater.Available)
                _ui.Post(_ => OnUpdateFound(code == BackgroundUpdater.Downloaded), null);
        }
        catch (Exception ex)
        {
            Log.Error("update", ex);
        }
        finally
        {
            Volatile.Write(ref _updaterRunning, 0);
        }
    }

    /// <summary>
    /// UI thread: a newer version is out (downloaded or not). An open app is told, so it shows it at once;
    /// otherwise one notification per version says so.
    /// </summary>
    private void OnUpdateFound(bool downloaded)
    {
        if (_pipe.ClientCount > 0)
        {
            _pipe.Broadcast(new AgentMessage { T = "update", UpdateStatus = "found" });
            return;
        }
        var version = UpdateStore.ReadCheck()?.Latest?.Version.ToString(3);
        if (version is null || _settings.LastUpdateNotice == version) return;
        MutateSettings(s => s.LastUpdateNotice = version);
        string body = !downloaded ? "Click to update."
            : _settings.AutoUpdate && _isAdmin ? "It installs by itself the next time you start your PC, or click to update now."
            : "It's downloaded. Click to finish updating.";
        _notices.Show(new Notice(NoticeKind.Update, downloaded ? $"Version {version} is ready" : $"Version {version} is out", body));
    }

    // ── Sampler thread ────────────────────────────────────────────────────

    private void SamplerLoop()
    {
        // Today's totals come from the database, not the sensors: load and send them first, so the app's
        // Home page isn't left waiting while hardware discovery runs (several seconds on a fresh start).
        _tracker.Initialize();
        _lastToday = _tracker.Today();
        if (_pipe.ClientCount > 0) _pipe.Broadcast(new AgentMessage { T = "tick", Time = TimeUtil.NowUnixMs(), Today = _lastToday });

        var discovery = Stopwatch.StartNew();
        try
        {
            _sensors.Open();
        }
        catch (Exception ex)
        {
            Log.Error("sensors", ex);
        }
        Log.Write("agent", $"Sensors ready in {discovery.Elapsed.TotalSeconds:0.0} s ({_sensors.SensorCount} sensors)");
        _sensorsReady = true;
        if (_pipe.ClientCount > 0) _pipe.Broadcast(BuildHello());

        // Hardware discovery allocates a lot of short-lived data; hand it back once so the
        // always-running agent settles at its real (small) footprint.
        CompactMemory();

        RecordDrives();
        RestoreExtremes();

        var clock = Stopwatch.StartNew();
        // The first daily-recap check waits a little so it doesn't pop up the instant Windows starts.
        long lastActivity = 0, lastProc = 0, nextSensor = 0, nextProc = 0, nextDrives = 6 * 3600_000L, nextMinuteCheck = 30_000, nextCrashScan = 20_000;
        // While the app is open every sensor is read each second, which grows the heap (~40 MB); once the
        // last window closes, hand that back too.
        bool wasLive = false;
        long compactAt = long.MaxValue;

        while (!_stopping)
        {
            try
            {
                while (_samplerWork.TryDequeue(out var work)) work();

                long now = clock.ElapsedMilliseconds;
                var settings = _settings;
                bool live = _pipe.ClientCount > 0;
                if (wasLive && !live) compactAt = now + 15_000;
                if (live) compactAt = long.MaxValue;
                wasLive = live;
                if (now >= compactAt)
                {
                    compactAt = long.MaxValue;
                    CompactMemory();
                }

                // Activity, every second. A long gap means the PC was asleep: don't count it.
                double dt = (now - lastActivity) / 1000.0;
                lastActivity = now;
                if (dt > 10) dt = 0;
                long t0 = Stopwatch.GetTimestamp();
                var sample = _activity.Sample();
                _tracker.OnActivity(sample, dt);
                Measure("activity", t0);

                if (now >= nextSensor)
                {
                    t0 = Stopwatch.GetTimestamp();
                    // Sensors on the overlay stay live in games (the app is usually closed then).
                    _sensors.Watch(_overlayVisible ? [.. _settings.Overlay.Sensors.Select(s => s.Id)] : []);
                    _sensors.Update(everything: live, now);
                    Measure("sensors", t0);
                    t0 = Stopwatch.GetTimestamp();
                    var keys = _sensors.ReadKeys();
                    long unixMs = TimeUtil.NowUnixMs();
                    _history.Add(unixMs, _sensors);
                    _tracker.OnSensors(keys);
                    var values = _sensors.ReadAll();
                    ObserveExtremes(values);
                    if (_sensors.DrivesUpdated) _driveHistory.Add(unixMs, _sensors);

                    var alert = _alerts.Check(keys, _tracker.Activity.Name, settings.Alerts, now);
                    PublishToUi(keys, sample.Fullscreen, alert);
                    Measure("track+publish", t0);

                    if (live)
                    {
                        // A newly connected app gets every range once; after that, only what changed.
                        bool full = _sendAllExtremes;
                        _sendAllExtremes = false;
                        _pipe.Broadcast(new AgentMessage
                        {
                            T = "tick",
                            Time = unixMs,
                            Values = values,
                            Activity = _tracker.Activity,
                            Today = _lastToday,
                            Extremes = full ? _extremes.Snapshot() : _extremes.TakeChanges(),
                            ExtremesDay = _extremes.Day,
                            ExtremesFull = full,
                        });
                    }

                    int interval = live ? Math.Min(settings.LiveRefreshMs, settings.Tracking.SensorIntervalMs) : settings.Tracking.SensorIntervalMs;
                    // The overlay is read mid-game: keep it to the second.
                    if (_overlayVisible) interval = Math.Min(interval, 1000);
                    nextSensor = now + interval;
                }

                if (now >= nextProc)
                {
                    double pdt = lastProc == 0 ? settings.Tracking.ProcessIntervalSeconds : Math.Min((now - lastProc) / 1000.0, 60);
                    lastProc = now;
                    t0 = Stopwatch.GetTimestamp();
                    var snapshot = _procSampler.Sample();
                    Measure("processes", t0);
                    t0 = Stopwatch.GetTimestamp();
                    _activity.UpdateProcessMap(snapshot.PidToExe);
                    var windows = _activity.ScanWindows();
                    _tracker.OnProcesses(snapshot, windows, pdt);
                    Measure("windows+apps", t0);
                    if (live) _pipe.Broadcast(new AgentMessage { T = "procs", Procs = BuildProcs(snapshot, windows) });
                    nextProc = now + (live ? 2000 : settings.Tracking.ProcessIntervalSeconds * 1000);
                }

                if (now >= nextMinuteCheck)
                {
                    nextMinuteCheck = now + 60_000;
                    MaybeShowDailyRecap();
                    SaveExtremes();
                }

                if (now >= nextCrashScan)
                {
                    nextCrashScan = now + 10 * 60_000;
                    ScanCrashes();
                }

                if (now >= nextDrives)
                {
                    nextDrives = now + 6 * 3600_000L;
                    RecordDrives();
                }

                // Sleep until the next whole second (or until woken for work).
                _wake.WaitOne((int)(1000 - clock.ElapsedMilliseconds % 1000));
            }
            catch (Exception ex)
            {
                Log.Error("sampler", ex);
                _wake.WaitOne(2000);
            }
        }

        _tracker.Flush(closeAllSessions: true);
        SaveExtremes(force: true);
        _sensors.Close();
        _db.Dispose();
    }

    /// <summary>Sampler thread: widens today's range of every sensor with this reading.</summary>
    private void ObserveExtremes(float?[] values)
    {
        _extremes.BeginTick();
        var ids = _sensors.Ids;
        var isTemp = _sensors.IsTemperature;
        for (int i = 0; i < values.Length && i < ids.Length; i++)
        {
            // Only sensors actually read this tick: others keep old values (hardware only read while the
            // app is open, or the GPU while its cheaper fast path is in use), which must not count as today.
            if (!_sensors.IsFresh(i)) continue;
            // Without driver access some temperature sensors report 0 instead of nothing.
            if (values[i] is float v && !(isTemp[i] && v <= 0)) _extremes.Observe(ids[i], v);
        }
        // Key sensors may come from a cheaper source (the NVIDIA fast path) while the app is closed.
        foreach (var (key, index) in _sensors.Keys)
            if (index < ids.Length) _extremes.Observe(ids[index], _sensors.Read(key));
    }

    /// <summary>
    /// Sampler thread, at start: today's ranges saved before a restart, widened with the CPU/GPU
    /// temperatures in today's minute history (so a game played before the first run of this version counts).
    /// </summary>
    private void RestoreExtremes()
    {
        try
        {
            _extremes.Load(_db.GetMeta("extremes"));
            var (cpuMin, cpuMax, gpuMin, gpuMax, hotMax, memMax) = _db.TempRange(TimeUtil.ToUnix(DateTime.Today), TimeUtil.NowUnix() + 60);
            void Include(string key, double? min, double? max)
            {
                if (_sensors.Keys.TryGetValue(key, out int i) && i < _sensors.Ids.Length) _extremes.Include(_sensors.Ids[i], min, max);
            }
            Include(KeySensors.CpuTemp, cpuMin, cpuMax);
            Include(KeySensors.GpuTemp, gpuMin, gpuMax);
            Include(KeySensors.GpuHotSpot, null, hotMax);
            Include(KeySensors.GpuMemJunction, null, memMax);
        }
        catch (Exception ex)
        {
            Log.Error("extremes", ex);
        }
    }

    /// <summary>Sampler thread: stores today's ranges (only when they changed, unless forced).</summary>
    private void SaveExtremes(bool force = false)
    {
        if (!force && !_extremes.Dirty) return;
        _db.SetMeta("extremes", _extremes.Serialize());
        _extremes.Dirty = false;
    }

    /// <summary>
    /// Windows is shutting down or signing out: finish the session in progress and save today's ranges
    /// now, on the sampler thread (which owns the database), waiting a few seconds at most.
    /// </summary>
    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        using var done = new ManualResetEventSlim();
        RunOnSampler(() =>
        {
            try
            {
                _tracker.Flush(closeAllSessions: true);
                SaveExtremes(force: true);
            }
            catch (Exception ex)
            {
                Log.Error("agent", ex);
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait(TimeSpan.FromSeconds(3));
        Log.Write("agent", $"Saved before Windows {(e.Reason == SessionEndReasons.SystemShutdown ? "shut down" : "signed out")}");
    }

    private static void OnTimeChanged(object? sender, EventArgs e)
    {
        TimeZoneInfo.ClearCachedData();
        System.Globalization.CultureInfo.CurrentCulture.ClearCachedData();
    }

    /// <summary>Returns memory the agent no longer needs to Windows (a one-off full, compacting collection).</summary>
    private static void CompactMemory()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private void RunOnSampler(Action work)
    {
        _samplerWork.Enqueue(work);
        _wake.Set();
    }

    /// <summary>The overlay's chosen sensors with their labels: its own short name, else the name given on All sensors.</summary>
    private List<OverlaySensorReading> OverlaySensors(RigsightSettings settings)
    {
        var list = new List<OverlaySensorReading>();
        foreach (var x in settings.Overlay.Sensors)
        {
            if (_sensors.ReadSensor(x.Id) is not { } r) continue;
            string label = x.Label ?? settings.SensorLabels.GetValueOrDefault(x.Id) ?? r.Name;
            list.Add(new OverlaySensorReading(label, r.Kind, r.Value));
        }
        return list;
    }

    private void PublishToUi(KeyValues k, bool fullscreen, string? alert)
    {
        var settings = _settings;
        var activity = _tracker.Activity;
        bool drawsHistory = settings.Widgets.Any(w => w.Enabled && w.Style is WidgetStyle.Compact or WidgetStyle.Graph);
        var data = new WidgetData
        {
            CpuTemp = k.CpuTemp, CpuLoad = k.CpuLoad, CpuPower = k.CpuPower, CpuClock = k.CpuClock,
            GpuTemp = k.GpuTemp, GpuLoad = k.GpuLoad, GpuPower = k.GpuPower, GpuHotSpot = k.GpuHotSpot, GpuClock = k.GpuClock,
            VramUsedMb = k.GpuVramUsed, VramTotalMb = k.GpuVramTotal,
            RamLoad = k.RamLoad, RamUsedGb = k.RamUsed,
            RamTotalGb = k.RamUsed + k.RamAvailable,
            // Only the compact and graph widgets draw history lines; skip scanning it otherwise.
            CpuHistory = drawsHistory ? _history.Recent(KeySensors.CpuTemp, 300_000) : [],
            GpuHistory = drawsHistory ? _history.Recent(KeySensors.GpuTemp, 300_000) : [],
            Activity = activity,
            Today = _tracker.Today(),
            Sensors = OverlaySensors(settings),
        };
        _lastToday = data.Today;

        var tip = $"Rigsight\nCPU {Units.TempShort(k.CpuTemp)}  ·  GPU {Units.TempShort(k.GpuTemp)}  ·  RAM {Units.Short(SensorKind.Load, k.RamLoad)}";
        if (activity.Paused) tip += "\nTracking paused";
        else if (activity.Name is not null && activity.SessionActiveSec > 0) tip += $"\n{activity.Name}  ·  {Units.Duration(activity.SessionActiveSec)}";

        // Our own windows can report as fullscreen-sized; only treat other apps as fullscreen.
        bool otherFullscreen = fullscreen && activity.Exe is not null &&
            !activity.Exe.StartsWith("Rigsight", StringComparison.OrdinalIgnoreCase);

        // Tray health dot: amber within 12° of a limit, red at or over it.
        var a = settings.Alerts;
        double Margin(double? v, double limit) => v is double d ? d - limit : double.MinValue;
        double worst = Math.Max(Margin(k.CpuTemp, a.CpuLimit), Math.Max(Margin(k.GpuTemp, a.GpuLimit), Margin(k.GpuHotSpot, a.GpuHotSpotLimit)));
        int health = worst >= 0 ? 2 : worst >= -12 ? 1 : 0;

        _ui.Post(_ =>
        {
            long t0 = Stopwatch.GetTimestamp();
            _tray.Update(tip, health);
            _widgets.Update(data, otherFullscreen);
            _overlay.Update(data);
            _notices.SetFullscreen(otherFullscreen);
            if (Profiling) RunOnSampler(() => Measure("ui:tray+widgets", t0));
            if (alert is not null)
                _notices.Show(new Notice(NoticeKind.Alert, "Running hot", alert, activity.Path, "temperatures", Urgent: true));
        }, null);
    }

    private List<ProcInfo> BuildProcs(ProcessSnapshot snapshot, Dictionary<string, WindowState> windows)
    {
        return [.. snapshot.Apps.Values
            .OrderByDescending(a => a.MemMB)
            .Where((a, i) => i < 80 || windows.ContainsKey(a.Exe))
            .Select(a =>
            {
                var (name, path) = _apps.Describe(a.Exe, a.FirstPid);
                if (_settings.AppNames.TryGetValue(a.Exe, out var alias)) name = alias;
                return new ProcInfo
                {
                    Exe = a.Exe, Name = name, Path = path, Count = a.Count,
                    Cpu = Math.Round(a.Cpu, 1), MemMB = Math.Round(a.MemMB, 1), HasWindow = windows.ContainsKey(a.Exe),
                };
            })];
    }

    private void RecordDrives()
    {
        try
        {
            long day = TimeUtil.ToUnix(DateTime.Today);
            foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                double total = d.TotalSize / 1073741824.0, free = d.TotalFreeSpace / 1073741824.0;
                _db.UpsertDriveDay(new DriveDay { Day = day, Drive = d.Name, UsedGb = total - free, TotalGb = total });
            }
        }
        catch (Exception ex)
        {
            Log.Error("drives", ex);
        }
    }

    private void MaybeShowDailyRecap()
    {
        var settings = _settings;
        string today = DateTime.Today.ToString("yyyy-MM-dd");
        if (settings.LastRecapDay == today) return;
        MutateSettings(s => s.LastRecapDay = today);
        if (!settings.Alerts.DailyRecap || settings.LastRecapDay is null) return; // skip on the very first run

        var report = ReportBuilder.Build(_db, ReportRange.Day, DateTime.Today.AddDays(-1), settings);
        if (!report.HasData || report.ActiveSec < 300) return;

        var parts = new List<string> { $"Active {Units.Duration(report.ActiveSec)}" };
        var top = report.Apps.FirstOrDefault(a => a.ActiveSec > 0 && a.Category != AppCategory.System);
        if (top is not null) parts.Add($"{top.Name} {Units.Duration(top.ActiveSec)}");
        if (report.CpuTempPeak is { } cpu) parts.Add($"CPU peak {Units.TempShort(cpu.Value)}");
        if (report.GpuTempPeak is { } gpu) parts.Add($"GPU peak {Units.TempShort(gpu.Value)}");

        string text = string.Join("  ·  ", parts) + ". Click for the full recap.";
        _ui.Post(_ => _notices.Show(new Notice(NoticeKind.Recap, "Yesterday on your PC", text, Page: "reports", Arg: "yesterday")), null);
    }

    private void OnSessionEnded(SessionRow row, AppInfo app)
    {
        var settings = _settings;
        bool summaryWanted = settings.Alerts.SessionSummaries && row.IsGame && row.ActiveSec >= settings.Alerts.SessionSummaryMinMinutes * 60;
        if (!summaryWanted && !settings.Alerts.CrashNotifications) return;
        string name = AppResolver.DisplayName(app, settings);

        // Windows logs a crash a few seconds after the process dies, so wait a moment before deciding
        // between a normal summary and a (gentle) "what happened". After a crash, the summary is
        // skipped entirely unless crash notifications are on — nobody needs a scoreboard right then.
        _ = Task.Run(async () =>
        {
            await Task.Delay(12_000);
            var crash = CrashLogReader.RecentAppCrash(app.Exe, TimeSpan.FromMinutes(3));
            if (crash is not null)
            {
                RunOnSampler(ScanCrashes);
                if (!_settings.Alerts.CrashNotifications || _settings.IsCrashMuted(app.Exe)) return;
                var ex = CrashExplainer.Explain(crash, name);
                _ui.Post(_ => _notices.Show(new Notice(NoticeKind.Crash, $"{name} closed unexpectedly",
                    $"Likely cause: {ex.Culprit}. You played for {Units.Duration(row.ActiveSec)}. Details are on the Crashes page whenever you want them.",
                    app.Path, "crashes")), null);
                return;
            }
            if (!summaryWanted) return;
            string text = $"Played for {Units.Duration(row.ActiveSec)}. Peak CPU {Units.TempShort(row.CpuTempMax)}, GPU {Units.TempShort(row.GpuTempMax)}. Click for the full breakdown.";
            _ui.Post(_ => _notices.Show(new Notice(NoticeKind.Session, $"{name} session", text, app.Path, "apps", app.Exe)), null);
        });
    }

    private bool _crashesScanned;

    /// <summary>Sampler thread: copies new crash records from the Windows event logs into the database.</summary>
    private void ScanCrashes()
    {
        try
        {
            var since = _crashesScanned ? DateTime.Now.AddMinutes(-30) : DateTime.Now.AddDays(-90);
            var added = _db.InsertCrashes(CrashLogReader.ReadSince(since));
            bool firstScan = !_crashesScanned;
            _crashesScanned = true;
            if (!_settings.Alerts.CrashNotifications) return;

            // Tell the user about system-level problems they may not have noticed (app crashes are covered by sessions).
            var recent = added.Where(e => e.Kind is CrashKind.SystemCrash or CrashKind.UnexpectedShutdown or CrashKind.GpuDriverReset
                                          && e.Time > DateTime.Now.AddHours(firstScan ? -24 : -1)).ToList();
            foreach (var e in recent.TakeLast(2))
            {
                var ex = CrashExplainer.Explain(e);
                _ui.Post(_ => _notices.Show(new Notice(NoticeKind.Crash, ex.Title,
                    $"{e.Time:ddd h:mm tt} — {ex.Reason} See the Crashes page for what to try.", Page: "crashes")), null);
            }
        }
        catch (Exception ex)
        {
            Log.Error("crashes", ex);
        }
    }

    /// <summary>Shows an example of each notification so the user can judge them (Settings → Notifications).</summary>
    private void PreviewNotification(string? kind)
    {
        var s = _settings;
        var activity = _tracker.Activity;
        Notice notice = kind switch
        {
            "alert" => new(NoticeKind.Alert, "Running hot",
                $"GPU {Units.TempShort(s.Alerts.GpuLimit + 3)} has been above your limit for {s.Alerts.SustainSeconds}+ seconds while Cyberpunk 2077 was running.", Page: "temperatures", Urgent: true),
            "session" => new(NoticeKind.Session, "Dota 2 session",
                $"Played for 2h 14m. Peak CPU {Units.TempShort(71)}, GPU {Units.TempShort(68)}. Click for the full breakdown.", activity.Path, "apps"),
            "crash" => new(NoticeKind.Crash, "Dota 2 closed unexpectedly",
                "Likely cause: NVIDIA driver. You played for 48m. Details are on the Crashes page whenever you want them.", activity.Path, "crashes"),
            _ => new(NoticeKind.Recap, "Yesterday on your PC",
                $"Active 6h 12m  ·  Dota 2 3h 05m  ·  CPU peak {Units.TempShort(78)}  ·  GPU peak {Units.TempShort(72)}. Click for the full recap.", Page: "reports", Arg: "yesterday"),
        };
        _notices.Show(notice with { Title = notice.Title + " (preview)" }, bypassQuiet: true);
    }

    // ── App connection ────────────────────────────────────────────────────

    private AgentMessage BuildHello()
    {
        var hello = new AgentMessage
        {
            T = "hello",
            Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3),
            IsAdmin = _isAdmin,
            StartupEnabled = _startupEnabled,
            Settings = _settings,
            Page = _pendingPage,
            Arg = _pendingArg,
            OverlayVisible = _overlayVisible,
            OverlayHotkeyTaken = _overlay.HotkeyTaken,
            RtssState = _overlay.RtssState,
            Today = _lastToday,
        };
        if (_sensorsReady)
        {
            hello.Hardware = _sensors.Schema;
            hello.Keys = _sensors.Keys;
            hello.History = [.. _history.Snapshot(), .. _driveHistory.Snapshot()];
            hello.Drives = _sensors.DriveHealth;
            _sendAllExtremes = true;
        }
        _pendingPage = _pendingArg = null;
        return hello;
    }

    private void OnUiMessage(UiMessage msg)
    {
        if (msg.T == "settings" && msg.Settings is not null)
        {
            // Through the store, so the app's copy gets the same repairs and limits as a file on disk
            // (case-insensitive app lookups, clamped intervals, a valid shortcut...).
            var incoming = SettingsStore.Deserialize(SettingsStore.Serialize(msg.Settings));
            MutateSettings(s =>
            {
                // Fields the agent owns are kept from the agent's copy (pausing goes through commands,
                // so a tray "Pause" is never undone by the app sending settings it had before).
                incoming.StartupConfigured = s.StartupConfigured;
                incoming.LastRecapDay = s.LastRecapDay;
                incoming.LastUpdateNotice = s.LastUpdateNotice;
                incoming.Tracking.PausedUntil = s.Tracking.PausedUntil;
                foreach (var w in incoming.Widgets)
                {
                    var mine = s.Widgets.FirstOrDefault(x => x.Style == w.Style);
                    if (mine is not null) { w.X = mine.X; w.Y = mine.Y; }
                }
            }, replaceWith: incoming);
            return;
        }

        switch (msg.Cmd)
        {
            case "clear-history":
                RunOnSampler(() => _tracker.ClearHistory());
                break;
            case "startup-on" or "startup-off":
                _startupEnabled = msg.Cmd == "startup-on" ? StartupTask.Enable() : !StartupTask.Disable();
                _startupEnabled = StartupTask.IsEnabled();
                _pipe.Broadcast(new AgentMessage { T = "status", StartupEnabled = _startupEnabled });
                break;
            case "pause":
                _ui.Post(_ => PauseFor(int.TryParse(msg.Arg, out var m) ? m : 60), null);
                break;
            case "resume":
                _ui.Post(_ => Resume(), null);
                break;
            case "quit":
                _ui.Post(_ => Quit(), null);
                break;
            case "render-previews":
                RenderPreviews(_settings);
                break;
            case "preview-notification":
                _ui.Post(_ => PreviewNotification(msg.Arg), null);
                break;
            case "overlay-toggle":
                _ui.Post(_ => _overlay.Toggle(), null);
                break;
            case "start-rtss":
                _ui.Post(_ => _overlay.StartRtss(), null);
                break;
            case "install-rtss":
                _ui.Post(_ => _overlay.InstallRtss(_ui), null);
                break;
            case "overlay-status":
                _ui.Post(_ => OnOverlayStateChanged(), null);
                break;
            case "restart-elevated":
                _ui.Post(_ => RestartElevated(), null);
                break;
            case "install-update":
                _ = Task.Run(async () =>
                {
                    bool started = _isAdmin && await UpdateInstaller.RunAsync(msg.Arg);
                    _pipe.Broadcast(new AgentMessage { T = "update", UpdateStatus = started ? "started" : "failed" });
                });
                break;
        }
    }

    /// <summary>Opens the Rigsight app (or brings it to the front) on an optional page.</summary>
    private void OpenApp(string? page, string? arg = null)
    {
        if (_pipe.ClientCount > 0)
        {
            Win32.AllowSetForegroundWindow(Win32.ASFW_ANY);
            _pipe.Broadcast(new AgentMessage { T = "navigate", Page = page, Arg = arg });
            return;
        }

        _pendingPage = page;
        _pendingArg = arg;
        var exe = RigsightPaths.Sibling(RigsightPaths.AppExe);
        if (!File.Exists(exe))
        {
            Log.Write("agent", $"App not found at {exe}");
            return;
        }
        try
        {
            // Launch through Explorer so the app runs with normal (non-admin) rights.
            if (_isAdmin) Process.Start("explorer.exe", $"\"{exe}\"");
            else Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("agent", ex);
        }
    }

    // ── Settings ──────────────────────────────────────────────────────────

    /// <summary>
    /// The agent is the only writer of settings.json. Every change goes through here: copy, change,
    /// save, apply locally, and tell the app.
    /// </summary>
    private void MutateSettings(Action<RigsightSettings> change) => MutateSettings(change, replaceWith: null);

    private void MutateSettings(Action<RigsightSettings> change, RigsightSettings? replaceWith)
    {
        RigsightSettings updated;
        lock (_settingsLock)
        {
            var current = _settings;
            updated = replaceWith ?? current.Clone();
            change(replaceWith is null ? updated : current);
            _settings = updated;
            SettingsStore.Save(updated);
        }

        Units.Fahrenheit = updated.UseFahrenheit;
        RunOnSampler(() => _tracker.SetSettings(updated));
        _ui.Post(_ =>
        {
            _widgets.Apply(updated);
            _overlay.Apply(updated.Overlay);
            // Keep the app's widget previews in sync with theme changes.
            if (_pipe.ClientCount > 0) RenderPreviews(updated);
        }, null);
        _pipe.Broadcast(new AgentMessage { T = "settings", Settings = updated });
    }

    /// <summary>
    /// Writes widget and overlay preview images and tells the app they're ready. The overlay's sensors are read
    /// on the sampler thread for these settings (so one just added shows straight away), then drawn on the UI thread.
    /// </summary>
    private void RenderPreviews(RigsightSettings settings) =>
        RunOnSampler(() =>
        {
            var sensors = OverlaySensors(settings);
            _ui.Post(_ => DrawPreviews(settings, sensors), null);
        });

    private void DrawPreviews(RigsightSettings settings, List<OverlaySensorReading> sensors)
    {
        _widgets.RenderPreviews(settings);
        try
        {
            _overlay.RenderPreview(Path.Combine(RigsightPaths.DataDir, "previews"), sensors);
        }
        catch (Exception ex)
        {
            Log.Error("overlay", ex);
        }
        _pipe.Broadcast(new AgentMessage { T = "previews" });
    }

    /// <summary>UI thread: the overlay appeared, disappeared, or its shortcut turned out to be taken.</summary>
    private void OnOverlayStateChanged()
    {
        _overlayVisible = _overlay.Visible;
        _pipe.Broadcast(new AgentMessage
        {
            T = "overlay", OverlayVisible = _overlay.Visible, OverlayHotkeyTaken = _overlay.HotkeyTaken, RtssState = _overlay.RtssState,
        });
    }

    /// <summary>
    /// The overlay was turned on over a fullscreen game it can't reach. Notifications wait until the game
    /// is closed or minimised (nothing can show over it), then explain what to do.
    /// </summary>
    private void OnOverlayCantReachGame(string app)
    {
        var (title, body) = _overlay.RtssState switch
        {
            "running" => ($"Restart {app} to see the overlay",
                $"{app} was already open when RivaTuner started, so RivaTuner isn't drawing in it yet. Restart it and the overlay will show."),
            "stopped" => ("The overlay can't show in fullscreen games",
                "RivaTuner isn't running, and fullscreen games need it for the overlay. Start it from the Overlay page, then restart the game."),
            _ => ("The overlay can't show in fullscreen games",
                "Fullscreen games need RivaTuner Statistics Server (free) for the overlay. Install it from the Overlay page, then restart the game."),
        };
        _notices.Show(new Notice(NoticeKind.Overlay, title, body, _tracker.Activity.Path, "overlay"));
    }

    private void PauseFor(int minutes) =>
        MutateSettings(s => s.Tracking.PausedUntil = minutes < 0 ? -1 : TimeUtil.NowUnix() + minutes * 60L);

    private void Resume() => MutateSettings(s => s.Tracking.PausedUntil = 0);

    /// <summary>Starts an elevated copy that takes over, then exits (needs the user to accept UAC).</summary>
    private void RestartElevated()
    {
        if (_isAdmin || Environment.ProcessPath is not { } exe) return;
        try
        {
            Process.Start(new ProcessStartInfo(exe, "--replace") { UseShellExecute = true, Verb = "runas" });
            Quit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC declined: keep running without admin.
        }
    }

    private void Quit()
    {
        if (_stopping) return;
        SystemEvents.SessionEnding -= OnSessionEnding;
        SystemEvents.TimeChanged -= OnTimeChanged;
        _quitWait?.Unregister(null);
        _stopping = true;
        _updateTimer?.Dispose();
        _wake.Set();
        _sampler.Join(5000);

        // Each step is independent: a failure in one must never leave the agent half-closed.
        foreach (var step in new Action[] { _pipe.Dispose, _widgets.CloseAll, _overlay.Dispose, _notices.CloseAll, _tray.Dispose })
        {
            try { step(); }
            catch (Exception ex) { Log.Error("agent", ex); }
        }
        ExitThread();
    }
}
