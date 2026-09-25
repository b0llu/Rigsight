using Rigsight.Core.Data;
using static Rigsight.Tests.Data.Make;

namespace Rigsight.Tests.Data;

/// <summary>
/// GetAppTotals reads whole months from app_month and only the partial months from app_hour: for any range it must
/// give exactly what adding up every hour in the range gives.
/// </summary>
public sealed class AppTotalsTests
{
    /// <summary>Checks GetAppTotals(from, to) against GetAppHours(from, to) added up per app, every column.</summary>
    internal static void AssertTotalsMatchHours(RigsightDb db, long from, long to, string when = "")
    {
        string range = $"{when} [{L(from):yyyy-MM-dd HH:mm:ss}, {L(to):yyyy-MM-dd HH:mm:ss})";
        var expected = db.GetAppHours(from, to).GroupBy(h => h.AppId).ToDictionary(g => g.Key, g => g.ToList());
        var totals = db.GetAppTotals(from, to);
        Assert.True(totals.Count == totals.Select(x => x.AppId).Distinct().Count(), $"{range}: an app appears twice");
        var actual = totals.ToDictionary(x => x.AppId);
        Assert.True(expected.Keys.ToHashSet().SetEquals(actual.Keys),
            $"{range}: apps {string.Join(",", actual.Keys.Order())} but the hours have {string.Join(",", expected.Keys.Order())}");
        foreach (var (app, hours) in expected)
        {
            var t = actual[app];
            string what = $"{range} app {app}";
            Assert.Equal(0, t.Ts);
            Rollups.Near(hours.Sum(h => h.FgSec), t.FgSec, what + " fg");
            Rollups.Near(hours.Sum(h => h.IdleSec), t.IdleSec, what + " idle");
            Rollups.Near(hours.Sum(h => h.BgSec), t.BgSec, what + " bg");
            Rollups.Near(hours.Sum(h => h.MinSec), t.MinSec, what + " min");
            Rollups.Near(hours.Sum(h => h.CpuSum), t.CpuSum, what + " cpu sum");
            Assert.True(hours.Sum(h => h.CpuN) == t.CpuN, what + " cpu n");
            Rollups.Near(hours.Sum(h => h.MemSum), t.MemSum, what + " mem sum");
            Assert.True(hours.Sum(h => h.MemN) == t.MemN, what + " mem n");
            Rollups.Near(hours.Sum(h => h.CpuTempSum), t.CpuTempSum, what + " cpu temp sum");
            Assert.True(hours.Sum(h => h.CpuTempN) == t.CpuTempN, what + " cpu temp n");
            Rollups.Near(hours.Sum(h => h.GpuTempSum), t.GpuTempSum, what + " gpu temp sum");
            Assert.True(hours.Sum(h => h.GpuTempN) == t.GpuTempN, what + " gpu temp n");
            Rollups.Near(hours.Sum(h => h.GpuLoadSum), t.GpuLoadSum, what + " gpu load sum");
            Assert.True(hours.Sum(h => h.GpuLoadN) == t.GpuLoadN, what + " gpu load n");
            Rollups.Same(Rollups.MaxOf(hours.Select(h => h.CpuMax)), t.CpuMax, what + " cpu max");
            Rollups.Same(Rollups.MaxOf(hours.Select(h => h.MemMax)), t.MemMax, what + " mem max");
            Rollups.Same(Rollups.MaxOf(hours.Select(h => h.CpuTempMax)), t.CpuTempMax, what + " cpu temp max");
            Rollups.Same(Rollups.MaxOf(hours.Select(h => h.GpuTempMax)), t.GpuTempMax, what + " gpu temp max");
            Rollups.Same(Rollups.MaxOf(hours.Select(h => h.GpuHotMax)), t.GpuHotMax, what + " hot max");
            Rollups.Same(Rollups.MaxOf(hours.Select(h => h.CpuPowerMax)), t.CpuPowerMax, what + " cpu power");
            Rollups.Same(Rollups.MaxOf(hours.Select(h => h.GpuPowerMax)), t.GpuPowerMax, what + " gpu power");
            Rollups.Same(Rollups.MaxOf(hours.Select(h => h.CpuVoltMax)), t.CpuVoltMax, what + " cpu volt");
            Rollups.Same(Rollups.MaxOf(hours.Select(h => h.GpuVoltMax)), t.GpuVoltMax, what + " gpu volt");
        }
    }

    /// <summary>Ranges relative to the seed's "now", by name (so each shows up as its own test case).</summary>
    public static TheoryData<string, string> Ranges
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var profile in new[] { "small", "typical" })
                foreach (var name in RangeNames) data.Add(profile, name);
            return data;
        }
    }

    private static readonly string[] RangeNames =
    [
        "all time", "everything to a month start", "this month", "last month", "last 3 whole months", "one whole year",
        "calendar year so far", "mid-month to mid-month", "inside one month", "one day", "one hour", "one second",
        "from a month start to mid-month", "mid-month to a month start", "across the new year", "a month start to itself",
        "empty (from equals to)", "backwards (to before from)", "before any history", "after all history",
        "unaligned seconds across months", "from one second after a month start", "to one second before a month end",
        "last 7 days", "last 30 days", "last 365 days", "two years spanning everything",
    ];

    private static (long From, long To) Range(string name, DateTime now)
    {
        var today = now.Date;
        var month = new DateTime(today.Year, today.Month, 1);
        return name switch
        {
            "all time" => (U(2000, 1, 1), U(today.AddDays(1))),
            "everything to a month start" => (U(2000, 1, 1), U(month)),
            "this month" => (U(month), U(month.AddMonths(1))),
            "last month" => (U(month.AddMonths(-1)), U(month)),
            "last 3 whole months" => (U(month.AddMonths(-3)), U(month)),
            "one whole year" => (U(new DateTime(today.Year - 1, 1, 1)), U(new DateTime(today.Year, 1, 1))),
            "calendar year so far" => (U(new DateTime(today.Year, 1, 1)), U(today.AddDays(1))),
            "mid-month to mid-month" => (U(month.AddMonths(-4).AddDays(14).AddHours(13)), U(month.AddMonths(-1).AddDays(9).AddHours(7))),
            "inside one month" => (U(month.AddMonths(-1).AddDays(3)), U(month.AddMonths(-1).AddDays(20))),
            "one day" => (U(today.AddDays(-1)), U(today)),
            "one hour" => (U(today.AddHours(-3)), U(today.AddHours(-2))),
            "one second" => (U(today.AddDays(-1).AddHours(12)), U(today.AddDays(-1).AddHours(12)) + 1),
            "from a month start to mid-month" => (U(month.AddMonths(-2)), U(month.AddMonths(-1).AddDays(15))),
            "mid-month to a month start" => (U(month.AddMonths(-3).AddDays(10)), U(month.AddMonths(-1))),
            "across the new year" => (U(new DateTime(today.Year - 1, 12, 17)), U(new DateTime(today.Year, 1, 12))),
            "a month start to itself" => (U(month), U(month)),
            "empty (from equals to)" => (U(today.AddDays(-2).AddHours(15)), U(today.AddDays(-2).AddHours(15))),
            "backwards (to before from)" => (U(today), U(month.AddMonths(-5))),
            "before any history" => (U(2001, 1, 1), U(2001, 6, 1)),
            "after all history" => (U(today.AddDays(2)), U(today.AddYears(1))),
            "unaligned seconds across months" => (U(month.AddMonths(-6)) + 12345, U(month.AddMonths(-1)) - 777),
            "from one second after a month start" => (U(month.AddMonths(-3)) + 1, U(month)),
            "to one second before a month end" => (U(month.AddMonths(-3)), U(month) - 1),
            "last 7 days" => (U(today.AddDays(-7)), U(today.AddDays(1))),
            "last 30 days" => (U(today.AddDays(-30)), U(today.AddDays(1))),
            "last 365 days" => (U(today.AddDays(-365)), U(today.AddDays(1))),
            "two years spanning everything" => (U(today.AddYears(-2)), U(today.AddDays(1))),
            _ => throw new ArgumentException(name),
        };
    }

    [Theory]
    [MemberData(nameof(Ranges))]
    public void Totals_equal_the_hours_added_up(string profile, string range)
    {
        using var db = Seeds.Open(profile);
        var (from, to) = Range(range, profile == "small" ? Seeds.SmallNow : Seeds.TypicalNow);
        AssertTotalsMatchHours(db, from, to, range);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Totals_equal_the_hours_added_up_for_random_ranges(int seed)
    {
        using var db = Seeds.Open("typical");
        var rnd = new Random(seed);
        long start = U(Seeds.TypicalNow.AddDays(-400)), end = U(Seeds.TypicalNow.AddDays(2));
        for (int i = 0; i < 40; i++)
        {
            long a = start + (long)(rnd.NextDouble() * (end - start)), b = start + (long)(rnd.NextDouble() * (end - start));
            // Now and then snap to a local month or day start, where the month logic switches.
            if (rnd.Next(3) == 0) a = MonthStart(a);
            if (rnd.Next(3) == 0) b = DayStart(b);
            AssertTotalsMatchHours(db, Math.Min(a, b), Math.Max(a, b), $"random {i}");
        }
    }

    [Fact]
    public void Totals_of_an_empty_database_are_empty()
    {
        using var t = new TestDb();
        Assert.Empty(t.Db.GetAppTotals(0, U(2030, 1, 1)));
        Assert.Empty(t.Db.GetAppTotals(U(2025, 1, 1), U(2025, 1, 1)));
    }

    [Fact]
    public void An_hour_at_a_month_start_belongs_to_that_month_and_the_last_hour_to_the_month_before()
    {
        using var t = new TestDb();
        t.Db.AddAppHour(Hour(U(2025, 1, 31, 23), 1, fg: 1));
        t.Db.AddAppHour(Hour(U(2025, 2, 1, 0), 1, fg: 10));
        t.Db.AddAppHour(Hour(U(2025, 2, 28, 23), 1, fg: 100));
        t.Db.AddAppHour(Hour(U(2025, 3, 1, 0), 1, fg: 1000));
        Assert.Equal(110, Assert.Single(t.Db.GetAppTotals(U(2025, 2, 1), U(2025, 3, 1))).FgSec);
        Assert.Equal(1111, Assert.Single(t.Db.GetAppTotals(U(2025, 1, 1), U(2025, 4, 1))).FgSec);
        Assert.Equal(1110, Assert.Single(t.Db.GetAppTotals(U(2025, 1, 31, 23) + 1, U(2025, 3, 1, 0) + 1)).FgSec);
        Assert.Equal(1, Assert.Single(t.Db.GetAppTotals(U(2025, 1, 31, 23), U(2025, 2, 1))).FgSec);
        foreach (var (f, to) in new[] { (U(2025, 1, 1), U(2025, 2, 1)), (U(2025, 2, 1), U(2025, 2, 1)), (U(2025, 2, 15), U(2025, 3, 15)) })
            AssertTotalsMatchHours(t.Db, f, to);
    }

    [Fact]
    public void Monthly_totals_cover_every_app_even_ones_only_in_a_partial_month()
    {
        using var t = new TestDb();
        t.Db.AddAppHour(Hour(U(2025, 1, 10, 10), 1, fg: 5));
        t.Db.AddAppHour(Hour(U(2025, 2, 10, 10), 2, fg: 6));
        t.Db.AddAppHour(Hour(U(2025, 3, 10, 10), 3, fg: 7));
        var totals = t.Db.GetAppTotals(U(2025, 1, 5), U(2025, 3, 20)).OrderBy(x => x.AppId).Select(x => (x.AppId, x.FgSec));
        Assert.Equal([(1L, 5.0), (2L, 6.0), (3L, 7.0)], totals);
    }

    [Fact]
    public void Monthly_chart_rows_are_the_month_totals_and_hourly_ones_the_hours()
    {
        using var t = new TestDb();
        t.Db.AddAppHour(Hour(U(2025, 1, 10, 10), 1, fg: 5));
        t.Db.AddAppHour(Hour(U(2025, 1, 11, 10), 1, fg: 6));
        t.Db.AddAppHour(Hour(U(2025, 2, 10, 10), 1, fg: 7));
        t.Db.AddAppHour(Hour(U(2025, 2, 10, 10), 2, fg: 100));
        t.Db.AddAppHour(Hour(U(2025, 2, 11, 10), 1, fg: 0, idle: 30)); // no time in front: not a bar
        Assert.Equal([(U(2025, 1, 1), 11.0), (U(2025, 2, 1), 7.0)], t.Db.GetAppTime(1, U(2025, 1, 1), U(2025, 3, 1), monthly: true).OrderBy(x => x.Ts));
        Assert.Equal([(U(2025, 2, 1), 7.0)], t.Db.GetAppTime(1, U(2025, 2, 1), U(2025, 3, 1), monthly: true));
        Assert.Equal([(U(2025, 1, 10, 10), 5.0), (U(2025, 1, 11, 10), 6.0)], t.Db.GetAppTime(1, U(2025, 1, 1), U(2025, 2, 1), monthly: false).OrderBy(x => x.Ts));
        Assert.Empty(t.Db.GetAppTime(1, U(2025, 2, 11), U(2025, 2, 12), monthly: false));
        Assert.Empty(t.Db.GetAppTime(9, U(2025, 1, 1), U(2025, 3, 1), monthly: true));
    }

    [Fact]
    public void App_months_list_only_months_starting_in_the_range_with_time_in_front()
    {
        using var t = new TestDb();
        t.Db.AddAppHour(Hour(U(2025, 1, 10, 10), 1, fg: 5));
        t.Db.AddAppHour(Hour(U(2025, 2, 10, 10), 1, fg: 7));
        t.Db.AddAppHour(Hour(U(2025, 2, 10, 10), 2, fg: 0, bg: 50));
        Assert.Equal([(U(2025, 2, 1), 1L, 7.0)], t.Db.GetAppMonths(U(2025, 1, 15), U(2025, 3, 1)));
        Assert.Equal(2, t.Db.GetAppMonths(U(2025, 1, 1), U(2025, 3, 1)).Count);
        Assert.Empty(t.Db.GetAppMonths(U(2025, 3, 1), U(2025, 1, 1)));
    }
}
