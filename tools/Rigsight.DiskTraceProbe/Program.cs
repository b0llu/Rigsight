// Measures the agent's disk trace on its own, from an elevated prompt:
//   dotnet run -c Release --project tools\Rigsight.DiskTraceProbe -- [--idle 30] [--work-gb 4]
// 1. Starts the same trace the agent starts (DiskTrace), as a test copy (its own data folder in %TEMP%, so the trace is
//    "Rigsight Disk.<hash>" and its log lines go to that folder, never the real one).
// 2. At rest for --idle seconds: this process's CPU (the trace's thread does nearly all of it), events a second, memory.
// 3. Heavy disk work in a child process (writes --work-gb GB with write-through, then reads it back unbuffered, in a temp
//    folder that is deleted after): the same numbers, and how much of the work the events put down to the child's
//    process ID (which says whether the event header's process ID is the process that asked).
// Without admin rights the trace can't start: it says so and stops.
using System.Diagnostics;
using System.Runtime.InteropServices;
using Rigsight.Agent.Storage;

if (args.Length >= 3 && args[0] == "--child-work") return ChildWork(args[1], long.Parse(args[2]));

string dataDir = Path.Combine(Path.GetTempPath(), "rigsight-disktrace-probe");
Directory.CreateDirectory(dataDir);
// Before anything reads RigsightPaths: a test copy's data folder and trace name.
Environment.SetEnvironmentVariable("RIGSIGHT_DATA_DIR", dataDir);

int idle = Arg("--idle", 30);
int workGb = Arg("--work-gb", 4);
string name = "Rigsight Disk" + Rigsight.Core.RigsightPaths.InstanceSuffix;

var trace = DiskTrace.Start(name);
if (trace is null)
{
    Console.WriteLine($"The disk trace didn't start (see {Rigsight.Core.RigsightPaths.LogFile}). Without admin rights it can't: run this from an elevated prompt.");
    return 1;
}

var me = Process.GetCurrentProcess();
var totals = new Dictionary<DiskIoKey, DiskCounts>();
using var sampler = new Timer(_ =>
{
    lock (totals)
        foreach (var (k, c) in trace.Take())
        {
            if (!totals.TryGetValue(k, out var t)) totals[k] = t = new DiskCounts();
            t.Read += c.Read;
            t.Write += c.Write;
        }
}, null, 1000, 1000);

try
{
    Console.WriteLine($"Trace \"{name}\" started. At rest for {idle} s…");
    var (cpu, events, seconds) = Measure(() => Thread.Sleep(idle * 1000));
    Report("At rest", cpu, events, seconds);
    lock (totals) totals.Clear();

    string workDir = Path.Combine(Path.GetTempPath(), "rigsight-disktrace-work");
    Directory.CreateDirectory(workDir);
    long bytes = workGb * 1024L * 1024 * 1024;
    int childPid = 0;
    Console.WriteLine($"Heavy disk work: {workGb} GB written and read back in {workDir}…");
    (cpu, events, seconds) = Measure(() =>
    {
        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--child-work \"{workDir}\" {bytes}") { UseShellExecute = false })!;
        childPid = child.Id;
        child.WaitForExit();
    });
    Thread.Sleep(2000); // the last events in
    Report("Heavy disk work", cpu, events, seconds);
    try { Directory.Delete(workDir, recursive: true); } catch { }

    lock (totals)
    {
        long child = totals.Where(kv => kv.Key.Pid == childPid).Sum(kv => kv.Value.Total);
        long system = totals.Where(kv => kv.Key.Pid == 4).Sum(kv => kv.Value.Total);
        long none = totals.Where(kv => kv.Key.Pid is 0 or -1).Sum(kv => kv.Value.Total);
        long all = totals.Values.Sum(c => c.Total);
        Console.WriteLine($"During the work: {Gb(all)} on the drives; the child process ({childPid}) {Gb(child)} (expected about {Gb(2 * bytes)}), "
            + $"System (4) {Gb(system)}, no process named {Gb(none)}");
        foreach (var g in totals.GroupBy(kv => kv.Key.Pid).OrderByDescending(g => g.Sum(kv => kv.Value.Total)).Take(8))
            Console.WriteLine($"  pid {g.Key,6} ({ExeOf(g.Key)}): read {Gb(g.Sum(kv => kv.Value.Read))}, written {Gb(g.Sum(kv => kv.Value.Write))}, drives {string.Join(",", g.Select(kv => kv.Key.Disk).Distinct().Order())}");
    }
}
finally
{
    trace.Dispose();
}
return 0;

(double CpuPercent, long Events, double Seconds) Measure(Action work)
{
    me.Refresh();
    var cpu0 = me.TotalProcessorTime;
    long ev0 = trace.Events;
    var sw = Stopwatch.StartNew();
    work();
    sw.Stop();
    me.Refresh();
    return ((me.TotalProcessorTime - cpu0).TotalSeconds / sw.Elapsed.TotalSeconds * 100, trace.Events - ev0, sw.Elapsed.TotalSeconds);
}

void Report(string what, double cpu, long events, double seconds)
{
    me.Refresh();
    Console.WriteLine($"{what}: {cpu:0.000}% of one core over {seconds:0.0} s, {events / seconds:0} events a second ({events} in all), "
        + $"working set {me.WorkingSet64 / 1048576.0:0.0} MB, private {me.PrivateMemorySize64 / 1048576.0:0.0} MB");
}

int Arg(string flag, int fallback) => Array.IndexOf(args, flag) is int i and >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int v) ? v : fallback;

static string Gb(long b) => $"{b / 1073741824.0:0.00} GB";

static string ExeOf(int pid)
{
    if (pid == 4) return "System";
    if (pid is 0 or -1) return "no process named";
    try { return Process.GetProcessById(pid).ProcessName; } catch { return "ended"; }
}

// The child: writes the file through to the drive, then reads it back past the file cache, 1 MB at a time.
static unsafe int ChildWork(string dir, long bytes)
{
    const int block = 1 << 20;
    const FileOptions NoBuffering = (FileOptions)0x20000000; // FILE_FLAG_NO_BUFFERING: straight to and from the drive
    string file = Path.Combine(dir, "work.bin");
    byte* buffer = (byte*)NativeMemory.AlignedAlloc(block, 4096);
    try
    {
        new Span<byte>(buffer, block).Fill(0x5A);
        using (var h = File.OpenHandle(file, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.WriteThrough | NoBuffering))
            for (long at = 0; at < bytes; at += block) RandomAccess.Write(h, new ReadOnlySpan<byte>(buffer, block), at);
        using (var h = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.None, NoBuffering))
            for (long at = 0; at < bytes; at += block) RandomAccess.Read(h, new Span<byte>(buffer, block), at);
    }
    finally
    {
        NativeMemory.AlignedFree(buffer);
        try { File.Delete(file); } catch { }
    }
    return 0;
}
