using System.Diagnostics;
using System.Runtime.InteropServices;
using Rigsight.Agent.Native;
using Rigsight.Core;

namespace Rigsight.Agent;

/// <summary>How a watched install ended.</summary>
internal enum WatchOutcome
{
    /// <summary>It finished by itself (see <see cref="WatchResult.ExitCode"/>).</summary>
    Exited,
    /// <summary>Nothing happened for <see cref="InstallWatch.IdleLimit"/> with nothing on screen: stopped.</summary>
    Stuck,
    /// <summary>Still going after <see cref="InstallWatch.HardLimit"/>: stopped.</summary>
    TooLong,
    /// <summary>The program couldn't be started.</summary>
    NotStarted,
}

internal sealed record WatchResult(WatchOutcome Outcome, int? ExitCode, TimeSpan Elapsed);

/// <summary>
/// Runs an installer (and whatever it starts) and tells slow from stuck. An install is working while any of its
/// processes uses CPU or reads, writes or downloads; it's asking the user while one of them shows a window (brought to
/// the front, and waited for); it's stuck only when none of that has happened for <see cref="IdleLimit"/>. A slow PC
/// or a slow download just takes longer: time alone never stops it, except the generous <see cref="HardLimit"/>.
/// </summary>
internal sealed class InstallWatch
{
    public TimeSpan SampleEvery { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan IdleLimit { get; init; } = TimeSpan.FromMinutes(4);
    public TimeSpan HardLimit { get; init; } = TimeSpan.FromMinutes(45);

    /// <summary>
    /// Less CPU time than this (per sample) with no I/O counts as doing nothing: by default, none at all. Measured: a
    /// process that's really waiting uses exactly 0 ms, while a slow one doing a little shows Windows' 15.6 ms steps.
    /// </summary>
    public TimeSpan CpuFloor { get; init; } = TimeSpan.FromTicks(1);

    /// <summary>Called as the state changes: "working", "asking" (a window is up) or "idle".</summary>
    public Action<string>? StateChanged { get; init; }

    public WatchResult Run(ProcessStartInfo start, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        Process root;
        try
        {
            root = Process.Start(start) ?? throw new InvalidOperationException("not started");
        }
        catch (Exception ex)
        {
            Log.Write("install", $"Couldn't start {start.FileName}: {ex.Message}");
            return new(WatchOutcome.NotStarted, null, clock.Elapsed);
        }

        using (root)
        {
            if (start.RedirectStandardInput) root.StandardInput.Close();
            var tree = new HashSet<int> { root.Id };
            long startedAt = root.StartTime.ToFileTimeUtc() - 10_000_000; // pids are reused: only processes started since
            var last = new Dictionary<int, (long Cpu, ulong Io)>();
            var lastActive = clock.Elapsed;
            var raised = new HashSet<IntPtr>();
            string state = "";

            while (true)
            {
                // Done when the installer and everything it started have ended.
                if (root.WaitForExit(SampleEvery))
                {
                    if (!TreeAlive(tree, root.Id)) break;
                    ct.WaitHandle.WaitOne(SampleEvery); // its children carry on: keep sampling at the same pace
                }
                ct.ThrowIfCancellationRequested();

                // The tree: the installer and everything it started (children outlive a parent that exits).
                var procs = Snapshot();
                bool added;
                do
                {
                    added = false;
                    foreach (var p in procs)
                        if (!tree.Contains(p.Pid) && tree.Contains(p.Parent) && p.Created >= startedAt)
                            added |= tree.Add(p.Pid);
                } while (added);

                bool active = false;
                foreach (var p in procs.Where(p => tree.Contains(p.Pid)))
                {
                    ulong io = IoBytes(p.Pid);
                    if (last.TryGetValue(p.Pid, out var before))
                        active |= p.Cpu - before.Cpu >= CpuFloor.Ticks || io > before.Io;
                    else
                        active = true; // a new process started: that's progress
                    last[p.Pid] = (p.Cpu, io);
                }

                var windows = VisibleWindows(tree);
                string now = windows.Count > 0 ? "asking" : active ? "working" : "idle";
                if (windows.Count > 0 || active) lastActive = clock.Elapsed;
                foreach (var w in windows.Where(raised.Add)) BringToFront(w);
                if (now != state)
                {
                    state = now;
                    Log.Write("install", $"{Path.GetFileName(start.FileName)}: {state} ({clock.Elapsed.TotalSeconds:0} s)");
                    StateChanged?.Invoke(state);
                }

                if (clock.Elapsed - lastActive >= IdleLimit)
                {
                    Stop(tree);
                    return new(WatchOutcome.Stuck, null, clock.Elapsed);
                }
                if (clock.Elapsed >= HardLimit)
                {
                    Stop(tree);
                    return new(WatchOutcome.TooLong, null, clock.Elapsed);
                }
            }
            return new(WatchOutcome.Exited, root.ExitCode, clock.Elapsed);
        }
    }

    private static bool TreeAlive(HashSet<int> tree, int rootPid) =>
        Snapshot().Any(p => p.Pid != rootPid && tree.Contains(p.Pid));

    private static void Stop(HashSet<int> tree)
    {
        foreach (int pid in tree)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone.
            }
        }
    }

    internal readonly record struct Proc(int Pid, int Parent, long Created, long Cpu);

    /// <summary>Every process with its parent and CPU time, in one call.</summary>
    internal static unsafe List<Proc> Snapshot()
    {
        int size = 1 << 20;
        while (true)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = Win32.NtQuerySystemInformation(Win32.SystemProcessInformation, buffer, size, out int needed);
                if (status == Win32.STATUS_INFO_LENGTH_MISMATCH)
                {
                    size = Math.Max(needed, size) + (256 << 10);
                    continue;
                }
                var list = new List<Proc>();
                if (status != 0) return list;
                byte* p = (byte*)buffer;
                while (true)
                {
                    var info = (Win32.SYSTEM_PROCESS_INFORMATION*)p;
                    list.Add(new Proc((int)info->UniqueProcessId, (int)info->InheritedFromUniqueProcessId, info->CreateTime, info->UserTime + info->KernelTime));
                    if (info->NextEntryOffset == 0) break;
                    p += info->NextEntryOffset;
                }
                return list;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>Bytes a process has read, written and moved otherwise (network, devices) so far.</summary>
    private static ulong IoBytes(int pid)
    {
        var h = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return 0;
        try
        {
            return GetProcessIoCounters(h, out var c) ? c.ReadTransferCount + c.WriteTransferCount + c.OtherTransferCount : 0;
        }
        finally
        {
            Win32.CloseHandle(h);
        }
    }

    /// <summary>Visible top-level windows of the tree's processes: a question waiting for an answer.</summary>
    private static List<IntPtr> VisibleWindows(HashSet<int> tree)
    {
        var found = new List<IntPtr>();
        Win32.EnumWindows((hwnd, _) =>
        {
            if (Win32.IsWindowVisible(hwnd))
            {
                Win32.GetWindowThreadProcessId(hwnd, out int pid);
                if (tree.Contains(pid)) found.Add(hwnd);
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Raises a window above everything once (setup's own window included), so its question is seen.</summary>
    private static void BringToFront(IntPtr hwnd)
    {
        const uint NoSizeMoveActivate = 0x0001 | 0x0002 | 0x0010;
        Win32.SetWindowPos(hwnd, new IntPtr(-1) /* topmost */, 0, 0, 0, 0, NoSizeMoveActivate);
        Win32.SetWindowPos(hwnd, new IntPtr(-2) /* not topmost: stays in front, can be covered again */, 0, 0, 0, 0, NoSizeMoveActivate);
        SetForegroundWindow(hwnd);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll")] private static extern bool GetProcessIoCounters(IntPtr process, out IO_COUNTERS counters);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
