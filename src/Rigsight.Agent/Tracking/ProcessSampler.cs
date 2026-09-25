using System.Diagnostics;
using System.Runtime.InteropServices;
using Rigsight.Agent.Native;

namespace Rigsight.Agent.Tracking;

/// <summary>Resource use of one app (all processes with the same exe name combined).</summary>
internal sealed class AppUsage
{
    public required string Exe { get; init; }
    public int FirstPid { get; set; }
    public int Count { get; set; }
    /// <summary>Share of total CPU capacity, 0–100 (like Task Manager).</summary>
    public double Cpu { get; set; }
    /// <summary>Private working set in MB (Task Manager's "Memory" column).</summary>
    public double MemMB { get; set; }
    /// <summary>Each process on its own, only for the apps asked for (<see cref="ProcessSampler.Detail"/>).</summary>
    public List<ProcessUsage>? Processes { get; set; }
}

internal readonly record struct ProcessUsage(int Pid, long Created, double Cpu, double MemMB);

internal sealed class ProcessSnapshot
{
    public Dictionary<string, AppUsage> Apps { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, string> PidToExe { get; } = [];
}

/// <summary>
/// Samples every process in one NtQuerySystemInformation call (a single syscall, no per-process
/// handles), which is far cheaper than System.Diagnostics.Process.
/// </summary>
internal sealed unsafe class ProcessSampler
{
    private IntPtr _buffer = Marshal.AllocHGlobal(1 << 20);
    private int _bufferSize = 1 << 20;
    private Dictionary<(int Pid, long Created), long> _lastCpu = [];
    private long _lastSampleTime;

    /// <summary>Apps (exe names) whose processes are also listed one by one, for the Memory page.</summary>
    public HashSet<string>? Detail { get; set; }

    public ProcessSnapshot Sample()
    {
        int status;
        while ((status = Win32.NtQuerySystemInformation(Win32.SystemProcessInformation, _buffer, _bufferSize, out int needed))
               == Win32.STATUS_INFO_LENGTH_MISMATCH)
        {
            Marshal.FreeHGlobal(_buffer);
            _bufferSize = Math.Max(needed, _bufferSize) + (256 << 10);
            _buffer = Marshal.AllocHGlobal(_bufferSize);
        }

        var snapshot = new ProcessSnapshot();
        if (status != 0) return snapshot;

        // By the precise clock: a sample taken right after another (the Memory page asking for its details) is only
        // milliseconds later, where TickCount's 16 ms steps would make CPU use look several times too high.
        long now = Stopwatch.GetTimestamp();
        double elapsed100ns = Stopwatch.GetElapsedTime(_lastSampleTime, now).Ticks * (double)Environment.ProcessorCount;
        bool haveBaseline = _lastSampleTime != 0 && elapsed100ns > 0;
        var current = new Dictionary<(int, long), long>(_lastCpu.Count);
        var detail = Detail;

        byte* p = (byte*)_buffer;
        while (true)
        {
            var info = (Win32.SYSTEM_PROCESS_INFORMATION*)p;
            int pid = (int)info->UniqueProcessId;
            if (pid > 4 && info->ImageName.Buffer != IntPtr.Zero)
            {
                var exe = new string((char*)info->ImageName.Buffer, 0, info->ImageName.Length / 2);
                long cpuTime = info->UserTime + info->KernelTime;
                var key = (pid, info->CreateTime);
                current[key] = cpuTime;

                double cpu = 0;
                if (haveBaseline && _lastCpu.TryGetValue(key, out var before))
                    cpu = Math.Clamp((cpuTime - before) / elapsed100ns * 100, 0, 100); // Windows counts CPU time in steps too

                if (!snapshot.Apps.TryGetValue(exe, out var app))
                {
                    app = new AppUsage { Exe = exe, FirstPid = pid };
                    snapshot.Apps[exe] = app;
                }
                app.Count++;
                app.Cpu = Math.Min(100, app.Cpu + cpu);
                double mem = info->WorkingSetPrivateSize / (1024.0 * 1024.0);
                app.MemMB += mem;
                if (detail?.Contains(exe) == true) (app.Processes ??= []).Add(new ProcessUsage(pid, info->CreateTime, cpu, mem));
                snapshot.PidToExe[pid] = exe;
            }

            if (info->NextEntryOffset == 0) break;
            p += info->NextEntryOffset;
        }

        _lastCpu = current;
        _lastSampleTime = now;
        return snapshot;
    }
}
