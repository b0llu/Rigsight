using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Rigsight.Agent.Widgets;
using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Core.Settings;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;

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
        Pc = PcSample(), Facts = FactsSample(),
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
        Pc = PcExtreme(), Facts = FactsExtreme(),
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
        Pc = new PcNow { Fans = [], Drives = [], Cores = [], TopCpu = [], TopMemory = [], NetApps = [], NetDown = 0, NetUp = 0, DownHistory = [0], UpHistory = [] },
        Facts = new PcFacts { Holds = (PcNeeds)255, Quarters = [] },
    };

    /// <summary>The PC's live tiles as the mock-up has them (this morning's figures).</summary>
    public static PcNow PcSample() => new()
    {
        Fans = [new("CPU fan", 820), new("Case fan 1", 640), new("Case fan 2", 655)],
        Drives = [new("C", 0.60, 178L << 30, 38), new("E", 0.84, 152L << 30, 34), new("F", 0.37, 1300L << 30, 31)],
        Cores = [.. new[] { 22, 9, 41, 6, 14, 3, 18, 5, 31, 7, 12, 4, 9, 2, 16, 6 }.Select(v => v / 100f)],
        TopCpu = [new("Google Chrome", null, "12%"), new("Discord", null, "4.0%"), new("Visual Studio Code", null, "3.0%"), new("Steam", null, "1.0%"), new("Dota 2", null, "0%")],
        TopMemory = [new("Google Chrome", null, "2.1 GB"), new("Visual Studio Code", null, "1.4 GB"), new("Discord", null, "614 MB")],
        NetPerApp = true, NetDown = 12.4 * 1048576, NetUp = 1.1 * 1048576,
        NetApps = [new("Google Chrome", null, "1.1 MB/s"), new("Steam", null, "0.3 MB/s"), new("Discord", null, "12 KB/s")],
        DownHistory = [.. Wave(60, 9, 6).Select(v => Math.Max(0.2f, v) * 1048576)],
        UpHistory = [.. Wave(60, 1, 0.7).Select(v => Math.Max(0.05f, v) * 1048576)],
    };

    /// <summary>What the tiles from the records show, as the mock-up has it (dated from today).</summary>
    public static PcFacts FactsSample()
    {
        var day = DateTime.Today;
        var f = new PcFacts
        {
            ReadAt = day.AddHours(9), Holds = (PcNeeds)255,
            MostUsed = [new("Google Chrome", null, "24m", 1), new("Visual Studio Code", null, "12m", 0.5), new("Discord", null, "6m", 0.25)],
            StandOut = ["Day started at 8:20 AM", "Steam downloaded 2.9 GB while you were away"],
            YesterdayHasData = true, YesterdayActiveSec = 8 * 3600 + 240,
            YesterdayApps = [new("Dota 2", null, "5h 55m", 0), new("Google Chrome", null, "1h 42m", 0), new("Discord", null, "19m", 0)],
            Days = [.. new[] { 6.2, 7.5, 3.1, 8.2, 5.4, 8.07 }.Select((h, i) => (day.AddDays(i - 6), h * 3600))],
            Crashes = 1, LatestCrash = "Dota 2 crashed on 2 Nov at 9:14 PM",
            Health = [new("\uE9CA", "Temperatures as usual", false), new("\uE9F5", "Fans at their usual speeds", false), new("\uE73E", "No PC problems in the last 7 days", false),
                new("\uEDA2", "Drives healthy; the E drive is 84% full", false)],
            Changes = [new(new SystemChange(day.AddDays(-1), ChangeKind.Windows, "Game Mode turned off"), "Game Mode turned off", "Yesterday"),
                new(new SystemChange(day.AddDays(-1), ChangeKind.AppUpdated, "Steam updated"), "Steam updated", "Yesterday"),
                new(new SystemChange(day.AddDays(-1), ChangeKind.AppUpdated, "Google Chrome updated"), "Google Chrome updated", "Yesterday"),
                new(new SystemChange(day.AddDays(-1), ChangeKind.AppUpdated, "Discord updated"), "Discord updated", "Yesterday")],
            LastGame = new("Dota 2", null, day.AddDays(-1).AddHours(22), 72 * 60, 141),
            NetHasData = true, NetDown = (long)(3.9 * (1L << 30)), NetUp = (long)(0.3 * (1L << 30)), NetBackground = 1L << 29,
            NetApps = [new("Steam", null, "2.9 GB", 1), new("Google Chrome", null, "1.1 GB", 0.38), new("Discord", null, "102 MB", 0.03)],
            Quarters = [.. Enumerable.Range(0, 96).Select(q => q is >= 33 and < 37)],
        };
        (f.HourTop[8], f.HourRest[8], f.HourTop[9], f.HourRest[9]) = (620, 300, 200, 100);
        return f;
    }

    /// <summary>The PC's tiles at their limits: long names, many of everything, huge and broken figures.</summary>
    public static PcNow PcExtreme() => new()
    {
        Fans = [.. Enumerable.Range(0, 30).Select(i => new FanNow(new string('F', 120) + i, i % 3 == 0 ? null : 99_999))],
        Drives = [.. Enumerable.Range(0, 26).Select(i => new DriveNow(((char)('A' + i)).ToString(), i % 2 == 0 ? 1.5 : double.NaN, long.MaxValue / 2, i % 3 == 0 ? null : 150))],
        Cores = [.. Enumerable.Range(0, 256).Select(i => i % 5 == 0 ? float.NaN : 1.5f)],
        TopCpu = [.. Enumerable.Range(0, 40).Select(i => new TileApp(new string('W', 300), @"C:\nowhere\x.exe", "100%"))],
        TopMemory = [.. Enumerable.Range(0, 40).Select(i => new TileApp("Ünïcödé 😀 שלום", null, "2048.0 GB"))],
        NetApps = [.. Enumerable.Range(0, 40).Select(i => new TileApp(new string('N', 200), null, "999.9 GB/s"))],
        NetPerApp = true, NetDown = 1e15, NetUp = double.NaN,
        DownHistory = [float.NaN, float.PositiveInfinity, -5, 1e30f], UpHistory = [.. Enumerable.Repeat(1e12f, 3600)],
    };

    public static PcFacts FactsExtreme()
    {
        var f = new PcFacts
        {
            Holds = (PcNeeds)255,
            MostUsed = [.. Enumerable.Range(0, 40).Select(i => new AppLine(new string('M', 200), @"C:\nowhere\y.exe", "99h 59m", i % 2 == 0 ? 2 : -1))],
            StandOut = [.. Enumerable.Range(0, 40).Select(i => new string('S', 400))],
            YesterdayHasData = false, YesterdayWhy = new string('Y', 900),
            Days = [(DateTime.MinValue, double.MaxValue), (DateTime.MaxValue.Date, -1)],
            Crashes = int.MaxValue, LatestCrash = string.Concat(Enumerable.Repeat("Ünïcödé 😀 crashed ", 80)),
            Health = [.. Enumerable.Range(0, 40).Select(i => new HealthLine(i % 2 == 0 ? "" : "\uE7BA", new string('H', 300), i % 3 == 0))],
            Changes = [.. Enumerable.Range(0, 40).Select(i => new ChangeCardLine(new SystemChange(DateTime.MinValue, ChangeKind.Driver, "x"), new string('C', 300), new string('W', 40), (EffectTone)(i % 3)))],
            LastGame = new(new string('G', 300), @"C:\nowhere\g.exe", DateTime.MinValue, 1e9, 9999),
            NetHasData = true, NetDown = long.MaxValue / 4, NetUp = long.MaxValue / 4, NetBackground = long.MaxValue / 4,
            NetApps = [.. Enumerable.Range(0, 40).Select(i => new AppLine(new string('A', 300), null, "1.25 TB", 1))],
            Quarters = [.. Enumerable.Range(0, 300).Select(q => q % 2 == 0)],
            Drops = [(DateTime.MinValue, DateTime.MaxValue), (DateTime.Today.AddHours(3), DateTime.Today.AddHours(2))],
        };
        Array.Fill(f.HourTop, long.MaxValue / 64);
        Array.Fill(f.HourRest, long.MaxValue / 64);
        return f;
    }

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
