using System.Diagnostics;
using System.Security;
using System.Xml;
using System.Xml.Linq;

namespace Rigsight.Core;

/// <summary>
/// The Task Scheduler task that starts the agent with admin rights and no UAC prompt. Each admin account has its own
/// ("Rigsight Agent" and the account's id), so one admin on a PC never takes it from another. Versions before 0.23.0
/// kept a single "Rigsight Agent" for the whole PC: it still counts for the account it names, until that account's
/// own is registered and the old one is removed.
/// Shared by the agent (which registers it) and the app (which uses it to start the agent).
/// </summary>
public static class AgentTask
{
    /// <summary>This account's task.</summary>
    public static string Mine
    {
        get
        {
            using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
            return NameFor(me.User?.Value);
        }
    }

    internal static string NameFor(string? sid) => string.IsNullOrEmpty(sid) ? RigsightPaths.AgentTaskName : $"{RigsightPaths.AgentTaskName} {sid}";

    /// <summary>
    /// The task that starts the agent for this account, as Windows prints it: its own, or the old single one if that
    /// names it. Null when neither.
    /// </summary>
    private static string? MyXml(out string name)
    {
        name = Mine;
        if (Schtasks(out var xml, "/Query", "/TN", name, "/XML") == 0) return xml;
        name = RigsightPaths.AgentTaskName;
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        return Schtasks(out xml, "/Query", "/TN", name, "/XML") == 0 && XmlIsFor(xml, me.User?.Value, me.Name) ? xml : null;
    }

    public static bool Exists() => MyXml(out _) is not null;

    /// <summary>
    /// True when the task starts <paramref name="exe"/>. A task left behind by an older install points
    /// somewhere else, and Windows reports "started" for it even though nothing runs.
    /// </summary>
    public static bool PointsTo(string exe) => MyXml(out _) is { } xml && XmlPointsTo(xml, exe);

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

    /// <summary>
    /// True when a task starts <paramref name="exe"/> for the account asking: its own, or the old single one where it
    /// names this account. Another admin's task is never counted: for anyone else Windows still says "started" and
    /// starts nothing.
    /// </summary>
    public static bool StartsForMe(string exe) => PointsTo(exe);

    /// <summary>Whether the task's XML names this account (by its SID, as Windows keeps it, or by its name) as the one it runs for.</summary>
    internal static bool XmlIsFor(string xml, string? sid, string name)
    {
        try
        {
            var users = XDocument.Parse(xml).Descendants().Where(e => e.Name.LocalName == "Principal")
                .SelectMany(p => p.Elements().Where(e => e.Name.LocalName == "UserId")).Select(e => e.Value.Trim()).ToList();
            return users.Any(u => string.Equals(u, sid, StringComparison.OrdinalIgnoreCase) || string.Equals(u, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (XmlException)
        {
            return false;
        }
    }

    public static bool Run() => MyXml(out var name) is not null && Schtasks(out _, "/Run", "/TN", name) == 0;

    /// <summary>Removes this account's task, and the old single one where it is this account's. Another admin's stays.</summary>
    public static bool Delete()
    {
        for (int i = 0; i < 2 && MyXml(out var name) is not null; i++)
            if (Schtasks(out _, "/Delete", "/TN", name, "/F") != 0) return false;
        return true;
    }

    /// <summary>Removes the old single task once this account has its own, where the old one was this account's.</summary>
    public static void DropOldOne()
    {
        if (Schtasks(out _, "/Query", "/TN", Mine) != 0) return;
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (Schtasks(out var xml, "/Query", "/TN", RigsightPaths.AgentTaskName, "/XML") == 0 && XmlIsFor(xml, me.User?.Value, me.Name))
            Schtasks(out _, "/Delete", "/TN", RigsightPaths.AgentTaskName, "/F");
    }

    /// <summary>On uninstall, with admin rights: every account's task, and the old single one.</summary>
    public static void DeleteAll()
    {
        if (Schtasks(out var list, "/Query", "/FO", "CSV", "/NH") != 0) return;
        foreach (var name in NamesIn(list))
            Schtasks(out _, "/Delete", "/TN", name, "/F");
    }

    /// <summary>Rigsight's agent tasks in what "schtasks /Query /FO CSV /NH" prints (one line a task: "\name","next run","status").</summary>
    internal static List<string> NamesIn(string list)
    {
        var names = new List<string>();
        foreach (var line in list.Split('\n'))
        {
            int end = line.IndexOf("\",", StringComparison.Ordinal);
            if (end < 2 || line[0] != '"') continue;
            string name = line[1..end].TrimStart('\\');
            bool ours = name.Equals(RigsightPaths.AgentTaskName, StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith(RigsightPaths.AgentTaskName + " S-", StringComparison.OrdinalIgnoreCase);
            if (ours && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }
        return names;
    }

    public static int Schtasks(out string output, params string[] args)
    {
        output = "";
        try
        {
            var psi = new ProcessStartInfo(RigsightPaths.TaskScheduler)
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
