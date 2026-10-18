using System.Data;

namespace Rigsight.Core.Data;

/// <summary>An app's time over all its sessions, for the Games library (see <see cref="RigsightDb.GetSessionTotals"/>).</summary>
public sealed record AppTotal(long AppId, int Sessions, double ActiveSec, long First, long Last);

/// <summary>
/// What the minutes say about a stretch of time with one app in front: temperatures, how hard the graphics card
/// worked, power, its memory and the frame rate (see <see cref="RigsightDb.GetPlayStats"/>).
/// </summary>
public sealed record PlayStats
{
    public int Minutes { get; init; }
    public double? Gpu { get; init; }
    public double? GpuMax { get; init; }
    public double? Cpu { get; init; }
    public double? CpuMax { get; init; }
    public double? HotMax { get; init; }
    public double? GpuLoad { get; init; }
    public double? GpuPower { get; init; }
    /// <summary>The CPU's and the graphics card's power together.</summary>
    public double? Power { get; init; }
    /// <summary>Minutes with the graphics card at 95% or more: it set the pace.</summary>
    public int GpuFullMinutes { get; init; }
    public double? VramMax { get; init; }
    public double? VramTotal { get; init; }
    public double? Fps { get; init; }
    public double? FpsLow { get; init; }
    public int FpsMinutes { get; init; }
}

public sealed partial class RigsightDb
{
    private bool? _hasFps;
    private bool HasFps => _hasFps ??= HasColumn("system_minute", "fps");

    /// <summary>Every app's sessions added up since <paramref name="from"/>: how many, the time in front, the first and last.</summary>
    public List<AppTotal> GetSessionTotals(long from = 0)
    {
        using var cmd = Cmd("""
            SELECT app_id, COUNT(*), SUM(active_sec), min(start), max(end) FROM sessions INDEXED BY ix_sessions_start
            WHERE start >= $from GROUP BY app_id
            """, ("$from", from));
        using var r = cmd.ExecuteReader();
        var list = new List<AppTotal>();
        while (r.Read()) list.Add(new AppTotal(r.GetInt64(0), r.GetInt32(1), r.GetDouble(2), r.GetInt64(3), r.GetInt64(4)));
        return list;
    }

    /// <summary>All of one app's sessions, oldest first.</summary>
    public List<SessionRow> GetAppSessions(long appId)
    {
        using var cmd = Cmd("""
            SELECT id, app_id, start, end, active_sec, cpu_temp_max, gpu_temp_max, is_game FROM sessions
            WHERE app_id = $app AND start >= 0 ORDER BY start
            """, ("$app", appId));
        return ReadSessions(cmd);
    }

    /// <summary>The minutes from <paramref name="from"/> to <paramref name="to"/> with <paramref name="appId"/> in front, summed up.</summary>
    public PlayStats GetPlayStats(long appId, long from, long to)
    {
        string vram = HasVram ? "max(vram_used), max(vram_total)" : "NULL, NULL";
        string fps = HasFps ? "AVG(fps), AVG(fps_low), COUNT(fps)" : "NULL, NULL, 0";
        using var cmd = Cmd($"""
            SELECT COUNT(*), AVG(gpu_temp), max(gpu_temp_max), AVG(cpu_temp), max(cpu_temp_max), max(gpu_hot_max), AVG(gpu_load),
                AVG(gpu_power), AVG(CASE WHEN cpu_power IS NULL AND gpu_power IS NULL THEN NULL ELSE coalesce(cpu_power, 0) + coalesce(gpu_power, 0) END),
                SUM(CASE WHEN gpu_load >= 95 THEN 1 ELSE 0 END), {vram}, {fps}
            FROM system_minute WHERE ts >= $from AND ts < $to AND fg_app = $app
            """, ("$from", from), ("$to", to), ("$app", appId));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return new PlayStats();
        return new PlayStats
        {
            Minutes = r.GetInt32(0), Gpu = D(r, 1), GpuMax = D(r, 2), Cpu = D(r, 3), CpuMax = D(r, 4), HotMax = D(r, 5),
            GpuLoad = D(r, 6), GpuPower = D(r, 7), Power = D(r, 8), GpuFullMinutes = r.IsDBNull(9) ? 0 : r.GetInt32(9),
            VramMax = D(r, 10), VramTotal = D(r, 11), Fps = D(r, 12), FpsLow = D(r, 13), FpsMinutes = r.IsDBNull(14) ? 0 : r.GetInt32(14),
        };
    }

    /// <summary>
    /// How busy each app kept the graphics card while in front, on average over all its months: the Games library's
    /// "Hardest on your PC" order, read from the monthly totals so it costs nothing with years of history.
    /// </summary>
    public Dictionary<long, double> GetAppGpuLoads()
    {
        var map = new Dictionary<long, double>();
        if (!HasAppMonth()) return map;
        using var cmd = Cmd("""
            SELECT app_id, SUM(gpu_load_sum) / SUM(gpu_load_n) FROM app_month
            WHERE month >= 0 GROUP BY app_id HAVING SUM(gpu_load_n) > 0
            """);
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetInt64(0)] = r.GetDouble(1);
        return map;
    }

    /// <summary>An app's time in front, in seconds, from <paramref name="from"/> to <paramref name="to"/> (by its hours).</summary>
    public double GetAppFrontSec(long appId, long from, long to)
    {
        using var cmd = Cmd("SELECT SUM(fg_sec) FROM app_hour WHERE ts >= $from AND ts < $to AND app_id = $app", ("$from", from), ("$to", to), ("$app", appId));
        return cmd.ExecuteScalar() is double v ? v : 0;
    }
}
