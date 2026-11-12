using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Core.Settings;
using Rigsight.Models;
using Rigsight.TaskbarDrawing;
using Rigsight.ViewModels;

namespace Rigsight.Services;

/// <summary>
/// A widget drawn in the app with the agent's own drawing (linked in, see Rigsight.csproj), from the readings the app
/// already has: the Widgets page's cards and its editor show a widget exactly as the desktop will, at once.
/// </summary>
public static class WidgetPictures
{
    /// <summary>What a widget would show now, from the app's live readings (nothing for a reading not read yet).</summary>
    internal static WidgetData DataFrom(LiveData? live)
    {
        var d = new WidgetData();
        if (live is null) return d;
        double? V(SensorItem? s) => s?.Value;
        d.CpuTemp = V(live.CpuTemp); d.CpuLoad = V(live.CpuLoad); d.CpuPower = V(live.CpuPower); d.CpuClock = V(live.CpuClock);
        d.GpuTemp = V(live.GpuTemp); d.GpuLoad = V(live.GpuLoad); d.GpuPower = V(live.GpuPower); d.GpuHotSpot = V(live.GpuHotSpot); d.GpuClock = V(live.GpuClock);
        d.RamLoad = V(live.RamLoad); d.RamUsedGb = V(live.RamUsed);
        d.RamTotalGb = live.RamUsed?.Value is double used && live.RamAvailable?.Value is double free ? used + free : null;
        d.VramUsedMb = V(live.GpuVramUsed); d.VramTotalMb = V(live.GpuVramTotal);
        d.CpuHistory = Recent(live.CpuTemp);
        d.GpuHistory = Recent(live.GpuTemp);
        d.Histories = new Dictionary<OverlayMetric, float[]>
        {
            [OverlayMetric.GpuHotSpot] = Recent(live.GpuHotSpot), [OverlayMetric.CpuLoad] = Recent(live.CpuLoad),
            [OverlayMetric.GpuLoad] = Recent(live.GpuLoad), [OverlayMetric.Ram] = Recent(live.RamLoad),
        };
        // By the name the desktop gives it (the one given on All sensors, else the sensor's own as All sensors shows it).
        foreach (var s in live.AllSensors) d.Readings[s.Id] = new OverlaySensorReading(s.DisplayName, s.Kind, s.Value);
        if (live.Activity is { } a) d.Activity = a;
        d.Today = live.Today;
        // An FPS tile with no game says what's missing: RivaTuner, or a game. The desktop asks RivaTuner; here the agent's
        // word for it (the Overlay and Games pages'), else the picture said "Needs RivaTuner" with it running.
        d.RtssRunning = RtssRunning;
        d.Pc = PcFrom(live);
        d.Facts = _facts;
        return d;
    }

    /// <summary>Whether RivaTuner runs, as the agent last said (set as its word comes).</summary>
    internal static volatile bool RtssRunning;

    /// <summary>Whether the agent has admin rights (it says so when the app connects).</summary>
    internal static volatile bool AgentIsAdmin;

    /// <summary>The PC's live tiles from the app's readings, the same ones its pages show.</summary>
    private static PcNow PcFrom(LiveData live)
    {
        var pc = new PcNow
        {
            Fans = FansFrom(live),
            Cores = [.. live.AllSensors.Where(s => s.HardwareType == "Cpu" && TrayParts.IsCoreLoad(s.Kind, s.Name)).Select(s => (float)Math.Clamp((s.Value ?? 0) / 100, 0, 1))],
            TopCpu = [.. live.Procs.OrderByDescending(p => p.Cpu).Take(8).Select(p => new TileApp(p.Name, p.Path, Units.AppCpu(p.Cpu)))],
            TopMemory = [.. live.Procs.OrderByDescending(p => p.MemMB).Take(8).Select(p => new TileApp(p.Name, p.Path, Units.Megabytes(p.MemMB)))],
            // Each app's internet is read with admin rights (the agent's trace), whether or not an app is using it now: by
            // the apps alone, a quiet moment read as no rights ("…while RigSight runs with admin rights").
            NetPerApp = AgentIsAdmin && live.Net is { Stalled: false } || live.Net is { Apps.Count: > 0 },
            NetDown = live.Net is { Stalled: false } n ? n.Down : null,
            NetUp = live.Net is { Stalled: false } u ? u.Up : null,
            NetApps = live.Net is { Stalled: false } m ? [.. m.Apps.Where(x => x.Down + x.Up >= 1024).OrderByDescending(x => x.Down + x.Up).Take(8)
                .Select(x => new TileApp(x.Name, x.Path, Units.Speed(x.Down + x.Up)))] : null,
            DownHistory = [.. live.NetHistory.Select(x => (float)x.Down)],
            UpHistory = [.. live.NetHistory.Select(x => (float)x.Up)],
        };
        if (pc.Cores.Length == 0) pc.Cores = null;
        if (live.Procs.Count == 0) (pc.TopCpu, pc.TopMemory) = (null, null);
        var temps = live.Drives.Where(x => x.Temperature is not null)
            .Select(x => (Disk: DriveSpace.DiskOfSensor(x.Temperature!.Id), x.Temperature!.Value)).Where(x => x.Disk is not null)
            .GroupBy(x => x.Disk!.Value).ToDictionary(gr => gr.Key, gr => gr.First().Value);
        if (Environment.TickCount64 - _volumes.At >= 30_000) _volumes = (Environment.TickCount64, DriveSpace.Fixed());
        pc.Drives = [.. _volumes.List.Select(v => new DriveNow(v.Letter, v.UsedShare, v.Free, v.Disk is int disk ? temps.GetValueOrDefault(disk) : null))];
        return pc;
    }

    /// <summary>
    /// The Fans page's rows (<see cref="Core.Reports.FanList"/>), from every fan sensor: LiveData's own fan list has only
    /// the board's and coolers' fans that spun today, so the graphics card's fans never showed here either.
    /// </summary>
    internal static List<FanNow>? FansFrom(LiveData live) => FansFrom(live.AllSensors, live.ReadingsPending, _facts);

    internal static List<FanNow>? FansFrom(IEnumerable<SensorItem> sensors, bool pending, Core.Reports.PcFacts? facts)
    {
        if (pending) return null;
        var fans = sensors.Where(x => x.Kind == SensorKind.Fan).ToList();
        var spun = facts is { } f && (f.Holds & Core.Reports.PcNeeds.Fans) != 0 ? f.FansSpun : null;
        var rows = Core.Reports.FanList.Rows(fans, x => x.HardwareType, x => x.HardwareName,
            x => x.Value >= Core.Reports.FanList.MinSpinRpm || x.Max >= Core.Reports.FanList.MinSpinRpm || spun?.Contains(x.Id) == true);
        // None to list before the records are read: a fan that spun yesterday may yet be one, so not "none" yet.
        if (rows.Count == 0 && fans.Count > 0 && spun is null) return null;
        return [.. rows.Select(r => new FanNow(Core.Reports.FanList.Title(r.Count, r[0].DisplayName), Core.Reports.FanList.Speed(r.Select(x => x.Value))))];
    }

    // "Never read" is 30 s before the clock's start: long.MinValue made now minus it run over, so it was never read.
    private static (long At, List<VolumeSpace> List) _volumes = (-30_000, []);

    // What the tiles from RigSight's records show, read in the background as the agent reads it (PcFacts), about once
    // a minute while a widget has such a tile; the page draws the widget again when it has come.
    private static volatile Core.Reports.PcFacts? _facts;
    private static DateTime _factsAt;
    private static int _factsReading;

    // Everyone who drew while a reading was due, told when it's in. Before, only the one that started a reading could
    // be told, and no picture asked: a Most used or Last 7 days tile stayed a heading until the widget was changed.
    private static readonly List<Action> _whenRead = [];

    /// <summary>
    /// Has the records read for these widgets, when one has a tile that shows them and the last reading is old;
    /// <paramref name="read"/> is told when it's in (from another thread), with everyone else who drew meanwhile.
    /// </summary>
    internal static void KeepFacts(IEnumerable<WidgetConfig> widgets, RigsightSettings settings, IReadOnlyCollection<Core.Reports.DriveCheck> drives, Action? read = null)
    {
        var needs = Core.Reports.PcFacts.NeedsOf(widgets);
        if (needs == Core.Reports.PcNeeds.None) return;
        var have = _facts;
        var now = DateTime.Now;
        if (have is not null && (have.Holds & needs) == needs && now - _factsAt < Core.Reports.PcFacts.Every && now.Date == _factsAt.Date) return;
        if (read is not null) lock (_whenRead) _whenRead.Add(read);
        // Every widget's needs together (the page draws them one by one): one reading serves them all.
        if (have is not null && now.Date == _factsAt.Date) needs |= have.Holds;
        if (Interlocked.CompareExchange(ref _factsReading, 1, 0) != 0) return;
        _factsAt = now;
        var s = settings.Clone();
        _ = Task.Run(() =>
        {
            bool done = false;
            try
            {
                using var db = Core.Data.RigsightDb.OpenReader();
                if (db is null) return;
                _facts = Core.Reports.PcFacts.Read(db, s, DateTime.Now, needs, drives, Core.Reports.HomeCards.Fullest(), have);
                done = true;
            }
            catch (Exception ex)
            {
                Log.Error("widgets", ex);
            }
            finally
            {
                Volatile.Write(ref _factsReading, 0);
                Action[] waiting;
                lock (_whenRead)
                {
                    waiting = [.. _whenRead];
                    _whenRead.Clear();
                }
                // Not after a failed reading: drawing again would only ask again, over and over.
                if (done) foreach (var told in waiting) told();
            }
        });
    }

    /// <summary>The last five minutes of a reading, as the agent gives a widget (one value a second).</summary>
    private static float[] Recent(SensorItem? s)
    {
        if (s is null || s.History.Count == 0) return [];
        long until = s.History.LastTime, from = until - 300_000;
        int first = s.History.IndexAtOrAfter(from);
        var values = new List<float>();
        for (int i = Math.Max(0, first); i < s.History.Count; i++) values.Add((float)s.History.ValueAt(i));
        return [.. values];
    }

    /// <summary>
    /// A widget as an image at <paramref name="scale"/> (its size times the screen's), with the app's own look for "Like
    /// RigSight". <paramref name="again"/> is told (from another thread) when what its tiles from the records show has
    /// been read, to draw it again with them.
    /// </summary>
    public static ImageSource? Draw(WidgetConfig cfg, LiveData? live, RigsightSettings settings, float scale = 1, Action? again = null)
    {
        try
        {
            WidgetRenderer.UseAppLook(settings.Theme, settings.DarkTheme, settings.LightTheme, settings.Accent);
            if (cfg.Tiles is { Count: > 0 })
                KeepFacts([cfg], settings, live is null ? [] : [.. live.Drives.Select(x => new Core.Reports.DriveCheck(x.Name, x.HasHealth, x.HealthReason is not null))], again);
            using var bmp = WidgetRenderer.Render(cfg, DataFrom(live), scale, hover: false, out _);
            return TaskbarBar.ToImage(bmp, scale);
        }
        catch (Exception ex)
        {
            Log.Error("widgets", ex);
            return null;
        }
    }

    /// <summary>A widget's size at size M, in pixels.</summary>
    public static (int W, int H) SizeOf(WidgetConfig cfg)
    {
        if (cfg.Tiles is { } tiles) return WidgetTiles.BoxPixels(tiles);
        using var bmp = WidgetRenderer.Render(cfg, null, 1, hover: false, out _);
        return (bmp.Width, bmp.Height);
    }
}
