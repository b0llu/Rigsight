using Rigsight.Core.Ai;
using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;
using static Rigsight.Tests.App.PlansTests;

namespace Rigsight.Tests.App;

/// <summary>
/// The free trial of Complete (2026-10-28): read from the relay's status (offered, running, ended), counted in days on the
/// line under the question box, told the day before it ends and once it has ended, and shown as Complete in the consent
/// while it's on offer (the first question starts it).
/// </summary>
[Collection("UI")]
public sealed class TrialTests
{
    private static string WithTrial(string status, string trial) => status.Replace("\"search\":true", $"\"search\":true,\"trial\":{trial}");

    private static AskViewModel Shown(string status, params string[] seen) => Shown(status, out _, seen);

    private static AskViewModel Shown(string status, out SettingsModel model, params string[] seen)
    {
        var settings = new RigsightSettings();
        settings.Ai.TrialNotesSeen = [.. seen];
        var m = model = Kit.OfflineSettings(settings);
        return Ui.Run(() =>
        {
            var ask = new AskViewModel(m);
            ask.Show(Relay.ReadStatus(status)!);
            return ask;
        });
    }

    [Fact]
    public void The_trial_is_read_in_each_state()
    {
        var offered = Relay.ReadStatus(WithTrial(Status(), """{"state":"offered","days":7,"plan":"complete","cap":300}"""))!.Trial!;
        Assert.True(offered.Offered);
        Assert.Equal((7, "complete", 300), (offered.Days, offered.Plan, offered.Cap));
        var running = Relay.ReadStatus(WithTrial(Status("complete", "trial", endsAt: "2026-11-04T04:42:00.000Z"),
            """{"state":"running","started":"2026-10-28T04:42:00.000Z","ends_at":"2026-11-04T04:42:00.000Z","plan":"complete"}"""))!;
        Assert.True(running.Trial!.Running);
        Assert.True(running.Plan!.IsTrial);
        Assert.Equal(TimeSpan.FromDays(7), running.Trial.Ends - running.Trial.Started);
        Assert.Null(Relay.ReadStatus(Status())!.Trial);
    }

    // Free's box said nothing of the trial waiting for the first question (2026-10-28).
    [Fact]
    public void Free_says_the_trial_is_waiting_until_it_starts()
    {
        var plans = Ui.Run(() =>
        {
            var ask = new AskViewModel(Kit.OfflineSettings(new RigsightSettings()));
            var p = new PlansViewModel(ask);
            ask.Plans = p;
            ask.Show(Relay.ReadStatus(WithTrial(Status(), """{"state":"offered","days":7,"plan":"complete","cap":300}"""))!);
            return p;
        });
        Assert.Equal("Try Complete free for 7 days. It starts with your first question. No card, nothing to cancel.", plans.TrialLine);
        var ended = Ui.Run(() =>
        {
            var ask = new AskViewModel(Kit.OfflineSettings(new RigsightSettings()));
            var p = new PlansViewModel(ask);
            ask.Plans = p;
            ask.Show(Relay.ReadStatus(WithTrial(Status(), """{"state":"ended","plan":"complete"}"""))!);
            return p;
        });
        Assert.False(ended.HasTrialLine);
    }

    [Theory]
    [InlineData(6.5, "Complete trial · 7 days left")]
    [InlineData(2.2, "Complete trial · 3 days left")]
    public void Days_left_are_counted_up_to_the_end(double daysLeft, string said)
    {
        var now = new DateTime(2026, 10, 28, 9, 0, 0);
        Assert.Equal(said, AskViewModel.TrialLeft("Complete", now.AddDays(daysLeft), now));
    }

    [Fact]
    public void The_last_day_says_when_it_ends()
    {
        var now = new DateTime(2026, 10, 28, 9, 0, 0);
        Assert.Equal("Complete trial ends today at 21:30", AskViewModel.TrialLeft("Complete", now.Date.AddHours(21.5), now));
        Assert.Equal("Complete trial ends tomorrow at 08:00", AskViewModel.TrialLeft("Complete", now.Date.AddDays(1).AddHours(8), now));
    }

    [Fact]
    public void The_line_under_the_box_counts_days_then_today()
    {
        var ends = DateTime.Now.AddDays(5.5).ToUniversalTime().ToString("o");
        var ask = Shown(WithTrial(Status("complete", "trial", used: 4, endsAt: ends, capIs: 300), $$"""{"state":"running","ends_at":"{{ends}}","plan":"complete"}"""));
        Assert.Equal("Complete trial · 6 days left · 296 of 300 left today", ask.QuestionsLine);
    }

    [Fact]
    public void The_day_before_it_ends_it_says_so_once_with_the_plans()
    {
        var ends = DateTime.Now.AddHours(20);
        var s = Relay.ReadStatus(WithTrial(Status("complete", "trial", endsAt: ends.ToUniversalTime().ToString("o")),
            $$"""{"state":"running","ends_at":"{{ends.ToUniversalTime():o}}","plan":"complete"}"""))!;
        var note = AskViewModel.TrialNoteFor(s, []);
        Assert.NotNull(note);
        Assert.True(note!.IsTrial);
        Assert.StartsWith("Your Complete trial ends ", note.Title);
        Assert.Equal("Then you're on Free: 10 questions a week. Your conversations stay.", note.Text);
        Assert.Null(AskViewModel.TrialNoteFor(s, [note.TrialKey!]));
        // Days before the end, nothing.
        var early = Relay.ReadStatus(WithTrial(Status("complete", "trial"), $$"""{"state":"running","ends_at":"{{DateTime.UtcNow.AddDays(3):o}}","plan":"complete"}"""))!;
        Assert.Null(AskViewModel.TrialNoteFor(early, []));
    }

    [Fact]
    public void Once_ended_it_says_so_once_and_closing_keeps_it_closed()
    {
        var ends = DateTime.UtcNow.AddHours(-2).ToString("o");
        var ask = Shown(WithTrial(Status(), $$"""{"state":"ended","started":"{{DateTime.UtcNow.AddDays(-7):o}}","ends_at":"{{ends}}","plan":"complete"}"""), out var model);
        Assert.Equal("Your Complete trial has ended", ask.Gift?.Title);
        Ui.Run(() => ask.CloseGiftCommand.Execute(null));
        Assert.Null(ask.Gift);
        Assert.Single(model.Current.Ai.TrialNotesSeen);
    }

    [Fact]
    public void On_offer_the_consent_is_Complete_and_says_when_the_trial_starts()
    {
        var ask = Shown(WithTrial(Status(), """{"state":"offered","days":7,"plan":"complete","cap":300}"""));
        Assert.Equal("Complete free for 7 days, from your first question", ask.FirstExtra);
        Assert.Equal("It can also search the web", ask.FirstMore);
        Assert.Equal(AiPages.All.Count, ask.PlanPagesNow()!.Count);
    }
}
