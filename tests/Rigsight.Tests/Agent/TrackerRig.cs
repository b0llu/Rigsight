using Microsoft.Data.Sqlite;
using Rigsight.Agent.Sensors;
using Rigsight.Agent.Tracking;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Agent;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class FakeClock(DateTimeOffset start)
{
    public DateTimeOffset Now { get; set; } = start;

    public long Unix => Now.ToUnixTimeSeconds();

    public void Advance(double seconds) => Now = Now.AddSeconds(seconds);

    /// <summary>A local time on a fixed date far from any daylight-saving change.</summary>
    public static DateTimeOffset At(int hour, int minute = 0, int second = 0, int day = 10, int month = 6, int year = 2026) =>
        new(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local));
}

/// <summary>
/// The agent's <see cref="Tracker"/> on a private database and a fake clock, driven second by second like the sampler
/// thread drives it (activity every second, sensors every 2 s, processes every 5 s).
/// </summary>
internal sealed class TrackerRig : IDisposable
{
    public string DbPath { get; }
    public FakeClock Clock { get; }
    public RigsightSettings Settings { get; }
    public RigsightDb Db { get; private set; }
    public AppResolver Apps { get; private set; }
    public Tracker Tracker { get; private set; }
    public List<(SessionRow Row, AppInfo App)> Ended { get; } = [];

    /// <summary>Apps with processes running (the one in front is added as it's used).</summary>
    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Apps with windows open (for background and minimized time).</summary>
    public Dictionary<string, WindowState> Windows { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resource use reported for running apps (default: a busy, windowed-size app).</summary>
    public Dictionary<string, (double Cpu, double MemMB)> Usage { get; } = new(StringComparer.OrdinalIgnoreCase);

    public KeyValues Keys { get; set; }

    /// <summary>Sensors are read every this many seconds (the agent: 2 with the app closed, 1 while it's open).</summary>
    public int SensorEvery { get; set; } = 2;
    private long _tick;

    public TrackerRig(DateTimeOffset? start = null, RigsightSettings? settings = null, string? dbPath = null,
        params (string Exe, string Name, AppCategory Category)[] apps)
    {
        DbPath = dbPath ?? Path.Combine(TestEnvironment.NewFolder("tracker"), "rigsight.db");
        Clock = new FakeClock(start ?? FakeClock.At(10));
        Settings = settings ?? new RigsightSettings();
        Db = RigsightDb.OpenWriter(DbPath);
        foreach (var (exe, name, category) in apps) Db.UpsertApp(exe, name, null, category);
        Apps = new AppResolver(Db);
        Tracker = NewTracker();
    }

    private Tracker NewTracker()
    {
        var t = new Tracker(Db, Apps, () => Clock.Now);
        t.SetSettings(Settings);
        t.SessionEnded += (row, app) => Ended.Add((row, app));
        t.Initialize();
        return t;
    }

    /// <summary>The agent stops (saving everything) and starts again on the same database.</summary>
    public void Restart()
    {
        Tracker.Flush(closeAllSessions: true);
        Db.Dispose();
        Db = RigsightDb.OpenWriter(DbPath);
        Apps = new AppResolver(Db);
        Tracker = NewTracker();
    }

    public static ActivitySample Front(string? exe, bool fullscreen = false, uint idleMs = 0, bool locked = false) =>
        new(0, exe, fullscreen, idleMs, locked);

    /// <summary>Using <paramref name="exe"/> (null: the desktop) for <paramref name="seconds"/>.</summary>
    public void Use(string? exe, int seconds, bool fullscreen = false, uint idleMs = 0, bool locked = false, double dt = 1)
    {
        if (exe is not null) Running.Add(exe);
        Run(Front(exe, fullscreen, idleMs, locked), seconds, dt);
    }

    /// <summary>Away from the PC with <paramref name="exe"/> in front.</summary>
    public void Away(string? exe, int seconds, double dt = 1) => Use(exe, seconds, idleMs: 60 * 60_000, dt: dt);

    public void Run(ActivitySample sample, int seconds, double dt = 1)
    {
        for (int i = 0; i < seconds; i++)
        {
            Clock.Advance(1);
            _tick++;
            Tracker.OnActivity(sample, dt);
            if (_tick % SensorEvery == 0) Tracker.OnSensors(Keys);
            if (_tick % 5 == 0) Tracker.OnProcesses(Snapshot(), new Dictionary<string, WindowState>(Windows, StringComparer.OrdinalIgnoreCase), 5);
        }
    }

    public ProcessSnapshot Snapshot()
    {
        var snapshot = new ProcessSnapshot();
        foreach (var exe in Running)
        {
            var (cpu, mem) = Usage.TryGetValue(exe, out var u) ? u : (5.0, 300.0);
            snapshot.Apps[exe] = new AppUsage { Exe = exe, FirstPid = 0, Count = 1, Cpu = cpu, MemMB = mem };
        }
        return snapshot;
    }

    /// <summary>The app quits: its process is gone at the next process sample.</summary>
    public void Quit(string exe)
    {
        Running.Remove(exe);
        Windows.Remove(exe);
        Tracker.OnProcesses(Snapshot(), new Dictionary<string, WindowState>(Windows, StringComparer.OrdinalIgnoreCase), 5);
    }

    public long AppId(string exe) => Db.LoadApps().Single(a => a.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)).Id;

    public bool HasApp(string exe) => Db.LoadApps().Any(a => a.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase));

    public List<SystemMinute> Minutes() => Db.GetMinutes(0, long.MaxValue);

    public List<AppHour> Hours() => Db.GetAppHours(0, long.MaxValue);

    public List<SessionRow> Sessions() => Db.GetSessions(0, long.MaxValue / 2);

    /// <summary>One app's hourly rows, summed.</summary>
    public AppHour HoursOf(string exe)
    {
        long id = AppId(exe);
        var list = Hours().Where(h => h.AppId == id).ToList();
        return new AppHour
        {
            AppId = id, FgSec = list.Sum(h => h.FgSec), IdleSec = list.Sum(h => h.IdleSec), BgSec = list.Sum(h => h.BgSec),
            MinSec = list.Sum(h => h.MinSec), CpuN = list.Sum(h => h.CpuN), CpuSum = list.Sum(h => h.CpuSum), MemN = list.Sum(h => h.MemN),
            MemSum = list.Sum(h => h.MemSum), CpuTempN = list.Sum(h => h.CpuTempN), CpuTempSum = list.Sum(h => h.CpuTempSum),
            GpuTempN = list.Sum(h => h.GpuTempN), GpuTempSum = list.Sum(h => h.GpuTempSum), GpuLoadN = list.Sum(h => h.GpuLoadN),
            GpuLoadSum = list.Sum(h => h.GpuLoadSum),
            CpuTempMax = list.Max(h => h.CpuTempMax), GpuTempMax = list.Max(h => h.GpuTempMax), GpuHotMax = list.Max(h => h.GpuHotMax),
            CpuPowerMax = list.Max(h => h.CpuPowerMax), GpuPowerMax = list.Max(h => h.GpuPowerMax), CpuMax = list.Max(h => h.CpuMax),
            MemMax = list.Max(h => h.MemMax), CpuVoltMax = list.Max(h => h.CpuVoltMax), GpuVoltMax = list.Max(h => h.GpuVoltMax),
        };
    }

    /// <summary>Checks the rollups agree with what they roll up (see <see cref="DbInvariants"/>).</summary>
    public void AssertInvariants() => DbInvariants.Check(DbPath);

    public void Dispose() => Db.Dispose();
}

/// <summary>
/// What must always hold in a database the agent wrote: app_month is exactly the sum of its app_hour rows, and
/// system_day exactly the aggregate of its system_minute rows.
/// </summary>
internal static class DbInvariants
{
    private const string MonthOf = "CAST(strftime('%s', ts, 'unixepoch', 'localtime', 'start of month', 'utc') AS INTEGER)";
    private const string DayOf = "CAST(strftime('%s', ts, 'unixepoch', 'localtime', 'start of day', 'utc') AS INTEGER)";

    private const string HourSums = """
        sum(fg_sec), sum(idle_sec), sum(bg_sec), sum(min_sec), sum(cpu_sum), sum(cpu_n), max(cpu_max),
        sum(mem_sum), sum(mem_n), max(mem_max), sum(cpu_temp_sum), sum(cpu_temp_n), max(cpu_temp_max),
        sum(gpu_temp_sum), sum(gpu_temp_n), max(gpu_temp_max), max(gpu_hot_max), max(cpu_power_max), max(gpu_power_max),
        max(cpu_volt_max), max(gpu_volt_max), sum(gpu_load_sum), sum(gpu_load_n)
        """;

    private const string MonthCols = """
        fg_sec, idle_sec, bg_sec, min_sec, cpu_sum, cpu_n, cpu_max, mem_sum, mem_n, mem_max, cpu_temp_sum, cpu_temp_n, cpu_temp_max,
        gpu_temp_sum, gpu_temp_n, gpu_temp_max, gpu_hot_max, cpu_power_max, gpu_power_max, cpu_volt_max, gpu_volt_max, gpu_load_sum, gpu_load_n
        """;

    private const string DaySums = """
        count(*), total(active_sec), total(idle_sec), total(cpu_temp), count(cpu_temp), total(gpu_temp), count(gpu_temp),
        total(cpu_load), count(cpu_load), total(gpu_load), count(gpu_load), max(cpu_temp_max), max(gpu_temp_max), max(gpu_hot_max),
        max(cpu_volt_max), max(gpu_volt_max), max(cpu_power), max(gpu_power)
        """;

    private const string DayCols = """
        minutes, active_sec, idle_sec, cpu_temp_sum, cpu_temp_n, gpu_temp_sum, gpu_temp_n, cpu_load_sum, cpu_load_n, gpu_load_sum,
        gpu_load_n, cpu_temp_max, gpu_temp_max, gpu_hot_max, cpu_volt_max, gpu_volt_max, cpu_power_max, gpu_power_max
        """;

    public static void Check(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        Same(conn, "app_month", $"SELECT month, app_id, {MonthCols} FROM app_month", $"SELECT {MonthOf}, app_id, {HourSums} FROM app_hour GROUP BY 1, 2", 2);
        Same(conn, "system_day", $"SELECT day, {DayCols} FROM system_day", $"SELECT {DayOf}, {DaySums} FROM system_minute GROUP BY 1", 1);
    }

    private static void Same(SqliteConnection conn, string what, string stored, string computed, int keyColumns)
    {
        var a = Read(conn, stored, keyColumns);
        var b = Read(conn, computed, keyColumns);
        Assert.True(a.Keys.ToHashSet().SetEquals(b.Keys), $"{what}: rows {string.Join(",", a.Keys)} vs expected {string.Join(",", b.Keys)}");
        foreach (var (key, row) in a)
        {
            var expected = b[key];
            for (int i = 0; i < row.Length; i++)
            {
                bool same = row[i] is null ? expected[i] is null
                    : expected[i] is double e && Math.Abs(row[i]!.Value - e) <= 1e-6 * Math.Max(1, Math.Abs(e));
                Assert.True(same, $"{what} {key} column {i}: {row[i]} vs expected {expected[i]}");
            }
        }
    }

    private static Dictionary<string, double?[]> Read(SqliteConnection conn, string sql, int keyColumns)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, double?[]>();
        while (r.Read())
        {
            string key = string.Join("/", Enumerable.Range(0, keyColumns).Select(i => r.GetInt64(i)));
            map[key] = [.. Enumerable.Range(keyColumns, r.FieldCount - keyColumns).Select(i => r.IsDBNull(i) ? (double?)null : r.GetDouble(i))];
        }
        return map;
    }
}
