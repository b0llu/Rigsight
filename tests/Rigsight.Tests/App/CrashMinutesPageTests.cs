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
