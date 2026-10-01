using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Tests.Data;

namespace Rigsight.Tests.Reports;

/// <summary>The Network page's report (totals, chart, apps, downloads, drops) and the network insights, each said only when it holds.</summary>
public sealed class NetReportTests : IDisposable
{
    private const long MB = 1L << 20, GB = 1L << 30;
    private static readonly DateTime Day = new(2026, 6, 10);

    private readonly TestDb _t = new("netreport");
    private RigsightDb Db => _t.Db;
    public void Dispose() => _t.Dispose();

    private long App(string exe, string name, AppCategory category = AppCategory.Other) => Db.UpsertApp(exe, name, null, category);

    /// <summary>A day's internet use: one minute at noon with its totals (and best steady speed), and each app's share.</summary>
    private void Use(DateTime day, long? best = null, params (long App, long Down, long Up, long Bg, long Away, long Game)[] apps)
    {
        long ts = TimeUtil.ToUnix(day.AddHours(12));
        long down = apps.Sum(a => a.Down), up = apps.Sum(a => a.Up);
        Db.WriteNetMinute(new NetMinute(ts, down, up, apps.Sum(a => a.Bg), 0, apps.Sum(a => a.Away), 0, 0, best, apps.Length > 0 ? apps[0].App : null));
        foreach (var (app, d, u, bg, away, game) in apps)
            Db.AddNetAppUse(new NetAppUse { Ts = ts, App = app, Down = d, Up = u, BgDown = bg, AwayDown = away, GameDown = game });
    }

    private NetReport Build(DateTime? day = null, ReportRange range = ReportRange.Day, params string[] games) =>
        NetReportBuilder.Build(Db, range, day ?? Day, Db.LoadApps().ToDictionary(a => a.Id), new RigsightSettings(), games);

    private Insight? Said(string key, DateTime? day = null, params string[] games) => Build(day, games: games).Insights.SingleOrDefault(i => i.Key == key);

    [Fact]
    public void Nothing_recorded_is_no_data()
    {
        var r = Build();
        Assert.False(r.HasData);
        Assert.Empty(r.Insights);
    }

    [Fact]
    public void A_day_adds_up_its_apps_hours_and_downloads()
    {
        long steam = App("steam.exe", "Steam"), chrome = App("chrome.exe", "Google Chrome");
        Use(Day, best: 12 * MB, (steam, 3 * GB, 10 * MB, 3 * GB, 0, 0), (chrome, GB, 50 * MB, 0, 0, 0));
        long start = TimeUtil.ToUnix(Day.AddHours(14));
        Db.InsertNetTransfer(new NetTransfer(start, steam, start + 600, 2 * GB, 600));

        var r = Build();
        Assert.True(r.HasData);
        Assert.Equal(4 * GB, r.Down);
        Assert.Equal(24, r.Bins.Count);
        Assert.Equal(4 * GB, r.Bins[12].Down);
        Assert.Equal(3 * GB, r.Bins[12].BgDown);
        Assert.Equal(["Steam", "Google Chrome"], r.Apps.Select(a => a.Name));
        Assert.Equal(Day.AddHours(12), r.Apps[0].BusiestAt);
        Assert.Equal(2.0 * GB / 600, r.Apps[0].Fastest);
        Assert.Equal(12 * MB, r.TopSpeed);
        Assert.Equal("Steam", r.TopSpeedApp);
        var d = Assert.Single(r.Downloads);
        Assert.Equal(("Steam", 2 * GB), (d.App, d.Bytes));
    }

    [Fact]
    public void A_week_has_a_bar_a_day_and_a_year_a_bar_a_month()
    {
        long steam = App("steam.exe", "Steam");
        Use(Day, null, (steam, GB, 0, 0, 0, 0));
        Use(Day.AddDays(-1), null, (steam, 2 * GB, 0, 0, 0, 0));
        var week = Build(range: ReportRange.Week);
        Assert.Equal(7, week.Bins.Count);
        Assert.Equal(3 * GB, week.Bins.Sum(b => b.Down));
        var year = Build(range: ReportRange.Year);
        Assert.Equal(12, year.Bins.Count);
        Assert.Equal(3 * GB, year.Bins[5].Down);
    }

    [Fact]
    public void A_day_against_a_usual_day_needs_a_week_of_days()
    {
        long steam = App("steam.exe", "Steam");
        for (int i = 1; i <= 6; i++) Use(Day.AddDays(-i), null, (steam, i * GB, 0, 0, 0, 0));
        Use(Day, null, (steam, GB, 0, 0, 0, 0));
        Assert.Null(Build().UsualDayDown);
        Use(Day.AddDays(-7), null, (steam, 7 * GB, 0, 0, 0, 0));
        Assert.Equal(4 * GB, Build().UsualDayDown);
    }

    // ── 1. Background downloads ──

    [Fact]
    public void An_app_downloading_a_lot_while_you_were_away_is_said()
    {
        long steam = App("steam.exe", "Steam");
        Use(Day, null, (steam, 3 * GB, 0, GB / 2, 5 * GB / 2, 0));
        Assert.Equal("Steam downloaded 3.0 GB that day, 2.5 GB of it while you were away.", Said("net-background")?.Text);
    }

    [Fact]
    public void An_app_downloading_in_the_background_with_you_there_is_said_so()
    {
        long steam = App("steam.exe", "Steam");
        Use(Day, null, (steam, 3 * GB, 0, 3 * GB, 0, 0));
        Assert.Equal("Steam downloaded 3.0 GB that day, 3.0 GB of it in the background.", Said("net-background")?.Text);
    }

    [Theory]
    [InlineData(1.5, 1.5)]   // not much
    [InlineData(5.0, 2.0)]   // mostly in front
    public void A_small_or_mostly_in_front_download_is_not(double gb, double bgGb)
    {
        long steam = App("steam.exe", "Steam");
        Use(Day, null, (steam, (long)(gb * GB), 0, (long)(bgGb * GB), 0, 0));
        Assert.Null(Said("net-background"));
    }

    // ── 2. Windows sharing updates ──

    [Fact]
    public void Windows_sharing_updates_with_the_internet_is_said()
    {
        long dosvc = Db.UpsertApp("svchost.exe:DoSvc", "Delivery Optimization", null, AppCategory.System);
        Use(Day, null, (dosvc, 90 * MB, 1100 * MB, 0, 0, 0));
        Assert.StartsWith("Windows uploaded 1.1 GB of updates to other PCs on the internet that day.", Said("net-sharing")?.Text);
    }

    [Fact]
    public void A_little_sharing_is_not()
    {
        long dosvc = Db.UpsertApp("svchost.exe:DoSvc", "Delivery Optimization", null, AppCategory.System);
        Use(Day, null, (dosvc, 0, 300 * MB, 0, 0, 0));
        Assert.Null(Said("net-sharing"));
    }

    // ── 3. Uploads ──

    private long OneDriveUsually(long upload, int days)
    {
        long app = App("onedrive.exe", "OneDrive");
        for (int i = 1; i <= days; i++) Use(Day.AddDays(-i), null, (app, MB, upload, 0, 0, 0));
        return app;
    }

    [Fact]
    public void An_app_uploading_far_more_than_usual_is_said()
    {
        long app = OneDriveUsually(100 * MB, days: 8);
        Use(Day, null, (app, MB, 2 * GB, 0, 0, 0));
        var line = Said("net-upload");
        Assert.Equal("OneDrive uploaded 2.0 GB that day, against about 100 MB on a usual day.", line?.Text);
        Assert.Equal(InsightTone.Neutral, line!.Tone);
    }

    [Fact]
    public void An_app_that_never_uploads_sending_a_lot_is_a_warning()
    {
        long app = OneDriveUsually(10_000, days: 8);
        Use(Day, null, (app, MB, 2 * GB, 0, 0, 0));
        var line = Said("net-upload");
        Assert.Equal("OneDrive uploaded 2.0 GB that day. It usually uploads next to nothing.", line?.Text);
        Assert.Equal(InsightTone.Warn, line!.Tone);
    }

    [Theory]
    [InlineData(600, 8)]    // 2 GB is under 4 times 600 MB
    [InlineData(100, 6)]    // too few days to know its usual
    public void An_upload_like_usual_or_without_a_usual_is_not(long usualMb, int days)
    {
        long app = OneDriveUsually(usualMb * MB, days);
        Use(Day, null, (app, MB, 2 * GB, 0, 0, 0));
        Assert.Null(Said("net-upload"));
    }

    // ── 4. Speed ──

    private void Speeds(params (int DaysBack, double MBps)[] days)
    {
        long steam = App("steam.exe", "Steam");
        foreach (var (back, speed) in days) Use(Day.AddDays(-back), (long)(speed * MB), (steam, GB, 0, 0, 0, 0));
    }

    private static (int, double)[] Usual(double mbps) => [.. Enumerable.Range(8, 12).Select(d => (d, mbps))];

    [Fact]
    public void Downloads_topping_out_lower_all_week_is_a_warning()
    {
        Speeds([.. Usual(11.8), (6, 11.8), (2, 6.0), (1, 6.1), (0, 6.2)]);
        // Six days ago is still in the week: its best wins, so no drop yet.
        Assert.Null(Said("net-speed"));
    }

    [Fact]
    public void The_week_best_well_under_the_usual_is_said_once()
    {
        Speeds([.. Usual(11.8), (2, 6.0), (1, 6.1), (0, 6.2)]);
        var line = Said("net-speed");
        Assert.Equal("Your downloads topped out at 6.2 MB/s (52 Mbps) over the last 7 days, down from your usual 11.8 MB/s (99 Mbps).", line?.Text);
        Assert.Equal(InsightTone.Warn, line!.Tone);
        // The day after, it was already said.
        Use(Day.AddDays(1), (long)(6 * MB), (Db.LoadApps().Single().Id, GB, 0, 0, 0, 0));
        Assert.Null(Said("net-speed", Day.AddDays(1)));
    }

    [Fact]
    public void One_slow_day_is_not_a_slow_line()
    {
        Speeds([.. Usual(11.8), (2, 11.7), (1, 4.0), (0, 11.9)]);
        Assert.Null(Said("net-speed"));
    }

    [Fact]
    public void Too_few_big_downloads_say_nothing_about_speed()
    {
        Speeds([.. Usual(11.8), (1, 4.0), (0, 4.1)]);
        Assert.Null(Said("net-speed"));
    }

    // ── 5. Behind a game ──

    [Fact]
    public void Downloading_behind_a_game_names_the_game()
    {
        long steam = App("steam.exe", "Steam");
        Use(Day, null, (steam, GB, 0, GB, 0, 700 * MB));
        Assert.Equal("Steam downloaded 700 MB while you played Elden Ring.", Said("net-game", games: ["Elden Ring"])?.Text);
        Assert.Equal("Steam downloaded 700 MB while you were in a game.", Said("net-game", games: ["Elden Ring", "Dota 2"])?.Text);
    }

    [Fact]
    public void A_little_behind_a_game_is_not()
    {
        long steam = App("steam.exe", "Steam");
        Use(Day, null, (steam, GB, 0, GB, 0, 300 * MB));
        Assert.Null(Said("net-game", games: ["Elden Ring"]));
    }

    // ── 6. Records ──

    [Fact]
    public void The_biggest_download_day_in_a_month_is_said()
    {
        long steam = App("steam.exe", "Steam");
        for (int i = 1; i <= 30; i++) Use(Day.AddDays(-i), null, (steam, 2 * GB, 0, 0, 0, 0));
        Use(Day, null, (steam, 6 * GB, 0, 0, 0, 0));
        Assert.Equal("Your biggest download day in a month: 6.0 GB.", Said("net-record")?.Text);
    }

    [Fact]
    public void A_record_needs_the_history_to_reach_back()
    {
        long steam = App("steam.exe", "Steam");
        for (int i = 1; i <= 20; i++) Use(Day.AddDays(-i), null, (steam, 2 * GB, 0, 0, 0, 0));
        Use(Day, null, (steam, 6 * GB, 0, 0, 0, 0));
        Assert.Null(Said("net-record"));
    }

    // ── 7. The recap line ──

    [Fact]
    public void The_recap_line_sets_the_day_against_the_one_before()
    {
        long steam = App("steam.exe", "Steam"), chrome = App("chrome.exe", "Google Chrome");
        Use(Day.AddDays(-1), null, (steam, GB, 0, 0, 0, 0));
        Use(Day, null, (steam, 3 * GB / 2, 0, 0, 0, 0), (chrome, GB / 2, 0, 0, 0, 0));
        Assert.Equal("That day: 2.0 GB downloaded, 2.0× yesterday, mostly Steam.", Said("net-recap")?.Text);
    }

    [Fact]
    public void A_quiet_day_has_no_recap_line()
    {
        long chrome = App("chrome.exe", "Google Chrome");
        Use(Day, null, (chrome, 50 * MB, 0, 0, 0, 0));
        Assert.Null(Said("net-recap"));
    }

    // ── 8. Drops ──

    private void Drops(DateTime day, int count, NetDropKind kind = NetDropKind.Internet)
    {
        for (int i = 0; i < count; i++)
        {
            long start = TimeUtil.ToUnix(day.AddHours(9 + i));
            Db.InsertNetDrop(new NetDrop(start, start + 60 + i * 60, kind));
        }
    }

    [Fact]
    public void Many_drops_without_a_usual_to_go_by_are_said()
    {
        long chrome = App("chrome.exe", "Google Chrome");
        Use(Day, null, (chrome, GB, 0, 0, 0, 0));
        Drops(Day, 5);
        var line = Said("net-drops");
        Assert.Equal("Your internet dropped 5 times that day, the longest for 5m at 1:00 PM. Your PC stayed connected to the router each time, "
            + "so the trouble was past it: the router or your internet provider.", line?.Text);
        Assert.Equal(InsightTone.Warn, line!.Tone);
    }

    [Fact]
    public void Drops_of_the_cable_or_WiFi_say_so()
    {
        long chrome = App("chrome.exe", "Google Chrome");
        Use(Day, null, (chrome, GB, 0, 0, 0, 0));
        Drops(Day, 5, NetDropKind.Link);
        Assert.EndsWith("Each time your PC's own connection to the router (the cable or Wi-Fi) went down.", Said("net-drops")?.Text);
    }

    [Fact]
    public void Drops_as_usual_are_not_news()
    {
        long chrome = App("chrome.exe", "Google Chrome");
        for (int i = 1; i <= 10; i++)
        {
            Use(Day.AddDays(-i), null, (chrome, GB, 0, 0, 0, 0));
            Drops(Day.AddDays(-i), 2);
        }
        Use(Day, null, (chrome, GB, 0, 0, 0, 0));
        Drops(Day, 3);
        Assert.Null(Said("net-drops")); // 3 is under twice the usual 2
        Drops(Day.AddHours(0.5), 2);
        Assert.NotNull(Said("net-drops")); // 5 is over it
    }

    [Fact]
    public void A_few_drops_on_a_new_install_are_not()
    {
        long chrome = App("chrome.exe", "Google Chrome");
        Use(Day, null, (chrome, GB, 0, 0, 0, 0));
        Drops(Day, 3);
        Assert.Null(Said("net-drops"));
    }

    [Fact]
    public void A_report_carries_the_network_lines_into_the_insights()
    {
        long steam = App("steam.exe", "Steam");
        Db.WriteMinute(new SystemMinute { Ts = TimeUtil.ToUnix(Day.AddHours(12)), ActiveSec = 60, CpuTemp = 50, GpuTemp = 50 });
        Use(Day, null, (steam, 3 * GB, 0, 0, 3 * GB, 0));
        var report = ReportBuilder.Build(Db, ReportRange.Day, Day, new RigsightSettings());
        Assert.NotNull(report.Net);
        Assert.Contains(report.Insights, i => i.Key == "net-background");
    }
}
