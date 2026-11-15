using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Tests.Data;

namespace Rigsight.Tests.Reports;

/// <summary>
/// The Fans card's loudness (approved mock-up 2026-11-14): each fan against its own range, a minute taking its busiest
/// fan, set speeds left out; the starts, where they bunched, the longest silent stretch; and the other PCs the mock-up
/// showed (a laptop with one fan that stops, GPU fans that never stop, eight fans).
/// </summary>
public class FanLevelsTests
{
    // A local 9 AM, so hours and days fall where a person would see them.
    private static readonly long T0 = TimeUtil.ToUnix(new DateTime(2026, 11, 14, 9, 0, 0));

    private const long Gpu1 = 1, Gpu2 = 2, Cpu = 3, Case = 4, Laptop = 5;

    private static readonly LevelRow GpuRow = new("GPU fans", [Gpu1, Gpu2], true), CpuRow = new("CPU fan", [Cpu], false), CaseRow = new("Fan #2", [Case], false);

    // GPU fans 1,000 to 2,000 rpm and stopping; a CPU fan 800 to 1,600; a case fan at a set speed.
    private static readonly LevelRange GpuRange = new(1000, 2000, true), CpuRange = new(800, 1600, false), CaseRange = new(640, 660, false);

    /// <summary>Minute <paramref name="i"/>'s speeds for the desktop: GPU fans (both), the CPU fan, the case fan.</summary>
    private static IEnumerable<FanMinute> Desk(int i, int gpu, int cpu, int fan = 650)
    {
        long ts = T0 + i * 60;
        yield return new FanMinute(ts, Gpu1, gpu, gpu);
        yield return new FanMinute(ts, Gpu2, gpu, gpu);
        yield return new FanMinute(ts, Cpu, cpu, cpu);
        yield return new FanMinute(ts, Case, fan, fan);
    }

    private static LevelRun Desktop(params (int Gpu, int Cpu)[] minutes) =>
        FanLevels.Of([GpuRow, CpuRow, CaseRow], [GpuRange, CpuRange, CaseRange], minutes.SelectMany((m, i) => Desk(i, m.Gpu, m.Cpu)));

    [Fact]
    public void Each_minute_takes_its_busiest_fan_against_its_own_range()
    {
        var run = Desktop((0, 850), (1100, 850), (1500, 850), (1900, 850), (0, 1500), (1100, 1100));
        Assert.Equal([FanLevel.Silent, FanLevel.Quiet, FanLevel.Busy, FanLevel.Loud, FanLevel.Loud, FanLevel.Busy], run.Minutes.Select(m => m.Level));
        // Still GPU fans with a CPU fan near its fastest: loud, and its score is the CPU fan's place (a Silent minute's is 0).
        Assert.Equal((1500 - 800) / 800.0, run.Minutes[4].Score, 3);
        Assert.Equal(0, run.Minutes[0].Score);
        Assert.Equal(-1, run.Minutes[0].Places[0]);
        Assert.Null(run.Minutes[0].Places[2]); // the case fan at a set speed is left out
        Assert.Equal(FanStartKind.GpuFans, run.Kind);
        Assert.True(run.HasSilent);
    }

    [Fact]
    public void A_fan_at_a_set_speed_never_moves_the_level()
    {
        // The case fan at its own fastest (660) all the while: still Silent and Quiet, never Loud.
        var minutes = new[] { (0, 850), (1050, 850) }.SelectMany((m, i) => Desk(i, m.Item1, m.Item2, fan: 660));
        var run = FanLevels.Of([GpuRow, CpuRow, CaseRow], [GpuRange, CpuRange, CaseRange], minutes);
        Assert.Equal([FanLevel.Silent, FanLevel.Quiet], run.Minutes.Select(m => m.Level));
        Assert.True(CaseRange.IsSet);
        Assert.False(CpuRange.IsSet);
    }

    [Fact]
    public void A_range_is_its_slowest_to_its_fastest_while_spinning_and_a_stray_zero_isnt_a_fan_that_stops()
    {
        var card = FanLevels.RangeOf([new FanSpan(Gpu1, 950, 1950, 300, 500), new FanSpan(Gpu2, 1050, 2050, 300, 500)])!;
        Assert.Equal((1000, 2000, true), (card.Slowest, card.Fastest, card.CanStop));
        Assert.False(FanLevels.RangeOf([new FanSpan(Cpu, 800, 1600, 2, 500)])!.CanStop); // two zero readings: not a fan that stops
        Assert.Null(FanLevels.RangeOf([new FanSpan(Case, null, 0, 900, 0)])); // never spun
        Assert.Null(FanLevels.RangeOf([]));
    }

    [Fact]
    public void Starts_count_still_then_turning_on_minutes_that_join()
    {
        var speed = new List<(long, double)> { (T0, 0), (T0 + 60, 1200), (T0 + 120, 0), (T0 + 180, 0), (T0 + 240, 1300),
            (T0 + 600, 0), (T0 + 1200, 1100) }; // the last one comes after a gap: not a start
        Assert.Equal([T0 + 60, T0 + 240], FanLevels.Starts(speed));
        var run = Desktop((0, 850), (1200, 850), (0, 850), (0, 850), (1300, 850));
        Assert.Equal([T0 + 60, T0 + 240], run.Starts);
        Assert.Equal((T0 + 120, 2), run.Longest);
    }

    [Fact]
    public void A_laptops_one_fan_that_stops_counts_its_own_starts()
    {
        var row = new LevelRow("CPU fan", [Laptop], false);
        var minutes = new[] { 0, 0, 2400, 0, 3000 }.Select((rpm, i) => new FanMinute(T0 + i * 60, Laptop, rpm, rpm));
        var run = FanLevels.Of([row], [new LevelRange(2000, 5200, true)], minutes);
        Assert.Equal(FanStartKind.OneFan, run.Kind);
        Assert.Equal(0, run.Starter);
        Assert.Equal(2, run.Starts.Count);
        Assert.Equal((T0, 2), run.Longest);
        Assert.Equal([FanLevel.Silent, FanLevel.Silent, FanLevel.Quiet, FanLevel.Silent, FanLevel.Quiet], run.Minutes.Select(m => m.Level));
    }

    [Fact]
    public void With_no_fan_that_stops_there_is_no_silent_and_speed_ups_from_quiet_are_counted()
    {
        // GPU fans that never stop: 1,000 to 2,300, and the CPU fan.
        var gpu = new LevelRange(1000, 2300, false);
        int[] gpuRpm = [1050, 1050, 1050, 1050, 1050, 1900, 1050, 1050, 1900, 1050, 1050, 1050, 1050, 1050, 1800];
        var minutes = gpuRpm.SelectMany((rpm, i) => Desk(i, rpm, 850));
        var run = FanLevels.Of([GpuRow, CpuRow], [gpu, CpuRange], minutes.Where(m => m.Fan != Case));
        Assert.Equal(FanStartKind.SpeedUps, run.Kind);
        Assert.False(run.HasSilent);
        Assert.DoesNotContain(run.Minutes, m => m.Level == FanLevel.Silent);
        // From 5 quiet minutes to loud twice (minutes 5 and 14); minute 8 came after only 2 quiet ones.
        Assert.Equal([T0 + 5 * 60, T0 + 14 * 60], run.Starts);
        Assert.Equal((T0, 5), run.Longest); // the longest quiet stretch (the first of two as long)
    }

    [Fact]
    public void Eight_fans_dont_make_a_minute_louder_than_its_busiest_one()
    {
        // Four case fans each a little above their slowest add up to a lot of rpm, but each is still quiet.
        var rows = new List<LevelRow> { GpuRow, CpuRow };
        var ranges = new List<LevelRange?> { GpuRange, CpuRange };
        var minutes = new List<FanMinute>();
        for (int k = 0; k < 4; k++)
        {
            rows.Add(new($"Fan #{k + 2}", [10 + k], false));
            ranges.Add(new LevelRange(500, 1500, false));
        }
        for (int i = 0; i < 3; i++)
        {
            minutes.AddRange(Desk(i, 1100, 850).Where(m => m.Fan != Case));
            for (int k = 0; k < 4; k++) minutes.Add(new FanMinute(T0 + i * 60, 10 + k, 700, 700));
        }
        var run = FanLevels.Of(rows, ranges, minutes);
        Assert.All(run.Minutes, m => Assert.Equal(FanLevel.Quiet, m.Level));
    }

    [Fact]
    public void The_loudest_hour_is_by_range_and_names_the_app_in_front_while_the_fans_ran_hardest()
    {
        // 9 AM: six case fans at 1,000 rpm (the old card's sums made this hour loudest), a browser in front.
        // 10 AM: the GPU fans near their top for 10 minutes of a game, a browser in front for the other 50.
        var rows = new List<LevelRow> { GpuRow };
        var ranges = new List<LevelRange?> { GpuRange };
        for (int k = 0; k < 6; k++)
        {
            rows.Add(new($"Fan #{k + 2}", [10 + k], false));
            ranges.Add(new LevelRange(900, 1500, false));
        }
        var minutes = new List<FanMinute>();
        var front = new Dictionary<long, long?>();
        for (int i = 0; i < 120; i++)
        {
            long ts = T0 + i * 60;
            bool game = i >= 60 && i < 70;
            int gpu = i < 60 ? 0 : game ? 1950 : 1100;
            minutes.Add(new(ts, Gpu1, gpu, gpu));
            minutes.Add(new(ts, Gpu2, gpu, gpu));
            for (int k = 0; k < 6; k++) minutes.Add(new(ts, 10 + k, 1000, 1000));
            front[ts] = game ? 77 : 11;
        }
        var run = FanLevels.Of(rows, ranges, minutes);
        var loud = FanLevels.Loudest(run.Minutes, t => t.Date.AddHours(t.Hour), FanLevels.HourMinutes)!.Value;
        Assert.Equal(new DateTime(2026, 11, 14, 10, 0, 0), loud.Slot);
        Assert.Equal(77, FanLevels.AppWhileHardest(loud.Minutes, t => front[t]));
        // An hour needs 20 minutes on.
        Assert.Null(FanLevels.Loudest(run.Minutes.Take(19), t => t.Date.AddHours(t.Hour), FanLevels.HourMinutes));
    }

    [Fact]
    public void Starts_bunch_when_three_hours_hold_two_in_five_of_them()
    {
        var six = TimeUtil.ToUnix(new DateTime(2026, 11, 14, 18, 0, 0));
        var starts = Enumerable.Range(0, 52).Select(i => six + i * 180L) // 52 between 6 PM and 9 PM
            .Concat(Enumerable.Range(0, 27).Select(i => T0 + i * 600L)).Order().ToList(); // 27 through the morning
        var bunch = FanLevels.Bunch(starts)!.Value;
        Assert.Equal((18, 52), (bunch.Hour, bunch.Starts.Count));
        // Spread evenly over the day: no bunch. Fewer than 5: none either.
        Assert.Null(FanLevels.Bunch([.. Enumerable.Range(0, 24).Select(h => T0 - 9 * 3600 + h * 3600L)]));
        Assert.Null(FanLevels.Bunch([six, six + 60, six + 120, six + 180]));
    }

    [Fact]
    public void A_days_loudness_is_kept_for_the_year_once_it_is_over()
    {
        using var t = new TestDb("fan-levels");
        long gpu1 = t.Db.FanId("/gpu-nvidia/0/fan/1", "GPU Fan 1", "RTX"), gpu2 = t.Db.FanId("/gpu-nvidia/0/fan/2", "GPU Fan 2", "RTX"),
            board = t.Db.FanId("/lpc/it8686e/0/fan/0", "CPU fan", "ITE IT8686E");
        var yesterday = DateTime.Today.AddDays(-1).AddHours(9);
        void Minute(DateTime at, int gpu, int cpu)
        {
            long ts = TimeUtil.ToUnix(at);
            t.Db.WriteMinute(new SystemMinute { Ts = ts, FgApp = 5, GpuTemp = gpu == 0 ? 48 : 56 });
            t.Db.WriteFanMinutes(ts, [(gpu1, gpu, gpu), (gpu2, gpu, gpu), (board, cpu, cpu)]);
        }
        // Yesterday: an hour of the GPU fans stopping and starting (still 4 minutes, then turning 2).
        for (int i = 0; i < 60; i++) Minute(yesterday.AddMinutes(i), i % 6 < 4 ? 0 : 1000 + i * 10, 800 + i * 5);
        Assert.Equal(0, t.Count("fan_level_day")); // not over yet
        Minute(DateTime.Today.AddHours(8), 0, 800);

        var rows = t.Reader().GetFanLevelDays(TimeUtil.ToUnix(DateTime.Today.AddDays(-2)), TimeUtil.ToUnix(DateTime.Today.AddDays(1)));
        var day = Assert.Single(rows);
        Assert.Equal(TimeUtil.ToUnix(DateTime.Today.AddDays(-1)), day.Day);
        Assert.Equal(60, day.Minutes);
        Assert.Equal((int)FanStartKind.GpuFans, day.Kind);
        Assert.Equal(10, day.Starts); // 10 runs of still, each followed by the fans turning
        Assert.Equal(4, day.LongestN);
        Assert.Equal(5, day.LoudApp);
        Assert.True(day.Silent > 0 && day.Silent + day.Quiet + day.Busy + day.Loud == 60);

        // The spans the page reads its ranges from.
        var spans = t.Reader().GetFanSpans(TimeUtil.ToUnix(DateTime.Today.AddDays(-29)), TimeUtil.ToUnix(DateTime.Today.AddDays(1))).ToDictionary(s => s.Fan);
        Assert.Equal(41, spans[gpu1].StillN);
        Assert.Equal(1000 + 4 * 10, spans[gpu1].Slowest);
        Assert.Equal(0, spans[board].StillN);
    }
}
