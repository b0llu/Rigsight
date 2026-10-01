using Rigsight.Core.Apps;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Reports;

/// <summary>Turns report numbers into short, plain-language observations.</summary>
/// <remarks>
/// Only things worth saying: each line needs enough data behind it, and comparisons are like for like or not made at
/// all. Temperatures are compared only for the same game at steady load, with the room taken out; a lighter game, a
/// warm-up, a GPU cooling after play or a warmer afternoon would each pass for a change in the PC. Plain readouts that
/// pages already show (a 60° peak) are left out unless they're a warning. Lines are ranked, so a page showing only a
/// few gets the important ones.
/// </remarks>
public static class InsightEngine
{
    private const double MinUseForInsights = 15 * 60; // below this there's nothing to say about a period yet
    private const double MinTimeDiff = 15 * 60;
    private const int MinUsualDays = 3; // days of history needed before comparing against "your usual"
    private const double HotSpotGapWarn = 30;

    // Held (a minute's average, not a spike) at or over these: a peak worth a line.
    private const double CpuWarm = 85, CpuHot = 92, GpuWarm = 83, GpuHot = 88;

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
            // lowercasing of names, nothing that only repeats the title. "in its own code" reads on from the time.
            string cause = ex.Cause switch
            {
                null => "",
                var c when c.StartsWith("in ", StringComparison.Ordinal) => $", {c}",
                var c => $": {c}",
            };
            list.Add(new Insight(IconWarn, $"{ex.Title} at {when}{cause}.{more}{CrashPattern(r, alerts)}", InsightTone.Warn, "crash", 95, CrashDetail(r)));
        }

        // Minutes a chip averaged at or over the user's alert limit (a moment's spike isn't one, as it isn't for the alert).
        if (r.CpuOverLimitMin > 0)
            list.Add(new Insight(IconWarn,
                $"Your CPU reached your {Units.TempShort(alerts.CpuLimit)} alert limit for {Minutes(r.CpuOverLimitMin)}.", InsightTone.Hot, "over-limit", 90));
        if (r.GpuOverLimitMin > 0)
            list.Add(new Insight(IconWarn,
                $"Your GPU reached your {Units.TempShort(alerts.GpuLimit)} alert limit for {Minutes(r.GpuOverLimitMin)}.", InsightTone.Hot, "over-limit", 90));

        // The GPU slowing itself down to stay cool, within one game session: the clearest sign cooling isn't keeping up.
        if (r.GpuThrottle is { } gt)
            list.Add(new Insight(IconGpu, $"Your GPU slowed itself for {Minutes(gt.Minutes)} to stay cool: clocks fell about {gt.DropPercent:0}% once it reached {Units.TempShort(gt.FromTemp)}.",
                InsightTone.Hot, "throttle", 80, $"Clocks {gt.DropPercent:0}% under where they were earlier in the same session, while cooler, at no more power"));

        // Peaks are shown elsewhere (tiles, hot moments); they're only news when a chip held the heat (a moment's spike on
        // a light load isn't that), and aren't already a limit warning. A record peak says so here, rather than twice.
        var peakRecords = new HashSet<RecordKind>();
        void PeakLine(string part, Peak? peak, double? held, int overLimit, double warm, double hot, RecordKind record)
        {
            if (peak is null || overLimit > 0 || held is not double h || ToneFor(h, warm, hot) == InsightTone.Neutral) return;
            string best = r.Records.FirstOrDefault(x => x.Kind == record) is { } rec ? $", the hottest in {Span(rec.Days)}" : "";
            if (best.Length > 0) peakRecords.Add(record);
            list.Add(new Insight(IconTemp, $"{part} peaked at {Units.TempShort(peak.Value)} {When(r, peak.Time)}{best}{PeakWords.With(peak.App)}.",
                ToneFor(h, warm, hot), "peak", 85));
        }
        PeakLine("CPU", r.CpuTempPeak, r.CpuTempHeld, r.CpuOverLimitMin, CpuWarm, CpuHot, RecordKind.HottestCpu);
        PeakLine("GPU", r.GpuTempPeak, r.GpuTempHeld, r.GpuOverLimitMin, GpuWarm, GpuHot, RecordKind.HottestGpu);

        // Hot spot far above the core at steady load: past about 30° the paste or cooler contact may have worn.
        if (r.HotSpotGap is { Gpu: double gap, Minutes: >= HotSpotMinutes } && gap >= HotSpotGapWarn)
            list.Add(new Insight(IconTemp,
                $"In games, your GPU hot spot runs {DegreesDiff(gap)} above the core. A gap past {DegreesDiff(HotSpotGapWarn)} can mean the thermal paste or cooler contact has worn.",
                InsightTone.Warn, "hotspot", 70, $"Over {r.HotSpotGap.Minutes} minutes of steady heavy load"));

        // Slow drift: the same game at steady load, now and a few months ago, with the room taken out (temperatures at rest
        // follow it). Only then is "hotter than it was" about the PC: dust, old paste. A day is too little to go on: it
        // counts with the week before it.
        if (context?.Then is { } then)
        {
            var nowSteady = isDay && usual is not null ? MergeSteady(r.Steady, usual.Steady) : r.Steady;
            var nowRest = isDay && usual is not null ? MergeTemps(r.RestTemps, usual.RestTemps) : r.RestTemps;
            var match = nowSteady
                .Select(n => (Now: n, Then: then.Steady.FirstOrDefault(t => t.AppId == n.AppId)))
                .Where(p => p.Then is not null && p.Now.Minutes >= DriftNowMinutes && p.Then.Minutes >= DriftThenMinutes && p.Then.Days >= DriftThenDays
                            && SamePower(p.Now, p.Then))
                .OrderByDescending(p => Math.Min(p.Now.Minutes, p.Then!.Minutes)).FirstOrDefault();
            if (match.Now is { } now && match.Then is { } before && nowRest is { Minutes: >= DriftRestMinutes } && then.Rest is { Minutes: >= DriftRestMinutes })
            {
                void Drift(string part, double? a, double? b, double? restNow, double? restThen)
                {
                    if (a is not double x || b is not double y || restNow is not double rn || restThen is not double rt) return;
                    double diff = x - y, beyondRoom = diff - (rn - rt);
                    if (Math.Abs(diff) < DriftMinDiff || Math.Abs(beyondRoom) < DriftMinBeyondRoom || Math.Sign(diff) != Math.Sign(beyondRoom)) return;
                    bool hotter = diff > 0;
                    string when = MonthsAgo(then);
                    list.Add(new Insight(IconCompare,
                        $"In {now.App}, your {part} {(inProgress ? "runs" : "ran")} {ShownDiff(x, y)} {(hotter ? "hotter" : "cooler")} than {when} ({Units.TempShort(x)} vs {Units.TempShort(y)})"
                        + (hotter ? ", and not because of the room. Dust or old thermal paste are the usual causes." : "."),
                        hotter ? InsightTone.Warn : InsightTone.Good, "drift", hotter ? 72 : 40,
                        $"{now.Minutes} minutes of steady play now, {before.Minutes} {when}; at rest {Units.TempShort(rn)} now, {Units.TempShort(rt)} then"));
                }
                Drift("GPU", now.Gpu, before.Gpu, nowRest.Gpu, then.Rest.Gpu);
                Drift("CPU", now.Cpu, before.Cpu, nowRest.Cpu, then.Rest.Cpu);
            }
        }

        // A fan reading 0 rpm where it always turns (a GPU fan with the GPU hot; any other fan whenever the PC is on):
        // stopped, unplugged or stuck. Fans that stop on purpose, and headers with nothing on them, never do that.
        var fanSaid = new HashSet<(string, string)>();
        if (usual is not null)
        {
            bool gpuSaid = false;
            foreach (var fan in r.Fans.Where(f => f.LongestStop >= (f.Gpu ? GpuFanStopMinutes : FanStopMinutes)).OrderByDescending(f => f.Gpu).ThenByDescending(f => f.LongestStop))
            {
                var was = usual.Fans.FirstOrDefault(f => f.Name == fan.Name && f.Hardware == fan.Hardware);
                if (was is null || was.SpinMinutes < FanUsualMinutes || was.StoppedMinutes > was.SpinMinutes * FanUsualStoppedShare || (fan.Gpu && gpuSaid)) continue;
                string from = When(r, fan.StopStart!.Value, at: false);
                list.Add(fan.Gpu
                    ? new Insight(IconWarn, $"Your GPU fan stopped for {Minutes(fan.LongestStop)} from {from} with the GPU at {Units.TempShort(fan.StopTemp)}, when it always turns at that heat. Check it isn't blocked or unplugged.",
                        InsightTone.Hot, "fan-stopped", 88, $"{fan.Name}: 0 rpm in {fan.StoppedMinutes} of {fan.SpinMinutes} minutes with the GPU at 70° or more")
                    : new Insight(IconWarn, $"{fan.Name} on your motherboard stopped for {Minutes(fan.LongestStop)} from {from}, when it always turns. Check it isn't blocked or unplugged.",
                        InsightTone.Warn, "fan-stopped", 86, $"0 rpm in {fan.StoppedMinutes} of {fan.SpinMinutes} minutes on; {was.StoppedMinutes} of {was.SpinMinutes} {usualLabel}"));
                gpuSaid |= fan.Gpu;
                fanSaid.Add((fan.Name, fan.Hardware));
            }

            // A fan turning slower than it did in the days before, like for like: a fan on a curve at the same
            // temperature of the chip it follows, in the same game, under the same steady heavy load; a fan at a set speed
            // at that speed. Faster isn't news (a curve does that as it gets hotter). Slower may be a setting: the BIOS
            // (only across a restart, when a fan can't have worn), the card's software or a fan app (named when one was
            // running), so the words say so first. Said once, the day it starts, not again every day until it's "usual".
            gpuSaid = r.Fans.Any(f => f.Gpu && fanSaid.Contains((f.Name, f.Hardware)));
            var running = r.Apps.Where(a => a.OpenSec > 0).Select(a => a.Exe).ToList();
            foreach (var fan in r.Fans.OrderByDescending(f => f.Gpu))
            {
                if (fanSaid.Contains((fan.Name, fan.Hardware)) || (fan.Gpu && gpuSaid)) continue;
                var was = usual.Fans.FirstOrDefault(f => f.Name == fan.Name && f.Hardware == fan.Hardware);
                if (was is null) continue;
                var before = previous?.Fans.FirstOrDefault(f => f.Name == fan.Name && f.Hardware == fan.Hardware);
                string? app = FanSoftware.Among(running, fan.Gpu);
                string Check(string what, bool plural) =>
                    $"If you changed {what} {(app is not null ? $"in {app}" : fan.Gpu ? "in the card's software" : "in a fan app")}, that's why; if not, check {(plural ? "they're" : "it's")} clean and turning freely.";
                if (Slower(fan, was) is { } slow)
                {
                    if (before is not null && Slower(before, was) is not null) continue; // already said
                    string part = fan.Follows == FanFollows.Cpu ? "CPU" : "GPU";
                    string who = fan.Gpu ? "Your GPU fans" : $"{fan.Name} on your motherboard";
                    // Hot, not just worth a look, when the chip was warm with its fans held back.
                    list.Add(new Insight(IconWarn,
                        $"{who} spun {slow.Percent:0}% slower than usual at the same heat: {slow.Now:N0} rpm with the {part} at {Units.TempShort(slow.Temp)}, "
                        + $"against {slow.Was:N0} rpm {usualLabel}. " + Check("the fan curve", fan.Gpu),
                        slow.Temp >= (part == "GPU" ? GpuWarm : CpuWarm) ? InsightTone.Hot : InsightTone.Warn, "fan-slower", 84,
                        $"{slow.NowMinutes} minutes of steady load at the same {part} temperatures now, {slow.WasMinutes} {usualLabel}"));
                    fanSaid.Add((fan.Name, fan.Hardware));
                    gpuSaid |= fan.Gpu;
                }
                // At a set speed before, and slowing while the PC was on (a run of minutes with no restart), so not the
                // BIOS. A fan that starts a run slower was set so while the PC was off: nothing to say.
                else if (!fan.Gpu && was is { Follows: FanFollows.Steady, SteadyRpm: int usualRpm } && was.SpinMinutes >= FanUsualMinutes
                         && fan.RunDrop is { } drop && drop.From >= usualRpm * (1 - SteadyUsualShare)
                         && drop.To <= usualRpm * (1 - SteadySlowerShare) && usualRpm - drop.To >= SteadySlowerRpm)
                {
                    list.Add(new Insight(IconWarn,
                        $"{fan.Name} on your motherboard slowed from {drop.From:N0} to {drop.To:N0} rpm while your PC was on. " + Check("its speed", false),
                        InsightTone.Warn, "fan-slower", 83, $"At a set {usualRpm:N0} rpm {usualLabel}"));
                    fanSaid.Add((fan.Name, fan.Hardware));
                }
            }

            // Slower than a few months ago at the same heat in the same game: wear or dust build up over months, too
            // slowly for the week before to show. A day counts with its week (a day alone is too little), and it's said
            // the first day it shows, not again each day after.
            if (context?.Fans is { } thenFans)
            {
                foreach (var fan in r.Fans.OrderByDescending(f => f.Gpu))
                {
                    if (fanSaid.Contains((fan.Name, fan.Hardware)) || (fan.Gpu && gpuSaid)) continue;
                    if (!thenFans.Curves.TryGetValue((fan.Name, fan.Hardware), out var thenCurve)) continue;
                    var was = fan with { Curve = thenCurve };
                    FanStat WithWeek(FanStat f) => isDay && usual.Fans.FirstOrDefault(u => u.Name == f.Name && u.Hardware == f.Hardware) is { } u ? MergeCurve(f, u) : f;
                    if (Slower(WithWeek(fan), was, OlderSlowerShare, OlderSlowerRpm) is not { } slow) continue;
                    if (previous?.Fans.FirstOrDefault(f => f.Name == fan.Name && f.Hardware == fan.Hardware) is { } before
                        && Slower(WithWeek(before), was, OlderSlowerShare, OlderSlowerRpm) is not null) continue;
                    string? app = FanSoftware.Among(running, fan.Gpu);
                    string part = fan.Follows == FanFollows.Cpu ? "CPU" : "GPU";
                    string who = fan.Gpu ? "Your GPU fans now spin" : $"{fan.Name} on your motherboard now spins";
                    string when = thenFans.Mid.Year == DateTime.Today.Year ? $"in {thenFans.Mid:MMMM}" : $"in {thenFans.Mid:MMMM yyyy}";
                    list.Add(new Insight(IconWarn,
                        $"{who} {slow.Percent:0}% slower at the same heat than {when}: {slow.Now:N0} rpm with the {part} at {Units.TempShort(slow.Temp)}, against {slow.Was:N0} rpm then. "
                        + $"If you changed the fan curve {(app is not null ? $"in {app}" : fan.Gpu ? "in the card's software" : "in the BIOS or a fan app")}, that's why; if not, check {(fan.Gpu ? "they're" : "it's")} clean and turning freely.",
                        slow.Temp >= (part == "GPU" ? GpuWarm : CpuWarm) ? InsightTone.Hot : InsightTone.Warn, "fan-older", 82,
                        $"{slow.NowMinutes} minutes of steady load at the same {part} temperatures now, {slow.WasMinutes} {when}"));
                    fanSaid.Add((fan.Name, fan.Hardware));
                    gpuSaid |= fan.Gpu;
                }
            }
        }

        // The internet: its own lines, with their own bars to clear (see NetInsights). A download while nobody was there is
        // news on the quietest day.
        if (r.Net is { } net) list.AddRange(net.Insights);

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
        var compare = ScreenComparison(r, previous, usual, haveUsual, inProgress, context?.Weekday);
        if (compare is { } said)
            list.Add(new Insight(IconScreen, said.Text, InsightTone.Neutral, "screen-compare", 65));

        // Records and streaks: what this day beat.
        foreach (var record in r.Records.Where(x => !peakRecords.Contains(x.Kind)))
        {
            // A hottest peak is only a record: the peak line (with its tone) says when a temperature is a worry.
            var (icon, text, tone, priority) = record.Kind switch
            {
                RecordKind.LongestGameSession => (IconGame, $"Longest gaming session in {Span(record.Days)}: {record.App}, {record.Value}.", InsightTone.Good, 66),
                RecordKind.HottestGpu => (IconTemp, $"Hottest GPU peak in {Span(record.Days)}: {record.Value}.", InsightTone.Neutral, 63),
                RecordKind.HottestCpu => (IconTemp, $"Hottest CPU peak in {Span(record.Days)}: {record.Value}.", InsightTone.Neutral, 62),
                _ => (IconScreen, $"Most screen time in {Span(record.Days)}: {record.Value}.", InsightTone.Neutral, 61),
            };
            list.Add(new Insight(icon, text, tone, "record", priority, $"Beats every day of the {record.Days} before"));
        }
        // Not next to "less than your usual Saturday": the two would say opposite things.
        if (r.StreakDays >= 3 && compare is not { Less: true })
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
            // Against a whole day's usual only for a whole day (a few hours picked on their own aren't one).
            if (r.Range == ReportRange.Day && context?.Weekday is { GamingSec: > 0 } wd)
            {
                double diff = total - wd.GamingSec;
                if (Math.Abs(diff) >= MinTimeDiff && (!inProgress || diff > 0))
                    text += $" That's {Units.Duration(Math.Abs(diff))} {(diff > 0 ? "more" : "less")} than your usual {wd.Day} ({Units.Duration(wd.GamingSec)}).";
            }
            else if (r.Range == ReportRange.Day && haveUsual)
            {
                double avg = usual!.GamingSec / usualDays;
                double diff = total - avg;
                if (avg > 0 && Math.Abs(diff) >= MinTimeDiff && (!inProgress || diff > 0))
                    text += $" That's {Units.Duration(Math.Abs(diff))} {(diff > 0 ? "more" : "less")} than your daily average ({Units.Duration(avg)}).";
            }
            // The longest session, only its part in this period (one from the night before counts from midnight). A game
            // still being played has the session that matters, not over yet: none is named until it is. Nor when it was
            // all of one game's play: that says nothing new (with several games, it says which one took the time).
            var sessions = r.Sessions.Where(s => s.IsGame).Select(s => (Session: s, Sec: InPeriod(r, s))).ToList();
            if (r.GameOngoing is null && sessions.Count > 0
                && sessions.MaxBy(x => x.Sec) is { Session: { } longest, Sec: >= 20 * 60 } best
                && (games.Count > 1 || best.Sec < total - 5 * 60))
                text += longest.Start < r.From
                    ? $" Longest session: {longest.Name}, {Units.Duration(best.Sec)}, carried on from the night before."
                    : $" Longest session: {longest.Name}, {Units.Duration(best.Sec)} starting {When(r, longest.Start, at: false)}.";
            list.Add(new Insight(IconGame, text, InsightTone.Neutral, "gaming", 64));
        }

        // Longest stretch without a break.
        if (r.LongestStretch is { } stretch && stretch.Seconds >= 90 * 60)
        {
            string app = stretch.App is null ? "" : $", mostly {stretch.App}";
            string range = isDay ? $"{stretch.Start:h:mm tt} – {Until(r, stretch.End)}" : $"{stretch.Start:ddd d MMM, h:mm tt}";
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
            // Where the heat went, not a warning: a GPU runs over 75° in most games (the limit and peak lines warn).
            list.Add(new Insight(icon, $"Your {part} spent {Minutes(hot)} over {Units.TempShort(Report.HotLine)}, {who}.",
                InsightTone.Neutral, "hot-app", priority, string.Join(" · ", by.Take(4).Select(x => $"{x.App} {x.Minutes} min"))));
        }
        HotBy("GPU", IconGpu, r.GpuHotMinutes, r.GpuHotByApp, 62);
        HotBy("CPU", IconCpu, r.CpuHotMinutes, r.CpuHotByApp, 61);

        // Heavy work done behind the app you were using.
        if (r.BackgroundWork is { } bg)
            list.Add(new Insight(bg.Gpu ? IconGpu : IconCpu,
                $"{bg.App} ran your {(bg.Gpu ? "GPU" : "CPU")} hard for {Minutes(bg.Minutes)} from {When(r, bg.Start, at: false)} while you were in {bg.FrontApp}.",
                InsightTone.Neutral, "background-work", 60,
                $"{When(r, bg.Start, at: false)} – {When(r, bg.Start.AddMinutes(bg.Minutes), at: false)}"));

        // The game that works the GPU hardest, from steady play only (a warm-up or a menu makes any game look cooler).
        // Heavier, not hotter: that's the game, not the PC.
        var steadyGames = r.Steady.Where(s => s.Minutes >= GameHeatMinutes && r.Apps.Any(a => a.Id == s.AppId && a.Category == AppCategory.Game)).ToList();
        if (steadyGames.Count >= 2)
        {
            var hottest = steadyGames.MaxBy(g => g.Gpu)!;
            var others = steadyGames.Where(g => g != hottest).ToList();
            double othersGpu = others.Sum(g => g.Gpu * g.Minutes) / others.Sum(g => g.Minutes);
            if (hottest.Gpu - othersGpu >= MinGameHeatDiff)
                list.Add(new Insight(IconGame, $"{hottest.App} works your GPU hardest: {Units.TempShort(hottest.Gpu)} in steady play, against {Units.TempShort(othersGpu)} in your other games.",
                    InsightTone.Neutral, "game-heat", 52, Join(steadyGames.Select(g => $"{g.App} {Units.TempShort(g.Gpu)}"))));
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

        // Left on with nobody there: only long stretches with nothing working (a short break, a render, a download aren't).
        if (r.LongAwaySec >= 3600 && isDay)
            list.Add(new Insight(IconAway,
                $"Your PC sat on with nobody there for {Units.Duration(r.LongAwaySec)}{(r.Range != ReportRange.Day ? "" : inProgress ? " today" : " that day")}.",
                InsightTone.Neutral, "away", 42, $"Stretches of {Report.LongAwayMinutes} minutes or more without use or heavy work"));

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

    /// <summary>How this period's screen time compares, and whether it's less (see the streak).</summary>
    private static (string Text, bool Less)? ScreenComparison(Report r, Report? previous, Report? usual, bool haveUsual, bool inProgress, WeekdayUsual? weekday)
    {
        // A day against the same weekday over recent weeks, when there are enough: a Saturday isn't a Tuesday.
        if (r.Range == ReportRange.Day && weekday is not null)
        {
            double diff = r.ActiveSec - weekday.ActiveSec;
            if (Math.Abs(diff) >= MinTimeDiff && (!inProgress || diff > 0))
                return (inProgress
                    ? $"You're already {Units.Duration(diff)} past your usual {weekday.Day} of {Units.Duration(weekday.ActiveSec)}."
                    : $"That's {Units.Duration(Math.Abs(diff))} {(diff > 0 ? "more" : "less")} than your usual {weekday.Day} ({Units.Duration(weekday.ActiveSec)}).", diff < 0);
            if (inProgress) return null;
        }
        if (r.Range == ReportRange.Day && haveUsual)
        {
            double avg = usual!.ActiveSec / usual.DaysWithData;
            double diff = r.ActiveSec - avg;
            // A day in progress can only be compared once it has passed your usual.
            if (Math.Abs(diff) >= MinTimeDiff && (!inProgress || diff > 0))
                return (inProgress
                    ? $"You're already {Units.Duration(diff)} past your daily average of {Units.Duration(avg)}."
                    : $"That's {Units.Duration(Math.Abs(diff))} {(diff > 0 ? "more" : "less")} than your daily average of {Units.Duration(avg)}.", diff < 0);
        }
        // A whole month or year against the one before, a day at a time: February isn't short on use for being short.
        if (!inProgress && r.Range is ReportRange.Month or ReportRange.Year && previous is { HasData: true, ActiveSec: > 0 })
        {
            double perDay = r.ActiveSec / (r.To - r.From).TotalDays - previous.ActiveSec / (previous.To - previous.From).TotalDays;
            if (Math.Abs(perDay) < MinDailyDiff) return null;
            return ($"That's {Units.Duration(Math.Abs(perDay))} a day {(perDay > 0 ? "more" : "less")} than {(r.Range == ReportRange.Year ? "the year before" : "the month before")}.", perDay < 0);
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
            return ($"That's {Units.Duration(Math.Abs(diff))} {(diff > 0 ? "more" : "less")} screen time than {than}.", diff < 0);
        }
        return null;
    }

    private const double MinDailyDiff = 10 * 60;

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
            : r.LastActive >= r.To
                ? $"Your day ran from {start:h:mm tt} until past midnight."
                : $"Your day ran from {start:h:mm tt} to {(r.LastActive ?? start):h:mm tt}.";
        return late is null ? day : $"{late} {day}";
    }

    /// <summary>When something ended: its time, or "past midnight" when it ran to the end of the day and on.</summary>
    private static string Until(Report r, DateTime end) => end >= r.To && r.Range == ReportRange.Day ? "past midnight" : end.ToString("h:mm tt");

    /// <summary>A session's active time within the period (one from the night before counts from midnight).</summary>
    private static double InPeriod(Report r, SessionInfo s)
    {
        var from = s.Start > r.From ? s.Start : r.From;
        var to = s.End < r.To ? s.End : r.To;
        return Math.Max(0, Math.Min(s.ActiveSec, (to - from).TotalSeconds));
    }

    private static string When(Report r, DateTime time, bool at = true) =>
        (at ? "at " : "") + (r.Range == ReportRange.Day || (r.Range == ReportRange.Custom && r.From.Date == r.To.AddTicks(-1).Date)
            ? time.ToString("h:mm tt") : time.ToString("ddd d MMM, h:mm tt"));

    private static string PeriodWord(Report r) => r.Range switch
    {
        ReportRange.Day => "day", ReportRange.Week => "week", ReportRange.Year => "year", ReportRange.All or ReportRange.Custom => "stretch", _ => "month",
    };

    private const int MinHotMinutes = 10, GameHeatMinutes = 30, HotSpotMinutes = 20;
    private const double MinGameHeatDiff = 5;

    // Drift: steady minutes of one game needed now and then (then over a few days: one evening is a mood, not a
    // baseline), minutes at rest on both sides, and the least difference worth a line: in all, and beyond what the room
    // explains (rest temperatures warm with dust too, if less, so taking all of theirs off takes some of the dust's).
    private const int DriftNowMinutes = 60, DriftThenMinutes = 120, DriftThenDays = 3, DriftRestMinutes = 60;
    private const double DriftMinDiff = 5, DriftMinBeyondRoom = 3;

    /// <summary>The same game at a GPU power this far apart isn't the same load (new settings, another card, a power limit).</summary>
    private const double DriftPowerShare = 0.15;

    // A fan stopped: minutes in a row at 0 rpm when it should turn, and how reliably it turned in the week before.
    private const int GpuFanStopMinutes = 3, FanStopMinutes = 10, FanUsualMinutes = 60;
    private const double FanUsualStoppedShare = 0.02;

    // A fan slower than before: at the same heat, a fifth slower and 250 rpm or more, over half an hour or more of steady
    // load at temperatures both periods saw; at a set speed, 15% and 150 rpm.
    private const double CurveSlowerShare = 0.2, SteadySlowerShare = 0.15, SteadyUsualShare = 0.05;
    private const int CurveSlowerRpm = 250, SteadySlowerRpm = 150, CurveMinutes = 30, CurveBinMinutes = 5;

    // Against a few months ago: many more minutes on each side, and wear builds slowly, so 15% and 200 rpm.
    private const double OlderSlowerShare = 0.15;
    private const int OlderSlowerRpm = 200;

    /// <summary>A fan's curve with another period's added in (a day with its week): each step's minutes and speeds together.</summary>
    internal static FanStat MergeCurve(FanStat a, FanStat b)
    {
        var bins = new Dictionary<(long App, int Temp), FanBin>(a.Curve);
        foreach (var (k, v) in b.Curve)
            bins[k] = bins.TryGetValue(k, out var x) ? new FanBin(x.Minutes + v.Minutes, (x.Rpm * x.Minutes + v.Rpm * v.Minutes) / (x.Minutes + v.Minutes)) : v;
        return a with { Curve = bins };
    }

    /// <summary>
    /// A fan on a curve compared with the days before at the temperatures both saw under steady load in the same app (each
    /// step weighed by its minutes now): how much slower, at what temperature, or null when it isn't clearly slower or there's too
    /// little to compare.
    /// </summary>
    internal static (double Percent, double Now, double Was, double Temp, int NowMinutes, int WasMinutes)? Slower(FanStat now, FanStat was,
        double share = CurveSlowerShare, int rpm = CurveSlowerRpm)
    {
        if (now.Follows is not (FanFollows.Gpu or FanFollows.Cpu) || was.Follows != now.Follows) return null;
        var both = now.Curve.Where(b => b.Value.Minutes >= CurveBinMinutes && was.Curve.TryGetValue(b.Key, out var w) && w.Minutes >= CurveBinMinutes)
            .Select(b => (Temp: b.Key.Temp + FanCurves.BinDegrees / 2.0, Now: b.Value, Was: was.Curve[b.Key])).ToList();
        int nowMinutes = both.Sum(b => b.Now.Minutes), wasMinutes = both.Sum(b => b.Was.Minutes);
        if (nowMinutes < CurveMinutes || wasMinutes < CurveMinutes) return null;
        double rpmNow = both.Sum(b => b.Now.Rpm * b.Now.Minutes) / nowMinutes;
        double rpmWas = both.Sum(b => b.Was.Rpm * b.Now.Minutes) / nowMinutes;
        double temp = both.Sum(b => b.Temp * b.Now.Minutes) / nowMinutes;
        if (rpmNow > rpmWas * (1 - share) || rpmWas - rpmNow < rpm) return null;
        return ((1 - rpmNow / rpmWas) * 100, rpmNow, rpmWas, temp, nowMinutes, wasMinutes);
    }

    private static bool SamePower(SteadyLoad a, SteadyLoad b) =>
        a.GpuPower is not double pa || b.GpuPower is not double pb || pb <= 0 || Math.Abs(pa - pb) / pb <= DriftPowerShare;

    /// <summary>A day's steady load with the week's before it: one app, one line.</summary>
    private static List<SteadyLoad> MergeSteady(List<SteadyLoad> a, List<SteadyLoad> b) =>
        [.. a.Concat(b).GroupBy(s => s.AppId).Select(g => g.Aggregate(SteadyLoad.Merge))];

    private static LoadTemps? MergeTemps(LoadTemps? a, LoadTemps? b)
    {
        if (a is null || b is null) return a ?? b;
        int n = a.Minutes + b.Minutes;
        static double? W(double? x, int nx, double? y, int ny) => x is null ? y : y is null ? x : (x * nx + y * ny) / (nx + ny);
        return new LoadTemps(W(a.Cpu, a.Minutes, b.Cpu, b.Minutes), W(a.Gpu, a.Minutes, b.Gpu, b.Minutes), n);
    }

    /// <summary>The difference between two temperatures as they're shown (each rounded), so "3° hotter (63° vs 60°)" adds up.</summary>
    private static string ShownDiff(double a, double b)
    {
        static double Shown(double c) => Math.Round(Units.Fahrenheit ? c * 9 / 5 + 32 : c);
        return $"{Math.Abs(Shown(a) - Shown(b)):0}°";
    }

    private static AppStat? TopApp(Report? r) => r?.Apps.FirstOrDefault(a => a.ActiveSec > 0 && a.Category != AppCategory.System);

    // For crashes, heat is only a pattern at the user's own alert limits: 83° is where many GPUs sit in every game.

    private static AppStat? IdleHog(Report? r) => r?.Apps
        .Where(a => a.BackgroundSec + a.MinimizedSec >= 3600 && a.ActiveSec < (a.BackgroundSec + a.MinimizedSec) / 4)
        .MaxBy(a => a.BackgroundSec + a.MinimizedSec);

    private static AppStat? MemHog(Report? r) => r?.Apps.Where(a => a.MemMax is not null).MaxBy(a => a.MemMax);

    /// <summary>
    /// What the crashes had in common, from the minutes before each: heat at the user's alert limits, or one app in
    /// front (other than the one that crashed: that one being in front goes without saying).
    /// </summary>
    private static string CrashPattern(Report r, AlertSettings alerts)
    {
        var contexts = r.Crashes.Select(c => r.CrashContexts.GetValueOrDefault(c.Id)).ToList();
        if (r.Crashes.Count == 1)
        {
            var ctx = contexts[0];
            if (ctx?.GpuBefore >= alerts.GpuLimit) return $" The GPU was at {Units.TempShort(ctx.GpuBefore)} just before.";
            if (ctx?.CpuBefore >= alerts.CpuLimit) return $" The CPU was at {Units.TempShort(ctx.CpuBefore)} just before.";
            return "";
        }
        string all = r.Crashes.Count == 2 ? "Both" : $"All {r.Crashes.Count}";
        var hotGpu = contexts.Where(c => c?.GpuBefore >= alerts.GpuLimit).Select(c => c!.GpuBefore!.Value).ToList();
        if (hotGpu.Count == r.Crashes.Count) return $" {all} came minutes after the GPU was over {Units.TempShort(hotGpu.Min())}.";
        var hotCpu = contexts.Where(c => c?.CpuBefore >= alerts.CpuLimit).Select(c => c!.CpuBefore!.Value).ToList();
        if (hotCpu.Count == r.Crashes.Count) return $" {all} came minutes after the CPU was over {Units.TempShort(hotCpu.Min())}.";
        var fronts = contexts.Select(c => c?.FrontApp).ToList();
        if (fronts[0] is long front && fronts.All(f => f == front) && r.Apps.FirstOrDefault(a => a.Id == front) is { } app
            && !r.Crashes.All(c => c.AppExe.Equals(app.Exe, StringComparison.OrdinalIgnoreCase)))
            return $" {all} happened while {app.Name} was in front.";
        return "";
    }

    private static string CrashDetail(Report r) => string.Join(" · ", r.Crashes.Take(4).Select(c =>
        r.CrashContexts.GetValueOrDefault(c.Id) is { } ctx && (ctx.CpuBefore is not null || ctx.GpuBefore is not null)
            ? $"{c.Time:d MMM h:mm tt}: {string.Join(", ", new[] { ctx.CpuBefore is { } cb ? $"CPU {Units.TempShort(cb)}" : null, ctx.GpuBefore is { } gb ? $"GPU {Units.TempShort(gb)}" : null }.OfType<string>())} just before"
            : $"{c.Time:d MMM h:mm tt}"));

    /// <summary>"a month", "three months", "a year": how far back a record holds.</summary>
    private static string Span(int days) => days switch { 30 => "a month", 90 => "three months", _ => "a year" };

    private static string Nth(int n) => n switch
    {
        3 => "Third day", 4 => "Fourth day", 5 => "Fifth day", 6 => "Sixth day", 7 => "Seventh day", _ => $"{n} days",
    };

    /// <summary>"in June" (the middle of the stretch), with the year when it isn't this one.</summary>
    private static string MonthsAgo(ThenHeat then)
    {
        var mid = then.From.AddDays((then.To - then.From).TotalDays / 2);
        return mid.Year == DateTime.Today.Year ? $"in {mid:MMMM}" : $"in {mid:MMMM yyyy}";
    }

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
