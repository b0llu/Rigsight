using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// A new install's welcome and its first-visit tips (approved mock-up "RigSight first-run journey", 2026-10-30): three
/// steps (what RigSight is, the pages, Ask RigSight), The basics picked to start, never after an update; a tip the first
/// time each page opens, never while a card covers the page, once each.
/// </summary>
[Collection("UI")]
public sealed class WelcomeTests
{
    private sealed record Kit2(WelcomeViewModel Welcome, SidebarViewModel Sidebar, SettingsModel Settings, List<string> Said);

    private static Kit2 Start(Action<RigsightSettings>? set = null)
    {
        var s = new RigsightSettings();
        s.Sidebar.AskPages = true;
        s.FirstVisitTips = true;
        set?.Invoke(s);
        var settings = Kit.OfflineSettings(s);
        var said = new List<string>();
        var sidebar = Ui.Run(() => new SidebarViewModel(settings, _ => { }));
        var welcome = Ui.Run(() => new WelcomeViewModel(sidebar, settings, askFirst: () => said.Add("ask"), later: () => said.Add("later")));
        return new Kit2(welcome, sidebar, settings, said);
    }

    [Fact]
    public void A_new_install_gets_the_welcome_and_its_tips_and_an_update_gets_neither()
    {
        string folder = TestEnvironment.NewFolder("welcome-new");
        var fresh = SettingsStore.Load(Path.Combine(folder, "settings.json"));
        Assert.True(fresh.Sidebar.AskPages);
        Assert.True(fresh.FirstVisitTips);
        string old = Path.Combine(folder, "old.json");
        File.WriteAllText(old, """{ "SettingsVersion": 11, "WhatsNewSeen": "0.36.0" }""");
        var updated = SettingsStore.Load(old);
        Assert.False(updated.Sidebar.AskPages);
        Assert.False(updated.FirstVisitTips);
    }

    [Fact]
    public void Three_steps_with_the_basics_picked_to_start_and_next_asks_after_the_pages()
    {
        var k = Start();
        Assert.True(k.Welcome.IsOpen);
        Assert.True(k.Welcome.IsAbout);
        Assert.Equal(3, k.Welcome.Steps);
        Assert.True(k.Welcome.BasicPicked);
        k.Welcome.NextCommand.Execute(null);
        Assert.True(k.Welcome.IsPages);
        k.Welcome.PickCommand.Execute("full");
        Assert.True(k.Welcome.FullPicked);
        Assert.False(k.Welcome.BasicPicked);
        k.Welcome.BackCommand.Execute(null);
        Assert.True(k.Welcome.IsAbout);
        k.Welcome.SkipCommand.Execute(null);
        Assert.True(k.Welcome.IsPages);
        // Picking alone answers nothing: Next does, on the way to Ask.
        Assert.True(k.Sidebar.AskPages);
        k.Welcome.NextCommand.Execute(null);
        Assert.True(k.Welcome.IsAsk);
        Assert.True(k.Sidebar.AskPages);
    }

    [Fact]
    public void Ask_your_first_question_answers_the_pages_and_opens_the_ask_box()
    {
        var k = Start();
        k.Welcome.NextCommand.Execute(null);
        k.Welcome.NextCommand.Execute(null);
        k.Welcome.AskFirstCommand.Execute(null);
        Assert.False(k.Welcome.IsOpen);
        Assert.Equal("basic", k.Settings.Current.Sidebar.Pages);
        Assert.Contains("fans", k.Settings.Current.Sidebar.Hidden);
        Assert.Equal(["ask"], k.Said);
    }

    [Fact]
    public void Later_with_everything_shows_every_page_and_points_at_the_ask_box()
    {
        var k = Start();
        k.Welcome.NextCommand.Execute(null);
        k.Welcome.PickCommand.Execute("full");
        k.Welcome.NextCommand.Execute(null);
        k.Welcome.LaterCommand.Execute(null);
        Assert.False(k.Welcome.IsOpen);
        Assert.Equal("full", k.Settings.Current.Sidebar.Pages);
        Assert.Empty(k.Settings.Current.Sidebar.Hidden);
        Assert.Equal(["later"], k.Said);
    }

    [Fact]
    public void With_the_AI_off_there_are_two_steps_and_nothing_about_asking()
    {
        var k = Start(s => s.Ai.Mode = AiMode.Off);
        Assert.Equal(2, k.Welcome.Steps);
        k.Welcome.NextCommand.Execute(null);
        k.Welcome.NextCommand.Execute(null);
        Assert.False(k.Welcome.IsOpen);
        Assert.Equal("basic", k.Settings.Current.Sidebar.Pages);
        Assert.Empty(k.Said);
    }

    // ── First-visit tips ─────────────────────────────────────────────────

    [Fact]
    public void A_page_s_tip_shows_on_its_first_visit_once_and_never_on_home()
    {
        var settings = Kit.OfflineSettings(new RigsightSettings { FirstVisitTips = true });
        var tips = new FirstVisitTipsViewModel(settings, () => false);
        tips.Visit("home");
        Assert.False(tips.IsOpen);
        tips.Visit("crashes");
        Assert.True(tips.IsPageTip);
        Assert.Equal("Crashes from the last 90 days", tips.Title);
        tips.SeenCommand.Execute(null);
        Assert.False(tips.IsOpen);
        tips.Visit("crashes");
        Assert.False(tips.IsOpen);
        Assert.Contains("crashes", settings.Current.TipsSeen);
        // Every dashboard of one's own shares one tip.
        tips.Visit("custom:abc");
        Assert.True(tips.IsPageTip);
        Assert.Equal("dashboard", tips.Showing);
    }

    [Fact]
    public void A_tip_waits_while_a_card_covers_the_page_and_goes_when_one_comes_over_it()
    {
        bool covered = true;
        var tips = new FirstVisitTipsViewModel(Kit.OfflineSettings(new RigsightSettings { FirstVisitTips = true }), () => covered);
        tips.Visit("reports");
        Assert.False(tips.IsOpen);
        covered = false;
        tips.Visit("reports");
        Assert.True(tips.IsPageTip);
        covered = true;
        tips.Covered();
        Assert.False(tips.IsOpen);
        // Not seen: the next clear visit shows it again.
        covered = false;
        tips.Visit("reports");
        Assert.True(tips.IsPageTip);
        // One at a time: another page's tip takes its place.
        tips.Visit("games");
        Assert.Equal("games", tips.Showing);
    }

    [Fact]
    public void An_install_from_before_gets_no_tips()
    {
        var tips = new FirstVisitTipsViewModel(Kit.OfflineSettings(new RigsightSettings()), () => false);
        tips.Visit("crashes");
        tips.ShowAskTip();
        Assert.False(tips.IsOpen);
    }

    [Fact]
    public void The_ask_box_s_tip_stays_over_page_visits_until_the_box_is_used()
    {
        var settings = Kit.OfflineSettings(new RigsightSettings { FirstVisitTips = true });
        var tips = new FirstVisitTipsViewModel(settings, () => false);
        tips.ShowAskTip();
        Assert.True(tips.IsAskTip);
        tips.Visit("crashes");
        Assert.True(tips.IsAskTip);
        tips.AskUsed();
        Assert.False(tips.IsOpen);
        Assert.Contains(FirstVisitTipsViewModel.AskTip, settings.Current.TipsSeen);
        tips.ShowAskTip();
        Assert.False(tips.IsOpen);
    }
}
