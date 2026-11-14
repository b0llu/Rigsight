namespace Rigsight.Core.Data;

/// <summary>
/// What one app read from and wrote to one physical drive in an hour (disk_app_hour) or a local day (disk_app_day), in
/// bytes: written as deltas and summed by the database. <see cref="Disk"/> is the drive's key (Storage.DriveKey), not
/// its number in Windows, which can change between starts; null in a total over every drive. <see cref="Ts"/> is the
/// hour, the day (local midnight) or 0 in a total.
/// </summary>
public sealed record DiskAppUse(long Ts, long App, string? Disk, long Read, long Write)
{
    public long Total => Read + Write;
}

// What each app reads and writes on each physical drive (2026-11-14, from Windows' disk events): per hour and per day, in
// tables of their own. The system process's row is what Windows' file cache read and wrote for the apps.
public sealed partial class RigsightDb
{
    private void EnsureDiskSchema() => Exec("""
        CREATE TABLE IF NOT EXISTS disk_app_hour(ts INTEGER NOT NULL, app INTEGER NOT NULL, disk TEXT NOT NULL, read INTEGER NOT NULL,
            write INTEGER NOT NULL, PRIMARY KEY(ts, app, disk)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS disk_app_day(day INTEGER NOT NULL, app INTEGER NOT NULL, disk TEXT NOT NULL, read INTEGER NOT NULL,
            write INTEGER NOT NULL, PRIMARY KEY(day, app, disk)) WITHOUT ROWID;
        """);

    // The app reads a database an older agent made (no tables yet): nothing recorded then.
    private bool? _hasDiskUse;
    private bool HasDiskUse => _hasDiskUse ??= HasTable("disk_app_day");

    // ── Writer ────────────────────────────────────────────────────────────

    /// <summary>Adds one app's reads and writes on one drive into its hour (<see cref="DiskAppUse.Ts"/>) and its day (sums add up).</summary>
    public void AddDiskAppUse(DiskAppUse u)
    {
        if (u.Disk is null) throw new ArgumentException("A drive's use is written for one drive", nameof(u));
        foreach (var (table, key, value) in new[] { ("disk_app_hour", "ts", "$ts"), ("disk_app_day", "day", DayOf("$ts")) })
        {
            using var cmd = Cmd($"""
                INSERT INTO {table}({key}, app, disk, read, write) VALUES({value}, $app, $disk, $r, $w)
                ON CONFLICT({key}, app, disk) DO UPDATE SET read = read + excluded.read, write = write + excluded.write
                """, ("$ts", u.Ts), ("$app", u.App), ("$disk", u.Disk), ("$r", u.Read), ("$w", u.Write));
            cmd.ExecuteNonQuery();
        }
        _hasDiskUse = true;
    }

    private void PruneDisk(long before)
    {
        if (!HasDiskUse) return;
        // As the network's app days: the day the cutoff falls in keeps its whole day.
        using var c = Cmd($"""
            DELETE FROM disk_app_hour WHERE ts < $t;
            DELETE FROM disk_app_day WHERE day < {DayOf("$t")};
            """, ("$t", before));
        c.ExecuteNonQuery();
    }

    // ── Reader ────────────────────────────────────────────────────────────

    /// <summary>Each app's use of each drive per hour, for the hours from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public List<DiskAppUse> GetDiskAppHours(long from, long to) =>
        ReadDiskUse("SELECT ts, app, disk, read, write FROM disk_app_hour WHERE ts >= $from AND ts < $to ORDER BY ts", from, to, null);

    /// <summary>Each app's use of each drive per local day, for the days from <paramref name="from"/> to <paramref name="to"/> (local midnights).</summary>
    public List<DiskAppUse> GetDiskAppDays(long from, long to) =>
        ReadDiskUse("SELECT day, app, disk, read, write FROM disk_app_day WHERE day >= $from AND day < $to ORDER BY day", from, to, null);

    /// <summary>
    /// What each app read and wrote from <paramref name="from"/> to <paramref name="to"/>, added up (Ts 0), most first
    /// (reads and writes together): on one drive (<paramref name="disk"/>, its key) or on every drive together (null:
    /// Disk is null in each row). From the days (local midnights) by default; <paramref name="byHour"/> adds up the
    /// hours instead, for a stretch that doesn't start at midnight (the last 24 hours). At most <paramref name="top"/> apps.
    /// </summary>
    public List<DiskAppUse> GetDiskAppTotals(long from, long to, string? disk = null, bool byHour = false, int top = int.MaxValue)
    {
        var (table, key) = byHour ? ("disk_app_hour", "ts") : ("disk_app_day", "day");
        string where = disk is null ? "" : " AND disk = $disk";
        return ReadDiskUse($"""
            SELECT 0, app, {(disk is null ? "NULL" : "disk")}, sum(read), sum(write) FROM {table}
            WHERE {key} >= $from AND {key} < $to{where} GROUP BY app ORDER BY sum(read) + sum(write) DESC, app LIMIT $top
            """, from, to, disk, top);
    }

    /// <summary>Each drive's reads and writes from <paramref name="from"/> to <paramref name="to"/> (local midnights), every app together (App 0), by key.</summary>
    public List<DiskAppUse> GetDiskTotals(long from, long to) =>
        ReadDiskUse("SELECT 0, 0, disk, sum(read), sum(write) FROM disk_app_day WHERE day >= $from AND day < $to GROUP BY disk ORDER BY disk", from, to, null);

    private List<DiskAppUse> ReadDiskUse(string sql, long from, long to, string? disk, int top = int.MaxValue)
    {
        var list = new List<DiskAppUse>();
        if (!HasDiskUse) return list;
        using var cmd = Cmd(sql, ("$from", from), ("$to", to), ("$disk", disk), ("$top", top));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new DiskAppUse(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(3), r.GetInt64(4)));
        return list;
    }
}
