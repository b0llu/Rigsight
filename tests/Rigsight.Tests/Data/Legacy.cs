using Microsoft.Data.Sqlite;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Data;

/// <summary>
/// Databases shaped as earlier versions left them, made with the DDL those versions ran (from git history), then
/// filled with <see cref="History"/> rows by plain SQL.
/// </summary>
public static class Legacy
{
    /// <summary>0.2.0 – 0.4.4: no gpu_mem_max, no rollups, only the sessions-by-start index.</summary>
    public const string V02 = "0.2";
    /// <summary>0.4.5 – 0.4.12: gpu_mem_max added.</summary>
    public const string V045 = "0.4.5";
    /// <summary>0.4.13 – 0.5.2: crash and per-app session indexes, app_month and the session bound; no system_day.</summary>
    public const string V0413 = "0.4.13";

    public static TheoryData<string> Versions => [V02, V045, V0413];

    private const string V02Ddl = """
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
        INSERT OR IGNORE INTO meta(key, value) VALUES('schema', '1');
        """;

    private const string V045Ddl = "ALTER TABLE system_minute ADD COLUMN gpu_mem_max REAL;";

    private const string V0413Ddl = """
        CREATE INDEX IF NOT EXISTS ix_crashes_ts ON crashes(ts);
        CREATE INDEX IF NOT EXISTS ix_sessions_app ON sessions(app_id, start);
        CREATE TABLE IF NOT EXISTS app_month(month INTEGER NOT NULL, app_id INTEGER NOT NULL,
            fg_sec REAL NOT NULL DEFAULT 0, idle_sec REAL NOT NULL DEFAULT 0, bg_sec REAL NOT NULL DEFAULT 0, min_sec REAL NOT NULL DEFAULT 0,
            cpu_sum REAL NOT NULL DEFAULT 0, cpu_n INTEGER NOT NULL DEFAULT 0, cpu_max REAL,
            mem_sum REAL NOT NULL DEFAULT 0, mem_n INTEGER NOT NULL DEFAULT 0, mem_max REAL,
            cpu_temp_sum REAL NOT NULL DEFAULT 0, cpu_temp_n INTEGER NOT NULL DEFAULT 0, cpu_temp_max REAL,
            gpu_temp_sum REAL NOT NULL DEFAULT 0, gpu_temp_n INTEGER NOT NULL DEFAULT 0, gpu_temp_max REAL, gpu_hot_max REAL,
            cpu_power_max REAL, gpu_power_max REAL, cpu_volt_max REAL, gpu_volt_max REAL,
            gpu_load_sum REAL NOT NULL DEFAULT 0, gpu_load_n INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(month, app_id));
        """;

    // What 0.4.13 filled app_month with, and kept in step afterwards.
    private const string V0413Fill = """
        INSERT INTO app_month SELECT CAST(strftime('%s', ts, 'unixepoch', 'localtime', 'start of month', 'utc') AS INTEGER), app_id,
            sum(fg_sec), sum(idle_sec), sum(bg_sec), sum(min_sec), sum(cpu_sum), sum(cpu_n), max(cpu_max),
            sum(mem_sum), sum(mem_n), max(mem_max), sum(cpu_temp_sum), sum(cpu_temp_n), max(cpu_temp_max),
            sum(gpu_temp_sum), sum(gpu_temp_n), max(gpu_temp_max), max(gpu_hot_max), max(cpu_power_max), max(gpu_power_max),
            max(cpu_volt_max), max(gpu_volt_max), sum(gpu_load_sum), sum(gpu_load_n)
        FROM app_hour GROUP BY 1, 2;
        INSERT OR REPLACE INTO meta(key, value) SELECT 'max_session_sec', coalesce(max(end - start), 0) FROM sessions;
        """;

    /// <summary>Makes a database as <paramref name="version"/> left it, holding <paramref name="history"/> (or nothing).</summary>
    public static string Create(string version, History? history)
    {
        var path = Path.Combine(TestEnvironment.NewFolder("legacy-" + version), "rigsight.db");
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        Exec(conn, "PRAGMA journal_mode=WAL;" + V02Ddl);
        if (version != V02) Exec(conn, V045Ddl);
        history?.WriteRaw(conn, withGpuMem: version != V02);
        if (version == V0413)
        {
            Exec(conn, V0413Ddl);
            Exec(conn, V0413Fill);
        }
        return path;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
