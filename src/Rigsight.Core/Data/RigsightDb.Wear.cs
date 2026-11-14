namespace Rigsight.Core.Data;

// What the wear tiles read (wear and drive details, 2026-11-14): a chip's power through its loaded minutes, to find the
// load it usually works at, and its temperature at that load, week by week.
public sealed partial class RigsightDb
{
    /// <summary>
    /// A chip's power (W) on every fifth recorded minute from <paramref name="from"/> to <paramref name="to"/> where it was
    /// read: enough to find where its working load sits without reading every minute.
    /// </summary>
    public List<double> SamplePower(bool gpu, long from, long to)
    {
        string power = gpu ? "gpu_power" : "cpu_power";
        using var cmd = Cmd($"SELECT {power} FROM system_minute WHERE ts >= $from AND ts < $to AND {power} IS NOT NULL AND (ts / 60) % 5 = 0",
            ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        var list = new List<double>();
        while (r.Read()) list.Add(r.GetDouble(0));
        return list;
    }

    /// <summary>
    /// A chip's average temperature and minutes, week by week (weeks of 7 days counted from <paramref name="origin"/>),
    /// over the minutes its power was between <paramref name="low"/> and <paramref name="high"/> W: the same load each week.
    /// </summary>
    public List<(int Week, int Bin, double Temp, int Minutes)> TempAtPowerByWeek(bool gpu, double low, double high, long origin, long to, double binWatts)
    {
        string power = gpu ? "gpu_power" : "cpu_power", temp = gpu ? "gpu_temp" : "cpu_temp";
        using var cmd = Cmd($"""
            SELECT (ts - $origin) / 604800, CAST(({power} - $lo) / $bin AS INTEGER), avg({temp}), count(*) FROM system_minute
            WHERE ts >= $origin AND ts < $to AND {temp} IS NOT NULL AND {power} BETWEEN $lo AND $hi
            GROUP BY 1, 2 ORDER BY 1, 2
            """, ("$origin", origin), ("$to", to), ("$lo", low), ("$hi", high), ("$bin", binWatts));
        using var r = cmd.ExecuteReader();
        var list = new List<(int, int, double, int)>();
        while (r.Read()) list.Add(((int)r.GetInt64(0), (int)r.GetInt64(1), r.GetDouble(2), (int)r.GetInt64(3)));
        return list;
    }
}
