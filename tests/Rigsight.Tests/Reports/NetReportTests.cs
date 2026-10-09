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
        // The biggest hours: each app at noon, with everything it moved then.
        Assert.Equal([("Steam", 3 * GB + 10 * MB), ("Google Chrome", GB + 50 * MB)], r.Downloads.Select(x => (x.App, x.Bytes)));
        Assert.All(r.Downloads, x => Assert.Equal(Day.AddHours(12), x.Start));
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
        Assert.Equal("Steam downloaded 3.0 GB that day, all of it in the background.", Said("net-background")?.Text);
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
    public void A_week_of_slower_downloads_says_nothing_about_the_line()
    {
        // A day's fastest download follows what was downloaded that day (a game from Steam, a film in a browser), not
        // what the line can do: on one unchanged 100 Mbps line the days' bests ran from 4 to 95 Mbps. So a week that
        // topped out at half the usual is no warning (it was one until 0.18.1).
        Speeds([.. Usual(11.8), (2, 6.0), (1, 6.1), (0, 6.2)]);
        var r = Build();
        Assert.DoesNotContain(r.Insights, i => i.Key == "net-speed");
        Assert.DoesNotContain(r.Insights, i => i.Text.Contains("topped out"));
        // The day's own fastest is still shown as a figure.
        Assert.Equal((long)(6.2 * MB), r.TopSpeed);
    }

    // ── The biggest hours and days, any sort of use ──

    [Fact]
    public void The_biggest_hours_count_any_sort_of_use_upload_too()
    {
        long steam = App("steam.exe", "Steam"), meet = App("meet.exe", "Meet"), backup = App("backup.exe", "Backup"), tiny = App("tiny.exe", "Tiny");
        // A game downloading at full speed; a two-hour call that never moves fast; a backup that only uploads.
        Hour(Day, 7, (steam, 30 * GB, 10 * MB, 0, 0));
        Hour(Day, 10, (meet, 400 * MB, 500 * MB, 0, 0), (tiny, 10 * MB, 0, 0, 0));
        Hour(Day, 11, (meet, 380 * MB, 450 * MB, 0, 0));
        Hour(Day, 15, (backup, MB, 2 * GB, 0, 0));

        var r = Build();
        Assert.Equal([("Steam", 7), ("Backup", 15), ("Meet", 10), ("Meet", 11)], r.Downloads.Select(d => (d.App, d.Start.Hour)));
        Assert.Equal(30 * GB + 10 * MB, r.Downloads[0].Bytes);       // both directions
        Assert.Equal(2 * GB + MB, r.Downloads[1].Bytes);             // upload alone gets it listed
        Assert.Equal(Day.AddHours(8), r.Downloads[0].End);
        // An app that moved next to nothing in its hour isn't one of the biggest.
        Assert.DoesNotContain(r.Downloads, d => d.App == "Tiny");
    }

    [Fact]
    public void A_longer_period_lists_its_biggest_days_and_at_most_five()
    {
        long steam = App("steam.exe", "Steam"), chrome = App("chrome.exe", "Google Chrome");
        for (int i = 0; i < 4; i++) Hour(Day.AddDays(-i), 9, (steam, (10 - i) * GB, 0, 0, 0), (chrome, (i + 1) * GB, 0, 0, 0));
        var week = Build(range: ReportRange.Week);
        var listed = week.Downloads;
        Assert.Equal(NetReportBuilder.DownloadsShown, listed.Count);
        Assert.Equal(("Steam", Day, 10 * GB), (listed[0].App, listed[0].Start, listed[0].Bytes));
        Assert.Equal(Day.AddDays(1), listed[0].End);
        Assert.Equal(listed.OrderByDescending(d => d.Bytes).Select(d => d.Bytes), listed.Select(d => d.Bytes));
    }

    // ── Who each bar is ──

    private void Hour(DateTime day, int hour, params (long App, long Down, long Up, long Bg, long Away)[] apps)
    {
        long ts = TimeUtil.ToUnix(day.AddHours(hour));
        Db.WriteNetMinute(new NetMinute(ts, apps.Sum(a => a.Down), apps.Sum(a => a.Up), apps.Sum(a => a.Bg), 0, apps.Sum(a => a.Away), 0, 0, null, null));
        foreach (var (app, d, u, bg, away) in apps)
            Db.AddNetAppUse(new NetAppUse { Ts = ts, App = app, Down = d, Up = u, BgDown = bg, AwayDown = away });
    }

    [Fact]
    public void Each_hour_of_a_day_knows_its_apps_and_its_background()
    {
        long steam = App("steam.exe", "Steam"), chrome = App("chrome.exe", "Google Chrome"), tiny = App("tiny.exe", "Tiny");
        Hour(Day, 7, (steam, 3 * GB, 10 * MB, 3 * GB, 0), (chrome, 200 * MB, 5 * MB, 0, 0), (tiny, 100_000, 0, 0, 0));
        Hour(Day, 20, (chrome, GB, 50 * MB, 0, 0), (steam, 300 * MB, 0, 0, 300 * MB));

        var r = Build();
        var seven = r.Bins[7];
        Assert.Equal(3 * GB + 200 * MB + 100_000 + 15 * MB, seven.Total);
        // Biggest first; an app that moved next to nothing isn't listed.
        Assert.Equal([steam, chrome], seven.Apps.Select(a => a.App));
        Assert.Equal(3 * GB + 10 * MB, seven.Apps[0].Total);
        Assert.Equal(3 * GB, seven.Background);
        Assert.Equal(0, seven.Away);
        var evening = r.Bins[20];
        Assert.Equal([chrome, steam], evening.Apps.Select(a => a.App));
        Assert.Equal(300 * MB, evening.Background);
        Assert.Equal(300 * MB, evening.Away);
        Assert.Empty(r.Bins[3].Apps);
        Assert.Equal(0, r.Bins[3].Total);
    }

    [Fact]
    public void A_weeks_days_and_a_years_months_know_their_apps_too()
    {
        long steam = App("steam.exe", "Steam"), chrome = App("chrome.exe", "Google Chrome");
        Hour(Day, 7, (steam, 3 * GB, 0, 3 * GB, 0), (chrome, GB, 0, 0, 0));
        Hour(Day.AddDays(-1), 9, (chrome, 2 * GB, 0, 0, 0));
        var week = Build(range: ReportRange.Week);
        var day = week.Bins.Single(b => b.Start == Day);
        Assert.Equal([steam, chrome], day.Apps.Select(a => a.App));
        Assert.Equal(3 * GB, day.Background);
        Assert.Equal([chrome], week.Bins.Single(b => b.Start == Day.AddDays(-1)).Apps.Select(a => a.App));
        Assert.Empty(week.Bins.Single(b => b.Start == Day.AddDays(1)).Apps);
        var june = Build(range: ReportRange.Year).Bins[5];
        Assert.Equal([steam, chrome], june.Apps.Select(a => a.App));
        Assert.Equal(3 * GB, june.Apps[0].Total);
        Assert.Equal(3 * GB, june.Apps[1].Total);
    }

    private NetReport Colours(RigsightSettings settings, DateTime asOf, DateTime? day = null) =>
        NetReportBuilder.Build(Db, ReportRange.Day, day ?? asOf, Db.LoadApps().ToDictionary(x => x.Id), settings, asOf: asOf.AddHours(20));

    [Fact]
    public void Free_colours_go_to_the_biggest_users_of_the_last_month()
    {
        long a = App("a.exe", "A"), b = App("b.exe", "B"), c = App("c.exe", "C"), d = App("d.exe", "D"), e = App("e.exe", "E"), old = App("old.exe", "Old");
        // Three months ago one app was by far the biggest; it hasn't been online since.
        Hour(Day.AddDays(-90), 9, (old, 500 * GB, 0, 0, 0));
        Hour(Day.AddDays(-3), 9, (a, 50 * GB, 0, 0, 0), (b, 20 * GB, 0, 0, 0));
        Hour(Day, 9, (e, 5 * GB, 0, 0, 0), (c, 4 * GB, 0, 0, 0), (d, 3 * GB, 0, 0, 0), (b, GB, 0, 0, 0));
        var r = Colours(new RigsightSettings(), Day);
        Assert.Equal([a, b, e, c], r.ColorApps);
        Assert.Equal(["a.exe", "b.exe", "e.exe", "c.exe"], r.ColorExes);
        // The same four whichever day is looked at.
        Assert.Equal(r.ColorApps, Colours(new RigsightSettings(), Day, Day.AddDays(-3)).ColorApps);
        Assert.True(r.ColorsSettled);
    }

    [Fact]
    public void An_app_keeps_its_colour_when_another_overtakes_it()
    {
        long steam = App("steam.exe", "Steam"), chrome = App("chrome.exe", "Google Chrome"), late = App("late.exe", "Late");
        Hour(Day.AddDays(-8), 9, (steam, 37 * GB, 0, 0, 0), (chrome, 31 * GB, 0, 0, 0));
        var settings = new RigsightSettings();
        var first = Colours(settings, Day.AddDays(-1), Day.AddDays(-8));
        Assert.Equal([steam, chrome], first.ColorApps);
        Assert.True(first.ColorsSettled);
        settings.NetColorApps = [.. first.ColorExes]; // remembered, as the app does once the choice is settled

        // Chrome passes Steam, and a third app passes both: nobody trades colours; the newcomer gets the next one free.
        Hour(Day, 9, (chrome, 20 * GB, 0, 0, 0), (late, 90 * GB, 0, 0, 0));
        var later = Colours(settings, Day);
        Assert.Equal([steam, chrome, late], later.ColorApps);
        Assert.Equal(later.ColorApps, Colours(settings, Day, Day.AddDays(-8)).ColorApps);
    }

    [Fact]
    public void An_app_that_stopped_using_the_internet_gives_its_colour_to_the_biggest_without_one()
    {
        long a = App("a.exe", "A"), b = App("b.exe", "B"), c = App("c.exe", "C"), d = App("d.exe", "D"), e = App("e.exe", "E"), f = App("f.exe", "F");
        Hour(Day.AddDays(-60), 9, (b, 80 * GB, 0, 0, 0));
        Hour(Day, 9, (a, 9 * GB, 0, 0, 0), (c, 7 * GB, 0, 0, 0), (d, 6 * GB, 0, 0, 0), (e, 5 * GB, 0, 0, 0), (f, 8 * GB, 0, 0, 0));
        // B has the second colour and hasn't been online for two months; an app that was uninstalled has the fourth.
        var settings = new RigsightSettings { NetColorApps = ["a.exe", "b.exe", "c.exe", "gone.exe"] };
        var r = Colours(settings, Day);
        // A and C stay where they are; the two free places go to the biggest without a colour, in order: F, then D.
        Assert.Equal(["a.exe", "f.exe", "c.exe", "d.exe"], r.ColorExes);
        Assert.Equal([a, f, c, d], r.ColorApps);
    }

    [Fact]
    public void A_new_PCs_first_days_do_not_fix_the_colours()
    {
        long setup = App("setup.exe", "Setup"), steam = App("steam.exe", "Steam");
        // The first day of recording: an installer is the biggest user there has ever been.
        Hour(Day, 9, (setup, 4 * GB, 0, 0, 0), (steam, GB, 0, 0, 0));
        var first = Colours(new RigsightSettings(), Day);
        Assert.Equal(["setup.exe", "steam.exe"], first.ColorExes); // coloured by use for now
        Assert.False(first.ColorsSettled);                          // and not to be remembered yet
        Assert.False(Colours(new RigsightSettings(), Day.AddDays(6)).ColorsSettled);
        // A week in, with Steam far ahead: this is what gets remembered.
        Hour(Day.AddDays(7), 9, (steam, 60 * GB, 0, 0, 0));
        var settled = Colours(new RigsightSettings(), Day.AddDays(7));
        Assert.True(settled.ColorsSettled);
        Assert.Equal(["steam.exe", "setup.exe"], settled.ColorExes);
        // The settings file never holds more than there are colours, or the same app twice.
        var kept = SettingsStore.Deserialize(SettingsStore.Serialize(new RigsightSettings { NetColorApps = ["a.exe", "A.EXE", " ", "b.exe", "c.exe", "d.exe", "e.exe"] }));
        Assert.Equal(["a.exe", "b.exe", "c.exe", "d.exe"], kept.NetColorApps);
    }

    // ── Not recorded ──

    /// <summary>The PC on from <paramref name="from"/> for <paramref name="minutes"/>, with internet use written or not.</summary>
    private void On(DateTime from, int minutes, bool net)
    {
        for (int i = 0; i < minutes; i++)
        {
            long ts = TimeUtil.ToUnix(from.AddMinutes(i));
            Db.WriteMinute(Make.Minute(ts));
            if (net) Db.WriteNetMinute(new NetMinute(ts, MB, 0, 0, 0, 0, 0, 0, null, null));
        }
    }

    [Fact]
    public void The_PC_on_with_nothing_written_is_not_recorded_not_zero()
    {
        // 5 Oct 2026: the reading of app network use went quiet from 1 PM to 8:30 PM. The day showed zeros and "No drops".
        On(Day.AddHours(8), 300, net: true);    // 8 AM to 1 PM
        On(Day.AddHours(13), 450, net: false);  // 1 PM to 8:30 PM: nothing
        On(Day.AddHours(20.5), 60, net: true);
        var r = Build();
        Assert.Equal([(Day.AddHours(13), Day.AddHours(20.5))], r.Unrecorded);
        Assert.Empty(r.Drops);
    }

    [Fact]
    public void Each_bar_knows_how_long_of_it_was_not_recorded()
    {
        On(Day.AddHours(8), 300, net: true);            // 8 AM to 1 PM
        On(Day.AddHours(13), 450, net: false);          // 1 PM to 8:30 PM: nothing
        On(Day.AddHours(20.5), 60, net: true);
        var day = Build();
        Assert.Equal(0, day.Bins[12].UnrecordedMinutes);
        Assert.All(Enumerable.Range(13, 7), h => Assert.Equal(60, day.Bins[h].UnrecordedMinutes));
        Assert.Equal(30, day.Bins[20].UnrecordedMinutes);
        Assert.Equal(0, day.Bins[21].UnrecordedMinutes);
        // The week's bar for that day carries the whole of it, and the week says so too; a year's months don't look.
        var week = Build(range: ReportRange.Week);
        Assert.Equal(450, week.Bins.Single(b => b.Start == Day).UnrecordedMinutes);
        Assert.Equal(0, week.Bins.Single(b => b.Start == Day.AddDays(-1)).UnrecordedMinutes);
        Assert.Single(week.Unrecorded);
        Assert.All(Build(range: ReportRange.Year).Bins, b => Assert.Equal(0, b.UnrecordedMinutes));
    }

    [Fact]
    public void A_few_quiet_minutes_or_the_PC_being_off_is_not_unrecorded()
    {
        On(Day.AddHours(8), 60, net: true);
        On(Day.AddHours(9), 14, net: false);                 // under a quarter hour
        On(Day.AddHours(9).AddMinutes(14), 30, net: true);
        // Off from 9:44 to 6 PM: no minutes at all.
        On(Day.AddHours(18), 30, net: true);
        Assert.Empty(Build().Unrecorded);
    }

    [Fact]
    public void A_drop_explains_its_own_silence()
    {
        On(Day.AddHours(8), 60, net: true);
        On(Day.AddHours(9), 44, net: false);
        On(Day.AddHours(9).AddMinutes(44), 30, net: true);
        Db.InsertNetDrop(new NetDrop(TimeUtil.ToUnix(Day.AddHours(9)) - 30, TimeUtil.ToUnix(Day.AddHours(9).AddMinutes(44)), NetDropKind.Internet));
        var r = Build();
        Assert.Single(r.Drops);
        Assert.Empty(r.Unrecorded);
    }

    [Fact]
    public void Nothing_is_unrecorded_before_internet_use_was_first_recorded()
    {
        On(Day.AddDays(-1).AddHours(8), 600, net: false); // the day before the first record: an older version
        On(Day.AddHours(8), 60, net: false);
        On(Day.AddHours(9), 60, net: true);
        Assert.Empty(Build().Unrecorded);
        Assert.Empty(Db.GetNetUnrecorded(0, long.MaxValue));
    }

    [Fact]
    public void A_day_with_an_hour_not_recorded_is_not_a_usual_day()
    {
        long steam = App("steam.exe", "Steam");
        for (int i = 1; i <= 7; i++) Use(Day.AddDays(-i), null, (steam, 4 * GB, 0, 0, 0, 0));
        Use(Day, null, (steam, GB, 0, 0, 0, 0));
        Assert.Equal(4 * GB, Build().UsualDayDown);
        // Two more days, both low because most of each wasn't recorded: they don't pull the usual down.
        foreach (int back in new[] { 8, 9 })
        {
            Use(Day.AddDays(-back), null, (steam, 100 * MB, 0, 0, 0, 0));
            On(Day.AddDays(-back).AddHours(13), 450, net: false);
        }
        Assert.Equal(4 * GB, Build().UsualDayDown);
        // A low day that was recorded whole does count.
        Use(Day.AddDays(-10), null, (steam, 100 * MB, 0, 0, 0, 0));
        Use(Day.AddDays(-11), null, (steam, 100 * MB, 0, 0, 0, 0));
        Use(Day.AddDays(-12), null, (steam, 100 * MB, 0, 0, 0, 0));
        Assert.Equal(4 * GB, Build().UsualDayDown); // 7 at 4 GB, 3 at 100 MB: the middle is still 4 GB
        Use(Day.AddDays(-13), null, (steam, 100 * MB, 0, 0, 0, 0));
        Use(Day.AddDays(-14), null, (steam, 100 * MB, 0, 0, 0, 0));
        Use(Day.AddDays(-15), null, (steam, 100 * MB, 0, 0, 0, 0));
        Use(Day.AddDays(-16), null, (steam, 100 * MB, 0, 0, 0, 0));
        Assert.Equal((4 * GB + 100 * MB) / 2, Build().UsualDayDown); // 7 and 7
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
        Use(Day.AddDays(-2), null, (steam, GB / 4, 0, 0, 0, 0));
        Use(Day.AddDays(-1), null, (steam, GB, 0, 0, 0, 0));
        Use(Day, null, (steam, 3 * GB / 2, 0, 0, 0, 0), (chrome, GB / 2, 0, 0, 0, 0));
        // A day looked back on: against "the day before" ("yesterday" is only said of today).
        Assert.Equal("2.0 GB downloaded, 2.0× the day before, mostly Steam.", Said("net-recap")?.Text);
    }

    [Fact]
    public void The_day_network_use_began_being_recorded_is_not_one_to_compare_with()
    {
        // The first day holds only what came after recording began: "2× the day before" against it would be false.
        long steam = App("steam.exe", "Steam");
        Use(Day.AddDays(-1), null, (steam, GB, 0, 0, 0, 0));
        Use(Day, null, (steam, 2 * GB, 0, 0, 0, 0));
        Assert.Equal("2.0 GB downloaded, mostly Steam.", Said("net-recap")?.Text);
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
