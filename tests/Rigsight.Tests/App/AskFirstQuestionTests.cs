using Rigsight.Core.Ai;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>The window shown before the first question with each way of answering: what is sent, where, and nothing before Start asking.</summary>
public sealed class AskFirstQuestionTests
{
    private static AskViewModel Window(AiMode mode, string ollama = "http://localhost:11434", params AiMode[] seen)
    {
        var settings = new RigsightSettings();
        settings.Ai.Mode = mode;
        settings.Ai.OllamaAddress = ollama;
        settings.Ai.FirstQuestionSeen = [.. seen];
        string file = Path.Combine(Path.GetTempPath(), $"ask-first-{Guid.NewGuid():N}.json");
        return Ui.Run(() => new AskViewModel(Kit.OfflineSettings(settings), file));
    }

    [Fact]
    public void Nothing_can_be_sent_until_Start_asking_and_then_only_that_way_is_past_it()
    {
        Ui.Run(() =>
        {
            var ask = Window(AiMode.OwnKey);
            ask.Draft = "Why did my PC turn off?";
            Assert.True(ask.ShowFirstQuestion);
            Assert.False(ask.ShowStarters);
            Assert.False(ask.SendCommand.CanExecute(null));

            ask.StartAskingCommand.Execute(null);
            Assert.False(ask.ShowFirstQuestion);
            Assert.True(ask.ShowStarters);
            Assert.True(ask.SendCommand.CanExecute(null));
        });
    }

    [Fact]
    public void Each_way_of_answering_says_where_the_records_go()
    {
        Ui.Run(() =>
        {
            Assert.Equal("Goes to RigSight AI.", Window(AiMode.RigSight).FirstWhere);
            Assert.Equal("Goes to OpenRouter.", Window(AiMode.OwnKey).FirstWhere);
            var here = Window(AiMode.Ollama);
            Assert.Equal("Stays on this PC.", here.FirstWhere);
            Assert.Equal("good", here.FirstTone);
            Assert.Null(here.FirstNever);
            // Ollama on another computer: the records leave this PC, so it never says they stay.
            var away = Window(AiMode.Ollama, "192.168.1.20:11434");
            Assert.Equal("Goes to the Ollama at 192.168.1.20.", away.FirstWhere);
            Assert.Equal("warm", away.FirstTone);
            Assert.NotNull(away.FirstNever);
        });
    }

    [Fact]
    public void A_way_already_read_doesnt_show_it_again()
    {
        Ui.Run(() =>
        {
            Assert.False(Window(AiMode.Ollama, seen: AiMode.Ollama).ShowFirstQuestion);
            Assert.True(Window(AiMode.OwnKey, seen: AiMode.Ollama).ShowFirstQuestion);
            Assert.False(Window(AiMode.Off).ShowFirstQuestion);
        });
    }

    [Theory]
    [InlineData("http://localhost:11434", true)]
    [InlineData("localhost:11434", true)]
    [InlineData("127.0.0.1:11434", true)]
    [InlineData("http://[::1]:11434", true)]
    [InlineData("192.168.1.20:11434", false)]
    [InlineData("https://gpu-box.example.com", false)]
    [InlineData("", false)]
    public void Only_a_local_address_counts_as_this_PC(string address, bool here) => Assert.Equal(here, Ollama.IsOnThisPc(address));
}
