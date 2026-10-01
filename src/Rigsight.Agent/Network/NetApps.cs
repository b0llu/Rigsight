using Rigsight.Agent.Native;
using Rigsight.Agent.Tracking;
using Rigsight.Core;

namespace Rigsight.Agent.Network;

/// <summary>
/// Which app each process ID that used the network belongs to. A Windows service host is named after the service inside
/// it ("Delivery Optimization", "Windows Update"), not "Service Host"; Windows' own kernel traffic (file sharing, some
/// VPNs) is "System". Sampler thread only.
/// </summary>
internal sealed class NetApps(AppResolver apps)
{
    private readonly Dictionary<int, AppInfo?> _byPid = [];

    /// <summary>A process's command line; tests give their own.</summary>
    internal Func<int, string?> CommandLine { get; init; } = Win32.ProcessCommandLine;

    /// <summary>
    /// The app a process belongs to, or null when it's gone before it could be looked at, or is one of the apps left out of
    /// tracking (<paramref name="excluded"/>: no record is made for it).
    /// </summary>
    public AppInfo? Of(int pid, IReadOnlyDictionary<int, string> running, Func<string, bool> excluded)
    {
        if (_byPid.TryGetValue(pid, out var known)) return known;
        AppInfo? app = null;
        try
        {
            if (pid == 4) app = apps.Get("System", 4);
            else if (pid > 4)
            {
                string? exe = running.TryGetValue(pid, out var e) ? e : Win32.ProcessPath(pid) is { } path ? Path.GetFileName(path) : null;
                if (exe is not null && excluded(exe)) return null;
                if (exe is not null)
                    app = exe.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase) && ServiceOf(pid) is { } service
                        ? apps.GetService(service, pid)
                        : apps.Get(exe, pid);
            }
        }
        catch (Exception ex)
        {
            Log.Error("network", ex);
        }
        // An unknown process is looked at again next time (it may just have started); a known one is kept.
        if (app is not null) _byPid[pid] = app;
        return app;
    }

    /// <summary>Forgets processes that have ended, so a reused process ID is looked at afresh.</summary>
    public void Prune(IReadOnlyDictionary<int, string> running)
    {
        foreach (int pid in _byPid.Keys.ToList())
            if (pid != 4 && (!running.TryGetValue(pid, out var exe) || !SameExe(_byPid[pid], exe))) _byPid.Remove(pid);
    }

    private static bool SameExe(AppInfo? app, string exe) =>
        app is not null && (app.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase) || app.Exe.StartsWith(exe + ":", StringComparison.OrdinalIgnoreCase));

    /// <summary>The service a service host runs: "svchost.exe -k netsvcs -p -s DoSvc" → "DoSvc" (null: several, or unreadable).</summary>
    private string? ServiceOf(int pid)
    {
        var commandLine = CommandLine(pid);
        if (commandLine is null) return null;
        int i = commandLine.IndexOf("-s ", StringComparison.Ordinal);
        if (i < 0) return null;
        var rest = commandLine[(i + 3)..].Trim();
        int end = rest.IndexOf(' ');
        var service = (end < 0 ? rest : rest[..end]).Trim('"');
        return service.Length > 0 ? service : null;
    }
}
