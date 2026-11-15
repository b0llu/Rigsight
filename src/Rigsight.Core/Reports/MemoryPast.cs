using Rigsight.Core.Data;

namespace Rigsight.Core.Reports;

/// <summary>
/// What is on record about every app's memory, for the Memory page's list: each app's usual, its hours and its peak of
/// today, and when today the memory was at its fullest. Read for all apps at once (the list has a hundred rows and
/// more), by the rule the Processes page's box uses for one app (see <see cref="AppPast"/>).
/// </summary>
public sealed class MemoryPast
{
    /// <summary>How far above its usual an app is before the Memory page points it out.</summary>
    public const double OverFactor = 1.6;

    /// <summary>
    /// How far above its usual an app pointed out has to stay to go on being pointed out. Lower than
    /// <see cref="OverFactor"/>: an app hovering at that line had its mark come and go every few seconds.
    /// </summary>
    public const double StayOverFactor = 1.3;

    /// <summary>
    /// How much of its memory and page file Windows has promised to apps (%) for it to be called nearly out of memory:
    /// the crash pages' line (see <see cref="Stability.CaseMinutes"/>).
    /// </summary>
    public const double NearlyOutPercent = 90;

    /// <summary>How many apps the fullest minute's hover box names.</summary>
    public const int FullestAppsListed = 4;

    /// <summary>Each app's average memory over the days before today (MB), by its program's name; apps with too few days have none.</summary>
    public Dictionary<string, double> Usual { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each app's memory in each hour of today (MB), from midnight to the hour now; null where it wasn't recorded.</summary>
    public Dictionary<string, double?[]> Hours { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each app's highest memory today (MB), the peak "Today's biggest memory users" gives, by its program's name.</summary>
    public Dictionary<string, double> Peak { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The most days any app has its memory on record in the <see cref="AppPast.UsualDays"/> days before today: an app
    /// has a usual from <see cref="AppPast.MinUsualDays"/> of them.
    /// </summary>
    public int RecordedDays { get; init; }

    /// <summary>No app has a usual yet, since no app has the days on record for one (the first days after installing).</summary>
    public bool TooSoon => Usual.Count == 0 && RecordedDays < AppPast.MinUsualDays;

    /// <summary>The minute today with the most memory in use, and how much that was (GB); null with nothing recorded today.</summary>
    public (DateTime At, double UsedGB)? Fullest { get; init; }

    /// <summary>
    /// What the apps held in the minute of <see cref="Fullest"/> (MB), biggest first, at most <see cref="FullestAppsListed"/>;
    /// their averages over its hour where that minute kept too few of them (see <see cref="FullestByHour"/>).
    /// </summary>
    public IReadOnlyList<(string Name, double MB)> FullestHeld { get; init; } = [];

    /// <summary><see cref="FullestHeld"/> is each app's average over the hour of the fullest minute, not that minute's.</summary>
    public bool FullestByHour { get; init; }

    /// <summary>How much of its memory and page file Windows had promised to apps in the fullest minute (%); null where not recorded.</summary>
    public double? FullestCommitPct { get; init; }

    /// <summary>The one or two apps that held the most at <see cref="Fullest"/>, biggest first.</summary>
    public IReadOnlyList<string> FullestApps => [.. FullestHeld.Take(2).Select(a => a.Name)];

    public static MemoryPast Read(RigsightDb db, DateTime now)
    {
        var today = now.Date;
        long from = TimeUtil.ToUnix(today), to = TimeUtil.ToUnix(today.AddDays(1));
        var apps = db.LoadApps().ToDictionary(a => a.Id);
        var fullest = db.GetFullestMinute(from, to);
        int fullestHour = fullest is { } f ? TimeUtil.FromUnix(f.Ts).Hour : -1;

        var inFullestHour = new List<(string Name, double MB)>();
        var hours = new Dictionary<string, double?[]>(StringComparer.OrdinalIgnoreCase);
        var peaks = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in db.GetAppHours(from, to))
        {
            if (h.MemN <= 0 || !apps.TryGetValue(h.AppId, out var app)) continue;
            int at = TimeUtil.FromUnix(h.Ts).Hour;
            if (at > now.Hour) continue;
            if (!hours.TryGetValue(app.Exe, out var of)) hours[app.Exe] = of = new double?[now.Hour + 1];
            of[at] = h.MemSum / h.MemN;
            if (h.MemMax is double max && max > peaks.GetValueOrDefault(app.Exe)) peaks[app.Exe] = max;
            if (at == fullestHour) inFullestHour.Add((app.Name, h.MemSum / h.MemN));
        }

        // What each app held in the fullest minute itself: the agent keeps the few apps holding the most each minute.
        List<(string Name, double MB)> held = [];
        double? commit = null;
        if (fullest is { } minute)
        {
            held = [.. db.GetAppMinutes(minute.Ts, minute.Ts + 1)
                .Where(a => a.MemMb > 0 && apps.ContainsKey(a.App))
                .GroupBy(a => apps[a.App].Name).Select(g => (Name: g.Key, MB: g.Sum(a => a.MemMb)))
                .OrderByDescending(a => a.MB).Take(FullestAppsListed)];
            commit = db.GetMinutes(minute.Ts, minute.Ts + 1).FirstOrDefault()?.CommitPct;
        }
        // Too few kept for that minute (or none, before 0.24.0): their averages over its hour instead.
        bool byHour = held.Count < 2 && inFullestHour.Count > held.Count;
        if (byHour) held = [.. inFullestHour.OrderByDescending(a => a.MB).Take(FullestAppsListed)];

        var (usual, days) = UsualAndDays(db, now, apps);
        var past = new MemoryPast
        {
            Fullest = fullest is { } m ? (TimeUtil.FromUnix(m.Ts), m.RamUsed) : null,
            FullestHeld = held,
            FullestByHour = byHour,
            FullestCommitPct = commit,
            RecordedDays = days,
        };
        foreach (var (exe, of) in hours) past.Hours[exe] = of;
        foreach (var (exe, peak) in peaks) past.Peak[exe] = peak;
        foreach (var (exe, mb) in usual) past.Usual[exe] = mb;
        return past;
    }

    /// <summary>
    /// Each app's usual memory (MB) by its program's name: its average over the <see cref="AppPast.UsualDays"/> days
    /// before today, for apps with at least <see cref="AppPast.MinUsualDays"/> of them. The one figure the Memory page and
    /// the AI's look-ups both give (2026-10-30: the page said Spotify "usually holds 356 MB", the AI's week average said
    /// 414 MB, and the answer called the page's number missing from the records).
    /// </summary>
    public static Dictionary<string, double> UsualOf(RigsightDb db, DateTime now, IReadOnlyDictionary<long, AppRow>? apps = null) =>
        UsualAndDays(db, now, apps).Usual;

    /// <summary><see cref="UsualOf"/>, and the most days any app has on record for it (<see cref="RecordedDays"/>).</summary>
    private static (Dictionary<string, double> Usual, int Days) UsualAndDays(RigsightDb db, DateTime now, IReadOnlyDictionary<long, AppRow>? apps)
    {
        apps ??= db.LoadApps().ToDictionary(a => a.Id);
        var today = now.Date;
        var usual = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        int most = 0;
        foreach (var (id, sum, n, days) in db.GetAppMemoryTotals(TimeUtil.ToUnix(today.AddDays(-AppPast.UsualDays)), TimeUtil.ToUnix(today)))
        {
            if (n <= 0 || !apps.TryGetValue(id, out var app)) continue;
            most = Math.Max(most, days);
            if (days >= AppPast.MinUsualDays) usual[app.Exe] = sum / n;
        }
        return (usual, most);
    }

    /// <summary>
    /// Since when an app now above its usual has been so: the first hour of today's latest run of hours above it. The
    /// run is the hours before the hour now (which may not be written yet) that stayed over <see cref="StayOverFactor"/>
    /// times its usual, and it came in at the first of them over <see cref="OverFactor"/>, the rule the list marks a row
    /// by. With none, since the hour now. <c>Started</c>: it started today and has been above since then, so the time
    /// is when it started ("since it started at 9:12 AM").
    /// </summary>
    /// <param name="hours">Its memory in each hour of today (MB), from midnight; null where not recorded.</param>
    /// <param name="started">When it started, where known.</param>
    public static (DateTime At, bool Started) AboveSince(IReadOnlyList<double?> hours, double usualMB, DateTime? started, DateTime now)
    {
        int last = Math.Min(now.Hour, hours.Count);
        int first = last;
        while (first > 0 && hours[first - 1] is double mb && mb > StayOverFactor * usualMB) first--;
        int came = now.Hour;
        for (int h = first; h < last; h++)
            if (hours[h] > OverFactor * usualMB)
            {
                came = h;
                break;
            }
        if (started is { } start && start.Date == now.Date && start <= now && came <= start.Hour) return (start, true);
        return (now.Date.AddHours(came), false);
    }
}
