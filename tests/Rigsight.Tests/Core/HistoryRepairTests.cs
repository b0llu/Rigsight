using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Rigsight.Core.Data;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Core;

/// <summary>
/// The history file damaged on purpose, the way a power cut or a failing write leaves it (pages of a table wiped, the
/// header gone, a lookup list broken, a whole file of noise), then opened as the agent opens it: what comes back, what
/// is kept, and that nothing healthy is ever taken for damaged.
/// </summary>
public sealed class HistoryRepairTests
{
    private const int Days = 4;
    private static readonly DateTime Start = new(2026, 10, 5);
    private static long T0 => TimeUtil.ToUnix(Start);

    // ── A history to damage ──────────────────────────────────────────────

    /// <summary>
    /// Four days of minutes, apps, sessions, crashes, per-app minutes (a table keyed without a rowid) and the
    /// inventory: enough rows that every table spans many pages.
    /// </summary>
    private static string History(string name = "history")
    {
        var dir = TestEnvironment.NewFolder(name);
        var path = Path.Combine(dir, "rigsight.db");
        using (var db = RigsightDb.OpenWriter(path)) { }
        using (var conn = Open(path))
        using (var tx = conn.BeginTransaction())
        {
            void Run(string sql, params (string, object)[] ps)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
                cmd.ExecuteNonQuery();
            }
            for (int a = 1; a <= 3; a++)
                Run("INSERT INTO apps(id, exe, name, path, category, first_seen) VALUES($id, $exe, $name, NULL, 'Game', $t)",
                    ("$id", a), ("$exe", $"game{a}.exe"), ("$name", $"Game {a}"), ("$t", T0));
            for (int m = 0; m < Days * 1440; m++)
            {
                long ts = T0 + m * 60L;
                Run("INSERT INTO system_minute(ts, cpu_temp, gpu_temp, cpu_load, gpu_load, gpu_power, fg_app, active_sec) VALUES($ts, $c, $g, 20, 60, 250, 1, 60)",
                    ("$ts", ts), ("$c", 40 + m % 30), ("$g", 50 + m % 25));
                if (m % 5 == 0)
                    for (int a = 1; a <= 2; a++)
                        Run("INSERT INTO app_minute(ts, app, cpu, gpu, mem, disk) VALUES($ts, $a, 5, 40, 1000, 0)", ("$ts", ts), ("$a", a));
            }
            for (int s = 0; s < 60; s++)
                Run("INSERT INTO sessions(app_id, start, end, active_sec, is_game) VALUES(1, $s, $e, 3000, 1)", ("$s", T0 + s * 5000L), ("$e", T0 + s * 5000L + 3000));
            for (int c = 0; c < 8; c++)
                Run("INSERT INTO crashes(ts, kind, app_exe, code, detail) VALUES($ts, 'AppCrash', 'game1.exe', 'c0000005', 'Application Error')", ("$ts", T0 + c * 30000L));
            Run("INSERT INTO inventory(kind, key, name, value) VALUES('game', 'steam:570', 'Dota 2', '25971480')");
            Run("INSERT OR REPLACE INTO meta(key, value) VALUES('test', 'kept')");
            tx.Commit();
        }
        SqliteConnection.ClearAllPools();
        return path;
    }

    private static SqliteConnection Open(string path, bool readOnly = false)
    {
        var conn = new SqliteConnection($"Data Source={path};Mode={(readOnly ? "ReadOnly" : "ReadWrite")};Pooling=False");
        conn.Open();
        return conn;
    }

    private static long Count(string path, string table)
    {
        using var conn = Open(path, readOnly: true);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {table}";
        return (long)cmd.ExecuteScalar()!;
    }

    private static List<long> Minutes(string path)
    {
        using var conn = Open(path, readOnly: true);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ts FROM system_minute ORDER BY ts";
        using var r = cmd.ExecuteReader();
        var list = new List<long>();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    private static void IntegrityOk(string path)
    {
        using var conn = Open(path, readOnly: true);
        Assert.True(HistoryRepair.Check(conn).Ok, "the repaired history should pass SQLite's own integrity check");
    }

    private static void Insert(string path, string sql)
    {
        using var conn = Open(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    private static void PowerCut(string path) => File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, HistoryRepair.RunningMarker), "x");

    // ── Damage, done the way it happens: whole pages of the file overwritten ──

    private const int PageSize = 4096;

    private static long RootOf(string path, string name)
    {
        using var conn = Open(path, readOnly: true);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT rootpage FROM sqlite_master WHERE name = $n";
        cmd.Parameters.AddWithValue("$n", name);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>The leaf pages of one table or index, in key order, read from the file's own b-tree.</summary>
    private static List<long> Leaves(string path, string name)
    {
        var bytes = File.ReadAllBytes(path);
        var leaves = new List<long>();
        void Walk(long page)
        {
            int at = (int)((page - 1) * PageSize) + (page == 1 ? 100 : 0);
            byte type = bytes[at];
            if (type is 0x0D or 0x0A) { leaves.Add(page); return; }
            int cells = bytes[at + 3] << 8 | bytes[at + 4];
            int start = (int)((page - 1) * PageSize);
            for (int i = 0; i < cells; i++)
            {
                int ptr = bytes[at + 12 + i * 2] << 8 | bytes[at + 13 + i * 2];
                Walk(BE32(bytes, start + ptr));
            }
            Walk(BE32(bytes, at + 8));
        }
        Walk(RootOf(path, name));
        return leaves;
    }

    private static long BE32(byte[] b, int at) => (long)b[at] << 24 | (long)b[at + 1] << 16 | (long)b[at + 2] << 8 | b[at + 3];

    private static void Wipe(string path, IEnumerable<long> pages, byte fill = 0xAB)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Write);
        foreach (var p in pages)
        {
            f.Position = (p - 1) * PageSize;
            f.Write(Enumerable.Repeat(fill, PageSize).ToArray());
        }
    }

    private static HistoryReport Reopen(string path, Func<string, long>? free = null)
    {
        using var db = HistoryRepair.Open(path, out var report, free, now: new DateTime(2026, 10, 18, 16, 47, 0));
        return report;
    }

    // ── Healthy files are never taken for damaged ───────────────────────

    [Fact]
    public void A_normal_start_doesnt_check_or_touch_anything()
    {
        var path = History();
        var report = Reopen(path);
        Assert.Equal(HistoryOutcome.Fine, report.Outcome);
        Assert.Null(report.Why);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "rigsight-damaged-*"));
    }

    [Fact]
    public void After_a_power_cut_a_healthy_file_is_checked_and_left_as_it_was()
    {
        var path = History();
        long minutes = Count(path, "system_minute");
        PowerCut(path);

        var report = Reopen(path);

        Assert.Equal(HistoryOutcome.Fine, report.Outcome);
        Assert.Contains("didn't close normally", report.Why);
        Assert.Equal(minutes, Count(path, "system_minute"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "rigsight-damaged-*"));
    }

    [Fact]
    public void A_file_held_busy_by_another_program_is_not_damage()
    {
        var path = History();
        using var other = Open(path);
        using (var cmd = other.CreateCommand()) { cmd.CommandText = "BEGIN EXCLUSIVE"; cmd.ExecuteNonQuery(); }
        var busy = Assert.ThrowsAny<SqliteException>(() =>
        {
            using var conn = new SqliteConnection($"Data Source={path};Mode=ReadWrite;Pooling=False;Default Timeout=1");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "BEGIN IMMEDIATE";
            cmd.ExecuteNonQuery();
        });
        Assert.False(HistoryRepair.IsDamage(busy));
        Assert.False(HistoryRepair.IsDamage(new IOException("The device is not ready.")));
        Assert.False(HistoryRepair.IsDamage(new SqliteException("database or disk is full", 13)));
        Assert.False(HistoryRepair.IsDamage(new SqliteException("disk I/O error", 10)));
        Assert.True(HistoryRepair.IsDamage(new SqliteException("database disk image is malformed", 11)));
        Assert.True(HistoryRepair.IsDamage(new InvalidOperationException("wrapped", new SqliteException("file is not a database", 26))));
    }

    [Fact]
    public void Only_damage_is_passed_on_to_whoever_watches_for_it()
    {
        var seen = new List<Exception>();
        void Seen(Exception e) => seen.Add(e);
        HistoryRepair.DamageSeen += Seen;
        try
        {
            HistoryRepair.Report(new SqliteException("database is locked", 5));
            HistoryRepair.Report(new SqliteException("database disk image is malformed", 11));
        }
        finally { HistoryRepair.DamageSeen -= Seen; }
        Assert.Single(seen);
    }

    [Fact]
    public void A_repair_asked_for_but_not_needed_changes_nothing()
    {
        // A minute that failed to save once, for some other reason: the check finds the file fine and that's the end of it.
        var path = History();
        HistoryRepair.AskRepair(Path.GetDirectoryName(path)!);
        var report = Reopen(path);
        Assert.Equal(HistoryOutcome.Fine, report.Outcome);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, HistoryRepair.RepairMarker)));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "rigsight-damaged-*"));
    }

    // ── Damaged pages: everything else is kept, and the lost hours are named ──

    [Fact]
    public void Wiped_pages_in_the_middle_lose_only_their_minutes_and_say_which()
    {
        var path = History();
        var before = Minutes(path);
        var leaves = Leaves(path, "system_minute");
        Assert.True(leaves.Count > 40, $"expected many pages, got {leaves.Count}");
        Wipe(path, leaves.Skip(leaves.Count / 2).Take(2));
        string damagedHash = Hash(path);
        PowerCut(path);

        var report = Reopen(path);

        Assert.Equal(HistoryOutcome.Repaired, report.Outcome);
        var after = Minutes(path);
        var lost = before.Except(after).ToList();
        Assert.NotEmpty(lost);
        Assert.True(lost.Count < 2 * 80, $"two pages hold well under 160 minutes, lost {lost.Count}");
        Assert.Equal(lost.Count, lost[^1] / 60 - lost[0] / 60 + 1); // one unbroken run
        var span = Assert.Single(report.Lost);
        Assert.Equal(TimeUtil.FromUnix(lost[0]), span.From);
        Assert.Equal(TimeUtil.FromUnix(lost[^1] + 60), span.To);
        Assert.Equal(Days, report.Days);
        // Two pages fall in one day or across a midnight, as the rows' size has them: each day they touch isn't whole.
        Assert.Equal(Days - lost.Select(t => TimeUtil.FromUnix(t).Date).Distinct().Count(), report.DaysWhole);
        // Everything else is all there.
        Assert.Equal(3, Count(path, "apps"));
        Assert.Equal(60, Count(path, "sessions"));
        Assert.Equal(8, Count(path, "crashes"));
        Assert.Equal(1, Count(path, "inventory"));
        IntegrityOk(path);
        // The damaged file is kept exactly as it was found.
        var kept = Path.Combine(Path.GetDirectoryName(path)!, report.DamagedCopy!);
        Assert.Equal("rigsight-damaged-2026-10-18-1647.db", report.DamagedCopy);
        Assert.Equal(damagedHash, Hash(kept));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "rigsight-repair-work.db")));
    }

    [Fact]
    public void Wiped_first_pages_lose_only_the_start()
    {
        var path = History();
        var before = Minutes(path);
        Wipe(path, Leaves(path, "system_minute").Take(1));
        PowerCut(path);

        var report = Reopen(path);

        var after = Minutes(path);
        Assert.Equal(HistoryOutcome.Repaired, report.Outcome);
        Assert.True(after.Count > before.Count - 80, $"kept {after.Count} of {before.Count}");
        Assert.Equal(before[^1], after[^1]);
        var span = Assert.Single(report.Lost);
        Assert.Null(span.From);
        Assert.Equal(TimeUtil.FromUnix(after[0]), span.To);
    }

    [Fact]
    public void Wiped_last_pages_lose_only_the_end()
    {
        var path = History();
        var before = Minutes(path);
        Wipe(path, Leaves(path, "system_minute").TakeLast(1));
        PowerCut(path);

        var report = Reopen(path);

        var after = Minutes(path);
        Assert.Equal(before[0], after[0]);
        Assert.True(after.Count > before.Count - 80, $"kept {after.Count} of {before.Count}");
        var span = Assert.Single(report.Lost);
        Assert.Equal(TimeUtil.FromUnix(after[^1] + 60), span.From);
        Assert.Null(span.To);
    }

    [Fact]
    public void A_table_keyed_without_a_rowid_loses_only_its_wiped_page()
    {
        var path = History();
        long before = Count(path, "app_minute");
        var leaves = Leaves(path, "app_minute");
        Assert.True(leaves.Count > 4);
        Wipe(path, [leaves[leaves.Count / 2]]);
        PowerCut(path);

        var report = Reopen(path);

        long after = Count(path, "app_minute");
        Assert.Equal(HistoryOutcome.Repaired, report.Outcome);
        Assert.InRange(after, before - 400, before - 1);
        Assert.Equal(Days * 1440, Count(path, "system_minute")); // the other tables untouched
        IntegrityOk(path);
    }

    [Fact]
    public void A_broken_lookup_list_is_rebuilt_without_losing_a_reading()
    {
        var path = History();
        long sessions = Count(path, "sessions"), minutes = Count(path, "system_minute");
        Wipe(path, Leaves(path, "ix_sessions_start").Take(1), fill: 0x00);
        PowerCut(path);

        var report = Reopen(path);

        Assert.Contains(report.Outcome, new[] { HistoryOutcome.ListsRebuilt, HistoryOutcome.Repaired });
        Assert.Empty(report.Lost);
        Assert.Equal(sessions, Count(path, "sessions"));
        Assert.Equal(minutes, Count(path, "system_minute"));
        IntegrityOk(path);
    }

    [Fact]
    public void A_lookup_list_pointing_at_the_wrong_rows_is_rebuilt_in_place()
    {
        // The page itself whole, one entry in it changed: the list says a session is somewhere it isn't. Nothing is lost
        // and nothing needs copying: the list is built again from the table.
        var path = History();
        long sessions = Count(path, "sessions");
        var leaf = Leaves(path, "ix_sessions_start")[0];
        var bytes = File.ReadAllBytes(path);
        int at = (int)((leaf - 1) * PageSize);
        int cells = bytes[at + 3] << 8 | bytes[at + 4];
        int last = bytes[at + 8 + (cells - 1) * 2] << 8 | bytes[at + 9 + (cells - 1) * 2];
        // The last cell: payload size, then the record; its final byte is the session's id.
        int size = bytes[at + last];
        bytes[at + last + size] ^= 0x40;
        File.WriteAllBytes(path, bytes);
        PowerCut(path);

        var report = Reopen(path);

        Assert.Equal(HistoryOutcome.ListsRebuilt, report.Outcome);
        Assert.Null(report.DamagedCopy);
        Assert.Equal(sessions, Count(path, "sessions"));
        IntegrityOk(path);
    }

    // ── Files that won't open ───────────────────────────────────────────

    [Fact]
    public void A_wiped_header_is_put_back_and_nothing_is_lost()
    {
        var path = History();
        long minutes = Count(path, "system_minute");
        using (var f = new FileStream(path, FileMode.Open, FileAccess.Write)) f.Write(new byte[18]); // name and page size gone

        var report = Reopen(path); // no power-cut marker: it simply won't open

        Assert.Equal(HistoryOutcome.Repaired, report.Outcome);
        Assert.Equal("it wouldn't open", report.Why);
        Assert.Empty(report.Lost);
        Assert.Equal(minutes, Count(path, "system_minute"));
        Assert.Equal(60, Count(path, "sessions"));
        IntegrityOk(path);
    }

    [Fact]
    public void A_file_of_noise_starts_a_fresh_history_and_keeps_the_noise()
    {
        var dir = TestEnvironment.NewFolder("noise");
        var path = Path.Combine(dir, "rigsight.db");
        var noise = new byte[300 * 1024];
        new Random(7).NextBytes(noise);
        File.WriteAllBytes(path, noise);

        using (var db = HistoryRepair.Open(path, out var report, now: new DateTime(2026, 10, 18, 16, 47, 0)))
        {
            Assert.Equal(HistoryOutcome.NothingSaved, report.Outcome);
            Assert.Equal(noise, File.ReadAllBytes(Path.Combine(dir, report.DamagedCopy!)));
        }
        // The fresh history records as usual.
        Insert(path, "INSERT INTO system_minute(ts, active_sec) VALUES(1, 60)");
        Assert.Equal(1, Count(path, "system_minute"));
        IntegrityOk(path);
    }

    [Fact]
    public void A_damaged_files_leftover_journal_is_never_applied_to_the_repaired_one()
    {
        var path = History();
        Wipe(path, Leaves(path, "system_minute").Skip(10).Take(1));
        // The damaged file's journal, as a power cut leaves it: it belongs to the damaged file only.
        File.WriteAllBytes(path + "-wal", new byte[32 * 1024]);
        PowerCut(path);

        var report = Reopen(path);

        Assert.Equal(HistoryOutcome.Repaired, report.Outcome);
        // (SQLite itself throws away a journal that isn't valid as it opens the file; a valid one holds saved minutes and is applied.)
        IntegrityOk(path);
        Assert.True(Count(path, "system_minute") > Days * 1440 - 80);
    }

    // ── When the repair can't run ───────────────────────────────────────

    [Fact]
    public void Without_room_on_the_drive_nothing_is_touched_and_it_tries_again_next_time()
    {
        var path = History();
        Wipe(path, Leaves(path, "system_minute").Skip(20).Take(1));
        string damaged = Hash(path);
        PowerCut(path);

        var report = Reopen(path, free: _ => 1L << 20);

        Assert.Equal(HistoryOutcome.Waiting, report.Outcome);
        Assert.Contains("needs", report.WaitingReason);
        Assert.Equal(damaged, Hash(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "rigsight-damaged-*"));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, HistoryRepair.RepairMarker)));

        // Room again: the next start repairs it.
        var next = Reopen(path);
        Assert.Equal(HistoryOutcome.Repaired, next.Outcome);
        IntegrityOk(path);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, HistoryRepair.RepairMarker)));
    }

    [Fact]
    public async Task The_repair_waits_for_the_app_to_let_go_of_the_file()
    {
        var path = History();
        Wipe(path, Leaves(path, "system_minute").Skip(5).Take(1));
        PowerCut(path);
        // The app reading the file at that moment: it holds it open for a second and a half.
        var reader = Open(path, readOnly: true);
        var letGo = Task.Run(async () => { await Task.Delay(1500); reader.Dispose(); });

        var report = Reopen(path);
        await letGo;

        Assert.Equal(HistoryOutcome.Repaired, report.Outcome);
        IntegrityOk(path);
    }

    [Fact]
    public void A_repaired_history_is_used_as_usual_afterwards()
    {
        var path = History();
        Wipe(path, Leaves(path, "system_minute").Skip(30).Take(3));
        PowerCut(path);
        Reopen(path);

        // Opened again as on the next start (a normal one): no check, no second repair, and new minutes save.
        File.Delete(Path.Combine(Path.GetDirectoryName(path)!, HistoryRepair.RunningMarker));
        using (var db = HistoryRepair.Open(path, out var again))
        {
            Assert.Equal(HistoryOutcome.Fine, again.Outcome);
        }
        Insert(path, $"INSERT INTO system_minute(ts, active_sec) VALUES({T0 + Days * 86400L}, 60)");
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "rigsight-damaged-*.db"));
        IntegrityOk(path);
    }
}

/// <summary>What the sidebar card says for each state of the history file (only what needs a look, or lost something).</summary>
public sealed class HistoryCardTests
{
    [Fact]
    public void Nothing_is_shown_for_a_healthy_file_a_lossless_repair_or_rebuilt_lists()
    {
        Assert.Null(Rigsight.Models.HistoryCard.Of(new()));
        Assert.Null(Rigsight.Models.HistoryCard.Of(new() { CheckedAt = DateTime.Now, CheckedWhy = "after RigSight last closed suddenly" }));
        Assert.Null(Rigsight.Models.HistoryCard.Of(new() { Outcome = "whole", RepairedAt = DateTime.Now }));
        Assert.Null(Rigsight.Models.HistoryCard.Of(new() { Outcome = "lists", RepairedAt = DateTime.Now }));
    }

    [Fact]
    public void Each_state_gets_its_own_card_and_buttons()
    {
        var damaged = Rigsight.Models.HistoryCard.Of(new() { Damaged = true })!;
        Assert.Equal(("Part of your history can't be read", true, true, "Not now"), (damaged.Title, damaged.IsWarm, damaged.CanRepair, damaged.DismissText));

        var waiting = Rigsight.Models.HistoryCard.Of(new() { Damaged = true, Outcome = "waiting", WaitingReason = "Drive C: needs 40 MB free for the repair. It has 12 MB.", Dismissed = true })!;
        Assert.Equal(("History repair is waiting", true, false), (waiting.Title, waiting.CanRetry, waiting.CanDismiss)); // dismissing doesn't hide a waiting repair

        var repaired = Rigsight.Models.HistoryCard.Of(new() { Outcome = "repaired", Summary = "Kept 41 of 42 days in full." })!;
        Assert.Equal(("History repaired", false, true), (repaired.Title, repaired.IsWarm, repaired.CanOpenFolder));
        Assert.Null(Rigsight.Models.HistoryCard.Of(new() { Outcome = "repaired", Dismissed = true }));

        var nothing = Rigsight.Models.HistoryCard.Of(new() { Outcome = "nothing" })!;
        Assert.Equal(("Your history couldn't be saved", true), (nothing.Title, nothing.IsWarm));
    }

    [Fact]
    public void The_summary_names_what_was_kept_and_the_hours_lost()
    {
        var r = new HistoryReport
        {
            Outcome = HistoryOutcome.Repaired, Days = 42, DaysWhole = 41,
            Lost = [new LostSpan(new DateTime(2026, 10, 7, 15, 10, 0), new DateTime(2026, 10, 7, 18, 40, 0))],
        };
        Assert.Equal("Kept 41 of 42 days in full. 3:10 PM to 6:40 PM on Wed 7 Oct couldn't be read. The damaged file is kept.", HistoryRepair.Summary(r));
        Assert.True(HistoryRepair.LostAny(r));
        Assert.False(HistoryRepair.LostAny(r with { Lost = [] }));
    }
}
