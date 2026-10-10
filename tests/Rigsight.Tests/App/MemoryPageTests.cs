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
/// amber figure, "Growing since", and the "Worth a look" cards. The record is made up here and handed to the page.
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

    /// <summary>The record: Discord at twice its usual and growing since it started, a part of Windows further over still.</summary>
    private static MemoryPast Past(Action<MemoryPast>? more = null, (DateTime At, double UsedGB)? fullest = null, params string[] fullestApps)
    {
        var past = new MemoryPast { Fullest = fullest, FullestApps = fullestApps };
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
            Assert.Contains(vm.Notes, n => n.Name == "Steam");

            // Away from the screen nothing is worked out; back on it, what started meanwhile is caught up with.
            vm.SetShown(false);
            vm.Apply(Past(p => p.Usual["slack.exe"] = 300), Now.AddMinutes(1));
            next.Add(App("slack.exe", "Slack", 700));
            live.ApplyProcs(next);
            Assert.False(Row(live, "slack.exe").HasUsual);
            Assert.DoesNotContain(vm.Notes, n => n.Name == "Slack");
            vm.SetShown(true);
            Assert.Equal("300 MB", Row(live, "slack.exe").UsualText);
            Assert.Contains(vm.Notes, n => n.Name == "Slack");
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
            next[3].MemMB = 520 * 1.6;
            live.ApplyProcs(next);
            Assert.False(Row(live, "Discord.exe").IsOver);
            next[3].MemMB = 520 * 1.6 + 1;
            var changes = Kit.Changes(Row(live, "Discord.exe"), () => live.ApplyProcs(next));
            Assert.True(Row(live, "Discord.exe").IsOver);
            Assert.Contains(nameof(ProcRow.IsOver), changes);
            Assert.Equal("1.6 times its usual", Row(live, "Discord.exe").OverText);
        });
        Assert.Equal("Twice its usual", ProcRow.OverTextOf(1900, 1000));
        Assert.Equal("Twice its usual", ProcRow.OverTextOf(2290, 1000));
        Assert.Equal("2.3 times its usual", ProcRow.OverTextOf(2300, 1000));
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
    public void Growing_since_takes_the_place_of_the_process_count()
    {
        var (vm, live) = Page();
        Ui.Run(() =>
        {
            Assert.Equal("4 processes", Row(live, "Discord.exe").SubText);
            vm.Apply(Past(), Now);
            Assert.True(Row(live, "Discord.exe").IsGrowing);
            Assert.Equal("Growing since 9:10 AM", Row(live, "Discord.exe").SubText);
            Assert.Equal(("30 processes", false), (Row(live, "chrome.exe").SubText, Row(live, "chrome.exe").IsGrowing));
            Assert.Equal("1 process", Row(live, "game.exe").SubText);

            // No longer rising at the next read: the count is back.
            var changes = Kit.Changes(Row(live, "Discord.exe"), () => vm.Apply(Past(p => p.Hours["discord.exe"][15] = 900), Now));
            Assert.Contains(nameof(ProcRow.SubText), changes);
            Assert.Equal("4 processes", Row(live, "Discord.exe").SubText);
        });
    }

    // ── Worth a look ──

    [Fact]
    public void Cards_name_the_apps_above_their_usual_then_the_fullest_the_memory_was()
    {
        var (vm, live) = Page();
        Ui.Run(() =>
        {
            Assert.False(vm.HasNotes);
            // Nothing stands out: no row at all.
            vm.Apply(Past(p => p.Usual["discord.exe"] = 1000), Now);
            Assert.False(vm.HasNotes);
            Assert.Empty(vm.Notes);

            live.RamLoad!.SetTodayRange(31, 86);
            var fullest = (DateTime.Today.AddHours(14).AddMinutes(10), 13.8);
            vm.Apply(Past(p => p.Usual["game.exe"] = 4000, fullest, "Dota 2", "Google Chrome"), Now);
            Assert.True(vm.HasNotes);
            // The game is 4 GB over its usual, Discord 580 MB; the part of Windows is not an app to point at.
            Assert.Equal(
            [
                new MemoryNote("ABOVE ITS USUAL", true, "Dota 2", Row(live, "game.exe").Icon, "7.8 GB", "It usually holds 3.9 GB."),
                new MemoryNote("ABOVE ITS USUAL", true, "Discord", Row(live, "Discord.exe").Icon, "1.1 GB", "It usually holds 520 MB, and has grown every hour since 9:10 AM."),
                new MemoryNote("FULLEST TODAY", false, "All memory", null, "86%", "At 2:10 PM, with Dota 2 and Google Chrome holding the most."),
            ], vm.Notes);
            Assert.All(vm.Notes.Take(2), n => Assert.True(n.HasIcon));
            Assert.False(vm.Notes[2].HasIcon);

            // The cards follow the apps: the same ones stay the cards they are, a changed one is replaced.
            var (first, second, third) = (vm.Notes[0], vm.Notes[1], vm.Notes[2]);
            live.ApplyProcs(Apps());
            Assert.Same(first, vm.Notes[0]);
            Assert.Same(second, vm.Notes[1]);
            var next = Apps();
            next[3].MemMB = 1300;
            live.ApplyProcs(next);
            Assert.Same(first, vm.Notes[0]);
            Assert.Equal("1.3 GB", vm.Notes[1].Figure);
            Assert.Same(third, vm.Notes[2]);
            // Back near its usual: its card goes.
            next[3].MemMB = 600;
            live.ApplyProcs(next);
            Assert.Equal(["Dota 2", "All memory"], vm.Notes.Select(n => n.Name));
        });
    }

    [Fact]
    public void There_are_four_cards_at_most()
    {
        var (vm, live) = Page();
        Ui.Run(() =>
        {
            live.RamLoad!.SetTodayRange(31, 91);
            var past = Past(p =>
            {
                p.Usual["game.exe"] = 4000;
                p.Usual["chrome.exe"] = 1000;
                p.Usual["updater.exe"] = 100;
            }, (DateTime.Today.AddHours(14), 14.5));
            vm.Apply(past, Now);
            Assert.Equal(["Dota 2", "Google Chrome", "Discord", "Adobe Updater"], vm.Notes.Select(n => n.Name)); // and no room for the fullest

            var next = Apps();
            next.Add(App("steam.exe", "Steam", 5000));
            past.Usual["steam.exe"] = 1500;
            vm.Apply(past, Now);
            live.ApplyProcs(next);
            Assert.Equal(["Dota 2", "Steam", "Google Chrome", "Discord"], vm.Notes.Select(n => n.Name));
        });
    }

    [Fact]
    public void The_fullest_card_needs_85_percent_and_a_time()
    {
        var at = (DateTime.Today.AddHours(14).AddMinutes(10), 13.8);
        var note = MemoryViewModel.FullestNote(new MemoryPast { Fullest = at }, 85.2, 16);
        Assert.Equal(new MemoryNote("FULLEST TODAY", false, "All memory", null, "85%", "At 2:10 PM."), note);
        Assert.Null(MemoryViewModel.FullestNote(new MemoryPast { Fullest = at }, 84.4, 16));
        Assert.Equal("85%", MemoryViewModel.FullestNote(new MemoryPast { Fullest = at }, 84.6, 16)!.Figure); // what the gauge shows as 85
        Assert.Null(MemoryViewModel.FullestNote(new MemoryPast(), 97, 16)); // no minute on record to say when
        Assert.Equal("At 2:10 PM, with Dota 2 holding the most.", MemoryViewModel.FullestNote(new MemoryPast { Fullest = at, FullestApps = ["Dota 2"] }, 90, 16)!.Text);
        // Without the day's highest reading: the fullest minute against all the memory there is.
        Assert.Equal("86%", MemoryViewModel.FullestNote(new MemoryPast { Fullest = at }, null, 16)!.Figure);
        Assert.Null(MemoryViewModel.FullestNote(new MemoryPast { Fullest = at }, null, 32));
        Assert.Null(MemoryViewModel.FullestNote(new MemoryPast { Fullest = at }, null, 0));
    }

    [Fact]
    public void Nothing_is_worked_out_while_the_page_is_away()
    {
        var (vm, live) = Page(shown: false);
        Ui.Run(() =>
        {
            vm.Apply(Past(), Now);
            Assert.Equal(["Discord"], vm.Notes.Select(n => n.Name));
            var next = Apps();
            next[3].MemMB = 600;
            live.ApplyProcs(next);
            Assert.Single(vm.Notes); // as it was left
            vm.SetShown(true);
            Assert.Empty(vm.Notes);
            Assert.False(vm.HasNotes);
            vm.SetShown(false);
        });
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
    public void The_page_shows_the_columns_the_marks_the_box_and_the_cards()
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
                foreach (var expected in new[] { "Memory", "APP", "NOW", "USUAL", "Discord", "Growing since 9:10 AM", "30 processes", "520 MB", "–",
                             "ABOVE ITS USUAL", "FULLEST TODAY", "All memory", "86%", "It usually holds 520 MB, and has grown every hour since 9:10 AM." })
                    Assert.Contains(expected, texts);
                Assert.DoesNotContain("MEMORY", texts);
                Assert.DoesNotContain("HIGHEST TODAY", texts);

                var list = (ListBox)view.FindName("LiveList");
                var head = (Grid)view.FindName("LiveHead");
                ListBoxItem Item(string exe) => (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(Row(live, exe));

                // Amber for the app well above its usual and for its "Growing since"; plain for the others and for Windows.
                var warm = (System.Windows.Media.Brush)view.FindResource("WarmBrush");
                TextBlock Text(string exe, string text) => Visuals.Descendants<TextBlock>(Item(exe)).First(t => t.Text == text);
                Assert.Same(warm, Text("Discord.exe", "1.1 GB").Foreground);
                Assert.Same(warm, Text("Discord.exe", "Growing since 9:10 AM").Foreground);
                Assert.NotSame(warm, Text("svchost.exe", "1.6 GB").Foreground);
                Assert.NotSame(warm, Text("chrome.exe", "30 processes").Foreground);

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

            // The cards: side by side, as many as the page has room for. Two take half each; a third fits beside them
            // at this width, and a fourth goes underneath.
            int Columns() => Ui.Run(() => Visuals.Descendants<UniformGrid>((ItemsControl)view.FindName("NotesRow")).Single().Columns);
            List<Rect> Cards() => Ui.Run(() => Visuals.Descendants<Controls.SmoothBorder>((ItemsControl)view.FindName("NotesRow"))
                .Select(c => new Rect(c.TranslatePoint(new Point(), view), new Size(c.ActualWidth, c.ActualHeight))).ToList());
            Assert.Equal(2, Columns());
            var two = Cards();
            Assert.Equal(2, two.Count);
            Assert.Equal(two[0].Top, two[1].Top);
            Assert.True(two[1].Left > two[0].Right);
            Ui.Run(() => Ui.SavePng(view, Path.Combine(TestEnvironment.DataDir, "screens", "memory-page.png")));

            Ui.Run(() => vm.Apply(Past(p => p.Usual["game.exe"] = 4000, fullest, "Dota 2", "Google Chrome"), Now));
            Ui.Pump(200);
            Assert.Equal(3, Columns());
            var three = Cards();
            Assert.Equal(3, three.Count);
            Assert.All(three, c => Assert.Equal(three[0].Top, c.Top));
            Assert.All(three, c => Assert.True(c.Width >= 320));
            // The row ends where the cards above it and the list under it end.
            double pageRight = Ui.Run(() => RightEdge((FrameworkElement)view.FindName("LiveCard"), view));
            Assert.Equal(pageRight, three[2].Right, 1.0);

            Ui.Run(() => vm.Apply(Past(p => { p.Usual["game.exe"] = 4000; p.Usual["chrome.exe"] = 1000; }, fullest, "Dota 2", "Google Chrome"), Now));
            Ui.Pump(200);
            Assert.Equal(3, Columns());
            var four = Cards();
            Assert.Equal(4, four.Count);
            Assert.True(four[3].Top > four[0].Bottom);
            Assert.Equal(four[0].Left, four[3].Left);
            Ui.Run(() => Ui.SavePng(view, Path.Combine(TestEnvironment.DataDir, "screens", "memory-page-four.png")));

            // Nothing to say: the row takes no room, and the list moves up into it.
            double listTop = Ui.Run(() => ((FrameworkElement)view.FindName("LiveCard")).TranslatePoint(new Point(), view).Y);
            Ui.Run(() =>
            {
                live.RamLoad!.SetTodayRange(31, 60);
                vm.Apply(Past(p => p.Usual["discord.exe"] = 1000), Now);
            });
            Ui.Pump(200);
            Ui.Run(() =>
            {
                Assert.False(((ItemsControl)view.FindName("NotesRow")).IsVisible);
                Assert.True(((FrameworkElement)view.FindName("LiveCard")).TranslatePoint(new Point(), view).Y < listTop - 100);
            });

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
