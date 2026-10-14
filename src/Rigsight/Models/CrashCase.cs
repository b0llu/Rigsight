using Rigsight.Core.Stability;

namespace Rigsight.Models;

/// <summary>
/// A stretch of the minutes before with nothing recorded, and why as far as Windows' own log says ("PC was off",
/// "PC shut off unexpectedly", "PC was asleep"; "Not recorded" where it doesn't say).
/// </summary>
public sealed record CaseGap(DateTime From, DateTime To, string Why);

/// <summary>A line in one of the case's lists: what, a second line under it, and what stands at its right.</summary>
public sealed record CaseLine(string Title, string? Detail, string? Side);

/// <summary>One bar of "how often, by month".</summary>
public sealed record CaseBar(string Label, int Count, double Share)
{
    public double Height => 3 + 44 * Share;
}

/// <summary>One thing the finding rests on or ruled out, ready to draw: the icon and the palette brush that goes with its kind.</summary>
public sealed record CaseEvidence(string Icon, string Brush, string Title, string Detail)
{
    public static CaseEvidence Of(Evidence e) => new(e.Kind switch
    {
        "power" => "", "pc" => "", "clock" => "", "pattern" => "", "change" => "",
        "warn" => "", "ok" => "", "unknown" => "", _ => "",
    }, e.Kind switch { "ok" => "GoodBrush", "unknown" => "MutedBrush", "warn" => "WarmBrush", _ => "CpuBrush" }, e.Title, e.Detail);
}

/// <summary>
/// A crash opened: its finding with what it rests on, the 45 minutes before it, what was running, what had changed,
/// its earlier times, and what Windows recorded. Everything a person would otherwise piece together from Event Viewer.
/// </summary>
public sealed class CrashCase
{
    /// <summary>Minutes of history shown before the crash.</summary>
    public const int MinutesBefore = 45;

    public required CrashGroup Group { get; init; }
    public required CrashFinding Finding { get; init; }
    /// <summary>The time shown: the row's only one, or the one picked of a row that stands for several.</summary>
    public required CrashRow Row { get; init; }

    public string Title => Group.Title;
    public string KindLabel => Group.KindLabel;
    public CrashSeverity Severity => Group.Severity;
    public string Chip => Finding.Chip;
    public string Sure => Finding.Confidence.ToString();
    /// <summary>The confidence alone for the case's own chip: its cause is the headline beside it.</summary>
    public string SureWord => Finding.Confidence switch { Confidence.Named => "NAMED BY WINDOWS", Confidence.Likely => "LIKELY", Confidence.Possible => "POSSIBLE", _ => "NOT ENOUGH TO SAY" };
    public string Head => Finding.Head;
    /// <summary>The case's headline: the finding itself, without its full stop.</summary>
    public string Headline => Finding.Head.TrimEnd('.');
    /// <summary>The one line under it: what it was called, when, and how often.</summary>
    public string Under => string.Join("  ·  ", new[] { Title == Headline ? null : Title, WhenText, WhenNote }.Where(s => !string.IsNullOrEmpty(s)));
    public string Text => Finding.Text;
    public string Advice => Finding.Advice;
    public string WhenText => Group.IsIncident ? $"{Group.First.Time:ddd d MMM, h:mm} to {Group.Latest.Time:h:mm tt}" : Row.TimeText;
    /// <summary>Under the time: where the time shown isn't Windows' own, or how many times it happened.</summary>
    public string? WhenNote { get; init; }

    public List<CaseEvidence> Points { get; init; } = [];
    public List<CaseEvidence> RuledOut { get; init; } = [];
    public bool HasRuledOut => RuledOut.Count > 0;
    /// <summary>What wasn't recorded and so couldn't be checked, and what the finding can't tell.</summary>
    public List<CaseEvidence> NotKnown { get; init; } = [];
    public bool HasNotKnown => NotKnown.Count > 0;
    public bool HasPoints => Points.Count > 0;

    /// <summary>Everything recorded about the PC in each of the minutes before (see <see cref="Controls.CaseLanes"/>).</summary>
    public List<CaseMinute> Minutes { get; init; } = [];
    public List<CaseLane> Lanes { get; init; } = [];
    public List<CaseStrip> Strips { get; init; } = [];
    public List<CaseMark> Marks { get; init; } = [];
    /// <summary>What stood out in those minutes, in words, above the lanes (the last line: what was calm).</summary>
    public List<StoodOut> StoodOut { get; init; } = [];
    /// <summary>The box beside the lanes: the minute pointed at, or the crash's own.</summary>
    public CaseReadout? Readout { get; init; }
    public List<CaseGap> Gaps { get; init; } = [];
    /// <summary>Above the lanes, at the right: the stretch they cover.</summary>
    public string RangeText => Minutes.Count == 0 ? "" : $"{ChartFrom:h:mm tt} to {Marks.LastOrDefault(k => k.IsCrash)?.At ?? ChartTo:h:mm tt} · one reading a minute";
    public DateTime ChartFrom { get; init; }
    public DateTime ChartTo { get; init; }
    public bool HasChart => Minutes.Count > 0;
    /// <summary>Why there is no chart ("Nothing was being recorded then…").</summary>
    public string NoChart { get; init; } = "";

    public List<CaseLine> Running { get; init; } = [];
    public bool HasRunning => Running.Count > 0;
    public List<CaseLine> Changes { get; init; } = [];
    public bool HasChanges => Changes.Count > 0;
    public string NoChanges { get; init; } = "";

    public string HistoryTitle { get; init; } = "";
    public List<CaseBar> Bars { get; init; } = [];
    public bool HasBars => Bars.Count > 0;
    public List<CaseLine> Times { get; init; } = [];
    public string HistoryNote { get; init; } = "";

    public List<CaseLine> Tech { get; init; } = [];
}
