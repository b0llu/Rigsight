using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;

namespace Rigsight.Core.Reports;

/// <summary>
/// Each app's internet use over a period, for the Apps page: what the Network page lists for the same period, read
/// from the same per-app tables, so the two pages never disagree. Nothing new is recorded for it.
/// </summary>
public sealed class AppInternet
{
    /// <summary>The period has internet use recorded at all: without it an app's "None" would be a guess.</summary>
    public bool Recorded { get; init; }

    /// <summary>Each app that moved anything, by its id.</summary>
    public IReadOnlyDictionary<long, NetAppUse> Use { get; init; } = new Dictionary<long, NetAppUse>();

    /// <summary>
    /// An entry for each app that moved anything (name, file, category, and its <see cref="AppStat.NetBytes"/>): the apps
    /// page adds the ones it has no time for (a command-line tool, a VPN), so its internet list holds every app the
    /// Network page lists.
    /// </summary>
    public IReadOnlyList<AppStat> Apps { get; init; } = [];

    public static readonly AppInternet None = new();

    /// <summary>
    /// The apps' use from <paramref name="from"/> to <paramref name="to"/>. Whole days are read from the day totals, as the
    /// Network page reads them; a custom range that starts or ends inside a day adds up its hours instead.
    /// </summary>
    public static AppInternet Of(RigsightDb db, DateTime from, DateTime to, IReadOnlyDictionary<long, AppRow> apps, RigsightSettings settings)
    {
        if (to <= from) return None;
        long f = TimeUtil.ToUnix(from), t = TimeUtil.ToUnix(to);
        bool wholeDays = from == from.Date && to == to.Date;
        // The days the period touches: a part of a day counts as recorded when its day was.
        bool recorded = db.GetNetDays(TimeUtil.ToUnix(from.Date), TimeUtil.ToUnix(to == to.Date ? to : to.Date.AddDays(1))).Any(d => d.Minutes > 0);
        if (!recorded) return None;

        var rows = wholeDays ? db.GetNetAppTotals(f, t) : Sum(db.GetNetAppHours(f, t));
        var use = rows.Where(u => u.Total > 0).ToDictionary(u => u.App);
        var stats = new List<AppStat>();
        foreach (var u in use.Values)
        {
            if (!apps.TryGetValue(u.App, out var row)) continue;
            stats.Add(new AppStat
            {
                Id = u.App, Exe = row.Exe, Path = row.Path,
                Name = settings.AppNames.TryGetValue(row.Exe, out var alias) ? alias : AppCatalog.KnownName(row.Exe) ?? row.Name,
                Category = settings.AppCategories.TryGetValue(row.Exe, out var c) ? c : row.Category,
                NetBytes = u.Total,
            });
        }
        return new AppInternet { Recorded = true, Use = use, Apps = stats };
    }

    /// <summary>Hour rows added up per app (Ts is 0, as the day totals have it).</summary>
    private static List<NetAppUse> Sum(List<NetAppUse> hours) =>
    [
        .. hours.GroupBy(u => u.App).Select(g => new NetAppUse
        {
            App = g.Key, Down = g.Sum(u => u.Down), Up = g.Sum(u => u.Up), BgDown = g.Sum(u => u.BgDown), BgUp = g.Sum(u => u.BgUp),
            AwayDown = g.Sum(u => u.AwayDown), AwayUp = g.Sum(u => u.AwayUp), GameDown = g.Sum(u => u.GameDown), Lan = g.Sum(u => u.Lan),
        }),
    ];
}
