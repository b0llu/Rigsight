using Rigsight.Core.Ask;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.Tests.Data;

namespace Rigsight.Tests.Ask;

/// <summary>Two times in one question, readings by name, and what they answer.</summary>
public sealed class AskCompareAndMetricTests
{
    // A Monday afternoon.
    private static readonly DateTime Now = new(2026, 10, 5, 14, 30, 0);
    private static readonly AskEngine Engine = new(new AskRouter(new AskEmbedder()));
    private static long U(DateTime t) => TimeUtil.ToUnix(t);

    [Theory]
    [InlineData("was my gpu hotter this week than last week", "this week", "last week")]
    [InlineData("did i game more last week than the week before", "last week", "in the week of 21 Sep")]
    [InlineData("more data than last month?", "this month", "last month")]
    [InlineData("was i on more yesterday than on friday", "yesterday", "on Fri 2 Oct")]
    [InlineData("cpu load today vs yesterday", "today", "yesterday")]
    [InlineData("compare september and august", "last month", "in August")]
    [InlineData("was it hotter than the day before yesterday", "today", "on Sat 3 Oct")]
    public void Two_times_are_the_one_asked_about_and_the_one_it_is_set_against(string text, string subject, string against)
    {
        var two = AskTime.ParseTwo(text, Now);
        Assert.NotNull(two);
        Assert.Equal((subject, against), (two.Value.Subject.Label, two.Value.Against.Label));
    }

    [Theory]
    [InlineData("how hot did it get yesterday")]
    [InlineData("what changed since friday")]
    [InlineData("is my gpu hotter than before")] // no second time: the answer about heat over months has its own way
    [InlineData("did my pc crash this week")]
    public void One_time_is_not_a_comparison(string text) => Assert.Null(AskTime.ParseTwo(text, Now));

    [Theory]
    [InlineData("what was my average cpu load yesterday", AskMetric.CpuLoad, AskAggregate.Average)]
    [InlineData("gpu usage while playing", AskMetric.GpuLoad, AskAggregate.Any)]
    [InlineData("highest gpu load today", AskMetric.GpuLoad, AskAggregate.Highest)]
    [InlineData("how much power did my gpu draw", AskMetric.GpuPower, AskAggregate.Any)]
    [InlineData("how many watts does my cpu use", AskMetric.CpuPower, AskAggregate.Any)]
    [InlineData("lowest cpu temperature today", AskMetric.CpuTemp, AskAggregate.Lowest)]
    [InlineData("average gpu temp last week", AskMetric.GpuTemp, AskAggregate.Average)]
    [InlineData("gpu hot spot yesterday", AskMetric.HotSpot, AskAggregate.Any)]
    [InlineData("average memory in use today", AskMetric.Ram, AskAggregate.Average)]
    public void A_reading_named_is_read_by_rule(string text, AskMetric metric, AskAggregate aggregate)
    {
        var q = Engine.Router.Read(text, Now, []);
        Assert.Equal((AskIntent.Metric, metric, aggregate), (q.Intent, q.Metric, q.Aggregate));
    }

    [Theory]
    // Their highs, and "is it OK", keep the answers they have.
    [InlineData("how hot did my gpu get yesterday", AskIntent.Temps)]
    [InlineData("is my cpu too hot", AskIntent.Temps)]
    [InlineData("whats eating all my memory", AskIntent.Memory)]
    [InlineData("why is my pc slow", AskIntent.Slow)]
    public void Other_questions_about_a_part_stay_what_they_were(string text, AskIntent intent) => Assert.Equal(intent, Engine.Router.Read(text, Now, []).Intent);

    /// <summary>Two weeks of evenings in one game: 2 hours a night last week, 1 the week before; a known load and power each minute.</summary>
    private static TestDb Pc(double gpuNow = 70, double gpuThen = 70, double restNow = 40, double restThen = 40)
    {
        var t = new TestDb("ask");
        long game = t.Db.UpsertApp("game.exe", "Star Racer", @"C:\Games\Star Racer\game.exe", AppCategory.Game);
        long desk = t.Db.UpsertApp("notes.exe", "Notes", @"C:\Apps\notes.exe", AppCategory.Productivity);
        for (var day = Now.Date.AddDays(-14); day < Now.Date; day = day.AddDays(1))
        {
            bool lastWeek = day >= Now.Date.AddDays(-7);
            int minutes = lastWeek ? 120 : 60;
            double gpu = lastWeek ? gpuNow : gpuThen, rest = lastWeek ? restNow : restThen;
            // An hour at the desk first (temperatures at rest), a break, then the game.
            for (int m = 0; m < 60; m++)
                t.Db.WriteMinute(new SystemMinute { Ts = U(day.AddHours(16).AddMinutes(m)), GpuTemp = rest, GpuTempMax = rest, CpuTemp = rest, CpuTempMax = rest, GpuLoad = 3, CpuLoad = 5, RamUsed = 6, GpuPower = 30, CpuPower = 20, FgApp = desk, ActiveSec = 60 });
            var start = day.AddHours(19);
            for (int m = 0; m < minutes; m++)
                t.Db.WriteMinute(new SystemMinute { Ts = U(start.AddMinutes(m)), GpuTemp = gpu, GpuTempMax = gpu + 1, CpuTemp = 60, CpuTempMax = 62, GpuLoad = 98, CpuLoad = 40, RamUsed = 12, GpuPower = 250, CpuPower = 90, FgApp = game, GpuApp = game, ActiveSec = 60 });
            t.Db.AddAppHour(new AppHour { Ts = U(start), AppId = game, FgSec = 3600 });
            if (lastWeek) t.Db.AddAppHour(new AppHour { Ts = U(start.AddHours(1)), AppId = game, FgSec = 3600 });
            t.Db.AddAppHour(new AppHour { Ts = U(day.AddHours(16)), AppId = desk, FgSec = 3600 });
        }
        return t;
    }

    private static AskAnswer Ask(TestDb t, string text, AskContext? context = null)
    {
        using var reader = t.Reader();
        return Engine.Ask(reader, new RigsightSettings(), text, context, Now);
    }

    [Fact]
    public void Time_is_set_against_time()
    {
        using var t = Pc();
        var a = Ask(t, "did i play more last week than the week before");
        Assert.Equal("More last week: you played for 14h 00m, against 7h 00m in the week of 21 Sep (2 times as much).", a.Lead.Replace("**", ""));
        Assert.Equal([("Last week", "14h 00m"), ("In the week of 21 Sep", "7h 00m")], a.Facts.Select(f => (f.Label, f.Value)));
        Assert.Equal("Time on the PC · last week against the week of 21 Sep", a.Read.Replace("Most used", "Time on the PC"));

        // A week still going on is set against the same part of the other, and says so.
        var now = Ask(t, "was i on my pc more this week than last week");
        Assert.StartsWith("Nothing to compare", now.Lead); // Monday before 4 PM on both sides: nothing yet
        Assert.Contains("This week isn't over, so it is set against the same part of last week.", now.Paragraphs);

        // A time from before the records can't be compared, and no numbers are given for it.
        var early = Ask(t, "did i play more in september than in august");
        Assert.StartsWith("I can't set those side by side: nothing is recorded in August.", early.Lead);
        Assert.Empty(early.Facts);
    }

    [Fact]
    public void Heat_is_compared_only_like_for_like_with_the_room_taken_out()
    {
        // 8° hotter in the same game at the same load, the room the same: that's the PC.
        using (var hotter = Pc(gpuNow: 78, gpuThen: 70))
        {
            var a = Ask(hotter, "was my gpu hotter last week than the week before");
            Assert.StartsWith("Yes, hotter last week: in Star Racer at the same steady load, the GPU held 78°, against 70° in the week of 21 Sep.", a.Lead.Replace("**", ""));
            Assert.Contains("The temperatures at rest were the same both times, so it isn't the room.", a.Paragraphs);
            Assert.NotNull(a.Advice);
        }
        // 8° hotter, but so was everything at rest: the room, not the PC.
        using (var room = Pc(gpuNow: 78, gpuThen: 70, restNow: 47, restThen: 40))
        {
            var a = Ask(room, "was my gpu hotter last week than the week before");
            Assert.StartsWith("Not in a way that says anything about the PC", a.Lead.Replace("**", ""));
            Assert.Null(a.Advice);
        }
        // The same within a degree.
        using (var same = Pc(gpuNow: 71, gpuThen: 70))
            Assert.StartsWith("No, about the same", Ask(same, "was my gpu hotter last week than the week before").Lead.Replace("**", ""));
        // Nothing like for like on one side: no verdict, the highs for reference only.
        using (var t = Pc())
        {
            var a = Ask(t, "was my gpu hotter yesterday than today");
            Assert.StartsWith("I can't", a.Lead.Replace("**", ""));
        }
    }

    [Fact]
    public void A_reading_is_averaged_over_the_time_and_over_the_app_when_one_is_named()
    {
        using var t = Pc();
        var day = Ask(t, "what was my average gpu load yesterday");
        // 60 minutes at 3% and 120 at 98%.
        Assert.Equal("GPU load averaged 66% yesterday.", day.Lead.Replace("**", ""));
        Assert.Equal([("Average", "66%"), ("Highest", "98%"), ("Lowest", "3%")], day.Facts.Select(f => (f.Label, f.Value)));
        Assert.Contains("with Star Racer in front", day.Paragraphs[0]);

        var inGame = Ask(t, "average gpu load in star racer yesterday");
        Assert.Equal("GPU load averaged 98% while Star Racer was in front yesterday.", inGame.Lead.Replace("**", ""));
        var power = Ask(t, "how much power did my gpu draw yesterday");
        Assert.StartsWith("GPU power draw averaged 177 W yesterday.", power.Lead.Replace("**", ""));
        var lowest = Ask(t, "lowest cpu temperature yesterday");
        Assert.Equal("CPU temperature was lowest at 40° yesterday.", lowest.Lead.Replace("**", ""));

        // The follow-up keeps the reading.
        var before = Ask(t, "and the day before?", day.Context);
        Assert.Equal((AskIntent.Metric, AskMetric.GpuLoad), (before.Query!.Intent, before.Query.Metric));
        Assert.Equal("GPU load averaged 66% on Sat 3 Oct.", before.Lead.Replace("**", ""));

        // Against another time: both averages, and a note that it isn't a verdict on the PC.
        var two = Ask(t, "average gpu load last week vs the week before");
        Assert.Equal("GPU load averaged 66% last week, against 51% in the week of 21 Sep.", two.Lead.Replace("**", ""));
        Assert.Contains("not a change in the PC by itself", two.Note);

        // Not recorded, or too far back: said, with no numbers.
        Assert.Equal("GPU load wasn't recorded today.", Ask(t, "average gpu load today").Lead);
        Assert.StartsWith("Readings are kept by the minute", Ask(t, "average gpu load this year").Lead);
    }
}
