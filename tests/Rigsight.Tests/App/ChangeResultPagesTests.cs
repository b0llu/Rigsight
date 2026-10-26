using Rigsight.Core.Reports;
using Rigsight.Core.Stability;
using Rigsight.Models;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// A change's result where it shows outside the Timeline: Home's "What changed lately" and PC health, an opened crash,
/// and the copied report. Each says it only where a change made a difference.
/// </summary>
public sealed class ChangeResultPagesTests
{
    private static readonly DateTime Today = new(2026, 10, 17);
    private static readonly DateTime Day = Today.AddDays(-7);

    private static readonly SystemChange Driver =
        new(Day.AddHours(21.5), ChangeKind.Driver, "NVIDIA graphics driver 618.10") { Subject = "logged-gpudriver:x", Was = "617.42", Now = "618.10" };

    private static ChangeResult Result(EffectTone tone = EffectTone.Worse, Confidence sure = Confidence.Likely, DateTime? day = null) => new()
    {
        Day = day ?? Day, Changes = [Driver], From = Day.AddDays(-14), To = Today.AddDays(1), Open = true, DaysBefore = 14, DaysAfter = 6,
        Tone = tone, Sure = sure, Cause = "THE DRIVER",
        Headline = "Crimson Desert runs 10° hotter on the GPU since this driver", Line = c => $"Crimson Desert runs 10° hotter on the GPU since {c}",
        Context = "Same game at the same load, and not because of the room.", Short = "10° hotter in Crimson Desert since",
        Rows = [new("GPU in Crimson Desert", "Same load", "68°", "78°", tone, "10° hotter")],
        Checks = [new(true, "The same game on both sides", "Crimson Desert: 12h 15m before, 2h 33m since.")],
        Why = "14 days before, 6 days since · 12h 15m and 2h 33m of Crimson Desert",
    };

    private static List<SystemChange> Changes() =>
    [
        Driver,
        new(Today.AddDays(-1).AddHours(8), ChangeKind.AppUpdated, "Discord updated to 1.0.9262"),
        new(Today.AddDays(-1).AddHours(9), ChangeKind.AppUpdated, "Google Chrome updated to 155.0.8059.80"),
        new(Today.AddDays(-2).AddHours(9), ChangeKind.AppInstalled, "THE FINALS installed"),
        new(Today.AddHours(7), ChangeKind.AppUpdated, "NVIDIA App updated to 11.0.9.565"),
    ];

    [Fact]
    public void Home_pins_the_latest_change_that_made_a_difference_with_what_it_did()
    {
        var lines = HomeViewModel.HomeChangeLines(Changes(), Today, new Dictionary<DateTime, ChangeResult> { [Day] = Result() });
        Assert.Equal(HomeViewModel.CardLines, lines.Count);
        // A week old, above three newer app updates: one statement of what it did, amber, opening its before and after.
        Assert.Equal(("Crimson Desert runs 10° hotter on the GPU since driver 618.10", "WarmBrush", (DateTime?)Day), (lines[0].Text, lines[0].TextBrush, lines[0].ChangeDay));
        Assert.All(lines.Skip(1), l => Assert.Null(l.ChangeDay));

        // Without a result it's just the newest four, nothing pinned or linked.
        var plain = HomeViewModel.HomeChangeLines(Changes(), Today, new Dictionary<DateTime, ChangeResult>());
        Assert.Equal("NVIDIA App updated", plain[0].Text);
        Assert.All(plain, l => Assert.False(l.CanOpen));

        // Two weeks on it's no longer pinned.
        var old = HomeViewModel.HomeChangeLines(Changes(), Today.AddDays(HomeViewModel.PinDays), new Dictionary<DateTime, ChangeResult> { [Day] = Result() });
        Assert.DoesNotContain(old, l => l.ChangeDay is not null);
    }

    [Fact]
    public void PC_health_calls_out_only_a_change_that_likely_made_things_worse()
    {
        var line = HomeViewModel.WorseLine(new Dictionary<DateTime, ChangeResult> { [Day] = Result() }, Today)!;
        // One statement on Home, amber, opening the change's before and after.
        Assert.Equal("Crimson Desert runs 10° hotter on the GPU since driver 618.10", line.Text);
        Assert.Equal(("WarmBrush", true), (line.TextBrush, line.CanOpen));
        Assert.Equal(Day, line.ChangeDay);
        Assert.Null(HomeViewModel.WorseLine(new Dictionary<DateTime, ChangeResult> { [Day] = Result(sure: Confidence.Possible) }, Today));
        Assert.Null(HomeViewModel.WorseLine(new Dictionary<DateTime, ChangeResult> { [Day] = Result(EffectTone.Better) }, Today));
        Assert.Null(HomeViewModel.WorseLine(new Dictionary<DateTime, ChangeResult>(), Today));
    }

    [Fact]
    public void An_opened_crash_after_a_change_that_made_things_worse_points_at_it_as_possibly_connected()
    {
        var results = new Dictionary<DateTime, ChangeResult> { [Day] = Result() };
        var reset = new CrashEvent { Kind = CrashKind.GpuDriverReset, Ts = Rigsight.Core.Data.TimeUtil.ToUnix(Day.AddDays(2).AddHours(22)) };
        var finding = new CrashFinding(Confidence.Named, "The GPU stopped answering and Windows reset its driver.", "", "", "");
        var point = CrashesViewModel.ChangePointOf(reset, finding, results)!;
        Assert.Equal("2 days after NVIDIA graphics driver 618.10", point.Title);
        Assert.Equal("Crimson Desert runs 10° hotter on the GPU since driver 618.10. Likely the driver. That alone doesn't prove this crash.", point.Detail);
        Assert.Equal(Day, point.Day);

        // Before the change, or after one that made things better, or an app crash a graphics driver can't cause: nothing.
        Assert.Null(CrashesViewModel.ChangePointOf(new CrashEvent { Kind = CrashKind.GpuDriverReset, Ts = Rigsight.Core.Data.TimeUtil.ToUnix(Day.AddDays(-1)) }, finding, results));
        Assert.Null(CrashesViewModel.ChangePointOf(reset, finding, new Dictionary<DateTime, ChangeResult> { [Day] = Result(EffectTone.Better) }));
        var appCrash = new CrashEvent { Kind = CrashKind.AppCrash, Ts = reset.Ts, AppExe = "Discord.exe" };
        Assert.Null(CrashesViewModel.ChangePointOf(appCrash, new CrashFinding(Confidence.Likely, "Discord crashed in its own code.", "", "", ""), results));
    }

    [Fact]
    public void The_copied_report_says_what_the_page_says()
    {
        var text = new ChangeCase(Result(), []).Report();
        Assert.Contains("NVIDIA graphics driver 618.10 (was 617.42)", text);
        Assert.Contains("Crimson Desert runs 10° hotter on the GPU since this driver. Likely the driver.", text);
        Assert.Contains("GPU in Crimson Desert · 68° before, 78° since (10° hotter)", text);
        Assert.Contains("14 days before, 6 days since", text);
    }

    [Fact]
    public void The_crashes_export_says_beside_a_change_what_it_made_worse()
    {
        var other = new SystemChange(Day.AddDays(1).AddHours(9), ChangeKind.WindowsUpdate, "Windows update KB1");
        var story = new Rigsight.Core.Export.CrashStory(new CrashEvent { Kind = CrashKind.GpuDriverReset }, null, null, null, [Driver, other])
        {
            Results = new Dictionary<DateTime, ChangeResult> { [Day] = Result() },
        };
        Assert.Equal($"NVIDIA graphics driver 618.10 ({Day:d MMM}; Crimson Desert runs 10° hotter on the GPU since driver 618.10; likely the driver); Windows update KB1 ({Day.AddDays(1):d MMM})",
            story.Changed);
    }

    [Fact]
    public void The_drivers_card_shows_only_when_another_driver_made_a_difference_too()
    {
        DriverLine Line(string v, bool isThis, string? result) => new(v, "", result, "WarmBrush", Day, isThis, result is not null);
        Assert.False(new ChangeCase(Result(), [Line("618.10", true, "10° hotter"), Line("617.42", false, null)]).HasDrivers);
        Assert.True(new ChangeCase(Result(), [Line("618.10", true, "10° hotter"), Line("617.42", false, "Fewer graphics problems since")]).HasDrivers);
    }
}
