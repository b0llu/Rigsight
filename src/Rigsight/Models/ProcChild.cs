using Rigsight.Core;
using Rigsight.Core.Protocol;

namespace Rigsight.Models;

/// <summary>One process of an app, listed under it on the Memory page.</summary>
public sealed class ProcChild(ProcDetail p)
{
    public string Label { get; } = p.Label;
    public string PidText { get; } = $"PID {p.Pid}";
    public string MemText { get; } = Units.Megabytes(p.MemMB);
    public string CpuText { get; } = ProcRow.FormatCpu(p.Cpu);
}
