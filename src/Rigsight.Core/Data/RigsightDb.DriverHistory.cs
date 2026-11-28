using Rigsight.Core.Stability;

namespace Rigsight.Core.Data;

// Every device's driver and each version it has had (1.22.0, "Drivers inside Timeline"): a row a version of a device's
// driver, from when it came (Windows' log or the agent's daily look) and when it was last seen. See DriverRecord for the
// rules and DriverHistory for what the pages read.
public sealed partial class RigsightDb
{
    /// <summary>When the agent first looked at every device's driver (Unix seconds): the history starts there and in Windows' log before it.</summary>
    public const string DriverLookFirstKey = "driver_look_first";

    /// <summary>When the agent last looked at every device's driver (Unix seconds).</summary>
    public const string DriverLookLastKey = "driver_look_last";

    private void EnsureDriverHistorySchema() => Exec("""
        CREATE TABLE IF NOT EXISTS driver_history(key TEXT NOT NULL, since INTEGER NOT NULL, version TEXT NOT NULL, shown TEXT NOT NULL,
            name TEXT NOT NULL, class TEXT NOT NULL, maker TEXT NOT NULL, inbox INTEGER NOT NULL, date INTEGER, seen INTEGER NOT NULL, how INTEGER NOT NULL,
            PRIMARY KEY(key, since)) WITHOUT ROWID;
        """);

    // The app reads a database an older agent made (no table yet): no history then.
    private bool? _hasDriverHistory;
    private bool HasDriverHistory => _hasDriverHistory ??= HasTable("driver_history");

    /// <summary>Writes rows of the driver history (new versions, and versions seen again), and keeps each device to its newest few.</summary>
    public void PutDriverRows(IReadOnlyCollection<DriverRow> rows)
    {
        if (rows.Count == 0) return;
        using var tx = BeginTransaction();
        foreach (var r in rows)
        {
            using var cmd = Cmd("""
                INSERT OR REPLACE INTO driver_history(key, since, version, shown, name, class, maker, inbox, date, seen, how)
                VALUES($k, $s, $v, $sh, $n, $c, $m, $i, $d, $seen, $h)
                """,
                ("$k", r.Key), ("$s", TimeUtil.ToUnix(r.Since)), ("$v", r.Version), ("$sh", r.Shown), ("$n", r.Name), ("$c", r.ClassGuid), ("$m", r.Maker),
                ("$i", r.Inbox ? 1 : 0), ("$d", r.Date is { } d ? TimeUtil.ToUnix(d) : null), ("$seen", TimeUtil.ToUnix(r.Seen)), ("$h", (int)r.How));
            cmd.ExecuteNonQuery();
        }
        foreach (var key in rows.Select(r => r.Key).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var trim = Cmd("""
                DELETE FROM driver_history WHERE key = $k AND since NOT IN (SELECT since FROM driver_history WHERE key = $k ORDER BY since DESC LIMIT $most)
                """, ("$k", key), ("$most", DriverRecord.MostVersions));
            trim.ExecuteNonQuery();
        }
        tx.Commit();
        _hasDriverHistory = true;
    }

    /// <summary>Every row of the driver history, oldest first by device.</summary>
    public List<DriverRow> GetDriverRows()
    {
        if (!HasDriverHistory) return [];
        using var cmd = Cmd("SELECT key, since, version, shown, name, class, maker, inbox, date, seen, how FROM driver_history ORDER BY key, since");
        using var r = cmd.ExecuteReader();
        var list = new List<DriverRow>();
        while (r.Read())
            list.Add(new DriverRow(r.GetString(0), TimeUtil.FromUnix(r.GetInt64(1)), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
                r.GetInt64(7) != 0, r.IsDBNull(8) ? null : TimeUtil.FromUnix(r.GetInt64(8)), TimeUtil.FromUnix(r.GetInt64(9)),
                Enum.IsDefined(typeof(DriverStart), (int)r.GetInt64(10)) ? (DriverStart)(int)r.GetInt64(10) : DriverStart.Unknown));
        return list;
    }

    /// <summary>The history as the pages read it (empty before the agent's first look), with the graphics card's versions from <paramref name="changes"/>.</summary>
    public DriverHistory GetDriverHistory(DateTime now, IEnumerable<SystemChange>? changes = null) => DriverHistory.Build(GetDriverRows(), now, changes);

    /// <summary>
    /// The shared cutoff, except that each device keeps the version it's on, and the version before a kept change (so a
    /// change still reads "from → to"): a version goes once the one after it started before the cutoff too. A device last
    /// seen before the cutoff goes whole.
    /// </summary>
    private void PruneDriverHistory(long before)
    {
        if (!HasDriverHistory) return;
        using var old = Cmd("""
            DELETE FROM driver_history WHERE (SELECT min(n.since) FROM driver_history AS n WHERE n.key = driver_history.key AND n.since > driver_history.since) < $t
            """, ("$t", before));
        old.ExecuteNonQuery();
        using var gone = Cmd("DELETE FROM driver_history WHERE key IN (SELECT key FROM driver_history GROUP BY key HAVING max(seen) < $t)", ("$t", before));
        gone.ExecuteNonQuery();
    }
}
