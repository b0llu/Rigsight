using Rigsight.Core.Ai;
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
    public void A_page_switched_off_or_outside_the_plan_gets_no_starter()
    {
        var settings = new RigsightSettings();
        settings.Ai.SetSends(AiPages.Games, false);
        var starters = new AiTools(settings, NoHistory).Starters(DateTime.Now, page => page != AiPages.Crashes);
        Assert.DoesNotContain(starters, s => s.Page is AiPages.Games or AiPages.Crashes);
    }

    [Theory]
    [InlineData("AMD Ryzen 7 5700X3D 8-Core Processor", "Ryzen 7 5700X3D")]
    [InlineData("12th Gen Intel(R) Core(TM) i7-12700K", "Core i7-12700K")]
    [InlineData("Intel(R) Core(TM) i5-8400 CPU @ 2.80GHz", "Core i5-8400")]
    public void The_processor_is_named_as_people_say_it(string name, string said) => Assert.Equal(said, AiTools.ShortCpu(name));
}
