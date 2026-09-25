using System.Diagnostics;
using System.Security;
using System.Xml;
using System.Xml.Linq;

namespace Rigsight.Core;

/// <summary>
/// The "Rigsight Agent" Task Scheduler task, which starts the agent with admin rights and no UAC prompt.
/// Shared by the agent (which registers it) and the app (which uses it to start the agent).
/// </summary>
public static class AgentTask
{
    public static bool Exists() => Schtasks(out _, "/Query", "/TN", RigsightPaths.AgentTaskName) == 0;

    /// <summary>
    /// True when the task starts <paramref name="exe"/>. A task left behind by an older install points
    /// somewhere else, and Windows reports "started" for it even though nothing runs.
    /// </summary>
    public static bool PointsTo(string exe)
    {
        return Schtasks(out var xml, "/Query", "/TN", RigsightPaths.AgentTaskName, "/XML") == 0 && XmlPointsTo(xml, exe);
    }

    /// <summary>Whether the task's XML (as "schtasks /Query /XML" prints it) starts <paramref name="exe"/>.</summary>
    internal static bool XmlPointsTo(string xml, string exe)
    {
        // Read as XML, not text: Windows needn't escape a path the way it was registered (e.g. "O'Brien", ' vs &apos;).
        try
        {
            return XDocument.Parse(xml).Descendants().Any(e => e.Name.LocalName == "Command" &&
                string.Equals(e.Value.Trim(), exe, StringComparison.OrdinalIgnoreCase));
        }
        catch (XmlException)
        {
            return xml.Contains($"<Command>{SecurityElement.Escape(exe)}</Command>", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static bool Run() => Schtasks(out _, "/Run", "/TN", RigsightPaths.AgentTaskName) == 0;

    public static bool Delete() => Schtasks(out _, "/Delete", "/TN", RigsightPaths.AgentTaskName, "/F") == 0;

    public static int Schtasks(out string output, params string[] args)
    {
        output = "";
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            output = stdout.Result;
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Error("task", ex);
            return -1;
        }
    }
}
