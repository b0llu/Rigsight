using System.Reflection;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using Rigsight.Core;

namespace Rigsight.Agent.Sensors;

/// <summary>
/// Cheap NVIDIA readings for background tracking. LibreHardwareMonitor's full GPU update costs
/// ~35 ms of CPU (it queries dozens of driver values and samples PCIe throughput); this reads just
/// what Rigsight records through NVML plus one NVAPI call for hot spot / memory junction, ~0.5 ms.
/// The full update is still used while the app is open.
/// </summary>
internal sealed class NvidiaFastPath
{
    private const uint NvApiThermalSensorsId = 0x65FE3AAD;

    private readonly IntPtr _nvmlDevice;
    private readonly IntPtr _gpuHandle;
    private readonly uint _thermalMask;
    private readonly ThermalSensorsDelegate? _thermal;

    private NvidiaFastPath(IntPtr nvmlDevice, IntPtr gpuHandle, uint thermalMask, ThermalSensorsDelegate? thermal)
    {
        _nvmlDevice = nvmlDevice;
        _gpuHandle = gpuHandle;
        _thermalMask = thermalMask;
        _thermal = thermal;
    }

    /// <summary>Sets up the fast path for a single NVIDIA GPU, or returns null (then the normal update is used).</summary>
    public static NvidiaFastPath? TryCreate(IHardware gpu)
    {
        try
        {
            if (nvmlInit_v2() != 0 || nvmlDeviceGetHandleByIndex_v2(0, out var device) != 0) return null;

            // The NVAPI GPU handle and supported-sensor mask were already discovered by LibreHardwareMonitor.
            ThermalSensorsDelegate? thermal = null;
            IntPtr handle = IntPtr.Zero;
            uint mask = 0;
            var type = gpu.GetType();
            var handleObj = type.GetField("_handle", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(gpu);
            var ptrField = handleObj?.GetType().GetField("ptr", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (ptrField?.GetValue(handleObj) is IntPtr h &&
                type.GetField("_thermalSensorsMask", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(gpu) is uint m &&
                m != 0 && NvApiQueryInterface(NvApiThermalSensorsId) is var fn && fn != IntPtr.Zero)
            {
                handle = h;
                mask = m;
                thermal = Marshal.GetDelegateForFunctionPointer<ThermalSensorsDelegate>(fn);
            }
            return new NvidiaFastPath(device, handle, mask, thermal);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return null;
        }
    }

    public void Read(Dictionary<string, double?> into)
    {
        into[KeySensors.GpuTemp] = nvmlDeviceGetTemperature(_nvmlDevice, 0, out uint temp) == 0 ? temp : null;
        into[KeySensors.GpuLoad] = nvmlDeviceGetUtilizationRates(_nvmlDevice, out var util) == 0 ? util.Gpu : null;
        into[KeySensors.GpuPower] = nvmlDeviceGetPowerUsage(_nvmlDevice, out uint mw) == 0 ? mw / 1000.0 : null;
        into[KeySensors.GpuClock] = nvmlDeviceGetClockInfo(_nvmlDevice, 0, out uint clock) == 0 ? clock : null;
        if (nvmlDeviceGetMemoryInfo(_nvmlDevice, out var mem) == 0 && mem.Total > 0)
        {
            into[KeySensors.GpuVramUsed] = mem.Used / 1048576.0;
            into[KeySensors.GpuVramTotal] = mem.Total / 1048576.0;
            into[KeySensors.GpuVramLoad] = 100.0 * mem.Used / mem.Total;
        }

        if (_thermal is not null)
        {
            var s = new ThermalSensors
            {
                Version = (uint)(Marshal.SizeOf<ThermalSensors>() | (2 << 16)),
                Mask = _thermalMask,
                Reserved = new int[8],
                Temperatures = new int[32],
            };
            if (_thermal(_gpuHandle, ref s) == 0)
            {
                into[KeySensors.GpuHotSpot] = s.Temperatures[1] > 0 ? s.Temperatures[1] / 256.0 : null;
                into[KeySensors.GpuMemJunction] = s.Temperatures[9] > 0 ? s.Temperatures[9] / 256.0 : null;
            }
        }
    }

    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization util);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetClockInfo(IntPtr device, int clockType, out uint mhz);
    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface")] private static extern IntPtr NvApiQueryInterface(uint id);

    [StructLayout(LayoutKind.Sequential)] private struct NvmlUtilization { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct NvmlMemory { public ulong Total, Free, Used; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThermalSensors
    {
        public uint Version;
        public uint Mask;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public int[] Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public int[] Temperatures;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ThermalSensorsDelegate(IntPtr gpu, ref ThermalSensors sensors);
}
