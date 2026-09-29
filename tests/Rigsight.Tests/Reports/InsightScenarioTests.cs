using System.Collections.Concurrent;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Tests.Data;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Reports;

/// <summary>
/// Insights over months of a simulated PC (see <see cref="PcSim"/>), built from its database exactly as the app builds
/// them: a healthy PC through the seasons raises no alarm on any day, week or month, and each fault (dust, a stopped
/// fan, a card slowing itself) is found on the days it happens and not before. The owner's real case is in here: a GPU
/// whose fans stop below 60° sits warm at idle with 0 rpm, and that is not a gaming evening's 1,800 rpm to compare with.
/// </summary>
[Collection("UI")]
public sealed class InsightScenarioTests : IDisposable
{
    private readonly IDisposable _culture = Make.Culture();

    public InsightScenarioTests() => Units.Fahrenheit = false;

    public void Dispose() => _culture.Dispose();

    /// <summary>The last day lived: mid-June, five months after mid-January (winter to the height of summer).</summary>
    private static readonly DateTime End = new(2025, 6, 15);
    private static readonly DateTime Start = End.AddDays(-150);

    // Each PC is lived once per test run (a few seconds each) and read by every test that asks for it.
    private static readonly ConcurrentDictionary<string, Lazy<string>> Pcs = new();

    private static string Pc(string name, PcSim.Options options, Action<PcSim, DateTime>? eachDay = null) =>
        Pcs.GetOrAdd(name, _ => new Lazy<string>(() =>
        {
            string path = Path.Combine(TestEnvironment.NewFolder("pc-" + name), "rigsight.db");
            PcSim.Live(path, Start, End.AddDays(1), options, eachDay);
            return path;
        })).Value;

    /// <summary>A healthy PC: a zero-rpm GPU, gaming most evenings, the room 2° warmer each afternoon and 5° warmer in May than its mean.</summary>
    private static readonly PcSim.Options Healthy = new() { SeasonSwing = 5 };

    private static Report Build(string path, ReportRange range, DateTime day)
    {
        using var db = RigsightDb.OpenReader(path)!;
        return ReportBuilder.Build(db, range, day, Make.Settings());
    }

    /// <summary>Every day of the last <paramref name="days"/>, every week of the last eight, and the last three months.</summary>
    private static IEnumerable<Report> Reports(string path, int days = 45)
    {
        for (int i = 0; i < days; i++) yield return Build(path, ReportRange.Day, End.AddDays(-i));
        for (int i = 0; i < 8; i++) yield return Build(path, ReportRange.Week, End.AddDays(-7 * i));
        for (int i = 0; i < 3; i++) yield return Build(path, ReportRange.Month, End.AddMonths(-i));
    }

    private static string Lines(IEnumerable<(Report R, Insight I)> found) =>
        string.Join("\n", found.Select(x => $"{x.R.Title} {x.R.From:d MMM}: [{x.I.Key}] {x.I.Text}"));

    [Fact]
    public void A_healthy_pc_raises_no_alarm_through_months_of_use_and_a_warming_season()
    {
        var alarms = Reports(Pc("healthy", Healthy))
            .SelectMany(r => r.Insights.Select(i => (R: r, I: i)))
            .Where(x => x.I.Tone is InsightTone.Warn or InsightTone.Hot)
            .ToList();
        Assert.True(alarms.Count == 0, "A healthy PC was warned about:\n" + Lines(alarms));
    }

    [Fact]
    public void A_zero_rpm_gpu_warm_at_idle_is_never_said_to_have_a_fan_problem()
    {
        // The owner's case: the week before is full of minutes with the GPU at 60° or so and its fans at 0 (stopped on
        // purpose, or after a game while it cools); a gaming day has the same temperatures at 1,800 rpm.
        var fanLines = Reports(Pc("healthy", Healthy)).SelectMany(r => r.Insights.Where(i => i.Key.StartsWith("fan", StringComparison.Ordinal)).Select(i => (R: r, I: i))).ToList();
        Assert.True(fanLines.Count == 0, Lines(fanLines));
    }

    [Fact]
    public void A_gpu_fan_that_stops_is_caught_the_day_it_stops_and_not_before()
    {
        string path = Pc("gpu-fan", Healthy with { Seed = 2, GameChance = 1, Game = 0, GpuFanDeadFrom = End.AddDays(-1).AddHours(12) });
        var day = Build(path, ReportRange.Day, End.AddDays(-1));
        var line = Assert.Single(day.Insights, i => i.Key == "fan-stopped");
        Assert.StartsWith("Your GPU fan stopped for ", line.Text);
        Assert.EndsWith("when it always turns at that heat. Check it isn't blocked or unplugged.", line.Text);
        Assert.Equal(InsightTone.Hot, line.Tone);
        for (int back = 2; back < 30; back++)
            Assert.DoesNotContain(Build(path, ReportRange.Day, End.AddDays(-back)).Insights, i => i.Key == "fan-stopped");
    }

    [Fact]
    public void A_case_fan_that_stops_is_caught_by_its_name()
    {
        string path = Pc("case-fan", Healthy with { Seed = 3, OnChance = 1, CaseFanDeadFrom = End.AddDays(-1).AddHours(14) });
        var line = Assert.Single(Build(path, ReportRange.Day, End.AddDays(-1)).Insights, i => i.Key == "fan-stopped");
        Assert.StartsWith("Fan #2 on your motherboard stopped for ", line.Text);
        Assert.DoesNotContain(Build(path, ReportRange.Day, End.AddDays(-2)).Insights, i => i.Key == "fan-stopped");
        // The empty header (Fan #4, always 0) never is.
        Assert.DoesNotContain(Reports(path, days: 20).SelectMany(r => r.Insights), i => i.Text.StartsWith("Fan #4", StringComparison.Ordinal));
    }

    [Fact]
    public void Dust_building_up_over_months_is_found_in_the_same_game()
    {
        // The coolers lose 3° a month from mid-January: by June the same game runs about 7° hotter than in March and April.
        string path = Pc("dust", Healthy with { Seed = 4, DustPerMonth = 3, DustFrom = Start, GameChance = 1 });
        var drift = Reports(path, days: 14).SelectMany(r => r.Insights.Where(i => i.Key == "drift").Select(i => (R: r, I: i))).ToList();
        Assert.NotEmpty(drift);
        Assert.All(drift, x => Assert.Matches(@"^In (Cyberpunk 2077|Dota 2|Hades), your (GPU|CPU) (runs|ran) \d+° hotter than in (January|February|March|April|May)( 2025)? \(\d+° vs \d+°\), and not because of the room\. Dust or old thermal paste are the usual causes\.$", x.I.Text));
        Assert.All(drift, x => Assert.Equal(InsightTone.Warn, x.I.Tone));
    }

    [Fact]
    public void Summer_alone_is_not_drift()
    {
        // The same PC, clean, through a hot season: everything is warmer in June than in March, at rest as much as in play.
        string path = Pc("summer", Healthy with { Seed = 5, SeasonSwing = 8, GameChance = 1 });
        var drift = Reports(path, days: 30).SelectMany(r => r.Insights.Where(i => i.Key == "drift").Select(i => (R: r, I: i))).ToList();
        Assert.True(drift.Count == 0, "The season was taken for the PC:\n" + Lines(drift));
    }

    [Fact]
    public void A_gpu_slowing_itself_is_found_only_on_the_days_it_does()
    {
        // A hot room: the card sits at 83–86° in a heavy game either way. From the last day it also slows itself there.
        string path = Pc("throttle", Healthy with { Seed = 6, Room = 36, SeasonSwing = 0, GameChance = 1, Game = 0, ThrottleFrom = End.AddDays(-1) });
        var line = Assert.Single(Build(path, ReportRange.Day, End.AddDays(-1)).Insights, i => i.Key == "throttle");
        Assert.Matches(@"^Your GPU slowed itself for (\d+ minutes|\d+h \d\dm) to stay cool: clocks fell about 26% once it reached \d+°\.$", line.Text);
        for (int back = 2; back < 20; back++)
            Assert.DoesNotContain(Build(path, ReportRange.Day, End.AddDays(-back)).Insights, i => i.Key == "throttle");
    }

    [Fact]
    public void Records_never_claim_more_history_than_there_is()
    {
        // 150 days of history: "in a month" and "in three months" can be earned, "in a year" can't.
        var claims = Reports(Pc("healthy", Healthy), days: 60).SelectMany(r => r.Insights).Where(i => i.Text.Contains("in a year", StringComparison.Ordinal)).ToList();
        Assert.Empty(claims);
    }

    /// <summary>
    /// Three evenings: an ordinary one; a game from 11 PM to 1:30 AM; a quiet night, then up at 4:30 AM. Around them,
    /// ordinary days.
    /// </summary>
    private static string Nights() => Pc("nights", Healthy with { Seed = 7 }, (sim, day) =>
    {
        var late = End.AddDays(-3);
        if (day == late)
        {
            var t = sim.Desk("chrome.exe", day.AddHours(18), TimeSpan.FromHours(5), day.AddDays(1));
            sim.Play(PcSim.Games[1], t, TimeSpan.FromHours(2.5), day.AddDays(2));
        }
        else if (day == late.AddDays(1))
            sim.Desk("code.exe", day.AddHours(10), TimeSpan.FromHours(8), day.AddDays(1));
        else if (day == late.AddDays(2))
            sim.Desk("code.exe", day.AddHours(4.5), TimeSpan.FromHours(6), day.AddDays(1));
        else
            sim.Day(day, day.AddDays(1));
    });

    [Fact]
    public void A_game_past_midnight_belongs_to_both_days_by_the_hours_in_each()
    {
        var after = Build(Nights(), ReportRange.Day, End.AddDays(-2));
        Assert.Equal("The night before ran late: you were on until 1:30 AM. Your day ran from 10:00 AM to 6:00 PM.", Assert.Single(after.Insights, i => i.Key == "span").Text);
        // Its gaming is the hour and a half after midnight, all one session: no "longest session" longer than it.
        Assert.StartsWith("You gamed for 1h 30m (Dota 2).", Assert.Single(after.Insights, i => i.Key == "gaming").Text);
        Assert.DoesNotContain("Longest session", Assert.Single(after.Insights, i => i.Key == "gaming").Text);
        var evening = Build(Nights(), ReportRange.Day, End.AddDays(-3));
        Assert.Equal("Your day ran from 6:00 PM until past midnight.", Assert.Single(evening.Insights, i => i.Key == "span").Text);
    }

    [Fact]
    public void Up_early_after_a_quiet_night_is_the_day_starting_not_the_night_before()
    {
        var early = Build(Nights(), ReportRange.Day, End.AddDays(-1));
        Assert.Equal("Your day ran from 4:30 AM to 10:30 AM.", Assert.Single(early.Insights, i => i.Key == "span").Text);
    }

    [Fact]
    public void Every_line_reads_as_a_finished_sentence_with_no_empty_values()
    {
        foreach (var path in new[] { Pc("healthy", Healthy), Pc("dust", Healthy with { Seed = 4, DustPerMonth = 3, DustFrom = Start, GameChance = 1 }) })
            foreach (var i in Reports(path, days: 20).SelectMany(r => r.Insights))
            {
                Assert.Matches(@"^[A-Z0-9].*[.]$", i.Text);
                Assert.DoesNotContain("  ", i.Text);
                Assert.DoesNotContain("–°", i.Text); // a missing temperature
                Assert.DoesNotContain(" 0 minutes", i.Text);
                Assert.DoesNotContain("NaN", i.Text);
                Assert.DoesNotContain("12:00 AM", i.Text); // midnight is said as such
            }
    }
}
