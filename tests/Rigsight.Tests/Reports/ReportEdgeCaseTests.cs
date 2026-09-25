using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;
using Rigsight.Tests.Data;
using static Rigsight.Tests.Data.Make;

namespace Rigsight.Tests.Reports;

/// <summary>Reports over unusual histories (nothing, one minute, only old data, apps without names) and the settings that change them.</summary>
public sealed class ReportEdgeCaseTests
{
    public static TheoryData<ReportRange> Ranges => [.. Enum.GetValues<ReportRange>()];

    private static Report Build(TestDb t, ReportRange range, DateTime anchor, RigsightSettings? settings = null)
    {
        var r = ReportBuilder.Build(t.Db, range, anchor, settings ?? Settings());
        ReportCheck.NoBadNumbers(r);
        return r;
    }

    [Theory]
    [MemberData(nameof(Ranges))]
    public void An_empty_database_gives_an_empty_report_without_insights(ReportRange range)
    {
        using var t = new TestDb();
        var r = Build(t, range, DateTime.Now);
        Assert.False(r.HasData);
        Assert.Empty(r.Insights);
        Assert.Empty(r.Apps);
        Assert.Empty(r.Sessions);
        Assert.Equal(0, r.OnSec);
        Assert.Null(r.CpuTempPeak);
        Assert.Null(r.CpuTempAvg);
        Assert.Equal(0, r.DaysWithData);
    }

    [Theory]
    [MemberData(nameof(Ranges))]
    public void An_empty_database_read_by_the_app_gives_an_empty_report(ReportRange range)
    {
        using var t = new TestDb();
        using var reader = t.Reader();
        Assert.False(ReportBuilder.Build(reader, range, DateTime.Now, Settings()).HasData);
    }

    [Theory]
    [MemberData(nameof(Ranges))]
    public void A_single_minute_of_history_makes_a_whole_report(ReportRange range)
    {
        Assert.SkipWhen(DateTime.Now.TimeOfDay < TimeSpan.FromMinutes(10), "too close to midnight");
        using var t = new TestDb();
        long app = t.Db.UpsertApp("game.exe", "Game", null, AppCategory.Game);
        long ts = U(TimeUtil.LocalMinuteStart(DateTime.Now.AddMinutes(-5)));
        t.Db.WriteMinute(Minute(ts, cpu: 60, app: app, cpuPower: 50));
        t.Db.AddAppHour(Hour(U(TimeUtil.LocalHourStart(L(ts))), app, fg: 60));
        var r = Build(t, range, DateTime.Now);
        Assert.True(r.HasData);
        Assert.Equal(60, r.OnSec);
        Assert.Equal(60, r.ActiveSec);
        Assert.Equal(62, r.CpuTempPeak!.Value);
        Assert.Equal(L(ts), r.CpuTempPeak.Time);
        Assert.Equal("Game", r.CpuTempPeak.App);
        Assert.Equal("Game", Assert.Single(r.Apps).Name);
        Assert.Equal(60, r.GamingSec);
        var early = Assert.Single(r.Insights, i => i.Key == "early");
        Assert.Equal("Too early for highlights: 1m of use so far.", early.Text);
    }

    [Theory]
    [MemberData(nameof(Ranges))]
    public void History_only_from_today_reports_today(ReportRange range)
    {
        using var t = new TestDb();
        var start = DateTime.Today.AddMinutes(1);
        int minutes = (int)Math.Min(120, (DateTime.Now - start).TotalMinutes);
        Assert.SkipWhen(minutes < 1, "too close to midnight");
        for (int i = 0; i < minutes; i++) t.Db.WriteMinute(Minute(U(start.AddMinutes(i)), app: 1));
        t.Db.AddAppHour(Hour(U(TimeUtil.LocalHourStart(start)), 1, fg: minutes * 60));
        var r = Build(t, range, DateTime.Now);
        Assert.True(r.HasData);
        Assert.Equal(minutes * 60, r.OnSec);
        Assert.Equal(minutes * 60, r.ActiveSec);
        if (range == ReportRange.All) Assert.Equal(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), Assert.Single(r.Days).Day);
    }

    [Fact]
    public void History_only_from_years_ago_is_in_all_time_but_not_in_recent_periods()
    {
        using var t = new TestDb();
        var old = DateTime.Today.AddYears(-3);
        for (int i = 0; i < 90; i++) t.Db.WriteMinute(Minute(U(old.AddHours(10).AddMinutes(i)), app: 1));
        t.Db.AddAppHour(Hour(U(old.AddHours(10)), 1, fg: 3600));
        t.Db.AddAppHour(Hour(U(old.AddHours(11)), 1, fg: 1800));
        foreach (var range in new[] { ReportRange.Day, ReportRange.Week, ReportRange.Month, ReportRange.Year })
            Assert.False(Build(t, range, DateTime.Now).HasData, $"{range}");
        var all = Build(t, ReportRange.All, DateTime.Now);
        Assert.True(all.HasData);
        Assert.Equal(90 * 60, all.OnSec);
        Assert.Equal(new DateTime(old.Year, old.Month, 1), all.Days[0].Day);
        Assert.Equal(90 * 60, all.Days[0].OnSec);
        var year = Build(t, ReportRange.Year, old);
        Assert.Equal(90 * 60, year.OnSec);
        Assert.Equal(12, year.Days.Count);
    }

    [Fact]
    public void Days_with_only_hourly_app_rows_still_count_their_time()
    {
        // Versions before 0.4.13 deleted minute detail after 90 days but kept the hourly per-app rows.
        using var t = new TestDb();
        var day = new DateTime(2024, 5, 14);
        t.Db.AddAppHour(Hour(U(day.AddHours(9)), 1, fg: 3000, idle: 300, cpuTemp: 60));
        t.Db.AddAppHour(Hour(U(day.AddHours(9)), 2, fg: 900, idle: 0, cpuTemp: 40)); // two apps in one hour: on is capped at the hour
        t.Db.AddAppHour(Hour(U(day.AddHours(10)), 1, fg: 600, idle: 0, cpuTemp: 50));
        foreach (var range in new[] { ReportRange.Day, ReportRange.Week, ReportRange.Month, ReportRange.Year, ReportRange.All })
        {
            var r = Build(t, range, day);
            Assert.True(r.HasData, $"{range}");
            Assert.Equal(4500, r.ActiveSec);
            Assert.Equal(300, r.AwaySec);
            Assert.Equal(3600 + 600, r.OnSec);
            Assert.Equal((600 + 400 + 500) / 30.0, r.CpuTempAvg!.Value, 6);
            Assert.Equal(63, r.CpuTempPeak!.Value);
            Assert.Equal(L(U(day.AddHours(9))), r.CpuTempPeak.Time);
            Assert.Null(r.CpuPowerPeak); // power highs are minute averages only
            if (range != ReportRange.Day) Assert.Equal(4500, r.Days.Sum(d => d.ActiveSec));
        }
    }

    [Fact]
    public void Apps_without_a_name_path_or_row_still_show_up()
    {
        using var t = new TestDb();
        t.Exec("INSERT INTO apps(id, exe, name, path, category, first_seen) VALUES(1, 'noname.exe', '', NULL, 'Other', 0)");
        long ts = U(2025, 4, 2, 10);
        t.Db.WriteMinute(Minute(ts, app: 1));
        t.Db.WriteMinute(Minute(ts + 60, app: 99, cpuMax: 99)); // an id with no apps row
        t.Db.AddAppHour(Hour(ts, 1, fg: 60));
        t.Db.AddAppHour(Hour(ts, 99, fg: 60));
        t.Db.InsertSession(Session(99, ts, ts + 120));
        foreach (var range in Enum.GetValues<ReportRange>())
        {
            var r = Build(t, range, L(ts));
            var ghost = r.Apps.Single(a => a.Id == 99);
            Assert.Equal(("Unknown", "?", null, AppCategory.Other), (ghost.Name, ghost.Exe, ghost.Path, ghost.Category));
            var nameless = r.Apps.Single(a => a.Id == 1);
            Assert.Equal(("", "noname.exe", null), (nameless.Name, nameless.Exe, nameless.Path));
            Assert.Equal("Unknown", r.CpuTempPeak!.App);
            Assert.Equal("Unknown", Assert.Single(r.Sessions).Name);
            Assert.Equal("?", r.Sessions[0].Exe);
        }
    }

    [Fact]
    public void A_minute_with_nothing_in_front_has_no_app_in_the_timeline_or_peak()
    {
        using var t = new TestDb();
        long ts = U(2025, 4, 2, 10);
        t.Db.WriteMinute(Minute(ts, app: null, active: 0, idle: 0, cpuMax: 90));
        var r = Build(t, ReportRange.Day, L(ts));
        Assert.Null(r.CpuTempPeak!.App);
        var seg = Assert.Single(r.Timeline);
        Assert.Null(seg.App);
        Assert.Null(seg.AppId);
        Assert.Null(r.FirstActive);
        Assert.Null(r.LongestStretch);
    }

    // ── Settings ──

    private static TestDb TwoApps(out long ts)
    {
        var t = new TestDb();
        t.Db.UpsertApp("chrome.exe", "Google Chrome", @"C:\chrome.exe", AppCategory.Browser);
        t.Db.UpsertApp("searchhost.exe", "Microsoft Windows Operating System", null, AppCategory.System);
        ts = U(2025, 4, 2, 10);
        for (int i = 0; i < 30; i++) t.Db.WriteMinute(Minute(ts + i * 60, app: i < 20 ? 1 : 2, cpuMax: i == 5 ? 95 : 60));
        t.Db.AddAppHour(Hour(ts, 1, fg: 1200));
        t.Db.AddAppHour(Hour(ts, 2, fg: 600));
        t.Db.InsertSession(Session(1, ts, ts + 1200));
        t.Db.InsertCrashes([new CrashEvent { Ts = ts + 600, Kind = CrashKind.AppCrash, AppExe = "Chrome.EXE" },
            new CrashEvent { Ts = ts + 700, Kind = CrashKind.GpuDriverReset }]);
        return t;
    }

    [Fact]
    public void Built_in_names_replace_windows_descriptions_and_a_users_name_replaces_both()
    {
        using var t = TwoApps(out long ts);
        var plain = Build(t, ReportRange.Day, L(ts));
        Assert.Equal(["Google Chrome", "Windows Search"], plain.Apps.Select(a => a.Name));

        var settings = Settings();
        settings.AppNames["CHROME.exe"] = "Browser";
        settings.AppNames["searchhost.exe"] = "Search";
        foreach (var range in Enum.GetValues<ReportRange>())
        {
            var r = Build(t, range, L(ts), settings);
            Assert.Equal(["Browser", "Search"], r.Apps.Select(a => a.Name));
            Assert.Equal("Browser", r.CpuTempPeak!.App);
            Assert.Equal("Browser", Assert.Single(r.Sessions).Name);
            if (!ReportBuilder.IsLong(range)) Assert.Equal(["Browser", "Search"], r.Timeline.Select(s => s.App));
            if (range != ReportRange.Day) Assert.Contains(r.Days, d => d.TopApp == "Browser");
        }
    }

    [Fact]
    public void A_category_override_moves_the_time_and_makes_it_gaming()
    {
        using var t = TwoApps(out long ts);
        var settings = Settings();
        settings.AppCategories["chrome.exe"] = AppCategory.Game;
        foreach (var range in Enum.GetValues<ReportRange>())
        {
            var r = Build(t, range, L(ts), settings);
            Assert.Equal(AppCategory.Game, r.Apps.Single(a => a.Exe == "chrome.exe").Category);
            Assert.Equal(1200, r.GamingSec);
            Assert.Equal(1200, r.ActiveByCategory[AppCategory.Game]);
            Assert.False(r.ActiveByCategory.ContainsKey(AppCategory.Browser));
            Assert.True(Assert.Single(r.Sessions).IsGame);
        }
        Assert.Equal(0, Build(t, ReportRange.Day, L(ts)).GamingSec);
    }

    [Fact]
    public void Muted_apps_crashes_are_left_out_but_crashes_of_no_app_never_are()
    {
        using var t = TwoApps(out long ts);
        Assert.Equal(2, Build(t, ReportRange.Day, L(ts)).Crashes.Count);
        var settings = Settings();
        settings.MutedCrashApps.Add("chrome.exe");
        foreach (var range in Enum.GetValues<ReportRange>())
            Assert.Equal(CrashKind.GpuDriverReset, Assert.Single(Build(t, range, L(ts), settings).Crashes).Kind);
        settings.MutedCrashApps.Add("");
        Assert.Single(Build(t, ReportRange.Day, L(ts), settings).Crashes);
    }

    [Theory]
    [InlineData(96, 0)]
    [InlineData(95, 1)] // reaching the limit counts
    [InlineData(90, 1)]
    [InlineData(61, 1)]
    [InlineData(60, 30)]
    public void Minutes_over_the_alert_limit_follow_the_users_limit(double limit, int expected)
    {
        using var t = TwoApps(out long ts);
        var settings = Settings();
        settings.Alerts.CpuLimit = limit;
        settings.Alerts.GpuLimit = 1000;
        var r = Build(t, ReportRange.Day, L(ts), settings);
        Assert.Equal(expected, r.CpuOverLimitMin);
        Assert.Equal(0, r.GpuOverLimitMin);
    }

    [Fact]
    public void Excluding_an_app_stops_recording_it_but_leaves_what_was_recorded_in_reports()
    {
        using var t = TwoApps(out long ts);
        var settings = Settings();
        settings.Tracking.ExcludedApps.Add("chrome.exe");
        Assert.Contains(Build(t, ReportRange.Day, L(ts), settings).Apps, a => a.Exe == "chrome.exe");
    }

    [Fact]
    public void Settings_are_only_read_never_changed_by_a_report()
    {
        using var t = TwoApps(out long ts);
        var settings = Settings();
        settings.AppNames["chrome.exe"] = "Browser";
        string before = SettingsStore.Serialize(settings);
        foreach (var range in Enum.GetValues<ReportRange>()) Build(t, range, L(ts), settings);
        Assert.Equal(before, SettingsStore.Serialize(settings));
    }

    // ── Apps page totals (BuildAppTotals) ──

    [Fact]
    public void App_totals_for_a_range_match_the_full_report()
    {
        using var db = Seeds.Open("typical");
        var apps = db.LoadApps().ToDictionary(a => a.Id);
        var s = Settings();
        foreach (var (from, to) in new[] { (Seeds.TypicalNow.Date.AddDays(-30), Seeds.TypicalNow.Date.AddDays(1)), (new DateTime(2000, 1, 1), Seeds.TypicalNow.Date.AddDays(1)),
            (Seeds.TypicalNow.Date.AddDays(-100).AddHours(13), Seeds.TypicalNow.Date.AddDays(-50).AddHours(2)) })
        {
            var totals = ReportBuilder.BuildAppTotals(db, from, to, apps, s);
            var raw = ReportBuilder.BuildRaw(db, ReportRange.Month, from, to, apps, s, withMinutes: false);
            ReportCheck.NoBadNumbers(totals);
            Assert.True(totals.HasData);
            Assert.Equal(raw.Apps.Select(a => a.Id), totals.Apps.Select(a => a.Id));
            Assert.Equal(raw.Apps.Sum(a => a.ActiveSec), totals.ActiveSec, 3);
            Assert.Equal(raw.GamingSec, totals.GamingSec, 3);
            foreach (var (a, b) in raw.Apps.Zip(totals.Apps))
            {
                Assert.Equal(a.ActiveSec, b.ActiveSec, 3);
                Assert.Equal(a.SessionCount, b.SessionCount);
                Assert.Equal(a.LongestSessionSec, b.LongestSessionSec);
                Assert.Equal(a.MemMax, b.MemMax);
                Assert.Equal(a.MemAvg ?? -1, b.MemAvg ?? -1, 6);
            }
        }
    }

    [Fact]
    public void App_totals_of_an_empty_range_have_no_data()
    {
        using var t = new TestDb();
        var r = ReportBuilder.BuildAppTotals(t.Db, DateTime.Today, DateTime.Today.AddDays(1), new Dictionary<long, AppRow>(), Settings());
        Assert.False(r.HasData);
        Assert.Empty(r.Apps);
        Assert.Equal(0, r.ActiveSec);
    }

    [Fact]
    public void Minute_free_raw_reports_skip_the_minute_history()
    {
        using var t = TwoApps(out long ts);
        var r = ReportBuilder.BuildRaw(t.Db, ReportRange.Day, L(ts).Date, L(ts).Date.AddDays(1), t.Db.LoadApps().ToDictionary(a => a.Id), Settings(), withMinutes: false);
        Assert.Empty(r.Temps);
        Assert.Empty(r.Timeline);
        Assert.Equal(2, r.Apps.Count);
        Assert.Equal(1800, r.ActiveSec); // from the hourly rows instead
    }
}
