using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Win32;
using Rigsight.Agent;
using Rigsight.Agent.Widgets;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Agent;

/// <summary>Finding RivaTuner however it was installed (setup must not offer to install it again).</summary>
public sealed class RtssFindTests
{
    private const string Rtss = @"C:\Program Files (x86)\RivaTuner Statistics Server\RTSS.exe";
    private const string Custom = @"D:\Tools\RTSS\RTSS.exe";

    private static RtssSetup.Sources Pc(
        Dictionary<(RegistryView, string), string>? registry = null,
        (string?, string?, string?, string?)[]? installed = null,
        string?[]? running = null,
        string[]? files = null,
        bool isRunning = false) => new(
        (view, _, value) => registry?.GetValueOrDefault((view, value)),
        () => installed ?? [],
        () => running ?? [],
        () => isRunning,
        path => (files ?? []).Contains(path, StringComparer.OrdinalIgnoreCase),
        f => f == Environment.SpecialFolder.ProgramFilesX86 ? @"C:\Program Files (x86)" : @"C:\Program Files");

    [Fact]
    public void Nothing_anywhere_is_not_installed() => Assert.Null(RtssSetup.Find(Pc()));

    [Fact]
    public void Its_registry_folder_is_found() =>
        Assert.Equal(Custom, RtssSetup.Find(Pc(new() { [(RegistryView.Registry32, "InstallDir")] = @"D:\Tools\RTSS" }, files: [Custom])));

    [Fact]
    public void Its_registry_program_path_is_found() =>
        Assert.Equal(Custom, RtssSetup.Find(Pc(new() { [(RegistryView.Registry32, "InstallPath")] = Custom }, files: [Custom])));

    [Fact]
    public void The_64_bit_registry_counts_too() =>
        Assert.Equal(Custom, RtssSetup.Find(Pc(new() { [(RegistryView.Registry64, "InstallDir")] = @"D:\Tools\RTSS\" }, files: [Custom])));

    [Fact]
    public void A_registry_entry_left_by_an_uninstall_is_not_an_install() =>
        Assert.Null(RtssSetup.Find(Pc(new() { [(RegistryView.Registry32, "InstallDir")] = @"D:\Tools\RTSS", [(RegistryView.Registry32, "InstallPath")] = Custom })));

    [Theory]
    [InlineData("RivaTuner Statistics Server 7.3.7", null, "\"D:\\Tools\\RTSS\\uninstall.exe\"", null)]   // as RivaTuner registers itself
    [InlineData("RivaTuner Statistics Server", "D:\\Tools\\RTSS", null, null)]
    [InlineData("RivaTuner Statistics Server 7.3.4", null, null, "D:\\Tools\\RTSS\\RTSS.exe,0")]
    [InlineData("rivatuner statistics server", " D:\\Tools\\RTSS ", null, null)]
    public void The_installed_programs_list_is_read(string name, string? location, string? uninstaller, string? icon) =>
        Assert.Equal(Custom, RtssSetup.Find(Pc(installed: [("Something else", @"D:\Tools\RTSS", null, null), (name, location, uninstaller, icon)], files: [Custom])));

    [Fact]
    public void Another_program_in_the_same_folder_is_not_rivatuner() =>
        Assert.Null(RtssSetup.Find(Pc(installed: [("MSI Afterburner 4.6.6", @"D:\Tools\RTSS", null, null)], files: [Custom])));

    [Fact]
    public void A_running_copy_is_found_wherever_it_is() =>
        Assert.Equal(Custom, RtssSetup.Find(Pc(running: [null, Custom], files: [Custom])));

    [Fact]
    public void The_usual_folder_is_found_without_any_registry() =>
        Assert.Equal(Rtss, RtssSetup.Find(Pc(files: [Rtss])));

    private static string Folders(Environment.SpecialFolder f) =>
        f == Environment.SpecialFolder.ProgramFilesX86 ? @"C:\Program Files (x86)" : @"C:\Program Files";

    [Theory]
    [InlineData(@"C:\Program Files (x86)\RivaTuner Statistics Server\RTSS.exe", true)]
    [InlineData(@"C:\Program Files\RivaTuner Statistics Server\RTSS.exe", true)]
    [InlineData(@"c:\program files (x86)\RivaTuner Statistics Server\RTSS.exe", true)]
    [InlineData(@"D:\Tools\RTSS\RTSS.exe", false)]
    [InlineData(@"C:\Users\me\AppData\Local\RTSS\RTSS.exe", false)]
    [InlineData(@"C:\Program Files Evil\RTSS.exe", false)]                           // only a look-alike name
    [InlineData(@"C:\Program Files\..\Users\me\RTSS.exe", false)]                // climbs back out
    [InlineData(@"RTSS.exe", false)]
    [InlineData(@"C:\Program Files", false)]
    public void RivaTuner_is_started_with_admin_rights_only_from_program_files(string exe, bool trusted) =>
        Assert.Equal(trusted, RtssSetup.InTrustedFolder(exe, Folders));

    [Fact]
    public void This_pcs_rivatuner_matches_what_windows_says() =>
        Assert.Equal(RtssSetup.Find() is not null, RtssSetup.IsInstalled() && (RtssSetup.Find() is not null || Process.GetProcessesByName("RTSS").Length > 0));
}

/// <summary>
/// Telling a slow install from a stuck one, with stand-in installers (PowerShell scripts) and short limits: busy for
/// longer than the idle limit is fine, a question on screen is waited for, and only silence ends it.
/// </summary>
[Trait("Category", "Machine")]
public sealed class InstallWatchTests
{
    private static ProcessStartInfo Script(string script) =>
        new("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script]) { UseShellExecute = false, CreateNoWindow = true };

    private static InstallWatch Quick(Action<string>? state = null) => new()
    {
        SampleEvery = TimeSpan.FromMilliseconds(250),
        IdleLimit = TimeSpan.FromSeconds(3),
        HardLimit = TimeSpan.FromMinutes(2),
        StateChanged = state,
    };

    [Fact]
    public void Busy_for_longer_than_the_idle_limit_is_left_to_finish()
    {
        var r = Quick().Run(Script("$end = (Get-Date).AddSeconds(8); while ((Get-Date) -lt $end) { $x = 0; foreach ($i in 1..20000) { $x += $i } }; exit 7"));
        Assert.Equal(WatchOutcome.Exited, r.Outcome);
        Assert.Equal(7, r.ExitCode);
        Assert.True(r.Elapsed >= TimeSpan.FromSeconds(7), $"ended after {r.Elapsed}");
    }

    [Fact]
    public void Short_pauses_between_bursts_of_work_are_not_stuck()
    {
        // Like a download on a poor connection: a little now and then, quiet in between (less than the idle limit).
        var r = Quick().Run(Script("foreach ($n in 1..5) { $x = 0; foreach ($i in 1..30000) { $x += $i }; Start-Sleep -Milliseconds 1500 }; exit 0"));
        Assert.Equal(WatchOutcome.Exited, r.Outcome);
        Assert.Equal(0, r.ExitCode);
    }

    [Fact]
    public void Writing_a_file_slowly_counts_as_progress()
    {
        var file = Path.Combine(TestEnvironment.NewFolder("slow-write"), "download.part");
        var r = Quick().Run(Script($"foreach ($n in 1..16) {{ Add-Content -Path '{file}' -Value ('x' * 65536); Start-Sleep -Milliseconds 500 }}; exit 0"));
        Assert.Equal(WatchOutcome.Exited, r.Outcome);
        Assert.True(new FileInfo(file).Length > 16 * 65536);
    }

    [Fact]
    public void Doing_nothing_at_all_is_stuck_and_ended()
    {
        var states = new ConcurrentQueue<string>();
        var sw = Stopwatch.StartNew();
        var r = Quick(states.Enqueue).Run(Script("Start-Sleep -Seconds 120"));
        Assert.Equal(WatchOutcome.Stuck, r.Outcome);
        Assert.InRange(sw.Elapsed.TotalSeconds, 3, 20);
        Assert.Contains("idle", states);
    }

    [Fact]
    public void A_stuck_install_is_ended_with_everything_it_started()
    {
        // The installer's own installer (winget → RivaTuner's setup): the whole tree goes.
        var marker = "rigsight-watch-" + Guid.NewGuid().ToString("N")[..8];
        var r = Quick().Run(Script($"Start-Process powershell.exe -ArgumentList '-NoProfile','-Command','Start-Sleep 120 # {marker}' -WindowStyle Hidden; Start-Sleep 120"));
        Assert.Equal(WatchOutcome.Stuck, r.Outcome);
        Thread.Sleep(500);
        Assert.DoesNotContain(Process.GetProcessesByName("powershell"), p => (Rigsight.Agent.Native.Win32.ProcessCommandLine(p.Id) ?? "").Contains(marker));
    }

    [Fact]
    public async Task A_question_on_screen_is_waited_for_not_ended()
    {
        // A window (off-screen here) as RivaTuner's installer would show to ask something: never "stuck".
        var states = new ConcurrentQueue<string>();
        var watch = Quick(states.Enqueue);
        var run = Task.Run(() => watch.Run(Script(
            "Add-Type -AssemblyName System.Windows.Forms; $f = New-Object System.Windows.Forms.Form; $f.StartPosition = 'Manual'; " +
            "$f.Location = New-Object System.Drawing.Point(-32000, -32000); $f.ShowInTaskbar = $false; $f.Text = 'Close RivaTuner?'; [void]$f.ShowDialog()")));
        Assert.True(SpinWait.SpinUntil(() => states.Contains("asking"), TimeSpan.FromSeconds(20)), "the window wasn't seen");
        Thread.Sleep(5000); // more than the idle limit
        Assert.False(run.IsCompleted, "a question on screen was ended");

        // Answered (closed): the install carries on and finishes.
        foreach (var p in Process.GetProcessesByName("powershell"))
            if ((Rigsight.Agent.Native.Win32.ProcessCommandLine(p.Id) ?? "").Contains("Close RivaTuner?")) p.Kill();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Equal(WatchOutcome.Exited, result.Outcome);
    }

    [Fact]
    public void A_program_that_cant_start_says_so() =>
        Assert.Equal(WatchOutcome.NotStarted, Quick().Run(new ProcessStartInfo(@"C:\nowhere\setup.exe") { UseShellExecute = false }).Outcome);

    [Fact]
    public void Going_on_past_the_hard_limit_is_ended()
    {
        var watch = new InstallWatch { SampleEvery = TimeSpan.FromMilliseconds(250), IdleLimit = TimeSpan.FromMinutes(1), HardLimit = TimeSpan.FromSeconds(3) };
        var r = watch.Run(Script("while ($true) { $x = 0; foreach ($i in 1..20000) { $x += $i } }"));
        Assert.Equal(WatchOutcome.TooLong, r.Outcome);
    }
}
