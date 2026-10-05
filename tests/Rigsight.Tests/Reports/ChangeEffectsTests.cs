using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;

namespace Rigsight.Tests.Reports;

/// <summary>
/// What the Timeline says was different after a big change: the same game at the same load with the room taken out,
/// and the PC's problems against its hours of use. Like for like, or nothing.
/// </summary>
public sealed class ChangeEffectsTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0);
    private static readonly DateTime Day = new(2026, 9, 28);
    private const long Game = 7;

    private static SystemChange Driver(DateTime day) =>
        new(day.AddHours(19), ChangeKind.Driver, "NVIDIA graphics driver 616.92") { Subject = "gpudriver:x", Was = "610.47", Now = "616.92" };

    private sealed class Pc
    {
        public List<HeatDay> Heat { get; } = [];
        public List<SystemDay> Use { get; } = [];
        public List<(DateTime, CrashKind)> Problems { get; } = [];

        /// <summary>A day of play: steady minutes at these temperatures, an hour at rest, and hours of use.</summary>
        public Pc Play(DateTime day, double gpu, double cpu = 60, int minutes = 40, double power = 300, double rest = 40, double hours = 3, int restMinutes = 30)
        {
            long ts = TimeUtil.ToUnix(day);
            if (minutes > 0) Heat.Add(new HeatDay(ts, Game, minutes, gpu * minutes, minutes, cpu * minutes, minutes, power * minutes, minutes));
            if (restMinutes > 0) Heat.Add(new HeatDay(ts, 0, restMinutes, rest * restMinutes, restMinutes, rest * restMinutes, restMinutes, 0, 0));
            Use.Add(new SystemDay { Day = ts, ActiveSec = hours * 3600 });
            return this;
        }

        public Pc Days(int from, int to, double gpu, double cpu = 60, int minutes = 40, double power = 300, double rest = 40, double hours = 3)
        {
            for (int i = from; i <= to; i++) Play(Day.AddDays(i), gpu, cpu, minutes, power, rest, hours);
            return this;
        }

        public Pc Problem(int day, double hour, CrashKind kind = CrashKind.GpuDriverReset)
        {
            Problems.Add((Day.AddDays(day).AddHours(hour), kind));
            return this;
        }

        public Dictionary<DateTime, ChangeEffect> Effects(params SystemChange[] changes) =>
            ChangeEffects.Of(changes.Length > 0 ? changes : [Driver(Day)], Heat, Use, Problems, id => id == Game ? "Cyberpunk 2077" : "?", Now);

        public ChangeEffect? Effect(params SystemChange[] changes) => Effects(changes).GetValueOrDefault(Day);
    }

    // ---- Temperatures ----

    [Fact]
    public void A_game_hotter_since_at_the_same_load_and_room_is_said_with_both_readings()
    {
        var effect = new Pc().Days(-10, -1, gpu: 70).Days(1, 6, gpu: 77).Effect()!;
        Assert.Equal(["In Cyberpunk 2077, the GPU runs 7° hotter since: 77° against 70° before, at the same load and not because of the room."], effect.Lines);
        Assert.Equal(EffectTone.Worse, effect.Tone);
    }

    [Fact]
    public void Cooler_is_good_news_and_the_CPU_gets_its_own_line()
    {
        var effect = new Pc().Days(-10, -1, gpu: 78, cpu: 70).Days(1, 6, gpu: 72, cpu: 64).Effect()!;
        Assert.Equal(2, effect.Lines.Count);
        Assert.StartsWith("In Cyberpunk 2077, the GPU runs 6° cooler since: 72° against 78° before", effect.Lines[0]);
        Assert.StartsWith("In Cyberpunk 2077, the CPU runs 6° cooler since: 64° against 70° before", effect.Lines[1]);
        Assert.Equal(EffectTone.Better, effect.Tone);
    }

    [Fact]
    public void A_warmer_room_isnt_the_change()
    {
        // 7° hotter in the game, and 6° hotter at rest too: the room. Not "hotter since", and not "as warm as before" either.
        Assert.Null(new Pc().Days(-10, -1, gpu: 70, rest: 40).Days(1, 6, gpu: 77, rest: 46, hours: 0).Effect());
    }

    [Theory]
    [InlineData(4.9)] // under five degrees
    [InlineData(-4.9)]
    public void A_few_degrees_either_way_is_nothing_to_say(double diff) =>
        Assert.Null(new Pc().Days(-10, -1, gpu: 70, hours: 0).Days(1, 6, gpu: 70 + diff, hours: 0).Effect());

    [Fact]
    public void The_same_game_at_another_power_isnt_the_same_load()
    {
        // New settings or a power limit: 20% less power, so cooler means nothing.
        Assert.Null(new Pc().Days(-10, -1, gpu: 78, power: 300, hours: 0).Days(1, 6, gpu: 70, power: 240, hours: 0).Effect());
        Assert.NotNull(new Pc().Days(-10, -1, gpu: 78, power: 300, hours: 0).Days(1, 6, gpu: 70, power: 260, hours: 0).Effect());
    }

    [Fact]
    public void Too_little_play_or_rest_on_either_side_says_nothing()
    {
        Assert.Null(new Pc().Days(-10, -1, gpu: 70, hours: 0).Play(Day.AddDays(1), 80, minutes: 59, hours: 0).Play(Day.AddDays(2), 80, minutes: 0, hours: 0).Effect());
        Assert.Null(new Pc().Play(Day.AddDays(-1), 70, minutes: 59, hours: 0).Days(1, 6, gpu: 80, hours: 0).Effect());
        // Enough play, but under an hour at rest before: the room can't be taken out.
        var noRest = new Pc().Play(Day.AddDays(-1), 70, minutes: 90, restMinutes: 59, hours: 0).Days(1, 6, gpu: 80, hours: 0);
        Assert.Null(noRest.Effect());
    }

    [Fact]
    public void The_changes_own_day_counts_on_neither_side()
    {
        // Hot only on the day of the change itself (half old driver, half new): not "after".
        Assert.Null(new Pc().Days(-10, -1, gpu: 70, hours: 0).Play(Day, 90, minutes: 300, hours: 0).Days(1, 6, gpu: 73, hours: 0).Effect());
    }

    // ---- Problems ----

    [Fact]
    public void Problems_that_started_after_the_change_are_set_against_none_before()
    {
        var effect = new Pc().Days(-14, 6, gpu: 70, minutes: 0).Problem(0, 20).Problem(1, 20).Problem(1, 21).Problem(2, 21.7, CrashKind.SystemCrash).Effect()!;
        Assert.Equal(["3 graphics driver resets and 1 blue screen since, none in the 14 days before."], effect.Lines);
        Assert.Equal(EffectTone.Worse, effect.Tone);
    }

    [Fact]
    public void A_problem_earlier_on_the_day_of_the_change_was_before_it()
    {
        // 10 AM, the driver went in at 7 PM.
        var effect = new Pc().Days(-14, 6, gpu: 70, minutes: 0).Problem(0, 10).Problem(-3, 10).Effect()!;
        Assert.Equal(["No problems since; 2 graphics driver resets in the 14 days before."], effect.Lines);
        Assert.Equal(EffectTone.Better, effect.Tone);
    }

    [Fact]
    public void Problems_on_both_sides_are_given_with_the_hours_of_use()
    {
        var effect = new Pc().Days(-14, 6, gpu: 70, minutes: 0).Problem(-5, 10).Problem(2, 10).Effect()!;
        Assert.Equal(["1 graphics driver reset since over 18 h of use; 1 graphics driver reset over 42 h before."], effect.Lines);
        Assert.Equal(EffectTone.Same, effect.Tone); // one against one isn't a pattern
    }

    [Fact]
    public void One_problem_after_none_is_said_plainly_without_alarm()
    {
        var effect = new Pc().Days(-14, 6, gpu: 70, minutes: 0).Problem(3, 10, CrashKind.SystemCrash).Effect()!;
        Assert.Equal(["1 blue screen since, none in the 14 days before."], effect.Lines);
        Assert.Equal(EffectTone.Same, effect.Tone);
    }

    [Fact]
    public void A_PC_barely_used_on_one_side_cant_be_compared()
    {
        Assert.Null(new Pc().Days(-14, -1, gpu: 70, minutes: 0).Play(Day.AddDays(1), 70, minutes: 0, hours: 4.9).Problem(1, 10).Effect());
        Assert.Null(new Pc().Play(Day.AddDays(-1), 70, minutes: 0, hours: 4.9).Days(1, 6, gpu: 70, minutes: 0).Problem(1, 10).Effect());
    }

    // ---- Nothing different ----

    [Fact]
    public void Nothing_different_is_worth_a_quiet_line_when_there_was_enough_to_tell()
    {
        var both = new Pc().Days(-10, -1, gpu: 71).Days(1, 6, gpu: 72).Effect()!;
        Assert.Equal(["Since then, Cyberpunk 2077 runs as warm as before, with no problems."], both.Lines);
        Assert.Equal(EffectTone.Same, both.Tone);

        var heatOnly = new Pc().Days(-10, -1, gpu: 71, hours: 0).Days(1, 6, gpu: 72, hours: 0).Effect()!;
        Assert.Equal(["Since then, Cyberpunk 2077 runs as warm as before."], heatOnly.Lines);

        var useOnly = new Pc().Days(-10, -1, gpu: 71, minutes: 0).Days(1, 6, gpu: 72, minutes: 0).Effect()!;
        Assert.Equal(["No problems since, as in the 14 days before."], useOnly.Lines);
    }

    [Fact]
    public void Nothing_recorded_says_nothing()
    {
        Assert.Empty(new Pc().Effects());
        Assert.Empty(ChangeEffects.Of([], [], [], [], _ => "", Now));
    }

    // ---- Which changes, and which days belong to them ----

    [Fact]
    public void Only_big_changes_get_a_before_and_after()
    {
        SystemChange Of(ChangeKind kind, string title) => new(Day.AddHours(10), kind, title);
        Assert.All(new[] { Driver(Day), Of(ChangeKind.Firmware, "BIOS updated to F67"), Of(ChangeKind.Hardware, "Memory changed to 32 GB"),
            Of(ChangeKind.Windows, "Windows 11 updated to 25H2"), Of(ChangeKind.WindowsUpdate, "Windows update KB5065789") }, c => Assert.True(ChangeEffects.IsMajor(c)));
        Assert.All(new[] { Of(ChangeKind.Driver, "Realtek audio driver 6.0.9"), Of(ChangeKind.AppUpdated, "Steam updated to 2.11"), Of(ChangeKind.AppInstalled, "Stremio installed"),
            Of(ChangeKind.Startup, "FACEIT now starts with Windows"), Of(ChangeKind.Setting, "Fast Startup turned off"), Of(ChangeKind.Storage, "61 GB more in use on E:") },
            c => Assert.False(ChangeEffects.IsMajor(c)));

        var pc = new Pc().Days(-10, -1, gpu: 70).Days(1, 6, gpu: 77);
        Assert.Empty(pc.Effects(Of(ChangeKind.AppUpdated, "Steam updated to 2.11"), Of(ChangeKind.Driver, "Realtek audio driver 6.0.9")));
    }

    [Fact]
    public void The_days_between_two_changes_belong_to_the_earlier_one()
    {
        // A Windows update five days before the driver, another change three days after it.
        var update = new SystemChange(Day.AddDays(-5).AddHours(3), ChangeKind.WindowsUpdate, "Windows update KB1");
        var bios = new SystemChange(Day.AddDays(3).AddHours(18), ChangeKind.Firmware, "BIOS updated to F67");
        var effects = new Pc().Days(-14, -6, gpu: 70, minutes: 0).Days(-4, -1, gpu: 70, minutes: 0).Days(1, 2, gpu: 70, minutes: 0).Days(4, 6, gpu: 70, minutes: 0)
            .Problem(-10, 9).Problem(-9, 9) // before the Windows update: not the driver's "before"
            .Problem(1, 9).Problem(2, 9)    // after the driver
            .Problem(4, 9)                  // after the BIOS: not the driver's "after"
            .Effects(update, Driver(Day), bios);

        // The driver: the four days since the update against the two until the BIOS, a closed stretch.
        Assert.Equal(["2 graphics driver resets in the 2 days after, none in the 4 days before."], effects[Day].Lines);
        Assert.Equal(EffectTone.Worse, effects[Day].Tone);
        // The update: two before it, none in the four days until the driver.
        Assert.Equal(["No problems in the 4 days after; 2 graphics driver resets in the 14 days before."], effects[update.Time.Date].Lines);
        // The BIOS, still running: one since, against the two days after the driver.
        Assert.Equal(["1 graphics driver reset since over 9 h of use; 2 graphics driver resets over 6 h before."], effects[bios.Time.Date].Lines);
    }

    [Fact]
    public void A_stretch_that_ended_reads_as_the_days_after()
    {
        var old = new DateTime(2026, 6, 1);
        var pc = new Pc();
        for (int i = -10; i <= 14; i++) if (i != 0) pc.Play(old.AddDays(i), i < 0 ? 70 : 77);
        var effect = pc.Effects(Driver(old))[old];
        Assert.Equal(["In Cyberpunk 2077, the GPU ran 7° hotter in the 14 days after: 77° against 70° before, at the same load and not because of the room."], effect.Lines);
    }

    [Fact]
    public void A_change_from_today_or_yesterday_has_no_after_yet()
    {
        var pc = new Pc().Days(-10, 7, gpu: 70);
        Assert.Empty(pc.Effects(Driver(Now.Date)));
        Assert.NotEmpty(pc.Effects(Driver(Now.Date.AddDays(-2))));
    }

    [Fact]
    public void Several_big_changes_on_one_day_are_one_before_and_after_from_the_first_of_them()
    {
        var update = new SystemChange(Day.AddHours(3), ChangeKind.WindowsUpdate, "Windows update KB1");
        // A problem at noon: after the 3 AM update, though before the 7 PM driver.
        var effects = new Pc().Days(-14, 6, gpu: 70, minutes: 0).Problem(0, 12).Problem(1, 12).Effects(update, Driver(Day));
        Assert.Equal(["2 graphics driver resets since, none in the 14 days before."], Assert.Single(effects).Value.Lines);
    }
}
