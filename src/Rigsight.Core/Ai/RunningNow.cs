using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Rigsight.Core.Apps;
using Rigsight.Core.Settings;

namespace Rigsight.Core.Ai;

/// <summary>One app running now (every process of its exe together), as the AI helper's "running now" look-up saw it.</summary>
/// <param name="Ours">Running in this person's Windows session (not a service, not another account's).</param>
public sealed record RunningApp(string Exe, string Name, string? Path, int Processes, double MemMb, double Cpu, double Gpu,
    bool HasWindow, bool InFront, AppCategory Category, bool Ours = true)
{
    /// <summary>What ending it would do (a part of Windows, RigSight itself): only an ordinary app may be closed from an answer.</summary>
    public EndRisk Risk => EndRisks.Classify(Exe, Path);

    /// <summary>
    /// Whether an answer may offer to close it: an ordinary app of this person's session whose program Windows says where
    /// it is (Windows keeps its own protected ones' place to itself: Memory Compression, the antivirus), not a game (it may
    /// be the one being played, alt-tabbed from to ask), not the app in front, and not RigSight. A first run listed
    /// "Memory Compression" and Microsoft Defender as closable: with no place known, the name alone let them through.
    /// </summary>
    public bool CanClose => Ours && !string.IsNullOrEmpty(Path) && Risk == EndRisk.None && Category != AppCategory.Game && !InFront;
}

/// <summary>Every app running at one moment, busiest first, and when it was looked at.</summary>
public sealed record RunningSnapshot(DateTime At, IReadOnlyList<RunningApp> Apps)
{
    /// <summary>The app an answer named ("python", "python.exe", "Python"), or null when nothing running matches.</summary>
    public RunningApp? Find(string named)
    {
        string n = named.Trim().Trim('"', '\'');
        if (n.Length == 0) return null;
        string exe = n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n : n + ".exe";
        return Apps.FirstOrDefault(a => a.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase))
            ?? Apps.FirstOrDefault(a => a.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The look-up's answer for the model: the apps using the most memory, CPU or graphics card now.</summary>
    public string Text()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Apps running now (looked at {At.ToString("h:mm:ss tt", CultureInfo.InvariantCulture)}, CPU and graphics card over one second):");
        var shown = Apps.Where(a => a.MemMb >= 150 || a.Cpu >= 2 || a.Gpu >= 2).Take(20).ToList();
        if (shown.Count == 0) shown = [.. Apps.Take(8)];
        foreach (var a in shown)
        {
            var notes = new List<string>();
            if (a.InFront) notes.Add("the app in front");
            if (a.Category == AppCategory.Game) notes.Add("a game");
            if (!a.HasWindow) notes.Add("no window, working in the background");
            if (a.Risk != EndRisk.None) notes.Add(a.Risk is EndRisk.OwnWindow or EndRisk.OwnAgent ? "RigSight itself" : "part of Windows, never closed from here");
            else if (!a.Ours || string.IsNullOrEmpty(a.Path)) notes.Add("a Windows or system process, never closed from here");
            sb.AppendLine($"  {a.Name} ({a.Exe}{(a.Processes > 1 ? $", {a.Processes} processes" : "")}): memory {Units.Megabytes(a.MemMb)}, CPU {a.Cpu:0}%, graphics card {a.Gpu:0}%"
                + (notes.Count > 0 ? $"; {string.Join(", ", notes)}" : "") + (a.CanClose ? "; can be closed from an answer" : ""));
        }
        sb.AppendLine("This is a moment, not history: an app's usual use is in get_apps and get_memory.");
        return sb.ToString();
    }
}

/// <summary>
/// Looks at every process now, from the app itself (no agent needed): memory as Task Manager's "Memory" column (private
/// working set), CPU over one second, and the main graphics card's 3D engine over the same second. One call to Windows
/// for all processes, like the agent's own sampler.
/// </summary>
public static unsafe class RunningNow
{
    public static RunningSnapshot Sample(int sampleMs = 1000)
    {
        using var gpu = new GpuUse();
        gpu.Collect();
        var first = Read();
        long t0 = Stopwatch.GetTimestamp();
        Thread.Sleep(sampleMs);
        var second = Read();
        double seconds = Stopwatch.GetElapsedTime(t0).TotalSeconds;
        var gpuByPid = gpu.CollectByPid();

        uint front = 0;
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero) GetWindowThreadProcessId(foreground, out front);
        var windowed = WindowedPids();

        double cores = Environment.ProcessorCount * seconds * TimeSpan.TicksPerSecond;
        uint session = (uint)Process.GetCurrentProcess().SessionId;
        var apps = new List<RunningApp>();
        foreach (var group in second.Values.Where(p => p.Pid > 4).GroupBy(p => p.Exe, StringComparer.OrdinalIgnoreCase))
        {
            double cpu = 0, gpuUse = 0, mem = 0;
            string? path = null;
            bool hasWindow = false, inFront = false, ours = false;
            foreach (var p in group)
            {
                if (first.TryGetValue((p.Pid, p.Created), out var before) && cores > 0) cpu += Math.Max(0, p.CpuTime - before.CpuTime) / cores * 100;
                gpuUse += gpuByPid.GetValueOrDefault(p.Pid);
                mem += p.MemMb;
                path ??= PathOf(p.Pid);
                hasWindow |= windowed.Contains((uint)p.Pid);
                inFront |= (uint)p.Pid == front;
                ours |= p.Session == session;
            }
            string exe = group.Key;
            string name = AppCatalog.ResolveName(exe, path);
            apps.Add(new RunningApp(exe, name, path, group.Count(), mem, Math.Min(100, cpu), Math.Min(100, gpuUse), hasWindow, inFront, AppCatalog.Classify(exe, path), ours));
        }
        // Busiest first: whichever of memory, CPU and graphics card it uses most of, as a share of the PC.
        double totalMb = Math.Max(1, apps.Sum(a => a.MemMb));
        return new RunningSnapshot(DateTime.Now, [.. apps.OrderByDescending(a => Math.Max(a.MemMb / totalMb * 100, Math.Max(a.Cpu, a.Gpu)))]);
    }

    private readonly record struct Proc(int Pid, long Created, string Exe, long CpuTime, double MemMb, uint Session);

    private static Dictionary<(int, long), Proc> Read()
    {
        var result = new Dictionary<(int, long), Proc>();
        int size = 1 << 20;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            int status;
            while ((status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out int needed)) == StatusInfoLengthMismatch)
            {
                Marshal.FreeHGlobal(buffer);
                size = Math.Max(needed, size) + (256 << 10);
                buffer = Marshal.AllocHGlobal(size);
            }
            if (status != 0) return result;
            byte* p = (byte*)buffer;
            while (true)
            {
                var info = (ProcessInformation*)p;
                int pid = (int)info->UniqueProcessId;
                if (pid > 4 && info->ImageName.Buffer != IntPtr.Zero)
                {
                    string exe = new((char*)info->ImageName.Buffer, 0, info->ImageName.Length / 2);
                    result[(pid, info->CreateTime)] = new Proc(pid, info->CreateTime, exe, info->UserTime + info->KernelTime, info->WorkingSetPrivateSize / 1_048_576.0, info->SessionId);
                }
                if (info->NextEntryOffset == 0) break;
                p += info->NextEntryOffset;
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static HashSet<uint> WindowedPids()
    {
        var pids = new HashSet<uint>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && GetWindowTextLength(hwnd) > 0 && GetWindow(hwnd, GwOwner) == IntPtr.Zero)
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                pids.Add(pid);
            }
            return true;
        }, IntPtr.Zero);
        return pids;
    }

    private static string? PathOf(int pid)
    {
        var h = OpenProcess(QueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int length = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref length) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// Each process's share of the graphics card's 3D engine (Task Manager's "GPU" column) from Windows' "GPU Engine"
    /// counter: a rate, so read twice, a second apart.
    /// </summary>
    private sealed class GpuUse : IDisposable
    {
        private const uint FormatDouble = 0x200; // PDH_FMT_DOUBLE
        private const int MoreData = unchecked((int)0x800007D2);
        private readonly IntPtr _query, _counter;

        public GpuUse()
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }
            if (PdhAddEnglishCounterW(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _counter) != 0) _counter = IntPtr.Zero;
        }

        public void Collect() { if (_counter != IntPtr.Zero) PdhCollectQueryData(_query); }

        public Dictionary<int, double> CollectByPid()
        {
            var result = new Dictionary<int, double>();
            if (_counter == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return result;
            uint bytes = 0;
            if (PdhGetFormattedCounterArrayW(_counter, FormatDouble, ref bytes, out uint count, IntPtr.Zero) != MoreData || bytes == 0) return result;
            IntPtr items = Marshal.AllocHGlobal((int)bytes);
            try
            {
                if (PdhGetFormattedCounterArrayW(_counter, FormatDouble, ref bytes, out count, items) != 0) return result;
                // PDH_FMT_COUNTERVALUE_ITEM_W: the instance's name, then its status and value (24 bytes on 64-bit Windows).
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = items + i * 24;
                    if (Marshal.ReadInt32(item, 8) != 0 || Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) is not { } name) continue;
                    double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                    // "pid_1234_luid_0x…_phys_0_eng_0_engtype_3D"
                    int end = name.IndexOf('_', 4);
                    if (value <= 0 || !name.StartsWith("pid_", StringComparison.Ordinal) || end < 0
                        || !int.TryParse(name.AsSpan(4, end - 4), NumberStyles.None, CultureInfo.InvariantCulture, out int pid)) continue;
                    result[pid] = result.GetValueOrDefault(pid) + value;
                }
            }
            finally { Marshal.FreeHGlobal(items); }
            return result;
        }

        public void Dispose() { if (_query != IntPtr.Zero) PdhCloseQuery(_query); }
    }

    // ── Windows ──────────────────────────────────────────────────────────
    private const int SystemProcessInformation = 5, StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const uint QueryLimitedInformation = 0x1000, GwOwner = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }

    /// <summary>The leading part of SYSTEM_PROCESS_INFORMATION (as the agent's sampler reads it).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public uint NextEntryOffset, NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount, NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime, UserTime, KernelTime;
        public UnicodeString ImageName;
        public int BasePriority;
        public IntPtr UniqueProcessId, InheritedFromUniqueProcessId;
        public uint HandleCount, SessionId;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr items);
    [DllImport("pdh.dll")] private static extern int PdhCloseQuery(IntPtr query);
}
