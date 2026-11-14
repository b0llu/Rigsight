using System.Runtime.InteropServices;
using Rigsight.Agent.Network;
using Rigsight.Core;

namespace Rigsight.Agent.Storage;

/// <summary>A process (by the ID the disk event names) and a physical drive (its number in Windows: \\.\PhysicalDriveN).</summary>
internal readonly record struct DiskIoKey(int Pid, int Disk);

/// <summary>What one process read from and wrote to one drive since the last <see cref="DiskTrace.Take"/>, in bytes.</summary>
internal sealed class DiskCounts
{
    public long Read, Write;
    public long Total => Read + Write;
    public bool IsEmpty => Read == 0 && Write == 0;
}

/// <summary>A physical drive: its number in Windows now, its key in the history (see Core's DriveKey), model and letters.</summary>
internal sealed record PhysicalDisk(int Number, string Key, string Model, List<string> Letters);

/// <summary>
/// Every read and write that reaches a physical drive, by the process that asked for it, from Windows' own disk events
/// (Microsoft-Windows-Kernel-Disk: the request as the drive got it, so what came from memory, the network or another
/// device isn't in it), in a real-time trace session of our own. Writes Windows' file cache makes on the apps' behalf,
/// and what it reads ahead for them, come from the system process (ID 4). Needs admin rights. Events are added up by
/// process and drive as they come, on the trace's thread; the sampler takes the totals once a second. Built the way
/// <see cref="NetworkTrace"/> is.
/// </summary>
internal sealed unsafe class DiskTrace : IDisposable
{
    private static readonly Guid KernelDisk = new("C7BDE69A-E1E0-4177-B6EF-283AD1525271");
    /// <summary>The provider's one keyword (its Analytic channel): reads (event 10), writes (11) and flushes (14).</summary>
    internal const ulong KeywordAnalytic = 0x8000000000000000;
    internal const ushort EventRead = 10, EventWrite = 11;
    /// <summary>DiskNumber (UInt32), IrpFlags (UInt32), TransferSize (UInt32): the part of the payload read.</summary>
    internal const int MinPayload = 12;

    private const int PropertiesSize = 120 + 2 * 256; // EVENT_TRACE_PROPERTIES, then the session's name
    private const int LogFileSize = 448; // EVENT_TRACE_LOGFILEW (x64)

    /// <summary>The start of every test copy's trace name (a test copy adds its own suffix: "Rigsight Disk.1A2B…").</summary>
    internal const string TestPrefix = "Rigsight Disk.";

    private readonly string _name;
    private readonly EventCallback _callback; // kept alive while the trace runs
    private ulong _session, _trace;
    private Thread? _thread;
    private readonly Lock _gate = new();
    private Dictionary<DiskIoKey, DiskCounts> _counts = [];
    private Dictionary<DiskIoKey, DiskCounts> _spare = [];
    private volatile bool _stopped;
    private long _events;

    /// <summary>Events delivered since the trace started, counted or not (see <see cref="TraceWatch"/>).</summary>
    public long Events => Interlocked.Read(ref _events);

    /// <summary>Windows ended the trace by itself (nobody stopped it): nothing more will come from this one.</summary>
    public volatile bool Ended;

    private DiskTrace(string name)
    {
        _name = name;
        _callback = OnEvent;
    }

    /// <summary>The trace, started; null without admin rights, or when Windows refuses another session.</summary>
    public static DiskTrace? Start(string name)
    {
        var trace = new DiskTrace(name);
        try
        {
            if (trace.Open()) return trace;
        }
        catch (Exception ex)
        {
            Log.Error("disk", ex);
        }
        trace.Dispose();
        return null;
    }

    private bool Open()
    {
        IntPtr props = Marshal.AllocHGlobal(PropertiesSize);
        try
        {
            // A session left behind by an agent that didn't stop cleanly is stopped first, or the name is taken.
            Control(props, 0, _name, EventTraceControlStop);
            InitProperties(props);
            int rc = StartTraceW(out _session, _name, props);
            if (rc != 0)
            {
                Log.Write("disk", rc == 5 ? "No admin rights: what each app reads and writes on the drives can't be read"
                    : $"Expected to start the disk trace \"{_name}\", Windows answered {rc}");
                _session = 0;
                return false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(props);
        }

        var provider = KernelDisk;
        int enabled = EnableTraceEx2(_session, &provider, 1 /* enable */, 4 /* information */, KeywordAnalytic, 0, 0, IntPtr.Zero);
        if (enabled != 0)
        {
            Log.Write("disk", $"Expected Windows' disk events (Microsoft-Windows-Kernel-Disk), Windows answered {enabled}");
            return false;
        }

        IntPtr logFile = Marshal.AllocHGlobal(LogFileSize);
        IntPtr loggerName = Marshal.StringToHGlobalUni(_name);
        try
        {
            new Span<byte>((void*)logFile, LogFileSize).Clear();
            byte* l = (byte*)logFile;
            *(IntPtr*)(l + 8) = loggerName;                                   // LoggerName
            *(uint*)(l + 28) = ProcessTraceModeRealTime | ProcessTraceModeEventRecord;
            *(IntPtr*)(l + 424) = Marshal.GetFunctionPointerForDelegate(_callback); // EventRecordCallback
            _trace = OpenTraceW(logFile);
            if (_trace == InvalidTrace)
            {
                _trace = 0;
                Log.Write("disk", $"Expected to open the disk trace for reading, found error {Marshal.GetLastPInvokeError()}");
                return false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(logFile);
            Marshal.FreeHGlobal(loggerName);
        }

        ulong handle = _trace;
        _thread = new Thread(() =>
        {
            ulong h = handle;
            int rc = ProcessTrace(&h, 1, IntPtr.Zero, IntPtr.Zero);
            if (_stopped) return;
            Log.Write("disk", $"The disk trace stopped by itself ({rc}) after {Events} events");
            Ended = true;
        }) { IsBackground = true, Name = "Disk trace" };
        _thread.Start();
        Log.Write("disk", $"Reading what each app reads and writes on each drive (trace \"{_name}\", Microsoft-Windows-Kernel-Disk enabled, keyword 0x{KeywordAnalytic:X}, events {EventRead} and {EventWrite})");
        return true;
    }

    private void InitProperties(IntPtr props)
    {
        new Span<byte>((void*)props, PropertiesSize).Clear();
        byte* b = (byte*)props;
        *(uint*)(b + 0) = PropertiesSize;   // Wnode.BufferSize
        *(uint*)(b + 40) = 1;               // Wnode.ClientContext: the precise clock
        *(uint*)(b + 44) = 0x20000;         // Wnode.Flags: WNODE_FLAG_TRACED_GUID
        *(uint*)(b + 48) = 64;              // BufferSize (KB)
        *(uint*)(b + 52) = 2;               // MinimumBuffers
        *(uint*)(b + 56) = 16;              // MaximumBuffers
        *(uint*)(b + 64) = 0x100;           // LogFileMode: EVENT_TRACE_REAL_TIME_MODE
        *(uint*)(b + 68) = 1;               // FlushTimer (seconds)
        *(uint*)(b + 116) = 120;            // LoggerNameOffset
    }

    private void Control(IntPtr props, ulong session, string? name, uint code)
    {
        InitProperties(props);
        ControlTraceW(session, name, props, code);
    }

    /// <summary>Stops the disk traces test copies of Rigsight left running (every "Rigsight Disk.…" but <paramref name="own"/>).</summary>
    public static List<string> StopOthers(string own) => NetworkTrace.StopOthers(own, TestPrefix);

    /// <summary>Each process's bytes on each drive since the last call (taken on the sampler thread).</summary>
    public Dictionary<DiskIoKey, DiskCounts> Take()
    {
        lock (_gate)
        {
            var taken = _counts;
            _spare.Clear();
            _counts = _spare;
            _spare = taken;
            return taken;
        }
    }

    /// <summary>
    /// One event's payload: a read (event 10) or a write (11) of <paramref name="size"/> bytes on drive
    /// <paramref name="disk"/>. False for any other event (a flush, 14) or a payload too short to hold them.
    /// </summary>
    internal static bool Decode(ushort id, ReadOnlySpan<byte> data, out int disk, out bool write, out long size)
    {
        disk = 0;
        size = 0;
        write = id == EventWrite;
        if (id != EventRead && id != EventWrite || data.Length < MinPayload) return false;
        disk = (int)MemoryMarshal.Read<uint>(data);
        size = MemoryMarshal.Read<uint>(data[8..]);
        return size > 0;
    }

    /// <summary>Adds one read or write into <paramref name="counts"/> (the trace's thread, under its lock).</summary>
    internal static void Add(Dictionary<DiskIoKey, DiskCounts> counts, int pid, int disk, bool write, long size)
    {
        var key = new DiskIoKey(pid, disk);
        if (!counts.TryGetValue(key, out var c)) counts[key] = c = new DiskCounts();
        if (write) c.Write += size;
        else c.Read += size;
    }

    // ── The trace's thread ────────────────────────────────────────────────

    private void OnEvent(IntPtr record)
    {
        Interlocked.Increment(ref _events);
        byte* r = (byte*)record;
        ushort id = *(ushort*)(r + 40);              // EventHeader.EventDescriptor.Id
        if (id != EventRead && id != EventWrite) return;
        int pid = *(int*)(r + 12);                   // EventHeader.ProcessId: the process that asked (-1 when Windows doesn't say)
        ushort length = *(ushort*)(r + 86);          // UserDataLength
        byte* data = *(byte**)(r + 96);              // UserData
        if (data is null || !Decode(id, new ReadOnlySpan<byte>(data, length), out int disk, out bool write, out long size)) return;
        lock (_gate) Add(_counts, pid, disk, write, size);
    }

    public void Dispose()
    {
        _stopped = true;
        if (_session != 0)
        {
            IntPtr props = Marshal.AllocHGlobal(PropertiesSize);
            try { Control(props, _session, null, EventTraceControlStop); }
            finally { Marshal.FreeHGlobal(props); }
            _session = 0;
        }
        if (_trace != 0)
        {
            CloseTrace(_trace);
            _trace = 0;
        }
        _thread?.Join(2000);
    }

    // ── Windows ───────────────────────────────────────────────────────────

    private const uint EventTraceControlStop = 1;
    private const uint ProcessTraceModeRealTime = 0x100, ProcessTraceModeEventRecord = 0x10000000;
    private const ulong InvalidTrace = ulong.MaxValue;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EventCallback(IntPtr record);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int StartTraceW(out ulong handle, string name, IntPtr properties);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int ControlTraceW(ulong handle, string? name, IntPtr properties, uint code);
    [DllImport("advapi32.dll")] private static extern int EnableTraceEx2(ulong handle, Guid* provider, uint control, byte level, ulong anyKeyword, ulong allKeyword, uint timeout, IntPtr parameters);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern ulong OpenTraceW(IntPtr logFile);
    [DllImport("advapi32.dll")] private static extern int ProcessTrace(ulong* handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")] private static extern int CloseTrace(ulong handle);
}
