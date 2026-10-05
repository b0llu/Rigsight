using Rigsight.Controls;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>Home, Reports, Apps and Memory on the run's two weeks of generated history (read only).</summary>
[Collection("UI")]
public sealed class HistoryPageTests
{
    private static readonly string[] KnownExes = [.. SeedData.KnownApps.Select(a => a.Exe)];

    private static (SettingsModel Settings, ReportService Reports, LiveData Live) Setup(RigsightSettings? start = null)
    {
        SharedData.EnsureSeeded();
        var (settings, live) = Kit.Greeted(start);
        return (settings, new ReportService(settings), live);
    }

    // ── Home ─────────────────────────────────────────────────────────────

    [Fact]
    public void Home_shows_today_and_yesterday()
    {
        var (_, reports, live) = Setup();
        var home = Ui.Run(() => new HomeViewModel(reports, live));
        Ui.Run(() =>
        {
            Assert.False(home.ShowLearning); // not before history has loaded
            Assert.Empty(home.TodayTopApps);
            Assert.Equal(1, home.TodayTopMax);
            Assert.False(home.HasTodayData);
        });
        Kit.Wait(() => home.RefreshAsync());
        Ui.Run(() =>
        {
            Assert.True(home.Loaded);
            Assert.True(home.HasTodayData);
            Assert.True(home.RecapHasData);
            Assert.Equal(RecapPeriod.Yesterday, home.Period);
            Assert.Equal("Yesterday", home.RecapTitle);
            Assert.InRange(home.TodayTopApps.Count, 1, 6);
            Assert.All(home.TodayTopApps, a => Assert.True(a.ActiveSec >= 30 && a.Category != AppCategory.System));
            Assert.Equal(home.TodayTopApps.Max(a => a.ActiveSec), home.TodayTopMax);
            Assert.InRange(home.RecapTopApps.Count, 1, 3);
            Assert.All(home.RecapTopApps, a => Assert.True(a.ActiveSec >= 60 && a.Category != AppCategory.System));
            Assert.True(home.TodayInsights.Count <= 5);
            Assert.True(home.RecapInsights.Count <= 3);
            Assert.DoesNotContain(home.TodayInsights.Concat(home.RecapInsights), i => i.Key is "screen" or "top-app");
            Assert.False(home.ShowLearning);
            Assert.Contains(home.Greeting, new[] { "Up late", "Good morning", "Good afternoon", "Good evening" });
            Assert.Equal(DateTime.Now.ToString("dddd, d MMMM"), home.DateText);
        });
    }

    [Fact]
    public void Home_reads_yesterday_once_a_day_and_today_every_time()
    {
        var (_, reports, live) = Setup();
        var home = Ui.Run(() => new HomeViewModel(reports, live));
        Kit.Wait(() => home.RefreshAsync());
        var (today, yesterday) = Ui.Run(() => (home.Today, home.Yesterday));
        Kit.Wait(() => home.RefreshAsync());
        Ui.Run(() =>
        {
            Assert.NotSame(today, home.Today);
            Assert.Same(yesterday, home.Yesterday);
        });
    }

    [Fact]
    public void Home_announces_everything_that_depends_on_today()
    {
        var (_, reports, live) = Setup();
        var home = Ui.Run(() => new HomeViewModel(reports, live));
        var changed = new List<string>();
        Ui.Run(() => home.PropertyChanged += (_, e) => changed.Add(e.PropertyName!));
        Kit.Wait(() => home.RefreshAsync());
        foreach (var p in new[] { nameof(HomeViewModel.Today), nameof(HomeViewModel.TodayTopApps), nameof(HomeViewModel.TodayInsights),
                     nameof(HomeViewModel.HasTodayData), nameof(HomeViewModel.ShowLearning), nameof(HomeViewModel.Yesterday),
                     nameof(HomeViewModel.Recap), nameof(HomeViewModel.RecapTopApps), nameof(HomeViewModel.Periods), nameof(HomeViewModel.Greeting), nameof(HomeViewModel.Loaded) })
            Assert.Contains(p, changed);
    }

    [Theory]
    [InlineData("2026-09-28", null, "Yesterday")]
    [InlineData("2026-09-28", "2026-09-27", "Yesterday, LastWeek")]                 // a day into last week is enough
    [InlineData("2026-09-28", "2026-09-21", "Yesterday, LastWeek")]                 // last week's Monday
    [InlineData("2026-09-28", "2026-08-31", "Yesterday, LastWeek, LastMonth")]      // a day of August
    [InlineData("2026-09-28", "2025-12-31", "Yesterday, LastWeek, LastMonth, LastYear")]
    [InlineData("2026-09-01", "2026-08-30", "Yesterday, LastWeek, LastMonth")]      // the 1st: last month is whole
    [InlineData("2026-01-01", "2025-06-01", "Yesterday, LastWeek, LastMonth, LastYear")]
    public void The_periods_offered_follow_when_history_starts(string todayText, string? firstText, string expected)
    {
        var today = DateTime.Parse(todayText);
        DateTime? first = firstText is null ? null : DateTime.Parse(firstText);
        Assert.Equal(expected, string.Join(", ", HomeViewModel.PeriodsFor(first, today)));
    }

    [Fact]
    public void A_PC_not_used_yesterday_shows_the_last_day_it_was_used_and_says_why()
    {
        var (_, reports, live) = Setup();
        var home = Ui.Run(() => new HomeViewModel(reports, live));
        var today = DateTime.Today;
        Ui.Run(() =>
        {
            // Used yesterday (or nothing known): yesterday, with nothing to explain.
            Assert.Equal(("Yesterday", "yesterday", null), (home.RecapTitle, home.RecapArg, home.RecapNote));
            home.LastUsedDay = today.AddDays(-1);
            Assert.Equal(("Yesterday", "yesterday", null), (home.RecapTitle, home.RecapArg, home.RecapNote));

            // Off for a day: the day before yesterday, by its name, and its own full recap.
            home.LastUsedDay = today.AddDays(-2);
            Assert.Equal((today.AddDays(-2).ToString("dddd"), today.AddDays(-2).ToString("yyyy-MM-dd"), "Your PC wasn't used yesterday."),
                (home.RecapTitle, home.RecapArg, home.RecapNote));

            // Off for two days, and for longer than a week (then the date, not a weekday that could be any week's).
            home.LastUsedDay = today.AddDays(-3);
            Assert.Equal((today.AddDays(-3).ToString("dddd"), "Your PC wasn't used for 2 days."), (home.RecapTitle, home.RecapNote));
            home.LastUsedDay = today.AddDays(-10);
            Assert.Equal((today.AddDays(-10).ToString("d MMMM"), "Your PC wasn't used for 9 days."), (home.RecapTitle, home.RecapNote));

            // The week, month and year recaps are whole periods either way.
            home.Periods = [RecapPeriod.Yesterday, RecapPeriod.LastWeek];
            home.Period = RecapPeriod.LastWeek;
            Assert.Equal(("Last week", "last-week", null), (home.RecapTitle, home.RecapArg, home.RecapNote));
        });
    }

    [Fact]
    public void The_last_day_used_is_found_in_the_history()
    {
        var (_, reports, _) = Setup();
        DateTime? last = null, none = null;
        Kit.Wait(async () =>
        {
            last = await reports.LastUsedDayAsync(DateTime.Today);
            none = await reports.LastUsedDayAsync(new DateTime(2000, 1, 1));
        });
        Assert.NotNull(last);
        Assert.True(last < DateTime.Today);
        Assert.Null(none);
    }

    [Fact]
    public void Each_period_is_the_whole_one_before_today()
    {
        var today = new DateTime(2026, 9, 28);
        Assert.Equal((ReportRange.Day, new DateTime(2026, 9, 27)), HomeViewModel.Bounds(RecapPeriod.Yesterday, today));
        var (range, anchor) = HomeViewModel.Bounds(RecapPeriod.LastWeek, today);
        Assert.Equal((new DateTime(2026, 9, 21), new DateTime(2026, 9, 28)), ReportBuilder.Bounds(range, anchor));
        (range, anchor) = HomeViewModel.Bounds(RecapPeriod.LastMonth, today);
        Assert.Equal((new DateTime(2026, 8, 1), new DateTime(2026, 9, 1)), ReportBuilder.Bounds(range, anchor));
        (range, anchor) = HomeViewModel.Bounds(RecapPeriod.LastYear, today);
        Assert.Equal((new DateTime(2025, 1, 1), new DateTime(2026, 1, 1)), ReportBuilder.Bounds(range, anchor));
    }

    [Fact]
    public void Picking_a_period_loads_its_report_once_and_names_it()
    {
        var (_, reports, live) = Setup();
        var home = Ui.Run(() => new HomeViewModel(reports, live));
        Kit.Wait(() => home.RefreshAsync());
        Ui.Run(() => Assert.True(home.HasLastWeek, "the seeded history reaches into last week"));
        Ui.Run(() => home.Period = RecapPeriod.LastWeek);
        Assert.True(Ui.WaitFor(() => home.Recap is { Range: ReportRange.Week }, 15_000));
        Ui.Run(() =>
        {
            Assert.Equal(("Last week", "last-week", true), (home.RecapTitle, home.RecapArg, home.IsLastWeek));
            Assert.Equal(ReportBuilder.Bounds(ReportRange.Week, DateTime.Today.AddDays(-7)).From, home.Recap!.From);
            Assert.True(home.RecapHasData);
        });
        var week = Ui.Run(() => home.Recap);
        Ui.Run(() => home.Period = RecapPeriod.Yesterday);
        Assert.True(Ui.WaitFor(() => home.Recap is { Range: ReportRange.Day }, 15_000));
        Ui.Run(() => home.Period = RecapPeriod.LastWeek);
        Assert.True(Ui.WaitFor(() => home.Recap is { Range: ReportRange.Week }, 15_000));
        Ui.Run(() => Assert.Same(week, home.Recap)); // kept, not built again
    }

    [Fact]
    public void Home_yesterday_is_the_calendar_day()
    {
        var (_, reports, live) = Setup();
        var home = Ui.Run(() => new HomeViewModel(reports, live));
        Kit.Wait(() => home.RefreshAsync());
        Ui.Run(() =>
        {
            Assert.Equal(ReportRange.Day, home.Yesterday!.Range);
            Assert.Equal((DateTime.Today.AddDays(-1), DateTime.Today), (home.Yesterday.From, home.Yesterday.To));
        });
    }

    // ── Reports ──────────────────────────────────────────────────────────

    private static ReportsViewModel Reports(out ReportService service)
    {
        (_, service, _) = Setup();
        var s = service;
        var vm = Ui.Run(() => new ReportsViewModel(s));
        Kit.Wait(() => vm.LoadAsync());
        return vm;
    }

    /// <summary>Picks a custom range, as the range editor does.</summary>
    private static void Custom(ReportsViewModel vm, DateTime from, DateTime to) => Ui.Run(() =>
    {
        vm.CustomFrom = from;
        vm.CustomTo = to;
        vm.Range = ReportRange.Custom;
    });

    private static void Settle(ReportsViewModel vm, Func<bool> done) =>
        Assert.True(Ui.WaitFor(() => !vm.IsLoading && done(), 15_000), "the report didn't load");

    [Fact]
    public void Reports_open_on_today()
    {
        var vm = Reports(out _);
        Ui.Run(() =>
        {
            Assert.True(vm.IsDay);
            Assert.False(vm.IsMultiDay);
            Assert.False(vm.IsLoading);
            Assert.NotNull(vm.Report);
            Assert.True(vm.HasData);
            Assert.Equal(DateTime.Today, vm.Report!.From);
            Assert.Equal("Today", vm.Title);
            Assert.Equal(DateTime.Today.ToString("dddd, d MMMM yyyy"), vm.Subtitle);
            Assert.True(vm.IncludesNow);
            Assert.Equal("What stands out so far", vm.InsightsTitle);
            Assert.Null(vm.CoverageNote); // never for a day
            Assert.Equal("Active time per day", vm.BarsTitle);
            Assert.NotNull(vm.FirstDay);
            Assert.Equal((int)(DateTime.Today - vm.FirstDay!.Value).TotalDays + 1, vm.TrackedDays);
            Assert.InRange(vm.TrackedDays, 2, 15);
        });
    }

    [Fact]
    public void Reports_of_a_past_day_say_what_stood_out()
    {
        var vm = Reports(out _);
        Ui.Run(() => vm.ShowDay(DateTime.Today.AddDays(-1).AddHours(15)));
        Settle(vm, () => vm.Report?.From == DateTime.Today.AddDays(-1));
        Ui.Run(() =>
        {
            Assert.Equal(DateTime.Today.AddDays(-1), vm.Anchor);
            Assert.Equal("Yesterday", vm.Title);
            Assert.False(vm.IncludesNow);
            Assert.Equal("What stood out", vm.InsightsTitle);
            Assert.True(vm.HasData);
        });
    }

    [Fact]
    public void Reports_before_history_have_no_data()
    {
        var vm = Reports(out _);
        Ui.Run(() => vm.ShowDay(DateTime.Today.AddYears(-3)));
        Settle(vm, () => vm.Report?.From == DateTime.Today.AddYears(-3));
        Ui.Run(() =>
        {
            Assert.False(vm.HasData);
            Assert.Empty(vm.Apps);
            Assert.Empty(vm.Peaks);
            Assert.Empty(vm.TopSessions);
            Assert.Empty(vm.CrashesShown);
            Assert.Equal(1, vm.MaxActive);
        });
    }

    [Theory]
    [InlineData(ReportRange.Week, "Active time per day", false)]
    [InlineData(ReportRange.Month, "Active time per day", false)]
    [InlineData(ReportRange.Year, "Active time per month", true)]
    public void Reports_of_longer_periods(ReportRange range, string barsTitle, bool isYear)
    {
        var vm = Reports(out _);
        Ui.Run(() => vm.Range = range);
        var from = ReportBuilder.Bounds(range, DateTime.Today).From;
        Settle(vm, () => vm.Report?.From == from);
        Ui.Run(() =>
        {
            Assert.True(vm.IsMultiDay);
            Assert.Equal(isYear, vm.IsYear);
            Assert.Equal(barsTitle, vm.BarsTitle);
            Assert.True(vm.IncludesNow);
            Assert.True(vm.HasData);
            Assert.Equal(ReportsViewModel.PeriodText(range, DateTime.Today), range switch
            {
                ReportRange.Week => "This week",
                ReportRange.Month => "This month",
                _ => "This year",
            });
        });
    }

    [Fact]
    public void Reports_of_a_custom_range_past_midnight()
    {
        var vm = Reports(out _);
        var (start, end) = (DateTime.Today.AddDays(-2).AddHours(8), DateTime.Today.AddDays(-1).AddHours(2));
        Custom(vm, start, end);
        Settle(vm, () => vm.Report is { Range: ReportRange.Custom } r && r.From == start);
        Ui.Run(() =>
        {
            Assert.Equal(ReportRange.Custom, vm.Range);
            Assert.Equal((start, end), (vm.CustomFrom, vm.CustomTo));
            Assert.Equal(end, vm.Report!.To);
            Assert.Equal(end, vm.TimelineEnd);
            Assert.True(vm.IsDay);
            Assert.False(vm.IsMultiDay);
            Assert.False(vm.IsYear);
            Assert.Equal("18 hours", vm.Subtitle);
            Assert.Equal(Report.CustomTitle(start, end), vm.Title);
            Assert.False(vm.IncludesNow);
            Assert.Null(vm.CoverageNote);
            Assert.All(vm.Report.Timeline, s => Assert.True(s.Start >= start && s.End <= end));
            Assert.DoesNotContain(vm.Report.Insights, i => i.Text.Contains("The night before ran late"));
        });

        // Back to a day: the whole day again.
        Ui.Run(() => vm.ShowDay(DateTime.Today.AddDays(-1)));
        Settle(vm, () => vm.Report is { Range: ReportRange.Day });
        Ui.Run(() =>
        {
            Assert.Null(vm.TimelineEnd);
            Assert.Equal("Yesterday", vm.Title);
        });
    }

    [Theory]
    [InlineData(5, false, "Active time per day")]
    [InlineData(120, true, "Active time per month")]
    public void Reports_of_long_custom_ranges(int days, bool isYear, string barsTitle)
    {
        var vm = Reports(out _);
        var (from, to) = (DateTime.Today.AddDays(-days), DateTime.Today);
        Custom(vm, from, to);
        Settle(vm, () => vm.Report is { Range: ReportRange.Custom } r && r.From == from);
        Ui.Run(() =>
        {
            Assert.False(vm.IsDay);
            Assert.True(vm.IsMultiDay);
            Assert.Equal(isYear, vm.IsYear);
            Assert.Equal(barsTitle, vm.BarsTitle);
            Assert.Equal($"{days} days", vm.Subtitle);
            Assert.True(vm.HasData);
        });
    }

    [Fact]
    public void Changing_both_ends_of_a_range_loads_once_with_both()
    {
        var vm = Reports(out _);
        var day = DateTime.Today.AddDays(-3);
        Ui.Run(() =>
        {
            vm.Range = ReportRange.Custom;
            vm.CustomFrom = day.AddHours(9);
            vm.CustomTo = day.AddHours(21);
        });
        Settle(vm, () => vm.Report is { Range: ReportRange.Custom } r && r.From == day.AddHours(9));
        Ui.Run(() => Assert.Equal(day.AddHours(21), vm.Report!.To));
    }

    [Fact]
    public void Reports_explain_a_year_with_only_two_weeks_of_history()
    {
        var vm = Reports(out _);
        Ui.Run(() => vm.Range = ReportRange.Year);
        Settle(vm, () => vm.Report?.Range == ReportRange.Year);
        Ui.Run(() =>
        {
            var note = vm.CoverageNote;
            // (Early in January the year is no longer than the history: nothing to explain.)
            if (DateTime.Today.DayOfYear <= vm.TrackedDays) Assert.Null(note);
            else Assert.Equal($"History starts on {vm.FirstDay:d MMMM}, so this year has {vm.TrackedDays} days of data.", note);
        });
    }

    [Fact]
    public void Report_apps_are_all_listed_and_crashes_start_short_and_grow_on_request()
    {
        var vm = Reports(out _);
        Ui.Run(() => vm.Range = ReportRange.Year);
        Settle(vm, () => vm.Report?.Range == ReportRange.Year);
        Ui.Run(() =>
        {
            // Every used app: the list scrolls inside its own box instead of growing the page.
            var apps = vm.Report!.Apps.Where(a => a.ActiveSec >= 30).ToList();
            Assert.Equal(apps.Select(a => a.Exe), vm.Apps.Select(a => a.Exe));

            Assert.Equal(Math.Min(5, vm.Crashes.Count), vm.CrashesShown.Count);
            Assert.Equal(vm.Crashes.Count > 5, vm.HasMoreCrashes);
            if (vm.HasMoreCrashes)
            {
                int left = vm.Crashes.Count - 5;
                Assert.Equal($"Show {Math.Min(20, left)} more ({left} not shown)", vm.MoreCrashesText);
            }
            vm.ShowMoreCrashesCommand.Execute(null);
            Assert.Equal(Math.Min(25, vm.Crashes.Count), vm.CrashesShown.Count);
        });
    }

    [Fact]
    public void Report_unused_apps_toggle_adds_background_only_apps()
    {
        var vm = Reports(out _);
        Ui.Run(() => vm.Range = ReportRange.Year);
        Settle(vm, () => vm.Report?.Range == ReportRange.Year);
        Ui.Run(() =>
        {
            int used = vm.Apps.Count;
            int background = vm.BackgroundOnlyCount;
            Assert.Equal($"Show unused apps ({background})", vm.BackgroundToggleText);
            vm.ShowBackgroundApps = true;
            Assert.Equal(used + background, vm.Apps.Count);
        });
    }

    [Fact]
    public void Report_peaks_sessions_and_scale()
    {
        var vm = Reports(out _);
        Ui.Run(() => vm.Range = ReportRange.Week);
        Settle(vm, () => vm.Report?.Range == ReportRange.Week);
        Ui.Run(() =>
        {
            var labels = vm.Peaks.Select(p => p.Label).ToList();
            Assert.Contains("CPU temperature", labels);
            Assert.Contains("GPU temperature", labels);
            Assert.All(vm.Peaks, p => { Assert.NotNull(p.Brush); Assert.NotEmpty(p.Value); Assert.NotEmpty(p.When); });
            Assert.EndsWith("°", vm.Peaks.First(p => p.Label == "CPU temperature").Value);
            Assert.True(vm.TopSessions.Count <= 10);
            Assert.Equal(vm.TopSessions.OrderByDescending(s => s.ActiveSec).Select(s => s.ActiveSec), vm.TopSessions.Select(s => s.ActiveSec));
            Assert.DoesNotContain(vm.TopSessions, s => s.Category == AppCategory.System);
            Assert.Equal(Math.Max(1, vm.Report!.Apps[0].ActiveSec), vm.MaxActive);
        });
    }

    [Fact]
    public void Only_the_latest_of_quick_period_changes_is_shown()
    {
        var vm = Reports(out _);
        Ui.Run(() =>
        {
            vm.Range = ReportRange.Month;
            vm.Range = ReportRange.Week;
            vm.ShowDay(DateTime.Today.AddDays(-2));
        });
        Settle(vm, () => vm.Report?.From == DateTime.Today.AddDays(-2));
        Ui.Pump(300);
        Ui.Run(() =>
        {
            Assert.Equal(ReportRange.Day, vm.Report!.Range);
            Assert.Equal(DateTime.Today.AddDays(-2), vm.Report.From);
        });
    }

    [Fact]
    public void A_new_period_starts_with_short_lists_again_and_a_refresh_keeps_them_open()
    {
        var vm = Reports(out _);
        Ui.Run(() => vm.Range = ReportRange.Year);
        Settle(vm, () => vm.Report?.Range == ReportRange.Year);
        Ui.Run(() => vm.ShowMoreCrashesCommand.Execute(null));
        int open = Ui.Run(() => vm.CrashesShown.Count);
        Kit.Wait(() => vm.LoadAsync());
        Assert.Equal(open, Ui.Run(() => vm.CrashesShown.Count));
        Ui.Run(() => vm.Range = ReportRange.Month);
        Settle(vm, () => vm.Report?.Range == ReportRange.Month);
        Ui.Run(() => Assert.Equal(Math.Min(5, vm.Crashes.Count), vm.CrashesShown.Count));
    }

    // ── Apps ─────────────────────────────────────────────────────────────

    private static (AppsViewModel Vm, SettingsModel Settings) Apps(RigsightSettings? start = null)
    {
        var (settings, reports, _) = Setup(start);
        var vm = Ui.Run(() => new AppsViewModel(reports, settings));
        Kit.Wait(() => vm.LoadAsync());
        return (vm, settings);
    }

    private static void Loaded(AppsViewModel vm, Func<bool> done) => Assert.True(Ui.WaitFor(done, 15_000), "the apps didn't load");

    /// <summary>
    /// The whole history, loaded: every known app is in it. (This week can be only a few hours on a Monday morning,
    /// without the app a test needs; and "… in total" alone doesn't tell the week's list from the whole history's.)
    /// </summary>
    private static void AllTime(AppsViewModel vm)
    {
        Ui.Run(() => vm.Unit = ReportRange.All);
        Loaded(vm, () => KnownExes.Take(7).All(exe => vm.Apps.Any(a => a.Stat.Exe == exe)));
    }

    [Fact]
    public void Apps_open_on_today_like_Home()
    {
        // A week by default put last week's highs beside Home's today's ones, and they looked like they disagreed.
        var (vm, _) = Apps();
        Ui.Run(() =>
        {
            Assert.Equal(ReportRange.Day, vm.Unit);
            Assert.Equal(DateTime.Today, vm.Anchor);
            Assert.True(vm.IncludesToday);
            Assert.True(vm.IsDay);
        });
    }

    [Fact]
    public void Apps_filter_by_category_with_a_choice_for_each_one_listed()
    {
        var (vm, settings) = Apps();
        AllTime(vm);
        Ui.Run(() =>
        {
            AppCategory Of(AppStat a) => settings.Current.AppCategories.GetValueOrDefault(a.Exe, a.Category);
            var all = vm.Apps.Select(r => r.Stat).ToList();
            var present = all.Select(Of).Distinct().ToHashSet();

            // "All categories", then one choice per category in the list (Other last), nothing for a category with no app.
            Assert.Null(vm.Filters[0].Category);
            Assert.Same(vm.Filters[0], vm.Filter);
            var shown = vm.Filters.Skip(1).Select(f => f.Category!.Value).ToList();
            Assert.Equal(present.OrderBy(c => c == AppCategory.Other).ThenBy(c => (int)c), shown);
            Assert.True(shown.Count >= 2, "the seeded apps should span several categories");

            var games = vm.Filters.First(f => f.Category == shown[0]);
            vm.Filter = games;
            Assert.NotEmpty(vm.Apps);
            Assert.All(vm.Apps, r => Assert.Equal(shown[0], Of(r.Stat)));
            Assert.Equal(all.Count(a => Of(a) == shown[0]), vm.Apps.Count);
            Assert.Equal(100, vm.Apps[0].Bar, 6); // bars measured within the category

            // Search narrows within the category.
            vm.Search = vm.Apps[0].Stat.Name;
            Assert.All(vm.Apps, r => Assert.Equal(shown[0], Of(r.Stat)));
            vm.Search = "";

            // Moving the only apps out of the category shown: it leaves the list and the apps fall back to all categories.
            var names = vm.Apps.Select(r => r.Stat.Exe).ToList();
            var target = shown[0] == AppCategory.Productivity ? AppCategory.Media : AppCategory.Productivity;
            foreach (var exe in names)
            {
                vm.SelectExe(exe);
                vm.SelectedCategory = target;
            }
            Assert.DoesNotContain(vm.Filters, f => f.Category == shown[0]);
            Assert.Null(vm.Filter?.Category);
            Assert.Equal(all.Count, vm.Apps.Count);

            vm.Filter = null; // the list clearing its selection: back to All
            Assert.Same(vm.Filters[0], vm.Filter);
        });
    }

    [Fact]
    public void Apps_of_this_week_are_sorted_by_active_time_with_the_first_selected()
    {
        var (vm, _) = Apps();
        Ui.Run(() => vm.Unit = ReportRange.Week);
        Loaded(vm, () => vm.Summary.EndsWith(" this week") && vm.Apps.Count > 0);
        Ui.Run(() =>
        {
            Assert.Equal(ReportRange.Week, vm.Unit);
            Assert.True(vm.IncludesToday);
            Assert.False(vm.IsDay);
            Assert.NotEmpty(vm.Apps);
            var times = vm.Apps.Select(a => a.Stat.ActiveSec).ToList();
            Assert.Equal(times.OrderDescending(), times);
            Assert.Equal(100, vm.Apps[0].Bar, 6);
            Assert.All(vm.Apps, a => Assert.InRange(a.Bar, 0, 100));
            Assert.All(vm.Apps, a => Assert.True(a.Stat.OpenSec >= 30));
            Assert.Equal(vm.Apps[0].Stat.ActiveSec, vm.MaxValue, 6);
            Assert.Same(vm.Apps[0], vm.SelectedRow);
            Assert.Same(vm.Apps[0].Stat, vm.Selected);
            Assert.True(vm.HasSelection);
            Assert.Equal("Active time per day", vm.ChartTitle);
            Assert.EndsWith(" this week", vm.Summary);
            Assert.NotNull(vm.FirstDay);
        });
        Loaded(vm, () => vm.SelectedChart is { Count: 7 });
        Loaded(vm, () => vm.SelectedSessions.Count > 0);
        Ui.Run(() =>
        {
            Assert.True(vm.SelectedSessions.Count <= 12);
            Assert.All(vm.SelectedSessions, s => Assert.Equal(vm.Selected!.Exe, s.Exe));
            Assert.Equal(vm.SelectedSessions.OrderByDescending(s => s.Start).Select(s => s.Start), vm.SelectedSessions.Select(s => s.Start));
        });
    }

    [Theory]
    [InlineData("Memory")]
    [InlineData("Cpu")]
    [InlineData("CpuTemp")]
    [InlineData("GpuTemp")]
    [InlineData("Background")]
    [InlineData("Active")]
    public void Apps_sort_by_each_measure(string sort)
    {
        var (vm, _) = Apps();
        Ui.Run(() =>
        {
            vm.Sort = sort;
            double Key(AppStat a) => sort switch
            {
                "GpuTemp" => a.GpuTempAvg ?? -1,
                "CpuTemp" => a.CpuTempAvg ?? -1,
                "Memory" => a.MemMax ?? -1,
                "Background" => a.BackgroundSec + a.MinimizedSec,
                "Cpu" => a.CpuAvg ?? -1,
                _ => a.ActiveSec,
            };
            Assert.NotEmpty(vm.Apps);
            var keys = vm.Apps.Select(a => Key(a.Stat)).ToList();
            Assert.Equal(keys.OrderDescending(), keys);
            Assert.All(vm.Apps, a => Assert.InRange(a.Bar, 0, 100.0000001));
            Assert.All(vm.Apps, a => Assert.False(string.IsNullOrEmpty(a.Metric)));
            if (sort is "CpuTemp" or "GpuTemp") Assert.All(vm.Apps, a => Assert.True(a.Stat.ActiveSec >= 60));
            if (sort == "Memory") Assert.EndsWith("B", vm.Apps[0].Metric);
            if (sort == "Cpu") Assert.EndsWith("%", vm.Apps[0].Metric);
            if (sort is "CpuTemp" or "GpuTemp") Assert.EndsWith("°", vm.Apps[0].Metric);
            // The selection stays on the same app.
            Assert.NotNull(vm.Selected);
            Assert.Same(vm.Selected, vm.SelectedRow?.Stat);
        });
    }

    [Fact]
    public void Apps_search_matches_name_or_exe()
    {
        var (vm, _) = Apps();
        AllTime(vm);
        Ui.Run(() =>
        {
            vm.Search = "CHROME";
            Assert.Equal("chrome.exe", Assert.Single(vm.Apps).Stat.Exe);
            vm.Search = " visual studio ";
            Assert.Equal("code.exe", Assert.Single(vm.Apps).Stat.Exe);
            vm.Search = "no such app";
            Assert.Empty(vm.Apps);
            vm.Search = "";
            Assert.True(vm.Apps.Count > 1);
        });
    }

    [Fact]
    public void Apps_show_today_for_an_app_from_a_notification()
    {
        var (vm, _) = Apps();
        Ui.Run(() =>
        {
            vm.Unit = ReportRange.Month;
            vm.ShowToday("DISCORD.EXE");
        });
        Loaded(vm, () => vm.Selected?.Exe == "discord.exe" && vm.Unit == ReportRange.Day);
        Ui.Run(() =>
        {
            Assert.Equal(DateTime.Today, vm.Anchor);
            Assert.True(vm.IsDay);
            Assert.Equal("Active time per hour", vm.ChartTitle);
            Assert.EndsWith(" today", vm.Summary);
            Assert.StartsWith("Chat & calls · ", vm.Summary);
        });
        Loaded(vm, () => vm.SelectedChart is { Count: 24 });
        Ui.Pump(300);
        Assert.Equal(24, Ui.Run(() => vm.SelectedChart!.Count));
    }

    [Fact]
    public void Apps_selection_of_an_app_not_in_the_list_waits_for_it()
    {
        var (vm, _) = Apps();
        var first = Ui.Run(() => vm.Selected);
        Ui.Run(() => vm.SelectExe("not-there.exe"));
        Ui.Run(() => Assert.Same(first, vm.Selected));
        Ui.Run(() => vm.SelectExe("steam.exe"));
        Ui.Run(() => Assert.Equal("steam.exe", vm.Selected?.Exe));
    }

    [Fact]
    public void Apps_a_period_without_the_selected_app_doesnt_keep_the_old_periods_numbers()
    {
        var (vm, _) = Apps();
        Assert.NotNull(Ui.Run(() => vm.Selected));
        // A week long before any history: no apps, so nothing selected (not the old week's app and figures).
        Ui.Run(() => vm.Anchor = DateTime.Today.AddYears(-1));
        Kit.Wait(() => vm.LoadAsync());
        Ui.Run(() =>
        {
            Assert.Empty(vm.Apps);
            Assert.Null(vm.Selected);
            Assert.False(vm.HasSelection);
        });
    }

    [Fact]
    public void Apps_an_app_picked_by_hand_isnt_replaced_by_one_asked_for_earlier()
    {
        var (vm, _) = Apps();
        Ui.Run(() => vm.SelectExe("not-there.exe")); // e.g. from a notification, for an app with no time in this week
        var picked = Ui.Run(() => vm.Apps[1]);
        Ui.Run(() => vm.SelectedRow = picked);
        Kit.Wait(() => vm.LoadAsync()); // the minute refresh
        Ui.Run(() => Assert.Equal(picked.Stat.Exe, vm.Selected?.Exe));
    }

    [Theory]
    [InlineData(ReportRange.Day, "Active time per hour", 24)]
    [InlineData(ReportRange.Week, "Active time per day", 7)]
    [InlineData(ReportRange.Year, "Active time per month", 12)]
    public void Apps_chart_per_period(ReportRange unit, string title, int bars)
    {
        var (vm, _) = Apps();
        Ui.Run(() => vm.Unit = unit);
        Loaded(vm, () => vm.SelectedChart?.Count == bars);
        Ui.Run(() =>
        {
            Assert.Equal(title, vm.ChartTitle);
            Assert.True(vm.SelectedChart!.Sum(b => b.ActiveSec) > 0);
        });
    }

    [Theory]
    [InlineData(19, "Active time per hour", 19)]
    [InlineData(24 * 5, "Active time per day", 6)] // 8 AM on day one to 8 AM on day six
    public void Apps_chart_over_a_custom_range(int hours, string title, int bars)
    {
        var (vm, _) = Apps();
        var (_, reports, _) = Setup();
        // From the start of a day with use (the generated history leaves some days empty), ending before today.
        int oldest = hours > 48 ? 8 : 4;
        DateTime? used = null;
        for (int i = oldest; i >= oldest - 2 && used is null; i--)
        {
            var day = DateTime.Today.AddDays(-i);
            Kit.Wait(async () => { used = (await reports.BuildAsync(ReportRange.Day, day))?.DayStart; });
        }
        Assert.SkipWhen(used is null, "no use in the generated history on those days");
        var from = ReportBuilder.HourStart(used!.Value);
        Ui.Run(() =>
        {
            vm.CustomFrom = from;
            vm.CustomTo = from.AddHours(hours);
            vm.Unit = ReportRange.Custom;
        });
        Loaded(vm, () => vm.SelectedChart?.Count == bars);
        Ui.Run(() =>
        {
            Assert.Equal(title, vm.ChartTitle);
            Assert.Equal(hours <= 48, vm.IsDay);
            Assert.False(vm.IncludesToday);
            Assert.Equal(PeriodPicker.Duration(TimeSpan.FromHours(hours)), vm.RangeNote);
            Assert.EndsWith($" in {Report.CustomTitle(from, from.AddHours(hours))}", vm.Summary);
            // The first bar is the range's first hour (or day), not midnight before it.
            Assert.Equal(hours <= 48 ? from : from.Date, vm.SelectedChart![0].Day);
        });
    }

    [Fact]
    public void Apps_of_an_earlier_period_do_not_include_today()
    {
        var (vm, _) = Apps();
        Ui.Run(() => { vm.Unit = ReportRange.Week; vm.Anchor = DateTime.Today.AddDays(-7); });
        Loaded(vm, () => !vm.IncludesToday);
        Ui.Run(() =>
        {
            Assert.EndsWith(" last week", vm.Summary);
            Assert.NotEmpty(vm.RangeNote);
        });
    }

    [Fact]
    public void Apps_all_time_counts_from_the_first_day()
    {
        var (vm, _) = Apps();
        Ui.Run(() => vm.Unit = ReportRange.All);
        Loaded(vm, () => vm.Summary.EndsWith(" in total"));
        Ui.Run(() =>
        {
            Assert.EndsWith(" days)", vm.RangeNote);
            Assert.True(vm.IncludesToday);
        });
    }

    [Fact]
    public void Renaming_categorizing_and_excluding_the_selected_app_go_to_settings()
    {
        var (vm, settings) = Apps();
        AllTime(vm);
        Ui.Run(() =>
        {
            vm.SelectExe("chrome.exe");
            var changed = Kit.Changes(vm, () => vm.SelectedAlias = "  Browser  ");
            Assert.Equal("Browser", settings.Current.AppNames["chrome.exe"]);
            Assert.Equal("Browser", vm.SelectedAlias);
            vm.SelectedAlias = "   ";
            Assert.False(settings.Current.AppNames.ContainsKey("chrome.exe"));
            Assert.Equal("Google Chrome", vm.SelectedAlias);

            Assert.Equal(AppCategory.Browser, vm.SelectedCategory);
            changed = Kit.Changes(vm, () => vm.SelectedCategory = AppCategory.Productivity);
            Assert.Equal(AppCategory.Productivity, settings.Current.AppCategories["CHROME.EXE"]);
            Assert.Contains(nameof(AppsViewModel.Summary), changed);
            Assert.StartsWith("Productivity · ", vm.Summary);

            Assert.False(vm.SelectedExcluded);
            vm.SelectedExcluded = true;
            vm.SelectedExcluded = true; // no duplicate
            Assert.Equal(["chrome.exe"], settings.Current.Tracking.ExcludedApps);
            settings.Update(s => s.Tracking.ExcludedApps = ["Chrome.EXE"]);
            Assert.True(vm.SelectedExcluded);
            vm.SelectedExcluded = false;
            Assert.Empty(settings.Current.Tracking.ExcludedApps);
        });
    }

    [Fact]
    public void Nothing_selected_means_nothing_to_rename()
    {
        var (settings, reports, _) = Setup();
        var vm = Ui.Run(() => new AppsViewModel(reports, settings));
        Ui.Run(() =>
        {
            Assert.False(vm.HasSelection);
            Assert.Equal("", vm.Summary);
            Assert.Equal("", vm.SelectedAlias);
            Assert.Equal(AppCategory.Other, vm.SelectedCategory);
            Assert.False(vm.SelectedExcluded);
            int changes = 0;
            settings.Changed += () => changes++;
            vm.SelectedAlias = "x";
            vm.SelectedCategory = AppCategory.Game;
            vm.SelectedExcluded = true;
            Assert.Equal(0, changes);
            vm.OpenFileLocationCommand.Execute(null); // no app: nothing opens
        });
    }

    [Fact]
    public void An_alias_shows_in_the_list_after_a_reload()
    {
        var start = SeedData.QuietSettings();
        start.AppNames["steam.exe"] = "Valve Steam";
        var (vm, _) = Apps(start);
        Ui.Run(() =>
        {
            vm.Search = "valve";
            Assert.Equal("steam.exe", Assert.Single(vm.Apps).Stat.Exe);
        });
    }

    [Fact]
    public void Apps_refresh_keeps_the_selection_and_rows()
    {
        var (vm, _) = Apps();
        Ui.Run(() => vm.SelectExe("discord.exe"));
        Loaded(vm, () => vm.SelectedChart is not null);
        var chart = Ui.Run(() => vm.SelectedChart);
        Kit.Wait(() => vm.LoadAsync());
        Ui.Run(() =>
        {
            Assert.Equal("discord.exe", vm.Selected?.Exe);
            Assert.NotNull(vm.SelectedChart);
        });
    }

    [Fact]
    public void Every_known_app_is_in_all_time()
    {
        var (vm, _) = Apps();
        AllTime(vm);
        Ui.Run(() => vm.Sort = "Memory");
        Ui.Run(() =>
        {
            Assert.EndsWith(" in total", vm.Summary);
            var exes = vm.Apps.Select(a => a.Stat.Exe).ToHashSet();
            foreach (var exe in KnownExes.Take(7)) Assert.Contains(exe, exes);
        });
    }

    // ── Memory ───────────────────────────────────────────────────────────

    [Fact]
    public void Memory_page_lists_todays_ten_biggest_apps()
    {
        var (_, reports, live) = Setup();
        var vm = Ui.Run(() => new MemoryViewModel(reports, live));
        Ui.Run(() => Assert.Equal(1, vm.TodayMemoryMax));
        Kit.Wait(() => vm.RefreshAsync());
        Ui.Run(() =>
        {
            Assert.InRange(vm.TodayTop.Count, 1, 10);
            var peaks = vm.TodayTop.Select(a => a.MemMax!.Value).ToList();
            Assert.Equal(peaks.OrderDescending(), peaks);
            Assert.Equal(peaks[0], vm.TodayMemoryMax);
            Assert.Same(live, vm.Live);
        });
    }
}
