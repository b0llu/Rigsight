using System.Diagnostics;
using Rigsight.Agent.Tracking;

namespace Rigsight.Tests.Agent;

/// <summary>Sampling this PC's processes (read-only): whatever is running, the numbers must be sane.</summary>
[Trait("Category", "Machine")]
public class ProcessSamplerTests
{
    private static readonly string Me = Path.GetFileName(Environment.ProcessPath)!;

    private static ProcessSnapshot TwoSamples(ProcessSampler sampler)
    {
        sampler.Sample();
        Thread.Sleep(300); // CPU use is measured between two samples
        return sampler.Sample();
    }

    [Fact]
    public void Every_process_is_grouped_under_its_exe()
    {
        var snapshot = TwoSamples(new ProcessSampler());
        Assert.True(snapshot.Apps.Count > 10, $"only {snapshot.Apps.Count} apps");
        Assert.Contains(Me, snapshot.Apps.Keys);
        Assert.Equal(Me, snapshot.PidToExe[Environment.ProcessId]);
        Assert.Equal(snapshot.Apps.Values.Sum(a => a.Count), snapshot.PidToExe.Count);
        Assert.True(snapshot.Apps["svchost.exe"].Count > 1);
        Assert.DoesNotContain(snapshot.PidToExe.Keys, pid => pid <= 4); // Idle and System aren't apps
        foreach (var app in snapshot.Apps.Values)
        {
            Assert.True(app.Count >= 1);
            Assert.True(app.FirstPid > 4);
            Assert.Equal(app.Exe, snapshot.PidToExe[app.FirstPid]);
            Assert.InRange(app.MemMB, 0, 1024 * 1024);
            Assert.InRange(app.Cpu, 0, 100.5);
            Assert.Null(app.Processes);
        }
    }

    [Fact]
    public void Lookups_ignore_case()
    {
        var snapshot = new ProcessSampler().Sample();
        Assert.True(snapshot.Apps.ContainsKey(Me.ToUpperInvariant()));
        Assert.True(snapshot.Apps.ContainsKey("SVCHOST.EXE"));
    }

    [Fact]
    public void This_process_uses_memory_and_the_total_cpu_is_at_most_100_percent()
    {
        var sampler = new ProcessSampler();
        sampler.Sample();
        // Keep a core busy between the samples, so this process has some CPU use to show.
        var sw = Stopwatch.StartNew();
        double x = 0;
        while (sw.ElapsedMilliseconds < 300) x += Math.Sqrt(sw.ElapsedTicks);
        var snapshot = sampler.Sample();
        GC.KeepAlive(x);
        var me = snapshot.Apps[Me];
        Assert.True(me.MemMB > 5, $"{me.MemMB} MB");
        Assert.True(me.Cpu > 0, "no CPU use measured for a busy process");
        Assert.InRange(snapshot.Apps.Values.Sum(a => a.Cpu), 0, 101);
    }

    [Fact]
    public void The_first_sample_has_no_cpu_use_yet()
    {
        var snapshot = new ProcessSampler().Sample();
        Assert.All(snapshot.Apps.Values, a => Assert.Equal(0, a.Cpu));
    }

    [Fact]
    public void Processes_are_listed_one_by_one_only_for_the_apps_asked_for()
    {
        var sampler = new ProcessSampler { Detail = new HashSet<string>([Me.ToUpperInvariant(), "svchost.exe"], StringComparer.OrdinalIgnoreCase) };
        var snapshot = TwoSamples(sampler);
        var me = snapshot.Apps[Me];
        Assert.NotNull(me.Processes);
        Assert.Equal(me.Count, me.Processes!.Count);
        Assert.Contains(me.Processes, p => p.Pid == Environment.ProcessId);
        Assert.Equal(me.MemMB, me.Processes.Sum(p => p.MemMB), 6);
        Assert.Equal(snapshot.Apps["svchost.exe"].Count, snapshot.Apps["svchost.exe"].Processes!.Count);
        Assert.All(snapshot.Apps.Values.Where(a => a.Exe != Me && !a.Exe.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase)),
            a => Assert.Null(a.Processes));
        var created = Process.GetCurrentProcess().StartTime.ToFileTimeUtc();
        Assert.Equal(created, me.Processes.Single(p => p.Pid == Environment.ProcessId).Created);

        sampler.Detail = null;
        Assert.All(sampler.Sample().Apps.Values, a => Assert.Null(a.Processes));
    }

    [Fact]
    public void Sampling_again_and_again_stays_consistent()
    {
        var sampler = new ProcessSampler();
        for (int i = 0; i < 50; i++)
        {
            var s = sampler.Sample();
            Assert.Contains(Me, s.Apps.Keys);
            Assert.All(s.Apps.Values, a => Assert.InRange(a.Cpu, 0, 100.5));
        }
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void A_sample_is_quick()
    {
        // Every 2 seconds while the app is open (every 5 s otherwise), on the always-running agent.
        var sampler = new ProcessSampler();
        sampler.Sample();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) sampler.Sample();
        double ms = sw.Elapsed.TotalMilliseconds / 20;
        Assert.True(ms < 8, $"{ms:0.0} ms per sample"); // measured 2.5 ms with 270 processes
    }
}
