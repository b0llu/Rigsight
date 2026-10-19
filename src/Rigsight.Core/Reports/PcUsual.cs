using Rigsight.Core.Data;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Reports;

/// <summary>
/// One reading of "Against your usual" ("GPU in Crimson Desert", "70°", "67–69°"), with what the difference means.
/// <see cref="Tone"/> Same is said in grey; only what moved is coloured.
/// </summary>
public sealed record UsualRow(string Key, string Label, string Detail, string Now, string Usual, string UsualDetail, EffectTone Tone, string Means, string? MeansDetail = null);

/// <summary>
/// A part's temperature in one game at steady load, now and usually, at the same GPU power, with the GPU at rest on both
/// sides (what the room does). <see cref="Tone"/> is Worse or Better only past the drift insight's bar.
/// </summary>
public sealed record UsualHeat(string App, string Part, double Now, double Usual, double Low, double High, int NowMinutes, int UsualMinutes,
    int UsualDays, double? RestNow, double? RestUsual, double? PowerNow, double? PowerUsual, EffectTone Tone)
{
    /// <summary>The difference as shown, each side rounded ("8°").</summary>
    public string Diff => Units.ShownDiff(Now, Usual);
    public bool Hotter => Now > Usual;

    /// <summary>The last day the game was played in the period, and the part's temperature that day.</summary>
    public DateTime? LastDay { get; init; }
    public double? LastTemp { get; init; }
    /// <summary>The difference on that day, as shown.</summary>
    public string LastDiff => LastTemp is double t ? Units.ShownDiff(t, Usual) : Diff;
}

/// <summary>What a problem counts as (see <see cref="PcUsual.ShutOffOf"/>).</summary>
public enum ShutOff { None, Pc, PowerLikely, PowerPossible }

/// <summary>A motherboard fan's speed now and its usual range, since its speed was last set (a fan's speed isn't compared across a setting).</summary>
public sealed record UsualFan(string Name, double Now, double Low, double High, DateTime? SetOn);

/// <summary>
/// The problems that are the PC's own (blue screens, graphics driver resets, sudden shut-offs that weren't the power) now
/// and usually, counted against the hours of use; and the shut-offs that look like the power, apart (see <see cref="ShutOff"/>).
/// </summary>
public sealed record UsualProblems(IReadOnlyList<CrashEvent> Now, int UsualCount, double NowHours, double UsualHours, double UsualWeeks, EffectTone Tone,
    int PowerNow, int PowerUsual)
{
    /// <summary>Of <see cref="PowerNow"/>, the ones that were likely a power cut (the rest were possibly the power).</summary>
    public int PowerLikelyNow { get; init; }

    /// <summary>
    /// The shut-offs that look like the power against their own usual: never judged in an ordinary week (the mains isn't
    /// the PC), Worse only when they jump far past it, which is also how a failing power supply shows.
    /// </summary>
    public EffectTone PowerTone { get; init; }

    /// <summary>"2 likely power cuts", "3 shut-offs, likely or possibly the power".</summary>
    public string PowerWords => Wording.PowerOffs(PowerLikelyNow, PowerNow);

    public string PowerUsualWords => PowerUsual == 0 ? Lang.T("usually none") : Lang.T($"usually {PcUsual.About(PowerUsual, UsualWeeks, PeriodWeeks)}");

    /// <summary>
    /// The kinds that came far more often than usual (each kind against its own usual: three graphics driver resets on a
    /// PC that has a sudden shut-off a week are news, set against the shut-offs they wouldn't be).
    /// </summary>
    public IReadOnlyList<CrashKind> Rising { get; init; } = [];
    /// <summary>How many of the rising kinds the usual weeks had.</summary>
    public int UsualOfRising { get; init; }

    /// <summary>What there was ("3 graphics driver resets and 1 blue screen"), only the rising kinds when worse; "No PC problems" when there were none.</summary>
    public string Words => Now.Count == 0 ? Lang.T("No PC problems")
        : Now.Where(e => Tone != EffectTone.Worse || Rising.Count == 0 || Rising.Contains(e.Kind)).GroupBy(e => e.Kind).OrderByDescending(g => g.Count())
            .Select(g => Wording.Count(g.Key, g.Count())).DefaultIfEmpty("").Aggregate((a, b) => Lang.T($"{a} and {b}"));

    /// <summary>The length of the period compared, in weeks (a month's usual is said as a month's).</summary>
    public double PeriodWeeks { get; init; } = 1;

    /// <summary>"usually none", "usually about 2 a week", "usually about 9 a month".</summary>
    public string UsualWords => (Tone == EffectTone.Worse && Rising.Count > 0 ? UsualOfRising : UsualCount) is var n && n == 0 ? Lang.T("usually none") : Lang.T($"usually {PcUsual.About(n, UsualWeeks, PeriodWeeks)}");
}

/// <summary>
/// What is normal for this PC, and whether a period was different from it: the same game's temperatures at steady load
/// against the settled weeks just before (not months ago: that needs months of history, and a PC just set up has none),
/// the temperatures at rest (the room), each motherboard fan against its speed since it was last set, and the PC's own
/// problems against the hours it was used. Like for like or nothing: a comparison with too little on a side isn't made.
/// Every page that says how the PC is doing against its usual reads this, so Home, Reports and the recap agree.
/// </summary>
public sealed class PcUsual
{
    /// <summary>The period compared ([From, To), up to now) and the usual it's compared with ([UsualFrom, From)).</summary>
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public DateTime UsualFrom { get; init; }

    /// <summary>The headline temperature: the GPU in the game played most on both sides, or its CPU when only that moved.</summary>
    public UsualHeat? Heat { get; init; }
    /// <summary>Every part compared in that game (GPU, then CPU).</summary>
    public List<UsualHeat> Parts { get; init; } = [];
    public LoadTemps? RestNow { get; init; }
    public LoadTemps? RestUsual { get; init; }
    public (double Low, double High)? RestGpuRange { get; init; }
    public (double Low, double High)? RestCpuRange { get; init; }
    public List<UsualFan> Fans { get; init; } = [];
    public UsualProblems? Problems { get; init; }

    /// <summary>Each app's crashes in the usual weeks, by exe (for "crashed 4 times this week; usually never").</summary>
    public IReadOnlyDictionary<string, int> AppCrashesUsual { get; init; } = new Dictionary<string, int>();

    /// <summary>
    /// When each motherboard fan's speed was last set while the PC was off (by name and hardware, as the fan checks
    /// have them): the one answer to "was this fan set in the BIOS?", for the Timeline, the fans' usual and the fan
    /// warnings alike (see <see cref="Stability.FanSettings"/>).
    /// </summary>
    public IReadOnlyDictionary<(string Name, string Hardware), DateTime> FansSetOn { get; init; } = new Dictionary<(string Name, string Hardware), DateTime>();

    /// <summary>A motherboard fan's speed was set (in the BIOS) at or after <paramref name="since"/>.</summary>
    public bool FanSetSince(string name, string hardware, DateTime since) => FansSetOn.TryGetValue((name, hardware), out var on) && on >= since;
    public double UsualDays => (From - UsualSince).TotalDays;

    /// <summary>Hours of use in the usual weeks, and the first day they hold any (history may start after <see cref="UsualFrom"/>).</summary>
    public double UsualHours { get; init; }
    public DateTime UsualSince { get; init; }

    /// <summary>The temperatures were compared (a game, or at least the PC at rest).</summary>
    public bool HasHeat => Heat is not null || (RestNow is { Minutes: >= MinRestMinutes } && RestUsual is { Minutes: >= MinRestMinutes });

    // The drift insight's bar: an hour of the game now, two over three days usually (one evening is a mood, not a usual),
    // an hour at rest each side, 5° apart and 3° beyond what the room did, at a GPU power within 15%.
    internal const int MinNowMinutes = 60, MinUsualMinutes = 120, MinUsualDays = 3, MinRestMinutes = 60;
    internal const double MinDiff = 5, MinBeyondRoom = 3, PowerShare = 0.15;

    /// <summary>Once said, a game stays hotter (or cooler) than usual while its last day of play is at least this far off.</summary>
    internal const double KeepDiff = 3;

    /// <summary>A day counts toward a usual range with at least this many steady minutes (a few minutes are noise).</summary>
    private const int RangeDayMinutes = 20, RestRangeDayMinutes = 30;

    /// <summary>Shut-offs that look like the power, at the least, before a jump in them is said.</summary>
    internal const int PowerJump = 3;

    /// <summary>Hours of use on each side before problems are counted against each other.</summary>
    internal const double MinProblemHours = 10;

    /// <summary>Days a fan needs at its speed before that speed is its usual.</summary>
    internal const int FanUsualDays = 3;

    /// <summary>How far back "usual" reaches: two months, three for a month's report (a month against one week isn't fair).</summary>
    public static int UsualDaysFor(ReportRange range) => range == ReportRange.Month ? 90 : 60;

    /// <summary>
    /// The usual for the period [<paramref name="from"/>, <paramref name="to"/>), read from the database: the daily heat
    /// totals, the days' use, the crashes (with what each most likely was) and the fans.
    /// </summary>
    public static PcUsual Read(RigsightDb db, RigsightSettings s, DateTime from, DateTime to, int usualDays, DateTime now, Func<long, string>? nameOf = null)
    {
        var usualFrom = from.Date.AddDays(-usualDays);
        long f = TimeUtil.ToUnix(usualFrom), t = TimeUtil.ToUnix(to);
        var heat = db.GetHeatDays(f, t);
        var use = db.GetSystemDays(f, t) ?? [];
        var crashes = db.GetCrashes(f, t).Where(c => !s.IsCrashMuted(c.AppExe)).ToList();
        // Only the PC's problems need a finding worked out (a shut-off may be the power, a driver reset the driver being
        // installed): an app's crash is counted as it is. One from before today has its cause settled: it's worked out
        // once and kept (Home asks every minute), by its id and time (ids start again after the history is cleared).
        var problems = crashes.Where(IsPcProblem).ToList();
        var found = new Dictionary<long, CrashFinding>();
        var cache = Cuts.GetOrAdd(db.Source, _ => new System.Collections.Concurrent.ConcurrentDictionary<(long Id, DateTime Time), (string? Cause, bool Outside)>());
        var unknown = problems.Where(e => !cache.ContainsKey((e.Id, e.Time))).ToList();
        if (unknown.Count > 0)
            foreach (var (id, finding) in CrashCases.Of(db, unknown, s))
            {
                found[id] = finding;
                if (unknown.First(e => e.Id == id) is { } e && e.Time < now.Date) cache[(id, e.Time)] = (finding.Cause, finding.Outside);
            }
        foreach (var e in problems)
            if (!found.ContainsKey(e.Id) && cache.TryGetValue((e.Id, e.Time), out var known))
                found[e.Id] = new CrashFinding(Confidence.Likely, "", "", "", "") { Cause = known.Cause, Outside = known.Outside };
        if (nameOf is null)
        {
            var names = db.LoadApps().ToDictionary(a => a.Id, a => s.AppNames.TryGetValue(a.Exe, out var alias) ? alias : Apps.AppCatalog.KnownName(a.Exe) ?? a.Name);
            nameOf = id => names.GetValueOrDefault(id) ?? Lang.T("a game");
        }
        var fans = db.GetFans().Where(x => !x.Sensor.StartsWith("/gpu", StringComparison.Ordinal)).ToList();
        var fanDays = fans.Count > 0 ? db.GetFanDays(f, t) : [];
        // Every fan setting change up to the period's end: the fan warnings look further back than the usual (months).
        var fanChanges = db.GetChanges(0, t).Where(c => c.Kind == ChangeKind.Fans).ToList();
        return Of(from, to, usualFrom, now, heat, use, crashes, found, fans, fanDays, fanChanges, nameOf);
    }

    /// <summary>Each past problem's cause, by database and crash (see <see cref="ShutOffOf"/>).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<(long Id, DateTime Time), (string? Cause, bool Outside)>> Cuts = new();

    /// <summary>The PC itself went down or its graphics driver reset: not one app, and not the PC shutting down.</summary>
    public static bool IsPcProblem(CrashEvent e) =>
        e.Kind is CrashKind.SystemCrash or CrashKind.GpuDriverReset || e is { Kind: CrashKind.UnexpectedShutdown, Moment: PowerMoment.Running };

    /// <summary>The causes a sudden shut-off is put down to that point at the power, sure or not (see CrashFinding).</summary>
    private static readonly HashSet<string> PowerCauses = ["A POWER CUT", "A POWER CUT OR A RESET", "A POWER DIP OR A RESET", "A POWER CUT OR A FREEZE", "MAINS OR POWER SUPPLY"];

    /// <summary>
    /// What a problem counts as, the one rule for every page: the PC's own (a blue screen, a graphics driver reset, a
    /// shut-off after the GPU hung or with no sign of the power), a shut-off that looks like the power (likely or possibly
    /// a power cut, a dip, the mains or the power supply: the mains isn't the PC, though a failing power supply looks the
    /// same), or neither (an app, Windows already shutting down, a driver being installed).
    /// </summary>
    public static ShutOff ShutOffOf(CrashEvent e, CrashFinding? f) =>
        !IsPcProblem(e) ? ShutOff.None
        : e.Kind != CrashKind.UnexpectedShutdown ? f is { Outside: true } ? ShutOff.None : ShutOff.Pc
        : f?.Cause == "A POWER CUT" ? ShutOff.PowerLikely
        : f?.Cause is { } cause && PowerCauses.Contains(cause) ? ShutOff.PowerPossible
        : f is { Outside: true } ? ShutOff.None
        : ShutOff.Pc;

    /// <summary>One of the PC's own problems (see <see cref="ShutOffOf"/>).</summary>
    public static bool CountsAsPcProblem(CrashEvent e, CrashFinding? f) => ShutOffOf(e, f) == ShutOff.Pc;

    /// <summary>A shut-off that looks like the power (see <see cref="ShutOffOf"/>).</summary>
    public static bool LooksLikePower(CrashEvent e, CrashFinding? f) => ShutOffOf(e, f) is ShutOff.PowerLikely or ShutOff.PowerPossible;

    public static PcUsual Of(DateTime from, DateTime to, DateTime usualFrom, DateTime now, IReadOnlyList<HeatDay> heat, IReadOnlyList<SystemDay> use,
        IReadOnlyList<CrashEvent> crashes, IReadOnlyDictionary<long, CrashFinding> found, IReadOnlyList<FanRow> fans, IReadOnlyList<FanDay> fanDays,
        IReadOnlyList<SystemChange> fanChanges, Func<long, string> nameOf)
    {
        if (to > now) to = now;
        long f = TimeUtil.ToUnix(from), t = TimeUtil.ToUnix(to), u = TimeUtil.ToUnix(usualFrom);
        // A day row is the day it starts: today's row counts toward now while it's still going.
        bool InNow(long day) => day >= TimeUtil.ToUnix(from.Date) && day < t;
        bool InUsual(long day) => day >= u && day < TimeUtil.ToUnix(from.Date);

        // ── Heat: the game played most on both sides, at the same power ──
        var nowRows = heat.Where(h => InNow(h.Day)).ToList();
        var usualRows = heat.Where(h => InUsual(h.Day)).ToList();
        var (nowSteady, restNow) = Steady(nowRows, nameOf);
        var (usualSteady, restUsual) = Steady(usualRows, nameOf);
        bool restOk = restNow is { Minutes: >= MinRestMinutes } && restUsual is { Minutes: >= MinRestMinutes };
        var parts = new List<UsualHeat>();
        HeatDay? lastPlay = null;
        if (restOk)
        {
            var game = nowSteady.Select(n => (Now: n, Was: usualSteady.FirstOrDefault(w => w.AppId == n.AppId)))
                .Where(p => p.Was is not null && p.Now.Minutes >= MinNowMinutes && p.Was.Minutes >= MinUsualMinutes && p.Was.Days >= MinUsualDays && SamePower(p.Now.GpuPower, p.Was.GpuPower))
                .OrderByDescending(p => Math.Min(p.Now.Minutes, p.Was!.Minutes)).FirstOrDefault();
            if (game.Now is { } n && game.Was is { } w)
            {
                var days = usualRows.Where(h => h.App == n.AppId && h.N >= RangeDayMinutes).ToList();
                // The last day it was played (with enough steady play to read): a game hot on Monday and fine again on
                // Thursday, after a clean, is fine now; the period as a whole would keep saying hotter for a week.
                lastPlay = nowRows.Where(h => h.App == n.AppId && h.N >= RangeDayMinutes).MaxBy(h => h.Day);
                parts.Add(Part("GPU", n, w, n.Gpu, w.Gpu, Range(days, Gpu) ?? (w.Gpu, w.Gpu), restNow!.Gpu, restUsual!.Gpu));
                if (n.Cpu is double nc && w.Cpu is double wc)
                    parts.Add(Part("CPU", n, w, nc, wc, Range(days, Cpu) ?? (wc, wc), restNow.Cpu, restUsual.Cpu));
            }
        }
        // The GPU unless only the CPU moved.
        var headline = parts.FirstOrDefault(p => p.Tone != EffectTone.Same) ?? parts.FirstOrDefault();

        // ── Fans: each motherboard fan against its speed since it was last set ──
        var usualFans = new List<UsualFan>();
        foreach (var fan in fans)
        {
            var mine = fanDays.Where(d => d.Fan == fan.Id && d.Rpm is > 0).ToList();
            var nowDays = mine.Where(d => InNow(d.Day)).ToList();
            if (nowDays.Count == 0) continue;
            // Since its speed was last set, before this period: a fan set slower in the BIOS has a new usual from then.
            var set = fanChanges.Where(c => c.Time < to && c.Subject.Contains($"|{fan.Id}|", StringComparison.Ordinal)).MaxBy(c => c.Time);
            long since = set is null ? u : Math.Max(u, TimeUtil.ToUnix(set.Time.Date.AddDays(1)));
            var before = mine.Where(d => d.Day >= since && d.Day < TimeUtil.ToUnix(from.Date)).ToList();
            if (before.Count < FanUsualDays) continue;
            double nowRpm = nowDays.Sum(d => d.RpmSum) / nowDays.Sum(d => d.RpmN);
            usualFans.Add(new UsualFan(fan.Name, nowRpm, before.Min(d => d.Rpm!.Value), before.Max(d => d.Rpm!.Value), set?.Time));
        }

        // ── Problems: the PC's own, against the hours of use; likely power cuts apart ──
        double nowHours = use.Where(d => InNow(d.Day)).Sum(d => d.ActiveSec) / 3600, usualHours = use.Where(d => InUsual(d.Day)).Sum(d => d.ActiveSec) / 3600;
        // Crashes reach back further than the history of use (Windows keeps its logs): the usual starts with the use.
        var firstUse = use.Where(d => d.ActiveSec > 0).Select(d => d.Day).DefaultIfEmpty(long.MaxValue).Min();
        if (firstUse > u) u = Math.Min(firstUse, TimeUtil.ToUnix(from.Date));
        var pcNow = new List<CrashEvent>();
        int pcUsual = 0, powerNow = 0, powerLikelyNow = 0, powerUsual = 0;
        var usualByKind = new Dictionary<CrashKind, int>();
        var appUsual = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // By day, as the hours of use are: a range starting in the afternoon has that whole day's crashes as now.
        long usualTo = TimeUtil.ToUnix(from.Date);
        foreach (var e in crashes)
        {
            long ts = TimeUtil.ToUnix(e.Time);
            bool isNow = ts >= usualTo && ts < t, isUsual = ts >= u && ts < usualTo;
            if (!isNow && !isUsual) continue;
            if (!IsPcProblem(e))
            {
                if (isUsual && e.Kind is CrashKind.AppCrash or CrashKind.AppHang && e.AppExe.Length > 0) appUsual[e.AppExe] = appUsual.GetValueOrDefault(e.AppExe) + 1;
                continue;
            }
            var what = ShutOffOf(e, found.GetValueOrDefault(e.Id));
            if (what == ShutOff.None) continue;
            if (what != ShutOff.Pc)
            {
                if (isNow) { powerNow++; if (what == ShutOff.PowerLikely) powerLikelyNow++; } else powerUsual++;
                continue;
            }
            if (isNow) pcNow.Add(e);
            else { pcUsual++; usualByKind[e.Kind] = usualByKind.GetValueOrDefault(e.Kind) + 1; }
        }
        UsualProblems? problems = null;
        double usualWeeks = (from.Date - TimeUtil.FromUnix(u)).TotalDays / 7;
        if (usualHours >= MinProblemHours && nowHours > 0)
        {
            double expected = usualHours > 0 ? pcUsual / usualHours * nowHours : 0;
            // More: a kind came two times at least, and twice (and one) what its usual rate gives the hours used. Fewer:
            // the usual rate would have given three or more in all, and twice what there were, over enough use to tell.
            var rising = pcNow.GroupBy(e => e.Kind).Where(g => g.Count() >= 2 && g.Count() >= 2 * (usualByKind.GetValueOrDefault(g.Key) / usualHours * nowHours) + 1)
                .Select(g => g.Key).ToList();
            var tone = rising.Count > 0 ? EffectTone.Worse
                : nowHours >= MinProblemHours && expected >= 3 && expected >= 2 * pcNow.Count + 1 ? EffectTone.Better : EffectTone.Same;
            // The power's shut-offs: only a jump far past their own usual (three at least, and three times it) is news.
            double powerExpected = powerUsual / usualHours * nowHours;
            var powerTone = powerNow >= PowerJump && powerNow >= 3 * powerExpected + 1 ? EffectTone.Worse : EffectTone.Same;
            problems = new UsualProblems([.. pcNow.OrderByDescending(e => e.Time)], pcUsual, nowHours, usualHours, usualWeeks, tone, powerNow, powerUsual)
            {
                PeriodWeeks = Math.Max(1, (to - from).TotalDays / 7), Rising = rising, UsualOfRising = rising.Sum(k => usualByKind.GetValueOrDefault(k)),
                PowerLikelyNow = powerLikelyNow, PowerTone = powerTone,
            };
        }

        return new PcUsual
        {
            From = from, To = to, UsualFrom = usualFrom, Heat = headline, Parts = parts,
            RestNow = restNow, RestUsual = restUsual,
            RestGpuRange = Range(usualRows.Where(h => h.App == 0 && h.N >= RestRangeDayMinutes).ToList(), Gpu),
            RestCpuRange = Range(usualRows.Where(h => h.App == 0 && h.N >= RestRangeDayMinutes).ToList(), Cpu),
            Fans = usualFans, Problems = problems, AppCrashesUsual = appUsual, UsualHours = usualHours, UsualSince = TimeUtil.FromUnix(u),
            FansSetOn = fans.Select(fan => (fan, set: fanChanges.Where(c => c.Time < to && c.Subject.Contains($"|{fan.Id}|", StringComparison.Ordinal)).Select(c => (DateTime?)c.Time).Max()))
                .Where(x => x.set is not null).GroupBy(x => (x.fan.Name, x.fan.Hardware)).ToDictionary(g => g.Key, g => g.Max(x => x.set!.Value)),
        };

        UsualHeat Part(string part, SteadyLoad n, SteadyLoad w, double a, double b, (double Low, double High) range, double? rn, double? rw)
        {
            // The room is what the part did at rest, read on both sides (one side alone says nothing about it).
            double diff = a - b, room = rn is double x && rw is double y ? x - y : 0, beyond = diff - room;
            bool moved = Math.Abs(diff) >= MinDiff && Math.Abs(beyond) >= MinBeyondRoom && Math.Sign(diff) == Math.Sign(beyond);
            double? last = lastPlay is null ? null : part == "GPU" ? Gpu(lastPlay) : Cpu(lastPlay);
            // Raised when the period moved past the bar; kept while the last day it was played is still a few degrees off
            // (the line then says that day's figure, 11° one day and 5° the next); gone once that day is back near usual.
            if (moved && last is double l && (Math.Abs(l - b) < KeepDiff || Math.Sign(l - b) != Math.Sign(diff))) moved = false;
            var tone = !moved ? EffectTone.Same : diff > 0 ? EffectTone.Worse : EffectTone.Better;
            return new UsualHeat(n.App, part, a, b, range.Low, range.High, n.Minutes, w.Minutes, w.Days, rn, rw, n.GpuPower, w.GpuPower, tone)
                { LastDay = lastPlay is null ? null : TimeUtil.FromUnix(lastPlay.Day), LastTemp = last };
        }
    }

    /// <summary>
    /// The card's rows: the temperatures, the fans, the PC's problems and the power cuts, each now and usually. The fans'
    /// meaning comes from the fan checks of the period (<paramref name="fanWarning"/>): speed alone moves with load.
    /// </summary>
    public List<UsualRow> Rows(Insight? fanWarning = null)
    {
        var rows = new List<UsualRow>();
        foreach (var p in Parts)
        {
            // Under the bar it isn't news, but a few degrees aren't "as usual" either: said as what they are, in grey.
            string diff = p.Diff;
            string means = p.Tone != EffectTone.Same ? p.Hotter ? Lang.T($"{diff} hotter in game") : Lang.T($"{diff} cooler in game")
                : Math.Abs(p.Now - p.Usual) < 2.5 ? Lang.T("As usual") : p.Hotter ? Lang.T($"{diff} warmer, too little to count") : Lang.T($"{diff} cooler, too little to count");
            string? rest = null;
            if (p.Tone != EffectTone.Same && (p.Part == "GPU" ? RestNow?.Gpu : RestNow?.Cpu) is double r)
            {
                string restTemp = Units.TempShort(r);
                rest = Math.Abs(r - ((p.Part == "GPU" ? RestUsual?.Gpu : RestUsual?.Cpu) ?? r)) < 1.5
                    ? Lang.T($"At rest {restTemp}, as usual") : Lang.T($"At rest {restTemp}, taken into account");
            }
            rows.Add(new($"heat-{p.Part.ToLowerInvariant()}", Lang.T($"{p.Part} in {p.App}"), p.Part == "GPU" ? Lang.T("Steady play, same power") : Lang.T("The same minutes"),
                Units.TempShort(p.Now), Span(p.Low, p.High), Lang.T($"{Days(p.UsualDays)} · {Units.Duration(p.UsualMinutes * 60.0)} of play"), p.Tone, means, rest));
        }
        if (RestNow is { Minutes: >= MinRestMinutes } rn && RestUsual is { Minutes: >= MinRestMinutes } ru)
        {
            string now = string.Join(" / ", new[] { rn.Gpu, rn.Cpu }.Where(x => x is not null).Select(x => Units.TempShort(x!.Value)));
            string usual = string.Join(" / ", new[] { RestGpuRange ?? Pair(ru.Gpu), RestCpuRange ?? Pair(ru.Cpu) }.Where(x => x is not null).Select(x => Span(x!.Value.Low, x.Value.High)));
            string label = rn.Gpu is not null && rn.Cpu is not null ? Lang.T("GPU / CPU, nothing running") : Lang.T("Nothing running");
            double? moved = rn.Gpu is double a && ru.Gpu is double b ? a - b : null;
            string meansRest = Lang.T("As usual");
            if (moved is double m && Math.Abs(m) >= 2)
            {
                string by = Units.Degrees(Math.Abs(m));
                meansRest = m > 0 ? Lang.T($"{by} warmer") : Lang.T($"{by} cooler");
            }
            // Warmer at rest is the room as often as the PC: said, never coloured.
            rows.Add(new("rest", Lang.T("At rest"), label, now, usual, Lang.T($"The {Days((int)Math.Round(UsualDays))} before"), EffectTone.Same, meansRest));
        }
        if (Fans.Count > 0)
        {
            var shown = Fans.OrderBy(x => x.Name, StringComparer.Ordinal).Take(4).ToList();
            var set = shown.Where(x => x.SetOn is not null).Select(x => x.SetOn!.Value).DefaultIfEmpty().Max();
            rows.Add(new("fans", Lang.T("Fans"), string.Join(" · ", shown.Select(x => x.Name)), string.Join(" · ", shown.Select(x => $"{x.Now:N0}")),
                string.Join(" · ", shown.Select(x => SpanRpm(x.Low, x.High))), set == default ? Lang.T("rpm, the days before") : Lang.T($"rpm, since your fan change on {set:d MMM}"),
                fanWarning is null ? EffectTone.Same : EffectTone.Worse, fanWarning is null ? Lang.T("As usual") : fanWarning.Key == "fan-stopped" ? Lang.T("Stopped") : Lang.T("Slower"), fanWarning?.Line));
        }
        if (Problems is { } pr)
        {
            rows.Add(new("problems", Lang.T("PC problems"), Lang.T("Blue screens, driver resets, shut-offs in use (not power cuts)"), pr.Now.Count == 0 ? Lang.T("None") : pr.Now.Count.ToString(),
                pr.UsualCount == 0 ? Lang.T("None") : Wording.Capital(About(pr.UsualCount, pr.UsualWeeks, pr.PeriodWeeks)), Lang.T("The weeks before, at this much use"), pr.Tone,
                // What went up, against its own usual: "3 graphics driver resets, usually none".
                pr.Tone switch { EffectTone.Worse => Lang.T("More than usual"), EffectTone.Better => Lang.T("Fewer than usual"), _ => Lang.T("As usual") },
                pr.Tone == EffectTone.Worse ? $"{Wording.Capital(pr.Words)}, {pr.UsualWords}" : null));
            if (pr.PowerNow + pr.PowerUsual > 0)
                rows.Add(new("power", Lang.T("Shut-offs that look like the power"), Lang.T("Likely or possibly a power cut, a dip, the mains or the power supply"), pr.PowerNow.ToString(),
                    pr.PowerUsual == 0 ? Lang.T("None") : Wording.Capital(About(pr.PowerUsual, pr.UsualWeeks, pr.PeriodWeeks)), Lang.T("The weeks before"), pr.PowerTone,
                    pr.PowerTone == EffectTone.Worse ? Lang.T("More than usual") : Lang.T("Not judged"), pr.PowerTone == EffectTone.Worse ? Lang.T("If the mains was steady, check the power supply") : null));
        }
        return rows;
    }

    /// <summary>
    /// The "Against your usual" rows of a report, as Reports and the export show them: a week, a month, or a custom range
    /// of a week or more (a day is Home's PC health to say); none for a year or all time.
    /// </summary>
    public static List<UsualRow> CardRows(Report? r)
    {
        if (r?.Usual is not { } usual || r.Range is not (ReportRange.Week or ReportRange.Month or ReportRange.Custom)) return [];
        if (r.Range == ReportRange.Custom && r.To - r.From < TimeSpan.FromDays(7)) return [];
        // Only a warning about one of the fans in the row (the motherboard's): the GPU's fans aren't in it.
        var fan = r.Insights.FirstOrDefault(i => i.Key is "fan-stopped" or "fan-slower" or "fan-older" && usual.Fans.Any(f => i.Line.StartsWith(f.Name + " ", StringComparison.Ordinal)));
        return usual.Rows(fan);
    }

    // ── Helpers ──

    private static (List<SteadyLoad> Loads, LoadTemps? AtRest) Steady(List<HeatDay> rows, Func<long, string> nameOf)
    {
        static double? Ratio(double sum, int n) => n > 0 ? sum / n : null;
        var steady = rows.Where(d => d.App != 0).GroupBy(d => d.App).Where(g => g.Sum(x => x.GpuN) > 0)
            .Select(g => new SteadyLoad(g.Key, nameOf(g.Key), g.Sum(x => x.N), g.Count(), g.Sum(x => x.GpuSum) / g.Sum(x => x.GpuN),
                Ratio(g.Sum(x => x.CpuSum), g.Sum(x => x.CpuN)), Ratio(g.Sum(x => x.PowerSum), g.Sum(x => x.PowerN))))
            .OrderByDescending(s => s.Minutes).ToList();
        var rest = rows.Where(d => d.App == 0).ToList();
        int minutes = rest.Sum(x => x.N);
        return (steady, minutes > 0 ? new LoadTemps(Ratio(rest.Sum(x => x.CpuSum), rest.Sum(x => x.CpuN)), Ratio(rest.Sum(x => x.GpuSum), rest.Sum(x => x.GpuN)), minutes) : null);
    }

    private static double? Gpu(HeatDay d) => d.GpuN > 0 ? d.GpuSum / d.GpuN : null;
    private static double? Cpu(HeatDay d) => d.CpuN > 0 ? d.CpuSum / d.CpuN : null;

    /// <summary>The lowest and highest of the days' averages; null with none.</summary>
    private static (double Low, double High)? Range(List<HeatDay> days, Func<HeatDay, double?> of)
    {
        var values = days.Select(of).OfType<double>().ToList();
        return values.Count == 0 ? null : (values.Min(), values.Max());
    }

    private static (double Low, double High)? Pair(double? v) => v is double x ? (x, x) : null;

    private static bool SamePower(double? a, double? b) => a is not double pa || b is not double pb || pb <= 0 || Math.Abs(pa - pb) / pb <= PowerShare;

    /// <summary>"67–69°", or "68°" when the two ends show the same.</summary>
    internal static string Span(double low, double high)
    {
        string a = Units.TempShort(low), b = Units.TempShort(high);
        return a == b ? a : $"{a.TrimEnd('°')}–{b}";
    }

    private static string SpanRpm(double low, double high) => Math.Round(low / 10) == Math.Round(high / 10) ? $"{low:N0}" : $"{low:N0}–{high:N0}";

    /// <summary>A count over some weeks as a week's worth (or a month's, for a month): "about 2 a week", "less than 1 a month".</summary>
    internal static string About(int count, double weeks, double periodWeeks)
    {
        bool month = periodWeeks >= 4;
        double each = month ? weeks / (periodWeeks) : weeks;
        double w = each <= 0 ? count : count / each;
        if (w < 0.75) return month ? Lang.T("less than 1 a month") : Lang.T("less than 1 a week");
        double n = Math.Round(w);
        return month ? Lang.T($"about {n:0} a month") : Lang.T($"about {n:0} a week");
    }

    private static string Days(int n) => n == 1 ? Lang.T("1 day") : Lang.T($"{n} days");
}
