using Rigsight.Core;

namespace Rigsight.Models;

/// <summary>
/// An hour of the Temperatures page's week: the CPU's and the GPU's average over it (what its box is coloured by), the
/// highest each read in it (said under the pointer), and the app in front most of it.
/// </summary>
public sealed record HeatHour(DateTime Hour, double? Cpu, double? Gpu, string? App)
{
    public double? CpuHigh { get; init; }
    public double? GpuHigh { get; init; }
}

/// <summary>A tile of the Temperatures page's "Motherboard" (in its chip's group, <see cref="BoardChip"/>): a board sensor, with the steps its colour goes by.</summary>
public sealed record PartTemp(string Name, SensorItem Sensor, string Steps);
