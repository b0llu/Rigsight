using System.Data;
using Microsoft.Data.Sqlite;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Data;

/// <summary>
/// SQLite store in %LocalAppData%\Rigsight\rigsight.db. The agent opens it read-write and is the
/// only writer; the app opens it read-only. WAL mode lets both work at the same time.
/// </summary>
public sealed class RigsightDb : IDisposable
{
    private const int SchemaVersion = 1;
    private readonly SqliteConnection _conn;

    private RigsightDb(SqliteConnection conn) => _conn = conn;

    public static RigsightDb OpenWriter()
    {
        Directory.CreateDirectory(RigsightPaths.DataDir);
        var conn = new SqliteConnection($"Data Source={RigsightPaths.Database};Mode=ReadWriteCreate;Pooling=False");
        conn.Open();
        var db = new RigsightDb(conn);
        db.Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;");
        db.EnsureSchema();
        return db;
    }

    /// <summary>Opens the database for reading, or returns null if the agent hasn't created it yet.</summary>
    public static RigsightDb? OpenReader()
    {
        if (!File.Exists(RigsightPaths.Database)) return null;
        try
        {
            var conn = new SqliteConnection($"Data Source={RigsightPaths.Database};Mode=ReadOnly;Pooling=False");
            conn.Open();
            return new RigsightDb(conn);
        }
        catch (Exception ex)
        {
            Log.Error("db", ex);
            return null;
        }
    }

    private void EnsureSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS apps(
                id INTEGER PRIMARY KEY, exe TEXT NOT NULL UNIQUE COLLATE NOCASE, name TEXT NOT NULL,
                path TEXT, category TEXT NOT NULL, first_seen INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS system_minute(
                ts INTEGER PRIMARY KEY, cpu_temp REAL, cpu_temp_max REAL, gpu_temp REAL, gpu_temp_max REAL, gpu_hot_max REAL,
                cpu_load REAL, gpu_load REAL, cpu_power REAL, gpu_power REAL, cpu_volt_max REAL, gpu_volt_max REAL,
                ram_used REAL, fg_app INTEGER, active_sec INTEGER NOT NULL DEFAULT 0, idle_sec INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS app_hour(
                ts INTEGER NOT NULL, app_id INTEGER NOT NULL,
                fg_sec REAL NOT NULL DEFAULT 0, idle_sec REAL NOT NULL DEFAULT 0, bg_sec REAL NOT NULL DEFAULT 0, min_sec REAL NOT NULL DEFAULT 0,
                cpu_sum REAL NOT NULL DEFAULT 0, cpu_n INTEGER NOT NULL DEFAULT 0, cpu_max REAL,
                mem_sum REAL NOT NULL DEFAULT 0, mem_n INTEGER NOT NULL DEFAULT 0, mem_max REAL,
                cpu_temp_sum REAL NOT NULL DEFAULT 0, cpu_temp_n INTEGER NOT NULL DEFAULT 0, cpu_temp_max REAL,
                gpu_temp_sum REAL NOT NULL DEFAULT 0, gpu_temp_n INTEGER NOT NULL DEFAULT 0, gpu_temp_max REAL, gpu_hot_max REAL,
                cpu_power_max REAL, gpu_power_max REAL, cpu_volt_max REAL, gpu_volt_max REAL,
                gpu_load_sum REAL NOT NULL DEFAULT 0, gpu_load_n INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(ts, app_id));
            CREATE TABLE IF NOT EXISTS sessions(
                id INTEGER PRIMARY KEY, app_id INTEGER NOT NULL, start INTEGER NOT NULL, end INTEGER NOT NULL,
                active_sec REAL NOT NULL, cpu_temp_max REAL, gpu_temp_max REAL, is_game INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_sessions_start ON sessions(start);
            CREATE TABLE IF NOT EXISTS crashes(
                id INTEGER PRIMARY KEY, ts INTEGER NOT NULL, kind TEXT NOT NULL, app_exe TEXT NOT NULL DEFAULT '',
                app_path TEXT, module TEXT, code TEXT, detail TEXT, during_sleep INTEGER NOT NULL DEFAULT 0,
                UNIQUE(kind, ts, app_exe));
            CREATE TABLE IF NOT EXISTS drive_day(
                day INTEGER NOT NULL, drive TEXT NOT NULL, used_gb REAL NOT NULL, total_gb REAL NOT NULL, PRIMARY KEY(day, drive));
            """);
        Exec($"INSERT OR IGNORE INTO meta(key, value) VALUES('schema', '{SchemaVersion}')");

        // Columns added after the first release.
        if (!HasColumn("system_minute", "gpu_mem_max")) Exec("ALTER TABLE system_minute ADD COLUMN gpu_mem_max REAL");
    }

    private bool HasColumn(string table, string column)
    {
        using var cmd = Cmd($"SELECT count(*) FROM pragma_table_info('{table}') WHERE name = $c", ("$c", column));
        return cmd.ExecuteScalar() is long n && n > 0;
    }

    public string? GetMeta(string key)
    {
        using var cmd = Cmd("SELECT value FROM meta WHERE key = $k", ("$k", key));
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string value)
    {
        using var cmd = Cmd("INSERT OR REPLACE INTO meta(key, value) VALUES($k, $v)", ("$k", key), ("$v", value));
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();

    public SqliteTransaction BeginTransaction() => _conn.BeginTransaction();

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private SqliteCommand Cmd(string sql, params (string Name, object? Value)[] args)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    // ── Writer ────────────────────────────────────────────────────────────

    public List<AppRow> LoadApps()
    {
        using var cmd = Cmd("SELECT id, exe, name, path, category, first_seen FROM apps");
        using var r = cmd.ExecuteReader();
        var list = new List<AppRow>();
        while (r.Read())
        {
            list.Add(new AppRow
            {
                Id = r.GetInt64(0),
                Exe = r.GetString(1),
                Name = r.GetString(2),
                Path = r.IsDBNull(3) ? null : r.GetString(3),
                Category = Enum.TryParse<AppCategory>(r.GetString(4), out var c) ? c : AppCategory.Other,
                FirstSeen = r.GetInt64(5),
            });
        }
        return list;
    }

    public long UpsertApp(string exe, string name, string? path, AppCategory category)
    {
        using var cmd = Cmd("""
            INSERT INTO apps(exe, name, path, category, first_seen) VALUES($exe, $name, $path, $cat, $now)
            ON CONFLICT(exe) DO UPDATE SET name = excluded.name, path = coalesce(excluded.path, apps.path), category = excluded.category
            RETURNING id
            """, ("$exe", exe), ("$name", name), ("$path", path), ("$cat", category.ToString()), ("$now", TimeUtil.NowUnix()));
        return (long)cmd.ExecuteScalar()!;
    }

    public void WriteMinute(SystemMinute m)
    {
        using var cmd = Cmd("""
            INSERT OR REPLACE INTO system_minute(ts, cpu_temp, cpu_temp_max, gpu_temp, gpu_temp_max, gpu_hot_max, gpu_mem_max, cpu_load, gpu_load,
                cpu_power, gpu_power, cpu_volt_max, gpu_volt_max, ram_used, fg_app, active_sec, idle_sec)
            VALUES($ts, $ct, $ctm, $gt, $gtm, $gh, $gm, $cl, $gl, $cp, $gp, $cv, $gv, $ram, $fg, $act, $idle)
            """,
            ("$ts", m.Ts), ("$ct", m.CpuTemp), ("$ctm", m.CpuTempMax), ("$gt", m.GpuTemp), ("$gtm", m.GpuTempMax),
            ("$gh", m.GpuHotMax), ("$gm", m.GpuMemMax), ("$cl", m.CpuLoad), ("$gl", m.GpuLoad), ("$cp", m.CpuPower), ("$gp", m.GpuPower),
            ("$cv", m.CpuVoltMax), ("$gv", m.GpuVoltMax), ("$ram", m.RamUsed), ("$fg", m.FgApp),
            ("$act", m.ActiveSec), ("$idle", m.IdleSec));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Adds per-app deltas into their hour buckets (sums add up, maxima take the larger value).</summary>
    public void AddAppHour(AppHour h)
    {
        static string Max(string col) => $"{col} = max(coalesce({col}, excluded.{col}), coalesce(excluded.{col}, {col}))";
        static string Sum(string col) => $"{col} = {col} + excluded.{col}";

        using var cmd = Cmd($"""
            INSERT INTO app_hour(ts, app_id, fg_sec, idle_sec, bg_sec, min_sec, cpu_sum, cpu_n, cpu_max, mem_sum, mem_n, mem_max,
                cpu_temp_sum, cpu_temp_n, cpu_temp_max, gpu_temp_sum, gpu_temp_n, gpu_temp_max, gpu_hot_max,
                cpu_power_max, gpu_power_max, cpu_volt_max, gpu_volt_max, gpu_load_sum, gpu_load_n)
            VALUES($ts, $app, $fg, $idle, $bg, $min, $cs, $cn, $cm, $ms, $mn, $mm, $cts, $ctn, $ctm, $gts, $gtn, $gtm, $ghm,
                $cpm, $gpm, $cvm, $gvm, $gls, $gln)
            ON CONFLICT(ts, app_id) DO UPDATE SET
                {Sum("fg_sec")}, {Sum("idle_sec")}, {Sum("bg_sec")}, {Sum("min_sec")},
                {Sum("cpu_sum")}, {Sum("cpu_n")}, {Max("cpu_max")}, {Sum("mem_sum")}, {Sum("mem_n")}, {Max("mem_max")},
                {Sum("cpu_temp_sum")}, {Sum("cpu_temp_n")}, {Max("cpu_temp_max")},
                {Sum("gpu_temp_sum")}, {Sum("gpu_temp_n")}, {Max("gpu_temp_max")}, {Max("gpu_hot_max")},
                {Max("cpu_power_max")}, {Max("gpu_power_max")}, {Max("cpu_volt_max")}, {Max("gpu_volt_max")},
                {Sum("gpu_load_sum")}, {Sum("gpu_load_n")}
            """,
            ("$ts", h.Ts), ("$app", h.AppId), ("$fg", h.FgSec), ("$idle", h.IdleSec), ("$bg", h.BgSec), ("$min", h.MinSec),
            ("$cs", h.CpuSum), ("$cn", h.CpuN), ("$cm", h.CpuMax), ("$ms", h.MemSum), ("$mn", h.MemN), ("$mm", h.MemMax),
            ("$cts", h.CpuTempSum), ("$ctn", h.CpuTempN), ("$ctm", h.CpuTempMax),
            ("$gts", h.GpuTempSum), ("$gtn", h.GpuTempN), ("$gtm", h.GpuTempMax), ("$ghm", h.GpuHotMax),
            ("$cpm", h.CpuPowerMax), ("$gpm", h.GpuPowerMax), ("$cvm", h.CpuVoltMax), ("$gvm", h.GpuVoltMax),
            ("$gls", h.GpuLoadSum), ("$gln", h.GpuLoadN));
        cmd.ExecuteNonQuery();
    }

    public void InsertSession(SessionRow s)
    {
        using var cmd = Cmd("""
            INSERT INTO sessions(app_id, start, end, active_sec, cpu_temp_max, gpu_temp_max, is_game)
            VALUES($app, $start, $end, $act, $ct, $gt, $game)
            """, ("$app", s.AppId), ("$start", s.Start), ("$end", s.End), ("$act", s.ActiveSec),
            ("$ct", s.CpuTempMax), ("$gt", s.GpuTempMax), ("$game", s.IsGame ? 1 : 0));
        cmd.ExecuteNonQuery();
    }

    public void UpsertDriveDay(DriveDay d)
    {
        using var cmd = Cmd("INSERT OR REPLACE INTO drive_day(day, drive, used_gb, total_gb) VALUES($d, $drive, $u, $t)",
            ("$d", d.Day), ("$drive", d.Drive), ("$u", d.UsedGb), ("$t", d.TotalGb));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Stores crashes; ones already recorded are ignored. Returns the newly added ones.</summary>
    public List<CrashEvent> InsertCrashes(IEnumerable<CrashEvent> crashes)
    {
        var added = new List<CrashEvent>();
        foreach (var e in crashes)
        {
            using var cmd = Cmd("""
                INSERT OR IGNORE INTO crashes(ts, kind, app_exe, app_path, module, code, detail, during_sleep)
                VALUES($ts, $kind, $exe, $path, $module, $code, $detail, $sleep)
                """, ("$ts", e.Ts), ("$kind", e.Kind.ToString()), ("$exe", e.AppExe ?? ""), ("$path", e.AppPath),
                ("$module", e.Module), ("$code", e.Code), ("$detail", e.Detail), ("$sleep", e.DuringSleep ? 1 : 0));
            if (cmd.ExecuteNonQuery() > 0) added.Add(e);
        }
        return added;
    }

    public void Prune(long detailedBefore, long historyBefore)
    {
        using (var c1 = Cmd("DELETE FROM system_minute WHERE ts < $t", ("$t", detailedBefore))) c1.ExecuteNonQuery();
        using (var c2 = Cmd("DELETE FROM app_hour WHERE ts < $t", ("$t", historyBefore))) c2.ExecuteNonQuery();
        using (var c3 = Cmd("DELETE FROM sessions WHERE start < $t", ("$t", historyBefore))) c3.ExecuteNonQuery();
        using (var c4 = Cmd("DELETE FROM drive_day WHERE day < $t", ("$t", historyBefore))) c4.ExecuteNonQuery();
        using (var c5 = Cmd("DELETE FROM crashes WHERE ts < $t", ("$t", historyBefore))) c5.ExecuteNonQuery();
    }

    public void ClearHistory()
    {
        Exec("DELETE FROM system_minute; DELETE FROM app_hour; DELETE FROM sessions; DELETE FROM drive_day; DELETE FROM crashes;");
        Exec("VACUUM");
    }

    // ── Reader ────────────────────────────────────────────────────────────

    /// <summary>When recording started: the oldest minute or hourly app total (minutes are kept for less time than app totals).</summary>
    public long? FirstDataTime()
    {
        using var cmd = Cmd("SELECT min(t) FROM (SELECT min(ts) AS t FROM system_minute UNION ALL SELECT min(ts) FROM app_hour)");
        return cmd.ExecuteScalar() is long v ? v : null;
    }

    // Whether system_minute has gpu_mem_max yet (added in 0.4.5; the agent adds it, the app may read first).
    private bool? _hasGpuMem;

    public List<SystemMinute> GetMinutes(long from, long to)
    {
        _hasGpuMem ??= HasColumn("system_minute", "gpu_mem_max");
        using var cmd = Cmd($"""
            SELECT ts, cpu_temp, cpu_temp_max, gpu_temp, gpu_temp_max, gpu_hot_max, cpu_load, gpu_load, cpu_power, gpu_power,
                   cpu_volt_max, gpu_volt_max, ram_used, fg_app, active_sec, idle_sec, {(_hasGpuMem.Value ? "gpu_mem_max" : "NULL")}
            FROM system_minute WHERE ts >= $from AND ts < $to ORDER BY ts
            """, ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        var list = new List<SystemMinute>();
        while (r.Read())
        {
            list.Add(new SystemMinute
            {
                Ts = r.GetInt64(0),
                CpuTemp = D(r, 1), CpuTempMax = D(r, 2), GpuTemp = D(r, 3), GpuTempMax = D(r, 4), GpuHotMax = D(r, 5),
                CpuLoad = D(r, 6), GpuLoad = D(r, 7), CpuPower = D(r, 8), GpuPower = D(r, 9),
                CpuVoltMax = D(r, 10), GpuVoltMax = D(r, 11), RamUsed = D(r, 12),
                FgApp = r.IsDBNull(13) ? null : r.GetInt64(13),
                ActiveSec = r.GetInt32(14), IdleSec = r.GetInt32(15), GpuMemMax = D(r, 16),
            });
        }
        return list;
    }

    public List<AppHour> GetAppHours(long from, long to)
    {
        using var cmd = Cmd("""
            SELECT ts, app_id, fg_sec, idle_sec, bg_sec, min_sec, cpu_sum, cpu_n, cpu_max, mem_sum, mem_n, mem_max,
                   cpu_temp_sum, cpu_temp_n, cpu_temp_max, gpu_temp_sum, gpu_temp_n, gpu_temp_max, gpu_hot_max,
                   cpu_power_max, gpu_power_max, cpu_volt_max, gpu_volt_max, gpu_load_sum, gpu_load_n
            FROM app_hour WHERE ts >= $from AND ts < $to
            """, ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        var list = new List<AppHour>();
        while (r.Read())
        {
            list.Add(new AppHour
            {
                Ts = r.GetInt64(0), AppId = r.GetInt64(1),
                FgSec = r.GetDouble(2), IdleSec = r.GetDouble(3), BgSec = r.GetDouble(4), MinSec = r.GetDouble(5),
                CpuSum = r.GetDouble(6), CpuN = r.GetInt32(7), CpuMax = D(r, 8),
                MemSum = r.GetDouble(9), MemN = r.GetInt32(10), MemMax = D(r, 11),
                CpuTempSum = r.GetDouble(12), CpuTempN = r.GetInt32(13), CpuTempMax = D(r, 14),
                GpuTempSum = r.GetDouble(15), GpuTempN = r.GetInt32(16), GpuTempMax = D(r, 17), GpuHotMax = D(r, 18),
                CpuPowerMax = D(r, 19), GpuPowerMax = D(r, 20), CpuVoltMax = D(r, 21), GpuVoltMax = D(r, 22),
                GpuLoadSum = r.GetDouble(23), GpuLoadN = r.GetInt32(24),
            });
        }
        return list;
    }

    public List<SessionRow> GetSessions(long from, long to)
    {
        using var cmd = Cmd("""
            SELECT id, app_id, start, end, active_sec, cpu_temp_max, gpu_temp_max, is_game
            FROM sessions WHERE start < $to AND end > $from ORDER BY start
            """, ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        var list = new List<SessionRow>();
        while (r.Read())
        {
            list.Add(new SessionRow
            {
                Id = r.GetInt64(0), AppId = r.GetInt64(1), Start = r.GetInt64(2), End = r.GetInt64(3),
                ActiveSec = r.GetDouble(4), CpuTempMax = D(r, 5), GpuTempMax = D(r, 6), IsGame = r.GetInt64(7) != 0,
            });
        }
        return list;
    }

    public List<DriveDay> GetDriveDays(long from)
    {
        using var cmd = Cmd("SELECT day, drive, used_gb, total_gb FROM drive_day WHERE day >= $from ORDER BY day", ("$from", from));
        using var r = cmd.ExecuteReader();
        var list = new List<DriveDay>();
        while (r.Read())
            list.Add(new DriveDay { Day = r.GetInt64(0), Drive = r.GetString(1), UsedGb = r.GetDouble(2), TotalGb = r.GetDouble(3) });
        return list;
    }

    /// <summary>Lowest and highest CPU/GPU temperatures recorded in a range (lowest from minute averages).</summary>
    public (double? CpuMin, double? CpuMax, double? GpuMin, double? GpuMax, double? HotMax, double? MemMax) TempRange(long from, long to)
    {
        _hasGpuMem ??= HasColumn("system_minute", "gpu_mem_max");
        using var cmd = Cmd($"""
            SELECT min(cpu_temp), max(cpu_temp_max), min(gpu_temp), max(gpu_temp_max), max(gpu_hot_max), {(_hasGpuMem.Value ? "max(gpu_mem_max)" : "NULL")}
            FROM system_minute WHERE ts >= $from AND ts < $to
            """, ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (D(r, 0), D(r, 1), D(r, 2), D(r, 3), D(r, 4), D(r, 5)) : default;
    }

    /// <summary>Average CPU and GPU temperature over a range (for "hotter than usual" comparisons).</summary>
    public (double? Cpu, double? Gpu) AverageTemps(long from, long to)
    {
        using var cmd = Cmd("SELECT avg(cpu_temp), avg(gpu_temp) FROM system_minute WHERE ts >= $from AND ts < $to",
            ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (D(r, 0), D(r, 1)) : (null, null);
    }

    /// <summary>When the oldest minute of history is from (Unix seconds), if any.</summary>
    public long? FirstMinuteTime()
    {
        using var cmd = Cmd("SELECT min(ts) FROM system_minute");
        return cmd.ExecuteScalar() is long v ? v : null;
    }

    /// <summary>When the oldest recorded crash happened (Unix seconds), if any.</summary>
    public long? FirstCrashTime()
    {
        using var cmd = Cmd("SELECT min(ts) FROM crashes");
        return cmd.ExecuteScalar() is long v ? v : null;
    }

    public List<CrashEvent> GetCrashes(long from, long to)
    {
        using var cmd = Cmd("""
            SELECT id, ts, kind, app_exe, app_path, module, code, detail, during_sleep
            FROM crashes WHERE ts >= $from AND ts < $to ORDER BY ts DESC
            """, ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        var list = new List<CrashEvent>();
        while (r.Read())
        {
            list.Add(new CrashEvent
            {
                Id = r.GetInt64(0), Ts = r.GetInt64(1),
                Kind = Enum.TryParse<CrashKind>(r.GetString(2), out var k) ? k : CrashKind.AppCrash,
                AppExe = r.GetString(3), AppPath = r.IsDBNull(4) ? null : r.GetString(4),
                Module = r.IsDBNull(5) ? null : r.GetString(5), Code = r.IsDBNull(6) ? null : r.GetString(6),
                Detail = r.IsDBNull(7) ? null : r.GetString(7), DuringSleep = r.GetInt64(8) != 0,
            });
        }
        return list;
    }

    /// <summary>Highest CPU and GPU temperature in the minutes before a moment (to spot heat-related crashes).</summary>
    public (double? Cpu, double? Gpu) PeakTempsBefore(long ts, int minutes)
    {
        using var cmd = Cmd("SELECT max(cpu_temp_max), max(gpu_temp_max) FROM system_minute WHERE ts >= $from AND ts <= $to",
            ("$from", ts - minutes * 60L), ("$to", ts));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (D(r, 0), D(r, 1)) : (null, null);
    }

    /// <summary>The app in front during the last recorded minute at or before <paramref name="ts"/> (within 3 minutes).</summary>
    public long? FrontAppAt(long ts)
    {
        using var cmd = Cmd("SELECT fg_app FROM system_minute WHERE ts <= $ts AND ts > $from AND fg_app IS NOT NULL ORDER BY ts DESC LIMIT 1",
            ("$ts", ts), ("$from", ts - 180));
        return cmd.ExecuteScalar() is long id ? id : null;
    }

    private static double? D(IDataRecord r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
}
