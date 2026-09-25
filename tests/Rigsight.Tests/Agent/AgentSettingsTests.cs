using Rigsight.Agent;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Agent;

/// <summary>
/// Settings sent by the app replace the agent's, except the fields only the agent changes: an app holding an older copy
/// must never undo a tray "Pause", a dragged widget, or the agent's own bookkeeping.
/// </summary>
public class AgentSettingsTests
{
    /// <summary>As the agent does it: the app's settings through the store, then the agent's fields kept.</summary>
    private static RigsightSettings Merge(RigsightSettings fromApp, RigsightSettings agents)
    {
        var incoming = SettingsStore.Deserialize(SettingsStore.Serialize(fromApp));
        AgentContext.KeepAgentFields(incoming, agents);
        return incoming;
    }

    /// <summary>Settings as the app holds them (loaded, so current in version).</summary>
    private static RigsightSettings App() => SettingsStore.Deserialize(SettingsStore.Serialize(new RigsightSettings()));

    private static RigsightSettings Agents()
    {
        var s = App();
        s.StartupConfigured = true;
        s.LastRecapDay = "2026-06-10";
        s.LastUpdateNotice = "0.5.12";
        s.Tracking.PausedUntil = -1;
        foreach (var w in s.Widgets) (w.X, w.Y) = (100 + (int)w.Style * 10, 200 + (int)w.Style * 10);
        return s;
    }

    [Fact]
    public void The_agents_own_fields_are_kept()
    {
        var app = App();
        (app.StartupConfigured, app.LastRecapDay, app.LastUpdateNotice, app.Tracking.PausedUntil) = (false, null, "0.4.0", 0);
        var merged = Merge(app, Agents());
        Assert.True(merged.StartupConfigured);
        Assert.Equal("2026-06-10", merged.LastRecapDay);
        Assert.Equal("0.5.12", merged.LastUpdateNotice);
        Assert.Equal(-1, merged.Tracking.PausedUntil);
    }

    [Fact]
    public void A_pause_ending_later_is_kept_and_so_is_not_being_paused()
    {
        var agents = Agents();
        agents.Tracking.PausedUntil = 1_900_000_000;
        var app = App();
        app.Tracking.PausedUntil = -1; // the app can't pause by sending settings
        Assert.Equal(1_900_000_000, Merge(app, agents).Tracking.PausedUntil);
        agents.Tracking.PausedUntil = 0;
        Assert.Equal(0, Merge(app, agents).Tracking.PausedUntil);
    }

    [Fact]
    public void Widget_positions_are_the_agents()
    {
        var app = App();
        foreach (var w in app.Widgets) (w.X, w.Y) = (null, null);
        var merged = Merge(app, Agents());
        foreach (var w in merged.Widgets)
            Assert.Equal((100 + (int)w.Style * 10, 200 + (int)w.Style * 10), (w.X!.Value, w.Y!.Value));
    }

    [Fact]
    public void Everything_else_about_a_widget_is_the_apps()
    {
        var app = App();
        var pill = app.Widgets.Single(w => w.Style == WidgetStyle.Pill);
        (pill.Enabled, pill.Theme, pill.Scale, pill.BackgroundOpacity, pill.ContentOpacity, pill.Locked, pill.Visibility, pill.X) =
            (false, WidgetTheme.Light, 1.5, 0.3, 0.7, true, WidgetVisibility.HideInFullscreen, 5);
        var merged = Merge(app, Agents()).Widgets.Single(w => w.Style == WidgetStyle.Pill);
        Assert.False(merged.Enabled);
        Assert.Equal(WidgetTheme.Light, merged.Theme);
        Assert.Equal(1.5, merged.Scale);
        Assert.Equal(0.3, merged.BackgroundOpacity);
        Assert.Equal(0.7, merged.ContentOpacity);
        Assert.True(merged.Locked);
        Assert.Equal(WidgetVisibility.HideInFullscreen, merged.Visibility);
        Assert.Equal(100 + (int)WidgetStyle.Pill * 10, merged.X);
    }

    [Fact]
    public void A_widget_the_agent_never_placed_keeps_the_apps_position()
    {
        var agents = Agents();
        agents.Widgets.RemoveAll(w => w.Style == WidgetStyle.Graph);
        var app = App();
        app.Widgets.Single(w => w.Style == WidgetStyle.Graph).X = 42;
        Assert.Equal(42, Merge(app, agents).Widgets.Single(w => w.Style == WidgetStyle.Graph).X);
    }

    [Fact]
    public void The_users_choices_come_from_the_app()
    {
        var app = App();
        (app.UseFahrenheit, app.Theme, app.AutoUpdate, app.StartPage) = (true, "light", false, "apps");
        app.AppNames["code.exe"] = "Editor";
        app.Tracking.ExcludedApps.Add("secret.exe");
        app.Tracking.KeepHistoryDays = 365;
        app.Alerts.CpuLimit = 90;
        app.Overlay.Corner = OverlayCorner.BottomRight;
        var merged = Merge(app, Agents());
        Assert.True(merged.UseFahrenheit);
        Assert.Equal("light", merged.Theme);
        Assert.False(merged.AutoUpdate);
        Assert.Equal("apps", merged.StartPage);
        Assert.Equal("Editor", merged.AppNames["CODE.EXE"]); // and still ignoring case after the trip
        Assert.Equal(["secret.exe"], merged.Tracking.ExcludedApps);
        Assert.Equal(365, merged.Tracking.KeepHistoryDays);
        Assert.Equal(90, merged.Alerts.CpuLimit);
        Assert.Equal(OverlayCorner.BottomRight, merged.Overlay.Corner);
    }

    [Fact]
    public void The_apps_settings_get_the_stores_repairs()
    {
        var app = App();
        (app.LiveRefreshMs, app.Theme) = (1, "neon");
        app.Tracking.SensorIntervalMs = 5;
        app.Widgets = [new WidgetConfig { Style = WidgetStyle.Pill, Scale = 9 }, new WidgetConfig { Style = WidgetStyle.Pill }];
        var merged = Merge(app, Agents());
        Assert.Equal(250, merged.LiveRefreshMs);
        Assert.Equal("dark", merged.Theme);
        Assert.Equal(500, merged.Tracking.SensorIntervalMs);
        Assert.Equal(Enum.GetValues<WidgetStyle>().Length, merged.Widgets.Count);
        Assert.Equal(2.0, merged.Widgets.Single(w => w.Style == WidgetStyle.Pill).Scale);
    }

    [Fact]
    public void The_agents_copy_is_left_as_it_was()
    {
        var agents = Agents();
        string before = SettingsStore.Serialize(agents);
        var app = App();
        app.UseFahrenheit = true;
        Merge(app, agents);
        Assert.Equal(before, SettingsStore.Serialize(agents));
    }
}

/// <summary>The updater run as "Rigsight.Agent.exe --update" answers the running agent through its exit code.</summary>
public class UpdaterContractTests
{
    [Fact]
    public void Exit_codes_never_change()
    {
        // A running agent of one version starts the updater of the next (the installer replaced the exe): the codes
        // are a contract between versions.
        Assert.Equal(0, BackgroundUpdater.UpToDate);
        Assert.Equal(1, BackgroundUpdater.Failed);
        Assert.Equal(2, BackgroundUpdater.Downloaded);
        Assert.Equal(3, BackgroundUpdater.Installing);
        Assert.Equal(4, BackgroundUpdater.Available);
    }

    [Fact]
    public async Task No_downloaded_installer_means_nothing_runs()
    {
        // Only the update store's own download is ever installed; with none there, nothing is looked up or run.
        Assert.False(await UpdateInstaller.RunAsync());
        Assert.False(await UpdateInstaller.RunAsync()); // and it's ready for the next request
    }
}
