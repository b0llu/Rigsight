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
    /// <summary>What its processes read and wrote since the last sample, in bytes a second.</summary>
    public double Disk { get; set; }
    /// <summary>When the earliest of its processes started (a Windows file time).</summary>
    public long Created { get; set; }
    /// <summary>Each process on its own, only for the apps asked for (<see cref="ProcessSampler.Detail"/>).</summary>
    public List<ProcessUsage>? Processes { get; set; }
}

internal readonly record struct ProcessUsage(int Pid, long Created, double Cpu, double MemMB, double Disk = 0);

internal sealed class ProcessSnapshot
{
    public Dictionary<string, AppUsage> Apps { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, string> PidToExe { get; } = [];
    /// <summary>Each process by ID and start time (an ID can be reused by a later process).</summary>
    public List<(int Pid, long Created)> Processes { get; } = [];
    /// <summary>Share of the main GPU each app kept busy, 0–100, by exe (see <see cref="GpuSampler"/>); empty when unknown.</summary>
    public Dictionary<string, double> Gpu { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>What every process together (Windows' own "System" too) read and wrote since the last sample, in bytes a second.</summary>
    public double Disk { get; set; }
}

/// <summary>
/// Samples every process in one NtQuerySystemInformation call (a single syscall, no per-process
/// handles), which is far cheaper than System.Diagnostics.Process.
/// </summary>
internal sealed unsafe class ProcessSampler
{
    private IntPtr _buffer = Marshal.AllocHGlobal(1 << 20);
    private int _bufferSize = 1 << 20;
    // Each process's CPU time and bytes read and written at the last sample. Windows counts both since the process
    // started, so what it is using now is the difference between two samples.
    private Dictionary<(int Pid, long Created), (long Cpu, long Io)> _last = [];
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

        return status != 0 ? new ProcessSnapshot() : Read(_buffer, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Reads what Windows wrote into <paramref name="buffer"/> (one SYSTEM_PROCESS_INFORMATION after another), taken
    /// at <paramref name="now"/> on the Stopwatch's clock. Apart from <see cref="Sample"/>, the tests call it with
    /// processes of their own making.
    /// </summary>
    internal ProcessSnapshot Read(IntPtr buffer, long now)
    {
        var snapshot = new ProcessSnapshot();

        // By the precise clock: a sample taken right after another (the Memory page asking for its details) is only
        // milliseconds later, where TickCount's 16 ms steps would make CPU use look several times too high.
        double seconds = Stopwatch.GetElapsedTime(_lastSampleTime, now).TotalSeconds;
        double elapsed100ns = seconds * TimeSpan.TicksPerSecond * Environment.ProcessorCount;
        bool haveBaseline = _lastSampleTime != 0 && elapsed100ns > 0;
        var current = new Dictionary<(int, long), (long, long)>(_last.Count);
        var detail = Detail;

        byte* p = (byte*)buffer;
        while (true)
        {
            var info = (Win32.SYSTEM_PROCESS_INFORMATION*)p;
            int pid = (int)info->UniqueProcessId;
            // "System" (4) is no app, but much of what apps write reaches the drive in its name: it counts in the total.
            if (pid >= 4)
            {
                long cpuTime = info->UserTime + info->KernelTime, io = info->ReadTransferCount + info->WriteTransferCount;
                var key = (pid, info->CreateTime);
                current[key] = (cpuTime, io);

                double cpu = 0, disk = 0;
                if (haveBaseline && _last.TryGetValue(key, out var before))
                {
                    cpu = Math.Clamp((cpuTime - before.Cpu) / elapsed100ns * 100, 0, 100); // Windows counts CPU time in steps too
                    disk = Math.Max(0, io - before.Io) / seconds;
                }
                snapshot.Disk += disk;

                if (pid > 4 && info->ImageName.Buffer != IntPtr.Zero)
                {
                    var exe = new string((char*)info->ImageName.Buffer, 0, info->ImageName.Length / 2);
                    if (!snapshot.Apps.TryGetValue(exe, out var app))
                    {
                        app = new AppUsage { Exe = exe, FirstPid = pid, Created = info->CreateTime };
                        snapshot.Apps[exe] = app;
                    }
                    app.Count++;
                    app.Cpu = Math.Min(100, app.Cpu + cpu);
                    double mem = info->WorkingSetPrivateSize / (1024.0 * 1024.0);
                    app.MemMB += mem;
                    app.Disk += disk;
                    if (info->CreateTime < app.Created) app.Created = info->CreateTime;
                    if (detail?.Contains(exe) == true) (app.Processes ??= []).Add(new ProcessUsage(pid, info->CreateTime, cpu, mem, disk));
                    snapshot.PidToExe[pid] = exe;
                    snapshot.Processes.Add(key);
                }
            }

            if (info->NextEntryOffset == 0) break;
            p += info->NextEntryOffset;
        }

        _last = current;
        _lastSampleTime = now;
        return snapshot;
    }
}
