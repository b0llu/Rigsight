using System.Runtime.InteropServices;
using Rigsight.Core;

namespace Rigsight.Agent.Sensors;

/// <summary>
/// Watches a hardware scan. A marker file is written before it starts and removed when it finishes, so a scan that
/// never finished (it hung, or the PC had to be restarted) is known the next time and the risky parts are skipped.
/// While the scan runs, Windows' kernel memory is checked every second: drivers use it, not programs, so Task Manager
/// can't show who's using it. A scan during which it grows by gigabytes is treated as unfinished too.
/// </summary>
internal sealed class ScanGuard : IDisposable
{
    /// <summary>Kernel memory growth during one scan that counts as a driver running away (a scan normally adds a few MB).</summary>
    public const long DefaultLimitBytes = 1536L << 20;

    private readonly string _marker;
    private readonly Func<long?> _kernelBytes;
    private readonly long _limitBytes;
    private readonly long? _startBytes;
    private readonly System.Threading.Timer? _timer;
    private readonly bool _keepMarker;
    private volatile bool _tripped;

    /// <param name="keepMarker">Leave the marker in place even after a clean scan: a safe-mode scan proves nothing about the
    /// parts it skipped, so the next start stays safe until the user asks to read everything again.</param>
    public ScanGuard(string marker, bool keepMarker = false, Func<long?>? kernelBytes = null, long limitBytes = DefaultLimitBytes, int everyMs = 1000)
    {
        _marker = marker;
        _keepMarker = keepMarker;
        _kernelBytes = kernelBytes ?? KernelBytes;
        _limitBytes = limitBytes;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, DateTime.Now.ToString("O"));
        }
        catch (Exception ex)
        {
            Log.Error("sensors", ex);
        }
        _startBytes = _kernelBytes();
        if (_startBytes is not null) _timer = new System.Threading.Timer(_ => Check(), null, everyMs, everyMs);
    }

    /// <summary>Whether the last scan never finished (its marker is still there).</summary>
    public static bool LastScanUnfinished(string marker) => File.Exists(marker);

    /// <summary>Forget an unfinished scan (the user asked to read everything again).</summary>
    public static void Forget(string marker)
    {
        try { File.Delete(marker); } catch { }
    }

    /// <summary>Kernel memory grew past the limit during the scan.</summary>
    public bool Tripped => _tripped;

    /// <summary>Raised (once, from a timer thread) when kernel memory grows past the limit.</summary>
    public event Action<long>? Exceeded;

    internal void Check()
    {
        if (_tripped || _startBytes is not long start || _kernelBytes() is not long now) return;
        long grown = now - start;
        if (grown < _limitBytes) return;
        _tripped = true;
        Log.Write("sensors", $"Kernel memory grew by {grown >> 20} MB during the hardware scan: the risky parts will be skipped");
        Exceeded?.Invoke(grown);
    }

    /// <summary>The scan finished. A clean one removes the marker; one that tripped the guard leaves it for the next start.</summary>
    public void Dispose()
    {
        _timer?.Dispose();
        if (!_tripped && !_keepMarker) Forget(_marker);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache;
        public nuint KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetPerformanceInfo(out PerformanceInformation info, uint size);

    /// <summary>Windows' kernel memory (paged and non-paged pool) in bytes, or null if it can't be read.</summary>
    public static long? KernelBytes()
    {
        var info = new PerformanceInformation { Size = (uint)Marshal.SizeOf<PerformanceInformation>() };
        if (!GetPerformanceInfo(out info, info.Size)) return null;
        return (long)info.KernelTotal * (long)info.PageSize;
    }
}
