using System.Globalization;
using Rigsight.Core;
using Rigsight.Core.Reports;

namespace Rigsight.Models;

/// <summary>
/// One chip on the "At rest" card: its temperature idle and settled today, its usual range at rest, and the same for
/// each of the last days as a line (null on a day with too little rest to say).
/// </summary>
public sealed record RestRow(string Part, RestReading Reading)
{
    /// <summary>Its rest on each of the last days, the last one <see cref="LastDay"/>; null where there was too little.</summary>
    public IReadOnlyList<double?> Days { get; init; } = [];
    public DateTime LastDay { get; init; } = DateTime.Today;
    /// <summary>The line's colour (a theme brush's name): the part's own.</summary>
    public string Brush { get; init; } = "CpuBrush";

    public double? Today => Reading.Today;
    public string TodayText => Reading.Today is double t ? Units.TempShort(t) : "—";

    public string UsualText => Reading is { UsualLow: double lo, UsualHigh: double hi }
        ? Math.Round(Units.Temp(lo)) == Math.Round(Units.Temp(hi)) ? Lang.T($"usually {Units.TempShort(lo)}") : Lang.T($"usually {Math.Round(Units.Temp(lo)):0}–{Units.TempShort(hi)}")
        : "";

    /// <summary>Warmer at rest today than its usual range, by 3° or more: its AI button asks why (mock-up, 2026-10-26).</summary>
    public bool IsWarmer => Reading is { Today: double t, UsualHigh: double hi } && t - hi >= 3;
    public Services.AiQuestion? Ask => IsWarmer
        ? new(Lang.T($"My {Part} sits at {TodayText} at rest today, {UsualText}. Why is it warmer?"), Core.Ai.AiPages.Temperatures) : null;

    /// <summary>Two days or more to draw a line through.</summary>
    public bool HasLine => Days.Count(d => d is not null) >= 2;

    /// <summary>What the line says under the pointer, a text for each day ("12 Oct · 44°").</summary>
    public IReadOnlyList<string> Tips => [.. Days.Select((d, i) =>
        $"{LastDay.AddDays(i - (Days.Count - 1)).ToString("d MMM", CultureInfo.CurrentCulture)} · {(d is double t ? Units.TempShort(t) : Lang.T("No rest recorded"))}")];
}

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
