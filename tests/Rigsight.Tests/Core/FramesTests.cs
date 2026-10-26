using Rigsight.Core.Games;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Core;

/// <summary>
/// Every frame (2026-10-25): a minute's spread of frame times gives true lows for any stretch, stutters are found as
/// they come, and every frame of the last 14 days is kept in files and read back with its time.
/// </summary>
public class FramesTests
{
    /// <summary>Frames all of <paramref name="us"/> µs, with some slow ones where said.</summary>
    private static uint[] Frames(int n, uint us, params (int At, uint Us)[] slow)
    {
        var frames = Enumerable.Repeat(us, n).ToArray();
        foreach (var (at, s) in slow) frames[at] = s;
        return frames;
    }

    private static double ExactLow(IEnumerable<uint> frames, double share)
    {
        var sorted = frames.OrderBy(x => x).ToArray();
        return 1_000_000.0 / sorted[sorted.Length - 1 - (int)(sorted.Length * share)];
    }

    [Fact]
    public void The_spread_gives_the_average_and_lows_within_a_band_of_the_exact_figures()
    {
        var random = new Random(25);
        var frames = Enumerable.Range(0, 20_000).Select(_ => (uint)(6000 + random.Next(0, 3000) + (random.NextDouble() < 0.02 ? random.Next(5000, 30000) : 0))).ToArray();
        var spread = new FrameSpread();
        foreach (var f in frames) spread.Add(f);

        Assert.Equal(frames.Length * 1_000_000.0 / frames.Sum(f => (long)f), spread.Fps!.Value, 6);
        Assert.InRange(spread.Low1!.Value / ExactLow(frames, 0.01), 0.95, 1.05);
        Assert.InRange(spread.Low01!.Value / ExactLow(frames, 0.001), 0.95, 1.05);
    }

    [Fact]
    public void Minutes_add_up_to_the_lows_of_all_their_frames_and_come_back_from_the_history_as_they_were()
    {
        var calm = new FrameSpread();
        foreach (var f in Frames(8000, 7000)) calm.Add(f);
        var rough = new FrameSpread();
        foreach (var f in Frames(8000, 7000, [.. Enumerable.Range(0, 200).Select(i => (i * 40, 40_000u))])) rough.Add(f);

        var both = FrameSpread.FromBytes(calm.ToBytes());
        both.Add(FrameSpread.FromBytes(rough.ToBytes()));
        Assert.Equal(16_000, both.Frames);
        // 200 slow frames of 16,000 is 1.25%: the 1% low is one of them (25 fps), not an average of two minutes' lows.
        Assert.InRange(both.Low1!.Value, 24, 26.5);
        Assert.InRange(calm.Low1!.Value, 136, 150);
        Assert.True(calm.ToBytes().Length < 40);
    }

    [Fact]
    public void Too_few_frames_give_no_low()
    {
        var spread = new FrameSpread();
        foreach (var f in Frames(500, 7000)) spread.Add(f);
        Assert.NotNull(spread.Low1);
        Assert.Null(spread.Low01);
        Assert.Null(new FrameSpread().Fps);
    }

    [Fact]
    public void A_stutter_is_a_frame_far_longer_than_the_ones_before_it_and_felt()
    {
        var finder = new StutterFinder();
        // 144 fps; a 25 ms frame is a stutter, a 15 ms one isn't (over 2x, but under 20 ms), and neither is one in the first 30.
        var frames = Frames(200, 7000, (10, 40_000), (60, 25_000), (120, 15_000));
        var found = finder.Feed(frames, endMs: 1_000_000);
        var one = Assert.Single(found);
        Assert.Equal(25, one.Ms, 1);
        Assert.Equal(7, one.UsualMs, 1);
        // Its time: the frames after it (one of them the 15 ms) counted back from the end.
        Assert.InRange(one.AtMs, 1_000_000 - (138 * 7 + 15) - 1, 1_000_000 - (138 * 7 + 15) + 1);

        // A loading screen at 10 fps: its 400 ms frame isn't a stutter.
        Assert.Empty(new StutterFinder().Feed(Frames(100, 100_000, (50, 400_000)), 0));
        // After frames were missed, the frames before don't count.
        finder.Reset();
        Assert.Empty(finder.Feed(Frames(20, 7000, (5, 30_000)), 0));
    }

    [Fact]
    public void Every_frame_is_kept_with_its_time_and_read_back_by_the_stretch_asked()
    {
        string folder = TestEnvironment.NewFolder("frames");
        long t0 = new DateTimeOffset(2026, 10, 25, 21, 0, 0, TimeSpan.FromHours(5.5)).ToUnixTimeMilliseconds();
        // Two seconds of 125 fps (8 ms frames), the second with a 50 ms frame at its end.
        FrameStore.Append(12, t0 + 1000, Frames(125, 8000), folder);
        FrameStore.Append(12, t0 + 2000, Frames(120, 8000, (119, 50_000)), folder);
        FrameStore.Append(99, t0 + 2000, Frames(10, 8000), folder);

        var all = FrameStore.Read(12, t0, t0 + 5000, folder);
        Assert.Equal(245, all.Count);
        Assert.Equal(t0 + 2000, all.EndMs[^1]);
        Assert.Equal(50, all.Ms[^1], 2);
        Assert.Equal(8, all.Ms[0], 2);
        Assert.Equal(t0 + 1000 - 124 * 8, all.EndMs[0]);
        Assert.True(all.EndMs.Zip(all.EndMs.Skip(1)).All(p => p.Second >= p.First));
        Assert.Equal(125, FrameStore.Read(12, t0, t0 + 1000, folder).Count);

        // A block cut short by a power cut: what's whole is kept.
        string file = Directory.GetFiles(folder, "*-12.bin").Single();
        using (var f = new FileStream(file, FileMode.Append)) f.Write([1, 2, 3, 4, 5, 6, 7, 8, 200, 0, 1]);
        Assert.Equal(245, FrameStore.Read(12, t0, t0 + 5000, folder).Count);
    }

    [Fact]
    public void Only_the_last_14_days_are_kept()
    {
        string folder = TestEnvironment.NewFolder("frames-prune");
        var today = new DateTime(2026, 10, 25);
        foreach (int back in new[] { 0, 13, 14, 30 })
            File.WriteAllBytes(Path.Combine(folder, $"{today.AddDays(-back):yyyyMMdd}-5.bin"), new byte[100]);
        FrameStore.Prune(today, folder);
        Assert.Equal(["20261012-5.bin", "20261025-5.bin"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());
    }
}
