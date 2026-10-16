using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Tests.Data;

namespace Rigsight.Tests.Reports;

/// <summary>
/// The Network page's biggest hours and days, whole (every app's use of each, with the apps it was), and the
/// connection's days for a week, month or year: up, dropped, not recorded, still to come.
/// </summary>
public sealed class NetWholeDaysTests : IDisposable
{
    private const long MB = 1L << 20, GB = 1L << 30;
    // Wednesday 10 June 2026.
    private static readonly DateTime Day = new(2026, 6, 10);

    private readonly TestDb _t = new("netwholedays");
    private RigsightDb Db => _t.Db;
    public void Dispose() => _t.Dispose();

    private long App(string exe, string name) => Db.UpsertApp(exe, name, null, AppCategory.Other);

    /// <summary>One minute of use at <paramref name="at"/>, each app's download in it.</summary>
    private void Use(DateTime at, params (long App, long Down)[] apps)
    {
        long ts = TimeUtil.ToUnix(at);
        Db.WriteNetMinute(new NetMinute(ts, apps.Sum(a => a.Down), 0, 0, 0, 0, 0, 0, null, apps[0].App));
        foreach (var (app, down) in apps) Db.AddNetAppUse(new NetAppUse { Ts = ts, App = app, Down = down });
    }

    private NetReport Build(ReportRange range, DateTime? asOf = null) =>
        NetReportBuilder.Build(Db, range, Day, Db.LoadApps().ToDictionary(a => a.Id), new RigsightSettings(), asOf: asOf ?? Day.AddDays(400));

    [Fact]
    public void A_months_biggest_days_are_whole_days_with_the_apps_that_made_them()
    {
        long steam = App("steam.exe", "Steam"), chrome = App("chrome.exe", "Google Chrome"), stremio = App("stremio.exe", "Stremio");
        // The 10th: Steam and Chrome, in two minutes. The 12th: Steam alone, less. The 14th: under the least listed.
        Use(Day.AddHours(9), (steam, 3 * GB), (chrome, GB));
        Use(Day.AddHours(20), (stremio, 500 * MB));
        Use(Day.AddDays(2).AddHours(9), (steam, 2 * GB));
        Use(Day.AddDays(4).AddHours(9), (chrome, 10 * MB));

        var r = Build(ReportRange.Month);
        // Each day once, everything every app moved in it: it used to list the 10th once for Steam and again for Chrome.
        Assert.Equal([(Day, 4 * GB + 500 * MB), (Day.AddDays(2), 2 * GB)], r.Biggest.Select(b => (b.Start, b.Bytes)));
        Assert.Equal(Day.AddDays(1), r.Biggest[0].End);
        Assert.Equal([steam, chrome, stremio], r.Biggest[0].Apps.Select(u => u.App));
        Assert.Equal([3 * GB, GB, 500 * MB], r.Biggest[0].Apps.Select(u => u.Total));
        // The same figure as the chart's bar for that day.
        Assert.Equal(r.Bins.Single(b => b.Start == Day).Total, r.Biggest[0].Bytes);
    }

    [Fact]
    public void A_days_biggest_are_whole_hours()
    {
        long steam = App("steam.exe", "Steam"), chrome = App("chrome.exe", "Google Chrome");
        Use(Day.AddHours(9), (steam, GB));
        Use(Day.AddHours(9).AddMinutes(30), (chrome, 300 * MB));
        Use(Day.AddHours(14), (chrome, 900 * MB));
        Use(Day.AddHours(16), (chrome, 20 * MB));

        var r = Build(ReportRange.Day);
        Assert.Equal([(Day.AddHours(9), GB + 300 * MB), (Day.AddHours(14), 900 * MB)], r.Biggest.Select(b => (b.Start, b.Bytes)));
        Assert.Equal(Day.AddHours(10), r.Biggest[0].End);
        Assert.Equal([steam, chrome], r.Biggest[0].Apps.Select(u => u.App));
    }

    [Fact]
    public void A_years_biggest_are_days_not_its_months()
    {
        long steam = App("steam.exe", "Steam"), chrome = App("chrome.exe", "Google Chrome");
        Use(Day.AddHours(9), (steam, 2 * GB));
        Use(Day.AddDays(1).AddHours(9), (chrome, GB));
        Use(Day.AddDays(40).AddHours(9), (steam, 3 * GB), (chrome, 100 * MB));

        var r = Build(ReportRange.Year);
        Assert.Equal([Day.AddDays(40), Day, Day.AddDays(1)], r.Biggest.Select(b => b.Start));
        Assert.Equal(3 * GB + 100 * MB, r.Biggest[0].Bytes);
        Assert.Equal([steam, chrome], r.Biggest[0].Apps.Select(u => u.App));
        Assert.All(r.Biggest, b => Assert.Equal(b.Start.AddDays(1), b.End));
    }

    [Fact]
    public void Each_day_of_a_week_is_up_dropped_not_recorded_or_still_to_come()
    {
        // The week of Monday 8 June. Recording began on Tuesday; a drop on Wednesday; Thursday is today; Friday on is to come.
        long steam = App("steam.exe", "Steam");
        var monday = Day.AddDays(-2);
        Use(monday.AddDays(1).AddHours(9), (steam, MB));
        Use(monday.AddDays(1).AddHours(10), (steam, MB));
        Use(Day.AddHours(9), (steam, MB));
        Use(Day.AddDays(1).AddHours(8), (steam, MB));
        long dropAt = TimeUtil.ToUnix(Day.AddHours(18).AddMinutes(12));
        Db.InsertNetDrop(new NetDrop(dropAt, dropAt + 43 * 60, NetDropKind.Internet));

        var r = Build(ReportRange.Week, asOf: Day.AddDays(1).AddHours(12));
        Assert.Equal(Enumerable.Range(0, 7).Select(i => monday.AddDays(i)), r.Days.Select(d => d.Day));
        Assert.Equal([NetDayState.NotRecorded, NetDayState.Up, NetDayState.Dropped, NetDayState.Up, NetDayState.ToCome, NetDayState.ToCome, NetDayState.ToCome],
            r.Days.Select(d => d.State));
        Assert.Equal([0, 2, 1, 1, 0, 0, 0], r.Days.Select(d => d.RecordedMinutes));
        Assert.Equal(dropAt, Assert.Single(r.Days[2].Drops).Start);
        // Recording began inside the week: said, for the line under the headline.
        Assert.Equal(monday.AddDays(1), r.RecordedSince);
        // A day's report has its quarter hours instead.
        Assert.Empty(Build(ReportRange.Day).Days);
    }

    [Fact]
    public void A_drop_belongs_to_the_day_it_began_in_and_a_day_without_minutes_is_not_recorded()
    {
        var from = new DateTime(2026, 10, 1);
        var days = new[] { new NetDay(TimeUtil.ToUnix(from), 580, 0, 0, 0, 0, 0, 0, 0, null, null) };
        long late = TimeUtil.ToUnix(from.AddDays(1).AddHours(23.9));
        var drops = new[] { new NetDrop(late, late + 3600, NetDropKind.Link) };
        var links = NetReportBuilder.DayLinks(from, from.AddMonths(1), new DateTime(2026, 10, 15, 12, 0, 0), days, drops);
        Assert.Equal(31, links.Count);
        Assert.Equal((NetDayState.Up, 580), (links[0].State, links[0].RecordedMinutes));
        Assert.Equal(NetDayState.Dropped, links[1].State);
        Assert.Equal(NetDayState.NotRecorded, links[2].State);
        Assert.Equal(NetDayState.NotRecorded, links[14].State); // today, nothing yet
        Assert.Equal(NetDayState.ToCome, links[15].State);
        Assert.Equal(NetDayState.ToCome, links[^1].State);
    }

    [Fact]
    public void A_year_has_every_day_of_it()
    {
        long steam = App("steam.exe", "Steam");
        Use(Day.AddHours(9), (steam, MB));
        var r = Build(ReportRange.Year, asOf: Day.AddHours(12));
        Assert.Equal(365, r.Days.Count);
        Assert.Equal(28, r.Days.Count(d => d.Day.Month == 2));
        Assert.Equal(NetDayState.Up, r.Days.Single(d => d.Day == Day).State);
        Assert.All(r.Days.Where(d => d.Day < Day), d => Assert.Equal(NetDayState.NotRecorded, d.State));
        Assert.All(r.Days.Where(d => d.Day > Day), d => Assert.Equal(NetDayState.ToCome, d.State));
        Assert.Equal(Day, r.RecordedSince);
    }
}
