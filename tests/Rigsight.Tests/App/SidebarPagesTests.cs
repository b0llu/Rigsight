using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// Which pages the sidebar shows at once (2026-10-25): the basics or every page, asked once of a new install and never
/// of a PC updating from before; and icons only, kept between starts.
/// </summary>
[Collection("UI")]
public sealed class SidebarPagesTests
{
    private static (SidebarViewModel Sidebar, SettingsModel Settings) Sidebar(RigsightSettings? start = null)
    {
        var settings = Kit.OfflineSettings(start);
        return (Ui.Run(() => new SidebarViewModel(settings, _ => { })), settings);
    }

    [Fact]
    public void Only_a_new_install_is_asked_which_pages_to_show()
    {
        string folder = TestEnvironment.NewFolder("pages-asked");
        Assert.True(SettingsStore.Load(Path.Combine(folder, "settings.json")).Sidebar.AskPages);
        // Settings from before (any file at all): never asked, everything stays as it was.
        string old = Path.Combine(folder, "old.json");
        File.WriteAllText(old, """{ "SettingsVersion": 10, "Sidebar": { "Hidden": ["fans"] } }""");
        var loaded = SettingsStore.Load(old);
        Assert.False(loaded.Sidebar.AskPages);
        Assert.Null(loaded.Sidebar.Pages);
        Assert.Equal(["fans"], loaded.Sidebar.Hidden);
    }

    [Fact]
    public void The_basics_hide_every_other_page_but_never_a_dashboard_and_everything_shows_them_all_again()
    {
        var start = new RigsightSettings();
        start.Sidebar.AskPages = true;
        start.Sidebar.Hidden.Add("dash-1");
        var (sidebar, settings) = Sidebar(start);
        Ui.Run(() =>
        {
            Assert.True(sidebar.AskPages);
            Assert.Equal("", sidebar.PagesMode);
            sidebar.PickPagesCommand.Execute("basic");
            Assert.False(sidebar.AskPages);
            Assert.Equal("basic", sidebar.PagesMode);
            Assert.Equal(["apps", "processes", "timeline", "fans", "memory", "network", "sensors", "widgets", "overlay", "taskbar"],
                settings.Current.Sidebar.Hidden.Where(k => k != "dash-1"));
            Assert.Contains("dash-1", settings.Current.Sidebar.Hidden);
            Assert.Equal(["home", "reports", "games", "crashes"], sidebar.Main.Where(e => e.IsVisible).Select(e => e.Key));
            Assert.Equal(["temperatures", "storage"], sidebar.Hardware.Where(e => e.IsVisible).Select(e => e.Key));
            Assert.False(sidebar.OnScreenGroup.HeadingInSidebar);
            Assert.False(sidebar.ShowNewDashboard);
            Assert.Equal("· 10 pages hidden", sidebar.HiddenSummary);

            // The editor can show one again, and its heading comes back with it.
            sidebar.OnScreen.Single(e => e.Key == "overlay").IsShown = true;
            Assert.True(sidebar.OnScreenGroup.HeadingInSidebar);

            sidebar.PickPagesCommand.Execute("full");
            Assert.Equal("full", sidebar.PagesMode);
            Assert.Equal(["dash-1"], settings.Current.Sidebar.Hidden);
            Assert.True(sidebar.ShowNewDashboard);
            Assert.All(sidebar.Main.Concat(sidebar.Hardware).Concat(sidebar.OnScreen), e => Assert.True(e.IsVisible));
        });
    }

    [Fact]
    public void Icons_only_is_kept_and_the_title_bar_button_says_what_it_does()
    {
        var (sidebar, settings) = Sidebar();
        Ui.Run(() =>
        {
            Assert.Equal("Collapse the sidebar", sidebar.IconsTip);
            Assert.Equal("", sidebar.HandleGlyph);
            sidebar.ToggleIconsCommand.Execute(null);
            Assert.True(settings.Current.Sidebar.IconsOnly);
            Assert.Equal("Expand the sidebar", sidebar.IconsTip);
            Assert.Equal("", sidebar.HandleGlyph);
        });
        var (again, _) = Sidebar(settings.Current);
        Assert.True(Ui.Run(() => again.IconsOnly));
    }
}
