using Rigsight.Agent;
using Rigsight.Agent.Sensors;
using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Agent;

/// <summary>
/// "Running hot" alerts: a temperature above its limit for a while (brief spikes ignored), then a quiet cooldown.
/// In the UI collection: the text uses the shared °C/°F setting, which the app's tests may change.
/// </summary>
[Collection("UI")]
public class AlertMonitorTests
{
    private static AlertSettings Settings(int sustain = 10, int cooldown = 10) =>
        new() { CpuLimit = 85, GpuLimit = 83, GpuHotSpotLimit = 100, SustainSeconds = sustain, CooldownMinutes = cooldown };

    /// <summary>Checks once a second from <paramref name="fromMs"/> for <paramref name="seconds"/>; returns the alerts raised.</summary>
    private static List<(long Ms, string Text)> Run(AlertMonitor m, KeyValues k, AlertSettings s, long fromMs, int seconds, string? app = null)
    {
        var alerts = new List<(long, string)>();
        for (int i = 0; i < seconds; i++)
        {
            long now = fromMs + i * 1000L;
            if (m.Check(k, app, s, now) is { } text) alerts.Add((now, text));
        }
        return alerts;
    }

    private static readonly KeyValues HotCpu = new() { CpuTemp = 90, GpuTemp = 60, GpuHotSpot = 70 };

    [Fact]
    public void Cool_readings_never_alert()
    {
        var m = new AlertMonitor();
        Assert.Empty(Run(m, new KeyValues { CpuTemp = 85, GpuTemp = 83, GpuHotSpot = 100 }, Settings(), 0, 600)); // at the limit isn't over it
        Assert.Empty(Run(m, new KeyValues(), Settings(), 600_000, 600));
    }

    [Fact]
    public void A_limit_passed_for_the_sustain_time_alerts_once()
    {
        var m = new AlertMonitor();
        var alerts = Run(m, HotCpu, Settings(sustain: 10), 1_000_000, 60);
        var (ms, text) = Assert.Single(alerts);
        Assert.Equal(1_010_000, ms);
        Units.Fahrenheit = false;
        Assert.Equal($"CPU {Units.TempShort(90)} has been above your limit for 10+ seconds.", m.Check(HotCpu, null, Settings(cooldown: 0), 1_100_000));
    }

    [Fact]
    public void A_brief_spike_is_ignored()
    {
        var m = new AlertMonitor();
        var s = Settings(sustain: 10);
        for (int round = 0; round < 20; round++)
        {
            Assert.Empty(Run(m, HotCpu, s, round * 20_000L, 9));
            Assert.Empty(Run(m, new KeyValues { CpuTemp = 70 }, s, round * 20_000L + 9_000, 1)); // a dip starts the count again
        }
    }

    [Fact]
    public void A_missing_reading_starts_the_count_again()
    {
        var m = new AlertMonitor();
        var s = Settings(sustain: 10);
        Assert.Empty(Run(m, HotCpu, s, 0, 9));
        Assert.Null(m.Check(new KeyValues(), null, s, 9_000));
        Assert.Empty(Run(m, HotCpu, s, 10_000, 9));
        Assert.Single(Run(m, HotCpu, s, 19_000, 2));
    }

    [Fact]
    public void After_an_alert_it_stays_quiet_for_the_cooldown()
    {
        var m = new AlertMonitor();
        var s = Settings(sustain: 10, cooldown: 10);
        var alerts = Run(m, HotCpu, s, 0, 45 * 60);
        Assert.Equal([10_000L, 610_000, 1_210_000, 1_810_000, 2_410_000], alerts.Select(a => a.Ms));
    }

    [Fact]
    public void Without_a_cooldown_every_check_alerts_while_hot()
    {
        var m = new AlertMonitor();
        Assert.Equal(51, Run(m, HotCpu, Settings(sustain: 10, cooldown: 0), 0, 61).Count);
    }

    [Fact]
    public void No_sustain_time_alerts_at_the_first_hot_reading()
    {
        var m = new AlertMonitor();
        Assert.NotNull(m.Check(HotCpu, null, Settings(sustain: 0), 5_000));
    }

    [Theory]
    [InlineData(86, 60, 70, "CPU")]
    [InlineData(60, 84, 70, "GPU")]
    [InlineData(60, 60, 101, "GPU hot spot")]
    public void Each_sensor_has_its_own_limit(double cpu, double gpu, double hot, string which)
    {
        Units.Fahrenheit = false;
        var m = new AlertMonitor();
        var k = new KeyValues { CpuTemp = cpu, GpuTemp = gpu, GpuHotSpot = hot };
        var text = Assert.Single(Run(m, k, Settings(), 0, 20)).Text;
        Assert.StartsWith($"{which} {Units.TempShort(which == "CPU" ? cpu : which == "GPU" ? gpu : hot)} has been", text);
    }

    [Fact]
    public void Limits_come_from_the_settings()
    {
        var m = new AlertMonitor();
        var s = Settings();
        s.CpuLimit = 95;
        Assert.Empty(Run(m, HotCpu, s, 0, 60));
        s.CpuLimit = 89.9;
        Assert.Single(Run(m, HotCpu, s, 60_000, 60));
    }

    [Fact]
    public void Several_hot_sensors_are_named_together_with_the_app()
    {
        Units.Fahrenheit = false;
        var m = new AlertMonitor();
        var k = new KeyValues { CpuTemp = 91.4, GpuTemp = 88.6, GpuHotSpot = 104 };
        var text = Assert.Single(Run(m, k, Settings(sustain: 5), 0, 10, app: "Cyberpunk 2077")).Text;
        Assert.Equal($"CPU {Units.TempShort(91.4)}, GPU {Units.TempShort(88.6)}, GPU hot spot {Units.TempShort(104)} have been above your limit for 5+ seconds while Cyberpunk 2077 was running.", text);
        Assert.Equal("CPU 91°, GPU 89°, GPU hot spot 104° have been above your limit for 5+ seconds while Cyberpunk 2077 was running.", text);
    }

    [Fact]
    public void Only_sensors_hot_for_long_enough_are_named()
    {
        var m = new AlertMonitor();
        var s = Settings(sustain: 10);
        Assert.Empty(Run(m, HotCpu, s, 0, 8));
        var both = new KeyValues { CpuTemp = 90, GpuTemp = 90 };
        var text = Assert.Single(Run(m, both, s, 8_000, 5)).Text;
        Assert.StartsWith("CPU ", text);
        Assert.DoesNotContain("GPU", text);
        Assert.Contains(" has been ", text);
    }

    [Fact]
    public void Temperatures_are_shown_in_the_chosen_unit()
    {
        var m = new AlertMonitor();
        try
        {
            Units.Fahrenheit = true;
            var text = Assert.Single(Run(m, HotCpu, Settings(), 0, 15)).Text;
            Assert.StartsWith("CPU 194° has been", text);
        }
        finally
        {
            Units.Fahrenheit = false;
        }
    }

    [Fact]
    public void Turned_off_it_never_alerts()
    {
        var m = new AlertMonitor();
        var s = Settings();
        s.Enabled = false;
        Assert.Empty(Run(m, new KeyValues { CpuTemp = 110, GpuTemp = 110, GpuHotSpot = 130 }, s, 0, 3600));
    }

    [Fact]
    public void Turned_back_on_a_brief_spike_is_still_ignored()
    {
        // Hot when alerts were turned off, cool for an hour, then a one-second spike right after turning them back on.
        var m = new AlertMonitor();
        var s = Settings(sustain: 10);
        Assert.Empty(Run(m, HotCpu, s, 0, 5));
        s.Enabled = false;
        Assert.Empty(Run(m, new KeyValues { CpuTemp = 50 }, s, 5_000, 3600));
        s.Enabled = true;
        Assert.Null(m.Check(HotCpu, null, s, 3_700_000));
    }

    [Fact]
    public void A_new_monitor_can_alert_straight_away()
    {
        // No cooldown from an earlier alert at the agent's start (its clock starts at 0).
        var m = new AlertMonitor();
        Assert.Single(Run(m, HotCpu, Settings(), 0, 11));
    }
}
