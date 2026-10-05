using Microsoft.Data.Sqlite;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;
using static Rigsight.Tests.Data.Make;

namespace Rigsight.Tests.Data;

public sealed class SchemaTests
{
    private static readonly string[] HourValueColumns =
        ["fg_sec", "idle_sec", "bg_sec", "min_sec", "cpu_sum", "cpu_n", "cpu_max", "mem_sum", "mem_n", "mem_max",
         "cpu_temp_sum", "cpu_temp_n", "cpu_temp_max", "gpu_temp_sum", "gpu_temp_n", "gpu_temp_max", "gpu_hot_max",
         "cpu_power_max", "gpu_power_max", "cpu_volt_max", "gpu_volt_max", "gpu_load_sum", "gpu_load_n"];

    /// <summary>Every table and its columns in order, as the current version makes them.</summary>
    public static readonly Dictionary<string, string[]> Tables = new()
    {
        ["meta"] = ["key", "value"],
        ["apps"] = ["id", "exe", "name", "path", "category", "first_seen"],
        ["system_minute"] = ["ts", "cpu_temp", "cpu_temp_max", "gpu_temp", "gpu_temp_max", "gpu_hot_max", "cpu_load", "gpu_load",
            "cpu_power", "gpu_power", "cpu_volt_max", "gpu_volt_max", "ram_used", "fg_app", "active_sec", "idle_sec", "gpu_mem_max", "cpu_app", "gpu_app",
            "cpu_clock", "gpu_clock"],
        ["app_hour"] = ["ts", "app_id", .. HourValueColumns],
        ["app_month"] = ["month", "app_id", .. HourValueColumns],
        ["system_day"] = ["day", "minutes", "active_sec", "idle_sec", "cpu_temp_sum", "cpu_temp_n", "gpu_temp_sum", "gpu_temp_n",
            "cpu_load_sum", "cpu_load_n", "gpu_load_sum", "gpu_load_n", "cpu_temp_max", "gpu_temp_max", "gpu_hot_max",
            "cpu_volt_max", "gpu_volt_max", "cpu_power_max", "gpu_power_max",
            "idle_cpu_sum", "idle_cpu_n", "idle_gpu_sum", "idle_gpu_n", "load_cpu_sum", "load_cpu_n", "load_gpu_sum", "load_gpu_n",
            "cpu_clock_sum", "cpu_clock_n", "gpu_clock_sum", "gpu_clock_n"],
        ["fans"] = ["id", "sensor", "name", "hardware"],
        ["fan_minute"] = ["ts", "fan", "rpm_avg", "rpm_max"],
        ["fan_curve_day"] = ["day", "fan", "app", "temp", "n", "rpm_sum"],
        ["fan_day"] = ["day", "fan", "rpm_sum", "rpm_n", "rpm_max", "idle_sum", "idle_n"],
        ["heat_day"] = ["day", "app", "n", "gpu_sum", "gpu_n", "cpu_sum", "cpu_n", "power_sum", "power_n"],
        ["sessions"] = ["id", "app_id", "start", "end", "active_sec", "cpu_temp_max", "gpu_temp_max", "is_game"],
        ["crashes"] = ["id", "ts", "kind", "app_exe", "app_path", "module", "code", "detail", "during_sleep"],
        ["drive_day"] = ["day", "drive", "used_gb", "total_gb"],
        ["net_minute"] = ["ts", "down", "up", "bg_down", "bg_up", "away_down", "away_up", "lan", "steady", "app"],
        ["net_day"] = ["day", "minutes", "down", "up", "bg_down", "bg_up", "away_down", "away_up", "lan", "best", "best_ts"],
        ["net_app_hour"] = ["ts", "app", "down", "up", "bg_down", "bg_up", "away_down", "away_up", "game_down", "lan"],
        ["net_app_day"] = ["day", "app", "down", "up", "bg_down", "bg_up", "away_down", "away_up", "game_down", "lan"],
        ["net_transfer"] = ["start", "app", "end", "bytes", "sec"],
        ["net_drop"] = ["start", "end", "kind"],
        ["changes"] = ["id", "ts", "kind", "subject", "title", "was", "now", "from_ts"],
        ["inventory"] = ["kind", "key", "name", "value"],
    };

    public static readonly Dictionary<string, string> Indexes = new()
    {
        ["ix_sessions_start"] = "sessions(start)",
        ["ix_sessions_app"] = "sessions(app_id, start)",
        ["ix_crashes_ts"] = "crashes(ts)",
        ["ix_changes_ts"] = "changes(ts)",
    };

    public static TheoryData<string> TableNames => [.. Tables.Keys];

    [Theory]
    [MemberData(nameof(TableNames))]
    public void A_new_database_has_the_table_with_every_column_in_order(string table)
    {
        using var t = new TestDb();
        var columns = t.Rows($"SELECT name FROM pragma_table_info('{table}')").Select(r => (string)r["name"]!).ToArray();
        Assert.Equal(Tables[table], columns);
    }

    [Fact]
    public void A_new_database_has_no_tables_beyond_the_known_ones()
    {
        using var t = new TestDb();
        var tables = t.Rows("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'").Select(r => (string)r["name"]!);
        Assert.Equal(Tables.Keys.Order(), tables.Order());
    }

    [Fact]
    public void A_new_database_has_every_index()
    {
        using var t = new TestDb();
        var indexes = t.Rows("SELECT name, tbl_name FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_%'")
            .ToDictionary(r => (string)r["name"]!, r => (string)r["tbl_name"]!);
        Assert.Equal(Indexes.Keys.Order(), indexes.Keys.Order());
        foreach (var (name, on) in Indexes)
        {
            var cols = t.Rows($"SELECT name FROM pragma_index_info('{name}') ORDER BY seqno").Select(r => (string)r["name"]!);
            Assert.Equal(on, $"{indexes[name]}({string.Join(", ", cols)})");
        }
    }

    [Theory]
    [InlineData("apps", "exe")]
    [InlineData("system_minute", "ts")]
    [InlineData("app_hour", "ts,app_id")]
    [InlineData("app_month", "month,app_id")]
    [InlineData("system_day", "day")]
    [InlineData("sessions", "id")]
    [InlineData("crashes", "id")]
    [InlineData("drive_day", "day,drive")]
    [InlineData("meta", "key")]
    [InlineData("fans", "id")]
    [InlineData("fan_minute", "ts,fan")]
    [InlineData("fan_day", "day,fan")]
    [InlineData("fan_curve_day", "day,fan,app,temp")]
    [InlineData("heat_day", "day,app")]
    [InlineData("net_minute", "ts")]
    [InlineData("net_day", "day")]
    [InlineData("net_app_hour", "ts,app")]
    [InlineData("net_app_day", "day,app")]
    [InlineData("net_transfer", "start,app")]
    [InlineData("net_drop", "start")]
    [InlineData("changes", "id")]
    [InlineData("inventory", "kind,key")]
    public void Tables_are_keyed_as_the_queries_expect(string table, string key)
    {
        using var t = new TestDb();
        var pk = t.Rows($"SELECT name, pk FROM pragma_table_info('{table}') WHERE pk > 0 ORDER BY pk").Select(r => (string)r["name"]!).ToList();
        if (table == "apps") Assert.Equal(["id"], pk); // exe is UNIQUE instead
        else Assert.Equal(key.Split(','), pk);
    }

    [Fact]
    public void Crashes_are_unique_by_kind_time_and_exe()
    {
        using var t = new TestDb();
        var unique = t.Rows("SELECT name FROM pragma_index_list('crashes') WHERE \"unique\" = 1").Select(r => (string)r["name"]!).Single();
        Assert.Equal(["kind", "ts", "app_exe"], t.Rows($"SELECT name FROM pragma_index_info('{unique}') ORDER BY seqno").Select(r => (string)r["name"]!));
    }

    [Fact]
    public void App_exe_names_are_unique_ignoring_case()
    {
        using var t = new TestDb();
        long a = t.Db.UpsertApp("Game.exe", "Game", null, AppCategory.Game);
        long b = t.Db.UpsertApp("GAME.EXE", "Game", null, AppCategory.Game);
        Assert.Equal(a, b);
        Assert.Equal(1, t.Count("apps"));
    }

    [Fact]
    public void A_new_database_records_the_schema_version_and_an_empty_session_bound()
    {
        using var t = new TestDb();
        Assert.Equal("1", t.Db.GetMeta("schema"));
        Assert.Equal("0", t.Db.GetMeta("max_session_sec"));
    }

    [Fact]
    public void A_new_database_uses_write_ahead_logging_so_the_app_can_read_while_the_agent_writes()
    {
        using var t = new TestDb();
        Assert.Equal("wal", t.Scalar("PRAGMA journal_mode"));
    }

    [Fact]
    public void A_new_database_is_empty()
    {
        using var t = new TestDb();
        foreach (var table in Tables.Keys.Where(k => k != "meta")) Assert.Equal(0, t.Count(table));
        Assert.Null(t.Db.FirstDataTime());
        Assert.Null(t.Db.FirstMinuteTime());
        Assert.Null(t.Db.FirstCrashTime());
    }

    [Fact]
    public void Opening_again_changes_nothing()
    {
        using var t = new TestDb();
        History.Sample().WriteFresh(t.Db);
        var shape = t.Shape();
        var month = t.Rows("SELECT * FROM app_month ORDER BY month, app_id");
        var day = t.Rows("SELECT * FROM system_day ORDER BY day");
        var meta = t.Rows("SELECT * FROM meta ORDER BY key");
        for (int i = 0; i < 3; i++)
        {
            t.OpenWriter();
            Assert.Equal(shape, t.Shape());
            Assert.Equal(month, t.Rows("SELECT * FROM app_month ORDER BY month, app_id"));
            Assert.Equal(day, t.Rows("SELECT * FROM system_day ORDER BY day"));
            Assert.Equal(meta, t.Rows("SELECT * FROM meta ORDER BY key"));
        }
    }

    [Fact]
    public void Two_writers_opening_at_once_both_see_one_schema()
    {
        using var t = new TestDb();
        using var second = RigsightDb.OpenWriter(t.Path);
        t.Db.WriteMinute(Minute(U(2025, 3, 1, 10)));
        Assert.Single(second.GetMinutes(U(2025, 3, 1), U(2025, 3, 2)));
    }

    [Fact]
    public void Open_writer_creates_missing_folders()
    {
        var path = Path.Combine(TestEnvironment.NewFolder("nested"), "a", "b", "rigsight.db");
        using (RigsightDb.OpenWriter(path)) { }
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Open_reader_returns_null_without_creating_a_file_when_there_is_no_database()
    {
        var path = Path.Combine(TestEnvironment.NewFolder("none"), "rigsight.db");
        Assert.Null(RigsightDb.OpenReader(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void The_reader_cannot_change_anything()
    {
        using var t = new TestDb();
        using var reader = t.Reader();
        Assert.Throws<SqliteException>(() => reader.SetMeta("x", "y"));
        Assert.Throws<SqliteException>(() => reader.WriteMinute(Minute(U(2025, 1, 1, 10))));
        Assert.Null(t.Db.GetMeta("x"));
    }

    [Fact]
    public void The_reader_sees_what_the_writer_committed_while_both_are_open()
    {
        using var t = new TestDb();
        using var reader = t.Reader();
        Assert.Empty(reader.GetMinutes(U(2025, 1, 1), U(2025, 1, 2)));
        t.Db.WriteMinute(Minute(U(2025, 1, 1, 10)));
        Assert.Single(reader.GetMinutes(U(2025, 1, 1), U(2025, 1, 2)));
    }

    [Fact]
    public void The_reader_does_not_see_an_uncommitted_transaction()
    {
        using var t = new TestDb();
        using var reader = t.Reader();
        using (var tx = t.Db.BeginTransaction())
        {
            t.Db.WriteMinute(Minute(U(2025, 1, 1, 10)));
            Assert.Empty(reader.GetMinutes(U(2025, 1, 1), U(2025, 1, 2)));
            tx.Rollback();
        }
        Assert.Empty(t.Db.GetMinutes(U(2025, 1, 1), U(2025, 1, 2)));
        Assert.Empty(t.Db.GetSystemDays(U(2025, 1, 1), U(2025, 1, 2))!);
    }

    [Fact]
    public void Meta_values_round_trip_and_overwrite()
    {
        using var t = new TestDb();
        Assert.Null(t.Db.GetMeta("extremes"));
        t.Db.SetMeta("extremes", "a");
        t.Db.SetMeta("extremes", "b");
        Assert.Equal("b", t.Db.GetMeta("extremes"));
        using var reader = t.Reader();
        Assert.Equal("b", reader.GetMeta("extremes"));
    }
}
