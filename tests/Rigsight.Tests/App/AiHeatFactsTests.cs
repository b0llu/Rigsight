using Rigsight.Core;
using Rigsight.Core.Ai;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.App;

/// <summary>
/// The CPU heat line in get_moment and get_temperatures: how many minutes were hot, the longest unbroken stretch, how
/// busy the CPU was then, the averages over each minute, power and clock, and that we don't record the CPU slowing to
/// cool. From audit Q035 (2026-11-17), where two hot minutes at 5% busy were read either as a faulty cooler or as nothing.
/// </summary>
public sealed class AiHeatFactsTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"rigsight-heat-{Guid.NewGuid():N}.db");
    private static readonly DateTime Start = new(2026, 11, 10, 16, 4, 0);

    public void Dispose()
    {
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" })
            try { File.Delete(f); } catch (IOException) { }
    }

    private AiTools Tools() => new(new RigsightSettings(), _db);

    private static long At(int minute) => TimeUtil.ToUnix(Start) + minute * 60;

    [Fact]
    public void Two_hot_minutes_apart_at_low_load_are_said_as_two_single_minutes_with_their_averages()
    {
        // Q035: 4:04 to 4:14 PM at 5 to 7% busy, 64 to 76 °C but for 82 °C at 4:05 and 85 °C at 4:14.
        double[] highs = [70, 82, 64, 68, 72, 76, 66, 70, 74, 69, 85];
        using (var w = RigsightDb.OpenWriter(_db))
            for (int i = 0; i < highs.Length; i++)
            {
                bool hot = highs[i] >= 80;
                w.WriteMinute(new SystemMinute
                {
                    Ts = At(i), CpuTempMax = highs[i], CpuTemp = hot ? highs[i] - 14 : highs[i] - 4, CpuLoad = 5 + i % 3,
                    CpuPower = hot ? 38 + i % 4 : 12 + i % 9, CpuClock = hot ? 4800 : 4200, GpuTempMax = 45, GpuLoad = 3, ActiveSec = 60,
                });
            }

        string said = Tools().Run("get_moment", """{"time":"2026-11-10 16:09"}""");

        Assert.Contains("CPU heat in these minutes (the CPU figure in each row is the minute's highest reading): 80 °C or more in 2 of 11 minutes, up to 85 °C (4:05 PM, 4:14 PM; longest unbroken stretch 1 minute), all while under 25% busy (6%); the other minutes were 64 to 76 °C.", said);
        Assert.Contains("Averaged over each whole minute, those minutes were 68 to 71 °C (an average well under the minute's highest means it was that hot for only part of the minute).", said);
        Assert.Contains("CPU power in those minutes: 39 to 40 W (the other minutes 12 to 20 W).", said);
        Assert.Contains("CPU clock in those minutes (the minute's average): 4.8 GHz (the other minutes 4.2 GHz).", said);
        Assert.Contains("We don't record whether the CPU slowed itself to stay cool", said);
    }

    [Fact]
    public void Heat_held_for_eight_minutes_at_low_load_is_one_unbroken_stretch()
    {
        using (var w = RigsightDb.OpenWriter(_db))
            for (int i = 0; i < 11; i++)
            {
                bool hot = i is >= 2 and < 10;
                w.WriteMinute(new SystemMinute { Ts = At(i), CpuTempMax = hot ? 85 : 62, CpuTemp = hot ? 84 : 58, CpuLoad = 8, GpuTempMax = 45, ActiveSec = 60 });
            }

        string said = Tools().Run("get_moment", """{"time":"2026-11-10 16:09"}""");

        Assert.Contains("80 °C or more in 8 of 11 minutes, up to 85 °C (4:06 to 4:13 PM; longest unbroken stretch 8 minutes), all while under 25% busy (8%); the other minutes were 62 °C.", said);
        Assert.Contains("Averaged over each whole minute, those minutes were 84 °C.", said);
        Assert.DoesNotContain("only part of the minute", said);
        Assert.Contains("We have no CPU power reading in these minutes (this PC's CPU may not give one).", said);
        Assert.DoesNotContain("CPU clock", said);
    }

    [Fact]
    public void Minutes_under_80_have_no_heat_line()
    {
        using (var w = RigsightDb.OpenWriter(_db))
            for (int i = 0; i < 5; i++)
                w.WriteMinute(new SystemMinute { Ts = At(i), CpuTempMax = 79, CpuTemp = 70, CpuLoad = 40, ActiveSec = 60 });

        string said = Tools().Run("get_moment", """{"time":"2026-11-10 16:06"}""");

        Assert.Contains("Each recorded minute:", said);
        Assert.DoesNotContain("CPU heat in these minutes", said);
        Assert.DoesNotContain("slowed itself", said);
    }

    [Fact]
    public void A_heavy_minute_is_said_as_busy_not_quiet()
    {
        using (var w = RigsightDb.OpenWriter(_db))
        {
            w.WriteMinute(new SystemMinute { Ts = At(0), CpuTempMax = 70, CpuTemp = 66, CpuLoad = 30, CpuPower = 40, ActiveSec = 60 });
            w.WriteMinute(new SystemMinute { Ts = At(1), CpuTempMax = 88, CpuTemp = 86, CpuLoad = 95, CpuPower = 120, ActiveSec = 60 });
        }

        string said = Tools().Run("get_moment", """{"time":"2026-11-10 16:05"}""");

        Assert.Contains("80 °C or more in 1 of 2 minutes, up to 88 °C (4:05 PM; longest unbroken stretch 1 minute), while 25% busy or more (95%); the other minutes were 70 °C.", said);
        Assert.Contains("Its average over the whole minute was 86 °C.", said);
        Assert.Contains("CPU power in those minutes: 120 W (the other minutes 40 W).", said);
    }

    [Fact]
    public void A_days_temperatures_count_its_hot_minutes_and_their_longest_stretch()
    {
        using (var w = RigsightDb.OpenWriter(_db))
        {
            for (int i = 0; i < 20; i++)
            {
                bool hot = i is 1 or (>= 10 and < 16);
                w.WriteMinute(new SystemMinute { Ts = At(i), CpuTempMax = hot ? 86 : 60, CpuTemp = hot ? 83 : 55, CpuLoad = i < 5 ? 6 : 70, CpuPower = hot ? 90 : 20, GpuTempMax = 50, ActiveSec = 60 });
            }
        }

        string said = Tools().Run("get_temperatures", """{"from":"2026-11-10","to":"2026-11-10"}""");

        Assert.Contains("CPU at 80 °C or more (a minute's highest reading): 7 minutes in this period, up to 86 °C, 1 of them while under 25% busy (6 to 70%); in 2 separate stretches, the longest 6 minutes (Tue 10 Nov 4:14 to 4:19 PM).", said);
        Assert.Contains("Averaged over each whole minute, those minutes were 83 °C.", said);
        Assert.Contains("CPU power in those minutes: 90 W.", said);
        Assert.Contains("We don't record whether the CPU slowed itself to stay cool", said);
    }
}
