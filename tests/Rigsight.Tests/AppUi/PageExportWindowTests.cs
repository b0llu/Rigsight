using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.AppUi;

/// <summary>
/// Export on each page, in the real window: every page with history has an Export button in its header that opens its
/// own box (only its things, its periods, set to what it shows), the live pages have none, a full dashboard's list
/// scrolls, and exporting writes the page's files.
/// </summary>
[Collection("UI")]
public sealed class PageExportWindowTests(AppHost host) : IClassFixture<AppHost>
{
    private static readonly string Shots = Path.Combine(TestEnvironment.DataDir, "screens");

    public static TheoryData<string> Pages => [.. AppHost.Pages.Where(PageExports.Has)];

    private Button? ExportButton() => Ui.Run(() => Visuals.Descendants<Button>(host.Window.PageHost)
        .FirstOrDefault(b => AutomationProperties.GetName(b) == "Export this page" && b.IsVisible));

    [Theory]
    [MemberData(nameof(Pages))]
    public void Each_page_opens_its_own_box_set_to_what_it_shows(string page)
    {
        Ui.TakeProblems();
        host.Show(page, 900);
        var button = ExportButton();
        Assert.NotNull(button);
        Ui.Run(() => button!.Command.Execute(null));
        Assert.True(Ui.WaitFor(() => host.Shell.PageExport.IsOpen && host.Shell.PageExport.Summary.Length > 0));
        Ui.Pump(300);
        Ui.Run(() =>
        {
            var box = host.Shell.PageExport;
            Assert.StartsWith("Export", box.Title);
            Assert.InRange(box.Items.Count, 1, 8);
            Assert.Contains(box.Items, i => i.IsOn);
            Assert.Single(box.Periods, p => p.IsOn);
            Assert.False(string.IsNullOrWhiteSpace(box.PeriodText));
            Assert.All(box.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.About)));
            Ui.SavePng(host.Window, Path.Combine(Shots, $"export-{page.Replace(':', '_')}.png"));
            box.CloseCommand.Execute(null);
            Assert.False(box.IsOpen);
        });
        Ui.AssertNoProblems($"{page}'s Export box");
    }

    [Theory]
    [InlineData("processes")]
    [InlineData("sensors")]
    public void The_live_pages_have_no_export(string page)
    {
        host.Show(page, 600);
        Assert.False(PageExports.Has(page));
        Assert.Null(ExportButton());
    }

    [Fact]
    public void A_dashboard_with_every_tile_has_one_line_a_kind_and_its_list_scrolls()
    {
        Ui.TakeProblems();
        host.Show("custom:" + AppHost.DashboardId, 900);
        Ui.Run(() => host.Shell.ExportPageCommand.Execute(null));
        Ui.Pump(400);
        Ui.Run(() =>
        {
            var box = host.Shell.PageExport;
            Assert.Equal(["Readings each minute", "Fans", "Memory by app", "Drives", "Internet", "Your day", "What stood out", "Crashes"], box.Items.Select(i => i.Name));
            Assert.True(box.ManyItems);
            Assert.Equal("8 of 8 picked", box.PickedText);
            box.PickAllCommand.Execute(null);
            Assert.Equal("0 of 8 picked", box.PickedText);
            Assert.False(box.CanExport);
            box.PickAllCommand.Execute(null);
            // The list keeps its own height: the box never fills the window.
            var layer = (FrameworkElement)host.Window.FindName("PageExportLayer");
            var scroller = Visuals.Descendants<ScrollViewer>(layer).First(s => s.MaxHeight < 1000);
            Assert.True(scroller.ActualHeight <= scroller.MaxHeight);
            Assert.True(scroller.ScrollableHeight > 0);
            Ui.SavePng(host.Window, Path.Combine(Shots, "export-dashboard-full.png"));
            box.CloseCommand.Execute(null);
        });
        Ui.AssertNoProblems("the dashboard's Export box");
    }

    [Fact]
    public void Nothing_can_be_exported_with_no_reading_picked()
    {
        host.Show("temperatures", 900);
        Ui.Run(() =>
        {
            host.Shell.ExportPageCommand.Execute(null);
            var box = host.Shell.PageExport;
            var readings = box.Scopes.Single();
            foreach (var o in readings.Options) o.IsOn = false;
            Assert.False(box.CanExport);
            readings.Options[0].IsOn = true;
            Assert.True(box.CanExport);
            box.CloseCommand.Execute(null);
        });
    }

    [Fact]
    public void The_crashes_exported_for_the_pages_period_are_the_ones_its_list_shows()
    {
        host.Show("crashes", 1500);
        Assert.True(Ui.WaitFor(() => host.Shell.Crashes.Loaded));
        foreach (var filter in new[] { "All", "Apps", "Pc" })
        {
            Ui.Run(() => host.Shell.Crashes.Filter = filter);
            Ui.Pump(300);
            var (listed, from, to) = Ui.Run(() =>
            {
                var vm = host.Shell.Crashes;
                var bounds = vm.Unit == Rigsight.Core.Reports.ReportRange.All ? (new DateTime(2000, 1, 1), DateTime.Today.AddDays(1))
                    : Controls.PeriodPicker.Bounds(vm.Unit, vm.Anchor, vm.CustomFrom, vm.CustomTo);
                return (vm.Crashes.Select(r => r.Event.Id).ToHashSet(), bounds.Item1, bounds.Item2);
            });
            var ids = Ui.Run(() => CrashesViewModel.ShownIdsAsync(host.Shell.Reports, host.Shell.Settings.Current, from, to, filter == "All" ? "All" : filter, [], host.Shell.Crashes.Grouped, false));
            Assert.True(Ui.WaitFor(() => ids.IsCompleted, 20_000));
            Assert.Equal(listed.Order(), ids.Result.Order());
        }
        Ui.Run(() => host.Shell.Crashes.Filter = "All");
    }

    [Fact]
    public void The_fans_summary_is_for_the_period_picked_in_the_box()
    {
        string parent = TestEnvironment.NewFolder("fans-export");
        var shown = new List<string>();
        Ui.Run(() =>
        {
            host.Shell.Settings.Update(s => { s.ExportFolder = parent; s.ExportAsCsv = false; });
            host.Shell.PageExport.ShowFolder = shown.Add;
        });
        try
        {
            host.Show("fans", 1500);
            Ui.Run(() =>
            {
                host.Shell.ExportPageCommand.Execute(null);
                var box = host.Shell.PageExport;
                foreach (var i in box.Items) i.IsOn = i.Name == "Summary";
                box.PickPeriod(box.Periods.Single(p => p.Unit == Rigsight.Core.Reports.ReportRange.Week));
            });
            Ui.Run(() => host.Shell.PageExport.ExportCommand.ExecuteAsync(null));
            Assert.True(Ui.WaitFor(() => !host.Shell.PageExport.IsOpen, 30_000), host.Shell.PageExport.Error);
            var text = File.ReadAllLines(Path.Combine(Assert.Single(shown), "Fans summary.txt"));
            var (from, to) = Rigsight.Core.Reports.ReportBuilder.Bounds(Rigsight.Core.Reports.ReportRange.Week, DateTime.Today);
            Assert.StartsWith($"{from:yyyy-MM-dd} to {to.AddDays(-1):yyyy-MM-dd}", text[2]);
            Assert.Contains(text, l => l.StartsWith("Quiet time", StringComparison.Ordinal));
        }
        finally
        {
            Ui.Run(() => host.Shell.Settings.Update(s => s.ExportFolder = null));
        }
    }

    [Fact]
    public void Exporting_from_a_page_writes_its_files_narrowed_the_way_it_is()
    {
        string parent = TestEnvironment.NewFolder("page-export");
        var shown = new List<string>();
        Ui.Run(() =>
        {
            host.Shell.Settings.Update(s => s.ExportFolder = parent);
            host.Shell.PageExport.ShowFolder = shown.Add;
        });
        try
        {
            // Apps, on the app picked: its usage and sessions, that app's rows only.
            host.Show("apps", 1200);
            Assert.True(Ui.WaitFor(() => host.Shell.Apps.Selected is not null));
            string exe = Ui.Run(() => host.Shell.Apps.Selected!.Exe);
            Ui.Run(() =>
            {
                host.Shell.ExportPageCommand.Execute(null);
                var box = host.Shell.PageExport;
                box.AsCsv = true;
                // All time, so the period surely has the app in it.
                box.PickPeriod(box.Periods.First(p => p.Unit == Rigsight.Core.Reports.ReportRange.All));
                // The app the test picked, not the one the page shows first by itself.
                box.Scopes.Single().Options.Single(o => o.Value as string == exe).IsOn = true;
                Assert.Equal(exe, box.Scopes.Single().Picked.Single());
            });
            Ui.Run(() => host.Shell.PageExport.ExportCommand.ExecuteAsync(null));
            Assert.True(Ui.WaitFor(() => !host.Shell.PageExport.IsOpen, 20_000), host.Shell.PageExport.Error);
            string folder = Assert.Single(shown);
            var usage = File.ReadAllLines(Path.Combine(folder, "App usage.csv"));
            Assert.True(usage.Length > 1);
            Assert.All(usage.Skip(1), l => Assert.Contains($",{exe},", l, StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(Path.Combine(folder, "Sessions.csv")));
            Assert.False(File.Exists(Path.Combine(folder, "Internet.csv")));

            // Games: the library and its plays, files of their own.
            host.Show("games", 1500);
            Ui.Run(() => host.Shell.ExportPageCommand.Execute(null));
            Ui.Run(() => host.Shell.PageExport.ExportCommand.ExecuteAsync(null));
            Assert.True(Ui.WaitFor(() => !host.Shell.PageExport.IsOpen, 30_000), host.Shell.PageExport.Error);
            Assert.True(File.Exists(Path.Combine(folder, "Games library.csv")));
            Assert.True(File.ReadAllLines(Path.Combine(folder, "Play sessions.csv")).Length > 1);
        }
        finally
        {
            Ui.Run(() =>
            {
                host.Shell.PageExport.CloseCommand.Execute(null);
                host.Shell.Settings.Update(s => { s.ExportFolder = null; s.ExportAsCsv = false; });
            });
        }
    }
}
