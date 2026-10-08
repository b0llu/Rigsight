using System.Runtime.InteropServices;
using Rigsight.Core;

namespace Rigsight.Agent.Sensors;

/// <summary>
/// Says when a graphics card's driver goes or comes back (a driver update, the card switched off in Device Manager,
/// an external card unplugged). The maker's libraries the readings come through don't survive that: a handle taken
/// before reads memory that's no longer there once the new driver is in, and that ends the whole agent (an NVIDIA
/// update on 7 Oct 2026: access violation in nvmlDeviceGetTemperature, ten seconds after the new driver started). A
/// crash like that can't be caught, so the graphics cards are left alone from the moment Windows says one is going,
/// and the agent starts again once things have been quiet for a while: a new process loads the new driver's libraries.
/// </summary>
internal sealed class DisplayDriverWatch : IDisposable
{
    /// <summary>Quiet for this long after the last change before starting again (ms): an install takes the card away and brings it back, sometimes twice.</summary>
    public const int SettleMs = 30_000;

    private static readonly Guid DisplayAdapters = new("5b45201d-f2f2-4f3b-85bb-30ff1f953599");

    private long _changedAt; // Environment.TickCount64 of the last change; 0: none
    private IntPtr _registration;
    private NotifyCallback? _callback; // kept: Windows calls it for as long as the registration lives

    /// <summary>A graphics card's driver went or came since the agent started: its readings must not be asked for.</summary>
    public bool Changed => Volatile.Read(ref _changedAt) != 0;

    /// <summary>Changed, and nothing more for <see cref="SettleMs"/>: time to start again.</summary>
    public bool Settled(long tick) => Volatile.Read(ref _changedAt) is var at and not 0 && tick - at >= SettleMs;

    internal void Note(long tick) => Volatile.Write(ref _changedAt, Math.Max(tick, 1));

    /// <summary>
    /// Whether a display adapter's device path is a real card's (on the PCI bus, as built-in graphics are too). Virtual
    /// screens (remote desktop, streaming, a headset's link) come and go as "ROOT" or "SWD" devices all day long.
    /// </summary>
    internal static bool IsGraphicsCard(string? devicePath) =>
        devicePath is not null && devicePath.StartsWith(@"\\?\PCI#", StringComparison.OrdinalIgnoreCase);

    public void Start()
    {
        try
        {
            var filter = new NotifyFilter { Size = Marshal.SizeOf<NotifyFilter>(), ClassGuid = DisplayAdapters };
            _callback = OnNotify;
            int result = CM_Register_Notification(ref filter, IntPtr.Zero, _callback, out _registration);
            if (result != 0) Log.Write("sensors", $"Graphics driver changes can't be watched for (error {result})");
        }
        catch (Exception ex)
        {
            Log.Error("sensors", ex);
        }
    }

    private int OnNotify(IntPtr notify, IntPtr context, int action, IntPtr data, int size)
    {
        // Arrival (0) or removal (1) of a display adapter; its path follows the filter type, a reserved field and the class.
        if (action is 0 or 1 && size > PathOffset && IsGraphicsCard(Marshal.PtrToStringUni(data + PathOffset)))
            Note(Environment.TickCount64);
        return 0;
    }

    public void Dispose()
    {
        if (_registration != IntPtr.Zero) CM_Unregister_Notification(_registration);
        _registration = IntPtr.Zero;
    }

    private const int PathOffset = 24;

    // CM_NOTIFY_FILTER for device interfaces: the rest of its union (a device's id, 200 characters) is left empty.
    [StructLayout(LayoutKind.Sequential, Size = 416)]
    private struct NotifyFilter
    {
        public int Size, Flags, FilterType, Reserved;
        public Guid ClassGuid;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int NotifyCallback(IntPtr notify, IntPtr context, int action, IntPtr data, int size);

    [DllImport("cfgmgr32.dll")] private static extern int CM_Register_Notification(ref NotifyFilter filter, IntPtr context, NotifyCallback callback, out IntPtr registration);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Unregister_Notification(IntPtr registration);
}
