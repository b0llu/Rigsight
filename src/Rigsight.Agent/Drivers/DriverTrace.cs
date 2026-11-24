using System.Diagnostics;
using System.Runtime.InteropServices;
using Rigsight.Agent.Network;
using Rigsight.Core;
using Rigsight.Core.Drivers;

namespace Rigsight.Agent.Drivers;

/// <summary>
/// How long each driver keeps a CPU core to itself (issue #15), from Windows' own timing of every deferred procedure call
/// (DPC) and interrupt service routine (ISR): the numbers LatencyMon shows. A real-time trace session of our own with the
/// kernel's DPC and interrupt events switched on. It is a private "system logger" session (Windows 8 and later), not the
/// one "NT Kernel Logger" that profilers and LatencyMon need for themselves, so running it takes nothing from them. Needs
/// admin rights. Each call's own time (an interrupt inside it on the same core taken out) is put down to the driver whose
/// code it ran and kept by <see cref="DriverLedger"/>; the sampler reads the recent bursts and takes each minute's tallies.
/// </summary>
internal sealed unsafe class DriverTrace : IDisposable
{
    /// <summary>The classic kernel provider the DPC and interrupt events come from (PerfInfo).</summary>
    internal static readonly Guid PerfInfo = new("CE1DBFB4-137E-4DA6-87B0-3F59AA102CBC");

    /// <summary>
    /// The session's own GUID. A system logger session names itself with a GUID of its own (not the kernel logger's
    /// SystemTraceControlGuid); this one is ours, made for RigSight's driver trace on 2026-11-22. A test copy gets one of
    /// its own, made from its suffix: with the same GUID Windows refused its session while the installed RigSight's ran
    /// (error 183, already exists, 2026-11-24), a different name or not.
    /// </summary>
    internal static readonly Guid SessionGuid = RigsightPaths.InstanceSuffix.Length == 0
        ? new("5B0B6C2E-8F4A-4D7B-9E21-6A3C1D7F0E94")
        : new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("Rigsight Drivers" + RigsightPaths.InstanceSuffix)));

    /// <summary>PerfInfo opcodes (EventDescriptor.Opcode): what kind of call each event is.</summary>
    internal const byte OpThreadedDpc = 66, OpIsr = 67, OpDpc = 68, OpTimerDpc = 69;

    /// <summary>
    /// An interrupt a device signalled by message (MSI/MSI-X, PERFINFO_LOG_TYPE_ISR_MSI): most PCIe devices (NVMe drives,
    /// graphics and network cards) interrupt this way. Its payload starts as an ISR's (a MessageNumber follows), and it is
    /// counted as one; <see cref="MsiIsrs"/> says how many came this way, for the probe.
    /// </summary>
    internal const byte OpIsrMsi = 50;

    /// <summary>InitialTime (UInt64) and Routine (a 64-bit address): what every DPC and ISR event starts with.</summary>
    internal const int MinPayload = 16;

    /// <summary>A call longer than this (seconds) is a clock read wrong, not a driver: left out (and counted, see <see cref="Odd"/>).</summary>
    private const int MaxCallSec = 1;

    /// <summary>How often the trace's clock is tied to the wall clock again (see DriverLedger.Anchor).</summary>
    private const int AnchorEverySec = 5 * 60;

    /// <summary>The fewest seconds between two reads of the driver list, however many addresses go unmatched.</summary>
    private const int MapEverySec = 60;

    /// <summary>Routine addresses remembered with their driver; past this the memory is cleared and built again.</summary>
    private const int CacheMax = 4096;

    private const int PropertiesSize = 120 + 2 * 256; // EVENT_TRACE_PROPERTIES, then the session's name
    private const int LogFileSize = 448; // EVENT_TRACE_LOGFILEW (x64)

    /// <summary>The start of every test copy's trace name (a test copy adds its own suffix: "Rigsight Drivers.1A2B…").</summary>
    internal const string TestPrefix = "Rigsight Drivers.";

    private readonly string _name;
    private readonly EventCallback _callback; // kept alive while the trace runs
    private ulong _session, _trace;
    private Thread? _thread;
    private volatile bool _stopped;
    private long _events, _matched, _odd, _msi;

    // Only the trace's thread touches these after the start.
    private readonly DriverLedger _ledger;
    private readonly Dictionary<ulong, int> _drivers = []; // routine address → driver number
    private ModuleMap _map = ModuleMap.Empty;
    private long _mapReadAt, _anchoredAt; // Stopwatch ticks
    private int _misses;
    private readonly long _maxCall, _anchorEvery, _mapEvery;

    /// <summary>Events delivered since the trace started, counted or not.</summary>
    public long Events => Interlocked.Read(ref _events);

    /// <summary>DPC and ISR events whose routine lay in a loaded driver (the rest are put down to "").</summary>
    public long Matched => Interlocked.Read(ref _matched);

    /// <summary>Events left out because their start came after their end, or more than a second before it.</summary>
    public long Odd => Interlocked.Read(ref _odd);

    /// <summary>Interrupts among the ISRs that came as message-signalled ones (see <see cref="OpIsrMsi"/>).</summary>
    public long MsiIsrs => Interlocked.Read(ref _msi);

    /// <summary>Windows ended the trace by itself (nobody stopped it): nothing more will come from this one.</summary>
    public volatile bool Ended;

    /// <summary>What is kept of each call (recent bursts, minute tallies, counts by kind).</summary>
    public DriverLedger Ledger => _ledger;

    private DriverTrace(string name)
    {
        _name = name;
        _callback = OnEvent;
        long f = Stopwatch.Frequency;
        _ledger = new DriverLedger(f, Environment.ProcessorCount);
        _maxCall = MaxCallSec * f;
        _anchorEvery = AnchorEverySec * f;
        _mapEvery = MapEverySec * f;
    }

    /// <summary>The trace, started; null without admin rights, or when Windows refuses another session.</summary>
    public static DriverTrace? Start(string name)
    {
        var trace = new DriverTrace(name);
        try
        {
            if (trace.Open()) return trace;
        }
        catch (Exception ex)
        {
            Log.Error("drivers", ex);
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
                Log.Write("drivers", rc switch
                {
                    5 => "No admin rights: how long each driver keeps a CPU core can't be read",
                    ErrorNoSystemResources => $"Expected to start the driver trace \"{_name}\", but every slot Windows has for this kind of trace is in use (answer {rc}); running now: {string.Join(", ", NetworkTrace.Running())}",
                    _ => $"Expected to start the driver trace \"{_name}\" (a system logger session, DPC and interrupt events), Windows answered {rc}",
                });
                _session = 0;
                return false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(props);
        }

        // The driver list before the first event, and the clocks tied together.
        _map = ModuleMap.Read();
        _mapReadAt = Stopwatch.GetTimestamp();
        if (_map.Hidden > 0 && _map.Count == 0)
            Log.Write("drivers", $"Expected where each driver is loaded, Windows gave {_map.Hidden} drivers with no address: calls can't be put down to a driver");
        Anchor();

        IntPtr logFile = Marshal.AllocHGlobal(LogFileSize);
        IntPtr loggerName = Marshal.StringToHGlobalUni(_name);
        try
        {
            new Span<byte>((void*)logFile, LogFileSize).Clear();
            byte* l = (byte*)logFile;
            *(IntPtr*)(l + 8) = loggerName;                                   // LoggerName
            // Raw time stamps: the event's TimeStamp stays the precise counter's own value, the clock the payload's
            // InitialTime is in. Without it ProcessTrace turns TimeStamp into a file time and the two can't be subtracted.
            *(uint*)(l + 28) = ProcessTraceModeRealTime | ProcessTraceModeEventRecord | ProcessTraceModeRawTimestamp;
            *(IntPtr*)(l + 424) = Marshal.GetFunctionPointerForDelegate(_callback); // EventRecordCallback
            _trace = OpenTraceW(logFile);
            if (_trace == InvalidTrace)
            {
                _trace = 0;
                Log.Write("drivers", $"Expected to open the driver trace for reading, found error {Marshal.GetLastPInvokeError()}");
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
            Log.Write("drivers", $"The driver trace stopped by itself ({rc}) after {Events} events");
            Ended = true;
        }) { IsBackground = true, Name = "Driver trace" };
        _thread.Start();
        Log.Write("drivers", $"Reading how long each driver keeps a CPU core (trace \"{_name}\", system logger, DPC and interrupt events, {_map.Count} drivers loaded)");
        return true;
    }

    private void InitProperties(IntPtr props)
    {
        new Span<byte>((void*)props, PropertiesSize).Clear();
        byte* b = (byte*)props;
        *(uint*)(b + 0) = PropertiesSize;   // Wnode.BufferSize
        *(Guid*)(b + 24) = SessionGuid;     // Wnode.Guid: the session's own (a system logger needs one)
        *(uint*)(b + 40) = 1;               // Wnode.ClientContext: the precise clock (QueryPerformanceCounter), as InitialTime is
        *(uint*)(b + 44) = 0x20000;         // Wnode.Flags: WNODE_FLAG_TRACED_GUID
        // DPCs and interrupts come at 5,000 to 50,000 a second (a busy network card or a high polling-rate mouse more):
        // 64 KB buffers hold about 1,500 events each, and up to 64 of them (4 MB, taken only under load) ride out a
        // second or more of the reader being late without losing any. The Phase 0 probe tunes these.
        *(uint*)(b + 48) = 64;              // BufferSize (KB)
        *(uint*)(b + 52) = 4;               // MinimumBuffers
        *(uint*)(b + 56) = 64;              // MaximumBuffers
        *(uint*)(b + 64) = EventTraceRealTimeMode | EventTraceSystemLoggerMode; // LogFileMode
        *(uint*)(b + 68) = 1;               // FlushTimer (seconds): an idle PC's few events still arrive within a second
        *(uint*)(b + 72) = EventTraceFlagDpc | EventTraceFlagInterrupt; // EnableFlags: the kernel's DPC and ISR events
        *(uint*)(b + 116) = 120;            // LoggerNameOffset
    }

    private void Control(IntPtr props, ulong session, string? name, uint code)
    {
        InitProperties(props);
        ControlTraceW(session, name, props, code);
    }

    /// <summary>Stops the driver traces test copies of Rigsight left running (every "Rigsight Drivers.…" but <paramref name="own"/>).</summary>
    public static List<string> StopOthers(string own) => NetworkTrace.StopOthers(own, TestPrefix);

    /// <summary>
    /// What Windows says it dropped (asked of the session each time): events it had no buffer for, plus whole buffers the
    /// reader was too late for (each held many events). Anything but 0 means calls went uncounted.
    /// </summary>
    public long Lost
    {
        get
        {
            if (_session == 0) return 0;
            IntPtr props = Marshal.AllocHGlobal(PropertiesSize);
            try
            {
                InitProperties(props);
                if (ControlTraceW(_session, null, props, EventTraceControlQuery) != 0) return 0;
                byte* b = (byte*)props;
                return *(uint*)(b + 88) + (long)*(uint*)(b + 100); // EventsLost + RealTimeBuffersLost
            }
            finally
            {
                Marshal.FreeHGlobal(props);
            }
        }
    }

    /// <summary>The bursts of <see cref="DriverRules.RingUs"/> or more that ended at or after <paramref name="sinceMs"/> (Unix ms).</summary>
    public IReadOnlyList<DriverBurst> Recent(double sinceMs) => _ledger.Recent(sinceMs);

    /// <summary>Each driver's minute since the last call, as the minute starting at <paramref name="minuteTs"/> (Unix seconds).</summary>
    public List<DriverTally> TakeMinute(long minuteTs) => _ledger.TakeMinute(minuteTs);

    /// <summary>
    /// One event's payload: a DPC, timer DPC, threaded DPC or ISR (by <paramref name="opcode"/>, a message-signalled ISR too), with the precise clock's
    /// value when it started and the address of the routine it ran. An ISR also carries its interrupt's vector
    /// (<paramref name="vector"/>, -1 when absent or not an ISR). False for any other opcode or a payload too short.
    /// </summary>
    /// <remarks>
    /// Layout (x64, packed as the kernel writes it): +0 InitialTime (UInt64), +8 Routine (pointer); an ISR then +16
    /// ReturnValue (byte), +17 Vector (UInt16), +19 Reserved (byte), and on newer Windows a MessageIndex after.
    /// </remarks>
    internal static bool Decode(byte opcode, ReadOnlySpan<byte> data, out DriverCall kind, out long initialTime, out ulong routine, out int vector)
    {
        initialTime = 0;
        routine = 0;
        vector = -1;
        kind = opcode switch
        {
            OpDpc => DriverCall.Dpc,
            OpTimerDpc => DriverCall.TimerDpc,
            OpThreadedDpc => DriverCall.ThreadedDpc,
            OpIsr or OpIsrMsi => DriverCall.Isr,
            _ => (DriverCall)(-1),
        };
        if ((int)kind < 0 || data.Length < MinPayload) return false;
        initialTime = MemoryMarshal.Read<long>(data);
        routine = MemoryMarshal.Read<ulong>(data[8..]);
        if (kind == DriverCall.Isr && data.Length >= 19) vector = MemoryMarshal.Read<ushort>(data[17..]);
        return true;
    }

    /// <summary>
    /// The core an event happened on (ETW_BUFFER_CONTEXT, EVENT_RECORD +80): the 16-bit ProcessorIndex when the header
    /// says it holds one (EVENT_HEADER_FLAG_PROCESSOR_INDEX, Windows 7 and later: PCs with more than 256 cores need it),
    /// else the one-byte ProcessorNumber in the same place.
    /// </summary>
    internal static int Cpu(ushort headerFlags, ReadOnlySpan<byte> bufferContext) =>
        (headerFlags & EventHeaderFlagProcessorIndex) != 0 ? MemoryMarshal.Read<ushort>(bufferContext) : bufferContext[0];

    // ── The trace's thread ────────────────────────────────────────────────

    private void OnEvent(IntPtr record)
    {
        Interlocked.Increment(ref _events);
        byte* r = (byte*)record;
        if (*(Guid*)(r + 24) != PerfInfo) return;            // EventHeader.ProviderId
        byte opcode = r[45];                                 // EventHeader.EventDescriptor.Opcode
        if (opcode is (< OpThreadedDpc or > OpTimerDpc) and not OpIsrMsi) return;
        ushort length = *(ushort*)(r + 86);                  // UserDataLength
        byte* data = *(byte**)(r + 96);                      // UserData
        if (data is null || !Decode(opcode, new ReadOnlySpan<byte>(data, length), out var kind, out long start, out ulong routine, out _)) return;

        long end = *(long*)(r + 16);                         // EventHeader.TimeStamp: the precise counter when it ended
        long ticks = end - start;
        if (ticks < 0 || ticks > _maxCall)
        {
            Interlocked.Increment(ref _odd);
            return;
        }
        int cpu = Cpu(*(ushort*)(r + 4), new ReadOnlySpan<byte>(r + 80, 2)); // EventHeader.Flags; BufferContext

        if (!_drivers.TryGetValue(routine, out int driver)) driver = Resolve(routine);
        if (driver != 0) Interlocked.Increment(ref _matched);
        if (opcode == OpIsrMsi) Interlocked.Increment(ref _msi);
        _ledger.Add(end, ticks, cpu, driver, kind);

        if (end - _anchoredAt > _anchorEvery) Anchor();
    }

    /// <summary>
    /// A routine address seen for the first time: its driver's number, remembered. Addresses no driver holds pile up when
    /// a driver loaded after the list was read; the list is then read again (once a minute at most) and every address
    /// looked up afresh, since an unloaded driver's addresses can belong to another now.
    /// </summary>
    private int Resolve(ulong routine)
    {
        string file = _map.Find(routine);
        if (file.Length == 0 && ++_misses >= 8 && Stopwatch.GetTimestamp() - _mapReadAt > _mapEvery)
        {
            int before = _map.Count;
            _map = ModuleMap.Read();
            _mapReadAt = Stopwatch.GetTimestamp();
            _misses = 0;
            _drivers.Clear();
            Log.Write("drivers", $"Addresses outside every loaded driver: read the driver list again ({before} drivers before, {_map.Count} now)");
            file = _map.Find(routine);
        }
        if (_drivers.Count >= CacheMax) _drivers.Clear();
        int driver = _ledger.Driver(file);
        _drivers[routine] = driver;
        return driver;
    }

    private void Anchor()
    {
        long now = Stopwatch.GetTimestamp();
        // DateTime.UtcNow is Windows' precise system time (to 100 ns), read right after the counter.
        _ledger.Anchor(now, (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds);
        _anchoredAt = now;
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

    private const uint EventTraceControlQuery = 0, EventTraceControlStop = 1;
    private const uint EventTraceRealTimeMode = 0x100, EventTraceSystemLoggerMode = 0x02000000;
    private const uint EventTraceFlagDpc = 0x20, EventTraceFlagInterrupt = 0x40;
    private const uint ProcessTraceModeRealTime = 0x100, ProcessTraceModeRawTimestamp = 0x1000, ProcessTraceModeEventRecord = 0x10000000;
    private const ushort EventHeaderFlagProcessorIndex = 0x0200;
    private const int ErrorNoSystemResources = 1450;
    private const ulong InvalidTrace = ulong.MaxValue;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EventCallback(IntPtr record);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int StartTraceW(out ulong handle, string name, IntPtr properties);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int ControlTraceW(ulong handle, string? name, IntPtr properties, uint code);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern ulong OpenTraceW(IntPtr logFile);
    [DllImport("advapi32.dll")] private static extern int ProcessTrace(ulong* handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")] private static extern int CloseTrace(ulong handle);
}
