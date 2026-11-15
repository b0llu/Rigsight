using Rigsight.Core.Data;
using Rigsight.Core.Protocol;
using Rigsight.Core.Storage;
using Rigsight.Tests.Data;

namespace Rigsight.Tests.Core;

/// <summary>
/// Every reading on an opened drive (approved mock-up "Every reading, made clearer", 2026-11-14): the plain list's two
/// groups, its change words against the month-ago day, the NVMe warning flag in words, and the drive's own list.
/// </summary>
public sealed class DriveReadingsTests
{
    private static readonly DateTime Today = new(2026, 11, 14);
    private static readonly DateTime MonthAgo = new(2026, 10, 14);
    private static string On(DateTime d) => $"{d:d MMM}";

    private static DriveAttribute A(int id, string name, ulong raw, int current = 100, int worst = 100, int threshold = 0) =>
        new() { Id = id, Name = name, Raw = raw, Current = current, Worst = worst, Threshold = threshold };

    private static DriveHealthDay Day(DateTime d, Action<DriveHealthDay>? set = null)
    {
        var row = new DriveHealthDay { Day = TimeUtil.ToUnix(d), Drive = "K1", Name = "Drive", Status = "Good" };
        set?.Invoke(row);
        return row;
    }

    /// <summary>The Kingston A400 of the mock-up: SATA SSD, nothing to watch but its cable errors (and 187, which it gives).</summary>
    private static DriveHealthInfo Kingston() => new()
    {
        Name = "KINGSTON SA400S37480G", Key = "K1", Status = "Good", IsSsd = true, Life = 69, WrittenGb = 129_600, ReadGb = 74_600,
        PowerOnHours = 12_371, PowerOnCount = 3_591, Temperature = 34, ReallocatedSectors = null,
        Attributes = [A(9, "Power-On Hours", 12_371), A(12, "Power Cycle Count", 3_591), A(187, "Reported Uncorrectable Errors", 0),
            A(194, "Temperature", 34, 34, 49), A(199, "UltraDMA CRC Error Count", 0), A(231, "SSD Life Left", 69, 69, 69, 10), A(241, "Total Host Writes", 129_600)],
    };

    [Fact]
    public void A_sata_ssd_says_what_changed_since_a_month_ago_in_words()
    {
        var days = new List<DriveHealthDay>
        {
            Day(MonthAgo, d => { d.Life = 71; d.WrittenGb = 127_400; d.ReadGb = 73_100; d.Hours = 12_010; d.PowerOns = 3_548; d.CableErrors = 0; d.ReportedUncorrectable = 0; d.TempAvg = 36; }),
            Day(Today.AddDays(-1), d => d.TempAvg = 34),
        };
        var list = DriveReadings.Plain(Kingston(), days, Today);

        Assert.Equal(MonthAgo, list.Since);
        Assert.Equal($"SINCE {On(MonthAgo)}".ToUpperInvariant(), list.SinceTitle);
        Assert.Equal("All as usual", list.Summary);
        Assert.Equal(["Data it couldn't read back", "Cable or connection errors"], list.Watch.Select(r => r.Name));
        var cable = list.Watch[1];
        Assert.Equal(("0", "Usually 0", ReadingDot.Good, $"Same as on {On(MonthAgo)}", false), (cable.Now, cable.Usual, cable.Dot, cable.Change, cable.ChangeWarm));
        Assert.Equal("UltraDMA CRC Error Count, 199 (C7)", cable.MakerLine);

        var wear = list.Wear.ToDictionary(r => r.Name);
        Assert.Equal($"Down 2% since {On(MonthAgo)}", wear["Life left"].Change);
        Assert.Equal($"Up 2.2 TB since {On(MonthAgo)}", wear["Data written"].Change);
        Assert.Equal($"Up 1.5 TB since {On(MonthAgo)}", wear["Data read"].Change);
        Assert.Equal($"Up 361 since {On(MonthAgo)}", wear["Hours on"].Change);
        Assert.Equal($"Up 43 since {On(MonthAgo)}", wear["Times switched on"].Change);
        Assert.Equal($"Average 35° since {On(MonthAgo)}", wear["Temperature"].Change);
        Assert.Equal("SSD Life Left, 231 (E7)", wear["Life left"].MakerLine);
        Assert.All(list.Wear, r => Assert.False(r.ChangeWarm));
    }

    [Fact]
    public void A_hard_drive_whose_sectors_rose_shows_them_in_amber()
    {
        var hdd = new DriveHealthInfo
        {
            Name = "WDC WD20EZAZ", Key = "K1", Status = "Caution", ReallocatedSectors = 24, PendingSectors = 8, UncorrectableSectors = 2, PowerOnHours = 31_204,
            Attributes = [A(5, "Reallocated Sectors Count", 24, 199, 199, 140), A(192, "Power-off Retract Count", 88), A(197, "Current Pending Sector Count", 8),
                A(198, "Uncorrectable Sector Count", 2), A(199, "UltraDMA CRC Error Count", 0)],
        };
        var days = new List<DriveHealthDay> { Day(MonthAgo, d => { d.Reallocated = 8; d.Pending = 3; d.Uncorrectable = 2; d.CableErrors = 0; d.ShutOffs = 86; }) };
        var list = DriveReadings.Plain(hdd, days, Today);

        Assert.Equal(["Bad sectors swapped out", "Sectors waiting to be swapped", "Sectors it couldn't read", "Cable or connection errors"], list.Watch.Select(r => r.Name));
        Assert.Equal(($"Up 16 since {On(MonthAgo)}", true, ReadingDot.Warm), (list.Watch[0].Change, list.Watch[0].ChangeWarm, list.Watch[0].Dot));
        Assert.Equal(($"Same as on {On(MonthAgo)}", false, ReadingDot.Warm), (list.Watch[2].Change, list.Watch[2].ChangeWarm, list.Watch[2].Dot));
        Assert.Equal("3 aren't as usual", list.Summary);
        var cuts = Assert.Single(list.Wear, r => r.Name == "Sudden shut-offs");
        Assert.Equal(($"Up 2 since {On(MonthAgo)}", false, "Power-off Retract Count, 192 (C0)"), (cuts.Change, cuts.ChangeWarm, cuts.MakerLine));
    }

    [Fact]
    public void A_falling_pending_count_is_not_amber_and_one_watched_reading_off_says_isnt()
    {
        var hdd = new DriveHealthInfo { Name = "HDD", Key = "K1", ReallocatedSectors = 0, PendingSectors = 2, Attributes = [] };
        var list = DriveReadings.Plain(hdd, [Day(MonthAgo, d => { d.Reallocated = 0; d.Pending = 7; })], Today);
        Assert.Equal(($"Down 5 since {On(MonthAgo)}", false), (list.Watch[1].Change, list.Watch[1].ChangeWarm));
        Assert.Equal("1 isn't as usual", list.Summary);
    }

    [Fact]
    public void An_nvme_drive_says_which_warning_it_gives_and_its_spare_blocks_against_its_limit()
    {
        var nvme = new DriveHealthInfo
        {
            Name = "WD Blue SN570 1TB", Key = "K1", IsNvme = true, IsSsd = true, Life = 31, Temperature = 74, TemperatureWarning = 70,
            Attributes = [A(1, "Critical Warning", 3), A(2, "Composite Temperature", 74), A(3, "Available Spare", 6), A(4, "Available Spare Threshold", 10),
                A(13, "Unsafe Shutdowns", 41), A(14, "Media and Data Integrity Errors", 3)],
        };
        var days = new List<DriveHealthDay> { Day(MonthAgo, d => { d.Life = 32; d.Warning = 0; d.Spare = 14; d.MediaErrors = 0; d.ShutOffs = 41; d.TempAvg = 66; }) };
        var list = DriveReadings.Plain(nvme, days, Today);

        var warn = list.Watch[0];
        Assert.Equal(("Warnings from the drive", "Yes", "It warns of low spare blocks and heat", $"New since {On(MonthAgo)}", true),
            (warn.Name, warn.Now, warn.What, warn.Change, warn.ChangeWarm));
        var spare = list.Watch[1];
        Assert.Equal(("6%", "Warns under 10%", ReadingDot.Warm, $"Down 8% since {On(MonthAgo)}", true), (spare.Now, spare.Usual, spare.Dot, spare.Change, spare.ChangeWarm));
        Assert.Equal(($"Up 3 since {On(MonthAgo)}", true), (list.Watch[2].Change, list.Watch[2].ChangeWarm));
        Assert.Equal("3 aren't as usual", list.Summary);

        var temp = Assert.Single(list.Wear, r => r.Name == "Temperature");
        Assert.True(temp.ValueWarm);
        Assert.Equal("The drive warns at 70°.", temp.Tip);
        Assert.Equal($"Same as on {On(MonthAgo)}", Assert.Single(list.Wear, r => r.Name == "Sudden shut-offs").Change);
    }

    [Fact]
    public void The_warning_flags_bits_become_words()
    {
        Assert.Equal("", DriveCounts.WarningWords(0));
        Assert.Equal("heat", DriveCounts.WarningWords(2));
        Assert.Equal("low spare blocks, errors lowering its reliability and a switch to read-only", DriveCounts.WarningWords(1 | 4 | 8));
        Assert.Equal("a failed power-loss backup", DriveCounts.WarningWords(16));
    }

    [Fact]
    public void A_healthy_nvme_says_none_and_its_own_flag()
    {
        var nvme = new DriveHealthInfo { Name = "990 PRO", Key = "K1", IsNvme = true, Attributes = [A(1, "Critical Warning", 0)] };
        var w = Assert.Single(DriveReadings.Plain(nvme, [Day(MonthAgo, d => d.Warning = 0)], Today).Watch);
        Assert.Equal(("None", "Usually none", ReadingDot.Good, "Its own flag for low spare blocks, heat or errors", $"Same as on {On(MonthAgo)}"), (w.Now, w.Usual, w.Dot, w.What, w.Change));
    }

    [Fact]
    public void Under_a_month_it_compares_with_the_oldest_kept_day_and_on_the_first_day_it_is_too_soon()
    {
        var early = Today.AddDays(-9);
        var list = DriveReadings.Plain(Kingston(), [Day(early, d => { d.Hours = 12_200; d.CableErrors = null; }), Day(Today.AddDays(-2), d => d.Hours = 12_300)], Today);
        Assert.Equal(early, list.Since);
        Assert.Equal($"Up 171 since {On(early)}", Assert.Single(list.Wear, r => r.Name == "Hours on").Change);
        // A count the day before this version didn't keep.
        Assert.Equal("Too soon to say", Assert.Single(list.Watch, r => r.Name == "Cable or connection errors").Change);

        var first = DriveReadings.Plain(Kingston(), [Day(Today)], Today);
        Assert.Null(first.Since);
        Assert.Equal("", first.SinceTitle);
        Assert.All(first.Watch.Concat(first.Wear), r => Assert.Equal("Too soon to say", r.Change));
    }

    [Fact]
    public void Packed_command_timeouts_take_only_their_lowest_counter()
    {
        var seagate = new DriveHealthInfo { Name = "ST2000", Attributes = [A(188, "Command Timeout", 0x0001_0001_0001)] };
        Assert.Equal(1, DriveCounts.Timeouts(seagate));
        var plain = new DriveHealthInfo { Name = "HDD", Attributes = [A(188, "Command Timeout", 3)] };
        Assert.Equal(3, DriveCounts.Timeouts(plain));
        Assert.Equal("Commands that timed out", Assert.Single(DriveReadings.Plain(plain, [], Today).Watch).Name);
    }

    [Fact]
    public void The_drives_own_list_ties_each_attribute_to_its_plain_name()
    {
        var h = Kingston();
        var own = DriveReadings.Own(h, DriveReadings.Plain(h, [], Today));
        var crc = Assert.Single(own, l => l.IdText == "199 · C7");
        Assert.Equal(("UltraDMA CRC Error Count", "Cable or connection errors", ReadingDot.Good, "100", "100", "0"), (crc.Name, crc.Plain, crc.Dot, crc.Score, crc.Lowest, crc.Limit));
        Assert.Equal("Score 100 now and 100 at its lowest. The drive sets no limit for it, so it never flags it.\nShown in plain words as Cable or connection errors.", crc.Tip);
        var hours = Assert.Single(own, l => l.IdText == "9 · 09");
        Assert.Equal(("Hours on", ReadingDot.None), (hours.Plain, hours.Dot));
        var life = Assert.Single(own, l => l.IdText == "231 · E7");
        Assert.StartsWith("Score 69 now and 69 at its lowest. The drive flags it once the score falls to 10.", life.Tip);

        var nvme = new DriveHealthInfo { Name = "990", IsNvme = true, Attributes = [A(1, "Critical Warning", 0), A(10, "Controller Busy Time", 1_906)] };
        var nOwn = DriveReadings.Own(nvme, DriveReadings.Plain(nvme, [], Today));
        Assert.Equal(("", "Shown in plain words as Warnings from the drive."), (nOwn[0].Score, nOwn[0].Tip));
        Assert.Equal(("1,906", "Not in the plain-words list."), (nOwn[1].Count, nOwn[1].Tip));
    }

    [Fact]
    public void The_daily_row_keeps_the_counts_the_plain_list_sets_against_a_month_ago()
    {
        using var t = new TestDb();
        long day = TimeUtil.ToUnix(Today);
        t.Db.UpsertDriveHealth(day, Kingston());
        t.Db.UpsertDriveHealth(day, new DriveHealthInfo
        {
            Name = "SN570", Key = "N1", IsNvme = true, Status = "Good",
            Attributes = [A(1, "Critical Warning", 1), A(3, "Available Spare", 6), A(13, "Unsafe Shutdowns", 41), A(14, "Media and Data Integrity Errors", 3)],
        });
        var rows = t.Db.GetDriveHealthDays(day).ToDictionary(r => r.Drive);
        Assert.Equal((0L, 0L, (long?)null, (long?)null), (rows["K1"].CableErrors, rows["K1"].ReportedUncorrectable, rows["K1"].Spare, rows["K1"].ShutOffs));
        Assert.Equal((1L, 6L, 41L, 3L, (long?)null), (rows["N1"].Warning, rows["N1"].Spare, rows["N1"].ShutOffs, rows["N1"].MediaErrors, rows["N1"].CableErrors));
    }
}
