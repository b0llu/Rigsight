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
        foreach (var s in live.AllSensors) d.Readings[s.Id] = new OverlaySensorReading(s.Name, s.Kind, s.Value);
        if (live.Activity is { } a) d.Activity = a;
        d.Today = live.Today;
        return d;
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
