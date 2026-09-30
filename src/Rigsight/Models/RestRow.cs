using Rigsight.Core;
using Rigsight.Core.Reports;

namespace Rigsight.Models;

/// <summary>One chip on the "At rest" card: its temperature idle and settled today, and its usual range at rest.</summary>
public sealed record RestRow(string Part, RestReading Reading)
{
    public double? Today => Reading.Today;
    public string TodayText => Reading.Today is double t ? Units.TempShort(t) : "—";

    public string UsualText => Reading is { UsualLow: double lo, UsualHigh: double hi }
        ? Math.Round(Units.Temp(lo)) == Math.Round(Units.Temp(hi)) ? $"usually {Units.TempShort(lo)}" : $"usually {Math.Round(Units.Temp(lo)):0}–{Units.TempShort(hi)}"
        : "";
}
