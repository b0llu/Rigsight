using Rigsight.Agent.Native;

namespace Rigsight.Agent.Tracking;

/// <summary>
/// Which running processes Windows itself marks as ones it can't lose (ending one stops Windows), for the warning
/// before ending something on the Processes page. Only read, and only while a window is open: nothing here ends or
/// changes a process. Without admin rights Windows doesn't say for its own processes, and those count as not marked
/// (the page still knows the best-known ones by name).
/// </summary>
internal sealed class CriticalMarks
{
    // A service host marks itself a moment after it starts: a young process is asked about again until it has settled.
    private static readonly long Settled = TimeSpan.FromSeconds(30).Ticks;

    private Dictionary<(int Pid, long Created), bool> _known = [];

    /// <summary>Asks Windows about one process (null: it wouldn't say). Tests give their own.</summary>
    internal Func<int, bool?> Read { get; set; } = Win32.IsProcessCritical;

    /// <summary>The marked ones among <paramref name="processes"/>, by process ID.</summary>
    /// <param name="now">The time, as a Windows file time (what a process's start is given in).</param>
    public HashSet<int> Marked(List<(int Pid, long Created)> processes, long now)
    {
        var marked = new HashSet<int>();
        var known = new Dictionary<(int, long), bool>(processes.Count);
        foreach (var key in processes)
        {
            if (!_known.TryGetValue(key, out bool mark))
            {
                mark = Read(key.Pid) == true;
                if (now - key.Created < Settled)
                {
                    if (mark) marked.Add(key.Pid);
                    continue;
                }
            }
            known[key] = mark;
            if (mark) marked.Add(key.Pid);
        }
        _known = known;
        return marked;
    }
}
