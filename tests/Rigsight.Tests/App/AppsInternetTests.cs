using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Rigsight.Core;
using Rigsight.Core.Reports;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;
using Rigsight.Views;

namespace Rigsight.Tests.App;

/// <summary>
/// The Apps page's internet use (the "Most internet use" order and the Internet group) beside the Network page's figures
/// for the same period, and the selected app's three groups, on the run's generated history (read only).
/// </summary>
[Collection("UI")]
public sealed class AppsInternetTests
{
    private static (AppsViewModel Vm, ReportService Reports) Apps()
    {
        SharedData.EnsureSeeded();
        var (settings, _) = Kit.Greeted();
        var reports = new ReportService(settings);
        var vm = Ui.Run(() => new AppsViewModel(reports, settings));
        Kit.Wait(() => vm.LoadAsync());
        return (vm, reports);
    }

    /// <summary>Today on the Network page: every app it lists, with what it moved.</summary>
    private static List<NetAppStat> NetworkToday(ReportService reports)
    {
        NetReport? report = null;
        Kit.Wait(async () => report = (await reports.NetworkAsync(ReportRange.Day, DateTime.Today)).Report);
        Assert.NotNull(report);
        Assert.NotEmpty(report.Apps);
        return report.Apps;
    }

    [Fact]
    public void Most_internet_use_lists_the_Network_pages_apps_biggest_first_then_the_rest_as_None()
    {
        var (vm, reports) = Apps();
        var net = NetworkToday(reports);
        Ui.Run(() =>
        {
            vm.Sort = "Internet";
            var rows = vm.Apps.ToList();
            // Every app the Network page lists today, in its order and with its figure, before any that moved nothing.
            Assert.Equal(net.Select(a => a.Id), rows.Take(net.Count).Select(r => r.Stat.Id));
            Assert.Equal(net.Select(a => Units.Data(a.Use.Total)), rows.Take(net.Count).Select(r => r.Metric));
            Assert.Equal(100, rows[0].Bar, 6);
            Assert.All(rows.Skip(net.Count), r => Assert.Equal("None", r.Metric));
            Assert.All(rows.Skip(net.Count), r => Assert.Equal(0, r.Bar));
            Assert.True(rows.Count > net.Count, "the seeded apps open a while without internet use should be listed as None");
        });
    }

    [Fact]
    public void The_internet_group_has_the_Network_pages_figures_and_None_when_the_app_moved_nothing()
    {
        var (vm, reports) = Apps();
        var net = NetworkToday(reports);
        Ui.Run(() =>
        {
            vm.Sort = "Internet";
            foreach (var a in net)
            {
                vm.SelectedRow = vm.Apps.Single(r => r.Stat.Id == a.Id);
                Assert.Equal(Units.Data(a.Use.Total), vm.InternetMain);
                Assert.Equal("used", vm.InternetWord);
                Assert.Equal(Units.Data(a.Use.Down), vm.InternetDown);
                Assert.Equal(Units.Data(a.Use.Up), vm.InternetUp);
                // The Network page's own share for the same app.
                int percent = (int)Math.Round(100.0 * a.Use.Background / a.Use.Total);
                Assert.Equal($"{percent}%", vm.InternetBackground);
            }

            vm.SelectedRow = vm.Apps.First(r => r.Metric == "None");
            Assert.Equal("None", vm.InternetMain);
            Assert.Equal("", vm.InternetWord);
            Assert.Equal(("—", "—", "—"), (vm.InternetDown, vm.InternetUp, vm.InternetBackground));
        });
    }

    [Fact]
    public void A_period_with_no_internet_recorded_says_so_rather_than_None()
    {
        // The generated history has internet use only for yesterday and today.
        var (vm, _) = Apps();
        Ui.Run(() => vm.Anchor = DateTime.Today.AddDays(-5));
        Assert.True(Ui.WaitFor(() => !vm.IsLoading && vm.Selected is not null && vm.Apps.Count > 0, 15_000), "the day didn't load");
        Ui.Run(() =>
        {
            vm.Sort = "Internet";
            Assert.All(vm.Apps, r => Assert.Equal("Not recorded", r.Metric));
            Assert.Equal("Not recorded", vm.InternetMain);
            Assert.Equal(("", "—", "—", "—"), (vm.InternetWord, vm.InternetDown, vm.InternetUp, vm.InternetBackground));
        });
    }

    [Fact]
    public void Other_sorts_keep_to_apps_with_time_of_their_own()
    {
        var (vm, reports) = Apps();
        NetworkToday(reports);
        Ui.Run(() =>
        {
            vm.Sort = "Active";
            Assert.All(vm.Apps, r => Assert.True(r.Stat.OpenSec >= 30));
            vm.Sort = "Memory";
            int memory = vm.Apps.Count;
            vm.Sort = "Internet";
            vm.Sort = "Memory";
            Assert.Equal(memory, vm.Apps.Count);
        });
    }

    private static string Text(TextBlock t) => t.Inlines.Count > 0 ? string.Concat(t.Inlines.OfType<Run>().Select(r => r.Text)) : t.Text;

    [Fact]
    public void The_selected_app_shows_three_groups_with_every_figure()
    {
        var (vm, reports) = Apps();
        var net = NetworkToday(reports);
        Ui.TakeProblems();
        Ui.Run(() =>
        {
            // The biggest user may be an app with no window of its own, which only this order lists.
            vm.Sort = "Internet";
            vm.SelectedRow = vm.Apps.Single(r => r.Stat.Id == net[0].Id);
            var a = vm.Selected!;
            var view = new AppsView { DataContext = vm };
            view.Measure(new Size(1500, 1000));
            view.Arrange(new Rect(0, 0, 1500, 1000));
            view.UpdateLayout();

            var groups = Visuals.Descendants<GroupRow>(view).Single();
            Assert.Equal(3, groups.Children.Count);
            Assert.False(groups.Stacks(groups.ActualWidth), $"the groups should sit side by side at {groups.ActualWidth:0} px");
            // Side by side, the three boxes line up: the same width, the same height, one row.
            var boxes = groups.Children.OfType<FrameworkElement>().ToList();
            Assert.Single(boxes.Select(b => Math.Round(b.ActualHeight)).Distinct());
            Assert.Single(boxes.Select(b => Math.Round(b.ActualWidth)).Distinct());

            List<string> Texts(FrameworkElement box) => [.. Visuals.Descendants<TextBlock>(box).Select(Text)];
            var time = Texts(boxes[0]);
            var hardware = Texts(boxes[1]);
            var internet = Texts(boxes[2]);

            Assert.Equal("TIME", time[0]);
            Assert.Equal($"{Units.Duration(a.ActiveSec)} active", time[1]);
            Assert.Equal(["In the background", "Minimized", "Longest session", "Sessions"], time.Where((_, i) => i >= 2 && i % 2 == 1));
            Assert.Contains(Units.Duration(a.BackgroundSec), time);
            Assert.Contains(Units.Duration(a.MinimizedSec), time);
            Assert.Contains(a.SessionCount.ToString(), time);

            Assert.Equal("HARDWARE while it was in front", hardware[0]);
            Assert.Equal($"{Units.TempShort(a.CpuTempMax)} · {Units.TempShort(a.GpuTempMax)} CPU · GPU peak", hardware[1]);
            Assert.Equal(["CPU temperature", "GPU temperature", "CPU use", "Memory"], hardware.Where((_, i) => i >= 2 && i % 2 == 1));
            Assert.Contains($"{Units.TempShort(a.CpuTempAvg)} average", hardware);
            Assert.Contains($"{Units.TempShort(a.GpuTempAvg)} average", hardware);
            Assert.Contains(a.CpuAvg is double c ? $"{c:0.0}% average" : "— average", hardware);
            Assert.Contains($"{(a.MemMax is double m ? Units.Megabytes(m) : "—")} peak", hardware);

            Assert.Equal("INTERNET", internet[0]);
            Assert.Equal($"{Units.Data(net[0].Use.Total)} used", internet[1]);
            Assert.Equal(["Downloaded", "Uploaded", "In the background"], internet.Where((_, i) => i >= 2 && i % 2 == 1));
            Assert.Contains(Units.Data(net[0].Use.Down), internet);

            // No "AVG" anywhere on the page any more.
            Assert.DoesNotContain(Visuals.Descendants<TextBlock>(view).Select(Text), t => t.Contains("AVG", StringComparison.Ordinal));
        });
        Ui.AssertNoProblems("apps page groups");
    }

    [Fact]
    public void The_groups_stack_on_a_narrow_window()
    {
        Ui.Run(() =>
        {
            var row = new GroupRow();
            for (int i = 0; i < 3; i++) row.Children.Add(new Border { Height = 40 + i * 10 });
            Assert.True(row.Stacks(500));
            Assert.False(row.Stacks(900));
            row.Measure(new Size(500, double.PositiveInfinity));
            row.Arrange(new Rect(0, 0, 500, row.DesiredSize.Height));
            Assert.Equal(40 + 50 + 60 + 2 * row.Gap, row.DesiredSize.Height);
            Assert.All(row.Children.OfType<Border>(), b => Assert.Equal(500, b.ActualWidth));
            row.Measure(new Size(900, double.PositiveInfinity));
            row.Arrange(new Rect(0, 0, 900, row.DesiredSize.Height));
            Assert.Equal(60, row.DesiredSize.Height);
        });
    }
}
