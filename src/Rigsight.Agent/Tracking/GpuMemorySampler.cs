using System.Globalization;
using System.Runtime.InteropServices;
using Rigsight.Agent.Sensors;
using Rigsight.Core;

namespace Rigsight.Agent.Tracking;

/// <summary>
/// Each app's own video memory on the main graphics card, as Task Manager's "Dedicated GPU memory" column shows it:
/// Windows' "GPU Process Memory" counter, one instance per process and card, read in one go. The card's memory in use
/// (the sensors' figure) is every app together; this is what lets a game's own share be told apart from it.
/// </summary>
internal sealed class GpuMemorySampler : IDisposable
{
    private const uint FormatLarge = 0x400; // PDH_FMT_LARGE
    private const int MoreData = unchecked((int)0x800007D2); // PDH_MORE_DATA

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr items);
    [DllImport("pdh.dll")] private static extern int PdhCloseQuery(IntPtr query);

    private IntPtr _query, _counter;
    private bool _opened;
    private string? _card;   // "luid_0x…_0x…", the main card's part of each instance name
    private IntPtr _buffer;
    private uint _bufferSize;

    /// <summary>Each app's own video memory in MB, by exe (only apps holding some); empty when Windows can't say.</summary>
    public Dictionary<string, double> Sample(ProcessSnapshot snapshot)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (!_opened) Open();
        if (_counter == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return result;

        uint bytes = _bufferSize;
        int status = PdhGetFormattedCounterArrayW(_counter, FormatLarge, ref bytes, out uint count, _buffer);
        if (status == MoreData)
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _bufferSize = bytes + 4096;
            _buffer = Marshal.AllocHGlobal((int)_bufferSize);
            bytes = _bufferSize;
            status = PdhGetFormattedCounterArrayW(_counter, FormatLarge, ref bytes, out count, _buffer);
        }
        if (status != 0) return result;

        // PDH_FMT_COUNTERVALUE_ITEM_W: the instance's name, then its status and value (24 bytes on 64-bit Windows).
        for (int i = 0; i < count; i++)
        {
            IntPtr item = _buffer + i * 24;
            if (Marshal.ReadInt32(item, 8) != 0) continue;
            long value = Marshal.ReadInt64(item, 16);
            if (value <= 0 || Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) is not { } name) continue;
            // "pid_1234_luid_0x00000000_0x0001122D_phys_0": another card's share isn't this card's memory.
            if (_card is not null && !name.Contains(_card, StringComparison.OrdinalIgnoreCase)) continue;
            int end = name.IndexOf('_', 4);
            if (!name.StartsWith("pid_", StringComparison.Ordinal) || end < 0
                || !int.TryParse(name.AsSpan(4, end - 4), NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
                || !snapshot.PidToExe.TryGetValue(pid, out var exe)) continue;
            result[exe] = result.GetValueOrDefault(exe) + value / 1_048_576.0;
        }
        return result;
    }

    private void Open()
    {
        _opened = true;
        if (GpuPreference.MainAdapterLuid() is long luid)
            _card = $"luid_0x{(uint)(luid >> 32):X8}_0x{(uint)luid:X8}";
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0
            || PdhAddEnglishCounterW(_query, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out _counter) != 0)
        {
            _counter = IntPtr.Zero;
            Log.Write("gpu", "Each app's video memory can't be read on this PC (no GPU Process Memory counter).");
            return;
        }
        Log.Write("gpu", $"Reading each app's video memory{(_card is null ? " (main card not known: every card counted)" : "")}.");
    }

    /// <summary>The main card is looked for again at the next sample (a driver update gives it a new identity).</summary>
    public void Reset()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        (_query, _counter, _opened) = (IntPtr.Zero, IntPtr.Zero, false);
    }

    public void Dispose()
    {
        Reset();
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
    }
}
