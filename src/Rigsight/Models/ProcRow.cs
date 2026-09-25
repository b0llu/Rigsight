using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Services;

namespace Rigsight.Models;

/// <summary>One app's live resource use on the Memory page.</summary>
public sealed partial class ProcRow(string exe) : ObservableObject
{
    public string Exe { get; } = exe;

    [ObservableProperty] private string _name = exe;
    [ObservableProperty] private string? _path;
    [ObservableProperty] private int _count;
    [ObservableProperty] private double _cpu;
    [ObservableProperty] private double _memMB;
    [ObservableProperty] private bool _hasWindow;
    /// <summary>Share of all the PC's memory (0–100), for the bar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemShareText))]
    private double _memShare;
    /// <summary>Showing each of its processes underneath.</summary>
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private List<ProcChild> _children = [];

    // Programs whose icon can't be read (protected ones) get the plain program icon.
    public ImageSource? Icon => IconCache.Get(Path) ?? IconCache.Program;
    public bool CanExpand => Count > 1;
    public string MemShareText => MemShare < 0.1 ? "Under 0.1% of your memory" : $"{MemShare:0.#}% of your memory";
    public string MemText => Units.Megabytes(MemMB);
    public string CpuText => FormatCpu(Cpu);
    public static string FormatCpu(double cpu) => cpu < 0.05 ? "0%" : cpu < 10 ? $"{cpu:0.0}%" : $"{cpu:0}%";
    public string CountText => Count > 1 ? $"{Count} processes" : "1 process";

    public void Update(ProcInfo p)
    {
        Name = p.Name;
        if (Path != p.Path)
        {
            Path = p.Path;
            OnPropertyChanged(nameof(Icon));
        }
        Count = p.Count;
        Cpu = p.Cpu;
        MemMB = p.MemMB;
        HasWindow = p.HasWindow;
        OnPropertyChanged(nameof(MemText));
        OnPropertyChanged(nameof(CpuText));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(CanExpand));
        if (p.Processes is not null && IsExpanded) Children = [.. p.Processes.Select(c => new ProcChild(c))];
    }
}
