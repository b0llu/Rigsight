using System.Data;
using Microsoft.Data.Sqlite;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Data;

/// <summary>
/// SQLite store in %LocalAppData%\Rigsight\rigsight.db. The agent opens it read-write and is the
/// only writer; the app opens it read-only. WAL mode lets both work at the same time.
/// </summary>
public sealed partial class RigsightDb : IDisposable
{
    private const int SchemaVersion = 1;
    private readonly SqliteConnection _conn;

    private RigsightDb(SqliteConnection conn) => _conn = conn;

    public static RigsightDb OpenWriter() => OpenWriter(RigsightPaths.Database);

    /// <summary>Opens (creating if needed) the database at <paramref name="path"/>: <see cref="RigsightPaths.Database"/>, or a test's.</summary>
    public static RigsightDb OpenWriter(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var conn = new SqliteConnection($"Data Source={path};Mode=ReadWriteCreate;Pooling=False");
        conn.Open();
        var db = new RigsightDb(conn);
        db.Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;");
        // All of an upgrade or none of it: a step is "done" when its table or column exists, so an agent ended between
        // making a table and filling it from the older history (the installer restarting it) left that table empty for good.
        using (var upgrade = db.BeginTransaction())
        {
            db.EnsureSchema();
            upgrade.Commit();
        }
        return db;
    }

    /// <summary>Opens the database for reading, or returns null if the agent hasn't created it yet.</summary>
    public static RigsightDb? OpenReader() => OpenReader(RigsightPaths.Database);

    public static RigsightDb? OpenReader(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
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
        if (!HasColumn("system_minute", "cpu_app")) Exec("ALTER TABLE system_minute ADD COLUMN cpu_app INTEGER");
        if (!HasColumn("system_minute", "gpu_app")) Exec("ALTER TABLE system_minute ADD COLUMN gpu_app INTEGER");
        // The clocks (0.9.1), for spotting a chip slowing itself down when hot.
        if (!HasColumn("system_minute", "cpu_clock")) Exec("ALTER TABLE system_minute ADD COLUMN cpu_clock REAL");
        if (!HasColumn("system_minute", "gpu_clock")) Exec("ALTER TABLE system_minute ADD COLUMN gpu_clock REAL");
        // Each minute's lowest temperatures: the temperature chart says a minute's average, highest and lowest.
        if (!HasColumn("system_minute", "cpu_temp_min")) Exec("ALTER TABLE system_minute ADD COLUMN cpu_temp_min REAL");
        if (!HasColumn("system_minute", "gpu_temp_min")) Exec("ALTER TABLE system_minute ADD COLUMN gpu_temp_min REAL");
        // …and the average of the GPU's hot spot and memory, so their lines on that chart are averages like the others.
        if (!HasColumn("system_minute", "gpu_hot_avg")) Exec("ALTER TABLE system_minute ADD COLUMN gpu_hot_avg REAL");
        if (!HasColumn("system_minute", "gpu_mem_avg")) Exec("ALTER TABLE system_minute ADD COLUMN gpu_mem_avg REAL");
        // …and their lowest, so the chart's lowest has all four lines (the user: "why don't I have the lowest of yesterday for the memory and the hot spot?").
        if (!HasColumn("system_minute", "gpu_hot_min")) Exec("ALTER TABLE system_minute ADD COLUMN gpu_hot_min REAL");
        if (!HasColumn("system_minute", "gpu_mem_min")) Exec("ALTER TABLE system_minute ADD COLUMN gpu_mem_min REAL");

        // Windows' own time for a PC going down, beside the one shown (0.19.1, see InsertCrashes).
        if (!HasColumn("crashes", "ts_windows")) Exec("ALTER TABLE crashes ADD COLUMN ts_windows INTEGER");

        // Added for long histories: crashes by time, and the longest session (see GetSessions).
        Exec("CREATE INDEX IF NOT EXISTS ix_crashes_ts ON crashes(ts)");
        Exec("CREATE INDEX IF NOT EXISTS ix_sessions_app ON sessions(app_id, start)");

        // Per-app totals per month, kept in step with app_hour, so long ranges ("All time") add up a few rows per
        // app instead of every hour. Filled from app_hour the first time (databases from before 0.4.13).
        bool newRollup = !HasTable("app_month");
        Exec($"CREATE TABLE IF NOT EXISTS app_month(month INTEGER NOT NULL, app_id INTEGER NOT NULL, {HourColumnsDdl}, PRIMARY KEY(month, app_id))");
        if (newRollup) Exec($"INSERT INTO app_month SELECT {MonthOf("ts")}, app_id, {HourSums} FROM app_hour GROUP BY 1, 2");
        if (GetMeta(MaxSessionKey) is null) SetMeta(MaxSessionKey, ComputeMaxSessionSec().ToString());

        // The minutes of each local day added up, recomputed from that day's minutes as each one is written, so a
        // year reads 365 rows instead of every minute. Filled from system_minute the first time (before 0.5.3).
        bool newDays = !HasTable("system_day");
        Exec("""
            CREATE TABLE IF NOT EXISTS system_day(
                day INTEGER PRIMARY KEY, minutes INTEGER NOT NULL, active_sec REAL NOT NULL, idle_sec REAL NOT NULL,
                cpu_temp_sum REAL NOT NULL, cpu_temp_n INTEGER NOT NULL, gpu_temp_sum REAL NOT NULL, gpu_temp_n INTEGER NOT NULL,
                cpu_load_sum REAL NOT NULL, cpu_load_n INTEGER NOT NULL, gpu_load_sum REAL NOT NULL, gpu_load_n INTEGER NOT NULL,
                cpu_temp_max REAL, gpu_temp_max REAL, gpu_hot_max REAL, cpu_volt_max REAL, gpu_volt_max REAL,
                cpu_power_max REAL, gpu_power_max REAL)
            """);
        // Temperatures by load band and the clocks, per day (0.9.1): how a PC ran months ago, without its minutes.
        // Added to a database from before, every day is added up again from its minutes, once.
        bool newBands = !HasColumn("system_day", "idle_cpu_sum");
        foreach (var column in new[] { "idle_cpu_sum REAL NOT NULL DEFAULT 0", "idle_cpu_n INTEGER NOT NULL DEFAULT 0",
                     "idle_gpu_sum REAL NOT NULL DEFAULT 0", "idle_gpu_n INTEGER NOT NULL DEFAULT 0",
                     "load_cpu_sum REAL NOT NULL DEFAULT 0", "load_cpu_n INTEGER NOT NULL DEFAULT 0",
                     "load_gpu_sum REAL NOT NULL DEFAULT 0", "load_gpu_n INTEGER NOT NULL DEFAULT 0",
                     "cpu_clock_sum REAL NOT NULL DEFAULT 0", "cpu_clock_n INTEGER NOT NULL DEFAULT 0",
                     "gpu_clock_sum REAL NOT NULL DEFAULT 0", "gpu_clock_n INTEGER NOT NULL DEFAULT 0" })
            if (!HasColumn("system_day", column.Split(' ')[0])) Exec($"ALTER TABLE system_day ADD COLUMN {column}");
        if (newDays || newBands) Exec($"DELETE FROM system_day; INSERT INTO system_day SELECT {DayOf("ts")}, {MinuteSums} FROM system_minute GROUP BY 1");

        // Every fan, minute by minute (0.9.1): what the fans did, and (per day) how fast they run at idle.
        Exec("""
            CREATE TABLE IF NOT EXISTS fans(id INTEGER PRIMARY KEY, sensor TEXT NOT NULL UNIQUE, name TEXT NOT NULL, hardware TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS fan_minute(ts INTEGER NOT NULL, fan INTEGER NOT NULL, rpm_avg INTEGER NOT NULL, rpm_max INTEGER NOT NULL,
                PRIMARY KEY(ts, fan)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS fan_day(day INTEGER NOT NULL, fan INTEGER NOT NULL, rpm_sum REAL NOT NULL, rpm_n INTEGER NOT NULL,
                rpm_max INTEGER NOT NULL, idle_sum REAL NOT NULL, idle_n INTEGER NOT NULL, PRIMARY KEY(day, fan));
            """);
        // Each day's steady load by app and its time at rest (0.10.2): how the PC ran months ago, in the same terms as a
        // report reads its own minutes (see HeatSql). Added up for the days already recorded, once.
        bool newHeat = !HasTable("heat_day");
        Exec("""
            CREATE TABLE IF NOT EXISTS heat_day(day INTEGER NOT NULL, app INTEGER NOT NULL, n INTEGER NOT NULL,
                gpu_sum REAL NOT NULL, gpu_n INTEGER NOT NULL, cpu_sum REAL NOT NULL, cpu_n INTEGER NOT NULL,
                power_sum REAL NOT NULL, power_n INTEGER NOT NULL, PRIMARY KEY(day, app)) WITHOUT ROWID;
            """);
        if (newHeat)
        {
            using var all = Cmd($"INSERT OR REPLACE INTO heat_day {HeatSql}", ("$from", long.MinValue / 2), ("$to", long.MaxValue / 2));
            all.ExecuteNonQuery();
        }

        // Each day's fan curves (0.12.0): a fan's speed at each temperature per app under steady load (see FanCurves), so a
        // report can set a fan against how it turned months ago without reading months of minutes. Added up by
        // WriteFanMinutes for the days before each new one (the days already recorded, the first time).
        Exec("""
            CREATE TABLE IF NOT EXISTS fan_curve_day(day INTEGER NOT NULL, fan INTEGER NOT NULL, app INTEGER NOT NULL, temp INTEGER NOT NULL,
                n INTEGER NOT NULL, rpm_sum REAL NOT NULL, PRIMARY KEY(day, fan, app, temp)) WITHOUT ROWID;
            """);

        // Until 0.10.2 a fan's last reading was recorded again and again while the app was closed (its chip wasn't read
        // then): those speeds are mostly hours old, so they go, once. The fans themselves stay.
        if (GetMeta(FansFreshKey) is null)
        {
            Exec("DELETE FROM fan_minute; DELETE FROM fan_day;");
            SetMeta(FansFreshKey, "1");
        }

        EnsureNetworkSchema();
        EnsureChangesSchema();
    }

    private const string FansFreshKey = "fans_fresh";

    private bool HasTable(string table)
    {
        using var cmd = Cmd("SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $t", ("$t", table));
        return cmd.ExecuteScalar() is long n && n > 0;
    }

    // app_hour's value columns, shared by app_month (same meaning, summed over the month).
    private const string HourColumnsDdl = """
        fg_sec REAL NOT NULL DEFAULT 0, idle_sec REAL NOT NULL DEFAULT 0, bg_sec REAL NOT NULL DEFAULT 0, min_sec REAL NOT NULL DEFAULT 0,
        cpu_sum REAL NOT NULL DEFAULT 0, cpu_n INTEGER NOT NULL DEFAULT 0, cpu_max REAL,
        mem_sum REAL NOT NULL DEFAULT 0, mem_n INTEGER NOT NULL DEFAULT 0, mem_max REAL,
        cpu_temp_sum REAL NOT NULL DEFAULT 0, cpu_temp_n INTEGER NOT NULL DEFAULT 0, cpu_temp_max REAL,
        gpu_temp_sum REAL NOT NULL DEFAULT 0, gpu_temp_n INTEGER NOT NULL DEFAULT 0, gpu_temp_max REAL, gpu_hot_max REAL,
        cpu_power_max REAL, gpu_power_max REAL, cpu_volt_max REAL, gpu_volt_max REAL,
        gpu_load_sum REAL NOT NULL DEFAULT 0, gpu_load_n INTEGER NOT NULL DEFAULT 0
        """;

    private const string HourSums = """
        sum(fg_sec), sum(idle_sec), sum(bg_sec), sum(min_sec), sum(cpu_sum), sum(cpu_n), max(cpu_max),
        sum(mem_sum), sum(mem_n), max(mem_max), sum(cpu_temp_sum), sum(cpu_temp_n), max(cpu_temp_max),
        sum(gpu_temp_sum), sum(gpu_temp_n), max(gpu_temp_max), max(gpu_hot_max), max(cpu_power_max), max(gpu_power_max),
        max(cpu_volt_max), max(gpu_volt_max), sum(gpu_load_sum), sum(gpu_load_n)
        """;

    /// <summary>
    /// SQL for the start of the (local) month containing a unix time, as a unix time. Every month key is computed
    /// by SQLite with this one expression, so writes, reads and clean-ups always agree on where a month starts.
    /// </summary>
    private static string MonthOf(string unix, string shift = "") =>
        $"CAST(strftime('%s', {unix}, 'unixepoch', 'localtime', 'start of month'{shift}, 'utc') AS INTEGER)";

    /// <summary>The start of the (local) day containing a unix time, computed by SQLite like <see cref="MonthOf"/>.</summary>
    private static string DayOf(string unix, string shift = "") =>
        $"CAST(strftime('%s', {unix}, 'unixepoch', 'localtime', 'start of day'{shift}, 'utc') AS INTEGER)";

    // system_minute rows added up into one system_day row (total() is 0 rather than NULL when nothing was read).
    // In system_day's column order (the daily rows are made of these, positionally).
    private const string MinuteSums = $"""
        count(*), total(active_sec), total(idle_sec), total(cpu_temp), count(cpu_temp), total(gpu_temp), count(gpu_temp),
        total(cpu_load), count(cpu_load), total(gpu_load), count(gpu_load), max(cpu_temp_max), max(gpu_temp_max), max(gpu_hot_max),
        max(cpu_volt_max), max(gpu_volt_max), max(cpu_power), max(gpu_power),
        total(CASE WHEN {Reports.LoadBands.IdleSql} THEN cpu_temp END), count(CASE WHEN {Reports.LoadBands.IdleSql} THEN cpu_temp END),
        total(CASE WHEN {Reports.LoadBands.IdleSql} THEN gpu_temp END), count(CASE WHEN {Reports.LoadBands.IdleSql} THEN gpu_temp END),
        total(CASE WHEN {Reports.LoadBands.CpuHeavySql} THEN cpu_temp END), count(CASE WHEN {Reports.LoadBands.CpuHeavySql} THEN cpu_temp END),
        total(CASE WHEN {Reports.LoadBands.GpuHeavySql} THEN gpu_temp END), count(CASE WHEN {Reports.LoadBands.GpuHeavySql} THEN gpu_temp END),
        total(cpu_clock), count(cpu_clock), total(gpu_clock), count(gpu_clock)
        """;

    /// <summary>
    /// heat_day's rows for the local days from $from to $to, from their minutes, as ReportBuilder.SteadyOf reads a
    /// report's: per app, its minutes at heavy GPU load past the first ten of each run (runs counted from $from); and as
    /// app 0, the minutes at rest (someone there, both chips under 15%, 16 minutes after either last worked hard, looked
    /// for a quarter of an hour before $from too). In heat_day's column order.
    /// </summary>
    private static readonly string HeatSql = $"""
        WITH m AS (
          SELECT ts, gpu_temp, cpu_temp, gpu_power, gpu_app, active_sec, cpu_load, gpu_load,
                 max(CASE WHEN gpu_load >= {Reports.LoadBands.GpuHeavyLoad} OR cpu_load >= {Reports.LoadBands.CpuHeavyLoad} THEN ts END)
                   OVER (ORDER BY ts ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) AS last_busy
          FROM system_minute WHERE ts >= $from - {(Reports.ReportBuilder.RestAfterMinutes + 1) * 60} AND ts < $to),
        runs AS (
          SELECT ts, gpu_temp, cpu_temp, gpu_power, gpu_app, ts / 60 - row_number() OVER (PARTITION BY gpu_app ORDER BY ts) AS run
          FROM m WHERE ts >= $from AND gpu_load >= {Reports.LoadBands.GpuHeavyLoad} AND gpu_app IS NOT NULL),
        steady AS (
          SELECT * FROM (SELECT *, row_number() OVER (PARTITION BY gpu_app, run ORDER BY ts) AS k FROM runs)
          WHERE k > {Reports.ReportBuilder.WarmUpMinutes} AND gpu_temp IS NOT NULL)
        SELECT {DayOf("ts")}, gpu_app, count(*), total(gpu_temp), count(gpu_temp), total(cpu_temp), count(cpu_temp), total(gpu_power), count(gpu_power)
        FROM steady GROUP BY 1, 2
        UNION ALL
        SELECT {DayOf("ts")}, 0, count(*), total(gpu_temp), count(gpu_temp), total(cpu_temp), count(cpu_temp), 0, 0 FROM m
        WHERE ts >= $from AND active_sec >= 20 AND cpu_load < {Reports.LoadBands.IdleMaxLoad} AND gpu_load < {Reports.LoadBands.IdleMaxLoad}
          AND (last_busy IS NULL OR ts >= last_busy + {(Reports.ReportBuilder.RestAfterMinutes + 1) * 60})
        GROUP BY 1
        """;

    // fan_day's, from a fan's minutes joined to the system minutes (for the idle band), in its column order.
    private const string FanSums = $"""
        total(f.rpm_avg), count(*), max(f.rpm_max),
        total(CASE WHEN m.{Reports.LoadBands.IdleSql} THEN f.rpm_avg END), count(CASE WHEN m.{Reports.LoadBands.IdleSql} THEN f.rpm_avg END)
        """;

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
                cpu_power, gpu_power, cpu_volt_max, gpu_volt_max, ram_used, fg_app, cpu_app, gpu_app, cpu_clock, gpu_clock, active_sec, idle_sec,
                cpu_temp_min, gpu_temp_min, gpu_hot_avg, gpu_mem_avg, gpu_hot_min, gpu_mem_min)
            VALUES($ts, $ct, $ctm, $gt, $gtm, $gh, $gm, $cl, $gl, $cp, $gp, $cv, $gv, $ram, $fg, $ca, $ga, $cc, $gc, $act, $idle, $ctl, $gtl, $gha, $gma, $ghl, $gml)
            """,
            ("$ghl", m.GpuHotMin), ("$gml", m.GpuMemMin),
            ("$ctl", m.CpuTempMin), ("$gtl", m.GpuTempMin), ("$gha", Tenth(m.GpuHotAvg)), ("$gma", Tenth(m.GpuMemAvg)),
            ("$ts", m.Ts), ("$ct", m.CpuTemp), ("$ctm", m.CpuTempMax), ("$gt", m.GpuTemp), ("$gtm", m.GpuTempMax),
            ("$gh", m.GpuHotMax), ("$gm", m.GpuMemMax), ("$cl", m.CpuLoad), ("$gl", m.GpuLoad), ("$cp", m.CpuPower), ("$gp", m.GpuPower),
            ("$cv", m.CpuVoltMax), ("$gv", m.GpuVoltMax), ("$ram", m.RamUsed), ("$fg", m.FgApp),
            // Clocks as whole MHz: an integer takes two bytes in the row where a decimal takes eight.
            ("$ca", m.CpuApp), ("$ga", m.GpuApp), ("$cc", WholeOrNull(m.CpuClock)), ("$gc", WholeOrNull(m.GpuClock)),
            ("$act", m.ActiveSec), ("$idle", m.IdleSec));
        cmd.ExecuteNonQuery();

        // The day's row, recomputed from its minutes (at most 1,440, by the primary key): always exact, even when
        // a minute is written again.
        using var day = Cmd($"""
            INSERT OR REPLACE INTO system_day SELECT {DayOf("$ts")}, {MinuteSums} FROM system_minute
            WHERE ts >= {DayOf("$ts")} AND ts < {DayOf("$ts", ", '+1 day'")}
            """, ("$ts", m.Ts));
        day.ExecuteNonQuery();
        // And its heat, the same way (a couple of milliseconds: one day's minutes).
        using var bounds = Cmd($"SELECT {DayOf("$ts")}, {DayOf("$ts", ", '+1 day'")}", ("$ts", m.Ts));
        using (var r = bounds.ExecuteReader())
        {
            r.Read();
            (long from, long to) = (r.GetInt64(0), r.GetInt64(1));
            r.Close();
            using var heat = Cmd($"DELETE FROM heat_day WHERE day = $from; INSERT OR REPLACE INTO heat_day {HeatSql}", ("$from", from), ("$to", to));
            heat.ExecuteNonQuery();
        }
    }

    private static object? WholeOrNull(double? value) => value is double v && double.IsFinite(v) ? (long)Math.Round(v) : null;

    private readonly Dictionary<string, long> _fanIds = [];

    /// <summary>The fans' row for a fan sensor, made the first time it's seen.</summary>
    public long FanId(string sensor, string name, string hardware)
    {
        if (_fanIds.TryGetValue(sensor, out long known)) return known;
        using var cmd = Cmd("""
            INSERT INTO fans(sensor, name, hardware) VALUES($s, $n, $h)
            ON CONFLICT(sensor) DO UPDATE SET name = excluded.name, hardware = excluded.hardware
            RETURNING id
            """, ("$s", sensor), ("$n", name), ("$h", hardware));
        return _fanIds[sensor] = (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// A minute's fan speeds (after the system minute, which the day's idle totals join to), and the day's fan totals
    /// added up again from its minutes.
    /// </summary>
    public void WriteFanMinutes(long ts, IReadOnlyList<(long Fan, int RpmAvg, int RpmMax)> fans)
    {
        if (fans.Count == 0) return;
        foreach (var (fan, avg, max) in fans)
        {
            using var cmd = Cmd("INSERT OR REPLACE INTO fan_minute(ts, fan, rpm_avg, rpm_max) VALUES($ts, $f, $a, $m)",
                ("$ts", ts), ("$f", fan), ("$a", avg), ("$m", max));
            cmd.ExecuteNonQuery();
        }
        using var day = Cmd($"""
            INSERT OR REPLACE INTO fan_day SELECT {DayOf("$ts")}, f.fan, {FanSums} FROM fan_minute f JOIN system_minute m ON m.ts = f.ts
            WHERE f.ts >= {DayOf("$ts")} AND f.ts < {DayOf("$ts", ", '+1 day'")} GROUP BY 2
            """, ("$ts", ts));
        day.ExecuteNonQuery();
        RollFanCurves(ts);
    }

    private const string FanCurveKey = "fan_curve_through";

    /// <summary>
    /// fan_curve_day for the days before <paramref name="ts"/>'s day not added up yet (once a day: a finished day doesn't
    /// change; today's curve comes from its minutes).
    /// </summary>
    private void RollFanCurves(long ts)
    {
        long DayStart(long t, string shift = "")
        {
            using var c = Cmd($"SELECT {DayOf("$t", shift)}", ("$t", t));
            return (long)c.ExecuteScalar()!;
        }
        long today = DayStart(ts);
        long through = GetMeta(FanCurveKey) is string s && long.TryParse(s, out long v) ? v : 0;
        if (through >= today) return;

        var days = new List<long>();
        using (var c = Cmd($"SELECT DISTINCT {DayOf("ts")} FROM fan_minute WHERE ts >= $from AND ts < $to", ("$from", through), ("$to", today)))
        using (var r = c.ExecuteReader())
            while (r.Read()) days.Add(r.GetInt64(0));
        var fans = GetFans().ToDictionary(f => f.Id);
        foreach (long day in days)
        {
            long next = DayStart(day, ", '+1 day'");
            var minutes = GetMinutes(day, next);
            var byTs = minutes.ToDictionary(m => m.Ts);
            var steady = Reports.FanCurves.Steady(minutes);
            using (var clear = Cmd("DELETE FROM fan_curve_day WHERE day = $d", ("$d", day))) clear.ExecuteNonQuery();
            foreach (var g in GetFanMinutes(day, next).GroupBy(f => f.Fan))
            {
                if (!fans.TryGetValue(g.Key, out var fan)) continue;
                var list = g.ToList();
                bool gpu = fan.Sensor.StartsWith("/gpu", StringComparison.Ordinal);
                var follows = Reports.FanAnalysis.Of(gpu, list, byTs).Follows;
                foreach (var ((app, temp), (n, sum)) in Reports.FanCurves.Of(follows, list, byTs, steady))
                {
                    using var ins = Cmd("INSERT OR REPLACE INTO fan_curve_day(day, fan, app, temp, n, rpm_sum) VALUES($d, $f, $a, $t, $n, $s)",
                        ("$d", day), ("$f", g.Key), ("$a", app), ("$t", temp), ("$n", n), ("$s", sum));
                    ins.ExecuteNonQuery();
                }
            }
        }
        SetMeta(FanCurveKey, today.ToString());
    }

    /// <summary>fan_curve_day's rows for the days from <paramref name="from"/> to <paramref name="to"/> (empty before 0.12.0).</summary>
    public List<FanCurveDay> GetFanCurveDays(long from, long to)
    {
        if (!(_hasFanCurves ??= HasTable("fan_curve_day"))) return [];
        using var cmd = Cmd("SELECT day, fan, app, temp, n, rpm_sum FROM fan_curve_day WHERE day >= $from AND day < $to", ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        var list = new List<FanCurveDay>();
        while (r.Read()) list.Add(new FanCurveDay(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt32(3), r.GetInt32(4), r.GetDouble(5)));
        return list;
    }

    private bool? _hasFanCurves;

    public List<FanRow> GetFans()
    {
        if (!HasFans) return [];
        using var cmd = Cmd("SELECT id, sensor, name, hardware FROM fans ORDER BY id");
        using var r = cmd.ExecuteReader();
        var list = new List<FanRow>();
        while (r.Read()) list.Add(new FanRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        return list;
    }

    public List<FanMinute> GetFanMinutes(long from, long to)
    {
        if (!HasFans) return [];
        using var cmd = Cmd("SELECT ts, fan, rpm_avg, rpm_max FROM fan_minute WHERE ts >= $from AND ts < $to ORDER BY ts, fan", ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        var list = new List<FanMinute>();
        while (r.Read()) list.Add(new FanMinute(r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetInt32(3)));
        return list;
    }

    /// <summary>The first day with fan speeds recorded (its local midnight), or null.</summary>
    public long? FirstFanDay()
    {
        if (!HasFans) return null;
        using var cmd = Cmd("SELECT min(day) FROM fan_day");
        return cmd.ExecuteScalar() is long v ? v : null;
    }

    public List<FanDay> GetFanDays(long from, long to)
    {
        if (!HasFans) return [];
        using var cmd = Cmd("SELECT day, fan, rpm_sum, rpm_n, rpm_max, idle_sum, idle_n FROM fan_day WHERE day >= $from AND day < $to ORDER BY day, fan", ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        var list = new List<FanDay>();
        while (r.Read()) list.Add(new FanDay(r.GetInt64(0), r.GetInt64(1), r.GetDouble(2), r.GetInt32(3), r.GetInt32(4), r.GetDouble(5), r.GetInt32(6)));
        return list;
    }

    /// <summary>Adds per-app deltas into their hour buckets (sums add up, maxima take the larger value).</summary>
    public void AddAppHour(AppHour h)
    {
        // The same numbers into the hour and into its month (see app_month).
        foreach (var (table, key) in new[] { ("app_hour", "ts"), ("app_month", "month") })
            AddAppDelta(table, key, h);
    }

    private void AddAppDelta(string table, string key, AppHour h)
    {
        static string Max(string col) => $"{col} = max(coalesce({col}, excluded.{col}), coalesce(excluded.{col}, {col}))";
        static string Sum(string col) => $"{col} = {col} + excluded.{col}";

        using var cmd = Cmd($"""
            INSERT INTO {table}({key}, app_id, fg_sec, idle_sec, bg_sec, min_sec, cpu_sum, cpu_n, cpu_max, mem_sum, mem_n, mem_max,
                cpu_temp_sum, cpu_temp_n, cpu_temp_max, gpu_temp_sum, gpu_temp_n, gpu_temp_max, gpu_hot_max,
                cpu_power_max, gpu_power_max, cpu_volt_max, gpu_volt_max, gpu_load_sum, gpu_load_n)
            VALUES({(key == "ts" ? "$ts" : MonthOf("$ts"))}, $app, $fg, $idle, $bg, $min, $cs, $cn, $cm, $ms, $mn, $mm, $cts, $ctn, $ctm, $gts, $gtn, $gtm, $ghm,
                $cpm, $gpm, $cvm, $gvm, $gls, $gln)
            ON CONFLICT({key}, app_id) DO UPDATE SET
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

    public void InsertSession(SessionRow s) => SaveSession(s);

    /// <summary>
    /// Writes a session and returns its row: a new one, or (with <paramref name="id"/>) the same one brought up to date.
    /// A session still going is saved each minute this way, so a PC that loses power mid-game keeps the session up to
    /// its last minute. A row that's gone (its first save was rolled back) is written again.
    /// </summary>
    public long SaveSession(SessionRow s, long? id = null)
    {
        if (id is { } row)
        {
            using var update = Cmd("""
                UPDATE sessions SET end = $end, active_sec = $act, cpu_temp_max = $ct, gpu_temp_max = $gt, is_game = $game
                WHERE id = $id AND app_id = $app AND start = $start
                """, ("$id", row), ("$app", s.AppId), ("$start", s.Start), ("$end", s.End), ("$act", s.ActiveSec),
                ("$ct", s.CpuTempMax), ("$gt", s.GpuTempMax), ("$game", s.IsGame ? 1 : 0));
            if (update.ExecuteNonQuery() == 0) id = null;
        }
        if (id is null)
        {
            using var cmd = Cmd("""
                INSERT INTO sessions(app_id, start, end, active_sec, cpu_temp_max, gpu_temp_max, is_game)
                VALUES($app, $start, $end, $act, $ct, $gt, $game);
                SELECT last_insert_rowid();
                """, ("$app", s.AppId), ("$start", s.Start), ("$end", s.End), ("$act", s.ActiveSec),
                ("$ct", s.CpuTempMax), ("$gt", s.GpuTempMax), ("$game", s.IsGame ? 1 : 0));
            id = (long)cmd.ExecuteScalar()!;
        }
        RaiseMaxSession(s.End - s.Start);
        return id.Value;
    }

    private void RaiseMaxSession(long length)
    {
        if (length > MaxSessionSec()) _maxSessionSec = length;
        // Raised against the stored value, not the cached one: a rolled-back transaction takes the stored bound back
        // with it, while the cache keeps the larger value.
        using var bound = Cmd("UPDATE meta SET value = $v WHERE key = $k AND CAST(value AS INTEGER) < $v", ("$k", MaxSessionKey), ("$v", length));
        bound.ExecuteNonQuery();
    }

    // Sessions have no length limit (one lasts as long as the app is used without a 10-minute break), so
    // "sessions overlapping a range" can't use the start index on its own: it would read every session
    // since the beginning of history. The longest session so far bounds how far back one can start.
    private const string MaxSessionKey = "max_session_sec";
    private long? _maxSessionSec;

    private long MaxSessionSec() =>
        _maxSessionSec ??= long.TryParse(GetMeta(MaxSessionKey), out var v) ? v : ComputeMaxSessionSec();

    private long ComputeMaxSessionSec()
    {
        using var cmd = Cmd("SELECT coalesce(max(end - start), 0) FROM sessions");
        return cmd.ExecuteScalar() is long v ? v : 0;
    }

    public void UpsertDriveDay(DriveDay d)
    {
        using var cmd = Cmd("INSERT OR REPLACE INTO drive_day(day, drive, used_gb, total_gb) VALUES($d, $drive, $u, $t)",
            ("$d", d.Day), ("$drive", d.Drive), ("$u", d.UsedGb), ("$t", d.TotalGb));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// When a PC that went down was last known to be running (Unix seconds). Windows' own time for it
    /// (<paramref name="windowsTs"/>) is the last one it happened to note, which on this PC was 5 to 35 minutes before
    /// the minutes recorded here stopped; so it is the end of the last minute recorded before Windows next started
    /// (<paramref name="nextStart"/>), where that is later. Windows' time stands where nothing was recorded after it
    /// (nothing was recording, or those minutes are no longer kept).
    /// </summary>
    public long LastSeenRunning(long windowsTs, long nextStart)
    {
        // A minute that ended before that start: the minute the PC started in can have a row too, written after it.
        using var cmd = Cmd("SELECT max(ts) FROM system_minute WHERE ts <= $before", ("$before", nextStart - 60));
        return cmd.ExecuteScalar() is long minute && minute + 60 > windowsTs ? minute + 60 : windowsTs;
    }

    /// <summary>
    /// A PC going down, read from Windows' log with the start it was written up at: given the time it was last known
    /// to be running, and if it is stored already (under Windows' time, as before 0.19.1, or under the one it was given
    /// since) that row is moved there, never back. True when it was stored already.
    /// </summary>
    private bool PlaceShutdown(CrashEvent e, long nextStart)
    {
        long windows = e.WindowsTs ?? e.Ts;
        e.WindowsTs = windows;
        e.Ts = LastSeenRunning(windows, nextStart);
        long? id = null;
        long storedTs = 0;
        using (var find = Cmd("SELECT id, ts FROM crashes WHERE kind = $kind AND app_exe = $exe AND (ts_windows = $w OR ts = $w OR ts = $ts) ORDER BY ts_windows IS NULL LIMIT 1",
                   ("$kind", e.Kind.ToString()), ("$exe", e.AppExe ?? ""), ("$w", windows), ("$ts", e.Ts)))
        using (var r = find.ExecuteReader())
            if (r.Read()) (id, storedTs) = (r.GetInt64(0), r.GetInt64(1));
        if (id is null) return false;
        // Never back: with the minutes around it gone (history is only kept so long), Windows' time is all there is to
        // work out again, and the stored one was worked out while they were there.
        e.Ts = Math.Max(e.Ts, storedTs);
        // An unexpected shutdown's moment is put right too, as before (older versions read it wrongly); a blue screen's
        // says nothing anyone is shown, and stays as stored.
        using var move = Cmd($"UPDATE OR IGNORE crashes SET ts = $ts, ts_windows = $w{(e.Kind == CrashKind.UnexpectedShutdown ? ", during_sleep = $sleep" : "")} WHERE id = $id",
            ("$ts", e.Ts), ("$w", windows), ("$sleep", (int)e.Moment), ("$id", id.Value));
        move.ExecuteNonQuery();
        return true;
    }

    /// <summary>
    /// Stores crashes; ones already recorded are ignored. Returns the newly added ones. A PC going down is stored at the
    /// time it was last known to be running, and one stored before under Windows' time is moved there (see
    /// <see cref="LastSeenRunning"/>); its event comes back with that time. With <paramref name="knownOnly"/> nothing
    /// is added: stored ones are put right, the rest left out.
    /// </summary>
    public List<CrashEvent> InsertCrashes(IEnumerable<CrashEvent> crashes, bool knownOnly = false)
    {
        var added = new List<CrashEvent>();
        foreach (var e in crashes)
        {
            if (e.NextStart is long nextStart && PlaceShutdown(e, nextStart)) continue;
            if (knownOnly) continue;
            using var cmd = Cmd("""
                INSERT OR IGNORE INTO crashes(ts, kind, app_exe, app_path, module, code, detail, during_sleep, ts_windows)
                VALUES($ts, $kind, $exe, $path, $module, $code, $detail, $sleep, $windows)
                """, ("$ts", e.Ts), ("$kind", e.Kind.ToString()), ("$exe", e.AppExe ?? ""), ("$path", e.AppPath),
                ("$module", e.Module), ("$code", e.Code), ("$detail", e.Detail), ("$sleep", (int)e.Moment), ("$windows", e.WindowsTs));
            if (cmd.ExecuteNonQuery() > 0) added.Add(e);
            else if (e.Kind == CrashKind.UnexpectedShutdown)
            {
                // Older versions read the moment wrongly (a shutdown that didn't finish showed as "asleep"); the
                // first scan after starting rereads 90 days, which puts those right without counting them as new.
                using var fix = Cmd("UPDATE crashes SET during_sleep = $sleep WHERE kind = $kind AND ts = $ts AND app_exe = $exe AND during_sleep <> $sleep",
                    ("$sleep", (int)e.Moment), ("$kind", e.Kind.ToString()), ("$ts", e.Ts), ("$exe", e.AppExe ?? ""));
                fix.ExecuteNonQuery();
            }
        }
        return added;
    }

    /// <summary>Deletes all history older than <paramref name="before"/>: every table uses the same cutoff.</summary>
    public void Prune(long before)
    {
        using (var c1 = Cmd("DELETE FROM system_minute WHERE ts < $t", ("$t", before))) c1.ExecuteNonQuery();
        using (var c2 = Cmd("DELETE FROM app_hour WHERE ts < $t", ("$t", before))) c2.ExecuteNonQuery();
        using (var c2m = Cmd($"""
            DELETE FROM app_month WHERE month <= {MonthOf("$t")};
            INSERT INTO app_month SELECT {MonthOf("ts")}, app_id, {HourSums} FROM app_hour
                WHERE ts >= {MonthOf("$t")} AND ts < {MonthOf("$t", ", '+1 month'")} GROUP BY 1, 2;
            """, ("$t", before))) c2m.ExecuteNonQuery();
        using (var c1d = Cmd($"""
            DELETE FROM system_day WHERE day <= {DayOf("$t")};
            INSERT INTO system_day SELECT {DayOf("ts")}, {MinuteSums} FROM system_minute
                WHERE ts >= {DayOf("$t")} AND ts < {DayOf("$t", ", '+1 day'")} GROUP BY 1;
            """, ("$t", before))) c1d.ExecuteNonQuery();
        using (var c1f = Cmd($"""
            DELETE FROM fan_minute WHERE ts < $t;
            DELETE FROM fan_day WHERE day <= {DayOf("$t")};
            DELETE FROM fan_curve_day WHERE day <= {DayOf("$t")};
            INSERT INTO fan_day SELECT {DayOf("f.ts")}, f.fan, {FanSums} FROM fan_minute f JOIN system_minute m ON m.ts = f.ts
                WHERE f.ts >= {DayOf("$t")} AND f.ts < {DayOf("$t", ", '+1 day'")} GROUP BY 1, 2;
            """, ("$t", before))) c1f.ExecuteNonQuery();
        using (var c1h = Cmd($"""
            DELETE FROM heat_day WHERE day <= {DayOf("$t")};
            """, ("$t", before))) c1h.ExecuteNonQuery();
        using (var c1b = Cmd($"SELECT {DayOf("$t")}, {DayOf("$t", ", '+1 day'")}", ("$t", before)))
        using (var r = c1b.ExecuteReader())
        {
            // The day the cutoff falls in, added up again from what's left of it (none past the calendar's end).
            if (r.Read() && !r.IsDBNull(0) && !r.IsDBNull(1))
            {
                (long from, long to) = (r.GetInt64(0), r.GetInt64(1));
                r.Close();
                using var heat = Cmd($"INSERT OR REPLACE INTO heat_day {HeatSql}", ("$from", from), ("$to", to));
                heat.ExecuteNonQuery();
            }
        }
        using (var c3 = Cmd("DELETE FROM sessions WHERE start < $t", ("$t", before))) c3.ExecuteNonQuery();
        using (var c4 = Cmd("DELETE FROM drive_day WHERE day < $t", ("$t", before))) c4.ExecuteNonQuery();
        using (var c5 = Cmd("DELETE FROM crashes WHERE ts < $t", ("$t", before))) c5.ExecuteNonQuery();
        PruneNetwork(before);
        PruneChanges(before);
    }

    public void ClearHistory()
    {
        Exec("DELETE FROM system_minute; DELETE FROM system_day; DELETE FROM app_hour; DELETE FROM app_month; DELETE FROM sessions; DELETE FROM drive_day; DELETE FROM crashes;"
            + " DELETE FROM fan_minute; DELETE FROM fan_day; DELETE FROM heat_day; DELETE FROM fan_curve_day;"
            + " DELETE FROM net_minute; DELETE FROM net_day; DELETE FROM net_app_hour; DELETE FROM net_app_day; DELETE FROM net_transfer; DELETE FROM net_drop;"
            + " DELETE FROM changes;"); // the inventory stays: what's installed isn't history, and clearing it would list every app as new
        Exec("VACUUM");
    }

    // ── Reader ────────────────────────────────────────────────────────────

    /// <summary>When recording started: the oldest minute or hourly app total (minutes are kept for less time than app totals).</summary>
    public long? FirstDataTime()
    {
        using var cmd = Cmd("SELECT min(t) FROM (SELECT min(ts) AS t FROM system_minute UNION ALL SELECT min(ts) FROM app_hour)");
        return cmd.ExecuteScalar() is long v ? v : null;
    }

    private bool? _hasSystemDay, _hasAppMonth;

    // app_month is added by the agent (0.4.13); the app may read an older database first.
    private bool HasAppMonth() => _hasAppMonth ??= HasTable("app_month");

    /// <summary>
    /// The daily totals from <paramref name="from"/> to <paramref name="to"/> (local midnights), or null when the
    /// database doesn't have them yet (the agent adds the table; the app may read first).
    /// </summary>
    public List<SystemDay>? GetSystemDays(long from, long to)
    {
        _hasSystemDay ??= HasTable("system_day");
        if (!_hasSystemDay.Value) return null;
        using var cmd = Cmd($"""
            SELECT day, minutes, active_sec, idle_sec, cpu_temp_sum, cpu_temp_n, gpu_temp_sum, gpu_temp_n, cpu_load_sum, cpu_load_n,
                   gpu_load_sum, gpu_load_n, cpu_temp_max, gpu_temp_max, gpu_hot_max, cpu_volt_max, gpu_volt_max, cpu_power_max, gpu_power_max,
                   {(HasBands ? "idle_cpu_sum, idle_cpu_n, idle_gpu_sum, idle_gpu_n, load_cpu_sum, load_cpu_n, load_gpu_sum, load_gpu_n, cpu_clock_sum, cpu_clock_n, gpu_clock_sum, gpu_clock_n"
                       : "0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0")}
            FROM system_day WHERE day >= $from AND day < $to ORDER BY day
            """, ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        var list = new List<SystemDay>();
        while (r.Read())
        {
            list.Add(new SystemDay
            {
                Day = r.GetInt64(0), Minutes = r.GetInt32(1), ActiveSec = r.GetDouble(2), IdleSec = r.GetDouble(3),
                CpuTempSum = r.GetDouble(4), CpuTempN = r.GetInt32(5), GpuTempSum = r.GetDouble(6), GpuTempN = r.GetInt32(7),
                CpuLoadSum = r.GetDouble(8), CpuLoadN = r.GetInt32(9), GpuLoadSum = r.GetDouble(10), GpuLoadN = r.GetInt32(11),
                CpuTempMax = D(r, 12), GpuTempMax = D(r, 13), GpuHotMax = D(r, 14), CpuVoltMax = D(r, 15), GpuVoltMax = D(r, 16),
                CpuPowerMax = D(r, 17), GpuPowerMax = D(r, 18),
                IdleCpuSum = r.GetDouble(19), IdleCpuN = r.GetInt32(20), IdleGpuSum = r.GetDouble(21), IdleGpuN = r.GetInt32(22),
                LoadCpuSum = r.GetDouble(23), LoadCpuN = r.GetInt32(24), LoadGpuSum = r.GetDouble(25), LoadGpuN = r.GetInt32(26),
                CpuClockSum = r.GetDouble(27), CpuClockN = r.GetInt32(28), GpuClockSum = r.GetDouble(29), GpuClockN = r.GetInt32(30),
            });
        }
        return list;
    }

    /// <summary>
    /// The first minute in a range where a system_minute column had the given value, and the app a column of that
    /// minute names (cpu_app or gpu_app; none when null): where a day's high (from <see cref="GetSystemDays"/>) happened,
    /// and what was doing the work. The range is one day, found by the primary key.
    /// </summary>
    public (long Ts, long? App)? FindMinute(string column, double value, long from, long to, string? appColumn)
    {
        string app = appColumn is not null && HasLoadApps ? appColumn : "NULL";
        using var cmd = Cmd($"SELECT ts, {app} FROM system_minute WHERE ts >= $from AND ts < $to AND {column} = $v ORDER BY ts LIMIT 1",
            ("$from", from), ("$to", to), ("$v", value));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1)) : null;
    }

    /// <summary>Time in front per app per month (app_month) for months starting in the range.</summary>
    public List<(long Month, long AppId, double FgSec)> GetAppMonths(long from, long to)
    {
        using var cmd = Cmd("SELECT month, app_id, fg_sec FROM app_month WHERE month >= $from AND month < $to AND fg_sec > 0",
            ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        var list = new List<(long, long, double)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetInt64(1), r.GetDouble(2)));
        return list;
    }

    /// <summary>One app's time in front per hour (app_hour) or per month (app_month) in a range, for its chart.</summary>
    public List<(long Ts, double FgSec)> GetAppTime(long appId, long from, long to, bool monthly)
    {
        using var cmd = Cmd(monthly
            ? HasAppMonth() ? "SELECT month, fg_sec FROM app_month WHERE app_id = $a AND month >= $from AND month < $to AND fg_sec > 0"
                : $"SELECT {MonthOf("ts")}, sum(fg_sec) FROM app_hour WHERE ts >= $from AND ts < $to AND app_id = $a GROUP BY 1 HAVING sum(fg_sec) > 0"
            : "SELECT ts, fg_sec FROM app_hour WHERE ts >= $from AND ts < $to AND app_id = $a AND fg_sec > 0",
            ("$a", appId), ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        var list = new List<(long, double)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetDouble(1)));
        return list;
    }

    /// <summary>The longest sessions overlapping a range (at least <paramref name="minSec"/> in front), longest first.</summary>
    public List<SessionRow> GetLongestSessions(long from, long to, double minSec, int count)
    {
        using var cmd = Cmd("""
            SELECT id, app_id, start, end, active_sec, cpu_temp_max, gpu_temp_max, is_game
            FROM sessions WHERE start >= $earliest AND start < $to AND end > $from AND active_sec >= $min
            ORDER BY active_sec DESC LIMIT $n
            """, ("$from", from), ("$to", to), ("$earliest", from - MaxSessionSec() - 1), ("$min", minSec), ("$n", count));
        return ReadSessions(cmd);
    }

    /// <summary>The longest game session (active seconds) in a range, if any.</summary>
    /// <summary>
    /// The longest session of any of <paramref name="games"/> (the apps that are games now: one relabelled since counts
    /// with its old sessions), from <paramref name="from"/> to <paramref name="to"/>.
    /// </summary>
    public double? LongestGameSessionSec(long from, long to, IReadOnlyCollection<long> games)
    {
        if (games.Count == 0) return null;
        using var cmd = Cmd($"""
            SELECT max(active_sec) FROM sessions WHERE start >= $earliest AND start < $to AND end > $from
              AND app_id IN ({string.Join(',', games.Select(g => g.ToString(System.Globalization.CultureInfo.InvariantCulture)))})
            """, ("$from", from), ("$to", to), ("$earliest", from - MaxSessionSec() - 1));
        return cmd.ExecuteScalar() is double v ? v : null;
    }

    // Whether system_minute has gpu_mem_max yet (added in 0.4.5; the agent adds it, the app may read first).
    private bool? _hasGpuMem;
    // …and the apps doing the work (added in 0.8.1), and the clocks (0.9.1).
    private bool? _hasLoadApps, _hasClocks, _hasFans, _hasBands;
    private bool HasLoadApps => _hasLoadApps ??= HasColumn("system_minute", "cpu_app");
    private bool HasClocks => _hasClocks ??= HasColumn("system_minute", "cpu_clock");
    // …and each minute's lowest temperatures.
    private bool? _hasTempLows, _hasHotAvgs, _hasHotLows;
    private bool HasHotLows => _hasHotLows ??= HasColumn("system_minute", "gpu_hot_min");
    private bool HasTempLows => _hasTempLows ??= HasColumn("system_minute", "cpu_temp_min");
    private bool HasHotAvgs => _hasHotAvgs ??= HasColumn("system_minute", "gpu_hot_avg");

    // An average to a tenth of a degree: nothing shows more, and the row isn't made longer by digits nobody reads.
    private static double? Tenth(double? v) => v is double d ? Math.Round(d, 1) : null;
    // The app reads a database the agent made, which may be an older one (0.9.0 or before) without the fans or the day bands.
    private bool HasFans => _hasFans ??= HasTable("fans");
    private bool HasBands => _hasBands ??= HasColumn("system_day", "idle_cpu_sum");

    public List<SystemMinute> GetMinutes(long from, long to)
    {
        _hasGpuMem ??= HasColumn("system_minute", "gpu_mem_max");
        using var cmd = Cmd($"""
            SELECT ts, cpu_temp, cpu_temp_max, gpu_temp, gpu_temp_max, gpu_hot_max, cpu_load, gpu_load, cpu_power, gpu_power,
                   cpu_volt_max, gpu_volt_max, ram_used, fg_app, active_sec, idle_sec, {(_hasGpuMem.Value ? "gpu_mem_max" : "NULL")},
                   {(HasLoadApps ? "cpu_app, gpu_app" : "NULL, NULL")}, {(HasClocks ? "cpu_clock, gpu_clock" : "NULL, NULL")},
                   {(HasTempLows ? "cpu_temp_min, gpu_temp_min" : "NULL, NULL")}, {(HasHotAvgs ? "gpu_hot_avg, gpu_mem_avg" : "NULL, NULL")},
                   {(HasHotLows ? "gpu_hot_min, gpu_mem_min" : "NULL, NULL")}
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
                CpuApp = r.IsDBNull(17) ? null : r.GetInt64(17), GpuApp = r.IsDBNull(18) ? null : r.GetInt64(18),
                CpuClock = D(r, 19), GpuClock = D(r, 20), CpuTempMin = D(r, 21), GpuTempMin = D(r, 22), GpuHotAvg = D(r, 23), GpuMemAvg = D(r, 24),
                GpuHotMin = D(r, 25), GpuMemMin = D(r, 26),
            });
        }
        return list;
    }

    /// <summary>
    /// The temperatures of [from, to) hour by hour, for the temperature chart's week and month (or, with
    /// <paramref name="byDay"/>, day by day for its year): each hour's average, its
    /// highest and its lowest reading, as a row whose <see cref="SystemMinute.Ts"/> is the start of the hour on the
    /// local clock (of the day: its local midnight). The same for the hot spot's and the memory's average: only when
    /// every minute that read them kept one (else the chart draws that hour from its highest, as it always did). An hour has a lowest only when every one of its minutes kept one (minutes from before that have
    /// only their average, and the lowest of those would pass for a reading that was never that low). Hours with
    /// nothing recorded are left out.
    /// </summary>
    public List<SystemMinute> GetTempHours(long from, long to, bool byDay = false)
    {
        _hasGpuMem ??= HasColumn("system_minute", "gpu_mem_max");
        // Hours as the clock shows them: a zone half an hour off (India) would otherwise get hours from :30 to :30.
        long offset = (long)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(to)).TotalSeconds;
        using var cmd = Cmd($"""
            SELECT {(byDay ? DayOf("ts") : "(ts + $off) / 3600 * 3600 - $off")}, avg(cpu_temp), max(cpu_temp_max),
                   {(HasTempLows ? "CASE WHEN count(cpu_temp_min) = count(cpu_temp) THEN min(cpu_temp_min) END" : "NULL")},
                   avg(gpu_temp), max(gpu_temp_max), {(HasTempLows ? "CASE WHEN count(gpu_temp_min) = count(gpu_temp) THEN min(gpu_temp_min) END" : "NULL")},
                   max(gpu_hot_max), {(_hasGpuMem.Value ? "max(gpu_mem_max)" : "NULL")},
                   {(HasHotAvgs ? "CASE WHEN count(gpu_hot_avg) = count(gpu_hot_max) THEN avg(gpu_hot_avg) END" : "NULL")},
                   {(HasHotAvgs && _hasGpuMem.Value ? "CASE WHEN count(gpu_mem_avg) = count(gpu_mem_max) THEN avg(gpu_mem_avg) END" : "NULL")},
                   {(HasHotLows ? "CASE WHEN count(gpu_hot_min) = count(gpu_hot_max) THEN min(gpu_hot_min) END" : "NULL")},
                   {(HasHotLows && _hasGpuMem.Value ? "CASE WHEN count(gpu_mem_min) = count(gpu_mem_max) THEN min(gpu_mem_min) END" : "NULL")}
            FROM system_minute WHERE ts >= $from AND ts < $to GROUP BY 1 ORDER BY 1
            """, ("$from", from), ("$to", to), ("$off", offset));
        using var r = cmd.ExecuteReader();
        var list = new List<SystemMinute>();
        while (r.Read())
        {
            list.Add(new SystemMinute
            {
                Ts = r.GetInt64(0),
                CpuTemp = D(r, 1), CpuTempMax = D(r, 2), CpuTempMin = D(r, 3),
                GpuTemp = D(r, 4), GpuTempMax = D(r, 5), GpuTempMin = D(r, 6), GpuHotMax = D(r, 7), GpuMemMax = D(r, 8),
                GpuHotAvg = D(r, 9), GpuMemAvg = D(r, 10), GpuHotMin = D(r, 11), GpuMemMin = D(r, 12),
            });
        }
        return list;
    }

    /// <summary>
    /// Each day's steady load by app and time at rest (app 0) from <paramref name="from"/> to <paramref name="to"/> (see
    /// heat_day). None from a database older than 0.10.2, which the app may read before the agent has updated it.
    /// </summary>
    public List<HeatDay> GetHeatDays(long from, long to)
    {
        if (!(_hasHeat ??= HasTable("heat_day"))) return [];
        using var cmd = Cmd("SELECT day, app, n, gpu_sum, gpu_n, cpu_sum, cpu_n, power_sum, power_n FROM heat_day WHERE day >= $from AND day < $to",
            ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        var list = new List<HeatDay>();
        while (r.Read())
            list.Add(new HeatDay(r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetDouble(3), r.GetInt32(4), r.GetDouble(5), r.GetInt32(6), r.GetDouble(7), r.GetInt32(8)));
        return list;
    }

    private bool? _hasHeat;

    public List<AppHour> GetAppHours(long from, long to)
    {
        using var cmd = Cmd("""
            SELECT ts, app_id, fg_sec, idle_sec, bg_sec, min_sec, cpu_sum, cpu_n, cpu_max, mem_sum, mem_n, mem_max,
                   cpu_temp_sum, cpu_temp_n, cpu_temp_max, gpu_temp_sum, gpu_temp_n, gpu_temp_max, gpu_hot_max,
                   cpu_power_max, gpu_power_max, cpu_volt_max, gpu_volt_max, gpu_load_sum, gpu_load_n
            FROM app_hour WHERE ts >= $from AND ts < $to
            """, ("$from", from), ("$to", to));
        return ReadHours(cmd);
    }

    /// <summary>One app's hours from <paramref name="from"/> to <paramref name="to"/> (the Processes page's history of an app).</summary>
    public List<AppHour> GetAppHoursOf(long appId, long from, long to)
    {
        using var cmd = Cmd("""
            SELECT ts, app_id, fg_sec, idle_sec, bg_sec, min_sec, cpu_sum, cpu_n, cpu_max, mem_sum, mem_n, mem_max,
                   cpu_temp_sum, cpu_temp_n, cpu_temp_max, gpu_temp_sum, gpu_temp_n, gpu_temp_max, gpu_hot_max,
                   cpu_power_max, gpu_power_max, cpu_volt_max, gpu_volt_max, gpu_load_sum, gpu_load_n
            FROM app_hour WHERE app_id = $app AND ts >= $from AND ts < $to
            """, ("$app", appId), ("$from", from), ("$to", to));
        return ReadHours(cmd);
    }

    /// <summary>
    /// Every app's memory over [from, to), added up by the database: the sum of its readings, how many there were, and
    /// on how many (local) days it had any. The Memory page's usual for every app listed, in one read.
    /// </summary>
    public List<(long AppId, double MemSum, long MemN, int Days)> GetAppMemoryTotals(long from, long to)
    {
        using var cmd = Cmd($"""
            SELECT app_id, total(mem_sum), total(mem_n), count(DISTINCT {DayOf("ts")})
            FROM app_hour WHERE ts >= $from AND ts < $to AND mem_n > 0 GROUP BY app_id
            """, ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        var list = new List<(long, double, long, int)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetDouble(1), (long)r.GetDouble(2), r.GetInt32(3)));
        return list;
    }

    /// <summary>The minute of [from, to) with the most memory in use (GB), the earliest of them; null with none recorded.</summary>
    public (long Ts, double RamUsed)? GetFullestMinute(long from, long to)
    {
        using var cmd = Cmd("""
            SELECT ts, ram_used FROM system_minute WHERE ts >= $from AND ts < $to AND ram_used IS NOT NULL
            ORDER BY ram_used DESC, ts LIMIT 1
            """, ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetDouble(1)) : null;
    }

    /// <summary>
    /// One row per app with the range summed (sums added, highs as the highest). Ts is 0. Whole months inside the
    /// range come from app_month and only the partial months at either end from app_hour: the same result as
    /// adding up every hour, at a fraction of the cost over long ranges.
    /// </summary>
    public List<AppHour> GetAppTotals(long from, long to)
    {
        // The first whole month starting at or after `from`, and the start of the month `to` falls in.
        long m1, m2;
        using (var b = Cmd($"""
            SELECT CASE WHEN {MonthOf("$from")} = $from THEN $from ELSE {MonthOf("$from", ", '+1 month'")} END, {MonthOf("$to")}
            """, ("$from", from), ("$to", to)))
        using (var r = b.ExecuteReader())
        {
            r.Read();
            (m1, m2) = (r.GetInt64(0), r.GetInt64(1));
        }
        if (m1 >= m2 || !HasAppMonth()) (m1, m2) = (to, to); // no whole month inside (or no monthly totals yet): hours only

        using var cmd = Cmd($"""
            SELECT 0, app_id, {HourSums} FROM (
                SELECT * FROM app_hour WHERE (ts >= $from AND ts < $m1) OR (ts >= $m2 AND ts < $to)
                {(HasAppMonth() ? "UNION ALL SELECT * FROM app_month WHERE month >= $m1 AND month < $m2" : "")})
            GROUP BY app_id
            """, ("$from", from), ("$to", to), ("$m1", m1), ("$m2", m2));
        return ReadHours(cmd);
    }

    private static List<AppHour> ReadHours(SqliteCommand cmd)
    {
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

    /// <summary>Per app: how many sessions of at least <paramref name="minSec"/> in the range, and the longest.</summary>
    public Dictionary<long, (int Count, double Longest)> GetSessionStats(long from, long to, double minSec)
    {
        using var cmd = Cmd("""
            SELECT app_id, count(*), max(active_sec) FROM sessions
            WHERE start >= $earliest AND start < $to AND end > $from AND active_sec >= $min GROUP BY app_id
            """, ("$from", from), ("$to", to), ("$earliest", from - MaxSessionSec() - 1), ("$min", minSec));
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<long, (int, double)>();
        while (r.Read()) map[r.GetInt64(0)] = (r.GetInt32(1), r.GetDouble(2));
        return map;
    }

    /// <summary>One app's most recent sessions of at least <paramref name="minSec"/> in the range, newest first.</summary>
    public List<SessionRow> GetRecentSessions(long appId, long from, long to, double minSec, int limit)
    {
        using var cmd = Cmd("""
            SELECT id, app_id, start, end, active_sec, cpu_temp_max, gpu_temp_max, is_game FROM sessions
            WHERE app_id = $app AND start >= $earliest AND start < $to AND end > $from AND active_sec >= $min
            ORDER BY start DESC LIMIT $limit
            """, ("$app", appId), ("$from", from), ("$to", to), ("$earliest", from - MaxSessionSec() - 1), ("$min", minSec), ("$limit", limit));
        return ReadSessions(cmd);
    }

    /// <summary>
    /// For every crash in the range, in one query: the highest CPU and GPU temperature in the 5 minutes before it,
    /// the app in front (within 3 minutes), and how long the crashed app's session had run (the one it ended).
    /// </summary>
    public Dictionary<long, CrashContext> GetCrashContext(long from, long to)
    {
        // The session may have ended up to 10 minutes before the crash, so it can start that much before the longest-session bound.
        using var cmd = Cmd("""
            SELECT c.id,
              (SELECT max(cpu_temp_max) FROM system_minute m WHERE m.ts >= c.ts - 300 AND m.ts <= c.ts),
              (SELECT max(gpu_temp_max) FROM system_minute m WHERE m.ts >= c.ts - 300 AND m.ts <= c.ts),
              (SELECT fg_app FROM system_minute m WHERE m.ts <= c.ts AND m.ts > c.ts - 180 AND fg_app IS NOT NULL ORDER BY m.ts DESC LIMIT 1),
              (SELECT s.active_sec FROM sessions s WHERE s.app_id = (SELECT a.id FROM apps a WHERE a.exe = c.app_exe)
                 AND s.start >= c.ts - $maxSession - 601 AND s.start <= c.ts + 60 AND s.end >= c.ts - 600 ORDER BY s.end DESC LIMIT 1),
              NOT EXISTS (SELECT 1 FROM system_minute m WHERE m.ts >= c.ts - 300 AND m.ts <= c.ts)
                 AND c.ts - 300 >= (SELECT min(ts) FROM system_minute)
            FROM crashes c WHERE c.ts >= $from AND c.ts < $to
            """, ("$from", from), ("$to", to), ("$maxSession", MaxSessionSec()));
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<long, CrashContext>();
        while (r.Read())
            map[r.GetInt64(0)] = new CrashContext(D(r, 1), D(r, 2), r.IsDBNull(3) ? null : r.GetInt64(3), D(r, 4), !r.IsDBNull(5) && r.GetInt64(5) != 0);
        return map;
    }

    public List<SessionRow> GetSessions(long from, long to)
    {
        using var cmd = Cmd("""
            SELECT id, app_id, start, end, active_sec, cpu_temp_max, gpu_temp_max, is_game
            FROM sessions WHERE start >= $earliest AND start < $to AND end > $from ORDER BY start
            """, ("$from", from), ("$to", to), ("$earliest", from - MaxSessionSec() - 1));
        return ReadSessions(cmd);
    }

    private static List<SessionRow> ReadSessions(SqliteCommand cmd)
    {
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

    /// <summary>
    /// The last day before <paramref name="before"/> (a local midnight) with at least <paramref name="minActiveSec"/> of
    /// use: its local midnight (Unix seconds), if any.
    /// </summary>
    public long? LastUsedDayBefore(long before, double minActiveSec = 300)
    {
        using var cmd = Cmd("SELECT max(day) FROM system_day WHERE day < $before AND active_sec >= $min", ("$before", before), ("$min", minActiveSec));
        return cmd.ExecuteScalar() is long v ? v : null;
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

    // crashes.ts_windows is added by the agent (0.19.1); the app may read an older database first, and goes on reading
    // it after the agent has added the column.
    private bool _hasWindowsTs;

    public List<CrashEvent> GetCrashes(long from, long to)
    {
        _hasWindowsTs = _hasWindowsTs || HasColumn("crashes", "ts_windows");
        using var cmd = Cmd($"""
            SELECT id, ts, kind, app_exe, app_path, module, code, detail, during_sleep, {(_hasWindowsTs ? "ts_windows" : "NULL")}
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
                Detail = r.IsDBNull(7) ? null : r.GetString(7), Moment = (PowerMoment)Math.Clamp(r.GetInt64(8), 0, 2),
                WindowsTs = r.IsDBNull(9) ? null : r.GetInt64(9),
            });
        }
        return list;
    }

    /// <summary>The last minute recorded before <paramref name="ts"/> (Unix seconds), or null.</summary>
    public long? LastMinuteBefore(long ts)
    {
        using var cmd = Cmd("SELECT max(ts) FROM system_minute WHERE ts < $ts", ("$ts", ts));
        return cmd.ExecuteScalar() is long v ? v : null;
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
