using System.Diagnostics;
using System.Runtime.InteropServices;
using Rigsight.Agent.Native;
using Rigsight.Agent.Sensors;
using Rigsight.Core;

namespace Rigsight.Agent.Tracking;

/// <summary>
/// How busy each app keeps the main GPU, as Task Manager's GPU column shows it: for each of the GPU's engines (3D,
/// copy, video encode…) the share of time the app's work ran on it, and the busiest engine counts. Read from the
/// graphics kernel's own counters (D3DKMTQueryStatistics), one call per process and engine. Only processes using the
/// GPU are held open; the rest are let go after one call and asked again now and then.
/// </summary>
internal sealed unsafe class GpuSampler : IDisposable
{
    // D3DKMT_QUERYSTATISTICS (d3dkmthk.h): Type, AdapterLuid, hProcess, the result (a union), then the query's input.
    private const int StatsSize = 0x328, TypeOffset = 0, LuidOffset = 4, ProcessOffset = 16, ResultOffset = 24, QueryOffset = 0x320;
    private const int QueryAdapter = 0, QueryProcessAdapter = 2, QueryProcessNode = 6; // D3DKMT_QUERYSTATISTICS_TYPE
    private const int NodeCountOffset = ResultOffset + 4; // ADAPTER_INFORMATION: NbSegments, NodeCount…
    private const uint QueryInformation = 0x400;          // PROCESS_QUERY_INFORMATION: the limited kind isn't enough here

    /// <summary>A process that isn't using the GPU is asked again after this many samples (it may start to).</summary>
    private const int RecheckIdleAfter = 12;

    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryStatistics(byte* stats);

    private sealed class Proc
    {
        public IntPtr Handle;         // held only while the process uses the GPU
        public long[]? Running;       // per engine, in 100 ns (null: not a GPU user when last asked)
        public int SkipUntil;
        public bool Denied;           // Windows won't let it be read (a protected process): not asked again
    }

    private readonly byte* _stats = (byte*)NativeMemory.AllocZeroed(StatsSize);
    private readonly Dictionary<(int Pid, long Created), Proc> _procs = [];
    private long _luid;
    private int _nodes = -1;
    private long _lastTime;
    private int _sample;

    /// <summary>Smaller shares than this (in %) are left out: an app drawing its window now and then.</summary>
    public double MinShare { get; set; } = 0.5;

    /// <summary>Processes being followed (each holds a handle).</summary>
    internal int Tracked => _procs.Count;

    /// <summary>Processes held open (only those using the GPU).</summary>
    internal int Held => _procs.Values.Count(p => p.Handle != IntPtr.Zero);

    /// <summary>Whether this PC's GPU can be read at all (false when Windows can't say which one is main).</summary>
    public bool Available => _nodes > 0;

    /// <summary>The apps' GPU shares (0–100, by exe) since the last sample; empty on the first one or when unavailable.</summary>
    public Dictionary<string, double> Sample(ProcessSnapshot snapshot)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (_nodes < 0) Open();
        if (_nodes <= 0) return result;

        long now = Stopwatch.GetTimestamp();
        double elapsed100ns = _lastTime == 0 ? 0 : Stopwatch.GetElapsedTime(_lastTime, now).Ticks;
        _lastTime = now;
        _sample++;

        // Per app, per engine: the busy time of all its processes added up; the busiest engine is the app's share.
        var perApp = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<(int, long)>();
        foreach (var key in snapshot.Processes)
        {
            seen.Add(key);
            if (!_procs.TryGetValue(key, out var p)) _procs[key] = p = new Proc();
            if (p.Denied || _sample < p.SkipUntil) continue;
            if (p.Handle == IntPtr.Zero && (p.Handle = Win32.OpenProcess(QueryInformation, false, key.Pid)) == IntPtr.Zero)
            {
                p.Denied = true;
                continue;
            }

            var before = p.Running;
            if (!UsesGpu(p.Handle))
            {
                // Most processes never touch the GPU: let go of them (a handle each would add up to hundreds) and
                // look again now and then.
                Win32.CloseHandle(p.Handle);
                p.Handle = IntPtr.Zero;
                p.Running = null;
                p.SkipUntil = _sample + RecheckIdleAfter;
                continue;
            }
            var running = new long[_nodes];
            for (int n = 0; n < _nodes; n++) running[n] = RunningTime(p.Handle, n);
            p.Running = running;
            if (before is null || elapsed100ns <= 0 || !snapshot.PidToExe.TryGetValue(key.Pid, out var exe)) continue;

            if (!perApp.TryGetValue(exe, out var engines)) perApp[exe] = engines = new double[_nodes];
            for (int n = 0; n < _nodes; n++)
                engines[n] += Math.Max(0, running[n] - before[n]) / elapsed100ns * 100;
        }

        // Processes that have ended.
        foreach (var gone in _procs.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            if (_procs[gone].Handle != IntPtr.Zero) Win32.CloseHandle(_procs[gone].Handle);
            _procs.Remove(gone);
        }

        foreach (var (exe, engines) in perApp)
        {
            double busiest = Math.Min(100, engines.Max());
            if (busiest >= MinShare) result[exe] = busiest;
        }
        return result;
    }

    /// <summary>The main GPU is looked for again at the next sample (the sensors were just found again: see SensorHealth).</summary>
    public void Reset()
    {
        if (_nodes < 0) return;
        _nodes = -1;
        _lastTime = 0;
        foreach (var p in _procs.Values) p.Running = null; // per-engine times of the card as it was
    }

    /// <summary>Finds the main GPU and how many engines it has.</summary>
    private void Open()
    {
        _nodes = 0;
        if (GpuPreference.MainAdapterLuid() is not long luid) return;
        _luid = luid;
        Prepare(QueryAdapter, IntPtr.Zero);
        if (D3DKMTQueryStatistics(_stats) == 0) _nodes = Math.Clamp(*(int*)(_stats + NodeCountOffset), 0, 64);
        Log.Write("gpu", _nodes > 0 ? $"Reading each app's GPU use ({_nodes} engines)." : "Each app's GPU use can't be read on this PC.");
    }

    private bool UsesGpu(IntPtr process)
    {
        Prepare(QueryProcessAdapter, process);
        return D3DKMTQueryStatistics(_stats) == 0;
    }

    private long RunningTime(IntPtr process, int node)
    {
        Prepare(QueryProcessNode, process);
        *(uint*)(_stats + QueryOffset) = (uint)node;
        return D3DKMTQueryStatistics(_stats) == 0 ? *(long*)(_stats + ResultOffset) : 0;
    }

    private void Prepare(int type, IntPtr process)
    {
        NativeMemory.Clear(_stats, StatsSize);
        *(int*)(_stats + TypeOffset) = type;
        *(long*)(_stats + LuidOffset) = _luid;
        *(IntPtr*)(_stats + ProcessOffset) = process;
    }

    public void Dispose()
    {
        foreach (var p in _procs.Values)
            if (p.Handle != IntPtr.Zero) Win32.CloseHandle(p.Handle);
        _procs.Clear();
        NativeMemory.Free(_stats);
    }
}
