using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// The Network page on the seeded history: Chrome a trickle in front, Steam downloading in big bursts in the background
/// (one a big download each afternoon), Discord sending as much as it takes in, and a drop early today. Since 1.3.0: Right
/// now always open (speeds, the line, every app), one line of figures, the graph with its Usage and Ping views and drag
/// to zoom, the apps (each opening on the Apps page) and "What happened".
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
    public void A_day_shows_its_figures_its_apps_and_its_hours()
    {
        var (vm, _) = Page(Yesterday);
        Ui.Run(() =>
        {
            Assert.True(vm.HasData);
            Assert.NotEqual("—", vm.DownText);
            Assert.Equal("Steam", vm.Apps[0].Name);
            Assert.Equal(100, vm.Apps[0].DownBar + vm.Apps[0].UpBar, 6); // the biggest app fills the bar
            Assert.False(vm.Apps[0].HasSpark); // a day has no day-by-day chart
            Assert.Equal(24, vm.Bins.Count);
            Assert.Equal("Each hour", vm.ChartTitle);
            // The line of figures: everything used, both ways, the share in the background, the drops.
            Assert.NotEqual("—", vm.TotalText);
            Assert.EndsWith("%", vm.BackgroundText);
            Assert.Equal(("No", "drops"), (vm.DropsCount, vm.DropsWord));
            Assert.Equal("No drops", vm.DropsTitle);
            // What happened: the day's insights as a summary on top, no time; the lines under it with no day heading
            // (the period picker already says which day).
            Assert.True(vm.HasSummary);
            Assert.All(vm.Summary, s => Assert.Equal("", s.When));
            Assert.DoesNotContain(vm.Happenings, h => h.IsHeader);
            // No line check in the seeded history: no ping figure, and the Ping view says why in place of a graph.
            Assert.False(vm.HasPing);
            vm.View = "Ping";
            Assert.True(vm.ShowPingNote);
            Assert.Equal("", vm.LineText);
            Assert.Equal("", vm.ChartHint);
            Assert.Empty(vm.Legend);
            Assert.StartsWith("Ping wasn't checked", vm.PingNoteTitle);
            Assert.Equal("Ping hasn't been checked on this PC yet.", vm.PingNoteBody);
            Assert.False(vm.PingNoteCollecting);
            // The status line under it: what's being done, and Pause (asking first).
            Assert.Equal("Checking every 5 seconds, to your internet provider's first router", vm.PingStatusText);
            Assert.Equal("Pause", vm.PingStatusAction);
            vm.PingStatusCommand.Execute(null);
            Assert.True(vm.ConfirmStopOpen);
            vm.CancelStopPingCommand.Execute(null);
            Assert.False(vm.ConfirmStopOpen);
            vm.View = "Usage";
            Assert.Equal("", vm.LineText);
            Assert.Equal("Drag across to zoom in · click an hour for its apps", vm.ChartHint);
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
    public void Time_the_PC_was_on_with_nothing_written_is_said_with_the_drops()
    {
        // 5 Oct 2026: online till 1 PM, nothing written till 8:30 PM, online again; off before 8 and after 10.
        var r = DayReport([(8, 13), (20.5, 22)], unrecorded: [(13, 20.5)]);
        // It isn't "No drops" for the whole day: the hover says how much wasn't recorded.
        Assert.Equal(("No drops", "7h 30m not recorded"), NetworkViewModel.DropsText(r, "that day"));
        r = DayReport([(8, 13.25)], unrecorded: [(13.1, 20.5)], drops: [(14, 14.25)]);
        Assert.Equal(("Dropped once", "Longest 15m at 2:00 PM · 7h 24m not recorded"), NetworkViewModel.DropsText(r, "that day"));
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
            Assert.False(vm.Apps[0].HasSpark);
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
            vm.Sort = "Total";
            Assert.False(vm.BackgroundOnly);
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
            // In front / background: two pieces, the same bar.
            chart.Mode = Controls.NetBarsMode.Background;
            pieces = chart.Pieces(bin);
            Assert.Equal(2, pieces.Count);
            Assert.Equal(bin.Total, pieces.Sum(x => x.Bytes));
            Assert.Equal(Math.Min(bin.Background, bin.Total), pieces[1].Bytes);
            Assert.Contains(chart.HoverLines(bin), l => l.StartsWith("In the background "));
            Assert.Equal(["Nothing"], chart.HoverLines(new NetBin(bin.Start, 0, 0, 0)));
            // An hour that wasn't recorded says so: alone when it is empty (it isn't "nothing"), under the total when part of it was.
            Assert.Equal(["Not recorded for 1h 00m"], chart.HoverLines(new NetBin(bin.Start, 0, 0, 0) { UnrecordedMinutes = 60 }));
            Assert.Equal("Not recorded for 30m", chart.HoverLines(bin with { UnrecordedMinutes = 30 })[1]);
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

            // The legend names what the colours are; the Ping view has none while its note shows in place of a graph
            // (no line check in the seeded history).
            Assert.Contains(vm.Legend, l => l.Text == "Every other app");
            Assert.Equal("Internet up", vm.Legend[^1].Text); // the internet strip's colour, last
            Assert.Contains(vm.Legend, l => l.Text == "Steam");
            vm.View = "Ping";
            Assert.True(vm.ShowPingNote);
            Assert.Empty(vm.Legend);
            vm.View = "Usage";
        });
    }

    [Fact]
    public void The_line_right_now_is_said_in_a_word()
    {
        LineLive Line(int? isp, double lost = 0, int? lan = 1) => new() { IspMs = isp, LanMs = lan, LostPercent = lost, ProviderFound = isp is not null };
        Assert.Equal(LineState.Checking, NetworkViewModel.LineNow(null, true, null).Item1);
        Assert.Equal((LineState.Good, "Working well"), NetworkViewModel.LineNow(Line(19), true, 21));
        Assert.Equal((LineState.Slow, "Slow right now"), NetworkViewModel.LineNow(Line(95), true, 21));
        // A line that is never faster (satellite) isn't slow at its own usual.
        Assert.Equal(LineState.Good, NetworkViewModel.LineNow(Line(600), true, 600).Item1);
        Assert.Equal((LineState.Losing, "Losing data"), NetworkViewModel.LineNow(Line(19, lost: 8), true, 21));
        Assert.Equal((LineState.Offline, "No internet"), NetworkViewModel.LineNow(Line(19), false, 21));
        // Without the provider's router, the router's ping is the line's.
        Assert.Equal(LineState.Good, NetworkViewModel.LineNow(Line(null, lan: 2), null, null).Item1);
        // Ping checks off: Windows seeing the internet is all there is.
        Assert.Equal((LineState.Good, "Working well"), NetworkViewModel.LineNow(null, true, 21, checksOn: false));
        Assert.Equal(LineState.Offline, NetworkViewModel.LineNow(null, false, 21, checksOn: false).Item1);

        var (vm, live) = Page(Yesterday);
        var tick = Fixtures.Tick();
        tick.Line = new LineLive { LanMs = 1, IspMs = 19, Jitter = 3, LostPercent = 0, ProviderFound = true };
        Ui.Run(() => live.ApplyTick(tick));
        Ui.Run(() =>
        {
            Assert.Equal(("19 ms", "3 ms", "0%"), (vm.PingNow, vm.JitterNow, vm.LostNow));
            Assert.Equal("To your internet provider's first router", vm.PingTip);
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
        var before = Ui.Run(() => (Apps: vm.Apps.ToList(), Happenings: vm.Happenings.ToList(), vm.Legend, vm.ColorApps, vm.AppNames, vm.Bins, vm.Drops));
        Kit.Wait(vm.RefreshAsync);
        Ui.Run(() =>
        {
            Assert.NotEmpty(before.Apps);
            Assert.Equal(before.Apps.Count, vm.Apps.Count);
            Assert.All(before.Apps.Zip(vm.Apps), x => Assert.Same(x.First, x.Second));
            Assert.All(before.Happenings.Zip(vm.Happenings), x => Assert.Same(x.First, x.Second));
            Assert.Same(before.Legend, vm.Legend);
            Assert.Same(before.ColorApps, vm.ColorApps);
            Assert.Same(before.AppNames, vm.AppNames);
            Assert.Same(before.Bins, vm.Bins);
            Assert.Same(before.Drops, vm.Drops);

            // What does change is said by the row that was there: counting only the background gives each app new figures.
            var steam = vm.Apps.First(r => r.Name == "Steam");
            string all = steam.DownText;
            var told = new List<string?>();
            steam.PropertyChanged += (_, e) => told.Add(e.PropertyName);
            vm.Sort = "Background";
            Assert.Same(steam, vm.Apps.First(r => r.Name == "Steam"));
            Assert.True(steam.BackgroundOnly);
            Assert.Contains("", told); // every figure of the row is read again
            vm.Sort = "Total";
            Assert.Equal(all, steam.DownText);
        });
    }

    [Fact]
    public void Todays_drop_is_counted_drawn_on_the_graph_and_said_in_its_hour()
    {
        var (vm, _) = Page();
        Ui.Run(() =>
        {
            Assert.Equal(("1", "drop"), (vm.DropsCount, vm.DropsWord));
            Assert.Equal("Dropped once", vm.DropsTitle);
            Assert.StartsWith("Longest 3m at 1:00", vm.DropsTip);
            var drop = Assert.Single(vm.Drops);
            Assert.Equal(DateTime.Today.AddHours(1), drop.From);
            // The 1 AM hour's hover box says it, in the one warning colour of the box.
            Assert.Equal("Your internet was down for 3 minutes, from 1:00 AM", vm.BuildHover(1).Drop);
            Assert.Null(vm.BuildHover(5).Drop);
        });
    }

    [Fact]
    public void Background_only_leaves_out_the_app_in_front()
    {
        var (vm, _) = Page(Yesterday);
        Ui.Run(() =>
        {
            Assert.Contains(vm.Apps, a => a.Name == "Google Chrome");
            vm.Sort = "Background";
            Assert.DoesNotContain(vm.Apps, a => a.Name == "Google Chrome");
            Assert.All(vm.Apps, a => Assert.True(a.BackgroundOnly));
        });
    }

    [Fact]
    public void An_app_row_opens_that_app_on_the_apps_page_for_the_same_period()
    {
        var (vm, _) = Page(Yesterday, ReportRange.Week);
        Ui.Run(() =>
        {
            (string Exe, ReportRange Unit, DateTime Anchor)? asked = null;
            vm.OpenAppRequested += (exe, unit, anchor) => asked = (exe, unit, anchor);
            var steam = vm.Apps.First(a => a.Name == "Steam");
            vm.OpenAppCommand.Execute(steam);
            Assert.Equal((steam.Stat.Exe, ReportRange.Week, Yesterday), asked);
            // A week's rows have its days, each hovered for its day and amount.
            Assert.Equal(7, steam.Spark.Count);
            Assert.Contains(steam.Spark, b => b.Height == 1);
            Assert.All(steam.Spark, b => Assert.Contains(" · ", b.Tip));
        });
    }

    [Fact]
    public void The_hover_box_says_the_hour_its_total_and_each_part_of_the_bar()
    {
        var (vm, _) = Page(Yesterday);
        Ui.Run(() =>
        {
            var box = vm.BuildHover(14);
            Assert.Equal("2 PM – 3 PM", box.Title);
            Assert.Equal(Rigsight.Core.Units.Data(vm.Bins[14].Total), box.Total);
            Assert.StartsWith("↓ ", box.Split);
            Assert.Equal("Steam", box.Rows[0].Name);
            Assert.Contains(box.Rows[0].BrushKey, Controls.NetBarsChart.AppBrushKeys);
            Assert.True(box.Rows.Count <= NetworkViewModel.HoverApps + 1);
            Assert.False(box.HasPing); // no line check in the seeded history
            Assert.Equal("Click to see this hour's apps · drag across to zoom in", box.Footer);
            vm.SelectedBin = 14;
            Assert.Equal("Click again to let go of this hour · drag across to zoom in", vm.BuildHover(14).Footer);
            vm.SelectedBin = -1;
            // The Ping view's box: an hour with no check says so.
            vm.View = "Ping";
            var ping = vm.BuildHover(14);
            Assert.True(ping.IsPing);
            Assert.False(ping.HasPingFigures);
            Assert.Equal("Ping wasn't checked", ping.Empty);
            vm.View = "Usage";
            // An empty hour says so, and nothing else.
            int empty = vm.Bins.ToList().FindIndex(b => b.Total == 0 && b.UnrecordedMinutes < Controls.NetBarsChart.MinUnrecorded);
            // (or, with nothing recorded in it at all, why: "PC was off", read from Windows' own log)
            if (empty >= 0) Assert.Equal(vm.Bins[empty].OffWhy ?? "No internet use", vm.BuildHover(empty).Empty);
            // Pointing at a bar opens it; away, it goes.
            vm.HoverIndex = 14;
            Assert.True(vm.HoverOpen);
            vm.HoverIndex = -1;
            Assert.False(vm.HoverOpen);
        });
    }

    [Fact]
    public void Zooming_into_a_stretch_lists_its_apps_and_the_button_zooms_back_out()
    {
        var (vm, _) = Page(Yesterday);
        Ui.Run(() =>
        {
            var whole = vm.Apps.Select(a => a.Amount).ToList();
            vm.Zoomed = new Controls.NetDropSpan(Yesterday.AddHours(13), Yesterday.AddHours(16));
            Assert.True(vm.IsZoomed);
            Assert.StartsWith("Apps · ", vm.AppsTitle);
            Assert.NotEqual("", vm.ZoomText);
            Assert.Equal("Whole day", vm.ResetText);
            long steam = vm.Apps.First(a => a.Name == "Steam").Stat.Id;
            long inStretch = vm.Bins.Skip(13).Take(3).SelectMany(b => b.Apps).Where(u => u.App == steam).Sum(u => u.Total);
            Assert.Equal(Rigsight.Core.Units.Data(inStretch), vm.Apps.First(a => a.Name == "Steam").Amount);
            vm.ResetZoomCommand.Execute(null);
            Assert.False(vm.IsZoomed);
            Assert.Equal(whole, vm.Apps.Select(a => a.Amount).ToList());
            // Another period lets go of the zoom.
            vm.Zoomed = new Controls.NetDropSpan(Yesterday.AddHours(13), Yesterday.AddHours(16));
            vm.Unit = ReportRange.Week;
            Assert.Null(vm.Zoomed);
        });
    }

    [Theory]
    [InlineData(ReportRange.Week, 168, "Each hour")]
    [InlineData(ReportRange.Month, 0, "Each day")]
    [InlineData(ReportRange.Year, 12, "Each month")]
    public void A_week_charts_its_hours_a_month_its_days_a_year_its_months(ReportRange unit, int bars, string title)
    {
        var (vm, _) = Page(unit: unit);
        Ui.Run(() =>
        {
            Assert.Equal(bars == 0 ? DateTime.DaysInMonth(DateTime.Today.Year, DateTime.Today.Month) : bars, vm.Bins.Count);
            Assert.Equal(title, vm.ChartTitle);
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
            Assert.Equal("11.3 MB/s", vm.UsingNow[0].DownText);
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
                // The apps card is as tall as What happened beside it, and the list scrolls inside it (the innermost
                // scrolling box around the list; the page's own is the outer one).
                var box = Visuals.Descendants<System.Windows.Controls.ScrollViewer>(view)
                    .Where(v => Visuals.Descendants<System.Windows.Controls.ItemsControl>(v).Contains(list)).MinBy(v => v.ActualHeight)!;
                Assert.True(box.ActualHeight > 100);
                Assert.True(box.ActualHeight < Views.NetworkView.ListHeight);
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
                Assert.True(chart.CanZoom);
                // Right now is always open: its minute's chart shows from the start.
                var live = Assert.Single(Visuals.Descendants<Controls.NetLiveChart>(view));
                Assert.True(live.IsVisible);
                // The Ping view draws on the same graph.
                vm.View = "Ping";
                Ui.Pump(100);
                Assert.Equal(Controls.NetChartView.Ping, chart.View);
            }
            finally { window.Close(); }
        });
    }
}
