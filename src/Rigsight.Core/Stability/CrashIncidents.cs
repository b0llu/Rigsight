namespace Rigsight.Core.Stability;

/// <summary>
/// Which failures are one moment: app crashes, freezes and graphics driver resets in quick succession across
/// different apps ("your PC froze"). One rule, so the list, a report's row and a notification can't split a moment
/// differently and then say different things about it.
/// </summary>
public static class CrashIncidents
{
    /// <summary>Failures this close together, of at least this many different things, are one incident.</summary>
    private static readonly TimeSpan Gap = TimeSpan.FromMinutes(5);
    private const int MinEvents = 3, MinApps = 2;

    /// <summary>The incidents among the crashes given (any order), each oldest first; crashes in none of them are left out.</summary>
    public static List<List<CrashEvent>> Find(IEnumerable<CrashEvent> crashes)
    {
        var found = new List<List<CrashEvent>>();
        var cluster = new List<CrashEvent>();
        void Close()
        {
            // Two apps crashing together (an app and its own helper, say) isn't a PC-wide problem; a freeze or a driver reset is.
            int apps = cluster.Select(e => e.AppExe.ToLowerInvariant()).Distinct().Count();
            bool pcWide = cluster.Any(e => e.Kind is CrashKind.AppHang or CrashKind.GpuDriverReset);
            if (cluster.Count >= MinEvents && (apps >= 3 || (apps >= MinApps && pcWide))) found.Add(cluster);
            cluster = [];
        }
        foreach (var e in crashes.Where(e => e.Kind is CrashKind.AppCrash or CrashKind.AppHang or CrashKind.GpuDriverReset).OrderBy(e => e.Ts).ThenBy(e => e.Id))
        {
            if (cluster.Count > 0 && e.Time - cluster[^1].Time > Gap) Close();
            cluster.Add(e);
        }
        Close();
        return found;
    }
}
