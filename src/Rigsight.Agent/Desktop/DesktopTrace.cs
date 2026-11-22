using System.Diagnostics;
using System.Runtime.InteropServices;
using Rigsight.Agent.Network;
using Rigsight.Core;
using Rigsight.Core.Data;

namespace Rigsight.Agent.Desktop;

/// <summary>
/// SCHEDULE_GLITCH (Dwm-Core event 24): a frame the compositor had ready that still missed the screen's refresh. Fields
/// as the manifest names them (2026-11-22, Windows 11 26200). The "t" times are read as ticks of the precise counter
/// (QueryPerformanceCounter): the event carries the counter's own <see cref="QpcFrequency"/> beside them and its refresh
/// period as <see cref="QpcPeriod"/>, so a time divided by the frequency is seconds. Not stated by Microsoft; the first
/// events are logged with <see cref="GlitchMs"/> beside <see cref="PeriodMs"/> so the log shows whether it holds (a
/// glitch should be about a refresh or a few long). On Windows 10 and later the counter runs at 10 MHz on most PCs, where
/// a tick is also 100 ns.
/// </summary>
internal readonly record struct DwmGlitch(ulong Frame, ulong VSyncLatency, ulong Elapsed, bool Induced, ulong QpcPresented, ulong QpcConfirmed,
    ulong PresentLatency, ulong QpcFrame, ulong SyncRefreshFrame, ulong GlitchTicks, ulong QpcFrequency, ulong QpcPeriod, uint Monitors,
    uint Predicted, uint Confirmed)
{
    /// <summary>tGlitch in milliseconds, by the event's own counter frequency (else <paramref name="fallbackFrequency"/>).</summary>
    public double? GlitchMs(long fallbackFrequency) => Ms(GlitchTicks, fallbackFrequency);

    /// <summary>The refresh period (qpcPeriod) in milliseconds, the same way.</summary>
    public double? PeriodMs(long fallbackFrequency) => Ms(QpcPeriod, fallbackFrequency);

    private double? Ms(ulong ticks, long fallback)
    {
        double f = QpcFrequency > 0 ? QpcFrequency : fallback;
        return f > 0 ? ticks * 1000.0 / f : null;
    }
}

/// <summary>TELEMETRY_ANIMATION_PERFORMANCE_ANALYSIS (event 237, version 1): how smooth one animation was, Windows' own report.</summary>
internal readonly record struct DwmSmooth(ulong FrameStart, ulong FrameStop, uint PercentMissed, uint Metrics1, uint Metrics2, string Scenario, string AppId, uint ScenarioIndex)
{
    /// <summary>Frames the animation ran over (cFrameStop - cFrameStart), 0 when they come the wrong way round.</summary>
    public long Frames => FrameStop >= FrameStart ? (long)Math.Min(FrameStop - FrameStart, int.MaxValue) : 0;
}

/// <summary>TELEMETRY_AGGREGATED_VSYNC_ANALYSIS (event 240, version 2): a scenario's refreshes added up.</summary>
internal readonly record struct DwmVsyncSummary(string Scenario, uint TotalHits, uint Metrics1, uint Metrics2, uint Metrics3, uint Fps);

/// <summary>
/// Desktop hitches (a probe, 2026-11-22; nothing is shown from it yet): Windows' compositor (DWM) says when a frame it had
/// ready missed the screen's refresh, in its own events (Microsoft-Windows-Dwm-Core). A real-time trace session of our own
/// with that provider switched on for its frame-glitch and low-overhead smoothness keywords only (never the per-frame
/// DetailedFrameInformation one), at the Informational level the five events are written at, and an event-ID filter so
/// only those five reach the session. Needs admin rights. Each event is added into its calendar minute by
/// <see cref="DesktopTally"/> on the trace's thread; the sampler takes the minutes that ended. An idle desktop draws no
/// frames and sends nothing: silence is normal here.
/// </summary>
internal sealed unsafe class DesktopTrace : IDisposable
{
    /// <summary>The provider's name; its GUID is looked up by name (older documents give another GUID).</summary>
    internal const string ProviderName = "Microsoft-Windows-Dwm-Core";

    /// <summary>Dwm-Core's GUID as Windows 11 26200 registers it, used when the look-up by name finds nothing.</summary>
    internal static readonly Guid KnownProvider = new("9E9BBA3C-2E38-40CB-99F4-9E8281425164");

    internal const ushort EventGlitch = 24, EventRecovery = 25, EventGlitchDetected = 172, EventSmooth = 237, EventVsyncSummary = 240;

    /// <summary>
    /// FrameDrawInconsistencyInformation (0x4): events 24 and 172 (172 is under DwmFrameRate 0x8 too; one keyword is
    /// enough). LowOverheadSmoothnessTracking (0x20000): 237 and 240.
    /// </summary>
    internal const ulong KeywordGlitch = 0x4, KeywordSmoothness = 0x20000;

    /// <summary>
    /// Scheduling (0x80): the only keyword event 25 (a recovery attempt) is under, with about 30 others written every frame
    /// (process frame, render, present, vblank loop). Left off so the probe doesn't add work to every frame of the thing it
    /// measures: with it off no recovery attempts come and their count stays 0. True turns it on (only with the event
    /// filter in place, which keeps the per-frame ones out of the session).
    /// </summary>
    internal const bool ListenForRecoveries = false;

    /// <summary>TRACE_LEVEL_INFORMATION: all five events are written at level 4 (the manifest's win:Informational).</summary>
    internal const byte Level = 4;

    /// <summary>The events let through (the filter's list).</summary>
    internal static readonly ushort[] EventIds = [EventGlitch, EventRecovery, EventGlitchDetected, EventSmooth, EventVsyncSummary];

    /// <summary>The fewest bytes each event's payload needs (24: through cMonitors; 172: Induced; 25: cRefreshFrame).</summary>
    internal const int GlitchMinPayload = 96, DetectedMinPayload = 4, RecoveryMinPayload = 8, SmoothMinPayload = 28, VsyncSummaryMinPayload = 2;

    /// <summary>How many events of each kind are logged in full when they first come.</summary>
    private const int LogFirst = 3;

    private const int PropertiesSize = 120 + 2 * 256; // EVENT_TRACE_PROPERTIES, then the session's name
    private const int LogFileSize = 448; // EVENT_TRACE_LOGFILEW (x64)

    /// <summary>The start of every test copy's trace name (a test copy adds its own suffix: "Rigsight Desktop.1A2B…").</summary>
    internal const string TestPrefix = "Rigsight Desktop.";

    private readonly string _name;
    private readonly EventCallback _callback; // kept alive while the trace runs
    private Guid _provider;
    private ulong _session, _trace;
    private Thread? _thread;
    private volatile bool _stopped;
    private long _events, _others, _short;
    private readonly int[] _logged = new int[EventIds.Length];

    /// <summary>Events delivered since the trace started, of any kind.</summary>
    public long Events => Interlocked.Read(ref _events);

    /// <summary>Events of a kind not asked for (the filter not in force), left out.</summary>
    public long Others => Interlocked.Read(ref _others);

    /// <summary>Events of a kind asked for whose payload was too short to read, left out.</summary>
    public long Short => Interlocked.Read(ref _short);

    /// <summary>Whether the event-ID filter was accepted (false: every event of the keywords comes, the others left out here).</summary>
    public bool Filtered { get; private set; }

    /// <summary>The keywords the provider was switched on with.</summary>
    public ulong Keywords { get; private set; }

    /// <summary>Windows ended the trace by itself (nobody stopped it): nothing more will come from this one.</summary>
    public volatile bool Ended;

    /// <summary>Each minute's counts, filled on the trace's thread.</summary>
    public DesktopTally Tally { get; } = new();

    private DesktopTrace(string name)
    {
        _name = name;
        _callback = OnEvent;
    }

    /// <summary>The trace, started; null without admin rights, or when Windows refuses another session.</summary>
    public static DesktopTrace? Start(string name)
    {
        var trace = new DesktopTrace(name);
        try
        {
            if (trace.Open()) return trace;
        }
        catch (Exception ex)
        {
            Log.Error("desktop", ex);
        }
        trace.Dispose();
        return null;
    }

    private bool Open()
    {
        Guid? found = FindProvider(ProviderName);
        _provider = found ?? KnownProvider;
        if (found is null)
            Log.Write("desktop", $"Expected Windows to list the provider {ProviderName}, found it missing from the list: trying its known GUID {KnownProvider}");
        else if (found != KnownProvider)
            Log.Write("desktop", $"{ProviderName} is registered as {found} here (expected {KnownProvider}): using the registered one");

        IntPtr props = Marshal.AllocHGlobal(PropertiesSize);
        try
        {
            // A session left behind by an agent that didn't stop cleanly is stopped first, or the name is taken.
            Control(props, 0, _name, EventTraceControlStop);
            InitProperties(props);
            int rc = StartTraceW(out _session, _name, props);
            if (rc != 0)
            {
                Log.Write("desktop", rc switch
                {
                    5 => "No admin rights: the compositor's missed refreshes can't be read",
                    ErrorNoSystemResources => $"Expected to start the desktop trace \"{_name}\", but every slot Windows has for traces is in use (answer {rc}); running now: {string.Join(", ", NetworkTrace.Running())}",
                    _ => $"Expected to start the desktop trace \"{_name}\", Windows answered {rc}",
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
            // Without raw time stamps: each event's TimeStamp comes as a file time (UTC), which places it in its minute.
            *(uint*)(l + 28) = ProcessTraceModeRealTime | ProcessTraceModeEventRecord;
            *(IntPtr*)(l + 424) = Marshal.GetFunctionPointerForDelegate(_callback); // EventRecordCallback
            _trace = OpenTraceW(logFile);
            if (_trace == InvalidTrace)
            {
                _trace = 0;
                Log.Write("desktop", $"Expected to open the desktop trace for reading, found error {Marshal.GetLastPInvokeError()}");
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
            Log.Write("desktop", $"The desktop trace stopped by itself ({rc}) after {Events} events");
            Ended = true;
        }) { IsBackground = true, Name = "Desktop trace" };
        _thread.Start();
        Log.Write("desktop", $"Reading the compositor's missed refreshes (trace \"{_name}\", {ProviderName} {_provider}, keywords 0x{Keywords:X}, level {Level}, "
            + (Filtered ? $"only events {string.Join(", ", EventIds)}" : "no event filter: other events are left out here") + ")");
        return true;
    }

    /// <summary>
    /// The provider switched on: with the event-ID filter first; when Windows refuses that, without it, and without the
    /// Scheduling keyword (its per-frame events would all come).
    /// </summary>
    private bool Enable()
    {
        var provider = _provider;
        ulong keywords = KeywordGlitch | KeywordSmoothness | (ListenForRecoveries ? KeywordScheduling : 0);
        byte[] filter = EventIdFilter(filterIn: true, EventIds);
        byte* desc = stackalloc byte[FilterDescriptorSize];
        byte* parameters = stackalloc byte[EnableParametersSize];
        int rc;
        fixed (byte* f = filter)
        {
            WriteFilterDescriptor(new Span<byte>(desc, FilterDescriptorSize), (ulong)f, (uint)filter.Length);
            WriteEnableParameters(new Span<byte>(parameters, EnableParametersSize), (ulong)desc, 1);
            rc = EnableTraceEx2(_session, &provider, 1 /* enable */, Level, keywords, 0, 0, (IntPtr)parameters);
        }
        if (rc == 0)
        {
            Filtered = true;
            Keywords = keywords;
            return true;
        }
        keywords = KeywordGlitch | KeywordSmoothness;
        int plain = EnableTraceEx2(_session, &provider, 1, Level, keywords, 0, 0, IntPtr.Zero);
        Log.Write("desktop", $"Expected Windows to take an event filter for {ProviderName} (events {string.Join(", ", EventIds)}), it answered {rc}; "
            + (plain == 0 ? $"switched on without it (keywords 0x{keywords:X})" : $"without it, it answered {plain}"));
        if (plain != 0) return false;
        Keywords = keywords;
        return true;
    }

    private void InitProperties(IntPtr props)
    {
        new Span<byte>((void*)props, PropertiesSize).Clear();
        byte* b = (byte*)props;
        *(uint*)(b + 0) = PropertiesSize;   // Wnode.BufferSize
        *(uint*)(b + 40) = 1;               // Wnode.ClientContext: the precise clock
        *(uint*)(b + 44) = 0x20000;         // Wnode.Flags: WNODE_FLAG_TRACED_GUID
        // A few events a minute, a burst of hundreds at the worst (a stalled desktop): small buffers are plenty.
        *(uint*)(b + 48) = 16;              // BufferSize (KB)
        *(uint*)(b + 52) = 2;               // MinimumBuffers
        *(uint*)(b + 56) = 8;               // MaximumBuffers
        *(uint*)(b + 64) = 0x100;           // LogFileMode: EVENT_TRACE_REAL_TIME_MODE
        *(uint*)(b + 68) = 1;               // FlushTimer (seconds)
        *(uint*)(b + 116) = 120;            // LoggerNameOffset
    }

    private void Control(IntPtr props, ulong session, string? name, uint code)
    {
        InitProperties(props);
        ControlTraceW(session, name, props, code);
    }

    /// <summary>Stops the desktop traces test copies of Rigsight left running (every "Rigsight Desktop.…" but <paramref name="own"/>).</summary>
    public static List<string> StopOthers(string own) => NetworkTrace.StopOthers(own, TestPrefix);

    /// <summary>
    /// What Windows says it dropped (asked of the session each time): events it had no buffer for, plus whole buffers the
    /// reader was too late for. Anything but 0 means hitches went uncounted.
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

    // ── Payloads (packed, as the manifest's templates lay them out; win:Boolean is 4 bytes) ──

    /// <summary>
    /// Event 24's payload: +0 cFrame, +8 tVSyncLatency, +16 tElapsed (UInt64 each), +24 Induced (Boolean, 4 bytes), +28
    /// qpcPresented, +36 qpcConfirmed, +44 tPresentLatency, +52 qpcFrame, +60 cSyncRefreshFrame, +68 tGlitch, +76
    /// qpcFrequency, +84 qpcPeriod (UInt64 each), +92 cMonitors (UInt32), +96 rgcDXRefresh[16] of {cPredicted,
    /// cConfirmed} (UInt32 each; the first monitor's is read). 224 bytes in all.
    /// </summary>
    internal static bool DecodeGlitch(ReadOnlySpan<byte> d, out DwmGlitch glitch)
    {
        glitch = default;
        if (d.Length < GlitchMinPayload) return false;
        uint predicted = 0, confirmed = 0;
        if (d.Length >= 104)
        {
            predicted = U32(d, 96);
            confirmed = U32(d, 100);
        }
        glitch = new DwmGlitch(U64(d, 0), U64(d, 8), U64(d, 16), U32(d, 24) != 0, U64(d, 28), U64(d, 36), U64(d, 44), U64(d, 52), U64(d, 60),
            U64(d, 68), U64(d, 76), U64(d, 84), U32(d, 92), predicted, confirmed);
        return true;
    }

    /// <summary>Event 172's payload: +0 Induced (Boolean, 4 bytes).</summary>
    internal static bool DecodeDetected(ReadOnlySpan<byte> d, out bool induced)
    {
        induced = d.Length >= DetectedMinPayload && U32(d, 0) != 0;
        return d.Length >= DetectedMinPayload;
    }

    /// <summary>Event 25's payload: +0 cRefreshFrame (UInt64).</summary>
    internal static bool DecodeRecovery(ReadOnlySpan<byte> d, out ulong refreshFrame)
    {
        refreshFrame = d.Length >= RecoveryMinPayload ? U64(d, 0) : 0;
        return d.Length >= RecoveryMinPayload;
    }

    /// <summary>
    /// Event 237's payload (version 1): +0 cFrameStart, +8 cFrameStop (UInt64), +16 PercentVsyncsMissed, +20 metrics1, +24
    /// metrics2 (UInt32), +28 AnimationScenario, then ApplicationId (UTF-16, each ending in a 0), then ScenarioIndex (UInt32).
    /// </summary>
    internal static bool DecodeSmooth(ReadOnlySpan<byte> d, out DwmSmooth smooth)
    {
        smooth = default;
        if (d.Length < SmoothMinPayload) return false;
        int at = 28;
        if (ReadString(d, ref at) is not { } scenario || ReadString(d, ref at) is not { } app) return false;
        uint index = d.Length >= at + 4 ? U32(d, at) : 0;
        smooth = new DwmSmooth(U64(d, 0), U64(d, 8), U32(d, 16), U32(d, 20), U32(d, 24), scenario, app, index);
        return true;
    }

    /// <summary>
    /// Event 240's payload (version 2): +0 AnimationScenario (UTF-16 ending in a 0), then TotalHits, metrics1, metrics2,
    /// metrics3, FPS (UInt32 each).
    /// </summary>
    internal static bool DecodeVsyncSummary(ReadOnlySpan<byte> d, out DwmVsyncSummary summary)
    {
        summary = default;
        int at = 0;
        if (ReadString(d, ref at) is not { } scenario || d.Length < at + 20) return false;
        summary = new DwmVsyncSummary(scenario, U32(d, at), U32(d, at + 4), U32(d, at + 8), U32(d, at + 12), U32(d, at + 16));
        return true;
    }

    /// <summary>A UTF-16 string ending in a 0 at <paramref name="at"/>, which is moved past the 0; null when it never ends.</summary>
    internal static string? ReadString(ReadOnlySpan<byte> d, ref int at)
    {
        for (int i = at; i + 1 < d.Length; i += 2)
        {
            if (d[i] != 0 || d[i + 1] != 0) continue;
            string s = new(MemoryMarshal.Cast<byte, char>(d[at..i]));
            at = i + 2;
            return s;
        }
        return null;
    }

    private static ulong U64(ReadOnlySpan<byte> d, int at) => MemoryMarshal.Read<ulong>(d[at..]);
    private static uint U32(ReadOnlySpan<byte> d, int at) => MemoryMarshal.Read<uint>(d[at..]);

    // ── The event filter ──

    /// <summary>
    /// EVENT_FILTER_EVENT_ID: +0 FilterIn (BOOLEAN, 1: only these events; 0: all but these), +1 Reserved (UCHAR), +2 Count
    /// (USHORT), +4 Events[Count] (USHORT each). Its size is 4 + 2 per event (FIELD_OFFSET of Events plus the list), at most
    /// 64 events (MAX_EVENT_FILTER_EVENT_ID_COUNT).
    /// </summary>
    internal static byte[] EventIdFilter(bool filterIn, ReadOnlySpan<ushort> ids)
    {
        if (ids.Length is 0 or > 64) throw new ArgumentOutOfRangeException(nameof(ids));
        var b = new byte[4 + 2 * ids.Length];
        b[0] = filterIn ? (byte)1 : (byte)0;
        MemoryMarshal.Write(b.AsSpan(2), (ushort)ids.Length);
        for (int i = 0; i < ids.Length; i++) MemoryMarshal.Write(b.AsSpan(4 + 2 * i), ids[i]);
        return b;
    }

    /// <summary>EVENT_FILTER_DESCRIPTOR (16 bytes): +0 Ptr (ULONGLONG), +8 Size (ULONG), +12 Type (ULONG).</summary>
    internal const int FilterDescriptorSize = 16;

    /// <summary>EVENT_FILTER_TYPE_EVENT_ID (Windows 8.1 and later).</summary>
    internal const uint EventFilterTypeEventId = 0x80000200;

    internal static void WriteFilterDescriptor(Span<byte> into, ulong ptr, uint size)
    {
        into[..FilterDescriptorSize].Clear();
        MemoryMarshal.Write(into, ptr);
        MemoryMarshal.Write(into[8..], size);
        MemoryMarshal.Write(into[12..], EventFilterTypeEventId);
    }

    /// <summary>
    /// ENABLE_TRACE_PARAMETERS (x64, 48 bytes): +0 Version (ULONG, ENABLE_TRACE_PARAMETERS_VERSION_2 = 2), +4
    /// EnableProperty, +8 ControlFlags (ULONG), +12 SourceId (GUID), +32 EnableFilterDesc (pointer, 8-aligned after the
    /// GUID's end at 28), +40 FilterDescCount (ULONG), 4 bytes of padding.
    /// </summary>
    internal const int EnableParametersSize = 48;

    internal static void WriteEnableParameters(Span<byte> into, ulong descriptors, uint count)
    {
        into[..EnableParametersSize].Clear();
        MemoryMarshal.Write(into, 2u);
        MemoryMarshal.Write(into[32..], descriptors);
        MemoryMarshal.Write(into[40..], count);
    }

    /// <summary>The calendar minute (Unix seconds) of an event's file-time stamp (UTC, 100 ns since 1601).</summary>
    internal static long MinuteOf(long fileTime)
    {
        long unix = fileTime > FileTimeUnixEpoch ? (fileTime - FileTimeUnixEpoch) / 10_000_000 : TimeUtil.NowUnix();
        return unix / 60 * 60;
    }

    private const long FileTimeUnixEpoch = 116_444_736_000_000_000;

    // ── The trace's thread ────────────────────────────────────────────────

    private void OnEvent(IntPtr record)
    {
        Interlocked.Increment(ref _events);
        byte* r = (byte*)record;
        if (*(Guid*)(r + 24) != _provider) return;          // EventHeader.ProviderId
        ushort id = *(ushort*)(r + 40);                      // EventHeader.EventDescriptor.Id
        int kind = Array.IndexOf(EventIds, id);
        if (kind < 0)
        {
            Interlocked.Increment(ref _others);
            return;
        }
        byte version = r[42];                                // EventHeader.EventDescriptor.Version
        long minute = MinuteOf(*(long*)(r + 16));            // EventHeader.TimeStamp (a file time: not raw)
        int pid = *(int*)(r + 12);                           // EventHeader.ProcessId (dwm.exe's)
        ushort length = *(ushort*)(r + 86);                  // UserDataLength
        byte* data = *(byte**)(r + 96);                      // UserData
        var d = data is null ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(data, length);
        bool first = _logged[kind] < LogFirst;
        if (first) _logged[kind]++;
        long freq = Stopwatch.Frequency;

        switch (id)
        {
            case EventGlitch when DecodeGlitch(d, out var g):
                double? ms = g.GlitchMs(freq);
                Tally.Glitch(minute, g.Induced, ms);
                if (first)
                    Log.Write("desktop", $"Event 24 (missed refresh) v{version}, {length} bytes, dwm {pid}: cFrame {g.Frame}, tVSyncLatency {g.VSyncLatency}, tElapsed {g.Elapsed}, "
                        + $"Induced {g.Induced}, qpcPresented {g.QpcPresented}, qpcConfirmed {g.QpcConfirmed}, tPresentLatency {g.PresentLatency}, qpcFrame {g.QpcFrame}, "
                        + $"cSyncRefreshFrame {g.SyncRefreshFrame}, tGlitch {g.GlitchTicks}, qpcFrequency {g.QpcFrequency} (this PC's counter {freq}), qpcPeriod {g.QpcPeriod}, "
                        + $"cMonitors {g.Monitors}, first monitor cPredicted {g.Predicted} cConfirmed {g.Confirmed}; read as {Fmt(ms)} ms late, refresh period {Fmt(g.PeriodMs(freq))} ms");
                break;
            case EventGlitchDetected when DecodeDetected(d, out bool induced):
                Tally.Detected(minute, induced);
                if (first) Log.Write("desktop", $"Event 172 (missed refresh found) v{version}, {length} bytes, dwm {pid}: Induced {induced}");
                break;
            case EventRecovery when DecodeRecovery(d, out ulong refresh):
                Tally.Recovery(minute);
                if (first) Log.Write("desktop", $"Event 25 (recovery attempt) v{version}, {length} bytes, dwm {pid}: cRefreshFrame {refresh}");
                break;
            case EventSmooth when DecodeSmooth(d, out var s):
                Tally.Smooth(minute, s);
                if (first)
                    Log.Write("desktop", $"Event 237 (animation smoothness) v{version}, {length} bytes, dwm {pid}: scenario \"{s.Scenario}\", PercentVsyncsMissed {s.PercentMissed}, "
                        + $"app \"{s.AppId}\", cFrameStart {s.FrameStart}, cFrameStop {s.FrameStop}, metrics1 0x{s.Metrics1:X}, metrics2 0x{s.Metrics2:X}, ScenarioIndex {s.ScenarioIndex}");
                break;
            case EventVsyncSummary when DecodeVsyncSummary(d, out var v):
                Tally.VsyncSummary(minute, v);
                if (first)
                    Log.Write("desktop", $"Event 240 (refreshes added up) v{version}, {length} bytes, dwm {pid}: scenario \"{v.Scenario}\", TotalHits {v.TotalHits}, FPS {v.Fps}, "
                        + $"metrics1 0x{v.Metrics1:X}, metrics2 0x{v.Metrics2:X}, metrics3 0x{v.Metrics3:X}");
                break;
            default:
                if (Interlocked.Increment(ref _short) <= LogFirst)
                    Log.Write("desktop", $"Expected event {id}'s fields, found a payload of {length} bytes (v{version}) too short to read: left out");
                break;
        }
    }

    private static string Fmt(double? v) => v is { } x ? x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "?";

    // ── Finding the provider ──

    /// <summary>The GUID Windows has registered for the provider called <paramref name="name"/> (TdhEnumerateProviders); null when none.</summary>
    internal static Guid? FindProvider(string name)
    {
        uint size = 64 * 1024;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                int rc = TdhEnumerateProviders(buf, ref size);
                if (rc == ErrorInsufficientBuffer) continue;
                if (rc != 0) return null;
                // PROVIDER_ENUMERATION_INFO: +0 NumberOfProviders, +4 Reserved, +8 TRACE_PROVIDER_INFO[] of 24 bytes each:
                // +0 ProviderGuid, +16 SchemaSource, +20 ProviderNameOffset (from the buffer's start, a UTF-16 string).
                byte* b = (byte*)buf;
                uint count = *(uint*)b;
                for (uint i = 0; i < count; i++)
                {
                    byte* e = b + 8 + i * 24;
                    uint offset = *(uint*)(e + 20);
                    if (offset == 0 || offset >= size) continue;
                    if (string.Equals(new string((char*)(b + offset)), name, StringComparison.OrdinalIgnoreCase)) return *(Guid*)e;
                }
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        return null;
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

    private const ulong KeywordScheduling = 0x80;
    private const uint EventTraceControlQuery = 0, EventTraceControlStop = 1;
    private const uint ProcessTraceModeRealTime = 0x100, ProcessTraceModeEventRecord = 0x10000000;
    private const int ErrorInsufficientBuffer = 122, ErrorNoSystemResources = 1450;
    private const ulong InvalidTrace = ulong.MaxValue;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EventCallback(IntPtr record);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int StartTraceW(out ulong handle, string name, IntPtr properties);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int ControlTraceW(ulong handle, string? name, IntPtr properties, uint code);
    [DllImport("advapi32.dll")] private static extern int EnableTraceEx2(ulong handle, Guid* provider, uint control, byte level, ulong anyKeyword, ulong allKeyword, uint timeout, IntPtr parameters);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern ulong OpenTraceW(IntPtr logFile);
    [DllImport("advapi32.dll")] private static extern int ProcessTrace(ulong* handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")] private static extern int CloseTrace(ulong handle);
    [DllImport("tdh.dll")] private static extern int TdhEnumerateProviders(IntPtr buffer, ref uint size);
}
