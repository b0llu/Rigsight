using Rigsight.Core.Reports;

namespace Rigsight.Tests.Core;

/// <summary>
/// The Processes page's LOAD TODAY: each part as a share of the PC over the time the app ran today, the word the highest
/// of the three, and its hover naming the part it comes from.
/// </summary>
public sealed class AppLoadTests
{
    // A PC with 16 threads and 32 GB, and an app that ran for 6 hours today.
    private const int Threads = 16;
    private const double Ram = 32 * 1024, Run = 6 * 3600;

    /// <summary>CPU share (%), GPU share (%), memory share (%) → the word and the part it names.</summary>
    public static TheoryData<double, double, double, LoadLevel, LoadPart> Shares() => new()
    {
        // Everything under its Low mark.
        { 0.2, 0.4, 1, LoadLevel.Low, LoadPart.Memory },
        { 0.99, 1.99, 2.99, LoadLevel.Low, LoadPart.Memory },
        // Each part reaching Medium on its own mark.
        { 1, 0, 0, LoadLevel.Medium, LoadPart.Cpu },
        { 0, 2, 0, LoadLevel.Medium, LoadPart.Gpu },
        { 0, 0, 3, LoadLevel.Medium, LoadPart.Memory },
        { 9.9, 19.9, 14.9, LoadLevel.Medium, LoadPart.Gpu }, // all just under High: the one nearest its mark
        // Each reaching High on its own mark.
        { 10, 0, 0, LoadLevel.High, LoadPart.Cpu },
        { 0, 20, 0, LoadLevel.High, LoadPart.Gpu },
        { 0, 0, 15, LoadLevel.High, LoadPart.Memory },
        // The highest level wins whatever the others are.
        { 0.5, 3.1, 1, LoadLevel.Medium, LoadPart.Gpu },
        { 0.6, 0, 16, LoadLevel.High, LoadPart.Memory },
        { 12, 25, 4, LoadLevel.High, LoadPart.Gpu }, // two High: the one furthest past its mark
    };

    [Theory]
    [MemberData(nameof(Shares))]
    public void The_word_is_the_highest_part(double cpu, double gpu, double mem, LoadLevel level, LoadPart part)
    {
        var load = AppLoad.Judge(cpu / 100 * Run * Threads, gpu / 100 * Run, mem / 100 * Ram, Run, Threads, Ram)!;
        Assert.Equal((level, part), (load.Level, load.Part));
        Assert.Equal(cpu, load.CpuPct, 6);
        Assert.Equal(gpu, load.GpuPct!.Value, 6);
        Assert.Equal(mem, load.MemPct, 6);
    }

    [Fact]
    public void The_marks_are_kept_in_one_place()
    {
        Assert.Equal((1, 10, 2, 20, 3, 15), (AppLoad.CpuLow, AppLoad.CpuHigh, AppLoad.GpuLow, AppLoad.GpuHigh, AppLoad.MemLow, AppLoad.MemHigh));
        Assert.Equal(600, AppLoad.MinRunSec);
    }

    [Fact]
    public void Under_ten_minutes_there_is_no_word()
    {
        Assert.Null(AppLoad.Judge(100, 0, 1000, 599, Threads, Ram));
        Assert.NotNull(AppLoad.Judge(100, 0, 1000, 600, Threads, Ram));
        Assert.Equal("Too soon to say. It needs 10 minutes of running today, and has 4 so far.", AppLoad.TooSoon(4 * 60 + 30));
        Assert.Equal("Too soon to say. It needs 10 minutes of running today.", AppLoad.TooSoon(20));
    }

    [Fact]
    public void The_hover_names_the_part_with_its_figures()
    {
        const double ran = 6 * 3600 + 40 * 60;
        var memory = AppLoad.Judge(0.006 * ran * Threads, null, 0.08 * Ram, ran, Threads, Ram)!;
        Assert.Equal("Medium because of its memory, 2.6 GB on average today, 8% of your 32 GB. CPU 0.6% on average, over 6h 40m running.", memory.Hover("32 GB"));
        var high = AppLoad.Judge(0.006 * ran * Threads, null, 0.2 * Ram, ran, Threads, Ram)!;
        Assert.StartsWith("High because of its memory, 6.4 GB on average today, 20% of your 32 GB.", high.Hover("32 GB"));
        var gpu = AppLoad.Judge(0, 0.031 * Run, 100, Run, Threads, Ram)!;
        Assert.Equal("Medium because of its GPU, 3.1% on average today while it ran.", gpu.Hover("32 GB"));
        var low = AppLoad.Judge(0.002 * Run * Threads, 0.004 * Run, 0.01 * Ram, Run, Threads, Ram)!;
        Assert.Equal("Low. CPU 0.2%, GPU 0.4% and memory 1% of your 32 GB, on average today.", low.Hover("32 GB"));
        var noGpu = AppLoad.Judge(0.002 * Run * Threads, null, 0.01 * Ram, Run, Threads, Ram)!;
        Assert.Equal("Low. CPU 0.2% and memory 1% of your 32 GB, on average today.", noGpu.Hover("32 GB"));
    }

    [Fact]
    public void An_app_keeping_a_core_or_more_busy_says_how_many()
    {
        // Two and a half cores' worth over its 6 hours: 15.6% of 16 threads.
        var busy = AppLoad.Judge(2.5 * Run, 0, 500, Run, Threads, Ram)!;
        Assert.Equal((LoadLevel.High, LoadPart.Cpu), (busy.Level, busy.Part));
        Assert.Equal("High because of its CPU, 16% on average today, over 6h 00m running. That's about 3 cores busy.", busy.Hover("32 GB"));
        // About one core on a 4-thread PC.
        var one = AppLoad.Judge(0.95 * Run, 0, 500, Run, 4, Ram)!;
        Assert.EndsWith("That's about one core busy.", one.Hover("32 GB"));
        // Under one core: nothing said of cores.
        Assert.DoesNotContain("core", AppLoad.Judge(0.5 * Run, 0, 500, Run, Threads, Ram)!.Hover("32 GB"));
    }

    [Fact]
    public void The_word_is_one_of_three()
    {
        Assert.Equal(["Low", "Medium", "High"], new[] { LoadLevel.Low, LoadLevel.Medium, LoadLevel.High }.Select(AppLoad.Words));
    }
}
