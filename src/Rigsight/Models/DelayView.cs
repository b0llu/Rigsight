using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Drivers;
using Rigsight.Services;

namespace Rigsight.Models;

/// <summary>One of the opened mark's four facts: a small caps label, the figure big, a quieter line under it.</summary>
public sealed record DelayFact(string Label, string Value, string Under);

/// <summary>An hour's bar in the opened mark's day chart: its height (px), its colour, and its hover.</summary>
public sealed record DelayBar(double Height, string Brush, string Tip);

/// <summary>Another driver busy much longer than usual then: "Windows' graphics system · dxgkrnl.sys", "5.1 ms".</summary>
public sealed record DelayOtherLine(string Name, string Value);

/// <summary>A step to try, with a quieter line under it where it helps.</summary>
public sealed record DelayTip(string Step, string? More)
{
    public bool HasMore => More is { Length: > 0 };
}

/// <summary>
/// A driver mark opened from the Timeline (issue #15 follow-up, approved mock-up 2026-11-24): in place of the list until
/// Back, as a big change's before and after opens. What happened, its four facts, what the driver is, what else was busy
/// longer than usual then, the big changes in the week before, the driver's longest each hour of that day against its
/// usual, and what can be tried.
/// </summary>
public sealed class DelayView(DelayCase c)
{
    /// <summary>The chart's height, px: the tallest bar reaches it.</summary>
    public const double ChartHeight = 120;

    private DelayStretch S => c.Stretch;

    public string Title => S.Title;

    public string When
    {
        get
        {
            DateTime from = TimeUtil.FromUnix(S.From), to = TimeUtil.FromUnix(S.To);
            string day = from.ToString("dddd d MMMM");
            return S.Minutes < 2 ? $"{day}, {TimeText.Time(from)}" : Lang.T($"{day}, {TimeText.Time(from)} to {TimeText.Time(to)}");
        }
    }

    public List<DelayFact> Facts =>
    [
        new(Lang.T("LONGEST"), DriverWords.Ms(S.LongestUs), Wording.Capital(DriverWords.Usually(S.UsualUs))),
        new(Lang.T("OVER 1 MS"), S.LongBursts == 1 ? Lang.T("Once") : Lang.T($"{S.LongBursts} times"),
            S.Minutes < 2 ? Lang.T("In that minute") : Lang.T($"In those {S.Minutes} minutes")),
        new(Lang.T("IN FRONT"), c.InFront ?? Lang.T("Nothing"), c.InGame ? Lang.T("A game") : Lang.T("No game running")),
        new(Lang.T("DRIVER FILE"), S.Driver, S.Who),
    ];

    public string What => c.What;
    public string WhyItMatters => DriverWords.WhyItMatters;

    public List<DelayOtherLine> Others => [.. c.Others.Select(o => new DelayOtherLine($"{o.Who} · {o.Driver}", DriverWords.Ms(o.LongestUs)))];
    public bool HasOthers => c.Others.Count > 0;

    public List<DelayOtherLine> Changes => [.. c.Changes.Select(ch => new DelayOtherLine(ch.Shown, TimeText.At(ch.Time, "ddd d MMM, h:mm tt")))];
    public bool HasChanges => c.Changes.Count > 0;

    public string ChartTitle => Lang.T($"Its longest each hour, {TimeUtil.FromUnix(S.LongestAt):dddd d}");

    /// <summary>The day's 24 hours, each the driver's longest burst in it; well past its usual in warm, the rest muted.</summary>
    public List<DelayBar> Bars
    {
        get
        {
            var day = TimeUtil.FromUnix(S.LongestAt).Date;
            double top = Math.Max(S.UsualUs * 1.5, c.Hours.Count == 0 ? 1 : c.Hours.Max(h => h.LongestUs));
            double floor = Math.Max(DriverRules.LongUs, DriverRules.MuchLonger * S.UsualUs);
            var bars = new List<DelayBar>();
            for (int i = 0; i < 24; i++)
            {
                var hour = c.Hours.Where(h => (int)Math.Floor((h.Start - day).TotalHours) == i || (i == 0 && h.Start < day)).MaxBy(h => h.LongestUs);
                // The hour as recorded: on the hour of Windows' clock, so it starts at :30 where the time zone is half an hour off.
                var start = hour?.Start ?? day.AddHours(i);
                string span = Lang.T($"{TimeText.Time(start)} to {TimeText.Time(start.AddHours(1))}");
                bars.Add(hour is null
                    ? new(0, "MutedBrush", Lang.T($"{span}: no reading"))
                    : new(Math.Max(2, ChartHeight * hour.LongestUs / top), hour.LongestUs >= floor ? "WarmBrush" : "MutedBrush", Lang.T($"{span}: up to {DriverWords.Ms(hour.LongestUs)}")));
            }
            return bars;
        }
    }

    /// <summary>The usual's dashed line, from the chart's bottom (px).</summary>
    public double UsualAt
    {
        get
        {
            double top = Math.Max(S.UsualUs * 1.5, c.Hours.Count == 0 ? 1 : c.Hours.Max(h => h.LongestUs));
            return ChartHeight * S.UsualUs / top;
        }
    }

    /// <summary>The usual's line, lifted from the chart's bottom.</summary>
    public System.Windows.Thickness UsualMargin => new(0, 0, 0, UsualAt);

    public string UsualText => Lang.T($"Usual, {DriverWords.Ms(S.UsualUs)}");

    public List<DelayTip> Tips => [.. c.Tips.Select(t => new DelayTip(t.Step, t.More))];
    public bool HasTips => c.Tips.Count > 0;

    /// <summary>The AI's question: what happened, with its figures, and what could be behind it.</summary>
    public AiQuestion Ask => new(Lang.T($"{S.Title} at {When}: {S.Driver}, up to {DriverWords.Ms(S.LongestUs)}, {DriverWords.Usually(S.UsualUs)}. What could cause this, and what can I do?"),
        Core.Ai.AiPages.Timeline, Explain: true);
}
