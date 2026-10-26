using System.Text.Json.Nodes;
using Rigsight.Core.Ai;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;
using static Rigsight.Tests.App.RelayFailureTests;

namespace Rigsight.Tests.App;

/// <summary>
/// The Ask window's states from the approved board 6 (2026-10-26): the questions-left line under the box, Stop as a line
/// with Ask again (also while waiting for the plan), Try again and Open Settings on problems, offline, one limit card,
/// paused free questions, Undo after Delete, a search with no matches, Esc on a rename, Skip under Not helpful.
/// </summary>
public sealed class AskStatesTests
{
    private static AskViewModel Shown(string status) => Ui.Run(() =>
    {
        var ask = new AskViewModel(Kit.OfflineSettings(new RigsightSettings()));
        ask.Plans = new PlansViewModel(ask);
        ask.Show(Relay.ReadStatus(status)!);
        return ask;
    });

    private static RigsightSettings RigSightAi()
    {
        var settings = new RigsightSettings();
        settings.Ai.Mode = AiMode.RigSight;
        settings.Ai.FirstQuestionSeen.Add(AiMode.RigSight);
        return settings;
    }

    private static string Answered(string words) =>
        $"data: {new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = new JsonObject { ["content"] = words } }) }.ToJsonString()}\n\ndata: [DONE]\n\n";

    // ── Questions left, under the box ───────────────────────────────────
    [Fact]
    public void The_line_says_the_free_weeks_count_in_full_words_and_when_they_come_back()
    {
        var ask = Shown(PlansTests.Status(used: 3));
        Assert.True(ask.ShowQuestionsLine);
        Assert.Equal("7 of 10 free questions left this week", ask.QuestionsLine);
        Assert.Equal("Back Monday", ask.QuestionsNote);
        Assert.False(ask.IsLow);
        Assert.False(ask.ShowSeePlans);
    }

    [Fact]
    public void A_paid_plan_counts_today_and_resets_at_midnight()
    {
        var ask = Shown(PlansTests.Status("complete", "subscription", used: 12));
        Assert.Equal("488 of 500 left today", ask.QuestionsLine);
        Assert.Equal("Resets at midnight", ask.QuestionsNote);
    }

    [Fact]
    public void Few_left_with_a_plan_to_offer_shows_See_plans_and_the_biggest_plan_shows_the_reset()
    {
        var ask = Shown(PlansTests.Status(used: 9));
        Assert.Equal("1 of 10 free questions left this week", ask.QuestionsLine);
        Assert.True(ask.IsLow);
        Assert.True(ask.ShowSeePlans);
        Assert.Equal("", ask.QuestionsNote);
        // Complete is the biggest: nothing bigger to offer, so the reset stays.
        var top = Shown(PlansTests.Status("complete", "subscription", used: 495));
        Assert.True(top.IsLow);
        Assert.False(top.ShowSeePlans);
        Assert.Equal("Resets at midnight", top.QuestionsNote);
    }

    [Fact]
    public void Before_the_relay_has_said_the_line_is_checking_never_a_guess()
    {
        var ask = Ui.Run(() => new AskViewModel(Kit.OfflineSettings(new RigsightSettings())));
        Assert.Equal("Checking your questions…", ask.QuestionsLine);
        Assert.Equal("", ask.QuestionsNote);
    }

    [Fact]
    public void Paused_free_questions_are_said_as_paused_in_the_line_the_out_box_and_the_card()
    {
        var ask = Shown(PlansTests.Status(used: 0, capIs: 0));
        Assert.Equal("Free questions paused", ask.QuestionsLine);
        Assert.Equal("", ask.QuestionsNote);
        Assert.Equal("Free questions are paused right now.", ask.OutTitle);
        Assert.Equal("They'll be back. You can still get a plan.", ask.OutText);
        foreach (var kind in new[] { "paused", "free" })
        {
            var wall = Ui.Run(() => ask.LimitWall(new AiLimit(kind, "free", null), "q"));
            Assert.Equal("Free questions are paused right now.", wall.Title);
            Assert.Equal("essentials", wall.Plan);
        }
    }

    [Fact]
    public void Only_one_limit_card_shows_the_out_box_hides_while_the_chat_s_card_is_last()
    {
        var ask = Shown(PlansTests.Status(used: 10));
        Ui.Run(() =>
        {
            Assert.True(ask.IsOutOfQuestions);
            Assert.True(ask.ShowOutBox);
            var wall = ask.LimitWall(new AiLimit("week", "free", null), "q");
            ask.Current!.Messages.Add(new AskQuestion("q"));
            ask.Current.Messages.Add(wall);
            Assert.False(ask.ShowOutBox);
            ask.Current.Messages.Remove(wall);
            Assert.True(ask.ShowOutBox);
        });
    }

    // ── Stopped ──────────────────────────────────────────────────────────
    [Fact]
    public async Task Stop_while_waiting_for_the_plan_sends_nothing_and_Ask_again_answers_without_a_second_bubble()
    {
        var plan = new TaskCompletionSource();
        int asks = 0;
        await using var relay = new FakeRelay(async (r, s) =>
        {
            if (r.Path == "/v1/ask")
            {
                Interlocked.Increment(ref asks);
                await FakeRelay.SseStart(s);
                await FakeRelay.Write(s, Answered("It was a power cut."));
                s.Close();
                return;
            }
            await plan.Task;
            await FakeRelay.Reply(s, 200, "application/json", PlansTests.Status("essentials", "subscription"));
        });
        var ask = Ui.Run(() => new AskViewModel(Kit.OfflineSettings(RigSightAi())));
        Ui.Run(() =>
        {
            Relay.TestRoot.Value = relay.Url;
            ask.Draft = "Why did my PC turn off?";
            ask.SendCommand.Execute(null);
        });
        try
        {
            // The thinking line says what it waits for.
            Assert.True(Ui.WaitFor(() => ask.Current!.Messages.OfType<AskThinking>().Any(t => t.Step == "Checking your plan…" && t.Note == "Your question goes as soon as it's known."), 5_000),
                "the wait for the plan wasn't said");
            Ui.Run(() => ask.StopCommand.Execute(null));
            Assert.True(Ui.WaitFor(() => !ask.IsAnswering, 5_000), "Stop didn't work during the wait for the plan");
            Ui.Run(() =>
            {
                var left = ask.Current!.Messages.ToList();
                Assert.Equal(2, left.Count);
                Assert.IsType<AskQuestion>(left[0]);
                Assert.Equal("You stopped this answer. It wasn't counted.", Assert.IsType<AskStoppedAnswer>(left[1]).Text);
                Assert.Equal("Why did my PC turn off?", ask.Draft);
            });
            Assert.Equal(0, asks);

            plan.SetResult();
            Assert.True(Ui.WaitFor(() => ask.Standing is not null, 15_000), "the plan never landed");
            Ui.Run(() =>
            {
                Relay.TestRoot.Value = relay.Url;
                ask.AskStoppedAgainCommand.Execute(ask.Current!.Messages.OfType<AskStoppedAnswer>().Single());
            });
            Assert.True(Ui.WaitFor(() => !ask.IsAnswering && ask.Current!.Messages.OfType<AskAnswer>().Any(), 15_000), "Ask again never answered");
            Ui.Run(() =>
            {
                var left = ask.Current!.Messages.ToList();
                Assert.Equal(2, left.Count);
                Assert.Equal("Why did my PC turn off?", Assert.IsType<AskQuestion>(left[0]).Text);
                Assert.IsType<AskAnswer>(left[1]);
                Assert.Equal("", ask.Draft);
            });
            Assert.Equal(1, asks);
        }
        finally { Ui.Run(() => Relay.TestRoot.Value = null); }
    }

    // ── Problems with a way forward ─────────────────────────────────────
    [Fact]
    public async Task Try_again_asks_the_same_question_in_its_place_without_a_second_bubble()
    {
        int asks = 0;
        await using var relay = new FakeRelay(async (r, s) =>
        {
            if (r.Path != "/v1/ask") { await FakeRelay.Reply(s, 200, "application/json", PlansTests.Status("essentials", "subscription")); return; }
            if (Interlocked.Increment(ref asks) == 1) { await FakeRelay.Reply(s, 503, "application/json", "{\"error\":\"unavailable\"}"); return; }
            await FakeRelay.SseStart(s);
            await FakeRelay.Write(s, Answered("Second answer."));
            s.Close();
        });
        var ask = Ui.Run(() => new AskViewModel(Kit.OfflineSettings(RigSightAi())));
        Ui.Run(() =>
        {
            Relay.TestRoot.Value = relay.Url;
            ask.Draft = "Why did my PC turn off?";
            ask.SendCommand.Execute(null);
        });
        try
        {
            Assert.True(Ui.WaitFor(() => !ask.IsAnswering && ask.Current!.Messages.OfType<AskProblem>().Any(), 15_000), "the problem never came");
            var problem = Ui.Run(() => ask.Current!.Messages.OfType<AskProblem>().Single());
            Assert.True(problem.CanRetry);
            Assert.False(problem.CanOpenSettings);
            Ui.Run(() =>
            {
                Relay.TestRoot.Value = relay.Url;
                ask.TryAgainCommand.Execute(problem);
            });
            Assert.True(Ui.WaitFor(() => !ask.IsAnswering && ask.Current!.Messages.OfType<AskAnswer>().Any(), 15_000), "Try again never answered");
            Ui.Run(() =>
            {
                var left = ask.Current!.Messages.ToList();
                Assert.Equal(2, left.Count);
                Assert.IsType<AskQuestion>(left[0]);
                Assert.IsType<AskAnswer>(left[1]);
            });
        }
        finally { Ui.Run(() => Relay.TestRoot.Value = null); }
    }

    [Theory]
    [InlineData("Add your API key in Settings › Ask RigSight and press Test.", true)]
    [InlineData("Ollama isn't answering at http://localhost:11434. Check that it's running, then ask again.", false)]
    public void Words_that_send_people_to_Settings_get_Open_Settings(string text, bool settings) =>
        Assert.Equal(settings, AskViewModel.SendsToSettings(text));

    [Fact]
    public void A_page_not_shared_offers_Open_Settings_and_names_the_tab_as_Settings_calls_it()
    {
        var s = new RigsightSettings();
        s.Ai.Mode = AiMode.OwnKey;
        s.Ai.FirstQuestionSeen = [AiMode.OwnKey];
        s.Ai.PagesOff = [AiPages.Crashes];
        Ui.Run(() =>
        {
            var ask = new AskViewModel(Kit.OfflineSettings(s));
            ask.Explain("The PC shut off suddenly at 4:44 PM");
            var problem = Assert.Single(ask.Current!.Messages.OfType<AskProblem>());
            Assert.Equal("Explain needs the Crashes page switched on in Settings › Ask RigSight.", problem.Text);
            Assert.True(problem.CanOpenSettings);
            Assert.False(problem.CanRetry);
        });
    }

    // ── Offline ──────────────────────────────────────────────────────────
    [Fact]
    public void Offline_shows_the_banner_and_stops_Send_until_the_network_is_back()
    {
        Ui.Run(() =>
        {
            var ask = new AskViewModel(Kit.OfflineSettings(RigSightAi()));
            ask.NetworkChanged(false);
            ask.Draft = "Is my GPU too hot?";
            Assert.False(ask.ShowOffline);
            Assert.True(ask.SendCommand.CanExecute(null));
            ask.NetworkChanged(true);
            Assert.True(ask.ShowOffline);
            Assert.False(ask.SendCommand.CanExecute(null));
            // A suggested question waits in the box rather than going out to fail.
            ask.Draft = "";
            ask.AskSuggestedCommand.Execute("Which game heats the GPU most?");
            Assert.Equal("Which game heats the GPU most?", ask.Draft);
            Assert.True(ask.IsEmpty);
            ask.NetworkChanged(false);
            Assert.False(ask.ShowOffline);
            Assert.True(ask.SendCommand.CanExecute(null));
        });
    }

    [Fact]
    public void Ollama_on_this_PC_needs_no_internet_so_offline_says_nothing()
    {
        var s = new RigsightSettings();
        s.Ai.Mode = AiMode.Ollama;
        s.Ai.OllamaAddress = "http://localhost:11434";
        Ui.Run(() =>
        {
            var ask = new AskViewModel(Kit.OfflineSettings(s));
            ask.NetworkChanged(true);
            Assert.False(ask.ShowOffline);
        });
    }

    // ── History ──────────────────────────────────────────────────────────
    [Fact]
    public void Delete_can_be_undone_and_the_conversation_goes_back_where_it_was()
    {
        Ui.Run(() =>
        {
            var ask = new AskViewModel(Kit.OfflineSettings(new RigsightSettings()));
            var a = new AskConversation { Title = "First" };
            var b = new AskConversation { Title = "Second" };
            ask.Conversations.Add(a);
            ask.Conversations.Add(b);
            int at = ask.Conversations.IndexOf(a);
            ask.DeleteCommand.Execute(a);
            Assert.DoesNotContain(a, ask.Conversations);
            Assert.True(ask.Deleted);
            ask.UndoDeleteCommand.Execute(null);
            Assert.False(ask.Deleted);
            Assert.Equal(at, ask.Conversations.IndexOf(a));
            Assert.Contains(ask.Groups.SelectMany(g => g.Conversations), c => c == a);
        });
    }

    [Fact]
    public void A_search_with_no_matches_says_so()
    {
        Ui.Run(() =>
        {
            var ask = new AskViewModel(Kit.OfflineSettings(new RigsightSettings()));
            Assert.Equal("No conversations yet.", ask.HistoryEmptyText);
            ask.Conversations.Add(new AskConversation { Title = "Fans at night" });
            ask.HistorySearch = "temperatures";
            Assert.False(ask.HasHistory);
            Assert.Equal("No conversations match", ask.HistoryEmptyText);
        });
    }

    [Fact]
    public void Esc_on_a_rename_puts_the_old_name_back_and_says_it_was_handled()
    {
        Ui.Run(() =>
        {
            var ask = new AskViewModel(Kit.OfflineSettings(new RigsightSettings()));
            var talk = new AskConversation { Title = "Fans at night" };
            ask.Conversations.Add(talk);
            Assert.False(ask.CancelRename());
            ask.StartRenameCommand.Execute(talk);
            talk.RenameText = "Something else";
            Assert.True(ask.CancelRename());
            Assert.False(talk.IsRenaming);
            Assert.Equal("Fans at night", talk.Title);
        });
    }

    // ── Smaller fixes ────────────────────────────────────────────────────
    [Fact]
    public void Skip_closes_the_reasons_box_and_sends_nothing()
    {
        var feedback = new AskFeedback("q1");
        feedback.NotHelpfulCommand.Execute(null);
        Assert.True(feedback.Asking);
        feedback.PickCommand.Execute("too_long");
        feedback.SkipCommand.Execute(null);
        Assert.False(feedback.Asking);
        Assert.Null(feedback.Rated);
        Assert.Null(feedback.Reason);
        Assert.False(feedback.HasSaid);
    }

    [Fact]
    public void A_plan_card_isnt_shown_for_a_page_the_plan_already_includes()
    {
        var ask = Shown(PlansTests.Status("complete", "subscription"));
        Ui.Run(() =>
        {
            var talk = ask.Current!;
            var answer = AskViewModel.ToAnswer("Your GPU stayed cool.\n[locked: games]", []);
            talk.Messages.Add(answer);
            // Complete reads Games: the plan card would sell what they have.
            ask.Show(Relay.ReadStatus(PlansTests.Status("essentials", "subscription"))!);
            ask.Show(Relay.ReadStatus(PlansTests.Status("complete", "subscription"))!);
            var now = Assert.IsType<AskAnswer>(talk.Messages.Single());
            Assert.Null(now.Locked);
            Assert.DoesNotContain(now.Parts, p => p is AskLockedCard);
        });
    }

    [Fact]
    public void The_new_words_have_no_colons()
    {
        var weekly = Shown(PlansTests.Status(used: 3));
        var paused = Shown(PlansTests.Status(used: 0, capIs: 0));
        string[] said =
        [
            weekly.QuestionsLine, weekly.QuestionsNote, paused.OutTitle, paused.OutText, paused.QuestionsLine,
            weekly.HistoryEmptyText, "You stopped this answer. It wasn't counted.", "You stopped this answer.",
            "You're offline. Answers need the internet, so questions can't be sent until it's back.",
        ];
        Assert.All(said, s => Assert.DoesNotContain(":", s));
    }
}
