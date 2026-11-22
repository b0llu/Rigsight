using System.Runtime.InteropServices;
using System.Text;
using Rigsight.Core;

namespace Rigsight.Agent.Drivers;

/// <summary>
/// Where each loaded driver sits in the kernel's memory, so the address of a routine Windows ran (the "Routine" in a DPC
/// or interrupt event) can be put down to its driver's file. Read from Windows' own list of kernel modules
/// (NtQuerySystemInformation, SystemModuleInformation), sorted by address for a binary search. Drivers load and unload
/// (a USB device plugged in, a driver updated), so the trace reads the list again when addresses stop matching.
/// </summary>
internal sealed unsafe class ModuleMap
{
    private readonly ulong[] _bases;
    private readonly uint[] _sizes;
    private readonly string[] _files;

    /// <summary>A map of nothing: every address is "".</summary>
    public static readonly ModuleMap Empty = new([], [], []);

    internal ModuleMap(ulong[] bases, uint[] sizes, string[] files)
    {
        _bases = bases;
        _sizes = sizes;
        _files = files;
    }

    /// <summary>Modules in the map.</summary>
    public int Count => _bases.Length;

    /// <summary>Modules whose address Windows gave as 0 (it hides kernel addresses from a process without debug rights).</summary>
    public int Hidden { get; private init; }

    /// <summary>The driver file holding <paramref name="address"/>, lower case ("nvlddmkm.sys"), or "" when none does.</summary>
    public string Find(ulong address) => Find(_bases, _sizes, address) is int i and >= 0 ? _files[i] : "";

    /// <summary>
    /// The module holding <paramref name="address"/>: the last whose base is at or below it, if the address is short of
    /// that module's end. -1 when none does (below the first, past an end, in a gap between two). Modules never overlap.
    /// </summary>
    internal static int Find(ReadOnlySpan<ulong> bases, ReadOnlySpan<uint> sizes, ulong address)
    {
        int lo = 0, hi = bases.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (bases[mid] <= address)
            {
                found = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }
        return found >= 0 && address - bases[found] < sizes[found] ? found : -1;
    }

    /// <summary>Modules given in any order, sorted by base; those with no base or no size left out.</summary>
    internal static ModuleMap Build(IEnumerable<(ulong Base, uint Size, string File)> modules)
    {
        var list = new List<(ulong Base, uint Size, string File)>();
        int hidden = 0;
        foreach (var m in modules)
        {
            if (m.Base == 0) hidden++;
            else if (m.Size > 0) list.Add(m);
        }
        list.Sort((a, b) => a.Base.CompareTo(b.Base));
        var bases = new ulong[list.Count];
        var sizes = new uint[list.Count];
        var files = new string[list.Count];
        for (int i = 0; i < list.Count; i++) (bases[i], sizes[i], files[i]) = list[i];
        return new ModuleMap(bases, sizes, files) { Hidden = hidden };
    }

    // ── Windows ───────────────────────────────────────────────────────────

    // RTL_PROCESS_MODULES (x64): ULONG NumberOfModules, then (8-byte aligned, at +8) RTL_PROCESS_MODULE_INFORMATION[]:
    //   +0 Section (HANDLE), +8 MappedBase (PVOID), +16 ImageBase (PVOID), +24 ImageSize (ULONG), +28 Flags (ULONG),
    //   +32 LoadOrderIndex, +34 InitOrderIndex, +36 LoadCount, +38 OffsetToFileName (USHORT each),
    //   +40 FullPathName (UCHAR[256], ANSI, "\SystemRoot\system32\drivers\nvlddmkm.sys") = 296 bytes each.
    private const int SystemModuleInformation = 11;
    private const int ModulesAt = 8, ModuleSize = 296, PathAt = 40, PathBytes = 256;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    /// <summary>
    /// The modules loaded now; <see cref="Empty"/> when Windows won't say. On Windows 11 24H2 and later the addresses are 0
    /// for a process without the debug privilege switched on, so it is switched on for the read and back off after (the
    /// agent runs as admin, which holds it); <see cref="Hidden"/> says when that didn't help.
    /// </summary>
    public static ModuleMap Read()
    {
        bool debug = Privilege.Enable(Privilege.Debug, out bool wasOn);
        int size = 256 * 1024;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            for (int tries = 0; tries < 4; tries++)
            {
                buffer = Marshal.AllocHGlobal(size);
                int status = NtQuerySystemInformation(SystemModuleInformation, buffer, size, out int needed);
                if (status == 0) return Parse(new ReadOnlySpan<byte>((void*)buffer, size));
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                if (status != StatusInfoLengthMismatch)
                {
                    Log.Write("drivers", $"Expected the list of loaded drivers from Windows, it answered 0x{status:X8}");
                    return Empty;
                }
                size = Math.Max(size * 2, needed + 16 * 1024);
            }
            Log.Write("drivers", "Expected the list of loaded drivers from Windows, it kept growing while read");
            return Empty;
        }
        catch (Exception ex)
        {
            Log.Error("drivers", ex);
            return Empty;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (debug && !wasOn) Privilege.Disable(Privilege.Debug);
        }
    }

    /// <summary>The modules out of an RTL_PROCESS_MODULES block (layout above).</summary>
    internal static ModuleMap Parse(ReadOnlySpan<byte> block)
    {
        if (block.Length < ModulesAt) return Empty;
        uint count = MemoryMarshal.Read<uint>(block);
        var list = new List<(ulong, uint, string)>((int)Math.Min(count, 4096));
        for (uint i = 0; i < count; i++)
        {
            int at = ModulesAt + (int)i * ModuleSize;
            if (at + ModuleSize > block.Length) break;
            var m = block.Slice(at, ModuleSize);
            ulong imageBase = MemoryMarshal.Read<ulong>(m[16..]);
            uint imageSize = MemoryMarshal.Read<uint>(m[24..]);
            int nameAt = MemoryMarshal.Read<ushort>(m[38..]);
            var path = m.Slice(PathAt, PathBytes);
            int end = path.IndexOf((byte)0);
            if (end < 0) end = PathBytes;
            if (nameAt > end) nameAt = 0;
            string file = Encoding.ASCII.GetString(path[nameAt..end]).ToLowerInvariant();
            list.Add((imageBase, imageSize, file));
        }
        return Build(list);
    }

    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);
}

/// <summary>Switching one of the process's own privileges on or off (they are held, not on, until asked for).</summary>
internal static class Privilege
{
    public const string Debug = "SeDebugPrivilege";

    /// <summary>Switches it on; false when the process doesn't hold it (not admin). <paramref name="wasOn"/>: it already was.</summary>
    public static bool Enable(string name, out bool wasOn) => Set(name, true, out wasOn);

    public static void Disable(string name) => Set(name, false, out _);

    private static bool Set(string name, bool on, out bool wasOn)
    {
        wasOn = false;
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out IntPtr token)) return false;
        try
        {
            if (!LookupPrivilegeValueW(null, name, out long luid)) return false;
            var state = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = on ? SePrivilegeEnabled : 0 };
            var before = new TOKEN_PRIVILEGES();
            int size = Marshal.SizeOf<TOKEN_PRIVILEGES>();
            // AdjustTokenPrivileges succeeds without the privilege too: only the last error (1300, not all assigned) says.
            bool ok = AdjustTokenPrivileges(token, false, ref state, size, ref before, out _) && Marshal.GetLastPInvokeError() == 0;
            // Windows hands back only the privileges it changed: none means it was already as asked.
            wasOn = ok && on && before.Count == 0;
            return ok;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private const uint TokenAdjustPrivileges = 0x20, TokenQuery = 0x08, SePrivilegeEnabled = 0x02;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TOKEN_PRIVILEGES { public uint Count; public long Luid; public uint Attributes; }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, int length, ref TOKEN_PRIVILEGES previous, out int returnLength);
}
