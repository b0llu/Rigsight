using System.Diagnostics;
using System.Runtime.InteropServices;
using Rigsight.Agent.Native;
using Rigsight.Core.Sound;

namespace Rigsight.Agent.Sound;

/// <summary>An app playing sound right now: its process, its file ("Spotify.exe") and the output it plays on ("Speakers (Realtek(R) Audio)").</summary>
internal readonly record struct PlayingApp(int Pid, string Exe, string? Device);

/// <summary>The answer to "which app was playing" asked at <paramref name="AtMs"/> (Unix ms): the app picked (Exe null when none could be), the output, how many were playing.</summary>
internal sealed record SoundAttribution(long AtMs, int Pid, string? Exe, string? Device, int Playing);

/// <summary>
/// Which apps are playing sound (sound dropouts, 2026-11-22), from Windows' own audio sessions: every active output
/// (IMMDeviceEnumerator, render endpoints that are active), its sessions (IAudioSessionManager2 → IAudioSessionEnumerator),
/// and of those the ones playing now (IAudioSessionControl2: state active), with their process and the output's name.
/// RigSight's own and Windows' system sounds are left out. Called on an MTA thread; everything is released before it returns.
/// </summary>
internal static class SoundSessions
{
    /// <summary>Every app playing now, by output (none when Windows' audio can't be asked).</summary>
    public static List<PlayingApp> Playing()
    {
        var list = new List<PlayingApp>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDeviceCollection? devices = null;
        try
        {
            if (enumerator.EnumAudioEndpoints(EDataFlowRender, DeviceStateActive, out devices) != 0 || devices is null) return list;
            devices.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                try
                {
                    if (devices.Item(i, out device) != 0 || device is null) continue;
                    AddSessions(device, list);
                }
                catch (Exception ex) { Rigsight.Core.Log.Error("sound", ex); }
                finally { Release(device); }
            }
        }
        finally
        {
            Release(devices);
            Release(enumerator);
        }
        return list;
    }

    private static void AddSessions(IMMDevice device, List<PlayingApp> list)
    {
        object? o = null;
        IAudioSessionEnumerator? sessions = null;
        string? name = null;
        try
        {
            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out o) != 0 || o is not IAudioSessionManager2 manager) return;
            if (manager.GetSessionEnumerator(out sessions) != 0 || sessions is null) return;
            sessions.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl? control = null;
                try
                {
                    if (sessions.GetSession(i, out control) != 0 || control is not IAudioSessionControl2 c2) continue;
                    if (c2.GetState(out int state) != 0 || state != AudioSessionStateActive) continue;
                    if (c2.IsSystemSoundsSession() == 0) continue; // S_OK: Windows' system sounds
                    c2.GetProcessId(out uint pid);
                    if (pid == 0 || pid == Environment.ProcessId) continue;
                    if (ExeOf((int)pid) is not { } exe || exe.StartsWith("Rigsight", StringComparison.OrdinalIgnoreCase)) continue;
                    name ??= FriendlyName(device);
                    list.Add(new PlayingApp((int)pid, exe, name));
                }
                finally { Release(control); }
            }
        }
        finally
        {
            Release(sessions);
            Release(o);
        }
    }

    /// <summary>
    /// The app the glitch was most likely heard in: the one app playing, or of several the one in front; none otherwise.
    /// The output is the app's, or the one output every playing app shares.
    /// </summary>
    public static (int Pid, string? Exe, string? Device) Pick(IReadOnlyList<PlayingApp> playing, int foregroundPid)
    {
        var apps = playing.GroupBy(p => p.Exe, StringComparer.OrdinalIgnoreCase).ToList();
        string? shared = playing.Select(p => p.Device).Distinct().Count() == 1 ? playing[0].Device : null;
        if (apps.Count == 1) return (apps[0].First().Pid, apps[0].Key, shared ?? apps[0].First().Device);
        if (apps.Count > 1 && playing.FirstOrDefault(p => p.Pid == foregroundPid) is { Exe: not null } front && front.Pid != 0)
            return (front.Pid, front.Exe, front.Device);
        return (0, null, shared);
    }

    /// <summary>The answer asked nearest to <paramref name="atMs"/>, within <see cref="SoundRules.AttributeWithinMs"/>; null when none was.</summary>
    public static SoundAttribution? Nearest(IReadOnlyList<SoundAttribution> asked, long atMs) =>
        asked.Where(a => Math.Abs(a.AtMs - atMs) <= SoundRules.AttributeWithinMs).MinBy(a => Math.Abs(a.AtMs - atMs));

    /// <summary>The process of the window in front (0 when none).</summary>
    public static int ForegroundPid()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        Win32.GetWindowThreadProcessId(hwnd, out int pid);
        return pid;
    }

    private static string? ExeOf(int pid)
    {
        if (Win32.ProcessPath(pid) is { } path) return Path.GetFileName(path);
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName + ".exe";
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static string? FriendlyName(IMMDevice device)
    {
        IPropertyStore? store = null;
        var value = default(PropVariant);
        try
        {
            if (device.OpenPropertyStore(StgmRead, out store) != 0 || store is null) return null;
            var key = FriendlyNameKey;
            if (store.GetValue(ref key, out value) != 0) return null;
            return value.Vt == VtLpwstr && value.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.Pointer) : null;
        }
        finally
        {
            PropVariantClear(ref value);
            Release(store);
        }
    }

    private static void Release(object? o)
    {
        if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }

    // ── Windows (mmdeviceapi.h, audiopolicy.h, propsys.h) ─────────────────

    private const int EDataFlowRender = 0;
    private const uint DeviceStateActive = 0x1, ClsCtxAll = 0x17, StgmRead = 0;
    private const int AudioSessionStateActive = 1;
    private const ushort VtLpwstr = 31;

    /// <summary>PKEY_Device_FriendlyName: {A45C254E-DF1C-4EFD-8020-67D146A850E0}, 14.</summary>
    private static readonly PropertyKey FriendlyNameKey = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid fmtid, uint pid)
    {
        public Guid FmtId = fmtid;
        public uint Pid = pid;
    }

    /// <summary>PROPVARIANT (x64, 24 bytes): vt, three reserved words, then the value (a string's pointer for VT_LPWSTR).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant pv);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
        // GetDefaultAudioEndpoint, GetDevice, RegisterEndpointNotificationCallback, UnregisterEndpointNotificationCallback: not used.
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
        // GetId, GetState: not used.
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        // SetValue, Commit: not used.
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager's two first, in order.
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        // RegisterSessionNotification and the rest: not used.
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl's nine first, in order.
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, IntPtr eventContext);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(IntPtr grouping, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        // Its own.
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        /// <summary>AUDCLNT_S_NO_SINGLE_PROCESS (a success) for a session several processes share; the pid is still given.</summary>
        [PreserveSig] int GetProcessId(out uint pid);
        /// <summary>S_OK (0) for Windows' system sounds session, S_FALSE (1) otherwise.</summary>
        [PreserveSig] int IsSystemSoundsSession();
    }
}
