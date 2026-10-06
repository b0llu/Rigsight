using Rigsight.Core.Ask;
using Rigsight.Core.Settings;
using Rigsight.Services;

namespace Rigsight.Tests.Ask;

/// <summary>Questions offered while typing: they fit what's typed, and every one of them is a question that gets an answer.</summary>
public sealed class AskSuggestTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 14, 30, 0);
    private static readonly AskApp[] Apps =
    [
        new(1, "Cyberpunk 2077", "Cyberpunk2077.exe", AppCategory.Game, 90_000), new(2, "Google Chrome", "chrome.exe", AppCategory.Browser, 400_000),
        new(3, "REMATCH", "rematch.exe", AppCategory.Game, 30_000),
    ];

    private static List<string> For(string typed) => AskSuggest.For(typed, Apps, Now);

    [Fact]
    public void Suggestions_fit_the_words_typed_so_far()
    {
        Assert.Empty(For("w"));
        Assert.Equal("Why did my PC crash yesterday?", For("why did my pc cr")[0]);
        Assert.Contains("How hot did my GPU get yesterday?", For("gpu temp"));
        Assert.Contains("Did my internet drop today?", For("wifi disc"));
        Assert.Contains("How full are my drives?", For("disk space"));
        Assert.Contains("Do I need more RAM?", For("more ram"));
        Assert.Empty(For("purple monkey dishwasher"));
        Assert.True(For("how").Count <= 4);
    }

    [Fact]
    public void An_app_or_a_time_typed_is_put_into_the_question()
    {
        Assert.Equal("How hot does Cyberpunk 2077 make my PC?", For("cyberpunk temp")[0]);
        Assert.All(For("rematch"), s => Assert.Contains("REMATCH", s));
        Assert.Contains("How much data did Google Chrome use this month?", For("chrome data"));
        Assert.DoesNotContain(For("chrome"), s => s.Contains("play")); // not a game
        Assert.Contains("Why did my PC crash last week?", For("crash last week"));
        Assert.Contains("How hot did my PC get on Fri 2 Oct?", For("how hot on friday"));
        // The question as typed isn't offered back.
        Assert.DoesNotContain("What can you do?", For("what can you do?"));
    }

    [Fact]
    public void Every_question_that_can_be_offered_is_read_as_what_it_asks()
    {
        var router = new AskRouter(new AskEmbedder());
        Assert.All(AskSuggest.Templates, t =>
        {
            var app = t.Text.Contains("{game}") ? Apps[0] : t.Text.Contains("{app}") ? Apps[1] : null;
            foreach (var period in new[] { null, AskTime.Day(Now.AddDays(-1), Now), AskTime.Week(Now.AddDays(-7), Now) })
            {
                string question = AskSuggest.Fill(t.Text, period, app);
                var read = router.Read(question, Now, Apps);
                Assert.True(read.Intent == t.Intent, $"\"{question}\" was read as {read.Intent} ({read.Score:0.00}), not {t.Intent}");
                Assert.False(read.HowTo, question);
                if (app is not null) Assert.Equal(app.Name, read.App);
            }
        });
    }
}
