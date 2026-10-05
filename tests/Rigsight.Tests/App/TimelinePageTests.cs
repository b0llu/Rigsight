using Rigsight.Core.Stability;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// The Timeline page on a made-up history: a driver, then driver resets the next day, then a blue screen; app updates
/// that fold into one line; a calendar to jump around; and days built a page at a time.
/// </summary>
[Collection("UI")]
public sealed class TimelinePageTests
{
    private static readonly DateTime Today = DateTime.Today;
    private static long _id;

    private static SystemChange Change(DateTime at, ChangeKind kind, string title, string? was = null, string? now = null) =>
        new(at, kind, title) { Id = ++_id, Subject = title, Was = was, Now = now };

    private static SystemChange Update(DateTime at, string app, string to = "2.0") => Change(at, ChangeKind.AppUpdated, $"{app} updated to {to}", "1.0", to);

    private static (TimelineViewModel Vm, List<DateTime> Opened) Page(IEnumerable<SystemChange>? changes = null, IEnumerable<TimelineProblem>? problems = null,
        IEnumerable<InventoryItem>? inventory = null)
    {
        var opened = new List<DateTime>();
        var settings = Kit.OfflineSettings();
        var vm = Ui.Run(() => new TimelineViewModel(new ReportService(settings), opened.Add));
        Ui.Run(() => vm.Apply(new TimelineData([.. changes ?? []], [.. problems ?? []], [.. inventory ?? []])));
        return (vm, opened);
    }

    /// <summary>The story from the page's own example: a new driver, resets the day after, a blue screen the day after that.</summary>
    private static (List<SystemChange> Changes, List<TimelineProblem> Problems) Story()
    {
        var d = Today.AddDays(-7);
        var changes = new List<SystemChange>
        {
            Change(d.AddHours(19.25), ChangeKind.Driver, "NVIDIA graphics driver 616.92", "610.47", "616.92"),
            Update(d.AddHours(19), "NVIDIA App"), Update(d.AddHours(19.5), "NVIDIA PhysX"), Update(d.AddHours(19.6), "NVIDIA HD Audio Driver"),
            Update(d.AddDays(1).AddHours(10), "Comet"),
            Change(d.AddDays(-3).AddHours(21), ChangeKind.AppInstalled, "Stremio installed", now: "5.0.26"),
            Change(d.AddDays(-3).AddHours(17), ChangeKind.Startup, "FACEIT now starts with Windows"),
            Change(d.AddDays(-3), ChangeKind.Storage, "61 GB more in use on E:", "1,104 GB", "1,165 GB"),
        };
        var problems = new List<TimelineProblem>
        {
            new(d.AddDays(1).AddHours(20), CrashKind.GpuDriverReset, "Graphics driver reset"),
            new(d.AddDays(1).AddHours(20.5), CrashKind.GpuDriverReset, "Graphics driver reset"),
            new(d.AddDays(1).AddHours(21), CrashKind.GpuDriverReset, "Graphics driver reset"),
            new(d.AddDays(2).AddHours(21.7), CrashKind.SystemCrash, "Windows crashed (blue screen)"),
        };
        return (changes, problems);
    }

    [Fact]
    public void Days_run_newest_first_with_what_happened_on_each()
    {
        var (changes, problems) = Story();
        var (vm, _) = Page(changes, problems);
        Ui.Run(() =>
        {
            Assert.False(vm.IsEmpty);
            Assert.Equal([Today.AddDays(-5), Today.AddDays(-6), Today.AddDays(-7), Today.AddDays(-10)], vm.Days.Select(d => d.Day));

            // The blue screen's day has nothing else on it: a red dot.
            var crash = vm.Days[0];
            Assert.Equal("HotBrush", crash.NodeBrush);
            var line = Assert.Single(crash.Entries);
            Assert.Equal(("Windows crashed (blue screen)", "HotBrush", true, true), (line.Title, line.Brush, line.IsProblem, line.IsKey));

            // Three resets are one line, after the update that morning (newest first).
            var resets = vm.Days[1];
            Assert.Equal("CpuBrush", resets.NodeBrush);
            Assert.Equal(["3 graphics driver resets", "Comet updated to 2.0"], resets.Entries.Select(e => e.Title));
            Assert.Equal(("WarmBrush", "8:00 PM"), (resets.Entries[0].Brush, resets.Entries[0].TimeText));

            // The driver stands out; the three app updates beside it are one line.
            var driver = vm.Days[2];
            Assert.Equal(["3 apps updated", "NVIDIA graphics driver 616.92"], driver.Entries.Select(e => e.Title));
            Assert.Equal((true, "Was 610.47", "7:15 PM"), (driver.Entries[1].IsKey, driver.Entries[1].Detail, driver.Entries[1].TimeText));
            Assert.Equal("NVIDIA HD Audio Driver, NVIDIA PhysX, NVIDIA App", driver.Entries[0].Detail);
            Assert.Equal("7:36 PM", driver.Entries[0].TimeText); // the last of the three
            Assert.False(driver.Entries[0].IsKey);

            // An install shows its version; drive space has no time and comes last.
            var older = vm.Days[3];
            Assert.Equal(["Stremio installed", "FACEIT now starts with Windows", "61 GB more in use on E:"], older.Entries.Select(e => e.Title));
            Assert.Equal(["5.0.26", null, "1,104 GB to 1,165 GB in use"], older.Entries.Select(e => e.Detail));
            Assert.Equal(["9:00 PM", "5:00 PM", "All day"], older.Entries.Select(e => e.TimeText)); // every line says when
            Assert.Equal("8 changes since " + changes.Min(c => c.Time).ToString("d MMM yyyy"), vm.CountText);
        });
    }

    [Fact]
    public void A_time_that_is_when_the_change_was_noticed_says_so()
    {
        var day = Today.AddDays(-3);
        SystemChange Found(double hour, string app) => Update(day.AddHours(hour), app) with { Subject = $"app:{app.ToLowerInvariant()}" };
        var (vm, _) = Page([Change(day.AddHours(19.25), ChangeKind.Driver, "NVIDIA graphics driver 616.92"),      // from Windows' log
            Found(20, "Steam"),
            Change(day, ChangeKind.Storage, "61 GB more in use on E:", "1,104 GB", "1,165 GB"),
            Found(9, "A"), Found(10, "C") with { Time = day.AddDays(-1).AddHours(10) }, Found(11, "D") with { Time = day.AddDays(-1).AddHours(11) },
            Found(12, "E") with { Time = day.AddDays(-1).AddHours(12) }]);
        Ui.Run(() =>
        {
            var entries = vm.Days[0].Entries;
            Assert.Equal(["~8:00 PM", "7:15 PM", "~9:00 AM", "All day"], entries.Select(e => e.TimeText));
            Assert.Equal("Between 7:50 PM and 8:00 PM", entries[0].TimeTip); // the ten minutes between two checks
            Assert.Null(entries[1].TimeTip); // exact: nothing to explain
            Assert.NotNull(entries[3].TimeTip);

            // A folded line carries the time of its newest update, approximate like it; so does each one under it.
            var folded = Assert.Single(vm.Days[1].Entries);
            Assert.Equal(("3 apps updated", "~12:00 PM"), (folded.Title, folded.TimeText));
            Assert.NotNull(folded.TimeTip);
            Assert.All(folded.Children, c => Assert.StartsWith("~", c.TimeText));
        });

        // The check before is known: the range is the real one, with its day when the PC was off in between.
        var at = Today.AddDays(-2).AddHours(9).AddMinutes(1);
        var (known, _) = Page([Found(0, "Steam") with { Time = at, NoticedFrom = at.AddMinutes(-4) },
            Found(0, "Edge") with { Time = at.AddHours(1), NoticedFrom = at.AddDays(-1).AddHours(14) }]);
        Ui.Run(() =>
        {
            Assert.Equal($"Between {at.AddDays(-1).AddHours(14):ddd d MMM, h:mm tt} and 10:01 AM", known.Days[0].Entries[0].TimeTip);
            Assert.Equal("Between 8:57 AM and 9:01 AM", known.Days[0].Entries[1].TimeTip);
        });
    }

    [Fact]
    public void Today_and_yesterday_are_named_and_each_month_gets_a_heading()
    {
        var first = new DateTime(Today.Year, Today.Month, 1);
        var (vm, _) = Page([Update(Today.AddHours(1), "A"), Update(Today.AddDays(-1).AddHours(1), "B"), Update(Today.AddDays(-2).AddHours(1), "C"),
            Update(first.AddMonths(-2).AddDays(3), "D"), Update(first.AddMonths(-2).AddDays(2), "E")]);
        Ui.Run(() =>
        {
            Assert.Equal(("Today", Today.ToString("dddd, d MMMM")), (vm.Days[0].Title, vm.Days[0].SubTitle));
            Assert.Equal("Yesterday", vm.Days[1].Title);
            Assert.Equal((Today.AddDays(-2).ToString("dddd d"), ""), (vm.Days[2].Title, vm.Days[2].SubTitle));
            Assert.Equal(Today.ToString("MMMM yyyy").ToUpper(), vm.Days[0].Month);
            var old = vm.Days.Where(d => d.Day < first.AddMonths(-1)).ToList();
            Assert.Equal([first.AddMonths(-2).ToString("MMMM yyyy").ToUpper(), null], old.Select(d => d.Month));
            // A heading only where the month changes.
            Assert.Equal(vm.Days.Select(d => (d.Day.Year, d.Day.Month)).Distinct().Count(), vm.Days.Count(d => d.Month is not null));
        });
    }

    [Fact]
    public void App_updates_fold_from_three_a_day_and_unfold_on_request()
    {
        var day = Today.AddDays(-2);
        var (vm, _) = Page([Update(day.AddHours(1), "A"), Update(day.AddHours(2), "B"), Update(day.AddHours(3), "C"),
            Update(day.AddDays(-1).AddHours(1), "X"), Update(day.AddDays(-1).AddHours(2), "Y")]);
        Ui.Run(() =>
        {
            var folded = Assert.Single(vm.Days[0].Entries);
            Assert.Equal(("3 apps updated", "C, B, A", true, false, "Show versions"), (folded.Title, folded.Detail, folded.HasChildren, folded.IsExpanded, folded.ToggleText));
            Assert.Equal(["C updated to 2.0", "B updated to 2.0", "A updated to 2.0"], folded.Children.Select(c => c.Title));
            Assert.All(folded.Children, c => Assert.Equal("Was 1.0", c.Detail));
            Assert.Equal(["Y updated to 2.0", "X updated to 2.0"], vm.Days[1].Entries.Select(e => e.Title)); // two stay as they are

            vm.ToggleVersionsCommand.Execute(folded);
            Assert.True(folded.IsExpanded);
            Assert.Equal("Hide versions", folded.ToggleText);

            // One line each: every update on its own, newest first.
            vm.GroupUpdates = false;
            Assert.Equal(["C updated to 2.0", "B updated to 2.0", "A updated to 2.0"], vm.Days[0].Entries.Select(e => e.Title));
            Assert.All(vm.Days[0].Entries, e => Assert.False(e.HasChildren));

            // Folded again, it's still open where it was opened.
            vm.GroupUpdates = true;
            Assert.True(Assert.Single(vm.Days[0].Entries).IsExpanded);
            vm.ToggleVersionsCommand.Execute(vm.Days[0].Entries[0]);
            vm.GroupUpdates = false;
            vm.GroupUpdates = true;
            Assert.False(vm.Days[0].Entries[0].IsExpanded);
        });
    }

    [Fact]
    public void A_months_app_updates_can_be_one_line_at_the_top_of_the_month()
    {
        // Two months back, so nothing here depends on how far into this month today is.
        var first = new DateTime(Today.Year, Today.Month, 1).AddMonths(-2);
        var driver = Change(first.AddDays(9).AddHours(19), ChangeKind.Driver, "NVIDIA graphics driver 616.92");
        var (vm, _) = Page([Update(first.AddDays(2).AddHours(9), "Steam"), Update(first.AddDays(9).AddHours(10), "Chrome", "2.0"), Update(first.AddDays(20).AddHours(10), "Chrome", "3.0"),
            Update(first.AddDays(20).AddHours(11), "Discord"), driver,
            Update(first.AddMonths(1).AddDays(3).AddHours(9), "Steam"), Update(first.AddMonths(1).AddDays(4).AddHours(9), "Edge")]); // two in the next month: too few to fold
        Ui.Run(() =>
        {
            Assert.True(vm.GroupUpdates);
            vm.Grouping = "Month";
            Assert.True(vm.GroupUpdates);

            // The later month keeps its two updates on their days; the earlier one opens with its four in one line.
            Assert.Equal([first.AddMonths(1).AddDays(4), first.AddMonths(1).AddDays(3)], vm.Days.Take(2).Select(d => d.Day));
            var month = vm.Days[2];
            Assert.True(month.IsSummary);
            Assert.Equal(("App updates", first.ToString("MMMM yyyy").ToUpper(), "FaintBrush"), (month.Title, month.Month, month.NodeBrush));
            var line = Assert.Single(month.Entries);
            Assert.Equal(("4 app updates", "Chrome ×2, Discord, Steam", "All month", true), (line.Title, line.Detail, line.TimeText, line.HasChildren));
            Assert.Equal(["Discord updated to 2.0", "Chrome updated to 3.0", "Chrome updated to 2.0", "Steam updated to 2.0"], line.Children.Select(c => c.Title));
            Assert.Equal($"{first.AddDays(20):d MMM} · was 1.0", line.Children[0].Detail);

            // The days under it show what's left: the driver. The update-only days are gone, and the heading isn't repeated.
            var rest = vm.Days.Skip(3).ToList();
            var day = Assert.Single(rest);
            Assert.Equal((first.AddDays(9), null, false), (day.Day, day.Month, day.IsSummary));
            Assert.Equal("NVIDIA graphics driver 616.92", Assert.Single(day.Entries).Title);

            // Its versions open and stay open; a month in the calendar goes to the top of that month.
            vm.ToggleVersionsCommand.Execute(line);
            vm.Grouping = "Each";
            vm.Grouping = "Month";
            Assert.True(vm.Days[2].Entries[0].IsExpanded);
            TimelineDay? asked = null;
            vm.JumpRequested += d => asked = d;
            vm.OnTopDay(first);
            vm.ZoomOutCommand.Execute(null);
            var cell = vm.CalendarMonths.Single(m => m.Month == first);
            Assert.Equal(5, cell.Count); // the folded updates still count
            vm.PickMonthCommand.Execute(cell);
            Assert.True(asked!.IsSummary);
            // A day of that month goes to the day, not the month's line.
            vm.JumpTo(first.AddDays(9));
            Assert.Equal(first.AddDays(9), asked.Day);

            // Back to days: every update is on its own day again.
            vm.Grouping = "Day";
            Assert.DoesNotContain(vm.Days, d => d.IsSummary);
            Assert.Equal(5, vm.Days.Count); // five days hold the seven changes
        });
    }

    [Fact]
    public void The_filter_lists_the_kinds_there_are_and_shows_one_at_a_time()
    {
        var (changes, problems) = Story();
        var (vm, _) = Page(changes, problems);
        Ui.Run(() =>
        {
            Assert.Equal(["Everything (8)", "Drivers (1)", "Apps (5)", "Startup programs (1)", "Drive space (1)"], vm.Filters.Select(f => f.Label));
            Assert.Equal("all", vm.Filter!.Key);

            vm.Filter = vm.Filters.Single(f => f.Key == "drivers");
            var day = Assert.Single(vm.Days);
            Assert.Equal("NVIDIA graphics driver 616.92", Assert.Single(day.Entries).Title);
            Assert.StartsWith("1 change since ", vm.CountText);
            // Problems are context for everything together, not part of one kind.
            Assert.DoesNotContain(vm.Days.SelectMany(d => d.Entries), e => e.IsProblem);
            Assert.Equal("drivers", vm.Filter!.Key); // still picked after the list was rebuilt

            vm.Filter = vm.Filters.Single(f => f.Key == "apps");
            Assert.Equal(3, vm.Days.Count);
            vm.Filter = vm.Filters.Single(f => f.Key == "all");
            Assert.Equal(4, vm.Days.Count);
        });
    }

    [Fact]
    public void A_problem_opens_its_day_on_the_Crashes_page()
    {
        var (changes, problems) = Story();
        var (vm, opened) = Page(changes, problems);
        Ui.Run(() =>
        {
            vm.OpenCrashesCommand.Execute(vm.Days[1].Entries[0]);
            vm.OpenCrashesCommand.Execute(vm.Days[1].Entries[1]); // a change: nothing to open
        });
        Assert.Equal([Today.AddDays(-6)], opened);
    }

    [Fact]
    public void Nothing_recorded_says_so()
    {
        var (vm, _) = Page();
        Ui.Run(() =>
        {
            Assert.True(vm.IsEmpty);
            Assert.Equal(("No changes yet", ""), (vm.EmptyText, vm.CountText));
            Assert.Empty(vm.Days);
            Assert.Equal("Everything (0)", Assert.Single(vm.Filters).Label);
            Assert.False(vm.HasMore);
            Assert.False(vm.CanGoBack);
            Assert.False(vm.CanGoForward);
            Assert.Empty(vm.Now);
        });
    }

    [Fact]
    public void What_was_different_after_goes_under_the_days_biggest_change()
    {
        var day = Today.AddDays(-7);
        var driver = Change(day.AddHours(19), ChangeKind.Driver, "NVIDIA graphics driver 616.92", "610.47", "616.92");
        var update = Change(day.AddHours(3), ChangeKind.WindowsUpdate, "Windows update KB1");
        var vm = Ui.Run(() => new TimelineViewModel(new ReportService(Kit.OfflineSettings()), _ => { }));
        TimelineData Data(Rigsight.Core.Reports.EffectTone tone, params string[] lines) => new([driver, update, Update(day.AddHours(20), "Steam")], [], [])
        {
            Effects = lines.Length == 0 ? [] : new() { [day] = new(lines.ToList(), tone) },
        };
        Ui.Run(() =>
        {
            vm.Apply(Data(Rigsight.Core.Reports.EffectTone.Worse, "3 graphics driver resets since, none in the 14 days before."));
            var entries = vm.Days.Single().Entries;
            var line = entries.Single(e => e.Title.StartsWith("NVIDIA"));
            Assert.Equal(["3 graphics driver resets since, none in the 14 days before."], line.Effect);
            Assert.Equal("WarmBrush", line.EffectBrush);
            Assert.All(entries.Where(e => e != line), e => Assert.Empty(e.Effect)); // once a day, not under every change

            // Better is green, nothing different is quiet, and new lines replace the old ones on a refresh.
            vm.Apply(Data(Rigsight.Core.Reports.EffectTone.Better, "No problems since; 2 blue screens in the 14 days before."), onlyIfChanged: true);
            line = vm.Days.Single().Entries.Single(e => e.Title.StartsWith("NVIDIA"));
            Assert.Equal(["No problems since; 2 blue screens in the 14 days before."], line.Effect);
            Assert.Equal("GpuBrush", line.EffectBrush);
            vm.Apply(Data(Rigsight.Core.Reports.EffectTone.Same, "No problems since, as in the 14 days before."), onlyIfChanged: true);
            Assert.Equal("MutedBrush", vm.Days.Single().Entries.Single(e => e.Title.StartsWith("NVIDIA")).EffectBrush);
            vm.Apply(Data(Rigsight.Core.Reports.EffectTone.Same), onlyIfChanged: true);
            Assert.All(vm.Days.Single().Entries, e => Assert.Empty(e.Effect));

            // With only drivers shown the line is still there; with only apps shown there's no change to hang it on.
            vm.Apply(Data(Rigsight.Core.Reports.EffectTone.Worse, "Hotter."), onlyIfChanged: true);
            vm.Filter = vm.Filters.Single(f => f.Key == "windows");
            Assert.Equal(["Hotter."], vm.Days.Single().Entries.Single().Effect);
            vm.Filter = vm.Filters.Single(f => f.Key == "apps");
            Assert.All(vm.Days.Single().Entries, e => Assert.Empty(e.Effect));
        });
    }

    // ---- A long history ----

    private static List<SystemChange> Long(int days = 200) =>
        [.. Enumerable.Range(0, days).Select(i => Update(Today.AddDays(-i * 2).AddHours(12), $"App {i}"))];

    [Fact]
    public void Days_are_built_a_page_at_a_time()
    {
        var (vm, _) = Page(Long());
        Ui.Run(() =>
        {
            Assert.Equal(TimelineViewModel.PageSize, vm.Days.Count);
            Assert.True(vm.HasMore);
            var first = vm.Days[0];
            vm.ShowMoreCommand.Execute(null);
            Assert.Equal(2 * TimelineViewModel.PageSize, vm.Days.Count);
            Assert.Same(first, vm.Days[0]); // what was there stays (the list doesn't jump)
            for (int i = 0; i < 60; i++) vm.ShowMoreCommand.Execute(null);
            Assert.Equal(200, vm.Days.Count);
            Assert.False(vm.HasMore);
            Assert.Equal(vm.Days.Select(d => d.Day).OrderByDescending(d => d), vm.Days.Select(d => d.Day));

            vm.ShowFirstPage();
            Assert.Equal(TimelineViewModel.PageSize, vm.Days.Count);
            Assert.True(vm.HasMore);
        });
    }

    [Fact]
    public void Jumping_to_a_day_builds_down_to_it_and_asks_the_list_to_scroll_there()
    {
        var (vm, _) = Page(Long());
        Ui.Run(() =>
        {
            TimelineDay? asked = null;
            vm.JumpRequested += d => asked = d;
            var target = Today.AddDays(-150);
            vm.JumpTo(target);
            Assert.Equal(target, asked!.Day);
            Assert.Contains(asked, vm.Days);
            Assert.True(vm.Days.Count > 75 && vm.Days.Count < 200);

            // A day with nothing on it: the nearest one before it.
            vm.JumpTo(Today.AddDays(-21));
            Assert.Equal(Today.AddDays(-22), asked.Day);
            // Before the first change: nowhere to go.
            asked = null;
            vm.JumpTo(Today.AddDays(-1000));
            Assert.Null(asked);
        });
    }

    [Fact]
    public void A_refresh_with_nothing_new_leaves_the_list_alone_and_something_new_keeps_your_place()
    {
        var changes = Long();
        var (vm, _) = Page(changes);
        Ui.Run(() =>
        {
            vm.ShowMoreCommand.Execute(null);
            var before = vm.Days.ToList();
            vm.Apply(new TimelineData([.. changes], [], []), onlyIfChanged: true);
            Assert.Equal(before, vm.Days);

            bool scrolled = false;
            vm.JumpRequested += _ => scrolled = true;
            vm.Apply(new TimelineData([.. changes, Update(Today.AddHours(13), "New")], [], []), onlyIfChanged: true);
            Assert.Equal(2 * TimelineViewModel.PageSize, vm.Days.Count); // as many days as were built
            Assert.Equal(2, vm.Days[0].Entries.Count);
            Assert.False(scrolled);
        });
    }

    // ---- The calendar ----

    [Fact]
    public void The_calendar_marks_the_days_with_something_on_them()
    {
        var (changes, problems) = Story();
        var (vm, _) = Page(changes, problems);
        Ui.Run(() =>
        {
            vm.JumpTo(Today.AddDays(-5));
            vm.OnTopDay(Today.AddDays(-5)); // as the list does when it scrolls there
            var month = new DateTime(Today.AddDays(-5).Year, Today.AddDays(-5).Month, 1);
            Assert.Equal(month, vm.CalendarMonth);
            Assert.Equal(month.ToString("MMMM yyyy"), vm.CalendarTitle);
            Assert.Equal(7, vm.Weekdays.Count);

            var days = vm.CalendarCells.Where(c => c.Day is not null).ToList();
            Assert.Equal(DateTime.DaysInMonth(month.Year, month.Month), days.Count);
            Assert.True(vm.CalendarCells.TakeWhile(c => c.Day is null).Count() < 7);
            Assert.Equal(1, days[0].Day!.Value.Day);

            var crash = days.Single(c => c.Day == Today.AddDays(-5));
            Assert.Equal((true, true, "HotBrush", "Windows crashed (blue screen)"), (crash.HasAny, crash.OnlyProblems, crash.DotBrush, crash.Tip));
            if (Today.AddDays(-6).Month == month.Month)
            {
                var resets = days.Single(c => c.Day == Today.AddDays(-6));
                Assert.Equal((true, false, "CpuBrush", "3 graphics driver resets\nComet updated to 2.0"), (resets.HasAny, resets.OnlyProblems, resets.DotBrush, resets.Tip));
            }
            Assert.All(days.Where(c => !c.HasAny), c => Assert.Null(c.Tip));
            Assert.Equal(Today.Month == month.Month ? 1 : 0, days.Count(c => c.IsToday));
        });
    }

    [Fact]
    public void A_busy_days_tip_names_the_first_few_with_what_matters_first()
    {
        var day = Today.AddDays(-1);
        var changes = Enumerable.Range(0, 7).Select(i => Update(day.AddHours(i + 1), $"App {i}")).ToList();
        changes.Add(Change(day.AddHours(1), ChangeKind.Driver, "NVIDIA graphics driver 616.92"));
        var (vm, _) = Page(changes);
        Ui.Run(() =>
        {
            vm.OnTopDay(day);
            var lines = vm.CalendarCells.Single(c => c.Day == day).Tip!.Split('\n');
            Assert.Equal(6, lines.Length);
            Assert.Equal("NVIDIA graphics driver 616.92", lines[0]);
            Assert.Equal("and 3 more", lines[^1]);
        });
    }

    [Fact]
    public void The_calendar_steps_between_the_first_month_and_this_one_and_zooms_out_to_the_year()
    {
        var first = new DateTime(Today.Year, Today.Month, 1);
        var (vm, _) = Page([Update(Today.AddHours(1), "A"), Update(first.AddMonths(-2).AddDays(4), "B"), Update(first.AddMonths(-2).AddDays(5), "C")]);
        Ui.Run(() =>
        {
            Assert.Equal(first, vm.CalendarMonth);
            Assert.True(vm.CanGoBack);
            Assert.False(vm.CanGoForward);
            vm.GoForwardCommand.Execute(null); // already this month
            Assert.Equal(first, vm.CalendarMonth);
            vm.GoBackCommand.Execute(null);
            vm.GoBackCommand.Execute(null);
            vm.GoBackCommand.Execute(null); // no further than the first change
            Assert.Equal(first.AddMonths(-2), vm.CalendarMonth);
            Assert.False(vm.CanGoBack);
            Assert.True(vm.CanGoForward);
            Assert.Equal(2, vm.CalendarCells.Count(c => c.HasAny));

            vm.ZoomOutCommand.Execute(null);
            Assert.True(vm.IsYearView);
            Assert.Equal(vm.CalendarMonth.Year.ToString(), vm.CalendarTitle);
            Assert.Equal(12, vm.CalendarMonths.Count);
            var cell = vm.CalendarMonths.Single(m => m.Month == first.AddMonths(-2));
            Assert.Equal((2, true, "2", true), (cell.Count, cell.HasAny, cell.CountText, cell.IsCurrent));
            Assert.Equal(first.AddMonths(-1).Year == cell.Month.Year ? 1 : 0, vm.CalendarMonths.Count(m => m.Month == first.AddMonths(-1) && !m.HasAny));

            // Picking a month shows its days and goes to its newest day in the list.
            TimelineDay? asked = null;
            vm.JumpRequested += d => asked = d;
            vm.PickMonthCommand.Execute(cell);
            Assert.False(vm.IsYearView);
            Assert.Equal(first.AddMonths(-2), vm.CalendarMonth);
            Assert.Equal(first.AddMonths(-2).AddDays(5), asked!.Day);

            vm.PickDayCommand.Execute(vm.CalendarCells.Single(c => c.Day == first.AddMonths(-2).AddDays(4)));
            Assert.Equal(first.AddMonths(-2).AddDays(4), asked.Day);
            asked = null;
            vm.PickDayCommand.Execute(vm.CalendarCells.First(c => c.Day is not null && !c.HasAny)); // an empty day does nothing
            Assert.Null(asked);
        });
    }

    [Fact]
    public void The_calendar_follows_the_month_being_read_but_not_while_zoomed_out()
    {
        var first = new DateTime(Today.Year, Today.Month, 1);
        var (vm, _) = Page(Long());
        Ui.Run(() =>
        {
            vm.OnTopDay(first.AddMonths(-3).AddDays(9));
            Assert.Equal(first.AddMonths(-3), vm.CalendarMonth);
            vm.ZoomOutCommand.Execute(null);
            vm.OnTopDay(Today);
            Assert.Equal(first.AddMonths(-3), vm.CalendarMonth);
            Assert.True(vm.IsYearView);
        });
    }

    [Fact]
    public void The_calendar_shows_what_the_filter_shows()
    {
        var (changes, problems) = Story();
        var (vm, _) = Page(changes, problems);
        Ui.Run(() =>
        {
            vm.Filter = vm.Filters.Single(f => f.Key == "drivers");
            vm.ZoomOutCommand.Execute(null);
            Assert.Equal(1, vm.CalendarMonths.Sum(m => m.Count));
        });
    }

    // ---- This PC now ----

    [Fact]
    public void This_PC_now_lists_what_it_runs_and_since_when_where_a_change_was_seen()
    {
        var driverDay = Today.AddDays(-7).AddHours(19);
        var (vm, _) = Page(
            [Change(driverDay, ChangeKind.Driver, "NVIDIA graphics driver 616.92", "610.47", "616.92"), Change(Today.AddYears(-1).AddDays(-3), ChangeKind.Firmware, "BIOS updated to F66d", "F65")],
            inventory:
            [
                new(Inventory.Windows, Inventory.Windows, "Windows 11", "25H2"),
                new(Inventory.GpuDriver, "pci", "NVIDIA graphics driver", "616.92"),
                new(Inventory.Bios, Inventory.Bios, "BIOS", "F66d"),
                new(Inventory.Ram, Inventory.Ram, "Memory", "16 GB"),
                new(Inventory.App, "steam", "Steam", "2.10"), new(Inventory.App, "discord", "Discord", "1.0"),
                new(Inventory.Startup, "steam", "Steam", Inventory.On), new(Inventory.Startup, "onedrive", "OneDrive", Inventory.Off),
            ]);
        Ui.Run(() =>
        {
            Assert.Equal(["Windows", "Graphics driver", "BIOS", "Memory", "Apps installed", "Start with Windows"], vm.Now.Select(f => f.Label));
            Assert.Equal(["11 25H2", "616.92", "F66d", "16 GB", "2", "1"], vm.Now.Select(f => f.Value));
            Assert.Null(vm.Now[0].Since); // no Windows version change seen
            Assert.Equal(driverDay.Year == Today.Year ? $"since {driverDay:d MMM}" : $"since {driverDay:d MMM yyyy}", vm.Now[1].Since);
            Assert.Equal($"since {Today.AddYears(-1).AddDays(-3):d MMM yyyy}", vm.Now[2].Since); // another year: with the year
        });
    }

    // ---- From the database ----

    [Fact]
    public void The_page_loads_from_the_recorded_history()
    {
        SharedData.EnsureSeeded();
        var settings = Kit.OfflineSettings();
        var vm = Ui.Run(() => new TimelineViewModel(new ReportService(settings), _ => { }));
        Kit.Wait(() => vm.LoadAsync());
        Ui.Run(() =>
        {
            Assert.True(vm.Loaded);
            Assert.Equal(vm.Days.Count == 0, vm.IsEmpty);
            Assert.True(vm.Days.Count <= TimelineViewModel.PageSize);
        });
        Kit.Wait(() => vm.LoadAsync(onlyIfChanged: true));
    }
}
