using System.Buffers.Binary;
using System.Globalization;

namespace Rigsight.Core.Games;

/// <summary>Every frame of a stretch of play: when each ended (Unix milliseconds) and how long it took (ms).</summary>
public sealed record FrameRun(long[] EndMs, float[] Ms)
{
    public static readonly FrameRun None = new([], []);
    public int Count => Ms.Length;
}

/// <summary>
/// Every frame of the games played, kept for 14 days (and at most 1 GB) in a file per game per day under the history's
/// folder: "frames\20261025-12.bin" (the day, then the game's app number). A game's minutes keep their summary for good
/// (see <see cref="FrameSpread"/>); this is what a play's frame-time chart is drawn from. Each block is the moment it
/// was written (the end of its last frame), how many frames, then each frame's time in tens of microseconds (up to
/// 655 ms; longer ones are kept as that). 144 fps is about 1 MB an hour.
/// </summary>
public static class FrameStore
{
    public const int KeepDays = 14;
    public const long MaxBytes = 1L << 30;

    public static string Folder => Path.Combine(RigsightPaths.DataDir, "frames");

    private static string FileOf(string folder, DateTime day, long app) => Path.Combine(folder, $"{day:yyyyMMdd}-{app}.bin");

    /// <summary>Adds a game's frames, the last ending at <paramref name="endMs"/>. Never throws (frames are a nice-to-have).</summary>
    public static void Append(long app, long endMs, ReadOnlySpan<uint> frames, string? folder = null)
    {
        if (frames.Length == 0) return;
        folder ??= Folder;
        try
        {
            Directory.CreateDirectory(folder);
            var day = DateTimeOffset.FromUnixTimeMilliseconds(endMs).LocalDateTime.Date;
            var bytes = new byte[10 + frames.Length * 2];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, endMs);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), (ushort)Math.Min(frames.Length, ushort.MaxValue));
            int n = Math.Min(frames.Length, ushort.MaxValue);
            for (int i = 0; i < n; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10 + i * 2), (ushort)Math.Min(frames[frames.Length - n + i] / 10, ushort.MaxValue));
            using var file = new FileStream(FileOf(folder, day, app), FileMode.Append, FileAccess.Write, FileShare.Read);
            file.Write(bytes, 0, 10 + n * 2);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("frames", $"Couldn't keep {frames.Length} frames: {ex.Message}");
        }
    }

    /// <summary>Every kept frame of <paramref name="app"/> ending from <paramref name="fromMs"/> to <paramref name="toMs"/>, oldest first.</summary>
    public static FrameRun Read(long app, long fromMs, long toMs, string? folder = null)
    {
        folder ??= Folder;
        var ends = new List<long>();
        var ms = new List<float>();
        // The game's files for the days asked (the day before too: a block can end just after midnight with frames from
        // before it), found by listing them rather than trying each day of a long stretch.
        const long Min = -62135596800000, Max = 253402300799999;
        var first = DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(fromMs, Min, Max)).LocalDateTime.Date.AddDays(-1);
        var last = DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(toMs, Min, Max)).LocalDateTime.Date;
        string[] found;
        try { found = Directory.Exists(folder) ? Directory.GetFiles(folder, $"*-{app}.bin") : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { found = []; }
        foreach (var file in found.Order(StringComparer.Ordinal))
        {
            if (!DateTime.TryParseExact(Path.GetFileName(file)[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || day < first || day > last) continue;
            byte[] bytes;
            try { if (!File.Exists(file)) continue; bytes = File.ReadAllBytes(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            int at = 0;
            while (at + 10 <= bytes.Length)
            {
                long end = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at));
                int n = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 8));
                at += 10;
                if (at + n * 2 > bytes.Length) break; // cut short by a power cut: what's whole is kept
                if (end < fromMs || end - 70_000 > toMs) { at += n * 2; continue; }
                // Each frame's end, counted back from the block's.
                var times = new float[n];
                double sum = 0;
                for (int i = 0; i < n; i++) { times[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + i * 2)) / 100f; sum += times[i]; }
                double t = end - sum;
                for (int i = 0; i < n; i++)
                {
                    t += times[i];
                    long e = (long)Math.Round(t);
                    if (e >= fromMs && e <= toMs) { ends.Add(e); ms.Add(times[i]); }
                }
                at += n * 2;
            }
        }
        return ends.Count == 0 ? FrameRun.None : new FrameRun([.. ends], [.. ms]);
    }

    /// <summary>Whether any frame of <paramref name="app"/> is kept for that day (a play's chart is only offered then).</summary>
    public static bool Has(long app, DateTime day, string? folder = null) => File.Exists(FileOf(folder ?? Folder, day.Date, app));

    /// <summary>Leaves the last 14 days, and of those the newest 1 GB. Run once a day by the agent.</summary>
    public static void Prune(DateTime today, string? folder = null)
    {
        folder ??= Folder;
        try
        {
            if (!Directory.Exists(folder)) return;
            var files = Directory.GetFiles(folder, "*.bin")
                .Select(f => (Path: f, Day: DateTime.TryParseExact(Path.GetFileName(f)[..Math.Min(8, Path.GetFileName(f).Length)], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.MinValue, Size: new FileInfo(f).Length))
                .OrderByDescending(f => f.Day).ToList();
            long kept = 0;
            foreach (var f in files)
            {
                kept += f.Size;
                if (f.Day < today.Date.AddDays(-KeepDays + 1) || kept > MaxBytes) File.Delete(f.Path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("frames", $"Couldn't tidy the kept frames: {ex.Message}");
        }
    }
}
