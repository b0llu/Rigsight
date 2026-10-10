using Rigsight.Agent.Tracking;
using Rigsight.Core.Apps;
using Rigsight.Core.Protocol;
using Rigsight.Models;
using Rigsight.ViewModels;

namespace Rigsight.Tests.Agent;

/// <summary>
/// Windows' own mark on a process it can't lose: the agent asks once for each process (a young one again, until it
/// has settled), and the Processes page gives a marked process the strong warning whatever it is called. Windows is
/// never asked here: the tests answer for it.
/// </summary>
public sealed class CriticalMarksTests
{
    private static readonly long Second = TimeSpan.TicksPerSecond;
    private const long Now = 1_000_000_000_000;

    private static (CriticalMarks Marks, List<int> Asked) Marks(Func<int, bool?> answer)
    {
        var asked = new List<int>();
        var marks = new CriticalMarks
        {
            Read = pid =>
            {
                asked.Add(pid);
                return answer(pid);
            },
        };
        return (marks, asked);
    }

    [Fact]
    public void Each_process_is_asked_about_once_and_the_marked_ones_come_back()
    {
        var (marks, asked) = Marks(pid => pid is 600 or 700);
        List<(int, long)> running = [(600, Now - 3600 * Second), (700, Now - 3600 * Second), (800, Now - 3600 * Second)];
        Assert.Equal([600, 700], marks.Marked(running, Now).Order());
        Assert.Equal([600, 700, 800], asked);

        // Two seconds on: nothing is asked again, and the answer is the same.
        Assert.Equal([600, 700], marks.Marked(running, Now + 2 * Second).Order());
        Assert.Equal(3, asked.Count);

        // One that has gone is forgotten; its number given to a later process is asked about anew.
        running = [(600, Now - 3600 * Second), (700, Now - 60 * Second)];
        Assert.Equal([600], marks.Marked(running, Now + 4 * Second).Order().Where(pid => pid == 600));
        Assert.Equal([600, 700, 800, 700], asked);
    }

    [Fact]
    public void A_process_that_has_just_started_is_asked_about_again_until_it_has_settled()
    {
        // A service host marks itself a moment after it starts.
        bool markedYet = false;
        var (marks, asked) = Marks(_ => markedYet);
        List<(int, long)> running = [(900, Now)];
        Assert.Empty(marks.Marked(running, Now + 1 * Second));
        markedYet = true;
        Assert.Equal([900], marks.Marked(running, Now + 3 * Second));
        Assert.Equal(2, asked.Count);

        // Settled: what it says now is kept.
        Assert.Equal([900], marks.Marked(running, Now + 40 * Second));
        Assert.Equal([900], marks.Marked(running, Now + 42 * Second));
        Assert.Equal(3, asked.Count);
    }

    [Fact]
    public void A_process_Windows_wont_say_anything_about_counts_as_not_marked()
    {
        // An agent without admin rights is refused for Windows' own processes.
        var (marks, asked) = Marks(_ => null);
        List<(int, long)> running = [(600, Now - 3600 * Second)];
        Assert.Empty(marks.Marked(running, Now));
        Assert.Empty(marks.Marked(running, Now + 2 * Second));
        Assert.Single(asked);
    }

    [Fact]
    public void This_PCs_own_processes_can_be_asked_about()
    {
        // Only read: this test's own process is an ordinary one, and a process that isn't there gets no answer.
        Assert.False(Rigsight.Agent.Native.Win32.IsProcessCritical(Environment.ProcessId));
        Assert.Null(Rigsight.Agent.Native.Win32.IsProcessCritical(-1));
    }

    private const string Windows = @"C:\Windows";

    [Fact]
    public void A_marked_process_gets_the_strong_warning_whatever_it_is_called_and_wherever_it_is()
    {
        Assert.Equal(EndRisk.Critical, EndRisks.Classify("svchost.exe", @"C:\Windows\System32\svchost.exe", wholeApp: false, Windows, marked: true));
        Assert.Equal(EndRisk.Critical, EndRisks.Classify("guard.exe", @"C:\Program Files\Guard\guard.exe", wholeApp: true, Windows, marked: true));
        Assert.Equal(EndRisk.Critical, EndRisks.Classify("csrss.exe", null, wholeApp: true, Windows, marked: true));
        // The service hosts as one row still can't be ended as one.
        Assert.Equal(EndRisk.ByProcessOnly, EndRisks.Classify("svchost.exe", @"C:\Windows\System32\svchost.exe", wholeApp: true, Windows, marked: true));
        // Rigsight's own stay what they are.
        Assert.Equal(EndRisk.OwnAgent, EndRisks.Classify("Rigsight.Agent.exe", null, wholeApp: true, Windows, marked: true));

        // No mark (none read, or an older agent): the few known by name keep their warning, the rest are as before.
        Assert.Equal(EndRisk.Critical, EndRisks.Classify("lsass.exe", null, wholeApp: true, Windows));
        Assert.Equal(EndRisk.Windows, EndRisks.Classify("svchost.exe", @"C:\Windows\System32\svchost.exe", wholeApp: false, Windows));
        Assert.Equal(EndRisk.None, EndRisks.Classify("guard.exe", @"C:\Program Files\Guard\guard.exe", wholeApp: true, Windows));
    }

    [Fact]
    public void The_mark_travels_from_the_agent_to_the_question()
    {
        string line = ProtocolJson.Serialize(new AgentMessage
        {
            T = "procs",
            Procs =
            [
                new ProcInfo
                {
                    Exe = "svchost.exe", Name = "Windows service", Path = @"C:\Windows\System32\svchost.exe", Count = 2, Critical = true,
                    Processes = [new ProcDetail { Pid = 1588, Label = "DCOM Server", Critical = true }, new ProcDetail { Pid = 4000, Label = "Windows Audio" }],
                },
                new ProcInfo { Exe = "chrome.exe", Name = "Google Chrome", Count = 1 },
            ],
        });
        // Said only where it is so: most processes carry nothing more.
        Assert.Equal(2, line.Split("\"Critical\":true").Length - 1);
        Assert.DoesNotContain("\"Critical\":false", line);

        var procs = ProtocolJson.Deserialize<AgentMessage>(line)!.Procs!;
        var hosts = new ProcRow("svchost.exe");
        hosts.Update(procs[0]);
        var dcom = new ProcChild(procs[0].Processes![0]);
        var audio = new ProcChild(procs[0].Processes![1]);
        Assert.True(hosts.Critical);
        Assert.Equal(EndRisk.Critical, new EndTarget(hosts, dcom).Risk);
        Assert.Equal(EndRisk.Windows, new EndTarget(hosts, audio).Risk);
        Assert.Equal(EndRisk.ByProcessOnly, new EndTarget(hosts).Risk);

        var question = ProcessesViewModel.QuestionFor([new EndTarget(hosts, dcom)])!;
        Assert.True(question.NeedsTick);
        Assert.StartsWith("Windows depends on this process.", question.Body);
        Assert.False(ProcessesViewModel.QuestionFor([new EndTarget(hosts, audio)])!.NeedsTick);

        var chrome = new ProcRow("chrome.exe");
        chrome.Update(procs[1]);
        Assert.False(chrome.Critical);
        Assert.Equal(EndRisk.None, new EndTarget(chrome).Risk);
    }
}
