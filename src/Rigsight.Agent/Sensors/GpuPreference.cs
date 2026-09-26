using System.Runtime.InteropServices;
using Rigsight.Core;

namespace Rigsight.Agent.Sensors;

/// <summary>
/// The graphics adapters in the order Windows offers them to games: the high-performance one first (DXGI's
/// EnumAdapterByGpuPreference, which follows Settings → Display → Graphics). The first is the main GPU Rigsight shows;
/// software adapters (Microsoft Basic Render Driver) are left out, and so is the same hardware listed again (Windows
/// lists a card a second time, with its own adapter ID, for a virtual display it draws, e.g. a USB monitor's; two
/// identical cards also come once, their order between them was never known by name anyway). Empty when Windows can't say (before Windows 10 1803,
/// or no DXGI): the GPUs are then ranked by name (see <see cref="KeySensors.Gpus"/>).
/// </summary>
internal static unsafe class GpuPreference
{
    private static readonly Guid IidFactory6 = new("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
    private static readonly Guid IidAdapter1 = new("29038f61-3839-4626-91fd-086879011a05");
    private const int HighPerformance = 1;         // DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE
    private const uint SoftwareAdapter = 2;       // DXGI_ADAPTER_FLAG_SOFTWARE
    private const int NotFound = unchecked((int)0x887A0002); // DXGI_ERROR_NOT_FOUND

    // Places in the COM method tables (IUnknown's 3 first).
    private const int Release = 2;
    private const int EnumAdapterByGpuPreference = 29; // IDXGIFactory6
    private const int GetDesc1 = 10;                   // IDXGIAdapter1

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid riid, out nint factory);

    public static List<KeySensors.PreferredGpu> Read()
    {
        var list = new List<KeySensors.PreferredGpu>();
        var seen = new HashSet<(uint, uint, uint, uint)>();
        try
        {
            if (CreateDXGIFactory1(IidFactory6, out var factory) < 0 || factory == 0) return list;
            try
            {
                var enumerate = (delegate* unmanaged[Stdcall]<nint, uint, int, Guid*, nint*, int>)Method(factory, EnumAdapterByGpuPreference);
                var iid = IidAdapter1;
                for (uint i = 0; i < 16; i++)
                {
                    nint adapter;
                    int hr = enumerate(factory, i, HighPerformance, &iid, &adapter);
                    if (hr == NotFound || hr < 0 || adapter == 0) break;
                    try
                    {
                        var desc = Describe(adapter);
                        if (desc is { } d && (d.Flags & SoftwareAdapter) == 0 && !string.IsNullOrWhiteSpace(d.Description) && seen.Add((d.VendorId, d.DeviceId, d.SubSysId, d.Revision)))
                            list.Add(new KeySensors.PreferredGpu(d.Description.Trim(), Maker(d.VendorId)));
                    }
                    finally
                    {
                        Free(adapter);
                    }
                }
            }
            finally
            {
                Free(factory);
            }
        }
        catch (Exception ex)
        {
            // No DXGI, or too old for the preference: the GPUs are ranked by name instead.
            Log.Write("sensors", $"Windows' GPU preference unavailable: {ex.Message}");
            list.Clear();
        }
        return list;
    }

    /// <summary>The LibreHardwareMonitor hardware type of an adapter's maker (PCI vendor ID).</summary>
    public static string Maker(uint vendorId) => vendorId switch
    {
        0x10DE => "GpuNvidia",
        0x1002 or 0x1022 => "GpuAmd",
        0x8086 => "GpuIntel",
        _ => "",
    };

    private static AdapterDesc1? Describe(nint adapter)
    {
        var getDesc = (delegate* unmanaged[Stdcall]<nint, nint, int>)Method(adapter, GetDesc1);
        nint buffer = Marshal.AllocHGlobal(Marshal.SizeOf<AdapterDesc1>());
        try
        {
            return getDesc(adapter, buffer) < 0 ? null : Marshal.PtrToStructure<AdapterDesc1>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static nint Method(nint com, int slot) => (*(nint**)com)[slot];

    private static void Free(nint com)
    {
        if (com != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Method(com, Release))(com);
    }
}
