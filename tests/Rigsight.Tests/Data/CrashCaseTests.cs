using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;
using static Rigsight.Tests.Data.Make;

namespace Rigsight.Tests.Data;

/// <summary>
/// What is kept about a crash beside the crash itself, and the finding made from a history: stored once, never lost
/// to a later scan, read by an app that opens the database before the agent has added to it, and the same on every page.
/// </summary>
public sealed class CrashCaseTests
{
    private static readonly long T0 = U(2025, 3, 10, 10, 21);

    private static CrashEvent ShutOff(long ts, bool? button = false) => new()
    {
        Ts = ts, Kind = CrashKind.UnexpectedShutdown,
        Facts = button is null ? null : new CrashFacts { PowerButton = button, HardwareErrors = 0, NextStart = ts + 360 },
    };

    [Fact]
    public void Facts_are_stored_with_a_crash_and_given_to_one_stored_before_they_were_kept()
    {
        using var t = new TestDb();
        // Stored by a version that kept no facts.
        t.Db.InsertCrashes([new CrashEvent { Ts = T0, Kind = CrashKind.AppCrash, AppExe = "a.exe", Module = "a.dll", Code = "c0000005" }]);
        Assert.Null(t.Db.GetCrashes(0, T0 + 1).Single().Facts);

        // The fill-in after an update: nothing is added, the one stored gets what Windows still holds about it.
        var again = new CrashEvent { Ts = T0, Kind = CrashKind.AppCrash, AppExe = "a.exe", Module = "a.dll", Code = "c0000005", Facts = new CrashFacts { AppVersion = "1.2.3", Offset = "1a2b", RanSec = 12 } };
        var other = new CrashEvent { Ts = T0 + 50, Kind = CrashKind.AppCrash, AppExe = "b.exe", Facts = new CrashFacts { AppVersion = "9" } };
        Assert.Empty(t.Db.InsertCrashes([again, other], knownOnly: true));
        var stored = Assert.Single(t.Db.GetCrashes(0, T0 + 100));
        Assert.Equal(("1.2.3", "1a2b", 12d), (stored.Facts!.AppVersion, stored.Facts.Offset, stored.Facts.RanSec));

        // A later scan reads it again: what it had stays (the first reading is the nearest to the event), and what
        // Windows has written about it since (its report) is added.
        again.Facts = new CrashFacts { AppVersion = "9.9.9", ReportType = "BEX64" };
        Assert.Empty(t.Db.InsertCrashes([again]));
        var kept = t.Db.GetCrashes(0, T0 + 100).Single().Facts!;
        Assert.Equal(("1.2.3", "1a2b", "BEX64"), (kept.AppVersion, kept.Offset, kept.ReportType));
        // And a new one is stored with its facts.
        Assert.Single(t.Db.InsertCrashes([other]));
        Assert.Equal("9", t.Db.GetCrashes(T0 + 50, T0 + 51).Single().Facts!.AppVersion);
    }

    [Fact]
    public void A_pc_going_down_keeps_its_facts_through_being_placed_and_read_again()
    {
        using var t = new TestDb();
        for (int i = 60; i >= 1; i--) t.Db.WriteMinute(Minute(T0 - i * 60));
        var e = ShutOff(T0 - 1500);
        e.NextStart = T0 + 360; // Windows' own time is 25 minutes early; minutes were recorded until T0
        Assert.Single(t.Db.InsertCrashes([e]));
        var first = t.Db.GetCrashes(0, T0 + 1000).Single();
        Assert.Equal((T0, false), (first.Ts, first.Facts!.PowerButton));

        // Read from Windows' log again at the next start: still one, at the same time, with its facts.
        var again = ShutOff(T0 - 1500, button: true);
        again.NextStart = T0 + 360;
        Assert.Empty(t.Db.InsertCrashes([again]));
        var after = Assert.Single(t.Db.GetCrashes(0, T0 + 1000));
        Assert.Equal((T0, false), (after.Ts, after.Facts!.PowerButton));
    }

    [Fact]
    public void A_blue_screen_stored_before_windows_named_its_driver_gets_the_driver_at_the_next_scan()
    {
        using var t = new TestDb();
        // The first scan after the start: the crash's own entry is there, the driver's is not yet.
        var early = new CrashEvent { Ts = T0, Kind = CrashKind.SystemCrash, Code = "0x3B", NextStart = T0 + 300, Facts = new CrashFacts { PowerButton = false, HardwareErrors = 0, NextStart = T0 + 300 } };
        Assert.Single(t.Db.InsertCrashes([early]));
        Assert.Null(t.Db.GetCrashes(0, T0 + 1).Single().Facts!.Driver);
        var late = new CrashEvent { Ts = T0, Kind = CrashKind.SystemCrash, Code = "0x3B", NextStart = T0 + 300, Facts = new CrashFacts { PowerButton = false, HardwareErrors = 0, NextStart = T0 + 300, Driver = "RtUsbA64.sys", DriverName = "Realtek USB Audio Driver" } };
        Assert.Empty(t.Db.InsertCrashes([late]));
        var stored = Assert.Single(t.Db.GetCrashes(0, T0 + 1));
        Assert.Equal(("RtUsbA64.sys", "CONFIRMED"), (stored.Facts!.Driver, CrashCases.Of(t.Db, [stored], new RigsightSettings())[stored.Id].Chip));
    }

    [Fact]
    public void A_pc_going_down_stored_without_facts_gets_them_from_the_fill_in()
    {
        using var t = new TestDb();
        var bare = new CrashEvent { Ts = T0, Kind = CrashKind.UnexpectedShutdown, NextStart = T0 + 360 };
        Assert.Single(t.Db.InsertCrashes([bare]));
        var read = ShutOff(T0);
        read.NextStart = T0 + 360;
        Assert.Empty(t.Db.InsertCrashes([read], knownOnly: true));
        Assert.False(Assert.Single(t.Db.GetCrashes(0, T0 + 1)).Facts!.PowerButton);
    }

    [Fact]
    public void Signs_are_stored_once_read_by_time_and_pruned_with_the_rest()
    {
        using var t = new TestDb();
        var signs = new[] { new CrashSign(T0, SignKind.Drive, "A drive failed to read or write"), new CrashSign(T0 + 600, SignKind.Hardware, "The hardware reported an error it corrected") };
        t.Db.InsertSigns(signs);
        t.Db.InsertSigns(signs); // every scan reads the last half hour again
        // A different thing of the same kind in the same minute is kept beside the first.
        t.Db.InsertSigns([new CrashSign(T0, SignKind.Drive, "A drive has a bad block")]);
        Assert.Equal(2, t.Db.GetSigns(T0, T0 + 1).Count);
        t.Db.Prune(T0 + 1);
        t.Db.InsertSigns([signs[0]]);
        Assert.Equal([SignKind.Drive, SignKind.Hardware], t.Db.GetSigns(0, T0 + 1000).Select(s => s.Kind));
        Assert.Equal(SignKind.Hardware, Assert.Single(t.Db.GetSigns(T0 + 1, T0 + 1000)).Kind);
        t.Db.Prune(T0 + 100);
        Assert.Equal(SignKind.Hardware, Assert.Single(t.Db.GetSigns(0, T0 + 1000)).Kind);
    }

    [Fact]
    public void A_database_from_before_the_agent_added_to_it_still_reads()
    {
        // The app can open a database the newer agent hasn't touched yet: no facts column, no table of signs.
        using var t = new TestDb();
        t.Db.InsertCrashes([new CrashEvent { Ts = T0, Kind = CrashKind.AppCrash, AppExe = "a.exe" }]);
        t.Db.Dispose();
        using (var raw = t.Raw())
        {
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "ALTER TABLE crashes DROP COLUMN facts; DROP TABLE crash_signs;";
            cmd.ExecuteNonQuery();
        }
        using var reader = t.Reader();
        var crash = Assert.Single(reader.GetCrashes(0, T0 + 1));
        Assert.Null(crash.Facts);
        Assert.Empty(reader.GetSigns(0, T0 + 1));
        var finding = CrashCases.Of(reader, [crash], new RigsightSettings())[crash.Id];
        Assert.Equal(Confidence.NotEnough, finding.Confidence);
    }

    /// <summary>An hour of history: used until 35 minutes before T0, idle and cool from then on; recorded again six minutes after.</summary>
    private static TestDb IdleHistory()
    {
        var t = new TestDb();
        for (int i = 60; i >= 1; i--)
            t.Db.WriteMinute(Minute(T0 - i * 60, cpu: 47, gpu: 50, cpuLoad: 2, gpuLoad: 11, active: i > 35 ? 60 : 0, idle: i > 35 ? 0 : 60, gpuPower: 41, ram: 7.6));
        t.Db.WriteMinute(Minute(T0 + 360));
        return t;
    }

    [Fact]
    public void From_a_history_an_idle_shut_off_is_likely_a_power_cut_and_one_with_no_readings_is_not()
    {
        using var t = IdleHistory();
        var e = ShutOff(T0);
        t.Db.InsertCrashes([e, ShutOff(T0 + 5 * 86400)]);
        var crashes = t.Db.GetCrashes(0, T0 + 10 * 86400);
        var found = CrashCases.Of(t.Db, crashes, new RigsightSettings());
        var idle = found[crashes.Single(c => c.Ts == T0).Id];
        Assert.Equal(("LIKELY A POWER CUT", true), (idle.Chip, idle.Outside));
        Assert.Equal("Idle and cool for 35 minutes. Back on 6 minutes later.", idle.Line);
        var blind = found[crashes.Single(c => c.Ts != T0).Id];
        Assert.Equal(("POSSIBLY A POWER CUT OR A RESET", false), (blind.Chip, blind.Outside));
        // The later one has the earlier one as an earlier time; the earlier one has none.
        Assert.Contains(blind.Points, p => p.Title == "It has happened once before");
        Assert.DoesNotContain(idle.Points, p => p.Title.StartsWith("It has happened"));
    }

    [Fact]
    public void On_a_standard_account_with_no_cpu_temperature_the_finding_still_stands_and_says_only_what_was_read()
    {
        using var t = new TestDb();
        for (int i = 60; i >= 1; i--)
            t.Db.WriteMinute(Minute(T0 - i * 60, cpu: null, gpu: 50, cpuLoad: 2, gpuLoad: 11, active: 0, idle: 60));
        t.Db.InsertCrashes([ShutOff(T0)]);
        var crash = t.Db.GetCrashes(0, T0 + 1).Single();
        var f = CrashCases.Of(t.Db, [crash], new RigsightSettings())[crash.Id];
        Assert.Equal("LIKELY A POWER CUT", f.Chip);
        var heat = Assert.Single(f.RuledOut, p => p.Title == "The GPU wasn't overheating");
        Assert.Equal("The GPU was at 51°, under your alert limit. The CPU's temperature wasn't recorded, so this only covers the GPU.", heat.Detail);
        // No use anywhere in what was recorded: "for some time", never a figure nobody measured.
        Assert.Equal("The PC had been idle for some time, Windows logged no error, and it recorded no press of the power button.", f.Text);
        Assert.Contains(f.Points, p => p.Title == "Nobody was using it" && p.Detail.StartsWith("No use on record in the hours before."));
    }

    [Fact]
    public void A_days_report_says_the_finding_and_a_likely_power_cut_is_not_a_warning()
    {
        using var t = IdleHistory();
        t.Db.InsertCrashes([ShutOff(T0)]);
        var report = ReportBuilder.Build(t.Db, ReportRange.Day, L(T0), new RigsightSettings());
        var insight = Assert.Single(report.Insights, i => i.Key == "crash");
        Assert.Equal("The PC shut off suddenly at 10:21 AM, likely a power cut.", insight.Text);
        Assert.Equal(InsightTone.Neutral, insight.Tone);

        // One that is the PC's own (a held power button means it had frozen) stays a warning, and is said first.
        t.Db.InsertCrashes([ShutOff(T0 + 1800, button: true)]);
        report = ReportBuilder.Build(t.Db, ReportRange.Day, L(T0), new RigsightSettings());
        insight = Assert.Single(report.Insights, i => i.Key == "crash");
        Assert.StartsWith("The PC shut off suddenly at 10:51 AM: someone held the power button. 2 problems in all", insight.Text);
        Assert.Equal(InsightTone.Warn, insight.Tone);
    }
}
