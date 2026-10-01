using System.Runtime.InteropServices;
using Rigsight.Core;

namespace Rigsight.Agent.Network;

/// <summary>What one process sent and received since the last <see cref="NetworkTrace.Take"/>, in bytes, by where it went.</summary>
internal sealed class NetCounts
{
    /// <summary>To and from the internet through a network card (Ethernet, Wi-Fi).</summary>
    public long Down, Up;
    /// <summary>To and from the internet through a virtual adapter (a VPN's tunnel, usually).</summary>
    public long TunnelDown, TunnelUp;
    /// <summary>To and from other devices on the local network (a NAS, another PC, the router).</summary>
    public long LanDown, LanUp;

    public long InternetDown => Down + TunnelDown;
    public long InternetUp => Up + TunnelUp;
    public bool IsEmpty => Down == 0 && Up == 0 && TunnelDown == 0 && TunnelUp == 0 && LanDown == 0 && LanUp == 0;
}

/// <summary>
/// Every byte each process sends and receives over TCP and UDP, from Windows' own kernel network events (the source
/// Resource Monitor reads), in a real-time trace session of our own. Needs admin rights; costs about 0.05% CPU at
/// 20,000 events a second (a 100 Mbps download). Events are added up by process as they come, on the trace's thread;
/// the sampler takes the totals once a second.
/// </summary>
internal sealed unsafe class NetworkTrace : IDisposable
{
    private static readonly Guid KernelNetwork = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    private const ulong KeywordIpv4 = 0x10, KeywordIpv6 = 0x20;
    private const int PropertiesSize = 120 + 2 * 256; // EVENT_TRACE_PROPERTIES, then the session's name
    private const int LogFileSize = 448; // EVENT_TRACE_LOGFILEW (x64)

    private readonly string _name;
    private readonly Func<AddressBook> _addresses;
    private readonly EventCallback _callback; // kept alive while the trace runs
    private ulong _session, _trace;
    private Thread? _thread;
    private readonly Lock _gate = new();
    private Dictionary<int, NetCounts> _counts = [];
    private Dictionary<int, NetCounts> _spare = [];
    private volatile bool _stopped;

    private NetworkTrace(string name, Func<AddressBook> addresses)
    {
        _name = name;
        _addresses = addresses;
        _callback = OnEvent;
    }

    /// <summary>The trace, started; null without admin rights, or when Windows refuses another session.</summary>
    public static NetworkTrace? Start(string name, Func<AddressBook> addresses)
    {
        var trace = new NetworkTrace(name, addresses);
        try
        {
            if (trace.Open()) return trace;
        }
        catch (Exception ex)
        {
            Log.Error("network", ex);
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
                Log.Write("network", rc == 5 ? "No admin rights: app network use can't be read" : $"Couldn't start the network trace ({rc})");
                _session = 0;
                return false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(props);
        }

        var provider = KernelNetwork;
        int enabled = EnableTraceEx2(_session, &provider, 1 /* enable */, 4 /* information */, KeywordIpv4 | KeywordIpv6, 0, 0, IntPtr.Zero);
        if (enabled != 0)
        {
            Log.Write("network", $"Couldn't read Windows' network events ({enabled})");
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
                Log.Write("network", $"Couldn't open the network trace ({Marshal.GetLastPInvokeError()})");
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
            if (!_stopped) Log.Write("network", $"The network trace stopped ({rc})");
        }) { IsBackground = true, Name = "Network trace" };
        _thread.Start();
        Log.Write("network", "Reading app network use");
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

    /// <summary>Each process's bytes since the last call (taken on the sampler thread).</summary>
    public Dictionary<int, NetCounts> Take()
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

    // ── The trace's thread ────────────────────────────────────────────────

    private void OnEvent(IntPtr record)
    {
        byte* r = (byte*)record;
        ushort id = *(ushort*)(r + 40);              // EventHeader.EventDescriptor.Id
        ushort length = *(ushort*)(r + 86);          // UserDataLength
        byte* data = *(byte**)(r + 96);              // UserData
        // TCP sent / received (IPv4 10, 11; IPv6 26, 27) and UDP (42, 43; 58, 59). The others (connects, retransmits,
        // TCP "copy" events that repeat a receive) aren't counted.
        bool send, v6;
        switch (id)
        {
            case 10 or 42: send = true; v6 = false; break;
            case 11 or 43: send = false; v6 = false; break;
            case 26 or 58: send = true; v6 = true; break;
            case 27 or 59: send = false; v6 = true; break;
            default: return;
        }
        if (length < (v6 ? 40 : 16)) return;
        int pid = *(int*)data;
        long size = *(uint*)(data + 4);
        var where = v6 ? _addresses().Classify6(data + 8, data + 24) : _addresses().Classify4(*(uint*)(data + 8), *(uint*)(data + 12));
        if (where == Route.Self || size <= 0) return;

        lock (_gate)
        {
            if (!_counts.TryGetValue(pid, out var c)) _counts[pid] = c = new NetCounts();
            switch (where)
            {
                case Route.Lan when send: c.LanUp += size; break;
                case Route.Lan: c.LanDown += size; break;
                case Route.Tunnel when send: c.TunnelUp += size; break;
                case Route.Tunnel: c.TunnelDown += size; break;
                case Route.Internet when send: c.Up += size; break;
                default: c.Down += size; break;
            }
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
