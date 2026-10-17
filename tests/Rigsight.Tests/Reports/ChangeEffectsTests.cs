using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;

namespace Rigsight.Tests.Reports;

/// <summary>
/// What a big change made better or worse: the same game at the same load with the room taken out (else the GPU at the
/// same power across games), and the problems the change can cause against the hours of use. A result only when
/// something got better or worse: a change that made no measurable difference says nothing at all.
/// </summary>
public sealed class ChangeEffectsTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0);
    private static readonly DateTime Day = new(2026, 9, 28);
    private const long Game = 7, Other = 8;

    private static SystemChange Driver(DateTime day) =>
        new(day.AddHours(19), ChangeKind.Driver, "NVIDIA graphics driver 616.92") { Subject = "gpudriver:x", Was = "610.47", Now = "616.92" };

    private sealed class Pc
    {
        public List<HeatDay> Heat { get; } = [];
        public List<SystemDay> Use { get; } = [];
        public List<ChangeProblem> Problems { get; } = [];

        /// <summary>A day of play: steady minutes at these temperatures, time at rest, and hours of use.</summary>
        public Pc Play(DateTime day, double gpu, double cpu = 60, int minutes = 40, double power = 300, double rest = 40, double hours = 3, int restMinutes = 30, long app = Game)
        {
            long ts = TimeUtil.ToUnix(day);
            if (minutes > 0) Heat.Add(new HeatDay(ts, app, minutes, gpu * minutes, minutes, cpu * minutes, minutes, power * minutes, minutes));
            if (restMinutes > 0) Heat.Add(new HeatDay(ts, 0, restMinutes, rest * restMinutes, restMinutes, rest * restMinutes, restMinutes, 0, 0));
            Use.Add(new SystemDay { Day = ts, ActiveSec = hours * 3600 });
            return this;
        }

        public Pc Days(int from, int to, double gpu, double cpu = 60, int minutes = 40, double power = 300, double rest = 40, double hours = 3, long app = Game)
        {
            for (int i = from; i <= to; i++) Play(Day.AddDays(i), gpu, cpu, minutes, power, rest, hours, app: app);
            return this;
        }

        public Pc Problem(int day, double hour, CrashKind kind = CrashKind.GpuDriverReset, bool? graphics = null)
        {
            Problems.Add(new(Day.AddDays(day).AddHours(hour), kind, graphics ?? kind == CrashKind.GpuDriverReset));
            return this;
        }

        public Dictionary<DateTime, ChangeResult> Results(params SystemChange[] changes) =>
            ChangeEffects.Of(changes.Length > 0 ? changes : [Driver(Day)], Heat, Use, Problems, id => id == Game ? "Cyberpunk 2077" : "Elden Ring", Now);

        public ChangeResult? Result(params SystemChange[] changes) => Results(changes).GetValueOrDefault(Day);
    }

    // ---- Temperatures ----

    [Fact]
    public void A_game_hotter_since_at_the_same_load_and_room_is_a_likely_worse_result()
    {
        var r = new Pc().Days(-10, -1, gpu: 70).Days(1, 6, gpu: 77).Result()!;
        Assert.Equal("Cyberpunk 2077 runs 7° hotter on the GPU since this driver", r.Headline);
        Assert.Equal(EffectTone.Worse, r.Tone);
        Assert.Equal("LIKELY THE DRIVER", r.Chip);
        Assert.Equal("GPU in Cyberpunk 2077", r.TempLabel);
        var gpu = r.Pairs[0];
        Assert.Equal(("70°", "77°", EffectTone.Worse), (gpu.Before, gpu.After, gpu.Tone));
        Assert.Equal("7° hotter in Cyberpunk 2077 since", r.Short);
        Assert.Null(r.Advice); // 77° is under the GPU's warm level: nothing to do about it
    }

    [Fact]
    public void Past_the_warm_level_it_says_what_to_try()
    {
        var r = new Pc().Days(-10, -1, gpu: 78).Days(1, 6, gpu: 85).Result()!;
        Assert.Equal("Try the next driver, or go back to 610.47 with a clean install. This page will show whether it changes.", r.Advice);
    }

    [Fact]
    public void An_hour_of_play_on_one_side_is_enough_to_see_but_only_possibly_the_change()
    {
        // 90 minutes since: past the hour that's needed, short of the two that make it likely.
        var r = new Pc().Days(-10, -1, gpu: 70).Play(Day.AddDays(2), 78, minutes: 90).Days(3, 6, gpu: 0, minutes: 0).Result()!;
        Assert.Equal("POSSIBLY THE DRIVER", r.Chip);
        Assert.Contains("only 1h 30m of play on one side", r.Why);
        Assert.Contains(r.Checks, c => !c.Ok && c.Title == "A thin side");
    }

    [Fact]
    public void Cooler_is_a_better_result()
    {
        var r = new Pc().Days(-10, -1, gpu: 78, cpu: 70).Days(1, 6, gpu: 72, cpu: 64).Result()!;
        Assert.Equal(EffectTone.Better, r.Tone);
        Assert.StartsWith("Cyberpunk 2077 runs 6° cooler on the GPU", r.Headline);
        Assert.Contains(r.Rows, x => x.Label == "CPU in Cyberpunk 2077" && x.Tone == EffectTone.Better);
    }

    [Fact]
    public void A_warmer_room_isnt_the_change()
    {
        // 7° hotter in the game, and 6° hotter at rest too: the room.
        Assert.Null(new Pc().Days(-10, -1, gpu: 70, rest: 40).Days(1, 6, gpu: 77, rest: 46, hours: 0).Result());
    }

    [Theory]
    [InlineData(4.9)] // under five degrees
    [InlineData(-4.9)]
    [InlineData(1)]
    public void A_few_degrees_either_way_is_no_result_at_all(double diff) =>
        Assert.Null(new Pc().Days(-10, -1, gpu: 70).Days(1, 6, gpu: 70 + diff).Result());

    [Fact]
    public void The_same_game_at_another_power_isnt_the_same_load()
    {
        // New settings or a power limit: 20% less power, so cooler means nothing (and no other game to compare).
        Assert.Null(new Pc().Days(-10, -1, gpu: 78, power: 300, hours: 0).Days(1, 6, gpu: 70, power: 240, hours: 0).Result());
        Assert.NotNull(new Pc().Days(-10, -1, gpu: 78, power: 300, hours: 0).Days(1, 6, gpu: 70, power: 260, hours: 0).Result());
    }

    [Fact]
    public void Too_little_play_or_rest_on_either_side_says_nothing()
    {
        Assert.Null(new Pc().Days(-10, -1, gpu: 70, hours: 0).Play(Day.AddDays(1), 80, minutes: 59, hours: 0).Play(Day.AddDays(2), 80, minutes: 0, hours: 0).Result());
        Assert.Null(new Pc().Play(Day.AddDays(-1), 70, minutes: 59, hours: 0).Days(1, 6, gpu: 80, hours: 0).Result());
        var noRest = new Pc().Play(Day.AddDays(-1), 70, minutes: 90, restMinutes: 59, hours: 0).Days(1, 6, gpu: 80, hours: 0);
        Assert.Null(noRest.Result());
    }

    [Fact]
    public void The_changes_own_day_counts_on_neither_side()
    {
        Assert.Null(new Pc().Days(-10, -1, gpu: 70, hours: 0).Play(Day, 90, minutes: 300, hours: 0).Days(1, 6, gpu: 73, hours: 0).Result());
    }

    [Fact]
    public void Different_games_at_the_same_GPU_power_are_compared_but_only_possibly()
    {
        // One game before, another after: no game on both sides, but the GPU at the same power is the same heat.
        var r = new Pc().Days(-10, -1, gpu: 70, hours: 0).Days(1, 6, gpu: 77, hours: 0, app: Other).Result()!;
        Assert.Equal("Games run 7° hotter on the GPU since this driver", r.Headline);
        Assert.Equal("POSSIBLY THE DRIVER", r.Chip);
        Assert.Contains("across games", r.Why);
        Assert.Contains(r.Checks, c => !c.Ok && c.Title == "Different games");
        // At another power it isn't the same heat to get rid of.
        Assert.Null(new Pc().Days(-10, -1, gpu: 70, hours: 0).Days(1, 6, gpu: 77, power: 380, hours: 0, app: Other).Result());
    }

    [Fact]
    public void The_power_plan_compares_the_same_game_even_at_another_power()
    {
        var plan = new SystemChange(Day.AddHours(19), ChangeKind.Setting, "Power plan changed to Power saver") { Subject = "setting:power-plan", Was = "Balanced", Now = "Power saver" };
        Assert.True(ChangeEffects.IsMajor(plan));
        var r = new Pc().Days(-10, -1, gpu: 70, cpu: 66, power: 300).Days(1, 6, gpu: 70, cpu: 58, power: 250).Result(plan)!;
        Assert.Equal("Cyberpunk 2077 runs 8° cooler on the CPU since this power plan", r.Headline);
        Assert.Equal("LIKELY THE POWER PLAN", r.Chip);
        Assert.Contains(r.Rows, x => x.Label == "GPU power in Cyberpunk 2077" && x.Before == "300 W" && x.After == "250 W");
    }

    // ---- Problems ----

    [Fact]
    public void Problems_that_started_after_the_change_are_a_worse_result()
    {
        var r = new Pc().Days(-14, 6, gpu: 70, minutes: 0).Problem(0, 20).Problem(1, 20).Problem(1, 21).Problem(2, 21.7).Result()!;
        Assert.Equal("More graphics problems since this driver: 4 graphics driver resets, none in the 14 days before", r.Headline);
        Assert.Equal(EffectTone.Worse, r.Tone);
        Assert.Equal("LIKELY THE DRIVER", r.Chip); // four on one side
        Assert.Equal(4, r.After.Count);
        Assert.Empty(r.Before);
        Assert.Equal("Try the next driver, or go back to 610.47 with a clean install. This page will show whether it changes.", r.Advice);
    }

    [Fact]
    public void A_few_problems_are_only_possibly_the_change()
    {
        var r = new Pc().Days(-14, 6, gpu: 70, minutes: 0).Problem(1, 20).Problem(2, 20).Result()!;
        Assert.Equal("POSSIBLY THE DRIVER", r.Chip);
        Assert.Null(r.Advice);
        Assert.Contains("a small count", r.Why);
    }

    [Fact]
    public void A_graphics_driver_isnt_set_against_shut_offs_it_cant_cause()
    {
        // The user's own PC on 7 Oct: four sudden shut-offs before the driver, one after; none from the graphics card.
        var pc = new Pc().Days(-14, 6, gpu: 70, minutes: 0)
            .Problem(-10, 10, CrashKind.UnexpectedShutdown).Problem(-7, 10, CrashKind.UnexpectedShutdown).Problem(-7, 12, CrashKind.UnexpectedShutdown)
            .Problem(-5, 10, CrashKind.UnexpectedShutdown).Problem(2, 10, CrashKind.UnexpectedShutdown);
        Assert.Null(pc.Result());
        // A BIOS update can cause them: there it's a better result.
        var bios = new SystemChange(Day.AddHours(19), ChangeKind.Firmware, "BIOS updated to F67") { Subject = "logged-bios:bios", Was = "F66d", Now = "F67" };
        Assert.Equal(EffectTone.Better, pc.Result(bios)!.Tone);
        // A shut-off after the GPU hung is the graphics card's.
        var hung = new Pc().Days(-14, 6, gpu: 70, minutes: 0).Problem(1, 10, CrashKind.UnexpectedShutdown, graphics: true).Problem(3, 10, CrashKind.UnexpectedShutdown, graphics: true);
        Assert.Equal(EffectTone.Worse, hung.Result()!.Tone);
    }

    [Fact]
    public void A_problem_earlier_on_the_day_of_the_change_was_before_it()
    {
        // 10 AM, the driver went in at 7 PM.
        var r = new Pc().Days(-14, 6, gpu: 70, minutes: 0).Problem(0, 10).Problem(-3, 10).Result()!;
        Assert.Equal(EffectTone.Better, r.Tone);
        Assert.Equal(2, r.Before.Count);
        Assert.StartsWith("Fewer graphics problems since this driver: none, against 2 graphics driver resets in the 14 days before", r.Headline);
    }

    [Fact]
    public void One_problem_against_one_or_none_is_no_result()
    {
        Assert.Null(new Pc().Days(-14, 6, gpu: 70, minutes: 0).Problem(-5, 10).Problem(2, 10).Result());
        Assert.Null(new Pc().Days(-14, 6, gpu: 70, minutes: 0).Problem(3, 10, CrashKind.SystemCrash, graphics: true).Result());
    }

    [Fact]
    public void A_PC_barely_used_on_one_side_cant_be_compared()
    {
        Assert.Null(new Pc().Days(-14, -1, gpu: 70, minutes: 0).Play(Day.AddDays(1), 70, minutes: 0, hours: 4.9).Problem(1, 10).Problem(1, 11).Result());
        Assert.Null(new Pc().Play(Day.AddDays(-1), 70, minutes: 0, hours: 4.9).Days(1, 6, gpu: 70, minutes: 0).Problem(-1, 10).Problem(-1, 11).Result());
    }

    // ---- Nothing different ----

    [Fact]
    public void Nothing_different_is_no_result_at_all()
    {
        // Enough on both sides, and nothing moved: nothing is shown anywhere.
        Assert.Empty(new Pc().Days(-10, -1, gpu: 71).Days(1, 6, gpu: 72).Results());
        Assert.Empty(new Pc().Days(-10, -1, gpu: 71, minutes: 0).Days(1, 6, gpu: 72, minutes: 0).Results());
        Assert.Empty(new Pc().Results());
        Assert.Empty(ChangeEffects.Of([], [], [], [], _ => "", Now));
    }

    [Fact]
    public void The_users_own_driver_617_42_is_no_result()
    {
        // Their PC: Crimson Desert 68.2° over 735 minutes before, 70.1° over 63 after, at 295 W and 291 W; rest 48° both sides.
        var pc = new Pc();
        foreach (var (d, m, g) in new[] { (-9, 136, 67.4), (-8, 135, 67.5), (-7, 246, 68.7), (-6, 72, 67.5), (-2, 146, 69.2) })
            pc.Play(Day.AddDays(d), g, cpu: 57, minutes: m, power: 295, rest: 48, hours: 9, restMinutes: 300);
        for (int d = 1; d <= 9; d++) pc.Play(Day.AddDays(d), 70.1, cpu: 61.6, minutes: d == 7 ? 63 : 0, power: 291, rest: 48, hours: 9, restMinutes: 300);
        Assert.Empty(pc.Results());
    }

    // ---- Which changes, and which days belong to them ----

    [Fact]
    public void Only_big_changes_get_a_before_and_after()
    {
        SystemChange Of(ChangeKind kind, string title) => new(Day.AddHours(10), kind, title);
        Assert.All(new[] { Driver(Day), Of(ChangeKind.Firmware, "BIOS updated to F67"), Of(ChangeKind.Hardware, "Memory changed to 32 GB"),
            Of(ChangeKind.Windows, "Windows 11 updated to 25H2"), Of(ChangeKind.WindowsUpdate, "Windows update KB5065789") }, c => Assert.True(ChangeEffects.IsMajor(c)));
        Assert.All(new[] { Of(ChangeKind.Driver, "Realtek audio driver 6.0.9"), Of(ChangeKind.AppUpdated, "Steam updated to 2.11"), Of(ChangeKind.AppInstalled, "Stremio installed"),
            Of(ChangeKind.Startup, "FACEIT now starts with Windows"), Of(ChangeKind.Setting, "Fast Startup turned off") with { Subject = "setting:fast-startup" },
            Of(ChangeKind.Storage, "61 GB added to the E drive") }, c => Assert.False(ChangeEffects.IsMajor(c)));

        var pc = new Pc().Days(-10, -1, gpu: 70).Days(1, 6, gpu: 77);
        Assert.Empty(pc.Results(Of(ChangeKind.AppUpdated, "Steam updated to 2.11"), Of(ChangeKind.Driver, "Realtek audio driver 6.0.9")));
    }

    [Fact]
    public void The_days_between_two_changes_belong_to_the_earlier_one()
    {
        // A Windows update five days before the driver, a BIOS update three days after it.
        var update = new SystemChange(Day.AddDays(-5).AddHours(3), ChangeKind.WindowsUpdate, "Windows update KB1");
        var bios = new SystemChange(Day.AddDays(3).AddHours(18), ChangeKind.Firmware, "BIOS updated to F67");
        var results = new Pc().Days(-14, -6, gpu: 70, minutes: 0, hours: 6).Days(-4, -1, gpu: 70, minutes: 0, hours: 6).Days(1, 2, gpu: 70, minutes: 0, hours: 6).Days(4, 6, gpu: 70, minutes: 0, hours: 6)
            .Problem(-10, 9).Problem(-9, 9) // before the Windows update: not the driver's "before"
            .Problem(1, 9).Problem(2, 9)    // after the driver
            .Problem(4, 9)                  // after the BIOS: not the driver's "after"
            .Results(update, Driver(Day), bios);

        var driver = results[Day];
        Assert.Equal(EffectTone.Worse, driver.Tone);
        Assert.Equal(4, driver.DaysBefore);
        Assert.Equal(2, driver.DaysAfter);
        Assert.False(driver.Open);
        Assert.StartsWith("More graphics problems in the 2 days after this driver", driver.Headline);
        // The update: two before it, none in the four days until the driver.
        Assert.Equal(EffectTone.Better, results[update.Time.Date].Tone);
    }

    [Fact]
    public void A_change_from_today_or_yesterday_has_no_after_yet()
    {
        // Hotter from 3 Oct on: a driver that day has its two whole days after by today; one from today has none.
        var pc = new Pc();
        for (int i = -10; i <= 7; i++) pc.Play(Day.AddDays(i), Day.AddDays(i) < Now.Date.AddDays(-2) ? 70 : 80);
        Assert.Empty(pc.Results(Driver(Now.Date)));
        Assert.NotEmpty(pc.Results(Driver(Now.Date.AddDays(-2))));
    }

    [Fact]
    public void Several_big_changes_on_one_day_are_one_result_and_only_possibly_either()
    {
        var update = new SystemChange(Day.AddHours(3), ChangeKind.WindowsUpdate, "Windows update KB1");
        // A problem at noon: after the 3 AM update, though before the 7 PM driver.
        var results = new Pc().Days(-14, 6, gpu: 70, minutes: 0).Problem(0, 12, CrashKind.SystemCrash).Problem(1, 12, CrashKind.SystemCrash).Problem(2, 12, CrashKind.SystemCrash)
            .Problem(3, 12, CrashKind.SystemCrash).Results(update, Driver(Day));
        var r = Assert.Single(results).Value;
        Assert.Equal("POSSIBLY ONE OF THESE", r.Chip);
        Assert.Equal(2, r.Changes.Count);
        Assert.True(r.Main.IsGraphicsDriver); // the driver names it
        Assert.Equal(4, r.After.Count);
        Assert.Contains(r.Checks, c => !c.Ok && c.Title == "Two big changes on one day");
    }

    [Fact]
    public void The_days_carry_what_the_graph_draws()
    {
        var r = new Pc().Days(-10, -1, gpu: 70).Days(1, 6, gpu: 77).Problem(2, 20).Result()!;
        Assert.Equal(r.From, r.Days[0].Day);
        Assert.Equal(r.To.AddDays(-1), r.Days[^1].Day);
        Assert.Equal(77, r.Days.Single(d => d.Day == Day.AddDays(3)).Temp!.Value, 3);
        Assert.Equal(40, r.Days.Single(d => d.Day == Day.AddDays(3)).Rest!.Value, 3);
        Assert.Equal(1, r.Days.Single(d => d.Day == Day.AddDays(2)).Problems);
    }
}
