using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Rigsight.Core.Data;

/// <summary>What became of the history file when the agent opened it.</summary>
public enum HistoryOutcome
{
    /// <summary>Nothing to do: not checked (a normal start) or checked and fine.</summary>
    Fine,
    /// <summary>Only the file's lookup lists (indexes) were damaged; rebuilt in place, no reading lost.</summary>
    ListsRebuilt,
    /// <summary>Damaged: what could be read was copied into a fresh file; the damaged one is kept beside it.</summary>
    Repaired,
    /// <summary>Damaged beyond reading: a fresh, empty history was started; the damaged file is kept.</summary>
    NothingSaved,
    /// <summary>Damaged, but the repair couldn't run (no room on the drive, the file held open): nothing was touched.</summary>
    Waiting,
}

/// <summary>Readings that couldn't be read, from just after the last good minute to just before the next one.</summary>
public sealed record LostSpan(DateTime? From, DateTime? To);

public sealed record HistoryReport
{
    public HistoryOutcome Outcome { get; init; }
    /// <summary>Why it was checked: "the agent didn't close normally last time", "a reading failed", "asked".</summary>
    public string? Why { get; init; }
    public string? DamagedCopy { get; init; }
    public long RowsKept { get; init; }
    /// <summary>Days with readings, from the first to the last (lost ones included).</summary>
    public int Days { get; init; }
    /// <summary>Of <see cref="Days"/>, those with nothing lost.</summary>
    public int DaysWhole { get; init; }
    public List<LostSpan> Lost { get; init; } = [];
    /// <summary>Tables that lost rows (or couldn't be read at all), for the log.</summary>
    public List<string> TablesHit { get; init; } = [];
    public string? WaitingReason { get; init; }
    public TimeSpan Took { get; init; }
}

/// <summary>The integrity check's answer: fine, or what's damaged, and whether that's only lookup lists.</summary>
public sealed record HistoryHealth(bool Ok, bool OnlyLists, IReadOnlyList<string> Problems)
{
    public static readonly HistoryHealth Fine = new(true, false, []);
}

/// <summary>
/// The history file checked and, when it really is damaged, repaired: nothing is ever deleted (the damaged file is kept
/// as rigsight-damaged-&lt;date&gt;.db) and everything that can still be read is copied into a fresh file.
/// <para>
/// One error is never taken for damage. Only SQLite saying the file itself is damaged ("malformed", "not a database")
/// starts a check, and only the check's answer counts. A busy file, a full drive or a drive that can't be read are
/// other errors, and never touch the file. The same as SQLite's own ".recover", without anyone installing a tool.
/// </para>
/// </summary>
public static partial class HistoryRepair
{
    private const int SqliteCorrupt = 11, SqliteNotADb = 26;
    private static readonly byte[] Magic = "SQLite format 3\0"u8.ToArray();

    /// <summary>Kept while the agent runs, removed when it closes normally: still there at start = power cut or crash.</summary>
    public const string RunningMarker = "agent.running";
    /// <summary>Left by an agent that found damage while running (a minute that wouldn't save): repair at the next start.</summary>
    public const string RepairMarker = "repair-history";

    /// <summary>A reading or a save that failed because the file itself is damaged, not for any other reason.</summary>
    public static bool IsDamage(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is SqliteException { SqliteErrorCode: SqliteCorrupt or SqliteNotADb }) return true;
        return false;
    }

    /// <summary>Raised (on whatever thread saw it) when a reading or save failed with <see cref="IsDamage"/>.</summary>
    public static event Action<Exception>? DamageSeen;

    /// <summary>Passes an error on to <see cref="DamageSeen"/> if it says the file is damaged; any other error is ignored here.</summary>
    public static void Report(Exception ex)
    {
        if (IsDamage(ex)) DamageSeen?.Invoke(ex);
    }

    // ── The check ────────────────────────────────────────────────────────

    /// <summary>
    /// SQLite's full integrity check. "Only lists" when every problem it names is in an index, which can be rebuilt
    /// from the tables without losing a reading.
    /// </summary>
    public static HistoryHealth Check(SqliteConnection conn)
    {
        var problems = new List<string>();
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA integrity_check(200)";
                using var r = cmd.ExecuteReader();
                // A row can hold several problems, a line each.
                while (r.Read()) problems.AddRange(r.GetString(0).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            if (problems is ["ok"]) return HistoryHealth.Fine;

            var indexes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name, rootpage FROM sqlite_master WHERE type = 'index'";
                using var r = cmd.ExecuteReader();
                while (r.Read()) indexes[r.GetString(0)] = r.IsDBNull(1) ? 0 : r.GetInt64(1);
            }
            var roots = indexes.Values.ToHashSet();
            // "*** in database main ***" heads the list: a title, not a problem.
            bool onlyLists = problems.Where(p => !p.StartsWith("***", StringComparison.Ordinal)).All(p =>
                IndexNamed().Match(p) is { Success: true } m && indexes.ContainsKey(m.Groups[1].Value)
                || TreeRoot().Match(p) is { Success: true } t && roots.Contains(long.Parse(t.Groups[1].Value)));
            return new HistoryHealth(false, onlyLists, problems);
        }
        catch (Exception ex) when (IsDamage(ex))
        {
            problems.Add(ex.Message);
            return new HistoryHealth(false, false, problems);
        }
    }

    [GeneratedRegex(@"\bindex (\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex IndexNamed();

    [GeneratedRegex(@"^Tree (\d+) page", RegexOptions.IgnoreCase)]
    private static partial Regex TreeRoot();

    /// <summary>Rebuilds every lookup list from its table (REINDEX), then checks again: true if the file is now fine.</summary>
    public static bool RebuildLists(SqliteConnection conn)
    {
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "REINDEX";
                cmd.ExecuteNonQuery();
            }
            return Check(conn).Ok;
        }
        catch (Exception ex) when (IsDamage(ex))
        {
            return false;
        }
    }

    // ── Opening, with the check and the repair ───────────────────────────

    /// <summary>
    /// Opens the history for writing as <see cref="RigsightDb.OpenWriter(string)"/> does, after checking it when the last
    /// run didn't end normally (or a repair was asked for), and repairing it when it really is damaged.
    /// </summary>
    /// <param name="freeBytes">Free space on the drive of a folder (a test's own, to play a full drive).</param>
    public static RigsightDb Open(string path, out HistoryReport report, Func<string, long>? freeBytes = null, DateTime? now = null)
    {
        var started = DateTime.Now;
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        bool unclean = File.Exists(Path.Combine(dir, RunningMarker));
        bool asked = File.Exists(Path.Combine(dir, RepairMarker));
        string why = asked ? AskedWhy(dir) : "the agent didn't close normally last time";

        RigsightDb? db = null;
        HistoryHealth health = HistoryHealth.Fine;
        try
        {
            db = RigsightDb.OpenWriter(path);
            if (unclean || asked) health = Check(db.Connection);
        }
        catch (Exception ex) when (IsDamage(ex))
        {
            db?.Dispose();
            db = null;
            why = "it wouldn't open";
            health = new HistoryHealth(false, false, [ex.Message]);
            Log.Write("history", $"Opening the history: {ex.Message}");
        }

        if (health.Ok)
        {
            Forget(dir, RepairMarker);
            if (unclean || asked) Log.Write("history", $"Checked the history file ({why}): it's fine ({(DateTime.Now - started).TotalSeconds:0.0} s)");
            report = new HistoryReport { Outcome = HistoryOutcome.Fine, Why = unclean || asked ? why : null };
            return db!;
        }

        Log.Write("history", $"The history file is damaged ({why}): {string.Join("; ", health.Problems.Take(6))}");
        if (db is not null && health.OnlyLists)
        {
            if (RebuildLists(db.Connection))
            {
                Forget(dir, RepairMarker);
                Log.Write("history", $"Only its lookup lists were damaged: rebuilt in {(DateTime.Now - started).TotalSeconds:0.0} s, no readings lost");
                report = new HistoryReport { Outcome = HistoryOutcome.ListsRebuilt, Why = why, Took = DateTime.Now - started };
                return db;
            }
            Log.Write("history", "Rebuilding the lookup lists didn't put it right: copying everything into a fresh file instead");
        }
        db?.Dispose();
        db = null;
        SqliteConnection.ClearAllPools();

        report = Repair(path, why, freeBytes ?? FreeBytes, now ?? DateTime.Now);
        if (report.Outcome == HistoryOutcome.Waiting)
        {
            Log.Write("history", $"Repair waiting: {report.WaitingReason}");
            AskRepair(dir); // tried again at the next start, and by the agent every 10 minutes meanwhile
            // The damaged file is used as it is meanwhile (what can be read still is); one that won't open at all can't be.
            return RigsightDb.OpenWriter(path);
        }
        Forget(dir, RepairMarker);
        return RigsightDb.OpenWriter(path);
    }

    /// <summary>The agent is running: a power cut or crash from here on leaves this behind for the next start to see.</summary>
    public static void MarkRunning(string dataDir)
    {
        try { File.WriteAllText(Path.Combine(dataDir, RunningMarker), DateTime.Now.ToString("O")); }
        catch (Exception ex) { Log.Write("history", $"The running marker couldn't be written: {ex.Message}"); }
    }

    /// <summary>The agent closed normally (or Windows is shutting down after everything was saved).</summary>
    public static void MarkStopped(string dataDir) => Forget(dataDir, RunningMarker);

    /// <summary>Damage found while running that needs the file closed to repair: done at the next start.</summary>
    /// <param name="why">Said in the log and in Settings: "you asked for a repair", "a reading failed to save".</param>
    public static void AskRepair(string dataDir, string why = "a reading failed to save")
    {
        try { File.WriteAllText(Path.Combine(dataDir, RepairMarker), why); }
        catch (Exception ex) { Log.Write("history", $"The repair marker couldn't be written: {ex.Message}"); }
    }

    private static string AskedWhy(string dir)
    {
        try
        {
            var text = File.ReadAllText(Path.Combine(dir, RepairMarker)).Trim();
            return text.Length is > 0 and < 80 && !char.IsDigit(text[0]) ? text : "a reading failed to save";
        }
        catch (Exception) { return "a reading failed to save"; }
    }

    private static void Forget(string dir, string marker)
    {
        try { File.Delete(Path.Combine(dir, marker)); } catch { }
    }

    private static long FreeBytes(string dir)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace; }
        catch { return long.MaxValue; } // unknown: try, and let the copy itself fail if there's really no room
    }

    // ── The repair ───────────────────────────────────────────────────────

    private static readonly string[] Sides = ["", "-wal", "-shm"];

    /// <summary>
    /// The damaged file is copied aside untouched, a working copy of it is read table by table into a fresh history
    /// (skipping only what can't be read), and the fresh file takes its place.
    /// </summary>
    internal static HistoryReport Repair(string path, string why, Func<string, long> freeBytes, DateTime now)
    {
        var started = DateTime.Now;
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        long size = Sides.Sum(s => File.Exists(path + s) ? new FileInfo(path + s).Length : 0);
        long need = size * 3 + (8 << 20); // the kept copy, the working copy and the fresh file, with room to spare
        long free = freeBytes(dir);
        if (free < need)
            return new HistoryReport
            {
                Outcome = HistoryOutcome.Waiting, Why = why,
                WaitingReason = $"Drive {Path.GetPathRoot(Path.GetFullPath(dir))?.TrimEnd('\\')} needs {Mb(need)} free for the repair. It has {Mb(free)}.",
            };

        string kept = Unique(Path.Combine(dir, $"rigsight-damaged-{now:yyyy-MM-dd-HHmm}.db"));
        string work = Path.Combine(dir, "rigsight-repair-work.db");
        string fresh = Path.Combine(dir, "rigsight-repair-new.db");
        foreach (var s in Sides) { TryDelete(work + s); TryDelete(fresh + s); }

        try
        {
            // The damaged file exactly as it is, journal and all, kept for good; the repair reads its own copy.
            foreach (var s in Sides.Where(s => File.Exists(path + s)))
            {
                File.Copy(path + s, kept + s);
                File.Copy(path + s, work + s);
            }
            FixHeader(work);

            var copy = new CopyResult();
            using (var target = RigsightDb.OpenWriter(fresh))
            {
                try
                {
                    using var source = new SqliteConnection($"Data Source={work};Mode=ReadWrite;Pooling=False");
                    source.Open();
                    CopyAll(source, target.Connection, copy);
                }
                catch (Exception ex)
                {
                    copy.TablesHit.Add($"the file ({ex.Message})");
                    Log.Write("history", $"The damaged file couldn't be read at all: {ex.Message}");
                }
                var after = Check(target.Connection);
                if (!after.Ok) throw new InvalidOperationException("the fresh history failed its own check: " + string.Join("; ", after.Problems.Take(3)));
                using var cmd = target.Connection.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            if (!Swap(path, fresh, out var holdUp))
            {
                foreach (var s in Sides) { TryDelete(fresh + s); TryDelete(kept + s); }
                return new HistoryReport { Outcome = HistoryOutcome.Waiting, Why = why, WaitingReason = holdUp };
            }

            var (days, whole) = DaysOf(path, copy.Lost);
            var report = new HistoryReport
            {
                Outcome = copy.Rows == 0 ? HistoryOutcome.NothingSaved : HistoryOutcome.Repaired,
                Why = why,
                DamagedCopy = Path.GetFileName(kept),
                RowsKept = copy.Rows,
                Days = days,
                DaysWhole = whole,
                Lost = copy.Lost,
                TablesHit = copy.TablesHit,
                Took = DateTime.Now - started,
            };
            Log.Write("history", (copy.Rows == 0 ? $"Nothing could be read from the history ({why}): started a fresh one" : $"Repaired the history ({why}): kept {copy.Rows:N0} rows, {whole} of {days} days in full")
                + (copy.Lost.Count > 0 ? $"; lost {string.Join(", ", copy.Lost.Select(Describe))}" : "")
                + (copy.TablesHit.Count > 0 ? $"; rows lost in {string.Join(", ", copy.TablesHit)}" : "")
                + $". The damaged file is kept as {Path.GetFileName(kept)} ({report.Took.TotalSeconds:0.0} s)");
            return report;
        }
        catch (Exception ex)
        {
            // Whatever went wrong, the original is where it was: the repair only ever replaces it at the very end.
            Log.Write("history", $"The repair didn't finish, nothing was changed: {ex}");
            foreach (var s in Sides) TryDelete(fresh + s);
            return new HistoryReport { Outcome = HistoryOutcome.Waiting, Why = why, WaitingReason = "The repair didn't finish: " + ex.Message };
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var s in Sides) TryDelete(work + s);
        }
    }

    /// <summary>
    /// The fresh file in place of the damaged one. The damaged file's journal goes first (left beside the fresh file,
    /// SQLite would apply it to it). The app may hold the file open for a moment while it reads: tried for 30 s.
    /// </summary>
    private static bool Swap(string path, string fresh, out string? holdUp)
    {
        holdUp = null;
        var until = DateTime.Now.AddSeconds(30);
        while (true)
        {
            try
            {
                foreach (var s in new[] { "-wal", "-shm" }) if (File.Exists(path + s)) File.Delete(path + s);
                File.Move(fresh, path, overwrite: true);
                foreach (var s in new[] { "-wal", "-shm" }) TryDelete(fresh + s);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (DateTime.Now > until)
                {
                    holdUp = $"The history file stayed in use: {ex.Message}";
                    return false;
                }
                Thread.Sleep(250);
            }
        }
    }

    /// <summary>A file whose first bytes no longer say "SQLite format 3" is otherwise often whole: the header is put back.</summary>
    private static void FixHeader(string file)
    {
        try
        {
            using var f = new FileStream(file, FileMode.Open, FileAccess.ReadWrite);
            if (f.Length < 100) return;
            var head = new byte[100];
            f.ReadExactly(head);
            bool magicOk = head.AsSpan(0, 16).SequenceEqual(Magic);
            int pageSize = head[16] << 8 | head[17];
            bool sizeOk = pageSize == 1 || pageSize >= 512 && (pageSize & (pageSize - 1)) == 0;
            if (magicOk && sizeOk) return;
            f.Position = 0;
            f.Write(Magic);
            if (!sizeOk) { f.Position = 16; f.Write([0x10, 0x00]); } // 4096, the size RigSight's files use
            Log.Write("history", $"The file's header was damaged: put back ({(magicOk ? "" : "its name")}{(magicOk || sizeOk ? "" : " and ")}{(sizeOk ? "" : "its page size")})");
        }
        catch (IOException) { }
    }

    private sealed class CopyResult
    {
        public long Rows;
        public List<LostSpan> Lost { get; } = [];
        public List<string> TablesHit { get; } = [];
    }

    private sealed record Column(string Name, int Pk, string Type);

    private static List<Column> Columns(SqliteConnection conn, string table)
    {
        var list = new List<Column>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new Column(r.GetString(1), r.GetInt32(5), r.IsDBNull(2) ? "" : r.GetString(2)));
        return list;
    }

    /// <summary>Every table the fresh history has, from the damaged one, with the columns both have.</summary>
    private static void CopyAll(SqliteConnection source, SqliteConnection target, CopyResult result)
    {
        var tables = new List<(string Name, string Sql)>();
        using (var cmd = target.CreateCommand())
        {
            cmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
            using var r = cmd.ExecuteReader();
            while (r.Read()) tables.Add((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1)));
        }
        // The damaged file's own list of tables; that list itself unreadable means nothing in it can be found.
        var there = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = source.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            using var r = cmd.ExecuteReader();
            while (r.Read()) there.Add(r.GetString(0));
        }

        foreach (var (table, sql) in tables.Where(t => there.Contains(t.Name)))
        {
            try { CopyTable(source, target, table, sql.Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase), result); }
            catch (Exception ex)
            {
                result.TablesHit.Add($"{table} (not read: {ex.Message})");
            }
        }
    }

    /// <summary>
    /// One table in key order, a batch at a time. Where a page can't be read, the rows after it are found again by
    /// probing further and further along the key, so one damaged page costs only its own rows.
    /// </summary>
    private static void CopyTable(SqliteConnection source, SqliteConnection target, string table, bool withoutRowid, CopyResult result)
    {
        var mine = Columns(target, table);
        var theirs = Columns(source, table).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cols = mine.Where(c => theirs.Contains(c.Name)).Select(c => c.Name).ToList();
        if (cols.Count == 0) return;

        var pk = mine.Where(c => c.Pk > 0).OrderBy(c => c.Pk).ToList();
        bool rowidAlias = !withoutRowid && pk.Count == 1 && pk[0].Type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase);
        // The order rows are read in: the rowid (named, or the table's own), else the primary key.
        List<string> key = withoutRowid ? [.. pk.Select(c => c.Name)] : rowidAlias ? [pk[0].Name] : ["rowid"];
        var select = (key.Contains("rowid") ? ["rowid"] : new List<string>()).Concat(cols).ToList();
        string Q(string c) => c == "rowid" ? "rowid" : $"\"{c}\"";
        string keyList = string.Join(", ", key.Select(Q));
        string selectList = string.Join(", ", select.Select(Q));
        int[] keyAt = [.. key.Select(k => select.FindIndex(s => s.Equals(k, StringComparison.OrdinalIgnoreCase)))];

        using var tx = target.BeginTransaction();
        using var insert = target.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = $"INSERT OR REPLACE INTO \"{table}\" ({selectList}) VALUES ({string.Join(", ", select.Select((_, i) => "$p" + i))})";
        var ps = select.Select((_, i) => insert.Parameters.Add(new SqliteParameter("$p" + i, null))).ToArray();

        object[]? last = null;   // the key of the last row read
        bool inclusive = false;  // after a skip: start at the row the probe found
        long lost = 0;
        bool hit = false;
        bool isMinutes = table == "system_minute";
        while (true)
        {
            int got = 0;
            try
            {
                using var cmd = source.CreateCommand();
                string where = last is null ? "" : $" WHERE ({keyList}) {(inclusive ? ">=" : ">")} ({string.Join(", ", key.Select((_, i) => "$k" + i))})";
                cmd.CommandText = $"SELECT {selectList} FROM \"{table}\"{where} ORDER BY {keyList} LIMIT 2000";
                if (last is not null) for (int i = 0; i < key.Count; i++) cmd.Parameters.AddWithValue("$k" + i, last[i]);
                using var r = cmd.ExecuteReader();
                inclusive = false;
                while (r.Read())
                {
                    for (int i = 0; i < select.Count; i++) ps[i].Value = r.IsDBNull(i) ? DBNull.Value : r.GetValue(i);
                    insert.ExecuteNonQuery();
                    last = [.. keyAt.Select(i => r.GetValue(i))];
                    got++;
                    result.Rows++;
                }
                if (got < 2000) break; // the end of the table
            }
            catch (Exception ex) when (IsDamage(ex))
            {
                hit = true;
                // Where the readable rows start again, along the first key column (a time, a day, an id).
                var gapFrom = last;
                var found = ProbePast(source, table, key, keyList, last);
                if (isMinutes)
                    result.Lost.Add(new LostSpan(gapFrom is null ? null : TimeUtil.FromUnix(Convert.ToInt64(gapFrom[0]) + 60),
                        found is null ? null : TimeUtil.FromUnix(Convert.ToInt64(found[0]))));
                if (found is null) break;
                if (gapFrom is not null && found[0] is long a && gapFrom[0] is long b) lost += Math.Max(0, (a - b) / (isMinutes ? 60 : 1) - 1);
                last = found;
                inclusive = true;
            }
        }
        tx.Commit();
        if (hit) result.TablesHit.Add(isMinutes && lost > 0 ? $"{table} (about {lost:N0} minutes)" : table);
    }

    /// <summary>
    /// The first readable row after <paramref name="last"/>: probes 1, 2, 4… further along the first key column until a
    /// read works. Null when nothing readable follows (or the key isn't a number to step along).
    /// </summary>
    private static object[]? ProbePast(SqliteConnection source, string table, List<string> key, string keyList, object[]? last)
    {
        string first = key[0] == "rowid" ? "rowid" : $"\"{key[0]}\"";
        object[]? Next(long after)
        {
            using var cmd = source.CreateCommand();
            cmd.CommandText = $"SELECT {keyList} FROM \"{table}\" WHERE {first} > $at ORDER BY {keyList} LIMIT 1";
            cmd.Parameters.AddWithValue("$at", after);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return [];
            var row = new object[key.Count];
            for (int i = 0; i < key.Count; i++) row[i] = r.GetValue(i);
            return row;
        }

        // Whether a read from just after a key works: it fails while the key still points into the damaged page.
        bool Works(long after, out object[]? row)
        {
            try { row = Next(after); return true; }
            catch (Exception ex) when (IsDamage(ex)) { row = null; return false; }
        }
        // The smallest key in (bad, good] whose read works, halving the gap: the edge of the damaged page, to the second.
        object[]? Edge(long bad, long good, object[]? goodRow)
        {
            while (good - bad > 1)
            {
                long mid = bad + (good - bad) / 2;
                if (Works(mid, out var row)) { good = mid; goodRow = row; }
                else bad = mid;
            }
            return goodRow is { Length: > 0 } ? goodRow : null;
        }

        if (last is null)
        {
            // The table's very start is damaged: from its last row (the other end of it) back, doubling the step until a
            // read fails, then the edge between the two.
            long end;
            try
            {
                using var cmd = source.CreateCommand();
                cmd.CommandText = $"SELECT {first} FROM \"{table}\" ORDER BY {first} DESC LIMIT 1";
                if (cmd.ExecuteScalar() is not long e) return null;
                end = e;
            }
            catch (Exception ex) when (IsDamage(ex)) { return null; }
            long good = end - 1;
            if (!Works(good, out var goodRow)) return null;
            for (int k = 0; k <= 50; k++)
            {
                long at = end - 1 - (1L << k);
                if (at > good) break; // stepped past the smallest number
                if (Works(at, out var row)) { good = at; goodRow = row; continue; }
                return Edge(at, good, goodRow);
            }
            return goodRow is { Length: > 0 } ? goodRow : null;
        }

        if (last[0] is not long from) return null; // a text key (inventory): what follows can't be stepped to, and it's read afresh anyway
        long failed = from;
        for (long step = 1; step > 0 && step <= 1L << 50; step *= 2)
        {
            long at = from + step;
            if (at < from) break;
            if (Works(at, out var row)) return Edge(failed, at, row); // the first read that works, then back to the edge
            failed = at;
        }
        return null;
    }

    /// <summary>Days with readings, first to last, and how many of them lost nothing.</summary>
    private static (int Days, int Whole) DaysOf(string path, List<LostSpan> lost)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            conn.Open();
            var days = new HashSet<DateTime>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT ts FROM system_minute";
                using var r = cmd.ExecuteReader();
                while (r.Read()) days.Add(TimeUtil.FromUnix(r.GetInt64(0)).Date);
            }
            var hit = new HashSet<DateTime>();
            foreach (var span in lost)
            {
                var a = (span.From ?? span.To)?.Date;
                var b = (span.To ?? span.From)?.Date;
                if (a is null || b is null) continue;
                for (var d = a.Value; d <= b.Value; d = d.AddDays(1)) { days.Add(d); hit.Add(d); }
            }
            return (days.Count, days.Count - hit.Count);
        }
        catch (Exception) { return (0, 0); }
    }

    /// <summary>What the card says after a repair: what was kept, and the hours that couldn't be read.</summary>
    public static string Summary(HistoryReport r)
    {
        if (r.Outcome == HistoryOutcome.NothingSaved)
            return "The file was too damaged to read. RigSight started a new history today. The old file is kept in case a tool can read it.";
        string kept = r.Days > 0 ? $"Kept {r.DaysWhole} of {r.Days} days in full. " : "";
        string lost = r.Lost.Count switch
        {
            0 when r.TablesHit.Count > 0 => "A few app readings couldn't be read. ",
            0 => "Nothing was lost. ",
            1 => $"{Capital(Describe(r.Lost[0]))} couldn't be read. ",
            _ => $"{Capital(Describe(r.Lost[0]))} and {r.Lost.Count - 1} more stretch{(r.Lost.Count > 2 ? "es" : "")} couldn't be read. ",
        };
        return kept + lost + "The damaged file is kept.";
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Whether something was lost, so the repair is worth a card (a repair that lost nothing is only logged).</summary>
    public static bool LostAny(HistoryReport r) => r.Outcome == HistoryOutcome.NothingSaved || r.Lost.Count > 0 || r.TablesHit.Count > 0;

    /// <summary>Room on the drive for a repair of the file at <paramref name="path"/> now (a waiting repair tries again when there is).</summary>
    public static bool RoomFor(string path)
    {
        long size = Sides.Sum(s => File.Exists(path + s) ? new FileInfo(path + s).Length : 0);
        return FreeBytes(Path.GetDirectoryName(Path.GetFullPath(path))!) >= size * 3 + (8 << 20);
    }

    /// <summary>A lost span as the log and the card say it: "3:10 PM to 6:40 PM on Tue 7 Oct".</summary>
    public static string Describe(LostSpan s) => (s.From, s.To) switch
    {
        ({ } a, { } b) when a.Date == b.Date => $"{TimeText.At(a, "h:mm tt")} to {TimeText.At(b, "h:mm tt")} on {a:ddd d MMM}",
        ({ } a, { } b) => $"{TimeText.At(a, "ddd d MMM, h:mm tt")} to {TimeText.At(b, "ddd d MMM, h:mm tt")}",
        ({ } a, null) => $"everything after {TimeText.At(a, "ddd d MMM, h:mm tt")}",
        (null, { } b) => $"everything before {TimeText.At(b, "ddd d MMM, h:mm tt")}",
        _ => "some readings",
    };

    private static string Mb(long bytes) => $"{Math.Max(1, (bytes + (1 << 20) - 1) >> 20)} MB";

    private static string Unique(string file)
    {
        if (!File.Exists(file)) return file;
        for (int i = 2; ; i++)
        {
            var next = Path.Combine(Path.GetDirectoryName(file)!, $"{Path.GetFileNameWithoutExtension(file)}-{i}.db");
            if (!File.Exists(next)) return next;
        }
    }

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { }
    }
}
