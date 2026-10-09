using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Reflection;
using Rigsight.Agent.Ipc;
using Rigsight.Agent.Native;
using Rigsight.Agent.Network;
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

    // Replaced (on the sampler thread) when the hardware is scanned again: another program started or quit (see
    // HardwareApps), or the user asked to read everything again. Read it once into a local where it's used twice.
    private volatile SensorHost _sensors = new();
    private volatile SensorStatus _sensorStatus = new();
    // The last hardware scan never finished: skip the risky parts until the user asks to read them again.
    private bool _safeMode;
    // Kernel memory ran away during a scan this run: the same.
    private bool _stoppedForMemory;
    // Sampler thread: when the programs we step aside for were last seen, for leaving them the hardware a little longer.
    private SensorParts _yieldedParts;
    private int _absentChecks;
    private static string ScanMarker => Path.Combine(RigsightPaths.DataDir, "sensors-scan.pending");
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
    private readonly GpuSampler _gpuSampler = new();
    private readonly ProcessLabels _procLabels = new();
    private bool _procsNow; // sampler thread: sample processes on this pass
    private bool _sensorsNow; // sampler thread: read the sensors (and redraw the taskbar readings) on this pass
    private readonly ActivityMonitor _activity = new();
    // Internet use (sampler thread): the trace of every app's bytes (null without admin rights), this PC's addresses (read
    // again when the adapters change), drops, and the last minute's speeds for an app that connects.
    private NetworkTrace? _netTrace;
    private volatile AddressBook _addresses = AddressBook.Empty;
    private volatile bool _addressesChanged = true;
    private readonly ConnectionWatch _connection = new();
    private readonly TraceWatch _traceWatch = new();
    private readonly SensorHealth _sensorHealth = new();
    private readonly DisplayDriverWatch _displayWatch = new();
    private bool _gpusPaused, _restarting; // sampler thread
    private readonly string[] _args;
    private IReadOnlyDictionary<int, string> _running = new Dictionary<int, string>();
    private readonly Queue<NetLive> _netHistory = new();
    private readonly Lock _netHistoryLock = new();
    // The PC was just turned on or woke from sleep: the recap is due once the user is here (sampler thread reads it).
    private volatile bool _justTurnedOn = DailyRecap.JustSignedIn();
    private bool _presentNow;
    private readonly AlertMonitor _alerts = new();
    private readonly PipeServer _pipe;
    private readonly TrayController _tray;
    private readonly TrayReadings _trayReadings;
    private readonly TaskbarStrip _taskbarStrip;
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
    private bool _procsSampled;

    /// <summary>With RIGSIGHT_PROFILE=1: the private memory at a step of starting up, to see which step costs what.</summary>
    private static void MemoryAt(string step)
    {
        if (Profiling) Log.Write("profile", $"{step}: {Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0:0.0} MB private");
    }

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
        _args = args;
        MemoryAt("starting");

        _settings = SettingsStore.Load();
        ForgetRiggySetting();
        Units.Fahrenheit = _settings.UseFahrenheit;
        DarkMenuRenderer.Theme = _settings.Theme;
        // A new install's first start, before the hardware is ever scanned: if an RGB or fan-control app is running,
        // step aside for it from the very first scan (that first scan is when two programs colliding does the most harm).
        if (!_settings.HardwareAppsChecked)
        {
            var apps = HardwareApps.Running();
            _settings.HardwareAppsChecked = true;
            if (apps.Count > 0) _settings.YieldToHardwareApps = true;
            SettingsStore.Save(_settings);
            Log.Write("sensors", apps.Count > 0
                ? $"First start: {string.Join(", ", apps.Select(a => a.Name))} running, so Rigsight steps aside for RGB and fan apps"
                : "First start: no RGB or fan apps running");
        }

        _db = RigsightDb.OpenWriter();
        _apps = new AppResolver(_db);
        _tracker = new Tracker(_db, _apps);
        _tracker.SetSettings(_settings);
        _tracker.SessionEnded += OnSessionEnded;
        MemoryAt("database open");

        _widgets = new WidgetManager(() => _settings, MutateSettings, id => OpenApp("widgets", id));
        MemoryAt("widgets");
        _overlay = new OverlayManager(isAdmin);
        _overlay.StateChanged += OnOverlayStateChanged;
        _overlay.CantReachGame += OnOverlayCantReachGame;
        _trayReadings = new TrayReadings(open: () => OpenApp(null),
            remove: ids => MutateSettings(s => s.TraySensors.RemoveAll(ids.Contains)),
            setGrouped: on => MutateSettings(s => s.TrayStyle = on ? TrayStyle.Grouped : TrayStyle.Icons),
            openPage: () => OpenApp("taskbar"));
        _taskbarStrip = new TaskbarStrip(open: () => OpenApp(null),
            remove: ids => MutateSettings(s => s.TraySensors.RemoveAll(ids.Contains)),
            setStyle: style => MutateSettings(s => s.TrayStyle = style),
            openPage: () => OpenApp("taskbar"));
        _tray = new TrayController(() => OpenApp(null), _widgets.BuildTrayMenu(), _overlay.TrayItem, PauseFor, Resume,
            () => _settings.Tracking.IsPaused(TimeUtil.NowUnix()), () => Quit("the tray menu"));
        _notices = new NotificationCenter(() => _settings, _tray, OpenApp, n => _overlay.ShowInGame(n, _settings.Alerts.CardSeconds));

        _pipe = new PipeServer(BuildHello, OnUiMessage);
        _pipe.Start();
        _widgets.Apply(_settings);
        _overlay.Apply(_settings.Overlay);
        if (NeedsRtss(_settings) && !RigsightPaths.IsTestInstance) _overlay.EnsureRtssRunning();

        // Save before Windows shuts down or signs out (otherwise the process is simply killed, losing
        // the game session in progress), and let the installer ask for a clean stop ("--quit").
        SystemEvents.SessionEnding += OnSessionEnding;
        // A time zone change (e.g. travelling) must move "today" too; .NET caches the zone otherwise.
        SystemEvents.TimeChanged += OnTimeChanged;
        // Waking from sleep is turning the PC on again, for the daily recap.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, RigsightPaths.AgentQuitEvent);
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitSignal, (_, _) => _ui.Post(_ => Quit("a quit signal (the installer, or --quit)"), null), null, Timeout.Infinite, executeOnlyOnce: true);

        // First run: register to start with Windows (the user can turn this off in Settings). A test copy leaves
        // the real startup task alone.
        _startupEnabled = StartupTask.IsEnabled();
        bool manageStartup = _isAdmin && !RigsightPaths.IsTestInstance;
        if (!_settings.StartupConfigured && manageStartup)
        {
            _startupEnabled = StartupTask.Enable();
            MutateSettings(s => s.StartupConfigured = true);
        }
        else if (_startupEnabled && manageStartup && !StartupTask.PointsHere())
        {
            // Installed somewhere new (or reinstalled): point the task at this copy.
            _startupEnabled = StartupTask.Enable();
        }

        _displayWatch.Start();
        _sampler = new Thread(SamplerLoop) { IsBackground = true, Name = "Sampler", Priority = ThreadPriority.BelowNormal };
        _sampler.Start();

        if (args.Contains("--open")) OpenApp(null);
        if (RigsightPaths.IsTestInstance) return; // no updates in a test copy
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
        // Clicked, the app opens asking to restart (or downloading), not on its usual page with no word of the update.
        _notices.Show(new Notice(NoticeKind.Update, downloaded ? $"Version {version} is ready" : $"Version {version} is out", body, Page: "update"));
    }

    // ── Sampler thread ────────────────────────────────────────────────────

    private void SamplerLoop()
    {
        // Today's totals come from the database, not the sensors: load and send them first, so the app's
        // Home page isn't left waiting while hardware discovery runs (several seconds on a fresh start).
        _tracker.Initialize();
        _lastToday = _tracker.Today();
        if (_pipe.ClientCount > 0) _pipe.Broadcast(new AgentMessage { T = "tick", Time = TimeUtil.NowUnixMs(), Today = _lastToday });

        _safeMode = ScanGuard.LastScanUnfinished(ScanMarker);
        if (_safeMode) Log.Write("sensors", "The last hardware scan didn't finish: skipping the motherboard, fan hubs and power supply");
        OpenSensors();

        // Hardware discovery allocates a lot of short-lived data; hand it back once so the
        // always-running agent settles at its real (small) footprint.
        CompactMemory();

        RecordDrives();
        RestoreExtremes();
        StartNetwork();

        var clock = Stopwatch.StartNew();
        // The first daily-recap check waits a little so it doesn't pop up the instant Windows starts.
        long lastNet = 0, nextAddresses = 0, nextTraceCheck = 60_000;
        long lastActivity = 0, lastProc = 0, nextSensor = 0, nextProc = 0, nextDrives = 6 * 3600_000L, nextMinuteCheck = 30_000, nextCrashScan = 20_000, nextChangeScan = 40_000,
            nextHardwareApps = 30_000;
        // While the app is open every sensor is read each second, which grows the heap (~40 MB); once the
        // last window closes, hand that back too.
        bool wasLive = false;
        long compactAt = long.MaxValue;

        while (!_stopping)
        {
            try
            {
                while (_samplerWork.TryDequeue(out var work)) work();

                // The RAM sticks, found in the background since the sensors opened: into the list, and tell the app.
                if (_sensors.TakeMemory())
                {
                    Log.Write("sensors", $"Now {_sensors.SensorCount} sensors");
                    MemoryAt("RAM sticks in");
                    CompactMemory();
                    if (_pipe.ClientCount > 0) _pipe.Broadcast(BuildHello());
                }

                long now = clock.ElapsedMilliseconds;
                var settings = _settings;
                // A timed pause that has run out is cleared, so the tray menu and Settings show tracking again.
                if (settings.Tracking.PausedUntil > 0 && settings.Tracking.PausedUntil <= TimeUtil.NowUnix())
                    MutateSettings(s => { if (s.Tracking.PausedUntil > 0 && s.Tracking.PausedUntil <= TimeUtil.NowUnix()) s.Tracking.PausedUntil = 0; });
                bool live = _pipe.ClientCount > 0;
                if (wasLive && !live) compactAt = now + 15_000;
                if (live) compactAt = long.MaxValue;
                wasLive = live;
                if (now >= compactAt)
                {
                    compactAt = long.MaxValue;
                    CompactMemory();
                }

                double dt = ActivityStep(lastActivity, now);
                lastActivity = now;
                long t0 = Stopwatch.GetTimestamp();
                var sample = _activity.Sample();
                _presentNow = !sample.Locked && (sample.IdleMs < settings.Tracking.IdleMinutes * 60_000L
                                                 || sample.Fullscreen && settings.Tracking.FullscreenCountsAsActive);
                _tracker.OnActivity(sample, dt);
                Measure("activity", t0);

                t0 = Stopwatch.GetTimestamp();
                if (_addressesChanged || now >= nextAddresses)
                {
                    _addressesChanged = false;
                    _addresses = AddressBook.Read();
                    nextAddresses = now + 60_000;
                }
                if (now >= nextTraceCheck)
                {
                    nextTraceCheck = now + 60_000;
                    CheckNetworkTrace();
                }
                // Once a second (a pass woken early for other work leaves it be: a few milliseconds' bytes say nothing of a speed).
                if (lastNet == 0 || now - lastNet >= 900)
                {
                    SampleNetwork(lastNet == 0 ? 1 : Math.Min((now - lastNet) / 1000.0, 60));
                    lastNet = now;
                }
                Measure("network", t0);

                if (now >= nextSensor || _sensorsNow)
                {
                    _sensorsNow = false;
                    t0 = Stopwatch.GetTimestamp();
                    // Sensors on the overlay stay live in games (the app is usually closed then).
                    // And those in the taskbar, which are always on show.
                    _sensors.Watch([.. _overlayVisible ? _settings.Overlay.Sensors.Select(s => s.Id) : [], .. _settings.TraySensors, .. WidgetSensorIds(_settings)]);
                    CheckDisplayDriver();
                    _sensors.Update(everything: live, now);
                    if (_sensorHealth.Check(_sensors.Unwell, now))
                    {
                        Log.Write("sensors", "Some hardware has stopped answering (a driver updated, or a device unplugged): finding the sensors again");
                        OpenSensors();
                        _sensors.Update(everything: live, now);
                    }
                    Measure("sensors", t0);
                    t0 = Stopwatch.GetTimestamp();
                    var keys = _sensors.ReadKeys();
                    long unixMs = TimeUtil.NowUnixMs();
                    _history.Add(unixMs, _sensors);
                    _tracker.OnSensors(keys);
                    _tracker.OnFans(_sensors.ReadFans());
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
                            // No trace on an agent that should have one, after the remedies: the page says it isn't recording.
                            Net = _netTrace is not null ? _tracker.NetNow : _isAdmin && _traceWatch.Stalled ? NotRecording : null,
                        });
                    }

                    int interval = live ? Math.Min(settings.LiveRefreshMs, settings.Tracking.SensorIntervalMs) : settings.Tracking.SensorIntervalMs;
                    // The overlay is read mid-game: keep it to the second.
                    if (_overlayVisible) interval = Math.Min(interval, 1000);
                    nextSensor = now + interval;
                }

                if (now >= nextProc || _procsNow)
                {
                    _procsNow = false;
                    double pdt = lastProc == 0 ? settings.Tracking.ProcessIntervalSeconds : ProcessStep(lastProc, now, settings.Tracking.ProcessIntervalSeconds);
                    lastProc = now;
                    t0 = Stopwatch.GetTimestamp();
                    bool firstProcs = !_procsSampled;
                    _procsSampled = true;
                    if (firstProcs) MemoryAt("before the first process sample");
                    var snapshot = _procSampler.Sample();
                    if (firstProcs) MemoryAt("processes sampled");
                    snapshot.Gpu = _gpuSampler.Sample(snapshot);
                    if (firstProcs) MemoryAt("GPU use by app sampled");
                    Measure("processes", t0);
                    t0 = Stopwatch.GetTimestamp();
                    _running = snapshot.PidToExe;
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

                if (now >= nextHardwareApps)
                {
                    nextHardwareApps = now + 30_000;
                    CheckHardwareApps();
                }

                if (now >= nextCrashScan)
                {
                    nextCrashScan = now + 10 * 60_000;
                    ScanCrashes();
                }

                if (now >= nextChangeScan)
                {
                    nextChangeScan = now + Inventory.ScanMinutes * 60_000;
                    ScanChanges();
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
        _netTrace?.Dispose();
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        _sensors.Close();
        _gpuSampler.Dispose();
        _db.Dispose();
    }

    /// <summary>
    /// Sampler thread, before each read: a graphics card's driver that is going or coming (see <see cref="DisplayDriverWatch"/>)
    /// stops the cards being read at once, and once it has settled the agent starts again to read them through the new driver.
    /// </summary>
    private void CheckDisplayDriver()
    {
        if (!_displayWatch.Changed) return;
        _sensors.PauseGpus();
        if (!_gpusPaused)
        {
            _gpusPaused = true;
            Log.Write("sensors", "A graphics card's driver is changing: leaving the graphics cards alone, and starting again once it has settled");
        }
        if (_restarting || !_displayWatch.Settled(Environment.TickCount64)) return;
        _restarting = true;
        _ui.Post(_ => Restart("a graphics driver that changed"), null);
    }

    /// <summary>
    /// Sampler thread: scans the hardware (again), leaving alone what an RGB or fan-control program is driving right now
    /// (see <see cref="HardwareApps"/>) and, after a scan that never finished or ran Windows' kernel memory up, the risky
    /// parts altogether (see <see cref="ScanGuard"/>). Nothing reads sensors meanwhile: this runs on the sampler thread.
    /// </summary>
    private void OpenSensors()
    {
        var apps = _settings.YieldToHardwareApps ? HardwareApps.Running() : [];
        _yieldedParts = HardwareApps.PartsFor(apps);
        _absentChecks = 0;
        bool safe = _safeMode || _stoppedForMemory;
        var skip = _yieldedParts | (safe ? SensorParts.Risky : SensorParts.None);

        _sensorsReady = false;
        _sensors.Close();
        var host = new SensorHost(skip, gpus: !_displayWatch.Changed);
        var scan = Stopwatch.StartNew();
        using (var guard = new ScanGuard(ScanMarker, keepMarker: safe))
        {
            guard.Exceeded += grown => _ui.Post(_ => _tray.ShowNotification("Rigsight stopped reading some sensors",
                "Windows' memory kept growing while it read the motherboard and fan hubs. Restart your PC if it stays slow.", warning: true), null);
            try
            {
                host.Open();
            }
            catch (Exception ex)
            {
                Log.Error("sensors", ex);
            }
            MemoryAt("sensors open");
            if (guard.Tripped) _stoppedForMemory = true;
        }
        if (_stoppedForMemory && !safe)
        {
            // It ran away with the risky parts in: read everything else without them.
            host.Close();
            OpenSensors();
            return;
        }

        _sensors = host;
        _gpuSampler.Reset(); // the graphics card may be a different one to Windows now (a driver update gives it a new identity)
        _sensorStatus = BuildSensorStatus(apps);
        var parts = new[] { SensorParts.Motherboard, SensorParts.FanHubs, SensorParts.PowerSupply }.Where(p => skip.HasFlag(p)).Select(HardwareApps.PartName);
        string left = skip == SensorParts.None ? ""
            : $", leaving out {string.Join(", ", parts).ToLowerInvariant()}" + (apps.Count > 0 ? $" (for {string.Join(", ", apps.Select(a => a.Name))})" : "") + (safe ? " (safe mode)" : "");
        Log.Write("agent", $"Sensors ready in {scan.Elapsed.TotalSeconds:0.0} s ({host.SensorCount} sensors{left})");
        _sensorsReady = true;
        if (_pipe.ClientCount > 0) _pipe.Broadcast(BuildHello());

        // The RAM sticks take seconds to find (the memory bus is probed): readings are already going out, so find them
        // meanwhile, under the same guard as the scan above. The sampler adds them when they're ready (TakeMemory).
        host.OpenMemoryInBackground(() =>
        {
            var took = Stopwatch.StartNew();
            using (var guard = new ScanGuard(ScanMarker, keepMarker: safe))
            {
                guard.Exceeded += grown => _ui.Post(_ => _tray.ShowNotification("Rigsight stopped reading some sensors",
                    "Windows' memory kept growing while it read the RAM sticks. Restart your PC if it stays slow.", warning: true), null);
                host.OpenMemory();
                if (guard.Tripped) _stoppedForMemory = true;
            }
            Log.Write("sensors", $"RAM sticks found in {took.Elapsed.TotalSeconds:0.0} s");
        });
    }

    /// <summary>
    /// Sampler thread, every half minute: scans again when an RGB or fan-control program has started (at once, so the two
    /// don't share the hardware) or has been gone for a whole minute (so a program restarting doesn't flip it back and forth).
    /// </summary>
    private void CheckHardwareApps()
    {
        var wanted = _settings.YieldToHardwareApps ? HardwareApps.PartsFor(HardwareApps.Running()) : SensorParts.None;
        if ((wanted & ~_yieldedParts) != SensorParts.None)
        {
            OpenSensors();
        }
        else if (wanted != _yieldedParts)
        {
            if (++_absentChecks >= 2) OpenSensors();
        }
        else
        {
            _absentChecks = 0;
        }
    }

    private SensorStatus BuildSensorStatus(List<HardwareApps.Known> apps)
    {
        var status = new SensorStatus { SafeMode = _safeMode, StoppedForMemory = _stoppedForMemory };
        foreach (var part in new[] { SensorParts.Motherboard, SensorParts.FanHubs, SensorParts.PowerSupply })
        {
            var by = apps.Where(a => a.Parts.HasFlag(part)).Select(a => a.Name).ToList();
            if (by.Count > 0) status.Paused.Add(new SkippedSensors { Part = HardwareApps.PartName(part), Because = by });
        }
        return status;
    }

    /// <summary>Sampler thread: the user asked to read everything again after a safe-mode start or a memory stop.</summary>
    private void RetryAllSensors()
    {
        Log.Write("sensors", "Reading everything again (asked from Settings)");
        ScanGuard.Forget(ScanMarker);
        _safeMode = false;
        _stoppedForMemory = false;
        OpenSensors();
    }

    /// <summary>
    /// Seconds an app was open since the last look at the processes. Far longer than the looks are apart means the PC was
    /// asleep (or the agent held up): not counted, where it used to add a minute of "open in the background" to every
    /// app with a window each time the PC woke.
    /// </summary>
    internal static double ProcessStep(long lastMs, long nowMs, int intervalSeconds)
    {
        double dt = (nowMs - lastMs) / 1000.0;
        return dt > Math.Max(10, intervalSeconds * 2) ? 0 : dt;
    }

    /// <summary>Seconds to count since the last activity sample. A long gap means the PC was asleep: don't count it.</summary>
    internal static double ActivityStep(long lastMs, long nowMs)
    {
        double dt = (nowMs - lastMs) / 1000.0;
        return dt > 10 ? 0 : dt;
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

    /// <summary>The sensors on shown widgets (kept live while they show, as the overlay's are).</summary>
    private static IEnumerable<string> WidgetSensorIds(RigsightSettings settings) =>
        settings.Widgets.Where(w => w.Enabled).SelectMany(WidgetCatalog.ItemsOf).Select(i => WidgetCatalog.Sensor(i.Id)).OfType<string>().Distinct();

    /// <summary>The widgets' sensors: the name given on All sensors, else the sensor's own (a widget's own short name wins when drawn).</summary>
    private Dictionary<string, OverlaySensorReading> WidgetSensors(RigsightSettings settings)
    {
        var map = new Dictionary<string, OverlaySensorReading>();
        foreach (var id in WidgetSensorIds(settings))
            if (_sensors.ReadSensor(id) is { } r)
                map[id] = new OverlaySensorReading(settings.SensorLabels.GetValueOrDefault(id) ?? r.Name, r.Kind, r.Value);
        return map;
    }

    /// <summary>Five minutes of each reading a tile or graph shows beyond CPU and GPU temperature (which have their own).</summary>
    private Dictionary<OverlayMetric, float[]> WidgetHistories(HashSet<OverlayMetric> metrics)
    {
        var map = new Dictionary<OverlayMetric, float[]>();
        foreach (var m in metrics)
        {
            string? key = m switch
            {
                OverlayMetric.GpuHotSpot => KeySensors.GpuHotSpot,
                OverlayMetric.CpuLoad => KeySensors.CpuLoad,
                OverlayMetric.GpuLoad => KeySensors.GpuLoad,
                OverlayMetric.Ram => KeySensors.RamLoad,
                _ => null,
            };
            if (key is not null) map[m] = _history.Recent(key, 300_000);
        }
        return map;
    }

    /// <summary>The taskbar's sensors (sampler thread): the name given on All sensors, else the sensor's own.</summary>
    private List<TrayReading> TrayReadingsFor(RigsightSettings settings)
    {
        var list = new List<TrayReading>(settings.TraySensors.Count);
        foreach (var id in settings.TraySensors)
        {
            list.Add(_sensors.ReadSensor(id) is { } r
                ? new TrayReading(id, settings.SensorLabels.GetValueOrDefault(id) ?? r.Name, r.Kind, r.Value, TrayParts.PartOf(r.HardwareType), r.HardwareName,
                    TrayParts.ShortLabel(r.Kind, r.Name, TrayParts.PartOf(r.HardwareType), settings.SensorLabels.GetValueOrDefault(id)))
                : new TrayReading(id, _sensorsReady ? "Not found on this PC" : "Starting…", SensorKind.Factor, null, TrayPart.Other, ""));
        }
        return list;
    }

    private void PublishToUi(KeyValues k, bool fullscreen, string? alert)
    {
        var settings = _settings;
        var trayReadings = TrayReadingsFor(settings);
        var activity = _tracker.Activity;
        // Only tiles and graphs draw history lines; skip scanning it otherwise.
        var historyOf = settings.Widgets.Where(w => w.Enabled && WidgetCatalog.LayoutOf(w) is WidgetLayout.Tiles or WidgetLayout.Graph)
            .SelectMany(WidgetCatalog.ItemsOf).Select(i => WidgetCatalog.Metric(i.Id)).OfType<OverlayMetric>().ToHashSet();
        bool drawsHistory = historyOf.Contains(OverlayMetric.CpuTemp) || historyOf.Contains(OverlayMetric.GpuTemp);
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
            Readings = WidgetSensors(settings),
            Histories = WidgetHistories(historyOf),
        };
        _lastToday = data.Today;

        var tip = $"Rigsight\nCPU {Units.TempShort(k.CpuTemp)}  ·  GPU {Units.TempShort(k.GpuTemp)}  ·  RAM {Units.Short(SensorKind.Load, k.RamLoad)}";
        if (activity.Paused) tip += "\nTracking paused";
        else if (activity.Name is not null && activity.SessionActiveSec > 0) tip += $"\n{activity.Name}  ·  {Units.Duration(activity.SessionActiveSec)}";

        // Our own windows can report as fullscreen-sized; only treat other apps as fullscreen.
        bool otherFullscreen = fullscreen && activity.Exe is not null &&
            !activity.Exe.StartsWith("Rigsight", StringComparison.OrdinalIgnoreCase);
        // Widgets are for the desktop and apps: with a game in front (any window mode) the overlay takes over.
        bool gameInFront = activity.Category == AppCategory.Game && activity.Exe is not null && !activity.Paused;

        // Tray health dot: amber within 12° of a limit, red at or over it.
        var a = settings.Alerts;
        double Margin(double? v, double limit) => v is double d ? d - limit : double.MinValue;
        double worst = Math.Max(Margin(k.CpuTemp, a.CpuLimit), Math.Max(Margin(k.GpuTemp, a.GpuLimit), Margin(k.GpuHotSpot, a.GpuHotSpotLimit)));
        int health = worst >= 0 ? 2 : worst >= -12 ? 1 : 0;

        _ui.Post(_ =>
        {
            long t0 = Stopwatch.GetTimestamp();
            _tray.Update(tip, health);
            // In the taskbar where it can be (Windows 11), else as icons.
            bool inStrip = settings.TrayStyle == TrayStyle.Strip && _taskbarStrip.Update(trayReadings, settings.TrayGrayscale);
            if (settings.TrayStyle != TrayStyle.Strip) _taskbarStrip.Hide();
            _trayReadings.Update(inStrip ? [] : trayReadings, settings.TrayStyle == TrayStyle.Grouped, settings.TrayGrayscale);
            _widgets.Update(data, gameInFront ? Win32.MonitorFromWindow(Win32.GetForegroundWindow(), 2 /* MONITOR_DEFAULTTONEAREST */) : IntPtr.Zero);
            _overlay.Update(data);
            _notices.SetFullscreen(otherFullscreen);
            if (Profiling) RunOnSampler(() => Measure("ui:tray+widgets", t0));
            if (alert is not null)
                _notices.Show(new Notice(NoticeKind.Alert, "Running hot", alert, activity.Path, "temperatures", Urgent: true));
        }, null);
    }

    private List<ProcInfo> BuildProcs(ProcessSnapshot snapshot, Dictionary<string, WindowState> windows)
    {
        Dictionary<int, string>? titles = null;
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
                    Processes = a.Processes is { } processes ? _procLabels.Describe(a.Exe, name, processes, ref titles) : null,
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

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _justTurnedOn = true;
            RunOnSampler(() => _connection.Settle(TimeUtil.NowUnix()));
        }
        else if (e.Mode == PowerModes.Suspend)
            RunOnSampler(() =>
            {
                KeepOngoingDrop();
                _connection.Suspend();
            });
    }

    // ── Internet ──────────────────────────────────────────────────────────

    /// <summary>Sampler thread: starts reading every app's network use (with admin rights), and watching for drops.</summary>
    private void StartNetwork()
    {
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        _addresses = AddressBook.Read();
        _addressesChanged = false;
        _connection.Settle(TimeUtil.NowUnix());
        if (_isAdmin) _netTrace = NetworkTrace.Start(NetTraceName, () => _addresses);
        MemoryAt("network");
    }

    private static string NetTraceName => "Rigsight Network" + RigsightPaths.InstanceSuffix;

    private static readonly NetLive NotRecording = new() { Stalled = true };

    /// <summary>
    /// Sampler thread, each minute: a trace that delivers nothing while megabytes go through the network cards is started
    /// again; if that doesn't help, traces left behind by test copies are stopped first (see <see cref="TraceWatch"/>).
    /// </summary>
    private void CheckNetworkTrace()
    {
        KeepOngoingDrop();
        if (!_isAdmin) return;
        long now = TimeUtil.NowUnix();
        // No trace at all (Windows wouldn't start one, or ended it): there are no events to count, only the remedies.
        var remedy = _netTrace is { Ended: false } trace ? _traceWatch.Check(now, NetAdapters.CardBytes(), trace.Events) : _traceWatch.Missing(now);
        switch (remedy)
        {
            case TraceRemedy.Restart:
                Log.Write("network", _netTrace is { Ended: false } ? "App network use stopped coming in while the network was busy: reading it again"
                    : "App network use isn't being read: starting it again");
                RestartNetworkTrace();
                break;
            case TraceRemedy.ClearOthers:
                var others = NetworkTrace.StopOthers(NetTraceName);
                Log.Write("network", others.Count == 0 ? "Still nothing coming in: reading it again"
                    : $"Still nothing coming in: stopped {others.Count} trace(s) left by test copies ({string.Join(", ", others)}), reading it again");
                RestartNetworkTrace();
                break;
            case TraceRemedy.GiveUp:
                Log.Write("network", $"App network use still isn't coming in: trying again in {TraceWatch.RetrySeconds / 60} minutes");
                break;
            case TraceRemedy.Recovered:
                Log.Write("network", "App network use is coming in again");
                break;
        }
    }

    /// <summary>Sampler thread, each minute and before sleep: a drop still going on is written as far as it has got, so
    /// one the PC is switched off or put to sleep in isn't lost (see <see cref="ConnectionWatch.Ongoing"/>).</summary>
    private void KeepOngoingDrop()
    {
        if (_connection.Ongoing(TimeUtil.NowUnix()) is { } drop) _tracker.OnDrop(drop);
    }

    private void RestartNetworkTrace()
    {
        _netTrace?.Dispose();
        _netTrace = NetworkTrace.Start(NetTraceName, () => _addresses);
        _traceWatch.Restarted();
    }

    private void OnAddressChanged(object? sender, EventArgs e) => _addressesChanged = true;
    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => _addressesChanged = true;

    /// <summary>Sampler thread, each second: what each app moved since the last time, and whether the internet is there.</summary>
    private void SampleNetwork(double seconds)
    {
        long? internetDown = null;
        if (_netTrace is { } trace)
        {
            var counts = trace.Take();
            long down = 0;
            foreach (var c in counts.Values) down += c.InternetDown;
            internetDown = down;
            _tracker.OnNetwork(counts, _running, seconds);
            var now = _tracker.NetNow;
            now.Stalled = _traceWatch.Stalled;
            lock (_netHistoryLock)
            {
                _netHistory.Enqueue(new NetLive { Time = now.Time, Down = now.Down, Up = now.Up });
                while (_netHistory.Count > 60) _netHistory.Dequeue();
            }
        }
        if (_connection.Observe(TimeUtil.NowUnix(), NetAdapters.WindowsSeesInternet(), _addresses.CardConnected, internetDown) is { } drop)
        {
            Log.Write("network", $"The internet dropped for {drop.Seconds} s ({drop.Kind})");
            _tracker.OnDrop(drop);
        }
    }

    /// <summary>
    /// Sampler thread, each minute: once the PC was just turned on (or woke) and the user is here, the recap of the last
    /// day it was used, unless that day's was shown already (see <see cref="DailyRecap"/>).
    /// </summary>
    private void MaybeShowDailyRecap()
    {
        if (!_justTurnedOn || !_presentNow) return;
        _justTurnedOn = false;
        var settings = _settings;
        var today = DateTime.Today;
        var over = DailyRecap.OverBefore(DateTime.Now);
        var lastUsed = _db.LastUsedDayBefore(TimeUtil.ToUnix(over)) is long d ? TimeUtil.FromUnix(d).Date : (DateTime?)null;
        if (DailyRecap.DayToRecap(over, lastUsed, DailyRecap.Recapped(settings)) is not { } day) return;
        // Built before the day is marked as recapped: if building fails, the next turn-on tries again.
        var report = settings.Alerts.DailyRecap ? ReportBuilder.Build(_db, ReportRange.Day, day, settings) : null;
        MutateSettings(s =>
        {
            s.RecappedDay = DailyRecap.Key(day);
            s.LastRecapDay = DailyRecap.Key(today);
        });
        if (report is null || !report.HasData || report.ActiveSec < 300) return;

        var parts = new List<string> { $"Active {Units.Duration(report.ActiveSec)}" };
        var top = report.Apps.FirstOrDefault(a => a.ActiveSec > 0 && a.Category != AppCategory.System);
        if (top is not null) parts.Add($"{top.Name} {Units.Duration(top.ActiveSec)}");
        if (report.CpuTempPeak is { } cpu) parts.Add($"CPU peak {Units.TempShort(cpu.Value)}");
        if (report.GpuTempPeak is { } gpu) parts.Add($"GPU peak {Units.TempShort(gpu.Value)}");

        string text = string.Join("  ·  ", parts) + ". Click for the full recap.";
        _ui.Post(_ => _notices.Show(new Notice(NoticeKind.Recap, DailyRecap.Title(day, today), text, Page: "reports", Arg: DailyRecap.Key(day))), null);
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
            try
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
            }
            catch (Exception ex)
            {
                Log.Error("session", ex); // a background task's error would otherwise vanish with it
            }
        });
    }

    private bool _crashesScanned;
    private const string ShutdownTimesKey = "shutdown_times_placed";

    /// <summary>Sampler thread: copies new crash records from the Windows event logs into the database.</summary>
    private void ScanCrashes()
    {
        try
        {
            var since = _crashesScanned ? DateTime.Now.AddMinutes(-30) : DateTime.Now.AddDays(-90);
            // Once: the PC going down, stored under Windows' time (often half an hour early) by versions before 0.19.1,
            // is put at the time it was last known to be running. The scan below does that for the last 90 days; this
            // is for the ones stored from before, as far back as Windows' log still has them.
            if (_db.GetMeta(ShutdownTimesKey) is null)
            {
                if (_db.FirstCrashTime() is long first && TimeUtil.FromUnix(first) < since)
                    _db.InsertCrashes(CrashLogReader.ReadSince(TimeUtil.FromUnix(first).AddDays(-1), systemOnly: true).Where(e => e.NextStart is not null), knownOnly: true);
                _db.SetMeta(ShutdownTimesKey, "1");
            }
            var added = _db.InsertCrashes(CrashLogReader.ReadSince(since));
            bool firstScan = !_crashesScanned;
            _crashesScanned = true;
            if (!_settings.Alerts.CrashNotifications) return;

            // Tell the user about system-level problems they may not have noticed (app crashes are covered by sessions).
            // A shutdown that didn't finish is usually harmless and on some PCs happens every day: the Crashes page has it.
            var recent = added.Where(e => e.Kind is CrashKind.SystemCrash or CrashKind.UnexpectedShutdown or CrashKind.GpuDriverReset
                                          && e.Moment != PowerMoment.ShuttingDown
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

    /// <summary>
    /// Sampler thread: notes what changed on the PC. Drivers and Windows updates come from the event logs (all they
    /// still hold the first time, then what's new); apps, startup programs, hardware and settings from setting the
    /// PC's inventory against the last one.
    /// </summary>
    private void ScanChanges()
    {
        try
        {
            var now = DateTime.Now;
            bool first = _db.ChangesScanned is null;
            var since = _db.ChangesScanned is long scanned ? TimeUtil.FromUnix(scanned).AddHours(-1) : now.AddYears(-2);
            var logged = ChangeLogReader.Read(since, now);
            var reading = Inventory.Read();
            Inventory.HoldRunningUpdates(reading, _db.GetInventory(), RunningPaths);
            var found = _db.ApplyInventory(reading, Inventory.Owner(), now);

            // A graphics driver is followed by its version in the inventory (the logs often don't name it); the
            // log entry only says when. Before the first inventory the logs are all there is.
            var graphics = logged.LastOrDefault(c => c.IsGraphicsDriver && c.Time > since.AddHours(1));
            if (!first) logged.RemoveAll(c => c.IsGraphicsDriver);
            // Found by this check, so it happened since the last one (which may be last night's).
            if (_db.ChangesScanned is long last) found = [.. found.Select(c => c with { NoticedFrom = TimeUtil.FromUnix(last) })];
            if (graphics is not null)
                found = [.. found.Select(c => c.IsGraphicsDriver ? c with { Time = graphics.Time, Subject = Inventory.ExactPrefix + c.Subject, NoticedFrom = null } : c)];

            _db.InsertChanges(logged);
            _db.InsertChanges(found);
            _db.ChangesScanned = TimeUtil.ToUnix(now);
        }
        catch (Exception ex)
        {
            Log.Error("changes", ex);
        }
    }

    /// <summary>
    /// Riggy (0.16.0 to 0.17.1) had a switch in the settings file. Settings no longer have it, but the file keeps the
    /// line until it's next written, and its backup copy one writing longer: written twice now, neither has it.
    /// </summary>
    private void ForgetRiggySetting()
    {
        try
        {
            string file = RigsightPaths.SettingsFile;
            if (!File.Exists(file) || !File.ReadAllText(file).Contains("\"ShowAsk\"", StringComparison.Ordinal)) return;
            SettingsStore.Save(_settings);
            SettingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            Log.Error("settings", ex);
        }
    }

    /// <summary>The full path of every program running now (a few milliseconds; asked for when an app's version changed).</summary>
    private static List<string> RunningPaths()
    {
        var paths = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (Win32.ProcessPath(p.Id) is { } path) paths.Add(path);
            }
            catch
            {
                // Exited meanwhile.
            }
            finally
            {
                p.Dispose();
            }
        }
        return paths;
    }

    /// <summary>
    /// The Timeline's refresh button: looks for new problems and changes now instead of at the next ten-minute check,
    /// and says when it's done so the page can read them.
    /// </summary>
    private void ScanNow()
    {
        ScanCrashes();
        ScanChanges();
        _pipe.Broadcast(new AgentMessage { T = "scanned" });
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
            var sensors = _sensors;
            hello.Hardware = sensors.Schema;
            hello.Keys = sensors.Keys;
            hello.PreferredGpus = sensors.PreferredGpus;
            hello.History = [.. _history.Snapshot(), .. _driveHistory.Snapshot()];
            hello.Drives = sensors.DriveHealth;
            hello.SensorStatus = _sensorStatus;
            if (_netTrace is not null)
                lock (_netHistoryLock) hello.NetHistory = [.. _netHistory];
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
            MutateSettings(s => KeepAgentFields(incoming, s), replaceWith: incoming);
            return;
        }

        switch (msg.Cmd)
        {
            case "procs-detail":
                // The Memory page opened (or closed) apps to see each of their processes: resample now, not in 2 s.
                var exes = string.IsNullOrEmpty(msg.Arg) ? null : new HashSet<string>(msg.Arg.Split('|'), StringComparer.OrdinalIgnoreCase);
                RunOnSampler(() =>
                {
                    _procSampler.Detail = exes;
                    _procsNow = true;
                });
                break;
            case "clear-history":
                RunOnSampler(() =>
                {
                    _tracker.ClearHistory();
                    _pipe.Broadcast(new AgentMessage { T = "history-cleared" });
                });
                break;
            case "startup-on" or "startup-off" when !RigsightPaths.IsTestInstance:
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
            case "sensors-retry":
                RunOnSampler(RetryAllSensors);
                break;
            case "scan-now":
                RunOnSampler(ScanNow);
                break;
            case "quit":
                _ui.Post(_ => Quit("the app"), null);
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
            case "start-rtss" when !RigsightPaths.IsTestInstance:
                _ui.Post(_ => _overlay.StartRtss(), null);
                break;
            case "install-rtss" when !RigsightPaths.IsTestInstance:
                _ui.Post(_ => _overlay.InstallRtss(_ui), null);
                break;
            case "overlay-status":
                _ui.Post(_ => OnOverlayStateChanged(), null);
                break;
            case "restart-elevated":
                _ui.Post(_ => RestartElevated(), null);
                break;
            case "install-update" when !RigsightPaths.IsTestInstance:
                _ = Task.Run(async () =>
                {
                    // Its own download of the latest release, not the path in the message (see UpdateInstaller).
                    bool started = _isAdmin && await UpdateInstaller.RunAsync();
                    _pipe.Broadcast(new AgentMessage { T = "update", UpdateStatus = started ? "started" : "failed" });
                });
                break;
        }
    }

    /// <summary>
    /// Settings from the app, before they replace the agent's: fields the agent owns are kept from the agent's copy
    /// (pausing goes through commands, so a tray "Pause" is never undone by the app sending settings it had before).
    /// </summary>
    internal static void KeepAgentFields(RigsightSettings incoming, RigsightSettings current)
    {
        incoming.StartupConfigured = current.StartupConfigured;
        incoming.LastRecapDay = current.LastRecapDay;
        incoming.RecappedDay = current.RecappedDay;
        incoming.LastUpdateNotice = current.LastUpdateNotice;
        incoming.HardwareAppsChecked = current.HardwareAppsChecked;
        incoming.Tracking.PausedUntil = current.Tracking.PausedUntil;
        foreach (var w in incoming.Widgets)
        {
            var mine = current.Widgets.FirstOrDefault(x => x.Id == w.Id);
            if (mine is not null) { w.X = mine.X; w.Y = mine.Y; }
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
            if (_isAdmin) Process.Start(RigsightPaths.Explorer, $"\"{exe}\"");
            else Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("agent", ex);
        }
    }

    // ── Settings ──────────────────────────────────────────────────────────

    /// <summary>
    /// A line in the log for a change to what's shown or tracked (the taskbar readings, the overlay's sensors, the
    /// widgets on, the apps left out), saying where it came from: a list that vanished can be traced.
    /// </summary>
    private static void LogSettingsChanges(RigsightSettings before, RigsightSettings after, string from)
    {
        void Note(string what, int a, int b) { if (a != b) Log.Write("settings", $"{what}: {a} -> {b} (from the {from})"); }
        Note("taskbar readings", before.TraySensors.Count, after.TraySensors.Count);
        if (before.TrayStyle != after.TrayStyle) Log.Write("settings", $"taskbar style: {before.TrayStyle} -> {after.TrayStyle} (from the {from})");
        Note("overlay sensors", before.Overlay.Sensors.Count, after.Overlay.Sensors.Count);
        // A user's overlay background kept coming back after he had set it to none (Oct 2026), with nothing to say what moved it.
        Note("overlay background (%)", (int)Math.Round(before.Overlay.BackgroundOpacity * 100), (int)Math.Round(after.Overlay.BackgroundOpacity * 100));
        Note("widgets on", before.Widgets.Count(w => w.Enabled), after.Widgets.Count(w => w.Enabled));
        Note("apps left out of tracking", before.Tracking.ExcludedApps.Count, after.Tracking.ExcludedApps.Count);
    }

    /// <summary>
    /// The agent is the only writer of settings.json. Every change goes through here: copy, change,
    /// save, apply locally, and tell the app.
    /// </summary>
    private void MutateSettings(Action<RigsightSettings> change) => MutateSettings(change, replaceWith: null);

    private void MutateSettings(Action<RigsightSettings> change, RigsightSettings? replaceWith)
    {
        RigsightSettings updated;
        bool yieldChanged, trayChanged;
        lock (_settingsLock)
        {
            var current = _settings;
            updated = replaceWith ?? current.Clone();
            change(replaceWith is null ? updated : current);
            yieldChanged = updated.YieldToHardwareApps != current.YieldToHardwareApps;
            trayChanged = updated.TrayStyle != current.TrayStyle || updated.TrayGrayscale != current.TrayGrayscale
                          || !updated.TraySensors.SequenceEqual(current.TraySensors);
            LogSettingsChanges(current, updated, replaceWith is null ? "agent" : "app");
            _settings = updated;
            SettingsStore.Save(updated);
        }

        Units.Fahrenheit = updated.UseFahrenheit;
        DarkMenuRenderer.Theme = updated.Theme;
        RunOnSampler(() => _tracker.SetSettings(updated));
        // A reading added to or removed from the taskbar, or another way of showing them: there now, not at the next read.
        if (trayChanged) RunOnSampler(() => _sensorsNow = true);
        // Stepping aside turned on or off: scan again now, with or without the parts those programs control.
        if (yieldChanged)
        {
            Log.Write("sensors", $"Stepping aside for RGB and fan apps turned {(updated.YieldToHardwareApps ? "on" : "off")}");
            RunOnSampler(OpenSensors);
        }
        _ui.Post(_ =>
        {
            _widgets.Apply(updated);
            _overlay.Apply(updated.Overlay);
            if (NeedsRtss(updated) && !RigsightPaths.IsTestInstance) _overlay.EnsureRtssRunning();
            // Keep the app's widget previews in sync with theme changes.
            if (_pipe.ClientCount > 0) RenderPreviews(updated);
        }, null);
        _pipe.Broadcast(new AgentMessage { T = "settings", Settings = updated });
    }

    /// <summary>
    /// Writes widget and overlay preview images and tells the app they're ready. The overlay's sensors are read
    /// on the sampler thread for these settings (so one just added shows straight away), then drawn on the UI thread.
    /// </summary>
    /// <summary>The overlay draws in games through RivaTuner, and the FPS widget reads the frame rate from it.</summary>
    private static bool NeedsRtss(RigsightSettings s) =>
        s.Overlay.Enabled || s.Widgets.Any(w => w.Enabled && WidgetManager.ShowsFrameRate(w));

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
        if (_isAdmin || RigsightPaths.IsTestInstance || Environment.ProcessPath is not { } exe) return;
        try
        {
            Process.Start(new ProcessStartInfo(exe, "--replace") { UseShellExecute = true, Verb = "runas" });
            Quit("an elevated copy taking over");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC declined: keep running without admin.
        }
    }

    /// <summary>Starts a new copy that takes over (with the same rights, so nothing to accept), then exits.</summary>
    private void Restart(string why)
    {
        if (_stopping || Environment.ProcessPath is not { } exe) return;
        try
        {
            Process.Start(new ProcessStartInfo(exe, [.. Program.RestartArgs(_args), "--replace"]) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("agent", ex);
            return;
        }
        Quit(why);
    }

    /// <summary>Stops the agent, saying why in the log (a copy that's simply gone can then be traced).</summary>
    private void Quit(string why)
    {
        if (_stopping) return;
        Log.Write("agent", $"Quitting: asked by {why}");
        SystemEvents.SessionEnding -= OnSessionEnding;
        SystemEvents.TimeChanged -= OnTimeChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _quitWait?.Unregister(null);
        _displayWatch.Dispose();
        _stopping = true;
        _updateTimer?.Dispose();
        _wake.Set();
        _sampler.Join(5000);

        // Each step is independent: a failure in one must never leave the agent half-closed.
        foreach (var step in new Action[] { _pipe.Dispose, _widgets.CloseAll, _overlay.Dispose, _notices.CloseAll, _trayReadings.Dispose, _taskbarStrip.Dispose, _tray.Dispose })
        {
            try { step(); }
            catch (Exception ex) { Log.Error("agent", ex); }
        }
        ExitThread();
    }
}
