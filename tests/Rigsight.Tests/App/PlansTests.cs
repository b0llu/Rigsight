using System.Text.Json.Nodes;
using Rigsight.Core.Ai;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;
using static Rigsight.Tests.App.RelayFailureTests;

namespace Rigsight.Tests.App;

/// <summary>
/// RigSight AI's plans in the app (2026-10-23): what the relay says about the plan, the plan actions and Stop through it,
/// the secret and clock every call carries, the words for each new refusal, and what Settings, the Plans screen and the
/// Ask window's cards show for each state (free, used up, paid, cancelled, payment failed, given by support).
/// </summary>
public sealed class PlansTests
{
    private static IDisposable Point(string url)
    {
        Relay.TestRoot.Value = url;
        return new Undo(() => Relay.TestRoot.Value = null);
    }

    private sealed class Undo(Action undo) : IDisposable { public void Dispose() => undo(); }

    private static readonly string[] Free = ["home", "parts", "temperatures"];
    private static readonly string[] Essentials = [.. Free, "crashes", "memory", "storage", "processes", "reports"];

    /// <summary>A status as the relay sends it, on <paramref name="plan"/>.</summary>
    internal static string Status(string plan = "free", string source = "free", string state = "active", int used = 3, string? endsAt = null, string? card = "\"card\":{\"brand\":\"visa\",\"last4\":\"4242\"},", int? capIs = null)
    {
        int cap = capIs ?? plan switch { "free" => 10, "essentials" => 100, _ => 500 };
        string period = plan == "free" ? "week" : "day";
        string pages = plan switch { "free" => Json(Free), "essentials" => Json(Essentials), _ => Json([.. AiPages.All.Select(p => p.Id)]) };
        string name = plan switch { "free" => "Free", "essentials" => "Essentials", _ => "Complete" };
        string price = plan switch { "free" => "$0", "essentials" => "$5", _ => "$10" };
        return $$"""
        {"questions_left":{{cap - used}},"free_left":0,"licence":{{(plan == "free" ? "null" : "{\"key\":\"RS-AAAA-BBBB-CCCC\",\"status\":\"active\"}")}},"packs":[],"search":true,
         "plan":{"id":"{{plan}}","name":"{{name}}","source":"{{source}}","state":"{{state}}","used":{{used}},"left":{{cap - used}},"cap":{{cap}},"period":"{{period}}",
                 "resets_at":"2026-10-26T00:00:00","renews_at":{{(plan == "free" ? "null" : "\"2026-11-22T10:00:00Z\"")}},"ends_at":{{(endsAt is null ? "null" : $"\"{endsAt}\"")}},
                 "price_label":"{{price}}",{{(plan == "free" ? "" : card)}}"pages":{{pages}},"can_manage":{{(source == "subscription" ? "true" : "false")}}},
         "plans":[{"id":"free","name":"Free","price_label":"$0","price_cents":0,"cap":10,"period":"week","pages":{{Json(Free)}},"url":""},
                  {"id":"essentials","name":"Essentials","price_label":"$5","price_cents":500,"cap":100,"period":"day","pages":{{Json(Essentials)}},"url":"https://rigsight.lemonsqueezy.com/checkout/buy/e?checkout[custom][device]=x"},
                  {"id":"complete","name":"Complete","price_label":"$10","price_cents":1000,"cap":500,"period":"day","pages":{{Json([.. AiPages.All.Select(p => p.Id)])}},"url":"https://rigsight.lemonsqueezy.com/checkout/buy/c"}]}
        """;
    }

    private static string Json(IEnumerable<string> ids) => new JsonArray([.. ids.Select(i => (JsonNode)i)]).ToJsonString();

    // ── What the relay says ─────────────────────────────────────────────
    [Fact]
    public void The_plan_and_the_plans_are_read_from_the_status()
    {
        var s = Relay.ReadStatus(Status("essentials", "subscription"))!;
        Assert.Equal("essentials", s.Plan!.Id);
        Assert.Equal(97, s.Plan.Left);
        Assert.Equal(new DateTime(2026, 10, 26), s.Plan.ResetsAt);
        Assert.Equal("Visa ending 4242", s.Plan.Card!.Text);
        Assert.True(s.Plan.CanManage);
        Assert.Equal(8, s.Plan.Pages.Count);
        Assert.Equal(["free", "essentials", "complete"], s.Plans.Select(p => p.Id));
        Assert.StartsWith("https://", s.Plans[1].Url);
    }

    [Fact]
    public void A_relay_from_before_plans_still_reads()
    {
        var s = Relay.ReadStatus("{\"questions_left\":7,\"free_left\":7,\"licence\":null,\"packs\":[]}")!;
        Assert.Null(s.Plan);
        Assert.Empty(s.Plans);
        Assert.Equal(7, s.QuestionsLeft);
    }

    [Fact]
    public void Pages_the_app_doesnt_know_are_left_out()
    {
        var s = Relay.ReadStatus(Status().Replace("\"home\",", "\"home\",\"passwords\","))!;
        Assert.DoesNotContain("passwords", s.Plan!.Pages);
    }

    [Theory]
    [InlineData("week", "That was your last free question this week. They're back on Monday.")]
    [InlineData("day", "That's all your questions for today. More at midnight.")]
    [InlineData("paused", "Free questions are paused for now. A plan keeps answering.")]
    public void Each_limit_has_its_words_and_is_read_for_the_card(string reason, string words)
    {
        string body = $"{{\"error\":\"out_of_questions\",\"reason\":\"{reason}\",\"resets_at\":\"2026-10-26T00:00:00\",\"plan\":\"free\"}}";
        Assert.Equal(words, Relay.Problem(body, 402));
        var limit = Relay.ReadLimit(body)!;
        Assert.Equal(reason, limit.Kind);
        Assert.Equal(new DateTime(2026, 10, 26), limit.ResetsAt);
    }

    [Theory]
    [InlineData("licence", "wrong_secret", "sign-in changed")]
    [InlineData("licence", "has_plan", "already has a plan")]
    [InlineData("unavailable", "too_fast", "wasn't counted")]
    [InlineData("unavailable", "billing", "Nothing was charged")]
    public void The_relays_new_refusals_are_said_plainly(string kind, string reason, string part)
    {
        Assert.Contains(part, Relay.Problem($"{{\"error\":\"{kind}\",\"reason\":\"{reason}\"}}", 403));
    }

    // ── What every call carries ─────────────────────────────────────────
    [Fact]
    public async Task Every_call_carries_this_PCs_secret_and_clock()
    {
        var seen = new List<FakeRequest>();
        await using var relay = new FakeRelay((r, s) => { lock (seen) seen.Add(r); return FakeRelay.Reply(s, 200, "application/json", Status()); });
        using var _ = Point(relay.Url);
        Relay.Clock = () => new DateTime(2026, 10, 23, 21, 5, 0);
        try
        {
            await Relay.StatusAsync();
            await Relay.CancelPlanAsync("too_expensive");
        }
        finally { Relay.Clock = null; }
        Assert.Equal(2, seen.Count);
        Assert.All(seen, r => Assert.Matches("^[0-9a-f]{64}$", r.Headers["X-RigSight-Secret"]));
        Assert.Equal(seen[0].Headers["X-RigSight-Secret"], seen[1].Headers["X-RigSight-Secret"]);
        Assert.All(seen, r => Assert.Equal("2026-10-23T21:05:00", r.Headers["X-RigSight-Now"]));
        // The secret is never the PC's id.
        Assert.NotEqual(seen[0].Headers["X-RigSight-Device"], seen[0].Headers["X-RigSight-Secret"]);
    }

    // ── Plan actions and Stop ───────────────────────────────────────────
    [Fact]
    public async Task Cancel_sends_the_reason_and_comes_back_with_the_plan_ending()
    {
        string? path = null, body = null;
        await using var relay = new FakeRelay((r, s) => { path = r.Path; body = r.Body; return FakeRelay.Reply(s, 200, "application/json", Status("essentials", "subscription", "cancelled", endsAt: "2026-11-22T10:00:00Z")); });
        using var _ = Point(relay.Url);
        var (status, problem) = await Relay.CancelPlanAsync("not_enough");
        Assert.Null(problem);
        Assert.Equal("/v1/plan/cancel", path);
        Assert.Equal("not_enough", (string?)JsonNode.Parse(body!)!["reason"]);
        Assert.True(status!.Plan!.Ending);
    }

    [Fact]
    public async Task A_plan_action_Lemon_Squeezy_refused_says_nothing_was_charged()
    {
        await using var relay = new FakeRelay((_, s) => FakeRelay.Reply(s, 503, "application/json", "{\"error\":\"unavailable\",\"reason\":\"billing\"}"));
        using var _ = Point(relay.Url);
        var (status, problem) = await Relay.SwitchPlanAsync("complete");
        Assert.Null(status);
        Assert.Contains("Nothing was charged", problem);
    }

    [Fact]
    public async Task A_billing_page_must_be_https()
    {
        await using var relay = new FakeRelay((_, s) => FakeRelay.Reply(s, 200, "application/json", "{\"url\":\"http://evil.example/\"}"));
        using var _ = Point(relay.Url);
        var (url, problem) = await Relay.BillingPageAsync("card");
        Assert.Null(url);
        Assert.NotNull(problem);
    }

    [Theory]
    [InlineData("{\"ok\":true,\"refunded\":true}", true)]
    [InlineData("{\"ok\":true,\"refunded\":false}", false)]
    [InlineData("<html>", false)]
    public async Task Stop_says_whether_the_relay_gave_the_question_back(string reply, bool back)
    {
        await using var relay = new FakeRelay((_, s) => FakeRelay.Reply(s, 200, "application/json", reply));
        using var _ = Point(relay.Url);
        Assert.Equal(back, await Relay.StopAsync("abcdef1234567890"));
    }

    // ── What the app shows ──────────────────────────────────────────────
    private static (AskViewModel Ask, PlansViewModel Plans) Shown(string status) => Ui.Run(() =>
    {
        var ask = new AskViewModel(Kit.OfflineSettings(new RigsightSettings()));
        var plans = new PlansViewModel(ask);
        ask.Plans = plans;
        ask.Show(Relay.ReadStatus(status)!);
        return (ask, plans);
    });

    [Fact]
    public void Free_shows_the_weeks_count_and_three_plans_to_choose_from()
    {
        var (ask, plans) = Shown(Status(used: 3));
        Assert.True(plans.IsFree);
        Assert.Equal("7 of 10 left this week", plans.StateText);
        Assert.Equal("7 of 10 left this week", ask.QuestionsLeftText);
        Assert.Equal(3, plans.Columns.Count);
        Assert.True(plans.Columns[0].IsMine);
        Assert.True(plans.Columns[2].IsBest);
        Assert.True(plans.Columns[1].CanBuy);
        Assert.False(plans.Columns[1].CanSwitch);
        // Each plan says what it adds, in the app's words, never a look-up's name.
        Assert.Contains(plans.Columns[1].Uses, u => u.Text == "Why it crashed or shut off" && u.From == "Crashes");
        Assert.DoesNotContain(plans.Columns[0].Uses, u => u.From == "Crashes");
        Assert.All(plans.Columns.SelectMany(c => c.Uses), u => Assert.DoesNotContain("get_", u.Text));
        Assert.Equal("Everything in Free, and:", plans.Columns[1].Everything);
    }

    [Fact]
    public void A_paid_plan_shows_everything_about_it_up_front()
    {
        var (_, plans) = Shown(Status("essentials", "subscription", used: 46));
        Assert.True(plans.IsSubscription);
        Assert.Equal("Active", plans.StateText);
        Assert.Equal("$5 a month · answers use 8 of 16 pages · web search", plans.Summary);
        Assert.StartsWith("$5 on ", plans.NextPayment);
        Assert.Equal("46 of 100 questions used today · starts again at midnight", plans.UsedText);
        Assert.Equal("Visa ending 4242", plans.CardText);
        Assert.False(plans.HasLastPayment); // the relay sent none
        Assert.True(plans.CanSwitchPlan);
        Assert.StartsWith("on ", plans.NextWhen);
        Assert.Equal(("VISA", "•••• 4242"), (plans.CardBrand, plans.CardDigits));
        Assert.True(plans.Columns[2].CanSwitch);
        Assert.False(plans.Columns[2].CanBuy);
    }

    [Fact]
    public void The_last_payment_takes_the_old_receipts_line()
    {
        var (_, plans) = Shown(Status("essentials", "subscription").Replace("\"price_label\":\"$5\",\"card\"",
            "\"price_label\":\"$5\",\"last_payment\":{\"amount_cents\":500,\"currency\":\"USD\",\"at\":\"2026-10-24T12:00:00Z\"},\"card\""));
        Assert.True(plans.HasLastPayment);
        Assert.StartsWith("$5 on ", plans.LastPayment);
        Assert.Equal("$4.17", new RelayPayment(417, "USD", DateTime.Now).Amount);
        Assert.Equal("12.50 EUR", new RelayPayment(1250, "eur", DateTime.Now).Amount);
    }

    [Fact]
    public void A_cancelled_plan_says_when_it_ends_and_offers_to_keep_it()
    {
        var (_, plans) = Shown(Status("complete", "subscription", "cancelled", endsAt: "2026-11-22T10:00:00Z"));
        Assert.True(plans.IsEnding);
        Assert.True(plans.StateIsWarning);
        Assert.False(plans.HasNextPayment);
        Assert.False(plans.CanSwitchPlan);
        Assert.Contains("you won't be charged again", plans.EndingText);
        Assert.Equal("Keep Complete", plans.KeepText);
    }

    [Fact]
    public void A_failed_payment_is_said_in_amber_and_the_plan_still_works()
    {
        var (_, plans) = Shown(Status("essentials", "subscription", "past_due"));
        Assert.True(plans.IsPastDue);
        Assert.Contains("keeps working", plans.PastDueText);
    }

    [Fact]
    public void A_plan_given_by_support_has_nothing_to_pay_or_manage()
    {
        var (_, plans) = Shown(Status("complete", "gift", endsAt: "2026-12-31T00:00:00Z", card: ""));
        Assert.True(plans.IsGift);
        Assert.False(plans.IsSubscription);
        Assert.False(plans.CanManage);
        Assert.Contains("Nothing to pay", plans.GiftText);
        Assert.Equal("Answers use all 16 pages · web search", plans.Summary);
    }

    [Fact]
    public void Switching_up_estimates_the_difference_and_down_says_it_comes_off_the_next_payment()
    {
        var (_, up) = Shown(Status("essentials", "subscription"));
        Ui.Run(() => up.AskSwitchCommand.Execute(up.Columns[2]));
        Assert.True(up.SwitchOpen);
        Assert.Contains("You pay the difference", up.SwitchText2);
        Assert.Contains("about $", up.SwitchText2);
        var (_, down) = Shown(Status("complete", "subscription"));
        Ui.Run(() => down.AskSwitchCommand.Execute(down.Columns[1]));
        Assert.Contains("comes off your next payment", down.SwitchText2);
    }

    [Fact]
    public void Settings_tiles_show_only_the_plans_pages_and_one_line_for_the_rest()
    {
        var (ask, _) = Shown(Status());
        Ui.Run(() =>
        {
            Assert.Equal(3, ask.Pages.Tiles.Count);
            Assert.Equal(13, ask.Pages.Outside.Count);
            Assert.Equal("13 more pages with a plan", ask.Pages.OutsideTitle);
            ask.Pages.ToggleCommand.Execute(ask.Pages.Tiles[0]);
            Assert.Equal("2 of 3 on", ask.Pages.OnText);
            Assert.True(ask.Pages.SomeOff);
        });
    }

    // ── The Ask window ──────────────────────────────────────────────────
    [Fact]
    public void The_free_weeks_card_offers_the_first_plan_and_keeps_the_question()
    {
        var (ask, _) = Shown(Status(used: 10));
        var wall = Ui.Run(() => ask.LimitWall(new AiLimit("week", "free", new DateTime(2026, 10, 26)), "Why did it restart?"));
        Assert.Equal("That was your last free question this week.", wall.Title);
        Assert.Contains("Essentials gives up to 100 a day for $5 a month", wall.Text);
        Assert.Equal("essentials", wall.Plan);
        Assert.False(wall.Switch);
        Assert.Equal("Get Essentials and ask", wall.MainText);
        Assert.Equal("Why did it restart?", wall.Question);
    }

    [Fact]
    public void A_days_card_on_a_paid_plan_switches_to_the_bigger_one_and_the_biggest_offers_none()
    {
        var (ask, _) = Shown(Status("essentials", "subscription", used: 100));
        var wall = Ui.Run(() => ask.LimitWall(new AiLimit("day", "essentials", null), "q"));
        Assert.Equal("complete", wall.Plan);
        Assert.True(wall.Switch);
        Assert.Equal("Switch to Complete and ask", wall.MainText);
        Assert.True(ask.IsOutOfQuestions);
        Assert.Equal("complete", ask.OutOffer!.Plan);
        Assert.Contains("Complete gives up to 500 a day", ask.OutText);
        var (top, _) = Shown(Status("complete", "subscription", used: 500));
        Assert.False(Ui.Run(() => top.LimitWall(new AiLimit("day", "complete", null), "q")).HasPlan);
        Assert.Null(top.OutOffer);
    }

    [Fact]
    public void Free_questions_paused_are_never_shown_as_none_left_of_none()
    {
        var (ask, plans) = Shown(Status(used: 0, capIs: 0));
        Assert.Equal("Paused for now", plans.StateText);
        Assert.Equal("Free questions paused", ask.QuestionsLeftText);
    }

    [Fact]
    public async Task Stop_hides_what_was_written_keeps_only_the_question_and_gives_it_back()
    {
        string? stopped = null;
        await using var relay = new FakeRelay(async (r, s) =>
        {
            if (r.Path == "/v1/stop") { stopped = (string?)JsonNode.Parse(r.Body)?["question_id"]; await FakeRelay.Reply(s, 200, "application/json", "{\"ok\":true,\"refunded\":true}"); return; }
            if (r.Path != "/v1/ask") { await FakeRelay.Reply(s, 200, "application/json", Status("essentials", "subscription")); return; }
            await FakeRelay.SseStart(s);
            var chunk = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = new JsonObject { ["content"] = "Your PC shut off because the power" } }) };
            await FakeRelay.Write(s, $"data: {chunk.ToJsonString()}\n\n");
            await Task.Delay(20_000); // still writing when Stop is pressed
        });
        var settings = new RigsightSettings();
        settings.Ai.Mode = AiMode.RigSight;
        settings.Ai.FirstQuestionSeen.Add(AiMode.RigSight);
        var ask = Ui.Run(() => new AskViewModel(Kit.OfflineSettings(settings)));
        Ui.Run(() =>
        {
            Relay.TestRoot.Value = relay.Url;
            ask.Draft = "Why did my PC turn off?";
            ask.SendCommand.Execute(null);
        });
        Assert.True(Ui.WaitFor(() => ask.Current!.Messages.OfType<AskWriting>().Any(w => w.Verdict.Length > 0 || w.Parts.Count > 0), 15_000), "the answer never started");
        Ui.Run(() => ask.StopCommand.Execute(null));
        Assert.True(Ui.WaitFor(() => !ask.IsAnswering && stopped is not null, 15_000), "Stop didn't reach the relay");
        Ui.Run(() =>
        {
            var left = ask.Current!.Messages.ToList();
            Assert.Single(left);
            Assert.Equal("Why did my PC turn off?", Assert.IsType<AskQuestion>(left[0]).Text);
            Relay.TestRoot.Value = null;
        });
    }

    [Fact]
    public void An_answer_that_needed_a_locked_page_says_which_once_and_hides_the_line()
    {
        var answer = AskViewModel.ToAnswer("Your GPU stayed cool.\nThe frame rates are on a page your plan doesn't open.\n[locked: games]\n[locked: network]", []);
        Assert.True(answer.HasLocked);
        Assert.Equal("games", answer.Locked!.Id);
        Assert.Equal("Couldn't read Games (not in your plan).", answer.LockedTitle);
        Assert.DoesNotContain(answer.Parts.OfType<AskLine>(), l => l.Text.Contains("locked"));
    }

    [Fact]
    public void A_locked_line_for_a_page_that_doesnt_exist_shows_nothing()
    {
        Assert.False(AskViewModel.ToAnswer("Fine.\n[locked: passwords]", []).HasLocked);
    }
}
