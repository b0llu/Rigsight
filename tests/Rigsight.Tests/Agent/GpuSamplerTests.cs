using System.Diagnostics;
using Rigsight.Agent.Tracking;

namespace Rigsight.Tests.Agent;

/// <summary>Reading how busy each app keeps this PC's main GPU (read-only): whatever is running, the numbers must be sane.</summary>
[Trait("Category", "Machine")]
public class GpuSamplerTests
{
    private static (GpuSampler Gpu, ProcessSnapshot Snapshot, Dictionary<string, double> Shares) TwoSamples()
    {
        var processes = new ProcessSampler();
        var gpu = new GpuSampler { MinShare = 0 };
        gpu.Sample(processes.Sample());
        Thread.Sleep(300); // use is measured between two samples
        var snapshot = processes.Sample();
        return (gpu, snapshot, gpu.Sample(snapshot));
    }

    [Fact]
    public void Each_apps_share_is_a_share_of_the_gpu()
    {
        var (gpu, snapshot, shares) = TwoSamples();
        using (gpu)
        {
            Assert.SkipUnless(gpu.Available, "no GPU Windows can read per app (a VM?)");
            foreach (var (exe, share) in shares)
            {
                Assert.Contains(exe, snapshot.Apps.Keys);
                Assert.InRange(share, 0, 100);
            }
        }
    }

    [Fact]
    public void The_first_sample_says_nothing_yet()
    {
        using var gpu = new GpuSampler { MinShare = 0 };
        Assert.Empty(gpu.Sample(new ProcessSampler().Sample()));
    }

    [Fact]
    public void Processes_that_end_are_let_go()
    {
        var (gpu, _, _) = TwoSamples();
        using (gpu)
        {
            Assert.SkipUnless(gpu.Available, "no GPU Windows can read per app (a VM?)");
            Assert.True(gpu.Tracked > 10);
            gpu.Sample(new ProcessSnapshot());
            Assert.Equal(0, gpu.Tracked);
        }
    }

    [Fact]
    public void Only_processes_using_the_gpu_are_held_open()
    {
        // A handle for every process would be hundreds more for the agent: those that never draw are let go.
        var (gpu, snapshot, _) = TwoSamples();
        using (gpu)
        {
            Assert.SkipUnless(gpu.Available, "no GPU Windows can read per app (a VM?)");
            Assert.True(gpu.Held < snapshot.Processes.Count / 2, $"{gpu.Held} held of {snapshot.Processes.Count} processes");
        }
    }

    [Fact]
    public void A_sample_is_cheap_once_started()
    {
        var (gpu, _, _) = TwoSamples();
        using (gpu)
        {
            Assert.SkipUnless(gpu.Available, "no GPU Windows can read per app (a VM?)");
            var processes = new ProcessSampler();
            var snapshot = processes.Sample();
            gpu.Sample(snapshot);
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 5; i++) gpu.Sample(snapshot);
            Assert.True(watch.ElapsedMilliseconds < 5 * 50, $"{watch.ElapsedMilliseconds / 5.0:0.0} ms a sample");
        }
    }
}
