using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Startup;

/// <summary>A usual as a spread, in seconds: the middle half of the last starts of one kind.</summary>
public sealed record Usual(double Lo, double Hi, int Count);

/// <summary>
/// How a start compares with its usual. <see cref="Partial"/>: only some of its parts are on record; <see cref="Pending"/>:
/// this start, minutes old, its parts still being read; <see cref="NotRecorded"/>: none of its parts is on record.
/// </summary>
public enum StartVerdict { Within, Quicker, Longer, AfterUpdate, NotRecorded, TooSoon, Partial, Pending, Above }

/// <summary>
/// An app Windows launched as a start went on, against its own usual (the middle half of its last 30 launches, once there
/// are 5). Built only by <see cref="StartupReport"/>.
/// </summary>
public sealed class AppView
{
    internal AppView(AppLaunch launch) => Launch = launch;

    public AppLaunch Launch { get; }
    public string Name => Launch.Name;
    public string Exe => Launch.Exe;
    public double Seconds => Launch.Ms / 1000.0;
    public Usual? Usual { get; internal set; }
    /// <summary>Launches of it on record for a usual, when there aren't enough yet.</summary>
    public int Count { get; internal set; }
    /// <summary>Clearly longer than its usual: 3 seconds and half again past its top (never on a restart after an update).</summary>
    public bool Longer { get; internal set; }
}

/// <summary>
/// One start as the page, Home and the export read it: its parts in seconds, its length, its usual of the same kind, how
/// it compares, and the start before it. Built only by <see cref="StartupReport"/>.
/// </summary>
public sealed class StartView
{
    internal StartView(StartRecord r) => Record = r;

    public StartRecord Record { get; }
    public DateTime Time => Record.Time;
    public StartKind Kind => Record.Kind;
    public bool AfterUpdate => Record.AfterUpdate == true;

    public double? Bios => Record.BiosMs / 1000.0;
    public double? Windows => Record.MainMs / 1000.0;
    public double? After => Record.PostMs / 1000.0;

    /// <summary>A part by its number: 0 before Windows, 1 Windows starting, 2 getting ready after sign-in.</summary>
    public double? PartOf(int part) => part switch { 0 => Bios, 1 => Windows, _ => After };

    /// <summary>None of its parts is on record: never drawn as 0s.</summary>
    public bool NotRecorded => Bios is null && Windows is null && After is null;

    /// <summary>Some of its parts are on record, not all (the rest still being read, or no longer in Windows' logs).</summary>
    public bool Partial => Length is null && !NotRecorded;

    /// <summary>The parts on record, added up (a partial start's figure).</summary>
    public double Known => (Bios ?? 0) + (Windows ?? 0) + (After ?? 0);

    /// <summary>This start, minutes old: its parts are still being read from Windows' logs.</summary>
    public bool Pending { get; internal set; }

    /// <summary>The start's length: its parts added up (no time before Windows on a PC whose firmware gives none). Null without Windows starting and getting ready after sign-in.</summary>
    public double? Length => Windows is double w && After is double a ? (Bios ?? 0) + w + a : null;

    /// <summary>What the page measures it by: its length.</summary>
    public double? Measure { get; internal set; }

    /// <summary>Each app launched as it started, in the order Windows launched them, against its own usual.</summary>
    public IReadOnlyList<AppView> Apps { get; internal set; } = [];

    /// <summary>The apps that took clearly longer than their usual, longest past it first.</summary>
    public IEnumerable<AppView> LongerApps => Apps.Where(a => a.Longer).OrderByDescending(a => a.Seconds - a.Usual!.Hi);

    public Usual? Usual { get; internal set; }
    /// <summary>Each part's usual, from the same starts as <see cref="Usual"/>.</summary>
    public Usual?[] PartUsuals { get; internal set; } = new Usual?[3];
    public StartVerdict Verdict { get; internal set; }
    public bool Longer => Verdict == StartVerdict.Longer;

    /// <summary>Starts of its kind on record for a usual, when there aren't enough yet (for "has 2 so far").</summary>
    public int KindCount { get; internal set; }

    /// <summary>The start before it (any kind), for what changed since.</summary>
    public StartView? Before { get; internal set; }

    /// <summary>What changed between the start before and this one, from the Timeline (never joined to what Windows reported).</summary>
    public List<SystemChange> Changes { get; internal set; } = [];

    /// <summary>A part well over its own usual (5 seconds past its top): said in amber.</summary>
    public bool PartLong(int part) => !AfterUpdate && PartOf(part) is double v && PartUsuals[part] is { } u && v > u.Hi + StartupReport.PartLongerBy;
}

/// <summary>A week of the Year chart: the average of its full starts' parts, and its starts.</summary>
public sealed record StartWeek(DateTime From, DateTime To, IReadOnlyList<StartView> Starts)
{
    /// <summary>The week's full starts with a figure.</summary>
    public IEnumerable<StartView> Measured => Starts.Where(s => !s.AfterUpdate && s.Measure is not null && s.Kind == StartKind.Full);
    public bool Empty => Starts.Count == 0;
    public double? Part(int part) => Measured.Select(s => s.PartOf(part)).OfType<double>().DefaultIfEmpty(double.NaN).Average() is var v && double.IsNaN(v) ? null : v;
    public double? Average => Measured.Select(s => s.Measure!.Value).DefaultIfEmpty(double.NaN).Average() is var v && double.IsNaN(v) ? null : v;
    public int Updates => Starts.Count(s => s.AfterUpdate);
    public List<StartView> LongerStarts => [.. Starts.Where(s => s.Longer)];
    public StartView? Longest => Measured.MaxBy(s => s.Measure);
}

/// <summary>
/// The Start-up page's facts for a period (1.22.0, approved mock-up 2026-11-27): one source for the page, Home's line and
/// the export. Starts are set only against their own kind (a full start, Fast Startup, waking from hibernation); the usual
/// is the middle half of the last 30 of that kind, once there are 5; a restart after an update is never in a usual; a
/// start is longer only past the top of its usual by 10 seconds and by a fifth. Each app launched as a start went on has its
/// own usual the same way (its last 30 launches), and took longer only past its top by 3 seconds and by half.
/// </summary>
public sealed class StartupReport
{
    /// <summary>How many starts of a kind a usual is taken from, and how many it needs.</summary>
    public const int UsualOf = 30, UsualNeeds = 5;

    /// <summary>Longer: past the top of its usual by this many seconds and by this share.</summary>
    public const double LongerBy = 10, LongerShare = 0.2;

    /// <summary>An app took longer: past the top of its own usual by this many seconds and by this share.</summary>
    public const double AppLongerBy = 3, AppLongerShare = 0.5;

    /// <summary>A part well over its own usual: this many seconds past its top.</summary>
    public const double PartLongerBy = 5;

    /// <summary>A change noticed this soon after a start (the first check after it) still happened before it.</summary>
    public static readonly TimeSpan NoticedAfter = TimeSpan.FromMinutes(15);

    public ReportRange Unit { get; private init; }
    public DateTime From { get; private init; }
    public DateTime To { get; private init; }

    /// <summary>The period's starts, oldest first.</summary>
    public List<StartView> Starts { get; private init; } = [];

    /// <summary>Every start on record up to the period's end, oldest first.</summary>
    public IReadOnlyList<StartView> All { get; private init; } = [];

    /// <summary>A start in the period has its length (Windows starting and getting ready after sign-in on record).</summary>
    public bool HasMeasured => Starts.Any(s => s.Length is not null);

    /// <summary>Any start on record up to the period's end has its length.</summary>
    public bool AnyMeasured => All.Any(s => s.Length is not null);

    /// <summary>This start is in the period and minutes old, its parts still being read (the page says it shows in a few minutes).</summary>
    public bool Pending { get; private init; }

    /// <summary>Each app's usual launch at the end of the period, by name (null: too soon to say), and its launches on record.</summary>
    public Dictionary<string, Usual?> AppUsuals { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> AppCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The usual of each kind at the end of the period (null: too soon to say).</summary>
    public Dictionary<StartKind, Usual?> Usuals { get; } = [];
    /// <summary>How many starts of each kind count towards a usual.</summary>
    public Dictionary<StartKind, int> Counted { get; } = [];

    /// <summary>The first start on record.</summary>
    public DateTime? FirstOnRecord { get; private init; }

    /// <summary>The same length of time just before, for the sentence.</summary>
    public List<StartView> BeforePeriod { get; private init; } = [];

    /// <summary>One week each on Year, from the first week on record.</summary>
    public List<StartWeek> Weeks { get; private init; } = [];

    /// <summary>The usual full start (the band on the chart, the figures' "Your usual is …").</summary>
    public Usual? FullUsual => Usuals.GetValueOrDefault(StartKind.Full);

    // ── Building ──

    /// <summary>The period's report, read from the history (the changes table too, for what changed before each start).</summary>
    public static StartupReport Build(RigsightDb db, ReportRange unit, DateTime from, DateTime to, DateTime now)
    {
        var (beforeFrom, _) = PeriodBefore(unit, from, to);
        // Every start up to now, not only to the period's end: a stretch with none is told apart from a hole in the record
        // by the start after it (each start's usual still comes only from the starts before it).
        var records = db.GetStarts(now.AddMinutes(1));
        var changes = records.Count == 0 ? [] : db.GetChanges(TimeUtil.ToUnix(records.Where(r => r.Time < from).Select(r => r.Time).DefaultIfEmpty(from).Max().AddDays(-1)),
            TimeUtil.ToUnix(to.Add(NoticedAfter)));
        return Build(records, changes, unit, from, to, now, beforeFrom, now - TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    /// <summary>The report from starts and changes already read (for tests and the export).</summary>
    /// <param name="thisStart">When this PC last started (for a start still being read); null where it isn't known.</param>
    public static StartupReport Build(IReadOnlyList<StartRecord> records, IReadOnlyList<SystemChange> changes, ReportRange unit, DateTime from, DateTime to, DateTime now,
        DateTime? beforeFrom = null, DateTime? thisStart = null)
    {
        var all = records.OrderBy(r => r.Time).Select(r => new StartView(r)).ToList();
        for (int i = 1; i < all.Count; i++) all[i].Before = all[i - 1];
        var inPeriod = all.Where(s => s.Time >= from && s.Time < to).ToList();
        foreach (var s in all) s.Measure = s.Length;
        // This start, minutes old, its parts still being read: the agent reads them a few minutes after it (it may not have yet).
        bool pending = false;
        if (thisStart is { } ts && ts >= from && ts < to && now - ts < StartupLogReader.RecordComesWithin)
        {
            var current = all.Where(s => Math.Abs((s.Time - ts).TotalMinutes) < 5).MaxBy(s => s.Time);
            if (current is null || current.Length is null)
            {
                pending = true;
                if (current is not null) current.Pending = true;
            }
        }

        var end = to < now ? to : now;
        var report = new StartupReport
        {
            Unit = unit, From = from, To = to, Starts = inPeriod, All = all, Pending = pending, FirstOnRecord = all.FirstOrDefault()?.Time,
            BeforePeriod = beforeFrom is { } bf ? [.. all.Where(s => s.Time >= bf && s.Time < from)] : [],
        };
        // Each app's usual launch: its last 30 launches up to the period's end, none on a restart after an update.
        var launches = all.Where(s => !s.AfterUpdate && s.Time < end).SelectMany(s => s.Record.Apps).GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var app in launches)
        {
            var last = app.TakeLast(UsualOf).ToList();
            report.AppCounts[app.Key] = last.Count;
            report.AppUsuals[app.Key] = UsualFrom(last.Select(a => a.Ms / 1000.0));
        }
        // Each start is set against the starts BEFORE it (never itself, never a later one), so a start's usual is the same
        // on Day, Week and Month, and a longer start isn't hidden by being part of its own usual.
        var earlier = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in all)
        {
            s.Apps = [.. s.Record.Apps.Select(a =>
            {
                var before = earlier.GetValueOrDefault(a.Name) is { } l ? l.TakeLast(UsualOf).ToList() : [];
                var u = UsualFrom(before);
                return new AppView(a) { Usual = u, Count = before.Count, Longer = !s.AfterUpdate && u is not null && IsAppLonger(a.Ms / 1000.0, u) };
            })];
            if (!s.AfterUpdate)
                foreach (var a in s.Record.Apps)
                {
                    if (!earlier.TryGetValue(a.Name, out var l)) earlier[a.Name] = l = [];
                    l.Add(a.Ms / 1000.0);
                }
        }
        foreach (var kind in Enum.GetValues<StartKind>())
        {
            var counted = Counting(all, kind, end);
            report.Counted[kind] = counted.Count;
            report.Usuals[kind] = UsualFrom(counted.Select(s => s.Measure!.Value));
            foreach (var s in all.Where(s => s.Kind == kind))
            {
                var prior = Counting(all, kind, s.Time);
                s.Usual = UsualFrom(prior.Select(p => p.Measure!.Value));
                s.PartUsuals = [.. Enumerable.Range(0, 3).Select(p => UsualFrom(prior.Select(x => x.PartOf(p)).OfType<double>()))];
                s.KindCount = prior.Count;
                s.Verdict = VerdictOf(s, 1);
            }
        }
        foreach (var s in inPeriod) s.Changes = ChangedBefore(changes, s.Before?.Time, s.Time);
        // Weeks up to now only: a week still to come isn't "No starts".
        if (unit == ReportRange.Year && report.FirstOnRecord is { } first) report.Weeks.AddRange(WeeksOf(inPeriod, from, end, first));
        return report;
    }

    /// <summary>The starts of a kind a usual is taken from: the last 30 before <paramref name="end"/>, none after an update, each with its figure.</summary>
    internal static List<StartView> Counting(IEnumerable<StartView> all, StartKind kind, DateTime end) =>
        [.. all.Where(s => s.Kind == kind && !s.AfterUpdate && s.Measure is not null && s.Time < end).TakeLast(UsualOf)];

    /// <summary>The middle half (the 25th to the 75th percentile), once there are 5.</summary>
    internal static Usual? UsualFrom(IEnumerable<double> values)
    {
        var v = values.Order().ToList();
        if (v.Count < UsualNeeds) return null;
        return new Usual(Quantile(v, 0.25), Quantile(v, 0.75), v.Count);
    }

    private static double Quantile(List<double> sorted, double p)
    {
        double k = (sorted.Count - 1) * p;
        int f = (int)Math.Floor(k);
        int c = Math.Min(f + 1, sorted.Count - 1);
        return sorted[f] + (sorted[c] - sorted[f]) * (k - f);
    }

    /// <summary>Longer: past the top of the usual by 10 seconds and by a fifth.</summary>
    public static bool IsLonger(double value, Usual u) => value > u.Hi + LongerBy && value > u.Hi * (1 + LongerShare);

    /// <summary>An app took longer: past the top of its own usual by 3 seconds and by half.</summary>
    public static bool IsAppLonger(double value, Usual u) => value > u.Hi + AppLongerBy && value > u.Hi * (1 + AppLongerShare);

    /// <param name="shown">How finely the figure is shown (a second): "quicker" only when it shows, never "48s, quicker
    /// than your usual 48s to 51s".</param>
    private static StartVerdict VerdictOf(StartView s, double shown)
    {
        if (s.AfterUpdate) return StartVerdict.AfterUpdate;
        if (s.Measure is not double m) return s.Pending ? StartVerdict.Pending : s.NotRecorded ? StartVerdict.NotRecorded : StartVerdict.Partial;
        if (s.Usual is not { } u) return StartVerdict.TooSoon;
        // Above its usual but not by enough to be called longer: said as it is ("a little longer"), never "within".
        return IsLonger(m, u) ? StartVerdict.Longer : m < u.Lo - shown / 2 ? StartVerdict.Quicker : m > u.Hi + shown / 2 ? StartVerdict.Above : StartVerdict.Within;
    }

    /// <summary>
    /// What changed between the start before and this one, from the Timeline: startup apps, Windows updates, drivers, apps,
    /// the BIOS, hardware and settings. A change noticed in the first minutes after this start (the first check after it)
    /// happened while the PC was off, before it.
    /// </summary>
    public static List<SystemChange> ChangedBefore(IEnumerable<SystemChange> changes, DateTime? before, DateTime start) =>
        [.. changes.Where(c => c.Kind is ChangeKind.Startup or ChangeKind.WindowsUpdate or ChangeKind.Windows or ChangeKind.Driver or ChangeKind.AppInstalled
                or ChangeKind.AppRemoved or ChangeKind.AppUpdated or ChangeKind.Firmware or ChangeKind.Hardware or ChangeKind.Setting)
            .Where(c => (before is not { } b || c.Likely >= b) && c.Likely < start && c.Time < start.Add(NoticedAfter))
            .OrderByDescending(c => c.Time)];

    /// <summary>A stretch with no start on record longer than this is a hole in the record (Windows' logs from then weren't kept), not days without a start.</summary>
    public static readonly TimeSpan HoleAfter = TimeSpan.FromDays(21);

    /// <summary>
    /// Whether the days from <paramref name="a"/> to <paramref name="b"/> have no start on record because nothing was kept
    /// (before the first start, or inside a hole of weeks), not because the PC didn't start then.
    /// </summary>
    public bool Unrecorded(DateTime a, DateTime b)
    {
        if (All.Count == 0 || b <= All[0].Time) return true;
        if (All.Any(s => s.Time >= a && s.Time < b)) return false;
        var prev = All.LastOrDefault(s => s.Time < a);
        var next = All.FirstOrDefault(s => s.Time >= b);
        return prev is not null && next is not null && next.Time - prev.Time > HoleAfter;
    }

    /// <summary>The start on record just before and just after a stretch with none (for "Nothing on record from … to …").</summary>
    public (DateTime? Before, DateTime? After) AroundHole(DateTime a, DateTime b) =>
        (All.LastOrDefault(s => s.Time < a)?.Time, All.FirstOrDefault(s => s.Time >= b)?.Time);

    /// <summary>The same length of time just before the period (a custom range's own length).</summary>
    public static (DateTime From, DateTime To) PeriodBefore(ReportRange unit, DateTime from, DateTime to) => unit switch
    {
        ReportRange.Year => (from.AddYears(-1), from),
        _ => (from - (to - from), from),
    };

    /// <summary>One week each from the first week on record (or the period's start) to the period's end.</summary>
    internal static List<StartWeek> WeeksOf(IReadOnlyList<StartView> starts, DateTime from, DateTime to, DateTime first)
    {
        var weeks = new List<StartWeek>();
        var start = from;
        while (start.AddDays(7) <= first.Date) start = start.AddDays(7);
        for (; start < to; start = start.AddDays(7))
        {
            var end = start.AddDays(7) < to ? start.AddDays(7) : to;
            var (a, b) = (start, end);
            weeks.Add(new StartWeek(a, b, [.. starts.Where(s => s.Time >= a && s.Time < b)]));
        }
        return weeks;
    }

    // ── The summary card ──

    /// <summary>The full starts the figures are about.</summary>
    private IEnumerable<StartView> FullStarts => Starts.Where(s => !s.AfterUpdate && s.Measure is not null && s.Kind == StartKind.Full);

    public int AfterUpdateCount => Starts.Count(s => s.AfterUpdate);
    public double? AverageFull => FullStarts.Select(s => s.Measure!.Value).DefaultIfEmpty(double.NaN).Average() is var v && double.IsNaN(v) ? null : v;
    public StartView? Longest => Starts.Where(s => !s.AfterUpdate && s.Measure is not null).MaxBy(s => s.Measure);
    public int LongerCount => Starts.Count(s => s.Longer);

    /// <summary>The usual the figures are set against: the full start's.</summary>
    public Usual? SummaryUsual => FullUsual;

    /// <summary>Whether any kind of start in the period has a usual (else "Too soon to say" for the longer ones).</summary>
    public bool AnyUsual => Starts.Any(s => s.Usual is not null);

    /// <summary>The icon's colour for the sentence: amber for a longer start or longer starts, grey with nothing to compare, else blue.</summary>
    public SentenceTone Tone { get; private set; }

    /// <summary>The sentence over the four figures (approved mock-up's summaryCard).</summary>
    public string Sentence(DateTime today)
    {
        if (Starts.Count == 0)
        {
            Tone = SentenceTone.Quiet;
            if (FirstOnRecord is not { } first || To <= first.Date) return FirstOnRecord is { } f ? Lang.T($"Not on record. Starts are on record from {f:d MMM yyyy}.") : Lang.T("No starts on record yet.");
            if (Unrecorded(From, To) && AroundHole(From, To) is ({ } hb, { } ha))
                return Lang.T($"Nothing on record from {hb:d MMM yyyy} to {ha:d MMM yyyy}.");
            return Unit == ReportRange.Day ? Lang.T("No starts this day. The PC was already on, or off all day.") : Lang.T("No starts in these days.");
        }
        if (Unit == ReportRange.Day)
        {
            var s0 = Longest ?? Starts[0];
            Tone = s0.Longer ? SentenceTone.Warm : SentenceTone.Plain;
            string? how = s0.Verdict switch
            {
                StartVerdict.AfterUpdate => Lang.T("a restart after an update"),
                StartVerdict.Longer when s0.Usual is { } u => Lang.T($"longer than your usual {StartupWords.Range(u)}"),
                StartVerdict.Quicker when s0.Usual is { } u => Lang.T($"a little quicker than your usual {StartupWords.Range(u)}"),
                StartVerdict.Within when s0.Usual is { } u => Lang.T($"within your usual {StartupWords.Range(u)}"),
                StartVerdict.Above when s0.Usual is { } u => Lang.T($"a little longer than your usual {StartupWords.Range(u)}"),
                _ => null,
            };
            double? took = s0.AfterUpdate ? s0.Length : s0.Measure;
            string time = TimeText.Time(s0.Time);
            if (took is not double t)
                return Starts.Count == 1 ? Lang.T($"The PC started once, at {time}.") : Lang.T($"The PC started {Starts.Count} times.");
            string length = StartupWords.Length(t);
            return (Starts.Count == 1, how) switch
            {
                (true, null) => Lang.T($"The PC started once, at {time}, and took {length}."),
                (true, _) => Lang.T($"The PC started once, at {time}, and took {length}, {how}."),
                (false, null) => Lang.T($"The PC started {Starts.Count} times. The longest, at {time}, took {length}."),
                _ => Lang.T($"The PC started {Starts.Count} times. The longest, at {time}, took {length}, {how}."),
            };
        }
        string words = StartupWords.Before(Unit, From, To);
        var now = UsualFrom(FullStarts.Select(s => s.Measure!.Value));
        var was = UsualFrom(BeforePeriod.Where(s => !s.AfterUpdate && s.Measure is not null && s.Kind == StartKind.Full).Select(s => s.Measure!.Value));
        if (now is not null && was is not null)
        {
            double d = (now.Lo + now.Hi) / 2 - (was.Lo + was.Hi) / 2;
            Tone = d >= 3 ? SentenceTone.Warm : SentenceTone.Plain;
            return Math.Abs(d) < 3 ? Lang.T($"Full starts took about as long as in {words}.")
                : d > 0 ? Lang.T($"Full starts took {StartupWords.Length(d)} longer than in {words}.")
                : Lang.T($"Full starts took {StartupWords.Length(-d)} less than in {words}.");
        }
        // No comparison with the days before: say why, and still say the longer starts (each is set against its own usual,
        // from every start before it, so "Longer than usual 3" beside "too few to compare" isn't a riddle).
        int longer = LongerCount;
        Tone = longer > 0 ? SentenceTone.Warm : SentenceTone.Quiet;
        string head = Starts.Count == 1 ? Lang.T("1 start on record.") : Lang.T($"{Starts.Count} starts on record.");
        string why = BeforePeriod.Count == 0 ? Lang.T($"Nothing on record in {words} to compare with.")
            : !BeforePeriod.Any(s => s.Measure is not null) ? Lang.T($"How long the starts in {words} took isn't on record.")
            : Lang.T($"Too few full starts to compare with {words}.");
        string tail = longer == 0 ? "" : " " + (longer == 1 ? Lang.T("1 took longer than its usual.") : Lang.T($"{longer} took longer than their usual."));
        return head + " " + why + tail;
    }

    // ── Home ──

    /// <summary>The key of Home's line about a longer start.</summary>
    public const string HomeKey = "startup-longer";

    /// <summary>
    /// Home's "What stands out today" line: the day's first full start, when it was clearly longer than its usual (never a
    /// restart after an update). Null otherwise: the day's own "Day started at" line stays.
    /// </summary>
    public static (double Seconds, Usual Usual, DateTime Time)? HomeLongerStart(StartupReport day)
    {
        var first = day.Starts.FirstOrDefault(s => s.Kind == StartKind.Full && !s.AfterUpdate);
        return first is { Longer: true, Measure: double m, Usual: { } u } ? (m, u, first.Time) : null;
    }

    /// <summary>The power button, as on the sidebar.</summary>
    public const string Icon = "";

    /// <summary>Home's line about today's first full start, when it was clearly longer than its usual (grey, opens Start-up on today); else null.</summary>
    public static Insight? HomeInsight(RigsightDb db, DateTime now)
    {
        var day = Build(db, ReportRange.Day, now.Date, now.Date.AddDays(1), now);
        return HomeLongerStart(day) is { } longer
            ? new Insight(Icon, StartupWords.HomeLine(longer.Seconds, longer.Usual), InsightTone.Neutral, HomeKey, 55) { Opens = InsightOpens.Startup }
            : null;
    }

    /// <summary>"What stands out today" with the longer start said in place of "Day started at …" (the same moment, the news of it).</summary>
    public static List<Insight> WithStartLine(List<Insight> lines, Insight? start)
    {
        if (start is null) return lines;
        int span = lines.FindIndex(i => i.Key == "span");
        if (span >= 0) lines[span] = start;
        else if (lines.Count < 5) lines.Add(start);
        return lines;
    }
}

/// <summary>The colour of the sentence's icon: blue, amber for longer, grey with nothing to set it against.</summary>
public enum SentenceTone { Plain, Warm, Quiet }
