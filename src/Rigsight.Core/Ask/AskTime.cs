using System.Globalization;
using System.Text.RegularExpressions;
using Rigsight.Core.Reports;

namespace Rigsight.Core.Ask;

/// <summary>
/// Finds the time a question is about ("yesterday", "last Friday", "in September", "since Monday", "3 days ago") and
/// takes those words out of it, so what's left can be matched against app names without "last" or "may" getting in the way.
/// </summary>
public static partial class AskTime
{
    private static readonly string[] Months =
        ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"];
    private static readonly string[] Weekdays = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    private const string MonthWords = "january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|jun|jul|aug|sept|sep|oct|nov|dec";
    private const string DayWords = "sunday|monday|tuesday|wednesday|thursday|friday|saturday|sun|mon|tues|tue|wed|thurs|thur|thu|fri|sat";
    private const string Count = @"(\d{1,4}|a|an|one|two|three|four|five|six|seven|eight|nine|ten|couple of|few)";

    /// <summary>The period named in <paramref name="text"/> (null when none is), and the text without those words.</summary>
    public static (AskPeriod? Period, string Left) Parse(string text, DateTime now)
    {
        // A time of day ("at 3pm", "at 9:30 yesterday", "around noon"): that hour, on the day named with it or today.
        if (ClockTime().Match(text.ToLowerInvariant()) is { Success: true } clock && HourOf(clock) is int hour)
        {
            string without = Spaces().Replace(text.Remove(clock.Index, clock.Length), " ").Trim();
            var (day, left) = ParseDay(without, now);
            if (day is null || day.Range == ReportRange.Day)
            {
                var date = day?.From ?? now.Date;
                // "at 8pm" asked at 3pm, with no day: the last time it was 8pm.
                if (day is null && date.AddHours(hour) > now) date = date.AddDays(-1);
                var from = date.AddHours(hour);
                string when = date == now.Date ? "today" : date == now.Date.AddDays(-1) ? "yesterday" : "on " + DayText(date, now);
                return (new AskPeriod(from, from.AddHours(1), ReportRange.Custom, $"around {from.ToString("h tt", CultureInfo.InvariantCulture)} {when}"), left);
            }
        }
        return ParseDay(text, now);
    }

    /// <summary>The hour of a clock time (0 to 23), or null when it isn't one ("at 30").</summary>
    private static int? HourOf(Match m)
    {
        if (m.Groups["word"].Success) return m.Groups["word"].Value is "midnight" ? 0 : 12;
        int hour = int.Parse(m.Groups["h"].Value);
        string half = m.Groups["half"].Value.Replace(".", "");
        if (half.Length > 0)
        {
            if (hour is < 1 or > 12) return null;
            return half == "pm" ? hour % 12 + 12 : hour % 12;
        }
        return hour <= 23 ? hour : null;
    }

    // With "am" or "pm", or with minutes after "at": a bare "at 3" is too often something else.
    [GeneratedRegex(@"\b(?:at |around |about |by )?(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<half>am|pm|a\.m\.|p\.m\.)(?![a-z])|\b(?:at|around|about) (?<h>\d{1,2}):(?<m>\d{2})\b|\b(?:at |around |about )?(?<word>noon|midnight|midday)\b")]
    private static partial Regex ClockTime();

    private static (AskPeriod? Period, string Left) ParseDay(string text, DateTime now)
    {
        string lower = text.ToLowerInvariant();
        // "from Monday to Wednesday", "between 28 Sep and 2 Oct": from the start of the first to the end of the second.
        if (Span().Match(lower) is { Success: true } span
            && ParseDay(text.Substring(span.Groups["a"].Index, span.Groups["a"].Length), now) is { Period: { IsReal: true } first, Left: var leftA } && Bits(leftA) == 0
            && ParseDay(text.Substring(span.Groups["b"].Index, span.Groups["b"].Length), now) is { Period: { IsReal: true } second, Left: var leftB } && Bits(leftB) == 0
            && (second.To > first.From || (first.IsDay && second.IsDay)))
        {
            // "from Monday to Wednesday" asked on a Monday: the Monday before, not today.
            if (second.To <= first.From) first = Day(first.From.AddDays(-7 * Math.Ceiling((first.From - second.From).TotalDays / 7.0)), now);
            string rest = Spaces().Replace(text.Remove(span.Index, span.Length), " ").Trim();
            return (new AskPeriod(first.From, second.To, ReportRange.Custom, $"from {Bare(first.Label)} to {Bare(second.Label)}"), rest);
        }
        foreach (var (regex, make) in Rules)
        {
            var m = regex.Match(lower);
            if (!m.Success) continue;
            if (make(m, now) is not { } period) continue;

            int start = m.Index;
            // "since Monday", "since last week": from then until now.
            var since = Since().Match(lower[..start]);
            if (since.Success)
            {
                start = since.Index;
                var end = now.Date.AddDays(1);
                if (period.From < end) period = new AskPeriod(period.From, end, ReportRange.Custom, "since " + Bare(period.Label));
            }
            else
            {
                // "on friday", "in september", "during last week": the little word goes with it.
                var lead = Lead().Match(lower[..start]);
                if (lead.Success) start = lead.Index;
            }
            string rest = (text[..start] + " " + text[(m.Index + m.Length)..]).Trim();
            return (period, Spaces().Replace(rest, " "));
        }
        return (null, text);
    }

    /// <summary>
    /// The two times of a comparison ("was it hotter this week than last week", "more than last month"): the one asked
    /// about and the one it is set against, and the text without either. With only the second named after "than", the
    /// first is the same unit as it is now (today against a day, this week against a week). Null when nothing is compared.
    /// </summary>
    public static (AskPeriod Subject, AskPeriod Against, string Left)? ParseTwo(string text, DateTime now)
    {
        string lower = text.ToLowerInvariant();
        var than = Than().Match(lower);
        var (first, rest) = Parse(text, now);
        if (first is null) return null;
        var (second, rest2) = Parse(rest, now);

        if (second is not null)
        {
            if (!than.Success && !Both().IsMatch(lower)) return null;
            // The one after "than" is what it's set against; without a "than", the earlier of the two is.
            var after = than.Success ? Parse(text[(than.Index + than.Length)..], now).Period : null;
            var against = after is not null && (Same(after, first) || Same(after, second)) ? after : first.From <= second.From ? first : second;
            var subject = Same(against, first) ? second : first;
            return Same(subject, against) ? null : (subject, against, rest2);
        }

        // "…last week than the week before": the same unit, one step back.
        if (than.Success && Before().Match(lower[(than.Index + than.Length)..]) is { Success: true } step)
        {
            AskPeriod? earlier = first.Range switch
            {
                ReportRange.Day => Day(first.From.AddDays(-1), now),
                ReportRange.Week => Week(first.From.AddDays(-7), now),
                ReportRange.Month => Month(first.From.AddMonths(-1), now),
                ReportRange.Year => Year(first.From.Year - 1, now),
                _ => null,
            };
            if (earlier is null) return null;
            int at = lower.IndexOf(step.Value, than.Index, StringComparison.Ordinal);
            // The text without "than the week before" as well (the time itself is already out of rest).
            string left = Spaces().Replace(Regex.Replace(rest, Regex.Escape(text.Substring(than.Index, at + step.Length - than.Index)), " ", RegexOptions.IgnoreCase), " ").Trim();
            return (first, earlier, left);
        }

        if (!than.Success || Parse(text[(than.Index + than.Length)..], now).Period is null) return null;
        AskPeriod? current = first.Range switch
        {
            ReportRange.Day => Day(now, now),
            ReportRange.Week => Week(now, now),
            ReportRange.Month => Month(now, now),
            ReportRange.Year => Year(now.Year, now),
            _ => null,
        };
        return current is null || Same(current, first) ? null : (current, first, rest);

        static bool Same(AskPeriod a, AskPeriod b) => a.From == b.From && a.To == b.To;
    }

    [GeneratedRegex(@"\b(than|vs\.?|versus|compared (?:to|with)|against)\b")]
    private static partial Regex Than();

    [GeneratedRegex(@"^\s*(?:the |in the |on the )?(?:(?:day|week|month|year|one|time) before(?: that| it)?|previous (?:day|week|month|year|one)|(?:day|week|month|year) earlier)\s*[?.!]*$")]
    private static partial Regex Before();

    [GeneratedRegex(@"\b(compare|or|and|between|difference)\b")]
    private static partial Regex Both();

    /// <summary>A label without its leading "on" or "in" ("on Fri 2 Oct" after "since" is "since Fri 2 Oct").</summary>
    private static string Bare(string label) => label.StartsWith("on ", StringComparison.Ordinal) || label.StartsWith("in ", StringComparison.Ordinal) ? label[3..] : label;

    // ── The periods themselves ──────────────────────────────────────────

    public static AskPeriod Day(DateTime day, DateTime now)
    {
        day = day.Date;
        string label = day == now.Date ? "today" : day == now.Date.AddDays(-1) ? "yesterday" : "on " + DayText(day, now);
        return new AskPeriod(day, day.AddDays(1), ReportRange.Day, label);
    }

    /// <summary>"Fri 2 Oct", with the year when it isn't this one.</summary>
    public static string DayText(DateTime day, DateTime now) => day.ToString(day.Year == now.Year ? "ddd d MMM" : "ddd d MMM yyyy", CultureInfo.InvariantCulture);

    public static AskPeriod Week(DateTime anchor, DateTime now)
    {
        var (from, to) = ReportBuilder.Bounds(ReportRange.Week, anchor);
        var (thisFrom, _) = ReportBuilder.Bounds(ReportRange.Week, now);
        string label = from == thisFrom ? "this week" : from == thisFrom.AddDays(-7) ? "last week" : $"in the week of {from.ToString("d MMM", CultureInfo.InvariantCulture)}";
        return new AskPeriod(from, to, ReportRange.Week, label);
    }

    public static AskPeriod Month(DateTime anchor, DateTime now)
    {
        var from = new DateTime(anchor.Year, anchor.Month, 1);
        var thisFrom = new DateTime(now.Year, now.Month, 1);
        string label = from == thisFrom ? "this month" : from == thisFrom.AddMonths(-1) ? "last month"
            : "in " + from.ToString(from.Year == now.Year ? "MMMM" : "MMMM yyyy", CultureInfo.InvariantCulture);
        return new AskPeriod(from, from.AddMonths(1), ReportRange.Month, label);
    }

    public static AskPeriod Year(int year, DateTime now) =>
        new(new DateTime(year, 1, 1), new DateTime(year + 1, 1, 1), ReportRange.Year, year == now.Year ? "this year" : year == now.Year - 1 ? "last year" : $"in {year}");

    /// <summary>The days up to and including today (<paramref name="days"/> whole ones before it).</summary>
    public static AskPeriod LastDays(int days, DateTime now) => days <= 0 ? Day(now, now) :
        new(now.Date.AddDays(-days), now.Date.AddDays(1), ReportRange.Custom, days == 1 ? "since yesterday" : $"in the last {days} days");

    public static AskPeriod All(DateTime now) => new(new DateTime(2000, 1, 1), now.Date.AddDays(1), ReportRange.All, "in everything recorded");

    /// <summary>A date that isn't on the calendar ("31 Feb", "the 31st" of a 30-day month): kept, so the answer can say so.</summary>
    private static AskPeriod NoSuchDate(string typed) => new(DateTime.MinValue, DateTime.MinValue, ReportRange.Custom, typed.Trim());

    private static AskPeriod Hours(DateTime from, DateTime to, string label) => new(from, to, ReportRange.Custom, label);

    public static AskPeriod PartOfDay(DateTime day, string part, DateTime now)
    {
        day = day.Date;
        // "this morning", "yesterday evening", "on the morning of Wed 30 Sep".
        string Named(string of) => day == now.Date ? $"this {of}" : day == now.Date.AddDays(-1) ? $"yesterday {of}" : $"on the {of} of {DayText(day, now)}";
        return part switch
        {
            "morning" => Hours(day.AddHours(5), day.AddHours(12), Named("morning")),
            "afternoon" => Hours(day.AddHours(12), day.AddHours(18), Named("afternoon")),
            "evening" => Hours(day.AddHours(18), day.AddHours(24), Named("evening")),
            // A night runs into the small hours of the next day.
            _ => Hours(day.AddHours(18), day.AddHours(30), day == now.Date ? "tonight" : day == now.Date.AddDays(-1) ? "last night" : Named("night")),
        };
    }

    private static int Number(string word) => word switch
    {
        "a" or "an" or "one" => 1, "two" or "couple of" => 2, "three" or "few" => 3, "four" => 4, "five" => 5, "six" => 6, "seven" => 7,
        "eight" => 8, "nine" => 9, "ten" => 10,
        _ => int.TryParse(word, out int n) ? n : 1,
    };

    private static int MonthOf(string word) => Array.FindIndex(Months, m => m.StartsWith(word[..3], StringComparison.Ordinal)) + 1;

    private static DayOfWeek WeekdayOf(string word) => (DayOfWeek)Array.FindIndex(Weekdays, d => d.StartsWith(word[..3], StringComparison.Ordinal));

    /// <summary>The most recent such weekday: today when it is one (unless "last" was said), else the one before.</summary>
    private static DateTime RecentWeekday(DayOfWeek day, DateTime now, bool last)
    {
        int back = ((int)now.DayOfWeek - (int)day + 7) % 7;
        if (back == 0 && last) back = 7;
        return now.Date.AddDays(-back);
    }

    /// <summary>A day and month without a year: the most recent one that isn't in the future.</summary>
    private static DateTime? RecentDate(int day, int month, int? year, DateTime now)
    {
        int y = year ?? now.Year;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(y, month)) return null;
        var date = new DateTime(y, month, day);
        if (year is null && date > now.Date) date = day <= DateTime.DaysInMonth(y - 1, month) ? new DateTime(y - 1, month, day) : date;
        return date;
    }

    private static readonly (Regex Regex, Func<Match, DateTime, AskPeriod?> Make)[] Rules =
    [
        // What hasn't happened yet is still a time: the answer is that it hasn't.
        (R(@"\b(?:the )?day after tomorrow\b"), (_, now) => Day(now.Date.AddDays(2), now)),
        (R(@"\btomorrow\b"), (_, now) => Day(now.Date.AddDays(1), now)),
        (R(@"\b(?:next|the next|the coming|coming) week\b"), (_, now) => Week(now.Date.AddDays(7), now)),
        (R(@"\b(?:next|the next|the coming|coming) month\b"), (_, now) => Month(now.Date.AddMonths(1), now)),
        (R(@"\b(?:next|the next|the coming|coming) year\b"), (_, now) => Year(now.Year + 1, now)),

        // Dates in figures, read the way this PC writes its dates (day first, or month first).
        (R(@"\b(\d{1,2})[/.](\d{1,2})(?:[/.]((?:19|20)?\d\d))?\b(?!\s*(?:gb|mb|ghz|mhz|fps|%|degrees|°))"), (m, now) =>
        {
            int a = int.Parse(m.Groups[1].Value), b = int.Parse(m.Groups[2].Value);
            int? year = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) is var y && y < 100 ? 2000 + y : int.Parse(m.Groups[3].Value) : null;
            bool monthFirst = CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern.TrimStart().StartsWith('M');
            var (usual, other) = monthFirst ? (RecentDate(b, a, year, now), RecentDate(a, b, year, now)) : (RecentDate(a, b, year, now), RecentDate(b, a, year, now));
            // Read the way this PC writes dates, unless only the other way round gives a recent day: questions here are about the last weeks.
            bool Recent(DateTime? d) => d is { } day && day <= now.Date && day >= now.Date.AddDays(-60);
            var date = usual is not null && (Recent(usual) || !Recent(other)) ? usual : other ?? usual;
            return date is { } d ? Day(d, now) : NoSuchDate(m.Value);
        }),

        // Years counted.
        (R($@"\b(?:in |over |during |for |within )?(?:the )?(?:last|past|previous) {Count} years?\b"), (m, now) =>
            Number(m.Groups[1].Value) is var n ? new AskPeriod(now.Date.AddYears(-n), now.Date.AddDays(1), ReportRange.Custom, n == 1 ? "in the last year" : $"in the last {n} years") : null),
        (R($@"\b{Count} years? ago\b"), (m, now) => Year(now.Year - Number(m.Groups[1].Value), now)),

        // Parts of a day first: "yesterday evening" isn't all of yesterday.
        (R(@"\b(?:the )?day before yesterday\b"), (_, now) => Day(now.Date.AddDays(-2), now)),
        (R(@"\byesterday (morning|afternoon|evening|night)\b"), (m, now) => PartOfDay(now.Date.AddDays(-1), m.Groups[1].Value, now)),
        (R(@"\blast night\b"), (_, now) => PartOfDay(now.Date.AddDays(-1), "night", now)),
        (R(@"\b(?:this|today in the|in the) (morning|afternoon|evening)\b"), (m, now) =>
            // "in the evening" asked at noon: the last evening there was.
            PartOfDay(!m.Value.StartsWith("this", StringComparison.Ordinal) && PartOfDay(now, m.Groups[1].Value, now).From > now ? now.Date.AddDays(-1) : now.Date, m.Groups[1].Value, now)),
        (R(@"\btonight\b"), (_, now) => PartOfDay(now, "night", now)),
        (R($@"\b(?:on |last )?({DayWords}) (morning|afternoon|evening|night)\b"),
            (m, now) => PartOfDay(RecentWeekday(WeekdayOf(m.Groups[1].Value), now, last: false), m.Groups[2].Value, now)),

        // Hours.
        (R($@"\b(?:in |over |during |for )?(?:the )?(?:last|past) {Count} hours?\b"), (m, now) =>
            Number(m.Groups[1].Value) is var n && n == 1 ? Hours(now.AddHours(-1), now, "in the last hour") : Hours(now.AddHours(-n), now, $"in the last {n} hours")),
        (R(@"\b(?:in |over |during )?(?:the )?(?:last|past) hour\b"), (_, now) => Hours(now.AddHours(-1), now, "in the last hour")),
        (R($@"\b{Count} hours? ago\b"), (m, now) => Number(m.Groups[1].Value) is var n ? Hours(now.AddHours(-n - 1), now.AddHours(-n + 1), n == 1 ? "about an hour ago" : $"about {n} hours ago") : null),

        // Counted days, weeks and months.
        (R($@"\b(?:in |over |during |for |within )?(?:the )?(?:last|past|previous) {Count} days?\b"), (m, now) => LastDays(Number(m.Groups[1].Value), now)),
        (R($@"\b(?:in |over |during |for |within )?(?:the )?(?:last|past|previous) {Count} weeks?\b"), (m, now) => LastDays(7 * Number(m.Groups[1].Value), now)),
        (R($@"\b(?:in |over |during |for |within )?(?:the )?(?:last|past|previous) {Count} months?\b"), (m, now) =>
            Number(m.Groups[1].Value) is var n ? new AskPeriod(now.Date.AddMonths(-n), now.Date.AddDays(1), ReportRange.Custom, n == 1 ? "in the last month" : $"in the last {n} months") : null),
        (R($@"\b{Count} days? ago\b"), (m, now) => Day(now.Date.AddDays(-Number(m.Groups[1].Value)), now)),
        (R($@"\b{Count} weeks? ago\b"), (m, now) => Week(now.Date.AddDays(-7 * Number(m.Groups[1].Value)), now)),
        (R($@"\b{Count} months? ago\b"), (m, now) => Month(now.Date.AddMonths(-Number(m.Groups[1].Value)), now)),
        (R(@"\b(?:in |over |during )?(?:the )?(?:last|past) 24 ?h(?:ours|rs)?\b"), (_, now) => Hours(now.AddHours(-24), now, "in the last 24 hours")),

        // Parts of a month: "the first week of October", "the end of September".
        (R($@"\b(?:the )?(first|last|second|third) week (?:of|in) ({MonthWords})\b"), (m, now) =>
        {
            var first = new DateTime(now.Year, MonthOf(m.Groups[2].Value), 1);
            if (first > now) first = first.AddYears(-1);
            int days = DateTime.DaysInMonth(first.Year, first.Month);
            var from = m.Groups[1].Value switch { "first" => first, "second" => first.AddDays(7), "third" => first.AddDays(14), _ => first.AddDays(days - 7) };
            return new AskPeriod(from, from.AddDays(7), ReportRange.Custom, $"in the {m.Groups[1].Value} week of {first.ToString(first.Year == now.Year ? "MMMM" : "MMMM yyyy", CultureInfo.InvariantCulture)}");
        }),
        (R($@"\b(?:the |at the |in the )?(start|beginning|end|middle) of ({MonthWords})\b"), (m, now) =>
        {
            var first = new DateTime(now.Year, MonthOf(m.Groups[2].Value), 1);
            if (first > now) first = first.AddYears(-1);
            int days = DateTime.DaysInMonth(first.Year, first.Month);
            var from = m.Groups[1].Value switch { "start" or "beginning" => first, "middle" => first.AddDays(10), _ => first.AddDays(days - 10) };
            string part = m.Groups[1].Value is "beginning" ? "start" : m.Groups[1].Value;
            return new AskPeriod(from, from.AddDays(10), ReportRange.Custom, $"at the {part} of {first.ToString(first.Year == now.Year ? "MMMM" : "MMMM yyyy", CultureInfo.InvariantCulture)}");
        }),
        // Calendar units.
        (R(@"\b(?:this|the current|current) week\b"), (_, now) => Week(now, now)),
        (R(@"\b(?:last|the past|past|previous|the previous) week\b"), (_, now) => Week(now.Date.AddDays(-7), now)),
        (R(@"\b(?:this|the current|current) month\b"), (_, now) => Month(now, now)),
        (R(@"\b(?:last|the past|past|previous|the previous) month\b"), (_, now) => Month(now.Date.AddMonths(-1), now)),
        (R(@"\b(?:this|the current|current) year\b"), (_, now) => Year(now.Year, now)),
        (R(@"\b(?:last|the past|past|previous|the previous) year\b"), (_, now) => Year(now.Year - 1, now)),
        (R(@"\b(?:last |this |the |over the |at the )?weekend\b"), (_, now) =>
        {
            // The weekend going on, or the last one.
            var saturday = RecentWeekday(DayOfWeek.Saturday, now, last: false);
            return new AskPeriod(saturday, saturday.AddDays(2), ReportRange.Custom, saturday.AddDays(2) > now ? "this weekend" : "last weekend");
        }),

        // Dates: "3 oct", "3rd of october 2025", "oct 3", "2026-10-03".
        (R(@"\b(20\d\d)-(\d{1,2})-(\d{1,2})\b"), (m, now) =>
            RecentDate(int.Parse(m.Groups[3].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[1].Value), now) is { } d ? Day(d, now) : NoSuchDate(m.Value)),
        (R($@"\b(\d{{1,2}})(?:st|nd|rd|th)?(?: of)? ({MonthWords})\.?(?:,? (20\d\d))?\b"), (m, now) =>
            RecentDate(int.Parse(m.Groups[1].Value), MonthOf(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : null, now) is { } d ? Day(d, now) : NoSuchDate(m.Value)),
        (R($@"\b({MonthWords})\.? (\d{{1,2}})(?:st|nd|rd|th)?(?:,? (20\d\d))?\b"), (m, now) =>
            RecentDate(int.Parse(m.Groups[2].Value), MonthOf(m.Groups[1].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : null, now) is { } d ? Day(d, now) : NoSuchDate(m.Value)),
        (R(@"\bthe (\d{1,2})(?:st|nd|rd|th)\b"), (m, now) =>
        {
            // "on the 3rd": this month's, or last month's when that's still to come.
            int day = int.Parse(m.Groups[1].Value);
            var month = new DateTime(now.Year, now.Month, 1);
            if (day > now.Day) month = month.AddMonths(-1);
            return day >= 1 && day <= DateTime.DaysInMonth(month.Year, month.Month) ? Day(month.AddDays(day - 1), now) : NoSuchDate(m.Value);
        }),

        // A month, a year. "may" alone is a verb more often than a month ("why may my PC…"), so it needs "in" or a year.
        (R($@"\b({MonthWords}) ((?:19|20)\d\d)\b"), (m, now) => Month(new DateTime(int.Parse(m.Groups[2].Value), MonthOf(m.Groups[1].Value), 1), now)),
        (R(@"\b(?:in|during|last|this) may\b|\b(january|february|march|april|june|july|august|september|october|november|december|jan|feb|mar|apr|jun|jul|aug|sept|sep|oct|nov|dec)\b"), (m, now) =>
        {
            int month = m.Groups[1].Success ? MonthOf(m.Groups[1].Value) : 5;
            var first = new DateTime(now.Year, month, 1);
            return Month(first > now ? first.AddYears(-1) : first, now);
        }),
        (R(@"\b(?:in|during|of|for|was|about|since|year) ((?:19|20)\d\d)\b"), (m, now) => Year(int.Parse(m.Groups[1].Value), now)),

        // Weekdays: "on friday", "last friday".
        (R($@"\b(last |this |past )?({DayWords})\b"), (m, now) => Day(RecentWeekday(WeekdayOf(m.Groups[2].Value), now, last: m.Groups[1].Value.StartsWith("last")), now)),

        // A part of the day with no day named: the last one there was.
        (R(@"\b(?:at|during the|in the|over) night\b"), (_, now) => PartOfDay(now.Date.AddDays(-1), "night", now)),
        (R(@"\b(?:in|during) the (evening|afternoon|morning)s?\b"), (m, now) =>
            PartOfDay(PartOfDay(now, m.Groups[1].Value, now).From > now ? now.Date.AddDays(-1) : now.Date, m.Groups[1].Value, now)),

        // Loose words.
        (R(@"\byesterday\b"), (_, now) => Day(now.Date.AddDays(-1), now)),
        (R(@"\b(?:today|right now|at the moment|currently|just now|earlier|so far today)\b"), (_, now) => Day(now, now)),
        (R(@"\b(?:lately|recently|these days|of late|nowadays|the other day)\b"), (_, now) => LastDays(7, now)),
        (R(@"\b(?:ever|all time|all-time|of all time|in total|overall|since the beginning|in all)\b"), (_, now) => All(now)),
    ];

    private static Regex R(string pattern) => new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static int Bits(string text) => text.Count(char.IsLetterOrDigit);

    [GeneratedRegex(@"\b(?:from|between)\s+(?<a>[^?!,]+?)\s+(?:to|and|until|till|through|thru)\s+(?<b>[^?!,]+?)(?=\s*(?:[?!,.]|$))")]
    private static partial Regex Span();

    [GeneratedRegex(@"\bsince (?:the )?$")]
    private static partial Regex Since();

    [GeneratedRegex(@"\b(?:on|in|during|over|for|from|at|of) (?:the )?$")]
    private static partial Regex Lead();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
