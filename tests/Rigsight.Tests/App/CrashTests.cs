using System.Reflection;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;
using Rigsight.Models;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>Made-up crashes for the crash models and page.</summary>
internal static class Crashes
{
    private static long _id = 1_000_000;

    public static CrashRow Row(CrashKind kind, DateTime time, string exe = "", string? name = null, string? module = null, string? code = null,
        bool sleep = false, double? cpu = null, double? gpu = null, string? front = null, double? session = null, bool game = false,
        string? dump = null, string? detail = null)
    {
        var e = new CrashEvent
        {
            Id = Interlocked.Increment(ref _id), Ts = TimeUtil.ToUnix(time), Kind = kind, AppExe = exe, Module = module, Code = code,
            DuringSleep = sleep, Detail = detail,
        };
        return new CrashRow
        {
            Event = e, Explanation = CrashExplainer.Explain(e, name), AppName = name, CpuBefore = cpu, GpuBefore = gpu, FrontApp = front,
            SessionSec = session, IsGame = game, DumpPath = dump,
        };
    }

    public static CrashRow App(DateTime time, string exe = "game.exe", string? module = "ntdll.dll") =>
        Row(CrashKind.AppCrash, time, exe, Path.GetFileNameWithoutExtension(exe), module, "0xc0000005");

    public static CrashRow Hang(DateTime time, string exe) => Row(CrashKind.AppHang, time, exe, Path.GetFileNameWithoutExtension(exe));

    public static CrashRow Bsod(DateTime time, string code = "0x00000124") => Row(CrashKind.SystemCrash, time, code: code, detail: "WHEA_UNCORRECTABLE_ERROR");

    public static CrashRow Reset(DateTime time) => Row(CrashKind.GpuDriverReset, time, module: "nvlddmkm");

    public static CrashRow Power(DateTime time, bool sleep = false) => Row(CrashKind.UnexpectedShutdown, time, sleep: sleep);
}

/// <summary>How crashes are explained, grouped (repeats, and bursts that are one incident) and described.</summary>
[Collection("UI")]
public sealed class CrashModelTests
{
    private static readonly DateTime Noon = DateTime.Today.AddDays(-3).AddHours(12);

    [Fact]
    public void Repeats_of_the_same_crash_are_one_card()
    {
        var rows = new[] { Crashes.App(Noon), Crashes.App(Noon.AddDays(-1)), Crashes.App(Noon.AddHours(-3)) };
        var g = Assert.Single(CrashGroup.Build(rows));
        Assert.Equal(3, g.Count);
        Assert.True(g.IsRepeated);
        Assert.False(g.IsIncident);
        Assert.Same(rows[0], g.Latest);
        Assert.Same(rows[1], g.First);
        Assert.Equal("×3", g.CountText);
        Assert.Equal($"Last: {rows[0].TimeText} · first {rows[1].Time:d MMM}", g.WhenText);
        Assert.Equal("Last time, just before: ", g.ContextLabel);
        Assert.Equal(CrashSeverity.Minor, g.Severity);
        Assert.Equal("game.exe", g.AppExe);
        Assert.True(g.CanMute);
    }

    [Fact]
    public void Listing_every_crash_keeps_repeats_apart()
    {
        var rows = new[] { Crashes.App(Noon), Crashes.App(Noon.AddDays(-1)), Crashes.App(Noon.AddHours(-3)) };
        var groups = CrashGroup.Build(rows, groupRepeats: false);
        Assert.Equal(3, groups.Count);
        Assert.All(groups, g => Assert.Equal(1, g.Count));
        Assert.Equal(groups.OrderByDescending(g => g.LastTime), groups);
        Assert.Equal(rows[0].TimeText, groups[0].WhenText);
        Assert.Equal("Just before: ", groups[0].ContextLabel);
    }

    [Fact]
    public void The_same_app_failing_in_another_place_is_another_card()
    {
        var rows = new[] { Crashes.App(Noon, module: "nvwgf2umx.dll"), Crashes.App(Noon.AddDays(-1), module: "ntdll.dll"), Crashes.App(Noon.AddDays(-2), "other.exe") };
        Assert.Equal(3, CrashGroup.Build(rows).Count);
    }

    [Fact]
    public void Blue_screens_group_by_code_and_power_losses_by_whether_asleep()
    {
        var rows = new[]
        {
            Crashes.Bsod(Noon), Crashes.Bsod(Noon.AddDays(-1)), Crashes.Bsod(Noon.AddDays(-2), "0x0000007e"),
            Crashes.Power(Noon.AddHours(1)), Crashes.Power(Noon.AddDays(-1).AddHours(1)), Crashes.Power(Noon.AddDays(-2).AddHours(1), sleep: true),
            Crashes.Reset(Noon.AddHours(2)), Crashes.Reset(Noon.AddDays(-4)),
        };
        var groups = CrashGroup.Build(rows);
        Assert.Equal(5, groups.Count);
        Assert.Equal([1, 1, 2, 2, 2], groups.Select(g => g.Count).Order());
        Assert.All(groups, g => Assert.Null(g.AppExe));
        Assert.All(groups, g => Assert.False(g.CanMute));
    }

    [Fact]
    public void Three_apps_failing_within_minutes_is_one_incident()
    {
        var rows = new[]
        {
            Crashes.App(Noon, "a.exe"), Crashes.App(Noon.AddMinutes(2), "b.exe"), Crashes.App(Noon.AddMinutes(4), "c.exe"),
            Crashes.App(Noon.AddHours(5), "a.exe"),
        };
        var groups = CrashGroup.Build(rows);
        Assert.Equal(2, groups.Count);
        var incident = groups.Single(g => g.IsIncident);
        Assert.Equal(3, incident.Count);
        Assert.Equal("SEVERAL AT ONCE", incident.KindLabel);
        Assert.Equal("Several things failed at once", incident.Title);
        Assert.Equal(CrashSeverity.Serious, incident.Severity);
        Assert.Null(incident.AppExe);
        Assert.Null(incident.IconPath);
        Assert.Equal($"3 problems within 4 minutes: a, b and c crashed or froze, starting at {Noon:h:mm tt}.", incident.Reason);
        Assert.StartsWith("When several apps fail together", incident.Advice);
        Assert.Equal("Windows PC freezes several apps not responding at once", incident.SearchQuery);
        // Rows in the incident aren't listed again as repeats.
        Assert.Equal(1, groups.Single(g => !g.IsIncident).Count);
    }

    [Fact]
    public void Freezes_of_two_apps_together_mean_the_pc_froze()
    {
        var rows = new[] { Crashes.Hang(Noon, "a.exe"), Crashes.Hang(Noon.AddMinutes(1), "b.exe"), Crashes.Hang(Noon.AddMinutes(3), "a.exe") };
        var g = Assert.Single(CrashGroup.Build(rows));
        Assert.True(g.IsIncident);
        Assert.Equal("PC FROZE", g.KindLabel);
        Assert.Equal("Your PC froze", g.Title);
        Assert.Contains("a and b stopped responding", g.Reason);
    }

    [Fact]
    public void Two_apps_crashing_together_is_not_an_incident()
    {
        var rows = new[] { Crashes.App(Noon, "a.exe"), Crashes.App(Noon.AddMinutes(1), "helper.exe"), Crashes.App(Noon.AddMinutes(2), "a.exe") };
        Assert.All(CrashGroup.Build(rows), g => Assert.False(g.IsIncident));
    }

    [Fact]
    public void Failures_more_than_five_minutes_apart_are_separate()
    {
        var rows = new[] { Crashes.Hang(Noon, "a.exe"), Crashes.Hang(Noon.AddMinutes(6), "b.exe"), Crashes.Hang(Noon.AddMinutes(12), "c.exe") };
        Assert.All(CrashGroup.Build(rows), g => Assert.False(g.IsIncident));
        // Five minutes between each is still one moment.
        rows = [Crashes.Hang(Noon, "a.exe"), Crashes.Hang(Noon.AddMinutes(5), "b.exe"), Crashes.Hang(Noon.AddMinutes(10), "c.exe")];
        Assert.True(Assert.Single(CrashGroup.Build(rows)).IsIncident);
    }

    [Fact]
    public void A_driver_reset_that_takes_apps_with_it()
    {
        var rows = new[] { Crashes.Reset(Noon), Crashes.App(Noon.AddSeconds(30), "game.exe"), Crashes.App(Noon.AddSeconds(50), "browser.exe") };
        var g = Assert.Single(CrashGroup.Build(rows));
        Assert.True(g.IsIncident);
        Assert.Equal("The graphics driver reset and took apps with it", g.Title);
        Assert.Equal($"The graphics driver reset, and game and browser were affected within 1 minute, starting at {Noon:h:mm tt}.", g.Reason);
        Assert.Equal("Display driver stopped responding and has recovered games crash", g.SearchQuery);
    }

    [Fact]
    public void Many_apps_in_an_incident_are_summarized()
    {
        var rows = Enumerable.Range(0, 6).Select(i => Crashes.App(Noon.AddMinutes(i), $"app{i}.exe")).ToArray();
        var g = Assert.Single(CrashGroup.Build(rows));
        Assert.Contains("app0, app1, app2 and 3 more crashed or froze", g.Reason);
    }

    [Theory]
    [InlineData(CrashKind.SystemCrash, false, "", CrashSeverity.Critical)]
    [InlineData(CrashKind.UnexpectedShutdown, false, "", CrashSeverity.Critical)]
    [InlineData(CrashKind.UnexpectedShutdown, true, "", CrashSeverity.Info)]
    [InlineData(CrashKind.GpuDriverReset, false, "", CrashSeverity.Serious)]
    [InlineData(CrashKind.AppCrash, false, "DWM.exe", CrashSeverity.Serious)]
    [InlineData(CrashKind.AppHang, false, "dwm.exe", CrashSeverity.Serious)]
    [InlineData(CrashKind.AppCrash, false, "game.exe", CrashSeverity.Minor)]
    [InlineData(CrashKind.AppHang, false, "game.exe", CrashSeverity.Minor)]
    public void Severity_of_each_kind(CrashKind kind, bool sleep, string exe, CrashSeverity expected) =>
        Assert.Equal(expected, CrashGroup.SeverityOf(Crashes.Row(kind, Noon, exe, sleep: sleep)));

    [Fact]
    public void Web_searches_for_each_kind()
    {
        string Query(CrashRow r) => CrashGroup.Build([r])[0].SearchQuery;
        Assert.Equal("game.exe crash ntdll.dll 0xc0000005", Query(Crashes.App(Noon)));
        // The module only helps when it isn't the app itself.
        Assert.Equal("game.exe crash 0xc0000005", Query(Crashes.App(Noon, module: "GAME.EXE")));
        Assert.Equal("game.exe not responding", Query(Crashes.Hang(Noon, "game.exe")));
        Assert.Equal("Display driver stopped responding and has recovered", Query(Crashes.Reset(Noon)));
        Assert.Equal("PC shuts off unexpectedly Kernel-Power 41", Query(Crashes.Power(Noon)));
        Assert.Equal("PC loses power during sleep Kernel-Power 41", Query(Crashes.Power(Noon, sleep: true)));
        Assert.EndsWith("0x00000124 blue screen", Query(Crashes.Bsod(Noon)));
    }

    [Fact]
    public void Card_toggles_change_their_texts()
    {
        var g = CrashGroup.Build([Crashes.App(Noon), Crashes.App(Noon.AddDays(-1))])[0];
        Assert.Equal("All 2 times", g.ExpandText);
        Assert.Equal("Details", g.DetailsText);
        Assert.Equal("Mute", g.MuteText);
        Assert.Equal("Copy report", g.CopyText);
        var changed = Kit.Changes(g, () => { g.IsExpanded = true; g.ShowDetails = true; g.IsMuted = true; g.Copied = true; });
        Assert.Equal("Hide times", g.ExpandText);
        Assert.Equal("Hide details", g.DetailsText);
        Assert.Equal("Unmute", g.MuteText);
        Assert.Equal("Copied", g.CopyText);
        foreach (var p in new[] { nameof(g.ExpandText), nameof(g.DetailsText), nameof(g.MuteText), nameof(g.CopyText) }) Assert.Contains(p, changed);
    }

    [Fact]
    public void A_dump_file_from_any_row_of_a_card()
    {
        var withDump = Crashes.Row(CrashKind.SystemCrash, Noon.AddDays(-1), code: "0x124", dump: @"C:\Windows\Minidump\a.dmp");
        var g = CrashGroup.Build([Crashes.Row(CrashKind.SystemCrash, Noon, code: "0x124"), withDump])[0];
        Assert.True(g.HasDump);
        Assert.Equal(@"C:\Windows\Minidump\a.dmp", g.DumpPath);
        Assert.False(CrashGroup.Build([Crashes.Bsod(Noon)])[0].HasDump);
    }

    [Fact]
    public void Nothing_to_group()
    {
        Assert.Empty(CrashGroup.Build([]));
    }

    // ── One crash's texts ────────────────────────────────────────────────

    [Fact]
    public void When_a_crash_happened_in_words()
    {
        var today = Crashes.App(DateTime.Today.AddHours(9).AddMinutes(5));
        var yesterday = Crashes.App(DateTime.Today.AddDays(-1).AddHours(21));
        var older = Crashes.App(new DateTime(2025, 3, 4, 13, 7, 0));
        Assert.Equal($"Today, {today.Time:h:mm tt}", today.TimeText);
        Assert.Equal($"Yesterday, {yesterday.Time:h:mm tt}", yesterday.TimeText);
        Assert.Equal(older.Time.ToString("ddd d MMM, h:mm tt"), older.TimeText);
    }

    [Theory]
    [InlineData(CrashKind.AppCrash, "APP CRASH", false)]
    [InlineData(CrashKind.AppHang, "NOT RESPONDING", false)]
    [InlineData(CrashKind.GpuDriverReset, "GPU DRIVER RESET", true)]
    [InlineData(CrashKind.SystemCrash, "BLUE SCREEN", true)]
    [InlineData(CrashKind.UnexpectedShutdown, "UNEXPECTED SHUTDOWN", true)]
    public void Kind_labels(CrashKind kind, string label, bool system)
    {
        var r = Crashes.Row(kind, Noon, "x.exe");
        Assert.Equal(label, r.KindLabel);
        Assert.Equal(system, r.IsSystem);
        Assert.False(string.IsNullOrEmpty(r.Title));
        Assert.False(string.IsNullOrEmpty(r.Reason));
        Assert.False(string.IsNullOrEmpty(r.Advice));
    }

    [Fact]
    public void What_was_going_on_just_before()
    {
        Ui.Run(() =>
        {
            Assert.Null(Crashes.App(Noon).TempsText);
            Assert.Null(Crashes.App(Noon).ContextText);
            var cool = Crashes.Row(CrashKind.AppCrash, Noon, "game.exe", "Game", cpu: 60, gpu: 70);
            Assert.Equal("Just before: CPU 60°, GPU 70°", cool.TempsText);
            Assert.Equal("CPU 60°, GPU 70°", cool.ContextText);
            var hot = Crashes.Row(CrashKind.AppCrash, Noon, "game.exe", "Game", cpu: 60, gpu: 83);
            Assert.EndsWith("running hot, which may have played a part.", hot.TempsText);
            Assert.EndsWith("(running hot)", hot.ContextText);
            Assert.EndsWith("(running hot)", Crashes.Row(CrashKind.AppCrash, Noon, cpu: 88).ContextText);
            Assert.Equal("CPU —, GPU 50°", Crashes.Row(CrashKind.AppCrash, Noon, gpu: 50).ContextText);

            var playing = Crashes.Row(CrashKind.AppCrash, Noon, "game.exe", "Crimson Desert", session: 4320, game: true, front: "Discord", cpu: 60, gpu: 78);
            Assert.Equal("1h 12m into playing Crimson Desert  ·  CPU 60°, GPU 78°", playing.ContextText);
            var using_ = Crashes.Row(CrashKind.AppCrash, Noon, "code.exe", "VS Code", session: 120);
            Assert.Equal("2m into using VS Code", using_.ContextText);
            // A short session says what was in front instead, unless that's the app itself.
            Assert.Equal("Discord was in front", Crashes.Row(CrashKind.AppCrash, Noon, "game.exe", "Game", session: 30, front: "Discord").ContextText);
            Assert.Null(Crashes.Row(CrashKind.AppCrash, Noon, "game.exe", "Game", front: "GAME").ContextText);
            Assert.Equal("5m into using it", Crashes.Row(CrashKind.AppCrash, Noon, "x.exe", session: 300).ContextText);
        });
    }

    [Fact]
    public void Technical_details_leave_out_what_is_missing()
    {
        var full = Crashes.Row(CrashKind.SystemCrash, Noon, "x.exe", module: "ntoskrnl.exe", code: "0x124", detail: "WHEA", dump: @"C:\d.dmp");
        Assert.Equal(@"x.exe  ·  module ntoskrnl.exe  ·  code 0x124  ·  WHEA  ·  dump C:\d.dmp", full.TechnicalText);
        Assert.Equal("", Crashes.Power(Noon).TechnicalText);
    }

    [Fact]
    public void Copied_report_for_a_forum_post()
    {
        Ui.Run(() =>
        {
            var one = CrashGroup.Build([Crashes.Row(CrashKind.AppCrash, Noon, "game.exe", "Game", "ntdll.dll", "0xc0000005", cpu: 55, gpu: 66)])[0];
            var text = CrashesViewModel.Report(one);
            Assert.Contains($"When: {Noon:dddd d MMMM yyyy, h:mm tt}", text);
            Assert.Contains("What happened: ", text);
            Assert.Contains("Just before: CPU 55°, GPU 66°", text);
            Assert.Contains("Technical: game.exe  ·  module ntdll.dll  ·  code 0xc0000005", text);
            Assert.Contains("Suggested fix: ", text);
            Assert.EndsWith(PcInfo.Text, text);

            var repeated = CrashGroup.Build([Crashes.App(Noon), Crashes.App(Noon.AddDays(-2))])[0];
            Assert.Contains($"When: 2 times, most recently {Noon:d MMM yyyy, h:mm tt} (first {Noon.AddDays(-2):d MMM yyyy})", CrashesViewModel.Report(repeated));

            var incident = CrashGroup.Build([Crashes.Reset(Noon), Crashes.App(Noon.AddSeconds(30), "a.exe"), Crashes.App(Noon.AddSeconds(50), "b.exe")])[0];
            var lines = CrashesViewModel.Report(incident);
            Assert.StartsWith("Problem: The graphics driver reset and took apps with it (several at once)", lines);
            Assert.Contains($"When: {Noon:dddd d MMMM yyyy, h:mm tt} – {Noon.AddSeconds(50):h:mm tt}", lines);
            Assert.Contains("Graphics driver: gpu driver reset", lines);
            Assert.Contains("a: app crash (a.exe  ·  module ntdll.dll  ·  code 0xc0000005)", lines);
            Assert.DoesNotContain("Technical:", lines);
        });
    }
}

/// <summary>The Crashes page, on the run's history and on made-up crashes.</summary>
[Collection("UI")]
public sealed class CrashesPageTests
{
    private static (CrashesViewModel Vm, SettingsModel Settings) Page()
    {
        SharedData.EnsureSeeded();
        var settings = Kit.OfflineSettings();
        var vm = Ui.Run(() => new CrashesViewModel(new ReportService(settings), settings));
        return (vm, settings);
    }

    private static CrashesViewModel Loaded(ReportRange unit = ReportRange.All)
    {
        var (vm, _) = Page();
        Ui.Run(() => vm.Unit = unit);
        Kit.Wait(() => vm.LoadAsync());
        return vm;
    }

    /// <summary>Puts made-up crashes on the page, as a load would.</summary>
    private static void Show(CrashesViewModel vm, IEnumerable<CrashRow> rows, List<SystemChange>? changes = null) => Ui.Run(() =>
    {
        var t = typeof(CrashesViewModel);
        t.GetField("_all", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, rows.ToList());
        t.GetField("_changes", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, changes ?? []);
        t.GetMethod("Regroup", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null);
        t.GetMethod("ApplyView", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null);
    });

    /// <summary>Picks the period without loading it (a load would replace the made-up crashes).</summary>
    private static void SetPeriod(CrashesViewModel vm, ReportRange unit, DateTime anchor) => Ui.Run(() =>
    {
        typeof(CrashesViewModel).GetField("_unit", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, unit);
        typeof(CrashesViewModel).GetField("_anchor", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, anchor);
    });

    private static int Counted(CrashesViewModel vm) => vm.AppCrashCount + vm.SystemCount + vm.DriverResetCount;

    [Fact]
    public void All_time_lists_every_generated_crash_with_consistent_counts()
    {
        var vm = Loaded();
        Ui.Run(() =>
        {
            Assert.True(vm.HasCrashes);
            Assert.NotNull(vm.Since);
            Assert.Equal(Counted(vm), vm.Crashes.Count);
            Assert.Equal(vm.Crashes.Count, vm.GroupsShown.Sum(g => g.Count));
            Assert.Equal(vm.Crashes.OrderByDescending(r => r.Time).Select(r => r.Event.Id), vm.Crashes.Select(r => r.Event.Id));
            Assert.Same(vm.Crashes[0], vm.Latest);
            Assert.Equal(0, vm.MutedCount);
            Assert.False(vm.HasMuted);
            Assert.False(vm.HasMore);
            Assert.True(vm.ShowTimeline);
            Assert.NotEmpty(vm.Days);
            Assert.Equal(DateTime.Today, vm.Days[^1].Day);
            Assert.Equal(vm.Crashes.Count, vm.Days.Sum(d => d.Total));
            Assert.EndsWith(" days)", vm.RangeNote);
            Assert.NotEmpty(vm.StatusTitle);
            Assert.EndsWith(".", vm.StatusDetail);
        });
    }

    [Fact]
    public void Each_crash_says_what_was_going_on()
    {
        var vm = Loaded();
        Ui.Run(() =>
        {
            var apps = vm.Crashes.Where(c => c.Event.Kind is CrashKind.AppCrash or CrashKind.AppHang).ToList();
            Assert.NotEmpty(apps);
            Assert.All(apps, c => Assert.False(string.IsNullOrEmpty(c.AppName)));
            Assert.All(vm.Crashes, c => Assert.False(string.IsNullOrEmpty(c.Title)));
        });
    }

    [Fact]
    public void Filters_split_app_problems_from_pc_problems()
    {
        var vm = Loaded();
        Ui.Run(() =>
        {
            int all = vm.GroupsShown.Count;
            int crashes = vm.Crashes.Count;
            vm.Filter = "Apps";
            Assert.All(vm.GroupsShown, g => Assert.True(!g.IsIncident && !g.Latest.IsSystem));
            Assert.All(vm.Crashes, c => Assert.False(c.IsSystem));
            Assert.Equal("No app or game crashes in this period", vm.EmptyText);
            int apps = vm.GroupsShown.Count, appCrashes = vm.Crashes.Count;
            vm.Filter = "Pc";
            Assert.All(vm.GroupsShown, g => Assert.True(g.IsIncident || g.Latest.IsSystem));
            Assert.Equal("No PC crashes, driver resets or sudden shutdowns in this period", vm.EmptyText);
            Assert.Equal(all, apps + vm.GroupsShown.Count);
            Assert.Equal(crashes, appCrashes + vm.Crashes.Count);
            // Counts are of the whole period, whatever the filter.
            Assert.Equal(crashes, Counted(vm));
            vm.Filter = "All";
            Assert.Equal("No crashes in this period", vm.EmptyText);
            Assert.Equal(all, vm.GroupsShown.Count);
        });
    }

    [Fact]
    public void Grouping_repeats_shows_fewer_cards_for_the_same_crashes()
    {
        var vm = Loaded();
        Ui.Run(() =>
        {
            int single = vm.GroupsShown.Count;
            int rows = vm.GroupsShown.Sum(g => g.Count);
            vm.Grouped = true;
            Assert.True(vm.GroupsShown.Count <= single);
            Assert.Equal(rows, vm.GroupsShown.Sum(g => g.Count));
            vm.Grouped = false;
            Assert.Equal(single, vm.GroupsShown.Count);
        });
    }

    [Fact]
    public void Muting_an_app_hides_its_crashes_and_unmuting_brings_them_back()
    {
        var (vm, settings) = Page();
        var now = DateTime.Now;
        Show(vm, [Crashes.App(now.AddHours(-1), "noisy.exe"), Crashes.App(now.AddHours(-2), "noisy.exe"), Crashes.App(now.AddHours(-3), "other.exe"), Crashes.Bsod(now.AddDays(-2))]);
        Ui.Run(() =>
        {
            Assert.Equal(4, vm.Crashes.Count);
            var noisy = vm.GroupsShown.First(g => g.AppExe == "noisy.exe");
            vm.ToggleMuteCommand.Execute(noisy);
            Assert.Equal(["noisy.exe"], settings.Current.MutedCrashApps);
            Assert.DoesNotContain(vm.GroupsShown, g => g.AppExe == "noisy.exe");
            Assert.Equal(2, vm.MutedCount);
            Assert.True(vm.HasMuted);
            Assert.Equal("Show muted (2)", vm.MutedToggleText);
            Assert.Equal(2, vm.Crashes.Count);
            Assert.Equal(1, vm.AppCrashCount);
            Assert.Equal(1, vm.SystemCount);
            Assert.Equal("other.exe", vm.Latest?.Event.AppExe);

            vm.ShowMuted = true;
            var shown = vm.GroupsShown.Where(g => g.AppExe == "noisy.exe").ToList();
            Assert.Equal(2, shown.Count);
            Assert.All(shown, g => Assert.True(g.IsMuted));
            Assert.Equal(2, vm.Crashes.Count); // shown faded, not counted

            vm.ToggleMuteCommand.Execute(shown[0]);
            Assert.Empty(settings.Current.MutedCrashApps);
            Assert.Equal(4, vm.Crashes.Count);
            Assert.Equal(0, vm.MutedCount);
        });
    }

    [Fact]
    public void Muting_matches_the_app_whatever_its_case_and_ignores_pc_problems()
    {
        var (vm, settings) = Page();
        settings.Update(s => s.MutedCrashApps = ["NOISY.EXE"]);
        Show(vm, [Crashes.App(DateTime.Now.AddHours(-1), "noisy.exe"), Crashes.Bsod(DateTime.Now.AddHours(-2))]);
        Ui.Run(() =>
        {
            Assert.Equal(1, vm.MutedCount);
            var bsod = vm.GroupsShown.Single();
            vm.ToggleMuteCommand.Execute(bsod); // can't be muted
            Assert.Equal(["NOISY.EXE"], settings.Current.MutedCrashApps);
            vm.ShowMuted = true;
            vm.ToggleMuteCommand.Execute(vm.GroupsShown.Single(g => g.IsMuted)); // unmute, whatever the case
            Assert.Empty(settings.Current.MutedCrashApps);
        });
    }

    [Fact]
    public void Long_histories_show_fifty_cards_at_a_time()
    {
        var (vm, _) = Page();
        var start = DateTime.Now.AddHours(-1);
        Show(vm, Enumerable.Range(0, 120).Select(i => Crashes.App(start.AddHours(-i * 6), $"app{i % 7}.exe")));
        Ui.Run(() =>
        {
            Assert.Equal(50, vm.GroupsShown.Count);
            Assert.True(vm.HasMore);
            Assert.Equal(120, vm.Crashes.Count); // the tile counts them all
            vm.ShowMoreCommand.Execute(null);
            Assert.Equal(100, vm.GroupsShown.Count);
            vm.ShowMoreCommand.Execute(null);
            Assert.Equal(120, vm.GroupsShown.Count);
            Assert.False(vm.HasMore);
            vm.ShowMoreCommand.Execute(null);
            Assert.Equal(120, vm.GroupsShown.Count);
            // Newest first, and the first page is the newest fifty.
            Assert.Equal(vm.GroupsShown.OrderByDescending(g => g.LastTime), vm.GroupsShown);

            // A new filter starts at fifty again.
            vm.Filter = "Apps";
            Assert.Equal(50, vm.GroupsShown.Count);
            vm.ShowMoreCommand.Execute(null);
            vm.Grouped = true;
            Assert.Equal(7, vm.GroupsShown.Count);
            Assert.False(vm.HasMore);
            vm.Grouped = false;
            Assert.Equal(50, vm.GroupsShown.Count);
            vm.ShowMoreCommand.Execute(null);
            vm.ShowMuted = true;
            Assert.Equal(50, vm.GroupsShown.Count);
        });
    }

    [Fact]
    public void Opened_cards_stay_open_when_the_list_is_rebuilt()
    {
        var (vm, _) = Page();
        var rows = new[] { Crashes.App(DateTime.Now.AddHours(-1)), Crashes.App(DateTime.Now.AddHours(-30)) };
        Show(vm, rows);
        Ui.Run(() =>
        {
            vm.ToggleExpandCommand.Execute(vm.GroupsShown[0]);
            vm.ToggleDetailsCommand.Execute(vm.GroupsShown[1]);
        });
        Show(vm, rows);
        Ui.Run(() =>
        {
            Assert.True(vm.GroupsShown[0].IsExpanded);
            Assert.False(vm.GroupsShown[0].ShowDetails);
            Assert.True(vm.GroupsShown[1].ShowDetails);
        });
    }

    [Fact]
    public void Latest_is_the_newest_crash_or_nothing()
    {
        var (vm, _) = Page();
        var changed = new List<string>();
        Ui.Run(() => vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!));
        Show(vm, [Crashes.App(DateTime.Now.AddHours(-5)), Crashes.Bsod(DateTime.Now.AddHours(-1))]);
        Ui.Run(() =>
        {
            Assert.Equal(CrashKind.SystemCrash, vm.Latest!.Event.Kind);
            Assert.Contains(nameof(CrashesViewModel.Latest), changed);
            vm.Filter = "Apps";
            Assert.Equal(CrashKind.AppCrash, vm.Latest!.Event.Kind);
        });
        Show(vm, []);
        Ui.Run(() =>
        {
            Assert.Null(vm.Latest);
            Assert.False(vm.HasCrashes);
        });
    }

    [Fact]
    public void A_day_on_the_timeline_opens_that_day()
    {
        var vm = Loaded(ReportRange.Month);
        var day = DateTime.Today.AddDays(-1);
        Ui.Run(() => vm.OpenDayCommand.Execute(day.AddHours(13)));
        Ui.Run(() => Assert.True(vm.IsDay && vm.Anchor == day));
        Kit.Wait(() => vm.LoadAsync()); // the latest load wins
        Ui.Run(() =>
        {
            Assert.False(vm.ShowTimeline);
            Assert.Empty(vm.Days);
            Assert.Equal("No crashes on this day", vm.EmptyText);
            Assert.All(vm.Crashes, c => Assert.Equal(day, c.Time.Date));
            Assert.Equal(day.ToString("dddd, d MMMM yyyy"), vm.RangeNote);
            Assert.EndsWith("on this day", vm.StatusTitle);
        });
    }

    [Fact]
    public void This_months_timeline_has_a_day_for_each_day_so_far()
    {
        var vm = Loaded(ReportRange.Month);
        Ui.Run(() =>
        {
            var first = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            Assert.Equal((int)(DateTime.Today - first).TotalDays + 1, vm.Days.Count);
            Assert.Equal(first, vm.Days[0].Day);
            Assert.Equal(Counted(vm), vm.Days.Sum(d => d.Total));
            Assert.True(vm.IncludesToday);
        });
    }

    [Fact]
    public void A_refresh_without_new_crashes_leaves_the_page_alone()
    {
        var vm = Loaded();
        var shown = Ui.Run(() => vm.GroupsShown);
        Kit.Wait(() => vm.LoadAsync(onlyIfChanged: true));
        Assert.Same(shown, Ui.Run(() => vm.GroupsShown));
        Kit.Wait(() => vm.LoadAsync());
        Assert.NotSame(shown, Ui.Run(() => vm.GroupsShown));
    }

    // ── The status line ──────────────────────────────────────────────────

    [Fact]
    public void Status_all_clear()
    {
        var (vm, _) = Page();
        Show(vm, []);
        Ui.Run(() =>
        {
            Assert.Equal("All clear", vm.StatusTitle);
            Assert.Equal("No blue screens in this period · no app crashes.", vm.StatusDetail);
            Assert.Equal("GpuBrush", vm.StatusBrush);
        });
    }

    [Fact]
    public void Status_an_app_crash_is_not_serious()
    {
        var (vm, _) = Page();
        Show(vm, [Crashes.App(DateTime.Today.AddHours(-20))]);
        Ui.Run(() =>
        {
            Assert.Equal("No serious problems", vm.StatusTitle);
            Assert.Equal("No blue screens in this period · last app crash yesterday.", vm.StatusDetail);
        });
    }

    [Theory]
    [InlineData(CrashKind.SystemCrash, "Blue screen", "HotBrush")]
    [InlineData(CrashKind.UnexpectedShutdown, "Your PC shut off unexpectedly", "HotBrush")]
    [InlineData(CrashKind.GpuDriverReset, "Graphics trouble", "WarmBrush")]
    public void Status_a_recent_serious_problem(CrashKind kind, string title, string brush)
    {
        var (vm, _) = Page();
        Show(vm, [Crashes.Row(kind, DateTime.Today.AddMinutes(1))]);
        Ui.Run(() =>
        {
            Assert.Equal($"{title} today", vm.StatusTitle);
            Assert.Equal(brush, vm.StatusBrush);
        });
    }

    [Fact]
    public void Status_a_freeze_of_the_pc()
    {
        var (vm, _) = Page();
        var t = DateTime.Today.AddDays(-2).AddHours(12);
        Show(vm, [Crashes.Hang(t, "a.exe"), Crashes.Hang(t.AddMinutes(1), "b.exe"), Crashes.Hang(t.AddMinutes(2), "c.exe")]);
        Ui.Run(() =>
        {
            Assert.Equal("Your PC froze 2 days ago", vm.StatusTitle);
            Assert.Equal("WarmBrush", vm.StatusBrush);
        });
    }

    [Fact]
    public void Status_stable_since_the_last_serious_problem()
    {
        var (vm, _) = Page();
        Show(vm, [Crashes.Bsod(DateTime.Today.AddDays(-10).AddHours(12))]);
        Ui.Run(() =>
        {
            Assert.Equal("Stable for 10 days", vm.StatusTitle);
            Assert.StartsWith("Last blue screen 10 days ago", vm.StatusDetail);
        });
    }

    [Fact]
    public void Status_power_lost_while_asleep_is_not_a_fault()
    {
        var (vm, _) = Page();
        Show(vm, [Crashes.Power(DateTime.Now.AddHours(-2), sleep: true), Crashes.Power(DateTime.Now.AddHours(-30), sleep: true)]);
        Ui.Run(() =>
        {
            Assert.Equal("No serious problems", vm.StatusTitle);
            Assert.Contains("2 power losses while asleep (not a fault)", vm.StatusDetail);
            Assert.Contains(vm.Patterns, p => p.StartsWith("2 of 2 unexpected shutdowns happened while the PC was asleep"));
        });
    }

    [Fact]
    public void Status_of_a_period_that_is_over_counts_its_problems()
    {
        var (vm, _) = Page();
        SetPeriod(vm, ReportRange.Day, DateTime.Today.AddDays(-1));
        var t = DateTime.Today.AddDays(-1).AddHours(10);
        Show(vm, [Crashes.App(t), Crashes.App(t.AddHours(1), "b.exe"), Crashes.Bsod(t.AddHours(2))]);
        Ui.Run(() =>
        {
            Assert.Equal("3 problems on this day", vm.StatusTitle);
            Assert.Equal("1 blue screen · 2 app crashes.", vm.StatusDetail);
            Assert.Equal("HotBrush", vm.StatusBrush);
        });
        Show(vm, []);
        Ui.Run(() =>
        {
            Assert.Equal("No problems on this day", vm.StatusTitle);
            Assert.Equal("Nothing crashed, froze or shut down unexpectedly.", vm.StatusDetail);
        });
    }

    [Fact]
    public void Patterns_across_several_problems()
    {
        var (vm, _) = Page();
        var now = DateTime.Now;
        Show(vm, [Crashes.Reset(now.AddHours(-1)), Crashes.Reset(now.AddHours(-30)), Crashes.Row(CrashKind.AppCrash, now.AddHours(-50), "g.exe", gpu: 90)]);
        Ui.Run(() =>
        {
            Assert.Contains(vm.Patterns, p => p.StartsWith("2 problems involved the graphics driver"));
            Assert.Contains("1 crash happened while your hardware was running hot. Check airflow and fan curves.", vm.Patterns);
        });
        Show(vm, [Crashes.App(now.AddHours(-1))]);
        Ui.Run(() => Assert.Empty(vm.Patterns));
    }

    [Fact]
    public void Drivers_installed_the_week_before_are_suggested_as_causes()
    {
        var (vm, _) = Page();
        var t = DateTime.Today.AddDays(-1).AddHours(12);
        var changes = new List<SystemChange>
        {
            new(t.AddDays(-2), ChangeKind.Driver, "NVIDIA graphics driver 581.29"),
            new(t.AddDays(-1), ChangeKind.Driver, "Realtek audio driver"),
            new(t.AddHours(-3), ChangeKind.WindowsUpdate, "2026-09 Cumulative Update"),
            new(t.AddDays(-9), ChangeKind.Driver, "Old graphics driver"),
        };
        Show(vm, [Crashes.Bsod(t), Crashes.App(t.AddHours(-1), "own-bug.exe")], changes);
        Ui.Run(() =>
        {
            var bsod = vm.GroupsShown.Single(g => g.Latest.Event.Kind == CrashKind.SystemCrash);
            Assert.Equal("In the week before: NVIDIA graphics driver 581.29 (2 days before), 2026-09 Cumulative Update (same day)", bsod.ChangesText);
            // A bug in an app's own code isn't explained by a driver.
            Assert.Null(vm.GroupsShown.Single(g => g.AppExe == "own-bug.exe").ChangesText);
        });
    }
}
