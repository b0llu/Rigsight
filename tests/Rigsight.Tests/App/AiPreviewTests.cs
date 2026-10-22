using Rigsight.Core.Ai;

namespace Rigsight.Tests.App;

/// <summary>The AI helper stays hidden until the preview is on for a PC (a file made by "--ai-preview on").</summary>
public sealed class AiPreviewTests
{
    [Theory]
    [InlineData(false, false, null, false)] // an installed RigSight: hidden
    [InlineData(false, true, null, true)]   // given the preview
    [InlineData(true, false, null, true)]   // a test copy
    [InlineData(true, false, "0", false)]   // a test copy checking the hidden app
    [InlineData(false, true, "0", false)]
    [InlineData(false, false, "1", true)]
    public void Hidden_until_the_preview_is_on(bool testInstance, bool fileThere, string? env, bool on) =>
        Assert.Equal(on, AiPreview.Decide(testInstance, fileThere, env));

    [Theory]
    [InlineData("on", true)]
    [InlineData(" ON ", true)]
    [InlineData("off", false)]
    [InlineData("0", false)]
    [InlineData("maybe", null)]
    [InlineData(null, null)]
    public void The_command_line_word(string? word, bool? means) => Assert.Equal(means, AiPreview.Parse(word));
}
