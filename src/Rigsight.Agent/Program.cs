using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Agent;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // A test copy started elevated (through UAC, which doesn't pass the environment on) is told its data folder
        // here; the same as RIGSIGHT_DATA_DIR, which anyone can already set. Must come before anything reads RigsightPaths.
        if (Array.IndexOf(args, "--data-dir") is var d and >= 0 && d + 1 < args.Length)
            Environment.SetEnvironmentVariable("RIGSIGHT_DATA_DIR", args[d + 1]);

        bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        // Run by the installer before replacing or removing files: ask a running agent to save and exit
        // cleanly (a forced kill would lose the game session in progress), waiting a few seconds.
        if (args.Contains("--quit"))
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(RigsightPaths.AgentQuitEvent, out var quit))
                {
                    quit.Set();
                    var others = Process.GetProcessesByName("Rigsight.Agent").Where(p => p.Id != Environment.ProcessId).ToList();
                    foreach (var p in others) p.WaitForExit(6000);
                }
            }
            catch (Exception ex)
            {
                Log.Error("agent", ex);
            }
            return;
        }

        // Run by the installer (already elevated): register "start with Windows" and start the agent
        // through the task, so the user never sees a separate UAC prompt.
        if (args.Contains("--register-startup"))
        {
            if (!isAdmin) Environment.Exit(1);
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

        // CPU and motherboard sensors need admin rights. Relaunch elevated unless told not to.
        if (!isAdmin && !args.Contains("--no-elevate") && !Debugger.IsAttached && !RigsightPaths.IsTestInstance)
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

        Log.Write("agent", $"Starting (admin: {isAdmin})");
        Application.Run(new AgentContext(args, isAdmin));
        GC.KeepAlive(mutex);
    }
}
