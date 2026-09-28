namespace Rigsight.Core.Reports;

/// <summary>
/// What counts as idle and as heavy load, for comparing temperatures like for like (an idle morning isn't "cooler
/// than usual" just because nothing ran). One place for it: the reports read minutes by these bands, and the
/// database adds the daily totals up by the same ones (<see cref="IdleSql"/> and the load ones are SQL over a
/// system_minute row).
/// </summary>
public static class LoadBands
{
    /// <summary>Both the CPU and the GPU under this much load: idle.</summary>
    public const double IdleMaxLoad = 15;

    /// <summary>The GPU at or over this: heavy GPU load (a game, a render).</summary>
    public const double GpuHeavyLoad = 80;

    /// <summary>The CPU at or over this: heavy CPU load (a compile, a game with a CPU-bound engine).</summary>
    public const double CpuHeavyLoad = 50;

    public const string IdleSql = "cpu_load < 15 AND gpu_load < 15";
    public const string GpuHeavySql = "gpu_load >= 80";
    public const string CpuHeavySql = "cpu_load >= 50";
}
