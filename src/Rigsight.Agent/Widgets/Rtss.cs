using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Rigsight.Core;

namespace Rigsight.Agent.Widgets;

/// <summary>
/// Talks to RivaTuner Statistics Server (RTSS) through its documented shared memory. RTSS draws
/// text that other programs hand it inside games (the way HWiNFO and AIDA64 show in-game), so the
/// overlay also works in exclusive fullscreen. Rigsight itself never touches the game.
/// </summary>
internal static unsafe class Rtss
{
    private const string MapName = "RTSSSharedMemoryV2";
    private const string Owner = "Rigsight";
    private const uint Signature = 0x52545353; // "RTSS"

    // Header layout (RTSSSharedMemory.h): signature, version, app entry size/offset/count,
    // OSD entry size/offset/count, OSD frame counter, busy flag (2.14+).
    private const int VersionAt = 4, AppEntrySizeAt = 8, AppArrOffsetAt = 12, AppArrSizeAt = 16;
    private const int OsdEntrySizeAt = 20, OsdArrOffsetAt = 24, OsdArrSizeAt = 28, OsdFrameAt = 32, BusyAt = 36;

    // OSD entry layout: szOSD[256], szOSDOwner[256], szOSDEx[4096] (2.7+).
    private const int OsdTextAt = 0, OsdOwnerAt = 256, OsdTextExAt = 512, OsdTextExSize = 4096;

    private delegate bool MemoryAction(byte* memory);

    private static bool _loggedError;

    /// <summary>RTSS.exe if RivaTuner is installed, else null.</summary>
    public static string? FindExe()
    {
        try
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = root.OpenSubKey(@"SOFTWARE\Unwinder\RTSS");
                if (key?.GetValue("InstallDir") is string dir && File.Exists(Path.Combine(dir, "RTSS.exe")))
                    return Path.Combine(dir, "RTSS.exe");
            }
        }
        catch
        {
            // Fall back to the usual places.
        }
        foreach (var pf in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            var exe = Path.Combine(Environment.GetFolderPath(pf), "RivaTuner Statistics Server", "RTSS.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    public static bool IsRunning()
    {
        var processes = Process.GetProcessesByName("RTSS");
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    /// <summary>Shows <paramref name="text"/> in games (RTSS's own tags allowed). Returns false if RTSS isn't running.</summary>
    public static bool Show(string text) => Use(memory => WriteEntry(memory, text));

    /// <summary>Removes Rigsight's text from games.</summary>
    public static void Clear() => Use(memory => WriteEntry(memory, null));

    /// <summary>Whether RTSS is drawing inside this process (so our own window isn't needed there).</summary>
    public static bool IsHooked(int pid) => pid > 0 && Use(memory =>
    {
        uint size = U(memory, AppEntrySizeAt), offset = U(memory, AppArrOffsetAt), count = U(memory, AppArrSizeAt);
        for (uint i = 0; i < count; i++)
            if (*(int*)(memory + offset + i * size) == pid) return true;
        return false;
    });

    // App entry fields (RTSSSharedMemory.h): framerate period start/end (ms) and frames in it, the last
    // frame time (µs), and a ring of the last 1,024 frame times (µs) that RTSS keeps for its graphs.
    private const int FrameTime0At = 268, FrameTime1At = 272, FramesAt = 276, FrameTimeAt = 280, FrameTimeBufAt = 924, FrameTimeBufLength = 1024;

    /// <summary>
    /// Frame rate, frame time and 1% low (over the last 1,024 frames) of a process RTSS is drawing in,
    /// or null when it isn't (not a game, or started before RTSS).
    /// </summary>
    public static FrameStats? ReadFrameStats(int pid)
    {
        FrameStats? stats = null;
        if (pid <= 0) return null;
        Use(memory =>
        {
            uint size = U(memory, AppEntrySizeAt), offset = U(memory, AppArrOffsetAt), count = U(memory, AppArrSizeAt);
            if (size < FrameTimeBufAt + FrameTimeBufLength * 4) return false;
            for (uint i = 0; i < count; i++)
            {
                byte* entry = memory + offset + i * size;
                if (*(int*)entry != pid) continue;
                uint t0 = U(entry, FrameTime0At), t1 = U(entry, FrameTime1At), frames = U(entry, FramesAt);
                if (t0 == 0 || t1 <= t0 || frames == 0) return false; // not rendering
                double fps = 1000.0 * frames / (t1 - t0);
                double frameTimeMs = U(entry, FrameTimeAt) / 1000.0;

                var times = new List<uint>(FrameTimeBufLength);
                for (int k = 0; k < FrameTimeBufLength; k++)
                    if (U(entry, FrameTimeBufAt + 4 * k) is > 0 and var ft) times.Add(ft);
                double? low = null;
                if (times.Count >= 100)
                {
                    // 1% low: the frame rate of the slowest 1% of recent frames.
                    times.Sort();
                    low = 1_000_000.0 / times[times.Count - 1 - times.Count / 100];
                }
                stats = new FrameStats(fps, frameTimeMs, low);
                return true;
            }
            return false;
        });
        return stats;
    }

    private static uint U(byte* memory, int at) => *(uint*)(memory + at);

    private static bool Use(MemoryAction action)
    {
        try
        {
            using var map = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.ReadWrite);
            using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            byte* memory = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref memory);
            try
            {
                memory += view.PointerOffset;
                if (U(memory, 0) != Signature || U(memory, VersionAt) < 0x00020000) return false;
                return action(memory);
            }
            finally
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
        catch (FileNotFoundException)
        {
            return false; // RTSS isn't running.
        }
        catch (Exception ex)
        {
            if (!_loggedError) Log.Error("rtss", ex);
            _loggedError = true;
            return false;
        }
    }

    /// <summary>Finds (or claims) Rigsight's OSD slot and writes the text; null frees the slot.</summary>
    private static bool WriteEntry(byte* memory, string? text)
    {
        uint version = U(memory, VersionAt);
        uint entrySize = U(memory, OsdEntrySizeAt), offset = U(memory, OsdArrOffsetAt), count = U(memory, OsdArrSizeAt);

        // 2.14+: a busy bit guards the memory against RTSS reading a half-written entry.
        int* busy = (int*)(memory + BusyAt);
        bool locking = version >= 0x0002000E;
        if (locking && (Interlocked.Or(ref *busy, 1) & 1) != 0) return true; // RTSS is busy; try again next update
        try
        {
            // Slot 0 belongs to RTSS itself. First look for ours, then claim a free one.
            for (int pass = 0; pass < 2; pass++)
            {
                for (uint i = 1; i < count; i++)
                {
                    byte* entry = memory + offset + i * entrySize;
                    string owner = ReadAnsi(entry + OsdOwnerAt, 256);
                    if (pass == 1 && text is not null && owner.Length == 0)
                    {
                        WriteAnsi(entry + OsdOwnerAt, Owner, 256);
                        owner = Owner;
                    }
                    if (owner != Owner) continue;

                    if (text is null) NativeMemory.Clear(entry, entrySize);
                    else if (version >= 0x00020007) WriteAnsi(entry + OsdTextExAt, text, OsdTextExSize);
                    else WriteAnsi(entry + OsdTextAt, text, 256);
                    (*(uint*)(memory + OsdFrameAt))++;
                    return true;
                }
                if (text is null) return true; // nothing to free
            }
            return false; // every slot taken
        }
        finally
        {
            if (locking) Interlocked.And(ref *busy, ~1);
        }
    }

    private static string ReadAnsi(byte* at, int max)
    {
        int n = 0;
        while (n < max && at[n] != 0) n++;
        return Encoding.Latin1.GetString(at, n);
    }

    private static void WriteAnsi(byte* at, string text, int max)
    {
        // RTSS reads plain 8-bit text: keep what Latin-1 can show (° included), swap the rest.
        var bytes = Encoding.Latin1.GetBytes(text.Replace('—', '-').Replace('…', '.'));
        int n = Math.Min(bytes.Length, max - 1);
        Marshal.Copy(bytes, 0, (IntPtr)at, n);
        at[n] = 0;
    }
}

/// <summary>A game's frame rate (per second), last frame time and 1% low, as measured by RivaTuner.</summary>
internal readonly record struct FrameStats(double Fps, double FrameTimeMs, double? OnePercentLow);
