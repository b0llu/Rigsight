using Rigsight.Core.Protocol;

namespace Rigsight.Core.Data;

// Each physical drive's health, a row a day (wear and drive details, 2026-11-14): its life left, hours, what it has read
// and written in its life, its bad sectors and its temperature, so a drive can be set against its own earlier days
// ("69% life left, about 8% used a year"). Kept under the drive's lasting key (DriveKey), not its letter or number.
public sealed partial class RigsightDb
{
    private void EnsureDriveHealthSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS drive_health_day(day INTEGER NOT NULL, drive TEXT NOT NULL, name TEXT NOT NULL,
                life REAL, hours INTEGER, power_ons INTEGER, read_gb REAL, written_gb REAL,
                temp_max REAL, temp_sum REAL NOT NULL DEFAULT 0, temp_n INTEGER NOT NULL DEFAULT 0,
                reallocated INTEGER, pending INTEGER, uncorrectable INTEGER, status TEXT NOT NULL,
                PRIMARY KEY(day, drive)) WITHOUT ROWID;
            """);
    }

    // The app reads a database the agent made, which may be from before this table.
    private bool? _hasDriveHealth;
    private bool HasDriveHealth => _hasDriveHealth ??= HasTable("drive_health_day");

    /// <summary>
    /// Today's row for a drive, from its latest reading: the counters as they are now, the day's highest temperature,
    /// and its temperature added to the day's average. Called each time the drives are read (every 5 minutes, or 10
    /// seconds while the app is open); a drive with no lasting key is left out.
    /// </summary>
    public void UpsertDriveHealth(long day, DriveHealthInfo d)
    {
        if (string.IsNullOrEmpty(d.Key)) return;
        double? t = d.Temperature;
        using var cmd = Cmd("""
            INSERT INTO drive_health_day(day, drive, name, life, hours, power_ons, read_gb, written_gb, temp_max, temp_sum, temp_n,
                reallocated, pending, uncorrectable, status)
            VALUES($day, $drive, $name, $life, $hours, $ons, $read, $written, $t, coalesce($t, 0), $tn, $re, $pe, $un, $status)
            ON CONFLICT(day, drive) DO UPDATE SET name = $name,
                life = coalesce($life, life), hours = coalesce($hours, hours), power_ons = coalesce($ons, power_ons),
                read_gb = coalesce($read, read_gb), written_gb = coalesce($written, written_gb),
                temp_max = CASE WHEN $t IS NULL THEN temp_max WHEN temp_max IS NULL OR $t > temp_max THEN $t ELSE temp_max END,
                temp_sum = temp_sum + coalesce($t, 0), temp_n = temp_n + $tn,
                reallocated = coalesce($re, reallocated), pending = coalesce($pe, pending), uncorrectable = coalesce($un, uncorrectable),
                status = $status
            """,
            ("$day", day), ("$drive", d.Key), ("$name", d.Name), ("$life", d.Life), ("$hours", d.PowerOnHours), ("$ons", d.PowerOnCount),
            ("$read", d.ReadGb), ("$written", d.WrittenGb), ("$t", t), ("$tn", t is null ? 0 : 1),
            ("$re", d.ReallocatedSectors), ("$pe", d.PendingSectors), ("$un", d.UncorrectableSectors), ("$status", d.Status));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every drive's daily rows from a day on (all drives, or one by its key), oldest first.</summary>
    public List<DriveHealthDay> GetDriveHealthDays(long from, string? drive = null)
    {
        var list = new List<DriveHealthDay>();
        if (!HasDriveHealth) return list;
        using var cmd = Cmd($"""
            SELECT day, drive, name, life, hours, power_ons, read_gb, written_gb, temp_max, temp_sum, temp_n, reallocated, pending, uncorrectable, status
            FROM drive_health_day WHERE day >= $from {(drive is null ? "" : "AND drive = $drive")} ORDER BY day
            """, ("$from", from), ("$drive", drive ?? ""));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            long n = r.GetInt64(10);
            list.Add(new DriveHealthDay
            {
                Day = r.GetInt64(0), Drive = r.GetString(1), Name = r.GetString(2), Life = D(r, 3),
                Hours = r.IsDBNull(4) ? null : r.GetInt64(4), PowerOns = r.IsDBNull(5) ? null : r.GetInt64(5),
                ReadGb = D(r, 6), WrittenGb = D(r, 7), TempMax = D(r, 8), TempAvg = n > 0 ? r.GetDouble(9) / n : null,
                Reallocated = r.IsDBNull(11) ? null : r.GetInt64(11), Pending = r.IsDBNull(12) ? null : r.GetInt64(12),
                Uncorrectable = r.IsDBNull(13) ? null : r.GetInt64(13), Status = r.GetString(14),
            });
        }
        return list;
    }

    private void PruneDriveHealth(long before)
    {
        if (!HasDriveHealth) return;
        using var cmd = Cmd("DELETE FROM drive_health_day WHERE day < $t", ("$t", before));
        cmd.ExecuteNonQuery();
    }
}
