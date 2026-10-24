using Rigsight.Core.Ai;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.App;

/// <summary>
/// A new chat's starters (approved mock-up 2026-10-24): four questions, each with the page it reads, never one for a page
/// switched off or outside the plan; the general ones when the records have nothing new. And the PC in a line.
/// </summary>
public sealed class AskStartersTests
{
    private static readonly string NoHistory = Path.Combine(Path.GetTempPath(), "rigsight-no-such.db");

    [Fact]
    public void Without_records_the_general_four_show_each_with_its_page()
    {
        var starters = new AiTools(new RigsightSettings(), NoHistory).Starters(new DateTime(2026, 10, 24, 12, 0, 0));
        Assert.True(starters.Count is >= 3 and <= 4);
        Assert.Contains(starters, s => s.Text == "Are my temperatures normal for my parts?" && s.Page == AiPages.Temperatures);
        Assert.Equal(starters.Count, starters.Select(s => s.Page).Distinct().Count());
    }

    [Fact]
    public void The_free_plans_three_pages_each_get_a_starter()
    {
        // Free opens Home, the parts and Temperatures: one starter each, not Temperatures alone (it showed one, 2026-10-24).
        string[] free = [AiPages.Home, AiPages.Parts, AiPages.Temperatures];
        var starters = new AiTools(new RigsightSettings(), NoHistory).Starters(DateTime.Now, free.Contains);
        Assert.Equal(free.Order(), starters.Select(s => s.Page).Order());
        Assert.Contains(starters, s => s.Text == "How is my PC doing today?");
        Assert.Contains(starters, s => s.Text == "Is my graphics driver up to date?");
    }

    [Fact]
    public void A_page_switched_off_or_outside_the_plan_gets_no_starter()
    {
        var settings = new RigsightSettings();
        settings.Ai.SetSends(AiPages.Games, false);
        var starters = new AiTools(settings, NoHistory).Starters(DateTime.Now, page => page != AiPages.Crashes);
        Assert.DoesNotContain(starters, s => s.Page is AiPages.Games or AiPages.Crashes);
    }

    private static Insight Line(string ask, string page, int priority = 76, InsightTone tone = InsightTone.Warn) =>
        new("", ask, tone, "test", priority) { Ask = ask, AskPage = page };

    private static string TempLog() => Path.Combine(Path.GetTempPath(), $"rigsight-starters-{Guid.NewGuid():N}.json");

    [Fact]
    public void Home_lines_that_raise_a_question_come_first_each_from_its_own_page()
    {
        var lines = new StarterLines(
            [Line("Why does Discord keep crashing?", AiPages.Crashes), Line("Why did my GPU slow itself down to stay cool?", AiPages.Temperatures, 80, InsightTone.Hot),
             Line("Is a 91° GPU peak safe for my PC?", AiPages.Temperatures, 85)], []);
        var starters = new AiTools(new RigsightSettings(), NoHistory).Starters(DateTime.Now, lines: lines);
        // A chip slowing itself (hot) over a peak only worth a look, though the peak ranks higher on Home.
        Assert.Equal("Why did my GPU slow itself down to stay cool?", starters[0].Text);
        Assert.Equal("Why does Discord keep crashing?", starters[1].Text);
        Assert.DoesNotContain(starters, s => s.Text.Contains("peak"));
        Assert.Equal(starters.Count, starters.Select(s => s.Page).Distinct().Count());
        Assert.Equal(4, starters.Count);
    }

    [Fact]
    public void Todays_lines_rank_over_the_day_befores()
    {
        var lines = new StarterLines([Line("Why does Steam keep crashing?", AiPages.Crashes, 70)], [Line("Why does Discord keep crashing?", AiPages.Crashes, 76)]);
        Assert.Equal("Why does Steam keep crashing?", new AiTools(new RigsightSettings(), NoHistory).Starters(DateTime.Now, lines: lines)[0].Text);
    }

    [Fact]
    public void A_question_already_asked_figures_aside_does_not_show_again()
    {
        var lines = new StarterLines([Line("Why did my GPU fan stop for 12 min?", AiPages.Fans, 88)], []);
        var starters = new AiTools(new RigsightSettings(), NoHistory).Starters(DateTime.Now, lines: lines, asked: ["why did my GPU fan stop for 40 min?"]);
        Assert.DoesNotContain(starters, s => s.Page == AiPages.Fans && s.Text.Contains("stop"));
    }

    [Fact]
    public void A_picked_starter_rests_and_one_shown_for_days_unpicked_sinks()
    {
        var path = TempLog();
        try
        {
            var log = new AiStarterLog(path);
            var now = new DateTime(2026, 10, 24, 12, 0, 0);
            var picked = Line("Why does Discord keep crashing?", AiPages.Crashes, 90);
            var tools = new AiTools(new RigsightSettings(), NoHistory);
            log.Picked(AiStarterLog.TopicOf(picked.Ask!), now.AddDays(-1));
            Assert.DoesNotContain(tools.Starters(now, lines: new([picked], []), log: log), s => s.Text == picked.Ask);
            Assert.Contains(tools.Starters(now.AddDays(3), lines: new([picked], []), log: log), s => s.Text == picked.Ask);

            // Shown on six days and never picked: under a smaller line it used to beat.
            var stale = Line("Is a 91° GPU peak safe for my PC?", AiPages.Temperatures, 85);
            var fresh = Line("Why did my GPU slow itself down to stay cool?", AiPages.Temperatures, 70);
            for (int d = 0; d < 6; d++) log.Shown([AiStarterLog.TopicOf(stale.Ask!)], now.AddDays(d - 6));
            log.Shown([AiStarterLog.TopicOf(stale.Ask!)], now.AddDays(-1)); // the same day again counts once
            var starters = new AiTools(new RigsightSettings(), NoHistory).Starters(now, lines: new([stale, fresh], []), log: new AiStarterLog(path));
            Assert.Contains(starters, s => s.Text == fresh.Ask);
            Assert.DoesNotContain(starters, s => s.Text == stale.Ask);
            Assert.Equal(6, new AiStarterLog(path).Get(AiStarterLog.TopicOf(stale.Ask!))!.Days);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("AMD Ryzen 7 5700X3D 8-Core Processor", "Ryzen 7 5700X3D")]
    [InlineData("12th Gen Intel(R) Core(TM) i7-12700K", "Core i7-12700K")]
    [InlineData("Intel(R) Core(TM) i5-8400 CPU @ 2.80GHz", "Core i5-8400")]
    public void The_processor_is_named_as_people_say_it(string name, string said) => Assert.Equal(said, AiTools.ShortCpu(name));
}
