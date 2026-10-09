using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;

namespace Rigsight.Core.Reports;

/// <summary>One bar of the internet chart: an hour (a day's chart), a day (a week's or month's) or a month (a year's).</summary>
/// <param name="BgDown">The part of the download by apps that weren't in front (someone at the PC or not).</param>
public sealed record NetBin(DateTime Start, long Down, long BgDown, long Up)
{
    /// <summary>Everything that moved, both ways.</summary>
    public long Total => Down + Up;
    /// <summary>What moved, both ways, while the app doing it wasn't in front; and the part of that with nobody at the PC.</summary>
    public long Background { get; init; }
    public long Away { get; init; }
    /// <summary>Minutes of this bar in which the PC was on and nothing was recorded (see <see cref="NetReport.Unrecorded"/>):
    /// what the bar shows is then only part of what was used, and an empty one isn't "nothing used".</summary>
    public int UnrecordedMinutes { get; init; }
    /// <summary>Each app's use in this bar, biggest first: who the bar is.</summary>
    public IReadOnlyList<NetAppUse> Apps { get; init; } = [];

    /// <summary>The same bar: the same figures and the same apps with the same use (not the same list object), so a
    /// report read again with nothing new is seen as nothing new and the chart isn't handed its bars afresh.</summary>
    public bool Equals(NetBin? other) =>
        other is not null && Start == other.Start && Down == other.Down && BgDown == other.BgDown && Up == other.Up
        && Background == other.Background && Away == other.Away && UnrecordedMinutes == other.UnrecordedMinutes
        && Apps.Count == other.Apps.Count
        && Apps.Zip(other.Apps).All(p => p.First.App == p.Second.App && p.First.Down == p.Second.Down && p.First.Up == p.Second.Up && p.First.Background == p.Second.Background);

    public override int GetHashCode() => HashCode.Combine(Start, Down, Up, Background);
}

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

/// <summary>
/// One of the period's biggest stretches of use: an app in one hour (a day's report) or on one day (longer), with
/// everything it moved then, download and upload. Any sort of use counts the same: a game downloading, a film watched,
/// a long call, a backup going up. (It used to be the recorder of big downloads, which only saw what came in at 1 MB a
/// second or more.)
/// </summary>
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
    /// <summary>The stretches the PC was on with no internet use written and no drop to explain it: not recorded (see
    /// <see cref="RigsightDb.GetNetUnrecorded"/>). They are not zeros.</summary>
    public List<(DateTime Start, DateTime End)> Unrecorded { get; set; } = [];
    public List<Insight> Insights { get; set; } = [];
    /// <summary>
    /// The apps that have a colour of their own on the chart, in the order of the colours. An app keeps its colour while
    /// it goes on using the internet (the settings remember them, see <see cref="RigsightSettings.NetColorApps"/>); a
    /// colour given up, or still free, goes to the biggest user of the last month that has none.
    /// </summary>
    public List<long> ColorApps { get; set; } = [];
    /// <summary>The same apps by exe, as the settings keep them.</summary>
    public List<string> ColorExes { get; set; } = [];
    /// <summary>Whether there is a week of history behind the choice: only then is it remembered.</summary>
    public bool ColorsSettled { get; set; }
}

/// <summary>Reads a <see cref="NetReport"/> from the network tables, and says what's worth saying about it.</summary>
public static class NetReportBuilder
{
    /// <summary>The least an app must have moved in an hour (or a day) to be listed among the biggest.</summary>
    public const long MinBiggest = 50L << 20;

    /// <summary>How many of the biggest stretches a report lists.</summary>
    public const int DownloadsShown = 5;

    public static NetReport Build(RigsightDb db, ReportRange range, DateTime anchor, IReadOnlyDictionary<long, AppRow> apps, RigsightSettings settings,
        IReadOnlyList<string>? gamesPlayed = null, DateTime? asOf = null)
    {
        var (from, to) = ReportBuilder.Bounds(range, anchor);
        var now = asOf ?? DateTime.Now;
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
            var away = new long[24];
            var behind = new long[24];
            foreach (var m in db.GetNetMinutes(f, t))
            {
                int h = Math.Clamp(TimeUtil.FromUnix(m.Ts).Hour, 0, 23);
                behind[h] += m.BgDown + m.BgUp + m.AwayDown + m.AwayUp;
                away[h] += m.AwayDown + m.AwayUp;
            }
            var byHour = db.GetNetAppHours(f, t).ToLookup(u => Math.Clamp(TimeUtil.FromUnix(u.Ts).Hour, 0, 23));
            r.Bins = [.. hours.Select((b, h) => b with { Background = behind[h], Away = away[h], Apps = Biggest(byHour[h]) })];
            r.Quarters = quarters;
        }
        else if (range == ReportRange.Year)
        {
            r.Bins = [.. Enumerable.Range(0, 12).Select(i => from.AddMonths(i)).Select(month =>
            {
                var inMonth = days.Where(d => TimeUtil.FromUnix(d.Day) is var day && day >= month && day < month.AddMonths(1)).ToList();
                return new NetBin(month, inMonth.Sum(d => d.Down), inMonth.Sum(d => d.BgDown + d.AwayDown), inMonth.Sum(d => d.Up))
                {
                    Background = inMonth.Sum(d => d.Background), Away = inMonth.Sum(d => d.AwayDown + d.AwayUp),
                    Apps = inMonth.Count == 0 ? [] : Biggest(db.GetNetAppTotals(TimeUtil.ToUnix(month), TimeUtil.ToUnix(month.AddMonths(1)))),
                };
            })];
        }
        else
        {
            // A date has two rows only where the PC's time zone changed during it (each zone's midnight began one): added up.
            var byDay = days.GroupBy(d => TimeUtil.FromUnix(d.Day).Date)
                .ToDictionary(g => g.Key, g => (Down: g.Sum(d => d.Down), Background: g.Sum(d => d.BgDown + d.AwayDown), Up: g.Sum(d => d.Up),
                    Behind: g.Sum(d => d.Background), Away: g.Sum(d => d.AwayDown + d.AwayUp)));
            var appDays = db.GetNetAppDays(f, t).ToLookup(u => TimeUtil.FromUnix(u.Ts).Date);
            r.Bins = [.. Enumerable.Range(0, (int)(to - from).TotalDays).Select(i => from.AddDays(i)).Select(day =>
                byDay.TryGetValue(day, out var d)
                    ? new NetBin(day, d.Down, d.Background, d.Up) { Background = d.Behind, Away = d.Away, Apps = Biggest(appDays[day]) }
                    : new NetBin(day, 0, 0, 0))];
        }

        // The apps with a colour. An app that has one keeps it for as long as it goes on using the internet; one that
        // hasn't for a month gives its colour up, and a free colour goes to the biggest user of the last month that
        // has none. So two apps never trade colours, and the colours follow a PC whose habits change.
        long monthAgo = TimeUtil.ToUnix(now.Date.AddDays(-ColorDays)), tomorrow = TimeUtil.ToUnix(now.Date.AddDays(1));
        var lately = db.GetNetAppTotals(monthAgo, tomorrow).Where(u => u.Total >= 1 << 20 && apps.ContainsKey(u.App))
            .OrderByDescending(u => u.Total).Select(u => apps[u.App].Exe).ToList();
        var slots = settings.NetColorApps.Take(ColoredApps).Select(exe => lately.Contains(exe, StringComparer.OrdinalIgnoreCase) ? exe : null).ToList();
        var waiting = new Queue<string>(lately.Where(exe => !slots.Contains(exe, StringComparer.OrdinalIgnoreCase)));
        for (int i = 0; i < ColoredApps; i++)
        {
            if (i < slots.Count && slots[i] is not null) continue;
            if (!waiting.TryDequeue(out var next)) break;
            if (i < slots.Count) slots[i] = next;
            else slots.Add(next);
        }
        r.ColorExes = [.. slots.OfType<string>()];
        r.ColorApps = [.. r.ColorExes.Select(exe => apps.Values.First(a => a.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)).Id)];
        // Not remembered until there is a week to go by: the biggest users of a PC's first hours (an installer, a
        // Windows update) shouldn't hold the colours for good. Until then they go by use, and can still change hands.
        r.ColorsSettled = db.FirstNetDay() is long began && TimeUtil.FromUnix(began).Date <= now.Date.AddDays(-ColorSettleDays);

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
        // The biggest stretches: each app's hours (a day) or days (longer), biggest first, both directions counted.
        var stretch = range == ReportRange.Day ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        r.Downloads = [.. busiest.Where(u => u.Total >= MinBiggest && apps.ContainsKey(u.App)).OrderByDescending(u => u.Total).Take(DownloadsShown).Select(u =>
            new NetDownload(NameOf(apps, settings, u.App), apps[u.App].Exe, apps[u.App].Path, TimeUtil.FromUnix(u.Ts), TimeUtil.FromUnix(u.Ts) + stretch, u.Total, 0))];
        r.Drops = db.GetNetDrops(f, t);
        // Not recorded: the PC on, nothing written, and no drop that says why. A year's bars are months: not looked for.
        if (range != ReportRange.Year)
        {
            r.Unrecorded = [.. db.GetNetUnrecorded(f, Math.Min(t, TimeUtil.ToUnix(now)))
                .Where(u => !r.Drops.Any(d => d.Start < u.End && d.End > u.Start))
                .Select(u => (TimeUtil.FromUnix(u.Start), TimeUtil.FromUnix(u.End)))];
            var length = range == ReportRange.Day ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
            if (r.Unrecorded.Count > 0)
                r.Bins = [.. r.Bins.Select(bin => bin with
                {
                    UnrecordedMinutes = (int)Math.Round(r.Unrecorded.Sum(u => Math.Max(0, (Min(u.End, bin.Start + length) - Max(u.Start, bin.Start)).TotalMinutes))),
                })];
        }

        // Against before: a day against a usual day; longer, the period before as far as this one has gone.
        if (range == ReportRange.Day)
        {
            // A day with an hour or more not recorded holds part of what was used: it isn't anyone's usual day.
            long usualFrom = TimeUtil.ToUnix(from.AddDays(-UsualDays));
            var partDays = db.GetNetUnrecorded(usualFrom, f).GroupBy(u => TimeUtil.FromUnix(u.Start).Date)
                .Where(g => g.Sum(u => u.End - u.Start) >= UnrecordedDaySeconds).Select(g => g.Key).ToHashSet();
            var before = db.GetNetDays(usualFrom, f).Where(d => d.Minutes > 0 && !partDays.Contains(TimeUtil.FromUnix(d.Day).Date)).Select(d => d.Down).ToList();
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

    /// <summary>The apps with a colour of their own on the chart (see <see cref="NetReport.ColorApps"/>).</summary>
    public const int ColoredApps = RigsightSettings.MaxNetColorApps;
    /// <summary>The days of use the colours go by: an app with none in them gives its colour up.</summary>
    public const int ColorDays = 30;
    /// <summary>The days of history there must be before the colours are remembered.</summary>
    public const int ColorSettleDays = 7;

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    /// <summary>A bar's apps, biggest first, without the ones that moved next to nothing.</summary>
    private static List<NetAppUse> Biggest(IEnumerable<NetAppUse> uses) => [.. uses.Where(u => u.Total >= 1 << 20).OrderByDescending(u => u.Total)];

    /// <summary>A day with this long not recorded (seconds) isn't counted among the usual days.</summary>
    internal const int UnrecordedDaySeconds = 3600;

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
