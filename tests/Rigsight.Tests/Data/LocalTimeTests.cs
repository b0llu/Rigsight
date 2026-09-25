using System.Globalization;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using static Rigsight.Tests.Data.Make;

namespace Rigsight.Tests.Data;

/// <summary>
/// Day and month totals are keyed by SQLite in local time, while reports work out days and months in .NET: the two
/// must agree on this machine's time zone at every midnight, month end and (where the zone has one) clock change.
/// </summary>
public sealed class LocalTimeTests
{
    [Fact]
    public void SQLite_and_dotnet_agree_on_local_time_for_any_instant()
    {
        using var t = new TestDb();
        using var conn = t.Raw(readOnly: true);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT strftime('%Y-%m-%d %H:%M:%S', $t, 'unixepoch', 'localtime')";
        var p = cmd.Parameters.Add("$t", Microsoft.Data.Sqlite.SqliteType.Integer);
        var rnd = new Random(11);
        long from = U(2005, 1, 1), to = U(2035, 1, 1);
        for (int i = 0; i < 3000; i++)
        {
            long ts = from + (long)(rnd.NextDouble() * (to - from));
            p.Value = ts;
            Assert.Equal(L(ts).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), (string)cmd.ExecuteScalar()!);
        }
    }

    [Fact]
    public void Every_day_of_three_years_is_keyed_at_its_local_midnight()
    {
        using var t = new TestDb();
        using (var tx = t.Db.BeginTransaction())
        {
            for (var d = new DateTime(2024, 1, 1); d < new DateTime(2027, 1, 1); d = d.AddDays(1))
            {
                t.Db.WriteMinute(Minute(U(d)));
                t.Db.WriteMinute(Minute(U(d.AddDays(1)) - 60));
            }
            tx.Commit();
        }
        var days = t.Db.GetSystemDays(U(2024, 1, 1), U(2027, 1, 1))!;
        Assert.Equal(1096, days.Count);
        Assert.All(days, d => Assert.Equal(2, d.Minutes));
        Assert.Equal(Enumerable.Range(0, 1096).Select(i => U(new DateTime(2024, 1, 1).AddDays(i))), days.Select(d => d.Day));
        Rollups.AssertSystemDay(t);
    }

    [Fact]
    public void Every_month_of_three_years_is_keyed_at_its_local_first_midnight()
    {
        using var t = new TestDb();
        using (var tx = t.Db.BeginTransaction())
        {
            for (var m = new DateTime(2024, 1, 1); m < new DateTime(2027, 1, 1); m = m.AddMonths(1))
            {
                t.Db.AddAppHour(Hour(U(m), 1, fg: 1));
                t.Db.AddAppHour(Hour(U(m.AddMonths(1).AddHours(-1)), 1, fg: 2));
            }
            tx.Commit();
        }
        var months = t.Rows("SELECT month, fg_sec FROM app_month ORDER BY month").Select(r => ((long)r["month"]!, (double)r["fg_sec"]!)).ToList();
        Assert.Equal(Enumerable.Range(0, 36).Select(i => (U(new DateTime(2024, 1, 1).AddMonths(i)), 3.0)), months);
        Rollups.AssertAppMonth(t);
    }

    [Fact]
    public void Around_this_nights_midnight_minutes_fall_on_the_right_days()
    {
        using var t = new TestDb();
        var today = DateTime.Today;
        foreach (var offset in new[] { -61, -60, -1, 0, 1, 59, 60 })
            t.Db.WriteMinute(Minute(U(today) + offset * 60L));
        var days = t.Db.GetSystemDays(U(today.AddDays(-1)), U(today.AddDays(1)))!;
        Assert.Equal([U(today.AddDays(-1)), U(today)], days.Select(d => d.Day));
        Assert.Equal(t.Db.GetMinutes(U(today.AddDays(-1)), U(today)).Count, days[0].Minutes);
        Assert.Equal(t.Db.GetMinutes(U(today), U(today.AddDays(1))).Count, days[1].Minutes);
        Rollups.AssertSystemDay(t);
    }

    [Fact]
    public void A_day_report_holds_exactly_the_minutes_of_its_local_day()
    {
        using var t = new TestDb();
        var day = new DateTime(2025, 2, 28);
        for (long ts = U(day) - 3600; ts < U(day.AddDays(1)) + 3600; ts += 60) t.Db.WriteMinute(Minute(ts));
        var r = ReportBuilder.Build(t.Db, ReportRange.Day, day.AddHours(13), Settings());
        Assert.Equal(U(day.AddDays(1)) - U(day), r.OnSec);
        Assert.Equal(day, r.Temps[0].Time);
        Assert.Equal(day.AddDays(1).AddMinutes(-1), r.Temps[^1].Time);
    }

    [Fact]
    public void The_current_hour_start_counted_back_from_now_matches_the_local_clock()
    {
        // Counting back avoids local-time conversion; outside a repeated hour both must give the same instant,
        // including in half-hour zones such as India (UTC+5:30).
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var now = DateTime.Now;
            long counted = TimeUtil.LocalHourStartUnix();
            if (DateTime.Now.Hour != now.Hour) continue; // the hour turned while measuring
            Assert.Equal(U(TimeUtil.LocalHourStart(now)), counted);
            Assert.Equal(0, L(counted).Minute);
            Assert.Equal(0, L(counted).Second);
            return;
        }
    }

    [Fact]
    public void Unix_conversions_round_trip_and_truncate_to_the_minute_and_hour()
    {
        var t = new DateTime(2025, 7, 14, 21, 37, 45);
        Assert.Equal(t, L(U(t)));
        Assert.Equal(new DateTime(2025, 7, 14, 21, 37, 0), TimeUtil.LocalMinuteStart(t));
        Assert.Equal(new DateTime(2025, 7, 14, 21, 0, 0), TimeUtil.LocalHourStart(t));
        Assert.Equal(DateTimeKind.Local, L(0).Kind);
        Assert.InRange(TimeUtil.NowUnixMs() / 1000 - TimeUtil.NowUnix(), -1, 1);
    }

    // ── Clock changes (daylight saving), when this machine's zone has them ──

    /// <summary>Local midnights of days on which the clocks change, within two years either side of today.</summary>
    private static List<DateTime> ClockChangeDays()
    {
        var zone = TimeZoneInfo.Local;
        var days = new List<DateTime>();
        if (!zone.SupportsDaylightSavingTime) return days;
        for (var d = DateTime.Today.AddYears(-2); d < DateTime.Today.AddYears(2); d = d.AddDays(1))
            if (U(d.AddDays(1)) - U(d) != 86400) days.Add(d);
        return days;
    }

    [Fact]
    public void On_a_clock_change_day_the_day_holds_every_real_minute_between_its_midnights()
    {
        var days = ClockChangeDays();
        Assert.SkipWhen(days.Count == 0, $"{TimeZoneInfo.Local.Id} has no clock changes");
        using var t = new TestDb();
        foreach (var day in days.Take(4))
        {
            long from = U(day), to = U(day.AddDays(1));
            for (long ts = from - 120; ts < to + 120; ts += 60) t.Db.WriteMinute(Minute(ts));
            var d = Assert.Single(t.Db.GetSystemDays(from, to)!);
            Assert.Equal((to - from) / 60, d.Minutes);
            var r = ReportBuilder.Build(t.Db, ReportRange.Day, day.AddHours(12), Settings());
            Assert.Equal(to - from, r.OnSec);
        }
        Rollups.AssertSystemDay(t);
    }

    [Fact]
    public void On_a_clock_change_day_every_local_hour_rolls_into_the_right_month()
    {
        var days = ClockChangeDays();
        Assert.SkipWhen(days.Count == 0, $"{TimeZoneInfo.Local.Id} has no clock changes");
        using var t = new TestDb();
        foreach (var day in days.Take(4))
            for (long ts = U(day); ts < U(day.AddDays(1)); ts += 3600) t.Db.AddAppHour(Hour(ts, 1, fg: 1));
        Rollups.AssertAppMonth(t);
        foreach (var day in days.Take(4))
            AppTotalsTests.AssertTotalsMatchHours(t.Db, U(new DateTime(day.Year, day.Month, 1)), U(new DateTime(day.Year, day.Month, 1).AddMonths(1)));
    }

    [Fact]
    public void Prune_at_a_clock_change_midnight_keeps_the_rollups_exact()
    {
        var days = ClockChangeDays();
        Assert.SkipWhen(days.Count == 0, $"{TimeZoneInfo.Local.Id} has no clock changes");
        using var t = new TestDb();
        var day = days[0];
        for (long ts = U(day.AddDays(-1)); ts < U(day.AddDays(2)); ts += 600)
        {
            t.Db.WriteMinute(Minute(ts));
            t.Db.AddAppHour(Hour(ts, 1, fg: 1));
        }
        t.Db.Prune(U(day));
        Rollups.AssertAll(t);
        t.Db.Prune(U(day) + 5 * 3600);
        Rollups.AssertAll(t);
    }
}
