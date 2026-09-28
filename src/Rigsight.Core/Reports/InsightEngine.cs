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
    /// <param name="context">Further back than the week before: the same weekday over recent weeks, and months ago.</param>
    public static List<Insight> Generate(Report r, Report? previous, Report? usual, AlertSettings alerts, InsightContext? context = null)
    {
        var list = new List<Insight>();
        if (!r.HasData) return list;

        bool isDay = ReportBuilder.IsDayLike(r.Range, r.From, r.To);
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
            // The cause after the title where there is one to add ("NVIDIA driver", a bugcheck name), as written: no
            // lowercasing of names, nothing that only repeats the title.
            string cause = ex.Cause is null ? "" : $": {ex.Cause}";
            list.Add(new Insight(IconWarn, $"{ex.Title} at {when}{cause}.{more}{CrashPattern(r)}", InsightTone.Warn, "crash", 95, CrashDetail(r)));
        }

        if (r.CpuOverLimitMin > 0)
            list.Add(new Insight(IconWarn,
                $"Your CPU reached your {Units.TempShort(alerts.CpuLimit)} alert limit during {Minutes(r.CpuOverLimitMin)}.", InsightTone.Hot, "over-limit", 90));
        if (r.GpuOverLimitMin > 0)
            list.Add(new Insight(IconWarn,
                $"Your GPU reached your {Units.TempShort(alerts.GpuLimit)} alert limit during {Minutes(r.GpuOverLimitMin)}.", InsightTone.Hot, "over-limit", 90));

        // A chip slowing itself down to stay cool: the clearest sign cooling isn't keeping up.
        if (r.GpuThrottle is { } gt)
            list.Add(new Insight(IconGpu, $"Your GPU slowed itself for {Minutes(gt.Minutes)} to stay cool: clocks fell about {gt.DropPercent:0}% once it passed {Units.TempShort(gt.FromTemp)}.",
                InsightTone.Hot, "throttle", 80, $"{gt.Minutes} minutes under heavy load with clocks {gt.DropPercent:0}% under their usual when cool"));
        if (r.CpuThrottle is { } ct)
            list.Add(new Insight(IconCpu, $"Your CPU slowed itself for {Minutes(ct.Minutes)} to stay cool: clocks fell about {ct.DropPercent:0}% once it passed {Units.TempShort(ct.FromTemp)}.",
                InsightTone.Hot, "throttle", 79, $"{ct.Minutes} minutes under heavy load with clocks {ct.DropPercent:0}% under their usual when cool"));

        // Peaks are shown elsewhere (tiles, hot moments); they're only news when they're high (and not already a limit warning).
        if (r.CpuTempPeak is { } cpu && r.CpuOverLimitMin == 0 && ToneFor(cpu.Value, 75, 88) != InsightTone.Neutral)
            list.Add(new Insight(IconTemp, $"CPU peaked at {Units.TempShort(cpu.Value)} {When(r, cpu.Time)}{PeakWords.With(cpu.App)}.",
                ToneFor(cpu.Value, 75, 88), "peak", 85));
        if (r.GpuTempPeak is { } gpu && r.GpuOverLimitMin == 0 && ToneFor(gpu.Value, 75, 85) != InsightTone.Neutral)
        {
            string hot = r.GpuHotPeak is { } hs ? $" (hot spot {Units.TempShort(hs.Value)})" : "";
            list.Add(new Insight(IconTemp, $"GPU peaked at {Units.TempShort(gpu.Value)}{hot} {When(r, gpu.Time)}{PeakWords.With(gpu.App)}.",
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

        // Slow drift: this period against a few months ago, at the same load. Dust and old paste show over months.
        var driftSaid = new HashSet<string>();
        if (context?.Then is { } then)
        {
            int drifts = 0;
            void Drift(string part, string state, double? now, int nowMinutes, double? before, int minMinutes, string cause)
            {
                if (drifts >= 2 || now is not double a || before is not double b || nowMinutes < minMinutes || then.Idle.Minutes < ThenMinMinutes) return;
                double diff = a - b;
                if (Math.Abs(diff) < MinTempDiff) return;
                bool hotter = diff > 0;
                string when = MonthsAgo(then);
                string detail = $"{Units.TempShort(a)} over {nowMinutes} minutes {state} now, {Units.TempShort(b)} over {then.Days} days {when}";
                list.Add(new Insight(IconCompare,
                    $"{Capitalize(state)}, your {part} runs {DegreesDiff(diff)} {(hotter ? "hotter" : "cooler")} than it did {when} ({Units.TempShort(a)} vs {Units.TempShort(b)}).{(hotter ? cause : "")}",
                    hotter ? InsightTone.Warn : InsightTone.Good, "drift", hotter ? (diff >= 5 ? 72 : 58) : 40, detail));
                drifts++;
                driftSaid.Add(part + state);
            }
            Drift("GPU", "at idle", r.IdleTemps?.Gpu, r.IdleTemps?.Minutes ?? 0, then.Idle.Gpu, MinIdleMinutes, " Dust building up is the usual cause.");
            Drift("CPU", "at idle", r.IdleTemps?.Cpu, r.IdleTemps?.Minutes ?? 0, then.Idle.Cpu, MinIdleMinutes, " Dust building up is the usual cause.");
            Drift("GPU", "under heavy load", r.GpuLoadTemps?.Gpu, r.GpuLoadTemps?.Minutes ?? 0, then.Load.Gpu, MinLoadMinutes, " Old thermal paste or a dusty cooler are the usual causes.");
            Drift("CPU", "under heavy load", r.CpuLoadTemps?.Cpu, r.CpuLoadTemps?.Minutes ?? 0, then.Load.Cpu, MinLoadMinutes, " Old thermal paste or a dusty cooler are the usual causes.");
        }

        // Fans working harder for the same temperature: a cooler clogging up. Then fans faster at idle.
        if (usual is not null && r.Fans.Count > 0)
        {
            int said = 0;
            foreach (var fan in r.Fans)
            {
                if (said >= 2) break;
                var was = usual.Fans.FirstOrDefault(f => f.Name == fan.Name && f.Hardware == fan.Hardware);
                if (was is null) continue;
                string who = fan.Gpu ? "Your GPU fan" : $"{fan.Name} on your {fan.Hardware}";
                if (fan.WarmRpm is double warm && was.WarmRpm is double warmBefore && fan.WarmMinutes >= MinFanMinutes && was.WarmMinutes >= MinFanMinutes
                    && warm - warmBefore >= MinFanRpmDiff && (warm - warmBefore) / warmBefore >= MinFanShareDiff)
                {
                    list.Add(new Insight(IconWarn,
                        $"{who} runs about {Rpm(warm - warmBefore)} rpm faster than usual at the same temperature ({Rpm(warm)} vs {Rpm(warmBefore)} rpm). That's what a clogging cooler looks like.",
                        InsightTone.Warn, "fan", 68, $"{fan.WarmMinutes} minutes with the {(fan.Gpu ? "GPU" : "CPU")} at 60–75° now, {was.WarmMinutes} {usualLabel}"));
                    said++;
                }
                else if (fan.IdleRpm is double idle && was.IdleRpm is double idleBefore && fan.IdleMinutes >= MinIdleMinutes && was.IdleMinutes >= MinIdleMinutes
                    && idleBefore > 0 && (idle - idleBefore) / idleBefore >= MinFanIdleShareDiff)
                {
                    list.Add(new Insight(IconInfo,
                        $"{who} runs about {(idle - idleBefore) / idleBefore * 100:0}% faster at idle than usual ({Rpm(idle)} vs {Rpm(idleBefore)} rpm).",
                        InsightTone.Neutral, "fan", 50, $"{fan.IdleMinutes} idle minutes now, {was.IdleMinutes} {usualLabel}"));
                    said++;
                }
            }
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
        if (ScreenComparison(r, previous, usual, haveUsual, inProgress, context?.Weekday) is { } compare)
            list.Add(new Insight(IconScreen, compare, InsightTone.Neutral, "screen-compare", 65));

        // Records and streaks: what this day beat.
        foreach (var record in r.Records)
        {
            var (icon, text, tone, priority) = record.Kind switch
            {
                RecordKind.LongestGameSession => (IconGame, $"Longest gaming session in {Span(record.Days)}: {record.App}, {record.Value}.", InsightTone.Good, 66),
                RecordKind.HottestGpu => (IconTemp, $"Hottest GPU peak in {Span(record.Days)}: {record.Value}.", InsightTone.Warn, 63),
                RecordKind.HottestCpu => (IconTemp, $"Hottest CPU peak in {Span(record.Days)}: {record.Value}.", InsightTone.Warn, 62),
                _ => (IconScreen, $"Most screen time in {Span(record.Days)}: {record.Value}.", InsightTone.Neutral, 61),
            };
            list.Add(new Insight(icon, text, tone, "record", priority, $"Beats every day of the {record.Days} before"));
        }
        if (r.StreakDays >= 3)
            list.Add(new Insight(IconScreen, $"{Nth(r.StreakDays)} in a row over your usual screen time.", InsightTone.Neutral, "streak", 56,
                "Each day against the average of the 7 before it"));

        // When the day started and ended, and whether the night before ran late.
        if (isDay && DaySpan(r, inProgress) is { } span)
            list.Add(new Insight(IconClock, span, InsightTone.Neutral, "span", 55));

        // Where the time went. The same app as the period before: said in passing, not as news.
        var top = r.Apps.FirstOrDefault(a => a.ActiveSec > 0 && a.Category != AppCategory.System);
        if (top is not null && top.ActiveSec >= 20 * 60)
        {
            int pct = (int)Math.Round(top.ActiveSec / r.ActiveSec * 100);
            bool again = TopApp(previous)?.Name == top.Name;
            list.Add(new Insight(IconApp, $"{top.Name} took most of your time{(again ? " again" : "")}: {Units.Duration(top.ActiveSec)} ({pct}% of active time).",
                InsightTone.Neutral, "top-app", again ? 25 : 50));
        }

        // Gaming, against your usual.
        var games = r.Apps.Where(a => a.Category == AppCategory.Game && a.ActiveSec >= 60).ToList();
        if (games.Count > 0)
        {
            double total = games.Sum(g => g.ActiveSec);
            string which = games.Count == 1 ? games[0].Name : $"{games.Count} games";
            string text = $"You gamed for {Units.Duration(total)} ({which}).";
            if (isDay && context?.Weekday is { GamingSec: > 0 } wd)
            {
                double diff = total - wd.GamingSec;
                if (Math.Abs(diff) >= MinTimeDiff && (!inProgress || diff > 0))
                    text += $" That's {Units.Duration(Math.Abs(diff))} {(diff > 0 ? "more" : "less")} than your usual {wd.Day} ({Units.Duration(wd.GamingSec)}).";
            }
            else if (isDay && haveUsual)
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

        // Who made the heat: the minutes over the warm line by the app that was working the part (not the window in front).
        void HotBy(string part, string icon, int hot, List<HotShare> by, int priority)
        {
            if (hot < MinHotMinutes || by.Count == 0) return;
            int known = by.Sum(x => x.Minutes);
            if (known < hot / 2) return; // mostly nobody's: peaks say when, and that's enough
            string who = by[0].Minutes >= hot * 0.8 ? $"nearly all of it on {by[0].App}"
                : by[0].Minutes >= hot * 0.5 ? $"mostly on {by[0].App}"
                : $"spread across {Join(by.Take(3).Select(x => x.App))}";
            list.Add(new Insight(icon, $"Your {part} spent {Minutes(hot)} over {Units.TempShort(Report.HotLine)}, {who}.",
                hot >= 60 ? InsightTone.Warn : InsightTone.Neutral, "hot-app", priority, string.Join(" · ", by.Take(4).Select(x => $"{x.App} {x.Minutes} min"))));
        }
        HotBy("GPU", IconGpu, r.GpuHotMinutes, r.GpuHotByApp, 62);
        HotBy("CPU", IconCpu, r.CpuHotMinutes, r.CpuHotByApp, 61);

        // Heavy work done behind the app you were using.
        if (r.BackgroundWork is { } bg)
            list.Add(new Insight(bg.Gpu ? IconGpu : IconCpu,
                $"{bg.App} ran your {(bg.Gpu ? "GPU" : "CPU")} hard for {Minutes(bg.Minutes)} from {When(r, bg.Start, at: false)} while you were in {bg.FrontApp}.",
                bg.Minutes >= 30 ? InsightTone.Warn : InsightTone.Neutral, "background-work", 60,
                $"{When(r, bg.Start, at: false)} – {When(r, bg.Start.AddMinutes(bg.Minutes), at: false)}"));

        // One game hotter than the others (games are what loads a GPU: like for like).
        var hotGames = r.Apps.Where(a => a.Category == AppCategory.Game && a.ActiveSec >= MinUseForRanking && a.GpuTempAvg is not null).ToList();
        if (hotGames.Count >= 2)
        {
            var hottest = hotGames.MaxBy(g => g.GpuTempAvg)!;
            double others = hotGames.Where(g => g != hottest).Average(g => g.GpuTempAvg!.Value);
            double diff = hottest.GpuTempAvg!.Value - others;
            if (diff >= MinGameHeatDiff)
                list.Add(new Insight(IconGame, $"{hottest.Name} runs your GPU about {DegreesDiff(diff)} hotter than your other games ({Units.TempShort(hottest.GpuTempAvg)} vs {Units.TempShort(others)}).",
                    diff >= 8 ? InsightTone.Warn : InsightTone.Neutral, "game-heat", 52, Join(hotGames.Select(g => $"{g.Name} {Units.TempShort(g.GpuTempAvg)}"))));
        }

        // Temperatures compared with your usual at the same load (heavy use first, then idle).
        if (haveUsual)
        {
            int added = 0;
            void Compare(string part, string state, LoadTemps? now, LoadTemps? before, Func<LoadTemps, double?> pick, int minMinutes)
            {
                // Months ago already said it about this part at this load: the week before adds nothing.
                if (added >= 2 || driftSaid.Contains(part + state) || now is null || before is null || now.Minutes < minMinutes || before.Minutes < minMinutes) return;
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

        // Open-but-unused apps (not the same one as the period before: that's nagging).
        var idleHog = IdleHog(r);
        if (idleHog is not null && IdleHog(previous)?.Name != idleHog.Name)
            list.Add(new Insight(IconIdle,
                $"{idleHog.Name} sat open in the background for {Units.Duration(idleHog.BackgroundSec + idleHog.MinimizedSec)} but you only used it for {Units.Duration(idleHog.ActiveSec)}.",
                InsightTone.Neutral, "idle-app", 40));

        // Memory: only when one app took a big share, and it isn't the same one as the period before.
        var memHog = MemHog(r);
        if (memHog?.MemMax is double mem && mem >= 4096 && MemHog(previous)?.Name != memHog.Name)
            list.Add(new Insight(IconMemory, $"{memHog.Name} used the most memory, peaking at {Units.Megabytes(mem)}.", InsightTone.Neutral, "memory", 35));

        // Away time.
        if (r.AwaySec >= 3600 && isDay)
            list.Add(new Insight(IconAway,
                $"Your PC sat unattended for {Units.Duration(r.AwaySec)} {(inProgress ? "today" : "this day")}. Letting it sleep sooner would save power.",
                InsightTone.Warn, "away", 42));

        // Cooling down after heavy load: slower than usual means heat is staying in the case.
        if (r.CooldownMinutes is double cool && r.CooldownCount > 0)
        {
            string line = $"After heavy load, your GPU took {Minutes((int)Math.Round(cool))} to cool below {Units.TempShort(50)}";
            string detail = $"Averaged over {r.CooldownCount} time{(r.CooldownCount == 1 ? "" : "s")} heavy load ended";
            if (usual?.CooldownMinutes is double coolBefore && usual.CooldownCount > 0 && Math.Abs(cool - coolBefore) >= 3)
            {
                if (cool >= coolBefore * 1.5)
                    list.Add(new Insight(IconTemp, $"{line}, against {Minutes((int)Math.Round(coolBefore))} usually. Poor airflow keeps heat in the case.", InsightTone.Warn, "cooldown", 54, detail));
                else if (cool <= coolBefore / 1.5)
                    list.Add(new Insight(IconGood, $"{line}, quicker than the {Minutes((int)Math.Round(coolBefore))} usual.", InsightTone.Good, "cooldown", 35, detail));
            }
            else if (usual?.CooldownCount is null or 0 && cool >= 20)
                list.Add(new Insight(IconTemp, $"{line}. Poor airflow keeps heat in the case.", InsightTone.Neutral, "cooldown", 50, detail));
        }

        // Reassurance when everything was cool (less of it when it's every day).
        if (r.CpuTempPeak is { Value: < 70 } && r.GpuTempPeak is null or { Value: < 70 })
        {
            bool again = previous is { CpuTempPeak: { Value: < 70 }, GpuTempPeak: null or { Value: < 70 } };
            list.Add(new Insight(IconGood, inProgress ? "Temperatures have stayed comfortable so far." : "Temperatures stayed comfortable the whole time.",
                InsightTone.Good, "comfortable", again ? 20 : 30));
        }

        return Ranked(list);
    }

    /// <summary>Most important first; equal ones keep the order they were written in.</summary>
    private static List<Insight> Ranked(List<Insight> list) => [.. list.OrderByDescending(i => i.Priority)];

    private static string? ScreenComparison(Report r, Report? previous, Report? usual, bool haveUsual, bool inProgress, WeekdayUsual? weekday)
    {
        // A day against the same weekday over recent weeks, when there are enough: a Saturday isn't a Tuesday.
        if (r.Range == ReportRange.Day && weekday is not null)
        {
            double diff = r.ActiveSec - weekday.ActiveSec;
            if (Math.Abs(diff) >= MinTimeDiff && (!inProgress || diff > 0))
                return inProgress
                    ? $"You're already {Units.Duration(diff)} past your usual {weekday.Day} of {Units.Duration(weekday.ActiveSec)}."
                    : $"That's {Units.Duration(Math.Abs(diff))} {(diff > 0 ? "more" : "less")} than your usual {weekday.Day} ({Units.Duration(weekday.ActiveSec)}).";
            if (inProgress) return null;
        }
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
                ReportRange.Year => inProgress ? "last year by this point" : "the year before",
                ReportRange.Custom when ReportBuilder.IsDayLike(r.Range, r.From, r.To) => (int)Math.Round((r.From - previous.From).TotalDays) == 1 ? "the same hours the day before" : "the same hours two days before",
                ReportRange.Custom => "the same length of time just before",
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
        if (r.Range == ReportRange.Custom)
        {
            // A stretch of hours, not a day; the weekdays when it covers more than one.
            string fmt = r.From.Date == r.To.AddTicks(-1).Date ? "h:mm tt" : "ddd h:mm tt";
            return inProgress
                ? $"You've been on since {start.ToString(fmt)}."
                : $"You were on from {start.ToString(fmt)} to {(r.LastActive ?? start).ToString(fmt)}.";
        }
        string day = inProgress
            ? $"Your day started at {start:h:mm tt}."
            : $"Your day ran from {start:h:mm tt} to {(r.LastActive ?? start):h:mm tt}.";
        return late is null ? day : $"{late} {day}";
    }

    private static string When(Report r, DateTime time, bool at = true) =>
        (at ? "at " : "") + (r.Range == ReportRange.Day || (r.Range == ReportRange.Custom && r.From.Date == r.To.AddTicks(-1).Date)
            ? time.ToString("h:mm tt") : time.ToString("ddd d MMM, h:mm tt"));

    private static string PeriodWord(Report r) => r.Range switch
    {
        ReportRange.Day => "day", ReportRange.Week => "week", ReportRange.Year => "year", ReportRange.All or ReportRange.Custom => "stretch", _ => "month",
    };

    private const int MinHotMinutes = 10, MinFanMinutes = 20;
    private const double MinGameHeatDiff = 5, MinFanRpmDiff = 150, MinFanShareDiff = 0.10, MinFanIdleShareDiff = 0.15;
    private const int ThenMinMinutes = 300;

    private static AppStat? TopApp(Report? r) => r?.Apps.FirstOrDefault(a => a.ActiveSec > 0 && a.Category != AppCategory.System);

    private static AppStat? IdleHog(Report? r) => r?.Apps
        .Where(a => a.BackgroundSec + a.MinimizedSec >= 3600 && a.ActiveSec < (a.BackgroundSec + a.MinimizedSec) / 4)
        .MaxBy(a => a.BackgroundSec + a.MinimizedSec);

    private static AppStat? MemHog(Report? r) => r?.Apps.Where(a => a.MemMax is not null).MaxBy(a => a.MemMax);

    /// <summary>What the crashes had in common, from the minutes before each: heat, or one app in front.</summary>
    private static string CrashPattern(Report r)
    {
        var contexts = r.Crashes.Select(c => r.CrashContexts.GetValueOrDefault(c.Id)).ToList();
        if (r.Crashes.Count == 1)
        {
            var ctx = contexts[0];
            if (ctx?.GpuBefore >= CrashHeatGpu) return $" It came minutes after the GPU passed {Units.TempShort(ctx.GpuBefore)}.";
            if (ctx?.CpuBefore >= CrashHeatCpu) return $" It came minutes after the CPU passed {Units.TempShort(ctx.CpuBefore)}.";
            return "";
        }
        string all = r.Crashes.Count == 2 ? "Both" : $"All {r.Crashes.Count}";
        var hotGpu = contexts.Where(c => c?.GpuBefore >= CrashHeatGpu).Select(c => c!.GpuBefore!.Value).ToList();
        if (hotGpu.Count == r.Crashes.Count) return $" {all} came minutes after the GPU was over {Units.TempShort(hotGpu.Min())}.";
        var hotCpu = contexts.Where(c => c?.CpuBefore >= CrashHeatCpu).Select(c => c!.CpuBefore!.Value).ToList();
        if (hotCpu.Count == r.Crashes.Count) return $" {all} came minutes after the CPU was over {Units.TempShort(hotCpu.Min())}.";
        var fronts = contexts.Select(c => c?.FrontApp).ToList();
        if (fronts[0] is long front && fronts.All(f => f == front) && r.Apps.FirstOrDefault(a => a.Id == front) is { } app)
            return $" {all} happened while {app.Name} was in front.";
        return "";
    }

    private static string CrashDetail(Report r) => string.Join(" · ", r.Crashes.Take(4).Select(c =>
        r.CrashContexts.GetValueOrDefault(c.Id) is { } ctx
            ? $"{c.Time:d MMM h:mm tt}: CPU {Units.TempShort(ctx.CpuBefore)}, GPU {Units.TempShort(ctx.GpuBefore)} just before"
            : $"{c.Time:d MMM h:mm tt}"));

    private const double CrashHeatGpu = 83, CrashHeatCpu = 88;

    /// <summary>"a month", "three months", "a year": how far back a record holds.</summary>
    private static string Span(int days) => days switch { 30 => "a month", 90 => "three months", _ => "a year" };

    private static string Nth(int n) => n switch
    {
        3 => "Third day", 4 => "Fourth day", 5 => "Fifth day", 6 => "Sixth day", 7 => "Seventh day", _ => $"{n} days",
    };

    /// <summary>"in June" (the middle of the stretch), with the year when it isn't this one.</summary>
    private static string MonthsAgo(ThenTemps then)
    {
        var mid = then.From.AddDays((then.To - then.From).TotalDays / 2);
        return mid.Year == DateTime.Today.Year ? $"in {mid:MMMM}" : $"in {mid:MMMM yyyy}";
    }

    private static string Rpm(double rpm) => Math.Round(rpm).ToString("#,0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"A, B and C".</summary>
    private static string Join(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count <= 1 ? string.Concat(list) : string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1];
    }

    private static string Minutes(int minutes) => minutes >= 60 ? Units.Duration(minutes * 60) : $"{minutes} minute{(minutes == 1 ? "" : "s")}";

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>" while playing Dota 2", " while browsing in Chrome"… (empty if no app was in front).</summary>
    private static string DegreesDiff(double celsiusDiff)
    {
        double d = Math.Abs(celsiusDiff) * (Units.Fahrenheit ? 9.0 / 5 : 1);
        return $"{d:0}°";
    }

    private static InsightTone ToneFor(double celsius, double warn, double hot) =>
        celsius >= hot ? InsightTone.Hot : celsius >= warn ? InsightTone.Warn : InsightTone.Neutral;
}
