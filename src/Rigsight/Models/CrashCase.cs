using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core.Stability;

namespace Rigsight.Models;

/// <summary>
/// A stretch of the minutes before with nothing recorded, and why as far as Windows' own log says ("PC was off",
/// "PC shut off suddenly", "PC was asleep"; "Not recorded" where it doesn't say).
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
public sealed partial class CrashCase : ObservableObject
{
    /// <summary>Minutes of history shown before the crash.</summary>
    public const int MinutesBefore = 45;

    /// <summary>Minutes of history shown after it.</summary>
    public const int MinutesAfter = 10;

    public required CrashGroup Group { get; init; }
    public required CrashFinding Finding { get; init; }
    /// <summary>The time shown: the row's only one, or the one picked of a row that stands for several.</summary>
    public required CrashRow Row { get; init; }

    public string Title => Group.Title;
    public string KindLabel => Group.KindLabel;
    public CrashSeverity Severity => Group.Severity;
    /// <summary>The chip, cause included: the same as the list's for this crash.</summary>
    public string Chip => Finding.Chip;
    public string Sure => Finding.Confidence.ToString();
    /// <summary>The chip's hover: where "Confirmed" comes from, or what the word means.</summary>
    public string SureHint => Finding.Confidence == Confidence.Named ? "Windows' own record says this."
        : "How sure RigSight is of what happened, by what it rests on";
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

    /// <summary>The 45 minutes before and the 10 after, a reading a minute (see <see cref="Controls.CaseLanes"/>).</summary>
    public required CaseView Wide { get; init; }
    /// <summary>The seconds around it, where they were kept (see <see cref="CloseUp"/>); null where they weren't.</summary>
    public CaseView? Close { get; init; }
    public bool HasCloseUp => Close is not null;

    /// <summary>The close-up is shown (where there is one): an opened crash starts on it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(View), nameof(StoodOut), nameof(Readout), nameof(RangeText), nameof(Minutes), nameof(Lanes), nameof(Strips), nameof(Marks), nameof(Gaps), nameof(Wider))]
    private bool _upClose;

    /// <summary>The other side of the switch: the minutes shown.</summary>
    public bool Wider { get => !UpClose; set => UpClose = !value; }

    /// <summary>What the lanes, the box and the lines above them show now.</summary>
    public CaseView View => UpClose && Close is not null ? Close : Wide;

    /// <summary>"Around the crash", "Around the sudden shut-off", "Around the blue screen".</summary>
    public string AroundTitle => Row.Event.Kind switch
    {
        CrashKind.UnexpectedShutdown => "Around the sudden shut-off",
        CrashKind.SystemCrash => "Around the blue screen",
        CrashKind.GpuDriverReset => "Around the graphics driver reset",
        _ => "Around the crash",
    };

    public List<CaseMinute> Minutes => View.Minutes;
    public List<CaseLane> Lanes => View.Lanes;
    public List<CaseStrip> Strips => View.Strips;
    public List<CaseMark> Marks => View.Marks;
    /// <summary>What stood out, in words, above the lanes (the last line: what was calm).</summary>
    public List<StoodOut> StoodOut => View.StoodOut;
    /// <summary>The box beside the lanes: the moment pointed at, or the crash's own.</summary>
    public CaseReadout? Readout => View.Readout;
    public List<CaseGap> Gaps => View.Gaps;
    /// <summary>Above the lanes, at the right: the stretch they cover.</summary>
    public string RangeText => View.RangeText;
    public DateTime ChartFrom => View.ChartFrom;
    public DateTime ChartTo => View.ChartTo;
    public bool HasChart => Wide.HasChart || Close is { HasChart: true };
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
