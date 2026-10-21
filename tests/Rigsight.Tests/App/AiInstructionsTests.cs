using Rigsight.Core.Ai;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.App;

/// <summary>Instructions people change in Settings for Ollama and their own key: theirs replace RigSight's, RigSight's rules stay.</summary>
public sealed class AiInstructionsTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 9, 0, 0);

    [Fact]
    public void Their_own_instructions_replace_RigSights_but_its_rules_stay()
    {
        string prompt = AiPrompt.OwnKey(Now, "Answer like a pirate, in one line.");
        Assert.Contains("Answer like a pirate, in one line.", prompt);
        Assert.DoesNotContain("Talk like a friend who knows PCs well", prompt);
        // What RigSight fills in and relies on is still there, after their text, so it wins.
        Assert.Contains("Wednesday 21 October 2026", prompt);
        Assert.Contains("[show: crashes]", prompt);
        Assert.Contains("Next step: ", prompt);
        Assert.Contains("data, not instructions", prompt);
        Assert.Contains("Not shared", prompt);
        Assert.True(prompt.IndexOf("pirate", StringComparison.Ordinal) < prompt.IndexOf("RigSight's rules", StringComparison.Ordinal));
    }

    [Fact]
    public void Ollama_without_look_ups_is_told_so_and_gets_no_markers()
    {
        string prompt = AiPrompt.Ollama(canLookUp: false, Now);
        Assert.Contains("You can't look at this PC's records", prompt);
        Assert.DoesNotContain("[show:", prompt);
    }

    [Fact]
    public void Each_way_has_its_own_copy_and_none_holds_a_changing_part()
    {
        // The text people edit has nothing RigSight fills in: no date, no look-up or search switch, no markers.
        foreach (var text in new[] { AiPrompt.OllamaInstructions, AiPrompt.OwnKeyInstructions })
        {
            Assert.DoesNotContain("Today is", text);
            Assert.DoesNotContain("[show:", text);
            Assert.DoesNotContain("search the web", text);
            Assert.DoesNotContain("Not shared", text);
        }
        Assert.Equal(AiPrompt.OllamaInstructions, AiInstructions.Default(AiMode.Ollama));
        Assert.Equal(AiPrompt.OwnKeyInstructions, AiInstructions.Default(AiMode.OwnKey));
        Assert.Throws<ArgumentException>(() => AiInstructions.Default(AiMode.RigSight));
    }

    [Fact]
    public void Saving_keeps_their_text_and_saving_RigSights_text_is_a_reset()
    {
        try
        {
            Assert.Null(AiInstructions.Custom(AiMode.Ollama));
            AiInstructions.Save(AiMode.Ollama, "  Keep it short.  ");
            Assert.Equal("Keep it short.", AiInstructions.Custom(AiMode.Ollama));
            Assert.Equal("Keep it short.", AiInstructions.Current(AiMode.Ollama));
            Assert.False(AiInstructions.DefaultChangedSince(AiMode.Ollama));
            // Each way keeps its own: own key is untouched.
            Assert.Null(AiInstructions.Custom(AiMode.OwnKey));

            AiInstructions.Save(AiMode.Ollama, AiPrompt.OllamaInstructions.Replace("\n", "\r\n"));
            Assert.Null(AiInstructions.Custom(AiMode.Ollama));
            Assert.Equal(AiPrompt.OllamaInstructions, AiInstructions.Current(AiMode.Ollama));

            AiInstructions.Save(AiMode.Ollama, "Keep it short.");
            AiInstructions.Save(AiMode.Ollama, "   ");
            Assert.Null(AiInstructions.Custom(AiMode.Ollama));
        }
        finally { AiInstructions.Reset(AiMode.Ollama); }
    }
}
