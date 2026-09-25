using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Reports;

/// <summary>
/// The app's side: ReportService reads the run's shared history (the default database, read-only) on background
/// threads. Its answers must be what the database and ReportBuilder say.
/// </summary>
[Collection("UI")]
public sealed class ReportServiceTests
{
    private readonly SettingsModel _settings;
    private readonly ReportService _service;

    public ReportServiceTests()
    {
        SharedData.EnsureSeeded();
        SharedData.ResetSettings();
        _settings = Ui.Run(() => new SettingsModel(new AgentClient(Ui.Dispatcher)));
        _service = new ReportService(_settings);
    }

    private static T Wait<T>(Task<T> task)
    {
        Assert.True(task.Wait(60_000), "the query didn't finish");
        return task.Result;
    }

    private static RigsightDb Db() => RigsightDb.OpenReader()!;

    public static TheoryData<ReportRange> Ranges => [.. Enum.GetValues<ReportRange>()];

    [Theory]
    [MemberData(nameof(Ranges))]
    public void A_report_is_what_the_report_builder_makes(ReportRange range)
    {
        var anchor = DateTime.Now;
        var report = Wait(_service.BuildAsync(range, anchor));
        using var db = Db();
        var expected = ReportBuilder.Build(db, range, anchor, _settings.Current);
        Assert.NotNull(report);
        Assert.True(report.HasData);
        Assert.Equal(expected.OnSec, report.OnSec);
        Assert.Equal(expected.ActiveSec, report.ActiveSec);
        Assert.Equal(expected.Apps.Select(a => a.Id), report.Apps.Select(a => a.Id));
        Assert.Equal(expected.Insights.Select(i => i.Key), report.Insights.Select(i => i.Key));
        ReportCheck.NoBadNumbers(report);
    }

    [Fact]
    public void Range_totals_are_the_apps_pages_numbers()
    {
        var (from, to) = (DateTime.Today.AddDays(-7), DateTime.Today.AddDays(1));
        var r = Wait(_service.BuildRangeAsync(from, to))!;
        using var db = Db();
        var hours = db.GetAppHours(TimeUtil.ToUnix(from), TimeUtil.ToUnix(to));
        Assert.Equal(hours.Sum(h => h.FgSec), r.ActiveSec, 3);
        Assert.Equal(hours.Select(h => h.AppId).Distinct().Order(), r.Apps.Select(a => a.Id).Order());
    }

    [Fact]
    public void An_apps_recent_sessions_are_newest_first_a_minute_or_longer_and_limited()
    {
        var month = Wait(_service.BuildRangeAsync(DateTime.Today.AddDays(-30), DateTime.Today.AddDays(1)))!;
        var app = month.Apps.First(a => a.SessionCount > 3);
        var sessions = Wait(_service.RecentSessionsAsync(app, DateTime.Today.AddDays(-30), DateTime.Today.AddDays(1), 3))!;
        Assert.Equal(3, sessions.Count);
        Assert.Equal(sessions.OrderByDescending(s => s.Start).Select(s => s.Start), sessions.Select(s => s.Start));
        Assert.All(sessions, s =>
        {
            Assert.True(s.ActiveSec >= ReportBuilder.MinSessionSec);
            Assert.Equal((app.Id, app.Name, app.Exe), (s.AppId, s.Name, s.Exe));
            Assert.Equal(app.Category == AppCategory.Game || s.IsGame, s.IsGame);
        });
        var all = Wait(_service.RecentSessionsAsync(app, DateTime.Today.AddDays(-30), DateTime.Today.AddDays(1), 1000))!;
        Assert.Equal(app.SessionCount, all.Count);
    }

    [Fact]
    public void The_first_days_and_tracked_days_come_from_the_oldest_history()
    {
        using var db = Db();
        var first = TimeUtil.FromUnix(db.FirstDataTime()!.Value).Date;
        Assert.Equal(first, Wait(_service.FirstDayAsync()));
        Assert.Equal(TimeUtil.FromUnix(db.FirstMinuteTime()!.Value).Date, Wait(_service.FirstMinuteDayAsync()));
        Assert.Equal((int)(DateTime.Today - first).TotalDays + 1, Wait(_service.TrackedDaysAsync()));
        var crashDay = Wait(_service.FirstCrashDayAsync());
        Assert.NotNull(crashDay);
        Assert.True(crashDay <= first);
    }

    public static TheoryData<ReportRange> ChartUnits => [ReportRange.Day, ReportRange.Week, ReportRange.Month, ReportRange.Year, ReportRange.All];

    [Theory]
    [MemberData(nameof(ChartUnits))]
    public void An_apps_chart_has_a_bar_per_slot_adding_up_to_its_time(ReportRange unit)
    {
        var first = Wait(_service.FirstDayAsync());
        var (from, to) = ReportBuilder.Bounds(unit, DateTime.Now);
        var totals = Wait(_service.BuildRangeAsync(unit == ReportRange.All ? new DateTime(first!.Value.Year, first.Value.Month, 1) : from, to))!;
        var app = totals.Apps[0];
        var bars = Wait(_service.AppChartAsync(app.Id, app.Category, unit, from, to, first))!;
        int expected = unit switch
        {
            ReportRange.Day => 24,
            ReportRange.Week => 7,
            ReportRange.Month => DateTime.DaysInMonth(from.Year, from.Month),
            ReportRange.Year => 12,
            _ => (to.Year - first!.Value.Year) * 12 + to.Month - first.Value.Month + (to.Day > 1 ? 1 : 0),
        };
        Assert.Equal(expected, bars.Count);
        Assert.Equal(app.ActiveSec, bars.Sum(b => b.ActiveSec), 3);
        Assert.Equal(bars.OrderBy(b => b.Day).Select(b => b.Day), bars.Select(b => b.Day));
        Assert.All(bars, b => Assert.Equal(b.ActiveSec, b.ActiveByCategory.Values.Sum(), 3));
    }

    [Fact]
    public void Drive_history_covers_the_days_asked_for()
    {
        var days = Wait(_service.DriveHistoryAsync(7))!;
        Assert.Equal(SeedData.Drives.Length * 8, days.Count); // today and the 7 days before
        Assert.All(days, d => Assert.True(d.Day >= TimeUtil.ToUnix(DateTime.Today.AddDays(-7))));
    }

    [Fact]
    public void Minutes_and_known_apps_are_read_as_stored()
    {
        long from = TimeUtil.ToUnix(DateTime.Today.AddDays(-1)), to = TimeUtil.ToUnix(DateTime.Today);
        using var db = Db();
        Assert.Equal(db.GetMinutes(from, to).Select(m => m.Ts), Wait(_service.MinutesAsync(from, to))!.Select(m => m.Ts));
        Assert.Equal(db.LoadApps().Count, Wait(_service.KnownAppsAsync())!.Count);
    }

    [Fact]
    public void Crashes_are_explained_with_their_context_and_muted_apps_left_out_unless_asked_for()
    {
        var (from, to) = (DateTime.Today.AddDays(-30), DateTime.Today.AddDays(1));
        using var db = Db();
        var stored = db.GetCrashes(TimeUtil.ToUnix(from), TimeUtil.ToUnix(to));
        var rows = Wait(_service.CrashesAsync(from, to))!;
        Assert.Equal(stored.Select(c => c.Id), rows.Select(r => r.Event.Id));
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.Explanation.Title)));
        Assert.All(rows.Where(r => !string.IsNullOrEmpty(r.Event.AppExe)), r => Assert.False(string.IsNullOrEmpty(r.AppName)));
        var context = db.GetCrashContext(TimeUtil.ToUnix(from), TimeUtil.ToUnix(to));
        foreach (var r in rows)
        {
            var ctx = context[r.Event.Id];
            Assert.Equal((ctx.CpuBefore, ctx.GpuBefore, ctx.FrontApp is null), (r.CpuBefore, r.GpuBefore, r.FrontApp is null));
        }

        var muted = stored.First(c => c.AppExe.Length > 0).AppExe;
        _settings.Current.MutedCrashApps.Add(muted.ToUpperInvariant());
        try
        {
            var shown = Wait(_service.CrashesAsync(from, to))!;
            Assert.DoesNotContain(shown, r => r.Event.AppExe.Equals(muted, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(stored.Count(c => !c.AppExe.Equals(muted, StringComparison.OrdinalIgnoreCase)), shown.Count);
            Assert.Equal(stored.Count, Wait(_service.CrashesAsync(from, to, includeMuted: true))!.Count);
        }
        finally
        {
            _settings.Current.MutedCrashApps.Clear();
        }
    }

    [Fact]
    public void A_renamed_app_shows_its_new_name_in_crashes_and_reports()
    {
        using var db = Db();
        var (from, to) = (DateTime.Today.AddDays(-30), DateTime.Today.AddDays(1));
        var used = Wait(_service.BuildRangeAsync(from, to))!.Apps[0];
        var crash = db.GetCrashes(TimeUtil.ToUnix(from), TimeUtil.ToUnix(to)).First(c => c.AppExe.Length > 0 && c.AppExe != used.Exe);
        _settings.Current.AppNames[crash.AppExe] = "Renamed App";
        _settings.Current.AppNames[used.Exe] = "Renamed Top App";
        try
        {
            Assert.All(Wait(_service.CrashesAsync(from, to))!.Where(r => r.Event.AppExe == crash.AppExe), r => Assert.Equal("Renamed App", r.AppName));
            Assert.Equal("Renamed Top App", Wait(_service.BuildRangeAsync(from, to))!.Apps.Single(a => a.Id == used.Id).Name);
            Assert.Equal("Renamed Top App", Wait(_service.BuildAsync(ReportRange.Month, DateTime.Now))!.Apps.Single(a => a.Id == used.Id).Name);
        }
        finally
        {
            _settings.Current.AppNames.Clear();
        }
    }
}
