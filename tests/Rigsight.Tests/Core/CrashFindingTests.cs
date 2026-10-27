using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Tests.Core;

/// <summary>
/// What a crash's finding says, and how sure, for recorded cases: each rule where it should fire, where it is at its
/// border, where what it needs is missing, and where the evidence pulls two ways. Several are crashes from a real PC
/// (October 2026), with the figures its history and Windows' logs held.
/// </summary>
public sealed class CrashFindingTests
{
    private static readonly DateTime At = new(2026, 10, 12, 10, 21, 0);
    private static readonly AlertSettings Alerts = new();

    private static CrashInput Input(CrashEvent e, CrashContext? context = null, string? name = null, string? front = null) =>
        new(e, CrashExplainer.Explain(e, name), Alerts) { AppName = name, Context = context, FrontApp = front, RamTotal = 16 };

    private static CrashEvent ShutOff(bool? button = false, int? hardware = 0, DateTime? at = null) => new()
    {
        Id = 1, Ts = TimeUtil.ToUnix(at ?? At), Kind = CrashKind.UnexpectedShutdown,
        Facts = button is null ? null : new CrashFacts { PowerButton = button, HardwareErrors = hardware, NextStart = TimeUtil.ToUnix((at ?? At).AddMinutes(5)) },
    };

    private static CrashContext Idle(double cpu = 47, double gpu = 50) => new(cpu, gpu, null, null)
    {
        Minutes = 10, CpuLoad = 2, GpuLoad = 11, GpuPower = 41, RamUsed = 7.6,
        LastActive = TimeUtil.ToUnix(At.AddMinutes(-35)), BackOn = TimeUtil.ToUnix(At.AddMinutes(6)),
    };

    private static CrashContext Gaming(double gpu = 68) => new(57, gpu, null, null) { Minutes = 10, CpuLoad = 27, GpuLoad = 95, GpuPower = 292, RamUsed = 11.9, LastActive = TimeUtil.ToUnix(At) };

    // ── The PC went off ──

    [Fact]
    public void An_idle_cool_pc_that_went_off_with_no_button_held_is_likely_a_power_cut()
    {
        // 12 Oct 2026, 10:21 AM: idle since 9:47, CPU 47°, GPU 50°, back on six minutes later.
        var f = CrashFindings.Judge(Input(ShutOff(), Idle()) with { Rest = new LoadTemps(49, 48, 3700) });
        Assert.Equal((Confidence.Likely, "LIKELY A POWER CUT", true), (f.Confidence, f.Chip, f.Outside));
        Assert.Equal("Most likely a power cut.", f.Head);
        Assert.Equal("The PC had been idle for 34 minutes, it was cool, Windows logged no error, and it recorded no press of the power button. It was back on 6 minutes later.", f.Text);
        // A row's line: what it was doing and when it came back. The rest is in the case.
        Assert.Equal("Idle and cool for 34 minutes. Back on 6 minutes later.", f.Line);
        Assert.Equal("The PC shut off suddenly, likely a power cut.", f.Told());
        Assert.Equal("Likely a power cut.", f.Verdict);
        Assert.Contains(f.Points, p => p.Title == "Nobody held the power button");
        Assert.Contains(f.Points, p => p.Title == "Nobody was using it" && p.Detail == "Idle since 9:47 AM. CPU at 2%, GPU at 11%.");
        Assert.Contains(f.Points, p => p.Title == "It came back on at 10:27 AM");
        Assert.Contains(f.RuledOut, p => p is { Title: "It wasn't overheating", Detail: "The CPU was at 47° and the GPU at 50°, close to the 49° and 48° this PC sits at when idle." });
        Assert.Contains(f.RuledOut, p => p is { Title: "Memory wasn't full", Detail: "7.6 of 16 GB was in use." });
        Assert.Contains(f.RuledOut, p => p.Title == "It wasn't a blue screen or a hardware fault");
        Assert.Contains("failing power supply", f.CantTell);
        Assert.Contains("unstable", f.CantTell); // the other thing that looks the same is always said
        Assert.Empty(f.NotChecked);
    }

    [Fact]
    public void A_warm_cpu_at_light_load_is_not_called_close_to_idle_or_cool()
    {
        // 26 Oct 2026, 11:24 AM on the real PC: CPU 78°, GPU 53° at light load said "close to the 49° and 48° this PC sits at
        // when idle" and "it was cool".
        var f = CrashFindings.Judge(Input(ShutOff(), Idle(cpu: 78, gpu: 53) with { LastActive = TimeUtil.ToUnix(At) }) with { Rest = new LoadTemps(49, 48, 3700) });
        Assert.Contains(f.RuledOut, p => p is { Title: "It wasn't overheating", Detail: "The CPU was at 78° and the GPU at 53°, under your alert limits." });
        Assert.DoesNotContain("cool", f.Text);
        Assert.DoesNotContain("cool", f.Line);
    }

    [Fact]
    public void A_pc_that_is_running_again_within_two_minutes_is_not_called_a_power_cut()
    {
        // Back that fast, it lost power for a moment or reset itself (an unstable processor setting does that at idle).
        var quick = CrashFindings.Judge(Input(ShutOff(), Idle() with { BackOn = TimeUtil.ToUnix(At.AddMinutes(2)) }));
        Assert.Equal((Confidence.Possible, "POSSIBLY A POWER DIP OR A RESET", false), (quick.Confidence, quick.Chip, quick.Outside));
        Assert.Equal("It shut off and was running again 2 minutes later.", quick.Head);
        Assert.Contains("Curve Optimizer", quick.Advice);
        // Three minutes, and it is the mains' usual way again.
        Assert.Equal("LIKELY A POWER CUT", CrashFindings.Judge(Input(ShutOff(), Idle() with { BackOn = TimeUtil.ToUnix(At.AddMinutes(3)) })).Chip);
        // Never started again in what was recorded: nothing against a cut.
        Assert.Equal("LIKELY A POWER CUT", CrashFindings.Judge(Input(ShutOff(), Idle() with { BackOn = null })).Chip);
    }

    [Fact]
    public void Trouble_on_record_just_before_takes_the_likely_power_cut_away()
    {
        var e = ShutOff();
        var drive = new CrashSign(e.Ts - 300, SignKind.Drive, "A drive failed to read or write");
        var f = CrashFindings.Judge(Input(e, Idle()) with { Signs = [drive] });
        Assert.Equal((Confidence.Possible, "POSSIBLY A POWER CUT OR A RESET", false), (f.Confidence, f.Chip, f.Outside));
        Assert.Contains(f.Points, p => p.Title == "A drive failed to read or write" && p.Detail == "5 minutes before the shut-off.");
        // Memory nearly full, or a driver this began with, do the same.
        Assert.False(CrashFindings.Judge(Input(e, Idle() with { RamUsed = 15.5 })).Outside);
        var driver = new SystemChange(At.AddDays(-1), ChangeKind.Driver, "NVIDIA graphics driver 617.42") { Subject = "nvidia-graphics" };
        Assert.False(CrashFindings.Judge(Input(e, Idle()) with { Changes = [driver] }).Outside);
        // A driver it was already happening before is no trouble: still the mains.
        Assert.True(CrashFindings.Judge(Input(e, Idle()) with { Changes = [driver], Earlier = [ShutOff(at: At.AddDays(-4))] }).Outside);
    }

    [Fact]
    public void An_error_the_hardware_corrected_is_shown_but_does_not_decide_the_finding()
    {
        // Corrected errors are logged all day on some healthy PCs.
        var e = ShutOff();
        var corrected = new CrashSign(e.Ts - 120, SignKind.Hardware, CrashLogReader.SignOf("Microsoft-Windows-WHEA-Logger", 17)!.Value.Detail);
        var f = CrashFindings.Judge(Input(e, Gaming()) with { Signs = [corrected] });
        Assert.Equal("POSSIBLY MAINS OR POWER SUPPLY", f.Chip);
        Assert.Contains(f.Points, p => p.Title == "The hardware reported an error it corrected");
        var fatal = new CrashSign(e.Ts - 120, SignKind.Hardware, CrashLogReader.SignOf("Microsoft-Windows-WHEA-Logger", 18)!.Value.Detail);
        Assert.Equal("LIKELY A HARDWARE FAULT", CrashFindings.Judge(Input(e, Gaming()) with { Signs = [fatal] }).Chip);
    }

    [Fact]
    public void A_load_that_was_not_read_is_not_a_low_load()
    {
        var blind = new CrashContext(47, 50, null, null) { Minutes = 10 };
        var f = CrashFindings.Judge(Input(ShutOff(), blind));
        Assert.NotEqual("A POWER CUT", f.Cause);
        Assert.False(f.Outside);
    }

    [Fact]
    public void Going_off_under_heavy_load_is_only_possible_and_stays_a_problem_of_the_pc()
    {
        // 7 Oct 2026, 5:21 PM: No Man's Sky, the card at 95% and 292 W.
        var f = CrashFindings.Judge(Input(ShutOff(), Gaming(), front: "No Man's Sky"));
        Assert.Equal((Confidence.Possible, "POSSIBLY MAINS OR POWER SUPPLY", false), (f.Confidence, f.Chip, f.Outside));
        Assert.Equal("In No Man's Sky, GPU drawing 292 W.", f.Line);
        Assert.StartsWith("It shut off in No Man's Sky with the GPU drawing 292 W. Windows recorded no press", f.Text);
        // Where the card's power isn't read, the sentence still reads as one.
        Assert.StartsWith("It shut off in No Man's Sky. Windows", CrashFindings.Judge(Input(ShutOff(), Gaming() with { GpuPower = null }, front: "No Man's Sky")).Text);
        Assert.Equal("The PC shut off suddenly, possibly the mains or the power supply.", f.Told());
        Assert.Contains(f.Points, p => p.Title == "It was working hard");
    }

    [Fact]
    public void A_load_of_thirty_percent_is_no_longer_a_pc_doing_little()
    {
        Assert.Equal("A POWER CUT", CrashFindings.Judge(Input(ShutOff(), Idle() with { GpuLoad = 29.9 })).Cause);
        Assert.Equal("MAINS OR POWER SUPPLY", CrashFindings.Judge(Input(ShutOff(), Idle() with { GpuLoad = 30 })).Cause);
    }

    [Fact]
    public void A_held_power_button_is_named_by_windows_whatever_else_is_known()
    {
        var f = CrashFindings.Judge(Input(ShutOff(button: true), Idle(), front: "Blender"));
        Assert.Equal((Confidence.Named, "CONFIRMED", false), (f.Confidence, f.Chip, f.Outside));
        Assert.Equal("It was switched off by holding the power button.", f.Head);
        Assert.Equal("Switched off by holding the power button, with Blender in front.", f.Line);
    }

    [Fact]
    public void With_nothing_from_windows_and_nothing_recorded_it_says_it_cannot_say()
    {
        var f = CrashFindings.Judge(Input(ShutOff(button: null)));
        Assert.Equal((Confidence.NotEnough, "NOT ENOUGH TO SAY", "Not enough on record to say why."), (f.Confidence, f.Chip, f.Verdict));
        Assert.False(f.Outside);
        // What wasn't recorded is not "ruled out": it stands in its own list.
        Assert.Contains(f.NotChecked, p => p is { Title: "Temperatures weren't recorded", Detail: "Nothing was being recorded in those minutes." });
        Assert.DoesNotContain(f.RuledOut, p => p.Title.Contains("overheating"));
    }

    [Fact]
    public void Heat_over_the_limit_is_never_listed_as_ruled_out_whatever_the_finding()
    {
        // A held power button decides the finding; the 95° beside it is still a lead, not something cleared.
        var f = CrashFindings.Judge(Input(ShutOff(button: true), Gaming(gpu: 95)));
        Assert.Equal(Confidence.Named, f.Confidence);
        Assert.Contains(f.Points, p => p.Title == "It was running hot");
        Assert.DoesNotContain(f.RuledOut, p => p.Title.Contains("overheating"));
        Assert.All(f.RuledOut, p => Assert.Equal("ok", p.Kind));
    }

    [Fact]
    public void Without_the_power_button_on_record_nothing_is_said_about_it()
    {
        var e = ShutOff();
        e.Facts!.PowerButton = null; // the field is missing from Windows' entry
        var f = CrashFindings.Judge(Input(e, new CrashContext(null, null, null, null, NoReadings: true)));
        Assert.DoesNotContain("button", f.Text + f.Line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(f.Points, p => p.Kind == "power");
    }

    [Fact]
    public void Idle_without_the_power_button_on_record_is_not_called_a_power_cut()
    {
        // An older Windows, or a crash stored before the button was read: a freeze switched off by hand can't be told apart.
        var f = CrashFindings.Judge(Input(ShutOff(button: null), Idle()));
        Assert.Equal((Confidence.Possible, "POSSIBLY A POWER CUT OR A FREEZE", false), (f.Confidence, f.Chip, f.Outside));
    }

    [Fact]
    public void No_readings_but_no_button_held_is_possible_power_lost()
    {
        var f = CrashFindings.Judge(Input(ShutOff(), new CrashContext(null, null, null, null, NoReadings: true)));
        Assert.Equal((Confidence.Possible, "POSSIBLY A POWER CUT OR A RESET"), (f.Confidence, f.Chip));
        Assert.Contains("Nothing was being recorded", f.Line);
    }

    [Fact]
    public void Heat_is_judged_by_the_users_own_alert_limits()
    {
        // 84° is over the default 83° limit: possible heat. With the limit at 90° the same reading is ruled out.
        var hot = CrashFindings.Judge(Input(ShutOff(), Gaming(gpu: 84)));
        Assert.Equal("POSSIBLY HEAT", hot.Chip);
        Assert.Contains(hot.Points, p => p.Title == "It was running hot" && p.Detail.Contains("above your alert limit of 83°"));
        var relaxed = Input(ShutOff(), Gaming(gpu: 84)) with { Alerts = new AlertSettings { GpuLimit = 90 } };
        Assert.Equal("POSSIBLY MAINS OR POWER SUPPLY", CrashFindings.Judge(relaxed).Chip);
        Assert.Contains(CrashFindings.Judge(relaxed).RuledOut, p => p is { Title: "It wasn't overheating", Detail: "The CPU was at 57° and the GPU at 84°, under your alert limits." });
    }

    [Fact]
    public void With_only_one_temperature_read_heat_is_cleared_for_that_part_only_and_the_pc_is_not_called_cool()
    {
        // A standard account reads no CPU temperature.
        var f = CrashFindings.Judge(Input(ShutOff(), Idle() with { CpuBefore = null }));
        var heat = Assert.Single(f.RuledOut, p => p.Title == "The GPU wasn't overheating");
        Assert.Equal("The GPU was at 50°, under your alert limit. The CPU's temperature wasn't recorded, so this only covers the GPU.", heat.Detail);
        Assert.DoesNotContain("cool", f.Text + f.Line);
        Assert.Equal("LIKELY A POWER CUT", f.Chip);
    }

    [Fact]
    public void A_graphics_hang_report_filed_at_the_next_start_makes_it_a_possible_hang_not_a_power_cut()
    {
        // 21 Sep 2026: went off at 11:07 PM; Windows filed a graphics-hang report a minute into the next start.
        var e = ShutOff();
        var sign = new CrashSign(e.Facts!.NextStart!.Value + 60, SignKind.GraphicsHang, "Windows filed a report that the graphics card stopped answering");
        var f = CrashFindings.Judge(Input(e, Idle()) with { Signs = [sign] });
        Assert.Equal((Confidence.Possible, "POSSIBLY THE GPU HUNG", false), (f.Confidence, f.Chip, f.Outside));
        Assert.Contains("the time it was filed, not the time of the hang", f.CantTell);
        // The same report from the day before has nothing to do with it.
        var old = Input(e, Idle()) with { Signs = [sign with { Ts = e.Ts - 86400 }] };
        Assert.Equal("LIKELY A POWER CUT", CrashFindings.Judge(old).Chip);
    }

    [Fact]
    public void A_hardware_error_at_the_next_start_outweighs_an_idle_pc()
    {
        var f = CrashFindings.Judge(Input(ShutOff(hardware: 2), Idle()));
        Assert.Equal((Confidence.Likely, "LIKELY A HARDWARE FAULT", false), (f.Confidence, f.Chip, f.Outside));
        Assert.DoesNotContain(f.RuledOut, p => p.Title == "It wasn't a blue screen or a hardware fault");
    }

    [Fact]
    public void Going_off_while_shutting_down_or_asleep_is_named_and_outside()
    {
        var e = ShutOff();
        e.Moment = PowerMoment.ShuttingDown;
        var f = CrashFindings.Judge(Input(e));
        Assert.Equal((Confidence.Named, true, "The PC shut off suddenly as Windows was already shutting down."), (f.Confidence, f.Outside, f.Told()));
        e.Moment = PowerMoment.Asleep;
        Assert.Equal((Confidence.Named, true, "The PC shut off suddenly while it was asleep."), (CrashFindings.Judge(Input(e)).Confidence, CrashFindings.Judge(Input(e)).Outside, CrashFindings.Judge(Input(e)).Told()));
    }

    // ── A new driver is a lead only when the problem is new ──

    [Fact]
    public void A_driver_is_ruled_out_when_the_problem_was_happening_before_it_and_a_lead_when_it_was_not()
    {
        var driver = new SystemChange(At.AddDays(-5), ChangeKind.Driver, "NVIDIA graphics driver 617.42") { Subject = "nvidia-graphics" };
        var earlier = Enumerable.Range(1, 3).Select(i => ShutOff(at: At.AddDays(-5 - i))).ToList();
        var before = CrashFindings.Judge(Input(ShutOff(), Idle()) with { Changes = [driver], Earlier = earlier });
        Assert.Contains(before.RuledOut, p => p.Title == "NVIDIA graphics driver 617.42 didn't cause it" && p.Detail == "It was installed 5 days before. This had already happened 3 times in the 30 days before it, and once since.");
        var fresh = CrashFindings.Judge(Input(ShutOff(), Idle()) with { Changes = [driver] });
        Assert.Contains(fresh.Points, p => p.Title == "NVIDIA graphics driver 617.42 was installed 5 days before" && p.Detail == "This never happened before it.");
        // No record of changes reaching back that far: nothing is said either way.
        var unknown = CrashFindings.Judge(Input(ShutOff(), Idle()));
        Assert.DoesNotContain(unknown.RuledOut.Concat(unknown.Points), p => p.Title.Contains("driver", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(CrashFindings.Judge(Input(ShutOff(), Idle()) with { Changes = [] }).RuledOut, p => p is { Title: "Nothing new was installed", Detail: "No new driver or Windows update in the 7 days before." });
    }

    // ── Windows crashed ──

    [Fact]
    public void A_blue_screen_whose_driver_windows_names_says_the_driver()
    {
        // 9 Sep 2026: SYSTEM_SERVICE_EXCEPTION in RtUsbA64.sys. Rigsight had said "update your GPU drivers".
        var e = new CrashEvent { Id = 1, Ts = TimeUtil.ToUnix(At), Kind = CrashKind.SystemCrash, Code = "0x3B", Facts = new CrashFacts { Driver = "RtUsbA64.sys", DriverName = "Realtek USB Audio Driver", HardwareErrors = 0 } };
        var f = CrashFindings.Judge(Input(e));
        Assert.Equal((Confidence.Named, "CONFIRMED"), (f.Confidence, f.Chip));
        Assert.Equal("Windows crashed inside RtUsbA64.sys, the Realtek USB audio driver.", f.Head);
        Assert.Equal("Windows crashed inside the Realtek USB audio driver (RtUsbA64.sys).", f.Line);
        Assert.StartsWith("Update or reinstall the Realtek USB audio driver.", f.Advice);
        Assert.Contains(f.RuledOut, p => p.Title == "It wasn't the graphics driver");
        Assert.Contains("Nothing was being recorded", f.CantTell);
    }

    [Fact]
    public void A_known_driver_file_is_called_by_its_plain_name_and_a_file_with_no_description_by_its_own()
    {
        CrashFinding Of(string file, string? name) => CrashFindings.Judge(Input(new CrashEvent { Kind = CrashKind.SystemCrash, Code = "0x116", Facts = new CrashFacts { Driver = file, DriverName = name } }));
        // The file's own description carries today's version, which says nothing of a crash from months ago.
        Assert.Equal("Windows crashed inside nvlddmkm.sys, the NVIDIA driver.", Of("nvlddmkm.sys", "NVIDIA Windows Kernel Mode Driver").Head);
        Assert.Equal("Windows crashed inside mystery.sys.", Of("mystery.sys", null).Head);
        Assert.StartsWith("Update or reinstall the driver that file belongs to.", Of("mystery.sys", null).Advice);
    }

    [Fact]
    public void Windows_own_core_named_for_a_blue_screen_is_not_a_cause()
    {
        var e = new CrashEvent { Kind = CrashKind.SystemCrash, Code = "0x133", Facts = new CrashFacts { Driver = "ntoskrnl.exe" } };
        var f = CrashFindings.Judge(Input(e));
        Assert.Equal((Confidence.Possible, "POSSIBLY", "Windows crashed with a blue screen."), (f.Confidence, f.Chip, f.Told()));
        Assert.Contains("Windows didn't name one", f.CantTell);
        // A hardware error's own code is likely hardware.
        Assert.Equal("LIKELY A HARDWARE FAULT", CrashFindings.Judge(Input(new CrashEvent { Kind = CrashKind.SystemCrash, Code = "0x124" })).Chip);
    }

    // ── One app ──

    [Fact]
    public void A_frozen_app_that_windows_says_was_waiting_on_another_names_it()
    {
        // 2 Sep 2026: Explorer waiting on Wallpaper Engine.
        var e = new CrashEvent { Kind = CrashKind.AppHang, AppExe = "explorer.exe", Facts = new CrashFacts { ReportType = "AppHangXProcB1", WaitedOn = "wallpaper64.exe", WaitedOnVersion = "2.8.0.42" } };
        var f = CrashFindings.Judge(Input(e, name: "Windows Explorer") with { NameOf = exe => exe == "wallpaper64.exe" ? "Wallpaper Engine" : exe });
        Assert.Equal((Confidence.Likely, "LIKELY", "Windows Explorer was stuck waiting on Wallpaper Engine."), (f.Confidence, f.Chip, f.Head));
        Assert.Contains(f.Points, p => p.Title == "Windows Explorer was waiting on wallpaper64.exe" && p.Detail == "Windows recorded this in its report (wallpaper64.exe version 2.8.0.42).");
        // Without that, nothing is guessed.
        var plain = CrashFindings.Judge(Input(new CrashEvent { Kind = CrashKind.AppHang, AppExe = "game.exe" }, name: "Game"));
        Assert.Equal((Confidence.NotEnough, "Game froze. Not enough on record to say why."), (plain.Confidence, plain.Told()));
    }

    [Fact]
    public void A_frozen_app_with_memory_nearly_full_is_possible_not_likely()
    {
        var e = new CrashEvent { Kind = CrashKind.AppHang, AppExe = "game.exe" };
        var f = CrashFindings.Judge(Input(e, new CrashContext(60, 60, null, null) { Minutes = 10, RamUsed = 15.2 }, "Game"));
        Assert.Equal("POSSIBLY OUT OF MEMORY", f.Chip);
        // A figure above what is installed is a reading not to be trusted: nothing is said of memory.
        var odd = CrashFindings.Judge(Input(e, new CrashContext(60, 60, null, null) { Minutes = 10, RamUsed = 20.8 }, "Game"));
        Assert.DoesNotContain(odd.Points.Concat(odd.RuledOut), p => p.Title.Contains("emory"));
    }

    [Fact]
    public void An_app_that_stops_twice_as_it_starts_is_likely_its_own_start_up()
    {
        // 25 Sep 2026: Rocket League at 11:44:54 PM and again nine seconds later.
        long ts = TimeUtil.ToUnix(new DateTime(2026, 9, 25, 23, 45, 3));
        var first = new CrashEvent { Id = 1, Ts = ts - 9, Kind = CrashKind.AppCrash, AppExe = "RocketLeague.exe", Module = "unknown", Code = "c0000005" };
        var e = new CrashEvent { Id = 2, Ts = ts, Kind = CrashKind.AppCrash, AppExe = "RocketLeague.exe", Module = "unknown", Code = "00000000", Facts = new CrashFacts { AppVersion = "0.0.0.0", ReportType = "BEX64", RanSec = 4 } };
        var f = CrashFindings.Judge(Input(e, new CrashContext(47, 44, null, 0) { Minutes = 10, CpuLoad = 10, GpuLoad = 12 }, "Rocket League") with { Earlier = [first] });
        Assert.Equal((Confidence.Likely, "LIKELY AT START-UP"), (f.Confidence, f.Chip));
        Assert.Equal("Rocket League crashed as it was starting, twice in 9 seconds.", f.Head);
        Assert.Contains(f.Points, p => p.Title == "Windows stopped it for touching memory it shouldn't");
        Assert.Contains(f.Points, p => p.Title == "Windows couldn't tell which file it crashed in");
        Assert.Contains(f.Points, p => p.Title == "It crashed 4 seconds after starting");
        // The time nine seconds earlier is this time, not "it has happened before".
        Assert.DoesNotContain(f.Points, p => p.Title.StartsWith("It has happened"));
    }

    [Fact]
    public void Only_windows_own_start_time_says_an_app_failed_as_it_was_starting()
    {
        // A stretch of use in Rigsight's history begins anew whenever the app comes to the front: a game alt-tabbed
        // back into a minute before it crashed has not "just started".
        var e = new CrashEvent { Kind = CrashKind.AppCrash, AppExe = "game.exe", Module = "game.exe", Code = "c0000005", Facts = new CrashFacts { RanSec = 4320 } };
        var f = CrashFindings.Judge(Input(e, new CrashContext(60, 70, null, 40) { Minutes = 10 }, "Game"));
        Assert.NotEqual("AT START-UP", f.Cause);
        Assert.Contains(f.Points, p => p.Title == "It had been running fine for 1 hour and 12 minutes");
        // On the border: 59 seconds is start-up, 60 is not.
        Assert.Equal("AT START-UP", CrashFindings.Judge(Input(new CrashEvent { Kind = CrashKind.AppCrash, AppExe = "game.exe", Module = "x.dll", Facts = new CrashFacts { RanSec = 59 } }, name: "Game")).Cause);
        Assert.NotEqual("AT START-UP", CrashFindings.Judge(Input(new CrashEvent { Kind = CrashKind.AppCrash, AppExe = "game.exe", Module = "x.dll", Facts = new CrashFacts { RanSec = 60 } }, name: "Game")).Cause);
        // Nothing from Windows about when it started: nothing is said about starting.
        Assert.NotEqual("AT START-UP", CrashFindings.Judge(Input(new CrashEvent { Kind = CrashKind.AppCrash, AppExe = "game.exe", Module = "x.dll" }, new CrashContext(60, 70, null, 5) { Minutes = 10 }, "Game")).Cause);
    }

    [Fact]
    public void How_long_a_program_had_run_is_read_from_its_start_time_in_either_form()
    {
        var logged = new DateTime(2026, 10, 7, 17, 53, 38, DateTimeKind.Local);
        long start = logged.AddSeconds(-90).ToFileTimeUtc();
        Assert.Equal(90, CrashLogReader.RanFor(start.ToString(), logged));
        Assert.Equal(90, CrashLogReader.RanFor(start.ToString("x"), logged));
        Assert.Null(CrashLogReader.RanFor("", logged));
        Assert.Null(CrashLogReader.RanFor("not a time", logged));
        Assert.Null(CrashLogReader.RanFor(logged.AddMinutes(5).ToFileTimeUtc().ToString(), logged)); // started after it crashed
        Assert.Null(CrashLogReader.RanFor(start.ToString(), null));
    }

    [Fact]
    public void The_same_spot_three_times_is_likely_a_fault_in_the_app_and_twice_is_not_yet()
    {
        CrashEvent Crash(int daysAgo, string offset) => new() { Id = daysAgo, Ts = TimeUtil.ToUnix(At.AddDays(-daysAgo)), Kind = CrashKind.AppCrash, AppExe = "app.exe", Module = "thing.dll", Code = "c0000005", Facts = new CrashFacts { Offset = offset, AppVersion = "2.8.0.42" } };
        var three = CrashFindings.Judge(Input(Crash(0, "1a2b"), name: "App") with { Earlier = [Crash(9, "1a2b"), Crash(4, "1a2b")] });
        Assert.Equal((Confidence.Likely, "LIKELY A FAULT IN THE APP", "App crashes at the same spot every time."), (three.Confidence, three.Chip, three.Head));
        Assert.Contains(three.Points, p => p.Title == "It has happened 2 times before" && p.Detail.EndsWith("Every time on version 2.8.0.42."));
        Assert.Equal(Confidence.NotEnough, CrashFindings.Judge(Input(Crash(0, "1a2b"), name: "App") with { Earlier = [Crash(4, "1a2b")] }).Confidence);
        Assert.Equal(Confidence.NotEnough, CrashFindings.Judge(Input(Crash(0, "1a2b"), name: "App") with { Earlier = [Crash(9, "ffff"), Crash(4, "eeee")] }).Confidence);
    }

    [Fact]
    public void A_net_app_says_the_error_its_runtime_recorded()
    {
        var e = new CrashEvent { Kind = CrashKind.AppCrash, AppExe = "Tool.exe", Module = "KERNELBASE.dll", Code = "e0434352", Facts = new CrashFacts { Error = "System.IO.FileNotFoundException", ErrorAt = "Tool.Program.Main" } };
        var f = CrashFindings.Judge(Input(e, name: "Tool"));
        Assert.Equal((Confidence.Named, "Tool crashed on a .NET error (System.IO.FileNotFoundException)."), (f.Confidence, f.Head));
        Assert.Contains("thrown in Tool.Program.Main", f.Text);
    }

    [Fact]
    public void An_app_crash_in_a_file_rigsight_does_not_know_is_not_enough_to_say()
    {
        var e = new CrashEvent { Kind = CrashKind.AppCrash, AppExe = "warp.exe", Module = "dcomp.dll", Code = "e0464645" };
        var f = CrashFindings.Judge(Input(e, new CrashContext(59, 53, null, 600) { Minutes = 10 }, "Cloudflare One Client"));
        Assert.Equal((Confidence.NotEnough, "Cloudflare One Client crashed. Not enough on record to say why."), (f.Confidence, f.Told()));
        Assert.Contains(f.RuledOut, p => p.Title == "It wasn't overheating");
    }

    [Fact]
    public void An_app_that_went_down_while_a_graphics_driver_was_installed_is_put_down_to_that()
    {
        // 7 Oct 2026: the driver's log entry is from five seconds after the crash (its last part's time).
        var t = new DateTime(2026, 10, 7, 17, 40, 26);
        var e = new CrashEvent { Id = 7, Ts = TimeUtil.ToUnix(t), Kind = CrashKind.AppCrash, AppExe = "warp.exe", Module = "dcomp.dll" };
        var driver = new SystemChange(t.AddSeconds(5), ChangeKind.Driver, "NVIDIA graphics driver 617.42") { Subject = "logged-nvidia-graphics" };
        var found = CrashCases.Findings(new() { [7] = Input(e, name: "Cloudflare One Client") with { Changes = [driver] } })[7];
        Assert.Equal((Confidence.Likely, "LIKELY THE DRIVER INSTALL", true), (found.Confidence, found.Chip, found.Outside));
        Assert.Equal("NVIDIA graphics driver 617.42 was being installed at that moment.", found.Line);
        // Ten minutes apart it is the app's own crash again; and a time only noticed by the inventory check proves nothing.
        var far = CrashCases.Findings(new() { [7] = Input(e, name: "X") with { Changes = [driver with { Time = t.AddMinutes(-10) }] } })[7];
        Assert.False(far.Outside);
        var noticed = CrashCases.Findings(new() { [7] = Input(e, name: "X") with { Changes = [driver with { Subject = Inventory.GpuDriver + ":nvidia" }] } })[7];
        Assert.False(noticed.Outside);
    }

    // ── Several at once ──

    [Fact]
    public void Several_things_freezing_together_take_their_finding_from_the_one_windows_explains()
    {
        var t = new DateTime(2026, 9, 2, 13, 3, 16);
        CrashInput Hang(int afterSec, string exe, string name, string? waitedOn = null) => Input(new CrashEvent
        {
            Ts = TimeUtil.ToUnix(t.AddSeconds(afterSec)), Kind = CrashKind.AppHang, AppExe = exe, Facts = waitedOn is null ? null : new CrashFacts { WaitedOn = waitedOn, WaitedOnVersion = "2.8.0.42" },
        }, name: name) with { NameOf = exe => exe == "wallpaper64.exe" ? "Wallpaper Engine" : exe };
        var f = CrashFindings.Together([Hang(0, "SystemSettings.exe", "Settings"), Hang(197, "explorer.exe", "Windows Explorer", "wallpaper64.exe"), Hang(312, "CrimsonDesert.exe", "Crimson Desert")]);
        Assert.Equal((Confidence.Likely, "Windows Explorer was stuck waiting on Wallpaper Engine."), (f.Confidence, f.Head));
        Assert.Contains("2 other things froze or failed within 6 minutes", f.Text);
        Assert.Equal("Windows Explorer was stuck waiting on Wallpaper Engine; 2 more froze within 6 minutes.", f.Line);
        Assert.Equal("Whether every one of them had the same cause.", f.CantTell);
        Assert.Contains(f.Points, p => p.Title == "3 things failed from 1:03 to 1:08 PM" && p.Detail == "Settings, Windows Explorer, Crimson Desert.");

        var plain = CrashFindings.Together([Hang(0, "a.exe", "A"), Hang(30, "b.exe", "B"), Hang(50, "c.exe", "C")]);
        Assert.Equal((Confidence.Possible, "POSSIBLY SEVERAL THINGS AT ONCE", "Possibly several things at once."), (plain.Confidence, plain.Chip, plain.Verdict));
    }

    [Fact]
    public void A_graphics_driver_reset_with_apps_failing_around_it_is_likely_not_named_and_counts_apps_only()
    {
        var t = new DateTime(2026, 9, 2, 13, 3, 0);
        CrashInput Reset(int after) => Input(new CrashEvent { Id = 10 + after, Ts = TimeUtil.ToUnix(t.AddSeconds(after)), Kind = CrashKind.GpuDriverReset });
        CrashInput App(int after, string exe) => Input(new CrashEvent { Id = 20 + after, Ts = TimeUtil.ToUnix(t.AddSeconds(after)), Kind = CrashKind.AppCrash, AppExe = exe }, name: exe);
        // Two resets and one app twice: one app, not three.
        var f = CrashFindings.Together([Reset(0), App(10, "game.exe"), Reset(20), App(30, "game.exe")]);
        Assert.Equal((Confidence.Likely, "LIKELY THE GRAPHICS DRIVER"), (f.Confidence, f.Chip));
        Assert.Equal("The graphics driver reset; 1 app failed within 1 minute.", f.Line);
        Assert.Contains("whether every app failed because of it", f.CantTell);
    }

    [Fact]
    public void Every_page_gets_the_same_finding_for_a_crash_that_was_one_of_several_at_once()
    {
        // A row on Reports reads each crash's own finding; the Crashes page shows them as one moment. Both are this.
        var t = new DateTime(2026, 9, 2, 13, 3, 0);
        var inputs = new[] { "a.exe", "b.exe", "c.exe" }.Select((exe, i) => Input(new CrashEvent { Id = i + 1, Ts = TimeUtil.ToUnix(t.AddSeconds(i * 40)), Kind = CrashKind.AppHang, AppExe = exe }, name: exe))
            .ToDictionary(x => x.Crash.Id);
        var found = CrashCases.Findings(inputs);
        Assert.Same(found[1], found[2]);
        Assert.Same(found[2], found[3]);
        Assert.Equal("POSSIBLY SEVERAL THINGS AT ONCE", found[1].Chip);
        Assert.Equal(found[1].Chip, CrashCases.Together([.. inputs.Values]).Chip);
        // Alone, each is only its own.
        Assert.Equal("NOT ENOUGH TO SAY", CrashCases.Findings(new() { [1] = inputs[1] })[1].Chip);
    }

    [Fact]
    public void Apps_that_fell_over_together_under_a_driver_install_are_put_down_to_the_install_as_a_moment_too()
    {
        var t = new DateTime(2026, 10, 7, 17, 40, 26);
        var driver = new SystemChange(t.AddSeconds(5), ChangeKind.Driver, "NVIDIA graphics driver 617.42") { Subject = "logged-nvidia-graphics" };
        var inputs = new[] { "a.exe", "b.exe", "c.exe" }.Select((exe, i) => Input(new CrashEvent { Id = i + 1, Ts = TimeUtil.ToUnix(t.AddSeconds(i)), Kind = CrashKind.AppCrash, AppExe = exe }, name: exe) with { Changes = [driver] })
            .ToDictionary(x => x.Crash.Id);
        var found = CrashCases.Findings(inputs)[2];
        Assert.Equal((Confidence.Likely, "LIKELY THE DRIVER INSTALL", true), (found.Confidence, found.Chip, found.Outside));
        Assert.Equal("NVIDIA graphics driver 617.42 was being installed; 3 apps failed with it.", found.Line);
    }

    [Fact]
    public void A_blue_screen_inside_any_makers_graphics_driver_is_a_graphics_crash()
    {
        foreach (var file in new[] { "amdkmdag.sys", "atikmdag.sys", "nvlddmkm.sys", "igdkmd64.sys", "dxgkrnl.sys" })
        {
            var f = CrashFindings.Judge(Input(new CrashEvent { Kind = CrashKind.SystemCrash, Code = "0x116", Facts = new CrashFacts { Driver = file } }) with { Changes = [] });
            Assert.DoesNotContain(f.RuledOut, p => p.Title == "The graphics driver");
        }
        Assert.Equal("Windows crashed inside amdkmdag.sys, the AMD driver.", CrashFindings.Judge(Input(new CrashEvent { Kind = CrashKind.SystemCrash, Code = "0x116", Facts = new CrashFacts { Driver = "amdkmdag.sys" } })).Head);
    }

    [Fact]
    public void Two_failures_in_the_same_second_are_not_twice_in_zero_seconds()
    {
        long ts = TimeUtil.ToUnix(At);
        var first = new CrashEvent { Id = 1, Ts = ts, Kind = CrashKind.AppCrash, AppExe = "g.exe", Module = "x.dll" };
        var e = new CrashEvent { Id = 2, Ts = ts, Kind = CrashKind.AppCrash, AppExe = "g.exe", Module = "x.dll", Facts = new CrashFacts { RanSec = 3 } };
        Assert.Equal("Game crashed as it was starting, twice in the same second.", CrashFindings.Judge(Input(e, name: "Game") with { Earlier = [first] }).Head);
    }

    [Fact]
    public void Facts_gain_what_windows_writes_later_and_keep_what_was_read_first()
    {
        // The scan that first saw a blue screen came before Windows wrote which driver it was in.
        var first = new CrashFacts { PowerButton = false, HardwareErrors = 0, NextStart = 100 };
        var later = new CrashFacts { PowerButton = true, Driver = "RtUsbA64.sys", DriverName = "Realtek USB Audio Driver", NextStart = 999 };
        var merged = first.With(later)!;
        Assert.Equal(("RtUsbA64.sys", "Realtek USB Audio Driver", false, 100L), (merged.Driver, merged.DriverName, merged.PowerButton, merged.NextStart));
        Assert.Null(first.Driver); // the one read first is left as it was
        Assert.Null(merged.With(later)); // nothing more to add
    }

    // ── What is read from Windows ──

    [Fact]
    public void The_power_fields_of_a_shut_off_are_read_and_their_absence_claims_nothing()
    {
        var read = CrashLogReader.PowerFacts(new Dictionary<string, string> { ["PowerButtonTimestamp"] = "0", ["LongPowerButtonPressDetected"] = "false", ["WHEABootErrorCount"] = "0" }, 0);
        Assert.Equal((false, 0), (read.PowerButton, read.HardwareErrors));
        Assert.True(CrashLogReader.PowerFacts(new Dictionary<string, string> { ["PowerButtonTimestamp"] = "134358493475522711" }, 0).PowerButton);
        Assert.True(CrashLogReader.PowerFacts(new Dictionary<string, string> { ["PowerButtonTimestamp"] = "0", ["LongPowerButtonPressDetected"] = "true" }, 0).PowerButton);
        // An older Windows without the fields.
        var old = CrashLogReader.PowerFacts(new Dictionary<string, string> { ["BugcheckCode"] = "0" }, 0);
        Assert.Null(old.PowerButton);
        Assert.Null(old.HardwareErrors);
        Assert.True(old.IsEmpty);
        var bug = CrashLogReader.PowerFacts(new Dictionary<string, string> { ["BugcheckParameter1"] = "0xc0000005", ["BugcheckParameter2"] = "0xfffff806e972fd4c", ["BugcheckParameter3"] = "0x0", ["BugcheckParameter4"] = "0x0" }, 0x3B);
        Assert.Equal(["c0000005", "fffff806e972fd4c", "0", "0"], bug.BugParams);
    }

    [Fact]
    public void A_blue_screens_four_numbers_are_read_from_windows_line()
    {
        Assert.Equal(["c0000005", "fffff806e972fd4c", "fffffe0216b395f0", "0"],
            CrashLogReader.BugParams("0x0000003b (0x00000000c0000005, 0xfffff806e972fd4c, 0xfffffe0216b395f0, 0x0000000000000000)"));
        Assert.Null(CrashLogReader.BugParams("0x0000003b"));
        Assert.Null(CrashLogReader.BugParams(null));
    }

    [Fact]
    public void The_net_runtimes_entry_gives_the_program_the_error_and_where()
    {
        var withError = CrashLogReader.DotNetError("Application: Tool.exe\nCoreCLR Version: 10.0\nDescription: The process was terminated due to an unhandled exception.\nException Info: System.IO.FileNotFoundException: Could not find file 'x'.\n   at Tool.Program.Main(System.String[])\n   at More()");
        Assert.Equal(("Tool.exe", "System.IO.FileNotFoundException", "Tool.Program.Main"), withError);
        // An access violation carries only a stack.
        var stackOnly = CrashLogReader.DotNetError("Application: Rigsight.Agent.exe\r\nStack:\r\n   at Rigsight.Agent.AgentContext.TempMove(IntPtr, IntPtr, Int32)\r\n");
        Assert.Equal(("Rigsight.Agent.exe", null, "Rigsight.Agent.AgentContext.TempMove"), stackOnly);
        Assert.Null(CrashLogReader.DotNetError("Something else"));
    }

    [Fact]
    public void A_hang_report_names_what_was_waited_on_only_for_a_wait_on_another_program()
    {
        var e = new CrashEvent { Kind = CrashKind.AppHang, AppExe = "explorer.exe" };
        CrashLogReader.AddReport(e, "AppHangXProcB1", "wallpaper64.exe", "2.8.0.42");
        Assert.Equal(("AppHangXProcB1", "wallpaper64.exe", "2.8.0.42"), (e.Facts!.ReportType, e.Facts.WaitedOn, e.Facts.WaitedOnVersion));
        var own = new CrashEvent { Kind = CrashKind.AppHang, AppExe = "game.exe" };
        CrashLogReader.AddReport(own, "AppHangB1", "0000", "Unknown");
        Assert.Equal(("AppHangB1", null), (own.Facts!.ReportType, own.Facts.WaitedOn));
        CrashLogReader.AddReport(own, "AppHangXProcB1", "game.exe", "1.0"); // waiting on itself says nothing
        Assert.Null(own.Facts.WaitedOn);
    }

    [Theory]
    [InlineData("Microsoft-Windows-WHEA-Logger", 18, SignKind.Hardware)]
    [InlineData("Microsoft-Windows-WHEA-Logger", 19, SignKind.Hardware)]
    [InlineData("disk", 51, SignKind.Drive)]
    [InlineData("Ntfs", 55, SignKind.Drive)]
    [InlineData("nvlddmkm", 13, SignKind.Graphics)]
    [InlineData("Microsoft-Windows-Resource-Exhaustion-Detector", 2004, SignKind.Memory)]
    public void Trouble_windows_logs_is_kept_as_a_sign(string provider, int id, string kind) =>
        Assert.Equal(kind, CrashLogReader.SignOf(provider, id)?.Kind);

    [Theory]
    [InlineData("nvlddmkm", 153)] // logged on healthy PCs all the time (232 on one day here)
    [InlineData("disk", 153)]     // a retry that worked
    [InlineData("Ntfs", 98)]
    [InlineData("volmgr", 162)]
    public void Routine_entries_are_not_signs(string provider, int id) => Assert.Null(CrashLogReader.SignOf(provider, id));

    [Fact]
    public void A_drivers_description_is_made_plain_and_its_registry_path_resolved()
    {
        Assert.Equal("Realtek USB Audio Driver", DriverFiles.Clean("Realtek (r) USB Audio Driver", "Realtek Semiconductor Corp."));
        Assert.Equal("NVIDIA Windows Kernel Mode Driver", DriverFiles.Clean("NVIDIA Windows Kernel Mode Driver, Version 617.42 ", "NVIDIA Corporation"));
        Assert.Equal("Acme Driver", DriverFiles.Clean("Driver", "Acme Inc."));
        Assert.Null(DriverFiles.Clean(" ", "Acme"));
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Equal(Path.Combine(windows, @"System32\DriverStore\FileRepository\x\a.sys"), DriverFiles.Resolve(@"\SystemRoot\System32\DriverStore\FileRepository\x\a.sys"));
        Assert.Equal(Path.Combine(windows, @"System32\drivers\a.sys"), DriverFiles.Resolve(@"System32\drivers\a.sys"));
        Assert.Equal(@"C:\Drivers\a.sys", DriverFiles.Resolve(@"\??\C:\Drivers\a.sys"));
        Assert.True(DriverFiles.IsWindowsCore("NTOSKRNL.EXE"));
        Assert.False(DriverFiles.IsWindowsCore("RtUsbA64.sys"));
    }

    [Fact]
    public void Facts_are_stored_as_json_without_what_is_missing_and_read_back()
    {
        var facts = new CrashFacts { AppVersion = "1.2", PowerButton = false, HardwareErrors = 0, BugParams = ["1", "2", "3", "4"] };
        string json = facts.ToJson();
        Assert.DoesNotContain("Driver", json);
        var back = CrashFacts.FromJson(json)!;
        Assert.Equal(("1.2", false, 0), (back.AppVersion, back.PowerButton, back.HardwareErrors));
        Assert.Equal(["1", "2", "3", "4"], back.BugParams);
        Assert.Null(CrashFacts.FromJson(null));
        Assert.Null(CrashFacts.FromJson("{ not json"));
        Assert.True(new CrashFacts().IsEmpty);
    }
}

/// <summary>A period is called by its own name wherever the app says it: today is "today", yesterday "yesterday".</summary>
public sealed class PeriodWordsTests
{
    private static readonly DateTime Today = new(2026, 10, 13); // a Tuesday

    [Theory]
    [InlineData(ReportRange.Day, 0, "today")]
    [InlineData(ReportRange.Day, -1, "yesterday")]
    [InlineData(ReportRange.Day, -2, "that day")]
    [InlineData(ReportRange.Week, 0, "this week")]
    [InlineData(ReportRange.Week, -1, "this week")]   // Monday of the same week
    [InlineData(ReportRange.Week, -2, "last week")]   // Sunday before
    [InlineData(ReportRange.Week, -9, "that week")]
    [InlineData(ReportRange.Month, -12, "this month")]
    [InlineData(ReportRange.Month, -13, "last month")]
    [InlineData(ReportRange.Month, -60, "that month")]
    [InlineData(ReportRange.Year, -200, "this year")]
    [InlineData(ReportRange.Year, -300, "last year")]
    [InlineData(ReportRange.Year, -700, "that year")]
    [InlineData(ReportRange.Custom, -3, "in that time")]
    public void In_a_recaps_sentence(ReportRange range, int daysAgo, string said) =>
        Assert.Equal(said, PeriodWords.Said(range, Today.AddDays(daysAgo), Today));

    [Fact]
    public void A_net_line_about_yesterday_says_yesterday()
    {
        // Under a card headed "Yesterday" the line read "…uploaded 1.1 GB that day".
        Assert.Equal("yesterday", PeriodWords.Said(ReportRange.Day, DateTime.Today.AddDays(-1)));
        Assert.Equal("today", PeriodWords.Said(ReportRange.Day, DateTime.Today));
    }
}
