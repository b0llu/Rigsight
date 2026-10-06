using System.Globalization;
using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Ask;

/// <summary>
/// Answers a question typed in plain words from what's recorded on this PC. The question is read by
/// <see cref="AskRouter"/>; the answer is put together here by code, from the same records and the same analysis the
/// pages use, so nothing in it is made up: a number is a reading, a verdict follows a rule, and where the records
/// don't say, the answer says that instead.
/// </summary>
public sealed class AskEngine(AskRouter router)
{
    public AskRouter Router => router;

    /// <param name="meaning">Set when the user said what the question meant (a correction): answered as that, whatever it reads as.</param>
    public AskAnswer Ask(RigsightDb db, RigsightSettings settings, string text, AskContext? context = null, DateTime? now = null, AskIntent meaning = AskIntent.None)
    {
        var run = new AskRun(db, settings, now ?? DateTime.Now);
        var q = router.Read(text, run.Now, run.AskApps, context);
        if (meaning != AskIntent.None) q = q with { Intent = meaning, Score = 1, HowTo = false, FollowsUp = false };
        var answer = run.Answer(q);
        answer.Query = q;
        if (answer.Understood && answer.Context is null)
            answer.Context = new AskContext(q.Intent, q.Anchor ?? run.Used ?? q.Period, q.AppId, q.App, q.Part, q.Why, run.Problem ?? q.Problem, run.ProblemApp ?? q.ProblemApp, q.Metric);
        return answer;
    }

    /// <summary>
    /// The same for a message that asks several things ("why did it crash yesterday and how hot was it?"): each part is
    /// answered in turn, a later part taking the time and app of the first where it names none. When a part can't be
    /// read as a question of its own, the message is answered whole, as one.
    /// </summary>
    public List<AskAnswer> AskAll(RigsightDb db, RigsightSettings settings, string text, AskContext? context = null, DateTime? now = null)
    {
        var parts = AskRouter.Split(text);
        if (parts.Count < 2) return [Ask(db, settings, text, context, now)];
        var answers = new List<AskAnswer>();
        var carry = context;
        foreach (string part in parts)
        {
            var run = new AskRun(db, settings, now ?? DateTime.Now);
            var q = router.Read(part, run.Now, run.AskApps, carry);
            // The first part's time and app go with the later ones.
            if (answers.Count > 0 && answers[0].Query is { } first)
                q = q with { Period = q.Period ?? first.Period, AppId = q.AppId ?? first.AppId, App = q.App ?? first.App };
            var answer = run.Answer(q);
            answer.Query = q;
            if (!answer.Understood) return [Ask(db, settings, text, context, now)];
            answer.Context ??= new AskContext(q.Intent, run.Used ?? q.Period, q.AppId, q.App, q.Part, q.Why, run.Problem ?? q.Problem, run.ProblemApp ?? q.ProblemApp, q.Metric);
            carry = answer.Context;
            answers.Add(answer);
        }
        // Every part came to the same thing ("the PC wasn't on yesterday"): said once.
        return answers.Select(x => x.Lead).Distinct().Count() == 1 ? [answers[0]] : answers;
    }

    /// <summary>The apps a question can name, with how much each is used (for suggestions while typing).</summary>
    public List<AskApp> Apps(RigsightDb db, RigsightSettings settings) => new AskRun(db, settings, DateTime.Now).AskApps;

    /// <summary>Questions to start from, made from what's on this PC (a real crash, the game played most this week).</summary>
    public List<string> Starters(RigsightDb db, RigsightSettings settings, DateTime? now = null) => new AskRun(db, settings, now ?? DateTime.Now).Starters();
}

/// <summary>One question being answered: the database, the settings and the clock it is answered against.</summary>
internal sealed partial class AskRun
{
    private readonly RigsightDb _db;
    private readonly RigsightSettings _s;
    private readonly Dictionary<long, AppRow> _apps;
    private readonly Dictionary<(DateTime, DateTime, ReportRange), Report> _reports = [];
    private AskQuery _q = new("");

    public DateTime Now { get; }
    public List<AskApp> AskApps { get; }

    /// <summary>The kind of problem the answer was about (and its app), so "that" in the next question can mean it.</summary>
    public string? Problem { get; private set; }
    public string? ProblemApp { get; private set; }

    /// <summary>The period the answer was given for, when the question named none (so a follow-up can move from it).</summary>
    public AskPeriod? Used { get; private set; }

    public AskRun(RigsightDb db, RigsightSettings settings, DateTime now)
    {
        (_db, _s, Now) = (db, settings, now);
        _apps = db.LoadApps().ToDictionary(a => a.Id);
        var time = db.GetAppMonths(0, long.MaxValue / 2).GroupBy(m => m.AppId).ToDictionary(g => g.Key, g => g.Sum(m => m.FgSec));
        AskApps = [.. _apps.Values.Select(a => new AskApp(a.Id, NameOf(a.Id), a.Exe, CategoryOf(a), time.GetValueOrDefault(a.Id)))];
    }

    public AskAnswer Answer(AskQuery q)
    {
        _q = q;
        try
        {
            bool asksData = q.Intent is not (AskIntent.None or AskIntent.Other or AskIntent.Hello or AskIntent.Thanks or AskIntent.Help or AskIntent.Who) && !q.HowTo;
            if ((asksData || (q.Intent is AskIntent.None or AskIntent.Other && NotRecorded(q.Text.ToLowerInvariant()) is not null)) && Guard(q) is { } stopped) return stopped;
            var answer = q.HowTo && q.Intent is not (AskIntent.Hello or AskIntent.Thanks or AskIntent.Help or AskIntent.Who) ? HowTo()
                : q.Against is not null && Compare() is { } compared ? compared
                : q.OtherAppId is not null && TwoApps() is { } both ? both
                : q.Intent switch
            {
                AskIntent.Crashes => q.History && q.Problem is not null ? CrashHistory() : Crashes(),
                AskIntent.Metric => Metric(),
                AskIntent.Changes => Changes(),
                AskIntent.Temps => Temps(),
                AskIntent.Usage => Usage(),
                AskIntent.TopApps => TopApps(),
                AskIntent.Slow => Slow(),
                AskIntent.Memory => Memory(),
                AskIntent.Network => Network(),
                AskIntent.Storage => Storage(),
                AskIntent.Fans => Fans(),
                AskIntent.Health => Health(),
                AskIntent.Specs => Specs(),
                AskIntent.Help => Help(),
                AskIntent.Hello => Hello(),
                AskIntent.Thanks => Thanks(),
                AskIntent.Who => Who(),
                AskIntent.Other => OutOfScope(),
                _ => NotSure(),
            };
            if (answer.Read.Length == 0 && answer.Understood) answer.Read = ReadLine(q);
            // Its time came from earlier in the conversation and isn't today: said above the answer, with today one click away.
            if (answer.Understood && q.CarriedTime && q.Period is { IsReal: true } kept && !IsToday(kept) && q.Intent is not (AskIntent.Storage or AskIntent.Specs))
            {
                answer.Carried = $"Still about {Bare((Used ?? kept).Label)}";
                var keep = _q;
                _q = q with { App = null };
                answer.CarriedInstead = Again(Today);
                _q = keep;
            }
            // It leaned on the question before, but could have stood alone: the other reading, one click away.
            if (answer.Understood && q.FollowsUp && q.Alternative != AskIntent.None && SuggestAlone(q) is { } other && !answer.FollowUps.Contains(other))
                answer.FollowUps.Insert(0, other);
            return answer;
        }
        catch (Exception ex)
        {
            Log.Error("ask", ex);
            return new AskAnswer
            {
                Lead = "Something went wrong while I was reading the records for that.",
                Note = "The question was understood, but its answer couldn't be put together. Asking it another way may work.",
                Understood = false,
            };
        }
    }

    /// <summary>
    /// What stops a question before any record is read: a date that isn't on the calendar, a time still to come, or
    /// an app this PC has never run. Each is said plainly; answering for today or for the whole PC as if nothing odd
    /// had been asked would be a wrong answer that looks right.
    /// </summary>
    private AskAnswer? Guard(AskQuery q)
    {
        if (NotRecorded(q.Text.ToLowerInvariant()) is { } missing)
        {
            var no = new AskAnswer { Lead = $"I don't have that: Rigsight doesn't record {missing}.", Understood = false };
            no.Paragraphs.Add("What it does record: temperatures, load and power of the CPU and GPU, memory, fans, internet use, time in each app, crashes, and what changed on the PC.");
            no.FollowUps.AddRange(Starters().Take(3));
            return no;
        }
        foreach (var p in new[] { q.Period, q.Against }.OfType<AskPeriod>())
        {
            if (!p.IsReal)
                return new AskAnswer { Lead = $"\u201C{p.Label}\u201D isn't a date on the calendar. Which day did you mean?", Understood = false };
            if (p.From > Now)
                return new AskAnswer { Lead = $"That hasn't happened yet: {Bare(p.Label)} is still to come. I can only look back.", Understood = false };
        }
        if (q.UnknownApp is { } name && q.AppId is null && q.Intent is AskIntent.Usage or AskIntent.Temps or AskIntent.Crashes or AskIntent.Memory
            or AskIntent.Network or AskIntent.Slow or AskIntent.Metric)
        {
            var a = new AskAnswer { Lead = $"I can't find an app or game called \u201C{name}\u201D on this PC.", Understood = false };
            // The nearest names, to pick from: picking one answers for it, and the name typed is that app's from then on.
            string typed = name.ToLowerInvariant();
            var words = typed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3).ToList();
            int Near(AskApp x)
            {
                string n = x.Name.ToLowerInvariant();
                return n.Contains(typed, StringComparison.Ordinal) ? 4
                    : words.Any(w => n.Contains(w, StringComparison.Ordinal)) ? 3
                    : typed.Length >= 3 && n.StartsWith(typed[..3], StringComparison.Ordinal) ? 2
                    : n.Split(' ').Any(w => Math.Abs(w.Length - typed.Length) <= 2 && w.Length >= 4 && Shared(w, typed) >= Math.Max(3, typed.Length - 2)) ? 1 : 0;
            }
            var near = AskApps.Where(x => x.ActiveSec >= 60).Select(x => (App: x, Rank: Near(x))).Where(x => x.Rank > 0)
                .OrderByDescending(x => x.Rank).ThenByDescending(x => x.App.ActiveSec).Take(4).Select(x => x.App).ToList();
            // Nothing like it: the most used of its kind, in case it goes by another name here.
            if (near.Count == 0)
                near = [.. AskApps.Where(x => x.ActiveSec >= 600 && (x.Category == Settings.AppCategory.Game) == GameAsked(q)).OrderByDescending(x => x.ActiveSec).Take(4)];
            a.Paragraphs.Add(near.Count > 0 ? "If it goes by another name here, pick it and I'll remember what you call it:"
                : FirstDay is { } first ? $"I only know what has been open here since {AskTime.DayText(first, Now)}." : "I only know the apps that have been open on this PC.");
            a.AppChoices.AddRange(near.Select(x => new AskAppChoice(x.Name, x.Exe)));
            a.Query = q;
            var top = AskApps.Where(x => x.Category == Settings.AppCategory.Game && x.ActiveSec >= 600).OrderByDescending(x => x.ActiveSec).Take(2).ToList();
            if (a.AppChoices.Count == 0) // the names to pick from say it already
                foreach (var g in top) a.FollowUps.Add($"How long did I play {g.Name} this week?");
            a.FollowUps.Add("What did I use most this week?");
            return a;
        }
        return null;
    }

    /// <summary>Something asked for by name that isn't among the records (so nothing else is answered in its place).</summary>
    private static string? NotRecorded(string lower) =>
        FpsFigure().IsMatch(lower) && !FpsTrouble().IsMatch(lower) ? "frame rates (FPS)"
        : VramWords().IsMatch(lower) ? "how much graphics memory (VRAM) is in use"
        : PingWords().IsMatch(lower) ? "ping or latency"
        : BatteryWords().IsMatch(lower) ? "the battery"
        : DiskSpeedWords().IsMatch(lower) ? "drive read and write speeds"
        : CostWords().IsMatch(lower) ? "what the electricity costs (only the watts the CPU and GPU draw)"
        : null;

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(fps|frame ?rates?|frames per second)\b")]
    private static partial System.Text.RegularExpressions.Regex FpsFigure();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(why|drop\w*|dip\w*|low|bad|trash|terrible|awful|lag\w*|stutter\w*|slow\w*|tank\w*|fell|falling|worse)\b")]
    private static partial System.Text.RegularExpressions.Regex FpsTrouble();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(vram|video memory|graphics memory|gpu memory)\b")]
    private static partial System.Text.RegularExpressions.Regex VramWords();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(ping|latency|packet loss|jitter)\b")]
    private static partial System.Text.RegularExpressions.Regex PingWords();

    [System.Text.RegularExpressions.GeneratedRegex(@"\bbatter(y|ies)\b")]
    private static partial System.Text.RegularExpressions.Regex BatteryWords();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(disk|drive|ssd|hdd|nvme) (read |write )?speeds?\b|\b(read|write) speeds?\b")]
    private static partial System.Text.RegularExpressions.Regex DiskSpeedWords();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(electricity|power) (cost|bill)s?\b|\bcost of (electricity|power|running)\b|\bhow much does (it|my pc) cost\b")]
    private static partial System.Text.RegularExpressions.Regex CostWords();

    /// <summary>Letters two words share, in order from the start (how alike a mistyped name is to a real one).</summary>
    private static int Shared(string a, string b)
    {
        int n = 0;
        foreach (char c in b)
        {
            int at = a.IndexOf(c, StringComparison.Ordinal);
            if (at < 0) continue;
            n++;
            a = a[(at + 1)..];
        }
        return n;
    }

    private static bool GameAsked(AskQuery q) => q.Games || System.Text.RegularExpressions.Regex.IsMatch(q.Text.ToLowerInvariant(), @"\b(play\w*|game\w*|fps)\b");

    /// <summary>The question as it would read by itself, for the reading a follow-up didn't take.</summary>
    private string? SuggestAlone(AskQuery q)
    {
        var keep = _q;
        _q = q with { Period = null, App = null };
        try { return Suggest(q.Alternative) is { } text ? "No, I meant: " + text : null; }
        finally { _q = keep; }
    }

    private bool IsToday(AskPeriod p) => p.IsDay && p.From.Date == Now.Date;

    /// <summary>The days in a period the PC was on at all (never the count of a long report's monthly buckets).</summary>
    private int DaysOn(AskPeriod p) => Math.Max(1, _db.GetSystemDays(TimeUtil.ToUnix(p.From.Date), TimeUtil.ToUnix(p.To))?.Count(d => d.Minutes > 0) ?? 1);

    /// <summary>Asked for something worked out over many days that isn't worked out yet (a habit): said, not guessed at.</summary>
    private AskAnswer Unsupported(string what)
    {
        var a = new AskAnswer { Lead = $"I can't work out {what} yet. I answer about a particular day, week or month, not about habits.", Understood = false };
        a.FollowUps.Add("How long was I on my PC this week?");
        a.FollowUps.Add("Which day was I on my PC the most?");
        return a;
    }

    // ── How a question was read ─────────────────────────────────────────

    private static string Topic(AskIntent intent, bool why) => intent switch
    {
        AskIntent.Crashes => why ? "Why it crashed" : "Crashes",
        AskIntent.Changes => "Changes",
        AskIntent.Temps => "Temperatures",
        AskIntent.Usage => "Time on the PC",
        AskIntent.TopApps => "Most used",
        AskIntent.Slow => "What was heavy",
        AskIntent.Memory => "Memory",
        AskIntent.Network => "Internet",
        AskIntent.Storage => "Storage",
        AskIntent.Fans => "Fans",
        AskIntent.Health => "How the PC is doing",
        AskIntent.Specs => "This PC",
        AskIntent.Metric => "Readings",
        _ => "",
    };

    private string ReadLine(AskQuery q)
    {
        var parts = new List<string> { Topic(q.Intent, q.Why && q.Against is null && !q.History) };
        if ((Used ?? q.Period) is { } p) parts.Add(q.Against is { } b ? $"{Bare(p.Label)} against {Bare(b.Label)}" : Bare(p.Label));
        if (q.App is not null) parts.Add(q.App);
        else if (q.Part is AskPart.Cpu) parts.Add("CPU");
        else if (q.Part is AskPart.Gpu) parts.Add("GPU");
        return string.Join(" · ", parts.Where(x => x.Length > 0));
    }

    private static string Bare(string label) =>
        label.StartsWith("on ", StringComparison.Ordinal) || label.StartsWith("in ", StringComparison.Ordinal) ? label[3..] : label;

    // ── Shared helpers ──────────────────────────────────────────────────

    private string NameOf(long id) => _apps.TryGetValue(id, out var a)
        ? _s.AppNames.TryGetValue(a.Exe, out var alias) ? alias : AppCatalog.KnownName(a.Exe) ?? a.Name
        : "an app";

    private string NameOfExe(string exe, string? path = null)
    {
        if (_s.AppNames.TryGetValue(exe, out var alias)) return alias;
        if (AppCatalog.KnownName(exe) is { } known) return known;
        if (_apps.Values.FirstOrDefault(a => string.Equals(a.Exe, exe, StringComparison.OrdinalIgnoreCase)) is { } row) return row.Name;
        return path is not null ? AppCatalog.ResolveName(exe, path) : AppCatalog.FallbackName(exe);
    }

    private AppCategory CategoryOf(AppRow a) => _s.AppCategories.TryGetValue(a.Exe, out var c) ? c : a.Category;

    /// <summary>The period asked about, or the answer's own when none was (remembered for the "how it was read" line).</summary>
    private AskPeriod PeriodOr(Func<AskPeriod> usual)
    {
        if (_q.Period is { } p) return p;
        return Used = usual();
    }

    private AskPeriod Today => AskTime.Day(Now, Now);

    /// <summary>The report for a period: the same one its page shows for a whole day, week, month or year.</summary>
    private Report ReportFor(AskPeriod p)
    {
        var key = (p.From, p.To, p.Range);
        if (_reports.TryGetValue(key, out var have)) return have;
        var report = p.Range is ReportRange.Day or ReportRange.Week or ReportRange.Month or ReportRange.Year or ReportRange.All
            ? ReportBuilder.Build(_db, p.Range, p.From, _s)
            : ReportBuilder.BuildCustom(_db, p.From, p.To > Now ? ReportBuilder.HourEnd(Now) : p.To, _s);
        return _reports[key] = report;
    }

    /// <summary>The first day anything was recorded.</summary>
    private DateTime? FirstDay => _db.FirstDataTime() is long f ? TimeUtil.FromUnix(f).Date : null;

    /// <summary>The answer when a period lies before the records, or nothing was recorded in it; null when there is something.</summary>
    private AskAnswer? NothingRecorded(AskPeriod p, Report r)
    {
        if (r.HasData && r.OnSec > 0) return null;
        var a = new AskAnswer();
        if (FirstDay is not { } first)
            a.Lead = "Nothing has been recorded yet. Give it a little while and ask again.";
        else if (p.To <= first)
            a.Lead = $"Nothing was recorded {p.Label}: the records start on {AskTime.DayText(first, Now)}.";
        else if (p.From > Now)
            a.Lead = $"That hasn't happened yet: {Bare(p.Label)} is still to come.";
        else if (p.Range == ReportRange.Custom && (p.To - p.From).TotalHours <= 24 && ReportFor(AskTime.Day(p.From, Now)) is { HasData: true, FirstActive: { } on, LastActive: { } off })
        {
            // Part of a day the PC was used on: say when it was, that day.
            a.Lead = $"Nothing was recorded {p.Label}: the PC wasn't on then.";
            a.Paragraphs.Add($"{AskTime.Day(p.From, Now).Title.Replace("On ", "On ")} it was in use from {Clock(on)} to {(p.From.Date == Now.Date ? "now" : Clock(off))}.");
            a.FollowUps.Add(Again(AskTime.Day(p.From, Now)));
        }
        else
        {
            a.Lead = $"Nothing was recorded {p.Label}: the PC wasn't on.";
            if (_db.LastUsedDayBefore(TimeUtil.ToUnix(p.From.Date)) is long last)
            {
                var day = TimeUtil.FromUnix(last).Date;
                a.Paragraphs.Add($"The last day it was used before that was {AskTime.DayText(day, Now)}.");
                a.FollowUps.Add(Again(AskTime.Day(day, Now)));
            }
        }
        return a;
    }

    /// <summary>The same question about another period, as a follow-up to click.</summary>
    private string Again(AskPeriod p) => _q.Intent switch
    {
        AskIntent.Crashes => $"Any crashes {p.Label}?",
        AskIntent.Changes => $"What changed {p.Label}?",
        AskIntent.Temps => $"How hot did it get {p.Label}?",
        AskIntent.Usage => $"How long was I on my PC {p.Label}?",
        AskIntent.TopApps => $"What did I use most {p.Label}?",
        AskIntent.Slow => $"What was heavy on my PC {p.Label}?",
        AskIntent.Memory => $"How much memory was used {p.Label}?",
        AskIntent.Network => $"How much data did I use {p.Label}?",
        AskIntent.Fans => $"How were my fans {p.Label}?",
        _ => $"How was my PC {p.Label}?",
    };

    private static string Temp(double? celsius) => Units.Short(SensorKind.Temperature, celsius);
    private static string Percent(double? value) => value is double v ? $"{v:0}%" : "—";
    private static string Clock(DateTime t) => t.ToString("h:mm tt", CultureInfo.InvariantCulture);
    private static string Dur(double seconds) => Units.Duration(seconds);

    /// <summary>"at 9:14 PM yesterday", "at 9:14 PM on Fri 2 Oct".</summary>
    private string At(DateTime t) => $"at {Clock(t)} {DayWord(t)}";

    /// <summary>"today", "yesterday", "on Fri 2 Oct".</summary>
    private string DayWord(DateTime t) => t.Date == Now.Date ? "today" : t.Date == Now.Date.AddDays(-1) ? "yesterday" : "on " + AskTime.DayText(t, Now);

    /// <summary>A moment within a period: the time alone inside one day, with its day otherwise.</summary>
    private string AtIn(DateTime t, AskPeriod p) => p.IsDay || p.From.Date == p.To.AddTicks(-1).Date ? $"at {Clock(t)}" : $"{AskTime.DayText(t, Now)}, {Clock(t)}";

    private static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n:N0} {many}";

    private static string Ordinal(int n) => (n % 100) is 11 or 12 or 13 ? $"{n}th" : (n % 10) switch { 1 => $"{n}st", 2 => $"{n}nd", 3 => $"{n}rd", _ => $"{n}th" };

    private static string Ago(DateTime then, DateTime from)
    {
        int days = (int)(from.Date - then.Date).TotalDays;
        return days <= 0 ? "the same day" : days == 1 ? "the day before" : $"{days} days before";
    }

    private static AskTone ToneOf(InsightTone t) => t switch
    {
        InsightTone.Good => AskTone.Good, InsightTone.Warn => AskTone.Warn, InsightTone.Hot => AskTone.Hot, _ => AskTone.Neutral,
    };

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    // ── Talk that isn't about the records ───────────────────────────────

    /// <summary>What can be asked, by example. The examples are real questions about this PC where there's something to ask.</summary>
    private AskAnswer Help()
    {
        var a = new AskAnswer
        {
            Lead = "Ask me anything about what this PC has been doing, in your own words.",
            PointsTitle = "Things I can look up",
            Understood = false,
        };
        a.Points.Add(new("**Crashes**: why the PC or a game crashed, what was going on just before, and what had changed."));
        a.Points.Add(new("**Changes**: what was installed, updated or removed, and when."));
        a.Points.Add(new("**Heat and fans**: how hot it got, in which game, and whether that's within your limits."));
        a.Points.Add(new("**Time**: how long you were on the PC, in which apps and games, and when you last played something."));
        a.Points.Add(new("**Slowdowns**: what was working the CPU, memory or drive at the time."));
        a.Points.Add(new("**Internet, memory and storage**: what used them, and how much."));
        a.Points.Add(new("**Readings**: the average, highest or lowest load, power or temperature, in any app."));
        a.Points.Add(new("**Comparisons**: one day, week or month against another."));
        a.Paragraphs.Add("Name a time (\"yesterday\", \"last Friday\", \"in September\") and an app or game if you like. A short follow-up such as \"and the day before?\" works too.");
        a.Note = "Everything is worked out on this PC from its own records. Nothing you type leaves it.";
        a.FollowUps.AddRange(Starters());
        return a;
    }

    private AskAnswer Hello()
    {
        var a = new AskAnswer { Lead = $"{Greeting()}! I'm Riggy. Ask me about this PC: crashes, heat, what changed, where your time went.", Understood = false };
        a.FollowUps.AddRange(Starters());
        return a;
    }

    private string Greeting() => Now.Hour switch { < 5 => "Hi, night owl", < 12 => "Good morning", < 18 => "Hi", _ => "Good evening" };

    private AskAnswer Thanks()
    {
        string lower = _q.Text.ToLowerInvariant();
        string lead = lower.Contains("bye") || lower.StartsWith("see ", StringComparison.Ordinal) || lower.StartsWith("cya", StringComparison.Ordinal) || lower.Contains("night")
                || lower.StartsWith("later", StringComparison.Ordinal) ? "See you. I'll be in the corner if you need me."
            : lower.Contains("thank") || lower.Contains("cheers") ? "Any time. Ask away if something else comes up."
            : "Glad that helped. Ask away if something else comes up.";
        return new AskAnswer { Lead = lead, Understood = false };
    }

    private AskAnswer Who()
    {
        var a = new AskAnswer
        {
            Lead = "I'm Riggy, the part of Rigsight you can talk to.",
            Understood = false,
        };
        a.Paragraphs.Add("I'm not a chatbot from the internet. A small language model on this PC works out what you're asking; the answer itself is put together from this PC's records, so every number in it is a real reading.");
        a.Paragraphs.Add("**Nothing you type leaves this PC**, and nothing is looked up online. It works with the internet off.");
        a.Paragraphs.Add("What I can see is what Rigsight records: which apps were open and for how long, the PC's readings, crashes, and what was installed or changed. Not your files, not what you type anywhere else, not your passwords.");
        a.Note = "That also means I only know this PC: I can't browse, write, or give general advice.";
        a.FollowUps.Add("What can you do?");
        return a;
    }

    /// <summary>Not about this PC's history at all.</summary>
    private AskAnswer OutOfScope()
    {
        if (_q.Rude)
        {
            var sorry = new AskAnswer { Lead = "Sorry that wasn't what you needed.", Understood = false };
            sorry.Paragraphs.Add("Tell me what you were after in other words. And if I read a question wrong, \"Not what I meant\" under the answer teaches me for next time.");
            return sorry;
        }
        if (NotEnglish(_q.Text)) return new AskAnswer { Lead = "I only understand English for now. Ask me about this PC in English and I'll look it up.", Understood = false };
        var a = new AskAnswer
        {
            Lead = "That one's outside what I know. I only answer from this PC's own records.",
            Understood = false,
        };
        a.Paragraphs.Add("Crashes, heat, what changed, where your time and data went: those I can look up.");
        a.FollowUps.AddRange(Starters().Take(3));
        return a;
    }

    /// <summary>Advice was asked for ("how do I…"): say so, and offer what the records do hold on the subject.</summary>
    private AskAnswer HowTo()
    {
        if (_q.Problem is not null && _q.Period is { IsReal: true } && FixWords().IsMatch(_q.Text.ToLowerInvariant()))
        {
            _q = _q with { Intent = AskIntent.Crashes, Why = true, HowTo = false };
            var crash = Crashes();
            if (crash.Advice is { Length: > 0 } advice)
            {
                var fix = new AskAnswer { Lead = "**What I'd try first:** " + advice };
                fix.Paragraphs.Add("That comes from what this crash's records point to. I can't walk you through the steps themselves.");
                fix.Links.AddRange(crash.Links);
                fix.Read = "What to try · " + Bare(_q.Period.Label);
                return fix;
            }
        }
        var a = new AskAnswer
        {
            Lead = "I can't walk you through that: I answer from what's recorded on this PC, not with how-to advice.",
            Understood = false,
        };
        var topic = _q.Intent is AskIntent.None or AskIntent.Other ? _q.Second : _q.Intent;
        string lower = _q.Text.ToLowerInvariant();
        string? about = lower.Contains("bios") ? "What is my BIOS version?"
            : lower.Contains("driver") ? "Which graphics driver is installed?"
            : lower.Contains("overclock") || lower.Contains("undervolt") ? "Is my PC overheating?"
            : lower.Contains("windows") ? "What Windows version is this?"
            : null;
        if ((about ?? Suggest(topic)) is { } s)
        {
            a.Paragraphs.Add("What I can tell you is how things stand here:");
            a.FollowUps.Add(s);
        }
        else a.FollowUps.AddRange(Starters().Take(3));
        return a;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(fix|solve|stop|prevent|repair|do about|avoid|get rid)\b")]
    private static partial System.Text.RegularExpressions.Regex FixWords();

    /// <summary>Mostly letters from outside the Latin alphabet: another language, which isn't understood yet.</summary>
    private static bool NotEnglish(string text) => text.Count(char.IsLetter) is >= 3 and var letters && text.Count(c => char.IsLetter(c) && c > 0x24F) > letters / 2;

    /// <summary>Not understood well enough to answer: ask back with the nearest readings.</summary>
    private AskAnswer NotSure()
    {
        var a = new AskAnswer { Understood = false };
        if (NotEnglish(_q.Text)) return new AskAnswer { Lead = "I only understand English for now. Ask me about this PC in English and I'll look it up.", Understood = false };
        var guesses = new[] { _q.Second }.Where(i => i != AskIntent.None).Select(Suggest).OfType<string>().ToList();
        if (guesses.Count > 0 && _q.Score >= 0.28)
        {
            a.Lead = "I'm not sure I got that. Did you mean something like this?";
            a.FollowUps.AddRange(guesses);
            a.FollowUps.AddRange(Starters().Where(s => !guesses.Contains(s)).Take(2));
        }
        else
        {
            // Mostly letters from outside English: say so, so nobody keeps rephrasing in vain.
            a.Lead = NotEnglish(_q.Text) ? "I only understand English for now. Ask me about this PC in English and I'll look it up."
                : "I didn't get that one. I can only answer about this PC's own history.";
            a.FollowUps.AddRange(Starters().Take(3));
            a.FollowUps.Add("What can you do?");
        }
        return a;
    }

    /// <summary>A plain question for an intent, with the app and time of the one just asked where they fit.</summary>
    private string? Suggest(AskIntent intent)
    {
        string when = _q.Period is { } p ? " " + p.Label : "";
        string app = _q.App is { } name ? name : "";
        return intent switch
        {
            AskIntent.Crashes => app.Length > 0 ? $"Why did {app} crash{when}?" : $"Did my PC crash{(when.Length > 0 ? when : " lately")}?",
            AskIntent.Changes => $"What changed on my PC{(when.Length > 0 ? when : " this week")}?",
            AskIntent.Temps => app.Length > 0 ? $"How hot did my PC get in {app}{when}?" : $"How hot did my PC get{(when.Length > 0 ? when : " today")}?",
            AskIntent.Usage => app.Length > 0 ? $"How long did I use {app}{(when.Length > 0 ? when : " this week")}?" : $"How long was I on my PC{(when.Length > 0 ? when : " today")}?",
            AskIntent.TopApps => $"What did I use most{(when.Length > 0 ? when : " this week")}?",
            AskIntent.Slow => $"What was heavy on my PC{(when.Length > 0 ? when : " today")}?",
            AskIntent.Memory => $"What used the most memory{(when.Length > 0 ? when : " today")}?",
            AskIntent.Network => $"How much data did I use{(when.Length > 0 ? when : " today")}?",
            AskIntent.Storage => "How full are my drives?",
            AskIntent.Fans => "Are my fans working?",
            AskIntent.Health => $"How is my PC doing{when}?",
            AskIntent.Specs => "What's in this PC?",
            AskIntent.Metric => $"What was my average CPU load{(when.Length > 0 ? when : " today")}?",
            _ => null,
        };
    }

    public List<string> Starters()
    {
        var list = new List<string>();
        try
        {
            // A real problem from the last two weeks, named as the user would.
            var crash = _db.GetCrashes(TimeUtil.ToUnix(Now.AddDays(-14)), TimeUtil.ToUnix(Now.AddMinutes(1)))
                .Where(c => !_s.IsCrashMuted(c.AppExe) && !(c.Kind == CrashKind.UnexpectedShutdown && c.Moment != PowerMoment.Running))
                .OrderByDescending(Severity).ThenByDescending(c => c.Ts).FirstOrDefault();
            if (crash is not null)
            {
                string what = crash.Kind is CrashKind.AppCrash or CrashKind.AppHang ? NameOfExe(crash.AppExe, crash.AppPath) : "my PC";
                list.Add($"Why did {what} crash {DayWord(crash.Time)}?");
            }
            else list.Add("Did my PC crash this month?");

            list.Add("What changed on my PC this week?");
            var game = AskApps.Where(a => a.Category == AppCategory.Game && a.ActiveSec >= 3600).MaxBy(a => a.ActiveSec);
            list.Add(game is not null ? $"How hot does {game.Name} make my PC?" : "How hot did my PC get today?");
            list.Add("How long was I on my PC yesterday?");
            list.Add(game is not null ? $"When did I last play {game.Name}?" : "What did I use most this week?");
            list.Add("What used the most data this week?");
        }
        catch (Exception ex)
        {
            Log.Error("ask", ex);
        }
        return list;
    }
}
