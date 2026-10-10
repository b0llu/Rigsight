using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Rigsight.Services;

/// <summary>How ending an app or a process went.</summary>
public enum EndOutcome
{
    Ended,
    /// <summary>It had closed by itself: there was nothing to end.</summary>
    Gone,
    /// <summary>Windows wouldn't end it, with admin rights either.</summary>
    Refused,
    /// <summary>It needs admin rights and Windows' prompt for them was turned down.</summary>
    Cancelled,
}

/// <summary>What Windows said to ending one process.</summary>
public enum KillResult { Ended, Gone, Denied, Failed }

/// <summary>How asking Windows for admin rights went.</summary>
public enum Elevation { Done, Cancelled, Failed }

/// <summary>
/// Ends apps and processes for the Processes page, from this window: the agent has admin rights and is never handed a
/// "end this process" command. What Windows refuses this window is tried once more through Windows' own taskkill with
/// admin rights, which shows the usual permission prompt. Every step that touches a process is a function a test
/// replaces, so no test ends anything.
/// </summary>
public sealed class TaskEnder
{
    /// <summary>The IDs of every process of an exe ("chrome.exe").</summary>
    public Func<string, IReadOnlyList<int>> PidsOf { get; set; } = RunningPids;

    /// <summary>Ends one process, if it is still the exe it was listed as.</summary>
    public Func<int, string, KillResult> Kill { get; set; } = KillProcess;

    /// <summary>Ends processes with admin rights (one prompt for all of them), each only if it is still the exe it was
    /// listed as.</summary>
    public Func<IReadOnlyList<(int Pid, string Exe)>, Elevation> Elevate { get; set; } = KillElevated;

    /// <summary>Whether a process is still there, and still the exe it was listed as.</summary>
    public Func<int, string, bool> IsRunning { get; set; } = StillRunning;

    /// <summary>Starts a program (Windows Explorer, after ending it to restart it).</summary>
    public Action<string> Start { get; set; } = exe => Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();

    /// <summary>
    /// Ends each target: an app (every process of its exe) or one process of it. The outcomes come back in the same
    /// order. Runs off the window's thread: it waits for the processes to go, and for the permission prompt.
    /// </summary>
    public IReadOnlyList<EndOutcome> End(IReadOnlyList<(string Exe, int? Pid)> targets)
    {
        int self = Environment.ProcessId;
        var ended = new bool[targets.Count];
        var failed = new bool[targets.Count];
        var denied = new List<(int Target, int Pid)>();
        // This window's own process last of all: once it has gone, nothing after it would be ended.
        var all = new List<(int Target, int Pid)>();
        for (int i = 0; i < targets.Count; i++)
            foreach (int id in targets[i].Pid is { } one ? [one] : PidsOf(targets[i].Exe)) all.Add((i, id));
        foreach (var (i, id) in all.OrderBy(p => p.Pid == self))
        {
            switch (Kill(id, targets[i].Exe))
            {
                case KillResult.Ended: ended[i] = true; break;
                case KillResult.Denied: denied.Add((i, id)); break;
                case KillResult.Failed: failed[i] = true; break;
            }
        }

        bool cancelled = false;
        if (denied.Count > 0)
        {
            cancelled = Elevate([.. denied.Select(d => (d.Pid, targets[d.Target].Exe))]) == Elevation.Cancelled;
            // What counts is whether they have gone, whatever taskkill made of it.
            foreach (var (target, id) in denied)
            {
                if (cancelled || IsRunning(id, targets[target].Exe)) failed[target] = true;
                else ended[target] = true;
            }
        }

        var outcomes = new EndOutcome[targets.Count];
        for (int i = 0; i < outcomes.Length; i++)
            outcomes[i] = failed[i] ? (cancelled && denied.Any(d => d.Target == i) ? EndOutcome.Cancelled : EndOutcome.Refused) : ended[i] ? EndOutcome.Ended : EndOutcome.Gone;
        return outcomes;
    }

    /// <summary>
    /// Ends Windows Explorer and starts it again. Windows sometimes starts it again by itself: then it is left at that
    /// (a second start would only open a folder window).
    /// </summary>
    public EndOutcome Restart(string exe)
    {
        var outcome = End([(exe, null)])[0];
        if (outcome is EndOutcome.Refused or EndOutcome.Cancelled) return outcome;
        Thread.Sleep(RestartPause);
        if (PidsOf(exe).Count == 0) Start(exe);
        return EndOutcome.Ended;
    }

    /// <summary>How long Windows is given to start Explorer again by itself (tests wait for nothing).</summary>
    internal TimeSpan RestartPause { get; set; } = TimeSpan.FromMilliseconds(800);

    // ── What the functions above do unless a test says otherwise ──

    /// <summary>"chrome.exe" as Windows names its processes: without the ".exe".</summary>
    internal static string ProcessName(string exe) => exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;

    private static IReadOnlyList<int> RunningPids(string exe)
    {
        var pids = new List<int>();
        foreach (var process in Process.GetProcessesByName(ProcessName(exe)))
        {
            pids.Add(process.Id);
            process.Dispose();
        }
        return pids;
    }

    /// <summary>A process ID is given to another process once its own has gone: only the exe it was listed as counts.</summary>
    private static bool StillRunning(int pid, string exe)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName.Equals(ProcessName(exe), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private const uint PROCESS_TERMINATE = 0x0001, SYNCHRONIZE = 0x00100000;
    private const int ERROR_ACCESS_DENIED = 5, ERROR_INVALID_PARAMETER = 87, ERROR_CANCELLED = 1223;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Ends it the way Task Manager's "End task" does (exit code 1), and waits a moment for it to go, so what is said
    /// afterwards is true. Windows doesn't start Explorer again by itself after that exit code, as it does after a crash.
    /// </summary>
    private static KillResult KillProcess(int pid, string exe)
    {
        if (!StillRunning(pid, exe)) return KillResult.Gone;
        IntPtr handle = OpenProcess(PROCESS_TERMINATE | SYNCHRONIZE, false, pid);
        if (handle == IntPtr.Zero)
        {
            return Marshal.GetLastWin32Error() switch
            {
                ERROR_ACCESS_DENIED => KillResult.Denied,
                ERROR_INVALID_PARAMETER => KillResult.Gone, // no such process any more
                _ => KillResult.Failed,
            };
        }
        try
        {
            if (!TerminateProcess(handle, 1))
                return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED ? (StillRunning(pid, exe) ? KillResult.Denied : KillResult.Gone) : KillResult.Failed;
            WaitForSingleObject(handle, 3000);
            return KillResult.Ended;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// What Windows is asked to run with admin rights: its own taskkill, once for each exe, each told to end only
    /// processes of that name. The permission prompt can stay open for any length of time, and a process ID whose
    /// process went meanwhile may by then belong to another program: that one is left alone. Several exes go through
    /// one command line (so one prompt), and only names with nothing in them that a command line reads as its own
    /// (<see cref="PlainName"/>); a process of any other name is left out, and is reported as not ended.
    /// </summary>
    internal static (string File, string Arguments)? ElevatedCommand(IReadOnlyList<(int Pid, string Exe)> pids)
    {
        string taskkill = Path.Combine(Environment.SystemDirectory, "taskkill.exe");
        var calls = pids.Where(p => PlainName(p.Exe)).GroupBy(p => p.Exe, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"/F {string.Join(' ', g.Select(p => $"/PID {p.Pid}"))} /FI \"IMAGENAME eq {g.Key}\"").ToList();
        return calls.Count switch
        {
            0 => null,
            1 => (taskkill, calls[0]),
            _ => (Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c \"{string.Join(" & ", calls.Select(c => $"\"{taskkill}\" {c}"))}\""),
        };
    }

    /// <summary>Letters, digits, spaces and . _ - + ( ) only.</summary>
    internal static bool PlainName(string exe) => exe.Length is > 0 and <= 260 && exe.All(c => char.IsAsciiLetterOrDigit(c) || " ._-+()".Contains(c));

    private static Elevation KillElevated(IReadOnlyList<(int Pid, string Exe)> pids)
    {
        if (ElevatedCommand(pids) is not { } command) return Elevation.Failed;
        try
        {
            using var taskkill = Process.Start(new ProcessStartInfo
            {
                FileName = command.File,
                Arguments = command.Arguments,
                UseShellExecute = true, // "runas" is ShellExecute's: it is what shows the prompt
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (taskkill is null) return Elevation.Failed;
            if (!taskkill.WaitForExit(15_000)) return Elevation.Failed;
            // A moment for the last of them to be gone from Windows' list.
            Thread.Sleep(150);
            return Elevation.Done;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            return Elevation.Cancelled;
        }
        catch (Exception ex)
        {
            Core.Log.Error("processes", ex);
            return Elevation.Failed;
        }
    }
}
