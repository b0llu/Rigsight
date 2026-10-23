using System.Diagnostics;
using Rigsight.Core;

namespace Rigsight.Services;

/// <summary>How closing an app from an answer went.</summary>
/// <summary>How closing an app from an answer went. StillOpen: it was asked and is still there (maybe asking to save).</summary>
public enum CloseOutcome { Closed, Ended, Gone, NeedsAdmin, Refused, StillOpen }

/// <summary>
/// Closes an app the way a person would: each of its windows is asked to close, so it can save or ask to. One with a
/// window is never ended by force: if it's still open after a few seconds it may be asking "Save changes?", and ending it
/// then would lose that work (a side agent's catch, 2026-10-23), so it's left for the person. Only an app with no window,
/// nothing to ask, is ended as End task does. Never with an admin prompt: an app that runs with admin rights is left for
/// the person to close (an answer's button shouldn't bring up a UAC prompt).
/// </summary>
public sealed class AppCloser
{
    /// <summary>How long the app gets to close by itself before it is ended.</summary>
    public TimeSpan Grace { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>Tests give their own (no real app is closed).</summary>
    internal Func<string, IReadOnlyList<int>> PidsOf { get; init; } = Running;
    internal Func<int, bool> AskToClose { get; init; } = CloseWindow;
    internal TaskEnder Ender { get; init; } = new() { Elevate = _ => Elevation.Cancelled };

    public CloseOutcome Close(string exe)
    {
        var pids = PidsOf(exe);
        if (pids.Count == 0) return CloseOutcome.Gone;
        bool asked = false;
        foreach (int pid in pids) asked |= AskToClose(pid);
        if (asked)
        {
            var until = DateTime.UtcNow + Grace;
            while (DateTime.UtcNow < until && PidsOf(exe).Count > 0) Thread.Sleep(250);
            if (PidsOf(exe).Count == 0) return CloseOutcome.Closed;
            Log.Write("ask", $"Closing {exe} from an answer: asked its windows to close, still open after {Grace.TotalSeconds:0} s (maybe asking to save): left open");
            return CloseOutcome.StillOpen;
        }
        var outcome = Ender.End([(exe, null)])[0];
        Log.Write("ask", $"Closing {exe} from an answer: no window to ask, ended it: {outcome}");
        return outcome switch
        {
            EndOutcome.Ended => CloseOutcome.Ended,
            EndOutcome.Gone => CloseOutcome.Gone,
            EndOutcome.Cancelled => CloseOutcome.NeedsAdmin,
            _ => CloseOutcome.Refused,
        };
    }

    private static IReadOnlyList<int> Running(string exe)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(exe);
        var pids = new List<int>();
        foreach (var p in Process.GetProcessesByName(name))
            using (p) pids.Add(p.Id);
        return pids;
    }

    private static bool CloseWindow(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainWindowHandle != IntPtr.Zero && p.CloseMainWindow();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}
