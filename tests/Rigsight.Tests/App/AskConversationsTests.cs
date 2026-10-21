using Rigsight.Core.Ai;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// Ask RigSight's conversations kept between runs (conversations.json), and History's panel: Pinned on top, then
/// Today, Yesterday and Earlier, archived ones behind their own line, and each one's pin, rename, archive and delete.
/// </summary>
public sealed class AskConversationsTests
{
    private static string NewFile() => Path.Combine(TestEnvironment.NewFolder("ask"), "conversations.json");

    private static AskViewModel Window(string file) => new(Kit.OfflineSettings(new RigsightSettings()), file);

    private static async Task Until(Func<bool> done)
    {
        for (int i = 0; i < 200 && !done(); i++) await Task.Delay(25);
        Assert.True(done());
    }

    [Fact]
    public async Task A_conversation_comes_back_after_a_restart_with_its_pin_and_new_name()
    {
        string file = NewFile();
        var first = Window(file);
        first.Draft = "why did my PC turn off?";
        first.SendCommand.Execute(null);
        var talk = first.Conversations.Single(c => c.Title.Length > 0);
        first.TogglePinCommand.Execute(talk);
        first.StartRenameCommand.Execute(talk);
        talk.RenameText = "The shut-offs";
        first.FinishRename(talk, keep: true);
        await first.Save();

        var again = Window(file);
        var back = again.Conversations.Single(c => c.Title.Length > 0);
        Assert.Equal("The shut-offs", back.Title);
        Assert.True(back.IsPinned);
        Assert.Equal(talk.Id, back.Id);
        // It opens on a fresh conversation; the old one waits in History, under Pinned.
        Assert.True(again.IsEmpty);
        Assert.Equal("PINNED", again.Groups[0].Heading);
        again.PickCommand.Execute(back);
        // RigSight AI (the default) can't answer before the relay, and says so: that's kept with the question.
        await Until(() => back.Messages.Count == 2);
        Assert.Equal("why did my PC turn off?", Assert.IsType<AskQuestion>(back.Messages[0]).Text);
        Assert.IsType<AskProblem>(back.Messages[1]);
    }

    [Fact]
    public void Words_typed_in_one_conversation_stay_with_it()
    {
        var window = Window(NewFile());
        window.Draft = "why is my fan loud?";
        var first = window.Current!;
        // A new conversation, even the empty one reused, starts with an empty box.
        window.NewCommand.Execute(null);
        Assert.Equal("", window.Draft);
        window.Draft = "something else";
        var second = new AskConversation { Title = "Older one" };
        window.Conversations.Add(second);
        window.PickCommand.Execute(second);
        Assert.Equal("", window.Draft);
        window.PickCommand.Execute(window.Conversations.First(c => c != second));
        Assert.Equal("something else", window.Draft);
    }

    [Fact]
    public async Task A_saved_answer_is_drawn_again_with_its_next_step()
    {
        string file = NewFile();
        AiConversations.Save([new SavedConversation("a1", "Is my PC ok?", DateTime.Now, null, false, false,
        [
            new SavedMessage(SavedMessage.Question, "Is my PC ok?"),
            new SavedMessage(SavedMessage.Answer, "Mostly yes.\nNext step: check the room's power.", "gemma4:12b", []),
            new SavedMessage(SavedMessage.Question, "thanks"),
            new SavedMessage(SavedMessage.Answer, "Any time!", "gemma4:12b", []),
        ])], file);
        var window = Window(file);
        var talk = window.Conversations.Single(c => c.Id == "a1");
        window.PickCommand.Execute(talk);
        await Until(() => talk.Messages.Count == 4);
        var answer = Assert.IsType<AskAnswer>(talk.Messages[1]);
        Assert.Equal("Mostly yes.", answer.Verdict);
        Assert.IsType<AskNext>(Assert.Single(answer.Parts));
        Assert.Equal("gemma4:12b", answer.Model);
        // What was said goes with the next question, as before the restart.
        Assert.Equal(4, talk.Sent.Count);
    }

    [Fact]
    public async Task An_old_answer_shows_the_numbers_it_showed_then_not_todays()
    {
        string file = NewFile();
        IReadOnlyList<AiBlock> then =
        [
            new AiTiles([new AiTile("10", "sudden shut-offs", "30 Sep – 20 Oct", AiTone.Warm)]),
            new AiBars("WHAT RIGSIGHT FOUND FOR THE 10", [new AiBar("Most likely a power cut", 1, "9", AiTone.Warm)]),
            new AiBands("AGAINST YOUR USUAL", [new AiBand("GPU in Risk of Rain 2", "62°", "usual 58–64", 0.62, 0.58, 0.64, null, AiTone.Gpu)]),
            new AiBarTable("GAME", "GRAPHICS CARD BUSY", "PLAYED", [new AiTableRow("Risk of Rain 2", 0.45, "45%", "5h 15m", true)]),
        ];
        AiConversations.Save([new SavedConversation("b1", "Is my PC ok?", DateTime.Now, null, false, false,
        [
            new SavedMessage(SavedMessage.Question, "Is my PC ok?"),
            // The look-ups name no records this test has: drawn again, nothing would show.
            new SavedMessage(SavedMessage.Answer, "Mostly yes.\n[show: crashes]\n[show: games]", "gemma4:12b",
                [new SavedLookUp("get_crashes", "{\"from\":\"2026-09-30\",\"to\":\"2026-10-20\"}", 100)],
                new Dictionary<string, IReadOnlyList<AiBlock>> { ["crashes"] = then.Take(3).ToList(), ["games"] = then.Skip(3).ToList() }),
        ])], file);
        var window = Window(file);
        var talk = window.Conversations.Single(c => c.Id == "b1");
        window.PickCommand.Execute(talk);
        await Until(() => talk.Messages.Count == 2);
        var answer = Assert.IsType<AskAnswer>(talk.Messages[1]);
        Assert.Equal(then, answer.Parts.Cast<AiBlock>(), new BlockText());
        // Saved again, they stay as they were.
        await window.Save();
        var saved = AiConversations.Load(file).Single().Messages[1].Drawn!;
        Assert.Equal(then.Take(3), saved["crashes"], new BlockText());
    }

    /// <summary>Records holding lists compare by reference: two blocks are the same when they serialise the same.</summary>
    private sealed class BlockText : IEqualityComparer<AiBlock>
    {
        public bool Equals(AiBlock? a, AiBlock? b) => System.Text.Json.JsonSerializer.Serialize(a) == System.Text.Json.JsonSerializer.Serialize(b);
        public int GetHashCode(AiBlock b) => 0;
    }

    [Fact]
    public void Archived_ones_wait_behind_their_own_line_and_deleting_takes_one_away()
    {
        string file = NewFile();
        var now = DateTime.Now;
        AiConversations.Save(
        [
            new SavedConversation("p", "Pinned one", now.AddDays(-5), null, true, false, []),
            new SavedConversation("t", "Today's", now, null, false, false, []),
            new SavedConversation("y", "Yesterday's", now.AddDays(-1), null, false, false, []),
            new SavedConversation("x", "Old and done", now.AddDays(-3), null, false, true, []),
        ], file);
        var window = Window(file);
        Assert.Equal(["PINNED", "TODAY", "YESTERDAY"], window.Groups.Select(g => g.Heading));
        Assert.Equal(1, window.ArchivedCount);

        window.ToggleArchivedCommand.Execute(null);
        Assert.Equal("Old and done", Assert.Single(Assert.Single(window.Groups).Conversations).Title);

        var old = window.Conversations.Single(c => c.Id == "x");
        window.ToggleArchiveCommand.Execute(old);
        Assert.False(window.ShowingArchived); // nothing left in the archive: back to the rest
        Assert.Equal(["PINNED", "TODAY", "YESTERDAY", "EARLIER"], window.Groups.Select(g => g.Heading));

        window.DeleteCommand.Execute(window.Conversations.Single(c => c.Id == "y"));
        Assert.DoesNotContain(window.Groups, g => g.Heading == "YESTERDAY");
    }

    [Fact]
    public void An_unreadable_file_is_kept_aside_and_nothing_is_lost_by_starting_again()
    {
        string file = NewFile();
        File.WriteAllText(file, "{ not json");
        var window = Window(file);
        Assert.False(window.HasHistory);
        Assert.True(File.Exists(file + ".bad"));
    }

    [Fact]
    public async Task Conversations_not_asked_in_for_longer_than_the_setting_go_and_pinned_ones_stay()
    {
        string file = NewFile();
        var now = DateTime.Now;
        AiConversations.Save(
        [
            new SavedConversation("new", "This week's", now.AddDays(-3), null, false, false, []),
            new SavedConversation("old", "Two months ago", now.AddDays(-60), null, false, false, []),
            new SavedConversation("kept", "Pinned long ago", now.AddDays(-200), null, true, false, []),
            new SavedConversation("gone", "Archived long ago", now.AddDays(-45), null, false, true, []),
        ], file);
        var settings = new RigsightSettings();
        settings.Ai.KeepConversationDays = 30;
        var window = new AskViewModel(Kit.OfflineSettings(settings), file);
        Assert.Equal(["kept", "new"], window.Conversations.Where(c => c.Title.Length > 0).Select(c => c.Id).Order());
        // Saved that way: they don't come back at the next start.
        await window.Save();
        Assert.Equal(["kept", "new"], AiConversations.Load(file).Select(c => c.Id).Order());

        var forever = new RigsightSettings();
        forever.Ai.KeepConversationDays = 0;
        AiConversations.Save([new SavedConversation("ancient", "Last year", now.AddDays(-400), null, false, false, [])], file);
        Assert.Single(new AskViewModel(Kit.OfflineSettings(forever), file).Conversations, c => c.Id == "ancient");
    }

    [Fact]
    public void Asking_again_in_an_old_conversation_keeps_it_and_moves_it_to_today()
    {
        string file = NewFile();
        AiConversations.Save([new SavedConversation("o", "Old one", DateTime.Now.AddDays(-20), null, false, false, [new SavedMessage(SavedMessage.Question, "hi")])], file);
        var window = Window(file);
        var old = window.Conversations.Single(c => c.Id == "o");
        window.PickCommand.Execute(old);
        window.Draft = "and now?";
        window.SendCommand.Execute(null);
        Assert.Equal(DateTime.Today, old.When.Date);
        Assert.Equal("TODAY", window.Groups[0].Heading);
    }

    [Fact]
    public async Task Clear_all_takes_every_conversation_pinned_and_archived_too()
    {
        string file = NewFile();
        var now = DateTime.Now;
        AiConversations.Save(
        [
            new SavedConversation("p", "Pinned", now, null, true, false, []),
            new SavedConversation("a", "Archived", now, null, false, true, []),
            new SavedConversation("t", "Today's", now, null, false, false, []),
        ], file);
        var window = Window(file);
        window.ClearAll();
        Assert.False(window.HasHistory);
        Assert.True(window.IsEmpty);
        await window.Save();
        Assert.Empty(AiConversations.Load(file));
    }

    [Fact]
    public async Task New_conversation_never_reuses_a_saved_one_that_isnt_opened_yet()
    {
        string file = NewFile();
        AiConversations.Save([new SavedConversation("old", "tell me about yesterday", DateTime.Now, null, false, false,
            [new SavedMessage(SavedMessage.Question, "tell me about yesterday"), new SavedMessage(SavedMessage.Answer, "It was quiet.", "gemma4:12b", [])])], file);
        var window = Window(file);
        var old = window.Conversations.Single(c => c.Id == "old");
        window.NewCommand.Execute(null);
        Assert.NotSame(old, window.Current);
        window.Draft = "why did this app crash?";
        window.SendCommand.Execute(null);
        Assert.Equal("why did this app crash?", window.Current!.Title);
        await window.Save();
        var saved = AiConversations.Load(file);
        Assert.Equal(2, saved.Single(c => c.Id == "old").Messages.Count);
        Assert.Equal("why did this app crash?", saved.Single(c => c.Id != "old").Title);
    }
}
