using System.Diagnostics;
using Rigsight.Core;

namespace Rigsight.Services;

/// <summary>
/// How long opening took, written to the log once per start: until the window was first drawn, and until the page it
/// opens on had read its history and was drawn with it. Both are timed from this program's own start; the start before
/// it that only sets the memory setting (see Program.Main, under a tenth of a second) isn't counted.
/// </summary>
public static class OpeningTime
{
    private static readonly object Gate = new();
    private static long _drawnMs = -1, _filledMs = -1;
    private static string? _page;
    private static bool _logged;

    private static long SinceStart()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return (long)(DateTime.Now - self.StartTime).TotalMilliseconds;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return -1;
        }
    }

    /// <summary>The window's first frame is on screen.</summary>
    public static void WindowDrawn()
    {
        lock (Gate)
        {
            if (_drawnMs < 0) _drawnMs = SinceStart();
            WriteOnce();
        }
    }

    /// <summary>The page the window opened on has read its history and been drawn with it.</summary>
    public static void PageFilled(string page)
    {
        lock (Gate)
        {
            if (_filledMs >= 0) return;
            _filledMs = SinceStart();
            _page = page;
            WriteOnce();
        }
    }

    // Once both are known, whichever came last.
    private static void WriteOnce()
    {
        if (_logged || _drawnMs < 0 || _filledMs < 0) return;
        _logged = true;
        // What .NET had to compile while opening (the rest was compiled ahead, see PublishReadyToRunComposite): a few
        // hundred methods is expected; thousands means the build's ahead-of-time image wasn't used.
        long methods = System.Runtime.JitInfo.GetCompiledMethodCount();
        double compileMs = System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds;
        Log.Write("app", $"Opened on {_page}: the window was drawn after {_drawnMs} ms, the page with its history after {Math.Max(_filledMs, _drawnMs)} ms ({methods} methods compiled while opening, {compileMs:0} ms of it)");
    }
}
