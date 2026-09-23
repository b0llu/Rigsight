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
    /// <summary>Memory relative to the biggest app (0–100), for the bar.</summary>
    [ObservableProperty] private double _memShare;

    public ImageSource? Icon => IconCache.Get(Path);
    public string MemText => Units.Megabytes(MemMB);
    public string CpuText => Cpu < 0.05 ? "0%" : Cpu < 10 ? $"{Cpu:0.0}%" : $"{Cpu:0}%";
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
    }
}
