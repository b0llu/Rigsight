using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Rigsight.Core;
using Rigsight.Core.Updates;

namespace Rigsight.Agent;

/// <summary>
/// The "Rigsight Updater" task: Windows itself runs it (as the system, no one's account) a few minutes after a sign-in,
/// and when a person's Rigsight asks as it starts or from its window; it installs a newer Rigsight if there is one,
/// and it ends. Never by the clock: an update in the middle of someone's day is what the owner ruled out. It is what keeps Rigsight up to date for accounts
/// that can't install anything, and it only exists where an admin asked for it. It takes nothing from anyone: the
/// version, the download and the check that it is the real installer are all its own.
/// </summary>
internal static class UpdaterTask
{
    public const string Name = RigsightPaths.UpdaterTaskName;

    /// <summary>Asks Windows to run it now. Anyone may: it takes nothing from who asks.</summary>
    public static bool Run() => AgentTask.Schtasks(out _, "/Run", "/TN", Name) == 0;
    public const string Argument = "--system-update";

    public static bool Exists() => AgentTask.Schtasks(out _, "/Query", "/TN", Name) == 0;

    public static bool Enable()
    {
        if (Environment.ProcessPath is not { } exe) return false;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Keeps Rigsight up to date on this PC for everyone who uses it. It only installs Rigsight's own releases.</Description>
                <SecurityDescriptor>D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;BU)</SecurityDescriptor>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <Delay>PT3M</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>S-1-5-18</UserId>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>true</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <Arguments>{Argument}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        // Next to the agent (Program Files), as the start-up task's file is: nowhere another program could swap it.
        var file = Path.Combine(AppContext.BaseDirectory, "Rigsight-updater-task.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            return AgentTask.Schtasks(out _, "/Create", "/TN", Name, "/XML", file, "/F") == 0;
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    public static bool Disable() => !Exists() || AgentTask.Schtasks(out _, "/Delete", "/TN", Name, "/F") == 0;

    /// <summary>Admin only: the choice and the task together. Whether it is now as asked.</summary>
    public static bool Set(bool on)
    {
        bool ok = on ? Enable() : Disable();
        if (ok) MachineSetup.SetKeepUpdated(on);
        return ok;
    }
}

/// <summary>
/// What the updater task runs. Never under an open Rigsight window (the installer closes it) unless its owner pressed
/// "Update now" there, and a version that was tried and didn't take is left for a day before it is tried again.
/// </summary>
internal static class SystemUpdater
{
    public const int UpToDate = 0, Failed = 1, Installing = 3, Later = 5, NotWanted = 6;

    public static async Task<int> RunAsync()
    {
        if (!MachineSetup.KeepUpdated) return NotWanted;
        Release? latest;
        try
        {
            // GitHub is asked at most once a day, unless someone pressed "Update now": anyone may start this task, and
            // each look counts against the PC's allowance there.
            latest = await UpdateStore.LatestAsync(force: SomeoneAskedNow());
        }
        catch (Exception ex)
        {
            Log.Write("update", $"Check failed: {ex.Message}");
            return Failed;
        }
        bool newer = ReleaseFeed.IsNewer(latest);
        UpdateStore.Tidy(newer ? latest : null);
        var attempt = UpdateStore.ReadAttempt();
        if (attempt is not null && attempt.Version <= ReleaseFeed.Current) UpdateStore.ClearAttempt(); // it worked
        // Said every run: this log (the system's own) is the only place that tells what the updater found.
        Log.Write("update", newer ? $"{latest!.Version} is out, this is {ReleaseFeed.Current}" : $"Up to date ({ReleaseFeed.Current})");
        if (!newer) return UpToDate;

        if (attempt is not null && ReleaseFeed.Normalize(attempt.Version) == latest!.Version && DateTime.Now - attempt.At < TimeSpan.FromHours(20))
            return Later;
        var windows = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(RigsightPaths.AppExe));
        foreach (var p in windows) p.Dispose();
        if (windows.Length > 0 && !SomeoneAskedNow())
        {
            Log.Write("update", $"{latest!.Version} is out, and a Rigsight window is open: left for the next run");
            return Later;
        }

        try
        {
            if (await UpdateStore.DownloadedAsync() is null) await UpdateStore.DownloadAsync(latest!);
        }
        catch (Exception ex)
        {
            Log.Write("update", $"Download failed: {ex.Message}");
            return Failed;
        }
        return await UpdateInstaller.RunAsync(ReleaseFeed.BackgroundArguments) ? Installing : Failed;
    }

    /// <summary>
    /// Whether a person on this PC pressed "Update now" a moment ago: a note in their own folder (see UpdateStore.AskNow).
    /// Only its being there is looked at, nothing in it: the most it can do is let the update close an open window.
    /// </summary>
    private static bool SomeoneAskedNow()
    {
        try
        {
            using var profiles = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (profiles is null) return false;
            foreach (var sid in profiles.GetSubKeyNames())
            {
                using var profile = profiles.OpenSubKey(sid);
                if (profile?.GetValue("ProfileImagePath") is not string home) continue;
                if (UpdateStore.AskedNow(Path.Combine(Environment.ExpandEnvironmentVariables(home), @"AppData\Local\Rigsight\updates"))) return true;
            }
        }
        catch (Exception ex)
        {
            Log.Error("update", ex);
        }
        return false;
    }
}

/// <summary>
/// Around an update installed by the system: runs the agent's own program for each person signed in, as that person.
/// Before the files are replaced, to ask each one's agent to save and close (the request only reaches an agent from
/// inside its own sign-in); afterwards, to start it again. Only the system account may do this; from any other it does nothing.
/// </summary>
internal static class SessionStarter
{
    /// <summary>
    /// Starts the agent again for each person whose agent the update closed (see <see cref="QuitAgents"/>'s note), not
    /// for someone signed in who had quit Rigsight or never opened it. Without a note (the version being replaced
    /// didn't leave one), for each person signed in. How many were started.
    /// </summary>
    public static int StartAgents()
    {
        HashSet<int>? only = null;
        try
        {
            if (File.Exists(ClosedNote))
            {
                only = File.ReadAllLines(ClosedNote).Select(l => int.TryParse(l, out int id) ? id : -1).ToHashSet();
                File.Delete(ClosedNote);
            }
        }
        catch (Exception ex)
        {
            Log.Error("setup", ex);
        }
        return RunForEachPerson(MachineSetup.ResumeArgument, waitMs: 0, only);
    }

    /// <summary>The sign-in sessions an agent was running in when the update closed them. Next to the program, where only an admin can write.</summary>
    private static string ClosedNote => Path.Combine(AppContext.BaseDirectory, "closed-for-update.txt");

    /// <summary>
    /// Asks each person's agent to save and close, and waits for them to. The request is an event in each sign-in
    /// session's own names; the system reaches it there directly (a program started in the session with the person's
    /// ordinary rights couldn't signal an agent that runs with admin rights). How many were asked.
    /// </summary>
    public static int QuitAgents()
    {
        int asked = 0;
        // Whose agent is running now: only they get it back after the update.
        try
        {
            var running = Process.GetProcessesByName("Rigsight.Agent");
            var sessions = running.Where(p => p.Id != Environment.ProcessId).Select(p => p.SessionId).Distinct().ToList();
            foreach (var p in running) p.Dispose();
            File.WriteAllLines(ClosedNote, sessions.Select(id => id.ToString()));
        }
        catch (Exception ex)
        {
            Log.Error("setup", ex);
        }
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var list, out int count)) return 0;
        try
        {
            int size = Marshal.SizeOf<SessionInfo>();
            for (int i = 0; i < count; i++)
            {
                var session = Marshal.PtrToStructure<SessionInfo>(list + i * size);
                string name = $@"Session\{session.Id}\{RigsightPaths.AgentQuitEvent[(RigsightPaths.AgentQuitEvent.IndexOf('\\') + 1)..]}";
                try
                {
                    if (!EventWaitHandle.TryOpenExisting(name, out var quit)) continue;
                    using (quit) quit.Set();
                    asked++;
                }
                catch (Exception ex)
                {
                    Log.Error("setup", ex);
                }
            }
        }
        finally
        {
            WTSFreeMemory(list);
        }
        if (asked == 0) return 0;
        // Until they have gone, or ten seconds: the installer ends by force whatever is left.
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            var left = Process.GetProcessesByName("Rigsight.Agent");
            int others = left.Count(p => p.Id != Environment.ProcessId);
            foreach (var p in left) p.Dispose();
            if (others == 0) break;
            Thread.Sleep(250);
        }
        return asked;
    }

    private static int RunForEachPerson(string argument, int waitMs, HashSet<int>? only = null)
    {
        if (Environment.ProcessPath is not { } exe) return 0;
        int started = 0;
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var list, out int count)) return 0;
        try
        {
            int size = Marshal.SizeOf<SessionInfo>();
            for (int i = 0; i < count; i++)
            {
                var session = Marshal.PtrToStructure<SessionInfo>(list + i * size);
                // Signed in and at the screen, or signed in and switched away from (their agent was closed too).
                if (only is not null && !only.Contains(session.Id)) continue;
                if (session.State is not (Active or Disconnected) || !WTSQueryUserToken(session.Id, out var token)) continue;
                try
                {
                    if (RunAs(token, exe, argument, waitMs)) started++;
                }
                finally
                {
                    CloseHandle(token);
                }
            }
        }
        finally
        {
            WTSFreeMemory(list);
        }
        return started;
    }

    private static bool RunAs(IntPtr token, string exe, string argument, int waitMs)
    {
        if (!CreateEnvironmentBlock(out var environment, token, false)) environment = IntPtr.Zero;
        try
        {
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
            var line = new StringBuilder($"\"{exe}\" {argument}");
            if (!CreateProcessAsUser(token, exe, line, IntPtr.Zero, IntPtr.Zero, false, UnicodeEnvironment, environment,
                    Path.GetDirectoryName(exe), ref startup, out var process))
                return false;
            CloseHandle(process.Thread);
            if (waitMs > 0) WaitForSingleObject(process.Process, waitMs);
            CloseHandle(process.Process);
            return true;
        }
        finally
        {
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
        }
    }

    private const int Active = 0, Disconnected = 4; // WTSActive, WTSDisconnected
    private const int UnicodeEnvironment = 0x400;  // CREATE_UNICODE_ENVIRONMENT

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SessionInfo
    {
        public int Id;
        public string Station;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public IntPtr Reserved3, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(int session, out IntPtr token);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUser(IntPtr token, string application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, int flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInfo process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int WaitForSingleObject(IntPtr handle, int milliseconds);
}
