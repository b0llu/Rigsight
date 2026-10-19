using Rigsight.Core;
using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;
using Rigsight.Models;

namespace Rigsight.Services;

/// <summary>
/// A crash's close-up (<see cref="CloseUp"/>) made ready for the same lanes, strips and box as its minutes: a reading
/// about every two seconds, what stood out in them (worked out in Core, <see cref="CloseUpWords"/>), and the crash
/// marked where it happened, with its two minutes after.
/// </summary>
internal static class CloseUpBuilder
{
    /// <summary>Readings further apart than this are a break: the sensors are read every two seconds.</summary>
    private const double BreakSec = 8;

    public static CaseView? Build(RigsightDb db, RigsightSettings s, CloseUp closeUp, CrashEvent crash, DateTime now)
    {
        var points = closeUp.Points.OrderBy(p => p.T).ToList();
        if (points.Count == 0) return null;
        var appRows = db.LoadApps().ToDictionary(a => a.Id);
        string NameOf(long id) => appRows.TryGetValue(id, out var a) ? (s.AppNames.TryGetValue(a.Exe, out var alias) ? alias : AppCatalog.KnownName(a.Exe) ?? a.Name) : Lang.T("An app");
        string? ColorOf(long id) => appRows.TryGetValue(id, out var a)
            ? ThemeManager.CategoryHex(s.AppCategories.TryGetValue(a.Exe, out var c) ? c : a.Category) : null;
        static string Gb(double mb) => mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";
        double? ramTotal = db.GetInventoryValue(Inventory.Ram) is { } installed
            && double.TryParse(installed.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture, out var gb) ? gb : null;

        // At Windows' own time for the crash.
        var at = crash.Time;
        var minutes = points.Select((p, i) => new CaseMinute
        {
            Time = p.Time,
            CpuTemp = p.CpuTemp, GpuTemp = p.GpuTemp, GpuHotSpot = p.GpuHot,
            CpuLoad = p.CpuLoad, GpuLoad = p.GpuLoad, CpuPower = p.CpuPower, GpuPower = p.GpuPower,
            RamGb = p.Ram, CommitPct = p.Commit, VramMb = p.Vram, VramTotalMb = p.VramTotal,
            DiskMbs = p.Disk / 1_048_576, DownMbps = p.Down * 8 / 1_000_000, UpMbps = p.Up * 8 / 1_000_000,
            Battery = p.Battery, OnBattery = p.OnBattery,
            Front = p.Here && p.Front is long f ? NameOf(f) : null,
            Here = p.Here,
            Fans = [.. (p.Fans ?? []).Select(x => (x.Name, x.Rpm))],
            CpuApps = [.. (p.Apps ?? []).Where(a => a.Cpu >= 1).OrderByDescending(a => a.Cpu).Select(a => new CaseApp(NameOf(a.App), $"{a.Cpu:0}%"))],
            GpuApps = [.. (p.Apps ?? []).Where(a => a.Gpu >= 1).OrderByDescending(a => a.Gpu).Select(a => new CaseApp(NameOf(a.App), $"{a.Gpu:0}%"))],
            MemApps = [.. (p.Apps ?? []).Where(a => a.MemMb >= 100).OrderByDescending(a => a.MemMb).Select(a => new CaseApp(NameOf(a.App), Gb(a.MemMb)))],
            Flags = Flags(points, i),
        }).ToList();

        // ── Lanes ──
        bool Any(Func<CaseMinute, double?> value) => minutes.Any(m => value(m) is not null);
        var lanes = new List<CaseLane>();
        void Lane(string name, string unit, double least, params (string Label, string Brush, Func<CaseMinute, double?> Value)[] lines)
        {
            var drawn = lines.Where(l => Any(l.Value)).ToList();
            if (drawn.Count == 0) return;
            double top = drawn.SelectMany(l => minutes.Select(l.Value)).OfType<double>().DefaultIfEmpty(0).Max();
            lanes.Add(new CaseLane(name, unit, CaseMinutesBuilder.Nice(Math.Max(least, top)), [.. drawn.Select(l => new CaseSeries(l.Label, l.Brush, [.. minutes.Select(l.Value)]))]));
        }
        double? T(double? celsius) => celsius is double c ? Units.Temp(c) : null;
        Lane(Lang.T("Temperature"), "°", Units.Fahrenheit ? 212 : 100,
            ("CPU", "CpuBrush", m => T(m.CpuTemp)), ("GPU", "GpuBrush", m => T(m.GpuTemp)), (Lang.T("GPU hot spot"), "PurpleBrush", m => T(m.GpuHotSpot)));
        Lane(Lang.T("Load"), "%", 100, ("CPU", "CpuBrush", m => m.CpuLoad), ("GPU", "GpuBrush", m => m.GpuLoad));
        Lane(Lang.T("Power"), " W", 50, ("CPU", "CpuBrush", m => m.CpuPower), ("GPU", "GpuBrush", m => m.GpuPower));
        Lane(Lang.T("Memory"), " GB", ramTotal ?? 4, (Lang.T("In use"), "RamBrush", m => m.RamGb), (Lang.T("GPU memory"), "GpuBrush", m => m.VramMb / 1024));
        // The fastest-turning fans, by name, each in a colour of its own from the palette.
        var fanNames = points.SelectMany(p => p.Fans ?? []).GroupBy(x => x.Name).OrderByDescending(g => g.Max(x => x.Rpm)).Select(g => g.Key).Take(4).ToList();
        if (fanNames.Count > 0)
        {
            string[] fanBrushes = ["PinkBrush", "SkyBrush", "PurpleBrush", "CpuBrush"];
            var series = fanNames.Select((name, k) => new CaseSeries(name, fanBrushes[k],
                [.. minutes.Select(m => m.Fans.FirstOrDefault(x => x.Name == name) is { Name: not null } r ? (double?)r.Rpm : null)])).ToList();
            double top = series.SelectMany(x => x.Values).OfType<double>().DefaultIfEmpty(0).Max();
            lanes.Add(new CaseLane(Lang.T("Fans"), " rpm", CaseMinutesBuilder.Nice(Math.Max(1000, top)), series));
        }
        Lane(Lang.T("Drives"), " MB/s", 10, (Lang.T("Read and written"), "PinkBrush", m => m.DiskMbs));
        Lane(Lang.T("Network"), " Mbps", 1, (Lang.T("Download"), "SkyBrush", m => m.DownMbps), (Lang.T("Upload"), "PurpleBrush", m => m.UpMbps));

        // ── Strips: who was in front, and who worked each part clearly hardest ──
        var strips = new List<CaseStrip>();
        void Strip(string name, Func<CloseUpPoint, long?> app, string? nobody)
        {
            if (!points.Any(p => app(p) is not null)) return;
            var bands = new List<CaseBand>();
            foreach (var p in points)
            {
                long? id = app(p);
                string? who = id is long x ? NameOf(x) : nobody;
                if (bands.Count > 0 && bands[^1].App == who && (p.Time - bands[^1].To).TotalSeconds <= BreakSec) bands[^1] = bands[^1] with { To = p.Time.AddSeconds(2) };
                else bands.Add(new CaseBand(p.Time, p.Time.AddSeconds(2), who, id is long y ? ColorOf(y) : null));
            }
            strips.Add(new CaseStrip(name, bands));
        }
        static long? Hardest(CloseUpPoint p, Func<CloseUpApp, double> share)
        {
            var ranked = (p.Apps ?? []).OrderByDescending(share).Take(2).ToList();
            return ranked.Count > 0 && share(ranked[0]) >= 10 && share(ranked[0]) >= (ranked.Count > 1 ? share(ranked[1]) : 0) * 1.5 ? ranked[0].App : null;
        }
        Strip(Lang.T("In front"), p => p.Here ? p.Front : null, Lang.T("Nobody at the PC"));
        Strip(Lang.T("Hardest on the CPU"), p => Hardest(p, a => a.Cpu), null);
        Strip(Lang.T("Hardest on the GPU"), p => Hardest(p, a => a.Gpu), null);

        // ── The crash, and how far the close-up reaches ──
        string word = crash.Kind switch
        {
            CrashKind.UnexpectedShutdown => Lang.T("Sudden shut-off"), CrashKind.SystemCrash => Lang.T("Blue screen"),
            CrashKind.GpuDriverReset => Lang.T("Graphics driver reset"), _ => Lang.T("Crash"),
        };
        var marks = new List<CaseMark> { new(at, word, true) };
        var from = points[0].Time;
        var to = crash.Time.AddSeconds(CloseUp.AfterSec);
        if (to < points[^1].Time) to = points[^1].Time;
        var gaps = new List<CaseGap>();
        for (int i = 1; i < points.Count; i++)
            if ((points[i].Time - points[i - 1].Time).TotalSeconds > BreakSec) gaps.Add(new CaseGap(points[i - 1].Time, points[i].Time, Wording.NotRecorded));
        // The after still to come (it is added two minutes on), or never kept (the PC went to sleep, tracking was paused).
        if ((to - points[^1].Time).TotalSeconds > BreakSec)
            gaps.Add(new CaseGap(points[^1].Time, to, !closeUp.Done && now < to.AddSeconds(30) ? Lang.T("Still being recorded") : Wording.NotRecorded));

        var stood = CloseUpWords.Find(points, crash.Ts, NameOf,
            crash.AppExe.Length > 0 ? appRows.Values.FirstOrDefault(a => a.Exe.Equals(crash.AppExe, StringComparison.OrdinalIgnoreCase))?.Id : null);
        int crashIndex = minutes.FindLastIndex(m => m.Time <= at.AddSeconds(1));
        return new CaseView
        {
            Minutes = minutes, Lanes = lanes, Strips = strips, Marks = marks, Gaps = gaps, StoodOut = stood,
            Readout = new CaseReadout(minutes, Math.Max(0, crashIndex), ramTotal, at, seconds: true, word: WordOf(crash.Kind)),
            ChartFrom = from, ChartTo = to, BreakSec = BreakSec, TickEvery = TimeSpan.FromMinutes(1),
            Rate = Lang.T("a reading every 2 to 3 seconds"), IsCloseUp = true,
        };
    }

    /// <summary>What it was, as the box beside the lanes says it.</summary>
    internal static string WordOf(CrashKind kind) => kind switch
    {
        CrashKind.UnexpectedShutdown => Lang.T("sudden shut-off"), CrashKind.SystemCrash => Lang.T("blue screen"),
        CrashKind.GpuDriverReset => Lang.T("graphics driver reset"), _ => Lang.T("crash"),
    };

    /// <summary>What stands out at one reading against half a minute before it: a part warming fast, power jumping.</summary>
    private static List<string> Flags(List<CloseUpPoint> points, int i)
    {
        var p = points[i];
        var flags = new List<string>();
        var earlier = points.Take(i).LastOrDefault(x => x.T <= p.T - 28_000 && x.T >= p.T - 40_000);
        if (earlier is not null)
        {
            void Rise(string what, double? now, double? then)
            {
                if (now - then is double d && d >= 5) flags.Add(Lang.T($"{what} up {(Units.Fahrenheit ? d * 9 / 5 : d):0}° in 30 s"));
            }
            Rise(Lang.T("GPU hot spot"), p.GpuHot, earlier.GpuHot);
            Rise("GPU", p.GpuTemp, earlier.GpuTemp);
            Rise("CPU", p.CpuTemp, earlier.CpuTemp);
            if (p.GpuPower - earlier.GpuPower is double w && w >= CloseUpWords.PowerWatts && p.GpuPower >= earlier.GpuPower * CloseUpWords.PowerTimes) flags.Add(Lang.T($"GPU power up {w:0} W in 30 s"));
        }
        if (p.Commit >= 95) flags.Add(Lang.T("Windows nearly out of memory"));
        if (p.VramTotal is double total && total > 0 && p.Vram >= total * 0.97) flags.Add(Lang.T("Graphics memory nearly full"));
        return [.. flags.Take(2)];
    }
}
