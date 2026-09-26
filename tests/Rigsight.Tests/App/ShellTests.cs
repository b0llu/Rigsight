using System.Reflection;
using System.Windows;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.Tests.AppUi;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>The shell: navigation (also from the agent), dashboards, the start page, and the agent connection.</summary>
[Collection("UI")]
public sealed class ShellTests : IClassFixture<AppHost>
{
    private readonly AppHost host;

    public ShellTests(AppHost host)
    {
        this.host = host;
        // A change from the test before may still be on its way to the agent; until it's there, the app (rightly)
        // ignores the agent's copies, which would confuse the next test.
        Assert.True(Ui.WaitFor(() => !Pending(host.Shell.Settings)), "settings never reached the agent");
    }

    private static bool Pending(SettingsModel settings)
    {
        var t = typeof(SettingsModel);
        return (bool)t.GetField("_dirty", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(settings)!
               || ((System.Windows.Threading.DispatcherTimer)t.GetField("_debounce", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(settings)!).IsEnabled;
    }

    private ShellViewModel Shell => host.Shell;

    private int CountActivations(Action action)
    {
        int n = 0;
        void On() => n++;
        Ui.Run(() => Shell.ActivateRequested += On);
        try { action(); }
        finally { Ui.Run(() => Shell.ActivateRequested -= On); }
        return n;
    }

    private static Dictionary<string, FrameworkElement> Views(MainWindow window) =>
        (Dictionary<string, FrameworkElement>)typeof(MainWindow).GetField("_pages", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

    [Fact]
    public void Built_in_pages_are_the_sidebars()
    {
        Assert.Equal(["home", "reports", "apps", "crashes", "temperatures", "memory", "storage", "sensors"], ShellViewModel.BuiltInPages.Select(p => p.Key));
        Assert.All(ShellViewModel.BuiltInPages, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
    }

    [Fact]
    public void Go_switches_pages_and_shows_the_right_view()
    {
        foreach (var page in new[] { "reports", "storage", "home" })
        {
            Ui.Run(() => Shell.GoCommand.Execute(page));
            Ui.Pump(100);
            Ui.Run(() =>
            {
                Assert.Equal(page, Shell.CurrentPage);
                Assert.Same(Views(host.Window)[page], host.Window.PageHost.Content);
            });
        }
    }

    [Fact]
    public void The_agent_can_open_an_app_on_today()
    {
        Ui.Run(() => Shell.CurrentPage = "home");
        int activated = CountActivations(() =>
        {
            host.Agent.Broadcast(new AgentMessage { T = "navigate", Page = "apps", Arg = "discord.exe" });
            Assert.True(Ui.WaitFor(() => Shell.CurrentPage == "apps"));
            Assert.True(Ui.WaitFor(() => Shell.Apps.Selected?.Exe == "discord.exe", 15_000));
        });
        Assert.Equal(1, activated);
        Ui.Run(() =>
        {
            Assert.Equal(ReportRange.Day, Shell.Apps.Unit);
            Assert.Equal(DateTime.Today, Shell.Apps.Anchor);
        });
    }

    [Fact]
    public void A_recap_of_an_older_day_opens_that_days_report()
    {
        var day = DateTime.Today.AddDays(-3);
        int activated = CountActivations(() => Ui.Run(() => Shell.Navigate("reports", day.ToString("yyyy-MM-dd"))));
        Assert.Equal(1, activated);
        Assert.True(Ui.WaitFor(() => Shell.ReportsPage.Report?.From == day && !Shell.ReportsPage.IsLoading, 15_000));
        Ui.Run(() =>
        {
            Assert.Equal("reports", Shell.CurrentPage);
            Assert.Equal(ReportRange.Day, Shell.ReportsPage.Range);
        });
        Ui.Run(() => Shell.CurrentPage = "home");
    }

    [Fact]
    public void The_agent_can_open_yesterdays_report()
    {
        CountActivations(() => Ui.Run(() => Shell.Navigate("reports", "yesterday")));
        Assert.True(Ui.WaitFor(() => Shell.ReportsPage.Report?.From == DateTime.Today.AddDays(-1) && !Shell.ReportsPage.IsLoading, 15_000));
        Ui.Run(() =>
        {
            Assert.Equal("reports", Shell.CurrentPage);
            Assert.Equal(ReportRange.Day, Shell.ReportsPage.Range);
        });
        Ui.Run(() => Shell.CurrentPage = "home");
    }

    [Fact]
    public void Navigating_nowhere_just_brings_the_window_forward()
    {
        Ui.Run(() => Shell.CurrentPage = "sensors");
        int activated = CountActivations(() =>
        {
            Ui.Run(() => Shell.Navigate(null, null));
            Ui.Run(() => Shell.Navigate("", "x"));
        });
        Assert.Equal(2, activated);
        Assert.Equal("sensors", Ui.Run(() => Shell.CurrentPage));
    }

    [Fact]
    public void A_hello_can_carry_a_page_to_open()
    {
        var hello = Fixtures.Hello(host.Agent.Settings);
        hello.Page = "crashes";
        host.Agent.Broadcast(hello);
        Assert.True(Ui.WaitFor(() => Shell.CurrentPage == "crashes"));
        Ui.Run(() => Shell.CurrentPage = "home");
    }

    [Fact]
    public void Dashboards_in_the_sidebar_follow_the_current_page()
    {
        var dash = Ui.Run(() => Shell.FindCustomPage("custom:" + AppHost.DashboardId))!;
        Assert.NotNull(dash);
        Ui.Run(() => Shell.CurrentPage = dash.NavKey);
        Ui.Run(() => Assert.True(dash.IsSelected));
        Ui.Run(() => Shell.CurrentPage = "memory");
        Ui.Run(() => Assert.False(dash.IsSelected));
        // Clicking it in the sidebar (selecting it) opens it.
        Ui.Run(() => dash.IsSelected = true);
        Assert.Equal(dash.NavKey, Ui.Run(() => Shell.CurrentPage));
        Ui.Run(() => Shell.CurrentPage = "home");
        Assert.Null(Ui.Run(() => Shell.FindCustomPage("custom:nope")));
    }

    [Fact]
    public void New_dashboards_get_numbered_names_open_for_editing()
    {
        var made = new List<CustomPageViewModel>();
        try
        {
            Ui.Run(() =>
            {
                Shell.NewPageCommand.Execute(null);
                made.Add(Shell.CustomPages[^1]);
                Shell.NewPageCommand.Execute(null);
                made.Add(Shell.CustomPages[^1]);
            });
            Ui.Run(() =>
            {
                Assert.Equal(["Dashboard", "Dashboard 2"], made.Select(p => p.Name));
                Assert.All(made, p => Assert.True(p.IsEditing));
                Assert.All(made, p => Assert.Equal(7, p.Tiles.Count));
                Assert.Equal(made[1].NavKey, Shell.CurrentPage);
                Assert.True(made[1].IsSelected);
                Assert.False(made[0].IsSelected);
                Assert.Contains(Shell.Settings.Current.CustomPages, p => p.Id == made[0].Id && p.Tiles.Count == 7);
                Assert.Contains(Shell.SettingsPage.StartPageOptions, o => o.Key == made[1].NavKey && o.Name == "Dashboard 2");
            });
        }
        finally
        {
            Ui.Run(() =>
            {
                Shell.ConfirmDelete = _ => true;
                foreach (var p in made) p.DeletePageCommand.Execute(null);
            });
        }
    }

    [Fact]
    public void Deleting_the_open_dashboard_goes_home_and_forgets_it()
    {
        Ui.TakeProblems();
        CustomPageViewModel page = null!;
        Ui.Run(() =>
        {
            Shell.NewPageCommand.Execute(null);
            page = Shell.CustomPages[^1];
            page.ToggleStartPageCommand.Execute(null);
        });
        Ui.Pump(200);
        Ui.Run(() =>
        {
            Assert.Equal(page.NavKey, Shell.Settings.Current.StartPage);
            Assert.True(Views(host.Window).ContainsKey(page.NavKey));

            // "No" leaves everything as it was.
            Shell.ConfirmDelete = _ => false;
            page.DeletePageCommand.Execute(null);
            Assert.Contains(page, Shell.CustomPages);
            Assert.Equal(page.NavKey, Shell.CurrentPage);

            var deleted = new List<string>();
            Shell.CustomPageDeleted += deleted.Add;
            Shell.ConfirmDelete = p => { Assert.Same(page, p); return true; };
            page.DeletePageCommand.Execute(null);
            Assert.DoesNotContain(page, Shell.CustomPages);
            Assert.Equal("home", Shell.CurrentPage);
            Assert.Equal("home", Shell.Settings.Current.StartPage);
            Assert.DoesNotContain(Shell.Settings.Current.CustomPages, p => p.Id == page.Id);
            Assert.Equal([page.NavKey], deleted);
            Assert.False(Views(host.Window).ContainsKey(page.NavKey));
            Assert.Null(Shell.FindCustomPage(page.NavKey));
        });
        Ui.AssertNoProblems("deleting the open dashboard");
    }

    [Fact]
    public void Deleting_a_dashboard_that_is_not_open_stays_put()
    {
        CustomPageViewModel page = null!;
        Ui.Run(() =>
        {
            Shell.NewPageCommand.Execute(null);
            page = Shell.CustomPages[^1];
            Shell.CurrentPage = "memory";
            Shell.ConfirmDelete = _ => true;
            page.DeletePageCommand.Execute(null);
            Assert.Equal("memory", Shell.CurrentPage);
            Assert.DoesNotContain(page, Shell.CustomPages);
            Shell.CurrentPage = "home";
        });
    }

    [Theory]
    [InlineData("memory", "memory")]
    [InlineData("sensors", "sensors")]
    [InlineData("custom:" + AppHost.DashboardId, "custom:" + AppHost.DashboardId)]
    [InlineData("custom:deleted-long-ago", "home")]
    [InlineData("widgets", "home")] // not a page the app opens on
    [InlineData("", "home")]
    public void The_app_opens_on_the_chosen_start_page(string start, string expected)
    {
        var settings = SharedData.ResetSettings();
        settings.StartPage = start;
        SettingsStore.Save(settings);
        try
        {
            var shell = Ui.Run(() => new ShellViewModel(new AgentClient(Ui.Dispatcher)));
            Ui.Run(() =>
            {
                Assert.Equal(expected, shell.CurrentPage);
                Assert.All(shell.CustomPages, p => Assert.Equal(p.NavKey == expected, p.IsSelected));
                // This extra shell has no window: stop its once-a-minute refresh.
                ((System.Windows.Threading.DispatcherTimer)typeof(ShellViewModel).GetField("_refresh", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(shell)!).Stop();
            });
        }
        finally
        {
            SharedData.ResetSettings();
        }
    }

    [Fact]
    public void Losing_and_regaining_the_agent_updates_the_sidebar()
    {
        Ui.Run(() =>
        {
            Assert.True(Shell.IsConnected);
            Assert.False(Shell.ShowAgentWarning);
            Assert.Equal("Restart with admin", Shell.AgentButtonText);
        });
        host.RestartAgent(() => Ui.Run(() =>
        {
            Assert.False(Shell.IsConnected);
            Assert.True(Shell.ShowAgentWarning);
            Assert.True(Shell.AgentIsAdmin); // unknown until it says hello again
            Assert.Equal("Tracking and some sensors need it.", Shell.AgentHint);
            Assert.Equal("Start agent", Shell.AgentButtonText);
            Assert.Equal("", Shell.AgentStatus);
        }));
        Assert.True(Ui.WaitFor(() => Shell.Live.Hardware.Count > 0));
        Ui.Run(() =>
        {
            Assert.True(Shell.IsConnected);
            Assert.False(Shell.ShowAgentWarning);
        });
    }

    [Fact]
    public void A_change_made_while_the_agent_is_down_survives_its_return_without_flipping_back()
    {
        var seen = new List<double>();
        void Record() => seen.Add(Shell.Settings.Current.Alerts.CpuLimit);
        Ui.Run(() => Shell.Settings.Changed += Record);
        try
        {
            double before = Ui.Run(() => Shell.Settings.Current.Alerts.CpuLimit);
            host.RestartAgent(() =>
            {
                Ui.Run(() => Shell.Settings.Update(s => s.Alerts.CpuLimit = before + 7));
                Kit.AfterDebounce();
            });
            Assert.True(Ui.WaitFor(() => host.Agent.Settings.Alerts.CpuLimit == before + 7, 5000), "the change never reached the agent");
            Ui.Pump(300);
            Assert.Equal([before + 7], seen);
            Assert.Equal(before + 7, Ui.Run(() => Shell.Settings.Current.Alerts.CpuLimit));
            Ui.Run(() => Shell.Settings.Update(s => s.Alerts.CpuLimit = before));
            Kit.AfterDebounce();
        }
        finally
        {
            Ui.Run(() => Shell.Settings.Changed -= Record);
        }
    }

    [Fact]
    public void An_agent_without_admin_rights_is_explained_but_a_test_copy_never_asks_for_them()
    {
        var hello = Fixtures.Hello(host.Agent.Settings);
        hello.IsAdmin = false;
        host.Agent.Broadcast(hello);
        try
        {
            Assert.True(Ui.WaitFor(() => !Shell.AgentIsAdmin));
            Ui.Run(() =>
            {
                Assert.True(Shell.ShowAgentWarning);
                Assert.False(Shell.SettingsPage.AgentIsAdmin);
                Assert.Equal("CPU temperatures, fans and voltages need admin rights.", Shell.AgentHint);
                Assert.Equal("Restart with admin", Shell.AgentButtonText);
            });
            Ui.Pump(300);
            Assert.Empty(host.Agent.Commands("restart-elevated"));
        }
        finally
        {
            host.Agent.Broadcast(Fixtures.Hello(host.Agent.Settings));
            Assert.True(Ui.WaitFor(() => Shell.AgentIsAdmin));
        }
    }

    [Fact]
    public void An_agent_that_restarts_is_asked_again_for_the_open_apps_processes()
    {
        Ui.Run(() => Shell.CurrentPage = "memory");
        host.Agent.Procs();
        Assert.True(Ui.WaitFor(() => Shell.Live.Procs.Any(p => p.Exe == "chrome.exe")));
        var chrome = Ui.Run(() => Shell.Live.Procs.Single(p => p.Exe == "chrome.exe"));
        try
        {
            Ui.Run(() => Shell.Live.ToggleProcessesCommand.Execute(chrome));
            Assert.True(Ui.WaitFor(() => host.Agent.Commands("procs-detail").Any(c => c.Arg == "chrome.exe")));
            host.RestartAgent();
            Assert.True(Ui.WaitFor(() => host.Agent.Commands("procs-detail").Any(c => c.Arg == "chrome.exe")), "the new agent wasn't asked");
            host.Agent.Procs(detailFor: "chrome.exe");
            Assert.True(Ui.WaitFor(() => chrome.ShowChildren));
        }
        finally
        {
            Ui.Run(() => { if (chrome.IsExpanded) Shell.Live.ToggleProcessesCommand.Execute(chrome); Shell.CurrentPage = "home"; });
        }
    }

    [Fact]
    public void Status_overlay_update_and_preview_messages_reach_their_pages()
    {
        Ui.TakeProblems();
        host.Agent.Broadcast(new AgentMessage { T = "status", StartupEnabled = true });
        Assert.True(Ui.WaitFor(() => Shell.SettingsPage.StartupEnabled));
        host.Agent.Broadcast(new AgentMessage { T = "status", StartupEnabled = false });
        Assert.True(Ui.WaitFor(() => !Shell.SettingsPage.StartupEnabled));

        host.Agent.Broadcast(new AgentMessage { T = "overlay", OverlayVisible = true, OverlayHotkeyTaken = true, RtssState = "stopped" });
        Assert.True(Ui.WaitFor(() => Shell.Overlay.IsVisible && Shell.Overlay.HotkeyTaken && Shell.Overlay.RtssState == "stopped"));
        host.Agent.Broadcast(new AgentMessage { T = "overlay", OverlayVisible = false });
        Assert.True(Ui.WaitFor(() => !Shell.Overlay.IsVisible));
        Ui.Run(() => Assert.True(Shell.Overlay.HotkeyTaken)); // not sent: unchanged

        host.Agent.Broadcast(new AgentMessage { T = "previews" });
        if (Directory.Exists(Rigsight.Core.Updates.UpdateStore.Folder)) Directory.Delete(Rigsight.Core.Updates.UpdateStore.Folder, recursive: true);
        host.Agent.Broadcast(new AgentMessage { T = "update", UpdateStatus = "found" }); // nothing found today: stays as it is
        host.Agent.Broadcast(new AgentMessage { T = "no-such-message" });
        Ui.Pump(300);
        Ui.Run(() => Assert.Equal(UpdateState.None, Shell.Update.State));
        Ui.AssertNoProblems("agent messages");
    }

    [Fact]
    public void Settings_from_the_agent_are_applied_everywhere()
    {
        var changed = host.Agent.Settings.Clone();
        changed.UseFahrenheit = true;
        host.Agent.Broadcast(new AgentMessage { T = "settings", Settings = changed });
        try
        {
            Assert.True(Ui.WaitFor(() => Shell.Settings.Current.UseFahrenheit));
            Ui.Run(() =>
            {
                Assert.True(Shell.SettingsPage.UseFahrenheit);
                Assert.EndsWith("°F", Shell.Live.CpuTemp!.FormattedValue);
            });
        }
        finally
        {
            changed.UseFahrenheit = false;
            host.Agent.Broadcast(new AgentMessage { T = "settings", Settings = changed });
            Assert.True(Ui.WaitFor(() => !Shell.Settings.Current.UseFahrenheit));
        }
    }

    [Fact]
    public void The_temperature_page_loads_minute_history()
    {
        Ui.Run(() => Shell.CurrentPage = "temperatures");
        Assert.True(Ui.WaitFor(() => Shell.Live.ChartHistoryStart is not null && Shell.Live.TempSeries.Any(s => s.Minutes.Count > 0), 15_000));
        // An earlier day loads that day's history.
        Ui.Run(() =>
        {
            Shell.Live.ChartWindowSeconds = 0;
            Shell.Live.ChartDay = DateTime.Today.AddDays(-1);
        });
        Assert.True(Ui.WaitFor(() => Shell.Live.TempSeries[0].Minutes is { Count: > 0 } m && m.FirstTime < new DateTimeOffset(DateTime.Today).ToUnixTimeMilliseconds(), 15_000));
        Ui.Run(() =>
        {
            Shell.Live.ChartDay = DateTime.Today;
            Shell.Live.ChartWindowSeconds = 300;
            Shell.CurrentPage = "home";
        });
    }
}
