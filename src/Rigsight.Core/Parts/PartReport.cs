using System.Globalization;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;

namespace Rigsight.Core.Parts;

/// <summary>Which part a page is about.</summary>
public enum Part { Cpu, Gpu }

/// <summary>What the part's chart shows.</summary>
public enum PartMetric { Temperature, Load, Power, Clock }

/// <summary>How the part's chart cuts a period: by hour (a day), by day (a week, a month, a custom range), by week (a year).</summary>
public enum PartStep { Hour, Day, Week }

/// <summary>The minutes with a game in front, or the rest, of one stretch: how many, and each metric's sums.</summary>
public sealed record PartSide(int Minutes, PartSum Temp, PartSum Load, PartSum Power, PartSum Clock)
{
    public PartSum Of(PartMetric m) => m switch
    {
        PartMetric.Temperature => Temp, PartMetric.Load => Load, PartMetric.Power => Power, _ => Clock,
    };

    public static PartSide operator +(PartSide a, PartSide b) =>
        new(a.Minutes + b.Minutes, a.Temp + b.Temp, a.Load + b.Load, a.Power + b.Power, a.Clock + b.Clock);
}

/// <summary>
/// One stretch of the chart: an hour, a day or a week, its minutes with a game in front and the others (null: none of
/// them), and each game's minutes in front, the most first. Not recorded when it has neither.
/// </summary>
/// <param name="Ahead">Still to come (later today): empty, never "Not recorded".</param>
public sealed record PartSlot(DateTime From, DateTime To, PartSide? Games, PartSide? Rest, IReadOnlyList<(long App, int Minutes)> GameTimes, bool Ahead = false)
{
    public bool Recorded => Games is not null || Rest is not null;

    /// <summary>Before the last new part came (1.23.0): an older part's stretch, drawn faint and left out of the usual.</summary>
    public bool Old { get; init; }

    /// <summary>The older part's full name on an <see cref="Old"/> stretch (null: not known).</summary>
    public string? OldName { get; init; }

    /// <summary>The whole stretch, both sides together (an hour's one bar).</summary>
    public PartSide? All => Games is { } g ? Rest is { } r ? g + r : g : Rest;
}

/// <summary>An app in front over the period: its time, and the part's average load, temperature and power while it was.</summary>
public sealed record PartApp(long App, int Minutes, double? Load, double? Temp, double? Power, bool IsGame);

/// <summary>
/// The part's usual temperature (°C): with a game in front (its games' steady-load days, as Normal for this PC reads
/// them) and at rest (as <see cref="RestTemps"/> has it), each the middle of the 30 days before; null where too soon.
/// </summary>
public sealed record PartUsual(double? GameLow, double? GameHigh, double? RestLow, double? RestHigh)
{
    public bool Any => GameLow is not null || RestLow is not null;

    /// <summary>
    /// "Usual 66° to 72° with a game in front, 41° to 45° at rest." (null when neither is known). After a new part, the
    /// part it's of: "Usual 61° to 66° with a game in front, 41° to 45° at rest, on the RTX 5080."
    /// </summary>
    public string? Words(string? part = null)
    {
        if (part is { Length: > 0 } && Words() is { } plain) return Lang.T($"{plain.TrimEnd('.')}, on the {part}.");
        string? game = GameLow is double gl && GameHigh is double gh ? Lang.T($"{Units.TempShort(gl)} to {Units.TempShort(gh)}") : null;
        string? rest = RestLow is double rl && RestHigh is double rh ? Lang.T($"{Units.TempShort(rl)} to {Units.TempShort(rh)}") : null;
        return (game, rest) switch
        {
            (not null, not null) => Lang.T($"Usual {game} with a game in front, {rest} at rest."),
            (not null, null) => Lang.T($"Usual {game} with a game in front."),
            (null, not null) => Lang.T($"Usual {rest} at rest."),
            _ => null,
        };
    }
}

/// <summary>
/// A part's history over a period for the GPU and CPU pages (1.23.0, approved mock-up section 10): each hour of a day,
/// each day of a week, month or custom range, each week of a year (from the first week on record), with a game in front
/// kept apart from everything else; the apps that worked it hardest; and its usual temperature. From system_minute, read
/// in a few grouped queries; built once here for the page and its export.
/// </summary>
public sealed class PartReport
{
    public required Part Part { get; init; }
    public required PartStep Step { get; init; }
    public required DateTime From { get; init; }
    public required DateTime To { get; init; }
    public required IReadOnlyList<PartSlot> Slots { get; init; }
    /// <summary>Every app in front for <see cref="MinAppMinutes"/> or more, the hardest working first (average load).</summary>
    public required IReadOnlyList<PartApp> Apps { get; init; }
    public PartUsual Usual { get; init; } = new(null, null, null, null);

    /// <summary>The driver updates and new parts in the period's stretches (1.23.0), each at its stretch.</summary>
    public IReadOnlyList<PartMark> Marks { get; init; } = [];

    /// <summary>The last new part on record (null: none): the usual counts only the days since it.</summary>
    public PartSwap? Swap { get; init; }

    /// <summary>
    /// Where the period's figures and What works it hardest start: the period's start, or the last new part in it (never
    /// joined across a part swap). A period wholly before the last new part counts the part it had.
    /// </summary>
    public DateTime CountedFrom { get; init; }

    /// <summary>The part the usual is said of (its short name), after a new part in the usual's days or the period; null otherwise.</summary>
    public string? UsualPart { get; init; }

    /// <summary>Whether a stretch's figures count in the period's (it's on the part counted, see <see cref="CountedFrom"/>).</summary>
    public bool Counts(PartSlot s) => s.To > CountedFrom;

    /// <summary>An app counts in "What works it hardest" from this many minutes in front: a glimpse says nothing.</summary>
    public const int MinAppMinutes = 10;

    public bool HasAny => Slots.Any(s => s.Recorded);

    /// <summary>Whether the period has any reading of <paramref name="m"/> (a CPU without its power on a standard account has none).</summary>
    public bool Has(PartMetric m) => Slots.Any(s => (s.Games?.Of(m).N ?? 0) + (s.Rest?.Of(m).N ?? 0) > 0);

    /// <summary>A custom range this long or shorter goes by the hour; past <see cref="CustomDaysMax"/> days by the week.</summary>
    public const int CustomDaysMax = 92;

    public static PartStep StepOf(ReportRange unit, DateTime from, DateTime to) => unit switch
    {
        ReportRange.Day => PartStep.Hour,
        ReportRange.Year => PartStep.Week,
        ReportRange.Custom when to - from <= ReportBuilder.DayLikeLimit => PartStep.Hour,
        ReportRange.Custom when (to - from).TotalDays > CustomDaysMax => PartStep.Week,
        _ => PartStep.Day,
    };

    private static long Unix(DateTime local) => TimeUtil.ToUnix(local);

    /// <param name="games">The apps counted as games (the person's own categories too).</param>
    /// <param name="firstMinute">The first minute on record (Unix seconds): a year's weeks start from its week.</param>
    /// <param name="events">The GPU's driver updates and the part's swaps (1.23.0, see <see cref="PartEvents.Of"/>); none when null.</param>
    public static PartReport Build(RigsightDb db, Part part, ReportRange unit, DateTime from, DateTime to, IReadOnlyCollection<long> games,
        long? firstMinute, DateTime now, PartEvents? events = null)
    {
        events ??= PartEvents.None;
        bool gpu = part == Part.Gpu;
        var step = StepOf(unit, from, to);
        bool hourly = step == PartStep.Hour;
        long f = Unix(from), t = Unix(to);

        // A new part: the stretches before the last one are an older part's (faint, not in the usual), and the figures
        // count from the last one in the period on. The stretch it came in counts only its minutes since (never an
        // average across the swap), so the minutes are read in two parts, either side of it.
        var swaps = events.Swaps.Where(s => s.At <= now).OrderBy(s => s.At).ToList();
        var swap = swaps.LastOrDefault();
        var counted = swaps.LastOrDefault(s => s.At < to)?.At is { } c && c > from ? c : from;
        long cut = Unix(counted);
        var buckets = new List<(PartBucket B, bool Late)>();
        var gameMinutes = new List<((long Start, long App, int Minutes) G, bool Late)>();
        if (cut > f)
        {
            buckets.AddRange(db.GetPartBuckets(f, cut, gpu, hourly, games).Select(b => (b, false)));
            gameMinutes.AddRange(db.GetPartGameMinutes(f, cut, hourly, games).Select(g => (g, false)));
        }
        buckets.AddRange(db.GetPartBuckets(cut, t, gpu, hourly, games).Select(b => (b, true)));
        gameMinutes.AddRange(db.GetPartGameMinutes(cut, t, hourly, games).Select(g => (g, true)));
        string? NameAt(DateTime at) => swaps.LastOrDefault(s => s.At <= at)?.NewName ?? swaps.FirstOrDefault(s => s.At > at)?.OldName;

        // The stretches: a day's hours, the period's days, or a year's weeks (from the week of the first minute on record).
        var stretches = new List<(DateTime From, DateTime To)>();
        if (hourly)
            for (var h = ReportBuilder.HourStart(from); h < to; h = h.AddHours(1)) stretches.Add((h, h.AddHours(1)));
        else if (step == PartStep.Day)
            for (var d = from.Date; d < to; d = d.AddDays(1)) stretches.Add((d, d.AddDays(1)));
        else
        {
            var start = from.Date;
            if (firstMinute is long fm)
                while (start.AddDays(7) <= TimeUtil.FromUnix(fm).Date) start = start.AddDays(7);
            // Weeks up to now only: a week still to come isn't "Not recorded" (as Start-up's Year).
            for (; start < to && start <= now; start = start.AddDays(7)) stretches.Add((start, start.AddDays(7) < to ? start.AddDays(7) : to));
        }

        var slots = new List<PartSlot>(stretches.Count);
        foreach (var (a, b) in stretches)
        {
            long sa = Unix(a), sb = Unix(b);
            // The stretch a new part came in has only its minutes since; one wholly before it, only the ones before.
            bool late = sb > cut;
            PartSide? Side(bool game) => buckets.Where(x => x.Late == late && x.B.Game == game && x.B.Start >= sa && x.B.Start < sb)
                .Select(x => new PartSide(x.B.Minutes, x.B.Temp, x.B.Load, x.B.Power, x.B.Clock))
                .Aggregate((PartSide?)null, (acc, s) => acc is null ? s : acc + s);
            var times = gameMinutes.Where(x => x.Late == late && x.G.Start >= sa && x.G.Start < sb).GroupBy(x => x.G.App)
                .Select(g => (g.Key, g.Sum(x => x.G.Minutes))).OrderByDescending(g => g.Item2).ToList();
            bool old = swap is not null && b <= swap.At;
            slots.Add(new PartSlot(a, b, Side(true), Side(false), times, a > now) { Old = old, OldName = old ? NameAt(a) : null });
        }

        var apps = db.GetPartApps(cut, t, gpu, games).Where(a => a.Minutes >= MinAppMinutes && a.Load is not null)
            .OrderByDescending(a => a.Load).Select(a => new PartApp(a.App, a.Minutes, a.Load, a.Temp, a.Power, a.IsGame)).ToList();

        // The usual is of the part in the PC now: only the whole days since it came.
        var heat = SinceSwap(db.GetHeatDays(Unix(now.Date.AddDays(-RestTemps.LookBackDays)), Unix(now.Date.AddDays(1))), swap);
        bool sayPart = swap is not null && (swap.At >= now.Date.AddDays(-RestTemps.LookBackDays) || slots.Any(s => s.Old));
        return new PartReport
        {
            Part = part, Step = step, From = from, To = to, Slots = slots, Apps = apps,
            Usual = UsualOf(heat, part, games, now),
            Marks = PartMarks.Build(db, part, events, slots, games, firstMinute, now),
            Swap = swap, CountedFrom = counted,
            UsualPart = sayPart ? PartMarks.Short(part, swap!.NewName) : null,
        };
    }

    /// <summary>The days a usual is made of after a new part: only the whole days since it came (none of the old part's).</summary>
    internal static IEnumerable<HeatDay> SinceSwap(IEnumerable<HeatDay> days, PartSwap? swap) =>
        swap is null ? days : days.Where(d => d.Day >= Unix(swap.At.Date.AddDays(1)));

    /// <summary>
    /// The usual temperature from heat_day: the games' steady-load days and the rest days of the 30 days before today,
    /// each a fifth off either end (a hot afternoon or a cold morning doesn't set it), from 5 days on.
    /// </summary>
    internal static PartUsual UsualOf(IEnumerable<HeatDay> days, Part part, IReadOnlyCollection<long> games, DateTime now)
    {
        var list = days.ToList();
        var rest = RestTemps.Of(list, now);
        var r = part == Part.Gpu ? rest.Gpu : rest.Cpu;
        long today = Unix(now.Date);
        var gameSet = games as IReadOnlySet<long> ?? games.ToHashSet();
        var played = list.Where(d => d.App != 0 && gameSet.Contains(d.App) && d.Day < today)
            .Select(d => part == Part.Gpu ? (d.GpuSum, d.GpuN) : (d.CpuSum, d.CpuN)).Where(p => p.Item2 > 0).Select(p => p.Item1 / p.Item2).Order().ToList();
        double? low = null, high = null;
        if (played.Count >= RestTemps.MinDays)
        {
            double P(double q) => played[Math.Min(played.Count - 1, (int)Math.Round(q * (played.Count - 1)))];
            (low, high) = (P(0.2), P(0.8));
        }
        return new PartUsual(low, high, r.UsualLow, r.UsualHigh);
    }

    // ── Words ────────────────────────────────────────────────────────────

    /// <summary>A metric's figure as the chart's hover says it: "68°", "97%", "214 W", "2,610 MHz".</summary>
    public static string Figure(PartMetric m, double v) => m switch
    {
        PartMetric.Temperature => Units.TempShort(v),
        PartMetric.Load => $"{Math.Round(v):0}%",
        PartMetric.Power => $"{Math.Round(v):0} W",
        _ => $"{Math.Round(v).ToString("N0", CultureInfo.CurrentCulture)} MHz",
    };

    /// <summary>The chart's title: "Temperature each day".</summary>
    public static string Title(PartMetric m, PartStep step) => (m, step) switch
    {
        (PartMetric.Temperature, PartStep.Hour) => Lang.T("Temperature each hour"),
        (PartMetric.Temperature, PartStep.Week) => Lang.T("Temperature each week"),
        (PartMetric.Temperature, _) => Lang.T("Temperature each day"),
        (PartMetric.Load, PartStep.Hour) => Lang.T("Load each hour"),
        (PartMetric.Load, PartStep.Week) => Lang.T("Load each week"),
        (PartMetric.Load, _) => Lang.T("Load each day"),
        (PartMetric.Power, PartStep.Hour) => Lang.T("Power each hour"),
        (PartMetric.Power, PartStep.Week) => Lang.T("Power each week"),
        (PartMetric.Power, _) => Lang.T("Power each day"),
        (_, PartStep.Hour) => Lang.T("Clock each hour"),
        (_, PartStep.Week) => Lang.T("Clock each week"),
        _ => Lang.T("Clock each day"),
    };

    /// <summary>
    /// A stretch's time split, the hover's grey line: "3h 55m with a game in front, 6h 12m of everything else"; "No game in
    /// front, 6h 12m of everything else"; "3h 55m with a game in front".
    /// </summary>
    public static string TimeSplit(PartSlot s)
    {
        string? game = s.Games is { } g ? Units.Duration(g.Minutes * 60.0) : null;
        string? rest = s.Rest is { } r ? Units.Duration(r.Minutes * 60.0) : null;
        return (game, rest) switch
        {
            (not null, not null) => Lang.T($"{game} with a game in front, {rest} of everything else"),
            (not null, null) => Lang.T($"{game} with a game in front"),
            (null, not null) => Lang.T($"No game in front, {rest} of everything else"),
            _ => Wording.NotRecorded,
        };
    }

    /// <summary>A side's figures in the hover: "68° average" and "highest 76°"; null without a reading.</summary>
    public static (string Average, string Highest)? SideWords(PartSide? side, PartMetric m)
    {
        var sum = side?.Of(m);
        if (sum is not { Average: double avg } s) return null;
        string high = s.Max is double h ? Lang.T($"highest {Figure(m, h)}") : "";
        return (Lang.T($"{Figure(m, avg)} average"), high);
    }

    /// <summary>The hover's title: "Thu 26 Nov, 9 PM to 10 PM", "Thursday 26 Nov", "Week of 1 Jun".</summary>
    public static string SlotTitle(PartSlot s, PartStep step) => step switch
    {
        PartStep.Hour => Lang.T($"{s.From.ToString("ddd d MMM", CultureInfo.CurrentCulture)}, {TimeText.At(s.From, "h tt")} to {TimeText.At(s.To, "h tt")}"),
        PartStep.Week => Startup.StartupWords.WeekOf(s.From),
        _ => s.From.ToString("dddd d MMM", CultureInfo.CurrentCulture),
    };
}
