using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Tests.Data;
using static Rigsight.Tests.Data.Make;

namespace Rigsight.Tests.Reports;

/// <summary>
/// Custom ranges (from a date and hour to another): whole hours, their titles, and what they're compared with (the same
/// hours a day earlier up to two days, else the same length just before).
/// </summary>
public sealed class CustomRangeTests
{
    // A Wednesday well in the past, away from daylight-saving changes.
    private static readonly DateTime Wed = new(2026, 6, 10);

    /// <summary>Active minutes from <paramref name="from"/> up to <paramref name="to"/>, with the app's hours to match.</summary>
    private static void Use(TestDb t, long app, DateTime from, DateTime to, int active = 60)
    {
        for (var m = from; m < to; m = m.AddMinutes(1))
            t.Db.WriteMinute(Minute(U(m), app: app, active: active, idle: 60 - active));
        for (var h = ReportBuilder.HourStart(from); h < to; h = h.AddHours(1))
        {
            var (s, e) = (h < from ? from : h, h.AddHours(1) > to ? to : h.AddHours(1));
            t.Db.AddAppHour(Hour(U(h), app, fg: (e - s).TotalSeconds * active / 60.0));
        }
    }

    private static (TestDb T, long App) Db()
    {
        var t = new TestDb();
        return (t, t.Db.UpsertApp("game.exe", "Game", null, AppCategory.Game));
    }

    [Fact]
    public void A_custom_report_is_whole_hours_around_what_was_asked()
    {
        var (t, app) = Db();
        using (t)
        {
            Use(t, app, Wed.AddHours(8).AddMinutes(4), Wed.AddDays(1).AddMinutes(27));
            var r = ReportBuilder.BuildCustom(t.Db, Wed.AddHours(8).AddMinutes(4), Wed.AddDays(1).AddMinutes(27), Settings());
            ReportCheck.NoBadNumbers(r);
            Assert.Equal(ReportRange.Custom, r.Range);
            Assert.Equal(Wed.AddHours(8), r.From);
            Assert.Equal(Wed.AddDays(1).AddHours(1), r.To);
            Assert.True(r.HasData);
            Assert.Equal((16 * 60 + 23) * 60, r.ActiveSec);
            Assert.Equal("Game", Assert.Single(r.Apps).Name);
        }
    }

    [Fact]
    public void An_empty_or_backwards_custom_range_is_one_hour()
    {
        var (t, _) = Db();
        using (t)
        {
            var r = ReportBuilder.BuildCustom(t.Db, Wed.AddHours(9), Wed.AddHours(9), Settings());
            Assert.Equal((Wed.AddHours(9), Wed.AddHours(10)), (r.From, r.To));
            r = ReportBuilder.BuildCustom(t.Db, Wed.AddHours(9), Wed.AddHours(3), Settings());
            Assert.Equal((Wed.AddHours(9), Wed.AddHours(10)), (r.From, r.To));
            Assert.False(r.HasData);
        }
    }

    [Fact]
    public void A_late_night_inside_a_custom_range_is_part_of_the_range_not_the_night_before()
    {
        using var _ = Culture();
        var (t, app) = Db();
        using (t)
        {
            Use(t, app, Wed.AddHours(8).AddMinutes(4), Wed.AddHours(12));
            Use(t, app, Wed.AddHours(17), Wed.AddDays(1).AddMinutes(27));
            var r = ReportBuilder.BuildCustom(t.Db, Wed.AddHours(8), Wed.AddDays(1).AddHours(1), Settings());
            Assert.Null(r.LateUntil);
            Assert.Equal(Wed.AddHours(8).AddMinutes(4), r.DayStart);
            Assert.Equal("You were on from Wed 8:04 AM to Thu 12:27 AM.", Assert.Single(r.Insights, i => i.Key == "span").Text);
        }
    }

    [Fact]
    public void An_evening_is_compared_with_the_evening_before()
    {
        using var _ = Culture();
        var (t, app) = Db();
        using (t)
        {
            // 6 PM to midnight: three hours of it the evening before, six now. The afternoon just before doesn't count.
            Use(t, app, Wed.AddHours(12), Wed.AddHours(18));
            Use(t, app, Wed.AddHours(-6), Wed.AddHours(-3));
            Use(t, app, Wed.AddHours(18), Wed.AddDays(1));
            var r = ReportBuilder.BuildCustom(t.Db, Wed.AddHours(18), Wed.AddDays(1), Settings());
            Assert.Contains(r.Insights, i => i.Text == "That's 3h 00m more screen time than the same hours the day before.");
        }
    }

    [Fact]
    public void A_range_longer_than_a_day_is_compared_with_the_same_hours_two_days_before()
    {
        using var _ = Culture();
        var (t, app) = Db();
        using (t)
        {
            // 8 AM to 2 PM the next day (30 hours): the same hours two days earlier, which don't overlap it.
            Use(t, app, Wed.AddDays(-2).AddHours(9), Wed.AddDays(-2).AddHours(10));
            Use(t, app, Wed.AddHours(9), Wed.AddHours(14));
            var r = ReportBuilder.BuildCustom(t.Db, Wed.AddHours(8), Wed.AddDays(1).AddHours(14), Settings());
            Assert.Contains(r.Insights, i => i.Text == "That's 4h 00m more screen time than the same hours two days before.");
            Assert.Equal("You were on from Wed 9:00 AM to Wed 2:00 PM.", Assert.Single(r.Insights, i => i.Key == "span").Text);
        }
    }

    [Fact]
    public void A_range_of_several_days_is_compared_with_the_same_length_just_before()
    {
        using var _ = Culture();
        var (t, app) = Db();
        using (t)
        {
            // Four days, against the four days before them.
            Use(t, app, Wed.AddDays(-3).AddHours(9), Wed.AddDays(-3).AddHours(10));
            Use(t, app, Wed.AddHours(9), Wed.AddHours(12));
            var r = ReportBuilder.BuildCustom(t.Db, Wed, Wed.AddDays(4), Settings());
            Assert.Contains(r.Insights, i => i.Text == "That's 2h 00m more screen time than the same length of time just before.");
        }
    }

    [Fact]
    public void Long_custom_ranges_read_like_weeks_or_years()
    {
        var from = Wed;
        Assert.True(ReportBuilder.IsDayLike(ReportRange.Custom, from, from.AddHours(48)));
        Assert.False(ReportBuilder.IsDayLike(ReportRange.Custom, from, from.AddHours(49)));
        Assert.True(ReportBuilder.IsDayLike(ReportRange.Day, from, from.AddDays(1)));
        Assert.False(ReportBuilder.IsDayLike(ReportRange.Week, from, from.AddDays(1)));
        Assert.False(ReportBuilder.IsLong(ReportRange.Custom, from, from.AddDays(92)));
        Assert.True(ReportBuilder.IsLong(ReportRange.Custom, from, from.AddDays(93)));
        Assert.True(ReportBuilder.IsLong(ReportRange.Year, from, from.AddDays(1)));
        Assert.False(ReportBuilder.IsLong(ReportRange.Month, from, from.AddDays(200)));
    }

    [Fact]
    public void A_long_custom_range_is_built_from_the_daily_totals()
    {
        var (t, app) = Db();
        using (t)
        {
            Use(t, app, Wed.AddHours(9), Wed.AddHours(10));
            var r = ReportBuilder.BuildCustom(t.Db, Wed.AddDays(-100), Wed.AddDays(2), Settings());
            ReportCheck.NoBadNumbers(r);
            Assert.True(r.HasData);
            Assert.Equal(3600, r.ActiveSec);
        }
    }

    [Fact]
    public void Hours_round_down_at_the_start_and_up_at_the_end()
    {
        Assert.Equal(Wed.AddHours(8), ReportBuilder.HourStart(Wed.AddHours(8).AddMinutes(59)));
        Assert.Equal(Wed.AddHours(9), ReportBuilder.HourEnd(Wed.AddHours(8).AddMinutes(1)));
        Assert.Equal(Wed.AddHours(9), ReportBuilder.HourEnd(Wed.AddHours(9)));
    }

    [Fact]
    public void A_range_across_years_names_the_year_on_both_ends()
    {
        // Only the start's year would read as if the end came first ("Thu 25 Sep 2025 – Thu 17 Sep").
        using var c = Culture();
        var thisYear = DateTime.Today.Year;
        var (from, to) = (new DateTime(thisYear - 1, 9, 25), new DateTime(thisYear, 9, 17, 11, 0, 0));
        Assert.Equal($"{from:ddd} 25 Sep {thisYear - 1}, 12 AM – {to:ddd} 17 Sep {thisYear}, 11 AM", Report.CustomTitle(from, to));
        // A single earlier year: once, as the day is named once.
        var (a, b) = (new DateTime(2020, 3, 4, 8, 0, 0), new DateTime(2020, 3, 4, 17, 0, 0));
        Assert.Equal("Wed 4 Mar 2020, 8 AM – 5 PM", Report.CustomTitle(a, b));
        // Both this year: no years.
        var today = DateTime.Today;
        Assert.DoesNotContain(thisYear.ToString(), Report.CustomTitle(today.AddHours(1), today.AddHours(2)));
    }

    [Theory]
    [InlineData(8, 0, 25, 0, "Wed 10 Jun 2026, 8 AM – Thu 11 Jun 2026, 1 AM")]
    [InlineData(8, 0, 24, 0, "Wed 10 Jun 2026, 8 AM – 12 AM")]        // ending at midnight: still Wednesday
    [InlineData(8, 30, 17, 45, "Wed 10 Jun 2026, 8:30 AM – 5:45 PM")]
    [InlineData(0, 0, 72, 0, "Wed 10 Jun 2026, 12 AM – Sat 13 Jun 2026, 12 AM")]
    public void Custom_titles_name_each_day_once(int fromHour, int fromMinute, int toHour, int toMinute, string expected)
    {
        using var c = Culture();
        if (DateTime.Today.Year == 2026) expected = expected.Replace(" 2026", ""); // the year only shows for other years
        var from = Wed.AddHours(fromHour).AddMinutes(fromMinute);
        var to = Wed.AddHours(toHour).AddMinutes(toMinute);
        Assert.Equal(expected, Report.CustomTitle(from, to));
        Assert.Equal(expected, new Report { Range = ReportRange.Custom, From = from, To = to }.Title);
    }
}
