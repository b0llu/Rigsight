using Rigsight.Core.Data;

namespace Rigsight.Core.Reports;

/// <summary>
/// What is on record about every app's memory, for the Memory page's list: each app's usual, its hours of today, and
/// when today the memory was at its fullest. Read for all apps at once (the list has a hundred rows and more), by the
/// rule the Processes page's box uses for one app (see <see cref="AppPast"/>).
/// </summary>
public sealed class MemoryPast
{
    /// <summary>Each app's average memory over the days before today (MB), by its program's name; apps with too few days have none.</summary>
    public Dictionary<string, double> Usual { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each app's memory in each hour of today (MB), from midnight to the hour now; null where it wasn't recorded.</summary>
    public Dictionary<string, double?[]> Hours { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The minute today with the most memory in use, and how much that was (GB); null with nothing recorded today.</summary>
    public (DateTime At, double UsedGB)? Fullest { get; init; }

    /// <summary>The one or two apps that held the most in the hour of <see cref="Fullest"/>, biggest first.</summary>
    public IReadOnlyList<string> FullestApps { get; init; } = [];

    public static MemoryPast Read(RigsightDb db, DateTime now)
    {
        var today = now.Date;
        long from = TimeUtil.ToUnix(today), to = TimeUtil.ToUnix(today.AddDays(1));
        var apps = db.LoadApps().ToDictionary(a => a.Id);
        var fullest = db.GetFullestMinute(from, to);
        int fullestHour = fullest is { } f ? TimeUtil.FromUnix(f.Ts).Hour : -1;

        var inFullestHour = new List<(string Name, double MB)>();
        var hours = new Dictionary<string, double?[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in db.GetAppHours(from, to))
        {
            if (h.MemN <= 0 || !apps.TryGetValue(h.AppId, out var app)) continue;
            int at = TimeUtil.FromUnix(h.Ts).Hour;
            if (at > now.Hour) continue;
            if (!hours.TryGetValue(app.Exe, out var of)) hours[app.Exe] = of = new double?[now.Hour + 1];
            of[at] = h.MemSum / h.MemN;
            if (at == fullestHour) inFullestHour.Add((app.Name, h.MemSum / h.MemN));
        }

        var past = new MemoryPast
        {
            Fullest = fullest is { } m ? (TimeUtil.FromUnix(m.Ts), m.RamUsed) : null,
            FullestApps = [.. inFullestHour.OrderByDescending(a => a.MB).Take(2).Select(a => a.Name)],
        };
        foreach (var (exe, of) in hours) past.Hours[exe] = of;
        foreach (var (id, sum, n, days) in db.GetAppMemoryTotals(TimeUtil.ToUnix(today.AddDays(-AppPast.UsualDays)), from))
            if (days >= AppPast.MinUsualDays && n > 0 && apps.TryGetValue(id, out var app)) past.Usual[app.Exe] = sum / n;
        return past;
    }
}
