using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Rigsight.Core;

namespace Rigsight.Services;

/// <summary>Starts the background agent when it isn't running.</summary>
public static class AgentLauncher
{
    /// <summary>
    /// Uses the startup task when it points at this install (runs elevated with no UAC prompt).
    /// Otherwise starts the agent directly, which asks for admin rights once and then re-registers
    /// the task for next time.
    /// </summary>
    public static bool Start()
    {
        var exe = RigsightPaths.Sibling(RigsightPaths.AgentExe);
        if (!File.Exists(exe)) return false;
        if (AgentTask.PointsTo(exe) && AgentTask.Run()) return true;

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
}
