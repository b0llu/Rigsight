using Rigsight.Core.Games;

namespace Rigsight.Core.Data;

// What Steam downloaded (2026-10-30): each download from Steam's own log, saved as the agent finds it, so a download stays
// named after Steam's log has moved past it (Steam keeps two files and deletes the oldest lines) and after its game is
// uninstalled (its name is kept from when it was installed).
public sealed partial class RigsightDb
{
    private void EnsureSteamDownloadSchema() => Exec("""
        CREATE TABLE IF NOT EXISTS steam_download(app TEXT NOT NULL, started INTEGER NOT NULL, ended INTEGER NOT NULL,
            bytes INTEGER NOT NULL, new_install INTEGER NOT NULL, how INTEGER NOT NULL, name TEXT, PRIMARY KEY(app, started)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_steam_download_ended ON steam_download(ended);
        """);

    // The app reads a database an older agent made (no table yet): nothing saved then.
    private bool? _hasSteamDownloads;
    private bool HasSteamDownloads => _hasSteamDownloads ??= HasTable("steam_download");

    /// <summary>
    /// Saves Steam's downloads: a new one is added, one already saved takes its later end, size and how it ended. A name
    /// is never lost: one Steam no longer gives (the game was uninstalled) keeps the one saved before.
    /// </summary>
    public void SaveSteamDownloads(IEnumerable<GameDownload> downloads)
    {
        // Through the usual commands: the agent saves inside its minute's transaction, which they take part in.
        foreach (var d in downloads)
        {
            using var cmd = Cmd("""
                INSERT INTO steam_download(app, started, ended, bytes, new_install, how, name) VALUES($app, $started, $ended, $bytes, $new, $how, $name)
                ON CONFLICT(app, started) DO UPDATE SET ended = excluded.ended, bytes = excluded.bytes, how = excluded.how,
                    new_install = excluded.new_install, name = coalesce(excluded.name, steam_download.name)
                """, ("$app", d.AppId), ("$started", TimeUtil.ToUnix(d.Started)), ("$ended", TimeUtil.ToUnix(d.Ended)), ("$bytes", d.Bytes),
                ("$new", d.NewInstall ? 1 : 0), ("$how", (int)d.How), ("$name", d.Name));
            cmd.ExecuteNonQuery();
        }
        _hasSteamDownloads = true;
    }

    /// <summary>The saved Steam downloads that ran during [from, to), oldest first.</summary>
    public List<GameDownload> GetSteamDownloads(long from, long to)
    {
        var list = new List<GameDownload>();
        if (!HasSteamDownloads) return list;
        using var cmd = Cmd("SELECT app, started, ended, bytes, new_install, how, name FROM steam_download WHERE started < $to AND ended >= $from ORDER BY started",
            ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new GameDownload(r.GetString(0), r.IsDBNull(6) ? null : r.GetString(6), TimeUtil.FromUnix(r.GetInt64(1)), TimeUtil.FromUnix(r.GetInt64(2)),
                r.GetInt64(3), r.GetInt64(4) == 1, (DownloadEnd)r.GetInt32(5)));
        return list;
    }
}
