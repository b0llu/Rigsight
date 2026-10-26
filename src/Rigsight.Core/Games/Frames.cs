using System.Buffers.Binary;

namespace Rigsight.Core.Games;

/// <summary>
/// How long a stretch of frames took, kept as counts of frame times in narrow bands (each 5% longer than the one
/// before, 1 ms to past 400 ms), so any stretch's true 1% and 0.1% lows can be had by adding minutes together (the
/// person's ask, 2026-10-25: "track every frame's FPS, not just the average"). The 1% low before this was an average of
/// RivaTuner's own lows over the last 1,024 frames, which smoothed a stutter away. About 60 bytes a minute.
/// </summary>
public sealed class FrameSpread
{
    public const int Bands = 128;
    private const double First = 1000, Step = 1.05;
    private static readonly double[] Edges = [.. Enumerable.Range(0, Bands + 1).Select(i => First * Math.Pow(Step, i))];

    private readonly int[] _counts = new int[Bands];

    /// <summary>Frames in it.</summary>
    public long Frames { get; private set; }
    /// <summary>All their times added up, in microseconds.</summary>
    public long TotalUs { get; private set; }

    public bool IsEmpty => Frames == 0;

    public void Add(uint us)
    {
        if (us == 0) return;
        _counts[Band(us)]++;
        Frames++;
        TotalUs += us;
    }

    public void Add(FrameSpread other)
    {
        for (int i = 0; i < Bands; i++) _counts[i] += other._counts[i];
        Frames += other.Frames;
        TotalUs += other.TotalUs;
    }

    private static int Band(uint us)
    {
        if (us <= First) return 0;
        int b = (int)(Math.Log(us / First) / Math.Log(Step));
        return Math.Clamp(b, 0, Bands - 1);
    }

    /// <summary>The average frames a second: frames over the time they took.</summary>
    public double? Fps => Frames > 0 && TotalUs > 0 ? Frames * 1_000_000.0 / TotalUs : null;

    /// <summary>
    /// The frame rate the slowest <paramref name="share"/> of frames ran at (0.01: the 1% low): the frame time that
    /// many frames are as slow as or slower, read inside its band. Null with too few frames to say (100 for the 1% low,
    /// 1,000 for the 0.1%).
    /// </summary>
    public double? Low(double share)
    {
        if (Frames < Math.Ceiling(1 / share)) return null;
        double slow = Frames * share, seen = 0;
        for (int i = Bands - 1; i >= 0; i--)
        {
            if (_counts[i] == 0) continue;
            if (seen + _counts[i] >= slow)
            {
                // Where in the band, from its fast edge to its slow one, the slowest share ends.
                double into = (slow - seen) / _counts[i];
                double us = Edges[i + 1] * Math.Pow(Edges[i] / Edges[i + 1], into);
                return 1_000_000.0 / us;
            }
            seen += _counts[i];
        }
        return null;
    }

    public double? Low1 => Low(0.01);
    public double? Low01 => Low(0.001);

    /// <summary>Kept in the history: the time in all, then each band that has frames, its number and how many.</summary>
    public byte[] ToBytes()
    {
        var bytes = new List<byte>(64);
        Span<byte> eight = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(eight, TotalUs);
        bytes.AddRange(eight.ToArray());
        for (int i = 0; i < Bands; i++)
        {
            if (_counts[i] == 0) continue;
            bytes.Add((byte)i);
            // How many, 7 bits at a time.
            uint n = (uint)_counts[i];
            while (n >= 0x80) { bytes.Add((byte)(n | 0x80)); n >>= 7; }
            bytes.Add((byte)n);
        }
        return [.. bytes];
    }

    public static FrameSpread FromBytes(ReadOnlySpan<byte> bytes)
    {
        var spread = new FrameSpread();
        if (bytes.Length < 8) return spread;
        spread.TotalUs = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        int at = 8;
        while (at < bytes.Length)
        {
            int band = bytes[at++];
            uint n = 0;
            for (int shift = 0; at < bytes.Length; shift += 7)
            {
                byte b = bytes[at++];
                n |= (uint)(b & 0x7F) << shift;
                if (b < 0x80) break;
            }
            if (band < Bands) { spread._counts[band] += (int)n; spread.Frames += n; }
        }
        return spread;
    }
}

/// <summary>A stutter: one frame that took far longer than the frames just before it, and when.</summary>
/// <param name="AtMs">When the frame ended (Unix milliseconds).</param>
/// <param name="Ms">How long it took.</param>
/// <param name="UsualMs">How long the frames before it took (their middle one).</param>
public sealed record Stutter(long AtMs, double Ms, double UsualMs);

/// <summary>
/// Finds stutters in a game's frames as they come: a frame at least 2.5 times as long as the middle of the 30 frames
/// before it, and at least 20 ms (a 144 fps game's 7 ms frames jumping to 20 ms is felt; 8 to 12 isn't). Nothing is
/// found while the game runs under 20 fps on the whole (a loading screen), or right after frames were missed.
/// </summary>
public sealed class StutterFinder
{
    public const double Times = 2.5, AtLeastMs = 20, LoadingMs = 50;
    private const int Window = 30;
    private readonly Queue<uint> _recent = new();
    private readonly uint[] _sorted = new uint[Window];

    /// <summary>Frames were missed (the game left the front, RivaTuner's list ran over): the frames before don't count.</summary>
    public void Reset() => _recent.Clear();

    /// <summary>
    /// Frames in the order they came, the last one ending at <paramref name="endMs"/>: the stutters among them, each
    /// with the time it ended.
    /// </summary>
    public List<Stutter> Feed(ReadOnlySpan<uint> frames, long endMs)
    {
        var found = new List<Stutter>();
        // The end of each frame, counted back from the last one.
        long total = 0;
        foreach (var f in frames) total += f;
        double at = endMs - total / 1000.0;
        foreach (var us in frames)
        {
            at += us / 1000.0;
            if (_recent.Count == Window)
            {
                _recent.CopyTo(_sorted, 0);
                Array.Sort(_sorted);
                double usual = (_sorted[Window / 2 - 1] + _sorted[Window / 2]) / 2000.0, ms = us / 1000.0;
                if (usual <= LoadingMs && ms >= Times * usual && ms >= AtLeastMs) found.Add(new Stutter((long)Math.Round(at), ms, usual));
                _recent.Dequeue();
            }
            _recent.Enqueue(us);
        }
        return found;
    }
}
