using System.Text;
using Microsoft.Win32;
using Rigsight.Agent.Native;

namespace Rigsight.Agent.Tracking;

/// <summary>Windows services' display names ("Task Scheduler" for "Schedule"), read once each. Sampler thread only.</summary>
internal static class ServiceNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);

    public static string Display(string service)
    {
        if (Names.TryGetValue(service, out var name)) return name;
        name = service;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}");
            if (key?.GetValue("DisplayName") is string display && display.Length > 0)
            {
                if (!display.StartsWith('@')) name = display;
                else
                {
                    // "@%SystemRoot%\system32\schedsvc.dll,-100": a string inside a Windows file.
                    var text = new StringBuilder(256);
                    if (Win32.SHLoadIndirectString(display, text, text.Capacity, IntPtr.Zero) == 0 && text.Length > 0) name = text.ToString();
                }
            }
        }
        catch
        {
            // Not readable: the short name will do.
        }
        return Names[service] = name;
    }
}
