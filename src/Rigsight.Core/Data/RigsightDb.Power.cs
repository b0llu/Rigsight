namespace Rigsight.Core.Data;

/// <summary>A stretch of the Power chart (an hour, a local day or a local month): the CPU and GPU energy in it, in Wh.</summary>
/// <param name="CpuWh">Null when the CPU gave no power reading in it (never 0 for "not read").</param>
/// <param name="TopApp">The app in front for most of that energy; null when nothing was.</param>
public sealed record PowerBucket(long Start, double? CpuWh, double? GpuWh, int Minutes, long? TopApp)
{
    public double Wh => (CpuWh ?? 0) + (GpuWh ?? 0);
}

/// <summary>One app's time in front over a period and the CPU and GPU energy in that time (Wh): the PC's, not the app's own.</summary>
public sealed record PowerApp(long App, int Minutes, double Wh, bool IsGame);

/// <summary>How one part ran over a period (see <see cref="RigsightDb.GetPowerParts"/>); every figure null where nothing was read.</summary>
/// <param name="Average">Average watts over the minutes it reported power.</param>
/// <param name="InGames">Average watts in the minutes a game was in front.</param>
/// <param name="AtRest">Average watts at low activity (both the CPU and the GPU under 15% load, as LoadBands has it).</param>
/// <param name="Highest">The highest minute's average watts: the same peak Reports shows.</param>
/// <param name="Clock">Average clock (MHz); for the GPU, in games (its clock at rest says nothing).</param>
/// <param name="HeavyMinutes">Minutes at heavy load (CPU 50% or more, GPU 80% or more, as LoadBands has it).</param>
public sealed record PowerPart(double? Average, double? InGames, double? AtRest, double? Highest, double? Clock, int HeavyMinutes);

public sealed record PowerParts(PowerPart Cpu, PowerPart Gpu, int GameMinutes, double GameWh, int BatteryMinutes);

// The Power page (1.18.0): energy over a period, worked out from each minute's CPU and GPU power. Kept to a few grouped
// queries so a year reads in one pass over its minutes rather than as rows in memory.
public sealed partial class RigsightDb
{
    // The games as the app counts them (the person's own category changes too), inlined: ids only, a few dozen at most.
    private static string IsGame(IReadOnlyCollection<long> games) =>
        games.Count == 0 ? "0" : $"s.fg_app IN ({string.Join(",", games)})";

    /// <summary>
    /// The energy of [from, to) in stretches: by hour, by local day, or by local month. Stretches with no minute recorded
    /// are left out (the chart draws them as "Not recorded").
    /// </summary>
    public List<PowerBucket> GetPowerBuckets(long from, long to, PowerStep step)
    {
        string bucket = step switch
        {
            // The local hour: India's +5:30 puts UTC's hours at half past (the user's PC, 2026-11-23).
            PowerStep.Hour => "((s.ts + $off) / 3600) * 3600 - $off",
            PowerStep.Day => DayOf("s.ts"),
            _ => MonthOf("s.ts"),
        };
        // Per stretch and app in front, so each stretch can name the app most of its energy went with.
        using var cmd = Cmd($"""
            SELECT {bucket} AS b, s.fg_app, count(*), total(s.cpu_power), count(s.cpu_power), total(s.gpu_power), count(s.gpu_power)
            FROM system_minute s WHERE s.ts >= $from AND s.ts < $to GROUP BY b, s.fg_app ORDER BY b
            """, ("$from", from), ("$to", to), ("$off", (long)TimeZoneInfo.Local.GetUtcOffset(TimeUtil.FromUnix(from)).TotalSeconds));
        using var r = cmd.ExecuteReader();
        var list = new List<PowerBucket>();
        long? current = null;
        double cpu = 0, gpu = 0, topWh = -1;
        long cpuN = 0, gpuN = 0;
        int minutes = 0;
        long? top = null;
        void Flush()
        {
            if (current is long b) list.Add(new PowerBucket(b, cpuN > 0 ? cpu / 60 : null, gpuN > 0 ? gpu / 60 : null, minutes, top));
        }
        while (r.Read())
        {
            long b = r.GetInt64(0);
            if (b != current)
            {
                Flush();
                (current, cpu, gpu, cpuN, gpuN, minutes, top, topWh) = (b, 0, 0, 0, 0, 0, null, -1);
            }
            long? app = r.IsDBNull(1) ? null : r.GetInt64(1);
            double c = r.GetDouble(3), g = r.GetDouble(5);
            minutes += r.GetInt32(2);
            cpu += c; cpuN += r.GetInt64(4);
            gpu += g; gpuN += r.GetInt64(6);
            if (app is not null && c + g > topWh) (top, topWh) = (app, c + g);
        }
        Flush();
        return list;
    }

    /// <summary>Each app's minutes in front over [from, to) and the CPU and GPU energy (Wh) in those minutes, the most first.</summary>
    public List<PowerApp> GetPowerApps(long from, long to, IReadOnlyCollection<long> games)
    {
        using var cmd = Cmd($"""
            SELECT s.fg_app, count(*), total(s.cpu_power) + total(s.gpu_power), max({IsGame(games)})
            FROM system_minute s WHERE s.ts >= $from AND s.ts < $to AND s.fg_app IS NOT NULL
            GROUP BY s.fg_app ORDER BY 3 DESC
            """, ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        var list = new List<PowerApp>();
        while (r.Read())
            list.Add(new PowerApp(r.GetInt64(0), r.GetInt32(1), r.GetDouble(2) / 60, !r.IsDBNull(3) && r.GetInt64(3) == 1));
        return list;
    }

    /// <summary>How the CPU and the GPU ran over [from, to): averages in games and at rest, the highest minute, clocks, heavy load.</summary>
    public PowerParts GetPowerParts(long from, long to, IReadOnlyCollection<long> games)
    {
        string IsGameSql = IsGame(games);
        using var cmd = Cmd($"""
            SELECT avg(s.cpu_power), avg(CASE WHEN {IsGameSql} THEN s.cpu_power END), avg(CASE WHEN {IdleOn("s")} THEN s.cpu_power END),
                   max(s.cpu_power), {(HasClocks ? "avg(s.cpu_clock)" : "NULL")}, count(CASE WHEN s.{Reports.LoadBands.CpuHeavySql} THEN 1 END),
                   avg(s.gpu_power), avg(CASE WHEN {IsGameSql} THEN s.gpu_power END), avg(CASE WHEN {IdleOn("s")} THEN s.gpu_power END),
                   max(s.gpu_power), {(HasClocks ? $"avg(CASE WHEN {IsGameSql} THEN s.gpu_clock END)" : "NULL")}, count(CASE WHEN s.{Reports.LoadBands.GpuHeavySql} THEN 1 END),
                   count(CASE WHEN {IsGameSql} THEN 1 END), total(CASE WHEN {IsGameSql} THEN coalesce(s.cpu_power, 0) + coalesce(s.gpu_power, 0) END),
                   {(HasDrives ? "count(CASE WHEN s.on_battery = 1 THEN 1 END)" : "0")}
            FROM system_minute s WHERE s.ts >= $from AND s.ts < $to
            """, ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        r.Read();
        PowerPart Part(int i) => new(D(r, i), D(r, i + 1), D(r, i + 2), D(r, i + 3), D(r, i + 4), r.GetInt32(i + 5));
        return new PowerParts(Part(0), Part(6), r.GetInt32(12), r.GetDouble(13) / 60, r.GetInt32(14));
    }

    // LoadBands.IdleSql with the table's alias.
    private static string IdleOn(string alias) => $"{alias}.cpu_load < {Reports.LoadBands.IdleMaxLoad} AND {alias}.gpu_load < {Reports.LoadBands.IdleMaxLoad}";

    /// <summary>The CPU and GPU energy (Wh) over [from, to) and how many of those minutes a game was in front.</summary>
    public (double? Wh, int GameMinutes) GetPowerTotal(long from, long to, IReadOnlyCollection<long> games)
    {
        using var cmd = Cmd($"""
            SELECT total(s.cpu_power) + total(s.gpu_power), count(s.cpu_power) + count(s.gpu_power), count(CASE WHEN {IsGame(games)} THEN 1 END)
            FROM system_minute s WHERE s.ts >= $from AND s.ts < $to
            """, ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt64(1) > 0 ? r.GetDouble(0) / 60 : null, r.GetInt32(2));
    }

    /// <summary>
    /// The CPU and GPU energy (Wh) over [from, to) in the minutes <paramref name="app"/> was in front or busy (it did 5%
    /// or more of the CPU or the GPU in that minute, as app_minute keeps it): the Processes page's energy line. Other
    /// apps busy in the same minutes count too.
    /// </summary>
    public double? GetPowerWhileBusy(long app, long from, long to)
    {
        if (!_hasAppMinutes && !(_hasAppMinutes = HasTable("app_minute"))) return null;
        using var cmd = Cmd("""
            SELECT total(s.cpu_power) + total(s.gpu_power), count(s.cpu_power) + count(s.gpu_power) FROM system_minute s
            WHERE s.ts >= $from AND s.ts < $to AND (s.fg_app = $app OR EXISTS(
                SELECT 1 FROM app_minute m WHERE m.ts = s.ts AND m.app = $app AND (m.cpu >= 5 OR m.gpu >= 5)))
            """, ("$app", app), ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        r.Read();
        return r.GetInt64(1) > 0 ? r.GetDouble(0) / 60 : null;
    }

    /// <summary>
    /// A game's GPU power by frame rate over [from, to): for each band of 10 FPS, the minutes in it and their average GPU
    /// watts. Only minutes with both a frame rate and a GPU power reading.
    /// </summary>
    public Dictionary<int, (int Minutes, double Watts)> GetGamePowerByFps(long app, long from, long to)
    {
        var map = new Dictionary<int, (int, double)>();
        if (!HasFps) return map;
        using var cmd = Cmd("""
            SELECT CAST(fps / 10 AS INTEGER), count(*), avg(gpu_power) FROM system_minute
            WHERE fg_app = $app AND ts >= $from AND ts < $to AND fps > 0 AND gpu_power IS NOT NULL GROUP BY 1
            """, ("$app", app), ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetInt32(0)] = (r.GetInt32(1), r.GetDouble(2));
        return map;
    }
}

/// <summary>How the Power chart cuts a period: by hour (a day), by local day (a week or a month), by local month (a year).</summary>
public enum PowerStep { Hour, Day, Month }
