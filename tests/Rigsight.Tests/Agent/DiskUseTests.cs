using System.Buffers.Binary;
using Rigsight.Agent.Storage;
using Rigsight.Core.Data;
using Rigsight.Core.Storage;

namespace Rigsight.Tests.Agent;

/// <summary>What each app reads and writes on each drive: the disk events read, added up, put down to apps and saved.</summary>
public class DiskUseTests
{
    /// <summary>A Kernel-Disk read or write payload as Windows lays it out (x64): DiskNumber, IrpFlags, TransferSize, Reserved,
    /// ByteOffset, FileObject, IORequestPacket, HighResResponseTime.</summary>
    private static byte[] Payload(uint disk, uint size)
    {
        var b = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(b, disk);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 0x60043);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), size);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), 123_456_789);
        return b;
    }

    [Fact]
    public void A_read_and_a_write_are_read_from_their_payload()
    {
        Assert.True(DiskTrace.Decode(10, Payload(2, 65536), out int disk, out bool write, out long size));
        Assert.Equal((2, false, 65536L), (disk, write, size));
        Assert.True(DiskTrace.Decode(11, Payload(0, 4096), out disk, out write, out size));
        Assert.Equal((0, true, 4096L), (disk, write, size));
    }

    [Fact]
    public void A_flush_a_short_payload_and_an_empty_transfer_count_for_nothing()
    {
        Assert.False(DiskTrace.Decode(14, Payload(1, 4096), out _, out _, out _));
        Assert.False(DiskTrace.Decode(10, Payload(1, 4096).AsSpan(0, 11), out _, out _, out _));
        Assert.False(DiskTrace.Decode(11, Payload(1, 0), out _, out _, out _));
    }

    [Fact]
    public void Events_add_up_by_process_and_drive_reads_and_writes_apart()
    {
        var counts = new Dictionary<DiskIoKey, DiskCounts>();
        DiskTrace.Add(counts, 100, 1, write: false, 4096);
        DiskTrace.Add(counts, 100, 1, write: false, 8192);
        DiskTrace.Add(counts, 100, 1, write: true, 512);
        DiskTrace.Add(counts, 100, 2, write: true, 1000);
        DiskTrace.Add(counts, 4, 1, write: true, 65536);
        Assert.Equal(3, counts.Count);
        Assert.Equal((12288L, 512L), (counts[new(100, 1)].Read, counts[new(100, 1)].Write));
        Assert.Equal((0L, 1000L), (counts[new(100, 2)].Read, counts[new(100, 2)].Write));
        Assert.Equal(65536L, counts[new(4, 1)].Write);
    }

    [Fact]
    public void The_cache_s_writes_are_the_system_s_own_row_and_unnamed_or_unknown_drives_are_left_out()
    {
        using var rig = new TrackerRig();
        rig.Use(null, 2);
        int game = rig.Pid("game.exe");
        rig.Tracker.SetDiskMap(new Dictionary<int, string> { [0] = "SERIAL-A", [1] = "Samsung SSD 980 1TB|1000" });
        var counts = new Dictionary<DiskIoKey, DiskCounts>
        {
            [new(game, 1)] = new() { Read = 5_000_000, Write = 1_000 },
            [new(4, 1)] = new() { Write = 7_000_000 },                 // Windows' file cache writing for the apps
            [new(4, 0)] = new() { Read = 300 },
            [new(-1, 1)] = new() { Read = 9_999 },                      // no process named: not put down to anyone
            [new(game, 5)] = new() { Read = 123 },                      // a drive the sensors didn't find
        };
        rig.Tracker.OnDisk(counts, new Dictionary<int, string> { [game] = "game.exe" });
        rig.Tracker.OnDisk(new() { [new(game, 1)] = new() { Read = 1_000_000 } }, new Dictionary<int, string> { [game] = "game.exe" });
        rig.Tracker.Flush(closeAllSessions: false);

        var rows = rig.Db.GetDiskAppHours(0, long.MaxValue);
        long gameId = rig.AppId("game.exe"), systemId = rig.AppId("System");
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.App == gameId && r.Disk == "Samsung SSD 980 1TB|1000" && r.Read == 6_000_000 && r.Write == 1_000);
        Assert.Contains(rows, r => r.App == systemId && r.Disk == "Samsung SSD 980 1TB|1000" && r.Write == 7_000_000);
        Assert.Contains(rows, r => r.App == systemId && r.Disk == "SERIAL-A" && r.Read == 300);
        Assert.Equal(rig.Db.GetDiskAppDays(0, long.MaxValue).Sum(r => r.Total), rows.Sum(r => r.Total));

        var summary = rig.Tracker.TakeDiskSummary();
        Assert.Equal((13_001_300L, 9_999L, 0L, 123L), summary);
    }

    [Fact]
    public void A_new_map_is_said_only_when_the_drives_change()
    {
        var tracker = new DiskTracker(null!);
        Assert.True(tracker.SetDiskMap(new Dictionary<int, string> { [0] = "A", [1] = "B" }));
        Assert.False(tracker.SetDiskMap(new Dictionary<int, string> { [1] = "B", [0] = "A" }));
        Assert.True(tracker.SetDiskMap(new Dictionary<int, string> { [0] = "B", [1] = "A" }));
    }

    [Fact]
    public void Use_adds_up_by_hour_and_day_reads_back_by_drive_and_in_total_and_is_pruned_and_cleared()
    {
        using var rig = new TrackerRig();
        var db = rig.Db;
        long day1 = TimeUtil.ToUnix(new DateTime(2026, 6, 10)), day2 = TimeUtil.ToUnix(new DateTime(2026, 6, 11));
        db.AddDiskAppUse(new DiskAppUse(day1 + 3600, 1, "A", 100, 10));
        db.AddDiskAppUse(new DiskAppUse(day1 + 3600, 1, "A", 50, 5));   // the same hour again: added
        db.AddDiskAppUse(new DiskAppUse(day1 + 7200, 1, "B", 1000, 0));
        db.AddDiskAppUse(new DiskAppUse(day1 + 7200, 2, "A", 0, 400));
        db.AddDiskAppUse(new DiskAppUse(day2 + 3600, 2, "A", 0, 600));

        Assert.Equal(4, db.GetDiskAppHours(0, long.MaxValue).Count);
        Assert.Equal(new DiskAppUse(day1 + 3600, 1, "A", 150, 15), db.GetDiskAppHours(day1, day1 + 7200).Single());
        Assert.Equal(4, db.GetDiskAppDays(0, long.MaxValue).Count);

        // On drive A over both days: app 2 wrote 1,000, app 1 read and wrote 165.
        var onA = db.GetDiskAppTotals(day1, day2 + 86400, "A");
        Assert.Equal([new DiskAppUse(0, 2, "A", 0, 1000), new DiskAppUse(0, 1, "A", 150, 15)], onA);
        // Every drive: app 1's 1,165 comes first; the top one only.
        Assert.Equal([new DiskAppUse(0, 1, null, 1150, 15)], db.GetDiskAppTotals(day1, day2 + 86400, top: 1));
        // The hours for a stretch that doesn't start at midnight.
        Assert.Equal([new DiskAppUse(0, 1, "B", 1000, 0)], db.GetDiskAppTotals(day1 + 7200, day1 + 10800, "B", byHour: true));
        Assert.Equal([new DiskAppUse(0, 0, "A", 150, 1015), new DiskAppUse(0, 0, "B", 1000, 0)], db.GetDiskTotals(day1, day2 + 86400));

        db.Prune(day2);
        Assert.All(db.GetDiskAppHours(0, long.MaxValue), r => Assert.True(r.Ts >= day2));
        Assert.All(db.GetDiskAppDays(0, long.MaxValue), r => Assert.Equal(day2, r.Ts));

        db.ClearHistory();
        Assert.Empty(db.GetDiskAppHours(0, long.MaxValue));
        Assert.Empty(db.GetDiskAppDays(0, long.MaxValue));
    }

    [Theory]
    [InlineData("Samsung SSD 980 1TB", " 0025_38D4_31A0_1E9D. ", 1_000_204_886_016UL, "0025_38D4_31A0_1E9D.")]
    [InlineData("KINGSTON SA400S37480G", "", 480_103_981_056UL, "KINGSTON SA400S37480G|480")]
    [InlineData(" ST2000VX008-2E3164 ", null, 2_000_398_934_016UL, "ST2000VX008-2E3164|2000")]
    public void A_drive_is_known_by_its_serial_or_else_its_model_and_size(string model, string? serial, ulong bytes, string key) =>
        Assert.Equal(key, DriveKey.Of(model, serial, bytes));
}
