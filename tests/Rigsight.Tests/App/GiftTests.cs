using Rigsight.Core.Ai;
using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;
using static Rigsight.Tests.App.PlansTests;

namespace Rigsight.Tests.App;

/// <summary>
/// Questions given from the admin site (the person's rule, 2026-10-25): kept until used, on any plan, used after the
/// plan's day or the free week. Read from the relay's status, told once in a card above the question box, closed by
/// "Got it" for good, counted in what can be asked, and said on their own line in Settings and the Plans screen.
/// </summary>
public sealed class GiftTests
{
    /// <summary>A status with <paramref name="given"/> in the plan and these gifts (JSON objects) in the reply.</summary>
    private static string With(string status, int given, params string[] gifts) => status
        .Replace("\"can_manage\"", $"\"given\":{given},\"can_manage\"")
        .Replace("\"search\":true", $"\"search\":true,\"gifts\":[{string.Join(",", gifts)}]");

    private static string Gift(long id, int n, string? note = null) =>
        $$"""{"id":{{id}},"n":{{n}},"note":{{(note is null ? "null" : $"\"{note}\"")}},"at":"2026-10-25T10:00:00.000Z"}""";

    private static (AskViewModel Ask, PlansViewModel Plans, SettingsModel Settings) Shown(string status, params long[] seen) => Ui.Run(() =>
    {
        var settings = new RigsightSettings();
        settings.Ai.GiftsSeen = [.. seen];
        var model = Kit.OfflineSettings(settings);
        var ask = new AskViewModel(model);
        var plans = new PlansViewModel(ask);
        ask.Plans = plans;
        ask.Show(Relay.ReadStatus(status)!);
        return (ask, plans, model);
    });

    [Fact]
    public void Gifts_and_the_given_count_are_read_and_a_broken_gift_is_left_out()
    {
        var s = Relay.ReadStatus(With(Status("complete", "subscription"), 30, Gift(7, 10), Gift(6, 20, "Thanks for the logs."), """{"id":4,"n":0}""", """{"n":3}"""))!;
        Assert.Equal([7L, 6L], s.Gifts.Select(g => g.Id));
        Assert.Equal("Thanks for the logs.", s.Gifts[1].Note);
        Assert.Equal(30, s.Plan!.Given);
        Assert.Equal(500, s.Plan.Cap);
        Assert.Equal(527, s.Plan.AllLeft);
    }

    [Fact]
    public void An_older_relay_has_no_gifts_and_nothing_is_shown()
    {
        var (ask, plans, _) = Shown(Status("complete", "subscription"));
        Assert.Null(ask.Gift);
        Assert.False(plans.HasGiven);
        Assert.Equal("Your plan", plans.Columns.Single(c => c.IsMine).MineText);
    }

    [Fact]
    public void On_a_plan_the_card_says_they_come_after_the_day_s_own_and_don_t_run_out()
    {
        var (ask, plans, _) = Shown(With(Status("complete", "subscription", used: 23), 20, Gift(1, 20, "Thanks for reporting the fan bug.")));
        Assert.Equal("You've got 20 extra questions", ask.Gift!.Title);
        Assert.Equal("They're used once Complete's 500 for the day are gone, and they don't run out.", ask.Gift.Text);
        Assert.Equal("Thanks for reporting the fan bug.", ask.Gift.Note);
        Assert.Equal(497, ask.QuestionsLeft);
        Assert.True(plans.HasGiven);
        Assert.Equal("20 given by RigSight · used after Complete's 500 a day, they don't run out", plans.GivenText);
        Assert.Equal("Your plan · 20 given", plans.Columns.Single(c => c.IsMine).MineText);
    }

    [Fact]
    public void Two_gifts_never_seen_are_told_as_one_and_a_seen_one_isn_t_told_again()
    {
        var (both, _, _) = Shown(With(Status("complete", "subscription"), 30, Gift(2, 10), Gift(1, 20, "Thanks.")));
        Assert.Equal("You've got 30 extra questions", both.Gift!.Title);
        Assert.Equal("Thanks.", both.Gift.Note);
        Assert.Equal([2L, 1L], both.Gift.Ids);

        var (later, _, _) = Shown(With(Status("complete", "subscription"), 30, Gift(2, 10), Gift(1, 20, "Thanks.")), seen: 1);
        Assert.Equal("You've got 10 extra questions", later.Gift!.Title);
        Assert.StartsWith("Given by RigSight. They're used once", later.Gift.Text);
        Assert.Null(later.Gift.Note);
    }

    [Fact]
    public void On_Free_with_the_week_used_up_given_ones_take_the_out_card_s_place()
    {
        // All 10 of the week used, 5 given: 5 can be asked, so the "No free questions left" card goes and Send works.
        var (ask, plans, _) = Shown(With(Status("free", used: 10), 5, Gift(3, 5)));
        Assert.Equal("You've got 5 extra questions", ask.Gift!.Title);
        Assert.Equal("Given by RigSight. They're used after your 10 free a week, and they don't run out.", ask.Gift.Text);
        Assert.Equal(5, ask.QuestionsLeft);
        Assert.False(ask.IsOutOfQuestions);
        Assert.Equal("All 10 used this week", plans.FreeLine);
        Assert.Equal("Back to 10 on Monday.", plans.FreeWhen);
        Assert.Equal("5 given by RigSight · used after this week's 10, they don't run out", plans.GivenText);
        Assert.Equal("All 10 used this week · 5 given", plans.Columns.Single(c => c.IsMine).MineText);
    }

    [Theory]
    [InlineData(2, "2 given questions left")]
    [InlineData(1, "1 given question left")]
    public void With_the_week_used_up_the_line_counts_the_given_ones(int given, string said)
    {
        var (ask, _, _) = Shown(With(Status("free", used: 10), given));
        Assert.Equal(said, ask.QuestionsLeftText);
        // The line under the question box since board 6 (2026-10-26), in place of the top bar's pill.
        Assert.True(ask.ShowQuestionsLine);
        Assert.Equal(said, ask.QuestionsLine);
    }

    [Fact]
    public void The_last_given_question_is_said_as_that_and_only_the_week_s_come_back()
    {
        var (ask, _, _) = Shown(With(Status("free", used: 10), 1));
        Assert.True(ask.IsLastQuestion);
        Assert.Equal("This is the last of the questions given to you.", ask.LastQuestionTitle);
        Assert.Equal("Your 10 free ones are back on Monday.", ask.LastQuestionText);
    }

    [Fact]
    public void Some_of_the_week_s_and_some_given_are_both_said_in_the_pill()
    {
        var (ask, _, _) = Shown(With(Status("free", used: 9), 2));
        Assert.Equal(3, ask.QuestionsLeft);
        Assert.Equal("1 of 10 left this week + 2 given", ask.QuestionsLeftText);
        Assert.Equal("1 of 10 free questions left this week + 2 given", ask.QuestionsLine);
    }

    [Fact]
    public void Got_it_closes_the_card_for_good()
    {
        string status = With(Status("complete", "subscription"), 20, Gift(9, 20));
        var (ask, _, settings) = Shown(status);
        Ui.Run(() => ask.CloseGiftCommand.Execute(null));
        Assert.Null(ask.Gift);
        Assert.Contains(9L, settings.Current.Ai.GiftsSeen);
        // The relay keeps sending it for 30 days; it isn't told again.
        Ui.Run(() => ask.Show(Relay.ReadStatus(status)!));
        Assert.Null(ask.Gift);
    }
}
