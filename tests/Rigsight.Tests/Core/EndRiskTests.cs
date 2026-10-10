using Rigsight.Core.Apps;
using Rigsight.Core.Protocol;
using Rigsight.Core.Stability;

namespace Rigsight.Tests.Core;

/// <summary>
/// What the Processes page needs to know before ending something (is it a part of Windows, and which), the line the
/// agent writes for the Timeline afterwards, and what an agent or a window from before the page makes of the new fields.
/// </summary>
public sealed class EndRiskTests
{
    private const string Windows = @"C:\Windows";

    [Theory]
    [InlineData("Discord.exe", @"C:\Users\you\AppData\Local\Discord\Discord.exe", EndRisk.None)]
    [InlineData("chrome.exe", null, EndRisk.None)]
    [InlineData("csrss.exe", @"C:\Windows\System32\csrss.exe", EndRisk.Critical)]
    [InlineData("CSRSS.EXE", null, EndRisk.Critical)] // Windows keeps a protected one's place to itself: it goes by its name
    [InlineData("wininit.exe", null, EndRisk.Critical)]
    [InlineData("smss.exe", null, EndRisk.Critical)]
    [InlineData("services.exe", @"C:\Windows\System32\services.exe", EndRisk.Critical)]
    [InlineData("lsass.exe", @"C:\WINDOWS\system32\lsass.exe", EndRisk.Critical)]
    [InlineData("winlogon.exe", @"C:\Windows\System32\winlogon.exe", EndRisk.Critical)]
    [InlineData("explorer.exe", @"C:\Windows\explorer.exe", EndRisk.Explorer)]
    [InlineData("dwm.exe", @"C:\Windows\System32\dwm.exe", EndRisk.WindowManager)]
    [InlineData("Rigsight.exe", @"C:\Program Files\Rigsight\Rigsight.exe", EndRisk.OwnWindow)]
    [InlineData("rigsight.agent.exe", null, EndRisk.OwnAgent)]
    [InlineData("svchost.exe", @"C:\Windows\System32\svchost.exe", EndRisk.ByProcessOnly)]
    [InlineData("svchost.exe", null, EndRisk.ByProcessOnly)]
    [InlineData("SearchHost.exe", @"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS\SearchHost.exe", EndRisk.Windows)]
    [InlineData("conhost.exe", @"c:\windows\system32\conhost.exe", EndRisk.Windows)]
    // Only the name of a part of Windows, somewhere else: an ordinary app.
    [InlineData("csrss.exe", @"C:\Users\you\Downloads\csrss.exe", EndRisk.None)]
    [InlineData("explorer.exe", @"D:\Tools\explorer.exe", EndRisk.None)]
    [InlineData("svchost.exe", @"C:\WindowsApps\svchost.exe", EndRisk.None)]
    [InlineData("MsMpEng.exe", @"C:\ProgramData\Microsoft\Windows Defender\Platform\MsMpEng.exe", EndRisk.None)]
    public void An_app_is_classed_by_its_name_and_where_its_program_is(string exe, string? path, EndRisk expected) =>
        Assert.Equal(expected, EndRisks.Classify(exe, path, windowsDir: Windows));

    [Fact]
    public void The_service_hosts_cant_be_ended_as_one_row_but_one_of_them_can_after_a_question()
    {
        string path = @"C:\Windows\System32\svchost.exe";
        Assert.Equal(EndRisk.ByProcessOnly, EndRisks.Classify("svchost.exe", path, wholeApp: true, Windows));
        Assert.Equal(EndRisk.Windows, EndRisks.Classify("svchost.exe", path, wholeApp: false, Windows));
        // Whole or one process makes no difference to the rest.
        Assert.Equal(EndRisk.Critical, EndRisks.Classify("lsass.exe", null, wholeApp: false, Windows));
        Assert.Equal(EndRisk.Explorer, EndRisks.Classify("explorer.exe", @"C:\Windows\explorer.exe", wholeApp: false, Windows));
        Assert.Equal(EndRisk.None, EndRisks.Classify("chrome.exe", null, wholeApp: false, Windows));
    }

    [Fact]
    public void This_PCs_own_Windows_folder_is_the_one_looked_in()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Equal(EndRisk.Windows, EndRisks.Classify("notepad.exe", Path.Combine(windows, "System32", "notepad.exe")));
        Assert.Equal(EndRisk.WindowManager, EndRisks.Classify("dwm.exe", Path.Combine(windows, "System32", "dwm.exe")));
    }

    [Fact]
    public void Every_risk_says_what_will_happen_and_none_claims_a_crash()
    {
        Assert.Null(EndRisks.Sentence(EndRisk.None));
        foreach (var risk in Enum.GetValues<EndRisk>().Where(r => r != EndRisk.None))
        {
            string sentence = EndRisks.Sentence(risk)!;
            Assert.EndsWith(".", sentence);
            Assert.DoesNotContain("blue screen", sentence, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("crash", sentence, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal("The taskbar, Start menu and desktop icons disappear until it starts again.", EndRisks.Sentence(EndRisk.Explorer));
        Assert.Equal("The screen goes black for a moment, then Windows starts it again.", EndRisks.Sentence(EndRisk.WindowManager));
        Assert.Equal("This window closes.", EndRisks.Sentence(EndRisk.OwnWindow));
        Assert.Equal("Nothing is recorded until it starts again.", EndRisks.Sentence(EndRisk.OwnAgent));
        Assert.Equal("This is part of Windows. Ending it may make Windows or other apps stop working.", EndRisks.Sentence(EndRisk.Windows));
        Assert.Equal("Open it and end one service at a time.", EndRisks.Sentence(EndRisk.ByProcessOnly));
        Assert.StartsWith("Windows depends on this process.", EndRisks.Sentence(EndRisk.Critical));
    }

    // ── The Timeline's line ──

    private static readonly DateTime At = new(2026, 10, 10, 15, 12, 0);

    [Fact]
    public void What_was_ended_goes_to_the_agent_and_comes_back_as_a_line()
    {
        var change = TaskEnded.Parse(TaskEnded.Format("Discord", "Discord.exe", 1126.4), At)!;
        Assert.Equal((At, ChangeKind.TaskEnded, "Discord ended from Processes", "Discord.exe"), (change.Time, change.Kind, change.Title, change.Subject));
        Assert.Equal("It held 1.1 GB.", change.Detail);
        Assert.Equal("Discord ended from Processes", change.Line);
        Assert.False(change.IsApproximate);
        Assert.False(change.IsSystemLevel); // never offered as the cause of a crash

        // A name may hold the separator (a window's title); nothing held says nothing about it.
        var tab = TaskEnded.Parse(TaskEnded.Format("Google Chrome · a | b", "chrome.exe", 0), At)!;
        Assert.Equal("Google Chrome · a | b ended from Processes", tab.Title);
        Assert.Null(tab.Detail);
        Assert.Equal("chrome.exe|1126.4|Discord", TaskEnded.Format("Discord", "chrome.exe", 1126.4));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Discord.exe")]
    [InlineData("Discord.exe|12")]
    [InlineData("|12|Discord")]
    [InlineData("Discord.exe|12|")]
    [InlineData("Discord.exe|lots|Discord")]
    [InlineData("Discord.exe|-5|Discord")]
    [InlineData("Discord.exe|NaN|Discord")]
    public void Anything_else_sent_as_that_command_is_ignored(string? arg) => Assert.Null(TaskEnded.Parse(arg, At));

    [Fact]
    public void A_name_too_long_to_be_one_is_ignored() => Assert.Null(TaskEnded.Parse($"a.exe|1|{new string('x', 201)}", At));

    // ── Older and newer ends of the pipe ──

    [Fact]
    public void An_older_agents_list_reads_as_no_disk_use_and_no_start_time()
    {
        var got = ProtocolJson.Deserialize<AgentMessage>("""{"T":"procs","Procs":[{"Exe":"a.exe","Name":"A","Count":2,"Cpu":1.5,"MemMB":300,"HasWindow":true,"Processes":[{"Pid":7,"Label":"A","Cpu":1,"MemMB":150}]}]}""")!;
        Assert.Null(got.Disk);
        Assert.Equal(0, got.Procs![0].Disk);
        Assert.Equal(0, got.Procs[0].Started);
        Assert.Equal(0, got.Procs[0].Processes![0].Disk);
    }

    [Fact]
    public void Disk_use_and_start_time_travel_with_the_list()
    {
        var message = new AgentMessage
        {
            T = "procs", Disk = 5_000_000,
            Procs = [new ProcInfo { Exe = "a.exe", Name = "A", Disk = 2_000_000, Started = 1_790_000_000, Processes = [new ProcDetail { Pid = 7, Disk = 1_500_000 }] }],
        };
        var got = ProtocolJson.Deserialize<AgentMessage>(ProtocolJson.Serialize(message))!;
        Assert.Equal(5_000_000, got.Disk);
        Assert.Equal(2_000_000, got.Procs![0].Disk);
        Assert.Equal(1_790_000_000, got.Procs[0].Started);
        Assert.Equal(1_500_000, got.Procs[0].Processes![0].Disk);

        // An app at rest (most of them, at any moment) adds nothing to the list sent every two seconds.
        string resting = ProtocolJson.Serialize(new ProcInfo { Exe = "a.exe", Name = "A", Processes = [new ProcDetail { Pid = 7 }] });
        Assert.DoesNotContain("Disk", resting);
        Assert.Equal(0, ProtocolJson.Deserialize<ProcInfo>(resting)!.Disk);
    }
}
