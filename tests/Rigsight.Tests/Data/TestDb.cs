using System.Globalization;
using Microsoft.Data.Sqlite;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Data;

/// <summary>A database of the test's own (never the run's shared one), with raw SQL access for checking what's stored.</summary>
public sealed class TestDb : IDisposable
{
    public string Path { get; }
    public RigsightDb Db { get; private set; }

    public TestDb(string name = "db")
    {
        Path = System.IO.Path.Combine(TestEnvironment.NewFolder(name), "rigsight.db");
        Db = RigsightDb.OpenWriter(Path);
    }

    private TestDb(string path, RigsightDb? db) { Path = path; Db = db!; }

    /// <summary>A database at <paramref name="path"/> made some other way (a legacy file, a seed), not opened yet.</summary>
    public static TestDb At(string path) => new(path, null);

    public RigsightDb OpenWriter() { Db?.Dispose(); return Db = RigsightDb.OpenWriter(Path); }

    public RigsightDb Reader() => RigsightDb.OpenReader(Path) ?? throw new InvalidOperationException("no database");

    public SqliteConnection Raw(bool readOnly = false)
    {
        var conn = new SqliteConnection($"Data Source={Path};Pooling=False{(readOnly ? ";Mode=ReadOnly" : "")}");
        conn.Open();
        return conn;
    }

    public void Exec(string sql, params (string Name, object? Value)[] args)
    {
        using var conn = Raw();
        using var cmd = Command(conn, sql, args);
        cmd.ExecuteNonQuery();
    }

    public object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        using var conn = Raw(readOnly: true);
        using var cmd = Command(conn, sql, args);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    public long Count(string table, string where = "1") => (long)Scalar($"SELECT count(*) FROM {table} WHERE {where}")!;

    /// <summary>Every row, as column name → value (DBNull as null).</summary>
    public List<Dictionary<string, object?>> Rows(string sql, params (string Name, object? Value)[] args)
    {
        using var conn = Raw(readOnly: true);
        using var cmd = Command(conn, sql, args);
        using var r = cmd.ExecuteReader();
        var list = new List<Dictionary<string, object?>>();
        while (r.Read())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            list.Add(row);
        }
        return list;
    }

    /// <summary>Names of the tables and indexes, and each table's columns in order: the database's shape.</summary>
    public List<string> Shape()
    {
        var shape = new List<string>();
        foreach (var o in Rows("SELECT type, name, tbl_name FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name"))
        {
            shape.Add($"{o["type"]} {o["name"]} on {o["tbl_name"]}");
            if ((string)o["type"]! == "table")
                foreach (var c in Rows($"SELECT name, type, \"notnull\", pk FROM pragma_table_info('{o["name"]}')"))
                    shape.Add($"  {o["name"]}.{c["name"]} {c["type"]} notnull={c["notnull"]} pk={c["pk"]}");
        }
        return shape;
    }

    private static SqliteCommand Command(SqliteConnection conn, string sql, (string Name, object? Value)[] args)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public void Dispose() => Db?.Dispose();
}

/// <summary>Short ways to make rows and local times.</summary>
public static class Make
{
    /// <summary>Unix seconds of a local wall-clock time.</summary>
    public static long U(int year, int month, int day, int hour = 0, int minute = 0, int second = 0) =>
        TimeUtil.ToUnix(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local));

    public static long U(DateTime local) => TimeUtil.ToUnix(local);

    public static DateTime L(long unix) => TimeUtil.FromUnix(unix);

    /// <summary>Start of the local day / month / hour containing a Unix time, worked out by .NET (not SQLite).</summary>
    public static long DayStart(long unix) => TimeUtil.ToUnix(L(unix).Date);
    public static long MonthStart(long unix) { var d = L(unix); return TimeUtil.ToUnix(new DateTime(d.Year, d.Month, 1)); }

    public static SystemMinute Minute(long ts, double? cpu = 50, double? gpu = 45, double? cpuLoad = 20, double? gpuLoad = 10,
        long? app = null, int active = 60, int idle = 0, double? cpuMax = null, double? gpuMax = null, double? hot = null,
        double? mem = null, double? cpuPower = null, double? gpuPower = null, double? cpuVolt = null, double? gpuVolt = null, double? ram = null) => new()
    {
        Ts = ts, CpuTemp = cpu, CpuTempMax = cpuMax ?? (cpu + 2), GpuTemp = gpu, GpuTempMax = gpuMax ?? (gpu + 1),
        GpuHotMax = hot ?? (gpu + 10), GpuMemMax = mem, CpuLoad = cpuLoad, GpuLoad = gpuLoad, CpuPower = cpuPower, GpuPower = gpuPower,
        CpuVoltMax = cpuVolt, GpuVoltMax = gpuVolt, RamUsed = ram, FgApp = app, ActiveSec = active, IdleSec = idle,
    };

    public static AppHour Hour(long ts, long app, double fg = 600, double idle = 0, double bg = 0, double min = 0,
        double? cpuTemp = 55, double? gpuTemp = 50, double? mem = 500, double? cpu = 10) => new()
    {
        Ts = ts, AppId = app, FgSec = fg, IdleSec = idle, BgSec = bg, MinSec = min,
        CpuSum = cpu is null ? 0 : cpu.Value * 10, CpuN = cpu is null ? 0 : 10, CpuMax = cpu is null ? null : cpu + 5,
        MemSum = mem is null ? 0 : mem.Value * 10, MemN = mem is null ? 0 : 10, MemMax = mem is null ? null : mem + 50,
        CpuTempSum = cpuTemp is null ? 0 : cpuTemp.Value * 10, CpuTempN = cpuTemp is null ? 0 : 10, CpuTempMax = cpuTemp + 3,
        GpuTempSum = gpuTemp is null ? 0 : gpuTemp.Value * 10, GpuTempN = gpuTemp is null ? 0 : 10, GpuTempMax = gpuTemp + 3,
        GpuHotMax = gpuTemp + 12, GpuLoadSum = gpuTemp is null ? 0 : 400, GpuLoadN = gpuTemp is null ? 0 : 10,
    };

    public static SessionRow Session(long app, long start, long end, double? active = null, bool game = false, double? cpu = 70, double? gpu = 65) => new()
    {
        AppId = app, Start = start, End = end, ActiveSec = active ?? end - start, IsGame = game, CpuTempMax = cpu, GpuTempMax = gpu,
    };

    /// <summary>Settings as a fresh install has them (normalized like a saved file).</summary>
    public static RigsightSettings Settings() => SettingsStore.Deserialize(SettingsStore.Serialize(new RigsightSettings()));

    /// <summary>Runs <paramref name="action"/> with a fixed culture, so user-facing text formats the same on every machine.</summary>
    public static IDisposable Culture(string name = "en-US")
    {
        var (c, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        return new Restore(() => (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (c, ui));
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
