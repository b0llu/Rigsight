using Rigsight.Core.Drivers;

namespace Rigsight.Core.Data;

// Driver activity (issue #15, 2026-11-22): how long each device's driver kept a CPU core to itself, a minute at a time
// for minutes worth keeping and an hour at a time for every driver (its usual), which part of the PC each driver is,
// and the minutes Windows dropped some of the readings (nothing is concluded from those). Each stutter also keeps the
// driver whose long bursts fell inside its frame. Tables of their own, so nothing else changes.
public sealed partial class RigsightDb
{
    private void EnsureDriverSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS driver_minute(ts INTEGER NOT NULL, driver TEXT NOT NULL, n INTEGER NOT NULL, total_us INTEGER NOT NULL,
                longest_us INTEGER NOT NULL, long_n INTEGER NOT NULL, PRIMARY KEY(ts, driver)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS driver_hour(ts INTEGER NOT NULL, driver TEXT NOT NULL, n INTEGER NOT NULL, total_us INTEGER NOT NULL,
                longest_us INTEGER NOT NULL, long_n INTEGER NOT NULL, PRIMARY KEY(ts, driver)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS driver_part(driver TEXT PRIMARY KEY, kind INTEGER NOT NULL, device TEXT, model TEXT, seen INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS driver_missed(ts INTEGER PRIMARY KEY);
            """);
        if (!HasColumn("stutter", "drv")) Exec("ALTER TABLE stutter ADD COLUMN drv TEXT");
        if (!HasColumn("stutter", "drv_us")) Exec("ALTER TABLE stutter ADD COLUMN drv_us INTEGER");
        // DPCs and interrupts apart, and calls by how long they took (the Latency page, issue #15): empty in older rows.
        foreach (var table in new[] { "driver_minute", "driver_hour" })
            foreach (var column in SplitColumns)
                if (!HasColumn(table, column)) Exec($"ALTER TABLE {table} ADD COLUMN {column} INTEGER");
        // The app wake-up check (issue #15): every minute it ran, and every hour.
        Exec("""
            CREATE TABLE IF NOT EXISTS wake_minute(ts INTEGER PRIMARY KEY, n INTEGER NOT NULL, median_us INTEGER NOT NULL, highest_us INTEGER NOT NULL,
                over_ms INTEGER NOT NULL, rate INTEGER NOT NULL) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS wake_hour(ts INTEGER PRIMARY KEY, n INTEGER NOT NULL, median_us INTEGER NOT NULL, highest_us INTEGER NOT NULL,
                over_ms INTEGER NOT NULL, rate INTEGER NOT NULL) WITHOUT ROWID;
            """);
    }

    /// <summary>The split's columns, in <see cref="DriverSplit"/>'s order, the six buckets last.</summary>
    private static readonly string[] SplitColumns = ["dpc_n", "dpc_us", "dpc_longest", "isr_n", "isr_us", "isr_longest", "b0", "b1", "b2", "b3", "b4", "b5"];
    private static readonly string SplitNames = ", " + string.Join(", ", SplitColumns);
    private static readonly string SplitValues = ", " + string.Join(", ", SplitColumns.Select(c => "$" + c));

    // Read: older databases (the app reading one the agent made before) may have no split columns yet.
    private bool? _hasSplit;
    private bool HasSplit => _hasSplit ??= HasDrivers && HasColumn("driver_hour", "b5");
    private string SplitRead => HasSplit ? SplitNames : "";

    private static void AddSplit(Microsoft.Data.Sqlite.SqliteCommand cmd, DriverSplit? s)
    {
        long?[] values = s is null ? new long?[SplitColumns.Length]
            : [s.DpcCount, s.DpcUs, s.DpcLongestUs, s.IsrCount, s.IsrUs, s.IsrLongestUs, .. s.Buckets.Select(b => (long?)b)];
        for (int i = 0; i < SplitColumns.Length; i++)
            cmd.Parameters.AddWithValue("$" + SplitColumns[i], i < values.Length && values[i] is long v ? v : DBNull.Value);
    }

    /// <summary>A row's tally, its split when it has one (columns 6 onwards when read with <see cref="SplitRead"/>).</summary>
    private DriverTally TallyOf(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        DriverSplit? split = null;
        if (r.FieldCount >= 6 + SplitColumns.Length && !r.IsDBNull(6))
        {
            long L(int i) => r.IsDBNull(6 + i) ? 0 : r.GetInt64(6 + i);
            split = new DriverSplit((int)L(0), L(1), (int)L(2), (int)L(3), L(4), (int)L(5), [L(6), L(7), L(8), L(9), L(10), L(11)]);
        }
        return new DriverTally(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt64(3), r.GetInt32(4), r.GetInt32(5), split);
    }

    // The app reads a database the agent made, which may be from before driver activity (no tables or columns yet).
    private bool? _hasDrivers;
    private bool HasDrivers => _hasDrivers ??= HasTable("driver_minute");
    private bool? _hasStutterDrivers;
    private bool HasStutterDrivers => _hasStutterDrivers ??= HasFrames && HasColumn("stutter", "drv");

    // ── Writer (the agent) ──

    /// <summary>
    /// One minute of driver tallies: only the drivers worth keeping (a burst of <see cref="DriverRules.KeepMinuteUs"/> or
    /// more, or <see cref="DriverRules.KeepTotalUs"/> of time in all), so a quiet minute stores nothing.
    /// </summary>
    public void WriteDriverMinute(long ts, IEnumerable<DriverTally> tallies)
    {
        foreach (var t in tallies)
        {
            if (t.LongestUs < DriverRules.KeepMinuteUs && t.TotalUs < DriverRules.KeepTotalUs) continue;
            using var cmd = Cmd($"INSERT OR REPLACE INTO driver_minute(ts, driver, n, total_us, longest_us, long_n{SplitNames}) VALUES($ts, $d, $n, $t, $l, $ln{SplitValues})",
                ("$ts", ts), ("$d", t.Driver), ("$n", t.Count), ("$t", t.TotalUs), ("$l", t.LongestUs), ("$ln", t.Long));
            AddSplit(cmd, t.Split);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>One hour's tallies, every driver seen in it (written again as the hour goes on: it replaces).</summary>
    public void WriteDriverHour(long ts, IEnumerable<DriverTally> tallies)
    {
        foreach (var t in tallies)
        {
            using var cmd = Cmd($"INSERT OR REPLACE INTO driver_hour(ts, driver, n, total_us, longest_us, long_n{SplitNames}) VALUES($ts, $d, $n, $t, $l, $ln{SplitValues})",
                ("$ts", ts), ("$d", t.Driver), ("$n", t.Count), ("$t", t.TotalUs), ("$l", t.LongestUs), ("$ln", t.Long));
            AddSplit(cmd, t.Split);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>A minute of the app wake-up check, and its hour so far (written again as the hour goes on: it replaces).</summary>
    public void WriteWake(WakeTally minute, WakeTally hour)
    {
        foreach (var (table, w) in new[] { ("wake_minute", minute), ("wake_hour", hour) })
        {
            using var cmd = Cmd($"INSERT OR REPLACE INTO {table}(ts, n, median_us, highest_us, over_ms, rate) VALUES($ts, $n, $m, $h, $o, $r)",
                ("$ts", w.Ts), ("$n", w.Checks), ("$m", w.MedianUs), ("$h", w.HighestUs), ("$o", w.OverMs), ("$r", w.Rate));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>A minute in which Windows dropped some of the driver readings.</summary>
    public void MarkDriverMissed(long ts)
    {
        using var cmd = Cmd("INSERT OR IGNORE INTO driver_missed(ts) VALUES($ts)", ("$ts", ts));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Which part of the PC a driver is, as found among the devices Windows has (replaces what was there).</summary>
    public void SaveDriverPart(DriverPart part, long seen)
    {
        using var cmd = Cmd("INSERT OR REPLACE INTO driver_part(driver, kind, device, model, seen) VALUES($d, $k, $dev, $m, $s)",
            ("$d", part.Driver), ("$k", (int)part.Kind), ("$dev", (object?)part.DeviceName ?? DBNull.Value), ("$m", (object?)part.Model ?? DBNull.Value), ("$s", seen));
        cmd.ExecuteNonQuery();
    }

    // ── Reader ──

    /// <summary>Every driver's part, by its file (lower case).</summary>
    public Dictionary<string, DriverPart> GetDriverParts()
    {
        var parts = new Dictionary<string, DriverPart>(StringComparer.OrdinalIgnoreCase);
        if (!HasDrivers) return parts;
        using var cmd = Cmd("SELECT driver, kind, device, model FROM driver_part");
        using var r = cmd.ExecuteReader();
        while (r.Read())
            parts[r.GetString(0)] = new DriverPart(r.GetString(0), (PartKind)r.GetInt32(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3));
        return parts;
    }

    /// <summary>The kept minutes from <paramref name="from"/> to <paramref name="to"/> (Unix seconds), of one driver or all.</summary>
    public List<DriverTally> GetDriverMinutes(long from, long to, string? driver = null)
    {
        var list = new List<DriverTally>();
        if (!HasDrivers) return list;
        using var cmd = driver is null
            ? Cmd($"SELECT ts, driver, n, total_us, longest_us, long_n{SplitRead} FROM driver_minute WHERE ts >= $f AND ts < $t ORDER BY ts", ("$f", from), ("$t", to))
            : Cmd($"SELECT ts, driver, n, total_us, longest_us, long_n{SplitRead} FROM driver_minute WHERE ts >= $f AND ts < $t AND driver = $d ORDER BY ts", ("$f", from), ("$t", to), ("$d", driver));
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(TallyOf(r));
        return list;
    }

    /// <summary>The minutes from <paramref name="from"/> to <paramref name="to"/> in which some driver readings were dropped.</summary>
    public HashSet<long> GetDriverMissed(long from, long to)
    {
        var set = new HashSet<long>();
        if (!HasDrivers) return set;
        using var cmd = Cmd("SELECT ts FROM driver_missed WHERE ts >= $f AND ts < $t", ("$f", from), ("$t", to));
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetInt64(0));
        return set;
    }

    /// <summary>
    /// A driver's usual longest burst before <paramref name="before"/> (Unix seconds): the middle of its hourly longest over
    /// the <see cref="DriverRules.UsualDays"/> days before, in µs. Null when its history doesn't reach that far back yet
    /// ("too soon to say", so nothing is said about it).
    /// </summary>
    public int? GetDriverUsualLongest(string driver, long before)
    {
        if (!HasDrivers) return null;
        long from = before - DriverRules.UsualDays * 86400L;
        using (var first = Cmd("SELECT min(ts) FROM driver_hour WHERE driver = $d", ("$d", driver)))
            if (first.ExecuteScalar() is not long oldest || oldest > from) return null;
        var values = new List<int>();
        using var cmd = Cmd("SELECT longest_us FROM driver_hour WHERE driver = $d AND ts >= $f AND ts < $t ORDER BY longest_us", ("$d", driver), ("$f", from), ("$t", before));
        using var r = cmd.ExecuteReader();
        while (r.Read()) values.Add(r.GetInt32(0));
        return values.Count == 0 ? null : values[values.Count / 2];
    }

    /// <summary>Every driver's kept minutes from <paramref name="from"/> to <paramref name="to"/> whose longest burst reached <paramref name="minUs"/>, oldest first.</summary>
    public List<DriverTally> GetDriverMinutesOver(long from, long to, int minUs)
    {
        var list = new List<DriverTally>();
        if (!HasDrivers) return list;
        using var cmd = Cmd("SELECT ts, driver, n, total_us, longest_us, long_n FROM driver_minute WHERE ts >= $f AND ts < $t AND longest_us >= $m ORDER BY ts",
            ("$f", from), ("$t", to), ("$m", minUs));
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new DriverTally(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt64(3), r.GetInt32(4), r.GetInt32(5)));
        return list;
    }

    /// <summary>The hours from <paramref name="from"/> to <paramref name="to"/> (Unix seconds), of one driver or all, oldest first.</summary>
    public List<DriverTally> GetDriverHours(long from, long to, string? driver = null)
    {
        var list = new List<DriverTally>();
        if (!HasDrivers) return list;
        using var cmd = driver is null
            ? Cmd($"SELECT ts, driver, n, total_us, longest_us, long_n{SplitRead} FROM driver_hour WHERE ts >= $f AND ts < $t ORDER BY ts", ("$f", from), ("$t", to))
            : Cmd($"SELECT ts, driver, n, total_us, longest_us, long_n{SplitRead} FROM driver_hour WHERE ts >= $f AND ts < $t AND driver = $d ORDER BY ts", ("$f", from), ("$t", to), ("$d", driver));
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(TallyOf(r));
        return list;
    }

    /// <summary>
    /// Each driver's kept minutes from <paramref name="from"/> to <paramref name="to"/> added up, only the minutes a game was in
    /// front (<paramref name="inGames"/> true) or only those it wasn't (false): the Latency page's In games and Outside games
    /// (issue #15). The minutes worth keeping hold every call of 0.5 ms or more, so the longest and the counts over 1 ms are
    /// whole; a driver's quiet minutes (under 5 ms in all) are left out of its total.
    /// </summary>
    public List<DriverTally> GetDriverTotalsByGames(long from, long to, IReadOnlyCollection<long> games, bool inGames)
    {
        var list = new List<DriverTally>();
        if (!HasDrivers) return list;
        using var cmd = Cmd($"""
            SELECT 0, d.driver, sum(d.n), sum(d.total_us), max(d.longest_us), sum(d.long_n)
            FROM driver_minute d LEFT JOIN system_minute s ON s.ts = d.ts
            WHERE d.ts >= $f AND d.ts < $t AND {GameWhere(games, inGames)} GROUP BY d.driver
            """, ("$f", from), ("$t", to));
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new DriverTally(0, r.GetString(1), r.GetInt32(2), r.GetInt64(3), r.GetInt32(4), r.GetInt32(5)));
        return list;
    }

    // The minutes a game was in front, or the others (the app's numbers come from the apps table, never from text).
    private static string GameWhere(IReadOnlyCollection<long> games, bool inGames)
    {
        string ids = games.Count == 0 ? "-1" : string.Join(",", games.Select(g => g.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return inGames ? $"s.fg_app IN ({ids})" : $"(s.fg_app IS NULL OR s.fg_app NOT IN ({ids}))";
    }

    /// <summary>
    /// Each hour's longest call (with whose) and calls over 1 ms from <paramref name="from"/> to <paramref name="to"/>, every
    /// driver together, from the kept minutes with a game in front or not (see <see cref="GetDriverTotalsByGames"/>).
    /// </summary>
    public List<(long Hour, int LongestUs, string Driver, int Long)> GetDriverHoursByGames(long from, long to, IReadOnlyCollection<long> games, bool inGames)
    {
        var list = new List<(long, int, string, int)>();
        if (!HasDrivers) return list;
        using var cmd = Cmd($"""
            SELECT d.ts / 3600 * 3600 AS h, d.driver, max(d.longest_us), sum(d.long_n)
            FROM driver_minute d LEFT JOIN system_minute s ON s.ts = d.ts
            WHERE d.ts >= $f AND d.ts < $t AND {GameWhere(games, inGames)} GROUP BY h, d.driver
            """, ("$f", from), ("$t", to));
        using var r = cmd.ExecuteReader();
        var byHour = new SortedDictionary<long, (int Us, string Driver, int Long)>();
        while (r.Read())
        {
            long h = r.GetInt64(0);
            int us = r.GetInt32(2), n = r.GetInt32(3);
            var was = byHour.TryGetValue(h, out var w) ? w : (Us: 0, Driver: "", Long: 0);
            byHour[h] = us > was.Us ? (us, r.GetString(1), was.Long + n) : (was.Us, was.Driver, was.Long + n);
        }
        foreach (var (h, v) in byHour) list.Add((h, v.Us, v.Driver, v.Long));
        return list;
    }

    /// <summary>The minute the longest call from <paramref name="from"/> to <paramref name="to"/> fell in (of one driver or any), how long it was and whose.</summary>
    public (long Minute, int Us, string Driver)? GetDriverLongestMinute(long from, long to, string? driver = null)
    {
        if (!HasDrivers) return null;
        using var cmd = driver is null
            ? Cmd("SELECT ts, longest_us, driver FROM driver_minute WHERE ts >= $f AND ts < $t ORDER BY longest_us DESC LIMIT 1", ("$f", from), ("$t", to))
            : Cmd("SELECT ts, longest_us, driver FROM driver_minute WHERE ts >= $f AND ts < $t AND driver = $d ORDER BY longest_us DESC LIMIT 1", ("$f", from), ("$t", to), ("$d", driver));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetInt32(1), r.GetString(2)) : null;
    }

    /// <summary>The first hour a driver's work was kept apart by kind (see <see cref="DriverSplit"/>), or null when none was yet.</summary>
    public long? FirstSplitHour()
    {
        if (!HasSplit) return null;
        using var cmd = Cmd("SELECT min(ts) FROM driver_hour WHERE b0 IS NOT NULL");
        return cmd.ExecuteScalar() is long ts ? ts : null;
    }

    // The wake-up check's tables (the app may read a database from before them).
    private bool? _hasWake;
    private bool HasWake => _hasWake ??= HasTable("wake_hour");

    /// <summary>The wake-up check's hours (or minutes) from <paramref name="from"/> to <paramref name="to"/>, oldest first.</summary>
    public List<WakeTally> GetWake(long from, long to, bool minutes = false)
    {
        var list = new List<WakeTally>();
        if (!HasWake) return list;
        using var cmd = Cmd($"SELECT ts, n, median_us, highest_us, over_ms, rate FROM {(minutes ? "wake_minute" : "wake_hour")} WHERE ts >= $f AND ts < $t ORDER BY ts", ("$f", from), ("$t", to));
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new WakeTally(r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5)));
        return list;
    }

    /// <summary>The first hour any driver activity was recorded (Unix seconds), or null when none ever was.</summary>
    public long? FirstDriverHour()
    {
        if (!HasDrivers) return null;
        using var cmd = Cmd("SELECT min(ts) FROM driver_hour");
        return cmd.ExecuteScalar() is long ts ? ts : null;
    }

    /// <summary>The first hour the app wake-up check was recorded (Unix seconds), or null when it never was.</summary>
    public long? FirstWakeHour()
    {
        if (!HasTable("wake_hour")) return null;
        using var cmd = Cmd("SELECT min(ts) FROM wake_hour");
        return cmd.ExecuteScalar() is long ts ? ts : null;
    }

    /// <summary>The app in front each minute from <paramref name="from"/> to <paramref name="to"/> (minutes with one only).</summary>
    public Dictionary<long, long> GetFrontApps(long from, long to)
    {
        var map = new Dictionary<long, long>();
        using var cmd = Cmd("SELECT ts, fg_app FROM system_minute WHERE ts >= $f AND ts < $t AND fg_app IS NOT NULL", ("$f", from), ("$t", to));
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetInt64(0)] = r.GetInt64(1);
        return map;
    }

    /// <summary>
    /// Each hour's app in front the most from <paramref name="from"/> to <paramref name="to"/>, with its minutes, by the
    /// hour's start as the driver hours have it (Unix seconds, on the hour).
    /// </summary>
    public Dictionary<long, (long App, int Minutes)> GetFrontHours(long from, long to)
    {
        var map = new Dictionary<long, (long App, int Minutes)>();
        using var cmd = Cmd("""
            SELECT ts / 3600 * 3600 AS h, fg_app, count(*) FROM system_minute
            WHERE ts >= $f AND ts < $t AND fg_app IS NOT NULL GROUP BY h, fg_app
            """, ("$f", from), ("$t", to));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            long h = r.GetInt64(0);
            int n = r.GetInt32(2);
            if (!map.TryGetValue(h, out var top) || n > top.Minutes) map[h] = (r.GetInt64(1), n);
        }
        return map;
    }

    private void PruneDrivers(long before)
    {
        if (!HasDrivers) return;
        using var cmd = Cmd("DELETE FROM driver_minute WHERE ts < $t; DELETE FROM driver_hour WHERE ts < $t; DELETE FROM driver_missed WHERE ts < $t", ("$t", before));
        cmd.ExecuteNonQuery();
        if (!HasWake) return;
        using var wake = Cmd("DELETE FROM wake_minute WHERE ts < $t; DELETE FROM wake_hour WHERE ts < $t", ("$t", before));
        wake.ExecuteNonQuery();
    }
}
