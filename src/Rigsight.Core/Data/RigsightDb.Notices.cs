using Rigsight.Core.Notices;

namespace Rigsight.Core.Data;

// The recent notifications (2026-11-07): every notification RigSight sent or held back, kept 30 days for the bell's list,
// and what each was about, so the same thing isn't said twice (across restarts too). The agent writes; the app reads.
public sealed partial class RigsightDb
{
    /// <summary>How long the list keeps a notification.</summary>
    public static readonly TimeSpan NoticesKept = TimeSpan.FromDays(30);

    private void EnsureNoticeSchema() => Exec("""
        CREATE TABLE IF NOT EXISTS notice(id INTEGER PRIMARY KEY, ts INTEGER NOT NULL, kind TEXT NOT NULL, level INTEGER NOT NULL,
            title TEXT NOT NULL, body TEXT NOT NULL, page TEXT, arg TEXT, icon TEXT, key TEXT, shown INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_notice_ts ON notice(ts);
        CREATE INDEX IF NOT EXISTS ix_notice_key ON notice(key);
        CREATE TABLE IF NOT EXISTS notice_said(key TEXT PRIMARY KEY, ts INTEGER NOT NULL, value TEXT) WITHOUT ROWID;
        """);

    // The app reads a database an older agent made (no table yet): nothing kept then.
    private bool? _hasNotices;
    private bool HasNotices => _hasNotices ??= HasTable("notice");

    /// <summary>Keeps a notification, and forgets those past <see cref="NoticesKept"/>. Returns its id.</summary>
    public long SaveNotice(NoticeRecord n)
    {
        long ts = TimeUtil.ToUnix(n.Time);
        using (var del = Cmd("DELETE FROM notice WHERE ts < $old", ("$old", ts - (long)NoticesKept.TotalSeconds))) del.ExecuteNonQuery();
        using var cmd = Cmd("""
            INSERT INTO notice(ts, kind, level, title, body, page, arg, icon, key, shown) VALUES($ts, $kind, $level, $title, $body, $page, $arg, $icon, $key, $shown);
            SELECT last_insert_rowid();
            """, ("$ts", ts), ("$kind", n.Kind), ("$level", (int)n.Level), ("$title", n.Title), ("$body", n.Body), ("$page", n.Page), ("$arg", n.Arg),
            ("$icon", n.Icon), ("$key", n.Key), ("$shown", n.Shown ? 1 : 0));
        _hasNotices = true;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>The notifications kept since <paramref name="from"/> (unix seconds), newest first.</summary>
    public List<NoticeRecord> GetNotices(long from)
    {
        var list = new List<NoticeRecord>();
        if (!HasNotices) return list;
        using var cmd = Cmd("SELECT id, ts, kind, level, title, body, page, arg, icon, key, shown FROM notice WHERE ts >= $from ORDER BY ts DESC, id DESC", ("$from", from));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new NoticeRecord(TimeUtil.FromUnix(r.GetInt64(1)), r.GetString(2), (NoticeLevel)r.GetInt32(3), r.GetString(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
                r.IsDBNull(9) ? null : r.GetString(9), r.GetInt64(10) == 1) { Id = r.GetInt64(0) });
        return list;
    }

    /// <summary>How many cards of these levels were shown since <paramref name="from"/> (the daily limit counts them).</summary>
    public int CountShownNotices(long from, params NoticeLevel[] levels)
    {
        if (!HasNotices || levels.Length == 0) return 0;
        using var cmd = Cmd($"SELECT count(*) FROM notice WHERE ts >= $from AND shown = 1 AND level IN ({string.Join(",", levels.Select(l => (int)l))})", ("$from", from));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>When something was last said, and what it was then (a drive's bad sector count); null: never.</summary>
    public (DateTime At, string? Value)? Said(string key)
    {
        if (!HasTable("notice_said")) return null;
        using var cmd = Cmd("SELECT ts, value FROM notice_said WHERE key = $key", ("$key", key));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (TimeUtil.FromUnix(r.GetInt64(0)), r.IsDBNull(1) ? null : r.GetString(1)) : null;
    }

    /// <summary>
    /// Each app's memory a minute since <paramref name="from"/> (unix seconds), for the apps that reached
    /// <paramref name="minBytes"/> in that time, oldest first (the "keeps using more memory" check).
    /// </summary>
    public Dictionary<long, List<(DateTime At, long Bytes)>> GetAppMemory(long from, long minBytes)
    {
        var map = new Dictionary<long, List<(DateTime, long)>>();
        using var cmd = Cmd("""
            SELECT ts, app, mem FROM app_minute WHERE ts >= $from AND app IN (SELECT app FROM app_minute WHERE ts >= $from GROUP BY app HAVING max(mem) >= $min)
            ORDER BY ts
            """, ("$from", from), ("$min", minBytes / 1048576.0));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            long app = r.GetInt64(1);
            if (!map.TryGetValue(app, out var list)) map[app] = list = [];
            list.Add((TimeUtil.FromUnix(r.GetInt64(0)), (long)(r.GetDouble(2) * 1048576))); // kept in MB
        }
        return map;
    }

    /// <summary>Marks something as said now (or seen, for a drive's state first read), with what it was.</summary>
    public void MarkSaid(string key, DateTime at, string? value = null)
    {
        using var cmd = Cmd("INSERT INTO notice_said(key, ts, value) VALUES($key, $ts, $value) ON CONFLICT(key) DO UPDATE SET ts = excluded.ts, value = excluded.value",
            ("$key", key), ("$ts", TimeUtil.ToUnix(at)), ("$value", value));
        cmd.ExecuteNonQuery();
    }
}
