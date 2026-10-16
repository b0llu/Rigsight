using Rigsight.Core.Data;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;
using Rigsight.Tests.Data;
using HangRecord = Rigsight.Core.Stability.CrashLogReader.HangRecord;

namespace Rigsight.Tests.Core;

/// <summary>
/// Graphics driver resets read from Windows' hang reports (LiveKernelEvent 141), as a real PC records them: most PCs
/// never write Windows' older display entry (4101), so before R2 their resets went uncounted. The times and addresses
/// here are the user's own, from 30 July and 22 September 2026.
/// </summary>
public sealed class DriverResetTests
{
    private static DateTime T(int month, int day, int h, int m, int s) => new(2026, month, day, h, m, s);
    private const string Bucket = "LKD_0x141_Tdr:6_IMAGE_nvlddmkm.sys_Ampere";

    [Fact]
    public void A_hang_written_up_twice_is_one_reset_at_its_first_record_naming_the_driver()
    {
        // 30 Jul: the quick record at the hang, the fuller one 21 seconds later, and the same hang written up again an hour on.
        var resets = CrashLogReader.ResetsFrom(
        [
            new HangRecord(T(7, 30, 12, 50, 5), "ffff8402385d5010", null, "141"),
            new HangRecord(T(7, 30, 12, 50, 26), "ffff8402385d5010", Bucket, "141"),
            new HangRecord(T(7, 30, 12, 50, 41), "ffff84025b14a2d0", null, "141"),
            new HangRecord(T(7, 30, 12, 50, 59), "ffff84025b14a2d0", Bucket, "141"),
            new HangRecord(T(7, 30, 13, 49, 14), "ffff84022b5d7010", Bucket, "141"),
            new HangRecord(T(7, 30, 12, 52, 31), "ffff84022b5d7010", null, "141"),
        ], [], []);

        Assert.Equal([T(7, 30, 12, 50, 5), T(7, 30, 12, 50, 41), T(7, 30, 12, 52, 31)], resets.Select(r => r.Time).Order());
        Assert.All(resets, r =>
        {
            Assert.Equal((CrashKind.GpuDriverReset, "", "nvlddmkm.sys", "141"), (r.Kind, r.AppExe, r.Module, r.Code));
            Assert.Equal(("nvlddmkm.sys", "LiveKernelEvent 141"), (r.Facts!.Driver, r.Facts.ReportType));
            Assert.False(string.IsNullOrEmpty(r.Facts.HangKey));
        });
    }

    [Fact]
    public void A_hang_written_up_just_after_a_sudden_restart_is_not_a_reset_of_its_own()
    {
        // 22 Sep: the card hung at 9:47:09, the PC went down, Kernel-Power 41 at 9:47:35, the report at 9:48:32.
        var resets = CrashLogReader.ResetsFrom([new HangRecord(T(9, 22, 9, 48, 32), "ffff818898ba71c0", Bucket, "141")], [T(9, 22, 9, 47, 35)], []);
        Assert.Empty(resets);
        // 13 Jul: the quick record at 6:54:21 while still running, the PC down and starting again at 6:56:56.
        Assert.Empty(CrashLogReader.ResetsFrom([new HangRecord(T(7, 13, 6, 54, 21), "ffff9f844b066010", null, "141")], [T(7, 13, 6, 56, 56)], []));
        // A recovered hang hours after a start is a reset.
        Assert.Single(CrashLogReader.ResetsFrom([new HangRecord(T(9, 22, 13, 0, 0), "a", Bucket, "141")], [T(9, 22, 9, 47, 35)], []));
    }

    [Fact]
    public void A_hang_Windows_also_logged_the_usual_way_is_counted_once()
    {
        var resets = CrashLogReader.ResetsFrom([new HangRecord(T(7, 30, 9, 36, 48), "a", Bucket, "141")], [], [T(7, 30, 9, 36, 40)]);
        Assert.Empty(resets);
    }

    [Theory]
    [InlineData("LKD_0x141_Tdr:6_IMAGE_nvlddmkm.sys_Ampere", "nvlddmkm.sys")]
    [InlineData("LKD_0x141_Tdr:C_IMAGE_amdkmdag.sys_Navi", "amdkmdag.sys")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("LKD_0x141_UNKNOWN", null)]
    public void The_driver_is_read_from_the_reports_bucket(string? bucket, string? driver) => Assert.Equal(driver, CrashLogReader.DriverOf(bucket));

    [Fact]
    public void A_reports_own_time_for_the_hang_is_read_from_its_EventTime()
    {
        var hung = new DateTime(2026, 9, 22, 9, 47, 9);
        Assert.Equal(hung, CrashLogReader.EventTimeOf(["Version=1", "EventType=LiveKernelEvent", $"EventTime={hung.ToFileTime()}", "ReportType=4"]));
        Assert.Null(CrashLogReader.EventTimeOf(["Version=1"]));
        Assert.Null(CrashLogReader.EventTimeOf(["EventTime=x"]));
    }

    [Fact]
    public void The_same_hang_written_up_again_later_is_not_stored_twice()
    {
        using var t = new TestDb("resets");
        var first = CrashLogReader.ResetsFrom([new HangRecord(T(7, 30, 12, 52, 31), "ffff84022b5d7010", null, "141")], [], []);
        Assert.Single(t.Db.InsertCrashes(first));
        // An hour on, only the late write-up is in the scan's window: the same hang, with the driver it names added.
        var again = CrashLogReader.ResetsFrom([new HangRecord(T(7, 30, 13, 49, 14), "ffff84022b5d7010", Bucket, "141")], [], []);
        Assert.Empty(t.Db.InsertCrashes(again));
        var stored = Assert.Single(t.Db.GetCrashes(0, long.MaxValue / 2));
        Assert.Equal((T(7, 30, 12, 52, 31), "nvlddmkm.sys"), (stored.Time, stored.Facts!.Driver));
    }

    // ── What a reset says ──

    private static CrashInput Input(CrashEvent e, string? front = null, IReadOnlyList<CrashSign>? signs = null) =>
        new(e, CrashExplainer.Explain(e), new AlertSettings()) { FrontApp = front, Signs = signs ?? [] };

    private static CrashEvent Reset(DateTime at) => CrashLogReader.ResetsFrom([new HangRecord(at, "k", Bucket, "141")], [], [])[0];

    [Fact]
    public void A_reset_from_a_hang_report_is_confirmed_and_names_the_driver_and_the_game()
    {
        var f = CrashFindings.Judge(Input(Reset(T(10, 16, 18, 10, 22)), front: "Dota 2"));
        Assert.Equal((Confidence.Named, "CONFIRMED"), (f.Confidence, f.Chip));
        Assert.Equal("Windows reset the graphics driver while Dota 2 was in front.", f.Line);
        Assert.Contains(f.Points, p => p.Title == "Windows reported that the GPU stopped answering" && p.Detail.StartsWith("Its report names nvlddmkm.sys"));
        Assert.StartsWith("If it keeps happening in Dota 2, lower its graphics settings", f.Advice);
        Assert.Contains("power", f.CantTell);
    }

    [Fact]
    public void A_reset_from_Windows_display_entry_still_reads_as_before()
    {
        var e = new CrashEvent { Id = 1, Ts = TimeUtil.ToUnix(T(10, 16, 18, 10, 22)), Kind = CrashKind.GpuDriverReset, Module = "nvlddmkm", Detail = "Display driver stopped responding and has recovered" };
        var f = CrashFindings.Judge(Input(e));
        Assert.Contains(f.Points, p => p.Title == "Windows restarted the graphics driver");
        Assert.StartsWith("Update or clean-reinstall the graphics driver", f.Advice);
    }

    // ── A shut-off right after the card hung ──

    private static CrashEvent ShutOff(DateTime lastRunning, DateTime nextStart) => new()
    {
        Id = 2, Ts = TimeUtil.ToUnix(lastRunning), Kind = CrashKind.UnexpectedShutdown,
        Facts = new CrashFacts { PowerButton = false, HardwareErrors = 0, NextStart = TimeUtil.ToUnix(nextStart) },
    };

    private static CrashContext Gaming => new(63, 74, null, null) { Minutes = 10, CpuLoad = 30, GpuLoad = 88, GpuPower = 285, LastActive = TimeUtil.ToUnix(T(9, 22, 9, 46, 0)) };

    [Fact]
    public void A_shut_off_seconds_after_Windows_recorded_the_card_hanging_is_likely_the_card()
    {
        // 22 Sep: last whole minute recorded 9:46 (the shut-off is given 9:47:00), hang at 9:47:09, next start 9:47:24.
        var e = ShutOff(T(9, 22, 9, 47, 0), T(9, 22, 9, 47, 24));
        var signs = new List<CrashSign>
        {
            new(TimeUtil.ToUnix(T(9, 22, 9, 47, 9)), SignKind.GraphicsHangAt, "The graphics card hung"),
            new(TimeUtil.ToUnix(T(9, 22, 9, 48, 0)), SignKind.GraphicsHang, "Windows filed a report that the graphics card stopped answering"),
        };
        var f = CrashFindings.Judge(new CrashInput(e, CrashExplainer.Explain(e), new AlertSettings()) { Context = Gaming, FrontApp = "Dota 2", Signs = signs });
        Assert.Equal((Confidence.Likely, "LIKELY THE GPU HUNG"), (f.Confidence, f.Chip));
        Assert.Contains("9:47:09 AM", f.Text);
        Assert.Contains("9:47:24 AM", f.Text);
        Assert.StartsWith("The GPU hung at 9:47:09 AM in Dota 2, then the PC shut off.", f.Line);
        Assert.Contains(f.Points, p => p.Kind == "warn" && p.Title == "Windows recorded the GPU hanging at 9:47:09 AM");
    }

    [Fact]
    public void Without_the_hangs_own_time_a_standard_account_keeps_it_possible()
    {
        // A standard account can't read the report's own time: only that one was filed at the next start.
        var e = ShutOff(T(9, 22, 9, 47, 0), T(9, 22, 9, 47, 24));
        var signs = new List<CrashSign> { new(TimeUtil.ToUnix(T(9, 22, 9, 48, 0)), SignKind.GraphicsHang, "Windows filed a report that the graphics card stopped answering") };
        var f = CrashFindings.Judge(new CrashInput(e, CrashExplainer.Explain(e), new AlertSettings()) { Context = Gaming, FrontApp = "Dota 2", Signs = signs });
        Assert.Equal((Confidence.Possible, "POSSIBLY THE GPU HUNG"), (f.Confidence, f.Chip));
    }

    [Fact]
    public void A_shut_off_filed_under_Windows_stale_time_is_still_put_down_to_the_hang_just_before_the_restart()
    {
        // 22 Sep as stored before R2: Windows' last-known time was 23:07 the night before; hang 9:47:09, restart 9:47:35.
        var e = ShutOff(T(9, 21, 23, 7, 49), T(9, 22, 9, 47, 35));
        var signs = new List<CrashSign> { new(TimeUtil.ToUnix(T(9, 22, 9, 47, 9)), SignKind.GraphicsHangAt, "The graphics card hung") };
        var f = CrashFindings.Judge(new CrashInput(e, CrashExplainer.Explain(e), new AlertSettings()) { Signs = signs });
        Assert.Equal("THE GPU HUNG", f.Cause);
        // A hang hours before the restart says nothing about this shut-off.
        var early = new List<CrashSign> { new(TimeUtil.ToUnix(T(9, 22, 2, 0, 0)), SignKind.GraphicsHangAt, "The graphics card hung") };
        Assert.NotEqual("THE GPU HUNG", CrashFindings.Judge(new CrashInput(e, CrashExplainer.Explain(e), new AlertSettings()) { Signs = early }).Cause);
    }

    [Fact]
    public void A_shut_off_is_moved_to_the_hang_the_PC_was_running_at()
    {
        using var t = new TestDb("hang-place");
        long hung = TimeUtil.ToUnix(T(9, 22, 9, 47, 9)), next = TimeUtil.ToUnix(T(9, 22, 9, 47, 35)), stale = TimeUtil.ToUnix(T(9, 21, 23, 7, 49));
        t.Db.InsertSigns([new CrashSign(hung, SignKind.GraphicsHangAt, "The graphics card hung")]);
        t.Db.InsertCrashes([new CrashEvent { Ts = stale, WindowsTs = stale, Kind = CrashKind.UnexpectedShutdown, AppExe = "", NextStart = next, Facts = new CrashFacts { NextStart = next } }]);
        Assert.Equal(hung, Assert.Single(t.Db.GetCrashes(0, long.MaxValue / 2)).Ts);
    }

    [Fact]
    public void A_hang_the_card_recovered_from_minutes_earlier_is_not_put_down_as_the_shut_offs_cause()
    {
        var e = ShutOff(T(9, 22, 9, 47, 0), T(9, 22, 9, 47, 24));
        var signs = new List<CrashSign> { new(TimeUtil.ToUnix(T(9, 22, 9, 40, 0)), SignKind.GraphicsHangAt, "The graphics card hung") };
        var f = CrashFindings.Judge(new CrashInput(e, CrashExplainer.Explain(e), new AlertSettings()) { Context = Gaming, FrontApp = "Dota 2", Signs = signs });
        Assert.NotEqual("THE GPU HUNG", f.Cause);
    }
}
