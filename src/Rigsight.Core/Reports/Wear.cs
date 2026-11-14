using Rigsight.Core.Data;

namespace Rigsight.Core.Reports;

/// <summary>
/// Where a part stands against its own earlier weeks (the "Over the months" tile): moved out of its usual band, steady
/// inside it, too soon to say (not enough weeks at the same load yet), or no reading at all.
/// </summary>
public enum WearState { Moved, Steady, TooSoon, NoReading }

/// <summary>One week of a part's wear figure (the week's Monday), and for cooling the load and rest temperatures behind it.</summary>
public sealed record WearWeek(DateTime Start, double Value, double? Load = null, double? Rest = null);

/// <summary>
/// One part on the "Over the months" tile: its weekly figure, its usual band (from its first two weeks), and where it
/// stands now. <see cref="Then"/> is the first two weeks' average, <see cref="Now"/> the last two weeks'.
/// </summary>
public sealed record WearRow
{
    /// <summary>"cpu", "gpu", "fan:&lt;id&gt;" or "drive:&lt;key&gt;".</summary>
    public required string Part { get; init; }
    public required string Name { get; init; }
    public WearState State { get; init; }
    public IReadOnlyList<WearWeek> Weeks { get; init; } = [];
    public double? Then { get; init; }
    public double? Now { get; init; }
    public double? BandLow { get; init; }
    public double? BandHigh { get; init; }
    /// <summary>The start of the first two weeks: what "since late September" is said from.</summary>
    public DateTime? ThenDate { get; init; }
    /// <summary>The latest weeks in a row outside the band ("for 4 weeks now").</summary>
    public int WeeksOutside { get; init; }
    /// <summary>The power band (W) a chip's load is read at, or the temperatures (°) a fan's speed is read at.</summary>
    public double? RangeLow { get; init; }
    public double? RangeHigh { get; init; }
    /// <summary>Cooling: the temperature under load and at rest, now (last two weeks) and then (first two).</summary>
    public double? LoadNow { get; init; }
    public double? RestNow { get; init; }
    public double? LoadThen { get; init; }
    public double? RestThen { get; init; }
    /// <summary>Drives: the share of rated life used a year at the pace seen, and bad sectors now and then.</summary>
    public double? LifePerYear { get; init; }
    public long? SectorsNow { get; init; }
    public long? SectorsThen { get; init; }
    /// <summary>Drives: the average temperature week by week.</summary>
    public IReadOnlyList<WearWeek> TempWeeks { get; init; } = [];
    /// <summary>Every week judged, not only the chart's last ones (a chart by month is drawn from these).</summary>
    public IReadOnlyList<WearWeek> History { get; init; } = [];
}

/// <summary>
/// Each part against its own earlier weeks. Cooling: how far a chip climbs above its resting temperature at the same
/// power (a warmer room lifts load and rest together, so the gap shows the cooling). Fans: speed at the same heat.
/// Drives: life left and its pace, bad sectors, temperature. Like for like or nothing: a week only counts with enough
/// minutes on both sides, and a part with too few weeks says so instead of guessing.
/// </summary>
public static class Wear
{
    /// <summary>Weeks of history looked at, weeks shown on a tile, and the weeks that make the usual band.</summary>
    public const int LookBackWeeks = 52, ChartWeeks = 12, BaselineWeeks = 2, MinWeeks = 4;

    /// <summary>A week counts for cooling with this many minutes at the load and at rest.</summary>
    public const int MinWeekMinutes = 30;

    /// <summary>The band is the first weeks' average this far either side at least (°), and a move this big (°) is worth saying.</summary>
    public const double CoolingBandMin = 1.5, CoolingMoved = 3;

    /// <summary>A fan's week counts with this many minutes at the temperatures read; a move of this share and speed is worth saying (as InsightEngine's fan-older).</summary>
    public const int FanWeekMinutes = 30, FanMovedRpm = 200;
    public const double FanMovedShare = 0.15;

    /// <summary>A drive's pace of wear is said once its readings span this many days.</summary>
    public const int DriveMinDays = 28;

    /// <summary>The Monday a week counts from, and the first Monday looked at.</summary>
    public static DateTime WeekOf(DateTime day) => day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));
    public static DateTime Origin(DateTime today) => WeekOf(today).AddDays(-7 * (LookBackWeeks - 1));

    // ── Cooling ─────────────────────────────────────────────────────────

    /// <summary>How long a chip's weeks read from a history file are kept: they move by the week, and reading them takes a year of minutes.</summary>
    public static readonly TimeSpan CoolingKeptFor = TimeSpan.FromHours(1);

    private static readonly Dictionary<(string Source, bool Gpu, DateTime Day), (DateTime At, WearRow Row)> CoolingKept = [];

    /// <summary>
    /// A chip's cooling gap week by week, up to <paramref name="today"/>. Read once and kept for <see cref="CoolingKeptFor"/>
    /// for each history file, chip and day, so Home, Reports, Temperatures and the widgets' PC health share one reading
    /// (reading it again cost a Month report about 150 ms and a Year one about 320 ms, the 2026-11-14 gate). A history held
    /// only in memory is read every time.
    /// </summary>
    public static WearRow Cooling(RigsightDb db, bool gpu, DateTime today)
    {
        var key = (db.Source, gpu, today.Date);
        bool keep = key.Source is { Length: > 0 } source && source != ":memory:";
        lock (CacheLock)
            if (keep && CoolingKept.TryGetValue(key, out var kept) && DateTime.Now - kept.At < CoolingKeptFor) return kept.Row;
        var row = CoolingRead(db, gpu, today);
        if (keep)
            lock (CacheLock)
            {
                if (CoolingKept.Count >= 16) CoolingKept.Clear(); // a few files and days at most: no need to pick which to drop
                CoolingKept[key] = (DateTime.Now, row);
            }
        return row;
    }

    private static WearRow CoolingRead(RigsightDb db, bool gpu, DateTime today)
    {
        var origin = Origin(today);
        long from = TimeUtil.ToUnix(origin), to = TimeUtil.ToUnix(today.Date.AddDays(1));
        string part = gpu ? "gpu" : "cpu", name = gpu ? "GPU" : "CPU";
        if (PowerBand(db.SamplePower(gpu, from, to)) is not { } band) return new WearRow { Part = part, Name = name, State = WearState.TooSoon };
        var load = ByWeek(db.TempAtPowerByWeek(gpu, band.Low, band.High, from, to, PowerBin));
        var rest = db.GetHeatDays(from, to).Where(d => d.App == 0)
            .GroupBy(d => (int)((d.Day - from) / 604800))
            .Select(g => (Week: g.Key, Sum: g.Sum(d => gpu ? d.GpuSum : d.CpuSum), N: g.Sum(d => gpu ? d.GpuN : d.CpuN)))
            .Where(w => w.N > 0).ToDictionary(w => w.Week, w => (Temp: w.Sum / w.N, Minutes: w.N));
        return CoolingOf(part, name, origin, band, load, rest);
    }

    /// <summary>The width (W) of the power steps a chip's weeks are compared in.</summary>
    public const double PowerBin = 5;

    /// <summary>
    /// The load a chip usually works at: its power on loaded minutes (above twice its idle draw, and 10 W over it),
    /// the middle half of them. Null with too few loaded minutes to say.
    /// </summary>
    public static (double Low, double High)? PowerBand(IReadOnlyList<double> powers)
    {
        if (powers.Count < 100) return null;
        var sorted = powers.Order().ToList();
        double idle = sorted[(int)(0.1 * (sorted.Count - 1))];
        var loaded = sorted.Where(p => p >= Math.Max(idle * 2, idle + 10)).ToList();
        if (loaded.Count < 50) return null;
        double P(double q) => loaded[(int)Math.Round(q * (loaded.Count - 1))];
        return (Math.Floor(P(0.25)), Math.Ceiling(P(0.75)));
    }

    /// <summary>
    /// Each week's temperature at the band, the same mix of power every week: each power step weighted by the minutes all
    /// weeks together spent at it, over the steps the week has (3 minutes or more), so a week spent near the top of the
    /// band doesn't read hotter for that. A week with under half the usual mix isn't counted.
    /// </summary>
    public static List<(int Week, double Temp, int Minutes)> ByWeek(IReadOnlyList<(int Week, int Bin, double Temp, int Minutes)> rows)
    {
        var weight = rows.GroupBy(r => r.Bin).ToDictionary(g => g.Key, g => (double)g.Sum(r => r.Minutes));
        double total = weight.Values.Sum();
        var list = new List<(int, double, int)>();
        foreach (var week in rows.GroupBy(r => r.Week).OrderBy(g => g.Key))
        {
            var bins = week.Where(r => r.Minutes >= 3).ToList();
            double w = bins.Sum(r => weight[r.Bin]);
            if (w < total * 0.5 || bins.Count == 0) continue;
            list.Add((week.Key, bins.Sum(r => weight[r.Bin] * r.Temp) / w, week.Sum(r => r.Minutes)));
        }
        return list;
    }

    /// <param name="load">Each week's temperature and minutes at the power band (week numbers from <paramref name="origin"/>).</param>
    /// <param name="rest">Each week's resting temperature and minutes.</param>
    public static WearRow CoolingOf(string part, string name, DateTime origin, (double Low, double High) band,
        IEnumerable<(int Week, double Temp, int Minutes)> load, IReadOnlyDictionary<int, (double Temp, int Minutes)> rest)
    {
        var weeks = load.Where(l => l.Minutes >= MinWeekMinutes && rest.TryGetValue(l.Week, out var r) && r.Minutes >= MinWeekMinutes)
            .OrderBy(l => l.Week)
            .Select(l => new WearWeek(origin.AddDays(7 * l.Week), l.Temp - rest[l.Week].Temp, l.Temp, rest[l.Week].Temp))
            .ToList();
        var row = Judge(part, name, weeks, CoolingBandMin, (now, then) => Math.Abs(now - then) >= CoolingMoved);
        if (weeks.Count < BaselineWeeks) return row with { RangeLow = band.Low, RangeHigh = band.High };
        var first = weeks.Take(BaselineWeeks).ToList();
        var last = weeks.TakeLast(BaselineWeeks).ToList();
        return row with
        {
            RangeLow = band.Low, RangeHigh = band.High,
            LoadThen = first.Average(w => w.Load!.Value), RestThen = first.Average(w => w.Rest!.Value),
            LoadNow = last.Average(w => w.Load!.Value), RestNow = last.Average(w => w.Rest!.Value),
        };
    }

    // ── Fans ────────────────────────────────────────────────────────────

    /// <summary>Every recorded fan's speed at the same heat, week by week (fans with no steady load recorded say so).</summary>
    public static List<WearRow> Fans(RigsightDb db, DateTime today)
    {
        var origin = Origin(today);
        long from = TimeUtil.ToUnix(origin), to = TimeUtil.ToUnix(today.Date.AddDays(1));
        var curves = db.GetFanCurveDays(from, to).GroupBy(c => c.Fan).ToDictionary(g => g.Key, g => g.ToList());
        var list = new List<WearRow>();
        foreach (var fan in db.GetFans())
            list.Add(FanOf(fan.Id, fan.Name, origin, from, curves.GetValueOrDefault(fan.Id) ?? []));
        return list;
    }

    public static WearRow FanOf(long id, string name, DateTime origin, long from, IReadOnlyList<FanCurveDay> days)
    {
        string part = $"fan:{id}";
        // Each week's minutes and speed at each temperature (every app together: the fan doesn't know what's running).
        var byWeek = days.GroupBy(d => (int)((d.Day - from) / 604800)).OrderBy(g => g.Key)
            .Select(g => (Week: g.Key, Temps: g.GroupBy(d => d.Temp).ToDictionary(t => t.Key, t => (N: t.Sum(x => x.N), Rpm: t.Sum(x => x.RpmSum) / Math.Max(1, t.Sum(x => x.N))))))
            .Where(w => w.Temps.Values.Sum(t => t.N) >= FanWeekMinutes).ToList();
        if (byWeek.Count < BaselineWeeks) return new WearRow { Part = part, Name = name, State = WearState.TooSoon };
        // The temperatures its first weeks spent time at, weighted by those minutes: the same heat every week after.
        var weights = byWeek.Take(BaselineWeeks).SelectMany(w => w.Temps).GroupBy(t => t.Key)
            .Select(g => (Temp: g.Key, N: g.Sum(t => t.Value.N))).Where(t => t.N >= 10).ToDictionary(t => t.Temp, t => t.N);
        if (weights.Count == 0) return new WearRow { Part = part, Name = name, State = WearState.TooSoon };
        double total = weights.Values.Sum();
        var weeks = new List<WearWeek>();
        foreach (var w in byWeek)
        {
            var shared = weights.Where(t => w.Temps.TryGetValue(t.Key, out var x) && x.N >= 3).ToList();
            if (shared.Sum(t => t.Value) < total * 0.5) continue;
            double rpm = shared.Sum(t => t.Value * w.Temps[t.Key].Rpm) / shared.Sum(t => t.Value);
            weeks.Add(new WearWeek(origin.AddDays(7 * w.Week), rpm));
        }
        var row = Judge(part, name, weeks, 0, (now, then) => then > 0 && Math.Abs(now - then) >= FanMovedRpm && Math.Abs(now - then) / then >= FanMovedShare);
        return row with { RangeLow = weights.Keys.Min(), RangeHigh = weights.Keys.Max() };
    }

    // ── Drives ──────────────────────────────────────────────────────────

    /// <summary>A drive's life left week by week (its last reading each week), its pace of wear, and its bad sectors then and now.</summary>
    public static WearRow Drive(RigsightDb db, string key, string name, DateTime today)
    {
        var origin = Origin(today);
        return DriveOf(key, name, origin, db.GetDriveHealthDays(TimeUtil.ToUnix(origin), key));
    }

    public static WearRow DriveOf(string key, string name, DateTime origin, IReadOnlyList<DriveHealthDay> days)
    {
        string part = $"drive:{key}";
        if (days.Count == 0) return new WearRow { Part = part, Name = name, State = WearState.NoReading };
        long from = TimeUtil.ToUnix(origin);
        static long Sectors(DriveHealthDay d) => (d.Reallocated ?? 0) + (d.Pending ?? 0) + (d.Uncorrectable ?? 0);
        bool hasSectors = days.Any(d => d.Reallocated is not null || d.Pending is not null || d.Uncorrectable is not null);
        var life = days.Where(d => d.Life is not null).ToList();
        var weeks = life.GroupBy(d => (int)((d.Day - from) / 604800)).OrderBy(g => g.Key)
            .Select(g => new WearWeek(origin.AddDays(7 * g.Key), g.Last().Life!.Value)).ToList();
        double? perYear = null;
        if (life.Count >= 2 && (life[^1].Day - life[0].Day) / 86400.0 is var span && span >= DriveMinDays)
            perYear = Math.Max(0, (life[0].Life!.Value - life[^1].Life!.Value) / span * 365);
        long? sectorsNow = hasSectors ? Sectors(days[^1]) : null, sectorsThen = hasSectors ? Sectors(days[0]) : null;
        double spanDays = (days[^1].Day - days[0].Day) / 86400.0;
        var state = sectorsNow > sectorsThen ? WearState.Moved : spanDays < DriveMinDays ? WearState.TooSoon : WearState.Steady;
        var temps = days.Where(d => d.TempAvg is not null).GroupBy(d => (int)((d.Day - from) / 604800)).OrderBy(g => g.Key)
            .Select(g => new WearWeek(origin.AddDays(7 * g.Key), g.Average(d => d.TempAvg!.Value))).TakeLast(ChartWeeks).ToList();
        return new WearRow
        {
            Part = part, Name = name, State = state, Weeks = weeks.TakeLast(ChartWeeks).ToList(), TempWeeks = temps,
            Then = life.Count > 0 ? life[0].Life : null, Now = life.Count > 0 ? life[^1].Life : null,
            ThenDate = TimeUtil.FromUnix(days[0].Day), LifePerYear = perYear, SectorsNow = sectorsNow, SectorsThen = sectorsThen,
        };
    }

    // ── Shared ──────────────────────────────────────────────────────────

    /// <summary>
    /// Where a weekly figure stands: its usual band from the first two weeks (their average, at least
    /// <paramref name="minHalfBand"/> either side, wider when they differed), and moved when the last two weeks are both
    /// outside it on the same side and <paramref name="bigEnough"/> says the move is worth a line.
    /// </summary>
    public static WearRow Judge(string part, string name, IReadOnlyList<WearWeek> weeks, double minHalfBand, Func<double, double, bool> bigEnough)
    {
        if (weeks.Count < BaselineWeeks) return new WearRow { Part = part, Name = name, State = WearState.TooSoon, Weeks = weeks, History = weeks };
        var first = weeks.Take(BaselineWeeks).ToList();
        double then = first.Average(w => w.Value);
        double half = Math.Max(minHalfBand, Math.Abs(first[0].Value - first[^1].Value));
        double low = then - half, high = then + half;
        var last = weeks.TakeLast(BaselineWeeks).ToList();
        double now = last.Average(w => w.Value);
        int side(double v) => v > high ? 1 : v < low ? -1 : 0;
        int outside = 0;
        for (int i = weeks.Count - 1; i >= BaselineWeeks; i--)
        {
            if (side(weeks[i].Value) != 0 && side(weeks[i].Value) == side(weeks[^1].Value)) outside++;
            else break;
        }
        bool moved = weeks.Count >= MinWeeks && last.All(w => side(w.Value) != 0 && side(w.Value) == side(last[0].Value)) && bigEnough(now, then);
        var state = weeks.Count < MinWeeks ? WearState.TooSoon : moved ? WearState.Moved : WearState.Steady;
        var mid = first[0].Start;
        return new WearRow
        {
            Part = part, Name = name, State = state, Weeks = weeks.TakeLast(ChartWeeks).ToList(), History = weeks,
            Then = then, Now = now, BandLow = low, BandHigh = high, ThenDate = mid, WeeksOutside = outside,
        };
    }

    /// <summary>
    /// PC health's line while a chip runs hotter at the same load than in its first weeks (the larger, when both do):
    /// "CPU runs 4° hotter at the same load than in late September". Null while neither does. It takes the place of
    /// "Temperatures as usual" (mock-up 2026-11-14), and opens Temperatures.
    /// </summary>
    public static Insight? HealthLine(WearRow cpu, WearRow gpu)
    {
        var hotter = new[] { cpu, gpu }.Where(r => r is { State: WearState.Moved, Now: double n, Then: double t, ThenDate: not null } && n > t)
            .MaxBy(r => r.Now!.Value - r.Then!.Value);
        return hotter is null ? null : new Insight(IconTemp,
            Lang.T($"{hotter.Name} runs {Units.TempGap(hotter.Now!.Value - hotter.Then!.Value)} hotter at the same load than in {WhenSaid(hotter.ThenDate!.Value)}"),
            InsightTone.Warn, HealthKey);
    }

    /// <summary>The PC health line's key (InsightLinks opens Temperatures from it).</summary>
    public const string HealthKey = "wear-cooling";
    private const string IconTemp = "";

    private static readonly Lock CacheLock = new();

    /// <summary><see cref="HealthLine(WearRow, WearRow)"/> from the history (the chips' weeks as <see cref="Cooling"/> keeps them).</summary>
    public static Insight? HealthLine(RigsightDb db, DateTime today) => HealthLine(Cooling(db, false, today), Cooling(db, true, today));

    /// <summary>The key of a Month or Year report's line about a drive's life or bad sectors (it opens Storage).</summary>
    public const string DriveKey = "wear-drive";
    private const string IconDrive = "";

    /// <summary>
    /// A Month or Year report's lines about wear (mock-up 2026-11-14; a day or a week is too short to say): a chip that
    /// sits further above rest under the same load than in its first weeks, as of the period's end; a drive whose life
    /// left went down by a point or more within the period, or whose bad sectors went up. Nothing when nothing moved.
    /// </summary>
    public static List<Insight> ReportLines(RigsightDb db, DateTime from, DateTime to, DateTime today)
    {
        var list = new List<Insight>();
        var end = to.Date.AddDays(-1) < today.Date ? to.Date.AddDays(-1) : today.Date;
        foreach (var gpu in new[] { false, true })
            if (Cooling(db, gpu, end) is { State: WearState.Moved, Now: double now, Then: double then, ThenDate: DateTime since } row)
                list.Add(new Insight(IconTemp, now > then
                    ? Lang.T($"Your {row.Name} sat {Units.TempGap(now)} above rest under the same load, up from {Units.TempGap(then)} in {WhenSaid(since)}.")
                    : Lang.T($"Your {row.Name} sat {Units.TempGap(now)} above rest under the same load, down from {Units.TempGap(then)} in {WhenSaid(since)}."),
                    now > then ? InsightTone.Warn : InsightTone.Good, HealthKey, now > then ? 62 : 38));
        long f = TimeUtil.ToUnix(from), t = TimeUtil.ToUnix(to);
        foreach (var drive in db.GetDriveHealthDays(f).Where(d => d.Day < t).GroupBy(d => d.Drive))
        {
            var days = drive.OrderBy(d => d.Day).ToList();
            string name = days[^1].Name;
            static long Sectors(DriveHealthDay d) => (d.Reallocated ?? 0) + (d.Pending ?? 0) + (d.Uncorrectable ?? 0);
            if (days.Count >= 2 && Sectors(days[^1]) > Sectors(days[0]))
                list.Add(new Insight(IconDrive, Sectors(days[^1]) == 1
                    ? Lang.T($"Your {name} reports 1 bad sector, up from none.")
                    : Lang.T($"Your {name} reports {Sectors(days[^1]):N0} bad sectors, up from {Sectors(days[0]):N0}."), InsightTone.Warn, DriveKey, 64));
            var life = days.Where(d => d.Life is not null).ToList();
            if (life.Count >= 2 && life[0].Life!.Value - life[^1].Life!.Value >= 1)
                list.Add(new Insight(IconDrive, Lang.T($"Your {name} went from {life[0].Life:0}% to {life[^1].Life:0}% life left."), InsightTone.Neutral, DriveKey, 36));
        }
        return list;
    }

    /// <summary>"late September", "mid October", "early November": when the first weeks were, as people say it.</summary>
    public static string WhenSaid(DateTime day)
    {
        string month = day.ToString("MMMM");
        return day.Day <= 10 ? Lang.T($"early {month}") : day.Day <= 20 ? Lang.T($"mid {month}") : Lang.T($"late {month}");
    }
}
