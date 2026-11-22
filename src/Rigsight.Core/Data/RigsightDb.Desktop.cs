namespace Rigsight.Core.Data;

/// <summary>
/// One minute of desktop hitches (a probe, 2026-11-22; nothing shows it yet): frames Windows' compositor (DWM) had ready
/// and still missed the screen's refresh with. <see cref="Glitches"/> leaves out the ones DWM says it caused on purpose
/// (<see cref="Induced"/>); <see cref="GlitchMs"/> and <see cref="GlitchMaxMs"/> are the late time it gave the
/// uninduced ones, in all and the longest (null when it gave none). <see cref="FrontApp"/> is the apps table's id of the
/// app in front most of the minute. <see cref="SmoothReports"/> is the compositor's own smoothness reports that minute,
/// as compact JSON (see DesktopTally in the agent), or null.
/// </summary>
public sealed record DesktopMinute(long Ts, int Glitches, int Induced, double? GlitchMs, double? GlitchMaxMs, int Recoveries, long? FrontApp, string? SmoothReports);

// Desktop hitches (probe, 2026-11-22): a row only for a minute with any compositor event, kept and cleared with the
// history. A table of its own, so nothing else changes.
public sealed partial class RigsightDb
{
    private void EnsureDesktopSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS desktop_minute(ts INTEGER PRIMARY KEY, glitches INTEGER NOT NULL, induced INTEGER NOT NULL,
                glitch_ms REAL, glitch_max_ms REAL, recoveries INTEGER NOT NULL, front_app INTEGER, smooth_reports TEXT);
            """);
    }

    // The app reads a database the agent made, which may be from before the probe (no table yet).
    private bool? _hasDesktop;
    private bool HasDesktop => _hasDesktop ??= HasTable("desktop_minute");

    /// <summary>One minute's hitches (replaces a row already there for the minute).</summary>
    public void WriteDesktopMinute(DesktopMinute m)
    {
        using var cmd = Cmd("""
            INSERT OR REPLACE INTO desktop_minute(ts, glitches, induced, glitch_ms, glitch_max_ms, recoveries, front_app, smooth_reports)
            VALUES($ts, $g, $i, $ms, $max, $r, $app, $s)
            """,
            ("$ts", m.Ts), ("$g", m.Glitches), ("$i", m.Induced), ("$ms", (object?)m.GlitchMs ?? DBNull.Value), ("$max", (object?)m.GlitchMaxMs ?? DBNull.Value),
            ("$r", m.Recoveries), ("$app", (object?)m.FrontApp ?? DBNull.Value), ("$s", (object?)m.SmoothReports ?? DBNull.Value));
        cmd.ExecuteNonQuery();
    }

    /// <summary>The minutes kept from <paramref name="from"/> to <paramref name="to"/> (Unix seconds), oldest first.</summary>
    public List<DesktopMinute> GetDesktopMinutes(long from, long to)
    {
        var list = new List<DesktopMinute>();
        if (!HasDesktop) return list;
        using var cmd = Cmd("SELECT ts, glitches, induced, glitch_ms, glitch_max_ms, recoveries, front_app, smooth_reports FROM desktop_minute WHERE ts >= $f AND ts < $t ORDER BY ts",
            ("$f", from), ("$t", to));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new DesktopMinute(r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetDouble(3), r.IsDBNull(4) ? null : r.GetDouble(4),
                r.GetInt32(5), r.IsDBNull(6) ? null : r.GetInt64(6), r.IsDBNull(7) ? null : r.GetString(7)));
        return list;
    }

    private void PruneDesktop(long before)
    {
        if (!HasDesktop) return;
        using var cmd = Cmd("DELETE FROM desktop_minute WHERE ts < $t", ("$t", before));
        cmd.ExecuteNonQuery();
    }

    private void ClearDesktop() => Exec("DELETE FROM desktop_minute;");
}
