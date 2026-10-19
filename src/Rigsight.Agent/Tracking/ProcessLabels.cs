using System.Text;
using Rigsight.Agent.Native;
using Rigsight.Core;
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

    /// <summary>The kind of the one process that runs the rest.</summary>
    internal const string MainKind = "Main";

    // Roles by process, read once per process (a command line never changes).
    private readonly Dictionary<(int Pid, long Created), string?> _roles = [];

    /// <summary>A process's command line (null if it can't be read). Tests give their own.</summary>
    internal Func<int, string?> CommandLine { get; init; } = Win32.ProcessCommandLine;

    /// <summary>The app's processes, biggest first. <paramref name="titles"/> is filled on first use.</summary>
    public List<ProcDetail> Describe(string exe, string appName, List<ProcessUsage> processes, ref Dictionary<int, string>? titles, HashSet<int>? marked = null)
    {
        titles ??= WindowTitles();
        if (_roles.Count > 4000) _roles.Clear();

        var roles = processes.Select(p =>
        {
            if (!_roles.TryGetValue((p.Pid, p.Created), out var role))
                _roles[(p.Pid, p.Created)] = role = Role(exe, CommandLine(p.Pid), ServiceNames.Display);
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
                : roles[i] is { } role ? Shown(role) : multiProcess ? Lang.T("Main process") : serviceHost ? Lang.T("Windows service") : appName;
            // Its part in a browser-style app, for the split of the app's memory (a service host's services aren't parts).
            string? kind = multiProcess ? roles[i] ?? MainKind : null;
            result.Add(new ProcDetail { Pid = p.Pid, Label = label, Kind = kind, Cpu = Math.Round(p.Cpu, 1), MemMB = Math.Round(p.MemMB, 1), Disk = Math.Round(p.Disk), Critical = marked?.Contains(p.Pid) == true });
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
                "renderer" when commandLine.Contains("--extension-process") => Lang.Key("Extension"),
                "renderer" => Browsers.Contains(exe) ? Lang.Key("Tab") : Lang.Key("Window"),
                "gpu-process" => Lang.Key("Graphics"),
                "crashpad-handler" => Lang.Key("Crash reporter"),
                "utility" => Arg(commandLine, "--utility-sub-type=") switch
                {
                    { } s when s.StartsWith("network.", StringComparison.Ordinal) => Lang.Key("Network"),
                    { } s when s.StartsWith("storage.", StringComparison.Ordinal) => Lang.Key("Storage"),
                    { } s when s.StartsWith("audio.", StringComparison.Ordinal) => Lang.Key("Audio"),
                    { } s when s.StartsWith("video_capture.", StringComparison.Ordinal) => Lang.Key("Camera"),
                    _ => Lang.Key("Helper"),
                },
                _ => Lang.Key("Helper"),
            };
        }

        // Firefox: "-contentproc … tab"
        if (commandLine.Contains(" -contentproc", StringComparison.Ordinal))
        {
            return commandLine.TrimEnd().Split(' ')[^1] switch
            {
                "tab" => Lang.Key("Tab"),
                "gpu" => Lang.Key("Graphics"),
                "socket" => Lang.Key("Network"),
                "rdd" => Lang.Key("Media"),
                _ => Lang.Key("Helper"),
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

    /// <summary>
    /// A role as the Memory page shows it, in the language in use; the role itself stays English, since it's also the
    /// process's kind the app sorts by.
    /// </summary>
    private static string Shown(string role)
    {
        if (role.StartsWith("Services (", StringComparison.Ordinal) && role.EndsWith(')'))
        {
            string group = role["Services (".Length..^1];
            return Lang.T($"Services ({group})");
        }
        return Lang.T(role);
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
