using System.Text.Json.Nodes;
using Rigsight.Core.Ai;
using Rigsight.Core.Settings;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// How a model's text becomes an answer in the Ask window: the plain statement first, RigSight's own numbers where a
/// marker asks for them (and only for a record it looked at), its tables, and the next step in a box of its own.
/// </summary>
public sealed class AskAnswerTests
{
    private static readonly AiLookUp Crashes = new("get_crashes", new JsonObject { ["from"] = "2026-09-20", ["to"] = "2026-10-20" }, 100);

    [Fact]
    public void Small_talk_is_only_words()
    {
        var answer = AskViewModel.ToAnswer("Hey! Ask me anything about your PC.", [], _ => throw new InvalidOperationException("no marker, nothing drawn"));
        Assert.Equal("Hey! Ask me anything about your PC.", answer.Verdict);
        Assert.Empty(answer.Parts);
        Assert.Empty(answer.Looked);
    }

    [Fact]
    public void A_marker_draws_RigSights_numbers_in_its_place_once()
    {
        var tiles = new AiTiles([new AiTile("19", "sudden shut-offs", null, AiTone.Warm)]);
        var asked = new List<string>();
        var answer = AskViewModel.ToAnswer("Mostly yes.\n[show: crashes]\nMost look like power cuts.\n[show: crashes]", [Crashes],
            what => { asked.Add(what); return [tiles]; });
        Assert.Equal(["crashes"], asked);
        Assert.Same(tiles, answer.Parts[0]);
        Assert.Equal("Most look like power cuts.", Assert.IsType<AskLine>(answer.Parts[1]).Text);
        Assert.Equal(2, answer.Parts.Count);
    }

    [Fact]
    public void A_marker_with_nothing_to_draw_leaves_no_trace()
    {
        var answer = AskViewModel.ToAnswer("Fine.\n[show: temps]\nAll good.", [], _ => []);
        Assert.Equal("All good.", Assert.IsType<AskLine>(Assert.Single(answer.Parts)).Text);
    }

    [Fact]
    public void A_table_the_model_wrote_becomes_a_table()
    {
        var answer = AskViewModel.ToAnswer("Here's how they compare.\n| Part | Normal | Yours |\n|---|---|---|\n| CPU | up to 90° | 82° |\n| GPU | up to 83° | 72° |", []);
        var table = Assert.IsType<AskTable>(Assert.Single(answer.Parts));
        Assert.Equal(["Part", "Normal", "Yours"], table.Headings);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(["GPU", "up to 83°", "72°"], table.Rows[1]);
    }

    [Fact]
    public void The_next_step_gets_its_box_and_the_page_the_records_are_on()
    {
        var answer = AskViewModel.ToAnswer("A power cut, most likely.\nNext step: check whether other things in the room lost power too.", [Crashes]);
        var next = Assert.IsType<AskNext>(Assert.Single(answer.Parts));
        Assert.Equal("Check whether other things in the room lost power too.", next.Text);
        Assert.Equal("crashes", next.Opens);
    }

    [Fact]
    public void A_marker_for_something_it_didnt_look_at_or_doesnt_exist_draws_nothing()
    {
        var tools = new AiTools(new RigsightSettings(), Path.Combine(Path.GetTempPath(), "rigsight-no-such.db"));
        Assert.Empty(tools.Blocks("crashes", []));
        Assert.Empty(tools.Blocks("temps", [Crashes]));
        Assert.Empty(tools.Blocks("psu", [Crashes]));
        var answer = AskViewModel.ToAnswer("Probably the power supply.\n[show: psu]\n[show: crashes]", [], what => tools.Blocks(what, []));
        Assert.Empty(answer.Parts);
    }

    [Fact]
    public void Only_RigSight_AI_is_told_it_can_search_the_web()
    {
        var now = new DateTime(2026, 10, 20, 15, 0, 0);
        Assert.Contains("You can't search the web", AiPrompt.System(true, withSearch: false, now));
        Assert.DoesNotContain("You can search the web", AiPrompt.System(true, withSearch: false, now));
        Assert.Contains("You can search the web", AiPrompt.System(true, withSearch: true, now));
        // The same rules for how sure to be, whichever way it answers.
        foreach (var search in new[] { false, true })
            Assert.Contains("not proof that a part is failing", AiPrompt.System(true, search, now));
    }

    [Fact]
    public void A_long_conversation_sends_its_first_exchange_and_the_latest()
    {
        var all = Enumerable.Range(0, 30).Select(i => new AiTurn(i % 2 == 0, $"m{i}")).ToList();
        var sent = AiTurns.Recent(all);
        Assert.Equal(AiTurns.MaxSent, sent.Count);
        Assert.Equal(["m0", "m1"], sent.Take(2).Select(t => t.Text));
        Assert.True(sent[2].FromUser);
        Assert.Equal("m29", sent[^1].Text);
        Assert.Equal(6, AiTurns.Recent(all.Take(6).ToList()).Count);
    }

    [Theory]
    [InlineData("[next step: check if the game updated.]")]
    [InlineData("- Next step: check if the game updated.")]
    [InlineData("**Next step:** check if the game updated.")]
    public void A_next_step_written_loosely_still_gets_its_box(string line)
    {
        var answer = AskViewModel.ToAnswer("The records don't say why.\n" + line, []);
        Assert.Equal("Check if the game updated.", Assert.IsType<AskNext>(Assert.Single(answer.Parts)).Text);
    }

    [Fact]
    public void Every_way_of_answering_is_told_not_to_end_on_offers()
    {
        var now = new DateTime(2026, 10, 20, 15, 0, 0);
        foreach (var (look, search) in new[] { (true, false), (false, false), (true, true) })
        {
            string prompt = AiPrompt.System(look, search, now);
            Assert.Contains("Never end with offers", prompt);
            Assert.Contains("never tell them to turn on recording", prompt);
        }
    }

    [Fact]
    public void A_next_step_that_is_only_a_question_back_is_dropped()
    {
        var answer = AskViewModel.ToAnswer("Your PC was busy yesterday.\n**Next Step**: Want to dive deeper into a specific game or app?", []);
        Assert.Empty(answer.Parts);
    }

    [Fact]
    public void Stars_around_a_name_go_and_points_keep_theirs()
    {
        var answer = AskViewModel.ToAnswer("Busy day.\n* mostly *Risk of Rain 2*, then 2*3 checks", []);
        var line = Assert.IsType<AskLine>(Assert.Single(answer.Parts));
        Assert.Equal("mostly Risk of Rain 2, then 2*3 checks", line.Text);
        Assert.True(line.IsPoint);
    }

    [Fact]
    public void Every_way_of_answering_is_told_to_stay_in_the_period_asked_about()
    {
        var now = new DateTime(2026, 10, 21, 9, 0, 0);
        foreach (var search in new[] { false, true })
            Assert.Contains("Stay in the period they ask about", AiPrompt.System(true, search, now));
    }

    [Fact]
    public void A_next_step_that_asks_them_to_tell_it_something_is_dropped_and_code_marks_go()
    {
        var answer = AskViewModel.ToAnswer("Most were Rigsight starting up.\nRigsight.Agent had a `.NET` error.\nNext step: Tell me the name of the app, and I'll look up the details.", []);
        Assert.Equal("Rigsight.Agent had a .NET error.", Assert.IsType<AskLine>(Assert.Single(answer.Parts)).Text);
    }

    [Theory]
    [InlineData("Next step: none.")]
    [InlineData("Next step: N/A")]
    [InlineData("Next step: Ask me about a specific game.")]
    public void A_next_step_with_nothing_to_do_gets_no_box(string line)
    {
        Assert.Empty(AskViewModel.ToAnswer("Apex Legends used the most memory.\n" + line, []).Parts);
    }
}
