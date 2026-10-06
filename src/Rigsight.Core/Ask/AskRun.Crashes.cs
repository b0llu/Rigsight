using System.Text.RegularExpressions;
using Rigsight.Core.Data;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Ask;

internal sealed partial class AskRun
{
    /// <summary>Days looked back for "why did it crash" without a time, and for how often a problem repeats.</summary>
    private const int CrashLookBack = 30;

    /// <summary>A driver, an update or a part changed this long before a problem is worth naming as a possible cause.</summary>
    private const int SuspectDays = 7;

    /// <summary>How bad a problem is: the PC going down outranks one app closing.</summary>
    private static int Severity(CrashEvent e) => e.Kind switch
    {
        CrashKind.SystemCrash => 5,
        CrashKind.UnexpectedShutdown => e.Moment == PowerMoment.Running ? 4 : 0,
        CrashKind.GpuDriverReset => 3,
        CrashKind.AppCrash => 2,
        _ => 1,
    };

    private static bool IsPcLevel(CrashEvent e) => e.Kind is CrashKind.SystemCrash or CrashKind.GpuDriverReset || (e.Kind == CrashKind.UnexpectedShutdown && e.Moment == PowerMoment.Running);

    private string CrashName(CrashEvent e) => string.IsNullOrEmpty(e.AppExe) ? "an app" : NameOfExe(e.AppExe, e.AppPath);

    /// <summary>A crash title in the middle of a sentence: "graphics driver reset", but still "Windows crashed" and "Rematch crashed".</summary>
    private static string Mid(string title) =>
        title.StartsWith("Graphics ", StringComparison.Ordinal) || title.StartsWith("Taskbar ", StringComparison.Ordinal) || title.StartsWith("A ", StringComparison.Ordinal)
            ? char.ToLowerInvariant(title[0]) + title[1..] : title.StartsWith("PC ", StringComparison.Ordinal) ? "the " + title : title;

    private CrashExplanation Explain(CrashEvent e) => CrashExplainer.Explain(e, string.IsNullOrEmpty(e.AppExe) ? null : CrashName(e));

    private List<CrashEvent> CrashesIn(DateTime from, DateTime to) =>
        [.. _db.GetCrashes(TimeUtil.ToUnix(from), TimeUtil.ToUnix(to)).Where(c => !_s.IsCrashMuted(c.AppExe))];

    /// <summary>The kinds of problem a question's words single out ("blue screen", "froze"), if any.</summary>
    private (Func<CrashEvent, bool> Match, string One, string Many)? KindAsked()
    {
        string t = _q.Text.ToLowerInvariant();
        if (BlueScreenWords().IsMatch(t)) return (e => e.Kind == CrashKind.SystemCrash, "blue screen", "blue screens");
        if (ResetWords().IsMatch(t)) return (e => e.Kind == CrashKind.GpuDriverReset, "graphics driver reset", "graphics driver resets");
        if (ShutdownWords().IsMatch(t)) return (e => e.Kind is CrashKind.UnexpectedShutdown or CrashKind.SystemCrash, "sudden shutdown or blue screen", "sudden shutdowns or blue screens");
        if (FreezeWords().IsMatch(t)) return (e => e.Kind == CrashKind.AppHang, "freeze", "freezes");
        return null;
    }

    private AskAnswer Crashes()
    {
        var asked = _q.Period;
        var period = asked ?? AskTime.LastDays(CrashLookBack, Now);
        if (asked is null) Used = period;

        var all = CrashesIn(period.From, period.To);
        string of = "";
        if (_q.AppId is long appId && _apps.TryGetValue(appId, out var row))
        {
            all = [.. all.Where(c => string.Equals(c.AppExe, row.Exe, StringComparison.OrdinalIgnoreCase))];
            of = $" of {_q.App}";
        }
        var kind = KindAsked();
        var matching = kind is { } k ? all.Where(k.Match).ToList() : all;
        var a = new AskAnswer();
        a.Links.Add(new("Open Crashes", "crashes", period.IsDay ? period.From : null));

        if (matching.Count == 0)
        {
            string what = kind is { } named ? named.Many : _q.App is not null ? $"crashes{of}" : "crashes or sudden shutdowns";
            a.Lead = $"No {what} {period.Label}.";
            if (kind is not null && all.Count > 0)
                a.Paragraphs.Add($"There {(all.Count == 1 ? "was" : "were")} {Tally(all)} in that time, though.");
            else if (LastBefore(period.From, kind?.Match) is { } earlier)
            {
                a.Paragraphs.Add($"The last {(kind is { } asking ? asking.One : "problem")} before that: {Mid(Explain(earlier).Title)}, {DayWord(earlier.Time)}.");
                a.FollowUps.Add($"Why did {(IsPcLevel(earlier) ? "my PC" : CrashName(earlier))} crash {DayWord(earlier.Time)}?");
            }
            else if (asked is null) a.Paragraphs.Add("None are on record before that either.");
            a.FollowUps.Add("What changed on my PC this week?");
            return a;
        }

        return _q.Why ? Diagnose(matching, period, asked is not null, a) : ListCrashes(matching, period, of, a);
    }

    private CrashEvent? LastBefore(DateTime before, Func<CrashEvent, bool>? match = null)
    {
        long to = TimeUtil.ToUnix(before);
        return _db.GetCrashes(0, to).Where(c => !_s.IsCrashMuted(c.AppExe) && Severity(c) > 0 && (match is null || match(c)))
            .Where(c => _q.AppId is not long id || (_apps.TryGetValue(id, out var row) && string.Equals(c.AppExe, row.Exe, StringComparison.OrdinalIgnoreCase)))
            .MaxBy(c => c.Ts);
    }

    /// <summary>"2 app crashes, 1 graphics driver reset", worst first.</summary>
    private static string Tally(IEnumerable<CrashEvent> crashes) => string.Join(", ",
        crashes.GroupBy(c => c.Kind).OrderByDescending(g => Severity(g.First())).Select(g => CrashWords.Count(g.Key, g.Count())));

    private AskAnswer ListCrashes(List<CrashEvent> crashes, AskPeriod period, string of, AskAnswer a)
    {
        bool anyPc = crashes.Any(IsPcLevel);
        // "Which app crashes the most": by app.
        if (MostWords().IsMatch(_q.Text.ToLowerInvariant()) && crashes.Where(c => c.Kind is CrashKind.AppCrash or CrashKind.AppHang && !string.IsNullOrEmpty(c.AppExe)).ToList() is { Count: > 0 } ofApps)
        {
            var by = ofApps.GroupBy(c => c.AppExe.ToLowerInvariant()).OrderByDescending(g => g.Count()).ToList();
            a.Lead = $"**{CrashName(by[0].First())}** crashed or froze the most {period.Label}: {Count(by[0].Count(), "time", "times")}.";
            foreach (var g in by.Skip(1).Take(5)) a.Points.Add(new($"**{CrashName(g.First())}**: {g.Count()}", AskTone.Warn));
            if (a.Points.Count > 0) a.PointsTitle = "Then";
            a.FollowUps.Add($"Why did {CrashName(by[0].First())} crash?");
            return a;
        }
        a.Lead = crashes.Count == 1
            ? $"Yes, once {period.Label}: {Mid(Explain(crashes[0]).Title)} {AtIn(crashes[0].Time, period)}."
            : $"{Tally(crashes).ToUpperFirst()}{of} {period.Label}.";
        if (crashes.Count > 1)
        {
            const int shown = 6;
            foreach (var c in crashes.OrderByDescending(c => c.Ts).Take(shown))
            {
                var ex = Explain(c);
                string cause = ex.Cause is null ? "" : $" ({ex.Cause})";
                a.Points.Add(new($"**{AtIn(c.Time, period).ToUpperFirst()}**: {ex.Title}{cause}", IsPcLevel(c) ? AskTone.Hot : AskTone.Warn));
            }
            if (crashes.Count > shown) a.Note = $"The {shown} most recent of {crashes.Count} are shown. The Crashes page has them all.";

            // The same thing over and over is worth saying before anyone has to count it.
            var repeat = crashes.GroupBy(c => (c.Kind, App: c.Kind is CrashKind.AppCrash or CrashKind.AppHang ? c.AppExe.ToLowerInvariant() : ""))
                .Where(g => g.Count() >= 3).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (repeat is not null)
                a.Paragraphs.Add(repeat.Key.App.Length > 0
                    ? $"{CrashName(repeat.First())} accounts for {repeat.Count()} of them."
                    : $"{CrashWords.Count(repeat.Key.Kind, repeat.Count()).ToUpperFirst()} is a pattern worth looking into.");
        }
        var worst = crashes.OrderByDescending(Severity).ThenByDescending(c => c.Ts).First();
        a.FollowUps.Add($"Why did {(IsPcLevel(worst) ? "my PC" : CrashName(worst))} crash {DayWord(worst.Time)}?");
        if (anyPc) a.FollowUps.Add($"What changed on my PC {(period.Days <= 7 ? "in the last 14 days" : period.Label)}?");
        return a;
    }

    // ── Why it crashed ──────────────────────────────────────────────────

    private static readonly HashSet<string> GraphicsCulprits = new(StringComparer.OrdinalIgnoreCase)
    {
        "NVIDIA driver", "AMD driver", "Intel graphics driver", "DirectX", "Vulkan", "Graphics driver", "GPU driver",
        "VIDEO_TDR_FAILURE", "VIDEO_SCHEDULER_INTERNAL_ERROR",
    };

    /// <summary>
    /// The cause of one problem as far as the records show it: what Windows logged, what was in front, how hot and how
    /// loaded the PC was just before, what had changed in the days before, and whether it keeps happening. With several
    /// in the period, the worst one (the latest of the worst kind) is the one explained.
    /// </summary>
    private AskAnswer Diagnose(List<CrashEvent> crashes, AskPeriod period, bool periodAsked, AskAnswer a)
    {
        // With a time asked for: the worst thing in it. Without one ("why did my PC crash"): the latest time the PC
        // itself went down, or the latest app crash when it never did.
        var e = periodAsked && !_q.Last ? crashes.OrderByDescending(Severity).ThenByDescending(c => c.Ts).First()
            : crashes.Where(IsPcLevel).MaxBy(c => c.Ts) ?? crashes.MaxBy(c => c.Ts)!;
        if (!periodAsked) a.Links[0] = new("Open Crashes", "crashes", e.Time.Date);
        var ex = Explain(e);
        long ts = e.Ts;
        bool graphics = GraphicsCulprits.Contains(ex.Culprit);
        bool pcLevel = IsPcLevel(e);
        bool noReport = e.Kind == CrashKind.UnexpectedShutdown;

        // What happened.
        _db.GetCrashContext(ts, ts + 1).TryGetValue(e.Id, out var ctx);
        string? front = ctx?.FrontApp is long f ? NameOf(f) : null;
        string happened = $"{ex.Title} {At(e.Time)}";
        if (front is not null && (pcLevel || !string.Equals(front, CrashName(e), StringComparison.OrdinalIgnoreCase))) happened += $", with {front} in front";
        else if (ctx?.SessionSec is double session && session >= 120) happened += $", {Dur(session)} into the session";
        // (With no crash report, the reason is the verdict itself: not said twice.)
        a.Paragraphs.Add(noReport ? $"{happened}." : $"{happened}. {ex.Reason}");

        // Heat.
        var (cpu, gpu) = (ctx?.CpuBefore, ctx?.GpuBefore);
        double cpuLimit = _s.Alerts.CpuLimit, gpuLimit = _s.Alerts.GpuLimit;
        bool hot = (cpu is double c && c >= cpuLimit) || (gpu is double g && g >= gpuLimit);
        if (cpu is null && gpu is null)
            a.Points.Add(new("**Heat**: no temperature readings from the minutes before it.", AskTone.Neutral, CantRuleOut));
        else if (hot)
        {
            var over = new List<string>();
            if (gpu is double gv && gv >= gpuLimit) over.Add($"the GPU reached {Temp(gv)} (your limit is {Temp(gpuLimit)})");
            if (cpu is double cv && cv >= cpuLimit) over.Add($"the CPU reached {Temp(cv)} (your limit is {Temp(cpuLimit)})");
            a.Points.Add(new($"**Heat**: {Join(over)} in the 5 minutes before.", AskTone.Hot, PointsTo));
        }
        else
        {
            var read = new List<string>();
            if (gpu is not null) read.Add($"GPU {Temp(gpu)}");
            if (cpu is not null) read.Add($"CPU {Temp(cpu)}");
            a.Points.Add(new($"**Heat**: {Join(read)} at most in the 5 minutes before, under your {(read.Count > 1 ? "limits" : "limit")}.", AskTone.Good, RuledOut));
        }

        // Load and memory in the last whole minute recorded before it.
        var minute = _db.GetMinutes(ts - 300, ts + 1).LastOrDefault();
        if (minute is not null)
        {
            var load = new List<string>();
            if (minute.GpuLoad is double gl) load.Add($"GPU at {gl:0}%");
            if (minute.CpuLoad is double cl) load.Add($"CPU at {cl:0}%");
            if (load.Count > 0) a.Points.Add(new($"**Load**: {Join(load)} in the last minute recorded before it.", AskTone.Neutral, AlsoNoted));
            if (minute.RamUsed is double used && TotalRamGb() is double total && total > 0 && used <= total * 1.02)
            {
                // Full at the end points to it; full at some point in the half hour before can't be ruled out; and only
                // with a fifth of it free the whole time is it ruled out.
                double peak = _db.GetMinutes(ts - TrailMinutes * 60, ts + 1).Where(m => m.RamUsed <= total * 1.02).Max(m => m.RamUsed) ?? used;
                if (used / total >= 0.9) a.Points.Add(new($"**Memory**: nearly full, {used:0.0} of {total:0} GB in use.", AskTone.Warn, PointsTo));
                else if (peak / total >= 0.9) a.Points.Add(new($"**Memory**: over 90% full in the half hour before ({Math.Min(peak, total):0.0} of {total:0} GB at the most), {Math.Min(used, total):0.0} GB at the end.", AskTone.Warn, CantRuleOut));
                else if (peak / total < 0.8 && !pcLevel) a.Points.Add(new($"**Memory**: {peak:0.0} of {total:0} GB in use at the most, so it wasn't short.", AskTone.Good, RuledOut));
            }
        }

        // What changed before it: any system-level change for the PC going down, graphics drivers and Windows updates
        // for a graphics failure. A bug in an app's own code isn't explained by a driver installed that week.
        SystemChange? suspect = null;
        if ((pcLevel || graphics) && _db.HasChanges)
        {
            var before = _db.GetChanges(TimeUtil.ToUnix(e.Time.AddDays(-SuspectDays)), ts + 1)
                .Where(ch => ch.Earliest < e.Time && ch.IsSystemLevel)
                .Where(ch => !graphics || ch.IsGraphicsDriver || ch.Kind == ChangeKind.WindowsUpdate)
                .OrderByDescending(ch => ch.IsGraphicsDriver).ThenByDescending(ch => ch.Time).ToList();
            if (before.Count > 0)
            {
                suspect = before[0];
                var named = before.Take(2).OrderBy(ch => ch.Time).Select(ch => $"{Thing(ch)} ({Ago(ch.Time, e.Time)})").ToList();
                a.Points.Add(new($"**What changed**: {Join(named)}.", AskTone.Warn, CantRuleOut));
            }
            else a.Points.Add(new($"**What changed**: no driver, Windows update or hardware change in the {SuspectDays} days before.", AskTone.Good, RuledOut));
        }

        // How often: the same thing in the 30 days up to it, and whether it only began after the change.
        var since = e.Time.AddDays(-CrashLookBack);
        bool Same(CrashEvent o) => o.Kind == e.Kind && (pcLevel || string.Equals(o.AppExe, e.AppExe, StringComparison.OrdinalIgnoreCase)) && Severity(o) > 0;
        var series = CrashesIn(since, e.Time.AddSeconds(1)).Where(Same).OrderBy(o => o.Ts).ToList();
        string kindOne = e.Kind switch
        {
            CrashKind.SystemCrash => "blue screen",
            CrashKind.GpuDriverReset => "graphics driver reset",
            CrashKind.UnexpectedShutdown => "unexpected shutdown",
            CrashKind.AppHang => $"freeze of {CrashName(e)}",
            _ => $"crash of {CrashName(e)}",
        };
        bool beganAfter = false;
        if (series.Count > 1)
        {
            string often = $"**How often**: the {Ordinal(series.Count)} {kindOne} in the {CrashLookBack} days up to it";
            if (suspect is not null && series.All(o => o.Time >= suspect.Earliest) && RecordedDaysBefore(suspect.Earliest, CrashLookBack) is int quiet and >= 7
                && !CrashesIn(suspect.Earliest.AddDays(-quiet), suspect.Earliest).Any(Same))
            {
                beganAfter = true;
                often += $", all of them after {Thing(suspect)}; none in the {quiet} days before it";
            }
            a.Points.Add(new(often + ".", AskTone.Warn, beganAfter ? PointsTo : AlsoNoted));
        }
        else a.Points.Add(new($"**How often**: the only {kindOne} in the {CrashLookBack} days up to it.", AskTone.Neutral, AlsoNoted));

        // A change the pattern backs (it began after it), or a graphics driver before a graphics failure, points at the cause;
        // any other change close by only can't be ruled out. Then: what points to a cause first, what is ruled out last.
        if (suspect is not null && (beganAfter || (graphics && suspect.IsGraphicsDriver)))
        {
            int at = a.Points.FindIndex(p => p.Text.StartsWith("**What changed**", StringComparison.Ordinal));
            if (at >= 0) a.Points[at] = a.Points[at] with { Group = PointsTo };
        }
        var sorted = a.Points.OrderBy(p => Array.IndexOf(GroupOrder, p.Group)).ToList();
        a.Points.Clear();
        a.Points.AddRange(sorted);
        Trail(a, e, ex, total: TotalRamGb());
        Problem = e.Kind.ToString();
        ProblemApp = pcLevel ? null : e.AppExe;

        // The verdict, from the strongest thing found.
        string? oldVersion = suspect is { IsGraphicsDriver: true, Was: { Length: > 0 } was } ? was : null;
        if (hot)
        {
            a.Lead = "**Heat is the likely cause.** The PC was over your limit just before it.";
            a.Advice = "Check that the fans turn and the vents and heatsinks are clear of dust, and see whether it only happens under heavy load.";
            a.FollowUps.Add($"How hot did it get {DayWord(e.Time)}?");
            a.FollowUps.Add("Are my fans working?");
        }
        else if (suspect is not null && (beganAfter || (graphics && suspect.IsGraphicsDriver)))
        {
            // "Most likely" only with the pattern to back it (it began after the change, with quiet days before); a change
            // close by on its own makes it the first thing to look at, no more.
            a.Lead = suspect.IsGraphicsDriver
                ? beganAfter ? $"**Most likely the graphics driver.** It changed {Ago(suspect.Time, e.Time)}, and the problem only began after that."
                    : $"**The graphics driver is the first suspect.** It changed {Ago(suspect.Time, e.Time)}; that alone doesn't prove it."
                : $"**Most likely {Thing(suspect)}.** It came {Ago(suspect.Time, e.Time)}, and the problem only began after that.";
            a.Advice = suspect.IsGraphicsDriver
                ? $"I'd look at the driver first: {(oldVersion is not null ? $"go back to {oldVersion}, the version before, or " : "")}reinstall the current one cleanly."
                : ex.Advice;
        }
        else if (noReport)
        {
            a.Lead = e.Moment switch
            {
                PowerMoment.Asleep => "**The PC lost power while it was asleep.** Windows saved no crash report, so nothing more is recorded.",
                PowerMoment.ShuttingDown => "**Windows didn't finish shutting down.** That is usually harmless, and no crash report was saved.",
                _ => "**The cause isn't recorded.** The PC went off without a crash report: a power cut, a held power button, or a hard freeze.",
            };
            a.Advice = ex.Advice;
        }
        else if (graphics)
        {
            a.Lead = e.Kind == CrashKind.GpuDriverReset
                ? "**The graphics driver stopped responding**, and Windows reset it."
                : $"**It points at the graphics side**: {ex.Culprit}.";
            a.Advice = ex.Advice;
        }
        else
        {
            a.Lead = e.Kind switch
            {
                CrashKind.SystemCrash => $"**Windows hit an error it couldn't recover from**: {ex.Culprit}.",
                CrashKind.AppHang => $"**{CrashName(e)} froze** and was closed. Windows doesn't record why an app stops responding.",
                _ => ex.Cause is { } cause ? $"**{CrashName(e)} crashed {(cause.StartsWith("in ", StringComparison.Ordinal) ? cause : "inside " + cause)}.**" : $"**{CrashName(e)} crashed**, and Windows didn't record where.",
            };
            // The verdict already says where it crashed: the line under it only adds when, and what was in front.
            if (e.Kind == CrashKind.AppCrash && ex.Cause is not null) a.Paragraphs[0] = $"{happened}.";
            a.Advice = ex.Advice;
        }

        if (crashes.Count > 1)
            a.Paragraphs.Add(periodAsked && !_q.Last
                ? $"There {(crashes.Count == 2 ? "was one other problem" : $"were {crashes.Count - 1} other problems")} {period.Label}; this is the most serious one."
                : $"It is the most recent of {crashes.Count} problems {period.Label}: {Tally(crashes)}.");
        // The PC going off again and again without a crash report is rarely chance.
        if (noReport && e.Moment == PowerMoment.Running && series.Count >= 3 && !hot)
            a.Advice = $"It has happened {series.Count} times in {CrashLookBack} days. Windows can't tell a power cut from the PC being switched off at the wall or with a held power button, so if that is how you turn it off, this is that. If it isn't, the power cable and the power supply are worth a look.";
        a.Note = "Readings are kept once a minute, so the last seconds before it aren't in the records.";
        if (suspect is not null) a.Links.Add(new("Open Timeline", "timeline"));
        if (series.Count > 1) a.FollowUps.Add("When did that start happening?");
        if (a.FollowUps.Count <= 1)
        {
            a.FollowUps.Add(e.Time >= Now.AddDays(-14) ? "What changed on my PC in the last 14 days?" : $"What changed on my PC {AskTime.Month(e.Time, Now).Label}?");
            a.FollowUps.Add($"How hot did it get {DayWord(e.Time)}?");
            if (crashes.Count > 1 || series.Count > 1) a.FollowUps.Add("Show me the crashes this month");
        }
        Used = AskTime.Day(e.Time, Now);
        return a;
    }

    // How a finding bears on the cause: an answer's evidence is shown under these, in this order.
    internal const string PointsTo = "Points to", CantRuleOut = "Can't rule out", RuledOut = "Ruled out", AlsoNoted = "Also noted";
    private static readonly string[] GroupOrder = [PointsTo, CantRuleOut, RuledOut, AlsoNoted, ""];

    /// <summary>How far back the steps before a problem are looked for, and the load that counts as a part being worked hard.</summary>
    private const int TrailMinutes = 30;
    private const double TrailLoad = 90;

    /// <summary>
    /// What led up to a problem, in order, from the half hour before it: the app that came to the front, a part going
    /// to full load and staying there, the hottest moment, memory filling up, and any other problem in the minutes
    /// around it (a driver reset just before a game closes is the chain worth seeing). Readings are one a minute, so
    /// this is the order of minutes, not of seconds; with fewer than three steps there's no story to tell.
    /// </summary>
    private void Trail(AskAnswer a, CrashEvent e, CrashExplanation ex, double? total)
    {
        long ts = e.Ts;
        var minutes = _db.GetMinutes(ts - TrailMinutes * 60, ts + 1);
        var steps = new List<(long Ts, string Text, AskTone Tone)>();
        if (minutes.Count > 0)
        {
            // Walks back from the last minute while a condition holds, with no gap in the minutes: where that run began.
            int Run(Func<SystemMinute, bool> holds)
            {
                int i = minutes.Count - 1;
                if (!holds(minutes[i])) return -1;
                while (i > 0 && holds(minutes[i - 1]) && minutes[i].Ts - minutes[i - 1].Ts <= 120) i--;
                return i;
            }

            var last = minutes[^1];
            if (last.FgApp is long front && Run(m => m.FgApp == front) is var began and >= 0)
                steps.Add((minutes[began].Ts, began == 0 ? $"{NameOf(front)} in front (since before this)" : $"{NameOf(front)} came to the front", AskTone.Neutral));
            if (Run(m => m.GpuLoad >= TrailLoad) is var gpuRun and >= 0 && minutes.Count - gpuRun >= 2)
                steps.Add((minutes[gpuRun].Ts + 1, $"GPU load at {TrailLoad:0}% or more from here on", AskTone.Neutral));
            if (Run(m => m.CpuLoad >= TrailLoad) is var cpuRun and >= 0 && minutes.Count - cpuRun >= 2)
                steps.Add((minutes[cpuRun].Ts + 2, $"CPU load at {TrailLoad:0}% or more from here on", AskTone.Neutral));
            if (minutes.Where(m => m.GpuTempMax is not null).MaxBy(m => m.GpuTempMax) is { } hottest)
                steps.Add((hottest.Ts + 3, $"GPU at its hottest, {Temp(hottest.GpuTempMax)}", hottest.GpuTempMax >= _s.Alerts.GpuLimit ? AskTone.Hot : AskTone.Neutral));
            if (total is double t && t > 0 && minutes.FirstOrDefault(m => m.RamUsed / t >= 0.9 && m.RamUsed <= t * 1.02) is { } full)
                steps.Add((full.Ts + 4, "Memory passed 90% full", AskTone.Warn));
        }
        // Other problems in the ten minutes before it and the two after.
        foreach (var other in CrashesIn(e.Time.AddMinutes(-10), e.Time.AddMinutes(2)).Where(o => o.Id != e.Id))
            steps.Add((other.Ts, Explain(other).Title, AskTone.Warn));
        steps.Add((ts, ex.Title, AskTone.Hot));
        if (steps.Count < 3) return;

        a.TrailTitle = "Leading up to it";
        // The same thing logged twice in a minute (Windows often does) is one step.
        foreach (var s in steps.OrderBy(s => s.Ts).GroupBy(s => (Clock(TimeUtil.FromUnix(s.Ts)), s.Text)).Select(g => g.MaxBy(s => s.Tone)))
            a.Trail.Add(new(Clock(TimeUtil.FromUnix(s.Ts)), s.Text, s.Tone));
        if (a.Trail.Count < 3)
        {
            a.Trail.Clear();
            a.TrailTitle = null;
        }
    }

    /// <summary>
    /// "When did that start?", "how often does that happen?": the history of the problem just explained, as far back
    /// as the records go, with what had changed in the week before the first one.
    /// </summary>
    private AskAnswer CrashHistory()
    {
        var a = new AskAnswer();
        a.Links.Add(new("Open Crashes", "crashes"));
        var kind = Enum.TryParse<CrashKind>(_q.Problem, out var k) ? k : CrashKind.AppCrash;
        string? exe = _q.ProblemApp;
        bool Same(CrashEvent o) => o.Kind == kind && Severity(o) > 0 && (exe is null || string.Equals(o.AppExe, exe, StringComparison.OrdinalIgnoreCase));
        var period = _q.Period ?? AskTime.All(Now);
        var all = CrashesIn(period.From, period.To).Where(Same).OrderBy(o => o.Ts).ToList();
        string what = exe is null ? CrashWords.Count(kind, 2)[2..] : $"{(kind == CrashKind.AppHang ? "freezes" : "crashes")} of {NameOfExe(exe)}";
        Problem = _q.Problem;
        ProblemApp = exe;
        if (all.Count == 0)
        {
            a.Lead = $"No {what} {(_q.Period is null ? "are on record" : period.Label)}.";
            return a;
        }

        var (first, latest) = (all[0], all[^1]);
        a.Lead = all.Count == 1
            ? $"Only the once: {DayWord(first.Time)} at {Clock(first.Time)}."
            : $"**{all.Count} {what} {(_q.Period is null ? "on record" : period.Label)}.** {(first.Time.Date == latest.Time.Date ? $"All of them {DayWord(first.Time)}." : $"The first was {DayWord(first.Time)}, the latest {DayWord(latest.Time)}.")}";

        // What had changed before the first one, and how long the records were quiet before it.
        if (_db.HasChanges)
        {
            var before = _db.GetChanges(TimeUtil.ToUnix(first.Time.AddDays(-SuspectDays)), first.Ts + 1).Where(c => c.Earliest < first.Time && c.IsSystemLevel)
                .OrderByDescending(c => c.IsGraphicsDriver).ThenByDescending(c => c.Time).Take(2).OrderBy(c => c.Time).ToList();
            if (before.Count > 0) a.Paragraphs.Add($"In the week before the first one: {Join([.. before.Select(c => $"{Thing(c)} ({Ago(c.Time, first.Time)})")])}.");
        }
        int quiet = RecordedDaysBefore(first.Time, 365);
        a.Paragraphs.Add(quiet >= 7 ? $"The records go back {quiet} days before that first one, with none in them." : "The records don't go back far enough before that to say it began then: there may have been earlier ones.");

        if (all.Count > 2)
        {
            a.PointsTitle = "By month";
            foreach (var month in all.GroupBy(o => new DateTime(o.Time.Year, o.Time.Month, 1)).OrderByDescending(g => g.Key).Take(6))
                a.Points.Add(new($"**{month.Key.ToString(month.Key.Year == Now.Year ? "MMMM" : "MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}**: {month.Count()}"));
        }
        a.FollowUps.Add($"What changed on my PC {AskTime.Month(first.Time, Now).Label}?");
        a.FollowUps.Add($"Why did {(exe is null ? "my PC" : NameOfExe(exe))} crash {DayWord(latest.Time)}?");
        return a;
    }

    /// <summary>A change as a thing ("NVIDIA graphics driver", not "NVIDIA graphics driver installed").</summary>
    private static string Thing(SystemChange c) => c.Title.EndsWith(" installed", StringComparison.Ordinal) ? c.Title[..^10] : c.Title;

    /// <summary>
    /// The minutes that can be set against the memory installed now: those after the last one that held more than
    /// that (the PC had more memory then, so how full it was can't be told from today's size).
    /// </summary>
    private static List<SystemMinute> SameMemory(List<SystemMinute> minutes, double? total)
    {
        if (total is not double t) return minutes;
        int last = minutes.FindLastIndex(m => m.RamUsed > t * 1.02);
        return last < 0 ? minutes : minutes.GetRange(last + 1, minutes.Count - last - 1);
    }

    /// <summary>Whole days with records in the <paramref name="max"/> days before a moment (0 when the records start after).</summary>
    private int RecordedDaysBefore(DateTime moment, int max)
    {
        var first = new[] { _db.FirstDataTime(), _db.FirstCrashTime() }.OfType<long>().Select(TimeUtil.FromUnix).DefaultIfEmpty(moment).Min();
        return (int)Math.Clamp((moment - first).TotalDays, 0, max);
    }

    /// <summary>Installed memory in GB, from the inventory ("32 GB").</summary>
    private double? TotalRamGb()
    {
        var ram = _db.HasChanges ? _db.GetInventory().FirstOrDefault(i => i.Kind == Inventory.Ram) : null;
        return ram is not null && Gigabytes().Match(ram.Value) is { Success: true } m
            ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*GB", RegexOptions.IgnoreCase)]
    private static partial Regex Gigabytes();

    [GeneratedRegex(@"\b(which|what) (app|game|program)s?\b.*\bmost\b|\bthe most\b")]
    private static partial Regex MostWords();

    [GeneratedRegex(@"\b(blue ?screens?|bsods?|bluescreens?|bugcheck)\b")]
    private static partial Regex BlueScreenWords();

    [GeneratedRegex(@"\b(driver reset|black screen|screen (went|go|goes|going) black|display (crash|driver))\b")]
    private static partial Regex ResetWords();

    [GeneratedRegex(@"\b(shut ?down|shut off|shuts? (itself )?(down|off)|turn(ed|s)? (itself )?off|power(ed)? off|restart\w*|reboot\w*|switch(ed|es)? off)\b")]
    private static partial Regex ShutdownWords();

    [GeneratedRegex(@"\b(froze|freez\w*|frozen|hang|hangs|hung|not responding|stopped responding|unresponsive)\b")]
    private static partial Regex FreezeWords();
}

internal static class AskText
{
    public static string ToUpperFirst(this string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;
}
