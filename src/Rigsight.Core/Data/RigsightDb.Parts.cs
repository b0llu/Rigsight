namespace Rigsight.Core.Data;

/// <summary>
/// One metric of a part over some minutes: the sum and count of its readings (the average) and its highest (for a
/// temperature, the minutes' own highest readings; for load, power and clock, the highest minute).
/// </summary>
public readonly record struct PartSum(double Sum, int N, double? Max)
{
    public double? Average => N > 0 ? Sum / N : null;
    public static PartSum operator +(PartSum a, PartSum b) =>
        new(a.Sum + b.Sum, a.N + b.N, a.Max is double x ? b.Max is double y ? Math.Max(x, y) : x : b.Max);
}

/// <summary>
/// A stretch of a part's chart (an hour or a local day), the minutes with a game in front or the others: how many there
/// were and each metric's sums.
/// </summary>
public sealed record PartBucket(long Start, bool Game, int Minutes, PartSum Temp, PartSum Load, PartSum Power, PartSum Clock);

/// <summary>An app in front over a period: its minutes and the part's average load, temperature and power in them.</summary>
public sealed record PartAppRow(long App, int Minutes, double? Load, double? Temp, double? Power, bool IsGame);

/// <summary>A game's minutes in front over some time, with each of the part's metrics' sums in them.</summary>
public sealed record PartAppSums(long App, int Minutes, PartSum Temp, PartSum Load, PartSum Power, PartSum Clock);

// The GPU and CPU pages (1.23.0): each hour or day of one part from system_minute, with a game in front kept apart from
// everything else, in two grouped queries so a year reads in one pass over its minutes.
public sealed partial class RigsightDb
{
    private static string PartColumn(bool gpu, string name) => (gpu ? "s.gpu_" : "s.cpu_") + name;

    // The stretch a minute belongs to: its local hour (as the Power chart's, India's +5:30 included) or its local day.
    private static string PartStretch(bool hourly) => hourly ? "((s.ts + $off) / 3600) * 3600 - $off" : DayOf("s.ts");

    private static long LocalOffset(long from) => (long)TimeZoneInfo.Local.GetUtcOffset(TimeUtil.FromUnix(from)).TotalSeconds;

    /// <summary>
    /// The part's figures over [from, to) by local hour (<paramref name="hourly"/>) or local day, each stretch split into
    /// the minutes with a game in front and the rest. Stretches with no minute are left out.
    /// </summary>
    public List<PartBucket> GetPartBuckets(long from, long to, bool gpu, bool hourly, IReadOnlyCollection<long> games)
    {
        string C(string n) => PartColumn(gpu, n);
        string clock = HasClocks ? $"total({C("clock")}), count({C("clock")}), max({C("clock")})" : "0, 0, NULL";
        using var cmd = Cmd($"""
            SELECT {PartStretch(hourly)} AS b, {IsGame(games)} AS g, count(*),
                   total({C("temp")}), count({C("temp")}), max(coalesce({C("temp_max")}, {C("temp")})),
                   total({C("load")}), count({C("load")}), max({C("load")}),
                   total({C("power")}), count({C("power")}), max({C("power")}),
                   {clock}
            FROM system_minute s WHERE s.ts >= $from AND s.ts < $to GROUP BY b, g ORDER BY b
            """, ("$from", from), ("$to", to), ("$off", LocalOffset(from)));
        using var r = cmd.ExecuteReader();
        var list = new List<PartBucket>();
        PartSum Sum(int i) => new(r.GetDouble(i), r.GetInt32(i + 1), D(r, i + 2));
        while (r.Read())
            list.Add(new PartBucket(r.GetInt64(0), !r.IsDBNull(1) && r.GetInt64(1) == 1, r.GetInt32(2), Sum(3), Sum(6), Sum(9), Sum(12)));
        return list;
    }

    /// <summary>Each game's minutes in front in each stretch (local hour or day) of [from, to).</summary>
    public List<(long Start, long App, int Minutes)> GetPartGameMinutes(long from, long to, bool hourly, IReadOnlyCollection<long> games)
    {
        if (games.Count == 0) return [];
        using var cmd = Cmd($"""
            SELECT {PartStretch(hourly)} AS b, s.fg_app, count(*) FROM system_minute s
            WHERE s.ts >= $from AND s.ts < $to AND {IsGame(games)} GROUP BY b, s.fg_app
            """, ("$from", from), ("$to", to), ("$off", LocalOffset(from)));
        using var r = cmd.ExecuteReader();
        var list = new List<(long, long, int)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetInt64(1), r.GetInt32(2)));
        return list;
    }

    /// <summary>
    /// Each game's minutes in front over [from, to) with the part's sums in them (the GPU and CPU pages' marks, 1.23.0: a
    /// driver update's or a new part's days before and since).
    /// </summary>
    public List<PartAppSums> GetPartGameSums(long from, long to, bool gpu, IReadOnlyCollection<long> games)
    {
        if (games.Count == 0 || to <= from) return [];
        string C(string n) => PartColumn(gpu, n);
        string clock = HasClocks ? $"total({C("clock")}), count({C("clock")}), max({C("clock")})" : "0, 0, NULL";
        using var cmd = Cmd($"""
            SELECT s.fg_app, count(*),
                   total({C("temp")}), count({C("temp")}), max(coalesce({C("temp_max")}, {C("temp")})),
                   total({C("load")}), count({C("load")}), max({C("load")}),
                   total({C("power")}), count({C("power")}), max({C("power")}),
                   {clock}
            FROM system_minute s WHERE s.ts >= $from AND s.ts < $to AND {IsGame(games)} GROUP BY s.fg_app
            """, ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        var list = new List<PartAppSums>();
        PartSum Sum(int i) => new(r.GetDouble(i), r.GetInt32(i + 1), D(r, i + 2));
        while (r.Read()) list.Add(new PartAppSums(r.GetInt64(0), r.GetInt32(1), Sum(2), Sum(5), Sum(8), Sum(11)));
        return list;
    }

    /// <summary>Each app in front over [from, to) with the part's average load, temperature and power in its minutes there.</summary>
    public List<PartAppRow> GetPartApps(long from, long to, bool gpu, IReadOnlyCollection<long> games)
    {
        string C(string n) => PartColumn(gpu, n);
        using var cmd = Cmd($"""
            SELECT s.fg_app, count(*), avg({C("load")}), avg({C("temp")}), avg({C("power")}), max({IsGame(games)})
            FROM system_minute s WHERE s.ts >= $from AND s.ts < $to AND s.fg_app IS NOT NULL GROUP BY s.fg_app
            """, ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        var list = new List<PartAppRow>();
        while (r.Read())
            list.Add(new PartAppRow(r.GetInt64(0), r.GetInt32(1), D(r, 2), D(r, 3), D(r, 4), !r.IsDBNull(5) && r.GetInt64(5) == 1));
        return list;
    }
}
