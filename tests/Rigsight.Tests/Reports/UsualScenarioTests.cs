using System.Collections.Concurrent;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;
using Rigsight.Tests.Data;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.Reports;

/// <summary>
/// "Normal for this PC" over months of a simulated PC (see <see cref="PcSim"/>), read as the app reads it: a healthy PC
/// is as usual on every day and week, Home's PC health stays calm and opens nothing; a real fault (a cooler gone worse
/// overnight, graphics driver resets) is said plainly on the days it shows and not before; a test tool crashing all day
/// isn't the PC's problem; a BIOS fan change is a change, not a fault. The usual is the settled weeks just before, so it
/// works on a PC set up weeks ago, not only on one with months of history.
/// </summary>
[Collection("UI")]
public sealed class UsualScenarioTests : IDisposable
{
    private readonly IDisposable _culture = Make.Culture();

    public UsualScenarioTests() => Units.Fahrenheit = false;

    public void Dispose() => _culture.Dispose();

    private static readonly DateTime End = new(2025, 6, 15);

    private static readonly ConcurrentDictionary<string, Lazy<string>> Pcs = new();

    /// <summary>A PC lived from <paramref name="days"/> days before <see cref="End"/> (two months by default) to its end.</summary>
    private static string Pc(string name, PcSim.Options options, int days = 60) =>
        Pcs.GetOrAdd(name, _ => new Lazy<string>(() =>
        {
            string path = Path.Combine(TestEnvironment.NewFolder("usual-" + name), "rigsight.db");
            PcSim.Live(path, End.AddDays(-days), End.AddDays(1), options);
            return path;
        })).Value;

    /// <summary>Healthy: gaming every evening in one game (so there's always a like-for-like), the room swinging each afternoon.</summary>
    private static readonly PcSim.Options Healthy = new() { Seed = 21, GameChance = 1, Game = 0, OnChance = 1 };

    private static Report Build(string path, ReportRange range, DateTime day)
    {
        using var db = RigsightDb.OpenReader(path)!;
        return ReportBuilder.Build(db, range, day, Make.Settings());
    }

    private static List<HomeLine> Health(Report today) =>
        HomeViewModel.HealthLines(today, [], 0, [], null, null);

    [Fact]
    public void A_healthy_pc_is_as_usual_every_day_and_week_and_home_stays_calm()
    {
        string path = Pc("healthy", Healthy);
        for (int i = 0; i < 30; i++)
        {
            var day = Build(path, ReportRange.Day, End.AddDays(-i));
            Assert.DoesNotContain(day.Insights, x => x.Key is "usual-heat" or "usual-problems" or "app-crashes");
            Assert.NotNull(day.Usual?.Heat);
            Assert.Equal(EffectTone.Same, day.Usual!.Heat!.Tone);
            var health = Health(day);
            Assert.Contains(health, l => l.Text == "Temperatures as usual");
            Assert.All(health, l => Assert.Null(l.TextBrush));
            Assert.All(health, l => Assert.False(l.CanOpen, $"{day.From:d MMM}: '{l.Text}' opens a page with nothing to look at"));
        }
        for (int i = 0; i < 4; i++)
        {
            var week = Build(path, ReportRange.Week, End.AddDays(-7 * i));
            var rows = ReportsViewModel.UsualOf(week);
            Assert.Contains(rows, r => r.Key == "heat-gpu");
            Assert.All(rows, r => Assert.Equal(EffectTone.Same, r.Tone));
        }
    }

    [Fact]
    public void A_warmer_season_is_not_the_pc_running_hotter_than_usual()
    {
        // Spring into summer: the room 8° warmer in mid-May, at rest as much as in play.
        string path = Pc("season", Healthy with { Seed = 22, SeasonSwing = 8 }, days: 90);
        for (int i = 0; i < 45; i++)
            Assert.DoesNotContain(Build(path, ReportRange.Day, End.AddDays(-i)).Insights, x => x.Key == "usual-heat" && x.Tone == InsightTone.Warn);
    }

    [Fact]
    public void A_cooler_gone_worse_overnight_is_said_from_the_day_it_shows_and_home_puts_it_first()
    {
        string path = Pc("cooler", Healthy with { Seed = 23, CoolerWorseFrom = End.AddDays(-6), CoolerWorseBy = 12 });
        // Before it: nothing.
        for (int i = 7; i < 20; i++)
            Assert.DoesNotContain(Build(path, ReportRange.Day, End.AddDays(-i)).Insights, x => x.Key == "usual-heat");

        var today = Build(path, ReportRange.Day, End);
        var line = Assert.Single(today.Insights, x => x.Key == "usual-heat");
        Assert.Equal(InsightTone.Warn, line.Tone);
        Assert.Matches(@"^In Cyberpunk 2077, your GPU (runs|ran) \d+° hotter than usual at the same power, \d+° against \d+(–\d+)?°\. With nothing running it (sits|sat) at its usual \d+°, so dust or old thermal paste are the likely causes\.$", line.Text);
        Assert.Matches(@"^Cyberpunk 2077 (runs|ran) \d+° hotter than usual$", line.Line);
        // It opens the game: its plays against its usual, on the Games page.
        Assert.Equal((InsightOpens.Game, "Cyberpunk 2077"), (line.Opens, line.Game));

        var health = Health(today);
        Assert.Equal(("WarmBrush", InsightOpens.Game, "Cyberpunk 2077"), (health[0].TextBrush, health[0].Opens, health[0].Game));
        Assert.Equal(line.Line, health[0].Text); // the same words as the list

        var row = ReportsViewModel.UsualOf(Build(path, ReportRange.Week, End)).Single(r => r.Key == "heat-gpu");
        Assert.Equal(EffectTone.Worse, row.Tone);
        Assert.EndsWith("hotter in game", row.Means);
    }

    [Fact]
    public void A_game_hot_one_day_and_fine_the_next_time_it_is_played_is_back_to_usual()
    {
        // The cooler went bad for five days, then was fixed (cleaned): played again since, it's as usual, not hot for a week.
        string path = Pc("cleaned", Healthy with { Seed = 29, CoolerWorseFrom = End.AddDays(-6), CoolerWorseBy = 12, CoolerWorseUntil = End.AddDays(-1) });
        Assert.Contains(Build(path, ReportRange.Day, End.AddDays(-2)).Insights, x => x.Key == "usual-heat");
        var today = Build(path, ReportRange.Day, End);
        Assert.DoesNotContain(today.Insights, x => x.Key == "usual-heat");
        Assert.Contains(Health(today), l => l.Text == "Temperatures as usual");
    }

    [Fact]
    public void A_pc_set_up_days_ago_says_nothing_against_a_usual_it_doesnt_have()
    {
        string path = Pc("young", Healthy with { Seed = 24, CoolerWorseFrom = End.AddDays(-2), CoolerWorseBy = 30 }, days: 6);
        var today = Build(path, ReportRange.Day, End);
        Assert.DoesNotContain(today.Insights, x => x.Key is "usual-heat" or "usual-problems");
        Assert.Null(today.Usual?.Heat);
        Assert.DoesNotContain(Health(today), l => l.Text.StartsWith("Temperatures", StringComparison.Ordinal));
    }

    [Fact]
    public void Graphics_driver_resets_that_never_happened_before_are_said_and_open_crashes()
    {
        string path = Pc("resets", Healthy with { Seed = 25, ResetsFrom = End.AddDays(-4) });
        Assert.DoesNotContain(Build(path, ReportRange.Day, End.AddDays(-6)).Insights, x => x.Key == "usual-problems");
        var today = Build(path, ReportRange.Day, End);
        var line = Assert.Single(today.Insights, x => x.Key == "usual-problems");
        // A day looked back on (the simulation's last day is in the past) says the week it ends.
        Assert.Equal("5 graphics driver resets in the 7 days to 15 Jun 2025; usually none on this PC. Each one is on the Crashes page.", line.Text);
        Assert.Equal(("5 graphics driver resets in the 7 days to 15 Jun 2025", InsightOpens.Crashes), (line.Line, line.Opens));
        // Home's PC health says it in the same words, and opens the same page.
        Assert.Contains(Health(today), l => l.Text == line.Line && l is { TextBrush: "WarmBrush", Opens: InsightOpens.Crashes });
    }

    [Fact]
    public void Fewer_problems_than_usual_is_good_news()
    {
        // A driver reset every day for weeks, then none since a fix ten days ago.
        string path = Pc("fixed", Healthy with { Seed = 26, ResetsFrom = End.AddDays(-50), ResetsUntil = End.AddDays(-10) });
        var today = Build(path, ReportRange.Day, End);
        var line = Assert.Single(today.Insights, x => x.Key == "usual-problems");
        Assert.Equal((InsightTone.Good, "Fewer PC problems than usual"), (line.Tone, line.Line));
        Assert.Contains(Health(today), l => l is { Text: "Fewer PC problems than usual", Brush: "GoodBrush" });
    }

    [Fact]
    public void Shut_offs_that_look_like_the_power_are_said_in_grey_until_they_jump_far_past_their_usual()
    {
        // A power cut every five days or so: the mains, not the PC. Home says it in grey and opens nothing.
        string calm = Pc("mains", Healthy with { Seed = 30, ShutOffEveryDays = 5 });
        var today = Build(calm, ReportRange.Day, End);
        Assert.DoesNotContain(today.Insights, x => x.Key is "usual-power" or "usual-problems");
        var health = Health(today);
        var power = Assert.Single(health, l => l.Text.Contains("power", StringComparison.Ordinal));
        Assert.Equal(("MutedBrush", false, (string?)null), (power.Brush, power.CanOpen, power.TextBrush));
        Assert.Contains(health, l => l.Text == "No PC problems in the last 7 days");

        // Then one every day for five days (a power supply giving out looks just like it): said in amber, opening Crashes.
        string failing = Pc("psu", Healthy with { Seed = 31, ShutOffEveryDays = 9, ShutOffsDailyFrom = End.AddDays(-4) });
        var line = Assert.Single(Build(failing, ReportRange.Day, End).Insights, x => x.Key == "usual-power");
        Assert.Equal((InsightTone.Warn, InsightOpens.Crashes), (line.Tone, line.Opens));
        Assert.EndsWith("Each looked like the power going off; if the mains was steady, check the power supply.", line.Text);
    }

    [Fact]
    public void A_test_tool_crashing_all_day_is_its_own_line_not_the_pcs_problem()
    {
        string path = Pc("tool", Healthy with { Seed = 27, ToolCrashesOn = End });
        var today = Build(path, ReportRange.Day, End);
        var line = Assert.Single(today.Insights, x => x.Key == "app-crashes");
        Assert.Equal("probe crashed 20 times that day; usually it never does.", line.Text);
        Assert.Equal(InsightOpens.Crashes, line.Opens);
        Assert.Equal(EffectTone.Same, today.Usual!.Problems!.Tone);
        Assert.Contains(Health(today), l => l.Text == "No PC problems in the last 7 days");
    }

    [Fact]
    public void A_case_fan_set_slower_in_the_bios_is_a_change_on_the_timeline_and_no_warning()
    {
        // Set at 5 AM, with the PC off: from that day's start the case fan holds 780 instead of 1,000 rpm.
        string path = Pc("bios", Healthy with { Seed = 28, CaseFanSlowFrom = End.AddDays(-5).AddHours(5) });
        using var db = RigsightDb.OpenReader(path)!;
        var found = FanSettings.Find(db.GetFans(), db.GetFanMinutes(0, long.MaxValue / 2));
        var change = Assert.Single(found);
        Assert.Equal(End.AddDays(-5).Date, change.Time.Date);
        Assert.Equal(("Fan speeds changed while the PC was off", ChangeKind.Fans), (change.Title, change.Kind));
        Assert.Matches(@"^Fan #2 1,0\d\d → 7\d\d rpm · likely a BIOS setting$", change.Now);
        for (int i = 0; i <= 5; i++)
            Assert.DoesNotContain(Build(path, ReportRange.Day, End.AddDays(-i)).Insights, x => x.Key.StartsWith("fan-", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_line_home_shows_is_one_statement_and_only_what_has_more_behind_it_opens()
    {
        // Facts about the day are complete in themselves: they never open anything.
        string[] facts = ["screen", "screen-compare", "gaming", "stretch", "span", "top-app", "idle-app", "memory", "away", "comfortable", "record", "streak",
            "hot-app", "background-work", "game-heat", "net-recap", "net-background", "net-game", "net-record", "net-sharing", "early"];
        var lines = new[] { Pc("healthy", Healthy), Pc("cooler", Healthy with { Seed = 23, CoolerWorseFrom = End.AddDays(-6), CoolerWorseBy = 12 }),
                Pc("resets", Healthy with { Seed = 25, ResetsFrom = End.AddDays(-4) }), Pc("tool", Healthy with { Seed = 27, ToolCrashesOn = End }) }
            .SelectMany(path => Enumerable.Range(0, 10).Select(i => Build(path, ReportRange.Day, End.AddDays(-i))).Append(Build(path, ReportRange.Week, End)))
            .SelectMany(r => r.Insights).ToList();
        Assert.NotEmpty(lines);
        foreach (var i in lines)
        {
            // One statement: no second sentence (a decimal point or "1.2 GB" isn't one), no full stop at its end.
            Assert.DoesNotMatch(@"\.\s", i.Line);
            Assert.False(i.Short is { } s && s.EndsWith('.'), $"[{i.Key}] '{i.Short}' ends with a full stop");
            Assert.True(i.Short is not null, $"[{i.Key}] has no short form: {i.Text}");
            if (facts.Contains(i.Key)) Assert.Equal(InsightOpens.None, i.Opens);
        }
    }
}
