using System.Text.Json.Nodes;
using Rigsight.Core.Ai;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// A paragraph's sources (the approved look of 2026-10-23): a pill at its end from the model's "[src: …]", holding only
/// what this answer really looked at or found; the marker never shows as words, and an answer naming none still gets one.
/// </summary>
public sealed class AskSourcesTests
{
    private const string SearchText = """
        [w1] GPU Benchmarks Hierarchy 2026 - Graphics Card Rankings (https://www.tomshardware.com/reviews/gpu-hierarchy), published 2025-04-17
          The RX 9060 XT 16GB …
        [w2] Best Graphics Cards for Gaming in India 2026 (https://gauravtiwari.org/best-gpus/)
          Prices in India …
        """;

    private static readonly AiLookUp Crashes = new("get_crashes", new JsonObject { ["from"] = "2026-10-16", ["to"] = "2026-10-22" }, 900);
    private static readonly AiLookUp Search = new("search_web", new JsonObject { ["query"] = "best gpu 1080p" }, 400, AiSources.ParseWeb(SearchText));

    [Fact]
    public void The_relays_numbered_results_are_read_with_their_dates()
    {
        var web = AiSources.ParseWeb(SearchText);
        Assert.Equal(2, web.Count);
        Assert.Equal((1, "GPU Benchmarks Hierarchy 2026 - Graphics Card Rankings", "https://www.tomshardware.com/reviews/gpu-hierarchy", "2025-04-17"), (web[0].N, web[0].Title, web[0].Url, web[0].Date));
        Assert.Null(web[1].Date);
        Assert.Empty(AiSources.ParseWeb("Ryzen 7 (https://amd.com)\n  no number"));
    }

    [Fact]
    public void Each_paragraph_gets_the_pill_of_what_it_used()
    {
        var answer = AskViewModel.ToAnswer(
            "The RX 9060 XT 16GB is the best value for your monitor. [src: w1 w2]\nIts 16GB of memory helps newer games. [src: w1]\nDota 2 crashed 4 times this week.\n[src: crashes]",
            [Search, Crashes]);
        Assert.Equal(["tomshardware.com", "gauravtiwari.org"], answer.VerdictRefs!.List.Select(s => s.Site));
        Assert.Equal("17 April 2025", answer.VerdictRefs.List[0].Date);
        var lines = answer.Parts.OfType<AskLine>().ToList();
        Assert.Equal("Its 16GB of memory helps newer games.", lines[0].Text);
        Assert.Equal("tomshardware.com", Assert.Single(lines[0].Refs!.List).Site);
        // A marker alone on a line belongs to the line above it.
        var record = Assert.Single(lines[1].Refs!.List);
        Assert.Equal((true, "Crashes", "Crashes, 16 Oct – 22 Oct", "crashes"), (record.IsApp, record.Site, record.Title, record.Page));
        Assert.DoesNotContain(answer.Parts, p => p is AskLine l && l.Text.Contains("[src", StringComparison.Ordinal));
    }

    [Fact]
    public void A_source_this_answer_didnt_look_at_or_find_never_shows()
    {
        var answer = AskViewModel.ToAnswer("Fine.\nAll normal here. [src: w9 fans crashes]", [Crashes]);
        var only = Assert.Single(answer.Parts.OfType<AskLine>().Single().Refs!.List);
        Assert.Equal("Crashes", only.Site);
        Assert.Null(AskViewModel.ToAnswer("Fine.\nAll normal here. [src: w9 fans]", [Crashes]).Parts.OfType<AskLine>().Single().Refs?.List.FirstOrDefault(s => s.Site == "Fans"));
    }

    [Fact]
    public void An_answer_that_named_no_sources_puts_what_it_looked_at_on_its_last_paragraph()
    {
        var answer = AskViewModel.ToAnswer("Dota 2 crashed 4 times.\nEach was at start.\nThe game updated that morning.", [Crashes]);
        var lines = answer.Parts.OfType<AskLine>().ToList();
        Assert.Null(lines[0].Refs);
        Assert.Equal("Crashes", Assert.Single(lines[^1].Refs!.List).Site);
        // Small talk, nothing looked at: no pill.
        Assert.Null(AskViewModel.ToAnswer("Hey! Ask me anything.", []).Parts.OfType<AskLine>().Single().Refs);
    }

    [Theory]
    [InlineData("It peaked at 82 °C. [src: cra", "It peaked at 82 °C.")]
    [InlineData("It peaked at 82 °C. [sr", "It peaked at 82 °C.")]
    [InlineData("It peaked at 82 °C. [src: crashes]", "It peaked at 82 °C. [src: crashes]")]
    public void A_source_still_being_typed_waits(string text, string shown) => Assert.Equal(shown, AskViewModel.Settled(text));

    [Fact]
    public void The_pill_says_the_first_source_and_how_many_more()
    {
        var refs = new AskRefs(AiSources.Resolve("w1 w2 crashes", [Search, Crashes]));
        Assert.Equal(("tomshardware.c…", "+2", true, "T"), (refs.Label, refs.More, refs.HasMore, refs.Mark));
        Assert.InRange(refs.Tone, 0, 4);
        Assert.Equal(refs.Tone, new AskRefs(AiSources.Resolve("w1", [Search])).Tone);
    }

    [Fact]
    public void Web_results_are_saved_with_the_answer()
    {
        var saved = Rigsight.Core.Ai.SavedLookUp.From(Search);
        Assert.Equal(2, saved.ToLookUp().Web!.Count);
    }

    // The model wrote "[show: memory]" after its Next step line, so the tiles came under the step (2026-10-23).
    [Fact]
    public void The_next_step_closes_the_answer_whatever_order_it_was_written_in()
    {
        var tiles = new AiTiles([new AiTile("27.1 GB", "memory at its fullest", null, AiTone.Plain)]);
        var memory = new AiLookUp("get_memory", new JsonObject { ["from"] = "2026-10-16", ["to"] = "2026-10-23" }, 800);
        var answer = AskViewModel.ToAnswer("Memory is the drag.\nIt peaked at 27.1 GB.\nNext step: open Processes by memory.\n[show: memory]", [memory], _ => [tiles]);
        Assert.IsType<AskNext>(answer.Parts[^1]);
        Assert.Same(tiles, answer.Parts[^2]);
    }

    // "Open the processes page sorted by memory…" had no button: only four pages were known (2026-10-23).
    [Theory]
    [InlineData("Open the processes page sorted by memory while memory is still high.", "processes", "memory", "Open Processes by memory")]
    [InlineData("Open Storage and look at the C drive's biggest folders.", "storage", null, "Open Storage")]
    [InlineData("When it happens again, open Crashes and note the time.", "crashes", null, "Open Crashes")]
    [InlineData("Check the Temperatures page after your next game.", "temperatures", null, "Open Temperatures")]
    public void A_step_that_names_a_page_gets_its_button(string step, string page, string? focus, string label)
    {
        var next = AskViewModel.ToAnswer($"Memory is the drag.\nIt peaked at 27 GB.\nNext step: {step}", []).Parts.OfType<AskNext>().Single();
        var open = Assert.Single(next.Actions).Action;
        Assert.Equal((AiActionKind.OpenPage, page, focus, label), (open.Kind, open.Target, open.Focus, open.Label));
    }

    [Theory]
    [InlineData("Close apps you don't need while memory is held in memory-heavy apps.")]
    [InlineData("Play as usual and see whether the fans calm down.")]
    public void A_common_word_isnt_a_page(string step) =>
        Assert.False(AskViewModel.ToAnswer($"Fine.\nAll normal.\nNext step: {step}", []).Parts.OfType<AskNext>().Single().HasActions);

    [Fact]
    public void Own_key_and_Ollama_prompts_ask_for_paragraph_sources() => Assert.Contains("[src: w2 w4]", AiPrompt.Sources);
}
