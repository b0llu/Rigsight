using Rigsight.Core;

namespace Rigsight.Tests.Core;

/// <summary>What the search boxes on Memory, Timeline and Crashes match: every word typed, anywhere in what a row says.</summary>
public sealed class TextMatchTests
{
    [Theory]
    [InlineData("discord", true)]
    [InlineData("DISCORD", true)]
    [InlineData("9261", true)]
    [InlineData("discord 9260", true)] // one word in the title, the other in the old version
    [InlineData("  updated   discord ", true)] // any order, any spacing
    [InlineData("discord nvidia", false)] // every word must be there
    [InlineData("steam", false)]
    public void Every_word_typed_must_be_somewhere_in_the_row(string typed, bool matches) =>
        Assert.Equal(matches, TextMatch.Has(TextMatch.Words(typed), "Discord updated to 1.0.9261", "1.0.9260", null));

    [Fact]
    public void Nothing_typed_matches_everything()
    {
        Assert.Empty(TextMatch.Words("   "));
        Assert.Empty(TextMatch.Words(null));
        Assert.True(TextMatch.Has([], "anything"));
        Assert.True(TextMatch.Has([]));
    }
}
