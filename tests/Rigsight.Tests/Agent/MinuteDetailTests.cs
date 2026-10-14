using Rigsight.Agent.Sensors;
using Rigsight.Agent.Tracking;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;
using Rigsight.Tests.Data;

namespace Rigsight.Tests.Agent;

/// <summary>
/// What each minute keeps beyond the readings, for an opened crash to show everything about the PC in the minutes
/// before it: the apps doing the most with how much, what the drives were doing, how near Windows was to running out of
/// memory, and the battery.
/// </summary>
public class MinuteDetailTests
{
    private static Tracker.AppAcc Acc(double cpu = 0, double gpu = 0, double mem = 0, double disk = 0, int samples = 12) =>
        new() { Cpu = cpu * samples, Gpu = gpu * samples, Mem = mem * samples, MemN = samples, Disk = disk * samples };

    [Fact]
    public void A_minute_keeps_the_hardest_on_each_thing_and_nobody_doing_nothing_much()
    {
        const double MB = 1_048_576;
        var apps = new Dictionary<string, Tracker.AppAcc>(StringComparer.OrdinalIgnoreCase)
        {
            ["encode.exe"] = Acc(cpu: 61, mem: 4800, disk: 40 * MB),
            ["game.exe"] = Acc(cpu: 20, gpu: 74, mem: 5800),
            ["chat.exe"] = Acc(cpu: 3, gpu: 3, mem: 900),
            ["browser.exe"] = Acc(cpu: 2, mem: 3100),
            ["idle.exe"] = Acc(cpu: 0.6, mem: 200),              // nothing worth naming
            ["copy.exe"] = Acc(cpu: 0.4, mem: 60, disk: 12 * MB), // only the drives
            ["sync.exe"] = Acc(cpu: 0.2, mem: 80, disk: 2 * MB),  // third on the drives: not kept
        };
        var kept = Tracker.TopApps(apps, 12);
        // Three by CPU (encode, game, chat), the GPU's one above the least (game), three by memory (game, encode,
        // browser), two by the drives (encode, copy): six apps, the hardest on the CPU first.
        Assert.Equal(["encode.exe", "game.exe", "chat.exe", "browser.exe", "copy.exe"], kept.Select(a => a.Exe));
        var encode = kept[0];
        Assert.Equal((61, 0, 4800, 40 * MB), (encode.Cpu, encode.Gpu, encode.MemMb, encode.Disk));
        Assert.Equal(74, kept[1].Gpu);
        // Nothing sampled, or nobody there: nothing kept.
        Assert.Empty(Tracker.TopApps(apps, 0));
        Assert.Empty(Tracker.TopApps(new Dictionary<string, Tracker.AppAcc>(), 12));
    }

    [Fact]
    public void An_app_that_ran_for_half_the_minute_counts_for_half_and_keeps_its_own_memory()
    {
        var half = new Tracker.AppAcc { Cpu = 80 * 6, Mem = 2000 * 6, MemN = 6 };
        var kept = Assert.Single(Tracker.TopApps(new Dictionary<string, Tracker.AppAcc> { ["burst.exe"] = half }, 12));
        Assert.Equal((40, 2000), (kept.Cpu, kept.MemMb));
    }

    [Fact]
    public void The_minute_is_written_with_its_apps_its_drives_its_memory_promised_and_its_battery()
    {
        using var rig = new TrackerRig();
        rig.Usage["encode.exe"] = (60, 4800);
        rig.DiskUsage["encode.exe"] = 40_000_000;
        rig.Usage["game.exe"] = (20, 5800);
        rig.GpuUsage["game.exe"] = 74;
        rig.Usage["idle.exe"] = (0.1, 40);
        rig.Running.Add("encode.exe");
        rig.Running.Add("idle.exe");
        rig.Keys = new KeyValues { CpuTemp = 88, CommitPct = 71.4, Battery = 37, OnBattery = true, GpuVramUsed = 6144.4, GpuVramTotal = 12288 };
        rig.Use("game.exe", 180);
        rig.Tracker.Flush(closeAllSessions: false);

        var minute = rig.Minutes().First(m => m.DiskRate is not null);
        Assert.Equal((40_000_000, 40_000_000), (minute.DiskRate, minute.DiskRateMax));
        Assert.Equal((71, 37, true), (minute.CommitPct, minute.Battery, minute.OnBattery));
        Assert.Equal((6144, 12288), (minute.VramUsed, minute.VramTotal));

        var apps = rig.Db.GetAppMinutes(minute.Ts, minute.Ts + 60);
        Assert.Equal([rig.AppId("encode.exe"), rig.AppId("game.exe")], apps.Select(a => a.App));
        Assert.Equal((60, 0, 4800, 40_000_000), (apps[0].Cpu, apps[0].Gpu, apps[0].MemMb, apps[0].Disk));
        Assert.Equal((20, 74, 5800, 0), (apps[1].Cpu, apps[1].Gpu, apps[1].MemMb, apps[1].Disk));
        // An app doing nothing never gets a row, nor a name in the list of apps just for having run.
        Assert.DoesNotContain(rig.Db.GetAppMinutes(0, long.MaxValue / 2), a => rig.HasApp("idle.exe") && a.App == rig.AppId("idle.exe"));
    }

    [Fact]
    public void An_app_left_out_of_tracking_is_never_kept_and_a_pc_without_a_battery_says_nothing_of_one()
    {
        var settings = new RigsightSettings();
        settings.Tracking.ExcludedApps.Add("secret.exe");
        using var rig = new TrackerRig(settings: settings);
        rig.Usage["secret.exe"] = (70, 6000);
        rig.Usage["code.exe"] = (6, 500);
        rig.Running.Add("secret.exe");
        rig.Keys = new KeyValues { CpuTemp = 60 };
        rig.Use("code.exe", 180);
        rig.Tracker.Flush(closeAllSessions: false);
        Assert.False(rig.HasApp("secret.exe"));
        Assert.All(rig.Db.GetAppMinutes(0, long.MaxValue / 2), a => Assert.Equal(rig.AppId("code.exe"), a.App));
        Assert.All(rig.Minutes(), m => Assert.Equal((null, null, null), (m.CommitPct, m.Battery, m.OnBattery)));
    }

    [Fact]
    public void The_apps_of_a_minute_are_read_by_range_replaced_when_written_again_and_go_with_the_history()
    {
        using var t = new TestDb();
        long t0 = 1_760_000_040;
        t.Db.WriteAppMinutes(t0, [new AppMinute(t0, 3, 61.26, 0, 4800.4, 41_943_040.3), new AppMinute(t0, 5, 20, 74.04, 5800, 0)]);
        t.Db.WriteAppMinutes(t0 + 60, [new AppMinute(t0 + 60, 3, 12, 0, 4000, 0)]);
        // To a tenth of a percent, a whole megabyte, a whole byte a second.
        Assert.Equal([new AppMinute(t0, 3, 61.3, 0, 4800, 41_943_040), new AppMinute(t0, 5, 20, 74, 5800, 0)], t.Db.GetAppMinutes(t0, t0 + 60));
        Assert.Equal(3, t.Db.GetAppMinutes(t0, t0 + 120).Count);
        Assert.Empty(t.Db.GetAppMinutes(t0 + 120, t0 + 600));
        // The minute written again (the agent rewrites the open minute as it goes): what it says now, not both.
        t.Db.WriteAppMinutes(t0, [new AppMinute(t0, 9, 5, 0, 300, 0)]);
        Assert.Equal([9L], t.Db.GetAppMinutes(t0, t0 + 60).Select(a => a.App));
        // Older than the history is kept: gone with the minutes.
        t.Db.Prune(t0 + 60);
        Assert.Equal([t0 + 60], t.Db.GetAppMinutes(0, long.MaxValue / 2).Select(a => a.Ts));
        t.Db.ClearHistory();
        Assert.Empty(t.Db.GetAppMinutes(0, long.MaxValue / 2));
    }

    [Fact]
    public void A_minutes_new_figures_come_back_as_written_and_are_blank_where_never_recorded()
    {
        using var t = new TestDb();
        long t0 = 1_760_000_040;
        var with = Make.Minute(t0);
        (with.DiskRate, with.DiskRateMax, with.CommitPct, with.Battery, with.OnBattery) = (1_234_567.8, 9_000_000, 93.6, 12, false);
        t.Db.WriteMinute(with);
        t.Db.WriteMinute(Make.Minute(t0 + 60));
        var read = t.Db.GetMinutes(t0, t0 + 120);
        Assert.Equal((1_234_568, 9_000_000, 94, 12, false), (read[0].DiskRate, read[0].DiskRateMax, read[0].CommitPct, read[0].Battery, read[0].OnBattery));
        Assert.Equal((null, null, null, null, null), (read[1].DiskRate, read[1].DiskRateMax, read[1].CommitPct, read[1].Battery, read[1].OnBattery));
    }

    [Theory]
    [InlineData(32UL, 8UL, 75.0)]
    [InlineData(32UL, 0UL, 100.0)]
    [InlineData(32UL, 32UL, 0.0)]
    public void Memory_promised_is_the_share_of_what_windows_can_promise(ulong limit, ulong free, double expected) =>
        Assert.Equal(expected, SystemState.CommitPct(limit, free));

    [Fact]
    public void Memory_promised_and_the_battery_say_nothing_when_windows_doesnt()
    {
        Assert.Null(SystemState.CommitPct(0, 0));
        Assert.Null(SystemState.CommitPct(8, 16));
        // No battery (a desktop), or not known: nothing, not "0%".
        Assert.Equal((null, null), SystemState.Battery(acLine: 1, flag: 128, percent: 255));
        Assert.Equal((null, null), SystemState.Battery(acLine: 255, flag: 255, percent: 255));
        // A laptop on its battery at 37%, plugged in and charging at 80%, and one that doesn't know its charge.
        Assert.Equal((37, true), SystemState.Battery(acLine: 0, flag: 0, percent: 37));
        Assert.Equal((80, false), SystemState.Battery(acLine: 1, flag: 8, percent: 80));
        Assert.Equal((null, true), SystemState.Battery(acLine: 0, flag: 0, percent: 255));
        // This PC, whatever it is: a share of 0 to 100, and a battery or none.
        Assert.InRange(SystemState.CommitPct()!.Value, 0, 100);
        var (charge, onBattery) = SystemState.Battery();
        Assert.True(charge is null or (>= 0 and <= 100));
        Assert.True(charge is not null || onBattery is null);
    }
}
