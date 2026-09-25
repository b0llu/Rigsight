using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Rigsight.Agent.Widgets;
using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Agent;

/// <summary>
/// Drawing tests: GDI+ drawing in the agent happens on one thread (its font cache isn't shared safely), and the
/// timing and handle counts are only meaningful with nothing else running, so these run alone, one at a time.
/// </summary>
[CollectionDefinition("Drawing", DisableParallelization = true)]
public sealed class DrawingCollection;

/// <summary>What widgets and the overlay are drawn from: typical, empty, extreme.</summary>
internal static class WidgetSamples
{
    public static float[] Wave(int n, double center = 60, double amplitude = 12) =>
        [.. Enumerable.Range(0, n).Select(i => (float)(center + amplitude * Math.Sin(i / 9.0)))];

    public static WidgetData Full() => new()
    {
        CpuTemp = 62.4, CpuLoad = 37, CpuPower = 88.5, CpuClock = 4450, GpuTemp = 71, GpuLoad = 97, GpuPower = 320,
        GpuHotSpot = 84, GpuClock = 1905, RamLoad = 58, RamUsedGb = 18.6, RamTotalGb = 32, VramUsedMb = 9800, VramTotalMb = 12288,
        Frame = new FrameStats(144.2, 6.9, 97),
        CpuHistory = Wave(300), GpuHistory = Wave(300, 70),
        Activity = new ActivityInfo
        {
            Exe = "cyberpunk2077.exe", Name = "Cyberpunk 2077", Category = AppCategory.Game, Present = true, Fullscreen = true,
            SessionStart = 1, SessionActiveSec = 5400, SessionCpuMax = 78, SessionGpuMax = 74,
        },
        Sensors =
        [
            new("Pump", SensorKind.Fan, 2100), new("Water", SensorKind.Temperature, 31.5), new("12V", SensorKind.Voltage, 12.096),
            new("VRM", SensorKind.Power, 41.2), new("Case", SensorKind.Control, 55),
        ],
        Today = new TodayInfo
        {
            OnSec = 8 * 3600, ActiveSec = 6 * 3600 + 720, IdleSec = 3000, TopApp = "Cyberpunk 2077", TopAppSec = 3 * 3600,
            CpuPeak = 81, CpuPeakApp = "Cyberpunk 2077", GpuPeak = 76, GpuPeakApp = "Cyberpunk 2077",
        },
    };

    /// <summary>No sensors at all (a PC without admin rights or drivers): every reading missing.</summary>
    public static WidgetData Empty() => new();

    /// <summary>The limits: very hot, flat out, huge memory, a very long name, broken history.</summary>
    public static WidgetData Extreme() => new()
    {
        CpuTemp = 150, CpuLoad = 100, CpuPower = 999.9, CpuClock = 9999, GpuTemp = 150, GpuLoad = 100, GpuPower = 1500,
        GpuHotSpot = 150, GpuClock = 9999, RamLoad = 100, RamUsedGb = 2048, RamTotalGb = 2048, VramUsedMb = 1_000_000_000, VramTotalMb = 1_000_000_000,
        Frame = new FrameStats(9999, 0.1, null),
        CpuHistory = [float.NaN, 150, float.PositiveInfinity, 0, float.NaN, 150, -40],
        GpuHistory = [.. Enumerable.Repeat(150f, 1800)],
        Activity = new ActivityInfo
        {
            Name = "The Elder Scrolls V: Skyrim Special Edition — Anniversary Upgrade with a very long name indeed ✨🎮",
            Exe = "skyrim.exe", Category = AppCategory.Game, Present = true, SessionActiveSec = 99 * 3600 + 3599, SessionCpuMax = 150, SessionGpuMax = 150,
        },
        Sensors = [.. Enumerable.Range(0, OverlaySettings.MaxSensors).Select(i =>
            new OverlaySensorReading(new string('W', OverlaySettings.MaxLabelLength), (SensorKind)(i % 21), i % 3 == 0 ? null : 1e9 * i))],
        Today = new TodayInfo
        {
            OnSec = 24 * 3600, ActiveSec = 24 * 3600, TopApp = new string('M', 200), TopAppSec = 24 * 3600, CpuPeak = 150, GpuPeak = 150,
        },
    };

    /// <summary>Zeros and the lowest readings.</summary>
    public static WidgetData Zero() => new()
    {
        CpuTemp = 0, CpuLoad = 0, CpuPower = 0, CpuClock = 0, GpuTemp = 0, GpuLoad = 0, GpuPower = 0, GpuHotSpot = 0, GpuClock = 0,
        RamLoad = 0, RamUsedGb = 0, RamTotalGb = 0, VramUsedMb = 0, VramTotalMb = 0,
        Frame = new FrameStats(0, 0, 0),
        CpuHistory = [0, 0], GpuHistory = [0],
        Activity = new ActivityInfo { Name = "", Exe = "x.exe", Present = false },
        Sensors = [new("", SensorKind.Temperature, 0)],
        Today = new TodayInfo { TopApp = "" },
    };

    public static IEnumerable<(string Name, WidgetData? Data)> AllData() =>
        [("full", Full()), ("null", null), ("empty", Empty()), ("extreme", Extreme()), ("zero", Zero()),
         ("paused", new WidgetData { Activity = new ActivityInfo { Paused = true } })];

    public static OverlaySettings AllMetrics(OverlayLayout layout = OverlayLayout.Rows) => new()
    {
        Layout = layout, Metrics = [.. Enum.GetValues<OverlayMetric>()],
    };

    /// <summary>Share of pixels drawn at all (alpha above zero).</summary>
    public static double Coverage(Bitmap bmp)
    {
        var raw = Raw(bmp);
        int drawn = 0;
        foreach (int argb in raw) if ((uint)argb >> 24 != 0) drawn++;
        return drawn / (double)raw.Length;
    }

    public static Color[] Pixels(Bitmap bmp) => [.. Raw(bmp).Select(Color.FromArgb)];

    private static int[] Raw(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var raw = new int[bmp.Width * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, raw, y * bmp.Width, bmp.Width);
            return raw;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public static bool SamePixels(Bitmap a, Bitmap b) => a.Size == b.Size && Raw(a).AsSpan().SequenceEqual(Raw(b));
}

/// <summary>Counts of this process's GDI and USER objects, to catch handle leaks in code that runs every second.</summary>
internal static partial class GuiResources
{
    [LibraryImport("user32.dll")]
    private static partial uint GetGuiResources(IntPtr process, uint flags);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    public static uint Gdi => GetGuiResources(GetCurrentProcess(), 0);
    public static uint User => GetGuiResources(GetCurrentProcess(), 1);
}
