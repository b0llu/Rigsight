using Rigsight.Core.Ai;

namespace Rigsight.Tests.App;

/// <summary>
/// Own key and Ollama take RigSight AI's prompt (2026-10-27) without what only RigSight AI has, own key in full and Ollama
/// in a short form for small models on the person's own card.
/// </summary>
public sealed class AiPromptShapeTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 9, 0, 0);

    [Fact]
    public void Neither_gets_what_only_RigSight_AI_has()
    {
        foreach (var prompt in new[] { AiPrompt.OwnKey(Now), AiPrompt.Ollama(true, Now), AiPrompt.Ollama(false, Now) })
        {
            Assert.DoesNotContain("[locked:", prompt);                // plans and their locked pages
            Assert.DoesNotContain("plan doesn't include", prompt);
            Assert.DoesNotContain("You can search the web", prompt);  // web search
            Assert.DoesNotContain("site:", prompt);
            Assert.DoesNotContain("You are RigSight", prompt);        // speaking as RigSight ("we")
            Assert.DoesNotContain("we record", prompt);
            Assert.Contains("You can't search the web", prompt);
        }
    }

    [Fact]
    public void Own_key_gets_every_look_up_rule_and_its_examples()
    {
        string prompt = AiPrompt.OwnKey(Now);
        Assert.Contains("start the answer with the correction", prompt);
        Assert.Contains("look up get_today first", prompt);
        Assert.Contains("at most three more look-ups", prompt);
        Assert.Contains("look up get_game", prompt);
        Assert.Contains("get_widgets, get_overlay or get_taskbar", prompt);
        Assert.Contains("[do: app <id>]", prompt);
        Assert.Contains("There's no K drive on this PC", prompt);
        Assert.Contains("[do: app nvidia-app]", prompt);
    }

    [Fact]
    public void Ollama_gets_the_short_form_and_one_example()
    {
        string prompt = AiPrompt.Ollama(true, Now);
        Assert.Contains("start the answer with the correction", prompt);
        Assert.Contains("look up get_report for that stretch first, then at most two more look-ups", prompt);
        Assert.DoesNotContain("look up get_game", prompt);
        Assert.Equal(1, prompt.Split("<example>").Length - 1);
        Assert.True(prompt.Length < AiPrompt.OwnKey(Now).Length * 3 / 4, $"Ollama {prompt.Length} chars, own key {AiPrompt.OwnKey(Now).Length}");
    }

    [Fact]
    public void Without_look_ups_no_look_up_rule_or_button_is_given()
    {
        string prompt = AiPrompt.Ollama(false, Now);
        Assert.DoesNotContain("get_", prompt);
        Assert.DoesNotContain("[do:", prompt);
        Assert.DoesNotContain("<example>", prompt);
    }

    /// <summary>RIGSIGHT_PROMPT_DUMP=folder writes each prompt there to read whole.</summary>
    [Fact]
    public void Prompts_can_be_written_out_to_read()
    {
        string? dir = Environment.GetEnvironmentVariable("RIGSIGHT_PROMPT_DUMP");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "own-key.txt"), AiPrompt.OwnKey(Now));
        File.WriteAllText(Path.Combine(dir, "ollama.txt"), AiPrompt.Ollama(true, Now));
        File.WriteAllText(Path.Combine(dir, "ollama-no-look-ups.txt"), AiPrompt.Ollama(false, Now));
    }
}
