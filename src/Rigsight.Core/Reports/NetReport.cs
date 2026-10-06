using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;

namespace Rigsight.Core.Reports;

/// <summary>One bar of the internet chart: an hour (a day's chart), a day (a week's or month's) or a month (a year's).</summary>
/// <param name="BgDown">The part of the download by apps that weren't in front (someone at the PC or not).</param>
public sealed record NetBin(DateTime Start, long Down, long BgDown, long Up);

/// <summary>One app's internet use over a period, and when it used the most.</summary>
public sealed class NetAppStat
{
    public long Id { get; init; }
    public string Exe { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Path { get; init; }
    public AppCategory Category { get; init; }
    public required NetAppUse Use { get; init; }
    /// <summary>Its busiest hour (a day) or day (longer), and how much moved then.</summary>
    public DateTime? BusiestAt { get; set; }
    public long BusiestBytes { get; set; }
    /// <summary>Its fastest big download (bytes a second), if it had one.</summary>
    public double? Fastest { get; set; }
}

/// <summary>A big download: who, when, how much, how long it took and how fast it went.</summary>
public sealed record NetDownload(string App, string Exe, string? Path, DateTime Start, DateTime End, long Bytes, double Speed)
{
    public TimeSpan Took => TimeSpan.FromSeconds(Math.Max(1, (End - Start).TotalSeconds));
}

/// <summary>The internet over a period: totals, the chart, every app, big downloads, drops, and what's worth saying.</summary>
public sealed class NetReport
{
    public ReportRange Range { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public DateTime BuiltAt { get; init; } = DateTime.Now;

    /// <summary>Minutes with any internet use recorded: none means the period has nothing to show yet.</summary>
    public int Minutes { get; set; }
    public bool HasData => Minutes > 0;

    public long Down { get; set; }
    public long Up { get; set; }
    public long BgDown { get; set; }
    public long BgUp { get; set; }
    public long AwayDown { get; set; }
    public long AwayUp { get; set; }
    /// <summary>To and from devices at home (both ways): not internet, and not in the totals.</summary>
    public long Lan { get; set; }
    public long Background => BgDown + BgUp + AwayDown + AwayUp;

    /// <summary>A day: the middle of the days before with any use (30 back, at least 7 of them), for "vs X on a usual day".</summary>
    public long? UsualDayDown { get; set; }
    /// <summary>Longer: the period before, as far into it as this one has gone (null when it had nothing).</summary>
    public long? PreviousDown { get; set; }

    /// <summary>The fastest steady download (bytes a second, all apps together), when, and the app doing most of it then.</summary>
    public long? TopSpeed { get; set; }
    public DateTime? TopSpeedAt { get; set; }
    public string? TopSpeedApp { get; set; }

    public List<NetBin> Bins { get; set; } = [];
    public List<NetAppStat> Apps { get; set; } = [];
    public List<NetDownload> Downloads { get; set; } = [];
    public List<NetDrop> Drops { get; set; } = [];
    /// <summary>A day's quarter hours (96), each true when the PC was on and online in it: the connection strip.</summary>
    public bool[] Quarters { get; set; } = [];
    public List<Insight> Insights { get; set; } = [];
}

/// <summary>Reads a <see cref="NetReport"/> from the network tables, and says what's worth saying about it.</summary>
public static class NetReportBuilder
{
    /// <summary>The big downloads a report lists.</summary>
    public const int DownloadsShown = 4;

    public static NetReport Build(RigsightDb db, ReportRange range, DateTime anchor, IReadOnlyDictionary<long, AppRow> apps, RigsightSettings settings,
        IReadOnlyList<string>? gamesPlayed = null)
    {
        var (from, to) = ReportBuilder.Bounds(range, anchor);
        var now = DateTime.Now;
        var r = new NetReport { Range = range, From = from, To = to, BuiltAt = now };
        long f = TimeUtil.ToUnix(from), t = TimeUtil.ToUnix(to);

        var days = db.GetNetDays(f, t);
        r.Minutes = days.Sum(d => d.Minutes);
        if (!r.HasData) return r;
        r.Down = days.Sum(d => d.Down);
        r.Up = days.Sum(d => d.Up);
        r.BgDown = days.Sum(d => d.BgDown);
        r.BgUp = days.Sum(d => d.BgUp);
        r.AwayDown = days.Sum(d => d.AwayDown);
        r.AwayUp = days.Sum(d => d.AwayUp);
        r.Lan = days.Sum(d => d.Lan);

        // The chart.
        if (range == ReportRange.Day)
        {
            var hours = new NetBin[24];
            for (int h = 0; h < 24; h++) hours[h] = new NetBin(from.AddHours(h), 0, 0, 0);
            var quarters = new bool[96];
            foreach (var m in db.GetNetMinutes(f, t))
            {
                var time = TimeUtil.FromUnix(m.Ts);
                quarters[Math.Clamp((time.Hour * 60 + time.Minute) / 15, 0, 95)] = true;
                int h = Math.Clamp(time.Hour, 0, 23);
                var b = hours[h];
                hours[h] = b with { Down = b.Down + m.Down, BgDown = b.BgDown + m.BgDown + m.AwayDown, Up = b.Up + m.Up };
            }
            r.Bins = [.. hours];
            r.Quarters = quarters;
        }
        else if (range == ReportRange.Year)
        {
            r.Bins = [.. Enumerable.Range(0, 12).Select(i => from.AddMonths(i)).Select(month =>
            {
                var inMonth = days.Where(d => TimeUtil.FromUnix(d.Day) is var day && day >= month && day < month.AddMonths(1)).ToList();
                return new NetBin(month, inMonth.Sum(d => d.Down), inMonth.Sum(d => d.BgDown + d.AwayDown), inMonth.Sum(d => d.Up));
            })];
        }
        else
        {
            // A date has two rows only where the PC's time zone changed during it (each zone's midnight began one): added up.
            var byDay = days.GroupBy(d => TimeUtil.FromUnix(d.Day).Date)
                .ToDictionary(g => g.Key, g => (Down: g.Sum(d => d.Down), Background: g.Sum(d => d.BgDown + d.AwayDown), Up: g.Sum(d => d.Up)));
            r.Bins = [.. Enumerable.Range(0, (int)(to - from).TotalDays).Select(i => from.AddDays(i)).Select(day =>
                byDay.TryGetValue(day, out var d) ? new NetBin(day, d.Down, d.Background, d.Up) : new NetBin(day, 0, 0, 0))];
        }

        // The fastest steady download.
        if (days.Where(d => d.Best is not null).MaxBy(d => d.Best) is { BestTs: long bestTs } fastest)
        {
            r.TopSpeed = fastest.Best;
            r.TopSpeedAt = TimeUtil.FromUnix(bestTs);
            if (db.GetNetMinutes(bestTs, bestTs + 60).FirstOrDefault()?.App is long app) r.TopSpeedApp = NameOf(apps, settings, app);
        }

        // Every app, biggest first, with its busiest hour or day and its fastest download.
        var stats = new Dictionary<long, NetAppStat>();
        foreach (var u in db.GetNetAppTotals(f, t))
        {
            if (u.Total <= 0 || !apps.TryGetValue(u.App, out var row)) continue;
            stats[u.App] = new NetAppStat
            {
                Id = u.App, Exe = row.Exe, Name = NameOf(apps, settings, u.App), Path = row.Path,
                Category = settings.AppCategories.TryGetValue(row.Exe, out var c) ? c : row.Category, Use = u,
            };
        }
        var busiest = range == ReportRange.Day ? db.GetNetAppHours(f, t) : db.GetNetAppDays(f, t);
        foreach (var g in busiest.GroupBy(u => u.App))
            if (stats.TryGetValue(g.Key, out var s) && g.MaxBy(u => u.Total) is { } top)
                (s.BusiestAt, s.BusiestBytes) = (TimeUtil.FromUnix(top.Ts), top.Total);
        var transfers = db.GetNetTransfers(f, t, 500);
        foreach (var g in transfers.GroupBy(x => x.App))
            if (stats.TryGetValue(g.Key, out var s)) s.Fastest = g.Max(x => x.Speed);
        r.Apps = [.. stats.Values.OrderByDescending(s => s.Use.Total)];
        r.Downloads = [.. transfers.Take(DownloadsShown).Where(x => apps.ContainsKey(x.App)).Select(x =>
            new NetDownload(NameOf(apps, settings, x.App), apps[x.App].Exe, apps[x.App].Path, TimeUtil.FromUnix(x.Start), TimeUtil.FromUnix(x.End), x.Bytes, x.Speed))];
        r.Drops = db.GetNetDrops(f, t);

        // Against before: a day against a usual day; longer, the period before as far as this one has gone.
        if (range == ReportRange.Day)
        {
            var before = db.GetNetDays(TimeUtil.ToUnix(from.AddDays(-UsualDays)), f).Where(d => d.Minutes > 0).Select(d => d.Down).ToList();
            if (before.Count >= MinUsualDays) r.UsualDayDown = Median(before);
        }
        // Only a period recorded from its start: the one network recording began in holds part of what was used, and
        // "less than last month" against it would be false.
        if (ReportBuilder.PreviousPeriod(range, anchor, now) is { } p && db.FirstNetDay() is long firstDay && p.From > TimeUtil.FromUnix(firstDay))
        {
            // Whole days from their totals, and the part of a day a period in progress ends in from its minutes (the
            // day's total would set this week's Monday morning against the whole of last Monday).
            long pf = TimeUtil.ToUnix(p.From), pt = TimeUtil.ToUnix(p.To), lastDay = TimeUtil.ToUnix(p.To.Date);
            long previous = range == ReportRange.Day ? db.GetNetMinutes(pf, pt).Sum(m => m.Down)
                : db.GetNetDays(pf, lastDay).Sum(d => d.Down) + (pt > lastDay ? db.GetNetMinutes(lastDay, pt).Sum(m => m.Down) : 0);
            if (previous > 0) r.PreviousDown = previous;
        }

        r.Insights = NetInsights.Generate(db, r, gamesPlayed ?? []);
        return r;
    }

    /// <summary>The games played (a minute or more in front) from <paramref name="from"/> to <paramref name="to"/>, by name.</summary>
    public static List<string> GamesPlayed(RigsightDb db, DateTime from, DateTime to, IReadOnlyDictionary<long, AppRow> apps, RigsightSettings settings) =>
        [.. db.GetAppTotals(TimeUtil.ToUnix(from), TimeUtil.ToUnix(to))
            .Where(h => h.FgSec >= 60 && apps.TryGetValue(h.AppId, out var a) && (settings.AppCategories.TryGetValue(a.Exe, out var c) ? c : a.Category) == AppCategory.Game)
            .Select(h => NameOf(apps, settings, h.AppId))];

    /// <summary>"Your usual day": the days with any use in this many before…</summary>
    internal const int UsualDays = 30;
    /// <summary>…at least this many of them.</summary>
    internal const int MinUsualDays = 7;

    internal static long Median(IReadOnlyCollection<long> values)
    {
        var sorted = values.Order().ToList();
        int n = sorted.Count;
        return n == 0 ? 0 : n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
    }

    internal static string NameOf(IReadOnlyDictionary<long, AppRow> apps, RigsightSettings settings, long id)
    {
        if (!apps.TryGetValue(id, out var a)) return "Unknown";
        return settings.AppNames.TryGetValue(a.Exe, out var alias) ? alias : AppCatalog.KnownName(a.Exe) ?? a.Name;
    }
}
