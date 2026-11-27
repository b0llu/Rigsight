using System.Globalization;
using Rigsight.Controls;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Drivers;
using Rigsight.ViewModels;

namespace Rigsight.Models;

/// <summary>A bucket of the opened driver's calls by how long they took: "0.25 to 0.5 ms", its count, and its bar (log scale, so rare long calls still show).</summary>
public sealed record LatencyBucket(string Label, string Count, double Share, bool Long);

/// <summary>
/// A driver opened on the Latency page (issue #15, approved mock-up 2026-11-24): from its row, from a Timeline driver line
/// (then the mark's own story comes first, as the Timeline showed it, and Back goes to Timeline), from the taskbar's Driver
/// delay or from a game play. What it did over the period: its longest and when, its calls over 1 ms, its time on the CPU,
/// DPCs against interrupts, how long its calls took, each hour's longest, what it is, the changes before, what can be tried.
/// </summary>
public sealed class LatencyDriverView
{
    public LatencyDriverView(string driver, DriverPart? part, LatencyDriver? row, LatencyDriverData data, DateTime from, DateTime to, DelayView? mark, bool fromTimeline)
    {
        Driver = driver;
        Mark = mark;
        FromTimeline = fromTimeline;
        Who = LatencyWords.Who(driver, part);
        File = LatencyWords.File(driver, part);
        Icon = LatencyWords.Icon(driver, part);
        Period = (to - from).TotalDays <= 1.01 ? from.ToString("dddd d MMMM", CultureInfo.CurrentCulture)
            : Lang.T($"{from:d MMMM} to {to.AddSeconds(-1):d MMMM}");

        int longest = data.Longest?.Us ?? row?.LongestUs ?? data.Hours.Select(h => h.LongestUs).DefaultIfEmpty().Max();
        int over = row?.Long ?? data.Hours.Sum(h => h.Long);
        long total = row?.TotalUs ?? data.Hours.Sum(h => h.TotalUs);
        string when = data.Longest is { Minute: > 0 } l ? TimeText.At(TimeUtil.FromUnix(l.Minute), "dddd d, h:mm tt") : "";
        string busy = total < 1_000_000 ? Lang.T("under 1s") : Units.Duration(total / 1_000_000.0);
        int hours = data.Hours.Select(h => h.Ts).Distinct().Count();

        var s = data.Split;
        string kind, kindUnder;
        if (s is null || s.DpcCount + s.IsrCount == 0)
        {
            kind = Lang.T("Not kept apart");
            kindUnder = data.FirstSplitHour is long fs ? Lang.T($"Kept apart from {TimeUtil.FromUnix(fs):d MMMM} on") : Lang.T("Not kept apart before this update");
        }
        else
        {
            kind = s.IsrCount == 0 ? Lang.T("DPCs only") : s.DpcCount == 0 ? Lang.T("Interrupts only")
                : s.DpcUs >= 4 * s.IsrUs ? Lang.T("Mostly DPCs") : s.IsrUs >= 4 * s.DpcUs ? Lang.T("Mostly interrupts") : Lang.T("DPCs and interrupts");
            kindUnder = s.IsrCount == 0 ? Lang.T($"Longest DPC {LatencyWords.Ms(s.DpcLongestUs)}") : s.DpcCount == 0 ? Lang.T($"Longest interrupt {LatencyWords.Ms(s.IsrLongestUs)}")
                : Lang.T($"Longest DPC {LatencyWords.Ms(s.DpcLongestUs)}, interrupt {LatencyWords.Ms(s.IsrLongestUs)}");
        }

        Facts =
        [
            new(Lang.T("LONGEST"), LatencyWords.Ms(longest), when.Length > 0 ? Wording.Capital(when) : data.UsualUs is int u ? Wording.Capital(DriverWords.Usually(u)) : ""),
            new(Lang.T("OVER 1 MS"), over == 0 ? Lang.T("None") : over == 1 ? Lang.T("Once") : Lang.T($"{over:N0} times"), (to - from).TotalDays <= 1.01 ? Lang.T("That day") : Lang.T("In these days")),
            new(Lang.T("BUSY IN TOTAL"), busy, hours == 1 ? Lang.T("In 1 hour recorded") : Lang.T($"In {hours:N0} hours recorded")),
            new(Lang.T("KIND OF CALL"), kind, kindUnder),
        ];

        // How long its calls took: six buckets, bars on a log scale so the rare long ones still show.
        if (s is not null && s.Buckets.Sum() > 0)
        {
            string[] labels = [Lang.T("Under 0.25 ms"), Lang.T("0.25 to 0.5 ms"), Lang.T("0.5 to 1 ms"), Lang.T("1 to 2 ms"), Lang.T("2 to 4 ms"), Lang.T("4 ms or more")];
            double top = Math.Log10(s.Buckets.Max() + 1);
            Buckets = [.. labels.Select((label, i) => new LatencyBucket(label, s.Buckets[i].ToString("N0", CultureInfo.CurrentCulture),
                s.Buckets[i] == 0 ? 0 : Math.Max(0.02, Math.Log10(s.Buckets[i] + 1) / Math.Max(1, top)), i >= 3))];
        }
        BucketsNote = Buckets.Count == 0
            ? (data.FirstSplitHour is long first ? Lang.T($"Counted from {TimeUtil.FromUnix(first):d MMMM} on, so not for these days.") : Lang.T("Counted from this update on."))
            : data.FirstSplitHour is long f2 && f2 > TimeUtil.ToUnix(from) ? Lang.T($"Counted from {TimeUtil.FromUnix(f2):d MMMM} on.") : "";

        // Its longest each hour (or day) of the period.
        bool hourly = (to - from).TotalDays <= LatencyReport.HourlyDays;
        var slots = LatencyReport.BuildSlots(from, to, hourly, [.. data.Hours.Select(h => (h.Ts, h.LongestUs, h.Driver, h.Long))], [], []);
        int every = Math.Max(1, (int)Math.Ceiling(slots.Count / 8.0));
        Slots = [.. slots.Select((x, i) =>
        {
            string axis = !hourly ? (i % every == 0 ? x.Start.ToString("d MMM", CultureInfo.CurrentCulture) : "")
                : slots.Count <= 24 ? (i % 3 == 0 ? TimeText.At(x.Start, "h tt") : "")
                : x.Start.Hour == 0 || i == 0 ? x.Start.ToString("ddd d", CultureInfo.CurrentCulture) : "";
            string title = hourly ? Lang.T($"{x.Start:ddd d MMM}, {TimeText.Time(x.Start)} to {TimeText.Time(x.End)}") : x.Start.ToString("dddd d MMMM", CultureInfo.CurrentCulture);
            var lines = x.LongestUs is int us
                ? new List<string> { Lang.T($"Longest {LatencyWords.Ms(us)}"), x.Long == 0 ? Lang.T("No call over 1 ms") : x.Long == 1 ? Lang.T("Over 1 ms once") : Lang.T($"Over 1 ms {x.Long:N0} times") }
                : [Wording.NotRecorded];
            return new LatencyChartSlot(axis, title, x.LongestUs, false, x.LongestUs >= DriverRules.LongUs, lines, x.Start, x.End);
        })];
        ChartTitle = hourly ? Lang.T("Its longest each hour") : Lang.T("Its longest each day");
        UsualText = data.UsualUs is int usual ? Lang.T($"Its usual longest is {LatencyWords.Ms(usual)}, from the {DriverRules.UsualDays} days before.") : Lang.T("Too soon to say what's usual for it.");

        What = DriverWords.What(driver, part);
        Tips = [.. DriverWords.Tips(DriverWords.AreaOf(driver, part)).Select(t => new DelayTip(t.Step, t.More))];
        // What to try (1.21.0): the same tips as one block, with a button where one is a screen RigSight can open.
        Try = Core.Advice.WhatToTry.Latency(DriverWords.AreaOf(driver, part), driver, $"{Who} ({File}). {UsualText}") is { } block ? new TryBlockView(block) : null;
        Changes = [.. data.Changes.Select(c => new DelayOtherLine(c.Shown, TimeText.At(c.Time, "ddd d MMM, h:mm tt")))];
    }

    public string Driver { get; }
    /// <summary>Opened from a Timeline driver line: the mark's own story first.</summary>
    public DelayView? Mark { get; }
    public bool IsMark => Mark is not null;
    /// <summary>Back goes to Timeline (the page it came from), not to the Latency list.</summary>
    public bool FromTimeline { get; }
    public string BackText => FromTimeline ? Lang.T("Timeline") : Lang.T("Latency");
    public string BackName => FromTimeline ? Lang.T("Back to Timeline") : Lang.T("Back to Latency");

    public string Who { get; }
    public string File { get; }
    public string Icon { get; }
    public string Period { get; }
    public List<DelayFact> Facts { get; }
    public List<LatencyBucket> Buckets { get; } = [];
    public bool HasBuckets => Buckets.Count > 0;
    public string BucketsNote { get; }
    public List<LatencyChartSlot> Slots { get; }
    public string ChartTitle { get; }
    public string UsualText { get; }
    public string What { get; }
    public string WhyItMatters => DriverWords.WhyItMatters;
    public List<DelayTip> Tips { get; }
    public bool HasTips => Tips.Count > 0;
    public TryBlockView? Try { get; }
    public List<DelayOtherLine> Changes { get; }
    public bool HasChanges => Changes.Count > 0;
}
