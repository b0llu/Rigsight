namespace Rigsight.Core.Apps;

/// <summary>What ending an app (or one of its processes) from the Processes page would do, beyond closing it.</summary>
public enum EndRisk
{
    /// <summary>An ordinary app: ended at once, nothing asked.</summary>
    None,
    /// <summary>A part of Windows: asked first, in general words.</summary>
    Windows,
    /// <summary>Windows marks it as a process it can't lose, or it is one of the few known by name to take Windows down
    /// with them: asked first, with a box to tick.</summary>
    Critical,
    /// <summary>Windows Explorer: the taskbar and the desktop go with it, and it can be started again.</summary>
    Explorer,
    /// <summary>The Desktop Window Manager: Windows starts it again by itself.</summary>
    WindowManager,
    /// <summary>Rigsight's own window.</summary>
    OwnWindow,
    /// <summary>Rigsight's agent, which does the recording.</summary>
    OwnAgent,
    /// <summary>
    /// Every Windows service host as one row: ending the row would stop all services at once, so it can't be ended as
    /// a whole. Its processes can, one at a time (each then counts as <see cref="Windows"/>).
    /// </summary>
    ByProcessOnly,
}

public static class EndRisks
{
    // Windows' own mark comes from the agent and is the better word. These few stay by name: an agent without admin
    // rights can't read the mark, and Windows doesn't put it on every process that takes the session down with it
    // (seen on Windows 11: lsass and winlogon carry none).
    private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss.exe", "wininit.exe", "smss.exe", "services.exe", "lsass.exe", "winlogon.exe",
    };

    /// <summary>
    /// What ending <paramref name="exe"/> would do, from its name and where its program is. A program that only
    /// carries one of Windows' names but is somewhere else is an ordinary app; one whose place isn't known (Windows
    /// keeps its own protected ones to itself) goes by its name.
    /// </summary>
    /// <param name="wholeApp">Every process of the app (its row in the list), not one of them.</param>
    /// <param name="windowsDir">Windows' folder (tests give their own).</param>
    /// <param name="marked">Windows itself marks it (or, for an app, one of its processes) as one it can't lose: that
    /// counts wherever its program is.</param>
    public static EndRisk Classify(string exe, string? path, bool wholeApp = true, string? windowsDir = null, bool marked = false)
    {
        if (exe.Equals("Rigsight.exe", StringComparison.OrdinalIgnoreCase)) return EndRisk.OwnWindow;
        if (exe.Equals("Rigsight.Agent.exe", StringComparison.OrdinalIgnoreCase)) return EndRisk.OwnAgent;
        bool serviceHost = exe.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase);
        if (marked && !(serviceHost && wholeApp)) return EndRisk.Critical;

        windowsDir ??= Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        bool inWindows = !string.IsNullOrEmpty(path) && windowsDir.Length > 0
            && path.StartsWith(windowsDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(path) && !inWindows) return EndRisk.None;

        if (Critical.Contains(exe)) return EndRisk.Critical;
        if (exe.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) return EndRisk.Explorer;
        if (exe.Equals("dwm.exe", StringComparison.OrdinalIgnoreCase)) return EndRisk.WindowManager;
        if (serviceHost) return wholeApp ? EndRisk.ByProcessOnly : EndRisk.Windows;
        return inWindows ? EndRisk.Windows : EndRisk.None;
    }

    /// <summary>What the question before ending it says will happen; nothing for an ordinary app.</summary>
    public static string? Sentence(EndRisk risk) => risk switch
    {
        EndRisk.Critical => "Windows depends on this process. Ending it can make Windows stop or shut down, and anything unsaved in any app would be lost.",
        EndRisk.Explorer => "The taskbar, Start menu and desktop icons disappear until it starts again.",
        EndRisk.WindowManager => "The screen goes black for a moment, then Windows starts it again.",
        EndRisk.OwnWindow => "This window closes.",
        EndRisk.OwnAgent => "Nothing is recorded until it starts again.",
        EndRisk.Windows => "This is part of Windows. Ending it may make Windows or other apps stop working.",
        EndRisk.ByProcessOnly => "Open it and end one service at a time.",
        _ => null,
    };
}
