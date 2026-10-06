using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;

namespace Rigsight.Core.Ask;

internal sealed partial class AskRun
{
    // The bar for saying a part ran hotter in one time than another: the Insights' own (see InsightEngine's drift). The
    // same game at steady heavy load on both sides, an hour of it each, at the same GPU power; and the difference has to
    // outlast what the room did, which the temperatures at rest measure.
    private const int CompareSteadyMinutes = 60, CompareRestMinutes = 30;
    private const double CompareMinDiff = 5, CompareBeyondRoom = 3, CompareSameWithin = 2, ComparePowerShare = 0.15;

    /// <summary>
    /// One time set against another ("this week than last week"). A time still going on is set against the same part of
    /// the other (Monday to Wednesday against last Monday to Wednesday), or it would always come out as less. Null for
    /// what can't be compared yet: the question is then answered for its first time alone.
    /// </summary>
    private AskAnswer? Compare()
    {
        var (a, b) = (_q.Period!, _q.Against!);
        bool partial = a.From <= Now && Now < a.To && a.Range == b.Range && a.Range is ReportRange.Day or ReportRange.Week or ReportRange.Month or ReportRange.Year;
        var fairA = partial ? new AskPeriod(a.From, ReportBuilder.HourEnd(Now), ReportRange.Custom, a.Label) : a;
        var fairB = partial ? new AskPeriod(b.From, b.From + (ReportBuilder.HourEnd(Now) - a.From), ReportRange.Custom, b.Label) : b;
        string soFar = partial ? $"{a.Title} isn't over, so it is set against the same part of {Bare(b.Label)}." : "";

        // A time from before the records can't be set against anything (crashes aside: Windows' own log reaches further back).
        if (_q.Intent != AskIntent.Crashes && FirstDay is { } first && (a.To <= first || b.To <= first))
            return new AskAnswer { Lead = $"I can't set those side by side: nothing is recorded {(b.To <= first ? b.Label : a.Label)}. The records start on {AskTime.DayText(first, Now)}." };

        var answer = _q.Intent switch
        {
            AskIntent.Usage or AskIntent.TopApps or AskIntent.Health => CompareUse(fairA, fairB),
            AskIntent.Crashes => CompareCrashes(fairA, fairB),
            AskIntent.Network => CompareData(fairA, fairB),
            AskIntent.Memory => CompareMemory(fairA, fairB),
            AskIntent.Temps => CompareHeat(a, b), // whole periods: an hour of the same game is needed on each side
            AskIntent.Metric => Metric(fairA, fairB),
            _ => null,
        };
        if (answer is null) return null;
        bool compared = answer.Facts.Count > 0 || answer.Points.Count > 0;
        if (!compared && soFar.Length > 0 && answer.Lead.StartsWith("Nothing to compare", StringComparison.Ordinal)) answer.Paragraphs.Add(soFar);
        if (compared && soFar.Length > 0 && _q.Intent != AskIntent.Temps) answer.Note = answer.Note is null ? soFar : $"{soFar} {answer.Note}";
        if (FirstDay is { } start && b.From < start && compared && _q.Intent != AskIntent.Crashes)
            answer.Note = $"The records start on {AskTime.DayText(start, Now)}, so {Bare(b.Label)} isn't all there.{(answer.Note is null ? "" : " " + answer.Note)}";
        answer.FollowUps.Add(Again(a));
        return answer;
    }

    /// <summary>A difference between two temperatures, in the unit the app shows (a 5° gap in Celsius is 9° in Fahrenheit).</summary>
    private static string Degrees(double celsius) => $"{Math.Abs(Units.Fahrenheit ? celsius * 9 / 5 : celsius):0}°";

    /// <summary>"12% more", "about the same", "3 times as much".</summary>
    private static string Against(double now, double then)
    {
        if (then <= 0) return now > 0 ? "up from nothing" : "the same";
        double ratio = now / then;
        return ratio switch
        {
            >= 0.95 and <= 1.05 => "about the same",
            >= 2 => $"{ratio:0.#} times as much",
            > 1 => $"{ratio - 1:P0} more",
            _ => $"{1 - ratio:P0} less",
        };
    }

    private AskAnswer CompareUse(AskPeriod a, AskPeriod b)
    {
        var (ra, rb) = (ReportFor(a), ReportFor(b));
        var answer = new AskAnswer();
        answer.Links.Add(new("Open Reports", "reports"));
        double x, y;
        string what;
        if (_q.AppId is long appId)
        {
            var category = _apps.TryGetValue(appId, out var row) ? CategoryOf(row) : AppCategory.Other;
            x = ra.Apps.FirstOrDefault(s => s.Id == appId)?.ActiveSec ?? 0;
            y = rb.Apps.FirstOrDefault(s => s.Id == appId)?.ActiveSec ?? 0;
            what = $"You {Verb(category)} {NameOf(appId)}";
        }
        else if (_q.Games)
        {
            (x, y, what) = (ra.GamingSec, rb.GamingSec, "You played");
        }
        else
        {
            (x, y, what) = (ra.ActiveSec, rb.ActiveSec, "You were on your PC");
        }
        if (x < 60 && y < 60)
        {
            answer.Lead = $"Nothing to compare: under a minute {a.Label} and {b.Label} alike.";
            return answer;
        }
        static string Time(double seconds) => seconds < 60 ? "None" : Dur(seconds);
        answer.Lead = x < 60 ? $"**Less {a.Label}**: none at all, against {Dur(y)} {b.Label}."
            : y < 60 ? $"**More {a.Label}**: {what.ToLowerFirstWordAny()} for **{Dur(x)}**, and not at all {b.Label}."
            : Against(x, y) is var how && how == "about the same" ? $"**About the same**: {what.ToLowerFirstWordAny()} for {Dur(x)} {a.Label} and {Dur(y)} {b.Label}."
            : $"**{(x > y ? "More" : "Less")} {a.Label}**: {what.ToLowerFirstWordAny()} for **{Dur(x)}**, against {Dur(y)} {b.Label} ({how}).";
        answer.Facts.Add(new(a.Title, Time(x)));
        answer.Facts.Add(new(b.Title, Time(y)));
        if (_q.AppId is null && !_q.Games && (ra.GamingSec >= 60 || rb.GamingSec >= 60))
            answer.Paragraphs.Add($"Of that, gaming was {Dur(ra.GamingSec)} {a.Label} and {Dur(rb.GamingSec)} {b.Label}.");
        return answer;
    }

    private AskAnswer CompareCrashes(AskPeriod a, AskPeriod b)
    {
        bool Mine(Stability.CrashEvent c) => Severity(c) > 0 && (_q.AppId is not long id || (_apps.TryGetValue(id, out var row) && string.Equals(c.AppExe, row.Exe, StringComparison.OrdinalIgnoreCase)));
        var ca = CrashesIn(a.From, a.To).Where(Mine).ToList();
        var cb = CrashesIn(b.From, b.To).Where(Mine).ToList();
        var answer = new AskAnswer();
        answer.Links.Add(new("Open Crashes", "crashes"));
        string of = _q.App is not null ? $" of {_q.App}" : "";
        answer.Lead = ca.Count == 0 && cb.Count == 0 ? $"**No problems{of} in either**: none {a.Label}, none {b.Label}."
            : ca.Count == cb.Count ? $"**The same number{of}**: {ca.Count} {a.Label} and {b.Label} alike."
            : $"**{(ca.Count > cb.Count ? "More" : "Fewer")} problems{of} {a.Label}**: {ca.Count}, against {cb.Count} {b.Label}.";
        if (_db.FirstCrashTime() is long firstCrash && b.From < TimeUtil.FromUnix(firstCrash).Date)
            answer.Note = $"Problems are on record from {AskTime.DayText(TimeUtil.FromUnix(firstCrash), Now)}, so {Bare(b.Label)} isn't all there.";
        if (ca.Count > 0) answer.Points.Add(new($"**{a.Title}**: {Tally(ca)}", ca.Any(IsPcLevel) ? AskTone.Hot : AskTone.Warn));
        if (cb.Count > 0) answer.Points.Add(new($"**{b.Title}**: {Tally(cb)}", cb.Any(IsPcLevel) ? AskTone.Hot : AskTone.Warn));
        return answer;
    }

    /// <summary>Data down and up over a period: its days where it is whole days, its minutes otherwise.</summary>
    private (long Down, long Up) DataIn(AskPeriod p)
    {
        long from = TimeUtil.ToUnix(p.From), to = TimeUtil.ToUnix(p.To);
        if (p.From.TimeOfDay == TimeSpan.Zero && p.To.TimeOfDay == TimeSpan.Zero)
        {
            var days = _db.GetNetDays(from, to);
            return (days.Sum(d => d.Down), days.Sum(d => d.Up));
        }
        var minutes = _db.GetNetMinutes(from, to);
        return (minutes.Sum(m => m.Down), minutes.Sum(m => m.Up));
    }

    private AskAnswer? CompareData(AskPeriod a, AskPeriod b)
    {
        if (_q.AppId is not null || _db.FirstNetDay() is not long first) return null;
        var answer = new AskAnswer();
        answer.Links.Add(new("Open Network", "network"));
        if (b.To <= TimeUtil.FromUnix(first).AddDays(1) || b.From < TimeUtil.FromUnix(first))
        {
            answer.Lead = $"I can't set those side by side: internet use is on record only from {AskTime.DayText(TimeUtil.FromUnix(first), Now)}, which leaves {Bare(b.Label)} out or cut short.";
            return answer;
        }
        var (x, y) = (DataIn(a), DataIn(b));
        long ta = x.Down + x.Up, tb = y.Down + y.Up;
        answer.Lead = Against(ta, tb) is var how && how == "about the same"
            ? $"**About the same**: {Units.Data(ta)} {a.Label} and {Units.Data(tb)} {b.Label}."
            : $"**{(ta > tb ? "More" : "Less")} data {a.Label}**: **{Units.Data(ta)}**, against {Units.Data(tb)} {b.Label} ({how}).";
        answer.Facts.Add(new($"{a.Title}, down", Units.Data(x.Down)));
        answer.Facts.Add(new($"{b.Title}, down", Units.Data(y.Down)));
        return answer;
    }

    private AskAnswer? CompareMemory(AskPeriod a, AskPeriod b)
    {
        if (_q.AppId is not null) return null;
        double? total = TotalRamGb();
        var ma = SameMemory(MinutesOf(a).Where(m => m.RamUsed is not null).ToList(), total);
        var mb = SameMemory(MinutesOf(b).Where(m => m.RamUsed is not null).ToList(), total);
        var answer = new AskAnswer();
        answer.Links.Add(new("Open Memory", "memory"));
        if (ma.Count == 0 || mb.Count == 0)
        {
            answer.Lead = $"I can't set those side by side: memory use is kept by the minute for about a month, and there is none on record {(ma.Count == 0 ? a.Label : b.Label)}.";
            return answer;
        }
        double pa = ma.Max(m => m.RamUsed!.Value), pb = mb.Max(m => m.RamUsed!.Value), aa = ma.Average(m => m.RamUsed!.Value), ab = mb.Average(m => m.RamUsed!.Value);
        answer.Lead = Math.Abs(aa - ab) < 0.5
            ? $"**About the same**: {aa:0.0} GB in use on average {a.Label}, {ab:0.0} GB {b.Label}."
            : $"**{(aa > ab ? "More" : "Less")} memory in use {a.Label}**: {aa:0.0} GB on average, against {ab:0.0} GB {b.Label}.";
        answer.Paragraphs.Add($"At the most: {pa:0.0} GB {a.Label}, {pb:0.0} GB {b.Label}{(total is double t ? $", of {t:0} GB installed" : "")}.");
        return answer;
    }

    /// <summary>
    /// Whether a part ran hotter in one time than another. Only like for like counts: the same game at steady heavy load
    /// on both sides, with the room's own change (read from the temperatures at rest) taken out. A peak or an average
    /// over everything depends on what was played and how warm the day was, so on their own they are given for
    /// reference and nothing is concluded from them.
    /// </summary>
    private AskAnswer CompareHeat(AskPeriod a, AskPeriod b)
    {
        var (ra, rb) = (ReportFor(a), ReportFor(b));
        bool cpu = _q.Part == AskPart.Cpu;
        string part = cpu ? "CPU" : "GPU";
        var answer = new AskAnswer();
        answer.Links.Add(new("Open Reports", "reports"));

        double? Heat(SteadyLoad s) => cpu ? s.Cpu : s.Gpu;
        var pairs = ra.Steady.Where(s => _q.AppId is not long id || s.AppId == id)
            .Select(s => (Now: s, Then: rb.Steady.FirstOrDefault(o => o.AppId == s.AppId)))
            .Where(p => p.Then is not null && p.Now.Minutes >= CompareSteadyMinutes && p.Then!.Minutes >= CompareSteadyMinutes && Heat(p.Now) is not null && Heat(p.Then) is not null)
            .Where(p => p.Now.GpuPower is not double x || p.Then!.GpuPower is not double y || Math.Abs(x - y) <= y * ComparePowerShare)
            .OrderByDescending(p => Math.Min(p.Now.Minutes, p.Then!.Minutes)).ToList();
        double? restA = cpu ? ra.RestTemps?.Cpu : ra.RestTemps?.Gpu, restB = cpu ? rb.RestTemps?.Cpu : rb.RestTemps?.Gpu;
        bool rest = restA is not null && restB is not null && ra.RestTemps!.Minutes >= CompareRestMinutes && rb.RestTemps!.Minutes >= CompareRestMinutes;

        if (pairs.Count > 0 && rest)
        {
            var (now, then) = (pairs[0].Now, pairs[0].Then!);
            double x = Heat(now)!.Value, y = Heat(then)!.Value, diff = x - y, room = restA!.Value - restB!.Value, beyond = diff - room;
            string at = $"in {now.App} at the same steady load";
            if (Math.Abs(diff) >= CompareMinDiff && Math.Abs(beyond) >= CompareBeyondRoom && Math.Sign(diff) == Math.Sign(beyond))
            {
                answer.Lead = $"**Yes, {(diff > 0 ? "hotter" : "cooler")} {a.Label}**: {at}, the {part} held {Temp(x)}, against {Temp(y)} {b.Label}.";
                answer.Paragraphs.Add(Math.Abs(room) >= 1
                    ? $"The room accounts for about {Degrees(room)} of that ({(room > 0 ? "warmer" : "cooler")} at rest); the other {Degrees(beyond)} is the PC."
                    : "The temperatures at rest were the same both times, so it isn't the room.");
                if (diff > 0) answer.Advice = "If nothing about the game's settings changed, check for dust on the heatsinks and that every fan turns.";
            }
            else if (Math.Abs(diff) < CompareSameWithin)
                answer.Lead = $"**No, about the same**: {at}, the {part} held {Temp(x)} {a.Label} and {Temp(y)} {b.Label}.";
            else
            {
                answer.Lead = $"**Not in a way that says anything about the PC**: {at}, the {part} held {Temp(x)} {a.Label} and {Temp(y)} {b.Label}.";
                answer.Paragraphs.Add($"That {Degrees(diff)} is within what a warmer or cooler room does: at rest it ran {Degrees(room)} {(room >= 0 ? "warmer" : "cooler")} {a.Label}.");
            }
            answer.Facts.Add(new($"{a.Title}, {now.App}", Temp(x)));
            answer.Facts.Add(new($"{b.Title}, {then.App}", Temp(y)));
            answer.Note = $"Compared over {Dur(now.Minutes * 60.0)} and {Dur(then.Minutes * 60.0)} of steady heavy load, past each session's warm-up.";
            return answer;
        }

        // Nothing like for like: say so, and give the highs for what they are.
        answer.Lead = $"**I can't say fairly.** Temperatures only compare at the same game and the same load, and there isn't an hour of that {a.Label} and {b.Label} both.";
        var (pa, pb) = cpu ? (ra.CpuTempPeak, rb.CpuTempPeak) : (ra.GpuTempPeak, rb.GpuTempPeak);
        if (pa is not null && pb is not null)
        {
            answer.Paragraphs.Add($"For reference, the {part}'s highest was {Temp(pa.Value)} {a.Label} and {Temp(pb.Value)} {b.Label}. A high depends on what was running and how warm the day was, so by itself it says little about the PC.");
            answer.Facts.Add(new($"{a.Title}, highest", Temp(pa.Value)));
            answer.Facts.Add(new($"{b.Title}, highest", Temp(pb.Value)));
        }
        else if (pa is null && pb is null) answer.Paragraphs.Add("No temperatures are on record for either.");
        return answer;
    }
}
