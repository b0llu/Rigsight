using Rigsight.Core.Protocol;
using Rigsight.Core.Storage;

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
        // The counts Every reading sets against a month ago (cable errors, 187, 188, NVMe media errors, spare blocks,
        // the NVMe warning flag, sudden shut-offs): added to the table, null on the days before.
        foreach (var column in CountColumns)
            if (!HasColumn("drive_health_day", column)) Exec($"ALTER TABLE drive_health_day ADD COLUMN {column} INTEGER");
    }

    private static readonly string[] CountColumns = ["crc", "uncorrectable_reported", "timeouts", "media_errors", "spare", "warning", "shut_offs"];

    // An older agent's table has no count columns yet (the app reads what the agent made).
    private bool? _hasDriveCounts;
    private bool HasDriveCounts => _hasDriveCounts ??= HasColumn("drive_health_day", "shut_offs");

    // The app reads a database the agent made, which may be from before this table.
    private bool? _hasDriveHealth;
    private bool HasDriveHealth => _hasDriveHealth ??= HasTable("drive_health_day");

    /// <summary>
    /// Today's row for a drive, from its latest reading: the counters as they are now, the day's highest temperature,
    /// and its temperature added to the day's average. Called each time the drives are read (every 5 minutes, or 10
    /// seconds while the app is open); a drive with no lasting key is left out.
    /// </summary>
    /// <param name="countTemp">
    /// False keeps the temperature out of the day's average (it still counts toward the highest): the drives are read 30
    /// times as often while the app is open, so the caller counts one reading per 5 minutes (see Storage.DriveTempPace)
    /// and the average isn't mostly the hours the window was open.
    /// </param>
    public void UpsertDriveHealth(long day, DriveHealthInfo d, bool countTemp = true)
    {
        if (string.IsNullOrEmpty(d.Key)) return;
        double? t = d.Temperature;
        double? counted = countTemp ? t : null;
        using var cmd = Cmd("""
            INSERT INTO drive_health_day(day, drive, name, life, hours, power_ons, read_gb, written_gb, temp_max, temp_sum, temp_n,
                reallocated, pending, uncorrectable, status, crc, uncorrectable_reported, timeouts, media_errors, spare, warning, shut_offs)
            VALUES($day, $drive, $name, $life, $hours, $ons, $read, $written, $t, coalesce($ct, 0), $tn, $re, $pe, $un, $status,
                $crc, $ur, $to, $me, $sp, $wa, $so)
            ON CONFLICT(day, drive) DO UPDATE SET name = $name,
                life = coalesce($life, life), hours = coalesce($hours, hours), power_ons = coalesce($ons, power_ons),
                read_gb = coalesce($read, read_gb), written_gb = coalesce($written, written_gb),
                temp_max = CASE WHEN $t IS NULL THEN temp_max WHEN temp_max IS NULL OR $t > temp_max THEN $t ELSE temp_max END,
                temp_sum = temp_sum + coalesce($ct, 0), temp_n = temp_n + $tn,
                reallocated = coalesce($re, reallocated), pending = coalesce($pe, pending), uncorrectable = coalesce($un, uncorrectable),
                status = $status, crc = coalesce($crc, crc), uncorrectable_reported = coalesce($ur, uncorrectable_reported),
                timeouts = coalesce($to, timeouts), media_errors = coalesce($me, media_errors), spare = coalesce($sp, spare),
                warning = coalesce($wa, warning), shut_offs = coalesce($so, shut_offs)
            """,
            ("$day", day), ("$drive", d.Key), ("$name", d.Name), ("$life", d.Life), ("$hours", d.PowerOnHours), ("$ons", d.PowerOnCount),
            ("$read", d.ReadGb), ("$written", d.WrittenGb), ("$t", t), ("$ct", counted), ("$tn", counted is null ? 0 : 1),
            ("$re", d.ReallocatedSectors), ("$pe", d.PendingSectors), ("$un", d.UncorrectableSectors), ("$status", d.Status),
            ("$crc", DriveCounts.CableErrors(d)), ("$ur", DriveCounts.ReportedUncorrectable(d)), ("$to", DriveCounts.Timeouts(d)),
            ("$me", DriveCounts.MediaErrors(d)), ("$sp", DriveCounts.Spare(d)), ("$wa", DriveCounts.Warning(d)), ("$so", DriveCounts.ShutOffs(d)));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every drive's daily rows from a day on (all drives, or one by its key), oldest first.</summary>
    public List<DriveHealthDay> GetDriveHealthDays(long from, string? drive = null)
    {
        var list = new List<DriveHealthDay>();
        if (!HasDriveHealth) return list;
        using var cmd = Cmd($"""
            SELECT day, drive, name, life, hours, power_ons, read_gb, written_gb, temp_max, temp_sum, temp_n, reallocated, pending, uncorrectable, status,
                {(HasDriveCounts ? "crc, uncorrectable_reported, timeouts, media_errors, spare, warning, shut_offs" : "NULL, NULL, NULL, NULL, NULL, NULL, NULL")}
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
                CableErrors = L(r, 15), ReportedUncorrectable = L(r, 16), Timeouts = L(r, 17), MediaErrors = L(r, 18),
                Spare = L(r, 19), Warning = L(r, 20), ShutOffs = L(r, 21),
            });
        }
        return list;
    }

    private static long? L(System.Data.IDataRecord r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    private void PruneDriveHealth(long before)
    {
        if (!HasDriveHealth) return;
        using var cmd = Cmd("DELETE FROM drive_health_day WHERE day < $t", ("$t", before));
        cmd.ExecuteNonQuery();
    }
}
