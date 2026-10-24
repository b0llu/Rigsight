using System.Text.Json.Nodes;
using Rigsight.Core.Ai;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;
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
        // A reply of one line is plain words, not a bold headline.
        Assert.Equal("", answer.Verdict);
        Assert.Equal("Hey! Ask me anything about your PC.", Assert.IsType<AskLine>(Assert.Single(answer.Parts)).Text);
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
    public void The_next_step_gets_its_box_and_a_button_only_for_the_page_it_names()
    {
        var answer = AskViewModel.ToAnswer("A power cut, most likely.\nNext step: check whether other things in the room lost power too.", [Crashes]);
        var next = Assert.IsType<AskNext>(Assert.Single(answer.Parts));
        Assert.Equal("Check whether other things in the room lost power too.", next.Text);
        Assert.Null(next.Opens);
        var named = AskViewModel.ToAnswer("A power cut, most likely.\nNext step: when it happens again, open Crashes and note the time.", [Crashes]);
        Assert.Equal("crashes", Assert.IsType<AskNext>(Assert.Single(named.Parts)).Opens);
    }

    [Fact]
    public void A_marker_for_something_it_didnt_look_at_or_doesnt_exist_draws_nothing()
    {
        var tools = new AiTools(new RigsightSettings(), Path.Combine(Path.GetTempPath(), "rigsight-no-such.db"));
        Assert.Empty(tools.Blocks("crashes", []));
        Assert.Empty(tools.Blocks("temps", [Crashes]));
        Assert.Empty(tools.Blocks("psu", [Crashes]));
        var answer = AskViewModel.ToAnswer("Probably the power supply.\n[show: psu]\n[show: crashes]", [], what => tools.Blocks(what, []));
        OnlyWords(answer, "Probably the power supply.");
    }

    [Fact]
    public void Ollama_and_own_key_are_told_they_cant_search_the_web()
    {
        var now = new DateTime(2026, 10, 20, 15, 0, 0);
        foreach (var prompt in new[] { AiPrompt.Ollama(true, now), AiPrompt.OwnKey(now) })
        {
            Assert.Contains("You can't search the web", prompt);
            Assert.DoesNotContain("You can search the web", prompt);
        }
        // RigSight AI's own prompt, with web search, lives in the relay (and its tests).
        // The same rules for how sure to be, whichever way it answers.
        foreach (var prompt in EveryPrompt(now))
            Assert.Contains("not proof that a part is failing", prompt);
    }

    /// <summary>Every copy of the prompt in the app: Ollama with and without look-ups, and own key (RigSight AI's is in the relay).</summary>
    internal static IEnumerable<string> EveryPrompt(DateTime now) =>
        [AiPrompt.Ollama(true, now), AiPrompt.Ollama(false, now), AiPrompt.OwnKey(now)];

    [Fact]
    public void A_marker_written_inside_a_sentence_still_draws_its_card()
    {
        var asked = new List<string>();
        var answer = AskViewModel.ToAnswer("Your PC shut off.\nWe put 9 down to a power cut, which could point to the mains. [show: crashes]\nCheck the lights.",
            [], marker => { asked.Add(marker); return []; });
        Assert.Equal(["crashes"], asked);
        Assert.DoesNotContain(answer.Parts.OfType<AskLine>(), l => l.Text.Contains("[show", StringComparison.Ordinal));
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
        foreach (var prompt in EveryPrompt(now))
        {
            Assert.Contains("Never end with offers", prompt);
            Assert.Contains("never tell them to turn on recording", prompt);
        }
    }

    [Fact]
    public void A_next_step_that_is_only_a_question_back_is_dropped()
    {
        var answer = AskViewModel.ToAnswer("Your PC was busy yesterday.\n**Next Step**: Want to dive deeper into a specific game or app?", []);
        OnlyWords(answer, "Your PC was busy yesterday.");
    }

    [Fact]
    public void Stars_around_a_name_are_italic_and_points_keep_theirs()
    {
        var answer = AskViewModel.ToAnswer("Busy day.\n* mostly *Risk of Rain 2*, then 2*3 checks", []);
        var line = Assert.IsType<AskLine>(Assert.Single(answer.Parts));
        // The marks stay for the view to draw (italic), and read as words they're gone; "2*3" is maths, not a mark.
        Assert.Equal("mostly *Risk of Rain 2*, then 2*3 checks", line.Text);
        Assert.Equal("mostly Risk of Rain 2, then 2*3 checks", Rigsight.Controls.Md.Plain(line.Text));
        Assert.True(line.IsPoint);
    }

    [Fact]
    public void Every_way_of_answering_is_told_to_stay_in_the_period_asked_about()
    {
        var now = new DateTime(2026, 10, 21, 9, 0, 0);
        foreach (var prompt in EveryPrompt(now))
            Assert.Contains("Stay in the period they ask about", prompt);
    }

    [Fact]
    public void A_next_step_that_asks_them_to_tell_it_something_is_dropped_and_code_is_drawn_as_code()
    {
        var answer = AskViewModel.ToAnswer("Most were Rigsight starting up.\nRigsight.Agent had a `.NET` error.\nNext step: Tell me the name of the app, and I'll look up the details.", []);
        Assert.Equal("Rigsight.Agent had a .NET error.", Rigsight.Controls.Md.Plain(Assert.IsType<AskLine>(Assert.Single(answer.Parts)).Text));
    }

    [Theory]
    [InlineData("Next step: none.")]
    [InlineData("Next step: N/A")]
    [InlineData("Next step: Ask me about a specific game.")]
    public void A_next_step_with_nothing_to_do_gets_no_box(string line)
    {
        OnlyWords(AskViewModel.ToAnswer("Apex Legends used the most memory.\n" + line, []), "Apex Legends used the most memory.");
    }

    [Fact]
    public void An_answer_sent_again_says_which_records_it_came_from()
    {
        var report = new AiLookUp("get_report", new JsonObject { ["from"] = "2026-10-14", ["to"] = "2026-10-21" }, 6000);
        string sent = (string)AiTurns.Message(new AiTurn(false, "You were on it for 83 hours.", [report]))["content"]!;
        Assert.StartsWith("You were on it for 83 hours.", sent);
        Assert.Contains("Records you looked at for this answer: get_report from 2026-10-14, to 2026-10-21", sent);
        // A question goes as it was asked.
        Assert.Equal("why?", (string)AiTurns.Message(new AiTurn(true, "why?"))["content"]!);
    }

    [Fact]
    public void The_records_note_copied_into_an_answer_is_left_out()
    {
        var answer = AskViewModel.ToAnswer("Yes, 82 °C is fine.\nIt was a short spike.\n(Records you looked at for this answer: get_temperatures from 2026-10-14.)", []);
        Assert.Equal("Yes, 82 °C is fine.", answer.Verdict);
        Assert.Equal("It was a short spike.", Assert.IsType<AskLine>(Assert.Single(answer.Parts)).Text);
    }

    [Theory]
    [InlineData("Your CPU is fine.\n[show: te", "Your CPU is fine.")]
    [InlineData("Your CPU is fine.\n| Part | Peak", "Your CPU is fine.")]
    [InlineData("Your CPU is fine.\nNext st", "Your CPU is fine.")]
    [InlineData("Your CPU is fine.\nIt peaked at 82", "Your CPU is fine.\nIt peaked at 82")]
    public void A_line_still_being_typed_that_could_become_a_chart_waits(string text, string shown)
    {
        Assert.Equal(shown, AskViewModel.Settled(text));
    }

    [Fact]
    public void The_ask_next_line_becomes_questions_under_the_answer_not_words()
    {
        var answer = AskViewModel.ToAnswer("A power cut, most likely.\nThe PC was idle.\nNext step: open Crashes and note the time.\n"
            + "Ask next: Was it the same last week? | \"Which game heats the GPU most?\" | Was it the same last week? | Is my PSU ok?", [Crashes]);
        // At most three, duplicates and quotes dropped, and the line itself never shows as text.
        Assert.Equal(["Was it the same last week?", "Which game heats the GPU most?", "Is my PSU ok?"], answer.Suggestions);
        Assert.DoesNotContain(answer.Parts, p => p is AskLine l && l.Text.Contains("Ask next", StringComparison.OrdinalIgnoreCase));
        Assert.IsType<AskNext>(answer.Parts[^1]);
    }

    [Theory]
    [InlineData("- Ask next: Is it normal? | What changed?")]
    [InlineData("**Ask next:** Is it normal? | What changed?")]
    [InlineData("Preguntas siguientes: Is it normal? | What changed?")]
    public void The_ask_next_line_as_models_write_it(string line)
    {
        var answer = AskViewModel.ToAnswer("All normal.\nIt peaked at 71 Â°C.\n" + line, []);
        Assert.Equal(["Is it normal?", "What changed?"], answer.Suggestions);
        Assert.Single(answer.Parts);
    }

    [Fact]
    public void Only_the_latest_finished_answer_offers_questions()
    {
        Ui.Run(() =>
        {
            var window = new AskViewModel(Kit.OfflineSettings(new RigsightSettings()));
            var talk = new AskConversation { Title = "t" };
            window.Conversations.Add(talk);
            window.Current = talk;
            var first = AskViewModel.ToAnswer("Fine.\nAll normal.\nAsk next: What changed?", []);
            talk.Messages.Add(new AskQuestion("q"));
            talk.Messages.Add(first);
            Assert.Equal(["What changed?"], window.NextQuestions);
            // A note under it keeps them; a new question takes them away until its answer comes.
            talk.Messages.Add(new AskStopped("Web search wasn't working."));
            Assert.True(window.HasNextQuestions);
            talk.Messages.Add(new AskQuestion("What changed?"));
            Assert.False(window.HasNextQuestions);
            window.IsAnswering = true;
            talk.Messages.Add(AskViewModel.ToAnswer("Nothing.\nNo changes.\nAsk next: Is it hot?", []));
            Assert.Empty(window.NextQuestions);
            window.IsAnswering = false;
            Assert.Equal(["Is it hot?"], window.NextQuestions);
        });
    }

    // Nothing but the one sentence: no box, no drawn numbers, and (alone) not a bold headline.
    private static void OnlyWords(AskAnswer answer, string text)
    {
        Assert.Equal("", answer.Verdict);
        Assert.Equal(text, Assert.IsType<AskLine>(Assert.Single(answer.Parts)).Text);
    }
}
