using System.Runtime.InteropServices;
using Rigsight.Core;

namespace Rigsight.Agent.Ui;

/// <summary>
/// Whether the screen is off (Windows turned the display off after its idle time, or the PC's lid is shut on its only
/// screen): nobody sees the taskbar readings or the widgets then, so the agent stops drawing them, and stops reading
/// sensors only they show, until it comes back. Recording goes on as ever. Windows tells this as it happens (the
/// console display state: off, on or dimmed; dimmed counts as on, it's still seen), on a thread of its own; the first
/// answer comes when watching starts. Nothing is ever paused if Windows won't say.
/// </summary>
internal sealed class ScreenState : IDisposable
{
    private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    private const uint DeviceNotifyCallback = 2, PowerSettingChange = 0x8013;

    private readonly Action<bool> _changed;
    private readonly DeviceNotifyCallbackRoutine _callback; // kept: Windows calls it for as long as the watch lasts
    private IntPtr _registration;
    private volatile bool _off;
    private bool _heard;

    /// <param name="changed">Told on Windows' thread when the screen goes off (true) or comes back (false).</param>
    public ScreenState(Action<bool> changed)
    {
        _changed = changed;
        _callback = OnSetting;
        try
        {
            var parameters = new SubscribeParameters { Callback = Marshal.GetFunctionPointerForDelegate(_callback) };
            var guid = ConsoleDisplayState;
            uint error = PowerSettingRegisterNotification(ref guid, DeviceNotifyCallback, ref parameters, out _registration);
            if (error != 0)
            {
                _registration = IntPtr.Zero;
                Log.Write("screen", $"Expected Windows to say when the screen goes off, found error {error}: the taskbar readings and widgets are drawn whatever the screen does");
            }
        }
        catch (Exception ex)
        {
            Log.Error("screen", ex);
        }
    }

    /// <summary>Whether the screen is off right now (false until Windows has said otherwise).</summary>
    public bool Off => _off;

    /// <summary>Whether a console display state means nobody can see the screen: 0 is off; 1 on and 2 dimmed are seen.</summary>
    internal static bool IsOff(uint state) => state == 0;

    private uint OnSetting(IntPtr context, uint type, IntPtr setting)
    {
        // Called from Windows' own code: nothing may escape.
        try
        {
            // POWERBROADCAST_SETTING: the setting's GUID, the data's length, then the data (a DWORD here).
            if (type != PowerSettingChange || setting == IntPtr.Zero || Marshal.PtrToStructure<Guid>(setting) != ConsoleDisplayState
                || Marshal.ReadInt32(setting, 16) < 4) return 0;
            uint state = (uint)Marshal.ReadInt32(setting, 20);
            bool off = IsOff(state);
            // The first answer comes as the watch starts: logged, so the log shows the watch works.
            if (!_heard)
            {
                _heard = true;
                Log.Write("screen", $"Watching whether the screen is on (it is {(state switch { 0 => "off", 2 => "dimmed", _ => "on" })} now)");
            }
            if (off == _off) return 0;
            _off = off;
            _changed(off);
        }
        catch (Exception ex)
        {
            Log.Error("screen", ex);
        }
        return 0;
    }

    public void Dispose()
    {
        if (_registration == IntPtr.Zero) return;
        try { PowerSettingUnregisterNotification(_registration); } catch { }
        _registration = IntPtr.Zero;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint DeviceNotifyCallbackRoutine(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct SubscribeParameters { public IntPtr Callback; public IntPtr Context; }

    [DllImport("powrprof.dll")]
    private static extern uint PowerSettingRegisterNotification(ref Guid setting, uint flags, ref SubscribeParameters recipient, out IntPtr registration);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSettingUnregisterNotification(IntPtr registration);
}
