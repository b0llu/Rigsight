using Rigsight.Agent.Tracking;
using Rigsight.Core.Data;
using Rigsight.Core.Stability;
using Rigsight.Tests.Data;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Agent;

/// <summary>
/// The agent's short memory of readings and what it keeps from it: an app's crash with its 5 minutes before (kept at
/// once) and 2 after (added once in), and what is stored.
/// </summary>
public sealed class CloseUpTests
{
    private static readonly long T0 = new DateTimeOffset(2026, 6, 10, 15, 0, 0, TimeSpan.FromHours(2)).ToUnixTimeMilliseconds();

    private static CloseUpPoint Point(long t, double cpu = 50, double gpu = 60) => new() { T = t, CpuTemp = cpu, GpuTemp = gpu, CpuLoad = 10, GpuLoad = 20, Here = true };

    /// <summary>A ring filled every 2 seconds from <paramref name="from"/> to <paramref name="to"/> (ms after T0).</summary>
    private static void Fill(CloseUpRing ring, long from, long to)
    {
        for (long t = from; t <= to; t += 2000) ring.Add(Point(T0 + t));
    }

    // ── The ring ──

    [Fact]
    public void The_ring_keeps_exactly_its_last_seven_minutes_and_never_grows()
    {
        var ring = new CloseUpRing();
        Fill(ring, 0, 60 * 60_000); // an hour of readings
        Assert.Equal(T0 + 60 * 60_000 - CloseUp.RingSec * 1000L, ring.Oldest);
        Assert.Equal(T0 + 60 * 60_000, ring.Newest);
        Assert.Equal(CloseUp.RingSec / 2 + 1, ring.Count);
    }

    [Fact]
    public void Readings_closer_than_two_seconds_are_one()
    {
        var ring = new CloseUpRing();
        for (long t = 0; t < 60_000; t += 1000) ring.Add(Point(T0 + t)); // the sensors every second while the app is open
        Assert.Equal(30, ring.Count);
    }

    [Fact]
    public void A_gap_stays_a_gap_and_the_clock_going_back_starts_again()
    {
        var ring = new CloseUpRing();
        Fill(ring, 0, 60_000);
        Fill(ring, 180_000, 240_000); // two minutes with nothing (asleep, paused)
        Assert.Empty(ring.Between(T0 + 61_000, T0 + 179_000));
        ring.Add(Point(T0 + 100_000)); // the clock put back
        Assert.Equal(1, ring.Count);
    }

    [Fact]
    public void The_tracker_fills_the_ring_with_each_reading_the_fans_and_the_apps_doing_the_most()
    {
        using var rig = new TrackerRig();
        rig.Keys = new Rigsight.Agent.Sensors.KeyValues { CpuTemp = 61.23, GpuTemp = 50, GpuVramUsed = 4000, GpuVramTotal = 8192 };
        rig.Fans.Add(new FanReading("/fan/0", "CPU fan", "Board", 1234));
        rig.Usage["busy.exe"] = (40, 2000);
        rig.Usage["quiet.exe"] = (1.5, 100);
        rig.Running.Add("quiet.exe");
        rig.Use("busy.exe", 60);
        var ring = rig.Tracker.Ring;
        Assert.InRange(ring.Count, 29, 31);
        var last = ring.Between(0, long.MaxValue)[^1];
        Assert.Equal((61.2, 4000d, 8192d), (last.CpuTemp, last.Vram, last.VramTotal));
        Assert.Equal(new CloseUpFan("CPU fan", 1234), Assert.Single(last.Fans!));
        Assert.Contains(last.Apps!, a => a.Cpu == 40 && a.MemMb == 2000);
        Assert.Equal(1, last.Apps!.Count); // an app doing too little to be noted anywhere isn't carried
        Assert.True(last.Here);
        Assert.NotNull(last.Front);
    }

    // ── An app crash ──

    private sealed class Rig : IDisposable
    {
        public TestDb Db { get; } = new("closeup");
        public CloseUpRing Ring { get; } = new();
        public long Now { get; set; } = T0;
        public CloseUpKeeper Keeper { get; }

        public Rig()
        {
            Keeper = new CloseUpKeeper(Db.Db, Ring, () => Now);
        }

        /// <summary>Readings every 2 seconds up to <paramref name="to"/> (ms after T0), the keeper ticking with them.</summary>
        public void Run(long to)
        {
            long from = Ring.Newest is long n ? n - T0 + 2000 : 0;
            for (long t = from; t <= to; t += 2000)
            {
                Now = T0 + t;
                Ring.Add(Point(Now));
                Keeper.Tick();
            }
        }

        public CrashEvent Store(CrashEvent e)
        {
            e = Db.Db.InsertCrashes([e]).Single();
            return Db.Db.GetCrashes(e.Ts - 1, e.Ts + 1).Single(c => c.Kind == e.Kind);
        }

        public void Dispose() => Db.Dispose();
    }

    private static CrashEvent AppCrash(long atMs) => new() { Ts = atMs / 1000, Kind = CrashKind.AppCrash, AppExe = "game.exe", Module = "game.exe" };

    [Fact]
    public void An_app_crash_keeps_its_five_minutes_before_at_once_and_its_two_after_once_they_are_in()
    {
        using var rig = new Rig();
        rig.Run(10 * 60_000);
        var crash = AppCrash(T0 + 9 * 60_000); // noticed a minute after it happened
        rig.Keeper.OnStored([crash]);

        var before = rig.Db.Db.GetCloseUp(crash.Ts, "game.exe")!;
        Assert.False(before.Done);
        Assert.Equal(crash.Ts * 1000 - CloseUp.BeforeSec * 1000, before.Points[0].T);
        Assert.Equal(T0 + 10 * 60_000, before.Points[^1].T); // and what came after so far

        rig.Run(11 * 60_000 + 2000);
        var full = rig.Db.Db.GetCloseUp(crash.Ts, "GAME.EXE")!;
        Assert.True(full.Done);
        Assert.Equal(CloseUp.BeforeSec / 2 + CloseUp.AfterSec / 2 + 1, full.Points.Count);
        Assert.Equal((crash.Ts + CloseUp.AfterSec) * 1000, full.Points[^1].T);
    }

    [Fact]
    public void A_crash_noticed_after_the_ring_has_let_its_minutes_go_keeps_nothing()
    {
        using var rig = new Rig();
        rig.Run(20 * 60_000);
        rig.Keeper.OnStored([AppCrash(T0 + 13 * 60_000)]);
        Assert.Equal((0, 0), rig.Db.Db.CloseUpSize());
    }

    [Fact]
    public void Shut_offs_and_blue_screens_get_no_close_up_the_PC_was_off_after_them()
    {
        using var rig = new Rig();
        rig.Run(10 * 60_000);
        rig.Keeper.OnStored([new CrashEvent { Ts = (T0 + 9 * 60_000) / 1000, Kind = CrashKind.UnexpectedShutdown },
            new CrashEvent { Ts = (T0 + 9 * 60_000 + 5000) / 1000, Kind = CrashKind.SystemCrash }]);
        Assert.Equal(0, rig.Db.Db.CloseUpSize().Count);
    }

    [Fact]
    public void A_graphics_driver_reset_gets_its_close_up_under_no_program()
    {
        using var rig = new Rig();
        rig.Run(10 * 60_000);
        var reset = new CrashEvent { Ts = (T0 + 9 * 60_000) / 1000, Kind = CrashKind.GpuDriverReset, AppExe = "" };
        rig.Keeper.OnStored([reset]);
        Assert.False(rig.Db.Db.GetCloseUp(reset.Ts, "")!.Done);
        rig.Run(11 * 60_000 + 2000);
        Assert.True(rig.Db.Db.GetCloseUp(reset.Ts, "")!.Done);
    }

    // ── Stored ──

    [Fact]
    public void A_close_up_is_kept_as_long_as_the_history_and_cleared_with_it()
    {
        using var db = new TestDb("closeup-store");
        var points = Enumerable.Range(0, 210).Select(i => Point(T0 + i * 2000L, 40 + i % 9, 60 + i % 5)).ToList();
        db.Db.SaveCloseUp(new CloseUp(T0 / 1000, "Game.exe", CloseUpKind.App, true, points));
        var back = db.Db.GetCloseUp(T0 / 1000, "game.exe")!;
        Assert.Equal(210, back.Points.Count);
        Assert.Equal(points[57].CpuTemp, back.Points[57].CpuTemp);
        // Tens of kilobytes for seven minutes.
        Assert.InRange(db.Db.CloseUpSize().Bytes, 1, 40_000);
        db.Db.Prune(T0 / 1000 - 1);
        Assert.NotNull(db.Db.GetCloseUp(T0 / 1000, "game.exe"));
        db.Db.Prune(T0 / 1000 + 1);
        Assert.Null(db.Db.GetCloseUp(T0 / 1000, "game.exe"));
        db.Db.SaveCloseUp(new CloseUp(T0 / 1000, "other.exe", CloseUpKind.App, true, points));
        db.Db.ClearHistory();
        Assert.Equal(0, db.Db.CloseUpSize().Count);
    }

    [Fact]
    public void Unreadable_data_is_no_close_up()
    {
        Assert.Empty(CloseUp.Unpack([1, 2, 3]));
        Assert.Empty(CloseUp.Unpack(null));
    }
}
