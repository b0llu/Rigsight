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
        // By the name the agent gives them on the desktop: a person's own, else the sensor's in the language in use.
        foreach (var s in live.AllSensors) d.Readings[s.Id] = new OverlaySensorReading(s.DisplayName, s.Kind, s.Value);
        foreach (var m in Enum.GetValues<OverlayMetric>())
            if (SensorOf(live, m)?.CustomLabel is { Length: > 0 } name) d.Names[m] = name;
        if (live.Activity is { } a) d.Activity = a;
        d.Today = live.Today;
        d.Pc = PcFrom(live);
        d.Facts = _facts;
        return d;
    }

    /// <summary>
    /// The sensor a reading is read from, for the readings that are one sensor (see WidgetCatalog.KeyOf): its name is the
    /// reading's name on widgets, and renaming the reading in the editor renames it.
    /// </summary>
    internal static SensorItem? SensorOf(LiveData? live, OverlayMetric m) => live is null ? null : WidgetCatalog.KeyOf(m) switch
    {
        KeySensors.CpuTemp => live.CpuTemp,
        KeySensors.CpuLoad => live.CpuLoad,
        KeySensors.CpuClock => live.CpuClock,
        KeySensors.CpuPower => live.CpuPower,
        KeySensors.GpuTemp => live.GpuTemp,
        KeySensors.GpuHotSpot => live.GpuHotSpot,
        KeySensors.GpuLoad => live.GpuLoad,
        KeySensors.GpuClock => live.GpuClock,
        KeySensors.GpuPower => live.GpuPower,
        KeySensors.GpuVramUsed => live.GpuVramUsed,
        KeySensors.RamLoad => live.RamLoad,
        _ => null,
    };

    /// <summary>The PC's live tiles from the app's readings, the same ones its pages show.</summary>
    private static PcNow PcFrom(LiveData live)
    {
        var pc = new PcNow
        {
            Fans = [.. live.Fans.Select(f => new FanNow(f.DisplayName, f.Value, f.Id))],
            Cores = [.. live.AllSensors.Where(s => s.HardwareType == "Cpu" && TrayParts.IsCoreLoad(s.Kind, s.Name)).Select(s => (float)Math.Clamp((s.Value ?? 0) / 100, 0, 1))],
            TopCpu = [.. live.Procs.OrderByDescending(p => p.Cpu).Take(8).Select(p => new TileApp(p.Name, p.Path, Units.AppCpu(p.Cpu)))],
            TopMemory = [.. live.Procs.OrderByDescending(p => p.MemMB).Take(8).Select(p => new TileApp(p.Name, p.Path, Units.Megabytes(p.MemMB)))],
            NetPerApp = live.Net is { Apps.Count: > 0 },
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

    // "Never read" is 30 s before the clock's start: long.MinValue made now minus it run over, so it was never read.
    private static (long At, List<VolumeSpace> List) _volumes = (-30_000, []);

    // What the tiles from RigSight's records show, read in the background as the agent reads it (PcFacts), about once
    // a minute while a widget has such a tile; the page draws the widget again when it has come.
    private static volatile Core.Reports.PcFacts? _facts;
    private static DateTime _factsAt;
    private static int _factsReading;

    /// <summary>Has the records read for these widgets, when one has a tile that shows them and the last reading is old; <paramref name="read"/> is told when it's in.</summary>
    internal static void KeepFacts(IEnumerable<WidgetConfig> widgets, RigsightSettings settings, IReadOnlyCollection<Core.Reports.DriveCheck> drives, Action? read = null)
    {
        var needs = Core.Reports.PcFacts.NeedsOf(widgets);
        if (needs == Core.Reports.PcNeeds.None) return;
        var have = _facts;
        var now = DateTime.Now;
        if (have is not null && (have.Holds & needs) == needs && now - _factsAt < Core.Reports.PcFacts.Every && now.Date == _factsAt.Date) return;
        // Every widget's needs together (the page draws them one by one): one reading serves them all.
        if (have is not null && now.Date == _factsAt.Date) needs |= have.Holds;
        if (Interlocked.CompareExchange(ref _factsReading, 1, 0) != 0) return;
        _factsAt = now;
        var s = settings.Clone();
        _ = Task.Run(() =>
        {
            try
            {
                using var db = Core.Data.RigsightDb.OpenReader();
                if (db is null) return;
                _facts = Core.Reports.PcFacts.Read(db, s, DateTime.Now, needs, drives, Core.Reports.HomeCards.Fullest(), have);
            }
            catch (Exception ex)
            {
                Log.Error("widgets", ex);
            }
            finally
            {
                Volatile.Write(ref _factsReading, 0);
            }
            read?.Invoke();
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

    /// <summary>A widget as an image at <paramref name="scale"/> (its size times the screen's), with the app's own look for "Like RigSight".</summary>
    public static ImageSource? Draw(WidgetConfig cfg, LiveData? live, RigsightSettings settings, float scale = 1)
    {
        try
        {
            WidgetRenderer.UseAppLook(settings.Theme, settings.DarkTheme, settings.LightTheme, settings.Accent);
            if (cfg.Tiles is { Count: > 0 })
                KeepFacts([cfg], settings, live is null ? [] : [.. live.Drives.Select(x => new Core.Reports.DriveCheck(x.Name, x.HasHealth, x.HealthReason is not null))]);
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
