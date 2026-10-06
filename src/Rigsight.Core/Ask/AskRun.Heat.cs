using System.Text.RegularExpressions;
using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;

namespace Rigsight.Core.Ask;

internal sealed partial class AskRun
{
    /// <summary>Days looked at for "how hot does this game make my PC" without a time: enough play to say.</summary>
    private const int AppHeatDays = 30;

    private static readonly string[] HeatKeys = ["over-limit", "throttle", "drift", "hotspot", "game-heat", "hot-app"];

    /// <summary>"What was the hottest day": the day with the highest reading in a period, from each day's highs.</summary>
    private AskAnswer HottestDay()
    {
        var period = PeriodOr(() => AskTime.LastDays(30, Now));
        var a = new AskAnswer();
        bool cpu = _q.Part == AskPart.Cpu;
        var days = (_db.GetSystemDays(TimeUtil.ToUnix(period.From.Date), TimeUtil.ToUnix(period.To)) ?? []).Where(d => (cpu ? d.CpuTempMax : d.GpuTempMax ?? d.CpuTempMax) is not null).ToList();
        if (days.Count == 0)
        {
            a.Lead = $"No temperatures are on record {period.Label}.";
            return a;
        }
        double Of(SystemDay d) => (cpu ? d.CpuTempMax : d.GpuTempMax ?? d.CpuTempMax)!.Value;
        var top = days.MaxBy(Of)!;
        var day = TimeUtil.FromUnix(top.Day);
        var read = new List<string>();
        if (top.GpuTempMax is double g && !cpu) read.Add($"the GPU reached **{Temp(g)}**");
        if (top.CpuTempMax is double c && _q.Part != AskPart.Gpu) read.Add($"the CPU reached **{Temp(c)}**");
        a.Lead = $"The hottest day {period.Label} was **{AskTime.DayText(day, Now)}**: {Join(read)}.";
        foreach (var d in days.Where(d => d != top).OrderByDescending(Of).Take(3))
            a.Points.Add(new($"**{AskTime.DayText(TimeUtil.FromUnix(d.Day), Now)}**: {Temp(Of(d))}"));
        if (a.Points.Count > 0) a.PointsTitle = "Then";
        a.Note = "A day's high is its hottest moment, and depends on what was running that day.";
        a.Links.Add(new("Open Reports", "reports", day));
        a.FollowUps.Add($"How hot did it get {AskTime.Day(day, Now).Label}?");
        return a;
    }

    /// <summary>"Which game runs hottest": games by the GPU temperature they hold at steady heavy load.</summary>
    private AskAnswer HottestGame()
    {
        var period = PeriodOr(() => AskTime.LastDays(AppHeatDays, Now));
        var r = ReportFor(period);
        if (NothingRecorded(period, r) is { } none) return none;
        var a = new AskAnswer();
        a.Links.Add(new("Open Reports", "reports"));
        var steady = r.Steady.Where(s => s.Minutes >= 20).OrderByDescending(s => s.Gpu).ToList();
        if (steady.Count == 0)
        {
            a.Lead = $"No game worked the graphics card hard for long enough {period.Label} to compare.";
            a.Note = "I rank games by the temperature the GPU holds once warmed up under heavy load: at least 20 minutes of it.";
            return a;
        }
        a.Lead = steady.Count == 1
            ? $"Only **{steady[0].App}** worked the graphics card hard {period.Label}: the GPU held {Temp(steady[0].Gpu)}."
            : $"**{steady[0].App}** runs hottest {period.Label}: the GPU holds {Temp(steady[0].Gpu)} at steady load.";
        if (steady.Count > 1)
            foreach (var s in steady.Take(6))
                a.Points.Add(new($"**{s.App}**: GPU {Temp(s.Gpu)}{(s.Cpu is double c ? $", CPU {Temp(c)}" : "")}{(s.GpuPower is double w ? $", {w:0} W" : "")}"));
        a.Note = "Each is the average once warmed up and at heavy GPU load, so a menu or a loading screen doesn't count.";
        a.FollowUps.Add($"How hot does {steady[0].App} make my PC?");
        return a;
    }

    private AskAnswer Temps()
    {
        string wording = _q.Text.ToLowerInvariant();
        if (_q.AppId is null && HottestDayWords().IsMatch(wording)) return HottestDay();
        if (_q.AppId is null && HottestGameWords().IsMatch(wording)) return HottestGame();
        var period = PeriodOr(() => _q.AppId is not null ? AskTime.LastDays(AppHeatDays, Now) : Today);
        var r = ReportFor(period);
        if (NothingRecorded(period, r) is { } none) return none;
        var a = new AskAnswer();
        a.Links.Add(new(IsToday(period) ? "Open Temperatures" : "Open Reports", IsToday(period) ? "temperatures" : "reports", period.IsDay && !IsToday(period) ? period.From : null));
        if (r.GpuTempPeak is null && r.CpuTempPeak is null)
        {
            a.Lead = $"No temperatures were recorded {period.Label}.";
            a.Note = "CPU temperatures need the agent to run with admin rights.";
            return a;
        }
        if (_q.AppId is long appId) return AppTemps(appId, period, r, a);

        bool wantGpu = _q.Part != AskPart.Cpu && r.GpuTempPeak is not null;
        bool wantCpu = _q.Part != AskPart.Gpu && r.CpuTempPeak is not null;
        if (!wantGpu && !wantCpu) (wantGpu, wantCpu) = (r.GpuTempPeak is not null, r.CpuTempPeak is not null);
        double gpuLimit = _s.Alerts.GpuLimit, cpuLimit = _s.Alerts.CpuLimit;
        int over = (wantGpu ? r.GpuOverLimitMin : 0) + (wantCpu ? r.CpuOverLimitMin : 0);
        bool throttled = wantGpu && r.GpuThrottle is not null;
        string lower = _q.Text.ToLowerInvariant();
        bool asksOk = OkWords().IsMatch(lower);

        string PeakOf(string part, Peak p) =>
            $"the {part} peaked at **{Temp(p.Value)}** ({AtIn(p.Time, period)}{(p.App is not null ? $", {PeakLine(p.App)}" : "")})";
        var peaks = new List<string>();
        if (wantGpu) peaks.Add(PeakOf("GPU", r.GpuTempPeak!));
        if (wantCpu) peaks.Add(PeakOf("CPU", r.CpuTempPeak!));
        string limits = wantGpu && wantCpu ? $"your limits ({Temp(gpuLimit)} for the GPU, {Temp(cpuLimit)} for the CPU)"
            : $"your {Temp(wantGpu ? gpuLimit : cpuLimit)} limit";

        // A moment's spike can touch the limit without a whole minute averaging there (which is what the alert goes by).
        bool atLimit = (wantGpu && r.GpuTempPeak!.Value >= gpuLimit) || (wantCpu && r.CpuTempPeak!.Value >= cpuLimit);
        // A long period (a year) is read from daily totals, which don't say how long a part stayed at its limit.
        bool coarse = ReportBuilder.IsLong(period.Range, period.From, period.To);
        bool spiked = over == 0 && atLimit;
        string Spike = coarse
            ? $"That is at or over {limits}. Over a period this long I can't say for how long: ask about one of its months to see."
            : $"That touched {limits} for a moment, but it didn't stay there: no whole minute averaged that high.";
        if (coarse && atLimit && asksOk) over = 1; // "is it OK" over a year with a reading at the limit: not a plain yes

        if (throttled || over > 0)
        {
            a.Lead = asksOk ? $"**It ran too hot at times {period.Label}.**" : $"**It ran hot {period.Label}.**";
            a.Paragraphs.Add($"{Join(peaks).ToUpperFirst()}.");
        }
        else if (asksOk)
        {
            a.Lead = $"**Temperatures look fine {period.Label}.**";
            a.Paragraphs.Add($"{Join(peaks).ToUpperFirst()}{(spiked ? "" : $", under {limits}")}.");
            if (spiked) a.Paragraphs.Add(Spike);
        }
        else
        {
            a.Lead = $"{period.Title}, {Join(peaks)}.";
            a.Paragraphs.Add(spiked ? Spike : $"That is under {limits}.");
        }

        // "Right now": the last minute on record comes first, the day's highs after it.
        if (_q.Now && IsToday(period) && _db.GetMinutes(TimeUtil.ToUnix(Now) - 240, TimeUtil.ToUnix(Now) + 60).LastOrDefault() is { } latest)
        {
            var read = new List<string>();
            if (wantGpu && latest.GpuTemp is double g) read.Add($"the GPU is at **{Temp(g)}**");
            if (wantCpu && latest.CpuTemp is double c) read.Add($"the CPU is at **{Temp(c)}**");
            if (read.Count > 0)
            {
                a.Paragraphs.Insert(0, a.Lead.Replace("**", "") is var day && day.StartsWith("Today, ", StringComparison.Ordinal) ? "Today so far, " + day[7..] : a.Lead);
                a.Lead = $"Right now {Join(read)} (the average of the last minute on record, {Clock(TimeUtil.FromUnix(latest.Ts))}).";
            }
        }

        if (wantGpu)
        {
            a.Facts.Add(new("GPU highest", Temp(r.GpuTempPeak!.Value), r.GpuOverLimitMin > 0 ? AskTone.Hot : AskTone.Neutral));
            if (r.GpuTempAvg is not null) a.Facts.Add(new("GPU average", Temp(r.GpuTempAvg)));
        }
        if (wantCpu)
        {
            a.Facts.Add(new("CPU highest", Temp(r.CpuTempPeak!.Value), r.CpuOverLimitMin > 0 ? AskTone.Hot : AskTone.Neutral));
            if (r.CpuTempAvg is not null) a.Facts.Add(new("CPU average", Temp(r.CpuTempAvg)));
        }

        // What the same analysis the Reports page runs found about heat.
        foreach (var i in r.Insights.Where(i => HeatKeys.Contains(i.Key)).Where(i => PartFits(i.Text, wantGpu, wantCpu)).Take(4))
            a.Points.Add(new(i.Text, ToneOf(i.Tone)));

        if (TrendWords().IsMatch(lower) && !r.Insights.Any(i => i.Key == "drift"))
            a.Paragraphs.Add("On running hotter than it used to: I compare the same game at the same load now and a few months ago, with the room's warmth taken out. Nothing stands out there, or there isn't yet enough of the same game on both sides to say.");
        if (throttled || over > 0)
            a.Advice = "Check that the fans turn and that vents and heatsinks are clear of dust. If it's an older PC, fresh thermal paste can help.";

        var hotGame = r.Steady.FirstOrDefault();
        if (hotGame is not null) a.FollowUps.Add($"How hot does {hotGame.App} make my PC?");
        if (!IsToday(period)) a.FollowUps.Add("How hot did it get today?");
        else a.FollowUps.Add("How hot did it get yesterday?");
        a.FollowUps.Add("Are my fans working?");
        return a;
    }

    /// <summary>"while playing Rematch", by the kind of app it is.</summary>
    private string PeakLine(string app)
    {
        var row = _apps.Values.FirstOrDefault(x => NameOf(x.Id) == app);
        return (PeakWords.Line(0, app, null, row is null ? null : CategoryOf(row)) ?? "").ToLowerFirstWordAny();
    }

    private static bool PartFits(string text, bool gpu, bool cpu) =>
        (gpu && cpu) || (gpu && !text.Contains("CPU", StringComparison.Ordinal)) || (cpu && !text.Contains("GPU", StringComparison.Ordinal));

    private AskAnswer AppTemps(long appId, AskPeriod period, Report r, AskAnswer a)
    {
        string app = NameOf(appId);
        var stat = r.Apps.FirstOrDefault(x => x.Id == appId);
        if (stat is null || stat.ActiveSec < 60 || (stat.GpuTempMax is null && stat.CpuTempMax is null))
        {
            a.Lead = $"{app} wasn't in use {period.Label}, so there are no temperatures for it.";
            if (LastSession(appId) is { } last) a.FollowUps.Add($"How hot did {app} make my PC {AskTime.Day(TimeUtil.FromUnix(last.Start), Now).Label}?");
            return a;
        }

        double gpuLimit = _s.Alerts.GpuLimit, cpuLimit = _s.Alerts.CpuLimit;
        bool gpuOver = stat.GpuTempMax >= gpuLimit, cpuOver = stat.CpuTempMax >= cpuLimit;
        var steady = r.Steady.FirstOrDefault(x => x.AppId == appId);
        var read = new List<string>();
        if (stat.GpuTempMax is not null && _q.Part != AskPart.Cpu) read.Add($"the GPU reached **{Temp(stat.GpuTempMax)}**{(stat.GpuTempAvg is not null ? $" and averaged {Temp(stat.GpuTempAvg)}" : "")}");
        if (stat.CpuTempMax is not null && _q.Part != AskPart.Gpu) read.Add($"the CPU reached **{Temp(stat.CpuTempMax)}**{(stat.CpuTempAvg is not null ? $" and averaged {Temp(stat.CpuTempAvg)}" : "")}");
        a.Lead = $"In {app} {period.Label}, {Join(read)}.";
        a.Paragraphs.Add(gpuOver || cpuOver
            ? $"That is over your {(gpuOver && cpuOver ? "limits" : "limit")}: {Join([.. new[] { gpuOver ? $"{Temp(gpuLimit)} for the GPU" : null, cpuOver ? $"{Temp(cpuLimit)} for the CPU" : null }.OfType<string>()])}."
            : $"Both are under your limits ({Temp(gpuLimit)} for the GPU, {Temp(cpuLimit)} for the CPU), over {Dur(stat.ActiveSec)} of use.");
        if (steady is not null && steady.Minutes >= 20)
            a.Paragraphs.Add($"Once warmed up and working hard, the GPU held around {Temp(steady.Gpu)} ({Dur(steady.Minutes * 60.0)} of steady load).");
        foreach (var i in r.Insights.Where(i => i.Key is "drift" or "game-heat" or "throttle" && i.Text.Contains(app, StringComparison.OrdinalIgnoreCase)).Take(2))
            a.Points.Add(new(i.Text, ToneOf(i.Tone)));
        a.FollowUps.Add($"How long did I play {app} {period.Label}?");
        a.FollowUps.Add($"Has {app} crashed {period.Label}?");
        return a;
    }

    private SessionRow? LastSession(long appId) =>
        _db.GetRecentSessions(appId, 0, TimeUtil.ToUnix(Now) + 60, ReportBuilder.MinSessionSec, 1).FirstOrDefault();

    // ── Fans ────────────────────────────────────────────────────────────

    private AskAnswer Fans()
    {
        var period = PeriodOr(() => Today);
        var r = ReportFor(period);
        if (NothingRecorded(period, r) is { } none) return none;
        var a = new AskAnswer();
        a.Links.Add(new("Open Fans", "fans"));

        var fans = _db.GetFans();
        var days = _db.GetFanDays(TimeUtil.ToUnix(period.From.Date), TimeUtil.ToUnix(period.To));
        var speeds = days.GroupBy(d => d.Fan).Select(g => (Fan: fans.FirstOrDefault(f => f.Id == g.Key), Avg: g.Sum(d => d.RpmSum) / Math.Max(1, g.Sum(d => d.RpmN)), Max: g.Max(d => d.RpmMax), N: g.Sum(d => d.RpmN)))
            .Where(x => x.Fan is not null && x.N > 0).OrderBy(x => x.Fan!.Hardware).ThenBy(x => x.Fan!.Name).ToList();
        if (speeds.Count == 0)
        {
            a.Lead = $"No fan speeds were recorded {period.Label}.";
            a.Note = "Fan speeds need the agent to run with admin rights, and a motherboard or graphics card that reports them.";
            return a;
        }

        var found = r.Insights.Where(i => i.Key.StartsWith("fan-", StringComparison.Ordinal)).ToList();
        // A header with nothing plugged in reads 0 the whole time: not a fan.
        speeds = [.. speeds.Where(x => x.Max > 0)];
        int turning = speeds.Count;
        if (turning == 0)
        {
            a.Lead = $"No fan turned {period.Label}, by the speeds recorded.";
            a.Note = "Some fans don't report their speed, and graphics card fans stop on purpose when the card is cool.";
            return a;
        }
        if (found.Count > 0)
        {
            a.Lead = found.Count == 1 ? "**One fan needs a look.**" : $"**{found.Count} things about the fans need a look.**";
            foreach (var i in found) a.Points.Add(new(i.Text, ToneOf(i.Tone)));
        }
        else
        {
            a.Lead = $"**Your fans look fine {period.Label}.** {Count(turning, "fan was", "fans were")} turning, and none stopped when it shouldn't have.";
        }
        bool dupNames = speeds.GroupBy(x => x.Fan!.Name).Any(g => g.Count() > 1);
        foreach (var x in speeds.Take(8))
        {
            string name = dupNames ? $"{x.Fan!.Name} ({x.Fan.Hardware})" : x.Fan!.Name;
            a.Facts.Add(new(name, $"{x.Avg:N0} rpm", AskTone.Neutral));
        }
        if (LoudWords().IsMatch(_q.Text.ToLowerInvariant()))
        {
            var fastest = speeds.MaxBy(x => x.Max);
            a.Paragraphs.Add($"On noise: fans speed up as parts heat up. The fastest {period.Label} was {fastest.Fan!.Name} at {fastest.Max:N0} rpm"
                + (r.GpuTempPeak is { } p ? $"; the GPU peaked at {Temp(p.Value)}{(p.App is not null ? $" {PeakLine(p.App)}" : "")}." : "."));
        }
        a.Note = "Speeds are averages while the PC was on. Some graphics card fans stop on purpose when the card is cool.";
        // Only the graphics card's fans on record: say so, or it reads as if the PC had no others.
        if (speeds.All(x => IsGpuFan(x.Fan!)))
            a.Paragraphs.Add("These are the graphics card's fans only. The CPU and case fans aren't being read: that needs the agent to run with admin rights, and a motherboard that reports them.");
        a.FollowUps.Add($"How hot did it get {period.Label}?");
        return a;
    }

    /// <summary>A fan on the graphics card (the sensors name them "GPU Fan", on the card's own hardware).</summary>
    private static bool IsGpuFan(FanRow fan) =>
        fan.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase) || fan.Hardware.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
        || fan.Hardware.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || fan.Hardware.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
        || fan.Hardware.Contains("AMD R", StringComparison.OrdinalIgnoreCase) || fan.Hardware.Contains("Intel Arc", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\b(overheat\w*|too hot|too warm|ok|okay|fine|safe|normal|worry|bad|healthy|good|high|problem|dangerous|alright)\b")]
    private static partial Regex OkWords();

    [GeneratedRegex(@"\b(hotter than|warmer than|than before|than usual|than it used to|getting (worse|hotter|warmer)|over time|trend|used to)\b")]
    private static partial Regex TrendWords();

    [GeneratedRegex(@"\b(hottest|warmest) (day|date)\b|\b(which|what) (day|date)\b")]
    private static partial Regex HottestDayWords();

    [GeneratedRegex(@"\b(which|what) (game|app|program)s?\b.*\b(hottest|hot|heats?|warm\w*|demanding|heaviest|hardest)\b|\b(hottest|heaviest|most demanding) (game|app)\b")]
    private static partial Regex HottestGameWords();

    [GeneratedRegex(@"\b(loud|noisy|noise|louder)\b")]
    private static partial Regex LoudWords();
}

internal static partial class AskTextMore
{
    public static string ToLowerFirstWordAny(this string s) => s.Length > 0 ? char.ToLowerInvariant(s[0]) + s[1..] : s;
}
