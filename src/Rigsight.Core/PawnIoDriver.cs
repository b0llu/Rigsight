using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Rigsight.Core;

/// <summary>
/// The PawnIO driver, which CPU and motherboard readings come through: whether it is on this PC and whether Windows has
/// it running. Asked by the agent (to warn in Settings and say so in the log) and by the window (for the copied logs).
/// </summary>
public static class PawnIoDriver
{
    public const string Service = "PawnIO";

    /// <summary>Where the driver is downloaded from by hand.</summary>
    public const string Site = "https://pawnio.eu/";

    /// <summary>Its own files are there (the same test as setup's: its uninstaller can leave the registry and the service behind).</summary>
    public static bool Installed => File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll"));

    /// <summary>The installed version as Windows lists it; null when it isn't listed.</summary>
    public static string? Version
    {
        get
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                return key?.GetValue("DisplayVersion") as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Whether Windows checks drivers in its isolated core ("Memory integrity"), which refuses some; null when it doesn't say.
    /// </summary>
    public static bool? MemoryIntegrity
    {
        get
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
                return key?.GetValue("Enabled") is int on ? on != 0 : null;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return null;
            }
        }
    }

    public enum State
    {
        /// <summary>Windows has no such service.</summary>
        NoService,
        Stopped,
        Running,
        /// <summary>Starting, stopping, or it couldn't be asked.</summary>
        Other,
    }

    /// <summary>What Windows says the driver is doing right now. Needs no admin rights.</summary>
    public static State Running()
    {
        nint manager = OpenSCManagerW(null, null, ScConnect);
        if (manager == 0) return State.Other;
        try
        {
            nint service = OpenServiceW(manager, Service, ServiceQueryStatus);
            if (service == 0) return Marshal.GetLastWin32Error() == NoSuchService ? State.NoService : State.Other;
            try
            {
                return !QueryServiceStatus(service, out var status) ? State.Other : status.CurrentState switch
                {
                    1 => State.Stopped,
                    4 => State.Running,
                    _ => State.Other,
                };
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    /// <summary>
    /// Asks Windows to start the driver (admin rights). Null when it is running afterwards; otherwise why not, in Windows'
    /// own words with its number ("1275: This driver has been blocked from loading"), which is what tells a blocked
    /// driver from a broken install.
    /// </summary>
    public static string? Start()
    {
        nint manager = OpenSCManagerW(null, null, ScConnect);
        if (manager == 0) return Why(Marshal.GetLastWin32Error());
        try
        {
            nint service = OpenServiceW(manager, Service, ServiceStart);
            if (service == 0) return Why(Marshal.GetLastWin32Error());
            try
            {
                if (StartServiceW(service, 0, 0)) return null;
                int error = Marshal.GetLastWin32Error();
                return error == AlreadyRunning ? null : Why(error);
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static string Why(int error) => $"{error}: {new Win32Exception(error).Message}";

    /// <summary>"installed 2.2.0.0, running" · "not installed" · "installed, stopped" · "not installed (its service is left behind, stopped)".</summary>
    public static string Describe() => Describe(Installed, Version, Running());

    internal static string Describe(bool installed, string? version, State state)
    {
        string doing = state switch
        {
            State.Running => "running",
            State.Stopped => "stopped",
            State.NoService => "no service in Windows",
            _ => "state unknown",
        };
        if (installed) return $"installed{(version is null ? "" : " " + version)}, {doing}";
        return state == State.NoService ? "not installed" : $"not installed (its service is left behind, {doing})";
    }

    private const int ScConnect = 1, ServiceQueryStatus = 4, ServiceStart = 0x10, NoSuchService = 1060, AlreadyRunning = 1056;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenSCManagerW(string? machine, string? database, int access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenServiceW(nint manager, string name, int access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(nint service, out ServiceStatus status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool StartServiceW(nint service, int count, nint args);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(nint handle);
}
