using Rigsight.Core.Ai;

namespace Rigsight.Tests.Ai;

/// <summary>
/// A long chat's summary (2026-10-26): when one is needed, and what goes with the next question once there is one. The
/// latest messages always go as written, and the summary rides in front of the first of them, so the chat still goes
/// question, answer.
/// </summary>
public sealed class AiMemoryTests
{
    private static List<AiTurn> Chat(int exchanges, int chars = 40) =>
        [.. Enumerable.Range(1, exchanges).SelectMany(i => new[] { new AiTurn(true, $"Question {i} " + new string('q', chars)), new AiTurn(false, $"Answer {i} " + new string('a', chars)) })];

    [Fact]
    public void A_short_chat_needs_none_and_is_sent_whole()
    {
        var chat = Chat(3);
        Assert.False(AiMemory.Needs(chat, 0, null));
        Assert.Same(chat, AiMemory.History(chat, 0, null));
    }

    [Fact]
    public void Past_the_messages_ever_sent_whole_or_past_the_characters_it_needs_one()
    {
        Assert.True(AiMemory.Needs(Chat(7), 0, null));               // 14 messages, more than AiTurns.MaxSent
        Assert.True(AiMemory.Needs(Chat(3, 12_000), 0, null));       // 6 messages, but about 72,000 characters
        Assert.False(AiMemory.Needs(Chat(2, 20_000), 0, null));      // only the latest 4: nothing older to summarise
        Assert.True(AiMemory.Needs(Chat(2, 50_000), 0, null));       // but 4 huge ones: only the latest exchange stays as written
    }

    [Fact]
    public void After_a_summary_only_what_came_since_counts()
    {
        var chat = Chat(7);
        Assert.False(AiMemory.Needs(chat, 10, "- earlier"));         // 4 since the summary
        Assert.True(AiMemory.Needs([.. chat, .. Chat(5)], 10, "- earlier"));
    }

    [Fact]
    public void The_summary_goes_in_front_of_the_first_message_kept_and_nothing_else_changes()
    {
        var chat = Chat(5);
        var sent = AiMemory.History(chat, 6, "- Two driver resets, at 5:13 PM and 5:25 PM on 22 Oct.");
        Assert.Equal(4, sent.Count);
        Assert.True(sent[0].FromUser);
        Assert.Equal($"{AiMemory.Lead}\n- Two driver resets, at 5:13 PM and 5:25 PM on 22 Oct.\n\n{chat[6].Text}", sent[0].Text);
        Assert.Equal(chat.Skip(7), sent.Skip(1));
        Assert.Equal(chat[6].Text, chat[6].Text); // the chat itself is left as it was
        Assert.DoesNotContain(AiMemory.Lead, chat[6].Text);
    }

    [Fact]
    public void A_summary_keeps_its_points_without_the_answers_marks()
    {
        const string raw = "- PC shut off suddenly four times in the last week [src: crashes].\n- Driver 621.01 installed on 22 Oct [src: crashes changes] [w2]\n- Next try the clean install [do: open crashes]\nAsk next: Was it hot? | Why?\nTitle: Black screens";
        Assert.Equal("- PC shut off suddenly four times in the last week.\n- Driver 621.01 installed on 22 Oct\n- Next try the clean install", AiMemory.Clean(raw));
    }

    [Fact]
    public void Two_huge_pastes_keep_only_the_latest_exchange_as_written()
    {
        var chat = Chat(4);
        chat[4] = new AiTurn(true, new string('p', 100_000));    // a paste
        chat[6] = new AiTurn(true, new string('q', 100_000));    // and another
        Assert.Equal(2, AiMemory.Keep(chat));
        Assert.Equal(AiMemory.KeepRecent, AiMemory.Keep(Chat(4)));
        Assert.True(AiMemory.Needs(chat, 0, null));
    }

    [Fact]
    public void A_request_too_big_for_the_relay_shortens_the_longest_earlier_messages_never_the_question()
    {
        var messages = new System.Text.Json.Nodes.JsonArray(
            new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = "small question" },
            new System.Text.Json.Nodes.JsonObject { ["role"] = "assistant", ["content"] = new string('a', 2000) },
            new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = new string('p', 150_000) },
            new System.Text.Json.Nodes.JsonObject { ["role"] = "assistant", ["content"] = "ok" },
            new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = new string('q', 100_000) },
            new System.Text.Json.Nodes.JsonObject { ["role"] = "tool", ["content"] = new string('t', 40_000) });
        int cut = AiMemory.Fit(messages, questionAt: 4, otherChars: 20_000);
        Assert.Equal(1, cut);
        Assert.StartsWith(new string('p', AiMemory.ShortenedTo) + "\n… (146,000 more characters", messages[2]!["content"]!.ToString());
        Assert.Equal(100_000, messages[4]!["content"]!.ToString().Length);   // the question as asked
        Assert.Equal(40_000, messages[5]!["content"]!.ToString().Length);    // this answer's own look-up
        Assert.Equal("small question", messages[0]!["content"]!.ToString());
        Assert.True(messages.ToJsonString().Length + 20_000 <= AiMemory.SafeRequest);
        // Already small enough: nothing changes.
        Assert.Equal(0, AiMemory.Fit(messages, 4, 20_000));
    }

    // "This month against every month this year" made 19 look-ups and its fifth round was refused for size (2026-10-27).
    [Fact]
    public void When_earlier_messages_arent_enough_this_answers_oldest_look_ups_are_shortened_never_the_latest()
    {
        static System.Text.Json.Nodes.JsonObject Msg(string role, string content) => new() { ["role"] = role, ["content"] = content };
        var messages = new System.Text.Json.Nodes.JsonArray(
            Msg("user", "compare this month with every month this year"),
            Msg("assistant", ""), Msg("tool", new string('a', 90_000)), Msg("tool", new string('b', 90_000)),
            Msg("assistant", ""), Msg("tool", new string('c', 90_000)),
            Msg("assistant", ""), Msg("tool", new string('d', 60_000)));
        int cut = AiMemory.Fit(messages, questionAt: 0, otherChars: 20_000);
        Assert.Equal(2, cut);
        Assert.Contains(AiMemory.LookUpCut, messages[2]!["content"]!.ToString());
        Assert.Contains("look it up again for a shorter stretch", messages[3]!["content"]!.ToString());
        Assert.Equal(90_000, messages[5]!["content"]!.ToString().Length);   // enough was cut before it
        Assert.Equal(60_000, messages[7]!["content"]!.ToString().Length);   // the latest round's look-up, as it came
        Assert.Equal("compare this month with every month this year", messages[0]!["content"]!.ToString());
        Assert.True(messages.ToJsonString().Length + 20_000 <= AiMemory.SafeRequest);
    }

    [Fact]
    public void A_summary_that_doesnt_fit_the_chat_is_ignored()
    {
        var chat = Chat(3);
        Assert.Same(chat, AiMemory.History(chat, 9, "- x"));          // covers more than there is
        Assert.Same(chat, AiMemory.History(chat, 1, "- x"));          // would start on an answer
        Assert.Same(chat, AiMemory.History(chat, 2, ""));
    }
}
