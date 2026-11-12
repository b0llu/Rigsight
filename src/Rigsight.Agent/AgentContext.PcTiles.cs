using System.Diagnostics;
using Rigsight.Agent.Tracking;
using Rigsight.Agent.Widgets;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;

namespace Rigsight.Agent;

// The widgets' PC tiles (the PC tiles mock-up, 2026-11-11): what they show, read only while a widget that is on has one.
// With none of them on a widget, nothing here is read: the cost of these tiles is nothing until one is added.
internal sealed partial class AgentContext
{
    /// <summary>The kinds of tile on the widgets that are on (sampler thread, worked out again when the settings change).</summary>
    private (RigsightSettings Settings, HashSet<string> Kinds, PcNeeds Needs) _pcKinds = (null!, [], PcNeeds.None);

    private (HashSet<string> Kinds, PcNeeds Needs) PcKindsOf(RigsightSettings settings)
    {
        if (!ReferenceEquals(_pcKinds.Settings, settings))
        {
            var on = settings.WidgetsShown ? settings.Widgets.Where(w => w.Enabled).ToList() : [];
            _pcKinds = (settings, [.. on.SelectMany(w => w.Tiles ?? []).Select(t => t.Kind)], PcFacts.NeedsOf(on));
        }
        return (_pcKinds.Kinds, _pcKinds.Needs);
    }

    private static readonly string[] LiveKinds =
    [
        WidgetTiles.Fans, WidgetTiles.Drives, WidgetTiles.Cores, WidgetTiles.TopCpu, WidgetTiles.TopMemory,
        WidgetTiles.NetSpeed, WidgetTiles.NetNow,
    ];

    /// <summary>Sampler thread: what the PC's live tiles on the widgets show now (null: none of them is on a widget).</summary>
    private PcNow? PcNowFor(RigsightSettings settings)
    {
        var (kinds, needs) = PcKindsOf(settings);
        KeepFacts(settings, needs);
        if (!LiveKinds.Any(kinds.Contains)) return null;
        long t0 = Stopwatch.GetTimestamp();
        var pc = new PcNow();
        if (kinds.Contains(WidgetTiles.Fans)) pc.Fans = FansNow(settings);
        if (kinds.Contains(WidgetTiles.Drives)) pc.Drives = DrivesNow();
        if (kinds.Contains(WidgetTiles.Cores)) pc.Cores = CoresNow();
        if (kinds.Contains(WidgetTiles.TopCpu) || kinds.Contains(WidgetTiles.TopMemory))
        {
            if (_lastProcs is { } procs)
            {
                var apps = procs.Apps.Values.ToList();
                if (kinds.Contains(WidgetTiles.TopCpu))
                    pc.TopCpu = [.. apps.OrderByDescending(a => a.Cpu).Take(8).Select(a => AppOf(a, Units.AppCpu(a.Cpu)))];
                if (kinds.Contains(WidgetTiles.TopMemory))
                    pc.TopMemory = [.. apps.OrderByDescending(a => a.MemMB).Take(8).Select(a => AppOf(a, Units.Megabytes(a.MemMB)))];
            }
        }
        if (kinds.Contains(WidgetTiles.NetSpeed) || kinds.Contains(WidgetTiles.NetNow))
        {
            bool reading = _netTrace is not null || _cardsBefore is not null;
            var net = reading ? _tracker.NetNow : null;
            pc.NetPerApp = _netTrace is not null;
            if (net is { HasSpeed: true })
            {
                (pc.NetDown, pc.NetUp) = (net.Down, net.Up);
                // As the Network page lists them (NetTodayViewModel): the apps moving a kilobyte a second or more.
                if (pc.NetPerApp)
                    pc.NetApps = [.. net.Apps.Where(a => a.Down + a.Up >= 1024).OrderByDescending(a => a.Down + a.Up).Take(8)
                        .Select(a => new TileApp(a.Name, a.Path, Units.Speed(a.Down + a.Up)))];
            }
            lock (_netHistoryLock)
            {
                pc.DownHistory = [.. _netHistory.Select(n => (float)n.Down)];
                pc.UpHistory = [.. _netHistory.Select(n => (float)n.Up)];
                pc.NetTimes = [.. _netHistory.Select(n => n.Time)];
            }
        }
        Measure("pc-tiles", t0);
        return pc;
    }

    /// <summary>An app on Top CPU users or Top memory users, by the name the Processes page gives it.</summary>
    private TileApp AppOf(AppUsage a, string value)
    {
        var (name, path) = _apps.Describe(a.Exe, a.FirstPid);
        if (_settings.AppNames.TryGetValue(a.Exe, out var alias)) name = alias;
        return new TileApp(name, path, value);
    }

    // ── Fans ─────────────────────────────────────────────────────────────

    /// <summary>Each fan's last speed read (every 10 seconds, with the tracker's), and those that have spun since the agent started.</summary>
    private readonly Dictionary<string, Tracking.FanReading> _fansRead = [];
    private readonly HashSet<string> _fansSpun = [];

    /// <summary>Sampler thread: the fans the last pass read.</summary>
    private void NoteFans(List<Tracking.FanReading> fans)
    {
        foreach (var f in fans)
        {
            _fansRead[f.Id] = f;
            if (f.Rpm > 0) _fansSpun.Add(f.Id);
        }
    }

    /// <summary>Every fan that spins, or has (a fan stopped at idle stays), by the name All sensors gives it; null until the first are read.</summary>
    private List<FanNow>? FansNow(RigsightSettings settings)
    {
        if (_fansRead.Count == 0) return _sensorsReady ? [] : null;
        return [.. _fansRead.Values.Where(f => _fansSpun.Contains(f.Id))
            .Select(f => new FanNow(settings.SensorLabels.GetValueOrDefault(f.Id) ?? SensorWords.Show(f.Name), f.Rpm, f.Id))];
    }

    // ── Drives ───────────────────────────────────────────────────────────

    // Read at the first look: "never read" is 30 s before the clock's start. long.MinValue was here, and now minus it ran
    // over to a negative, so the drives were never read and the tile stayed empty (the user's test copy, 2026-11-12).
    private (long At, List<VolumeSpace> Volumes) _volumes = (-DrivesEveryMs, []);
    private const long DrivesEveryMs = 30_000;

    /// <summary>The fixed drives, how full (asked of Windows every 30 seconds) and their temperature (read with the drives' own).</summary>
    private List<DriveNow> DrivesNow()
    {
        long now = Environment.TickCount64;
        if (now - _volumes.At >= DrivesEveryMs) _volumes = (now, DriveSpace.Fixed());
        var temps = DiskTemperatures();
        return [.. _volumes.Volumes.Select(v => new DriveNow(v.Letter, v.UsedShare, v.Free, v.Disk is int disk ? temps.GetValueOrDefault(disk) : null))];
    }

    /// <summary>Each physical drive's temperature (its composite one where it has several), by Windows' number for it.</summary>
    private Dictionary<int, double?> DiskTemperatures()
    {
        var map = new Dictionary<int, double?>();
        foreach (var hw in _sensors.Schema.Where(h => h.Type == "Storage"))
        {
            var temps = hw.Sensors.Where(s => s.Kind == SensorKind.Temperature && !s.Name.Contains("Warning", StringComparison.OrdinalIgnoreCase)
                && !s.Name.Contains("Critical", StringComparison.OrdinalIgnoreCase)).ToList();
            var temp = temps.FirstOrDefault(s => s.Name == "Composite Temperature") ?? temps.FirstOrDefault(s => s.Name == "Temperature") ?? temps.FirstOrDefault();
            if (temp is null || DriveSpace.DiskOfSensor(temp.Id) is not int disk) continue;
            map[disk] = _sensors.ReadSensor(temp.Id)?.Value;
        }
        return map;
    }

    /// <summary>The drives as PC health judges them (as Home does, from what the drives report: SMART and an SSD's life left).</summary>
    private List<DriveCheck> DriveChecks()
    {
        var list = new List<DriveCheck>();
        var health = _sensors.DriveHealth;
        foreach (var hw in _sensors.Schema.Where(h => h.Type == "Storage"))
        {
            var life = hw.Sensors.FirstOrDefault(s => s.Kind == SensorKind.Level && s.Name is "Life" or "Remaining Life");
            double? left = life is null ? null : _sensors.ReadSensor(life.Id)?.Value;
            string status = health.FirstOrDefault(h => h.Name == hw.Name)?.Status ?? "Unknown";
            list.Add(new DriveCheck(hw.Name, life is not null || status != "Unknown", HomeCards.DriveNeedsLook(status, left)));
        }
        return list;
    }

    // ── Cores ────────────────────────────────────────────────────────────

    /// <summary>Each core's load now, from the CPU's own sensors (as the taskbar's "Each core" look reads them).</summary>
    private float[]? CoresNow()
    {
        if (!_sensors.Keys.TryGetValue(KeySensors.CpuLoad, out int i) || i < 0 || i >= _sensors.Ids.Length) return null;
        return CoresOf(_sensors.Ids[i]);
    }

    // ── From the records, off the drawing thread ─────────────────────────

    private volatile PcFacts? _facts;
    private DateTime _factsAt;
    private int _factsReading;

    /// <summary>
    /// Sampler thread: has the records read again in the background when a tile needs what isn't there, about once a
    /// minute while one does, and at midnight; with none of these tiles on a widget, lets go of what was read.
    /// </summary>
    private void KeepFacts(RigsightSettings settings, PcNeeds needs)
    {
        if (needs == PcNeeds.None)
        {
            _facts = null;
            return;
        }
        var now = DateTime.Now;
        var have = _facts;
        bool due = have is null || (have.Holds & needs) != needs || now - _factsAt >= PcFacts.Every || now.Date != _factsAt.Date;
        if (!due || Interlocked.CompareExchange(ref _factsReading, 1, 0) != 0) return;
        _factsAt = now;
        var drives = (needs & PcNeeds.Health) != 0 ? DriveChecks() : null;
        _ = Task.Run(() =>
        {
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                using var db = RigsightDb.OpenReader();
                if (db is null) return;
                string? fullest = (needs & PcNeeds.Health) != 0 ? HomeCards.Fullest() : null;
                _facts = PcFacts.Read(db, settings, DateTime.Now, needs, drives, fullest, have);
            }
            catch (Exception ex)
            {
                Log.Error("widgets", ex);
            }
            finally
            {
                if (Profiling) Log.Write("profile", $"pc-facts ({needs}): {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0} ms");
                Volatile.Write(ref _factsReading, 0);
            }
        });
    }
}
