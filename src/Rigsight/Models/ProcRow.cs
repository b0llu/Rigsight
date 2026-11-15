using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core;
using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Services;

namespace Rigsight.Models;

/// <summary>One app's live resource use on the Memory and Processes pages.</summary>
public sealed partial class ProcRow(string exe) : ObservableObject
{
    public string Exe { get; } = exe;

    [ObservableProperty] private string _name = exe;
    [ObservableProperty] private string? _path;
    [ObservableProperty] private int _count;
    [ObservableProperty] private double _cpu;
    [ObservableProperty] private double _memMB;
    [ObservableProperty] private bool _hasWindow;
    /// <summary>What its processes read and write, in bytes a second.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiskCell))]
    private double _disk;
    /// <summary>What it moves over the network right now (down and up, bytes a second); the Processes page sets it
    /// from the internet's own readings while it is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NetCell))]
    private double _net;
    /// <summary>When the earliest of its processes started (Unix seconds; 0: not known).</summary>
    public long Started { get; private set; }
    /// <summary>Windows marks one of its processes as one it can't lose.</summary>
    public bool Critical { get; private set; }
    /// <summary>Share of all the PC's memory (0–100), for the bar's tooltip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemShareText))]
    private double _memShare;
    /// <summary>The bar (0–100): memory next to the biggest figure in the list (an app's memory or its usual), which fills it.</summary>
    [ObservableProperty] private double _bar;
    /// <summary>Where its usual falls on the bar (0–100), for the mark there.</summary>
    [ObservableProperty] private double _mark;

    /// <summary>Its usual memory (MB), as the Memory page read it for every app; null with too little on record.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsualText), nameof(HasUsual))]
    private double? _usualMB;
    /// <summary>Well above its usual (see <see cref="OverFactor"/>): its figure turns amber, its row is shaded and has its
    /// line (<see cref="OverLine"/>). Never a part of Windows, whose memory isn't the user's to act on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverText), nameof(OverLine), nameof(OverLineIsGrowing), nameof(OverAsk), nameof(SubText), nameof(SubIsGrowing))]
    private bool _isOver;
    /// <summary>The time it has grown since ("9:10 AM"), when it has grown in every hour of today; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubText), nameof(IsGrowing), nameof(SubIsGrowing), nameof(OverLine), nameof(OverLineIsGrowing), nameof(OverAsk))]
    private string? _growingSince;
    /// <summary>What the Memory page read about it for today: its hours and its peak (null until it has read them).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverLine), nameof(OverAsk))]
    private MemoryToday? _today;
    /// <summary>Showing each of its processes underneath.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChildren), nameof(IsLoading))]
    private bool _isExpanded;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChildren), nameof(IsLoading))]
    private List<ProcChild> _children = [];

    /// <summary>Where its memory is, by kind of process ("8 tabs", "6 extensions", "Google Chrome itself"); empty unless
    /// it is an app of several kinds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasShares))]
    private IReadOnlyList<ProcShare> _shares = [];
    public bool HasShares => Shares.Count > 0;

    /// <summary>Being ended from the Processes page: its row dims until it has gone.</summary>
    [ObservableProperty] private bool _isEnding;

    /// <summary>What is on record about it, for the box under its row on the Processes page (made when first opened there).</summary>
    [ObservableProperty] private ProcHistory? _history;

    // The list opens once the agent has sent the processes (a spinner in the chevron's place until then), not as an
    // empty gap before.
    public bool ShowChildren => IsExpanded && Children.Count > 0;
    public bool IsLoading => IsExpanded && Children.Count == 0;

    // Programs whose icon can't be read (protected ones) get the plain program icon.
    public ImageSource? Icon => IconCache.Get(Path) ?? IconCache.Program;
    public bool CanExpand => Count > 1;
    public string MemShareText => MemShare < 0.1 ? Lang.T("Under 0.1% of your memory") : Lang.T($"{MemShare:0.#}% of your memory");
    public string MemText => Units.Megabytes(MemMB);
    public string CpuText => FormatCpu(Cpu);
    public static string FormatCpu(double cpu) => Units.AppCpu(cpu);
    public string CountText => Count > 1 ? Lang.T($"{Count} processes") : Lang.T("1 process");
    /// <summary>The heading over its processes when opened ("21 PROCESSES").</summary>
    public string CountCaption => CountText.ToUpperInvariant();

    /// <summary>How far above its usual an app is before the Memory page points it out (see <see cref="MemoryPast.OverFactor"/>).</summary>
    public const double OverFactor = MemoryPast.OverFactor;
    /// <summary>How far above its usual an app pointed out has to stay to go on being pointed out (see <see cref="MemoryPast.StayOverFactor"/>).</summary>
    public const double StayOverFactor = MemoryPast.StayOverFactor;
    public bool HasUsual => UsualMB is not null;
    public string UsualText => UsualMB is { } usual ? Units.Megabytes(usual) : Nothing;
    public bool IsGrowing => GrowingSince is not null;
    /// <summary>
    /// The line under its name on the Memory page: since when it has grown, or else how many processes it is. A row
    /// above its usual says since when in its own line (<see cref="OverLine"/>), so its name keeps the count.
    /// </summary>
    public string SubText => GrowingSince is { } since && !IsOver ? Lang.T($"Growing since {since}") : CountText;
    /// <summary>The line under its name says it is growing (amber).</summary>
    public bool SubIsGrowing => IsGrowing && !IsOver;
    /// <summary>How far above its usual it is, for the box over its bar; null unless it is well above.</summary>
    public string? OverText => IsOver && UsualMB is { } usual ? OverTextOf(MemMB, usual) : null;

    /// <summary>
    /// The line under a row above its usual (Memory page, mock-up 2026-11-14 B): since when, and its peak today.
    /// "Above its usual since 1:00 PM, with a peak of 561 MB.", "Above its usual since it started at 9:12 AM, …", or,
    /// growing, "Growing every hour since 8:00 AM, …". Null unless it is above its usual.
    /// </summary>
    public string? OverLine
    {
        get
        {
            if (!IsOver || UsualMB is not { } usual) return null;
            // Today's highest reading on record, or what it holds now where that is higher still.
            string peak = Units.Megabytes(Math.Max(Today?.PeakMB ?? 0, MemMB));
            if (GrowingSince is { } growing) return Lang.T($"Growing every hour since {growing}, with a peak of {peak}.");
            var started = Started > 0 ? TimeUtil.FromUnix(Started) : (DateTime?)null;
            var (at, sinceStart) = MemoryPast.AboveSince(Today?.Hours ?? [], usual, started, Today?.ReadAt ?? DateTime.Now);
            string time = at.ToString(TimeText.Fit("h:mm tt"), CultureInfo.CurrentCulture);
            return sinceStart
                ? Lang.T($"Above its usual since it started at {time}, with a peak of {peak}.")
                : Lang.T($"Above its usual since {time}, with a peak of {peak}.");
        }
    }

    /// <summary>Its line says it keeps growing (amber, like "Growing since").</summary>
    public bool OverLineIsGrowing => IsOver && IsGrowing;

    /// <summary>The AI button on a row above its usual: what to do about it, from the Memory page's records.</summary>
    public AiQuestion? OverAsk => OverLine is { } line
        ? new(Lang.T($"{Name} is at {MemText}. It usually holds {UsualText}. {line} What should I do?"), Core.Ai.AiPages.Memory)
        : null;

    /// <summary>
    /// "Twice its usual" around double, else the times over ("2.6 times its usual"). Decided on the figure as it is
    /// written: 1.86 times was written "1.9 times its usual" while 1.9 was "Twice its usual".
    /// </summary>
    public static string OverTextOf(double memMB, double usualMB)
    {
        double ratio = Math.Round(memMB / usualMB, 1, MidpointRounding.AwayFromZero);
        return ratio is >= 1.9 and < 2.3 ? Lang.T("Twice its usual") : Lang.T($"{ratio:0.#} times its usual");
    }

    // Worked out once: it is asked on every update of the list.
    private bool? _ordinary;
    internal bool Ordinary => _ordinary ??= EndRisks.Classify(Exe, Path, marked: Critical) == EndRisk.None;

    partial void OnUsualMBChanged(double? value) => Judge();

    private void Judge() => IsOver = UsualMB is > 0 and { } usual && MemMB > (IsOver ? StayOverFactor : OverFactor) * usual && Ordinary;

    // On the Processes page a figure of nothing is a dash, so the few apps doing something stand out of a long list.
    public const string Nothing = "–";
    public string CpuCell => CpuCellOf(Cpu);
    public string NetCell => RateCell(Net);
    public string DiskCell => RateCell(Disk);
    public static string CpuCellOf(double cpu) => cpu < 0.05 ? Nothing : FormatCpu(cpu);
    public static string RateCell(double bytesPerSec) => bytesPerSec < 1024 ? Nothing : Units.Speed(bytesPerSec);

    public void Update(ProcInfo p)
    {
        Name = p.Name;
        if (Path != p.Path)
        {
            Path = p.Path;
            _ordinary = null;
            OnPropertyChanged(nameof(Icon));
        }
        Count = p.Count;
        Cpu = p.Cpu;
        MemMB = p.MemMB;
        HasWindow = p.HasWindow;
        Disk = p.Disk;
        Started = p.Started;
        if (Critical != p.Critical)
        {
            Critical = p.Critical;
            _ordinary = null;
        }
        Judge();
        OnPropertyChanged(nameof(MemText));
        if (IsOver)
        {
            OnPropertyChanged(nameof(OverText));
            OnPropertyChanged(nameof(OverLine));
            OnPropertyChanged(nameof(OverAsk));
        }
        OnPropertyChanged(nameof(CpuText));
        OnPropertyChanged(nameof(CpuCell));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(CountCaption));
        OnPropertyChanged(nameof(SubText));
        OnPropertyChanged(nameof(CanExpand));
        if (p.Processes is not null && IsExpanded)
        {
            Children = KeptRows(Children, p.Processes);
            // The rows are kept as they were while only their figures move: the split is worked out from them each time.
            Shares = Kept.Or(Shares, ProcShare.Of(Name, Children));
        }
    }

    partial void OnChildrenChanged(List<ProcChild> value) => Shares = Kept.Or(Shares, ProcShare.Of(Name, value));

    /// <summary>
    /// The processes as they are now, in rows that are kept: the same processes in the same order keep their list (the
    /// page builds nothing anew, and a row under the pointer or with its menu open stays that row), and when one came
    /// or went the others keep their rows in the new list.
    /// </summary>
    private static List<ProcChild> KeptRows(List<ProcChild> shown, List<ProcDetail> fresh)
    {
        bool same = shown.Count == fresh.Count;
        for (int i = 0; same && i < fresh.Count; i++) same = shown[i].Pid == fresh[i].Pid;
        var byPid = same ? null : shown.ToDictionary(c => c.Pid);
        var list = same ? shown : new List<ProcChild>(fresh.Count);
        for (int i = 0; i < fresh.Count; i++)
        {
            if (same) shown[i].Update(fresh[i]);
            else if (byPid!.TryGetValue(fresh[i].Pid, out var row))
            {
                row.Update(fresh[i]);
                list.Add(row);
            }
            else list.Add(new ProcChild(fresh[i]));
        }
        return list;
    }
}

/// <summary>
/// What the Memory page read about one app for today: its memory in each hour from midnight (MB, null where not
/// recorded), its highest reading today (MB), and when that was read.
/// </summary>
public sealed record MemoryToday(IReadOnlyList<double?> Hours, double? PeakMB, DateTime ReadAt);
