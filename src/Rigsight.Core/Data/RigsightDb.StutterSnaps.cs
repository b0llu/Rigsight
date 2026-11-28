using Microsoft.Data.Sqlite;
using Rigsight.Core.Games;

namespace Rigsight.Core.Data;

// What the PC was doing in the seconds around each stutter (1.23.0, issue #18, see StutterSnaps): a few numbers on the
// stutter's own row, empty in rows from before and where nothing was read near it.
public sealed partial class RigsightDb
{
    /// <summary>The snapshot's columns, in <see cref="SnapValues"/>' order.</summary>
    private static readonly string[] SnapColumns = ["s_gpu", "s_cpu", "s_gpu_t", "s_vram", "s_vram_t", "s_commit", "s_disk", "s_disk_own", "s_app", "s_app_cpu"];

    private void EnsureStutterSnapSchema()
    {
        foreach (var column in SnapColumns)
            if (!HasColumn("stutter", column))
                Exec($"ALTER TABLE stutter ADD COLUMN {column} {(column is "s_disk_own" or "s_app" ? "INTEGER" : "REAL")}");
    }

    // The app reads a database the agent made, which may be from before the snapshots.
    private bool? _hasStutterSnaps;
    private bool HasStutterSnaps => _hasStutterSnaps ??= HasFrames && HasColumn("stutter", "s_app_cpu");

    private long? _firstStutterSnap;

    /// <summary>When the first stutter with a snapshot was (Unix ms), or null with none yet: plays before it were recorded without them.</summary>
    public long? FirstStutterSnap()
    {
        if (_firstStutterSnap is not null || !HasStutterSnaps) return _firstStutterSnap;
        // The table's own order is by time, so this stops at the first one found.
        using var cmd = Cmd("SELECT ts_ms FROM stutter WHERE s_cpu IS NOT NULL OR s_gpu IS NOT NULL OR s_disk IS NOT NULL OR s_vram IS NOT NULL OR s_app IS NOT NULL ORDER BY ts_ms LIMIT 1");
        return _firstStutterSnap = cmd.ExecuteScalar() is long v ? v : null;
    }

    private static object?[] SnapValues(StutterSnap s) =>
    [
        SnapRound(s.Gpu, 0), SnapRound(s.Cpu, 0), SnapRound(s.GpuTemp, 1), SnapRound(s.VramMb, 0), SnapRound(s.VramTotalMb, 0), SnapRound(s.Commit, 0), SnapRound(s.Disk, 0),
        s.Disk is null ? null : s.DiskIsGame ? 1L : 0L, s.OtherApp, s.OtherApp is null ? null : SnapRound(s.OtherCpu, 1),
    ];

    private static object? SnapRound(double? v, int digits) => v is double d && double.IsFinite(d) ? Math.Round(d, digits) : null;

    private static void AddSnap(SqliteCommand cmd, StutterSnap s)
    {
        var values = SnapValues(s);
        for (int i = 0; i < SnapColumns.Length; i++) cmd.Parameters.AddWithValue("$" + SnapColumns[i], values[i] ?? DBNull.Value);
    }

    /// <summary>A row's snapshot from <paramref name="at"/> on (the columns in <see cref="SnapColumns"/>' order), or null when it has none.</summary>
    private static StutterSnap? SnapOf(SqliteDataReader r, int at)
    {
        double? D(int i) => r.IsDBNull(at + i) ? null : r.GetDouble(at + i);
        bool any = false;
        for (int i = 0; i < SnapColumns.Length; i++) any |= !r.IsDBNull(at + i);
        if (!any) return null;
        return new StutterSnap(D(0), D(1), D(2), D(3), D(4), D(5), D(6), !r.IsDBNull(at + 7) && r.GetInt64(at + 7) == 1,
            r.IsDBNull(at + 8) ? null : r.GetInt64(at + 8), D(9));
    }
}
