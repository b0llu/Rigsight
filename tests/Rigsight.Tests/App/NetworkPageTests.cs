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
            Assert.Equal("Each hour", vm.ChartTitle);
            var download = Assert.Single(vm.Downloads);
            Assert.Equal("Steam", download.Name);
            Assert.Equal("20 min at 11.8 MB/s", download.How);
            Assert.StartsWith("11.8 MB/s", vm.SpeedText);
            Assert.Contains("Steam", vm.SpeedNote);
            Assert.Equal("No drops", vm.DropsTitle);
            Assert.Equal(96, vm.Strip.Count);
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
    [InlineData(ReportRange.Week, 7, "Each day")]
    [InlineData(ReportRange.Month, 0, "Each day")]
    [InlineData(ReportRange.Year, 12, "Each month")]
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
                var box = Visuals.Descendants<System.Windows.Controls.ScrollViewer>(view).Single(v => Visuals.Descendants<System.Windows.Controls.ItemsControl>(v).Contains(list) && v.MaxHeight == Views.NetworkView.AppsMaxHeight);
                Assert.True(box.ActualHeight <= Views.NetworkView.AppsMaxHeight);
                var first = vm.Apps[0];
                for (int i = 0; i < 20; i++) vm.Apps.Add(first); // a PC with many more apps than fit
                Ui.Pump(150);
                Assert.Equal(Views.NetworkView.AppsMaxHeight, box.ActualHeight, 1);
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
                Assert.Single(Visuals.Descendants<Controls.NetLiveChart>(view));
                vm.ToggleCommand.Execute(vm.Apps[0]);
                Ui.Pump(100);
                Assert.Single(Visuals.Descendants<Controls.ShareBar>(view), b => b.IsVisible);
            }
            finally { window.Close(); }
        });
    }
}
