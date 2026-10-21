using Rigsight.Core.Ai;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.App;

/// <summary>
/// Settings › Ask RigSight's switches: every look-up is offered to the model, and one that a switch turns off answers
/// that it isn't shared and where to turn it on, so the model says so instead of never knowing those records exist.
/// </summary>
public sealed class AiSharingTests
{
    private static readonly string NoHistory = Path.Combine(Path.GetTempPath(), "rigsight-no-such.db");

    private static AiTools Tools(Action<AiSettings> off)
    {
        var settings = new RigsightSettings();
        off(settings.Ai);
        return new AiTools(settings, NoHistory);
    }

    private static List<string> Names(AiTools tools) => [.. tools.Definitions().Select(d => (string)d!["function"]!["name"]!)];

    [Fact]
    public void Every_look_up_is_offered_whatever_is_turned_off()
    {
        var all = Names(new AiTools(new RigsightSettings(), NoHistory));
        Assert.Equal(Names(Tools(a => { a.ShareNetwork = false; a.ShareMemory = false; a.ShareCrashes = false; })), all);
        foreach (var name in new[] { "get_moment", "get_temperatures", "get_fans", "get_apps", "get_memory", "get_storage", "get_network", "get_on_times" })
            Assert.Contains(name, all);
        Assert.Equal(14, all.Count);
    }

    [Theory]
    [InlineData("get_network", "\"Internet use\"")]
    [InlineData("get_memory", "\"Memory and storage\"")]
    [InlineData("get_storage", "\"Memory and storage\"")]
    [InlineData("get_crashes", "\"Crashes and shut-offs\"")]
    [InlineData("get_on_times", "\"When your PC was on and in use\"")]
    public void A_turned_off_look_up_says_it_isnt_shared_and_where_to_turn_it_on(string name, string label)
    {
        var tools = Tools(a => { a.ShareNetwork = false; a.ShareMemory = false; a.ShareCrashes = false; a.ShareUsage = false; });
        string said = tools.Run(name, "{\"from\":\"2026-10-20\",\"to\":\"2026-10-20\"}");
        Assert.StartsWith("Not shared", said);
        Assert.Contains(label, said);
        Assert.Contains("Settings › Ask RigSight", said);
    }

    [Fact]
    public void A_look_up_that_needs_either_of_two_switches_names_both_when_both_are_off()
    {
        string said = Tools(a => { a.ShareHeat = false; a.ShareApps = false; }).Run("get_report", "{\"from\":\"2026-10-20\",\"to\":\"2026-10-20\"}");
        Assert.Contains("\"Temperatures and fans\" or \"Apps and games you use\"", said);
    }

    [Fact]
    public void A_made_up_look_up_is_still_an_error()
    {
        Assert.StartsWith("Error: there is no tool called", new AiTools(new RigsightSettings(), NoHistory).Run("get_passwords", "{}"));
    }

    [Fact]
    public void RigSights_own_numbers_are_never_drawn_from_records_that_arent_shared()
    {
        var tools = Tools(a => a.ShareCrashes = false);
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
        // With every switch off, nothing at all answers with records.
        var tools = Tools(a => { a.ShareCrashes = a.ShareHeat = a.ShareApps = a.ShareChanges = a.ShareMemory = a.ShareNetwork = a.ShareParts = a.ShareUsage = false; });
        foreach (var name in Names(tools))
            Assert.StartsWith("Not shared", tools.Run(name, "{\"from\":\"2026-10-20\",\"to\":\"2026-10-20\",\"time\":\"2026-10-20 15:00\",\"name\":\"Dota 2\"}"));
    }
}
