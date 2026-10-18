using Rigsight.Core.Data;
using Rigsight.Core.Stability;

namespace Rigsight.Tests.Core;

/// <summary>
/// Fan speeds set in the BIOS, found from the minutes already recorded: a set speed that comes back from a restart at
/// another set speed, or a fixed fan put on a curve. A fan on a curve moving with the temperature is never one, nor a
/// fan slowing while the PC is on, nor a graphics card's fans.
/// </summary>
public sealed class FanSettingsTests
{
    private static readonly DateTime Day = new(2026, 10, 1);
    private static readonly FanRow Case = new(2, "/lpc/it8686e/0/fan/1", "Fan #2", "ITE IT8686E"), Pump = new(5, "/lpc/it8686e/0/fan/4", "Fan #5", "ITE IT8686E"),
        Gpu = new(9, "/gpu-nvidia/0/fan/1", "GPU Fan 1", "NVIDIA GeForce RTX 3080 Ti");

    /// <summary>A run of minutes from <paramref name="from"/>: the fan at <paramref name="rpm"/>, or what the function gives for each minute.</summary>
    private static IEnumerable<FanMinute> Run(FanRow fan, DateTime from, int minutes, Func<int, int> rpm) =>
        Enumerable.Range(0, minutes).Select(i => new FanMinute(TimeUtil.ToUnix(from.AddMinutes(i)), fan.Id, rpm(i), rpm(i)));

    private static int Steady(int rpm, int i) => rpm + (i * 7 % 11) - 5;

    [Fact]
    public void A_set_speed_back_from_a_restart_at_another_set_speed_is_one_change_dated_when_the_pc_was_on()
    {
        var minutes = Run(Case, Day.AddHours(9), 600, i => Steady(1600, i))
            .Concat(Run(Case, Day.AddDays(1).AddHours(9), 600, i => Steady(1600, i)))
            .Concat(Run(Case, Day.AddDays(2).AddHours(10), 600, i => i < 3 ? 2000 : Steady(1050, i))).ToList();
        var c = Assert.Single(FanSettings.Find([Case], minutes));
        Assert.Equal(Day.AddDays(2).AddHours(10), c.Time);
        Assert.Equal(("Fan speeds changed while the PC was off", "Fan #2 1,600 → 1,050 rpm · likely a BIOS setting", "fans|2|"), (c.Title, c.Now, c.Subject));
    }

    [Fact]
    public void A_fixed_fan_put_on_a_curve_and_another_slowed_at_the_same_restart_are_one_change()
    {
        int Curve(int i) => 1700 + (int)(300 * Math.Sin(i / 20.0));
        var minutes = new[] { Case, Pump }.SelectMany(f => Run(f, Day.AddHours(9), 600, i => Steady(f == Pump ? 2280 : 1600, i))
                .Concat(Run(f, Day.AddDays(1).AddHours(9), 600, i => Steady(f == Pump ? 2280 : 1600, i)))
                .Concat(Run(f, Day.AddDays(2).AddHours(10).AddMinutes(f == Pump ? 1 : 0), 600, i => f == Pump ? Curve(i) : Steady(1050, i)))).ToList();
        var c = Assert.Single(FanSettings.Find([Case, Pump], minutes));
        Assert.Equal("Fan #2 1,600 → 1,050 rpm · Fan #5 from a set 2,280 rpm to following the temperature · likely a BIOS setting", c.Now);
        Assert.Equal("fans|2|5|", c.Subject);
    }

    [Fact]
    public void A_curve_fan_starting_cold_a_short_run_in_the_bios_and_a_gpu_fan_are_not_settings()
    {
        // A fan on a curve: low after a cold start, high late in a warm evening.
        int Warm(int i) => 1300 + Math.Min(500, i * 2) + (int)(80 * Math.Sin(i / 7.0));
        var curve = Enumerable.Range(0, 4).SelectMany(d => Run(Pump, Day.AddDays(d).AddHours(9), 600, Warm)).ToList();
        Assert.Empty(FanSettings.Find([Pump], curve));

        // Two minutes in the BIOS between two runs at the same set speed: no change.
        var bios = Run(Case, Day.AddHours(9), 600, i => Steady(1600, i)).Concat(Run(Case, Day.AddDays(1).AddHours(9), 600, i => Steady(1600, i)))
            .Concat(Run(Case, Day.AddDays(2).AddHours(9), 2, _ => 2200)).Concat(Run(Case, Day.AddDays(2).AddHours(9).AddMinutes(5), 600, i => Steady(1600, i))).ToList();
        Assert.Empty(FanSettings.Find([Case], bios));

        // A graphics card's fans follow its own curve, set from software while the PC is on: never looked at.
        var gpu = Run(Gpu, Day.AddHours(9), 600, i => Steady(1600, i)).Concat(Run(Gpu, Day.AddDays(1).AddHours(9), 600, i => Steady(1600, i)))
            .Concat(Run(Gpu, Day.AddDays(2).AddHours(9), 600, i => Steady(900, i))).ToList();
        Assert.Empty(FanSettings.Find([Gpu], gpu));
    }

    [Fact]
    public void A_fan_slowing_while_the_pc_is_on_is_the_fan_warnings_to_say_not_a_setting()
    {
        var minutes = Run(Case, Day.AddHours(9), 600, i => Steady(1600, i))
            .Concat(Run(Case, Day.AddDays(1).AddHours(9), 600, i => i < 300 ? Steady(1600, i) : Steady(1100, i)))
            .Concat(Run(Case, Day.AddDays(2).AddHours(9), 600, i => Steady(1100, i))).ToList();
        Assert.Empty(FanSettings.Find([Case], minutes));
    }
}
