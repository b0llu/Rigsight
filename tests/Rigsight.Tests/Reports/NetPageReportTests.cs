using Rigsight.Core.Data;
using Rigsight.Core.Games;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Tests.Data;

namespace Rigsight.Tests.Reports;

/// <summary>
/// What only the Network page's report carries (1.3.0): a week by the hour, the line check on each bar and over the
/// period with its sentence, the game played and Steam's downloads on each hour, and "What happened".
/// </summary>
public sealed class NetPageReportTests : IDisposable
{
    private const long MB = 1L << 20, GB = 1L << 30;
    private static readonly DateTime Day = new(2026, 6, 10);

    private readonly TestDb _t = new("netpage");
    private RigsightDb Db => _t.Db;
    public void Dispose() => _t.Dispose();

    private long App(string exe, string name, AppCategory category = AppCategory.Other) => Db.UpsertApp(exe, name, null, category);

    /// <summary>An hour of use: one minute in it with the totals, and each app's share (down, up, in the background).</summary>
    private void Hour(DateTime at, params (long App, long Down, long Up, long Bg)[] apps)
    {
        long ts = TimeUtil.ToUnix(at);
        Db.WriteNetMinute(new NetMinute(ts, apps.Sum(a => a.Down), apps.Sum(a => a.Up), apps.Sum(a => a.Bg), 0, 0, 0, 0, null, apps.Length > 0 ? apps[0].App : null));
        foreach (var (app, d, u, bg) in apps) Db.AddNetAppUse(new NetAppUse { Ts = TimeUtil.ToUnix(at.Date.AddHours(at.Hour)), App = app, Down = d, Up = u, BgDown = bg });
    }

    /// <summary>The line check's minutes through an hour: each with this usual and highest ping to the provider.</summary>
    private void Pings(DateTime hour, int usual, int highest, int lost = 0)
    {
        for (int m = 0; m < 60; m++)
            Db.WriteNetPing(new NetPing(TimeUtil.ToUnix(hour.AddMinutes(m)), 12, 1, 2, 0, usual, m == 30 ? highest : usual + 2, m == 0 ? lost : 0, 2));
    }

    private void Played(long game, DateTime hour, double seconds = 3000) => Db.AddAppHour(new AppHour { Ts = TimeUtil.ToUnix(hour), AppId = game, FgSec = seconds });

    private NetReport Build(DateTime? day = null, ReportRange range = ReportRange.Day, bool page = true) =>
        NetReportBuilder.Build(Db, range, day ?? Day, Db.LoadApps().ToDictionary(a => a.Id), new RigsightSettings(), asOf: Day.AddDays(1).AddHours(9), forPage: page);

    [Fact]
    public void A_week_on_the_page_is_by_the_hour_and_elsewhere_by_the_day()
    {
        long steam = App("steam.exe", "Steam");
        Hour(Day.AddHours(14), (steam, 3 * GB, 0, 3 * GB));
        var page = Build(range: ReportRange.Week);
        Assert.True(page.Hourly);
        Assert.Equal(168, page.Bins.Count);
        var bar = page.Bins.Single(b => b.Total > 0);
        Assert.Equal(Day.AddHours(14), bar.Start);
        Assert.Equal(1, bar.AppCount);
        var reports = Build(range: ReportRange.Week, page: false);
        Assert.False(reports.Hourly);
        Assert.Equal(7, reports.Bins.Count);
    }

    [Fact]
    public void The_line_check_is_on_each_bar_and_over_the_period()
    {
        long chrome = App("chrome.exe", "Google Chrome");
        Hour(Day.AddHours(9), (chrome, 100 * MB, 0, 0));
        Pings(Day.AddHours(9), 20, 30);
        Pings(Day.AddHours(10), 22, 35, lost: 6);
        var r = Build();
        Assert.Equal(new NetLinePing(20, 30, 0, 60, true, 1) { Low = 20, Most = 20 }, r.Bins[9].Ping);
        Assert.Equal(22, r.Bins[10].Ping!.Usual);
        Assert.Equal(0.8, r.Bins[10].Ping!.LostPercent, 1);
        Assert.Null(r.Bins[11].Ping); // not checked then: nothing, never 0 ms
        Assert.Equal(22, r.Line!.Usual);
        Assert.Equal(35, r.Line.Highest);
        Assert.Equal("Ping was usually 22 ms. It stayed under 80 ms.", r.LineText);
    }

    [Fact]
    public void Without_the_line_check_there_is_no_ping_at_all()
    {
        long chrome = App("chrome.exe", "Google Chrome");
        Hour(Day.AddHours(9), (chrome, 100 * MB, 0, 0));
        var r = Build();
        Assert.Null(r.Line);
        Assert.Equal("", r.LineText);
        Assert.All(r.Bins, b => Assert.Null(b.Ping));
    }

    [Fact]
    public void The_sentence_names_the_app_uploading_when_the_ping_went_high()
    {
        long obs = App("obs64.exe", "OBS Studio"), chrome = App("chrome.exe", "Google Chrome");
        for (int h = 8; h < 22; h++)
        {
            Hour(Day.AddHours(h), (chrome, 50 * MB, MB, 0));
            Pings(Day.AddHours(h), 20, 30);
        }
        foreach (int h in new[] { 19, 20 })
        {
            Hour(Day.AddHours(h).AddMinutes(1), (obs, 8 * MB, 3 * GB, 3 * GB));
            Pings(Day.AddHours(h), 45, 112);
        }
        var r = Build();
        Assert.Equal("Ping was usually 20 ms. It went past 80 ms in 2 hours, every time while OBS Studio was uploading.", r.LineText);
        // A line far from 80 ms on every check (satellite) is high only from twice its own usual.
        Assert.Equal(80, NetLinePing.HighFor(20));
        Assert.Equal(1200, NetLinePing.HighFor(600));
    }

    [Fact]
    public void Each_hour_knows_the_game_played_and_what_steam_started_downloading()
    {
        long steam = App("steam.exe", "Steam"), game = App("PBZ.exe", "Phantom Blade Zero", AppCategory.Game);
        Hour(Day.AddHours(18), (steam, 40 * GB, 0, 40 * GB));
        Played(game, Day.AddHours(18));
        Played(game, Day.AddHours(19), seconds: 300); // five minutes isn't an hour of that game
        Db.SaveSteamDownloads([new GameDownload("1", "Phantom Blade Zero", Day.AddHours(18).AddMinutes(4), Day.AddHours(19).AddMinutes(34), 148 * GB, true, DownloadEnd.Finished),
            new GameDownload("2", "Dota 2", Day.AddHours(18).AddMinutes(5), Day.AddHours(18).AddMinutes(6), 10 * MB, false, DownloadEnd.Finished)]);
        var r = Build();
        Assert.Equal("Phantom Blade Zero", r.Bins[18].Game);
        Assert.Null(r.Bins[19].Game);
        // Steam checking a game (10 MB) isn't a download worth a dot.
        Assert.Equal(["Phantom Blade Zero"], r.Bins[18].Steam.Select(d => d.Name));
    }

    [Fact]
    public void What_happened_tells_steam_by_name_and_what_was_used_while_a_game_was_played()
    {
        long steam = App("steam.exe", "Steam"), obs = App("obs64.exe", "OBS Studio"), game = App("PBZ.exe", "Phantom Blade Zero", AppCategory.Game);
        Hour(Day.AddDays(-3).AddHours(9), (steam, MB, 0, 0)); // recording began before the day
        foreach (int h in new[] { 18, 19, 20 })
        {
            Played(game, Day.AddHours(h));
            Hour(Day.AddHours(h), (obs, 2 * MB, 2500 * MB, 2500 * MB));
            Pings(Day.AddHours(h), 21, h == 19 ? 112 : 30);
        }
        Db.SaveSteamDownloads([
            new GameDownload("1", "Crimson Desert", Day.AddHours(21).AddMinutes(45), Day.AddHours(21).AddMinutes(56), 900 * MB, false, DownloadEnd.Finished),
            new GameDownload("2", "Counter-Strike 2", Day.AddHours(21).AddMinutes(46), Day.AddHours(21).AddMinutes(47), 110 * MB, false, DownloadEnd.Finished),
        ]);
        var r = Build();
        var lines = r.Happenings.Where(h => h.Kind != NetHappeningKind.Insight).ToList();
        var steamLine = lines.Single(h => h.Kind == NetHappeningKind.Steam);
        Assert.Equal("Steam updated 2 games", steamLine.Text);
        Assert.Equal("Crimson Desert 900 MB · Counter-Strike 2 110 MB", steamLine.Detail);
        Assert.Equal("1010 MB", steamLine.Amount);
        Assert.Equal("9:45 PM", steamLine.When);
        var played = lines.Single(h => h.Kind == NetHappeningKind.Played);
        Assert.Equal("OBS Studio used 7.3 GB while you played Phantom Blade Zero", played.Text);
        Assert.Equal("6 – 9 PM", played.When);
        // The ping rose meanwhile: said in plain words, with why, never as a warning.
        Assert.Equal("Ping rose to 112 ms meanwhile", played.PingNote);
        Assert.StartsWith("When one app fills the upload, game traffic waits behind it", played.Detail);
        // Newest first, under the day.
        Assert.Equal([steamLine, played], lines.Where(h => h.Kind is NetHappeningKind.Steam or NetHappeningKind.Played));
        Assert.All(lines, h => Assert.Equal(Day, h.Day));
    }

    [Fact]
    public void An_app_new_on_the_internet_is_said_with_the_others_new_that_day()
    {
        long chrome = App("chrome.exe", "Google Chrome");
        Hour(Day.AddDays(-2).AddHours(9), (chrome, 200 * MB, 0, 0));
        long setup = App("Claude Setup.exe", "Claude Setup"), mrt = App("MRT.exe", "MRT"), cowork = App("cowork.exe", "Cowork Svc");
        Hour(Day.AddHours(12), (setup, 271 * MB, 0, 0), (mrt, 30 * MB, 0, 30 * MB), (cowork, 5 * MB, 0, 5 * MB), (chrome, 10 * MB, 0, 0));
        var line = Build().Happenings.Single(h => h.Kind == NetHappeningKind.New);
        Assert.Equal("Claude Setup used the internet for the first time", line.Text);
        Assert.Equal("Also new: MRT, Cowork Svc", line.Detail);
        Assert.True(line.IsNew);
        Assert.Equal("12 PM", line.When);
        // On the first day anything was recorded, every app is "new": nothing is said.
        Assert.DoesNotContain(Build(Day.AddDays(-2)).Happenings, h => h.Kind == NetHappeningKind.New);
    }

    [Fact]
    public void A_busy_hour_is_said_only_when_it_is_far_more_than_that_app_usually_uses()
    {
        long svc = App("svchost.exe", "Windows service"), stream = App("stremio.exe", "Freedom to Stream");
        for (int h = 9; h < 23; h++) Hour(Day.AddHours(h), (stream, 1300 * MB, 0, 0), (svc, 5 * MB, 0, 5 * MB));
        Hour(Day.AddHours(13).AddMinutes(2), (svc, 1800 * MB, 0, 1800 * MB));
        var busy = Build().Happenings.Where(h => h.Kind == NetHappeningKind.Busy).ToList();
        // The Windows service's 1.8 GB hour is news; streaming 1.3 GB every hour isn't.
        var line = Assert.Single(busy);
        Assert.Equal("Windows service used 1.8 GB", line.Text);
        Assert.Equal("In the background", line.Detail);
        Assert.Equal("1 PM", line.When);
    }

    [Fact]
    public void An_hours_spread_is_its_lowest_where_most_checks_fell_and_its_highest()
    {
        var rows = Enumerable.Range(1, 10).Select(v => new NetPing(v * 60, 12, 1, 2, 0, v, v + 5, 0, 1)).ToList();
        var p = NetLine.Of(rows)!;
        Assert.Equal(1, p.Low);
        Assert.Equal(9, p.Most); // nine in ten minutes at or under it
        Assert.Equal(6, p.Usual);
        Assert.Equal(15, p.Highest);
    }

    [Fact]
    public void The_graph_waits_for_enough_checked_hours()
    {
        Hour(Day.AddHours(9), (App("chrome.exe", "Google Chrome"), 100 * MB, 0, 0));
        for (int h = 9; h < 14; h++) Pings(Day.AddHours(h), 5, 8);
        var few = Build();
        Assert.Equal((5, 6), (few.PingBars, few.PingBarsNeeded));
        Assert.False(few.PingEnough);
        Pings(Day.AddHours(14), 5, 8);
        Assert.True(Build().PingEnough);
        Assert.Equal(3, NetLine.BarsToDraw(false, ReportRange.Month));
        Assert.Equal(2, NetLine.BarsToDraw(false, ReportRange.Year));
    }

    [Fact]
    public void Stopping_ping_checks_keeps_what_was_checked_and_marks_the_rest_off()
    {
        long chrome = App("chrome.exe", "Google Chrome");
        Hour(Day.AddHours(9), (chrome, 100 * MB, 0, 0));
        Hour(Day.AddDays(1).AddHours(2), (chrome, 100 * MB, 0, 0));
        for (int h = 6; h < 10; h++) Pings(Day.AddHours(h), 5, 8);
        for (int m = 0; m < 30; m++) Db.WriteNetPing(new NetPing(TimeUtil.ToUnix(Day.AddHours(10).AddMinutes(m)), 12, 1, 2, 0, 5, 7, 0, 2)); // up to 10:30
        Db.WriteNetPingSwitch(TimeUtil.ToUnix(Day.AddHours(10).AddMinutes(30)), false);
        Db.WriteNetPingSwitch(TimeUtil.ToUnix(Day.AddHours(15)), true);
        Db.WriteNetPingSwitch(TimeUtil.ToUnix(Day.AddHours(18)), false);
        var r = Build();
        Assert.Equal(3, r.PingSwitches.Count);
        Assert.False(r.PingOffAtStart);
        Assert.NotNull(r.Bins[9].Ping); // checked before: kept
        Assert.False(r.Bins[10].PingOff); // checked for half of it
        Assert.True(r.Bins[11].PingOff);
        Assert.True(r.Bins[14].PingOff);
        Assert.False(r.Bins[15].PingOff); // on again (nothing checked then, but not off)
        Assert.True(r.Bins[19].PingOff);
        Assert.Equal("Ping was usually 5 ms until checks were paused at 6:00 PM. It stayed under 80 ms.", r.LineText);
        var lines = r.Happenings.Where(h => h.Kind == NetHappeningKind.PingSwitch).Select(h => (h.When, h.Text)).ToList();
        Assert.Contains(("10:30 AM", "Ping checks were paused"), lines);
        Assert.Contains(("3:00 PM", "Ping checks were resumed"), lines);
        // The next day began with them off: every hour of it is off, and the switch before it says so.
        var next = Build(Day.AddDays(1));
        Assert.True(next.PingOffAtStart);
        Assert.Empty(next.PingSwitches);
        Assert.True(next.Bins[3].PingOff);
    }

    [Fact]
    public void The_period_knows_since_when_ping_is_checked()
    {
        long chrome = App("chrome.exe", "Google Chrome");
        Hour(Day.AddHours(9), (chrome, 100 * MB, 0, 0));
        Hour(Day.AddDays(-3).AddHours(9), (chrome, 100 * MB, 0, 0));
        Assert.Null(Build().PingSince);
        Pings(Day.AddHours(9), 5, 8);
        Assert.Equal(Day.AddHours(9), Build(Day.AddDays(-3)).PingSince);
    }

    [Fact]
    public void The_internet_strip_says_up_dropped_off_and_still_to_come_and_each_drop_is_a_line()
    {
        long chrome = App("chrome.exe", "Google Chrome");
        for (int m = 0; m < 120; m++) Db.WriteMinute(new SystemMinute { Ts = TimeUtil.ToUnix(Day.AddHours(9).AddMinutes(m)), ActiveSec = 60 });
        Hour(Day.AddHours(9), (chrome, 100 * MB, 0, 0));
        Db.InsertNetDrop(new NetDrop(TimeUtil.ToUnix(Day.AddHours(10).AddMinutes(20)), TimeUtil.ToUnix(Day.AddHours(10).AddMinutes(21)), NetDropKind.Internet));
        // The router answered the line check meanwhile.
        Db.WriteNetPing(new NetPing(TimeUtil.ToUnix(Day.AddHours(10).AddMinutes(20)), 12, 1, 2, 0, null, null, 12, 0));
        var r = Build();
        Assert.Equal(96, r.Link.Count);
        Assert.Equal(NetLinkState.Off, r.Link[8 * 4].State);           // 8 AM: the PC off
        Assert.Equal(NetLinkState.Up, r.Link[9 * 4].State);            // 9 AM: on and up
        Assert.Equal(NetLinkState.Dropped, r.Link[10 * 4 + 1].State);  // 10:15 - 10:30 holds the drop
        Assert.NotEqual(NetLinkState.Dropped, r.Link[10 * 4 + 2].State);
        var drop = Assert.Single(r.Happenings, h => h.Kind == NetHappeningKind.Drop);
        Assert.Equal(("10:20 AM", "Your internet was down for 1 minute", "Your router was reachable, but there was no internet connection."), (drop.When, drop.Text, drop.Detail));
        // Today, the hours to come are to come.
        var today = NetReportBuilder.Build(Db, ReportRange.Day, Day, Db.LoadApps().ToDictionary(a => a.Id), new RigsightSettings(), asOf: Day.AddHours(11), forPage: true);
        Assert.Equal(NetLinkState.ToCome, today.Link[^1].State);
        // A week by the hour, a month by the day.
        Assert.Equal(168, Build(range: ReportRange.Week).Link.Count);
        Assert.Equal(DateTime.DaysInMonth(Day.Year, Day.Month), Build(range: ReportRange.Month).Link.Count);
    }

    [Fact]
    public void The_periods_insights_come_first()
    {
        long steam = App("steam.exe", "Steam");
        Hour(Day.AddHours(3), (steam, 3 * GB, 0, 3 * GB));
        var r = Build();
        Assert.NotEmpty(r.Insights);
        Assert.Equal(NetHappeningKind.Insight, r.Happenings[0].Kind);
        Assert.Null(r.Happenings[0].Day);
    }
}
