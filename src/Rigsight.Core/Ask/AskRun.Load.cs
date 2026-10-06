using System.Text.RegularExpressions;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Ask;

internal sealed partial class AskRun
{
    /// <summary>The longest period whose minutes are read one by one (longer ones go by the report alone).</summary>
    private const int MinuteDays = 31;

    // What counts as a part being worked to its limit, by the minute's average.
    private const double CpuFlatOut = 90, MemoryFull = 0.9, GpuAtLimit = 97;

    private List<SystemMinute> MinutesOf(AskPeriod p) =>
        p.Days <= MinuteDays ? _db.GetMinutes(TimeUtil.ToUnix(p.From), Math.Min(TimeUtil.ToUnix(p.To), TimeUtil.ToUnix(Now) + 60)) : [];

    /// <summary>
    /// What was working the PC hard: the things the records can show behind "slow" or "laggy" (a part flat out, memory
    /// full, a chip slowing itself, something busy in the background, a full drive), strongest first. The frame rate
    /// itself isn't recorded, and the answer says so.
    /// </summary>
    /// <summary>"What uses the most CPU": the apps by their own share of the processor, or by how hard the GPU worked while each was in use.</summary>
    private AskAnswer TopBy()
    {
        var period = PeriodOr(() => Today);
        var r = ReportFor(period);
        if (NothingRecorded(period, r) is { } none) return none;
        var a = new AskAnswer();
        a.Links.Add(new("Open Apps", "apps"));
        if (_q.Part == AskPart.Gpu)
        {
            var apps = r.Apps.Where(x => x.GpuLoadAvg is not null && x.ActiveSec >= 300).OrderByDescending(x => x.GpuLoadAvg).ToList();
            if (apps.Count == 0)
            {
                a.Lead = $"No app was in use long enough {period.Label} to say how hard it worked the graphics card.";
                return a;
            }
            a.Lead = $"**{apps[0].Name}** worked the graphics card hardest {period.Label}: {apps[0].GpuLoadAvg:0}% GPU load on average while you used it.";
            foreach (var x in apps.Skip(1).Take(4)) a.Points.Add(new($"**{x.Name}**: {x.GpuLoadAvg:0}%"));
            a.Note = "This is the whole GPU's load while each app was in front, for apps used five minutes or more.";
        }
        else
        {
            var apps = r.Apps.Where(x => x.CpuAvg >= 0.5).OrderByDescending(x => x.CpuAvg).ToList();
            if (apps.Count == 0)
            {
                a.Lead = $"No app took a share of the processor worth naming {period.Label}.";
                return a;
            }
            a.Lead = $"**{apps[0].Name}** used the most CPU {period.Label}: {apps[0].CpuAvg:0.#}% of the processor on average{(apps[0].CpuMax is double most ? $", up to {most:0}%" : "")}.";
            foreach (var x in apps.Skip(1).Take(4)) a.Points.Add(new($"**{x.Name}**: {x.CpuAvg:0.#}% on average{(x.CpuMax is double m ? $", up to {m:0}%" : "")}"));
            a.Note = "Each app's own share of the whole processor, averaged over the time it was running.";
        }
        if (a.Points.Count > 0) a.PointsTitle = "Then";
        a.FollowUps.Add($"What used the most memory {period.Label}?");
        return a;
    }

    private AskAnswer Slow()
    {
        if (_q.TopBy) return TopBy();
        var period = PeriodOr(() => Today);
        var r = ReportFor(period);
        if (NothingRecorded(period, r) is { } none) return none;
        var a = new AskAnswer();
        a.Links.Add(new("Open Reports", "reports", period.IsDay ? period.From : null));

        var minutes = MinutesOf(period);
        string scope = period.Label;
        AppCategory? category = null;
        if (_q.AppId is long appId)
        {
            string app = NameOf(appId);
            minutes = [.. minutes.Where(m => m.FgApp == appId)];
            if (minutes.Count == 0)
            {
                a.Lead = $"{app} wasn't in front {period.Label}, so there's nothing to go on.";
                if (LastSession(appId) is { } last) a.FollowUps.Add($"Why was {app} slow {AskTime.Day(TimeUtil.FromUnix(last.Start), Now).Label}?");
                return a;
            }
            scope = $"while {app} was in front {period.Label}";
            category = _apps.TryGetValue(appId, out var row) ? CategoryOf(row) : null;
        }

        var found = new List<(int Weight, string Short, AskPoint Point)>();
        // With an app named, the report's own findings count only when they are about that app.
        bool About(Reports.Insight i) => _q.App is null || i.Text.Contains(_q.App, StringComparison.OrdinalIgnoreCase);
        double? total = TotalRamGb();

        // The graphics card slowing itself to stay cool.
        if (r.Insights.FirstOrDefault(i => i.Key == "throttle" && About(i)) is { } throttle)
            found.Add((100, "the graphics card slowed itself down to stay cool", new(throttle.Text, AskTone.Hot)));

        // Memory nearly full: Windows then pages to the drive, and everything drags.
        var full = total is double t && t > 0 ? SameMemory(minutes, total).Where(m => m.RamUsed / t >= MemoryFull).ToList() : [];
        if (full.Count >= 3)
        {
            string who = ReportFor(period).Apps.Where(x => x.MemMax >= 1024).MaxBy(x => x.MemMax) is { } hog ? $" The biggest user was {hog.Name}, at up to {Units.Megabytes(hog.MemMax!.Value)}." : "";
            found.Add((90, "memory was nearly full", new($"**Memory was nearly full** for {Count(full.Count, "minute", "minutes")} (up to {full.Max(m => m.RamUsed):0.0} of {total:0} GB). Windows then moves things to the drive, and everything drags.{who}", AskTone.Warn)));
        }

        // The processor flat out, and what was doing it.
        var pegged = minutes.Where(m => m.CpuLoad >= CpuFlatOut).ToList();
        if (pegged.Count >= 5)
        {
            var by = pegged.Where(m => m.CpuApp is not null).GroupBy(m => m.CpuApp!.Value).OrderByDescending(g => g.Count()).FirstOrDefault();
            bool named = by is not null && by.Count() >= pegged.Count / 2.0;
            bool behind = named && pegged.Count(m => m.CpuApp == by!.Key && m.FgApp != by.Key) >= by!.Count() / 2.0;
            string who = !named ? "" : behind ? $", mostly from {NameOf(by!.Key)} working in the background" : $", mostly from {NameOf(by!.Key)}";
            found.Add((behind ? 85 : 80, behind ? $"{NameOf(by!.Key)} kept the processor flat out in the background" : "the processor was flat out",
                new($"**The CPU was flat out** ({CpuFlatOut:0}% or more) for {Count(pegged.Count, "minute", "minutes")}{who}.", AskTone.Warn)));
        }

        // Heat at the limit.
        if (_q.App is null && r.GpuOverLimitMin + r.CpuOverLimitMin > 0)
            foreach (var i in r.Insights.Where(i => i.Key == "over-limit"))
                found.Add((75, "it ran at your temperature limit", new(i.Text + " A chip that hot can slow itself down.", AskTone.Hot)));

        // Something else busy while another app was in front.
        if (r.Insights.FirstOrDefault(i => i.Key == "background-work" && About(i)) is { } background && !found.Any(f => f.Weight == 85))
            found.Add((70, "something was busy in the background", new(background.Text, AskTone.Warn)));

        // A full system drive.
        var drives = DrivesNow();
        if (drives.FirstOrDefault(d => d.Drive.StartsWith("C", StringComparison.OrdinalIgnoreCase)) is { } c && (c.TotalGb - c.UsedGb < 10 || c.UsedGb / c.TotalGb >= 0.93))
            found.Add((60, "the Windows drive is nearly full", new($"**The Windows drive is nearly full**: {c.TotalGb - c.UsedGb:0} GB free of {c.TotalGb:0}. Windows needs room to work, and slows down without it.", AskTone.Warn)));

        // A download while a game was on.
        if (r.Net?.Insights.FirstOrDefault(i => i.Key == "net-game" && About(i)) is { } download)
            found.Add((50, "a big download ran while you were playing", new(download.Text, AskTone.Warn)));

        // The graphics card at its limit in a game: not a fault, but it is what sets the frame rate.
        var gpuBusy = minutes.Where(m => m.GpuLoad >= GpuAtLimit).ToList();
        if (minutes.Count >= 10 && gpuBusy.Count >= minutes.Count * 0.6 && (category == AppCategory.Game || _q.AppId is null && r.GamingSec >= 600))
            found.Add((20, "", new($"**The graphics card was the limit** {(_q.AppId is null ? "in games" : "here")}: at {GpuAtLimit:0}% or more for {gpuBusy.Count * 100.0 / minutes.Count:0}% of the time. That is normal for a demanding game; lower settings would lift the frame rate.")));

        // What changed just before: a new driver or update is always worth knowing.
        if (_db.HasChanges)
        {
            var changed = _db.GetChanges(TimeUtil.ToUnix(period.From.AddDays(-3)), TimeUtil.ToUnix(period.To)).Where(ch => ch.IsSystemLevel || ch.Kind == ChangeKind.Startup)
                .OrderByDescending(ch => ch.Time).Take(2).Select(ch => $"{ch.Title} ({DayWord(ch.Time)})").ToList();
            if (changed.Count > 0) found.Add((10, "", new($"**Changed recently**: {Join(changed)}.")));
        }

        found = [.. found.OrderByDescending(f => f.Weight)];
        var strong = found.Where(f => f.Weight >= 50).ToList();
        if (strong.Count > 0)
        {
            a.Lead = $"**The clearest thing in the records: {strong[0].Short}.**";
            if (strong.Count > 1) a.Paragraphs.Add($"{Count(strong.Count, "thing", "things")} stood out {scope}.");
        }
        else
        {
            a.Lead = $"**Nothing in the records stands out {scope}.**";
            var seen = new List<string>();
            if (minutes.Max(m => m.CpuLoad) is double cpuMax) seen.Add($"the CPU's busiest minute averaged {cpuMax:0}%");
            if (SameMemory(minutes, total).Max(m => m.RamUsed) is double ramMax && total is double tot) seen.Add($"memory peaked at {ramMax:0.0} of {tot:0} GB");
            if (seen.Count > 0) a.Paragraphs.Add($"{Join(seen).ToUpperFirst()}, and no part was held at its limit.");
        }
        foreach (var f in found) a.Points.Add(f.Point);
        if (a.Points.Count > 0) a.PointsTitle = strong.Count > 0 ? "What I found" : "For what it's worth";

        bool aboutFrames = FrameWords().IsMatch(_q.Text.ToLowerInvariant()) || category == AppCategory.Game;
        a.Note = aboutFrames
            ? "Frame rates aren't recorded, so a stutter itself doesn't show here. This is what was working the PC at the time, by the minute."
            : "Loads are averages over each minute, so a few seconds' spike doesn't show.";
        a.FollowUps.Add($"What used the most memory {period.Label}?");
        a.FollowUps.Add($"How hot did it get {period.Label}?");
        a.FollowUps.Add("What changed on my PC this week?");
        return a;
    }

    private AskAnswer Memory()
    {
        string lower = _q.Text.ToLowerInvariant();
        bool need = NeedWords().IsMatch(lower);
        var period = PeriodOr(() => need ? AskTime.LastDays(30, Now) : Today);
        var r = ReportFor(period);
        if (NothingRecorded(period, r) is { } none) return none;
        var a = new AskAnswer();
        a.Links.Add(new("Open Memory", "memory"));

        var minutes = MinutesOf(period).Where(m => m.RamUsed is not null).ToList();
        double? total = TotalRamGb();
        var apps = r.Apps.Where(x => x.MemMax >= 200).OrderByDescending(x => x.MemMax).ToList();

        if (_q.AppId is long appId)
        {
            string app = NameOf(appId);
            var stat = r.Apps.FirstOrDefault(x => x.Id == appId);
            if (stat?.MemMax is not double max)
            {
                a.Lead = $"{app} wasn't running {period.Label}, so there's no memory use on record for it.";
                return a;
            }
            a.Lead = $"**{app}** used up to **{Units.Megabytes(max)}** of memory {period.Label}{(stat.MemAvg is double avg ? $", {Units.Megabytes(avg)} on average" : "")}.";
            int rank = apps.FindIndex(x => x.Id == appId) + 1;
            if (rank == 1 && apps.Count > 1) a.Paragraphs.Add("That is the most of any app in that time.");
            else if (rank > 1) a.Paragraphs.Add($"{apps[0].Name} used the most, at up to {Units.Megabytes(apps[0].MemMax!.Value)}.");
            a.FollowUps.Add($"What used the most memory {period.Label}?");
            return a;
        }

        if (minutes.Count == 0)
        {
            a.Lead = period.Days > MinuteDays ? "Memory use is kept by the minute, for about a month back. Ask about a shorter, more recent time." : $"No memory use was recorded {period.Label}.";
            return a;
        }
        // More in use than is installed now: the PC had more memory then. Only the minutes since can be judged.
        var comparable = SameMemory(minutes, total);
        if (comparable.Count < minutes.Count)
        {
            if (comparable.Count == 0)
            {
                a.Lead = $"The memory readings {period.Label} don't fit the {total:0} GB installed now, so how full it was can't be judged.";
                a.Paragraphs.Add("The PC had a different amount of memory then.");
                return a;
            }
            double most = minutes.Max(m => m.RamUsed) ?? 0;
            a.Paragraphs.Add((total is double now && most <= now * 4
                    ? $"The PC had more memory for part of that time (up to {most:0.0} GB was in use). "
                    : "Some older readings in that time don't fit the memory installed now. ")
                + $"This looks only at the time since, from {AskTime.DayText(TimeUtil.FromUnix(comparable[0].Ts), Now)}.");
            minutes = comparable;
        }
        double peak = minutes.Max(m => m.RamUsed!.Value), average = minutes.Average(m => m.RamUsed!.Value);
        int nearlyFull = total is double t && t > 0 ? minutes.Count(m => m.RamUsed / t >= MemoryFull) : 0;
        string of = total is double tt ? $" of {tt:0} GB ({peak / tt:P0})" : " GB";

        if (need && total is double size)
        {
            a.Lead = nearlyFull >= 10 ? $"**More memory would help.** It was nearly full ({MemoryFull:P0} or more) for {Count(nearlyFull, "minute", "minutes")} {period.Label}."
                : peak / size < 0.75 ? $"**{size:0} GB is enough for what you do.** The most in use {period.Label} was {peak:0.0} GB ({peak / size:P0})."
                : $"**{size:0} GB is holding up, with little to spare.** The most in use {period.Label} was {peak:0.0} GB ({peak / size:P0}).";
            a.Paragraphs.Add($"On average {average:0.0} GB was in use.");
        }
        else
        {
            a.Lead = $"Memory use peaked at **{peak:0.0}{of}** {period.Label}, and averaged {average:0.0} GB.";
            if (nearlyFull >= 3) a.Paragraphs.Add($"It was nearly full ({MemoryFull:P0} or more) for {Count(nearlyFull, "minute", "minutes")}: that is when a PC starts to drag.");
        }
        if (_q.Now && IsToday(period) && minutes[^1] is { } latest && TimeUtil.ToUnix(Now) - latest.Ts <= 240)
        {
            a.Paragraphs.Insert(0, a.Lead.Replace("**", ""));
            a.Lead = $"Right now **{latest.RamUsed:0.0}{(total is double installed ? $" of {installed:0} GB" : " GB")}** is in use (the last minute on record, {Clock(TimeUtil.FromUnix(latest.Ts))}).";
        }
        a.Facts.Add(new("Highest", $"{peak:0.0} GB", nearlyFull >= 3 ? AskTone.Warn : AskTone.Neutral));
        a.Facts.Add(new("Average", $"{average:0.0} GB"));
        if (total is double all) a.Facts.Add(new("Installed", $"{all:0} GB"));
        if (apps.Count > 0)
        {
            a.PointsTitle = "What used the most";
            foreach (var x in apps.Take(5))
                a.Points.Add(new($"**{x.Name}**: up to {Units.Megabytes(x.MemMax!.Value)}{(x.MemAvg is double avg ? $", {Units.Megabytes(avg)} on average" : "")}"));
        }
        a.FollowUps.Add(need ? "What used the most memory today?" : "Do I need more RAM?");
        a.FollowUps.Add($"What was heavy on my PC {period.Label}?");
        return a;
    }

    /// <summary>Each drive as last recorded (within the last week).</summary>
    private List<DriveDay> DrivesNow() =>
        [.. _db.GetDriveDays(TimeUtil.ToUnix(Now.Date.AddDays(-7))).GroupBy(d => d.Drive, StringComparer.OrdinalIgnoreCase).Select(g => g.MaxBy(d => d.Day)!)
            .Where(d => d.TotalGb >= 1).OrderBy(d => d.Drive, StringComparer.OrdinalIgnoreCase)];

    [GeneratedRegex(@"\b(fps|frames?|frame ?rate|stutter\w*|lag\w*|choppy|jitter\w*|hitch\w*)\b")]
    private static partial Regex FrameWords();

    [GeneratedRegex(@"\b(need|enough|more ram|more memory|upgrade|running out|run out|short of|too little|sufficient)\b")]
    private static partial Regex NeedWords();
}
