using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Rigsight.Core.Data;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Models;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// The Memory page's list beside what is on record: each app's usual, the mark on its bar, the
/// amber figure, "Growing since", the marked rows with their line and the filter, the first days, and today's fullest
/// moment. The record is made up here and handed to the page.
/// </summary>
[Collection("UI")]
public sealed class MemoryPageTests
{
    private static readonly long T0 = TimeUtil.NowUnixMs();
    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    // Mid-afternoon, so "today" has hours behind it whatever the clock says.
    private static readonly DateTime Now = DateTime.Today.AddHours(15).AddMinutes(20);

    private static ProcInfo App(string exe, string name, double memMB, int count = 1, string? path = null, DateTime? started = null)
    {
        var p = Kit.Proc(exe, memMB, count, window: true, name: name);
        p.Path = path ?? $@"C:\Apps\{exe}";
        p.Started = started is { } s ? TimeUtil.ToUnix(s) : 0;
        return p;
    }

    private static List<ProcInfo> Apps() =>
    [
        App("game.exe", "Dota 2", 8000),
        App("chrome.exe", "Google Chrome", 4000, count: 30),
        App("svchost.exe", "Windows service", 1600, count: 90, path: Path.Combine(Windows, "System32", "svchost.exe")),
        App("Discord.exe", "Discord", 1100, count: 4, started: DateTime.Today.AddHours(9).AddMinutes(10)),
        App("updater.exe", "Adobe Updater", 300),
    ];

    /// <summary>
    /// The record: Discord at twice its usual and growing since it started, a part of Windows further over still. The
    /// apps named for the fullest minute held 5 GB, 4 GB and so on then.
    /// </summary>
    private static MemoryPast Past(Action<MemoryPast>? more = null, (DateTime At, double UsedGB)? fullest = null, params string[] fullestApps)
    {
        var past = new MemoryPast { Fullest = fullest, FullestHeld = [.. fullestApps.Select((n, i) => (n, 5120.0 - i * 1024))] };
        past.Usual["discord.exe"] = 520;
        past.Usual["chrome.exe"] = 3900;
        past.Usual["svchost.exe"] = 600;
        past.Usual["updater.exe"] = 600;
        past.Hours["discord.exe"] = [.. Enumerable.Range(0, 16).Select(h => h < 9 ? (double?)null : 500 + (h - 9) * 100)];
        more?.Invoke(past);
        return past;
    }

    private static List<AppStat> Peaks() =>
    [
        new() { Exe = "Discord.exe", Name = "Discord", MemMax = 1115, MemAvg = 720 },
        new() { Exe = "chrome.exe", Name = "Google Chrome", MemMax = 3174, MemAvg = 2610 },
        new() { Exe = "game.exe", Name = "Dota 2", MemMax = 9300, MemAvg = 7800 },
    ];

    /// <summary>The page on a PC with 32 GB of memory, half of it in use, and the apps above running.</summary>
    private static (MemoryViewModel Vm, LiveData Live) Page(bool shown = true)
    {
        var (settings, live) = Kit.Greeted();
        var vm = Ui.Run(() =>
        {
            var made = new MemoryViewModel(new ReportService(settings), live);
            live.ApplyTick(Pc.Tick(T0, 40, ("/ram/data/0", 16), ("/ram/data/1", 16)));
            live.ApplyProcs(Apps());
            if (shown) made.SetShown(true);
            return made;
        });
        return (vm, live);
    }

    private static ProcRow Row(LiveData live, string exe) => live.Procs.Single(p => p.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase));

    // ── The columns ──

    [Fact]
    public void Each_app_says_its_usual()
    {
        var (vm, live) = Page();
        Ui.Run(() =>
        {
            // Before anything is read: no usual.
            Assert.Equal(("1.1 GB", "–"), (Row(live, "Discord.exe").MemText, Row(live, "Discord.exe").UsualText));
            Assert.False(Row(live, "Discord.exe").HasUsual);

            vm.Apply(Past(), Now);
            Assert.Equal("520 MB", Row(live, "Discord.exe").UsualText);
            Assert.Equal("–", Row(live, "game.exe").UsualText); // too little on record
            Assert.Equal("3.8 GB", Row(live, "chrome.exe").UsualText);
            Assert.Equal("600 MB", Row(live, "updater.exe").UsualText);
        });
    }

    [Fact]
    public void An_app_that_starts_while_the_page_shows_gets_its_usual()
    {
        var (vm, live) = Page();
        Ui.Run(() =>
        {
            vm.Apply(Past(p => p.Usual["steam.exe"] = 250), Now);
            var next = Apps();
            next.Add(App("steam.exe", "Steam", 640));
            live.ApplyProcs(next);
            Assert.Equal("250 MB", Row(live, "steam.exe").UsualText);
            Assert.True(Row(live, "steam.exe").IsOver);
            Assert.Equal(250.0 / 8000 * 100, Row(live, "steam.exe").Mark, 6);
            Assert.NotNull(Row(live, "steam.exe").OverLine);
            Assert.Equal(2, live.AboveUsualCount); // Discord and Steam

            // Away from the screen nothing is worked out; back on it, what started meanwhile is caught up with.
            vm.SetShown(false);
            vm.Apply(Past(p => p.Usual["slack.exe"] = 300), Now.AddMinutes(1));
            next.Add(App("slack.exe", "Slack", 700));
            live.ApplyProcs(next);
            Assert.False(Row(live, "slack.exe").HasUsual);
            Assert.False(Row(live, "slack.exe").IsOver);
            vm.SetShown(true);
            Assert.Equal("300 MB", Row(live, "slack.exe").UsualText);
            Assert.True(Row(live, "slack.exe").IsOver);
            Assert.Equal(2, live.AboveUsualCount); // Discord and Slack: Steam's usual wasn't in this read
        });
    }

    // ── The bar and its mark ──

    [Fact]
    public void Bars_fit_the_biggest_of_now_and_usual_among_the_apps_shown()
    {
        var (vm, live) = Page();
        Ui.Run(() =>
        {
            vm.Apply(Past(), Now);
            // Everything shown: the game fills its bar, and every usual is measured on the same scale.
            Assert.Equal(100, Row(live, "game.exe").Bar);
            Assert.Equal(0, Row(live, "game.exe").Mark); // no usual: no mark
            Assert.False(Row(live, "game.exe").HasUsual);
            Assert.Equal(1100.0 / 8000 * 100, Row(live, "Discord.exe").Bar, 6);
            Assert.Equal(520.0 / 8000 * 100, Row(live, "Discord.exe").Mark, 6);
            Assert.True(Row(live, "Discord.exe").HasUsual);

            // Only Discord shown: it fills its bar and its mark sits under halfway.
            live.ProcSearch = "discord";
            Assert.Equal(100, Row(live, "Discord.exe").Bar);
            Assert.Equal(520.0 / 1100 * 100, Row(live, "Discord.exe").Mark, 6);

            // An app under its usual: the usual is the biggest figure, so the mark is at the end and the bar halfway.
            live.ProcSearch = "adobe";
            Assert.Equal(50, Row(live, "updater.exe").Bar, 6);
            Assert.Equal(100, Row(live, "updater.exe").Mark, 6);

            // The scale follows the apps as they change.
            live.ProcSearch = "";
            var next = Apps();
            next[0].MemMB = 2000;
            live.ApplyProcs(next);
            Assert.Equal(100, Row(live, "chrome.exe").Bar);
            Assert.Equal(3900.0 / 4000 * 100, Row(live, "chrome.exe").Mark, 6);
        });
    }

    // ── Amber ──

    [Fact]
    public void A_figure_is_amber_well_above_the_usual_and_never_for_a_part_of_windows()
    {
        var (vm, live) = Page();
        Ui.Run(() =>
        {
            Assert.DoesNotContain(live.Procs, p => p.IsOver);
            vm.Apply(Past(), Now);
            Assert.True(Row(live, "Discord.exe").IsOver); // 1.1 GB against 520 MB
            Assert.Equal("Twice its usual", Row(live, "Discord.exe").OverText);
            Assert.False(Row(live, "chrome.exe").IsOver); // a little above
            Assert.Null(Row(live, "chrome.exe").OverText);
            Assert.False(Row(live, "updater.exe").IsOver); // under it
            Assert.False(Row(live, "game.exe").IsOver); // no usual to be above
            // The service host holds 2.7 times its usual, and is Windows' own.
            Assert.False(Row(live, "svchost.exe").IsOver);
            Assert.Null(Row(live, "svchost.exe").OverText);

            // A program that only carries one of Windows' names, somewhere else, is an ordinary app.
            var next = Apps();
            next[2].Path = @"C:\Games\svchost.exe";
            live.ApplyProcs(next);
            Assert.True(Row(live, "svchost.exe").IsOver);
            Assert.Equal("2.7 times its usual", Row(live, "svchost.exe").OverText);

            // The line is 1.6 times the usual, and follows the app as it changes.
            next = Apps();
            next[3].MemMB = 520 * 1.3;
            live.ApplyProcs(next);
            Assert.False(Row(live, "Discord.exe").IsOver);
            next[3].MemMB = 520 * 1.6;
            live.ApplyProcs(next);
            Assert.False(Row(live, "Discord.exe").IsOver);
            next[3].MemMB = 520 * 1.6 + 1;
            var changes = Kit.Changes(Row(live, "Discord.exe"), () => live.ApplyProcs(next));
            Assert.True(Row(live, "Discord.exe").IsOver);
            Assert.Contains(nameof(ProcRow.IsOver), changes);
            Assert.Equal("1.6 times its usual", Row(live, "Discord.exe").OverText);

            // Once pointed out it stays so until it is clearly back down (1.3 times): an app at the line doesn't flicker.
            next[3].MemMB = 520 * 1.4;
            live.ApplyProcs(next);
            Assert.True(Row(live, "Discord.exe").IsOver);
            Assert.Equal("1.4 times its usual", Row(live, "Discord.exe").OverText);
            next[3].MemMB = 520 * 1.3;
            live.ApplyProcs(next);
            Assert.False(Row(live, "Discord.exe").IsOver);
        });
        Assert.Equal("Twice its usual", ProcRow.OverTextOf(1900, 1000));
        Assert.Equal("Twice its usual", ProcRow.OverTextOf(2240, 1000));
        Assert.Equal("2.3 times its usual", ProcRow.OverTextOf(2300, 1000));
        // Decided on the figure as written: what would read "1.9 times" is twice, and what would read "2.3 times" isn't.
        Assert.Equal("Twice its usual", ProcRow.OverTextOf(1860, 1000));
        Assert.Equal("Twice its usual", ProcRow.OverTextOf(1850, 1000));
        Assert.Equal("1.8 times its usual", ProcRow.OverTextOf(1849, 1000));
        Assert.Equal("2.3 times its usual", ProcRow.OverTextOf(2290, 1000));
        Assert.Equal("Twice its usual", ProcRow.OverTextOf(2249, 1000));
        Assert.Equal("2.6 times its usual", ProcRow.OverTextOf(2600, 1000));
        Assert.Equal("1.8 times its usual", ProcRow.OverTextOf(1800, 1000));
        Assert.Equal("3 times its usual", ProcRow.OverTextOf(3000, 1000));
    }

    // ── Growing since ──

    private static double?[] Hours(params (int Hour, double MB)[] recorded)
    {
        var hours = new double?[Now.Hour + 1];
        foreach (var (hour, mb) in recorded) hours[hour] = mb;
        return hours;
    }

    private static readonly DateTime At910 = DateTime.Today.AddHours(9).AddMinutes(10);

    [Fact]
    public void An_app_is_growing_when_every_hour_since_it_started_is_higher_than_the_last()
    {
        var rising = Hours((9, 500), (10, 600), (11, 700), (12, 800), (13, 900), (14, 1000), (15, 1100));
        Assert.Equal(At910, MemoryViewModel.GrowingSince(rising, 1100, 520, At910, Now));
        // Not above its usual now, or with no usual: nothing to say.
        Assert.Null(MemoryViewModel.GrowingSince(rising, 1100, 1100, At910, Now));
        Assert.Null(MemoryViewModel.GrowingSince(rising, 1100, null, At910, Now));
        // Three hours are the fewest.
        Assert.Equal(DateTime.Today.AddHours(13).AddMinutes(5),
            MemoryViewModel.GrowingSince(Hours((13, 500), (14, 600), (15, 700)), 700, 400, DateTime.Today.AddHours(13).AddMinutes(5), Now));
        Assert.Null(MemoryViewModel.GrowingSince(Hours((14, 600), (15, 700)), 700, 400, DateTime.Today.AddHours(14), Now));
        // Each hour has to be higher by a margin: a per cent up is the same as flat.
        Assert.Null(MemoryViewModel.GrowingSince(Hours((13, 500), (14, 505), (15, 700)), 700, 400, DateTime.Today.AddHours(13), Now));
        // Started today and flat at first: it hasn't grown in every hour since.
        var flatThenUp = Hours((9, 500), (10, 500), (11, 500), (12, 600), (13, 700), (14, 800), (15, 900));
        Assert.Null(MemoryViewModel.GrowingSince(flatThenUp, 900, 400, At910, Now));
        // Running from before today (or since nobody knows when): from the first hour of the rise.
        Assert.Equal(DateTime.Today.AddHours(11), MemoryViewModel.GrowingSince(flatThenUp, 900, 400, At910.AddDays(-1), Now));
        Assert.Equal(DateTime.Today.AddHours(11), MemoryViewModel.GrowingSince(flatThenUp, 900, 400, null, Now));
        // A fall, or an hour not recorded, ends the run; and a rise that ended hours ago says nothing about now.
        Assert.Null(MemoryViewModel.GrowingSince(Hours((11, 500), (12, 600), (13, 700), (14, 650), (15, 800)), 800, 400, null, Now));
        Assert.Null(MemoryViewModel.GrowingSince(Hours((11, 500), (12, 600), (14, 700), (15, 800)), 800, 400, null, Now));
        Assert.Null(MemoryViewModel.GrowingSince(Hours((9, 500), (10, 600), (11, 700), (12, 800)), 800, 400, null, Now));
        // The hour now may not be written yet: the one before it is late enough.
        Assert.Equal(DateTime.Today.AddHours(12), MemoryViewModel.GrowingSince(Hours((12, 500), (13, 600), (14, 700)), 700, 400, null, Now));
        Assert.Null(MemoryViewModel.GrowingSince([], 700, 400, null, Now));
    }

    [Fact]
    public void Growing_since_takes_the_place_of_the_process_count_until_the_row_has_its_own_line()
    {
        var (vm, live) = Page();
        Ui.Run(() =>
        {
            Assert.Equal("4 processes", Row(live, "Discord.exe").SubText);
            // Growing and above its usual, though not by enough to be marked: said under its name, in amber.
            vm.Apply(Past(p => p.Usual["discord.exe"] = 900), Now);
            Assert.True(Row(live, "Discord.exe").IsGrowing);
            Assert.False(Row(live, "Discord.exe").IsOver);
            Assert.Equal("Growing since 9:10 AM", Row(live, "Discord.exe").SubText);
            Assert.True(Row(live, "Discord.exe").SubIsGrowing);
            Assert.Null(Row(live, "Discord.exe").OverLine);
            Assert.Equal(("30 processes", false), (Row(live, "chrome.exe").SubText, Row(live, "chrome.exe").IsGrowing));
            Assert.Equal("1 process", Row(live, "game.exe").SubText);

            // Marked: its own line says it, so the name keeps the count and nothing is said twice.
            vm.Apply(Past(p => p.Peak["discord.exe"] = 1115), Now);
            Assert.True(Row(live, "Discord.exe").IsOver);
            Assert.Equal("4 processes", Row(live, "Discord.exe").SubText);
            Assert.False(Row(live, "Discord.exe").SubIsGrowing);
            Assert.Equal("Growing every hour since 9:10 AM, with a peak of 1.1 GB.", Row(live, "Discord.exe").OverLine);
            Assert.True(Row(live, "Discord.exe").OverLineIsGrowing);

            // No longer rising at the next read: since when it has been above its usual instead.
            var changes = Kit.Changes(Row(live, "Discord.exe"), () => vm.Apply(Past(p => p.Hours["discord.exe"][15] = 900), Now));
            Assert.Contains(nameof(ProcRow.OverLine), changes);
            Assert.Equal("4 processes", Row(live, "Discord.exe").SubText);
            Assert.False(Row(live, "Discord.exe").OverLineIsGrowing);
            Assert.StartsWith("Above its usual since ", Row(live, "Discord.exe").OverLine);
        });
    }

    // ── Above its usual ──

    [Fact]
    public void A_row_above_its_usual_says_since_when_with_its_peak_and_asks_what_to_do()
    {
        var (vm, live) = Page();
        Ui.Run(() =>
        {
            // Flat at 900 MB from noon against a usual of 520 MB, started at 9:10 AM: above it since noon.
            vm.Apply(Past(p =>
            {
                p.Hours["discord.exe"] = Hours((9, 500), (10, 500), (11, 500), (12, 900), (13, 900), (14, 900), (15, 900));
                p.Peak["discord.exe"] = 1250;
            }), Now);
            var discord = Row(live, "Discord.exe");
            Assert.True(discord.IsOver);
            Assert.Equal("Above its usual since 12:00 PM, with a peak of 1.2 GB.", discord.OverLine);
            Assert.Equal("Discord is at 1.1 GB. It usually holds 520 MB. Above its usual since 12:00 PM, with a peak of 1.2 GB. What should I do?", discord.OverAsk!.Text);
            Assert.Equal(Rigsight.Core.Ai.AiPages.Memory, discord.OverAsk.Page);

            // Above it in every hour since it started today: since then, to the minute.
            vm.Apply(Past(p => p.Hours["discord.exe"] = Hours((9, 900), (10, 900), (11, 900), (12, 900), (13, 900), (14, 900), (15, 900))), Now);
            Assert.Equal("Above its usual since it started at 9:10 AM, with a peak of 1.1 GB.", discord.OverLine); // nothing higher on record than now

            // Nothing on record lately (or ever): since the hour now.
            vm.Apply(Past(p => p.Hours.Remove("discord.exe")), Now);
            Assert.Equal("Above its usual since 3:00 PM, with a peak of 1.1 GB.", discord.OverLine);

            // Not above its usual: no line, no question.
            vm.Apply(Past(p => p.Usual["discord.exe"] = 1000), Now);
            Assert.Null(discord.OverLine);
            Assert.Null(discord.OverAsk);
        });
    }

    [Fact]
    public void The_filter_shows_only_the_apps_above_their_usual_while_there_are_any()
    {
        var (vm, live) = Page();
        Ui.Run(() =>
        {
            Assert.Equal((0, false), (live.AboveUsualCount, live.HasAboveUsual));
            vm.Apply(Past(p => p.Usual["game.exe"] = 4000), Now);
            Assert.Equal((2, true), (live.AboveUsualCount, live.HasAboveUsual)); // the game and Discord; never the part of Windows
            var all = live.ProcsView.Cast<ProcRow>().Count();

            live.OnlyAboveUsual = true;
            Assert.Equal(["game.exe", "Discord.exe"], live.ProcsView.Cast<ProcRow>().Select(p => p.Exe));
            // The rows keep their order, and the bars are measured on the apps shown.
            Assert.Equal(100, Row(live, "game.exe").Bar);

            // An app that comes down leaves the list, one that goes up comes in.
            var next = Apps();
            next[3].MemMB = 500;
            live.ApplyProcs(next);
            Assert.Equal(["game.exe"], live.ProcsView.Cast<ProcRow>().Select(p => p.Exe));
            Assert.Equal(1, live.AboveUsualCount);

            // The count follows the search and the switch too.
            live.ProcSearch = "chrome";
            Assert.Equal(0, live.AboveUsualCount);
            // With none left, the filter goes off and every app shows again.
            Assert.False(live.OnlyAboveUsual);
            live.ProcSearch = "";
            Assert.Equal(all, live.ProcsView.Cast<ProcRow>().Count());
            Assert.Equal(1, live.AboveUsualCount);
        });
    }

    [Fact]
    public void In_the_first_days_one_line_says_it_is_too_soon_and_no_row_is_marked()
    {
        var (vm, live) = Page();
        Ui.Run(() =>
        {
            Assert.Null(vm.TooSoonText); // nothing read yet
            vm.Apply(new MemoryPast { RecordedDays = 1 }, Now);
            Assert.Equal("Too soon to say which apps are above their usual. It needs 3 days of records, and has 1 so far.", vm.TooSoonText);
            Assert.DoesNotContain(live.Procs, p => p.IsOver);
            Assert.Equal((0, false), (live.AboveUsualCount, live.HasAboveUsual));
            vm.Apply(new MemoryPast { RecordedDays = 2 }, Now);
            Assert.Equal("Too soon to say which apps are above their usual. It needs 3 days of records, and has 2 so far.", vm.TooSoonText);
            vm.Apply(new MemoryPast(), Now);
            Assert.Equal("Too soon to say which apps are above their usual. It needs 3 days of records.", vm.TooSoonText);
            // With usuals, the line goes.
            vm.Apply(Past(), Now);
            Assert.Null(vm.TooSoonText);
        });
    }

    // ── Today's fullest moment ──

    private static readonly (DateTime At, double UsedGB) At210 = (DateTime.Today.AddHours(14).AddMinutes(10), 13.8);

    [Fact]
    public void The_fullest_moment_needs_85_percent_and_names_the_apps_holding_the_most()
    {
        var held = new List<(string, double)> { ("Dota 2", 8200), ("Google Chrome", 3100), ("Discord", 600) };
        var moment = MemoryViewModel.FullestOf(new MemoryPast { Fullest = At210, FullestHeld = held }, 86.2, 16)!;
        Assert.Equal("Memory was 86% full at 2:10 PM, with Dota 2 and Google Chrome holding the most.", moment.Text);
        Assert.Null(moment.CommitText);
        Assert.False(moment.HasCommit);
        // Its box: what each app held that minute, then everything else (13.8 GB in use).
        Assert.Equal("At 2:10 PM", moment.TipTitle);
        Assert.Equal(
        [
            new HeldRow("Dota 2", "8.0 GB", false), new HeldRow("Google Chrome", "3.0 GB", false), new HeldRow("Discord", "600 MB", false),
            new HeldRow("Everything else", "2.2 GB", true),
        ], moment.Held);

        Assert.Equal("Memory was 85% full at 2:10 PM.", MemoryViewModel.FullestOf(new MemoryPast { Fullest = At210 }, 85.2, 16)!.Text);
        Assert.Null(MemoryViewModel.FullestOf(new MemoryPast { Fullest = At210 }, 84.4, 16));
        Assert.Equal("Memory was 85% full at 2:10 PM.", MemoryViewModel.FullestOf(new MemoryPast { Fullest = At210 }, 84.6, 16)!.Text); // what the gauge shows as 85
        Assert.Null(MemoryViewModel.FullestOf(new MemoryPast(), 97, 16)); // no minute on record to say when
        Assert.Equal("Memory was 90% full at 2:10 PM, with Dota 2 holding the most.",
            MemoryViewModel.FullestOf(new MemoryPast { Fullest = At210, FullestHeld = [("Dota 2", 8200)] }, 90, 16)!.Text);
        // Without the day's highest reading: the fullest minute against all the memory there is.
        Assert.StartsWith("Memory was 86% full", MemoryViewModel.FullestOf(new MemoryPast { Fullest = At210 }, null, 16)!.Text);
        Assert.Null(MemoryViewModel.FullestOf(new MemoryPast { Fullest = At210 }, null, 32));
        Assert.Null(MemoryViewModel.FullestOf(new MemoryPast { Fullest = At210 }, null, 0));
    }

    [Fact]
    public void Windows_nearly_out_of_memory_then_is_a_second_line_from_90_percent_promised()
    {
        var moment = MemoryViewModel.FullestOf(new MemoryPast { Fullest = At210, FullestCommitPct = 94 }, 97, 8)!;
        Assert.Equal("Windows was nearly out of memory then, with 94% of its memory and page file already promised to apps.", moment.CommitText);
        Assert.True(moment.HasCommit);
        Assert.NotNull(MemoryViewModel.FullestOf(new MemoryPast { Fullest = At210, FullestCommitPct = 90 }, 97, 8)!.CommitText);
        Assert.Null(MemoryViewModel.FullestOf(new MemoryPast { Fullest = At210, FullestCommitPct = 89 }, 97, 8)!.CommitText);
        Assert.Null(MemoryViewModel.FullestOf(new MemoryPast { Fullest = At210 }, 97, 8)!.CommitText); // not recorded
    }

    [Fact]
    public void Averages_over_the_hour_stand_in_for_a_minute_that_kept_too_few_apps_and_say_so()
    {
        var past = new MemoryPast { Fullest = At210, FullestHeld = [("Dota 2", 7900), ("Google Chrome", 3000)], FullestByHour = true };
        var moment = MemoryViewModel.FullestOf(past, 88, 16)!;
        Assert.Equal("Average between 2:00 PM and 3:00 PM", moment.TipTitle);
        // What everything else held that minute isn't known from averages.
        Assert.Equal([new HeldRow("Dota 2", "7.7 GB", false), new HeldRow("Google Chrome", "2.9 GB", false)], moment.Held);
    }

    [Fact]
    public void The_fullest_moment_is_the_last_row_of_todays_biggest_and_always_shows()
    {
        var apps = Peaks();
        var moment = new FullestMoment("Memory was 86% full at 2:10 PM.", null, "At 2:10 PM", []);
        var rows = MemoryViewModel.RowsOf(apps, moment);
        Assert.Equal(4, rows.Count);
        Assert.Equal([1, 2, 3, 0], rows.Select(r => r.Rank)); // in the card's order, the fullest moment first to be given room
        Assert.All(rows, r => Assert.True(r.Essential)); // the first three apps and the moment
        Assert.Same(moment, rows[^1].Fullest);
        Assert.False(rows[^1].IsApp);
        var many = Enumerable.Range(0, 8).Select(i => new AppStat { Exe = $"a{i}.exe", Name = $"A{i}", MemMax = 100 - i }).ToList();
        Assert.Equal([true, true, true, false, false, false, false, false], MemoryViewModel.RowsOf(many, null).Select(r => r.Essential));
    }

    [Fact]
    public void The_page_reads_the_record_with_its_refresh()
    {
        SharedData.EnsureSeeded();
        var (vm, live) = Page();
        Kit.Wait(() => vm.RefreshAsync());
        Ui.Run(() =>
        {
            Assert.True(vm.Ready);
            // The seeded fortnight knows Chrome: a usual, read for every app at once.
            Assert.True(Row(live, "chrome.exe").HasUsual);
            Assert.EndsWith("B", Row(live, "chrome.exe").UsualText);
            Assert.True(Row(live, "chrome.exe").Mark > 0);
        });
    }

    // ── The page itself ──

    private static double RightEdge(FrameworkElement element, UIElement within) => element.TranslatePoint(new Point(element.ActualWidth, 0), within).X;

    [Fact]
    public void The_page_shows_the_columns_the_marks_the_boxes_and_the_marked_rows()
    {
        // The AI off, as the button checks below expect: AiGate is the process's own, and a test before this one in the
        // same run could leave it on (the gate, 2026-11-15: the hidden button read visible). Put back as it was after.
        bool aiWasOn = AiGate.IsOn;
        AiGate.Update(false, _ => true, _ => { });
        try { Marked(); }
        finally { AiGate.Update(aiWasOn, _ => true, _ => { }); }
    }

    private void Marked()
    {
        SharedData.EnsureSeeded();
        var (vm, live) = Page(shown: false);
        Ui.Run(() =>
        {
            vm.TodayTop = [.. Peaks().OrderByDescending(a => a.MemMax)];
            vm.Ready = true;
        });
        Ui.TakeProblems();
        var window = Ui.Run(() =>
        {
            var w = new Window
            {
                Content = new Views.MemoryView { DataContext = vm }, Left = -32000, Top = -32000, Width = 1170, Height = 900,
                ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            };
            w.Show();
            return w;
        });
        try
        {
            var fullest = (DateTime.Today.AddHours(14).AddMinutes(10), 13.8);
            Ui.Run(() =>
            {
                live.RamLoad!.SetTodayRange(31, 86);
                vm.Apply(Past(null, fullest, "Dota 2", "Google Chrome"), Now);
            });
            Ui.Pump(300);
            var view = Ui.Run(() => (Views.MemoryView)window.Content);
            Ui.Run(() =>
            {
                var texts = Visuals.Descendants<TextBlock>(view).Where(t => t.IsVisible).Select(t => t.Text).ToList();
                const string discordLine = "Growing every hour since 9:10 AM, with a peak of 1.1 GB.";
                foreach (var expected in new[] { "Memory", "APP", "NOW", "USUAL", "Discord", "4 processes", "30 processes", "520 MB", "–",
                             discordLine, "Above their usual", "1", "Memory was 86% full at 2:10 PM, with Dota 2 and Google Chrome holding the most." })
                    Assert.Contains(expected, texts);
                // The note cards are gone; "Growing since" isn't said again under the name of a row with its own line.
                foreach (var gone in new[] { "MEMORY", "HIGHEST TODAY", "ABOVE ITS USUAL", "FULLEST TODAY", "All memory", "Growing since 9:10 AM" })
                    Assert.DoesNotContain(gone, texts);
                Assert.Null(view.FindName("NotesRow"));

                var list = (ListBox)view.FindName("LiveList");
                var head = (Grid)view.FindName("LiveHead");
                ListBoxItem Item(string exe) => (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(Row(live, exe));

                // Amber for the app well above its usual and for its growing line; plain for the others and for Windows.
                var warm = (System.Windows.Media.Brush)view.FindResource("WarmBrush");
                TextBlock Text(string exe, string text) => Visuals.Descendants<TextBlock>(Item(exe)).First(t => t.Text == text);
                Assert.Same(warm, Text("Discord.exe", "1.1 GB").Foreground);
                Assert.Same(warm, Text("Discord.exe", discordLine).Foreground);
                Assert.NotSame(warm, Text("Discord.exe", "4 processes").Foreground);
                Assert.NotSame(warm, Text("svchost.exe", "1.6 GB").Foreground);
                Assert.NotSame(warm, Text("chrome.exe", "30 processes").Foreground);

                // The marked row is shaded; the others aren't.
                Border Tint(string exe) => Visuals.Descendants<Border>(Item(exe)).First(b => b.Name == "Tint");
                Assert.True(Tint("Discord.exe").IsVisible);
                Assert.False(Tint("chrome.exe").IsVisible);
                Assert.False(Tint("svchost.exe").IsVisible);

                // Its AI button keeps its room with the AI off (as here): the hidden twin under it holds the line's height.
                var buttons = Visuals.Descendants<Button>(Item("Discord.exe")).ToList();
                Assert.Equal(2, buttons.Count);
                Assert.All(buttons, b => Assert.False(b.IsVisible));
                Assert.True(buttons.Max(b => b.ActualHeight) > 0);
                var line = Text("Discord.exe", discordLine);
                Assert.True(((FrameworkElement)line.Parent).ActualHeight >= buttons.Max(b => b.ActualHeight));

                // The filter: its count, usable with an app above its usual.
                var filter = (CheckBox)view.FindName("OverFilter");
                Assert.True(filter.IsEnabled);
                Assert.Equal(false, filter.IsChecked);

                // Today's fullest moment sits right under the apps (no gap above it), and the card is as tall as the RAM card.
                var fullestLine = (Border)Visuals.Descendants<Border>(view).Single(b => b.Name == "FullestLine" && b.DataContext is FullestMoment);
                var todayList = (ItemsControl)view.FindName("TodayList");
                var shownApps = Visuals.Descendants<ContentPresenter>(todayList).Where(c => c.DataContext is TodayRow { IsApp: true } && c.Opacity > 0).ToList();
                Assert.Equal(3, shownApps.Count);
                double lastBottom = shownApps.Max(c => c.TranslatePoint(new Point(0, c.ActualHeight), view).Y);
                Assert.Equal(lastBottom + fullestLine.Margin.Top, fullestLine.TranslatePoint(new Point(), view).Y, 1.0);
                var cards = ((Grid)view.FindName("Overview")).Children.OfType<Controls.SmoothBorder>().ToList();
                Assert.Equal(2, cards.Count);
                Assert.Equal(cards[0].ActualHeight, cards[1].ActualHeight, 0.5);

                // The mark stands at the app's usual along its bar; an app with no usual has none.
                var bars = Visuals.Descendants<ProgressBar>(Item("Discord.exe")).ToList();
                Assert.Equal(2, bars.Count);
                var mark = Visuals.Descendants<Border>(bars[1]).Single(b => b.Width == 2);
                Assert.True(mark.IsVisible);
                Assert.True(bars[1].ActualHeight > bars[0].ActualHeight);
                Assert.Equal(bars[0].ActualWidth * 520 / 8000, RightEdge(mark, bars[0]), 1.0);
                Assert.Equal(bars[0].ActualWidth * 1100 / 8000, Visuals.Descendants<Border>(bars[0]).Last().ActualWidth, 1.0);
                Assert.DoesNotContain(Visuals.Descendants<ProgressBar>(Item("game.exe")), b => b.IsVisible && b.ActualHeight > 6);

                // The box over a bar: taller to point at than the bar, and nothing in it but the two figures and how far over.
                var target = (Grid)bars[0].Parent;
                Assert.True(target.ActualHeight >= 24);
                Assert.NotNull(target.Background);
                Assert.Null(bars[0].ToolTip);
            });
            List<string> Tip(string exe)
            {
                var tip = Ui.Run(() =>
                {
                    var list = (ListBox)view.FindName("LiveList");
                    var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(Row(live, exe));
                    var target = (Grid)Visuals.Descendants<ProgressBar>(item).First().Parent;
                    var made = (ToolTip)target.ToolTip;
                    made.PlacementTarget = target;
                    made.IsOpen = true;
                    return made;
                });
                Ui.Pump(100);
                return Ui.Run(() =>
                {
                    var said = Visuals.Descendants<TextBlock>(tip).Where(t => t.IsVisible).Select(t => t.Text).ToList();
                    tip.IsOpen = false;
                    return said;
                });
            }
            Assert.Equal(["Discord", "1.1 GB", "Now", "520 MB", "Usually", "Twice its usual"], Tip("Discord.exe"));
            Assert.Equal(["Google Chrome", "3.9 GB", "Now", "3.8 GB", "Usually"], Tip("chrome.exe"));
            Assert.Equal(["Dota 2", "7.8 GB", "Now"], Tip("game.exe"));

            // The headings end where the figures end, each of the three: with the few apps there are, and with a list
            // long enough to scroll (its bar takes room from the rows).
            void HeadingsAlign(bool scrolls)
            {
                Ui.Pump(200);
                Ui.Run(() =>
                {
                    var list = (ListBox)view.FindName("LiveList");
                    var head = (Grid)view.FindName("LiveHead");
                    var scroller = Visuals.Descendants<ScrollViewer>(list).First();
                    Assert.Equal(scrolls, scroller.ComputedVerticalScrollBarVisibility == Visibility.Visible);
                    var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(Row(live, "Discord.exe"));
                    var figures = Visuals.Descendants<TextBlock>(item).Where(t => t.IsVisible && t.HorizontalAlignment == HorizontalAlignment.Right).ToList();
                    Assert.Equal(["1.1 GB", "520 MB"], figures.Select(t => t.Text));
                    var headings = Visuals.Descendants<TextBlock>(head).Where(t => t.HorizontalAlignment == HorizontalAlignment.Right).ToList();
                    Assert.Equal(["NOW", "USUAL"], headings.Select(t => t.Text));
                    for (int i = 0; i < 2; i++) Assert.Equal(RightEdge(figures[i], view), RightEdge(headings[i], view), 1.0);
                });
            }
            HeadingsAlign(scrolls: false);

            Ui.Run(() => Ui.SavePng(view, Path.Combine(TestEnvironment.DataDir, "screens", "memory-page.png")));

            // The fullest moment's box: what each app held that minute, then everything else.
            var fullestTip = Ui.Run(() =>
            {
                var target = Visuals.Descendants<Border>(view).Single(b => b.Name == "FullestLine" && b.DataContext is FullestMoment);
                var made = (ToolTip)target.ToolTip;
                made.PlacementTarget = target;
                made.IsOpen = true;
                return made;
            });
            Ui.Pump(100);
            Assert.Equal(["At 2:10 PM", "5.0 GB", "Dota 2", "4.0 GB", "Google Chrome", "4.8 GB", "Everything else"],
                Ui.Run(() =>
                {
                    var said = Visuals.Descendants<TextBlock>(fullestTip).Where(t => t.IsVisible).Select(t => t.Text).ToList();
                    fullestTip.IsOpen = false;
                    return said;
                }));

            // The filter: only the apps above their usual, in the same box; the page doesn't move.
            Rect At(string name) => Ui.Run(() =>
            {
                var e = (FrameworkElement)view.FindName(name);
                return new Rect(e.TranslatePoint(new Point(), view), new Size(e.ActualWidth, e.ActualHeight));
            });
            var card = At("LiveCard");
            Ui.Run(() => vm.Apply(Past(p => p.Usual["game.exe"] = 4000, fullest, "Dota 2", "Google Chrome"), Now));
            Ui.Pump(100);
            Assert.Equal(2, Ui.Run(() => ((CheckBox)view.FindName("OverFilter")).Tag));
            Ui.Run(() => ((CheckBox)view.FindName("OverFilter")).IsChecked = true);
            Ui.Pump(200);
            Assert.Equal(["game.exe", "Discord.exe"], Ui.Run(() => ((ListBox)view.FindName("LiveList")).Items.Cast<ProcRow>().Select(p => p.Exe).ToList()));
            Assert.Equal(card, At("LiveCard"));
            Ui.Run(() => Ui.SavePng(view, Path.Combine(TestEnvironment.DataDir, "screens", "memory-page-filter.png")));
            Ui.Run(() => ((CheckBox)view.FindName("OverFilter")).IsChecked = false);

            // Many apps today: the card shows as many as its height has room for, and the fullest moment right under them.
            Ui.Run(() => vm.TodayTop = [.. Enumerable.Range(0, MemoryViewModel.MostToday).Select(i => new AppStat { Exe = $"a{i}.exe", Name = $"App {i}", MemMax = 9000 - i * 300, MemAvg = 500 })]);
            Ui.Pump(200);
            Ui.Run(() =>
            {
                var todayList = (ItemsControl)view.FindName("TodayList");
                var shown = Visuals.Descendants<ContentPresenter>(todayList).Where(c => c.DataContext is TodayRow { IsApp: true } && c.Opacity > 0).ToList();
                Assert.InRange(shown.Count, MemoryViewModel.AlwaysToday + 1, MemoryViewModel.MostToday - 1);
                var fullestLine = Visuals.Descendants<Border>(todayList).Single(b => b.Name == "FullestLine" && b.DataContext is FullestMoment);
                double lastBottom = shown.Max(c => c.TranslatePoint(new Point(0, c.ActualHeight), view).Y);
                Assert.Equal(lastBottom + fullestLine.Margin.Top, fullestLine.TranslatePoint(new Point(), view).Y, 1.0);
                // Less than a row is left under it: no room for another app.
                double listBottom = todayList.TranslatePoint(new Point(0, todayList.ActualHeight), view).Y;
                double lineBottom = fullestLine.TranslatePoint(new Point(0, fullestLine.ActualHeight), view).Y;
                Assert.InRange(listBottom - lineBottom, -0.5, shown[0].ActualHeight);
                var cards = ((Grid)view.FindName("Overview")).Children.OfType<Controls.SmoothBorder>().ToList();
                Assert.Equal(cards[0].ActualHeight, cards[1].ActualHeight, 0.5);
            });
            Ui.Run(() => Ui.SavePng(view, Path.Combine(TestEnvironment.DataDir, "screens", "memory-page-today.png")));

            // The first days: one line says it's too soon, and the filter is greyed.
            Ui.Run(() => vm.Apply(new MemoryPast { RecordedDays = 1 }, Now));
            Ui.Pump(200);
            Ui.Run(() =>
            {
                var texts = Visuals.Descendants<TextBlock>(view).Where(t => t.IsVisible).Select(t => t.Text).ToList();
                Assert.Contains("Too soon to say which apps are above their usual. It needs 3 days of records, and has 1 so far.", texts);
                Assert.False(((CheckBox)view.FindName("OverFilter")).IsEnabled);
            });
            Ui.Run(() => Ui.SavePng(view, Path.Combine(TestEnvironment.DataDir, "screens", "memory-page-first-days.png")));

            // A long list scrolls, and the headings still end at the figures.
            Ui.Run(() =>
            {
                var many = Apps();
                many.AddRange(Enumerable.Range(0, 60).Select(i => App($"filler{i}.exe", $"Filler {i}", 200 - i)));
                live.ApplyProcs(many);
                vm.Apply(Past(), Now);
            });
            HeadingsAlign(scrolls: true);
            Ui.Run(() => Ui.SavePng(view, Path.Combine(TestEnvironment.DataDir, "screens", "memory-page-long.png")));
            Ui.AssertNoProblems("the Memory page");
        }
        finally
        {
            Ui.Run(() =>
            {
                window.Close();
                vm.SetShown(false);
            });
        }
    }
}
