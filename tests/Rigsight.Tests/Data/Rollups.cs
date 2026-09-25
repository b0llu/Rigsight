namespace Rigsight.Tests.Data;

/// <summary>
/// The rollup invariants, checked against the raw tables with the day and month worked out by .NET (not by the
/// SQL under test): app_month is app_hour added up per local month, system_day is system_minute added up per local
/// day, and the longest-session bound covers every session.
/// </summary>
public static class Rollups
{
    public static readonly string[] HourSumColumns =
        ["fg_sec", "idle_sec", "bg_sec", "min_sec", "cpu_sum", "cpu_n", "mem_sum", "mem_n", "cpu_temp_sum", "cpu_temp_n",
         "gpu_temp_sum", "gpu_temp_n", "gpu_load_sum", "gpu_load_n"];

    public static readonly string[] HourMaxColumns =
        ["cpu_max", "mem_max", "cpu_temp_max", "gpu_temp_max", "gpu_hot_max", "cpu_power_max", "gpu_power_max", "cpu_volt_max", "gpu_volt_max"];

    // system_day column ← (aggregate, system_minute column)
    private static readonly (string Day, string Agg, string Minute)[] DayColumns =
    [
        ("active_sec", "total", "active_sec"), ("idle_sec", "total", "idle_sec"),
        ("cpu_temp_sum", "total", "cpu_temp"), ("cpu_temp_n", "count", "cpu_temp"),
        ("gpu_temp_sum", "total", "gpu_temp"), ("gpu_temp_n", "count", "gpu_temp"),
        ("cpu_load_sum", "total", "cpu_load"), ("cpu_load_n", "count", "cpu_load"),
        ("gpu_load_sum", "total", "gpu_load"), ("gpu_load_n", "count", "gpu_load"),
        ("cpu_temp_max", "max", "cpu_temp_max"), ("gpu_temp_max", "max", "gpu_temp_max"), ("gpu_hot_max", "max", "gpu_hot_max"),
        ("cpu_volt_max", "max", "cpu_volt_max"), ("gpu_volt_max", "max", "gpu_volt_max"),
        ("cpu_power_max", "max", "cpu_power"), ("gpu_power_max", "max", "gpu_power"),
    ];

    public static void AssertAll(TestDb db, string when = "")
    {
        AssertAppMonth(db, when);
        AssertSystemDay(db, when);
        AssertMaxSession(db, when);
    }

    public static void AssertAppMonth(TestDb db, string when = "")
    {
        var expected = db.Rows("SELECT * FROM app_hour")
            .GroupBy(r => (Month: Make.MonthStart((long)r["ts"]!), App: (long)r["app_id"]!))
            .ToDictionary(g => g.Key, g => g.ToList());
        var actual = db.Rows("SELECT * FROM app_month").ToDictionary(r => (Month: (long)r["month"]!, App: (long)r["app_id"]!));
        Assert.True(expected.Keys.ToHashSet().SetEquals(actual.Keys),
            $"{when}: app_month has months/apps {Keys(actual.Keys)} but app_hour adds up to {Keys(expected.Keys)}");
        foreach (var (key, hours) in expected)
        {
            var row = actual[key];
            foreach (var c in HourSumColumns) Near(hours.Sum(h => Num(h[c]) ?? 0), Num(row[c]) ?? 0, $"{when}: app_month {key}.{c}");
            foreach (var c in HourMaxColumns) Same(MaxOf(hours.Select(h => Num(h[c]))), Num(row[c]), $"{when}: app_month {key}.{c}");
        }
    }

    public static void AssertSystemDay(TestDb db, string when = "")
    {
        var expected = db.Rows("SELECT * FROM system_minute").GroupBy(r => Make.DayStart((long)r["ts"]!)).ToDictionary(g => g.Key, g => g.ToList());
        var actual = db.Rows("SELECT * FROM system_day").ToDictionary(r => (long)r["day"]!);
        Assert.True(expected.Keys.ToHashSet().SetEquals(actual.Keys),
            $"{when}: system_day has days {Days(actual.Keys)} but system_minute has {Days(expected.Keys)}");
        foreach (var (day, minutes) in expected)
        {
            var row = actual[day];
            Assert.Equal((long)minutes.Count, (long)row["minutes"]!);
            foreach (var (col, agg, src) in DayColumns)
            {
                var values = minutes.Select(m => Num(m[src])).ToList();
                string what = $"{when}: system_day {Make.L(day):yyyy-MM-dd}.{col}";
                switch (agg)
                {
                    case "total": Near(values.Sum(v => v ?? 0), Num(row[col])!.Value, what); break;
                    case "count": Assert.True(values.Count(v => v is not null) == Num(row[col]), what); break;
                    default: Same(MaxOf(values), Num(row[col]), what); break;
                }
            }
        }
    }

    public static void AssertMaxSession(TestDb db, string when = "")
    {
        long longest = (long)(db.Scalar("SELECT coalesce(max(end - start), 0) FROM sessions") ?? 0L);
        var meta = db.Scalar("SELECT value FROM meta WHERE key = 'max_session_sec'") as string;
        Assert.True(meta is not null, $"{when}: max_session_sec is missing");
        Assert.True(long.Parse(meta) >= longest, $"{when}: max_session_sec {meta} is shorter than a stored session ({longest} s)");
    }

    public static double? Num(object? v) => v is null ? null : Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);

    public static double? MaxOf(IEnumerable<double?> values)
    {
        double? max = null;
        foreach (var v in values) if (v is double d && (max is null || d > max)) max = d;
        return max;
    }

    public static void Near(double expected, double actual, string what) =>
        Assert.True(Math.Abs(expected - actual) <= 1e-6 * Math.Max(1, Math.Abs(expected)), $"{what}: expected {expected}, stored {actual}");

    public static void Same(double? expected, double? actual, string what) =>
        Assert.True(expected is null ? actual is null : actual is double a && Math.Abs(a - expected.Value) < 1e-9, $"{what}: expected {expected?.ToString() ?? "NULL"}, stored {actual?.ToString() ?? "NULL"}");

    private static string Keys(IEnumerable<(long Month, long App)> keys) =>
        string.Join(", ", keys.OrderBy(k => k).Take(12).Select(k => $"{Make.L(k.Month):yyyy-MM}/{k.App}"));

    private static string Days(IEnumerable<long> days) => string.Join(", ", days.Order().Take(12).Select(d => $"{Make.L(d):yyyy-MM-dd HH:mm}"));
}
