using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Rigsight.Core;

namespace Rigsight.Agent;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        // CPU and motherboard sensors need admin rights. Relaunch elevated unless told not to.
        if (!isAdmin && !args.Contains("--no-elevate") && !Debugger.IsAttached)
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
            mutex = new Mutex(true, @"Local\Rigsight.Agent", out bool created);
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
