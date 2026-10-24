using Rigsight.Controls;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// The AI marks how its words look (Markdown) and the app only draws it (the person's call, 2026-10-24); the parts RigSight
/// checks (Next step, Ask next, Title, tags) are still read as tags, whatever marks they carry.
/// </summary>
public sealed class AskMarkdownTests
{
    [Fact]
    public void Bold_italic_and_code_are_drawn_and_nothing_else()
    {
        var p = Md.Parse("The **GPU** hit *83 °C* during `Crimson Desert.exe`.");
        Assert.Equal(["The ", "GPU", " hit ", "83 °C", " during ", "Crimson Desert.exe", "."], p.Select(x => x.Text));
        Assert.True(p[1].Bold);
        Assert.True(p[3].Italic);
        Assert.True(p[5].Code);
        Assert.Equal("The GPU hit 83 °C during Crimson Desert.exe.", Md.Plain("The **GPU** hit *83 °C* during `Crimson Desert.exe`."));
    }

    [Fact]
    public void A_link_shows_its_words_never_something_to_press()
    {
        // A web page the AI read could slip one in: only a checked source pill opens a page.
        Assert.Equal("Download the fix here.", Md.Plain("[Download the fix](https://bad.example/fix.exe) here."));
    }

    [Fact]
    public void A_mark_left_open_never_shows_its_stars_and_lone_stars_in_maths_stay()
    {
        // An answer still arriving: "**GPU" before its closing marks.
        Assert.Equal("The GPU", Md.Plain("The **GPU"));
        Assert.Equal("5 * 3 = 15", Md.Plain("5 * 3 = 15"));
        Assert.Equal("file_name_here", Md.Plain("file_name_here"));
    }

    [Fact]
    public void A_bolded_next_step_and_ask_next_are_still_read_as_theirs()
    {
        var answer = AskViewModel.ToAnswer("It was the **power**.\n**Next step:** check the router.\n**Ask next:** Was it the same last week?", []);
        Assert.Equal("check the router.", Assert.Single(answer.Parts.OfType<AskNext>()).Text, ignoreCase: true);
        Assert.Equal(["Was it the same last week?"], answer.Suggestions);
        // The paragraph keeps its marks, for the view to draw.
        Assert.Equal("It was the **power**.", answer.Verdict);
    }
}
