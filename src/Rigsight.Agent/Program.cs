using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Rigsight.Core;
using Rigsight.Agent.Widgets;
using Rigsight.Core.Settings;

namespace Rigsight.Agent;

internal static class Program
{
    private const string Gen0Variable = "DOTNET_GCgen0size";
    private const string Gen0Size = "0x400000"; // 4 MB, as the app has

    [STAThread]
    private static void Main(string[] args)
    {
        // A test copy started elevated (through UAC, which doesn't pass the environment on) is told its data folder
        // here; the same as RIGSIGHT_DATA_DIR, which anyone can already set. Must come before anything reads RigsightPaths.
        if (Array.IndexOf(args, "--data-dir") is var d and >= 0 && d + 1 < args.Length)
            Environment.SetEnvironmentVariable("RIGSIGHT_DATA_DIR", args[d + 1]);
        Log.Rotates = true; // the agent keeps the shared log to size (see Log.Rotates)
        bool setupStep = args.Any(a => a is "--install-rtss" or "--install-pawnio" or "--register-startup" or "--everyone" or "--updater" or "--quit" or "--start-in-sessions" or "--forget-tasks");
        if (setupStep && Account.ElevatedAsSomeoneElse) Log.Muted = true;

        bool isAdmin = Account.Kind == AccountKind.Admin;

        // Run by the installer before replacing or removing files: ask a running agent to save and exit
        // cleanly (a forced kill would lose the game session in progress), waiting a few seconds.
        if (args.Contains("--quit"))
        {
            try
            {
                // Run by the system (its updater's install, an IT tool's): the request only reaches an agent from inside
                // its own sign-in, so it is made there, once for each person signed in, as that person.
                using (var who = WindowsIdentity.GetCurrent())
                    if (who.IsSystem)
                    {
                        SessionStarter.QuitAgents();
                        return;
                    }
                if (EventWaitHandle.TryOpenExisting(RigsightPaths.AgentQuitEvent, out var quit))
                {
                    quit.Set();
                    // This sign-in's agent only: another person's isn't ours to wait on (nor was it asked).
                    int session = Process.GetCurrentProcess().SessionId;
                    var others = Process.GetProcessesByName("Rigsight.Agent").Where(p => p.Id != Environment.ProcessId && p.SessionId == session).ToList();
                    foreach (var p in others) p.WaitForExit(6000);
                }
            }
            catch (Exception ex)
            {
                Log.Error("agent", ex);
            }
            return;
        }

        // Run by the installer (already elevated) when "Install RivaTuner" is ticked: see RtssSetup.Install.
        // Exit code: 0 installed (or already there), 1 failed, 2 stopped responding and ended, 3 no winget.
        if (args.Contains("--install-rtss"))
        {
            int code;
            try
            {
                code = RtssSetup.Install() switch
                {
                    RtssInstallResult.Installed or RtssInstallResult.AlreadyInstalled => 0,
                    RtssInstallResult.Stuck => 2,
                    RtssInstallResult.NoWinget => 3,
                    _ => 1,
                };
            }
            catch (Exception ex)
            {
                Log.Error("install", ex);
                code = 1;
            }
            Environment.Exit(code);
        }

        // Run by the installer (already elevated) when "Install the PawnIO sensor driver" is ticked: through here so
        // winget is App Installer's own file, not whatever "winget" on PATH turns out to be (see Winget).
        // Exit code: 0 winget finished, 1 it failed or was ended, 3 no winget.
        if (args.Contains("--install-pawnio"))
        {
            int code;
            try
            {
                code = RtssSetup.WingetInstall("namazso.PawnIO") switch
                {
                    null => 3,
                    { Outcome: WatchOutcome.Exited, ExitCode: 0 } => 0,
                    _ => 1,
                };
            }
            catch (Exception ex)
            {
                Log.Error("install", ex);
                code = 1;
            }
            Environment.Exit(code);
        }

        // Run by the installer (already elevated), or by Settings with an admin's go-ahead: Rigsight starts for everyone
        // who signs in on this PC ("--everyone on"), or stops doing so ("off"). The same for keeping it up to date.
        if (Array.IndexOf(args, "--everyone") is var ev and >= 0)
        {
            if (!isAdmin || Environment.ProcessPath is not { } self) Environment.Exit(1);
            // Exit code 2: not installed where only an admin can change the program (see MachineSetup.OnlyAdminsCanChange).
            if (ev + 1 < args.Length && args[ev + 1] == "on" && !MachineSetup.OnlyAdminsCanChange(AppContext.BaseDirectory)) Environment.Exit(2);
            try { MachineSetup.SetEveryone(ev + 1 < args.Length && args[ev + 1] == "on", Environment.ProcessPath!); }
            catch (Exception ex) { Log.Error("setup", ex); Environment.Exit(1); }
            Environment.Exit(0);
        }
        if (Array.IndexOf(args, "--updater") is var up and >= 0)
        {
            if (!isAdmin) Environment.Exit(1);
            if (up + 1 < args.Length && args[up + 1] == "on" && !MachineSetup.OnlyAdminsCanChange(AppContext.BaseDirectory)) Environment.Exit(2);
            bool ok;
            try { ok = UpdaterTask.Set(up + 1 < args.Length && args[up + 1] == "on"); }
            catch (Exception ex) { Log.Error("setup", ex); ok = false; }
            Environment.Exit(ok ? 0 : 1);
        }

        // Run by the installer when an update came from the system's own updater, which closed every agent: start one
        // again for each person signed in. Only the system account can; from any other this starts nothing.
        if (args.Contains("--start-in-sessions"))
        {
            int started = 0;
            try { started = SessionStarter.StartAgents(); } catch (Exception ex) { Log.Error("setup", ex); }
            Log.Write("agent", $"Started for {started} signed-in account(s) after an update");
            Environment.Exit(0);
        }

        // The updater task (see UpdaterTask): the system installs a newer Rigsight if there is one, and ends.
        if (args.Contains(UpdaterTask.Argument))
        {
            int code;
            try { code = SystemUpdater.RunAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { Log.Error("update", ex); code = SystemUpdater.Failed; }
            Environment.Exit(code);
        }

        // Run by the installer (already elevated): register "start with Windows" and start the agent
        // through the task, so the user never sees a separate UAC prompt.
        // Uninstall: every account's start-up task goes (there is one per admin since 0.23.0).
        if (args.Contains("--forget-tasks"))
        {
            try { AgentTask.DeleteAll(); } catch (Exception ex) { Log.Error("setup", ex); }
            Environment.Exit(0);
        }

        if (args.Contains("--register-startup"))
        {
            if (!isAdmin) Environment.Exit(1);
            // Setup was started from someone's sign-in with another account's admin password: that admin isn't the one
            // who signs in here, so no task for them and nothing saved under them. The person's own Rigsight starts
            // without admin rights (through the window setup opens, and at sign-in where it is set up for everyone).
            // Exit code 4 says so: setup then starts the person's own agent, as them.
            if (Account.ElevatedAsSomeoneElse) Environment.Exit(4);
            // Setup run by the system itself (the updater's install, or an IT tool's): the system is nobody who signs in.
            // A task registered here would belong to it, take the place of an admin's own, and start an agent as the
            // system (seen on a test machine). Whoever is signed in gets their agent back from "--start-in-sessions".
            using (var who = WindowsIdentity.GetCurrent())
                if (who.IsSystem) Environment.Exit(0);
            bool ok = StartupTask.Enable();
            if (ok)
            {
                var settings = SettingsStore.Load();
                if (!settings.StartupConfigured)
                {
                    settings.StartupConfigured = true;
                    SettingsStore.Save(settings);
                }
                ok = AgentTask.Run();
            }
            Log.Write("agent", $"Registered startup task: {ok}");
            Environment.Exit(ok ? 0 : 1);
        }

        // Started by the agent to check for (and download or install) an update, then exit. See BackgroundUpdater.
        if (args.Contains("--update"))
        {
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            int code;
            try { code = BackgroundUpdater.RunAsync(args.Contains("--at-startup"), !args.Contains("--no-download")).GetAwaiter().GetResult(); }
            catch (Exception ex) { Log.Error("update", ex); code = BackgroundUpdater.Failed; }
            Environment.Exit(code);
        }

        // Started at sign-in on a PC where Rigsight is set up for everyone (see MachineSetup): each person gets the
        // agent their account allows, and nobody is asked anything.
        // Or started again for someone signed in, after an update closed their agent ("--resume"): the same, without
        // asking whether they want it at sign-in (it was running).
        bool resume = args.Contains(MachineSetup.ResumeArgument);
        bool signIn = resume || args.Contains(MachineSetup.SignInArgument);
        if (signIn && !RigsightPaths.IsTestInstance)
        {
            // This person switched "Start with Windows" off for themselves.
            if (!resume && !SettingsStore.Load().StartAtSignIn) return;
            // An admin with a start-up task of their own: the task starts the agent, with admin rights.
            // (Asked to start it now as well: at sign-in the task is about to anyway and a second start is ignored,
            // and after an update installed by the system nothing else would bring this admin's agent back.)
            if (Account.Kind == AccountKind.AdminNotElevated && Environment.ProcessPath is { } me && AgentTask.StartsForMe(me))
            {
                AgentTask.Run();
                return;
            }
        }

        // CPU and motherboard sensors need admin rights. Relaunch elevated unless told not to. Only an admin's own
        // account is asked (one "Yes"): on a standard account the prompt wants a password the person doesn't have,
        // so the agent runs without admin rights straight away.
        if (Account.Kind == AccountKind.AdminNotElevated && !signIn && !args.Contains("--no-elevate") && !Debugger.IsAttached && !RigsightPaths.IsTestInstance)
        {
            try
            {
                var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                foreach (var a in args.Append("--no-elevate")) psi.ArgumentList.Add(a);
                Process.Start(psi);
                return;
            }
            catch (Win32Exception)
            {
                // UAC declined: run in limited mode.
            }
        }

        // The garbage collector sizes its short-lived area from the CPU's cache: tens of megabytes on a large-cache CPU (a
        // Ryzen X3D has 96 MB of L3), filled and emptied over and over by an agent that needs a few. It can only be set
        // before the runtime starts, so, as the app does, start again with it set (about a tenth of a second), and clear
        // it so what the agent starts (the update helper, RivaTuner) doesn't inherit it. See Rigsight's Program.
        if (Environment.GetEnvironmentVariable(Gen0Variable) is null && Environment.ProcessPath is { } exe)
        {
            try
            {
                var start = new ProcessStartInfo(exe) { UseShellExecute = false };
                foreach (var a in args) start.ArgumentList.Add(a);
                start.Environment[Gen0Variable] = Gen0Size;
                using var child = Process.Start(start);
                if (child is not null) return;
            }
            catch (Exception ex)
            {
                Log.Error("agent", ex); // run as we are, with the runtime's own sizing
            }
        }
        Environment.SetEnvironmentVariable(Gen0Variable, null);

        Mutex mutex;
        try
        {
            mutex = new Mutex(true, RigsightPaths.AgentMutex, out bool created);
            if (!created && args.Contains("--replace"))
            {
                // Taking over from a non-admin copy that is shutting down: wait for it to let go.
                try { created = mutex.WaitOne(15_000); }
                catch (AbandonedMutexException) { created = true; }
            }
            if (!created) return;
        }
        catch (UnauthorizedAccessException)
        {
            return; // an elevated copy is already running
        }

        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => Log.Error("agent", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("agent", (Exception)e.ExceptionObject);

        // Should the agent ever crash, Windows starts it again (only one that had run for a minute, so never round and round).
        try { RegisterApplicationRestart(CrashRestartLine(args, TestDataDir), RestartNoPatch | RestartNoReboot); } catch (Exception ex) { Log.Error("agent", ex); }

        // Every start says which version it is, where it runs from, on which Windows and in which language and time
        // zone: a log read weeks later, or after an update or two, still says what each line was written by.
        var offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
        Log.Write("agent", $"Starting {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} (admin: {isAdmin}, account: {Account.Kind}, process {Environment.ProcessId}{(args.Contains(AfterCrash) ? ", after a crash" : "")}"
            + $", Windows {Environment.OSVersion.Version}, {System.Globalization.CultureInfo.CurrentCulture.Name}, UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}, from {AppContext.BaseDirectory.TrimEnd('\\')})");
        Application.Run(new AgentContext(args, isAdmin));
        GC.KeepAlive(mutex);
    }

    private const string AfterCrash = "--after-crash";
    private const int RestartNoPatch = 4, RestartNoReboot = 8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterApplicationRestart(string? commandLine, int flags);

    // A test copy's data folder is said outright: what Windows starts after a crash doesn't get our environment.
    private static string? TestDataDir => RigsightPaths.IsTestInstance ? RigsightPaths.DataDir : null;

    /// <summary>What a copy that carries on from this one is started with: as this one was, without what was only for its own start.</summary>
    internal static List<string> RestartArgs(string[] args) => RestartArgs(args, TestDataDir);

    internal static List<string> RestartArgs(string[] args, string? testDataDir)
    {
        // (Nor how it came to start at sign-in: a copy carrying on from this one isn't a sign-in.)
        var list = args.Where(a => a is not ("--open" or "--replace" or AfterCrash or MachineSetup.SignInArgument or MachineSetup.ResumeArgument)).ToList();
        if (testDataDir is not null && !list.Contains("--data-dir")) list.AddRange(["--data-dir", testDataDir]);
        return list;
    }

    /// <summary>The same as one command line, for Windows to start the agent with after a crash.</summary>
    internal static string CrashRestartLine(string[] args, string? testDataDir) =>
        string.Join(' ', RestartArgs(args, testDataDir).Append(AfterCrash)
            .Select(a => a.Contains(' ') ? '"' + Path.TrimEndingDirectorySeparator(a) + '"' : a));
}
