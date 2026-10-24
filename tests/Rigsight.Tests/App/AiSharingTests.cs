using Rigsight.Core.Ai;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.App;

/// <summary>
/// Settings › AI › What answers can read: every look-up belongs to one page, and a page switched off answers that it
/// isn't shared and where to switch it on, so the model says so instead of never knowing those records exist. A plan
/// (RigSight AI) offers only its pages' look-ups.
/// </summary>
public sealed class AiSharingTests
{
    private static readonly string NoHistory = Path.Combine(Path.GetTempPath(), "rigsight-no-such.db");
    private const string Args = "{\"from\":\"2026-10-20\",\"to\":\"2026-10-20\",\"time\":\"2026-10-20 15:00\",\"name\":\"Dota 2\",\"period\":\"year\"}";

    private static AiTools Tools(params string[] off)
    {
        var settings = new RigsightSettings();
        foreach (var page in off) settings.Ai.SetSends(page, false);
        return new AiTools(settings, NoHistory);
    }

    private static List<string> Names(AiTools tools) => [.. tools.Definitions().Select(d => (string)d!["function"]!["name"]!)];

    [Fact]
    public void Every_look_up_is_offered_whatever_is_switched_off()
    {
        var all = Names(new AiTools(new RigsightSettings(), NoHistory));
        Assert.Equal(Names(Tools(AiPages.Network, AiPages.Memory, AiPages.Crashes)), all);
        foreach (var name in new[] { "get_moment", "get_temperatures", "get_fans", "get_apps", "get_memory", "get_storage", "get_network", "get_on_times", "get_running_now",
                     "get_today", "get_sensors_now", "get_rewind", "get_dashboard", "get_drive_scan" })
            Assert.Contains(name, all);
        Assert.Equal(20, all.Count);
    }

    [Fact]
    public void Every_look_up_belongs_to_one_page_and_every_page_has_one()
    {
        var all = Names(new AiTools(new RigsightSettings(), NoHistory));
        foreach (var name in all) Assert.Single(AiPages.All, p => p.Tools.Contains(name));
        foreach (var page in AiPages.All) Assert.NotEmpty(page.Tools.Intersect(all));
        Assert.Equal(16, AiPages.All.Count);
    }

    [Theory]
    [InlineData("get_network", "\"Network\"")]
    [InlineData("get_memory", "\"Memory\"")]
    [InlineData("get_storage", "\"Storage\"")]
    [InlineData("get_crashes", "\"Crashes\"")]
    [InlineData("get_on_times", "\"Reports\"")]
    public void A_switched_off_look_up_says_it_isnt_shared_and_where_to_switch_it_on(string name, string label)
    {
        var tools = Tools(AiPages.Network, AiPages.Memory, AiPages.Storage, AiPages.Crashes, AiPages.Reports);
        string said = tools.Run(name, Args);
        Assert.StartsWith("Not shared", said);
        Assert.Contains(label, said);
        Assert.Contains("Settings › AI", said);
    }

    // What is running now is the Processes page: switched off, nothing is looked at (so no Close button either).
    [Fact]
    public void Running_now_needs_processes_switched_on()
    {
        bool looked = false;
        var tools = Tools(AiPages.Processes);
        tools.SampleRunning = () => { looked = true; return new RunningSnapshot(DateTime.Now, []); };
        string said = tools.Run("get_running_now", "{}");
        Assert.StartsWith("Not shared", said);
        Assert.Contains("\"Processes\"", said);
        Assert.False(looked);
    }

    [Fact]
    public void A_made_up_look_up_is_still_an_error()
    {
        Assert.StartsWith("Error: there is no tool called", new AiTools(new RigsightSettings(), NoHistory).Run("get_passwords", "{}"));
    }

    [Fact]
    public void RigSights_own_numbers_are_never_drawn_from_records_that_arent_shared()
    {
        var tools = Tools(AiPages.Crashes);
        Assert.Empty(tools.Blocks("crashes", [new AiLookUp("get_crashes", new() { ["from"] = "2026-10-01", ["to"] = "2026-10-20" }, 100)]));
    }

    [Fact]
    public void Every_way_of_answering_is_told_what_to_say_when_something_isnt_shared()
    {
        var now = new DateTime(2026, 10, 21, 9, 0, 0);
        foreach (var prompt in AskAnswerTests.EveryPrompt(now).Where(p => !p.Contains("can't look at this PC's records")))
            Assert.Contains("Not shared", prompt);
    }

    [Fact]
    public void Every_look_up_has_a_switch()
    {
        // With every page off, nothing at all answers with records.
        var tools = Tools([.. AiPages.All.Select(p => p.Id)]);
        foreach (var name in Names(tools))
            Assert.StartsWith("Not shared", tools.Run(name, Args));
    }

    // ── Plans ────────────────────────────────────────────────────────────
    [Fact]
    public void A_plan_offers_only_its_pages_look_ups()
    {
        var tools = new AiTools(new RigsightSettings(), NoHistory) { PlanPages = AiPages.DefaultFor(0) };
        var offered = Names(tools);
        Assert.Equal(["get_pc_info", "get_temperatures", "get_today"], [.. offered.Order()]);
        // One from outside the plan (a changed app could still ask) answers that the plan doesn't include it.
        Assert.StartsWith("Not in this plan", tools.Run("get_crashes", Args));
        Assert.Contains("\"Crashes\"", tools.Run("get_crashes", Args));
    }

    [Fact]
    public void Own_key_and_Ollama_have_no_plan_so_every_page_is_offered()
    {
        var tools = new AiTools(new RigsightSettings(), NoHistory) { PlanPages = null };
        Assert.Equal(20, Names(tools).Count);
    }

    [Fact]
    public void Essentials_adds_crashes_and_complete_has_every_page()
    {
        Assert.Contains(AiPages.Crashes, AiPages.DefaultFor(1));
        Assert.DoesNotContain(AiPages.Crashes, AiPages.DefaultFor(0));
        Assert.Equal(AiPages.All.Count, AiPages.DefaultFor(2).Count);
    }

    // ── Moving from the old switches ─────────────────────────────────────
    [Fact]
    public void An_old_switch_turned_off_turns_off_every_page_showing_it()
    {
        var a = new AiSettings { ShareHeat = false, ShareApps = false };
        Assert.Null(a.PagesOff);
        foreach (var page in new[] { AiPages.Temperatures, AiPages.Fans, AiPages.Sensors, AiPages.Apps, AiPages.Games, AiPages.Processes })
            Assert.False(a.Sends(page));
        Assert.True(a.Sends(AiPages.Crashes));
        // The first page switched writes the list down; from then on the old switches no longer count.
        a.SetSends(AiPages.Fans, true);
        Assert.NotNull(a.PagesOff);
        Assert.True(a.Sends(AiPages.Fans));
        Assert.False(a.Sends(AiPages.Temperatures));
        a.ShareHeat = true;
        Assert.False(a.Sends(AiPages.Temperatures));
    }

    [Fact]
    public void Every_page_is_on_for_a_new_install()
    {
        var a = new AiSettings();
        Assert.All(AiPages.All, p => Assert.True(a.Sends(p.Id)));
    }
}
