using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Tests.Data;

namespace Rigsight.Tests.Reports;

/// <summary>Each app's internet use for the Apps page: the same figures the Network page lists for the same period.</summary>
public sealed class AppInternetTests : IDisposable
{
    private const long MB = 1L << 20, GB = 1L << 30;
    private static readonly DateTime Day = new(2026, 6, 10);

    private readonly TestDb _t = new("appinternet");
    private RigsightDb Db => _t.Db;
    public void Dispose() => _t.Dispose();

    private long App(string exe, string name) => Db.UpsertApp(exe, name, null, AppCategory.Other);

    /// <summary>One minute's internet use at <paramref name="at"/>, and each app's share of it.</summary>
    private void Use(DateTime at, params (long App, long Down, long Up, long Bg, long Away)[] apps)
    {
        long ts = TimeUtil.ToUnix(at);
        Db.WriteNetMinute(new NetMinute(ts, apps.Sum(a => a.Down), apps.Sum(a => a.Up), apps.Sum(a => a.Bg), 0, apps.Sum(a => a.Away), 0, 0, null, apps[0].App));
        foreach (var (app, d, u, bg, away) in apps)
            Db.AddNetAppUse(new NetAppUse { Ts = ts, App = app, Down = d, Up = u, BgDown = bg, AwayDown = away });
    }

    private Dictionary<long, AppRow> Apps => Db.LoadApps().ToDictionary(a => a.Id);

    private AppInternet Of(DateTime from, DateTime to) => AppInternet.Of(Db, from, to, Apps, new RigsightSettings());

    [Theory]
    [InlineData(ReportRange.Day)]
    [InlineData(ReportRange.Week)]
    [InlineData(ReportRange.Month)]
    [InlineData(ReportRange.Year)]
    [InlineData(ReportRange.All)]
    public void Every_app_has_what_the_Network_page_lists_for_the_same_period(ReportRange range)
    {
        long steam = App("steam.exe", "Steam"), chrome = App("chrome.exe", "Google Chrome"), warp = App("warp-svc.exe", "Cloudflare WARP");
        Use(Day.AddHours(9), (steam, 3 * GB, 10 * MB, 3 * GB, 0), (chrome, GB, 50 * MB, 0, 0));
        Use(Day.AddHours(20), (chrome, 200 * MB, 5 * MB, 20 * MB, 100 * MB), (warp, 30 * MB, 30 * MB, 30 * MB, 0));
        Use(Day.AddDays(-3).AddHours(12), (chrome, 700 * MB, 0, 0, 0));
        Use(Day.AddMonths(-2).AddHours(12), (steam, 5 * GB, 0, 0, 0));

        var net = NetReportBuilder.Build(Db, range, Day, Apps, new RigsightSettings());
        var (from, to) = ReportBuilder.Bounds(range, Day);
        var mine = AppInternet.Of(Db, from, to, Apps, new RigsightSettings());

        Assert.True(mine.Recorded);
        Assert.Equal(net.Apps.Select(a => a.Id).Order(), mine.Use.Keys.Order());
        foreach (var a in net.Apps)
        {
            var u = mine.Use[a.Id];
            Assert.Equal((a.Use.Down, a.Use.Up, a.Use.Background), (u.Down, u.Up, u.Background));
            Assert.Equal(a.Use.Total, mine.Apps.Single(s => s.Id == a.Id).NetBytes);
            Assert.Equal(a.Name, mine.Apps.Single(s => s.Id == a.Id).Name);
        }
    }

    [Fact]
    public void A_day_adds_up_download_upload_and_the_background_share()
    {
        long chrome = App("chrome.exe", "Google Chrome");
        Use(Day.AddHours(9), (chrome, GB, 50 * MB, 0, 0));
        Use(Day.AddHours(20), (chrome, 200 * MB, 5 * MB, 20 * MB, 100 * MB));
        var u = Of(Day, Day.AddDays(1)).Use[chrome];
        Assert.Equal(GB + 200 * MB, u.Down);
        Assert.Equal(55 * MB, u.Up);
        Assert.Equal(120 * MB, u.Background); // in the background, and while nobody was there
    }

    [Fact]
    public void A_custom_range_inside_a_day_adds_up_only_its_hours()
    {
        long chrome = App("chrome.exe", "Google Chrome");
        Use(Day.AddHours(9), (chrome, GB, 0, 0, 0));
        Use(Day.AddHours(20), (chrome, 200 * MB, 0, 0, 0));
        Assert.Equal(GB, Of(Day.AddHours(8), Day.AddHours(12)).Use[chrome].Down);
        Assert.Equal(200 * MB, Of(Day.AddHours(18), Day.AddDays(1).AddHours(2)).Use[chrome].Down);
        // Hours with nothing in them, on a day that was recorded: recorded, and nobody used any.
        var quiet = Of(Day.AddHours(13), Day.AddHours(15));
        Assert.True(quiet.Recorded);
        Assert.Empty(quiet.Use);
    }

    [Fact]
    public void An_app_with_no_use_is_left_out_and_a_period_with_none_recorded_says_so()
    {
        long chrome = App("chrome.exe", "Google Chrome");
        long idle = App("notepad.exe", "Notepad");
        Use(Day.AddHours(9), (chrome, GB, 0, 0, 0), (idle, 0, 0, 0, 0));
        var day = Of(Day, Day.AddDays(1));
        Assert.True(day.Recorded);
        Assert.False(day.Use.ContainsKey(idle));
        Assert.DoesNotContain(day.Apps, a => a.Id == idle);

        var before = Of(Day.AddDays(-5), Day.AddDays(-4));
        Assert.False(before.Recorded);
        Assert.Empty(before.Use);
        Assert.False(Of(Day, Day).Recorded);
    }

    [Fact]
    public void A_renamed_app_goes_by_its_new_name()
    {
        long chrome = App("chrome.exe", "Google Chrome");
        Use(Day.AddHours(9), (chrome, GB, 0, 0, 0));
        var settings = new RigsightSettings();
        settings.AppNames["chrome.exe"] = "Browser";
        Assert.Equal("Browser", AppInternet.Of(Db, Day, Day.AddDays(1), Apps, settings).Apps.Single().Name);
    }
}
