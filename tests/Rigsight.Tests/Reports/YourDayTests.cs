using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Tests.Data;
using static Rigsight.Tests.Data.Make;

namespace Rigsight.Tests.Reports;

/// <summary>
/// "Your day" (use from 5 AM to 5 AM, so a night that runs past midnight stays with the day it started) and the custom
/// ranges it opens: whole hours, compared with the same length just before, linked from Home and the recap.
/// </summary>
public sealed class YourDayTests
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
    public void A_day_without_use_has_no_span()
    {
        var (t, _) = Db();
        using (t) Assert.Null(ReportBuilder.YourDay(t.Db, Wed));
    }

    [Fact]
    public void A_day_ending_before_midnight_runs_from_its_first_to_its_last_minute()
    {
        var (t, app) = Db();
        using (t)
        {
            Use(t, app, Wed.AddHours(9).AddMinutes(4), Wed.AddHours(18).AddMinutes(30));
            var span = ReportBuilder.YourDay(t.Db, Wed)!.Value;
            Assert.Equal(Wed.AddHours(9).AddMinutes(4), span.From);
            Assert.Equal(Wed.AddHours(18).AddMinutes(30), span.To);
            Assert.False(ReportBuilder.RanPastMidnight(span, Wed));
        }
    }

    [Fact]
    public void Gaming_until_3_AM_belongs_to_the_day_it_started()
    {
        var (t, app) = Db();
        using (t)
        {
            Use(t, app, Wed.AddHours(8), Wed.AddHours(12));
            Use(t, app, Wed.AddHours(17), Wed.AddDays(1).AddHours(3));
            var span = ReportBuilder.YourDay(t.Db, Wed)!.Value;
            Assert.Equal(Wed.AddHours(8), span.From);
            Assert.Equal(Wed.AddDays(1).AddHours(3), span.To);
            Assert.True(ReportBuilder.RanPastMidnight(span, Wed));
        }
    }

    [Fact]
    public void Use_before_5_AM_is_the_night_before_and_after_5_AM_the_next_day()
    {
        var (t, app) = Db();
        using (t)
        {
            Use(t, app, Wed.AddHours(1), Wed.AddHours(2));         // Tuesday night's
            Use(t, app, Wed.AddHours(10), Wed.AddHours(11));
            Use(t, app, Wed.AddDays(1).AddHours(6), Wed.AddDays(1).AddHours(7)); // Thursday's
            var span = ReportBuilder.YourDay(t.Db, Wed)!.Value;
            Assert.Equal((Wed.AddHours(10), Wed.AddHours(11)), span);
            Assert.Equal((Wed.AddHours(1), Wed.AddHours(2)), ReportBuilder.YourDay(t.Db, Wed.AddDays(-1)));
        }
    }

    [Fact]
    public void The_PC_left_on_but_idle_overnight_is_not_part_of_your_day()
    {
        var (t, app) = Db();
        using (t)
        {
            Use(t, app, Wed.AddHours(9), Wed.AddHours(22));
            Use(t, app, Wed.AddHours(22), Wed.AddDays(1).AddHours(4), active: 0); // a download overnight
            var span = ReportBuilder.YourDay(t.Db, Wed)!.Value;
            Assert.Equal(Wed.AddHours(22), span.To);
            Assert.False(ReportBuilder.RanPastMidnight(span, Wed));
        }
    }

    [Fact]
    public void Ending_exactly_at_midnight_did_not_run_past_it()
    {
        Assert.False(ReportBuilder.RanPastMidnight((Wed.AddHours(8), Wed.AddDays(1)), Wed));
        Assert.True(ReportBuilder.RanPastMidnight((Wed.AddHours(8), Wed.AddDays(1).AddMinutes(1)), Wed));
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
    public void A_late_night_inside_a_custom_range_is_part_of_the_day_not_the_night_before()
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
            Assert.Equal("Your day ran from 8:04 AM to 12:27 AM.", Assert.Single(r.Insights, i => i.Key == "span").Text);
        }
    }

    [Fact]
    public void A_custom_range_is_compared_with_the_same_length_just_before()
    {
        using var _ = Culture();
        var (t, app) = Db();
        using (t)
        {
            // Six hours from 6 PM, against the six hours before (noon to 6 PM): three hours then, six now.
            Use(t, app, Wed.AddHours(12), Wed.AddHours(15));
            Use(t, app, Wed.AddHours(18), Wed.AddDays(1));
            var r = ReportBuilder.BuildCustom(t.Db, Wed.AddHours(18), Wed.AddDays(1), Settings());
            Assert.Contains(r.Insights, i => i.Text.Contains("the same length of time just before"));
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
    public void Links_go_back_to_the_same_day_or_range()
    {
        var day = new Report { Range = ReportRange.Day, From = Wed, To = Wed.AddDays(1) };
        Assert.Equal("2026-06-10", ReportBuilder.LinkFor(day));
        Assert.Equal((Wed, null), ReportBuilder.ReadLink(ReportBuilder.LinkFor(day), DateTime.Today));

        var range = new Report { Range = ReportRange.Custom, From = Wed.AddHours(8), To = Wed.AddDays(1).AddHours(1) };
        Assert.Equal("2026-06-10T08:00/2026-06-11T01:00", ReportBuilder.LinkFor(range));
        Assert.Equal((null, (Wed.AddHours(8), Wed.AddDays(1).AddHours(1))), ReportBuilder.ReadLink(ReportBuilder.LinkFor(range), DateTime.Today));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tomorrow")]
    [InlineData("2026-13-40")]
    [InlineData("2026-06-10T08:00/2026-06-10T07:00")] // backwards
    [InlineData("2026-06-10T08:00/")]
    [InlineData("2026-06-10T08:00/2026-06-11T01:00/x")]
    public void A_bad_link_opens_nothing(string? link) => Assert.Equal((null, null), ReportBuilder.ReadLink(link, Wed));

    [Fact]
    public void Yesterday_is_the_day_before_today() => Assert.Equal((Wed.AddDays(-1), null), ReportBuilder.ReadLink("yesterday", Wed.AddHours(15)));

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
