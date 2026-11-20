namespace Rigsight.Core.Data;

// When each wireless device was last at 100% (2026-11-20): one row each time a device's battery is seen reaching full,
// so its low-battery alert and the taskbar card can say how long the charge has lasted.
public sealed partial class RigsightDb
{
    private void EnsureDeviceFullSchema() => Exec("""
        CREATE TABLE IF NOT EXISTS device_full(device TEXT NOT NULL, ts INTEGER NOT NULL, PRIMARY KEY(device, ts)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_device_full_ts ON device_full(ts);
        """);

    // The app reads a database an older agent made (no table yet): nothing saved then.
    private bool? _hasDeviceFull;
    private bool HasDeviceFull => _hasDeviceFull ??= HasTable("device_full");

    /// <summary>Notes that <paramref name="device"/> (its key) was seen at 100% at <paramref name="at"/>.</summary>
    public void AddDeviceFull(string device, DateTime at)
    {
        using var cmd = Cmd("INSERT OR IGNORE INTO device_full(device, ts) VALUES($d, $t)", ("$d", device), ("$t", TimeUtil.ToUnix(at)));
        cmd.ExecuteNonQuery();
        _hasDeviceFull = true;
    }

    /// <summary>When <paramref name="device"/> was last seen at 100%, or null if never.</summary>
    public DateTime? LastDeviceFull(string device)
    {
        if (!HasDeviceFull) return null;
        using var cmd = Cmd("SELECT max(ts) FROM device_full WHERE device = $d", ("$d", device));
        return cmd.ExecuteScalar() is long t ? TimeUtil.FromUnix(t) : null;
    }

    private void PruneDeviceFull(long before)
    {
        if (!HasDeviceFull) return;
        // The newest of each device stays, however old: a battery that lasts months still knows its last charge.
        using var cmd = Cmd("DELETE FROM device_full WHERE ts < $t AND ts < (SELECT max(ts) FROM device_full AS f WHERE f.device = device_full.device)", ("$t", before));
        cmd.ExecuteNonQuery();
    }
}
