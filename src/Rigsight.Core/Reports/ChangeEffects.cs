using Rigsight.Core.Data;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Reports;

public enum EffectTone { Same, Better, Worse }

/// <summary>A problem a change could be set against. <see cref="Graphics"/>: one a graphics driver can cause.</summary>
public sealed record ChangeProblem(DateTime Time, CrashKind Kind, bool Graphics, string Title = "");

/// <summary>
/// One reading before a change and since it ("GPU in Crimson Desert", "71°", "77°"), with what the difference means.
/// <see cref="Counted"/> false: shown so nothing is hidden, but not something this change can cause.
/// </summary>
public sealed record ChangeFigure(string Label, string Detail, string Before, string After, EffectTone Tone, string Meaning, bool Counted = true);

/// <summary>One thing the result rests on ("The same load"), or one that makes it less sure (<see cref="Ok"/> false).</summary>
public sealed record ChangeCheck(bool Ok, string Title, string Detail);

/// <summary>
/// A day around a change: the compared temperature where it was read (and the steady minutes it's from), at rest, the
/// problems counted, the hours of use.
/// </summary>
public sealed record ChangeDay(DateTime Day, double? Temp, double? Rest, int Problems, double Hours, int Minutes = 0);

/// <summary>
/// What a big change did, when it did something: hotter or cooler at the same load, more or fewer of the problems it
/// can cause. Only ever made for a change that made a difference (<see cref="Tone"/> is Better or Worse); a change
/// that changed nothing measurable, or with too little on a side to tell, has no result and nothing is shown for it.
/// </summary>
public sealed record ChangeResult
{
    /// <summary>The day of the change(s); several big changes on one day are one result.</summary>
    public DateTime Day { get; init; }
    /// <summary>The big changes that day, the one that names the result first.</summary>
    public List<SystemChange> Changes { get; init; } = [];
    public SystemChange Main => Changes[0];

    /// <summary>The days compared: [From, Day) before, (Day, To) since; Open when the "since" side still runs today.</summary>
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public bool Open { get; init; }
    public int DaysBefore { get; init; }
    public int DaysAfter { get; init; }

    public EffectTone Tone { get; init; }
    public Confidence Sure { get; init; }
    /// <summary>The chip's cause in capitals: "THE DRIVER", "THE UPDATE", "ONE OF THESE".</summary>
    public string Cause { get; init; } = "";

    /// <summary>"Crimson Desert runs 6° hotter on the GPU since this driver".</summary>
    public string Headline { get; init; } = "";
    /// <summary>The headline without the change it's about, to follow "Since it, ": "Crimson Desert runs 6° hotter on the GPU".</summary>
    public string Plain { get; init; } = "";
    /// <summary>The line under it: what makes it like for like.</summary>
    public string Context { get; init; } = "";
    /// <summary>Two at most, for the line or card on the Timeline.</summary>
    public List<ChangeFigure> Pairs { get; init; } = [];
    /// <summary>Every reading compared, for the opened change.</summary>
    public List<ChangeFigure> Rows { get; init; } = [];
    public List<ChangeCheck> Checks { get; init; } = [];
    /// <summary>The days and hours compared, and why it isn't more than possible where it isn't.</summary>
    public string Why { get; init; } = "";
    /// <summary>Only when it matters: past the warm level, or problems likely brought by the change.</summary>
    public string? Advice { get; init; }

    /// <summary>The problems counted, each side.</summary>
    public List<ChangeProblem> Before { get; init; } = [];
    public List<ChangeProblem> After { get; init; } = [];

    /// <summary>What <see cref="ChangeDay.Temp"/> is ("GPU in Crimson Desert"); null when no temperature was compared.</summary>
    public string? TempLabel { get; init; }
    /// <summary>The compared temperature's average before and since, as compared (every steady minute on each side).</summary>
    public double? TempBefore { get; init; }
    public double? TempAfter { get; init; }
    public List<ChangeDay> Days { get; init; } = [];

    /// <summary>A few words for a link under the thing it's about: "6° hotter in Crimson Desert since".</summary>
    public string Short { get; init; } = "";

    /// <summary>"LIKELY THE DRIVER", "POSSIBLY ONE OF THESE".</summary>
    public string Chip => Wording.Chip(Sure, Cause);
    public string HowSure => Wording.HowSure(Sure, Cause);
}

/// <summary>
/// How the PC ran after a big change (a graphics driver, a Windows update or version, the BIOS, new hardware, the power
/// plan) against before it: the same game's temperatures at the same load with the room taken out (as the drift insight
/// compares months), else the GPU at the same power across games, and the problems the change can cause against the
/// hours the PC was used. The days between two big changes belong to the earlier one, so nothing is put down to the
/// wrong change. A result is made only when something got better or worse; everything else says nothing.
/// </summary>
public static class ChangeEffects
{
    /// <summary>Days looked at on each side of a change (fewer when another change is nearer).</summary>
    public const int WindowDays = 14;

    // The drift insight's bar: an hour of steady play and of rest on each side, 5° apart and 3° beyond what the room did,
    // at a GPU power within 15% (else it isn't the same load).
    private const int MinSteadyMinutes = 60, MinRestMinutes = 60;
    private const double MinDiff = 5, MinBeyondRoom = 3, PowerShare = 0.15;

    /// <summary>Steady minutes on each side for a temperature to be "likely" the change's, not only "possibly".</summary>
    private const int SureMinutes = 120;

    /// <summary>Hours of use on each side before problems are counted against each other.</summary>
    private const double MinUseHours = 5;

    /// <summary>Problems on the busier side for "likely" rather than "possibly".</summary>
    private const int LikelyCount = 4;

    /// <summary>The warm levels of the insights: past them after a change, it's worth doing something.</summary>
    private const double GpuWarm = 83, CpuWarm = 85;

    /// <summary>
    /// A problem a graphics driver can cause: a graphics driver reset, a blue screen in a graphics driver, a sudden
    /// shut-off after the GPU hung. Anything else isn't set against a graphics driver.
    /// </summary>
    public static bool IsGraphics(CrashEvent e, CrashFinding? finding) => e.Kind switch
    {
        CrashKind.GpuDriverReset => true,
        CrashKind.SystemCrash => e.Facts?.Driver is { Length: > 0 } file && CrashFindings.IsGraphicsFile(file),
        CrashKind.UnexpectedShutdown => finding?.Cause == "THE GPU HUNG",
        _ => false,
    };

    /// <summary>The power plan, as the inventory records its changes.</summary>
    public static bool IsPowerPlan(SystemChange c) => c.Kind == ChangeKind.Setting && c.Subject.EndsWith(":" + Inventory.PowerPlan, StringComparison.Ordinal);

    /// <summary>The changes worth a before and after: what every game and app runs on.</summary>
    public static bool IsMajor(SystemChange c) =>
        c.IsGraphicsDriver || c.Kind is ChangeKind.Firmware or ChangeKind.Hardware or ChangeKind.Windows or ChangeKind.WindowsUpdate or ChangeKind.Fans || IsPowerPlan(c);

    /// <summary>
    /// The results from a database, as every page and the export show them: the problems a change can be set against
    /// (blue screens, graphics driver resets, shut-offs while running; none whose cause was outside the PC), each app
    /// by the name the user knows it by.
    /// </summary>
    public static Dictionary<DateTime, ChangeResult> Read(RigsightDb db, Settings.RigsightSettings s, List<SystemChange>? changes = null,
        List<CrashEvent>? crashes = null, Dictionary<long, CrashFinding>? found = null)
    {
        changes ??= db.GetChanges(0, long.MaxValue / 2);
        crashes ??= PcProblems(db);
        found ??= CrashCases.Of(db, crashes, s);
        var names = db.LoadApps().ToDictionary(a => a.Id, a => s.AppNames.TryGetValue(a.Exe, out var alias) ? alias : Apps.AppCatalog.KnownName(a.Exe) ?? a.Name);
        // The PC's own problems only (PcUsual.ShutOffOf, the rule every page counts by): not the power, not what was outside the PC.
        var against = crashes.Select(e => (e, f: found.GetValueOrDefault(e.Id))).Where(x => PcUsual.CountsAsPcProblem(x.e, x.f))
            .Select(x => new ChangeProblem(x.e.Time, x.e.Kind, IsGraphics(x.e, x.f), x.f?.Said ?? "")).ToList();
        return Of(changes, db.GetHeatDays(0, long.MaxValue / 2), db.GetSystemDays(0, long.MaxValue / 2) ?? [], against,
            id => names.GetValueOrDefault(id) ?? Lang.T("a game"), DateTime.Now);
    }

    /// <summary>The problems a change can be set against: blue screens, graphics driver resets, shut-offs while running.</summary>
    public static List<CrashEvent> PcProblems(RigsightDb db) => db.GetCrashes(0, long.MaxValue / 2)
        .Where(e => e.Kind is CrashKind.SystemCrash or CrashKind.GpuDriverReset || (e.Kind == CrashKind.UnexpectedShutdown && e.Moment == PowerMoment.Running)).ToList();

    /// <summary>The result of each day's big changes, by day; days where nothing got better or worse are left out.</summary>
    public static Dictionary<DateTime, ChangeResult> Of(IEnumerable<SystemChange> changes, IReadOnlyList<HeatDay> heat, IReadOnlyList<SystemDay> use,
        IReadOnlyList<ChangeProblem> problems, Func<long, string> nameOf, DateTime now)
    {
        var results = new Dictionary<DateTime, ChangeResult>();
        // Fan speeds set in the BIOS are compared too, but they never take days from the others: a driver the day after
        // a fan change keeps its days before, and two big changes on one day stay the driver's and the update's. A fan
        // change's own days end at any other big change, so nothing is put down to the fans that another change did.
        var major = changes.Where(IsMajor).ToList();
        var days = Days(major.Where(c => c.Kind != ChangeKind.Fans));
        var all = Days(major);
        var tomorrow = now.Date.AddDays(1);
        foreach (var (list, isFans) in new[] { (days, false), (all.Where(d => d.Of.All(c => c.Kind == ChangeKind.Fans)).ToList(), true) })
            foreach (var (day, at, of) in list)
            {
                var bounds = isFans ? all : days;
                var from = day.AddDays(-WindowDays);
                if (bounds.LastOrDefault(d => d.Day < day) is { Of: not null } before && before.Day.AddDays(1) > from) from = before.Day.AddDays(1);
                var to = day.AddDays(WindowDays + 1);
                if (bounds.FirstOrDefault(d => d.Day > day) is { Of: not null } after && after.Day < to) to = after.Day;
                bool open = to >= tomorrow; // still running: "since", not "in the days after"
                if (open) to = tomorrow;
                if (!results.ContainsKey(day) && new Comparison(day, at, from, to, open, of, heat, use, problems, nameOf).Result() is { } result) results[day] = result;
            }
        return results;

        // a problem in the minutes before it was noticed may be its doing
        static List<(DateTime Day, DateTime At, List<SystemChange> Of)> Days(IEnumerable<SystemChange> list) =>
            [.. list.GroupBy(c => c.Time.Date).OrderBy(g => g.Key).Select(g => (Day: g.Key, At: g.Min(c => c.Earliest), Of: g.OrderBy(Rank).ThenBy(c => c.Time).ToList()))];
    }

    /// <summary>Which of a day's big changes names it: what everything runs on first.</summary>
    private static int Rank(SystemChange c) => c switch
    {
        { IsGraphicsDriver: true } => 0,
        { Kind: ChangeKind.Hardware } => 1,
        { Kind: ChangeKind.Firmware } => 2,
        { Kind: ChangeKind.Windows } => 3,
        { Kind: ChangeKind.WindowsUpdate } => 4,
        { Kind: ChangeKind.Fans } => 6,
        _ => 5,
    };

    /// <summary>What the change is called in a sentence: "this driver", "this update".</summary>
    private static (string This, string Cause) Names(IReadOnlyList<SystemChange> of) => of.Count > 1 ? (Lang.T("these changes"), Lang.T("ONE OF THESE")) : of[0] switch
    {
        { IsGraphicsDriver: true } => (Lang.T("this driver"), Lang.T("THE DRIVER")),
        { Kind: ChangeKind.Hardware } => (Lang.T("this hardware change"), Lang.T("THE NEW HARDWARE")),
        { Kind: ChangeKind.Firmware } => (Lang.T("this BIOS update"), Lang.T("THE BIOS")),
        { Kind: ChangeKind.Windows } => (Lang.T("this Windows version"), Lang.T("THE NEW WINDOWS")),
        { Kind: ChangeKind.WindowsUpdate } => (Lang.T("this update"), Lang.T("THE UPDATE")),
        { Kind: ChangeKind.Fans } => (Lang.T("this fan change"), Lang.T("THE FAN CHANGE")),
        _ => (Lang.T("this power plan"), Lang.T("THE POWER PLAN")),
    };

    /// <summary>One change's comparison: each side's heat and problems, then what (if anything) got better or worse.</summary>
    private sealed class Comparison(DateTime day, DateTime at, DateTime from, DateTime to, bool open, List<SystemChange> of,
        IReadOnlyList<HeatDay> heat, IReadOnlyList<SystemDay> use, IReadOnlyList<ChangeProblem> problems, Func<long, string> nameOf)
    {
        // Whole days on each side; the change's own day is neither.
        private readonly int _daysBefore = (int)(day - from).TotalDays, _daysAfter = (int)(to - day).TotalDays - 1;
        private readonly bool _graphics = of.All(c => c.IsGraphicsDriver);
        private readonly bool _plan = of.All(IsPowerPlan);
        // Fan speeds change how hot things run, not whether the PC crashes or the mains cuts out: only heat is set against them.
        private readonly bool _fans = of.All(c => c.Kind == ChangeKind.Fans);
        private string Since => open ? Lang.T("since") : Lang.T($"in the {_daysAfter} days after");

        /// <summary>The thinner side's play, when under <see cref="SureMinutes"/>.</summary>
        private string? _thin;

        private sealed record Finding(EffectTone Tone, Confidence Sure, string Headline, string Context, string Short, bool Heat, bool Warm)
        {
            public string Plain { get; init; } = "";
        }

        public ChangeResult? Result()
        {
            if (_daysBefore < 1 || _daysAfter < 2) return null;
            var (thisOne, cause) = Names(of);
            var findings = new List<Finding>();
            var rows = new List<ChangeFigure>();
            var checks = new List<ChangeCheck>();
            var pairs = new List<ChangeFigure>();
            string? tempLabel = null;
            Func<HeatDay, bool>? tempRows = null;
            Func<HeatDay, double?>? tempOf = null;
            double? tempBefore = null, tempAfter = null;
            string playWhy = "";

            // ── Heat: the same game, else (not for the power plan, which changes power by design) the GPU at the same power across games.
            var (was, wasRest) = Heat(from, day);
            var (isNow, nowRest) = Heat(day.AddDays(1), to);
            bool restOk = nowRest is { Minutes: >= MinRestMinutes } && wasRest is { Minutes: >= MinRestMinutes };
            var game = isNow.Select(n => (Now: n, Was: was.FirstOrDefault(w => w.AppId == n.AppId)))
                .Where(p => p.Was is not null && p.Now.Minutes >= MinSteadyMinutes && p.Was.Minutes >= MinSteadyMinutes && (_plan || SamePower(p.Now, p.Was)))
                .OrderByDescending(p => Math.Min(p.Now.Minutes, p.Was!.Minutes)).FirstOrDefault();
            if (restOk && game.Now is { } n1 && game.Was is { } w1)
            {
                long app = n1.AppId;
                bool any = false;
                // Two hours on each side for "likely"; the hour that's enough to see it makes it only possible.
                bool thin = Math.Min(n1.Minutes, w1.Minutes) < SureMinutes;
                var sureHeat = of.Count == 1 && !thin ? Confidence.Likely : Confidence.Possible;
                foreach (var (part, a, b, rn, rw, warm) in new[] { ("GPU", n1.Gpu, w1.Gpu, nowRest!.Gpu, wasRest!.Gpu, GpuWarm), ("CPU", n1.Cpu ?? double.NaN, w1.Cpu ?? double.NaN, nowRest.Cpu, wasRest.Cpu, CpuWarm) })
                {
                    if (double.IsNaN(a) || double.IsNaN(b)) continue;
                    var f = Temp(part, Lang.T($"in {n1.App}"), n1.App, a, b, rn, rw, warm, sureHeat, thisOne);
                    rows.Add(f.Row with { Detail = part == "GPU" ? Lang.T($"Same load, after the first 10 minutes · {Play(w1.Minutes)} before, {Play(n1.Minutes)} {Since}") : Lang.T("Same minutes") });
                    if (f.Finding is { } found)
                    {
                        findings.Add(found);
                        if (!any)
                        {
                            pairs.Add(f.Row);
                            tempLabel = Lang.T($"{part} in {n1.App}");
                            tempRows = d => d.App == app;
                            tempOf = part == "GPU" ? Gpu : Cpu;
                            (tempBefore, tempAfter) = (b, a);
                        }
                        any = true;
                    }
                }
                rows.Add(new(Lang.T($"GPU power in {n1.App}"), _plan ? Lang.T("The power plan changes power by design, so it's shown, not held the same") : Lang.T("Within 15%, so it's the same load"),
                    Watts(w1.GpuPower), Watts(n1.GpuPower), EffectTone.Same, _plan ? "" : Lang.T("Same load")));
                checks.Add(new(true, Lang.T("The same game on both sides"), Lang.T($"{n1.App}: {Play(w1.Minutes)} before, {Play(n1.Minutes)} {Since}.")));
                if (!_plan) checks.Add(new(true, Lang.T("The same load"), Lang.T($"GPU power {Watts(w1.GpuPower)} before, {Watts(n1.GpuPower)} {Since}.")));
                playWhy = Lang.T($"{Play(w1.Minutes)} and {Play(n1.Minutes)} of {n1.App}");
                if (thin)
                {
                    string least = Play(Math.Min(n1.Minutes, w1.Minutes));
                    checks.Add(new(false, Lang.T("A thin side"), n1.Minutes < w1.Minutes
                        ? Lang.T($"Only {least} of {n1.App} {Since}; two hours on each side make it likely.")
                        : Lang.T($"Only {least} of {n1.App} before; two hours on each side make it likely.")));
                }
                _thin = thin ? Play(Math.Min(n1.Minutes, w1.Minutes)) : null;
            }
            else if (restOk && !_plan && Pooled(was) is { } pw && Pooled(isNow) is { } pn && pw.Minutes >= MinSteadyMinutes && pn.Minutes >= MinSteadyMinutes
                     && SamePower(pn.Power, pw.Power))
            {
                // No one game on both sides: the GPU at the same power is the same heat to get rid of, whatever the game.
                var f = Temp("GPU", Lang.T("in games"), Lang.T("games"), pn.Gpu, pw.Gpu, nowRest!.Gpu, wasRest!.Gpu, GpuWarm, Confidence.Possible, thisOne, plural: true);
                rows.Add(f.Row with { Detail = Lang.T($"Every game, at the same GPU power · {Play(pw.Minutes)} before, {Play(pn.Minutes)} {Since}") });
                rows.Add(new(Lang.T("GPU power in games"), Lang.T("Within 15%, so it's the same heat to get rid of"), Watts(pw.Power), Watts(pn.Power), EffectTone.Same, Lang.T("Same load")));
                if (f.Finding is { } found)
                {
                    findings.Add(found);
                    pairs.Add(f.Row);
                    tempLabel = Lang.T("GPU in games");
                    tempRows = d => d.App != 0;
                    tempOf = Gpu;
                    (tempBefore, tempAfter) = (pw.Gpu, pn.Gpu);
                }
                checks.Add(new(true, Lang.T("The same GPU power"), Lang.T($"{Watts(pw.Power)} before, {Watts(pn.Power)} {Since}, across games.")));
                checks.Add(new(false, Lang.T("Different games"), Lang.T("No one game was played for an hour on both sides, so it's every game at the same power: possibly, not likely.")));
                playWhy = Lang.T($"{Play(pw.Minutes)} and {Play(pn.Minutes)} of games");
            }
            if (rows.Count > 0)
            {
                double roomGpu = (nowRest!.Gpu ?? 0) - (wasRest!.Gpu ?? 0);
                string roomBy = Units.Degrees(Math.Abs(roomGpu)), restTemp = Units.TempShort(nowRest.Gpu);
                bool sameRest = Math.Abs(roomGpu) < 1;
                rows.Add(new(Lang.T("The GPU with nothing running"), Lang.T("Moves with the warmth around the PC"), Units.TempShort(wasRest.Gpu), restTemp, EffectTone.Same,
                    sameRest ? Lang.T("Same at rest") : roomGpu > 0 ? Lang.T($"{roomBy} warmer at rest, taken out above") : Lang.T($"{roomBy} cooler at rest, taken out above")));
                checks.Add(new(true, sameRest ? Lang.T("Same at rest") : Lang.T("At rest, taken into account"),
                    sameRest ? Lang.T($"With nothing running the GPU sat at {restTemp} both times.")
                    : roomGpu > 0 ? Lang.T($"With nothing running the GPU is {roomBy} warmer; that much is taken out.") : Lang.T($"With nothing running the GPU is {roomBy} cooler; that much is taken out.")));
            }

            ChangeCheck? problemCheck = null;

            // ── Problems: only those this change can cause, from the moment of the change, against the hours of use.
            var counted = problems.Where(Counts).ToList();
            var before = counted.Where(p => p.Time >= from && p.Time < at).ToList();
            var since = counted.Where(p => p.Time >= at && p.Time < to).ToList();
            // The change's own day is known only as a whole: its hours go with "after", where its problems from the
            // change on are counted (a rate after can then only come out lower, never falsely worse).
            double hoursBefore = Hours(from, day), hoursAfter = Hours(day, to);
            string what = _graphics ? Lang.T("Graphics problems") : Wording.Capital(Wording.Problems);
            if (!_fans && hoursBefore >= MinUseHours && hoursAfter >= MinUseHours)
            {
                double rateBefore = before.Count / hoursBefore, rateAfter = since.Count / hoursAfter;
                var tone = since.Count >= 2 && rateAfter >= 2 * rateBefore ? EffectTone.Worse
                    : before.Count >= 2 && rateBefore >= 2 * rateAfter ? EffectTone.Better : EffectTone.Same;
                var figure = new ChangeFigure(what, (_graphics ? Lang.T("Graphics driver resets, blue screens in a graphics driver, the graphics card hanging · ") : "")
                    + Lang.T($"{hoursBefore:0} h of use before, {hoursAfter:0} h {Since}"), Count(before), Count(since), tone,
                    tone == EffectTone.Worse ? Lang.T("More") : tone == EffectTone.Better ? Lang.T("Fewer") : before.Count + since.Count is > 0 and < 4 ? Lang.T("Too few to compare") : Lang.T("As before"));
                rows.Add(figure);
                if (tone != EffectTone.Same)
                {
                    var sure = of.Count == 1 && Math.Max(before.Count, since.Count) >= LikelyCount ? Confidence.Likely : Confidence.Possible;
                    string wordsSince = Words(since), wordsBefore = Words(before);
                    int days = _daysBefore;
                    // One day and many said whole: another language words "the day before" apart from "the 5 days before".
                    string counts = tone == EffectTone.Worse
                        ? before.Count == 0
                            ? days == 1 ? Lang.T($"{wordsSince}, none in the day before") : Lang.T($"{wordsSince}, none in the {days} days before")
                            : Lang.T($"{wordsSince} in {hoursAfter:0} h of use, against {wordsBefore} in {hoursBefore:0} h before")
                        : since.Count == 0
                            ? days == 1 ? Lang.T($"none, against {wordsBefore} in the day before") : Lang.T($"none, against {wordsBefore} in the {days} days before")
                            : Lang.T($"{wordsSince} in {hoursAfter:0} h of use, against {wordsBefore} in {hoursBefore:0} h before");
                    string lower = what.ToLowerInvariant(), since0 = Since;
                    bool more = tone == EffectTone.Worse;
                    // Whole for each case: "since this driver" and "in the 5 days after this driver" join the change differently elsewhere.
                    string said = (more, open) switch
                    {
                        (true, true) => Lang.T($"More {lower} since {thisOne}: {counts}"),
                        (true, false) => Lang.T($"More {lower} in the {_daysAfter} days after {thisOne}: {counts}"),
                        (false, true) => Lang.T($"Fewer {lower} since {thisOne}: {counts}"),
                        (false, false) => Lang.T($"Fewer {lower} in the {_daysAfter} days after {thisOne}: {counts}"),
                    };
                    findings.Add(new(tone, sure, said,
                        Lang.T("Counted against the hours the PC was used on each side."), more ? Lang.T($"More {lower} {since0}") : Lang.T($"Fewer {lower} {since0}"), false, false)
                        { Plain = more ? Lang.T($"more {lower}: {counts}") : Lang.T($"fewer {lower}: {counts}") });
                    problemCheck = new(sure == Confidence.Likely, sure == Confidence.Likely ? Lang.T("Enough problems to go on") : Lang.T("A small count"),
                        Lang.T($"{before.Count + since.Count} in all; at least {LikelyCount} on one side makes it likely."));
                }
                if (pairs.Count < 2) pairs.Add(figure);
                if (_graphics && problems.Where(p => !p.Graphics).ToList() is { Count: > 0 } other
                    && other.Count(p => p.Time >= from && p.Time < at) is var ob && other.Count(p => p.Time >= at && p.Time < to) is var oa && ob + oa > 0)
                    rows.Add(new(Lang.T($"Not counted: {And(other.Select(p => Wording.Noun(p.Kind, many: true)).Distinct())}"),
                        Lang.T("A graphics driver doesn't cause these unless the graphics card hung"), ob.ToString(), oa.ToString(), EffectTone.Same, Lang.T("See Crashes"), Counted: false));
            }

            var head = findings.OrderBy(f => f.Tone == EffectTone.Worse ? 0 : 1).ThenBy(f => f.Sure).ThenBy(f => f.Heat ? 0 : 1).FirstOrDefault();
            if (head is null) return null;
            // How sure is about the headline: the heat's checks when it's heat, the count when it's problems.
            if (!head.Heat)
            {
                string thinSide = Lang.T("A thin side"), differentGames = Lang.T("Different games");
                checks.RemoveAll(c => c.Title == thinSide || c.Title == differentGames);
                if (problemCheck is not null) checks.Insert(0, problemCheck);
            }

            checks.Add(of.Count == 1
                ? new(true, Lang.T("Nothing else big changed in these days"), BoundsText())
                : new(false, Lang.T("Two big changes on one day"), Lang.T($"{And(of.Select(c => c.Shown))} came in on the same day, so they can't be told apart.")));

            string dB = Days(_daysBefore), dA = Days(_daysAfter);
            string why = (open ? Lang.T($"{dB} before, {dA} since") : Lang.T($"{dB} before, {dA} after"))
                + " · " + (playWhy.Length > 0 && head.Heat ? playWhy : Lang.T($"{hoursBefore:0} h and {hoursAfter:0} h of use"))
                + (head.Sure == Confidence.Possible ? " · " + PossibleWhy(head, of.Count) : "");
            string? advice = head.Tone == EffectTone.Worse && (head.Warm || (!head.Heat && head.Sure == Confidence.Likely)) ? Advice(of[0]) : null;

            // Day by day: the compared temperature, at rest, the problems counted, the hours.
            var list = new List<ChangeDay>();
            for (var d = from; d < to; d = d.AddDays(1))
            {
                long ts = TimeUtil.ToUnix(d);
                var dayRows = heat.Where(h => h.Day == ts).ToList();
                var played = tempRows is null ? [] : dayRows.Where(tempRows).ToList();
                double? temp = tempRows is null ? null : Mean(played, tempOf!);
                double? rest = Mean(dayRows.Where(h => h.App == 0).ToList(), Gpu);
                list.Add(new(d, temp, rest, counted.Count(p => p.Time.Date == d), use.Where(u => u.Day == ts).Sum(u => u.ActiveSec) / 3600, played.Sum(r => r.N)));
            }

            return new ChangeResult
            {
                Day = day, Changes = of, From = from, To = to, Open = open, DaysBefore = _daysBefore, DaysAfter = _daysAfter,
                Tone = head.Tone, Sure = head.Sure, Cause = cause, Headline = head.Headline, Plain = head.Plain, Context = head.Context, Short = head.Short,
                Pairs = [.. pairs.Take(2)], Rows = rows, Checks = checks, Why = why, Advice = advice,
                Before = before, After = since, TempLabel = tempLabel, TempBefore = tempBefore, TempAfter = tempAfter, Days = list,
            };
        }

        /// <summary>A temperature before and since: its row, and a finding when it moved beyond the room by enough.</summary>
        private (ChangeFigure Row, Finding? Finding) Temp(string part, string where, string subject, double now, double then, double? restNow, double? restWas, double warm,
            Confidence sure, string thisOne, bool plural = false)
        {
            double diff = now - then, room = (restNow ?? 0) - (restWas ?? 0), beyondRoom = diff - room;
            bool moved = Math.Abs(diff) >= MinDiff && Math.Abs(beyondRoom) >= MinBeyondRoom && Math.Sign(diff) == Math.Sign(beyondRoom);
            var tone = !moved ? EffectTone.Same : diff > 0 ? EffectTone.Worse : EffectTone.Better;
            string by = Units.Degrees(Math.Abs(diff)), beyond = Units.Degrees(Math.Abs(beyondRoom));
            bool hotter = diff > 0, roomMoved = Math.Abs(room) >= 1;
            string meaning = moved
                ? (hotter, roomMoved) switch
                {
                    (true, true) => Lang.T($"{by} hotter, {beyond} beyond what rest moved"),
                    (true, false) => Lang.T($"{by} hotter"),
                    (false, true) => Lang.T($"{by} cooler, {beyond} beyond what rest moved"),
                    (false, false) => Lang.T($"{by} cooler"),
                }
                : Math.Abs(diff) <= 2 ? Lang.T("About the same: within 2°") : Lang.T($"Not enough to say: under {Units.Degrees(MinDiff)}");
            var row = new ChangeFigure($"{part} {where}", "", Units.TempShort(then), Units.TempShort(now), tone, meaning);
            if (!moved) return (row, null);
            string context = _plan ? Lang.T("Same game; the power plan changes power by design, so GPU power is shown too.")
                : (plural, Math.Abs(room) < 1) switch
                {
                    (true, true) => Lang.T("Every game at the same GPU power, and the same at rest."),
                    (true, false) => Lang.T("Every game at the same GPU power, with the change at rest taken out."),
                    (false, true) => Lang.T("Same game at the same load, and the same at rest."),
                    (false, false) => Lang.T("Same game at the same load, with the change at rest taken out."),
                };
            string who = Wording.Capital(subject);
            string plain = (open, plural, hotter) switch
            {
                (true, false, true) => Lang.T($"{who} runs {by} hotter on the {part}"),
                (true, false, false) => Lang.T($"{who} runs {by} cooler on the {part}"),
                (true, true, true) => Lang.T($"{who} run {by} hotter on the {part}"),
                (true, true, false) => Lang.T($"{who} run {by} cooler on the {part}"),
                (false, _, true) => Lang.T($"{who} ran {by} hotter on the {part}"),
                (false, _, false) => Lang.T($"{who} ran {by} cooler on the {part}"),
            };
            string since = Since;
            string headline = open ? Lang.T($"{plain} since {thisOne}") : Lang.T($"{plain} in the {_daysAfter} days after {thisOne}");
            return (row, new(tone, sure, headline, context,
                hotter ? Lang.T($"{by} hotter {where} {since}") : Lang.T($"{by} cooler {where} {since}"), true, diff > 0 && now >= warm) { Plain = plain });
        }

        private bool Counts(ChangeProblem p) => !_graphics || p.Graphics;

        private string BoundsText()
        {
            // Up to now, or up to a day: whole for each, since another language joins them differently ("y ahora", "y el 10 oct").
            var until = to.AddDays(-1);
            return open
                ? Lang.T($"No other graphics driver, Windows update, BIOS, hardware or power plan change from {from:d MMM} to now.")
                : Lang.T($"No other graphics driver, Windows update, BIOS, hardware or power plan change from {from:d MMM} to {until:d MMM}.");
        }

        private string PossibleWhy(Finding head, int changes) => changes > 1 ? Lang.T("Possibly, not likely: two big changes on one day.")
            : !head.Heat ? Lang.T("Possibly, not likely: a small count.")
            : _thin is { } thin ? Lang.T($"Possibly, not likely: only {thin} of play on one side.") : Lang.T("Possibly, not likely: across games, not one game.");

        private string? Advice(SystemChange c) => c switch
        {
            { IsGraphicsDriver: true } => c.Was is { Length: > 0 } was
                ? Lang.T($"Try the next driver, or go back to {was} with a clean install. This page will show whether it changes.")
                : Lang.T("Try the next driver. This page will show whether it changes."),
            { Kind: ChangeKind.WindowsUpdate } => Lang.T("Windows lets you uninstall a recent update in Settings, Windows Update, Update history. This page will show whether it changes."),
            { Kind: ChangeKind.Firmware } => Lang.T("Look for a newer BIOS from the board's maker, or load its optimized defaults. This page will show whether it changes."),
            { Kind: ChangeKind.Hardware } => Lang.T("Check the new part is seated properly and has its power connected."),
            { Kind: ChangeKind.Fans } => Lang.T("Set the fans back, or to a faster curve, in the BIOS. This page will show whether it changes."),
            _ when IsPowerPlan(c) && c.Was is { Length: > 0 } was => Lang.T($"Switch back to {was} in Windows' power settings. This page will show whether it changes."),
            _ => null,
        };

        // Each app's steady load, and the time at rest (app 0), over the days from one midnight to another.
        private (List<SteadyLoad> Steady, LoadTemps? AtRest) Heat(DateTime f0, DateTime t0)
        {
            long f = TimeUtil.ToUnix(f0), t = TimeUtil.ToUnix(t0);
            var rows = heat.Where(d => d.Day >= f && d.Day < t).ToList();
            var steady = rows.Where(d => d.App != 0).GroupBy(d => d.App).Where(g => g.Sum(x => x.GpuN) > 0)
                .Select(g => new SteadyLoad(g.Key, nameOf(g.Key), g.Sum(x => x.N), g.Count(), g.Sum(x => x.GpuSum) / g.Sum(x => x.GpuN),
                    Ratio(g.Sum(x => x.CpuSum), g.Sum(x => x.CpuN)), Ratio(g.Sum(x => x.PowerSum), g.Sum(x => x.PowerN)))).ToList();
            var rest = rows.Where(d => d.App == 0).ToList();
            int minutes = rest.Sum(x => x.N);
            return (steady, minutes > 0 ? new LoadTemps(Ratio(rest.Sum(x => x.CpuSum), rest.Sum(x => x.CpuN)), Ratio(rest.Sum(x => x.GpuSum), rest.Sum(x => x.GpuN)), minutes) : null);
        }

        /// <summary>Every game together, weighted by minutes; only games with a power reading count.</summary>
        private static (int Minutes, double Gpu, double Power)? Pooled(List<SteadyLoad> loads)
        {
            var read = loads.Where(l => l.GpuPower is not null).ToList();
            int minutes = read.Sum(l => l.Minutes);
            return minutes == 0 ? null : (minutes, read.Sum(l => l.Gpu * l.Minutes) / minutes, read.Sum(l => l.GpuPower!.Value * l.Minutes) / minutes);
        }

        private double Hours(DateTime f0, DateTime t0)
        {
            long f = TimeUtil.ToUnix(f0), t = TimeUtil.ToUnix(t0);
            return use.Where(d => d.Day >= f && d.Day < t).Sum(d => d.ActiveSec) / 3600;
        }
    }

    private static double? Ratio(double sum, int n) => n > 0 ? sum / n : null;
    private static double? Gpu(HeatDay d) => d.GpuN > 0 ? d.GpuSum / d.GpuN : null;
    private static double? Cpu(HeatDay d) => d.CpuN > 0 ? d.CpuSum / d.CpuN : null;

    private static double? Mean(List<HeatDay> rows, Func<HeatDay, double?> of)
    {
        var read = rows.Where(r => of(r) is not null).ToList();
        int n = read.Sum(r => r.N);
        return n == 0 ? null : read.Sum(r => of(r)!.Value * r.N) / n;
    }

    private static bool SamePower(SteadyLoad a, SteadyLoad b) => SamePower(a.GpuPower, b.GpuPower);

    private static bool SamePower(double? a, double? b) =>
        a is not double pa || b is not double pb || pb <= 0 || Math.Abs(pa - pb) / pb <= PowerShare;

    /// <summary>"3 graphics driver resets and 1 blue screen".</summary>
    private static string Words(IEnumerable<ChangeProblem> problems) =>
        And(problems.GroupBy(p => p.Kind).OrderByDescending(g => g.Count()).Select(g => CrashWords.Count(g.Key, g.Count())));

    /// <summary>"A and B and C", each pair joined as the language joins two things.</summary>
    private static string And(IEnumerable<string> items) => items.DefaultIfEmpty("").Aggregate((a, b) => Lang.T($"{a} and {b}"));

    private static string Count(List<ChangeProblem> problems) => problems.Count == 0 ? Lang.T("None") : problems.Count.ToString();

    private static string Days(int n) => n == 1 ? Lang.T("day") : Lang.T($"{n} days");

    private static string Play(int minutes) => Units.Duration(minutes * 60.0);

    private static string Watts(double? w) => w is double x ? $"{x:0} W" : "–";
}
