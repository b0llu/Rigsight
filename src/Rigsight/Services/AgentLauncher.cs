using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Rigsight.Core;

namespace Rigsight.Services;

/// <summary>Starts the background agent when it isn't running.</summary>
public static class AgentLauncher
{
    /// <summary>
    /// Tries the startup task first (runs elevated with no UAC prompt), then falls back to starting
    /// the agent directly, which asks for admin rights.
    /// </summary>
    public static bool Start(bool allowUacPrompt)
    {
        if (RunTask()) return true;
        if (!allowUacPrompt) return false;

        var exe = RigsightPaths.Sibling(RigsightPaths.AgentExe);
        if (!File.Exists(exe)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static bool RunTask()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "/Run", "/TN", RigsightPaths.AgentTaskName }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
