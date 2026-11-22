using Rigsight.Core.Sound;

namespace Rigsight.Core.Data;

// Sound dropouts (2026-11-22): each glitch Windows' audio engine logged, heard ones (the sound ran out of data on its way
// out) and late ones (something in the chain was late, kept as supporting evidence), with the app that was playing, the
// output it played on and the driver whose long bursts fell just before it. A table of its own, so nothing else changes.
public sealed partial class RigsightDb
{
    private void EnsureSoundSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS sound_glitch(ts_ms INTEGER NOT NULL, kind INTEGER NOT NULL, heard INTEGER NOT NULL, app INTEGER,
                device TEXT, frames INTEGER, drv TEXT, drv_us INTEGER, PRIMARY KEY(ts_ms, kind)) WITHOUT ROWID;
            """);
    }

    // The app reads a database the agent made, which may be from before sound dropouts (no table yet).
    private bool? _hasSound;
    private bool HasSound => _hasSound ??= HasTable("sound_glitch");

    // ── Writer (the agent) ──

    /// <summary>Glitches as the agent tagged them; one already kept for that millisecond and kind stays as it was.</summary>
    public void WriteSoundGlitches(IEnumerable<SoundGlitch> glitches)
    {
        using var tx = BeginTransaction();
        foreach (var g in glitches)
        {
            using var cmd = Cmd("INSERT OR IGNORE INTO sound_glitch(ts_ms, kind, heard, app, device, frames, drv, drv_us) VALUES($ts, $k, $h, $a, $dev, $f, $d, $du)",
                ("$ts", g.AtMs), ("$k", g.Kind), ("$h", g.Heard ? 1 : 0), ("$a", g.App), ("$dev", g.Device), ("$f", g.Frames),
                ("$d", g.Drv), ("$du", g.Drv is null ? null : g.DrvUs));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ── Reader ──

    /// <summary>The glitches from <paramref name="fromMs"/> to <paramref name="toMs"/> (Unix ms, end excluded), in time order; only heard ones when <paramref name="heardOnly"/>.</summary>
    public List<SoundGlitch> GetSoundGlitches(long fromMs, long toMs, bool heardOnly)
    {
        var list = new List<SoundGlitch>();
        if (!HasSound) return list;
        using var cmd = Cmd("SELECT ts_ms, kind, heard, app, device, frames, drv, drv_us FROM sound_glitch WHERE ts_ms >= $f AND ts_ms < $t"
            + (heardOnly ? " AND heard = 1" : "") + " ORDER BY ts_ms, kind", ("$f", fromMs), ("$t", toMs));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new SoundGlitch(r.GetInt64(0), r.GetInt32(1), r.GetInt64(2) != 0, r.IsDBNull(3) ? null : r.GetInt64(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetInt64(5), r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? 0 : r.GetInt32(7)));
        return list;
    }

    /// <summary>How many times the sound cut out (cuts, see SoundDropouts.Cuts) while each app was the one playing, from <paramref name="fromMs"/> to <paramref name="toMs"/> (Unix ms).</summary>
    public Dictionary<long, int> GetSoundDropoutsByApp(long fromMs, long toMs) =>
        SoundDropouts.Cuts(GetSoundGlitches(fromMs, toMs, heardOnly: true)).Where(c => c.App is not null)
            .GroupBy(c => c.App!.Value).ToDictionary(g => g.Key, g => g.Count());

    private void PruneSound(long before)
    {
        if (!HasSound) return;
        using var cmd = Cmd("DELETE FROM sound_glitch WHERE ts_ms < $t * 1000", ("$t", before));
        cmd.ExecuteNonQuery();
    }

    private void ClearSound()
    {
        if (HasSound) Exec("DELETE FROM sound_glitch;");
    }
}
