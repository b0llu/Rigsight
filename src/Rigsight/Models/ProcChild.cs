using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core;
using Rigsight.Core.Protocol;

namespace Rigsight.Models;

/// <summary>One process of an app, listed under it on the Memory and Processes pages.</summary>
public sealed partial class ProcChild : ObservableObject
{
    public ProcChild(ProcDetail p)
    {
        Pid = p.Pid;
        Label = p.Label;
        MemMB = p.MemMB;
        Cpu = p.Cpu;
        Disk = p.Disk;
        Critical = p.Critical;
    }

    public int Pid { get; }
    public string PidText => $"PID {Pid}";
    public string Label { get; private set; }
    public double MemMB { get; private set; }
    public double Cpu { get; private set; }
    public double Disk { get; private set; }
    /// <summary>Windows marks it as a process it can't lose.</summary>
    public bool Critical { get; private set; }

    public string MemText => Units.Megabytes(MemMB);
    public string CpuText => ProcRow.FormatCpu(Cpu);

    // On the Processes page a figure of nothing is a dash.
    public string CpuCell => ProcRow.CpuCellOf(Cpu);
    public string DiskCell => ProcRow.RateCell(Disk);

    /// <summary>Being ended from the Processes page: its row dims until it has gone.</summary>
    [ObservableProperty] private bool _isEnding;

    /// <summary>The same process a couple of seconds on: its row stays, only what changed is said again.</summary>
    public void Update(ProcDetail p)
    {
        if (Label != p.Label)
        {
            Label = p.Label;
            OnPropertyChanged(nameof(Label));
        }
        if (MemMB != p.MemMB)
        {
            MemMB = p.MemMB;
            OnPropertyChanged(nameof(MemText));
        }
        if (Cpu != p.Cpu)
        {
            Cpu = p.Cpu;
            OnPropertyChanged(nameof(CpuText));
            OnPropertyChanged(nameof(CpuCell));
        }
        Critical = p.Critical;
        if (Disk != p.Disk)
        {
            Disk = p.Disk;
            OnPropertyChanged(nameof(DiskCell));
        }
    }
}
