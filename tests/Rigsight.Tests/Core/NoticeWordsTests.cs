using Rigsight.Core;
using Rigsight.Core.Stability;

namespace Rigsight.Tests.Core;

/// <summary>
/// The notifications' words (rewritten 2026-11-05, the user's rules): the title says what happened, the most useful fact
/// under it, temperatures with their unit, nothing said twice, no "Click for the full picture.".
/// </summary>
public sealed class NoticeWordsTests
{
    [Fact]
    public void A_hot_part_is_named_in_the_title_with_its_reading_and_limit_under_it()
    {
        Units.Fahrenheit = false;
        Assert.Equal(("Your GPU is running hot", "It has been at 86°C for over 10 seconds, above your 83°C limit, while Cyberpunk 2077 was running."),
            NoticeWords.Running([new("GPU", 86, 83)], 10, "Cyberpunk 2077"));
        Assert.Equal(("Your GPU is running hot", "Its hot spot has been at 104°C for over 10 seconds, above your 100°C limit."),
            NoticeWords.Running([new("GPU hot spot", 104, 100)], 10, null));
        Assert.Equal(("Your GPU is running hot", "GPU 86°C and GPU hot spot 104°C, above your limits for over 10 seconds."),
            NoticeWords.Running([new("GPU", 86, 83), new("GPU hot spot", 104, 100)], 10, null)); // the hot spot is the GPU's
        Assert.Equal(("Your CPU and GPU are running hot", "CPU 91°C and GPU 86°C, above your limits for over 10 seconds while Cyberpunk 2077 was running."),
            NoticeWords.Running([new("CPU", 91, 85), new("GPU", 86, 83)], 10, "Cyberpunk 2077"));
    }

    [Fact]
    public void A_session_and_a_day_say_what_happened_first()
    {
        Units.Fahrenheit = false;
        Assert.Equal(("You played Dota 2 for 2h 14m", "The CPU peaked at 71°C and the GPU at 68°C."), NoticeWords.Session("Dota 2", 8040, 71, 68));
        Assert.Equal("The GPU peaked at 68°C.", NoticeWords.Session("Dota 2", 8040, null, 68).Body);
        var today = new DateTime(2026, 11, 5);
        Assert.Equal(("Yesterday: 6h 12m active", "Mostly Dota 2, for 3h 05m. The CPU peaked at 78°C and the GPU at 72°C."),
            NoticeWords.Recap(today.AddDays(-1), today, 22320, "Dota 2", 11100, 78, 72));
        Assert.Equal("Monday: 2h 00m active", NoticeWords.Recap(today.AddDays(-3), today, 7200, null, 0, null, null).Title);
        Assert.Equal("", NoticeWords.Recap(today.AddDays(-3), today, 7200, null, 0, null, null).Body);
    }

    [Fact]
    public void The_PC_going_down_says_when_and_why_under_a_title_that_says_what()
    {
        var now = new DateTime(2026, 11, 5, 10, 0, 0);
        Assert.Equal("Your PC shut off suddenly", NoticeWords.PcDownTitle(CrashKind.UnexpectedShutdown));
        Assert.Equal("Windows crashed with a blue screen", NoticeWords.PcDownTitle(CrashKind.SystemCrash));
        Assert.Equal("It happened on Tuesday at 9:14 PM.", NoticeWords.PcDownBody(new DateTime(2026, 11, 3, 21, 14, 0), now, null, null));
        Assert.Equal("It happened yesterday at 9:14 PM.", NoticeWords.PcDownBody(new DateTime(2026, 11, 4, 21, 14, 0), now, null, null));
        Assert.Equal("It happened at 8:05 AM. Windows logged it.", NoticeWords.PcDownBody(new DateTime(2026, 11, 5, 8, 5, 0), now, null, "Windows logged it."));
        Assert.Equal("78°C", NoticeWords.Temp(78));
    }
}
