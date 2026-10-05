using Rigsight.Core.Data;
using Rigsight.Core.Reports;

namespace Rigsight.Tests.Reports;

/// <summary>
/// What a fan's minutes show (the Fans page): what its speed goes with, how long it stood still, and its fastest moment
/// with what was working then. Shaped on a real PC's day: steady case fans, a graphics card's fans that stand still while
/// it's cool.
/// </summary>
public class FanFactsTests
{
    private const long T0 = 1_790_000_000;

    /// <summary>A day's minutes: the CPU and GPU warm and cool in turn (a game, then desktop work).</summary>
    private static Dictionary<long, SystemMinute> Day(int minutes = 300, long? gpuApp = 7, long? cpuApp = 8)
    {
        var day = new Dictionary<long, SystemMinute>();
        for (int i = 0; i < minutes; i++)
        {
            double gpuWave = Math.Sin(i / 20.0), cpuWave = Math.Cos(i / 13.0);
            day[T0 + i * 60] = new SystemMinute
            {
                Ts = T0 + i * 60, GpuTemp = 55 + gpuWave * 20, CpuTemp = 55 + cpuWave * 15, GpuApp = gpuApp, CpuApp = cpuApp,
            };
        }
        return day;
    }

    private static List<FanMinute> Fan(Dictionary<long, SystemMinute> day, Func<SystemMinute, int> rpm) =>
        [.. day.Values.OrderBy(m => m.Ts).Select(m => new FanMinute(m.Ts, 1, rpm(m), rpm(m) + 20))];

    [Fact]
    public void A_case_fan_at_a_fixed_speed_runs_steady()
    {
        // As the real board fans: 1,590-1,664 rpm all day, whatever the chips did.
        var day = Day();
        var rnd = new Random(1);
        var facts = FanAnalysis.Of(false, Fan(day, _ => 1630 + rnd.Next(-40, 35)), day);
        Assert.Equal(FanFollows.Steady, facts.Follows);
        Assert.Equal(0, facts.StoppedMinutes);
        Assert.Null(facts.Fastest!.App); // a fan that follows nothing isn't put down to an app
    }

    [Fact]
    public void A_fan_on_a_curve_follows_the_chip_it_cools()
    {
        var day = Day();
        Assert.Equal(FanFollows.Cpu, FanAnalysis.Of(false, Fan(day, m => (int)(600 + m.CpuTemp!.Value * 15)), day).Follows);
        Assert.Equal(FanFollows.Gpu, FanAnalysis.Of(false, Fan(day, m => (int)(600 + m.GpuTemp!.Value * 15)), day).Follows);
    }

    [Fact]
    public void A_fan_that_changes_speed_with_neither_chip_says_so()
    {
        var day = Day();
        var rnd = new Random(3);
        Assert.Equal(FanFollows.Varies, FanAnalysis.Of(false, Fan(day, _ => 800 + rnd.Next(0, 900)), day).Follows);
    }

    [Fact]
    public void Under_an_hour_of_spinning_is_too_little_to_say()
    {
        var day = Day(minutes: FanAnalysis.MinMinutes - 1);
        Assert.Equal(FanFollows.Unknown, FanAnalysis.Of(false, Fan(day, m => (int)(600 + m.CpuTemp!.Value * 15)), day).Follows);
    }

    [Fact]
    public void A_graphics_cards_fan_follows_its_gpu_and_counts_its_rest()
    {
        // Zero-RPM mode: still while the card is under 50°, then faster the hotter it gets.
        var day = Day();
        var fan = Fan(day, m => m.GpuTemp < 50 ? 0 : (int)(900 + (m.GpuTemp!.Value - 50) * 60));
        var facts = FanAnalysis.Of(true, [.. fan.Select(f => f.RpmAvg == 0 ? f with { RpmMax = 0 } : f)], day);
        Assert.Equal(FanFollows.Gpu, facts.Follows);
        Assert.Equal(day.Values.Count(m => m.GpuTemp < 50), facts.StoppedMinutes);
        Assert.Equal(day.Values.Count(m => m.GpuTemp >= 50), facts.SpinningMinutes);
    }

    [Fact]
    public void The_fastest_moment_names_the_app_working_the_chip_the_fan_follows()
    {
        var day = Day(gpuApp: 7, cpuApp: 8);
        var minutes = Fan(day, m => (int)(600 + m.GpuTemp!.Value * 15));
        var gpuFan = FanAnalysis.Of(true, minutes, day);
        // The first minute at the day's highest speed (the minute's highest reading, not its average).
        var fastest = minutes.First(f => f.RpmMax == minutes.Max(x => x.RpmMax));
        Assert.Equal((fastest.Ts, fastest.RpmMax, (long?)7), (gpuFan.Fastest!.Ts, gpuFan.Fastest.Rpm, gpuFan.Fastest.App));

        var cpuFan = FanAnalysis.Of(false, Fan(day, m => (int)(600 + m.CpuTemp!.Value * 15)), day);
        Assert.Equal((long?)8, cpuFan.Fastest!.App);
    }

    [Fact]
    public void A_fan_that_never_spun_has_no_fastest_moment()
    {
        var day = Day();
        var facts = FanAnalysis.Of(false, [.. Fan(day, _ => 0).Select(f => f with { RpmMax = 0 })], day);
        Assert.Null(facts.Fastest);
        Assert.Equal(day.Count, facts.StoppedMinutes);
        Assert.Equal(FanFollows.Unknown, facts.Follows);
    }

    /// <summary>A fan's days from each day's speed while idle (and its idle minutes), a day apart.</summary>
    private static List<FanDay> Days(params (int Rpm, int IdleMinutes)[] days) =>
        [.. days.Select((d, i) => new FanDay(T0 + i * 86400L, 1, d.Rpm * 600.0, 600, d.Rpm + 80, d.Rpm * (double)d.IdleMinutes, d.IdleMinutes))];

    [Fact]
    public void A_new_speed_setting_starts_the_day_the_idle_speed_steps()
    {
        // As the real board fan: about 1,640 rpm for days, then about 1,100 from a restart on (one short day among them).
        var days = Days((1659, 184), (1632, 402), (1633, 351), (1098, 112), (1097, 23), (1107, 488));
        Assert.Equal(days[3].Day, FanSetting.ChangedOn(days));
        // From a set speed to a curve that idles lower, and the other way.
        Assert.Equal(T0 + 2 * 86400L, FanSetting.ChangedOn(Days((1755, 300), (1749, 300), (1333, 112), (1368, 488))));
        Assert.Equal(T0 + 86400L, FanSetting.ChangedOn(Days((900, 300), (1400, 300), (1380, 300))));
    }

    [Fact]
    public void One_setting_is_not_split_by_ordinary_days()
    {
        Assert.Null(FanSetting.ChangedOn([]));
        Assert.Null(FanSetting.ChangedOn(Days((1640, 300))));
        // A curve's idle speed wanders a little with the room.
        Assert.Null(FanSetting.ChangedOn(Days((1010, 300), (1080, 200), (960, 400), (1100, 300), (1040, 90))));
        // One odd day among days at the same level is a day, not a setting.
        Assert.Null(FanSetting.ChangedOn(Days((1640, 300), (1650, 300), (1100, 200), (1645, 300), (1635, 300))));
        // A day with hardly any idle time says nothing either way.
        Assert.Null(FanSetting.ChangedOn(Days((1640, 300), (1650, 300), (1100, 5), (1645, 300))));
        // A new level is a setting once it has an hour of idle time behind it.
        Assert.Null(FanSetting.ChangedOn(Days((1640, 300), (1650, 300), (1100, 30))));
        Assert.NotNull(FanSetting.ChangedOn(Days((1640, 300), (1650, 300), (1100, 30), (1110, 40))));
    }

    [Fact]
    public void A_setting_changed_twice_counts_from_the_last_change()
    {
        var days = Days((1640, 300), (1650, 300), (1100, 300), (1110, 300), (1900, 300), (1890, 300));
        Assert.Equal(days[4].Day, FanSetting.ChangedOn(days));
    }
}

/// <summary>The Temperatures page's "At rest" card: today's resting temperatures against the usual range on earlier days.</summary>
public class RestTempsTests
{
    private static readonly DateTime Today = new(2026, 9, 29);

    private static HeatDay Rest(int daysAgo, double cpu, double gpu, int minutes = 60, long app = 0) =>
        new(TimeUtil.ToUnix(Today.AddDays(-daysAgo)), app, minutes, gpu * minutes, minutes, cpu * minutes, minutes, 0, 0);

    [Fact]
    public void Today_is_set_against_the_middle_of_the_days_before()
    {
        var days = new List<HeatDay> { Rest(0, 50.4, 48.1) };
        // As a real week: 46.8-49° at rest, and one hot afternoon that mustn't set "usual".
        double[] earlier = [47.9, 49.0, 48.5, 48.1, 46.8, 46.8, 60];
        for (int i = 0; i < earlier.Length; i++) days.Add(Rest(i + 1, earlier[i], 48));
        var (cpu, gpu) = RestTemps.Of(days, Today);
        Assert.Equal(50.4, cpu.Today!.Value, 6);
        Assert.Equal(60, cpu.TodayMinutes);
        Assert.Equal((46.8, 49.0), (cpu.UsualLow!.Value, cpu.UsualHigh!.Value));
        Assert.Equal((48.0, 48.0), (gpu.UsualLow!.Value, gpu.UsualHigh!.Value));
    }

    [Fact]
    public void Fewer_than_five_earlier_days_make_no_usual_range()
    {
        var days = new List<HeatDay> { Rest(0, 45, 40) };
        for (int i = 1; i < RestTemps.MinDays; i++) days.Add(Rest(i, 44, 39));
        var (cpu, _) = RestTemps.Of(days, Today);
        Assert.Equal(45, cpu.Today!.Value, 6);
        Assert.Null(cpu.UsualLow);
    }

    [Fact]
    public void A_few_idle_minutes_and_apps_heat_are_not_rest()
    {
        var days = new List<HeatDay> { Rest(0, 45, 40, minutes: RestTemps.MinMinutes - 1), Rest(0, 80, 75, app: 12) };
        for (int i = 1; i <= RestTemps.MinDays; i++) days.Add(Rest(i, 44, 39));
        var (cpu, gpu) = RestTemps.Of(days, Today);
        Assert.Null(cpu.Today);
        Assert.Null(gpu.Today);
        Assert.Equal(44, cpu.UsualLow!.Value, 6);
    }
}
