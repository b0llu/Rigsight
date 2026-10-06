using System.Text.RegularExpressions;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;

namespace Rigsight.Core.Ask;

internal sealed partial class AskRun
{
    /// <summary>Days a question about one app's time covers when it names no time.</summary>
    private const int AppUseDays = 7;

    private static string Verb(AppCategory c, bool past = true) => c == AppCategory.Game ? (past ? "played" : "play") : past ? "used" : "use";

    private AskAnswer Usage()
    {
        if (_q.AppId is long appId) return AppUsage(appId);
        string asked = _q.Text.ToLowerInvariant();
        if (HabitWords().IsMatch(asked)) return Usual(asked);
        if (AwayWords().IsMatch(asked)) return LeftOn(asked);
        if (LongestWords().IsMatch(asked)) return LongestSession();
        if (WhichDayWords().IsMatch(asked)) return BusiestDay();
        if (HowManyDaysWords().IsMatch(asked)) return DaysUsed();
        if (_q.Period is null && SinceWhenWords().IsMatch(asked)) return RecordsStart();

        var period = PeriodOr(() => Today);
        var r = ReportFor(period);
        if (NothingRecorded(period, r) is { } none) return none;
        var a = new AskAnswer();
        a.Links.Add(new("Open Reports", "reports", period.IsDay ? period.From : null));
        string lower = _q.Text.ToLowerInvariant();
        int days = DaysOn(period);

        if (_q.Games)
        {
            var games = r.Apps.Where(x => x.Category == AppCategory.Game && x.ActiveSec >= 60).OrderByDescending(x => x.ActiveSec).ToList();
            if (games.Count == 0)
            {
                a.Lead = $"No games were played {period.Label}.";
                if (AskApps.Where(x => x.Category == AppCategory.Game).MaxBy(x => x.ActiveSec) is { } fav) a.FollowUps.Add($"When did I last play {fav.Name}?");
                return a;
            }
            a.Lead = $"You played for **{Dur(r.GamingSec)}** {period.Label}{(games.Count == 1 ? $", all of it {games[0].Name}" : $", across {games.Count} games")}.";
            if (games.Count > 1)
                foreach (var g in games.Take(5)) a.Points.Add(new($"**{g.Name}**: {Dur(g.ActiveSec)}"));
            if (!period.IsDay && days > 1) a.Paragraphs.Add($"That is {Dur(r.GamingSec / days)} a day over the {days} days the PC was on.");
            AddInsights(a, r, "record", "streak");
            a.FollowUps.Add($"How hot does {games[0].Name} make my PC?");
            a.FollowUps.Add($"What did I use most {period.Label}?");
            return a;
        }

        // "When did I start", "how late": the clock times first.
        if (period.IsDay && ClockWords().IsMatch(lower) && r.FirstActive is { } first && r.LastActive is { } last)
        {
            var start = r.DayStart ?? first;
            a.Lead = IsToday(period)
                ? $"You started at **{Clock(start)}** today, and have been on for {Dur(r.ActiveSec)} since."
                : $"{period.Title} you started at **{Clock(start)}** and were last on at **{Clock(last)}**: {Dur(r.ActiveSec)} of use.";
            if (r.LateUntil is { } late) a.Paragraphs.Add($"The night before ran on until {Clock(late)}.");
        }
        else
        {
            a.Lead = $"You were on your PC for **{Dur(r.ActiveSec)}** {period.Label}.";
            if (period.IsDay && r.FirstActive is { } f && r.LastActive is { } l)
                a.Paragraphs.Add($"From {Clock(r.DayStart ?? f)} to {(IsToday(period) ? "now" : Clock(l))}; the PC itself was on for {Dur(r.OnSec)}.");
            else if (days > 1)
                a.Paragraphs.Add($"That is {Dur(r.ActiveSec / days)} a day over the {days} days it was on. The PC itself was on for {Dur(r.OnSec)}.");
        }
        a.Facts.Add(new("In use", Dur(r.ActiveSec)));
        if (r.GamingSec >= 60) a.Facts.Add(new("Gaming", Dur(r.GamingSec)));
        a.Facts.Add(new("PC on", Dur(r.OnSec)));
        if (r.Apps.Where(x => x.ActiveSec >= 60).MaxBy(x => x.ActiveSec) is { } top) a.Facts.Add(new("Most used", top.Name));
        AddInsights(a, r, "screen-compare", "stretch", "away", "streak", "early", "record");
        a.FollowUps.Add($"What did I use most {period.Label}?");
        a.FollowUps.Add(IsToday(period) ? "How long was I on my PC yesterday?" : "How long was I on my PC this week?");
        return a;
    }

    /// <summary>The report's own observations on a subject, as points (they're whole sentences already).</summary>
    private static void AddInsights(AskAnswer a, Report r, params string[] keys)
    {
        foreach (var i in r.Insights.Where(i => keys.Contains(i.Key)).Take(3)) a.Points.Add(new(i.Text, ToneOf(i.Tone)));
    }

    private AskAnswer AppUsage(long appId)
    {
        string app = NameOf(appId);
        var category = _apps.TryGetValue(appId, out var row) ? CategoryOf(row) : AppCategory.Other;
        var a = new AskAnswer();
        a.Links.Add(new("Open Apps", "apps"));
        var lastSession = LastSession(appId);
        double total = AskApps.FirstOrDefault(x => x.Id == appId)?.ActiveSec ?? 0;

        if (lastSession is null && total < 60)
        {
            // Never in front, but open all the same (a launcher, a helper): how long it was open is what there is to say.
            var span = PeriodOr(() => AskTime.LastDays(AppUseDays, Now));
            var open = ReportFor(span).Apps.FirstOrDefault(x => x.Id == appId);
            a.Lead = open is { OpenSec: >= 60 }
                ? $"**{app}** was open for **{Dur(open.OpenSec)}** {span.Label}, but never in front for a minute or more."
                : $"{app} has been open, but never in use for a minute or more, so there's no time on record for it.";
            return a;
        }

        // "When did I last play…"
        if (_q.Last && _q.Period is null)
        {
            if (lastSession is null)
            {
                a.Lead = $"There's no session of {app} on record, though {Dur(total)} of use is counted in the monthly totals.";
                return a;
            }
            var (start, end) = (TimeUtil.FromUnix(lastSession.Start), TimeUtil.FromUnix(lastSession.End));
            a.Lead = $"You last {Verb(category)} **{app}** {DayWord(start)}, from {Clock(start)} to {Clock(end)}: {Dur(lastSession.ActiveSec)}.";
            int ago = (int)(Now.Date - start.Date).TotalDays;
            if (ago >= 2) a.Paragraphs.Add($"That was {ago} days ago.");
            a.Paragraphs.Add($"In everything recorded: {Dur(total)}.");
            Used = AskTime.Day(start, Now);
            a.Links[0] = new("Open Reports", "reports", start.Date);
            a.FollowUps.Add($"How long did I {Verb(category, past: false)} {app} this month?");
            a.FollowUps.Add($"How hot did {app} make my PC {Used.Label}?");
            return a;
        }

        var period = PeriodOr(() => AskTime.LastDays(AppUseDays, Now));
        var r = ReportFor(period);
        var stat = r.Apps.FirstOrDefault(x => x.Id == appId);
        if (stat is null || stat.ActiveSec < 60)
        {
            a.Lead = $"You didn't {Verb(category, past: false)} {app} {period.Label}.";
            if (lastSession is not null)
            {
                var start = TimeUtil.FromUnix(lastSession.Start);
                a.Paragraphs.Add($"The last time was {DayWord(start)}, for {Dur(lastSession.ActiveSec)}.");
            }
            return a;
        }

        a.Lead = $"You {Verb(category)} **{app}** for **{Dur(stat.ActiveSec)}** {period.Label}.";
        var more = new List<string>();
        if (stat.SessionCount > 1) more.Add($"{stat.SessionCount} sessions, the longest {Dur(stat.LongestSessionSec)}");
        if (r.ActiveSec > 0 && !period.IsDay && stat.ActiveSec / r.ActiveSec >= 0.01) more.Add($"{stat.ActiveSec / r.ActiveSec:P0} of your time on the PC");
        if (more.Count > 0) a.Paragraphs.Add($"{Join(more).ToUpperFirst()}.");
        if (stat.BackgroundSec + stat.MinimizedSec >= 3600 && stat.BackgroundSec + stat.MinimizedSec > stat.ActiveSec)
            a.Paragraphs.Add($"It was also open in the background for {Dur(stat.BackgroundSec + stat.MinimizedSec)}.");
        if (period.Range != ReportRange.All && total > stat.ActiveSec * 1.05) a.Paragraphs.Add($"In everything recorded: {Dur(total)}.");
        a.FollowUps.Add($"When did I last {Verb(category, past: false)} {app}?");
        if (category == AppCategory.Game) a.FollowUps.Add($"How hot does {app} make my PC?");
        a.FollowUps.Add($"What did I use most {period.Label}?");
        return a;
    }

    private AskAnswer TopApps()
    {
        var period = PeriodOr(() => AskTime.Week(Now, Now));
        var r = ReportFor(period);
        if (NothingRecorded(period, r) is { } none) return none;
        var a = new AskAnswer();
        a.Links.Add(new("Open Apps", "apps"));
        bool gamesOnly = _q.Games;
        var kind = KindAskedFor(_q.Text.ToLowerInvariant());
        var apps = r.Apps.Where(x => x.ActiveSec >= 60 && (!gamesOnly || x.Category == AppCategory.Game) && (kind is null || x.Category == kind.Value.Category))
            .OrderByDescending(x => x.ActiveSec).ToList();
        if (kind is { } k)
        {
            // "My most used browser": the same list, of that kind only.
            if (apps.Count == 0) a.Lead = $"No {k.Word} was in use for a minute or more {period.Label}.";
            else
            {
                a.Lead = $"Your most used {k.Word} {period.Label} was **{apps[0].Name}**: {Dur(apps[0].ActiveSec)}.";
                foreach (var x in apps.Skip(1).Take(4)) a.Points.Add(new($"**{x.Name}**: {Dur(x.ActiveSec)}"));
                if (a.Points.Count > 0) a.PointsTitle = "Then";
            }
            return a;
        }
        if (apps.Count == 0)
        {
            a.Lead = gamesOnly ? $"No games were played {period.Label}." : $"No app was in use for a minute or more {period.Label}.";
            return a;
        }
        double all = gamesOnly ? apps.Sum(x => x.ActiveSec) : r.ActiveSec;
        var top = apps[0];
        a.Lead = apps.Count == 1
            ? $"Just **{top.Name}** {period.Label}: {Dur(top.ActiveSec)}."
            : $"**{top.Name}** took the most of your time {period.Label}: {Dur(top.ActiveSec)} of {Dur(all)}{(gamesOnly ? " of play" : "")}.";
        if (apps.Count > 1)
            foreach (var x in apps.Take(5))
                a.Points.Add(new($"**{x.Name}**: {Dur(x.ActiveSec)}{(gamesOnly ? "" : $" · {AppCatalogLabel(x.Category)}")}"));
        if (!gamesOnly)
        {
            var byKind = r.ActiveByCategory.Where(k => k.Value >= 60).OrderByDescending(k => k.Value).Take(3).Select(k => $"{AppCatalogLabel(k.Key).ToLowerInvariant()} {Dur(k.Value)}").ToList();
            if (byKind.Count > 1) a.Paragraphs.Add($"By kind: {Join(byKind)}.");
        }
        a.FollowUps.Add($"How long did I {Verb(top.Category, past: false)} {top.Name} this month?");
        a.FollowUps.Add($"How long was I on my PC {period.Label}?");
        return a;
    }

    /// <summary>A kind of app named in the question ("browser", "launcher"), for a list of that kind only.</summary>
    private static (AppCategory Category, string Word)? KindAskedFor(string lower) =>
        lower.Contains("browser") ? (AppCategory.Browser, "browser")
        : lower.Contains("launcher") ? (AppCategory.Launcher, "launcher")
        : KindWords2().Match(lower) is { Success: true } m ? m.Value switch
        {
            "chat app" or "messenger" or "chat" => (AppCategory.Communication, "chat app"),
            "media player" or "music app" or "video player" => (AppCategory.Media, "media app"),
            _ => (AppCategory.Development, "coding tool"),
        }
        : null;

    [GeneratedRegex(@"\b(chat app|messenger|chat|media player|music app|video player|coding tool|code editor|ide)\b")]
    private static partial Regex KindWords2();

    /// <summary>"When did the records start", "how far back do you go".</summary>
    private AskAnswer RecordsStart()
    {
        var a = new AskAnswer();
        if (FirstDay is not { } first)
        {
            a.Lead = "Nothing has been recorded yet.";
            return a;
        }
        int days = (int)(Now.Date - first).TotalDays;
        a.Lead = $"The records start on **{AskTime.DayText(first, Now)}**{(days >= 1 ? $", {Count(days, "day", "days")} ago" : "")}.";
        if (_db.FirstCrashTime() is long crash && TimeUtil.FromUnix(crash).Date < first)
            a.Paragraphs.Add($"Crashes go further back, to {AskTime.DayText(TimeUtil.FromUnix(crash), Now)}: Windows keeps its own log of those.");
        a.FollowUps.Add("How many days have I used my PC?");
        a.FollowUps.Add("What did I use most in everything recorded?");
        return a;
    }

    /// <summary>"How many days did I use my PC": the days it was really used (five minutes or more), and the time in all.</summary>
    private AskAnswer DaysUsed()
    {
        var period = PeriodOr(() => AskTime.All(Now));
        var days = (_db.GetSystemDays(TimeUtil.ToUnix(period.From.Date), TimeUtil.ToUnix(period.To)) ?? []).Where(d => d.ActiveSec >= 300).ToList();
        var a = new AskAnswer();
        a.Links.Add(new("Open Reports", "reports"));
        if (days.Count == 0)
        {
            a.Lead = $"The PC wasn't used {period.Label}, as far as the records show.";
            return a;
        }
        a.Lead = $"You used your PC on **{Count(days.Count, "day", "days")}** {period.Label}: {Dur(days.Sum(d => d.ActiveSec))} in all, {Dur(days.Average(d => d.ActiveSec))} a day.";
        if (FirstDay is { } first && period.From < first) a.Note = $"The records start on {AskTime.DayText(first, Now)}.";
        a.FollowUps.Add($"Which day was I on my PC the most {period.Label}?");
        return a;
    }

    /// <summary>"Which day did I play the most", "my busiest day": the day with the most use (or play) in a period.</summary>
    private AskAnswer BusiestDay()
    {
        var period = PeriodOr(() => AskTime.LastDays(30, Now));
        var a = new AskAnswer();
        var r = ReportFor(period);
        // Daily bars only: a year's report is in months, and a single day has no days to pick from.
        var days = r.Days.Count > 1 && (r.Days[1].Day - r.Days[0].Day).TotalHours is > 20 and < 28 ? r.Days : [];
        double Of(DayBucket d) => _q.Games ? d.ActiveByCategory.GetValueOrDefault(AppCategory.Game) : d.ActiveSec;
        var best = days.Where(d => Of(d) >= 60).MaxBy(Of);
        if (best is null)
        {
            a.Lead = days.Count == 0 ? "I can pick out a day within a week, a month, or up to about three months. Ask about one of those."
                : _q.Games ? $"No games were played {period.Label}." : $"The PC wasn't used {period.Label}.";
            return a;
        }
        a.Links.Add(new("Open Reports", "reports", best.Day));
        a.Lead = _q.Games
            ? $"You played most on **{AskTime.DayText(best.Day, Now)}** {period.Label}: {Dur(Of(best))} of play."
            : $"Your busiest day {period.Label} was **{AskTime.DayText(best.Day, Now)}**: {Dur(Of(best))} in use{(best.TopApp is { } top ? $", mostly {top}" : "")}.";
        foreach (var d in days.Where(d => d != best && Of(d) >= 60).OrderByDescending(Of).Take(3))
            a.Points.Add(new($"**{AskTime.DayText(d.Day, Now)}**: {Dur(Of(d))}"));
        if (a.Points.Count > 0) a.PointsTitle = "Then";
        a.FollowUps.Add($"What did I use most {AskTime.Day(best.Day, Now).Label}?");
        return a;
    }

    /// <summary>"My longest session", "longest gaming session ever": the longest unbroken stretches in one app.</summary>
    private AskAnswer LongestSession()
    {
        var period = PeriodOr(() => AskTime.All(Now));
        var a = new AskAnswer();
        var sessions = _db.GetLongestSessions(TimeUtil.ToUnix(period.From), TimeUtil.ToUnix(period.To), ReportBuilder.MinSessionSec, 40)
            .Where(x => _apps.ContainsKey(x.AppId) && (!_q.Games || x.IsGame || CategoryOf(_apps[x.AppId]) == AppCategory.Game)).Take(4).ToList();
        if (sessions.Count == 0)
        {
            a.Lead = _q.Games ? $"No game sessions are on record {period.Label}." : $"No sessions are on record {period.Label}.";
            return a;
        }
        var s = sessions[0];
        var start = TimeUtil.FromUnix(s.Start);
        a.Links.Add(new("Open Reports", "reports", start.Date));
        a.Lead = $"Your longest {(_q.Games ? "gaming " : "")}session {period.Label} was **{NameOf(s.AppId)}**: **{Dur(s.ActiveSec)}**, {DayWord(start)} from {Clock(start)}.";
        foreach (var x in sessions.Skip(1))
            a.Points.Add(new($"**{NameOf(x.AppId)}**: {Dur(x.ActiveSec)}, {DayWord(TimeUtil.FromUnix(x.Start))}"));
        if (a.Points.Count > 0) a.PointsTitle = "Then";
        a.Note = "A session is one stretch in one app; a break away from the PC ends it.";
        return a;
    }

    /// <summary>Days looked at for a habit, and the days of use among them it takes to call something usual.</summary>
    private const int HabitDays = 21, HabitMinDays = 10;

    /// <summary>
    /// "When do I usually play", "what time do I usually start", "how long am I usually on": what the last three weeks
    /// show, said with its count ("on 14 of the last 21 days"). Under ten days of use it isn't a habit yet, and that is said.
    /// </summary>
    private AskAnswer Usual(string asked)
    {
        var a = new AskAnswer();
        a.Links.Add(new("Open Reports", "reports"));
        var from = Now.Date.AddDays(-HabitDays);
        var minutes = _db.GetMinutes(TimeUtil.ToUnix(from), TimeUtil.ToUnix(Now.Date)).Where(m => m.ActiveSec >= 30).ToList();
        bool IsGame(SystemMinute m) => m.FgApp is long id && _apps.TryGetValue(id, out var row) && CategoryOf(row) == AppCategory.Game;
        bool games = _q.Games || asked.Contains("play") || asked.Contains("gam");
        var counted = games ? [.. minutes.Where(IsGame)] : minutes;
        var days = counted.GroupBy(m => TimeUtil.FromUnix(m.Ts).Date).ToList();
        string what = games ? "played" : "used the PC";
        if (days.Count < HabitMinDays)
        {
            a.Lead = $"Not enough days yet to call it a habit: you {what} on {days.Count} of the last {HabitDays} days, and I'd want {HabitMinDays}.";
            a.Understood = false;
            a.FollowUps.Add(games ? "How much did I play this week?" : "How long was I on my PC this week?");
            return a;
        }
        string of = $"on {days.Count} of the last {HabitDays} days";

        // "What time do I usually start": the middle one of each day's first minute (from 5 AM: before that is the night before).
        if (StartWords().IsMatch(asked))
        {
            var starts = days.Select(g => g.Select(m => TimeUtil.FromUnix(m.Ts)).Where(t => t.Hour >= 5).Select(t => t.TimeOfDay).DefaultIfEmpty(TimeSpan.MaxValue).Min())
                .Where(t => t != TimeSpan.MaxValue).OrderBy(t => t).ToList();
            var (early, mid, late) = (starts[starts.Count / 4], starts[starts.Count / 2], starts[starts.Count * 3 / 4]);
            string T(TimeSpan t) => Clock(Now.Date + t);
            a.Lead = $"You usually start {(games ? "playing " : "")}around **{T(mid)}**: {of} you {what}, and on half of them the first minute fell between {T(early)} and {T(late)}.";
            return a;
        }

        // "How long am I usually on": the middle day.
        if (HowLongWords().IsMatch(asked))
        {
            var lengths = days.Select(g => g.Sum(m => (double)m.ActiveSec)).OrderBy(x => x).ToList();
            a.Lead = $"On a usual day you {what.Replace("used the PC", "use the PC").Replace("played", "play")} for about **{Dur(lengths[lengths.Count / 2])}**: {of} you did, from {Dur(lengths[lengths.Count / 4])} on a light day to {Dur(lengths[lengths.Count * 3 / 4])} on a heavy one.";
            return a;
        }

        // "When do I usually play": the hours of the day that hold most of it.
        var byHour = new double[24];
        foreach (var m in counted) byHour[TimeUtil.FromUnix(m.Ts).Hour] += m.ActiveSec;
        double total = byHour.Sum();
        // The shortest run of hours (wrapping past midnight) that holds at least 70% of the time.
        (int Start, int Length) best = (0, 24);
        for (int start = 0; start < 24; start++)
        {
            double sum = 0;
            for (int length = 1; length <= 24; length++)
            {
                sum += byHour[(start + length - 1) % 24];
                if (sum >= total * 0.7)
                {
                    if (length < best.Length) best = (start, length);
                    break;
                }
            }
        }
        string H(int hour) => Clock(Now.Date.AddHours(hour % 24)).Replace(":00", "");
        double share = Enumerable.Range(best.Start, best.Length).Sum(h => byHour[h % 24]) / total;
        a.Lead = $"Mostly between **{H(best.Start)} and {H(best.Start + best.Length)}**: {share:P0} of the time you {what} in the last {HabitDays} days falls in those hours.";
        var weekdays = days.GroupBy(g => g.Key.DayOfWeek).Select(g => (Day: g.Key, Avg: g.Average(d => d.Sum(m => (double)m.ActiveSec)), N: g.Count())).Where(x => x.N >= 2).OrderByDescending(x => x.Avg).ToList();
        if (weekdays.Count >= 3) a.Paragraphs.Add($"{weekdays[0].Day}s are the longest ({Dur(weekdays[0].Avg)} on average), {weekdays[^1].Day}s the shortest ({Dur(weekdays[^1].Avg)}).");
        a.Note = $"From {of} with {(games ? "play" : "use")}. A habit is only what those days show.";
        return a;
    }

    [GeneratedRegex(@"\b(start\w*|begin|turn(ed)? on|get on|sit down|wake)\b")]
    private static partial Regex StartWords();

    [GeneratedRegex(@"\bhow (long|much|many hours)\b")]
    private static partial Regex HowLongWords();

    /// <summary>"Did I leave my PC on overnight", "was it on while I was away": the time it was on with nobody at it.</summary>
    private AskAnswer LeftOn(string asked)
    {
        var period = PeriodOr(() => asked.Contains("night") ? AskTime.PartOfDay(Now.Date.AddDays(-1), "night", Now) : Today);
        var r = ReportFor(period);
        var a = new AskAnswer();
        a.Links.Add(new("Open Reports", "reports", period.From.Date));
        if (!r.HasData || r.OnSec <= 0)
        {
            a.Lead = $"No: the PC wasn't on {period.Label}.";
            return a;
        }
        double away = Math.Max(0, r.OnSec - r.ActiveSec);
        a.Lead = away < 300
            ? $"Hardly: the PC was on for {Dur(r.OnSec)} {period.Label}, and you were at it nearly all that time."
            : $"The PC was on for **{Dur(r.OnSec)}** {period.Label}, and **{Dur(away)}** of that with nobody at it.";
        if (r.LongAwaySec >= 1800) a.Paragraphs.Add($"{Dur(r.LongAwaySec)} of it was in stretches of half an hour or more with nothing working hard: left on, doing nothing.");
        a.Facts.Add(new("PC on", Dur(r.OnSec)));
        a.Facts.Add(new("In use", Dur(r.ActiveSec)));
        a.Facts.Add(new("Nobody there", Dur(away)));
        return a;
    }

    [GeneratedRegex(@"\b(overnight|left (it|my pc|the pc|the computer)? ?(on|running)|leave (it|my pc|the pc|the computer) (on|running)|while i was (away|out|asleep|sleeping|gone)|unattended|nobody (was )?(there|at it)|idle time|sitting idle)\b")]
    private static partial Regex AwayWords();

    [GeneratedRegex(@"\b(usually|typically|normally|on a (normal|typical|usual) day|habit\w*|routine|tend to|most often|on average do)\b")]
    private static partial Regex HabitWords();

    [GeneratedRegex(@"\blongest\b.*\b(session|stretch|sitting|run|streak)\b|\b(session|stretch)\b.*\blongest\b")]
    private static partial Regex LongestWords();

    [GeneratedRegex(@"\b(which|what) (day|date)\b|\b(busiest|most active) day\b|\bday\b.*\bthe most\b")]
    private static partial Regex WhichDayWords();

    [GeneratedRegex(@"\bhow many days\b")]
    private static partial Regex HowManyDaysWords();

    [GeneratedRegex(@"\b(first (use|used|started|turned|time)|records? (start|begin|go back)|how far back|since when|start(ed)? (recording|tracking))\b")]
    private static partial Regex SinceWhenWords();

    private static string AppCatalogLabel(AppCategory c) => Apps.AppCatalog.Label(c);

    [GeneratedRegex(@"\b(start\w*|turn(ed)? on|switch(ed)? on|boot\w*|first|began|begin|until|how late|stop\w*|last on|log(ged)? (on|off)|what time|when did i)\b")]
    private static partial Regex ClockWords();
}
