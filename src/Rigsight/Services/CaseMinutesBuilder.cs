using Rigsight.Core;
using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;
using Rigsight.Models;

namespace Rigsight.Services;

/// <summary>
/// Everything recorded about the PC in the minutes before a crash, made ready for its lanes, strips, marks and the box
/// beside them, and what stood out (worked out in Core, <see cref="CaseMinutes"/>). A lane or strip with nothing
/// recorded in those minutes is left out rather than drawn empty; the box keeps all its lines, with a dash.
/// </summary>
internal static class CaseMinutesBuilder
{
    public sealed record Built(List<CaseMinute> Minutes, List<CaseLane> Lanes, List<CaseStrip> Strips, List<CaseMark> Marks,
        List<StoodOut> StoodOut, CaseReadout Readout);

    /// <summary>The most fans drawn on the Fans lane (the fastest-turning); the box lists the first <see cref="CaseReadout.FanLines"/>.</summary>
    private const int FanSeries = 4;

    public static Built Build(RigsightDb db, RigsightSettings s, DateTime from, DateTime to, DateTime crash, IReadOnlyList<SessionRow> sessions)
    {
        long f = TimeUtil.ToUnix(from), t = TimeUtil.ToUnix(to) + 60, at = TimeUtil.ToUnix(crash);
        var rows = db.GetMinutes(f, t);
        var appRows = db.LoadApps().ToDictionary(a => a.Id);
        string NameOf(long id) => appRows.TryGetValue(id, out var a) ? (s.AppNames.TryGetValue(a.Exe, out var alias) ? alias : AppCatalog.KnownName(a.Exe) ?? a.Name) : "An app";
        // An app's own colour, the one the Apps page gives its kind (a shade of the accent once one is picked).
        string? ColorOf(long id) => appRows.TryGetValue(id, out var a)
            ? ThemeManager.CategoryHex(s.AppCategories.TryGetValue(a.Exe, out var c) ? c : a.Category) : null;

        var apps = db.GetAppMinutes(f, t).ToLookup(a => a.Ts);
        var fans = db.GetFans().ToDictionary(x => x.Id, x => x.Name);
        var fanRows = db.GetFanMinutes(f, t);
        // The fastest-turning fans: the ones the lane draws and the box lists, in that order.
        var fanOrder = fanRows.GroupBy(x => x.Fan).OrderByDescending(g => g.Max(x => x.RpmMax)).Select(g => g.Key).ToList();
        var fanByMinute = fanRows.ToLookup(x => x.Ts);
        var net = db.GetNetMinutes(f, t).ToDictionary(x => x.Ts);
        var drops = db.GetNetDrops(f - 3600, t);
        double? ramTotal = db.GetInventoryValue(Inventory.Ram) is { } installed
            && double.TryParse(installed.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture, out var gb) ? gb : null;
        static string Gb(double mb) => mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";

        var minutes = rows.Select((m, i) =>
        {
            var mine = apps[m.Ts].ToList();
            net.TryGetValue(m.Ts, out var n);
            return new CaseMinute
            {
                Time = TimeUtil.FromUnix(m.Ts),
                CpuTemp = m.CpuTemp ?? m.CpuTempMax, GpuTemp = m.GpuTemp ?? m.GpuTempMax, GpuHotSpot = m.GpuHotAvg ?? m.GpuHotMax,
                CpuLoad = m.CpuLoad, GpuLoad = m.GpuLoad, CpuPower = m.CpuPower, GpuPower = m.GpuPower,
                RamGb = m.RamUsed, CommitPct = m.CommitPct, VramMb = m.VramUsed, VramTotalMb = m.VramTotal,
                DiskMbs = m.DiskRate / 1_048_576,
                DownMbps = n is null ? null : n.Down * 8 / 60.0 / 1_000_000, UpMbps = n is null ? null : n.Up * 8 / 60.0 / 1_000_000,
                Battery = m.Battery, OnBattery = m.OnBattery,
                Front = m.FgApp is long fg && m.ActiveSec > 0 ? NameOf(fg) : null,
                Here = m.ActiveSec > 0,
                Fans = [.. fanByMinute[m.Ts].OrderBy(x => fanOrder.IndexOf(x.Fan)).Select(x => (fans.GetValueOrDefault(x.Fan) ?? "Fan", x.RpmAvg))],
                // With how much where each minute's apps were kept (0.24.0 on); before that, the one app the minute
                // names as working the part hardest, without a figure.
                CpuApps = mine.Count > 0 ? [.. mine.Where(a => a.Cpu >= 1).OrderByDescending(a => a.Cpu).Select(a => new CaseApp(NameOf(a.App), $"{a.Cpu:0}%"))]
                    : m.CpuApp is long cpuApp ? [new CaseApp(NameOf(cpuApp), "hardest")] : [],
                GpuApps = mine.Count > 0 ? [.. mine.Where(a => a.Gpu >= 1).OrderByDescending(a => a.Gpu).Select(a => new CaseApp(NameOf(a.App), $"{a.Gpu:0}%"))]
                    : m.GpuApp is long gpuApp ? [new CaseApp(NameOf(gpuApp), "hardest")] : [],
                MemApps = [.. mine.Where(a => a.MemMb >= 100).OrderByDescending(a => a.MemMb).Select(a => new CaseApp(NameOf(a.App), Gb(a.MemMb)))],
                Flags = Flags(rows, i),
            };
        }).ToList();

        // ── Lanes: one value per minute for each line ──
        double?[] Of(Func<CaseMinute, double?> value) => [.. minutes.Select(value)];
        bool Any(Func<CaseMinute, double?> value) => minutes.Any(m => value(m) is not null);
        var lanes = new List<CaseLane>();
        void Lane(string name, string unit, double least, params (string Label, string Brush, Func<CaseMinute, double?> Value)[] lines)
        {
            var drawn = lines.Where(l => Any(l.Value)).ToList();
            if (drawn.Count == 0) return;
            double top = drawn.SelectMany(l => minutes.Select(l.Value)).OfType<double>().DefaultIfEmpty(0).Max();
            lanes.Add(new CaseLane(name, unit, Nice(Math.Max(least, top)), [.. drawn.Select(l => new CaseSeries(l.Label, l.Brush, Of(l.Value)))]));
        }
        double? T(double? celsius) => celsius is double c ? Units.Temp(c) : null;
        Lane("Temperature", "°", Units.Fahrenheit ? 212 : 100,
            ("CPU", "CpuBrush", m => T(m.CpuTemp)), ("GPU", "GpuBrush", m => T(m.GpuTemp)), ("GPU hot spot", "PurpleBrush", m => T(m.GpuHotSpot)));
        Lane("Load", "%", 100, ("CPU", "CpuBrush", m => m.CpuLoad), ("GPU", "GpuBrush", m => m.GpuLoad));
        Lane("Power", " W", 50, ("CPU", "CpuBrush", m => m.CpuPower), ("GPU", "GpuBrush", m => m.GpuPower));
        Lane("Memory", " GB", ramTotal ?? 4, ("In use", "RamBrush", m => m.RamGb), ("GPU memory", "GpuBrush", m => m.VramMb / 1024));
        // The fastest-turning fans, each a line in a colour of its own from the palette.
        var fanSeries = fanOrder.Take(FanSeries).ToList();
        string[] fanBrushes = ["PinkBrush", "SkyBrush", "PurpleBrush", "CpuBrush"];
        if (fanSeries.Count > 0)
        {
            var series = fanSeries.Select((id, k) => new CaseSeries(fans.GetValueOrDefault(id) ?? "Fan", fanBrushes[k],
                [.. minutes.Select(m => fanByMinute[TimeUtil.ToUnix(m.Time)].FirstOrDefault(x => x.Fan == id) is { } r ? (double?)r.RpmAvg : null)])).ToList();
            double top = series.SelectMany(x => x.Values).OfType<double>().DefaultIfEmpty(0).Max();
            lanes.Add(new CaseLane("Fans", " rpm", Nice(Math.Max(1000, top)), series));
        }
        Lane("Drives", " MB/s", 10, ("Read and written", "PinkBrush", m => m.DiskMbs));
        Lane("Network", " Mbps", 1, ("Download", "SkyBrush", m => m.DownMbps), ("Upload", "PurpleBrush", m => m.UpMbps));

        // ── Strips: who was in front, and who worked each part hardest ──
        var strips = new List<CaseStrip>();
        void Strip(string name, Func<SystemMinute, long?> app, string? nobody)
        {
            if (!rows.Any(m => app(m) is not null)) return;
            var bands = new List<CaseBand>();
            foreach (var m in rows)
            {
                var time = TimeUtil.FromUnix(m.Ts);
                long? id = app(m);
                string? who = id is long x ? NameOf(x) : nobody;
                if (bands.Count > 0 && bands[^1].App == who && (time - bands[^1].To).TotalSeconds <= 90) bands[^1] = bands[^1] with { To = time.AddMinutes(1) };
                else bands.Add(new CaseBand(time, time.AddMinutes(1), who, id is long y ? ColorOf(y) : null));
            }
            strips.Add(new CaseStrip(name, bands));
        }
        Strip("In front", m => m.ActiveSec > 0 ? m.FgApp : null, "Nobody at the PC");
        Strip("Hardest on the CPU", m => m.CpuApp, null);
        Strip("Hardest on the GPU", m => m.GpuApp, null);

        // ── Marks: an app on a strip opening in these minutes, and the crash ──
        var onStrips = strips.SelectMany(x => x.Bands).Select(b => b.App).OfType<string>().ToHashSet();
        var marks = sessions.Where(x => x.Start > f && x.Start < at && onStrips.Contains(NameOf(x.AppId)))
            .GroupBy(x => NameOf(x.AppId)).Select(g => g.MinBy(x => x.Start)!)
            .OrderByDescending(x => x.Start).Take(3)
            .Select(x => new CaseMark(TimeUtil.FromUnix(x.Start), NameOf(x.AppId), false)).ToList();
        marks.Add(new CaseMark(crash, "Crash", true));
        // The box says it too, for the minute it happened in: a line on the lanes is never left to be guessed at.
        foreach (var mark in marks.Where(k => !k.IsCrash))
            if (minutes.FindLastIndex(m => m.Time <= mark.At) is int found and >= 0 && minutes[found].Flags.Count < 2)
                minutes[found].Flags.Add($"{mark.Label} opened");

        // Never in the way of the crash opening: what stood out is left out, and the log says why.
        List<StoodOut> stood;
        try { stood = CaseMinutes.Find(rows, [.. apps.SelectMany(g => g)], fanRows, fans, drops, at, ramTotal, NameOf); }
        catch (Exception ex)
        {
            Core.Log.Error("crashes", ex);
            stood = [];
        }
        // On a standard account, why the CPU's temperature and power and the motherboard's fans aren't there: missing
        // lines and dashes read as "nothing happened" without it. Only where they really are missing from these minutes.
        if (Account.Kind == AccountKind.Standard && minutes.Count > 0 && minutes.All(m => m.CpuTemp is null))
            stood.Insert(stood.FindIndex(s => s.IsCalm) is int calm and >= 0 ? calm : stood.Count,
                new StoodOut("Windows keeps the CPU's temperature and power and the motherboard's fans for admin accounts, so they aren't shown here.", false));
        int crashIndex = minutes.FindLastIndex(m => m.Time <= crash);
        return new Built(minutes, lanes, strips, marks, stood, new CaseReadout(minutes, Math.Max(0, crashIndex), ramTotal, crash));
    }

    /// <summary>What stands out in one minute alone, for the box: a part that warmed fast, memory nearly out, a battery nearly empty.</summary>
    private static List<string> Flags(List<SystemMinute> rows, int i)
    {
        var m = rows[i];
        var flags = new List<string>();
        var earlier = rows.Take(i).LastOrDefault(x => x.Ts <= m.Ts - 300 && x.Ts >= m.Ts - 420);
        if (earlier is not null)
        {
            if ((m.CpuTemp ?? m.CpuTempMax) - (earlier.CpuTemp ?? earlier.CpuTempMax) is double cpu && cpu >= 6) flags.Add($"CPU up {Delta(cpu)} in 5 minutes");
            if ((m.GpuTemp ?? m.GpuTempMax) - (earlier.GpuTemp ?? earlier.GpuTempMax) is double gpu && gpu >= 6) flags.Add($"GPU up {Delta(gpu)} in 5 minutes");
        }
        if (m.CommitPct >= 90) flags.Add("Windows nearly out of memory");
        if (m is { OnBattery: true, Battery: <= 10 }) flags.Add($"On its battery, at {m.Battery}%");
        return [.. flags.Take(2)];
    }

    /// <summary>A rise in temperature, in the unit the user reads temperatures in.</summary>
    private static string Delta(double celsius) => $"{(Units.Fahrenheit ? celsius * 9 / 5 : celsius):0}°";

    /// <summary>A round top for a scale: 1, 2 or 5 times a power of ten, at or above <paramref name="v"/>.</summary>
    internal static double Nice(double v)
    {
        if (v <= 0) return 1;
        double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (double step in new[] { 1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 })
            if (step * p >= v - 1e-9) return step * p;
        return 10 * p;
    }
}
