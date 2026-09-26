using System.Windows;
using Rigsight.Core.Reports;
using Rigsight.Services;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.AppUi;

/// <summary>
/// The whole app, page by page: every page must build, fill with data and render in both themes and at small and
/// large window sizes, with no binding WPF can't resolve and no exception on the UI thread.
/// </summary>
[Collection("UI")]
public sealed class AppWindowTests(AppHost host) : IClassFixture<AppHost>
{
    private static readonly string Shots = Path.Combine(TestEnvironment.DataDir, "screens");

    public static TheoryData<string> Pages => [.. AppHost.Pages];

    [Theory]
    [MemberData(nameof(Pages))]
    public void Page_renders_without_errors_in_dark_and_light(string page)
    {
        foreach (var theme in new[] { ThemeManager.Dark, ThemeManager.Light })
        {
            Ui.TakeProblems();
            Ui.Run(() => host.Shell.Settings.Update(s => s.Theme = theme));
            Ui.Pump(200);
            host.Show(page, 900);
            host.ScrollThrough();
            host.Agent.Tick(1);
            host.Agent.Procs();
            Ui.Pump(300);
            Ui.Run(() => Ui.SavePng(host.Window, Path.Combine(Shots, $"{theme}-{page.Replace(':', '_')}.png")));
            Ui.AssertNoProblems($"{page} ({theme})");
            Assert.Equal(theme == ThemeManager.Light, Ui.Run(() => ThemeManager.IsLight));
        }
        Ui.Run(() => host.Shell.Settings.Update(s => s.Theme = ThemeManager.Dark));
        Ui.Pump(200);
    }

    [Theory]
    [InlineData(1140, 680)] // the window's minimum size
    [InlineData(1600, 1000)]
    [InlineData(2560, 1440)]
    public void Every_page_lays_out_at_window_size(int width, int height)
    {
        Ui.TakeProblems();
        Ui.Run(() => { host.Window.Width = width; host.Window.Height = height; });
        try
        {
            foreach (var page in AppHost.Pages)
            {
                host.Show(page, 500);
                Ui.AssertNoProblems($"{page} at {width}×{height}");
                // No text may run off the right edge of the page (cut off rather than trimmed or wrapped).
                var cut = Ui.Run(() => Layout.CutOffText(host.Window.PageHost));
                Assert.True(cut.Count == 0, $"{page} at {width}×{height} cuts off: " + string.Join(" | ", cut.Take(10)));
            }
        }
        finally
        {
            Ui.Run(() => { host.Window.Width = 1440; host.Window.Height = 900; });
        }
    }

    [Fact]
    public void Pages_survive_a_minute_of_live_ticks_and_process_lists()
    {
        foreach (var page in new[] { "home", "temperatures", "memory", "sensors", "custom:" + AppHost.DashboardId })
        {
            host.Show(page);
            Ui.TakeProblems();
            for (int i = 0; i < 60; i++)
            {
                host.Agent.Tick(i);
                if (i % 2 == 0) host.Agent.Procs(40 + i % 30);
                Ui.Pump(i % 10 == 0 ? 60 : 5);
            }
            Ui.Pump(200);
            Ui.AssertNoProblems($"{page} with live data");
        }
    }

    [Fact]
    public void Custom_ranges_render_on_every_page_that_offers_them()
    {
        Ui.TakeProblems();
        var from = DateTime.Today.AddDays(-2).AddHours(8);
        foreach (var (page, to) in new[] { ("reports", from.AddHours(19)), ("reports", from.AddDays(40)), ("apps", from.AddHours(19)),
                     ("apps", from.AddDays(120)), ("crashes", from.AddHours(19)), ("crashes", from.AddDays(20)) })
        {
            host.Show(page, 200);
            // The range set as the editor would set it. (The editor itself is tested on its own: popping it up here
            // wakes whatever accessibility tools run on the PC, which then hold on to parts of the window for a while
            // and upset the memory tests that follow.)
            Ui.Run(() =>
            {
                var picker = Visuals.Descendants<Controls.PeriodPicker>(host.Window.PageHost).Single();
                picker.Unit = ReportRange.Custom;
                picker.CustomFrom = from;
                picker.CustomTo = to;
                Assert.Equal(Report.CustomTitle(from, to), picker.Pager.Label);
            });
            Ui.Pump(600);
            host.ScrollThrough();
            Ui.Run(() => Ui.SavePng(host.Window, Path.Combine(Shots, $"custom-{page}-{(to - from).TotalHours:0}h.png")));
            Ui.AssertNoProblems($"{page} over {(to - from).TotalHours:0} hours");
        }
        Ui.Run(() =>
        {
            host.Shell.ReportsPage.ShowDay(DateTime.Today);
            host.Shell.Apps.Unit = ReportRange.Week;
            host.Shell.Crashes.Unit = ReportRange.Month;
        });
        Ui.Pump(300);
    }

    [Fact]
    public void Visiting_every_page_twice_gives_the_same_page_objects()
    {
        // Pages are built once and kept (MainWindow caches them); a second visit must not build new ones.
        var first = AppHost.Pages.Select(p => { host.Show(p, 100); return Ui.Run(() => host.Window.PageHost.Content); }).ToList();
        var second = AppHost.Pages.Select(p => { host.Show(p, 100); return Ui.Run(() => host.Window.PageHost.Content); }).ToList();
        for (int i = 0; i < first.Count; i++) Assert.Same(first[i], second[i]);
        Ui.AssertNoProblems("revisiting pages");
    }
}
