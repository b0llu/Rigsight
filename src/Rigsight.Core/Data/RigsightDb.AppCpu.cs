namespace Rigsight.Core.Data;

/// <summary>
/// One app's CPU and GPU time in an hour (app_cpu_hour) or a local day (app_cpu_day), written as deltas a minute at a
/// time and summed by the database. <see cref="Ts"/> is the hour, the day (local midnight) or 0 in a total.
/// <list type="bullet">
/// <item><see cref="CpuMs"/>: CPU time of all its processes, every core counted (one core busy for a second is 1000).</item>
/// <item><see cref="GpuMs"/>: time its work ran on the main GPU, its busiest engine's.</item>
/// <item><see cref="FgCpuMs"/> and <see cref="FgSec"/>: the CPU time it used while in front and in use, and how long that was.</item>
/// <item><see cref="RunSec"/> and <see cref="MemMbSec"/> (days only): how long it ran, and its memory (private working set, MB)
/// times the seconds it held it, so <c>MemMbSec / RunSec</c> is its average memory while it ran.</item>
/// </list>
/// App <see cref="AllApps"/> is every process together (Windows' "System" too, never the idle time): CPU time only.
/// </summary>
public sealed record AppCpuUse(long Ts, long App, long CpuMs, long GpuMs = 0, long FgCpuMs = 0, long FgSec = 0, long RunSec = 0, long MemMbSec = 0)
{
    /// <summary>The app key of every process together (no app has it: the apps table counts from 1).</summary>
    public const long AllApps = 0;

    /// <summary>Its average memory while it ran (MB); null when it wasn't seen running.</summary>
    public double? MemMbAvg => RunSec > 0 ? (double)MemMbSec / RunSec : null;
}

// Each app's CPU and GPU time: per hour and per day, in tables of their own. History starts with the version that added
// them: the averages app_hour kept can't be turned into time.
public sealed partial class RigsightDb
{
    private void EnsureAppCpuSchema() => Exec("""
        CREATE TABLE IF NOT EXISTS app_cpu_hour(ts INTEGER NOT NULL, app INTEGER NOT NULL, cpu_ms INTEGER NOT NULL, gpu_ms INTEGER NOT NULL,
            fg_cpu_ms INTEGER NOT NULL DEFAULT 0, fg_sec INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(ts, app)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS app_cpu_day(day INTEGER NOT NULL, app INTEGER NOT NULL, cpu_ms INTEGER NOT NULL, gpu_ms INTEGER NOT NULL,
            fg_cpu_ms INTEGER NOT NULL DEFAULT 0, fg_sec INTEGER NOT NULL DEFAULT 0, run_sec INTEGER NOT NULL DEFAULT 0,
            mem_mbs INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(day, app)) WITHOUT ROWID;
        """);

    // The app reads a database an older agent made (no tables yet): nothing recorded then.
    private bool? _hasAppCpu;
    private bool HasAppCpu => _hasAppCpu ??= HasTable("app_cpu_day");

    // ── Writer ────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a minute of one app's use into its hour (<see cref="AppCpuUse.Ts"/>) and its day (sums add up). The hour only
    /// gets a row where it used the CPU or the GPU; the day gets one for every app that ran, so a day without its row is a
    /// day it didn't run.
    /// </summary>
    public void AddAppCpuUse(AppCpuUse u)
    {
        if (u.CpuMs > 0 || u.GpuMs > 0 || u.FgCpuMs > 0 || u.FgSec > 0)
        {
            using var hour = Cmd("""
                INSERT INTO app_cpu_hour(ts, app, cpu_ms, gpu_ms, fg_cpu_ms, fg_sec) VALUES($ts, $app, $cpu, $gpu, $fgc, $fgs)
                ON CONFLICT(ts, app) DO UPDATE SET cpu_ms = cpu_ms + excluded.cpu_ms, gpu_ms = gpu_ms + excluded.gpu_ms,
                    fg_cpu_ms = fg_cpu_ms + excluded.fg_cpu_ms, fg_sec = fg_sec + excluded.fg_sec
                """, ("$ts", u.Ts), ("$app", u.App), ("$cpu", u.CpuMs), ("$gpu", u.GpuMs), ("$fgc", u.FgCpuMs), ("$fgs", u.FgSec));
            hour.ExecuteNonQuery();
        }
        using var day = Cmd($"""
            INSERT INTO app_cpu_day(day, app, cpu_ms, gpu_ms, fg_cpu_ms, fg_sec, run_sec, mem_mbs) VALUES({DayOf("$ts")}, $app, $cpu, $gpu, $fgc, $fgs, $run, $mem)
            ON CONFLICT(day, app) DO UPDATE SET cpu_ms = cpu_ms + excluded.cpu_ms, gpu_ms = gpu_ms + excluded.gpu_ms,
                fg_cpu_ms = fg_cpu_ms + excluded.fg_cpu_ms, fg_sec = fg_sec + excluded.fg_sec, run_sec = run_sec + excluded.run_sec,
                mem_mbs = mem_mbs + excluded.mem_mbs
            """, ("$ts", u.Ts), ("$app", u.App), ("$cpu", u.CpuMs), ("$gpu", u.GpuMs), ("$fgc", u.FgCpuMs), ("$fgs", u.FgSec),
            ("$run", u.RunSec), ("$mem", u.MemMbSec));
        day.ExecuteNonQuery();
        _hasAppCpu = true;
    }

    private void PruneAppCpu(long before)
    {
        if (!HasAppCpu) return;
        // As the drives' app days: the day the cutoff falls in keeps its whole day.
        using var c = Cmd($"""
            DELETE FROM app_cpu_hour WHERE ts < $t;
            DELETE FROM app_cpu_day WHERE day < {DayOf("$t")};
            """, ("$t", before));
        c.ExecuteNonQuery();
    }

    // ── Reader ────────────────────────────────────────────────────────────

    /// <summary>One app's hours from <paramref name="from"/> to <paramref name="to"/> (only those it used the CPU or the GPU in).</summary>
    public List<AppCpuUse> GetAppCpuHours(long app, long from, long to) => ReadAppCpu(
        "SELECT ts, app, cpu_ms, gpu_ms, fg_cpu_ms, fg_sec, 0, 0 FROM app_cpu_hour WHERE app = $app AND ts >= $from AND ts < $to ORDER BY ts",
        ("$app", app), ("$from", from), ("$to", to));

    /// <summary>One app's days from <paramref name="from"/> to <paramref name="to"/> (local midnights); <see cref="AppCpuUse.AllApps"/> for every process together.</summary>
    public List<AppCpuUse> GetAppCpuDays(long app, long from, long to) => ReadAppCpu(
        "SELECT day, app, cpu_ms, gpu_ms, fg_cpu_ms, fg_sec, run_sec, mem_mbs FROM app_cpu_day WHERE app = $app AND day >= $from AND day < $to ORDER BY day",
        ("$app", app), ("$from", from), ("$to", to));

    /// <summary>Every app's row of one day (its local midnight), the total of every process among them.</summary>
    public List<AppCpuUse> GetAppCpuDay(long day) => ReadAppCpu(
        "SELECT day, app, cpu_ms, gpu_ms, fg_cpu_ms, fg_sec, run_sec, mem_mbs FROM app_cpu_day WHERE day = $day", ("$day", day));

    /// <summary>
    /// Each app's use from <paramref name="from"/> to <paramref name="to"/>, added up (Ts 0): from the days (local
    /// midnights) by default, or from the hours with <paramref name="byHour"/> for a stretch that doesn't start at midnight
    /// (the hours have no running time or memory).
    /// </summary>
    public List<AppCpuUse> GetAppCpuTotals(long from, long to, bool byHour = false) => byHour
        ? ReadAppCpu("""
            SELECT 0, app, sum(cpu_ms), sum(gpu_ms), sum(fg_cpu_ms), sum(fg_sec), 0, 0 FROM app_cpu_hour
            WHERE ts >= $from AND ts < $to GROUP BY app
            """, ("$from", from), ("$to", to))
        : ReadAppCpu("""
            SELECT 0, app, sum(cpu_ms), sum(gpu_ms), sum(fg_cpu_ms), sum(fg_sec), sum(run_sec), sum(mem_mbs) FROM app_cpu_day
            WHERE day >= $from AND day < $to GROUP BY app
            """, ("$from", from), ("$to", to));

    /// <summary>Each app's CPU time (ms) on each day from <paramref name="from"/> to <paramref name="to"/>, by (local midnight, app).</summary>
    public Dictionary<(long Day, long App), long> GetAppCpuTotalsByDay(long from, long to)
    {
        var map = new Dictionary<(long, long), long>();
        foreach (var d in ReadAppCpu(
            "SELECT day, app, cpu_ms, 0, 0, 0, 0, 0 FROM app_cpu_day WHERE day >= $from AND day < $to AND app <> 0", ("$from", from), ("$to", to)))
            map[(d.Ts, d.App)] = d.CpuMs;
        return map;
    }

    /// <summary>The first day with CPU time on record (local midnight); null before any.</summary>
    public long? FirstAppCpuDay()
    {
        if (!HasAppCpu) return null;
        using var cmd = Cmd("SELECT min(day) FROM app_cpu_day");
        return cmd.ExecuteScalar() is long day ? day : null;
    }

    private List<AppCpuUse> ReadAppCpu(string sql, params (string Name, object? Value)[] args)
    {
        var list = new List<AppCpuUse>();
        if (!HasAppCpu) return list;
        using var cmd = Cmd(sql, args);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new AppCpuUse(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7)));
        return list;
    }
}
