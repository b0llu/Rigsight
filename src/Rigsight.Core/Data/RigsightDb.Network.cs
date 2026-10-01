using Microsoft.Data.Sqlite;

namespace Rigsight.Core.Data;

// Internet use (0.14.0): all apps together per minute and per day, each app per hour and per day, big downloads, and the
// times the internet dropped. Kept in tables of their own, so nothing about apps' time or temperatures changes.
public sealed partial class RigsightDb
{
    /// <summary>The slowest download that counts as steady (bytes a second): below it a line's speed can't be judged.</summary>
    public const long MinSteadyBytes = 500_000;

    private void EnsureNetworkSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS net_minute(ts INTEGER PRIMARY KEY, down INTEGER NOT NULL, up INTEGER NOT NULL,
                bg_down INTEGER NOT NULL, bg_up INTEGER NOT NULL, away_down INTEGER NOT NULL, away_up INTEGER NOT NULL,
                lan INTEGER NOT NULL, steady INTEGER, app INTEGER);
            CREATE TABLE IF NOT EXISTS net_day(day INTEGER PRIMARY KEY, minutes INTEGER NOT NULL, down INTEGER NOT NULL, up INTEGER NOT NULL,
                bg_down INTEGER NOT NULL, bg_up INTEGER NOT NULL, away_down INTEGER NOT NULL, away_up INTEGER NOT NULL,
                lan INTEGER NOT NULL, best INTEGER, best_ts INTEGER);
            CREATE TABLE IF NOT EXISTS net_app_hour(ts INTEGER NOT NULL, app INTEGER NOT NULL, down INTEGER NOT NULL, up INTEGER NOT NULL,
                bg_down INTEGER NOT NULL, bg_up INTEGER NOT NULL, away_down INTEGER NOT NULL, away_up INTEGER NOT NULL,
                game_down INTEGER NOT NULL, lan INTEGER NOT NULL, PRIMARY KEY(ts, app)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS net_app_day(day INTEGER NOT NULL, app INTEGER NOT NULL, down INTEGER NOT NULL, up INTEGER NOT NULL,
                bg_down INTEGER NOT NULL, bg_up INTEGER NOT NULL, away_down INTEGER NOT NULL, away_up INTEGER NOT NULL,
                game_down INTEGER NOT NULL, lan INTEGER NOT NULL, PRIMARY KEY(day, app)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS net_transfer(start INTEGER NOT NULL, app INTEGER NOT NULL, end INTEGER NOT NULL, bytes INTEGER NOT NULL,
                sec INTEGER NOT NULL, PRIMARY KEY(start, app)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS net_drop(start INTEGER PRIMARY KEY, end INTEGER NOT NULL, kind INTEGER NOT NULL);
            """);
    }

    // The app reads a database the agent made, which may be from before 0.14.0 (no network tables yet).
    private bool? _hasNetwork;
    private bool HasNetwork => _hasNetwork ??= HasTable("net_minute");

    // ── Writer ────────────────────────────────────────────────────────────

    /// <summary>A minute of internet use, and its day added up again from its minutes (at most 1,440, by the primary key).</summary>
    public void WriteNetMinute(NetMinute m)
    {
        using (var cmd = Cmd("""
            INSERT OR REPLACE INTO net_minute(ts, down, up, bg_down, bg_up, away_down, away_up, lan, steady, app)
            VALUES($ts, $d, $u, $bd, $bu, $ad, $au, $lan, $s, $app)
            """, ("$ts", m.Ts), ("$d", m.Down), ("$u", m.Up), ("$bd", m.BgDown), ("$bu", m.BgUp), ("$ad", m.AwayDown), ("$au", m.AwayUp),
            ("$lan", m.Lan), ("$s", m.Steady), ("$app", m.App)))
            cmd.ExecuteNonQuery();
        RollNetDay(m.Ts);
    }

    private void RollNetDay(long ts)
    {
        // The day's totals, and its fastest steady minute (both one row, found through the minutes' key).
        using var day = Cmd($"""
            WITH d AS (SELECT {DayOf("$ts")} AS day, count(*) AS n, total(down) AS down, total(up) AS up, total(bg_down) AS bg_down,
                              total(bg_up) AS bg_up, total(away_down) AS away_down, total(away_up) AS away_up, total(lan) AS lan
                       FROM net_minute WHERE ts >= {DayOf("$ts")} AND ts < {DayOf("$ts", ", '+1 day'")}),
                 b AS (SELECT ts, steady FROM net_minute WHERE ts >= {DayOf("$ts")} AND ts < {DayOf("$ts", ", '+1 day'")}
                         AND steady >= {MinSteadyBytes} ORDER BY steady DESC, ts LIMIT 1)
            INSERT OR REPLACE INTO net_day(day, minutes, down, up, bg_down, bg_up, away_down, away_up, lan, best, best_ts)
            SELECT d.day, d.n, d.down, d.up, d.bg_down, d.bg_up, d.away_down, d.away_up, d.lan, b.steady, b.ts
            FROM d LEFT JOIN b WHERE d.n > 0
            """, ("$ts", ts));
        day.ExecuteNonQuery();
    }

    /// <summary>Adds one app's use into its hour and its day (sums add up).</summary>
    public void AddNetAppUse(NetAppUse u)
    {
        foreach (var (table, key, value) in new[] { ("net_app_hour", "ts", "$ts"), ("net_app_day", "day", DayOf("$ts")) })
        {
            using var cmd = Cmd($"""
                INSERT INTO {table}({key}, app, down, up, bg_down, bg_up, away_down, away_up, game_down, lan)
                VALUES({value}, $app, $d, $u, $bd, $bu, $ad, $au, $gd, $lan)
                ON CONFLICT({key}, app) DO UPDATE SET down = down + excluded.down, up = up + excluded.up,
                    bg_down = bg_down + excluded.bg_down, bg_up = bg_up + excluded.bg_up, away_down = away_down + excluded.away_down,
                    away_up = away_up + excluded.away_up, game_down = game_down + excluded.game_down, lan = lan + excluded.lan
                """, ("$ts", u.Ts), ("$app", u.App), ("$d", u.Down), ("$u", u.Up), ("$bd", u.BgDown), ("$bu", u.BgUp),
                ("$ad", u.AwayDown), ("$au", u.AwayUp), ("$gd", u.GameDown), ("$lan", u.Lan));
            cmd.ExecuteNonQuery();
        }
    }

    public void InsertNetTransfer(NetTransfer t)
    {
        using var cmd = Cmd("INSERT OR REPLACE INTO net_transfer(start, app, end, bytes, sec) VALUES($s, $a, $e, $b, $sec)",
            ("$s", t.Start), ("$a", t.App), ("$e", t.End), ("$b", t.Bytes), ("$sec", t.Sec));
        cmd.ExecuteNonQuery();
    }

    public void InsertNetDrop(NetDrop d)
    {
        using var cmd = Cmd("INSERT OR REPLACE INTO net_drop(start, end, kind) VALUES($s, $e, $k)", ("$s", d.Start), ("$e", d.End), ("$k", (int)d.Kind));
        cmd.ExecuteNonQuery();
    }

    private void PruneNetwork(long before)
    {
        using (var c = Cmd($"""
            DELETE FROM net_minute WHERE ts < $t;
            DELETE FROM net_day WHERE day < {DayOf("$t")};
            DELETE FROM net_app_hour WHERE ts < $t;
            DELETE FROM net_app_day WHERE day < {DayOf("$t")};
            DELETE FROM net_transfer WHERE start < $t;
            DELETE FROM net_drop WHERE start < $t;
            """, ("$t", before))) c.ExecuteNonQuery();
        // The day the cutoff falls in, added up again from what's left of its minutes (its apps keep the whole day, as
        // app_month keeps the cutoff's month: a day isn't split).
        using (var c = Cmd($"DELETE FROM net_day WHERE day = {DayOf("$t")}", ("$t", before))) c.ExecuteNonQuery();
        RollNetDay(before);
    }

    // ── Reader ────────────────────────────────────────────────────────────

    /// <summary>The first day with any internet use recorded (null: none yet).</summary>
    public long? FirstNetDay()
    {
        if (!HasNetwork) return null;
        using var cmd = Cmd("SELECT min(day) FROM net_day");
        return cmd.ExecuteScalar() is long v ? v : null;
    }

    public List<NetMinute> GetNetMinutes(long from, long to)
    {
        if (!HasNetwork) return [];
        using var cmd = Cmd("SELECT ts, down, up, bg_down, bg_up, away_down, away_up, lan, steady, app FROM net_minute WHERE ts >= $from AND ts < $to ORDER BY ts",
            ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        var list = new List<NetMinute>();
        while (r.Read())
            list.Add(new NetMinute(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6),
                r.GetInt64(7), L(r, 8), L(r, 9)));
        return list;
    }

    public List<NetDay> GetNetDays(long from, long to)
    {
        if (!HasNetwork) return [];
        using var cmd = Cmd("""
            SELECT day, minutes, down, up, bg_down, bg_up, away_down, away_up, lan, best, best_ts FROM net_day
            WHERE day >= $from AND day < $to ORDER BY day
            """, ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        var list = new List<NetDay>();
        while (r.Read())
            list.Add(new NetDay(r.GetInt64(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6),
                r.GetInt64(7), r.GetInt64(8), L(r, 9), L(r, 10)));
        return list;
    }

    /// <summary>Each app's use per hour from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public List<NetAppUse> GetNetAppHours(long from, long to) =>
        ReadNetUse("SELECT ts, app, down, up, bg_down, bg_up, away_down, away_up, game_down, lan FROM net_app_hour WHERE ts >= $from AND ts < $to", from, to);

    /// <summary>Each app's use per local day, for the days from <paramref name="from"/> to <paramref name="to"/> (local midnights).</summary>
    public List<NetAppUse> GetNetAppDays(long from, long to) =>
        ReadNetUse("SELECT day, app, down, up, bg_down, bg_up, away_down, away_up, game_down, lan FROM net_app_day WHERE day >= $from AND day < $to", from, to);

    /// <summary>Each app's use over the days from <paramref name="from"/> to <paramref name="to"/>, added up (Ts is 0).</summary>
    public List<NetAppUse> GetNetAppTotals(long from, long to) =>
        ReadNetUse("""
            SELECT 0, app, sum(down), sum(up), sum(bg_down), sum(bg_up), sum(away_down), sum(away_up), sum(game_down), sum(lan)
            FROM net_app_day WHERE day >= $from AND day < $to GROUP BY app
            """, from, to);

    private List<NetAppUse> ReadNetUse(string sql, long from, long to)
    {
        if (!HasNetwork) return [];
        using var cmd = Cmd(sql, ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        var list = new List<NetAppUse>();
        while (r.Read())
        {
            list.Add(new NetAppUse
            {
                Ts = r.GetInt64(0), App = r.GetInt64(1), Down = r.GetInt64(2), Up = r.GetInt64(3), BgDown = r.GetInt64(4), BgUp = r.GetInt64(5),
                AwayDown = r.GetInt64(6), AwayUp = r.GetInt64(7), GameDown = r.GetInt64(8), Lan = r.GetInt64(9),
            });
        }
        return list;
    }

    /// <summary>The biggest downloads that started from <paramref name="from"/> to <paramref name="to"/>, biggest first.</summary>
    public List<NetTransfer> GetNetTransfers(long from, long to, int count)
    {
        if (!HasNetwork) return [];
        using var cmd = Cmd("SELECT start, app, end, bytes, sec FROM net_transfer WHERE start >= $from AND start < $to ORDER BY bytes DESC LIMIT $n",
            ("$from", from), ("$to", to), ("$n", count));
        using var r = cmd.ExecuteReader();
        var list = new List<NetTransfer>();
        while (r.Read()) list.Add(new NetTransfer(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt32(4)));
        return list;
    }

    /// <summary>The drops that started from <paramref name="from"/> to <paramref name="to"/>, in order.</summary>
    public List<NetDrop> GetNetDrops(long from, long to)
    {
        if (!HasNetwork) return [];
        using var cmd = Cmd("SELECT start, end, kind FROM net_drop WHERE start >= $from AND start < $to ORDER BY start", ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        var list = new List<NetDrop>();
        while (r.Read()) list.Add(new NetDrop(r.GetInt64(0), r.GetInt64(1), (NetDropKind)r.GetInt32(2)));
        return list;
    }

    private static long? L(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
}
