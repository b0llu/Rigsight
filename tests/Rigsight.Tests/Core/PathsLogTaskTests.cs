using System.Security;
using System.Text;
using Rigsight.Core;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Core;

public sealed class RigsightPathsTests
{
    [Fact]
    public void A_test_run_is_a_test_instance_with_its_own_names()
    {
        Assert.True(RigsightPaths.IsTestInstance);
        Assert.Matches("^\\.[0-9A-F]{12}$", RigsightPaths.InstanceSuffix);
        Assert.Equal("Rigsight.Agent.v1" + RigsightPaths.InstanceSuffix, RigsightPaths.PipeName);
        Assert.Equal(@"Local\Rigsight.Agent" + RigsightPaths.InstanceSuffix, RigsightPaths.AgentMutex);
        Assert.Equal(@"Local\Rigsight.Agent.Quit" + RigsightPaths.InstanceSuffix, RigsightPaths.AgentQuitEvent);
        Assert.NotEqual("Rigsight.Agent.v1", RigsightPaths.PipeName);
        Assert.NotEqual(@"Local\Rigsight.Agent", RigsightPaths.AgentMutex);
        Assert.NotEqual(@"Local\Rigsight.Agent.Quit", RigsightPaths.AgentQuitEvent);
    }

    [Fact]
    public void The_real_copy_has_no_suffix() => Assert.Equal("", RigsightPaths.SuffixFor(null));

    [Fact]
    public void The_suffix_belongs_to_the_folder()
    {
        Assert.Equal(RigsightPaths.InstanceSuffix, RigsightPaths.SuffixFor(TestEnvironment.DataDir));
        Assert.Equal(RigsightPaths.SuffixFor(@"C:\Data\Rigsight"), RigsightPaths.SuffixFor(@"C:\Data\Rigsight"));
        Assert.Equal(RigsightPaths.SuffixFor(@"C:\Data\Rigsight"), RigsightPaths.SuffixFor(@"c:\DATA\rigsight"));
        Assert.NotEqual(RigsightPaths.SuffixFor(@"C:\Data\Rigsight"), RigsightPaths.SuffixFor(@"C:\Data\Rigsight2"));
        Assert.NotEqual(RigsightPaths.SuffixFor(@"C:\Data\A"), RigsightPaths.SuffixFor(@"C:\Data\B"));
    }

    [Fact]
    public void Everything_lives_in_the_data_folder()
    {
        Assert.Equal(TestEnvironment.DataDir, RigsightPaths.DataDir);
        Assert.Equal(Path.Combine(RigsightPaths.DataDir, "settings.json"), RigsightPaths.SettingsFile);
        Assert.Equal(Path.Combine(RigsightPaths.DataDir, "rigsight.db"), RigsightPaths.Database);
        Assert.Equal(Path.Combine(RigsightPaths.DataDir, "icons"), RigsightPaths.IconCache);
        Assert.Equal(Path.Combine(RigsightPaths.DataDir, "rigsight.log"), RigsightPaths.LogFile);
        Assert.Equal(Path.Combine(RigsightPaths.DataDir, "updates"), Rigsight.Core.Updates.UpdateStore.Folder);
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rigsight");
        Assert.NotEqual(real, RigsightPaths.DataDir, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Siblings_are_next_to_this_program()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Rigsight.Agent.exe"), RigsightPaths.Sibling(RigsightPaths.AgentExe));
        Assert.Equal("Rigsight.exe", RigsightPaths.AppExe);
        Assert.Equal("Rigsight Agent", RigsightPaths.AgentTaskName);
    }
}

public sealed class LogTests
{
    private static string NewLog() => Path.Combine(TestEnvironment.NewFolder("log"), "rigsight.log");

    [Fact]
    public void Lines_are_appended_with_time_and_source()
    {
        var file = NewLog();
        Log.WriteTo(file, "test", "first");
        Log.WriteTo(file, "other", "second");
        var lines = File.ReadAllLines(file);
        Assert.Equal(2, lines.Length);
        Assert.Matches(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] \[test\] first$", lines[0]);
        Assert.EndsWith("[other] second", lines[1]);
    }

    [Fact]
    public void A_line_isnt_lost_while_the_other_program_is_writing()
    {
        // The agent and the app share one log: while one of them has it open to write, the other's line must still land.
        var file = NewLog();
        Log.WriteTo(file, "app", "first");
        using (var other = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            other.Write("[agent line]\r\n"u8);
            other.Flush();
            Log.WriteTo(file, "app", "while the agent writes");
        }
        var text = File.ReadAllText(file);
        Assert.Contains("[app] while the agent writes", text);
        Assert.Contains("[agent line]", text);
    }

    [Fact]
    public void Two_programs_writing_at_once_keep_every_line()
    {
        // Another process appending as fast as it can (the way Log does: append-only), while this one logs.
        var file = NewLog();
        Log.WriteTo(file, "app", "start");
        var script = $"$f = New-Object IO.FileStream('{file}', [IO.FileMode]::Append, [Security.AccessControl.FileSystemRights]::AppendData, [IO.FileShare]'ReadWrite, Delete', 4096, [IO.FileOptions]::None); " +
                     "$b = [Text.Encoding]::UTF8.GetBytes(\"[agent] busy`r`n\"); " +
                     "for ($i = 0; $i -lt 30000; $i++) { $f.Write($b, 0, $b.Length); $f.Flush() }; $f.Close()";
        using var other = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe", ["-NoProfile", "-Command", script]) { UseShellExecute = false, CreateNoWindow = true })!;
        // Only once the other one is writing (PowerShell takes a moment to start), so the two really overlap.
        Assert.True(SpinWait.SpinUntil(() => new FileInfo(file).Length > 2000, 30_000), "the other writer never started");
        for (int i = 0; i < 300; i++) Log.WriteTo(file, "app", $"line {i}");
        bool overlapped = !other.HasExited;
        other.WaitForExit(60_000);
        Assert.Equal(0, other.ExitCode);
        Assert.True(overlapped, "the other writer finished first: nothing was tested");
        var lines = File.ReadAllLines(file);
        Assert.Equal(300, lines.Count(l => l.Contains("[app] line ")));
        Assert.Equal(30000, lines.Count(l => l == "[agent] busy"));
    }

    [Fact]
    public void Only_the_program_that_rotates_moves_a_full_log_aside()
    {
        // The app (not rotating) adds to a full log; the agent moves it to .old and starts a new one.
        var file = NewLog();
        File.WriteAllText(file, new string('x', 1_000_100));
        Log.WriteTo(file, "app", "still here", rotate: false);
        Assert.False(File.Exists(file + ".old"));
        Assert.EndsWith("[app] still here" + Environment.NewLine, File.ReadAllText(file));
        Log.WriteTo(file, "agent", "fresh start");
        Assert.True(File.Exists(file + ".old"));
        Assert.EndsWith("[app] still here" + Environment.NewLine, File.ReadAllText(file + ".old"));
        Assert.EndsWith("[agent] fresh start" + Environment.NewLine, File.ReadAllText(file));
        Assert.False(Log.Rotates); // the tests (like the app) never rotate the shared log
    }

    [Fact]
    public void The_folder_is_created_when_missing()
    {
        var file = Path.Combine(TestEnvironment.NewFolder("log"), "a", "b", "rigsight.log");
        Log.WriteTo(file, "x", "y");
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void A_log_over_a_megabyte_moves_to_old()
    {
        var file = NewLog();
        File.WriteAllText(file, new string('a', 1_000_001));
        File.WriteAllText(file + ".old", "older");
        Log.WriteTo(file, "x", "after");
        Assert.Equal(1_000_001, new FileInfo(file + ".old").Length);
        Assert.Single(File.ReadAllLines(file));
        Assert.EndsWith("[x] after", File.ReadAllText(file).TrimEnd());
    }

    [Fact]
    public void A_log_of_exactly_a_megabyte_is_kept()
    {
        var file = NewLog();
        File.WriteAllText(file, new string('a', 1_000_000));
        Log.WriteTo(file, "x", "after");
        Assert.False(File.Exists(file + ".old"));
        Assert.True(new FileInfo(file).Length > 1_000_000);
    }

    [Fact]
    public void Writing_a_lot_keeps_the_log_small()
    {
        var file = NewLog();
        var line = new string('z', 10_000);
        for (int i = 0; i < 250; i++) Log.WriteTo(file, "x", line);
        Assert.True(new FileInfo(file).Length < 1_100_000);
        Assert.True(new FileInfo(file + ".old").Length < 1_100_000);
    }

    [Fact]
    public void Writes_from_many_threads_are_whole_lines()
    {
        var file = NewLog();
        Parallel.For(0, 400, i => Log.WriteTo(file, "t", $"line {i}"));
        var lines = File.ReadAllLines(file);
        Assert.Equal(400, lines.Length);
        Assert.Equal(400, lines.Select(l => l[(l.LastIndexOf(' ') + 1)..]).Distinct().Count());
    }

    [Fact]
    public void Errors_are_written_with_their_details()
    {
        var file = NewLog();
        Exception ex;
        try { throw new InvalidOperationException("boom"); } catch (Exception e) { ex = e; }
        Log.WriteTo(file, "test", ex.ToString());
        var text = File.ReadAllText(file);
        Assert.Contains("System.InvalidOperationException: boom", text);
        Assert.Contains(nameof(Errors_are_written_with_their_details), text);
    }

    [Fact]
    public void Unicode_and_newlines_are_kept()
    {
        var file = NewLog();
        Log.WriteTo(file, "ü", "Grüße — 温度 °C\nsecond line");
        Assert.Contains("[ü] Grüße — 温度 °C\nsecond line", File.ReadAllText(file, Encoding.UTF8));
    }

    [Fact]
    public void Logging_never_throws()
    {
        var folder = TestEnvironment.NewFolder("log");
        Log.WriteTo(folder, "x", "a folder, not a file");
        Log.WriteTo(Path.Combine(folder, "bad\0name.log"), "x", "invalid name");
        Log.WriteTo("", "x", "empty path");
        Log.WriteTo(@"Z:\\\\?\\nope\\x.log", "x", "no such drive");
        var locked = NewLog();
        using (new FileStream(locked, FileMode.Create, FileAccess.Write, FileShare.None))
            Log.WriteTo(locked, "x", "locked");
        Log.Write("smoke", "the real log of this test run");
        Log.Error("smoke", new Exception("not a real error"));
    }
}

public sealed class AgentTaskTests
{
    private const string Exe = @"C:\Program Files\Rigsight\Rigsight.Agent.exe";

    /// <summary>A task as "schtasks /Query /XML" prints it.</summary>
    private static string TaskXml(string command, string extra = "") => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <URI>\Rigsight Agent</URI>
          </RegistrationInfo>
          <Principals>
            <Principal id="Author">
              <UserId>S-1-5-21-1</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Actions Context="Author">
            <Exec>
              <Command>{command}</Command>
            </Exec>{extra}
          </Actions>
        </Task>
        """;

    [Fact]
    public void A_task_that_starts_this_agent_points_to_it()
    {
        Assert.True(AgentTask.XmlPointsTo(TaskXml(Exe), Exe));
        Assert.True(AgentTask.XmlPointsTo(TaskXml(Exe.ToUpperInvariant()), Exe));
        Assert.True(AgentTask.XmlPointsTo(TaskXml("\n        " + Exe + "\n      "), Exe));
    }

    [Theory]
    [InlineData(@"C:\Old\Rigsight\Rigsight.Agent.exe")]
    [InlineData(@"C:\Program Files\Rigsight\Rigsight.exe")]
    [InlineData(@"C:\Program Files\Rigsight\Rigsight.Agent.exe.old")]
    [InlineData(@"C:\Program Files\Rigsight\Rigsight.Agent.exe --open")]
    [InlineData("")]
    public void A_task_left_by_another_install_does_not(string command) =>
        Assert.False(AgentTask.XmlPointsTo(TaskXml(command), Exe));

    [Theory]
    [InlineData(@"D:\R&D\Rigsight\Rigsight.Agent.exe")]
    [InlineData(@"C:\Users\O'Brien\Apps\Rigsight\Rigsight.Agent.exe")]
    [InlineData(@"C:\Users\<odd>\Rigsight.Agent.exe")]
    [InlineData(@"C:\Program Files\Rigsight ""x""\Rigsight.Agent.exe")]
    [InlineData(@"C:\Programme\Überwachung\Rigsight.Agent.exe")]
    public void Paths_with_characters_XML_escapes_are_recognised(string exe)
    {
        // As registered (everything escaped), and as Windows may print it back (only what XML requires).
        Assert.True(AgentTask.XmlPointsTo(TaskXml(SecurityElement.Escape(exe)), exe));
        Assert.True(AgentTask.XmlPointsTo(TaskXml(exe.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")), exe));
    }

    [Fact]
    public void One_of_several_actions_is_enough()
    {
        var xml = TaskXml(@"C:\Other\tool.exe", $"\n    <Exec>\n      <Command>{Exe}</Command>\n      <Arguments>--open</Arguments>\n    </Exec>");
        Assert.True(AgentTask.XmlPointsTo(xml, Exe));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ERROR: The system cannot find the file specified.")]
    [InlineData("<Task><Actions>")]
    [InlineData("<Task/>")]
    public void No_task_or_an_error_does_not(string output) => Assert.False(AgentTask.XmlPointsTo(output, Exe));

    [Fact]
    public void Unparsable_output_that_still_names_the_agent_is_recognised() =>
        Assert.True(AgentTask.XmlPointsTo($"garbage <Command>{Exe}</Command> <unclosed>", Exe));
}
