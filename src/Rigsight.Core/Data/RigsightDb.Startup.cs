using Rigsight.Core.Startup;

namespace Rigsight.Core.Data;

// Every start of the PC (1.22.0): Windows' sign-in logs hold about a week (Shell-Core, 1 MB) to a month (Winlogon), so each
// start is copied here a few minutes after it, the parts filled in as they come (the firmware's time and Windows starting at
// once, getting ready after sign-in once the apps that start with Windows were launched). Each app's launch is kept too.
// Windows' own start-up record (event 100, admin only) is kept apart, used only where the sign-in logs no longer reached a
// start. Kept as long as the rest of the history.
public sealed partial class RigsightDb
{
    private void EnsureStartupSchema()
    {
        // main_ms, logon_wait_ms and post_ms are event 100's; win_ms, signin_ms and ready_ms the sign-in logs'.
        Exec("""
            CREATE TABLE IF NOT EXISTS startup(ts INTEGER PRIMARY KEY, kind INTEGER NOT NULL, bios_ms INTEGER, main_ms INTEGER, logon_wait_ms INTEGER,
                post_ms INTEGER, after_update INTEGER, read INTEGER NOT NULL DEFAULT 0, win_ms INTEGER, signin_ms INTEGER, ready_ms INTEGER,
                apps_done INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS startup_slow(ts INTEGER NOT NULL, event INTEGER NOT NULL, name TEXT NOT NULL, file TEXT, total_ms INTEGER NOT NULL,
                slower_ms INTEGER NOT NULL, PRIMARY KEY(ts, event, name)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS startup_app(ts INTEGER NOT NULL, seq INTEGER NOT NULL, name TEXT NOT NULL, exe TEXT NOT NULL, cmd TEXT,
                at_ms INTEGER NOT NULL, ms INTEGER NOT NULL, PRIMARY KEY(ts, seq)) WITHOUT ROWID;
            """);
        // A test copy of 1.22.0 made the table before the sign-in logs were read.
        foreach (var column in new[] { "win_ms", "signin_ms", "ready_ms" })
            if (!HasColumn("startup", column)) Exec($"ALTER TABLE startup ADD COLUMN {column} INTEGER");
        if (!HasColumn("startup", "apps_done")) Exec("ALTER TABLE startup ADD COLUMN apps_done INTEGER NOT NULL DEFAULT 0");
    }

    // The app reads a database an older agent made (no table yet): no starts then.
    private bool? _hasStartup;
    private bool HasStartup => _hasStartup ??= HasTable("startup");
    private bool? _hasStartupSignIn;

    /// <summary>How far apart two readings of one start can be (a start is kept by the second Windows logged it; two starts can be a minute apart).</summary>
    private const int SameStartSec = 30;

    /// <summary>
    /// Keeps starts read from Windows' logs: a start already kept (within half a minute of one) gets what it didn't have
    /// yet, the sign-in logs' parts replace what an earlier read had (a read that no longer reaches a start leaves it as it
    /// was), and its apps are written until a read has them all; a new one is added. Returns how many were new and how many
    /// were filled in.
    /// </summary>
    public (int Added, int Filled) SaveStarts(IEnumerable<StartupLogReader.FoundStart> starts)
    {
        int added = 0, filled = 0;
        foreach (var s in starts)
        {
            long ts = TimeUtil.ToUnix(s.Time);
            long? kept = null;
            bool appsKept = false;
            using (var find = Cmd("SELECT ts, apps_done FROM startup WHERE ts BETWEEN $a AND $b ORDER BY abs(ts - $t) LIMIT 1",
                       ("$a", ts - SameStartSec), ("$b", ts + SameStartSec), ("$t", ts)))
            using (var row = find.ExecuteReader())
                if (row.Read()) (kept, appsKept) = (row.GetInt64(0), row.GetInt64(1) != 0);
            bool changed = false;
            if (kept is long at)
            {
                // The kind from the System log wins over a start first made from Windows' start-up record alone.
                using var update = Cmd("""
                    UPDATE startup SET kind = CASE WHEN $known = 1 THEN $kind ELSE kind END, bios_ms = coalesce(bios_ms, $bios), main_ms = coalesce(main_ms, $main),
                        logon_wait_ms = coalesce(logon_wait_ms, $wait), post_ms = coalesce(post_ms, $post), after_update = coalesce(after_update, $upd),
                        read = max(read, $read), win_ms = coalesce($win, win_ms), signin_ms = coalesce($sw, signin_ms), ready_ms = coalesce($ready, ready_ms),
                        apps_done = max(apps_done, $done)
                    WHERE ts = $ts AND (($known = 1 AND kind <> $kind) OR (bios_ms IS NULL AND $bios IS NOT NULL) OR (main_ms IS NULL AND $main IS NOT NULL)
                        OR (post_ms IS NULL AND $post IS NOT NULL) OR (after_update IS NULL AND $upd IS NOT NULL) OR (read < $read)
                        OR ($win IS NOT NULL AND win_ms IS NOT $win) OR ($sw IS NOT NULL AND signin_ms IS NOT $sw) OR ($ready IS NOT NULL AND ready_ms IS NOT $ready)
                        OR apps_done < $done)
                    """, Args(s, at));
                changed = update.ExecuteNonQuery() > 0;
                ts = at;
            }
            else
            {
                using var insert = Cmd("""
                    INSERT INTO startup(ts, kind, bios_ms, main_ms, logon_wait_ms, post_ms, after_update, read, win_ms, signin_ms, ready_ms, apps_done)
                    VALUES($ts, $kind, $bios, $main, $wait, $post, $upd, $read, $win, $sw, $ready, $done)
                    """, Args(s, ts));
                insert.ExecuteNonQuery();
                added++;
            }
            // Each app's launch, until a read has them all (then it's never written again).
            if (s.Apps.Count > 0 && !appsKept)
            {
                using (var clear = Cmd("DELETE FROM startup_app WHERE ts = $ts", ("$ts", ts))) clear.ExecuteNonQuery();
                for (int i = 0; i < s.Apps.Count; i++)
                {
                    var a = s.Apps[i];
                    using var app = Cmd("INSERT INTO startup_app(ts, seq, name, exe, cmd, at_ms, ms) VALUES($ts, $seq, $n, $e, $c, $at, $ms)",
                        ("$ts", ts), ("$seq", i), ("$n", a.Name), ("$e", a.Exe), ("$c", a.Command), ("$at", a.AtMs), ("$ms", a.Ms));
                    app.ExecuteNonQuery();
                }
                changed = true;
            }
            if (changed && kept is not null) filled++;
            foreach (var slow in s.Slow)
            {
                using var item = Cmd("INSERT OR IGNORE INTO startup_slow(ts, event, name, file, total_ms, slower_ms) VALUES($ts, $e, $n, $f, $total, $slower)",
                    ("$ts", ts), ("$e", slow.Event), ("$n", slow.Name), ("$f", slow.File), ("$total", slow.TotalMs), ("$slower", slow.SlowerMs));
                item.ExecuteNonQuery();
            }
        }
        _hasStartup = true;
        return (added, filled);

        static (string, object?)[] Args(StartupLogReader.FoundStart s, long ts) =>
        [
            ("$ts", ts), ("$kind", (int)s.Kind), ("$known", s.KindKnown ? 1 : 0), ("$bios", s.BiosMs), ("$main", s.MainMs), ("$wait", s.LogonWaitMs), ("$post", s.PostMs),
            ("$upd", s.AfterUpdate is bool b ? (b ? 1 : 0) : null), ("$read", s.Read ? 1 : 0), ("$win", s.WindowsMs), ("$sw", s.SignInWaitMs),
            ("$ready", s.ReadyMs), ("$done", s.AppsDone ? 1 : 0),
        ];
    }

    /// <summary>
    /// Where the next read of Windows' logs starts: the newest start kept, or when the history was last cleared (so Clear all
    /// history doesn't read the starts Windows still holds back in); null the first time (all of them).
    /// </summary>
    public DateTime? LatestStart()
    {
        if (!HasStartup) return null;
        using var cmd = Cmd("SELECT max(ts) FROM startup");
        long? latest = cmd.ExecuteScalar() is long t ? t : null;
        long? cleared = long.TryParse(GetMeta(StartupClearedKey), out long c) ? c : null;
        return latest is null && cleared is null ? null : TimeUtil.FromUnix(Math.Max(latest ?? 0, cleared ?? 0));
    }

    private const string StartupClearedKey = "startup_cleared";

    private const string StartupSignInKey = "startup_signin_read";

    /// <summary>Windows' sign-in logs were read once in full (so the starts kept before were given their parts).</summary>
    public bool StartupSignInRead() => GetMeta(StartupSignInKey) == "1";

    public void MarkStartupSignInRead() => SetMeta(StartupSignInKey, "1");

    /// <summary>When the history was last cleared: starts before it aren't read back in.</summary>
    public DateTime? StartsClearedAt() => long.TryParse(GetMeta(StartupClearedKey), out long c) ? TimeUtil.FromUnix(c) : null;

    /// <summary>The oldest start kept, or null.</summary>
    public DateTime? FirstStart()
    {
        if (!HasStartup) return null;
        using var cmd = Cmd("SELECT min(ts) FROM startup");
        return cmd.ExecuteScalar() is long t ? TimeUtil.FromUnix(t) : null;
    }

    /// <summary>
    /// The starts before <paramref name="to"/> (all of them: a few a day), oldest first, with what Windows named as slower on
    /// each and each app launched. The parts are the sign-in logs' where they have both Windows starting and getting ready
    /// after sign-in (so every start is measured the same way); else Windows' own record's, where it has one; else what the
    /// sign-in logs gave.
    /// </summary>
    public List<StartRecord> GetStarts(DateTime to)
    {
        if (!HasStartup) return [];
        long t = TimeUtil.ToUnix(to);
        var slow = new Dictionary<long, List<SlowItem>>();
        using (var s = Cmd("SELECT ts, event, name, file, total_ms, slower_ms FROM startup_slow WHERE ts < $t ORDER BY slower_ms DESC", ("$t", t)))
        using (var r = s.ExecuteReader())
            while (r.Read())
            {
                long ts = r.GetInt64(0);
                if (!slow.TryGetValue(ts, out var items)) slow[ts] = items = [];
                items.Add(new SlowItem((int)r.GetInt64(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), (int)r.GetInt64(4), (int)r.GetInt64(5)));
            }
        var apps = new Dictionary<long, List<AppLaunch>>();
        // An agent from before the sign-in logs were read made the table without them (the app reads before it updates).
        bool signIn = _hasStartupSignIn ??= HasTable("startup_app") && HasColumn("startup", "ready_ms");
        if (signIn)
            using (var a = Cmd("SELECT ts, name, exe, cmd, at_ms, ms FROM startup_app WHERE ts < $t ORDER BY ts, seq", ("$t", t)))
            using (var r = a.ExecuteReader())
                while (r.Read())
                {
                    long ts = r.GetInt64(0);
                    if (!apps.TryGetValue(ts, out var launched)) apps[ts] = launched = [];
                    launched.Add(new AppLaunch(r.GetString(1), r.GetString(2), (int)r.GetInt64(4), (int)r.GetInt64(5)) { Command = r.IsDBNull(3) ? null : r.GetString(3) });
                }
        var list = new List<StartRecord>();
        using var cmd = Cmd($"SELECT ts, kind, bios_ms, main_ms, logon_wait_ms, post_ms, after_update, read, {(signIn ? "win_ms, ready_ms" : "NULL, NULL")} FROM startup WHERE ts < $t ORDER BY ts", ("$t", t));
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            long ts = rd.GetInt64(0);
            int? Ms(int i) => rd.IsDBNull(i) ? null : (int)rd.GetInt64(i);
            var kind = Enum.IsDefined(typeof(StartKind), (int)rd.GetInt64(1)) ? (StartKind)(int)rd.GetInt64(1) : StartKind.Full;
            int? windows = Ms(8), after = Ms(9);
            // Lengths only ever come from the sign-in logs, so every start is measured the same way: event 100's parts end
            // elsewhere (its "after sign-in" runs until the PC settles), and mixing them made a usual of 43s to 1m 24s out of
            // starts of about 45s (event 100, 2025) and about 1m 30s (the sign-in logs). Event 100 keeps what it named as slower.
            list.Add(new StartRecord(TimeUtil.FromUnix(ts), kind, Ms(2), windows, after, rd.IsDBNull(6) ? null : rd.GetInt64(6) != 0, rd.GetInt64(7) != 0)
            {
                Slow = slow.TryGetValue(ts, out var items) ? items : [],
                Apps = apps.TryGetValue(ts, out var launched) ? launched : [],
            });
        }
        return list;
    }

    private void PruneStartup(long before)
    {
        if (!HasStartup) return;
        using var cmd = Cmd("DELETE FROM startup WHERE ts < $t; DELETE FROM startup_slow WHERE ts < $t; DELETE FROM startup_app WHERE ts < $t;", ("$t", before));
        cmd.ExecuteNonQuery();
    }

    private void ClearStartup()
    {
        if (!HasStartup) return;
        Exec("DELETE FROM startup; DELETE FROM startup_slow; DELETE FROM startup_app;");
        SetMeta(StartupClearedKey, TimeUtil.NowUnix().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
