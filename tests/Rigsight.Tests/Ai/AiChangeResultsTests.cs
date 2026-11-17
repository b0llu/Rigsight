using Rigsight.Core.Ai;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;

namespace Rigsight.Tests.Ai;

/// <summary>
/// The changes look-up carries RigSight's own before-and-after result with every figure it rests on (2026-11-16): asked
/// to explain "ran 6° hotter on the CPU in the 9 days after the changes on 3 Nov", a model that only had the plays after
/// the change compared them with a usual made of those same plays and told the person RigSight was wrong.
/// </summary>
public sealed class AiChangeResultsTests
{
    private static ChangeResult Result() => new()
    {
        Day = new DateTime(2026, 11, 3),
        Changes = [new SystemChange(new DateTime(2026, 11, 3, 9, 0, 0), ChangeKind.WindowsUpdate, "Windows update KB5070000")],
        From = new DateTime(2026, 10, 20), To = new DateTime(2026, 11, 13), DaysBefore = 14, DaysAfter = 9,
        Tone = EffectTone.Worse, Sure = Confidence.Likely, Cause = "THE UPDATE",
        Line = change => $"Phantom Blade Zero ran 6° hotter on the CPU in the 9 days after {change}",
        Context = "Same game at the same load, with the change at rest taken out.",
        Rows = [new("CPU in Phantom Blade Zero", "Same minutes", "57°", "63°", EffectTone.Worse, "6° hotter, 5° beyond what rest moved")],
        Checks = [new(true, "The same game on both sides", "4h 29m of Phantom Blade Zero before, 26h 31m in the 9 days after.")],
    };

    [Fact]
    public void A_result_is_given_with_its_days_figures_and_what_it_rests_on()
    {
        string text = AiTools.ChangeResults([Result()]);
        Assert.Contains("Result: Phantom Blade Zero ran 6° hotter on the CPU in the 9 days after", text);
        Assert.Contains("Days compared: 20 Oct to 2 Nov before (14 days), 4 Nov to 12 Nov 2026 after (9 days)", text);
        Assert.Contains("CPU in Phantom Blade Zero: 57° before, 63° after, 6° hotter, 5° beyond what rest moved (Same minutes)", text);
        Assert.Contains("Rests on: The same game on both sides. 4h 29m of Phantom Blade Zero before", text);
        Assert.Contains("Windows update KB5070000 [WindowsUpdate]", text);
    }

    [Fact]
    public void The_model_is_told_whole_play_averages_are_another_measure_and_cant_overturn_it()
    {
        string text = AiTools.ChangeResults([Result()]);
        Assert.Contains("steady minutes", text);
        Assert.Contains("can't confirm or overturn these figures", text);
    }
}
