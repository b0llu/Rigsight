using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Rigsight.Agent.Network;
using Rigsight.Core;
using Rigsight.Core.Data;

namespace Rigsight.Agent.Frames;

/// <summary>One ask of RigSight's own frames (see <see cref="PresentTrace.Take"/>).</summary>
/// <param name="Ok">The trace watches this game.</param>
/// <param name="Missed">What came before doesn't join on (see <see cref="PresentTimes.Take"/>).</param>
/// <param name="Lost">Windows dropped some of the trace's events since the last ask: the frames may be missing presents.</param>
/// <param name="EndMs">When the last of the frames was presented (Unix ms; 0: not known).</param>
internal readonly record struct PresentRead(bool Ok, bool Missed, bool Lost, long EndMs);

/// <summary>
/// RigSight's own frame times for the game in front, for players without RivaTuner: every present call the game makes,
/// from Windows' own events (the way Intel PresentMon reads them), in a real-time trace session of our own that runs only
/// while a game is in front. DXGI (DirectX 10 to 12) and Direct3D 9 say each present call as it starts and ends, with its
/// swap chain; a game that presents only through the kernel (Vulkan, OpenGL) is counted from the kernel's flip, copy and
/// present history events. Only those events are asked for (an event ID filter), and the runtimes only from the game's
/// own processes (a process filter); the kernel's come from every process and the rest are left out here. Needs admin
/// rights. The time between presents is kept by <see cref="PresentTimes"/>; the sampler takes it once a second.
/// </summary>
internal sealed unsafe class PresentTrace : IDisposable
{
    internal static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    internal static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    internal static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D");

    /// <summary>The controller's ID handed to Windows with each provider's filters; RigSight's own, made 2026-11-22.</summary>
    internal static readonly Guid SourceId = new("3E6F1C2A-7B5D-4E8F-A1C3-9D2B6F4E8A17");

    /// <summary>
    /// DXGI's Present (42/43) and PresentMultiplaneOverlay (55/56) start and stop; Direct3D 9's Present (1/2). Both
    /// providers' events are in their Analytic channel with the "Events" keyword (0x8000000000000002).
    /// </summary>
    internal const ushort DxgiStart = 42, DxgiStop = 43, DxgiMpoStart = 55, DxgiMpoStop = 56, D3D9Start = 1, D3D9Stop = 2;
    internal const ulong RuntimeKeywords = 0x8000000000000002;

    /// <summary>
    /// DxgKrnl's Blit_Info (166), Flip_Info (168), FlipMultiPlaneOverlay_Info (252), PresentHistory_Start (171),
    /// PresentHistoryDetailed_Start (215) and Present_Info (184). Their keyword is Base | Present (and Performance, which
    /// PresentMon takes off as too broad); Windows 10 has no Present keyword on them, so Base alone there.
    /// </summary>
    internal const ushort KBlit = 166, KFlip = 168, KFlipMpo = 252, KHistory = 171, KHistoryDetailed = 215, KPresent = 184;
    internal const ulong KernelKeywordsWin11 = 0x8000001, KernelKeywordsWin10 = 0x1;

    /// <summary>D3DKMT_PRESENT_MODEL values of a present history event that aren't a game's present: GDI drawing and DirectComposition (PresentMon leaves both out).</summary>
    internal const uint ModelRedirectedGdi = 1, ModelRedirectedComposition = 7;

    private const uint FilterTypePid = 0x80000004, FilterTypeEventId = 0x80000200;
    private const uint EnablePropertyIgnoreKeyword0 = 0x10;
    internal const int ParametersSize = 48, FilterSize = 16;

    private const int PropertiesSize = 120 + 2 * 256; // EVENT_TRACE_PROPERTIES, then the session's name
    private const int LogFileSize = 448; // EVENT_TRACE_LOGFILEW (x64)

    /// <summary>The start of every test copy's trace name (a test copy adds its own suffix: "Rigsight Frames.1A2B…").</summary>
    internal const string TestPrefix = "Rigsight Frames.";

    private readonly string _name;
    private readonly EventCallback _callback; // kept alive while the trace runs
    private ulong _session, _trace;
    private Thread? _thread;
    private volatile bool _stopped;
    private long _events, _lostSeen;
    private readonly Lock _gate = new();
    private readonly PresentTimes _times;
    private readonly int[] _pids;
    private readonly long _frequency = Stopwatch.Frequency;

    /// <summary>The game this trace is for (the process in front), and every process it watches (the game first).</summary>
    public int GamePid { get; }
    public IReadOnlyList<int> Pids => _pids;

    /// <summary>Whether the runtimes' events are filtered to the game's processes by Windows (false: every process's come, and are left out here).</summary>
    public bool PidFiltered { get; private set; }

    /// <summary>The kernel's present events are on (false: a game that presents only through the kernel, Vulkan or OpenGL, gives nothing).</summary>
    public bool Kernel { get; private set; }

    /// <summary>Events delivered since the trace started, from any process.</summary>
    public long Events => Interlocked.Read(ref _events);

    /// <summary>The game's presents counted since the start.</summary>
    public long Presents { get { lock (_gate) return _times.Presents; } }

    /// <summary>When the trace started (Unix seconds).</summary>
    public long StartedAt { get; } = TimeUtil.NowUnix();

    /// <summary>Windows ended the trace by itself (nobody stopped it): nothing more will come from this one.</summary>
    public volatile bool Ended;

    private PresentTrace(string name, int game, IReadOnlyList<int> pids)
    {
        _name = name;
        _callback = OnEvent;
        GamePid = game;
        _pids = [.. pids];
        _times = new PresentTimes(_frequency, Stopwatch.GetTimestamp());
    }

    /// <summary>The trace for <paramref name="game"/>'s processes (<paramref name="pids"/>, the game first), started; null without admin rights, or when Windows refuses.</summary>
    public static PresentTrace? Start(string name, int game, IReadOnlyList<int> pids)
    {
        var trace = new PresentTrace(name, game, pids);
        try
        {
            if (trace.Open()) return trace;
        }
        catch (Exception ex)
        {
            Log.Error("frames", ex);
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
                Log.Write("frames", rc == 5 ? "No admin rights: the game's frames can't be read by RigSight itself"
                    : $"Expected to start the frames trace \"{_name}\", Windows answered {rc}; running now: {string.Join(", ", NetworkTrace.Running())}");
                _session = 0;
                return false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(props);
        }

        // The kernel's first, so a present isn't half seen before the runtimes' come on (PresentMon's order).
        bool win11 = Environment.OSVersion.Version.Build >= 22000;
        ulong kernelKeywords = win11 ? KernelKeywordsWin11 : KernelKeywordsWin10;
        // Without it only games that present through DXGI or Direct3D 9 are read (said in the log by Enable).
        Kernel = Enable(DxgKrnl, "Microsoft-Windows-DxgKrnl", kernelKeywords, [KBlit, KFlip, KFlipMpo, KHistory, KHistoryDetailed, KPresent], pids: false) == 0;
        PidFiltered = true;
        foreach (var (guid, what, ids) in new[] { (Dxgi, "Microsoft-Windows-DXGI", new[] { DxgiStart, DxgiStop, DxgiMpoStart, DxgiMpoStop }), (D3D9, "Microsoft-Windows-D3D9", new[] { D3D9Start, D3D9Stop }) })
        {
            int rc = Enable(guid, what, RuntimeKeywords, ids, pids: true);
            if (rc != 0)
            {
                // Windows didn't take the process filter: every process's presents come, and are left out here.
                PidFiltered = false;
                rc = Enable(guid, what, RuntimeKeywords, ids, pids: false);
            }
            if (rc != 0) return false;
        }

        IntPtr logFile = Marshal.AllocHGlobal(LogFileSize);
        IntPtr loggerName = Marshal.StringToHGlobalUni(_name);
        try
        {
            new Span<byte>((void*)logFile, LogFileSize).Clear();
            byte* l = (byte*)logFile;
            *(IntPtr*)(l + 8) = loggerName;                                   // LoggerName
            // Raw time stamps: each event's TimeStamp stays the precise counter's own value (Stopwatch's clock).
            *(uint*)(l + 28) = ProcessTraceModeRealTime | ProcessTraceModeEventRecord | ProcessTraceModeRawTimestamp;
            *(IntPtr*)(l + 424) = Marshal.GetFunctionPointerForDelegate(_callback); // EventRecordCallback
            _trace = OpenTraceW(logFile);
            if (_trace == InvalidTrace)
            {
                _trace = 0;
                Log.Write("frames", $"Expected to open the frames trace for reading, found error {Marshal.GetLastPInvokeError()}");
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
            Log.Write("frames", $"The frames trace stopped by itself ({rc}) after {Events} events");
            Ended = true;
        }) { IsBackground = true, Name = "Frames trace" };
        _thread.Start();
        Log.Write("frames", $"Reading the game's frames (trace \"{_name}\", processes {string.Join(", ", _pids)}"
            + $"{(PidFiltered ? "" : ", runtime events from every process: Windows didn't take the process filter")}; DXGI events {DxgiStart}/{DxgiStop}/{DxgiMpoStart}/{DxgiMpoStop}, "
            + $"Direct3D 9 {D3D9Start}/{D3D9Stop}, keyword 0x{RuntimeKeywords:X}; "
            + (Kernel ? $"DxgKrnl {KBlit}/{KFlip}/{KFlipMpo}/{KHistory}/{KHistoryDetailed}/{KPresent}, keyword 0x{kernelKeywords:X})" : "DxgKrnl off: Vulkan and OpenGL games give nothing)"));
        return true;
    }

    /// <summary>One provider on, with only <paramref name="ids"/> and (when <paramref name="pids"/>) only the game's processes. Windows' answer (0: on).</summary>
    private int Enable(Guid provider, string what, ulong keywords, ushort[] ids, bool pids)
    {
        byte[] idFilter = EventIdFilter(ids), pidFilter = PidFilter(_pids);
        byte* filters = stackalloc byte[2 * FilterSize];
        byte* parameters = stackalloc byte[ParametersSize];
        fixed (byte* idData = idFilter)
        fixed (byte* pidData = pidFilter)
        {
            WriteFilter(new Span<byte>(filters, FilterSize), (nint)idData, idFilter.Length, FilterTypeEventId);
            if (pids) WriteFilter(new Span<byte>(filters + FilterSize, FilterSize), (nint)pidData, pidFilter.Length, FilterTypePid);
            WriteParameters(new Span<byte>(parameters, ParametersSize), (nint)filters, pids ? 2 : 1, SourceId);
            int rc = EnableTraceEx2(_session, &provider, 1 /* enable */, 4 /* information */, keywords, keywords, 0, (IntPtr)parameters);
            if (rc != 0)
                Log.Write("frames", $"Expected {what} on (events {string.Join(", ", ids)}{(pids ? $", processes {string.Join(", ", _pids)}" : "")}), Windows answered {rc}");
            return rc;
        }
    }

    private void InitProperties(IntPtr props)
    {
        new Span<byte>((void*)props, PropertiesSize).Clear();
        byte* b = (byte*)props;
        *(uint*)(b + 0) = PropertiesSize;   // Wnode.BufferSize
        *(uint*)(b + 40) = 1;               // Wnode.ClientContext: the precise clock (QueryPerformanceCounter)
        *(uint*)(b + 44) = 0x20000;         // Wnode.Flags: WNODE_FLAG_TRACED_GUID
        // A game at 300 fps makes about 600 runtime events a second, the kernel's for every process a few hundred more:
        // 64 KB buffers hold several hundred each, and up to 32 of them ride out the reader being late for seconds.
        *(uint*)(b + 48) = 64;              // BufferSize (KB)
        *(uint*)(b + 52) = 4;               // MinimumBuffers
        *(uint*)(b + 56) = 32;              // MaximumBuffers
        // Real time, into one buffer for every core (EVENT_TRACE_NO_PER_PROCESSOR_BUFFERING): with a buffer per core, a
        // game thread that moves between cores has its presents arrive out of order. Few events a second, so it costs nothing.
        *(uint*)(b + 64) = 0x100 | 0x10000000; // LogFileMode: EVENT_TRACE_REAL_TIME_MODE | EVENT_TRACE_NO_PER_PROCESSOR_BUFFERING
        *(uint*)(b + 68) = 1;               // FlushTimer (seconds)
        *(uint*)(b + 116) = 120;            // LoggerNameOffset
    }

    private void Control(IntPtr props, ulong session, string? name, uint code)
    {
        InitProperties(props);
        ControlTraceW(session, name, props, code);
    }

    /// <summary>Stops the frames traces test copies of Rigsight left running (every "Rigsight Frames.…" but <paramref name="own"/>).</summary>
    public static List<string> StopOthers(string own) => NetworkTrace.StopOthers(own, TestPrefix);

    /// <summary>What Windows says it dropped (events it had no buffer for, plus whole buffers the reader was too late for).</summary>
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

    /// <summary>
    /// The game's frames since the last ask (µs each, oldest first, into <paramref name="into"/>): the time between
    /// presents on its busiest swap chain. Not Ok for another process than the game this trace is for.
    /// </summary>
    public PresentRead Take(int pid, List<uint> into)
    {
        into.Clear();
        if (pid != GamePid || _session == 0) return default;
        long lost = Lost;
        bool dropped = lost > _lostSeen;
        _lostSeen = lost;
        bool missed;
        long last, now;
        lock (_gate)
        {
            now = Stopwatch.GetTimestamp();
            missed = _times.Take(into, now, out last);
        }
        long endMs = last == 0 ? 0 : TimeUtil.NowUnixMs() - (now - last) * 1000 / _frequency;
        return new PresentRead(true, missed, dropped, endMs);
    }

    /// <summary>The chain the frames came from last (for the log).</summary>
    public ChainKey? Picked { get { lock (_gate) return _times.Picked; } }

    // ── Payloads ──────────────────────────────────────────────────────────

    /// <summary>
    /// A DXGI (Present_Start 42, PresentMultiplaneOverlay_Start 55) or Direct3D 9 (Present_Start 1) start: its swap chain
    /// (a pointer: 4 bytes from a 32-bit game) and whether it only tests (DXGI_PRESENT_TEST, 1: nothing presented).
    /// </summary>
    /// <remarks>Layouts (from the providers' manifests): pIDXGISwapChain, Flags (UInt32), SyncInterval (Int32), …; pSwapchain, Flags (UInt32).</remarks>
    internal static bool DecodeStart(bool dxgi, ReadOnlySpan<byte> data, bool is32, out ulong chain, out bool test)
    {
        int ptr = is32 ? 4 : 8;
        chain = 0;
        test = false;
        if (data.Length < ptr + 4) return false;
        chain = is32 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : BinaryPrimitives.ReadUInt64LittleEndian(data);
        test = dxgi && (BinaryPrimitives.ReadUInt32LittleEndian(data[ptr..]) & 1) != 0;
        return true;
    }

    /// <summary>A present's stop (DXGI 43, 56; Direct3D 9 2): its Result (an HRESULT, below 0 when it failed).</summary>
    internal static bool DecodeStop(ReadOnlySpan<byte> data, out int result)
    {
        result = 0;
        if (data.Length < 4) return false;
        result = BinaryPrimitives.ReadInt32LittleEndian(data);
        return true;
    }

    /// <summary>
    /// A present history start (171, 215): its token and present model. Layout: hAdapter (pointer), Token (pointer),
    /// Model (UInt32), TokenSize, TokenData, … (the kernel's pointers: 8 bytes on 64-bit Windows).
    /// </summary>
    internal static bool DecodeHistory(ReadOnlySpan<byte> data, bool is32, out ulong token, out uint model)
    {
        int ptr = is32 ? 4 : 8;
        token = 0;
        model = 0;
        if (data.Length < 2 * ptr + 4) return false;
        token = is32 ? BinaryPrimitives.ReadUInt32LittleEndian(data[ptr..]) : BinaryPrimitives.ReadUInt64LittleEndian(data[ptr..]);
        model = BinaryPrimitives.ReadUInt32LittleEndian(data[(2 * ptr)..]);
        return true;
    }

    /// <summary>The kernel event's kind, by its ID (null: not one of ours).</summary>
    internal static KernelPresent? KernelKind(ushort id) => id switch
    {
        KFlip or KFlipMpo => KernelPresent.Flip,
        KBlit => KernelPresent.Blit,
        KHistory or KHistoryDetailed => KernelPresent.History,
        KPresent => KernelPresent.End,
        _ => null,
    };

    // ── Filters (ENABLE_TRACE_PARAMETERS, x64) ────────────────────────────

    /// <summary>EVENT_FILTER_EVENT_ID: FilterIn (1: only these), Reserved, Count (UInt16), then each ID (UInt16).</summary>
    internal static byte[] EventIdFilter(ReadOnlySpan<ushort> ids)
    {
        var b = new byte[4 + 2 * ids.Length];
        b[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)ids.Length);
        for (int i = 0; i < ids.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4 + 2 * i), ids[i]);
        return b;
    }

    /// <summary>EVENT_FILTER_TYPE_PID's data: each process ID (UInt32), at most 8 (MAX_EVENT_FILTER_PID_COUNT).</summary>
    internal static byte[] PidFilter(IReadOnlyList<int> pids)
    {
        int n = Math.Min(pids.Count, 8);
        var b = new byte[4 * n];
        for (int i = 0; i < n; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4 * i), (uint)pids[i]);
        return b;
    }

    /// <summary>EVENT_FILTER_DESCRIPTOR: Ptr (UInt64), Size (UInt32), Type (UInt32).</summary>
    internal static void WriteFilter(Span<byte> into, nint data, int size, uint type)
    {
        into[..FilterSize].Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(into, (ulong)data);
        BinaryPrimitives.WriteUInt32LittleEndian(into[8..], (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(into[12..], type);
    }

    /// <summary>
    /// ENABLE_TRACE_PARAMETERS version 2: Version (2), EnableProperty (EVENT_ENABLE_PROPERTY_IGNORE_KEYWORD_0, as
    /// PresentMon), ControlFlags, SourceId (GUID), then at +32 EnableFilterDesc (pointer) and at +40 FilterDescCount.
    /// </summary>
    internal static void WriteParameters(Span<byte> into, nint filters, int count, Guid source)
    {
        into[..ParametersSize].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(into, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(into[4..], EnablePropertyIgnoreKeyword0);
        source.TryWriteBytes(into[12..]);
        BinaryPrimitives.WriteUInt64LittleEndian(into[32..], (ulong)filters);
        BinaryPrimitives.WriteUInt32LittleEndian(into[40..], (uint)count);
    }

    // ── The trace's thread ────────────────────────────────────────────────

    private void OnEvent(IntPtr record)
    {
        Interlocked.Increment(ref _events);
        byte* r = (byte*)record;
        int pid = *(int*)(r + 12);                           // EventHeader.ProcessId
        if (Array.IndexOf(_pids, pid) < 0) return;
        int tid = *(int*)(r + 8);                            // EventHeader.ThreadId
        long qpc = *(long*)(r + 16);                         // EventHeader.TimeStamp: the precise counter (raw)
        bool is32 = (*(ushort*)(r + 4) & HeaderFlag32Bit) != 0; // EventHeader.Flags: written by a 32-bit process
        ushort id = *(ushort*)(r + 40);                      // EventHeader.EventDescriptor.Id
        Guid provider = *(Guid*)(r + 24);                    // EventHeader.ProviderId
        var data = new ReadOnlySpan<byte>(*(byte**)(r + 96), *(ushort*)(r + 86)); // UserData, UserDataLength

        if (provider == DxgKrnl)
        {
            if (KernelKind(id) is not { } kind) return;
            ulong token = 0;
            if (kind == KernelPresent.History)
            {
                if (!DecodeHistory(data, is32, out token, out uint model) || model is ModelRedirectedGdi or ModelRedirectedComposition) return;
            }
            lock (_gate) _times.Kernel(kind, pid, tid, token, qpc);
            return;
        }
        bool dxgi = provider == Dxgi;
        if (!dxgi && provider != D3D9) return;
        if (dxgi ? id is DxgiStart or DxgiMpoStart : id == D3D9Start)
        {
            if (!DecodeStart(dxgi, data, is32, out ulong chain, out bool test) || test) return;
            lock (_gate) _times.RuntimeStart(pid, tid, chain, qpc);
        }
        else if ((dxgi ? id is DxgiStop or DxgiMpoStop : id == D3D9Stop) && DecodeStop(data, out int result))
        {
            lock (_gate) _times.RuntimeStop(tid, result);
        }
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
    private const uint ProcessTraceModeRealTime = 0x100, ProcessTraceModeRawTimestamp = 0x1000, ProcessTraceModeEventRecord = 0x10000000;
    private const ushort HeaderFlag32Bit = 0x0020;
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
