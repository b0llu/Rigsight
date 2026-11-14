using Rigsight.Core.Data;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Tests.Data;

namespace Rigsight.Tests.Reports;

/// <summary>
/// The "Over the months" tile's judgement: a part's usual band from its first two weeks, moved only when the last two
/// weeks both sit outside it on one side and the move is big enough, too soon with few weeks; chips compared at the
/// same mix of power; a drive's pace of wear; and the drive's daily record written and read back.
/// </summary>
public sealed class WearTests
{
    private static readonly DateTime Monday = new(2026, 9, 28);

    private static List<WearWeek> Weeks(params double[] values) => values.Select((v, i) => new WearWeek(Monday.AddDays(7 * i), v)).ToList();

    [Fact]
    public void A_cooling_gap_that_climbs_and_stays_up_has_moved()
    {
        var row = Wear.Judge("cpu", "CPU", Weeks(13.8, 14.0, 16.6, 17.6, 16.3, 17.3, 18.1), Wear.CoolingBandMin, (now, then) => Math.Abs(now - then) >= Wear.CoolingMoved);
        Assert.Equal(WearState.Moved, row.State);
        Assert.Equal(13.9, row.Then!.Value, 1);
        Assert.Equal(17.7, row.Now!.Value, 1);
        Assert.Equal(5, row.WeeksOutside);
        Assert.Equal("late September", Wear.WhenSaid(row.ThenDate!.Value));
    }

    [Fact]
    public void Two_hot_weeks_too_small_to_matter_stay_steady()
    {
        // Outside the band, but under 3° from where it started: not worth a line.
        var row = Wear.Judge("gpu", "GPU", Weeks(19.8, 19.5, 20.3, 18.3, 20.1, 21.7, 21.6), Wear.CoolingBandMin, (now, then) => Math.Abs(now - then) >= Wear.CoolingMoved);
        Assert.Equal(WearState.Steady, row.State);
    }

    [Fact]
    public void One_hot_week_after_steady_ones_is_not_a_move()
    {
        var row = Wear.Judge("cpu", "CPU", Weeks(14, 14, 14.5, 13.8, 14.2, 19), Wear.CoolingBandMin, (now, then) => Math.Abs(now - then) >= Wear.CoolingMoved);
        Assert.Equal(WearState.Steady, row.State);
        Assert.Equal(1, row.WeeksOutside);
    }

    [Fact]
    public void Fewer_than_four_weeks_is_too_soon()
    {
        Assert.Equal(WearState.TooSoon, Wear.Judge("cpu", "CPU", Weeks(14, 15, 20), 1.5, (_, _) => true).State);
        Assert.Equal(WearState.TooSoon, Wear.Judge("cpu", "CPU", Weeks(14), 1.5, (_, _) => true).State);
    }

    [Fact]
    public void A_week_spent_near_the_top_of_the_band_does_not_read_hotter()
    {
        // Two power steps, each the same temperature both weeks; week 1 just spent most of its time in the hotter step.
        var rows = new List<(int Week, int Bin, double Temp, int Minutes)>
        {
            (0, 0, 60, 90), (0, 1, 66, 10),
            (1, 0, 60, 10), (1, 1, 66, 90),
        };
        var weeks = Wear.ByWeek(rows);
        Assert.Equal(2, weeks.Count);
        Assert.Equal(weeks[0].Temp, weeks[1].Temp, 3);
    }

    [Fact]
    public void A_chips_working_load_is_the_middle_half_of_its_loaded_minutes()
    {
        var powers = Enumerable.Repeat(20.0, 200).Concat(Enumerable.Range(0, 200).Select(i => 50.0 + i * 0.25)).ToList();
        var band = Wear.PowerBand(powers);
        Assert.NotNull(band);
        Assert.InRange(band!.Value.Low, 60, 64);
        Assert.InRange(band.Value.High, 86, 90);
        Assert.Null(Wear.PowerBand(Enumerable.Repeat(20.0, 300).ToList()));
    }

    [Fact]
    public void A_drive_says_its_pace_only_after_four_weeks()
    {
        var origin = Wear.Origin(new DateTime(2026, 11, 13));
        DriveHealthDay Day(DateTime d, double life, long sectors = 0) => new() { Day = TimeUtil.ToUnix(d), Drive = "S1", Name = "SSD", Life = life, Reallocated = sectors, Status = "Good" };
        var young = Wear.DriveOf("S1", "SSD", origin, [Day(new(2026, 11, 1), 70), Day(new(2026, 11, 13), 69.9)]);
        Assert.Equal(WearState.TooSoon, young.State);
        Assert.Null(young.LifePerYear);

        var old = Wear.DriveOf("S1", "SSD", origin, [Day(new(2026, 8, 1), 72), Day(new(2026, 11, 13), 70)]);
        Assert.Equal(WearState.Steady, old.State);
        Assert.InRange(old.LifePerYear!.Value, 6.5, 7.5);

        var sectors = Wear.DriveOf("S1", "SSD", origin, [Day(new(2026, 8, 1), 72, 4), Day(new(2026, 11, 13), 70, 12)]);
        Assert.Equal(WearState.Moved, sectors.State);
        Assert.Equal((4L, 12L), (sectors.SectorsThen!.Value, sectors.SectorsNow!.Value));
    }

    [Fact]
    public void A_drives_day_keeps_its_highest_temperature_and_latest_counters()
    {
        using var t = new TestDb();
        long day = TimeUtil.ToUnix(new DateTime(2026, 11, 13));
        DriveHealthInfo Read(double temp, double life, long written) => new()
        {
            Name = "KINGSTON SA400S37480G", Key = "50026B7", Status = "Good", Temperature = temp, Life = life, WrittenGb = written, PowerOnHours = 12358, ReallocatedSectors = 0,
        };
        t.Db.UpsertDriveHealth(day, Read(33, 69, 9800));
        t.Db.UpsertDriveHealth(day, Read(45, 69, 9810));
        t.Db.UpsertDriveHealth(day, Read(35, 69, 9812));
        t.Db.UpsertDriveHealth(day, new DriveHealthInfo { Name = "No key", Status = "Good", Temperature = 30 });

        var rows = t.Db.GetDriveHealthDays(day);
        var row = Assert.Single(rows);
        Assert.Equal("50026B7", row.Drive);
        Assert.Equal(45, row.TempMax);
        Assert.Equal(37.7, row.TempAvg!.Value, 1);
        Assert.Equal(9812, row.WrittenGb);
        Assert.Equal(12358, row.Hours);
    }
}

/// <summary>The Temperatures page's "Over the months": what each part says, its months, and the scale both share.</summary>
public sealed class CoolingCardTests
{
    private static readonly DateTime Monday = new(2026, 9, 28);

    private static WearRow Row(string part, params double[] gaps)
    {
        var weeks = gaps.Select((v, i) => new WearWeek(Monday.AddDays(7 * i), v, 45 + v, 45)).ToList();
        return Wear.Judge(part, part.ToUpperInvariant(), weeks, Wear.CoolingBandMin, (now, then) => Math.Abs(now - then) >= Wear.CoolingMoved);
    }

    [Fact]
    public void A_part_that_runs_warmer_says_by_how_much_and_since_when()
    {
        var parts = Rigsight.Models.CoolingCard.Of(Row("cpu", 13.8, 14.0, 16.6, 17.6, 16.3, 17.3, 18.1), Row("gpu", 19.8, 19.5, 20.3, 18.3, 20.1, 21.7, 20.9), byMonth: false);
        var (cpu, gpu) = (parts[0], parts[1]);
        Assert.True(cpu.IsWarmer);
        Assert.Equal("18°", cpu.BigText);
        Assert.Equal("Up 4° since late September, ", cpu.LeadText);
        Assert.Equal("for 5 weeks now", cpu.TailText);
        Assert.False(gpu.IsWarmer);
        Assert.Equal("", gpu.LeadText);
        Assert.Equal("Inside its usual band, as in late September", gpu.TailText);
        // One scale for both, wide enough for every point and band.
        Assert.Equal(cpu.ScaleLow, gpu.ScaleLow);
        Assert.Equal(cpu.ScaleSpan, gpu.ScaleSpan);
        Assert.True(cpu.ScaleLow <= 12.4 && cpu.ScaleLow + cpu.ScaleSpan >= 21.7);
        Assert.Equal($"14° · {Monday:d MMM}", cpu.FirstLabel);
        Assert.Equal(7, cpu.Tips.Count);
    }

    [Fact]
    public void By_month_each_month_is_the_average_of_its_weeks()
    {
        var months = Rigsight.Models.CoolingCard.Months(Row("cpu", 10, 12, 14, 16, 18, 20).History);
        // 28 Sep is September's; the four Mondays from 5 Oct are October's; 2 Nov is November's.
        Assert.Equal([10.0, 15.0, 20.0], months.Select(m => m.Value));
        Assert.Equal(new DateTime(2026, 10, 1), months[1].Start);
    }

    [Fact]
    public void Too_soon_says_how_many_weeks_it_has()
    {
        var part = Rigsight.Models.CoolingCard.Of(Row("cpu", 14, 15) with { LoadNow = 60, LoadThen = 59 }, null, byMonth: false).Single();
        Assert.Equal("Too soon to say. It needs 4 weeks of games or other heavy use, and has 2 so far.", part.TailText);
        Assert.Equal("Under load 60°", part.LoadText); // no "was" while too soon
        var none = Rigsight.Models.CoolingCard.Of(new WearRow { Part = "cpu", Name = "CPU", State = WearState.TooSoon }, null, byMonth: false).Single();
        Assert.Equal("—", none.BigText);
        Assert.Equal("Too soon to say. It needs 4 weeks of games or other heavy use.", none.TailText);
    }
}

/// <summary>The Fans page's "Over the months" lines: each fan's speed at the same heat, and many fans said together.</summary>
public sealed class FanWearLinesTests
{
    private static readonly DateTime Monday = new(2026, 9, 28);

    private static Rigsight.Models.WearTiles.FanWear Fan(string title, params double[] rpm)
    {
        var weeks = rpm.Select((v, i) => new WearWeek(Monday.AddDays(7 * i), v)).ToList();
        var row = Wear.Judge("fan", title, weeks, 0, (now, then) => then > 0 && Math.Abs(now - then) >= Wear.FanMovedRpm && Math.Abs(now - then) / then >= Wear.FanMovedShare);
        return new(title, title.StartsWith("GPU"), row with { RangeLow = 66, RangeHigh = 70 }, System.Windows.Media.Brushes.Gray);
    }

    [Fact]
    public void A_fan_that_spins_faster_for_the_same_heat_comes_first()
    {
        var lines = Rigsight.Models.WearTiles.Fans([Fan("GPU fans", 1986, 1985, 1992, 1988, 1990), Fan("Fan #1", 1500, 1510, 1800, 1850, 1860)]);
        Assert.Equal("Fan #1 spins 350 RPM faster for the same heat", lines[0].Statement);
        Assert.Equal(WearState.Moved, lines[0].State);
        Assert.Equal("GPU fans spin as fast for the heat as they did", lines[1].Statement);
        Assert.Equal("1989 RPM at 66° to 72°, 1986 then", lines[1].Detail);
    }

    [Fact]
    public void Past_four_lines_the_steady_fans_are_said_together()
    {
        var lines = Rigsight.Models.WearTiles.Fans([Fan("Fan #1", 1500, 1500, 1500, 1500), Fan("Fan #2", 1200, 1200, 1210, 1205), Fan("Fan #3", 900, 900, 900, 900),
            Fan("Fan #4", 800, 800, 816, 816), Fan("Fan #5", 700, 700, 700, 700), Fan("Fan #6", 600, 610)]);
        Assert.Equal(4, lines.Count);
        Assert.Equal("Fan #3, Fan #4 and Fan #5 as they did", lines[2].Statement);
        Assert.Equal("Each within 2% at the same heat", lines[2].Detail);
        Assert.Equal("Too soon to say for Fan #6", lines[3].Statement);
        Assert.Equal("It needs 4 weeks of games or other heavy use, and has 2 so far.", lines[3].Detail);
    }
}

/// <summary>PC health's line while a chip runs hotter at the same load than in its first weeks.</summary>
public sealed class WearHealthLineTests
{
    [Fact]
    public void Only_a_chip_that_runs_hotter_gets_a_line_and_it_opens_Temperatures()
    {
        var since = new DateTime(2026, 9, 28);
        var cpu = new WearRow { Part = "cpu", Name = "CPU", State = WearState.Moved, Now = 18, Then = 14, ThenDate = since };
        var gpu = new WearRow { Part = "gpu", Name = "GPU", State = WearState.Steady, Now = 21, Then = 20, ThenDate = since };
        var line = Wear.HealthLine(cpu, gpu)!;
        Assert.Equal("CPU runs 4° hotter at the same load than in late September", line.Text);
        Assert.Equal(InsightTone.Warn, line.Tone);
        Assert.Equal(InsightOpens.Temperatures, InsightLinks.Of(line));
        Assert.Null(Wear.HealthLine(gpu, gpu));
        Assert.Null(Wear.HealthLine(cpu with { Now = 10 }, gpu)); // cooler: no line
        // In the place of "Temperatures as usual".
        var parts = HomeCards.Health(null, [], 0, [], null, null, line);
        Assert.Equal(line.Text, parts[0].Text);
    }
}

/// <summary>Every reading in plain words: each group a column of its own.</summary>
public sealed class ReadingColumnsTests
{
    private static Rigsight.Models.ReadingRow H(string g) => new(g, "", "", "", null) { IsHeader = true };
    private static Rigsight.Models.ReadingRow R(string g, string n) => new(g, n, "1", "", null);

    [Fact]
    public void Each_group_keeps_all_its_lines_in_its_own_column()
    {
        var list = new List<Rigsight.Models.ReadingRow> { H("WATCH"), R("WATCH", "Cable"), H("WEAR"), R("WEAR", "Life"), R("WEAR", "Written"),
            R("WEAR", "Read"), R("WEAR", "Hours") };
        var groups = Rigsight.Models.DriveReadings.Groups(list);
        Assert.Equal(["WATCH", "WEAR"], groups.Select(g => g.Title));
        Assert.Equal(["Cable"], groups[0].Rows.Select(r => r.Name));
        Assert.Equal(["Life", "Written", "Read", "Hours"], groups[1].Rows.Select(r => r.Name));
    }
}
