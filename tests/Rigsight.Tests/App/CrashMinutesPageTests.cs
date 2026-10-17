using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Rigsight.Controls;
using Rigsight.Core.Stability;
using Rigsight.Models;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// An opened crash's minutes before it, on made-up history: lanes and strips for what was recorded, a mark for the
/// crash, what stood out, and the box beside the lanes, which keeps one size whatever minute is pointed at.
/// </summary>
[Collection("UI")]
public sealed class CrashMinutesPageTests
{
    private static CrashesViewModel Loaded()
    {
        SharedData.EnsureSeeded();
        var settings = Kit.OfflineSettings();
        var vm = Ui.Run(() => new CrashesViewModel(new ReportService(settings), settings));
        Ui.Run(() => vm.Unit = Rigsight.Core.Reports.ReportRange.All);
        Kit.Wait(() => vm.LoadAsync());
        return vm;
    }

    /// <summary>The newest app crash with recorded minutes before it.</summary>
    private static CrashGroup WithMinutes(CrashesViewModel vm) => Ui.Run(() =>
        vm.GroupsShown.First(g => g.Latest.Event.Kind is CrashKind.AppCrash or CrashKind.AppHang && g.Latest.Input?.Context is { NoReadings: false, CpuBefore: not null }));

    [Fact]
    public void A_timeline_line_of_several_days_opens_those_days_with_only_the_pcs_own_problems()
    {
        var vm = Loaded();
        var first = new DateTime(2026, 10, 12);
        Ui.Run(() =>
        {
            vm.Filter = "Apps";
            vm.ShowPcProblems(first, first.AddDays(3));
            Assert.Equal((Rigsight.Core.Reports.ReportRange.Custom, first, first.AddDays(4), "Pc"), (vm.Unit, vm.CustomFrom, vm.CustomTo, vm.Filter));
            vm.ShowPcProblems(first, first.AddHours(20));
            Assert.Equal((Rigsight.Core.Reports.ReportRange.Day, first, "Pc"), (vm.Unit, vm.Anchor, vm.Filter));
        });
    }

    [Fact]
    public void A_crash_opened_from_another_page_goes_back_there_by_its_name()
    {
        var vm = Loaded();
        var group = WithMinutes(vm);
        var went = new List<string>();
        Ui.Run(() => vm.GoBackTo = went.Add);
        Kit.Wait(() => vm.ShowCrashAsync(group.Latest.Time, ("timeline", "Timeline")));
        Ui.Run(() =>
        {
            Assert.NotNull(vm.Case);
            Assert.Equal("Back to Timeline", vm.BackText);
            vm.BackCommand.Execute(null);
            Assert.Null(vm.Case);
            Assert.Equal(["timeline"], went);
            Assert.Equal("Crashes", vm.BackText);
        });

        // Opened here (or from a notification): back is this page's list, nowhere else.
        Kit.Wait(() => vm.ShowCrashAsync(group.Latest.Time));
        Ui.Run(() =>
        {
            Assert.Equal("Crashes", vm.BackText);
            vm.BackCommand.Execute(null);
            Assert.Null(vm.Case);
            Assert.Equal(["timeline"], went);
        });
    }

    [Fact]
    public void An_opened_crash_has_its_lanes_strips_marks_and_box()
    {
        var vm = Loaded();
        var group = WithMinutes(vm);
        Kit.Wait(() => vm.OpenCaseCommand.ExecuteAsync(group));
        Ui.Run(() =>
        {
            var c = vm.Case!;
            Assert.True(c.HasChart);
            Assert.Contains(c.Lanes, l => l.Name == "Temperature");
            Assert.Contains(c.Lanes, l => l.Name == "Load");
            // Every line has a value (or a break) for every minute, and every colour is one of the palette's, so a picked accent recolours it.
            string[] palette = ["CpuBrush", "GpuBrush", "PurpleBrush", "PinkBrush", "SkyBrush", "RamBrush"];
            Assert.All(c.Lanes.SelectMany(l => l.Series), s => { Assert.Equal(c.Minutes.Count, s.Values.Length); Assert.Contains(s.Brush, palette); });
            Assert.All(c.Lanes, l => Assert.True(l.Max > 0));
            Assert.Contains(c.Strips, s => s.Name == "In front");
            Assert.Single(c.Marks, m => m.IsCrash);
            Assert.NotEmpty(c.StoodOut);
            Assert.EndsWith("one reading a minute", c.RangeText);

            // The box: the crash's own minute, every list at its fixed number of lines.
            var box = c.Readout!;
            Assert.Matches("^(At|Just before) the crash, ", box.Title);
            Assert.Equal((CaseReadout.FanLines, CaseReadout.CpuLines, CaseReadout.GpuLines, CaseReadout.MemLines),
                (box.Fans.Count, box.CpuApps.Count, box.GpuApps.Count, box.MemApps.Count));
            box.Show(0);
            Assert.StartsWith("At ", box.Title);
            Assert.EndsWith(" min before the crash", box.When);
            Assert.Equal((CaseReadout.FanLines, CaseReadout.CpuLines, CaseReadout.GpuLines, CaseReadout.MemLines),
                (box.Fans.Count, box.CpuApps.Count, box.GpuApps.Count, box.MemApps.Count));
            box.Show(null);
            Assert.Matches("^(At|Just before) the crash, ", box.Title);
        });
    }

    [Fact]
    public void The_box_is_one_size_for_every_minute_and_the_lanes_end_level_with_it()
    {
        var vm = Loaded();
        var group = WithMinutes(vm);
        Ui.TakeProblems();
        Ui.Run(() =>
        {
            var window = new Window { Content = new Views.CrashesView { DataContext = vm }, Left = -32000, Top = -32000, Width = 1400, Height = 1000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            try
            {
                Ui.Run(() => vm.OpenCaseCommand.ExecuteAsync(group));
                Ui.Pump(400);
                var box = Visuals.Descendants<Border>(window).Single(b => AutomationProperties.GetName(b) == "The minute's readings");
                var lanes = Visuals.Descendants<CaseLanes>(window).Single();
                double height = box.ActualHeight;
                Assert.True(height > 400);
                Assert.Equal(height, lanes.ActualHeight, 1);
                for (int i = 0; i < vm.Case!.Minutes.Count; i += 3)
                {
                    vm.Case.Readout!.Show(i);
                    Ui.Pump(10);
                    Assert.Equal(height, box.ActualHeight);
                }
            }
            finally { window.Close(); }
        });
        Ui.AssertNoProblems("Opened crash");
    }

    /// <summary>The opened crash given a close-up of its own (made on a database of the test's own: the shared one stays as it is).</summary>
    private static CrashCase WithCloseUp(CrashCase c)
    {
        using var t = new Rigsight.Tests.Data.TestDb("closeup-page");
        long app = t.Db.UpsertApp("game.exe", "A game", null, Rigsight.Core.Settings.AppCategory.Game);
        var crash = c.Row.Event;
        long at = crash.Ts * 1000;
        var points = Enumerable.Range(-150, 211).Select(i => new CloseUpPoint
        {
            T = at + i * 2000L, CpuTemp = 60 + (i > -15 && i <= 0 ? 15 + i : 0), GpuTemp = 70, GpuHot = 82 + (i > -15 && i <= 0 ? 15 + i * 0.5 : 0),
            CpuLoad = 40, GpuLoad = i <= 0 ? 95 : 3, CpuPower = 70, GpuPower = 220, Ram = 14, Commit = 60, Vram = 9000, VramTotal = 12288,
            Disk = 2_000_000, Down = 50_000, Up = 5_000, Here = true, Front = i <= 0 ? app : null,
            Fans = [new CloseUpFan("CPU fan", 1200), new CloseUpFan("GPU fan", 1600)],
            Apps = [new CloseUpApp(app, 30, i <= 0 ? 90 : 0, 6000)],
        }).ToList();
        var view = CloseUpBuilder.Build(t.Db, Kit.OfflineSettings().Current, new CloseUp(crash.Ts, crash.AppExe, CloseUpKind.App, true, points), crash, DateTime.Now)!;
        return new CrashCase
        {
            Group = c.Group, Row = c.Row, Finding = c.Finding, Wide = c.Wide, Close = view, UpClose = true,
            Points = c.Points, RuledOut = c.RuledOut, NotKnown = c.NotKnown, Running = c.Running, Changes = c.Changes, NoChanges = c.NoChanges,
            HistoryTitle = c.HistoryTitle, Bars = c.Bars, Times = c.Times, HistoryNote = c.HistoryNote, Tech = c.Tech, NoChart = c.NoChart,
        };
    }

    [Fact]
    public void A_crash_with_a_close_up_opens_on_it_and_the_switch_shows_the_minutes_with_their_after_side()
    {
        var vm = Loaded();
        var group = WithMinutes(vm);
        Kit.Wait(() => vm.OpenCaseCommand.ExecuteAsync(group));
        Ui.TakeProblems();
        Ui.Run(() =>
        {
            var c = WithCloseUp(vm.Case!);
            Assert.True(c.HasCloseUp);
            Assert.EndsWith("a reading every 2 to 3 seconds", c.RangeText);
            Assert.All(c.Lanes.SelectMany(l => l.Series), s => Assert.Equal(c.Minutes.Count, s.Values.Length));
            Assert.StartsWith("Last reading before the crash, ", c.Readout!.Title);
            Assert.Contains(c.StoodOut, x => x.Text.StartsWith("After: the GPU went from"));
            c.Readout.Show(0);
            Assert.EndsWith("before the crash", c.Readout.When);

            var window = new Window { Content = new Views.CrashesView { DataContext = vm }, Left = -32000, Top = -32000, Width = 1400, Height = 1000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            try
            {
                vm.Case = c;
                Ui.Pump(400);
                var box = Visuals.Descendants<Border>(window).Single(b => AutomationProperties.GetName(b) == "The minute's readings");
                var lanes = Visuals.Descendants<CaseLanes>(window).Single();
                Assert.Same(c.Close, lanes.View);
                double height = box.ActualHeight;
                Assert.Equal(height, lanes.ActualHeight, 1);
                for (int i = 0; i < c.Minutes.Count; i += 17)
                {
                    c.Readout!.Show(i);
                    Ui.Pump(5);
                    Assert.Equal(height, box.ActualHeight);
                }
                var switches = Visuals.Descendants<RadioButton>(window).Where(r => r.Content is "5 minutes" or "45 minutes").ToList();
                Assert.Equal(2, switches.Count);
                Assert.True(switches[0].IsChecked);

                // The minutes: 45 before and the after side.
                switches[1].IsChecked = true;
                Ui.Pump(200);
                Assert.False(c.UpClose);
                Assert.Same(c.Wide, lanes.View);
                Assert.EndsWith("one reading a minute", c.RangeText);
                Assert.True(c.Wide.ChartTo > c.Row.Time);
                Assert.Equal(height, box.ActualHeight, 1);
            }
            finally { window.Close(); }
        });
        Ui.AssertNoProblems("Opened crash with a close-up");
    }

    [Fact]
    public void A_driver_resets_close_up_breaks_the_graphics_lines_while_the_driver_restarts()
    {
        using var t = new Rigsight.Tests.Data.TestDb("closeup-reset");
        var at = new DateTime(2026, 10, 16, 18, 10, 22);
        long ts = Rigsight.Core.Data.TimeUtil.ToUnix(at);
        var reset = new CrashEvent { Id = 1, Ts = ts, Kind = CrashKind.GpuDriverReset, AppExe = "" };
        // Every 2 seconds; no GPU readings for the 6 seconds the driver was restarting.
        var points = Enumerable.Range(-150, 211).Select(i =>
        {
            bool gone = i > 0 && i <= 3;
            return new CloseUpPoint
            {
                T = ts * 1000 + i * 2000L, CpuTemp = 61, CpuLoad = 29, Here = true,
                GpuTemp = gone ? null : 70, GpuHot = gone ? null : 84, GpuLoad = gone ? null : 72, GpuPower = gone ? null : (i > -3 && i <= 0 ? 305 : 180),
            };
        }).ToList();
        var view = Ui.Run(() => CloseUpBuilder.Build(t.Db, Kit.OfflineSettings().Current, new CloseUp(ts, "", CloseUpKind.App, true, points), reset, DateTime.Now))!;

        var crashMark = Assert.Single(view.Marks, m => m.IsCrash);
        Assert.Equal(("Graphics driver reset", at), (crashMark.Label, crashMark.At));
        Assert.Equal(at.AddSeconds(CloseUp.AfterSec), view.ChartTo);
        // The GPU's lines are a break for those seconds, not a drop to zero.
        var gpuLoad = view.Lanes.Single(l => l.Name == "Load").Series.Single(x => x.Label == "GPU").Values;
        Assert.All(Enumerable.Range(151, 3), i => Assert.Null(gpuLoad[i]));
        Assert.NotNull(gpuLoad[155]);
        Assert.StartsWith("Last reading before the graphics driver reset, ", view.Readout!.Title);
        view.Readout.Show(0);
        Assert.EndsWith("before the graphics driver reset", view.Readout.When);
        Assert.Contains(view.StoodOut, x => x.Warn && x.Text.StartsWith("GPU power jumped to about"));
    }

    [Theory]
    [InlineData(4.1, 5)]
    [InlineData(100, 100)]
    [InlineData(101, 120)]
    [InlineData(1830, 2000)]
    [InlineData(0, 1)]
    public void Scales_end_on_a_round_number(double top, double expected) => Assert.Equal(expected, CaseMinutesBuilder.Nice(top));

    [Fact]
    public void A_list_in_the_box_always_has_its_lines()
    {
        Assert.Equal(["Chrome", "–", "–"], CaseReadout.Pad([new CaseApp("Chrome", "3%")], 3).Select(a => a.Name));
        Assert.Equal(["a", "b"], CaseReadout.Pad([new CaseApp("a", ""), new CaseApp("b", ""), new CaseApp("c", "")], 2).Select(a => a.Name));
    }
}
