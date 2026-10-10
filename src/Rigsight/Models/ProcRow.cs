using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core;
using Rigsight.Core.Apps;
using Rigsight.Core.Protocol;
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
    /// <summary>Well above its usual (see <see cref="OverFactor"/>): its figure turns amber. Never a part of Windows,
    /// whose memory isn't the user's to act on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverText))]
    private bool _isOver;
    /// <summary>The time it has grown since ("9:10 AM"), when it has grown in every hour of today; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubText), nameof(IsGrowing))]
    private string? _growingSince;
    /// <summary>Showing each of its processes underneath.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChildren), nameof(IsLoading))]
    private bool _isExpanded;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChildren), nameof(IsLoading))]
    private List<ProcChild> _children = [];

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
    public string MemShareText => MemShare < 0.1 ? "Under 0.1% of your memory" : $"{MemShare:0.#}% of your memory";
    public string MemText => Units.Megabytes(MemMB);
    public string CpuText => FormatCpu(Cpu);
    public static string FormatCpu(double cpu) => cpu < 0.05 ? "0%" : cpu < 10 ? $"{cpu:0.0}%" : $"{cpu:0}%";
    public string CountText => Count > 1 ? $"{Count} processes" : "1 process";

    /// <summary>How far above its usual an app is before the Memory page points it out.</summary>
    public const double OverFactor = 1.6;
    public bool HasUsual => UsualMB is not null;
    public string UsualText => UsualMB is { } usual ? Units.Megabytes(usual) : Nothing;
    public bool IsGrowing => GrowingSince is not null;
    /// <summary>The line under its name on the Memory page: since when it has grown, or else how many processes it is.</summary>
    public string SubText => GrowingSince is { } since ? $"Growing since {since}" : CountText;
    /// <summary>How far above its usual it is, for the box over its bar; null unless it is well above.</summary>
    public string? OverText => IsOver && UsualMB is { } usual ? OverTextOf(MemMB, usual) : null;

    /// <summary>"Twice its usual" around double, else the times over ("2.6 times its usual").</summary>
    public static string OverTextOf(double memMB, double usualMB)
    {
        double ratio = memMB / usualMB;
        return ratio is >= 1.9 and < 2.3 ? "Twice its usual" : $"{ratio:0.#} times its usual";
    }

    // Worked out once: it is asked on every update of the list.
    private bool? _ordinary;
    private bool Ordinary => _ordinary ??= EndRisks.Classify(Exe, Path, marked: Critical) == EndRisk.None;

    partial void OnUsualMBChanged(double? value) => Judge();

    private void Judge() => IsOver = UsualMB is > 0 and { } usual && MemMB > OverFactor * usual && Ordinary;

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
        if (IsOver) OnPropertyChanged(nameof(OverText));
        OnPropertyChanged(nameof(CpuText));
        OnPropertyChanged(nameof(CpuCell));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(SubText));
        OnPropertyChanged(nameof(CanExpand));
        if (p.Processes is not null && IsExpanded) Children = KeptRows(Children, p.Processes);
    }

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
