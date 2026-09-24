using Rigsight.Core.Apps;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Reports;

/// <summary>Turns report numbers into short, plain-language observations.</summary>
/// <remarks>
/// Only things worth saying: each line needs enough data behind it, comparisons are like for like (temperatures at the
/// same load, time against your usual), and plain readouts that pages already show (a 60° peak) are left out unless
/// they're a warning. Lines are ranked, so a page showing only a few gets the important ones.
/// </remarks>
public static class InsightEngine
{
    private const double MinUseForRanking = 10 * 60; // an app needs 10 min of use to be ranked by temperature
    private const double MinUseForInsights = 15 * 60; // below this there's nothing to say about a period yet
    private const double MinTimeDiff = 15 * 60;
    private const int MinUsualDays = 3; // days of history needed before comparing against "your usual"
    private const int MinLoadMinutes = 20, MinIdleMinutes = 30;
    private const double MinTempDiff = 3;
    private const double HotSpotGapWarn = 25;

    // Icons (Segoe Fluent).
    private const string IconScreen = "", IconApp = "", IconGame = "", IconTemp = "",
        IconGpu = "", IconCpu = "", IconCompare = "", IconIdle = "", IconMemory = "",
        IconAway = "", IconGood = "", IconWarn = "", IconStretch = "", IconClock = "", IconInfo = "";

    /// <param name="previous">The period before (for today: yesterday up to the same time).</param>
    /// <param name="usual">The 7 days before this period: daily averages and temperatures at the same load.</param>
    public static List<Insight> Generate(Report r, Report? previous, Report? usual, AlertSettings alerts)
    {
        var list = new List<Insight>();
        if (!r.HasData) return list;

        bool isDay = r.Range == ReportRange.Day;
        bool inProgress = r.From <= DateTime.Now && DateTime.Now < r.To;
        bool enoughUse = r.ActiveSec >= MinUseForInsights;
        int usualDays = usual?.DaysWithData ?? 0;
        bool haveUsual = usual is not null && usualDays >= MinUsualDays;
        string usualLabel = isDay ? "over the previous 7 days" : "in the 7 days before";

        // ── Warnings first: crashes and time spent too hot matter even on a short day ──

        if (r.Crashes.Count > 0)
        {
            var latest = r.Crashes[0];
            var name = r.Apps.FirstOrDefault(a => a.Exe.Equals(latest.AppExe, StringComparison.OrdinalIgnoreCase))?.Name;
            var ex = CrashExplainer.Explain(latest, name);
            string more = r.Crashes.Count > 1 ? $" ({r.Crashes.Count} crashes in total — see the Crashes page)" : "";
            string when = isDay ? $"{latest.Time:h:mm tt}" : $"{latest.Time:ddd d MMM, h:mm tt}";
            list.Add(new Insight(IconWarn, $"{ex.Title} at {when}: {ex.Culprit.ToLowerInvariant()}.{more}", InsightTone.Warn, "crash", 95));
        }

        if (r.CpuOverLimitMin > 0)
            list.Add(new Insight(IconWarn,
                $"Your CPU reached your {Units.TempShort(alerts.CpuLimit)} alert limit during {Minutes(r.CpuOverLimitMin)}.", InsightTone.Hot, "over-limit", 90));
        if (r.GpuOverLimitMin > 0)
            list.Add(new Insight(IconWarn,
                $"Your GPU reached your {Units.TempShort(alerts.GpuLimit)} alert limit during {Minutes(r.GpuOverLimitMin)}.", InsightTone.Hot, "over-limit", 90));

        // Peaks are shown elsewhere (tiles, hot moments); they're only news when they're high (and not already a limit warning).
        if (r.CpuTempPeak is { } cpu && r.CpuOverLimitMin == 0 && ToneFor(cpu.Value, 75, 88) != InsightTone.Neutral)
            list.Add(new Insight(IconTemp, $"CPU peaked at {Units.TempShort(cpu.Value)} {When(r, cpu.Time)}{While(r, cpu.App)}.",
                ToneFor(cpu.Value, 75, 88), "peak", 85));
        if (r.GpuTempPeak is { } gpu && r.GpuOverLimitMin == 0 && ToneFor(gpu.Value, 75, 85) != InsightTone.Neutral)
        {
            string hot = r.GpuHotPeak is { } hs ? $" (hot spot {Units.TempShort(hs.Value)})" : "";
            list.Add(new Insight(IconTemp, $"GPU peaked at {Units.TempShort(gpu.Value)}{hot} {When(r, gpu.Time)}{While(r, gpu.App)}.",
                ToneFor(gpu.Value, 75, 85), "peak", 85));
        }

        // Hot spot far above the core under load: worn paste or poor cooler contact.
        if (r.HotSpotGap is { Gpu: double gap, Minutes: >= 10 } && gap >= HotSpotGapWarn)
        {
            string trend = usual?.HotSpotGap is { Gpu: double before, Minutes: >= 10 } && gap - before >= 3
                ? $", up from {DegreesDiff(before)} {usualLabel}" : "";
            list.Add(new Insight(IconTemp,
                $"Under load, your GPU hot spot ran {DegreesDiff(gap)} above the core temperature{trend}. A gap over about 25° usually means the thermal paste or cooler contact has worn, and a repaste would help.",
                InsightTone.Warn, "hotspot", 80));
        }

        // ── Everything else needs enough use to mean something ──

        if (!enoughUse)
        {
            string text = inProgress
                ? $"Too early for highlights: {Units.Duration(r.ActiveSec)} of use so far."
                : $"A quiet {PeriodWord(r)}: only {Units.Duration(r.ActiveSec)} of use, so there's not much to point out.";
            list.Add(new Insight(IconInfo, text, InsightTone.Neutral, "early", 60));
            return Ranked(list);
        }

        // Screen time: the totals, then how that compares.
        list.Add(new Insight(IconScreen, $"You actively used your PC for {Units.Duration(r.ActiveSec)} (on for {Units.Duration(r.OnSec)}).",
            InsightTone.Neutral, "screen", 99));
        if (ScreenComparison(r, previous, usual, haveUsual, inProgress) is { } compare)
            list.Add(new Insight(IconScreen, compare, InsightTone.Neutral, "screen-compare", 65));

        // When the day started and ended, and whether the night before ran late.
        if (isDay && DaySpan(r, inProgress) is { } span)
            list.Add(new Insight(IconClock, span, InsightTone.Neutral, "span", 55));

        // Where the time went.
        var top = r.Apps.FirstOrDefault(a => a.ActiveSec > 0 && a.Category != AppCategory.System);
        if (top is not null && top.ActiveSec >= 20 * 60)
        {
            int pct = (int)Math.Round(top.ActiveSec / r.ActiveSec * 100);
            list.Add(new Insight(IconApp, $"{top.Name} took most of your time: {Units.Duration(top.ActiveSec)} ({pct}% of active time).",
                InsightTone.Neutral, "top-app", 50));
        }

        // Gaming, against your usual.
        var games = r.Apps.Where(a => a.Category == AppCategory.Game && a.ActiveSec >= 60).ToList();
        if (games.Count > 0)
        {
            double total = games.Sum(g => g.ActiveSec);
            string which = games.Count == 1 ? games[0].Name : $"{games.Count} games";
            string text = $"You gamed for {Units.Duration(total)} ({which}).";
            if (isDay && haveUsual)
            {
                double avg = usual!.GamingSec / usualDays;
                double diff = total - avg;
                if (avg > 0 && Math.Abs(diff) >= MinTimeDiff && (!inProgress || diff > 0))
                    text += $" That's {Units.Duration(Math.Abs(diff))} {(diff > 0 ? "more" : "less")} than your daily average ({Units.Duration(avg)}).";
            }
            var longest = r.Sessions.Where(s => s.IsGame).MaxBy(s => s.ActiveSec);
            if (longest is not null && longest.ActiveSec >= 20 * 60)
                text += $" Longest session: {longest.Name}, {Units.Duration(longest.ActiveSec)} starting {When(r, longest.Start, at: false)}.";
            list.Add(new Insight(IconGame, text, InsightTone.Neutral, "gaming", 64));
        }

        // Longest stretch without a break.
        if (r.LongestStretch is { } stretch && stretch.Seconds >= 90 * 60)
        {
            string app = stretch.App is null ? "" : $", mostly {stretch.App}";
            string range = isDay ? $"{stretch.Start:h:mm tt} – {stretch.End:h:mm tt}" : $"{stretch.Start:ddd d MMM, h:mm tt}";
            list.Add(new Insight(IconStretch, $"Your longest stretch without a break was {Units.Duration(stretch.Seconds)} ({range}){app}.",
                InsightTone.Neutral, "stretch", 58));
        }

        // Which app runs the hardware hottest on average (not Windows parts or Rigsight itself).
        var ranked = r.Apps.Where(a => a.ActiveSec >= MinUseForRanking && a.Category != AppCategory.System).ToList();
        var hottestGpu = ranked.Where(a => a.GpuTempAvg is not null).MaxBy(a => a.GpuTempAvg);
        var hottestCpu = ranked.Where(a => a.CpuTempAvg is not null).MaxBy(a => a.CpuTempAvg);
        if (hottestGpu is not null && ranked.Count > 1)
            list.Add(new Insight(IconGpu, $"{hottestGpu.Name} ran your GPU the hottest, averaging {Units.TempShort(hottestGpu.GpuTempAvg)}.",
                ToneFor(hottestGpu.GpuTempAvg!.Value, 72, 82), "hottest-app", 48));
        if (hottestCpu is not null && ranked.Count > 1 && hottestCpu != hottestGpu)
            list.Add(new Insight(IconCpu, $"{hottestCpu.Name} ran your CPU the hottest, averaging {Units.TempShort(hottestCpu.CpuTempAvg)}.",
                ToneFor(hottestCpu.CpuTempAvg!.Value, 72, 82), "hottest-app", 47));

        // Temperatures compared with your usual at the same load (heavy use first, then idle).
        if (haveUsual)
        {
            int added = 0;
            void Compare(string part, string state, LoadTemps? now, LoadTemps? before, Func<LoadTemps, double?> pick, int minMinutes)
            {
                if (added >= 2 || now is null || before is null || now.Minutes < minMinutes || before.Minutes < minMinutes) return;
                if (pick(now) is not double a || pick(before) is not double b || Math.Abs(a - b) < MinTempDiff) return;
                bool hotter = a > b;
                string hint = hotter && a - b >= 5 && state == "at idle" ? " A warmer room or dust build-up are the usual causes." : "";
                list.Add(new Insight(IconCompare,
                    $"{Capitalize(state)}, your {part} ran {DegreesDiff(a - b)} {(hotter ? "hotter" : "cooler")} than {usualLabel} ({Units.TempShort(a)} vs {Units.TempShort(b)}).{hint}",
                    hotter ? InsightTone.Warn : InsightTone.Good, "temp-compare", hotter ? 70 : 45));
                added++;
            }
            Compare("GPU", "under heavy load", r.GpuLoadTemps, usual!.GpuLoadTemps, t => t.Gpu, MinLoadMinutes);
            Compare("CPU", "under heavy load", r.CpuLoadTemps, usual!.CpuLoadTemps, t => t.Cpu, MinLoadMinutes);
            Compare("GPU", "at idle", r.IdleTemps, usual!.IdleTemps, t => t.Gpu, MinIdleMinutes);
            Compare("CPU", "at idle", r.IdleTemps, usual!.IdleTemps, t => t.Cpu, MinIdleMinutes);
        }

        // Open-but-unused apps.
        var idleHog = r.Apps
            .Where(a => a.BackgroundSec + a.MinimizedSec >= 3600 && a.ActiveSec < (a.BackgroundSec + a.MinimizedSec) / 4)
            .MaxBy(a => a.BackgroundSec + a.MinimizedSec);
        if (idleHog is not null)
            list.Add(new Insight(IconIdle,
                $"{idleHog.Name} sat open in the background for {Units.Duration(idleHog.BackgroundSec + idleHog.MinimizedSec)} but you only used it for {Units.Duration(idleHog.ActiveSec)}.",
                InsightTone.Neutral, "idle-app", 40));

        // Memory: only when one app took a big share.
        var memHog = r.Apps.Where(a => a.MemMax is not null).MaxBy(a => a.MemMax);
        if (memHog?.MemMax is double mem && mem >= 4096)
            list.Add(new Insight(IconMemory, $"{memHog.Name} used the most memory, peaking at {Units.Megabytes(mem)}.", InsightTone.Neutral, "memory", 35));

        // Away time.
        if (r.AwaySec >= 3600 && isDay)
            list.Add(new Insight(IconAway,
                $"Your PC sat unattended for {Units.Duration(r.AwaySec)} {(inProgress ? "today" : "this day")}. Letting it sleep sooner would save power.",
                InsightTone.Warn, "away", 42));

        // Reassurance when everything was cool.
        if (r.CpuTempPeak is { Value: < 70 } && r.GpuTempPeak is null or { Value: < 70 })
            list.Add(new Insight(IconGood, inProgress ? "Temperatures have stayed comfortable so far." : "Temperatures stayed comfortable the whole time.",
                InsightTone.Good, "comfortable", 30));

        return Ranked(list);
    }

    /// <summary>Most important first; equal ones keep the order they were written in.</summary>
    private static List<Insight> Ranked(List<Insight> list) => [.. list.OrderByDescending(i => i.Priority)];

    private static string? ScreenComparison(Report r, Report? previous, Report? usual, bool haveUsual, bool inProgress)
    {
        if (r.Range == ReportRange.Day && haveUsual)
        {
            double avg = usual!.ActiveSec / usual.DaysWithData;
            double diff = r.ActiveSec - avg;
            // A day in progress can only be compared once it has passed your usual.
            if (Math.Abs(diff) >= MinTimeDiff && (!inProgress || diff > 0))
                return inProgress
                    ? $"You're already {Units.Duration(diff)} past your daily average of {Units.Duration(avg)}."
                    : $"That's {Units.Duration(Math.Abs(diff))} {(diff > 0 ? "more" : "less")} than your daily average of {Units.Duration(avg)}.";
        }
        if (previous is { HasData: true } && previous.ActiveSec > 0)
        {
            double diff = r.ActiveSec - previous.ActiveSec;
            if (Math.Abs(diff) < MinTimeDiff) return null;
            string than = r.Range switch
            {
                ReportRange.Day => inProgress ? "yesterday by this time" : "the day before",
                ReportRange.Week => inProgress ? "last week by this point" : "the week before",
                _ => inProgress ? "last month by this point" : "the month before",
            };
            return $"That's {Units.Duration(Math.Abs(diff))} {(diff > 0 ? "more" : "less")} screen time than {than}.";
        }
        return null;
    }

    private static string? DaySpan(Report r, bool inProgress)
    {
        string? late = r.LateUntil is { } l ? $"The night before ran late: you were on until {l:h:mm tt}." : null;
        if (r.DayStart is not { } start) return late;
        string day = inProgress
            ? $"Your day started at {start:h:mm tt}."
            : $"Your day ran from {start:h:mm tt} to {(r.LastActive ?? start):h:mm tt}.";
        return late is null ? day : $"{late} {day}";
    }

    private static string When(Report r, DateTime time, bool at = true) =>
        (at ? "at " : "") + (r.Range == ReportRange.Day ? time.ToString("h:mm tt") : time.ToString("ddd d MMM, h:mm tt"));

    private static string PeriodWord(Report r) => r.Range switch { ReportRange.Day => "day", ReportRange.Week => "week", _ => "month" };

    private static string Minutes(int minutes) => minutes >= 60 ? Units.Duration(minutes * 60) : $"{minutes} minute{(minutes == 1 ? "" : "s")}";

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>" while playing Dota 2", " while browsing in Chrome"… (empty if no app was in front).</summary>
    private static string While(Report r, string? app) => app is null ? ""
        : " " + ActivityWords.While(app, r.Apps.FirstOrDefault(a => a.Name == app)?.Category ?? AppCategory.Other);

    private static string DegreesDiff(double celsiusDiff)
    {
        double d = Math.Abs(celsiusDiff) * (Units.Fahrenheit ? 9.0 / 5 : 1);
        return $"{d:0}°";
    }

    private static InsightTone ToneFor(double celsius, double warn, double hot) =>
        celsius >= hot ? InsightTone.Hot : celsius >= warn ? InsightTone.Warn : InsightTone.Neutral;
}
