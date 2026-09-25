using System.Text;
using Microsoft.Win32;
using Rigsight.Agent.Native;
using Rigsight.Core.Protocol;

namespace Rigsight.Agent.Tracking;

/// <summary>
/// Says what each process of an app is, for the Memory page: its window's title if it has one, else its role read from
/// its command line (a browser's tabs, graphics and network processes, the services inside a service host). Sampler
/// thread only.
/// </summary>
internal sealed class ProcessLabels
{
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "brave.exe", "opera.exe", "opera_gx.exe", "vivaldi.exe", "arc.exe", "firefox.exe", "zen.exe",
    };

    // Roles by process, read once per process (a command line never changes).
    private readonly Dictionary<(int Pid, long Created), string?> _roles = [];
    private readonly Dictionary<string, string> _services = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A process's command line (null if it can't be read). Tests give their own.</summary>
    internal Func<int, string?> CommandLine { get; init; } = Win32.ProcessCommandLine;

    /// <summary>The app's processes, biggest first. <paramref name="titles"/> is filled on first use.</summary>
    public List<ProcDetail> Describe(string exe, string appName, List<ProcessUsage> processes, ref Dictionary<int, string>? titles)
    {
        titles ??= WindowTitles();
        if (_roles.Count > 4000) _roles.Clear();

        var roles = processes.Select(p =>
        {
            if (!_roles.TryGetValue((p.Pid, p.Created), out var role))
                _roles[(p.Pid, p.Created)] = role = Role(exe, CommandLine(p.Pid), ServiceName);
            return role;
        }).ToList();
        // In a browser-style app the one process without a role is the one that runs the rest. (A service host
        // whose command line can't be read is just a service.)
        bool serviceHost = exe.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase);
        bool multiProcess = !serviceHost && roles.Any(r => r is not null);

        var result = new List<ProcDetail>(processes.Count);
        for (int i = 0; i < processes.Count; i++)
        {
            var p = processes[i];
            string label = titles.TryGetValue(p.Pid, out var title) ? title
                : roles[i] ?? (multiProcess ? "Main process" : serviceHost ? "Windows service" : appName);
            result.Add(new ProcDetail { Pid = p.Pid, Label = label, Cpu = Math.Round(p.Cpu, 1), MemMB = Math.Round(p.MemMB, 1) });
        }
        result.Sort((a, b) => b.MemMB.CompareTo(a.MemMB));
        return result;
    }

    /// <summary>What a process of <paramref name="exe"/> does, from its command line (null: nothing says).</summary>
    internal static string? Role(string exe, string? commandLine, Func<string, string> serviceName)
    {
        if (commandLine is null) return null;

        // Chrome, Edge and every Electron app (Discord, VS Code, Spotify…)
        if (Arg(commandLine, "--type=") is { } type)
        {
            return type switch
            {
                "renderer" when commandLine.Contains("--extension-process") => "Extension",
                "renderer" => Browsers.Contains(exe) ? "Tab" : "Window",
                "gpu-process" => "Graphics",
                "crashpad-handler" => "Crash reporter",
                "utility" => Arg(commandLine, "--utility-sub-type=") switch
                {
                    { } s when s.StartsWith("network.", StringComparison.Ordinal) => "Network",
                    { } s when s.StartsWith("storage.", StringComparison.Ordinal) => "Storage",
                    { } s when s.StartsWith("audio.", StringComparison.Ordinal) => "Audio",
                    { } s when s.StartsWith("video_capture.", StringComparison.Ordinal) => "Camera",
                    _ => "Helper",
                },
                _ => "Helper",
            };
        }

        // Firefox: "-contentproc … tab"
        if (commandLine.Contains(" -contentproc", StringComparison.Ordinal))
        {
            return commandLine.TrimEnd().Split(' ')[^1] switch
            {
                "tab" => "Tab",
                "gpu" => "Graphics",
                "socket" => "Network",
                "rdd" => "Media",
                _ => "Helper",
            };
        }

        // Service hosts: "svchost.exe -k netsvcs -p -s Schedule"
        if (exe.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
        {
            if (Arg(commandLine, "-s ") is { } service) return serviceName(service);
            if (Arg(commandLine, "-k ") is { } group) return $"Services ({group})";
        }
        return null;
    }

    /// <summary>The value after <paramref name="name"/>, up to the next space (null if absent).</summary>
    private static string? Arg(string commandLine, string name)
    {
        int i = commandLine.IndexOf(name, StringComparison.Ordinal);
        if (i < 0) return null;
        i += name.Length;
        int end = commandLine.IndexOf(' ', i);
        var value = (end < 0 ? commandLine[i..] : commandLine[i..end]).Trim('"');
        return value.Length > 0 ? value : null;
    }

    /// <summary>A service's display name ("Task Scheduler" for "Schedule").</summary>
    private string ServiceName(string service)
    {
        if (_services.TryGetValue(service, out var name)) return name;
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
        return _services[service] = name;
    }

    /// <summary>Titles of the visible top-level windows, by process.</summary>
    private static Dictionary<int, string> WindowTitles()
    {
        var titles = new Dictionary<int, string>();
        var text = new StringBuilder(256);
        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd) || Win32.GetWindow(hwnd, Win32.GW_OWNER) != IntPtr.Zero) return true;
            if (Win32.GetWindowTextLength(hwnd) == 0) return true;
            Win32.GetWindowThreadProcessId(hwnd, out int pid);
            text.Clear();
            if (!titles.ContainsKey(pid) && Win32.GetWindowText(hwnd, text, text.Capacity) > 0) titles[pid] = text.ToString();
            return true;
        }, IntPtr.Zero);
        return titles;
    }
}
