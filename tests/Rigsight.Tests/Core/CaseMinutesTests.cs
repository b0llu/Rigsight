using Rigsight.Core.Data;
using Rigsight.Core.Stability;

namespace Rigsight.Tests.Core;

/// <summary>
/// What stood out in the minutes before a crash: a part getting hot and the app doing the work then, with how much;
/// fans, memory, drives, the battery and the connection; and what was calm, said only for what was recorded.
/// </summary>
public class CaseMinutesTests
{
    private static readonly long Crash = 1_760_000_000 / 60 * 60;
    private static readonly Dictionary<long, string> Names = new() { [1] = "Rocket League", [2] = "HandBrake", [3] = "Chrome" };
    private static string NameOf(long id) => Names.GetValueOrDefault(id) ?? "An app";

    /// <summary>45 minutes, oldest first; <paramref name="cpu"/> gives each one's CPU temperature by minutes before the crash.</summary>
    private static List<SystemMinute> Minutes(Func<int, double> cpu, Action<int, SystemMinute>? also = null) =>
        [.. Enumerable.Range(0, 46).Reverse().Select(before =>
        {
            var m = new SystemMinute { Ts = Crash - before * 60, CpuTemp = cpu(before), GpuTemp = 66, RamUsed = 18, ActiveSec = 60 };
            also?.Invoke(before, m);
            return m;
        })];

    private static List<StoodOut> Find(List<SystemMinute> minutes, List<AppMinute>? apps = null, List<FanMinute>? fans = null, List<NetDrop>? drops = null,
        List<(long, long)>? opened = null, bool power = false) =>
        CaseMinutes.Find(minutes, apps ?? [], fans ?? [], new Dictionary<long, string> { [7] = "CPU fan", [8] = "Case fan" }, drops ?? [], Crash, 32, NameOf, opened, power);

    // The CPU sits at 63° and climbs to 91° over the last six minutes.
    private static double Climb(int before) => before > 5 ? 63 : 91 - before * 4;

    [Fact]
    public void A_climb_names_the_app_far_ahead_of_the_rest_with_its_share()
    {
        var apps = Enumerable.Range(0, 7).SelectMany(before => new[]
        {
            new AppMinute(Crash - before * 60, 2, 60, 0, 4800, 0),
            new AppMinute(Crash - before * 60, 1, 20, 74, 5800, 0),
        }).ToList();
        var said = Find(Minutes(Climb), apps);
        Assert.Equal("CPU went from 63° to 91° in the 5 minutes before the crash, while HandBrake was using about 60% of the CPU.", said[0].Text);
        Assert.True(said[0].Warn);
        // The GPU stayed where it was: calm, with the rest that was recorded.
        Assert.Equal("Nothing unusual in these minutes: GPU temperature, memory (56% full at most) and the connection.", said[^1].Text);
        Assert.True(said[^1].IsCalm);
    }

    [Fact]
    public void A_climb_with_the_work_shared_out_says_no_one_app_did_most_of_it()
    {
        var apps = Enumerable.Range(0, 7).SelectMany(before => new[]
        {
            new AppMinute(Crash - before * 60, 2, 30, 0, 900, 0),
            new AppMinute(Crash - before * 60, 1, 26, 0, 900, 0),
        }).ToList();
        Assert.EndsWith("before the crash. No one app was doing most of the work.", Find(Minutes(Climb), apps)[0].Text);
    }

    [Fact]
    public void A_climb_as_a_game_was_starting_before_a_power_cut_says_so_and_is_no_warning()
    {
        // The power went a few minutes after Rocket League was opened: its loading warmed the part, with the work
        // counted under its launcher, so no one app was far ahead. (REMATCH, 17 Oct, 11:16 PM.)
        var apps = Enumerable.Range(0, 7).SelectMany(before => new[]
        {
            new AppMinute(Crash - before * 60, 2, 9, 0, 900, 0),
            new AppMinute(Crash - before * 60, 1, 8, 0, 900, 0),
        }).ToList();
        long opened = Crash - 6 * 60 + 20;
        var said = Find(Minutes(Climb), apps, opened: [(1, opened)], power: true);
        Assert.Equal($"CPU went from 63° to 91° as Rocket League was starting at {TimeUtil.FromUnix(opened):h:mm tt}. That's Rocket League loading; it isn't why the PC went off.", said[0].Text);
        Assert.False(said[0].Warn);
        // Opened long before the climb: the climb is said as before.
        Assert.EndsWith("No one app was doing most of the work.", Find(Minutes(Climb), apps, opened: [(1, Crash - 30 * 60)], power: true)[0].Text);
        // Not the power: the game may be why, so the climb stays the warning it was.
        var game = Find(Minutes(Climb), apps, opened: [(1, opened)])[0];
        Assert.True(game.Warn);
        Assert.EndsWith("No one app was doing most of the work.", game.Text);
    }

    [Fact]
    public void Before_shares_were_kept_a_climb_names_the_app_each_minute_named_and_only_one_that_held_throughout()
    {
        var named = Minutes(Climb, (before, m) => m.CpuApp = 2);
        Assert.EndsWith(", while HandBrake was working it hardest.", Find(named)[0].Text);
        var mixed = Minutes(Climb, (before, m) => m.CpuApp = before % 2 == 0 ? 2 : 3);
        Assert.EndsWith("in the 5 minutes before the crash.", Find(mixed)[0].Text);
    }

    [Fact]
    public void A_small_rise_is_not_a_climb_and_nothing_is_said_of_what_was_never_recorded()
    {
        var said = Find(Minutes(before => before > 5 ? 63 : 70));
        Assert.Single(said);
        // No drives recorded (a crash from before 0.24.0): the drives aren't called calm.
        Assert.Equal("Nothing unusual in these minutes: CPU temperature, GPU temperature, memory (56% full at most) and the connection.", said[0].Text);
    }

    [Fact]
    public void Windows_nearly_out_of_memory_is_a_warning_naming_the_app_with_the_most()
    {
        var minutes = Minutes(_ => 60, (before, m) => m.CommitPct = before < 4 ? 96 : 60);
        var apps = new List<AppMinute> { new(Crash, 3, 5, 0, 9216, 0), new(Crash, 1, 20, 70, 5800, 0) };
        var memory = Assert.Single(Find(minutes, apps), s => s.Text.StartsWith("Windows was nearly out of memory"));
        Assert.Equal("Windows was nearly out of memory: 96% of what it can promise was promised to apps, the most to Chrome (9.0 GB).", memory.Text);
        Assert.True(memory.Warn);
    }

    [Fact]
    public void Memory_nearly_full_earlier_on_is_said_plainly_not_called_calm()
    {
        var minutes = Minutes(_ => 60, (before, m) => m.RamUsed = before == 30 ? 31 : 18);
        var said = Find(minutes);
        var memory = Assert.Single(said, s => s.Text.StartsWith("Memory was 97% full at "));
        Assert.False(memory.Warn);
        Assert.EndsWith(", 56% by the end.", memory.Text);
        Assert.DoesNotContain("memory", said[^1].Text);
    }

    [Fact]
    public void Fans_that_sped_up_together_are_one_line()
    {
        var fans = Enumerable.Range(0, 46).SelectMany(before => new[]
        {
            new FanMinute(Crash - before * 60, 7, before < 4 ? 1850 : 930, before < 4 ? 1900 : 950),
            new FanMinute(Crash - before * 60, 8, before < 4 ? 1300 : 800, before < 4 ? 1320 : 820),
        }).ToList();
        var fan = Assert.Single(Find(Minutes(_ => 60), fans: fans), s => s.Text.Contains("sped up"));
        Assert.StartsWith("CPU fan and Case fan sped up from ", fan.Text);
        Assert.EndsWith(", to as much as 1,900 rpm.", fan.Text);
    }

    [Fact]
    public void Busy_drives_the_battery_nearly_empty_and_a_dropped_connection_are_each_said()
    {
        const double MB = 1_048_576;
        var minutes = Minutes(_ => 60, (before, m) => { m.DiskRate = before < 5 ? 80 * MB : 2 * MB; m.OnBattery = true; m.Battery = before < 2 ? 4 : 30; });
        var apps = new List<AppMinute> { new(Crash, 2, 30, 0, 900, 70 * MB) };
        var said = Find(minutes, apps, drops: [new NetDrop(Crash - 600, Crash - 560, NetDropKind.Internet)]);
        Assert.Contains(said, s => s.Text == "The drives were busy, about 80 MB/s, mostly HandBrake.");
        Assert.Contains(said, s => s.Text == "The PC was running on its battery, at 4%." && s.Warn);
        Assert.Contains(said, s => s.Text.StartsWith("The connection dropped at ") && s.Text.EndsWith(", for 40s."));
        Assert.DoesNotContain("connection", said[^1].Text);
    }

    [Fact]
    public void A_fan_that_peaked_late_in_its_last_minute_is_said_from_that_minute()
    {
        // The 12 Oct shut-off: the fan's top jumped in the last minute while its average for it stayed low (opening it
        // failed: no minute's average reached halfway).
        var fans = Enumerable.Range(0, 46).Select(before => new FanMinute(Crash - before * 60, 7, before == 0 ? 1000 : 900, before == 0 ? 2100 : 920)).ToList();
        var fan = Assert.Single(Find(Minutes(_ => 60), fans: fans), s => s.Text.Contains("sped up"));
        Assert.StartsWith("CPU fan sped up from 900 to 2,100 rpm from ", fan.Text);
    }

    [Fact]
    public void Nothing_recorded_before_it_says_nothing()
    {
        Assert.Empty(CaseMinutes.Find([], [], [], new Dictionary<long, string>(), [], Crash, 32, NameOf));
    }
}
