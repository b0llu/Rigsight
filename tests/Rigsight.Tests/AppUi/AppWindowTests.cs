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

    [Fact]
    public void The_question_before_ending_a_part_of_Windows_shows_over_the_window()
    {
        Ui.TakeProblems();
        host.Show("processes", 600);
        var layer = Ui.Run(() => (FrameworkElement)host.Window.FindName("EndQuestionLayer"));
        List<System.Windows.Controls.Button> Buttons() => [.. Visuals.Descendants<System.Windows.Controls.Button>(layer).Where(b => b.IsVisible)];

        // Windows can't run without it: the red button is off until the box is ticked, and Cancel is the one in front.
        var asked = Ui.Run(() => host.Shell.Processes.Question.AskAsync(new ViewModels.EndQuestion
        {
            Title = "End Client Server Runtime Process?", Body = Rigsight.Core.Apps.EndRisks.Sentence(Rigsight.Core.Apps.EndRisk.Critical), NeedsTick = true,
        }));
        Ui.Pump(300);
        Ui.Run(() =>
        {
            Assert.True(layer.IsVisible);
            Assert.True(host.Shell.Live.HoldProcs); // the list behind holds still meanwhile
            Assert.Equal(["End anyway", "Cancel"], Buttons().Select(b => (string)b.Content));
            Assert.False(Buttons()[0].IsEnabled);
            Assert.True(Buttons()[1].IsDefault);
            var tick = Visuals.Descendants<System.Windows.Controls.CheckBox>(layer).Single(c => c.IsVisible);
            Assert.Equal("I understand that unsaved work may be lost", tick.Content);
            Ui.SavePng(host.Window, Path.Combine(Shots, "end-question-critical.png"));
            tick.IsChecked = true;
            Assert.True(Buttons()[0].IsEnabled);
            Buttons()[1].Command.Execute(Buttons()[1].CommandParameter);
            Assert.False(layer.IsVisible);
            Assert.Equal(ViewModels.EndAnswer.Cancel, asked.Result);
            Assert.False(host.Shell.Live.HoldProcs);
        });

        // Windows Explorer: it can be restarted instead, and that is the button in front.
        var explorer = Ui.Run(() => host.Shell.Processes.Question.AskAsync(new ViewModels.EndQuestion
        {
            Title = "End Windows Explorer?", Body = Rigsight.Core.Apps.EndRisks.Sentence(Rigsight.Core.Apps.EndRisk.Explorer), CanRestart = true,
        }));
        Ui.Pump(300);
        Ui.Run(() =>
        {
            Assert.Equal(["End anyway", "Cancel", "Restart it"], Buttons().Select(b => (string)b.Content));
            Assert.Equal([false, false, true], Buttons().Select(b => b.IsDefault));
            Assert.DoesNotContain(Visuals.Descendants<System.Windows.Controls.CheckBox>(layer), c => c.IsVisible);
            Ui.SavePng(host.Window, Path.Combine(Shots, "end-question-explorer.png"));
            Buttons()[2].Command.Execute(Buttons()[2].CommandParameter);
            Assert.Equal(ViewModels.EndAnswer.Restart, explorer.Result);
        });

        // Several picked, some of them parts of Windows: each of those with what ending it does.
        var several = Ui.Run(() => host.Shell.Processes.Question.AskAsync(new ViewModels.EndQuestion
        {
            Title = "End 3 apps?", Intro = "One of them is part of Windows:",
            Lines = [new("Desktop Window Manager", Rigsight.Core.Apps.EndRisks.Sentence(Rigsight.Core.Apps.EndRisk.WindowManager)!)],
        }));
        Ui.Pump(300);
        Ui.Run(() =>
        {
            Assert.Equal(["End anyway", "Cancel"], Buttons().Select(b => (string)b.Content));
            Assert.True(Buttons()[0].IsEnabled);
            Ui.SavePng(host.Window, Path.Combine(Shots, "end-question-several.png"));
            Buttons()[0].Command.Execute(Buttons()[0].CommandParameter);
            Assert.Equal(ViewModels.EndAnswer.End, several.Result);
            Assert.False(layer.IsVisible);
        });
        Ui.AssertNoProblems("the question before ending");
    }

    [Fact]
    public void Whats_new_draws_a_removal_without_error_and_says_nothing_about_another_page_on_it()
    {
        Ui.TakeProblems();
        host.Show("home", 900);
        Ui.Run(() => host.Shell.WhatsNew.SeeAllCommand.Execute(null));
        Ui.Pump(600);
        Ui.Run(() =>
        {
            var removal = host.Shell.WhatsNew.Shown.Select(r => r.Feature).First(f => f is { IsRemoval: true })!;
            // It opens no page of its own, so no page's "already found on this PC" line belongs on it.
            Assert.Null(removal.Fact);
            Ui.SavePng(host.Window, Path.Combine(Shots, "whatsnew-removal.png"));
            host.Shell.WhatsNew.CloseCommand.Execute(null);
        });
        Ui.AssertNoProblems("what's new");
    }

    // The one in the Refresh button, where it takes the icon's place (the search box has its own).
    private Controls.Spinner RefreshSpinner() =>
        Visuals.Descendants<Controls.Spinner>(host.Window.PageHost).Single(s => s.Parent is System.Windows.Controls.Grid);

    [Fact]
    public void The_timelines_refresh_button_turns_while_the_pc_is_looked_over()
    {
        Ui.TakeProblems();
        host.Show("timeline", 900);
        Ui.Run(() =>
        {
            Assert.False(RefreshSpinner().IsVisible);
            Ui.SavePng(host.Window, Path.Combine(Shots, "timeline-refresh.png"));
            host.Shell.Timeline.IsRefreshing = true;
        });
        Ui.Pump(150);
        Ui.Run(() =>
        {
            Assert.True(RefreshSpinner().IsVisible);
            Ui.SavePng(host.Window, Path.Combine(Shots, "timeline-refreshing.png"));
            host.Shell.Timeline.IsRefreshing = false;
        });
        Ui.AssertNoProblems("timeline refresh");
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
        foreach (var page in new[] { "home", "temperatures", "memory", "processes", "sensors", "custom:" + AppHost.DashboardId })
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
