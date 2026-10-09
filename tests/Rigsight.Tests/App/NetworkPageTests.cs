using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// The Network page on the seeded history: Chrome a trickle in front, Steam downloading in big bursts in the background
/// (one a big download each afternoon), Discord sending as much as it takes in, and a drop early today.
/// </summary>
[Collection("UI")]
public sealed class NetworkPageTests
{
    private static (NetworkViewModel Vm, LiveData Live) Page(DateTime? day = null, ReportRange unit = ReportRange.Day)
    {
        SharedData.EnsureSeeded();
        var (settings, live) = Kit.Greeted(hello: Fixtures.Hello());
        var vm = Ui.Run(() => new NetworkViewModel(new ReportService(settings), live));
        Ui.Run(() =>
        {
            vm.Unit = unit;
            vm.Anchor = day ?? DateTime.Today;
        });
        Kit.Wait(vm.RefreshAsync);
        return (vm, live);
    }

    private static readonly DateTime Yesterday = DateTime.Today.AddDays(-1);

    [Fact]
    public void A_day_shows_its_totals_apps_and_big_download()
    {
        var (vm, _) = Page(Yesterday);
        Ui.Run(() =>
        {
            Assert.True(vm.HasData);
            Assert.NotEqual("—", vm.DownText);
            Assert.Equal("Steam", vm.Apps[0].Name);
            Assert.Equal(100, vm.Apps[0].DownBar + vm.Apps[0].UpBar, 6); // the biggest app fills the bar
            Assert.Equal(24, vm.Bins.Count);
            Assert.Equal("Usage each hour", vm.ChartTitle);
            // The biggest hours: an app and an hour, with everything it moved then (no speed, no kind of use).
            Assert.StartsWith("Biggest hours", vm.DownloadsTitle);
            Assert.InRange(vm.Downloads.Count, 1, Rigsight.Core.Reports.NetReportBuilder.DownloadsShown);
            Assert.Equal("Steam", vm.Downloads[0].Name);
            Assert.Matches(@"^\d{1,2} [AP]M – \d{1,2} [AP]M$", vm.Downloads[0].When);
            Assert.Equal("", vm.Downloads[0].How);
            // Everything used, both ways, leads the tiles.
            Assert.NotEqual("—", vm.TotalText);
            Assert.StartsWith("download and upload · ", vm.TotalNote);
            Assert.Equal("No drops", vm.DropsTitle);
            Assert.Equal(96, vm.Strip.Count);
        });
    }

    private static readonly DateTime StripDay = new(2026, 10, 5);

    private static NetReport DayReport(IEnumerable<(double From, double To)> online, IEnumerable<(double From, double To)>? unrecorded = null,
        IEnumerable<(double From, double To)>? drops = null)
    {
        var quarters = new bool[96];
        foreach (var (from, to) in online)
            for (int i = (int)(from * 4); i < (int)(to * 4); i++) quarters[i] = true;
        return new NetReport
        {
            Range = ReportRange.Day, From = StripDay, To = StripDay.AddDays(1), Minutes = 1, Quarters = quarters,
            Unrecorded = [.. (unrecorded ?? []).Select(u => (StripDay.AddHours(u.From), StripDay.AddHours(u.To)))],
            Drops = [.. (drops ?? []).Select(d => new Rigsight.Core.Data.NetDrop(Rigsight.Core.Data.TimeUtil.ToUnix(StripDay.AddHours(d.From)), Rigsight.Core.Data.TimeUtil.ToUnix(StripDay.AddHours(d.To)), Rigsight.Core.Data.NetDropKind.Internet))],
        };
    }

    [Fact]
    public void A_drop_covers_every_quarter_hour_it_touches_its_last_one_too()
    {
        // 10:59 to 11:43: the quarter hour from 11:30 used to stay "online".
        var strip = NetworkViewModel.StripStates(DayReport([(8, 14)], drops: [(10 + 59 / 60.0, 11 + 43 / 60.0)]));
        Assert.Equal(NetQuarter.Online, strip[42]);                                       // 10:30
        Assert.All(Enumerable.Range(43, 4), i => Assert.Equal(NetQuarter.Dropped, strip[i])); // 10:45 to 11:45
        Assert.Equal(NetQuarter.Online, strip[47]);                                       // 11:45
        // One that ends on the quarter hour doesn't spill into the next; a short one is one cell.
        strip = NetworkViewModel.StripStates(DayReport([(8, 14)], drops: [(9, 9.25), (12, 12.05)]));
        Assert.Equal([NetQuarter.Dropped, NetQuarter.Online], [strip[36], strip[37]]);
        Assert.Equal([NetQuarter.Dropped, NetQuarter.Online], [strip[48], strip[49]]);
        // One that runs past midnight stops at the day's end.
        strip = NetworkViewModel.StripStates(DayReport([(8, 24)], drops: [(23.9, 24.5)]));
        Assert.Equal(NetQuarter.Dropped, strip[95]);
    }

    [Fact]
    public void Time_the_PC_was_on_with_nothing_written_shows_as_not_recorded()
    {
        // 5 Oct 2026: online till 1 PM, nothing written till 8:30 PM, online again; off before 8 and after 10.
        var r = DayReport([(8, 13), (20.5, 22)], unrecorded: [(13, 20.5)]);
        var strip = NetworkViewModel.StripStates(r);
        Assert.Equal(NetQuarter.None, strip[31]);
        Assert.Equal(NetQuarter.Online, strip[51]);
        Assert.All(Enumerable.Range(52, 30), i => Assert.Equal(NetQuarter.Unrecorded, strip[i]));
        Assert.Equal(NetQuarter.Online, strip[82]);
        Assert.Equal(NetQuarter.None, strip[88]);
        // It isn't "No drops" for the whole day: the line under it says how much wasn't recorded.
        Assert.Equal(("No drops", "7 h 30 min not recorded"), NetworkViewModel.DropsText(r, "that day"));
        // A quarter hour with any use in it stays online; a drop goes over both.
        r = DayReport([(8, 13.25)], unrecorded: [(13.1, 20.5)], drops: [(14, 14.25)]);
        strip = NetworkViewModel.StripStates(r);
        Assert.Equal([NetQuarter.Online, NetQuarter.Unrecorded], [strip[52], strip[53]]);
        Assert.Equal(NetQuarter.Dropped, strip[56]);
        Assert.Equal(("Dropped once", "Longest 15 min at 2:00 PM · 7 h 24 min not recorded"), NetworkViewModel.DropsText(r, "that day"));
    }

    [Fact]
    public void A_day_without_drops_says_no_drops_and_nothing_about_being_online_all_day()
    {
        Assert.Equal(("No drops", ""), NetworkViewModel.DropsText(DayReport([(8, 14)]), "today"));
        var week = new NetReport { Range = ReportRange.Week, From = StripDay, To = StripDay.AddDays(7), Minutes = 1 };
        Assert.Equal(("No drops", "The internet stayed up that week"), NetworkViewModel.DropsText(week, "that week"));
        // Today too, whatever the hour (it said "Online all day" from the first minute of the day).
        var (vm, _) = Page(Yesterday);
        Ui.Run(() => Assert.NotEqual("Online all day", vm.DropsTitle));
    }

    [Fact]
    public void A_bar_picked_on_the_chart_lists_that_hours_apps()
    {
        var (vm, _) = Page(Yesterday);
        Ui.Run(() =>
        {
            Assert.Equal("Apps", vm.AppsTitle);
            Assert.False(vm.HasSelection);
            var whole = vm.Apps.Select(a => (a.Name, a.Amount)).ToList();
            // 2 PM, the hour of Steam's seeded download.
            vm.SelectedBin = 14;
            Assert.True(vm.HasSelection);
            Assert.Equal("Apps · 2 PM – 3 PM", vm.AppsTitle);
            Assert.Equal("Steam", vm.Apps[0].Name);
            var bin = vm.Bins[14];
            Assert.Equal(Rigsight.Core.Units.Data(bin.Apps[0].Total), vm.Apps[0].Amount);
            Assert.All(vm.Apps, a => Assert.Contains(bin.Apps, u => u.App == a.Stat.Id));
            Assert.NotEqual(whole, vm.Apps.Select(a => (a.Name, a.Amount)).ToList());
            // Letting go, or another period, is the whole period again.
            vm.ClearSelectionCommand.Execute(null);
            Assert.Equal(whole, vm.Apps.Select(a => (a.Name, a.Amount)).ToList());
            vm.SelectedBin = 14;
            vm.Unit = ReportRange.Week;
            Assert.Equal(-1, vm.SelectedBin);
        });
    }

    [Fact]
    public void Apps_sort_by_total_download_upload_or_background()
    {
        var (vm, _) = Page(Yesterday);
        Ui.Run(() =>
        {
            Assert.Equal("Total", vm.Sort);
            Assert.Equal("Steam", vm.Apps[0].Name);
            Assert.Equal(Rigsight.Core.Units.Data(vm.Apps[0].Stat.Use.Total), vm.Apps[0].Amount);
            // Discord sends as much as it takes in: the most upload after Steam's little, so Upload brings it up.
            vm.Sort = "Upload";
            var byUp = vm.Apps.Select(a => a.Stat.Use.Up).ToList();
            Assert.Equal(byUp.OrderByDescending(x => x), byUp);
            Assert.Equal(Rigsight.Core.Units.Data(vm.Apps[0].Stat.Use.Up), vm.Apps[0].Amount);
            Assert.Equal(0, vm.Apps[0].DownBar);
            Assert.Equal(100, vm.Apps[0].UpBar, 6);
            vm.Sort = "Download";
            Assert.Equal("Steam", vm.Apps[0].Name);
            Assert.Equal(0, vm.Apps[0].UpBar);
            // Background counts only what moved behind another app, as the old switch did.
            vm.Sort = "Background";
            Assert.True(vm.BackgroundOnly);
            Assert.All(vm.Apps, a => Assert.True(a.BackgroundOnly));
            Assert.DoesNotContain(vm.Apps, a => a.Name == "Google Chrome"); // always in front in the seeded day
            vm.BackgroundOnly = false;
            Assert.Equal("Total", vm.Sort);
            // Each row says its direction and its background share, and has its chart colour.
            var steam = vm.Apps.First(a => a.Name == "Steam");
            Assert.Contains("in the background", steam.SubText);
            // An app that was never in the background says nothing about it (not "all in front").
            var chrome = vm.Apps.First(a => a.Name == "Google Chrome");
            Assert.DoesNotContain("background", chrome.SubText);
            Assert.DoesNotContain("front", chrome.SubText);
            Assert.StartsWith("↓ ", steam.SubText);
            Assert.Contains(steam.DotKey, Controls.NetBarsChart.AppBrushKeys);
        });
    }

    [Fact]
    public void The_chart_splits_a_bar_by_app_or_by_background_and_a_click_picks_it()
    {
        var (vm, _) = Page(Yesterday);
        Ui.Run(() =>
        {
            var chart = new Controls.NetBarsChart { Bins = vm.Bins, Unit = vm.Unit, ColorApps = vm.ColorApps, AppNames = vm.AppNames };
            var bin = vm.Bins[14];
            // By app: one piece per app with a colour of its own, then every other app; together they are the bar.
            var pieces = chart.Pieces(bin);
            Assert.Equal(vm.ColorApps.Count + 1, pieces.Count);
            Assert.Equal(bin.Total, pieces.Sum(x => x.Bytes));
            Assert.All(pieces, x => Assert.True(x.Bytes >= 0));
            long steam = vm.Apps.First(a => a.Name == "Steam").Stat.Id;
            Assert.Equal(bin.Apps.First(a => a.App == steam).Total, pieces[vm.ColorApps.ToList().IndexOf(steam)].Bytes);
            Assert.StartsWith("Total ", chart.HoverLines(bin)[0]);
            Assert.Contains(chart.HoverLines(bin), l => l.StartsWith("Steam "));
            Assert.Equal("2 PM – 3 PM", chart.Title(bin.Start));
            // In front / background: two pieces, the same bar.
            chart.Mode = Controls.NetBarsMode.Background;
            pieces = chart.Pieces(bin);
            Assert.Equal(2, pieces.Count);
            Assert.Equal(bin.Total, pieces.Sum(x => x.Bytes));
            Assert.Equal(Math.Min(bin.Background, bin.Total), pieces[1].Bytes);
            Assert.Contains(chart.HoverLines(bin), l => l.StartsWith("In the background "));
            Assert.Equal(["Nothing"], chart.HoverLines(new NetBin(bin.Start, 0, 0, 0)));
            // An hour that wasn't recorded says so: alone when it is empty (it isn't "nothing"), under the total when part of it was.
            Assert.Equal(["Not recorded for 1 h 0 min"], chart.HoverLines(new NetBin(bin.Start, 0, 0, 0) { UnrecordedMinutes = 60 }));
            Assert.Equal("Not recorded for 30 min", chart.HoverLines(bin with { UnrecordedMinutes = 30 })[1]);
            Assert.DoesNotContain(chart.HoverLines(bin with { UnrecordedMinutes = 5 }), l => l.StartsWith("Not recorded"));

            // A click picks the bar, a second lets go; an empty bar can't be picked.
            chart.Pick(14);
            Assert.Equal(14, chart.Selected);
            chart.Pick(14);
            Assert.Equal(-1, chart.Selected);
            int empty = vm.Bins.ToList().FindIndex(b => b.Total == 0);
            if (empty >= 0)
            {
                chart.Pick(14);
                chart.Pick(empty);
                Assert.Equal(-1, chart.Selected);
            }
            chart.Pick(99);
            Assert.Equal(-1, chart.Selected);

            // The legend names what the colours are, in either view.
            Assert.Equal("Every other app", vm.Legend[^1].Text);
            Assert.Contains(vm.Legend, l => l.Text == "Steam");
            vm.ChartMode = "Background";
            Assert.Equal(["The app in front", "In the background, or while you were away"], vm.Legend.Select(l => l.Text));
        });
    }

    [Fact]
    public void The_live_part_is_one_line_that_opens()
    {
        var (vm, _) = Page(Yesterday);
        Ui.Run(() =>
        {
            Assert.False(vm.ShowLive);
            Assert.Equal("Expand", vm.LiveButtonText);
            vm.ToggleLiveCommand.Execute(null);
            Assert.True(vm.ShowLive);
            Assert.Equal("Collapse", vm.LiveButtonText);
        });
    }

    // ── The internet on dashboards ──

    [Fact]
    public void A_dashboards_internet_tiles_read_today_and_right_now()
    {
        SharedData.EnsureSeeded();
        var (settings, live) = Kit.Greeted(hello: Fixtures.Hello());
        var net = Ui.Run(() => new NetTodayViewModel(new ReportService(settings), live));
        Ui.Run(() =>
        {
            // Before anything is read: dashes, not zeros.
            Assert.False(net.HasData);
            Assert.Equal("—", net.TotalText);
            Assert.Equal("—", net.DownNow);
        });
        Kit.Wait(net.RefreshAsync);
        Ui.Run(() =>
        {
            Assert.True(net.HasData);
            Assert.NotEqual("—", net.TotalText);
            Assert.NotEqual("—", net.DownText);
            Assert.NotEqual("—", net.UpText);
            Assert.NotEqual("—", net.BackgroundText);
            Assert.Equal(24, net.Bins.Count);
            Assert.InRange(net.Top.Count, 1, NetTodayViewModel.TopApps);
            Assert.Contains(net.Top, a => a.Name == "Google Chrome");
            Assert.All(net.Top, a => Assert.False(string.IsNullOrEmpty(a.DotKey)));
            Assert.NotEmpty(net.ColorApps);
            Assert.Equal("Dropped once", net.DropsTitle); // the seeded drop at 1 AM today
        });
        // Read again with nothing new: the lists handed to the tiles are the same ones.
        var (top, bins) = Ui.Run(() => (net.Top, net.Bins));
        Kit.Wait(net.RefreshAsync);
        Ui.Run(() =>
        {
            Assert.Same(top, net.Top);
            Assert.Same(bins, net.Bins);
        });
    }

    [Fact]
    public void There_is_a_network_dashboard_and_every_internet_tile_can_be_added()
    {
        var network = Models.DashboardPresets.All.Single(p => p.Name == "Network");
        Assert.True(Models.DashboardPresets.Offered(network, _ => true));
        var internet = Models.TileCatalog.Groups.Single(g => g.Name == "INTERNET").Tiles;
        Assert.Equal(["net-speed", "net-now", "net-today", "net-apps", "net-chart", "net-drops"], internet.Select(t => t.Kind));
        // The template is made of tiles from the list, each drawn by a template of its own.
        Assert.All(network.Tiles, t => Assert.Contains(internet, k => k.Kind == t.Kind));
        // Laid out it fills three whole rows of the twelve columns: no tile left hanging beside a gap.
        var (settings, live) = Kit.Greeted(hello: Fixtures.Hello());
        Ui.Run(() =>
        {
            var page = new CustomPageViewModel(new Rigsight.Core.Settings.CustomPageConfig { Name = "Network" }, settings, live, null!, null!, _ => { }, _ => { });
            page.AddTiles(network.Tiles);
            Assert.Equal([("net-today", 0, 0, 8, 2), ("net-drops", 8, 0, 4, 2), ("net-chart", 0, 2, 12, 4), ("net-apps", 0, 6, 4, 4), ("net-now", 4, 6, 4, 4), ("net-speed", 8, 6, 4, 4)],
                page.Tiles.Select(t => (t.Kind, t.X, t.Y, t.W, t.H)));
            // Every row is full.
            Assert.All(page.Tiles.GroupBy(t => t.Y), row => Assert.Equal(Rigsight.Core.Settings.TileConfig.Columns, row.Sum(t => t.W)));
            page.Dispose();
        });
        Ui.Run(() =>
        {
            var view = new Views.CustomPageView();
            Assert.All(internet, t => Assert.NotNull(view.TryFindResource("Tile." + t.Kind)));
        });
    }

    [Fact]
    public void Coming_back_to_the_page_keeps_the_rows_it_has()
    {
        // Read again on every visit and every minute: with nothing new to say, no list is given to the page anew (each
        // one would have its rows built and laid out again: a fifth of a second on a real PC's Network page).
        // Yesterday, whose seeded day always has Steam's download (today has it only for some hours of the clock).
        var (vm, _) = Page(Yesterday);
        var before = Ui.Run(() => (Apps: vm.Apps.ToList(), vm.Insights, vm.Strip, vm.Downloads, vm.Legend, vm.ColorApps, vm.AppNames, vm.Bins, vm.Facts));
        Kit.Wait(vm.RefreshAsync);
        Ui.Run(() =>
        {
            Assert.NotEmpty(before.Apps);
            Assert.Equal(before.Apps.Count, vm.Apps.Count);
            Assert.All(before.Apps.Zip(vm.Apps), x => Assert.Same(x.First, x.Second));
            Assert.Same(before.Insights, vm.Insights);
            Assert.Same(before.Strip, vm.Strip);
            Assert.Same(before.Downloads, vm.Downloads);
            Assert.Same(before.Legend, vm.Legend);
            Assert.Same(before.ColorApps, vm.ColorApps);
            Assert.Same(before.AppNames, vm.AppNames);
            Assert.Same(before.Bins, vm.Bins);
            Assert.Same(before.Facts, vm.Facts);

            // What does change is said by the row that was there: counting only the background gives each app new figures.
            var steam = vm.Apps.First(r => r.Name == "Steam");
            string all = steam.DownText;
            var told = new List<string?>();
            steam.PropertyChanged += (_, e) => told.Add(e.PropertyName);
            vm.BackgroundOnly = true;
            Assert.Same(steam, vm.Apps.First(r => r.Name == "Steam"));
            Assert.True(steam.BackgroundOnly);
            Assert.Contains("", told); // every figure of the row is read again
            vm.BackgroundOnly = false;
            Assert.Equal(all, steam.DownText);
        });
    }

    [Fact]
    public void Todays_drop_shows_on_the_strip()
    {
        var (vm, _) = Page();
        Ui.Run(() =>
        {
            Assert.Equal("Dropped once", vm.DropsTitle);
            Assert.StartsWith("Longest 3 min at 1:00", vm.DropsNote);
            Assert.Equal(NetQuarter.Dropped, vm.Strip[4].State);
            // Hovering a quarter hour says when it was and what happened.
            Assert.Equal("1:00 – 1:15 AM · Dropped", vm.Strip[4].Tip);
            Assert.Equal("12:00 – 12:15 AM · " + (vm.Strip[0].State == NetQuarter.Online ? "Online" : "No record"), vm.Strip[0].Tip);
            Assert.StartsWith("11:45 PM – 12:00 AM · ", vm.Strip[^1].Tip);
            if (DateTime.Now < DateTime.Today.AddHours(23.75)) Assert.EndsWith("· Still to come", vm.Strip[^1].Tip);
        });
    }

    [Fact]
    public void Background_only_leaves_out_the_app_in_front()
    {
        var (vm, _) = Page(Yesterday);
        Ui.Run(() =>
        {
            Assert.Contains(vm.Apps, a => a.Name == "Google Chrome");
            vm.BackgroundOnly = true;
            Assert.DoesNotContain(vm.Apps, a => a.Name == "Google Chrome");
            Assert.All(vm.Apps, a => Assert.True(a.BackgroundOnly));
        });
    }

    [Fact]
    public void One_app_is_open_at_a_time_and_stays_open_when_the_page_refreshes()
    {
        var (vm, _) = Page();
        Ui.Run(() =>
        {
            vm.ToggleCommand.Execute(vm.Apps[0]);
            vm.ToggleCommand.Execute(vm.Apps[1]);
            Assert.Equal([false, true], vm.Apps.Take(2).Select(a => a.IsOpen));
        });
        var open = Ui.Run(() => vm.Apps.Single(a => a.IsOpen).Name);
        Kit.Wait(vm.RefreshAsync);
        Ui.Run(() => Assert.Equal(open, vm.Apps.Single(a => a.IsOpen).Name));
    }

    [Fact]
    public void An_open_app_splits_its_use_three_ways()
    {
        var (vm, _) = Page(Yesterday);
        Ui.Run(() =>
        {
            var steam = vm.Apps.Single(a => a.Name == "Steam");
            Assert.Equal(0, steam.FrontShare);
            Assert.Equal(1, steam.BgShare + steam.AwayShare, 3);
            var chrome = vm.Apps.Single(a => a.Name == "Google Chrome");
            Assert.Equal(1, chrome.FrontShare, 3);
            Assert.Equal("Busiest hour", chrome.BusiestLabel);
        });
    }

    [Theory]
    [InlineData(ReportRange.Week, 7, "Usage each day")]
    [InlineData(ReportRange.Month, 0, "Usage each day")]
    [InlineData(ReportRange.Year, 12, "Usage each month")]
    public void Longer_periods_chart_days_or_months(ReportRange unit, int bars, string title)
    {
        var (vm, _) = Page(unit: unit);
        Ui.Run(() =>
        {
            Assert.Equal(bars == 0 ? DateTime.DaysInMonth(DateTime.Today.Year, DateTime.Today.Month) : bars, vm.Bins.Count);
            Assert.Equal(title, vm.ChartTitle);
            Assert.Empty(vm.Strip);
        });
    }

    [Fact]
    public void Live_speeds_fill_the_using_now_list()
    {
        var (vm, live) = Page();
        var tick = Fixtures.Tick();
        tick.Net = new NetLive
        {
            Time = 1000, Down = 12_000_000, Up = 300_000,
            Apps = [new() { Exe = "steam.exe", Name = "Steam", Down = 11_900_000, Up = 40_000 }, new() { Exe = "discord.exe", Name = "Discord", Down = 46_000, Up = 52_000 }],
        };
        Ui.Run(() => live.ApplyTick(tick));
        Ui.Run(() =>
        {
            Assert.True(vm.HasLive);
            Assert.Equal("11.4 MB/s", vm.DownNow);
            Assert.Equal(["Steam", "Discord"], vm.UsingNow.Select(r => r.Name));
            Assert.Equal("↓ 11.3 MB/s", vm.UsingNow[0].DownText);
            Assert.False(vm.NobodyNow);
            Assert.Single(live.NetHistory);
        });
    }

    [Fact]
    public void The_page_says_when_network_use_is_not_being_recorded()
    {
        var (vm, live) = Page();
        var tick = Fixtures.Tick();
        tick.Net = new NetLive { Time = 1000, Stalled = true };
        Ui.Run(() => live.ApplyTick(tick));
        Ui.Run(() => Assert.True(vm.Stalled));
        // Events are coming in again: the line goes.
        tick = Fixtures.Tick();
        tick.Net = new NetLive { Time = 1001, Down = 50_000 };
        Ui.Run(() => live.ApplyTick(tick));
        Ui.Run(() => Assert.False(vm.Stalled));
    }

    [Fact]
    public void Without_the_trace_the_page_says_why()
    {
        var (vm, _) = Page(new DateTime(2001, 1, 1));
        Ui.Run(() =>
        {
            Assert.False(vm.HasData);
            Assert.False(vm.HasLive);
            Assert.Empty(vm.Apps);
        });
    }

    [Fact]
    public void Hovering_the_live_chart_shows_that_seconds_speeds()
    {
        Ui.Run(() =>
        {
            long now = 1_000_000;
            var chart = new Controls.NetLiveChart
            {
                History = [.. Enumerable.Range(0, 60).Select(i => new NetLive { Time = now - 59 + i, Down = i % 7 * 300_000, Up = i % 5 * 40_000 })],
            };
            int plain = Draw.Inked(Draw.Render(chart, 600, 150));
            var hover = typeof(Controls.FanChartBase).GetProperty("HoverX", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            hover.SetValue(chart, 300.0);
            chart.InvalidateVisual();
            Assert.True(Draw.Inked(Draw.Again(chart)) > plain); // a guide, two dots and the box with the speeds
            hover.SetValue(chart, null);
            chart.InvalidateVisual();
            Assert.Equal(plain, Draw.Inked(Draw.Again(chart)));
        });
    }

    [Fact]
    public void Long_app_lists_scroll_inside_their_box()
    {
        var (vm, _) = Page(Yesterday, ReportRange.Year);
        Ui.Run(() =>
        {
            var view = new Views.NetworkView { DataContext = vm };
            var window = new System.Windows.Window
            {
                Content = view, Left = -32000, Top = -32000, Width = 1440, Height = 900, ShowActivated = false, ShowInTaskbar = false,
            };
            window.Show();
            try
            {
                Ui.Pump(150);
                var list = Visuals.Descendants<System.Windows.Controls.ItemsControl>(view).Single(i => System.Windows.Automation.AutomationProperties.GetName(i) == "Apps");
                // The apps card is as tall as the chart's card beside it, and the list scrolls inside it (the innermost
                // scrolling box around the list; the page's own is the outer one).
                var box = Visuals.Descendants<System.Windows.Controls.ScrollViewer>(view)
                    .Where(v => Visuals.Descendants<System.Windows.Controls.ItemsControl>(v).Contains(list)).MinBy(v => v.ActualHeight)!;
                var chart = Visuals.Descendants<Controls.NetBarsChart>(view).Single();
                Assert.True(box.ActualHeight > 100);
                Assert.True(box.ActualHeight < chart.ActualHeight + 80);
                double before = box.ActualHeight;
                var first = vm.Apps[0];
                for (int i = 0; i < 20; i++) vm.Apps.Add(first); // a PC with many more apps than fit
                Ui.Pump(150);
                Assert.Equal(before, box.ActualHeight, 1);
                Assert.True(box.ScrollableHeight > 0);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void The_page_renders_its_charts()
    {
        var (vm, _) = Page(Yesterday);
        Ui.Run(() =>
        {
            var view = new Views.NetworkView { DataContext = vm };
            var window = new System.Windows.Window
            {
                Content = view, Left = -32000, Top = -32000, Width = 1440, Height = 900, ShowActivated = false, ShowInTaskbar = false,
            };
            window.Show();
            try
            {
                Ui.Pump(150);
                var chart = Visuals.Descendants<Controls.NetBarsChart>(view).Single();
                Assert.True(chart.ActualWidth > 600);
                // The live chart is there, behind the "Right now" line until it's opened.
                var live = Assert.Single(Visuals.Descendants<Controls.NetLiveChart>(view));
                Assert.False(live.IsVisible);
                vm.ToggleLiveCommand.Execute(null);
                Ui.Pump(100);
                Assert.True(live.IsVisible);
                vm.ToggleCommand.Execute(vm.Apps[0]);
                Ui.Pump(100);
                Assert.Single(Visuals.Descendants<Controls.ShareBar>(view), b => b.IsVisible);
            }
            finally { window.Close(); }
        });
    }
}
