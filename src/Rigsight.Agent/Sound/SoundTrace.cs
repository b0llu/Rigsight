using System.Runtime.InteropServices;
using System.Text;
using Rigsight.Agent.Network;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Sound;

namespace Rigsight.Agent.Sound;

/// <summary>One glitch as the trace's thread saw it, before it is tagged (Unix ms, its event ID, heard or late, the frames it names).</summary>
internal readonly record struct RawGlitch(long AtMs, int Kind, bool Heard, long? Frames);

/// <summary>
/// Sound dropouts (2026-11-22): every time the sound Windows plays runs out of data on its way out, its audio engine logs
/// a glitch event (Microsoft-Windows-Audio, GlitchDetection and Informational channels). A real-time trace session of
/// our own with that provider enabled, the way <see cref="Storage.DiskTrace"/> is built. Needs admin rights.
/// <para>
/// The provider's Informational and Performance channels also log an event per engine pass (about 100 a second), so the
/// provider is enabled once with an event-ID filter that lets through only <see cref="SoundRules.FilterIds"/>. If Windows
/// refuses the filter, only the GlitchDetection channel is enabled (keyword <see cref="KeywordGlitch"/>): the busy
/// channels are never taken unfiltered.
/// </para>
/// Heard glitches (48, 142) start an ask of which app is playing (<see cref="SoundSessions"/>, at most every
/// <see cref="SoundRules.AttributeEveryMs"/>, on a worker of its own); the agent takes what came once a minute.
/// </summary>
internal sealed unsafe class SoundTrace : IDisposable
{
    /// <summary>Microsoft-Windows-Audio.</summary>
    internal static readonly Guid Audio = new("AE4BD3BE-F36F-45B6-8D21-BDD6FB832853");

    /// <summary>The provider's channel keywords (from its manifest: wevtutil gp Microsoft-Windows-Audio /ge /gm:true /f:xml).</summary>
    internal const ulong KeywordGlitch = 0x0400000000000000, KeywordInformational = 0x0200000000000000, KeywordPerformance = 0x1000000000000000;

    /// <summary>All three channels: what the event-ID filter narrows.</summary>
    internal const ulong KeywordFiltered = KeywordGlitch | KeywordInformational | KeywordPerformance;

    /// <summary>Verbose: the glitch events are logged at level 5 (118, 19 and 127 to 134 at 4).</summary>
    internal const byte Level = 5;

    /// <summary>EVENT_FILTER_TYPE_EVENT_ID (evntprov.h).</summary>
    internal const uint FilterTypeEventId = 0x80000200;

    /// <summary>ENABLE_TRACE_PARAMETERS_VERSION_2.</summary>
    internal const uint ParametersVersion2 = 2;

    /// <summary>ENABLE_TRACE_PARAMETERS on x64 (see <see cref="Parameters"/>).</summary>
    internal const int ParametersSize = 48;

    /// <summary>EVENT_FILTER_DESCRIPTOR (see <see cref="Descriptor"/>).</summary>
    internal const int DescriptorSize = 16;

    private const int PropertiesSize = 120 + 2 * 256; // EVENT_TRACE_PROPERTIES, then the session's name
    private const int LogFileSize = 448; // EVENT_TRACE_LOGFILEW (x64)
    private const long FileTimeUnixEpoch = 116_444_736_000_000_000;

    /// <summary>The start of every test copy's trace name (a test copy adds its own suffix: "Rigsight Sound.1A2B…").</summary>
    internal const string TestPrefix = "Rigsight Sound.";

    private readonly string _name;
    private readonly EventCallback _callback; // kept alive while the trace runs
    private ulong _session, _trace;
    private Thread? _thread, _worker;
    private volatile bool _stopped;
    private readonly AutoResetEvent _wake = new(false);

    // Counts since the last Summary (the trace's thread adds, the sampler reads and resets).
    private readonly long[] _byId = new long[256];
    private long _events, _heard, _settled, _asked, _named;
    private readonly bool[] _logged = new bool[256];

    // Only the trace's thread touches these after the start.
    private readonly SoundSettle _settle;
    private long _askedAt = long.MinValue / 2;

    private readonly Lock _gate = new();
    private List<RawGlitch> _pending = [];
    private readonly Queue<SoundGlitch> _ring = new();
    private readonly List<SoundAttribution> _attributions = [];

    /// <summary>When the trace started (Unix ms).</summary>
    public long StartedMs { get; }

    /// <summary>The event-ID filter was taken (false: only the GlitchDetection channel is on, so 142, 118 and the stream starts never come).</summary>
    public bool Filtered { get; private set; }

    /// <summary>Events delivered since the trace started.</summary>
    public long Events => Interlocked.Read(ref _events);

    /// <summary>Windows ended the trace by itself (nobody stopped it): nothing more will come from this one.</summary>
    public volatile bool Ended;

    private SoundTrace(string name)
    {
        _name = name;
        _callback = OnEvent;
        StartedMs = NowMs();
        _settle = new SoundSettle(StartedMs);
    }

    /// <summary>The trace, started; null without admin rights, or when Windows refuses another session.</summary>
    public static SoundTrace? Start(string name)
    {
        var trace = new SoundTrace(name);
        try
        {
            if (trace.Open()) return trace;
        }
        catch (Exception ex)
        {
            Log.Error("sound", ex);
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
                Log.Write("sound", rc switch
                {
                    5 => "No admin rights: when the sound cuts out can't be read",
                    ErrorNoSystemResources => $"Expected to start the sound trace \"{_name}\", but every slot Windows has for traces is in use (answer {rc}); running now: {string.Join(", ", NetworkTrace.Running())}",
                    _ => $"Expected to start the sound trace \"{_name}\", Windows answered {rc}",
                });
                _session = 0;
                return false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(props);
        }

        if (!Enable()) return false;

        IntPtr logFile = Marshal.AllocHGlobal(LogFileSize);
        IntPtr loggerName = Marshal.StringToHGlobalUni(_name);
        try
        {
            new Span<byte>((void*)logFile, LogFileSize).Clear();
            byte* l = (byte*)logFile;
            *(IntPtr*)(l + 8) = loggerName;                                   // LoggerName
            // Not raw time stamps: the event's TimeStamp comes as a file time (UTC), turned into Unix ms as it is.
            *(uint*)(l + 28) = ProcessTraceModeRealTime | ProcessTraceModeEventRecord;
            *(IntPtr*)(l + 424) = Marshal.GetFunctionPointerForDelegate(_callback); // EventRecordCallback
            _trace = OpenTraceW(logFile);
            if (_trace == InvalidTrace)
            {
                _trace = 0;
                Log.Write("sound", $"Expected to open the sound trace for reading, found error {Marshal.GetLastPInvokeError()}");
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
            Log.Write("sound", $"The sound trace stopped by itself ({rc}) after {Events} events");
            Ended = true;
        }) { IsBackground = true, Name = "Sound trace" };
        _thread.Start();

        // Which app is playing is asked of Windows' audio sessions (COM) on a thread of its own, in the multithreaded apartment.
        _worker = new Thread(AskLoop) { IsBackground = true, Name = "Sound sessions" };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
        return true;
    }

    /// <summary>
    /// The provider on, filtered to <see cref="SoundRules.FilterIds"/> across its three channels; when Windows refuses the
    /// filter, the GlitchDetection channel alone. Says which it got.
    /// </summary>
    private bool Enable()
    {
        var provider = Audio;
        byte[] filter = EventIdFilter(SoundRules.FilterIds, filterIn: true);
        IntPtr filterMem = Marshal.AllocHGlobal(filter.Length);
        IntPtr descMem = Marshal.AllocHGlobal(DescriptorSize);
        IntPtr parMem = Marshal.AllocHGlobal(ParametersSize);
        try
        {
            Marshal.Copy(filter, 0, filterMem, filter.Length);
            Marshal.Copy(Descriptor((ulong)filterMem, (uint)filter.Length, FilterTypeEventId), 0, descMem, DescriptorSize);
            Marshal.Copy(Parameters((ulong)descMem, 1), 0, parMem, ParametersSize);
            int rc = EnableTraceEx2(_session, &provider, 1 /* enable */, Level, KeywordFiltered, 0, 0, parMem);
            if (rc == 0)
            {
                Filtered = true;
                Log.Write("sound", $"Reading when the sound cuts out (trace \"{_name}\", Microsoft-Windows-Audio, keywords 0x{KeywordFiltered:X16}, level {Level}, "
                    + $"event-ID filter of {SoundRules.FilterIds.Count} IDs: {Ranges(SoundRules.FilterIds)})");
                return true;
            }
            Log.Write("sound", $"Expected Windows to take the event-ID filter ({SoundRules.FilterIds.Count} IDs, keywords 0x{KeywordFiltered:X16}), it answered {rc}: "
                + $"enabling the GlitchDetection channel alone (keyword 0x{KeywordGlitch:X16}), so the audio engine's own glitch event (142) won't come");
            rc = EnableTraceEx2(_session, &provider, 1 /* enable */, Level, KeywordGlitch, 0, 0, IntPtr.Zero);
            if (rc != 0)
            {
                Log.Write("sound", $"Expected Windows' audio events (Microsoft-Windows-Audio, keyword 0x{KeywordGlitch:X16}, level {Level}), Windows answered {rc}");
                return false;
            }
            Log.Write("sound", $"Reading when the sound cuts out (trace \"{_name}\", Microsoft-Windows-Audio, GlitchDetection only, keyword 0x{KeywordGlitch:X16}, level {Level})");
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(filterMem);
            Marshal.FreeHGlobal(descMem);
            Marshal.FreeHGlobal(parMem);
        }
    }

    // ── The structures EnableTraceEx2 takes (x64) ─────────────────────────

    /// <summary>
    /// EVENT_FILTER_EVENT_ID (evntprov.h), as Windows reads it:
    /// <code>
    /// +0 BOOLEAN FilterIn   (1 byte: TRUE lets only these IDs through)
    /// +1 UCHAR   Reserved
    /// +2 USHORT  Count
    /// +4 USHORT  Events[Count]
    /// </code>
    /// Its size is 4 + 2·Count (FIELD_OFFSET(EVENT_FILTER_EVENT_ID, Events[Count])); at most 64 IDs (MAX_EVENT_FILTER_EVENT_ID_COUNT).
    /// </summary>
    internal static byte[] EventIdFilter(IReadOnlyList<ushort> ids, bool filterIn)
    {
        if (ids.Count is 0 or > 64) throw new ArgumentOutOfRangeException(nameof(ids), ids.Count, "An event-ID filter holds 1 to 64 IDs");
        var b = new byte[4 + 2 * ids.Count];
        b[0] = filterIn ? (byte)1 : (byte)0;
        MemoryMarshal.Write(b.AsSpan(2), (ushort)ids.Count);
        for (int i = 0; i < ids.Count; i++) MemoryMarshal.Write(b.AsSpan(4 + 2 * i), ids[i]);
        return b;
    }

    /// <summary>
    /// EVENT_FILTER_DESCRIPTOR (evntprov.h):
    /// <code>
    /// +0  ULONGLONG Ptr   (the filter's data)
    /// +8  ULONG     Size  (its bytes)
    /// +12 ULONG     Type  (EVENT_FILTER_TYPE_EVENT_ID here)
    /// </code>
    /// </summary>
    internal static byte[] Descriptor(ulong ptr, uint size, uint type)
    {
        var b = new byte[DescriptorSize];
        MemoryMarshal.Write(b, ptr);
        MemoryMarshal.Write(b.AsSpan(8), size);
        MemoryMarshal.Write(b.AsSpan(12), type);
        return b;
    }

    /// <summary>
    /// ENABLE_TRACE_PARAMETERS (evntrace.h), version 2, x64:
    /// <code>
    /// +0  ULONG  Version          (ENABLE_TRACE_PARAMETERS_VERSION_2)
    /// +4  ULONG  EnableProperty   (0)
    /// +8  ULONG  ControlFlags     (0)
    /// +12 GUID   SourceId         (zero: the session's own)
    /// +28 (4 bytes padding: the pointer after is 8-aligned)
    /// +32 PEVENT_FILTER_DESCRIPTOR EnableFilterDesc
    /// +40 ULONG  FilterDescCount
    /// +44 (4 bytes padding to 48)
    /// </code>
    /// </summary>
    internal static byte[] Parameters(ulong descriptors, uint count)
    {
        var b = new byte[ParametersSize];
        MemoryMarshal.Write(b, ParametersVersion2);
        MemoryMarshal.Write(b.AsSpan(32), descriptors);
        MemoryMarshal.Write(b.AsSpan(40), count);
        return b;
    }

    // ── Payloads ──────────────────────────────────────────────────────────

    /// <summary>
    /// What an event's payload says that is kept or logged (layouts from the provider's manifest via TDH, 2026-11-22;
    /// every one starts with a pointer of <paramref name="pointerSize"/> bytes, 8 from a 64-bit process):
    /// <list type="bullet">
    /// <item>48 STREN Device Starved: ptr, DevicePosition u64, StreamPosition u64, AvailableFrames u32. Value = AvailableFrames.</item>
    /// <item>142 AE Glitch: ptr, ucType u32, WritePosition i64, PlayPosition u64, StreamPosition u64, GlitchFrameCount u64. Value = GlitchFrameCount.</item>
    /// <item>33 Server Input Starvation: ptr, WriteOffset, ReadOffset, BufferSize, BytesAvailable (u32 each). Value = BytesAvailable.</item>
    /// <item>118 Pump missed deadline (version 2): ptr, TaskGroup u32, Hns i64, success BOOL. Value = Hns (100 ns).</item>
    /// <item>37 IOCTL time limit: ptr, IoctlTimeHNS u64. Value = it (100 ns).</item>
    /// <item>41 End Glitch: ptr, GlitchStreamPosition u64, GlitchDuration u32. Value = GlitchDuration.</item>
    /// <item>127 CreateStream starting and 133 CreateDeviceEndpointInstance starting: Endpoint Id (UTF-16, ends with a 0), then
    /// numbers. Text = the endpoint's ID.</item>
    /// <item>19 glitch threshold count exceeded: dwGlitchCount u32, hnsTimeWindow u64 (no pointer). Value = dwGlitchCount.</item>
    /// </list>
    /// False for any other event or a payload too short.
    /// </summary>
    internal static bool Decode(int id, ReadOnlySpan<byte> data, int pointerSize, out long value, out string? text)
    {
        value = 0;
        text = null;
        int p = pointerSize;
        switch (id)
        {
            case SoundRules.DeviceStarved when data.Length >= p + 20:
                value = MemoryMarshal.Read<uint>(data[(p + 16)..]);
                return true;
            case SoundRules.AeGlitch when data.Length >= p + 36:
                value = (long)MemoryMarshal.Read<ulong>(data[(p + 28)..]);
                return true;
            case SoundRules.ServerStarved when data.Length >= p + 16:
                value = MemoryMarshal.Read<uint>(data[(p + 12)..]);
                return true;
            case SoundRules.PumpMissed when data.Length >= p + 12: // success (BOOL, +12) isn't read
                value = MemoryMarshal.Read<long>(data[(p + 4)..]);
                return true;
            case SoundRules.IoctlTooLong when data.Length >= p + 8:
                value = (long)MemoryMarshal.Read<ulong>(data[p..]);
                return true;
            case SoundRules.EndGlitch when data.Length >= p + 12:
                value = MemoryMarshal.Read<uint>(data[(p + 8)..]);
                return true;
            case SoundRules.CreateStream or SoundRules.EndpointInstance when data.Length >= 2:
                var chars = MemoryMarshal.Cast<byte, char>(data[..(data.Length & ~1)]);
                int end = chars.IndexOf('\0');
                text = new string(end < 0 ? chars : chars[..end]);
                return true;
            case SoundRules.ThresholdExceeded when data.Length >= 4:
                value = MemoryMarshal.Read<uint>(data);
                return true;
            default:
                return false;
        }
    }

    // ── The trace's thread ────────────────────────────────────────────────

    private void OnEvent(IntPtr record)
    {
        Interlocked.Increment(ref _events);
        byte* r = (byte*)record;
        if (*(Guid*)(r + 24) != Audio) return;                // EventHeader.ProviderId
        int id = *(ushort*)(r + 40);                          // EventHeader.EventDescriptor.Id
        if (id < _byId.Length) Interlocked.Increment(ref _byId[id]);
        long at = (*(long*)(r + 16) - FileTimeUnixEpoch) / 10_000; // EventHeader.TimeStamp: a file time (UTC)
        ushort flags = *(ushort*)(r + 4);                     // EventHeader.Flags
        int pointerSize = (flags & EventHeaderFlag32BitHeader) != 0 ? 4 : 8;
        var cls = SoundRules.Classify(id);

        long value = 0;
        string? text = null;
        bool decoded = false;
        if (cls != SoundClass.Other || id < _logged.Length && !_logged[id])
        {
            ushort length = *(ushort*)(r + 86);               // UserDataLength
            byte* data = *(byte**)(r + 96);                   // UserData
            if (data is not null) decoded = Decode(id, new ReadOnlySpan<byte>(data, length), pointerSize, out value, out text);
        }
        if (id < _logged.Length && !_logged[id])
        {
            // The first of each event ID, with what it said: the logs are all there is to check this on another PC.
            _logged[id] = true;
            int pid = *(int*)(r + 12);                        // EventHeader.ProcessId: the process that logged it
            Log.Write("sound", $"First event {id} ({cls}) at {TimeUtil.FromUnix(at / 1000):HH:mm:ss}, from process {pid}"
                + (decoded ? text is null ? $", value {value}" : $", endpoint {text}" : ", payload not read"));
        }

        switch (cls)
        {
            case SoundClass.StreamStart:
                _settle.StreamStarted(at);
                return;
            case SoundClass.Heard:
                if (_settle.Settling(at))
                {
                    Interlocked.Increment(ref _settled);
                    return;
                }
                Interlocked.Increment(ref _heard);
                long? frames = decoded ? value : null;
                lock (_gate)
                {
                    _pending.Add(new RawGlitch(at, id, true, frames));
                    _ring.Enqueue(new SoundGlitch(at, id, true, Frames: frames));
                    while (_ring.Count > SoundRules.RingMax || _ring.Count > 0 && _ring.Peek().AtMs < at - Core.Drivers.DriverRules.LookBackSec * 1000L)
                        _ring.Dequeue();
                }
                if (at - _askedAt >= SoundRules.AttributeEveryMs)
                {
                    _askedAt = at;
                    _wake.Set();
                }
                return;
            case SoundClass.Late:
                lock (_gate) _pending.Add(new RawGlitch(at, id, false, null));
                return;
        }
    }

    // ── Which app is playing (the worker) ─────────────────────────────────

    private void AskLoop()
    {
        while (true)
        {
            _wake.WaitOne();
            if (_stopped) return;
            try
            {
                long at = NowMs();
                var playing = SoundSessions.Playing();
                var pick = SoundSessions.Pick(playing, SoundSessions.ForegroundPid());
                Interlocked.Increment(ref _asked);
                if (pick.Exe is not null) Interlocked.Increment(ref _named);
                lock (_gate) _attributions.Add(new SoundAttribution(at, pick.Pid, pick.Exe, pick.Device, playing.Count));
                if (Interlocked.Read(ref _asked) == 1)
                    Log.Write("sound", $"First ask of which app was playing: {playing.Count} playing ({string.Join(", ", playing.Select(s => $"{s.Exe} {s.Pid} on \"{s.Device}\""))}), "
                        + (pick.Exe is null ? "none picked" : $"picked {pick.Exe}"));
            }
            catch (Exception ex) { Log.Error("sound", ex); }
        }
    }

    // ── The sampler's side ────────────────────────────────────────────────

    /// <summary>
    /// The glitches that came at or before <paramref name="untilMs"/> (Unix ms; later ones wait for the next take, so the
    /// driver bursts around them have arrived), with the answers to "which app is playing" that may be theirs.
    /// </summary>
    public (List<RawGlitch> Glitches, List<SoundAttribution> Attributions) Take(long untilMs)
    {
        lock (_gate)
        {
            var taken = _pending.Where(g => g.AtMs <= untilMs).ToList();
            if (taken.Count > 0) _pending = _pending.Where(g => g.AtMs > untilMs).ToList();
            _attributions.RemoveAll(a => a.AtMs < untilMs - 60_000 - SoundRules.AttributeWithinMs);
            return (taken, [.. _attributions]);
        }
    }

    /// <summary>The heard glitches (not settling) at or after <paramref name="sinceMs"/> (Unix ms), kept in memory for <see cref="Core.Drivers.DriverRules.LookBackSec"/> seconds.</summary>
    public IReadOnlyList<SoundGlitch> Recent(long sinceMs)
    {
        lock (_gate) return _ring.Where(g => g.AtMs >= sinceMs).ToList();
    }

    /// <summary>What came since the last call, for the agent's hourly line (English, never shown); the counts start again.</summary>
    public string Summary()
    {
        var ids = new List<string>();
        for (int i = 0; i < _byId.Length; i++)
            if (Interlocked.Exchange(ref _byId[i], 0) is var n and > 0) ids.Add($"{i}×{n}");
        long heard = Interlocked.Exchange(ref _heard, 0), settled = Interlocked.Exchange(ref _settled, 0);
        long asked = Interlocked.Exchange(ref _asked, 0), named = Interlocked.Exchange(ref _named, 0);
        return $"events by ID {(ids.Count == 0 ? "none" : string.Join(", ", ids))}; {heard} heard, {settled} left out as a stream starting; "
            + $"{asked} asks of which app was playing, {named} named one; {Lost} lost by Windows; {(Filtered ? "filtered" : "GlitchDetection only")}";
    }

    /// <summary>What Windows says it dropped (asked of the session each time): events it had no buffer for, plus whole buffers the reader was too late for.</summary>
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

    /// <summary>Stops the sound traces test copies of Rigsight left running (every "Rigsight Sound.…" but <paramref name="own"/>).</summary>
    public static List<string> StopOthers(string own) => NetworkTrace.StopOthers(own, TestPrefix);

    private void InitProperties(IntPtr props)
    {
        new Span<byte>((void*)props, PropertiesSize).Clear();
        byte* b = (byte*)props;
        *(uint*)(b + 0) = PropertiesSize;   // Wnode.BufferSize
        *(uint*)(b + 40) = 1;               // Wnode.ClientContext: the precise clock, given back as a file time (no raw time stamps)
        *(uint*)(b + 44) = 0x20000;         // Wnode.Flags: WNODE_FLAG_TRACED_GUID
        // A handful of events a minute on a healthy PC, a few hundred a second in a storm: small buffers are plenty.
        *(uint*)(b + 48) = 16;              // BufferSize (KB)
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

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>"19, 26-49, 118, …" for the log.</summary>
    internal static string Ranges(IReadOnlyList<ushort> ids)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < ids.Count; i++)
        {
            int j = i;
            while (j + 1 < ids.Count && ids[j + 1] == ids[j] + 1) j++;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(j > i ? $"{ids[i]}-{ids[j]}" : $"{ids[i]}");
            i = j;
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        _stopped = true;
        _wake.Set();
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
        _worker?.Join(2000);
    }

    // ── Windows ───────────────────────────────────────────────────────────

    private const uint EventTraceControlQuery = 0, EventTraceControlStop = 1;
    private const uint ProcessTraceModeRealTime = 0x100, ProcessTraceModeEventRecord = 0x10000000;
    private const ushort EventHeaderFlag32BitHeader = 0x0020;
    private const int ErrorNoSystemResources = 1450;
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
