using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Games;
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
    /// <summary>How many apps used anything in it (the bar's hover box says how many it doesn't name).</summary>
    public int AppCount { get; init; }
    /// <summary>The line check in this bar (null: not checked then).</summary>
    public NetLinePing? Ping { get; init; }
    /// <summary>The game in front for most of the hour (an hourly chart only), and its program for the icon.</summary>
    public string? Game { get; init; }
    public string? GamePath { get; init; }
    /// <summary>What Steam started downloading in this bar, by name (its own log).</summary>
    public IReadOnlyList<GameDownload> Steam { get; init; } = [];
    /// <summary>Ping checks were off for the whole of it (stopped from Settings or the Network page).</summary>
    public bool PingOff { get; init; }
    /// <summary>Why nothing at all was recorded in it, where Windows' log says ("PC was off", "PC was asleep"): an
    /// empty bar of a PC that was off isn't "no internet use". Null for a bar with anything in it, or one still to come.</summary>
    public string? OffWhy { get; init; }

    /// <summary>The same bar: the same figures and the same apps with the same use (not the same list object), so a
    /// report read again with nothing new is seen as nothing new and the chart isn't handed its bars afresh.</summary>
    public bool Equals(NetBin? other) =>
        other is not null && Start == other.Start && Down == other.Down && BgDown == other.BgDown && Up == other.Up
        && Background == other.Background && Away == other.Away && UnrecordedMinutes == other.UnrecordedMinutes
        && AppCount == other.AppCount && Ping == other.Ping && Game == other.Game && OffWhy == other.OffWhy && PingOff == other.PingOff && Steam.SequenceEqual(other.Steam)
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

/// <summary>
/// One of the period's biggest hours (a day's report) or days (longer), whole: everything every app moved then, both
/// ways, as the chart's bar for it says, and the apps it was, biggest first. (An app's own biggest hours listed the same
/// day once for each app that was busy in it.)
/// </summary>
public sealed record NetStretch(DateTime Start, DateTime End, long Bytes, IReadOnlyList<NetAppUse> Apps);

/// <summary>How a day of a week, month or year went for the connection: one square of the Connection card's strip.</summary>
public enum NetDayState { Up, Dropped, NotRecorded, ToCome }

/// <summary>A stretch of the Network page's internet strip: up, dropped, PC off, on but not recorded, or still to come.</summary>
public enum NetLinkState { Up, Dropped, Off, NotRecorded, ToCome }

/// <summary>One cell of the internet strip under the Each hour graph (a quarter hour, an hour, a day or a month).</summary>
public sealed record NetLinkCell(DateTime Start, DateTime End, NetLinkState State);

/// <summary>A day of the connection strip: its state, the minutes of it recorded, and its drops in order.</summary>
public sealed record NetDayLink(DateTime Day, NetDayState State, int RecordedMinutes, IReadOnlyList<NetDrop> Drops)
{
    /// <summary>The same day: the same state, minutes and drops (not the same list object), so a report read again
    /// with nothing new hands the strip nothing new.</summary>
    public bool Equals(NetDayLink? other) =>
        other is not null && Day == other.Day && State == other.State && RecordedMinutes == other.RecordedMinutes && Drops.SequenceEqual(other.Drops);

    public override int GetHashCode() => HashCode.Combine(Day, State, RecordedMinutes, Drops.Count);
}

/// <summary>The internet over a period: totals, the chart, every app, big downloads, drops, and what's worth saying.</summary>
public sealed class NetReport
{
    public ReportRange Range { get; init; }
    /// <summary>The chart's bars are hours (a day, and a week on the Network page); else days (a month) or months (a year).</summary>
    public bool Hourly { get; init; }
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
    /// <summary>The period's biggest hours (a day) or days (longer), whole, biggest first: the "Biggest days" card.</summary>
    public List<NetStretch> Biggest { get; set; } = [];
    public List<NetDrop> Drops { get; set; } = [];
    /// <summary>A week's, month's or year's days for the connection strip, its first to its last (empty for a day,
    /// which has <see cref="Quarters"/>).</summary>
    public List<NetDayLink> Days { get; set; } = [];
    /// <summary>The first day internet use was recorded, when it falls inside the period. A year isn't searched for
    /// stretches not recorded (see <see cref="Unrecorded"/>), so this is the part of it the Connection card can say.</summary>
    public DateTime? RecordedSince { get; set; }
    /// <summary>
    /// The internet strip under the Network page's graph, on its time line: a day in quarter hours, a week in hours, a
    /// month in days, a year in months (the 1.2.1 Connection card's strip, back where it's seen; the user, 2026-11-05).
    /// </summary>
    public List<NetLinkCell> Link { get; set; } = [];
    /// <summary>A day's quarter hours (96), each true when the PC was on and online in it: the connection strip.</summary>
    public bool[] Quarters { get; set; } = [];
    /// <summary>The stretches the PC was on with no internet use written and no drop to explain it: not recorded (see
    /// <see cref="RigsightDb.GetNetUnrecorded"/>). They are not zeros.</summary>
    public List<(DateTime Start, DateTime End)> Unrecorded { get; set; } = [];
    public List<Insight> Insights { get; set; } = [];
    /// <summary>The line check over the whole period (null: not checked in it), and the page's sentence about it.</summary>
    public NetLinePing? Line { get; set; }
    public string LineText { get; set; } = "";
    /// <summary>When ping checks were turned off or on in the period, in order, and whether they were off as it began.</summary>
    public List<(DateTime At, bool On)> PingSwitches { get; set; } = [];
    public bool PingOffAtStart { get; set; }
    /// <summary>The first minute ping was ever checked (null: never yet).</summary>
    public DateTime? PingSince { get; set; }
    /// <summary>When ping checks were paused, while they still are (whatever the period): the page's status line says since when.</summary>
    public DateTime? PingPausedSince { get; set; }
    /// <summary>The bars with ping checked, and how many the graph wants before it's drawn (see <see cref="NetLine.BarsToDraw"/>).</summary>
    public int PingBars { get; set; }
    public int PingBarsNeeded { get; set; }
    public bool PingEnough => PingBars >= PingBarsNeeded;
    /// <summary>The period told in lines: the Network page's "What happened" (built only when asked for).</summary>
    public List<NetHappening> Happenings { get; set; } = [];
    /// <summary>
    /// The apps that have a colour of their own on the chart, one per colour in the order of the colours (0: no app has
    /// that colour in this period). See <see cref="NetReportBuilder.Colour"/>.
    /// </summary>
    public List<long> ColorApps { get; set; } = [];
    /// <summary>Who had which colour, newest first ("2:obs64.exe"), as the settings remember it.</summary>
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

    /// <param name="forPage">For the Network page: a week's bars are its hours, and the bars carry the games played and
    /// Steam's downloads, the line check, and "What happened" is told. Reports and Home don't show those.</param>
    public static NetReport Build(RigsightDb db, ReportRange range, DateTime anchor, IReadOnlyDictionary<long, AppRow> apps, RigsightSettings settings,
        IReadOnlyList<string>? gamesPlayed = null, DateTime? asOf = null, bool forPage = false,
        Func<DateTime, IReadOnlyList<Stability.PowerEvent>?>? readPower = null)
    {
        var (from, to) = ReportBuilder.Bounds(range, anchor);
        var now = asOf ?? DateTime.Now;
        bool hourly = range == ReportRange.Day || range == ReportRange.Week && forPage;
        var r = new NetReport { Range = range, From = from, To = to, BuiltAt = now, Hourly = hourly };
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
        if (hourly)
        {
            // An hour by its place from the period's start (a day has 24; a week 168).
            int count = (int)Math.Round((to - from).TotalHours);
            int HourOf(long ts) => Math.Clamp((int)Math.Floor((TimeUtil.FromUnix(ts) - from).TotalHours), 0, count - 1);
            var hours = new NetBin[count];
            for (int h = 0; h < count; h++) hours[h] = new NetBin(from.AddHours(h), 0, 0, 0);
            var quarters = new bool[96];
            var away = new long[count];
            var behind = new long[count];
            foreach (var m in db.GetNetMinutes(f, t))
            {
                var time = TimeUtil.FromUnix(m.Ts);
                if (range == ReportRange.Day) quarters[Math.Clamp((time.Hour * 60 + time.Minute) / 15, 0, 95)] = true;
                int h = HourOf(m.Ts);
                var b = hours[h];
                hours[h] = b with { Down = b.Down + m.Down, BgDown = b.BgDown + m.BgDown + m.AwayDown, Up = b.Up + m.Up };
                behind[h] += m.BgDown + m.BgUp + m.AwayDown + m.AwayUp;
                away[h] += m.AwayDown + m.AwayUp;
            }
            var byHour = db.GetNetAppHours(f, t).ToLookup(u => HourOf(u.Ts));
            r.Bins = [.. hours.Select((b, h) => b with
            {
                Background = behind[h], Away = away[h], Apps = Biggest(byHour[h]), AppCount = byHour[h].Count(u => u.Total > 0),
            })];
            if (range == ReportRange.Day) r.Quarters = quarters;
        }
        else if (range == ReportRange.Year)
        {
            r.Bins = [.. Enumerable.Range(0, 12).Select(i => from.AddMonths(i)).Select(month =>
            {
                var inMonth = days.Where(d => TimeUtil.FromUnix(d.Day) is var day && day >= month && day < month.AddMonths(1)).ToList();
                var monthApps = inMonth.Count == 0 ? [] : db.GetNetAppTotals(TimeUtil.ToUnix(month), TimeUtil.ToUnix(month.AddMonths(1)));
                return new NetBin(month, inMonth.Sum(d => d.Down), inMonth.Sum(d => d.BgDown + d.AwayDown), inMonth.Sum(d => d.Up))
                {
                    Background = inMonth.Sum(d => d.Background), Away = inMonth.Sum(d => d.AwayDown + d.AwayUp),
                    Apps = Biggest(monthApps), AppCount = monthApps.Count(u => u.Total > 0),
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
                    ? new NetBin(day, d.Down, d.Background, d.Up) { Background = d.Behind, Away = d.Away, Apps = Biggest(appDays[day]), AppCount = appDays[day].Count(u => u.Total > 0) }
                    : new NetBin(day, 0, 0, 0))];
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
        Colour(r, settings);
        // Not remembered until there is a week to go by: the biggest users of a PC's first hours (an installer, a
        // Windows update) shouldn't fill the memory. Until then the colours still go to the period's biggest users.
        r.ColorsSettled = db.FirstNetDay() is long began && TimeUtil.FromUnix(began).Date <= now.Date.AddDays(-ColorSettleDays);
        // The biggest stretches: each app's hours (a day) or days (longer), biggest first, both directions counted.
        var stretch = hourly ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        r.Downloads = [.. busiest.Where(u => u.Total >= MinBiggest && apps.ContainsKey(u.App)).OrderByDescending(u => u.Total).Take(DownloadsShown).Select(u =>
            new NetDownload(NameOf(apps, settings, u.App), apps[u.App].Exe, apps[u.App].Path, TimeUtil.FromUnix(u.Ts), TimeUtil.FromUnix(u.Ts) + stretch, u.Total, 0))];
        // The biggest hours or days, whole: the chart's own bars, so a row and its bar say the same (a year's bars are
        // months: its days are added up here).
        IEnumerable<NetBin> wholes = range == ReportRange.Year ? YearDays(db, days, f, t) : r.Bins;
        r.Biggest = [.. wholes.Where(b => b.Total >= MinBiggest).OrderByDescending(b => b.Total).Take(DownloadsShown)
            .Select(b => new NetStretch(b.Start, b.Start + stretch, b.Total, b.Apps))];
        r.Drops = db.GetNetDrops(f, t);
        if (range != ReportRange.Day)
        {
            r.Days = DayLinks(from, to, now, days, r.Drops);
            if (db.FirstNetDay() is long firstNet && TimeUtil.FromUnix(firstNet).Date > from) r.RecordedSince = TimeUtil.FromUnix(firstNet).Date;
        }
        // Not recorded: the PC on, nothing written, and no drop that says why. A year's bars are months: not looked for.
        if (range != ReportRange.Year)
        {
            r.Unrecorded = [.. db.GetNetUnrecorded(f, Math.Min(t, TimeUtil.ToUnix(now)))
                .Where(u => !r.Drops.Any(d => d.Start < u.End && d.End > u.Start))
                .Select(u => (TimeUtil.FromUnix(u.Start), TimeUtil.FromUnix(u.End)))];
            var length = hourly ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
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
        if (forPage) ForPage(db, r, apps, settings, f, t, readPower ?? Stability.CrashLogReader.ReadPower);
        return r;
    }

    /// <summary>
    /// What only the Network page shows: on each bar the line check, the game played (hours) and what Steam started
    /// downloading; the period's line check and its sentence; and "What happened".
    /// </summary>
    private static void ForPage(RigsightDb db, NetReport r, IReadOnlyDictionary<long, AppRow> apps, RigsightSettings settings, long f, long t,
        Func<DateTime, IReadOnlyList<Stability.PowerEvent>?> readPower)
    {
        DateTime End(NetBin b) => r.Hourly ? b.Start.AddHours(1) : r.Range == ReportRange.Year ? b.Start.AddMonths(1) : b.Start.AddDays(1);
        var pings = db.GetNetPings(f, t);
        r.Line = NetLine.Of(pings);
        r.PingSince = db.FirstNetPing() is long firstPing ? TimeUtil.FromUnix(firstPing) : null;
        var switches = db.GetNetPingSwitches(f, t);
        r.PingOffAtStart = switches.Count > 0 && switches[0].Ts < f && !switches[0].On;
        r.PingSwitches = [.. switches.Where(s => s.Ts >= f).Select(s => (TimeUtil.FromUnix(s.Ts), s.On))];
        long nowTs = TimeUtil.ToUnix(r.BuiltAt);
        r.PingPausedSince = db.GetNetPingSwitches(nowTs, nowTs + 1) is { Count: > 0 } latest && !latest[^1].On ? TimeUtil.FromUnix(latest[^1].Ts) : null;
        var steam = db.GetSteamDownloads(f, t).Where(d => d.Started >= r.From && d.Started < r.To && d.Bytes >= NetHappenings.MinSteamBytes).ToList();
        // The game in front for most of each hour (ten minutes or more of it).
        var games = new Dictionary<DateTime, (string Name, string? Path)>();
        if (r.Hourly)
            foreach (var g in db.GetAppHours(f, t).Where(h => h.FgSec >= NetHappenings.PlayedHourSeconds && apps.TryGetValue(h.AppId, out var a)
                         && (settings.AppCategories.TryGetValue(a.Exe, out var c) ? c : a.Category) == AppCategory.Game).GroupBy(h => TimeUtil.FromUnix(h.Ts)))
            {
                var top = g.MaxBy(h => h.FgSec)!;
                games[g.Key] = (NameOf(apps, settings, top.AppId), apps[top.AppId].Path);
            }
        // Whether ping checks were on at a moment: the last switch before it says (none: on, as they start).
        bool OnAt(DateTime at)
        {
            bool on = !r.PingOffAtStart;
            foreach (var s in r.PingSwitches) if (s.At <= at) on = s.On;
            return on;
        }
        r.Bins = [.. r.Bins.Select(b =>
        {
            long bf = TimeUtil.ToUnix(b.Start), bt = TimeUtil.ToUnix(End(b));
            var game = games.TryGetValue(b.Start, out var x) ? x : default;
            var ping = NetLine.Of(pings.Where(p => p.Ts >= bf && p.Ts < bt));
            bool off = ping is null && b.Start < r.BuiltAt && !OnAt(b.Start) && !r.PingSwitches.Any(s => s.On && s.At > b.Start && s.At < End(b));
            return b with
            {
                Ping = ping, PingOff = off,
                Game = game.Name, GamePath = game.Path,
                Steam = [.. steam.Where(d => d.Started >= b.Start && d.Started < End(b))],
            };
        })];
        r.PingBars = r.Bins.Count(b => b.Ping is not null);
        r.PingBarsNeeded = NetLine.BarsToDraw(r.Hourly, r.Range);
        if (r.Range != ReportRange.Year) r.Bins = OffReasons(db, r, f, t, readPower);
        r.Link = LinkCells(db, r, f, t);
        r.LineText = NetLine.Sentence(r, r.Apps.ToDictionary(a => a.Id, a => a.Name));
        r.Happenings = NetHappenings.Build(db, r, apps, settings);
    }

    /// <summary>
    /// The internet strip: each cell dropped when a drop touches it, off when the PC wasn't on in any minute of it, not
    /// recorded when it lies in a stretch with nothing written, still to come after now, otherwise up.
    /// </summary>
    private static List<NetLinkCell> LinkCells(RigsightDb db, NetReport r, long f, long t)
    {
        TimeSpan step = r.Range switch { ReportRange.Day => TimeSpan.FromMinutes(15), ReportRange.Week => TimeSpan.FromHours(1), _ => TimeSpan.FromDays(1) };
        var cells = new List<(DateTime Start, DateTime End)>();
        if (r.Range == ReportRange.Year) for (var m = r.From; m < r.To; m = m.AddMonths(1)) cells.Add((m, m.AddMonths(1)));
        else for (var c = r.From; c < r.To; c += step) cells.Add((c, c + step));
        var drops = r.Drops.Select(d => (From: TimeUtil.FromUnix(d.Start), To: TimeUtil.FromUnix(d.End))).ToList();
        // Which cells the PC was on in: its minutes (a year's months by their days with any use, not 500,000 minutes).
        var on = new HashSet<int>();
        if (r.Range == ReportRange.Year)
        {
            for (int i = 0; i < cells.Count && i < r.Bins.Count; i++) if (r.Bins[i].Total > 0) on.Add(i);
        }
        else
            foreach (var ts in db.GetMinuteTimes(f, Math.Min(t, TimeUtil.ToUnix(r.BuiltAt) + 60)))
                on.Add((int)((TimeUtil.FromUnix(ts) - r.From) / step));
        var list = new List<NetLinkCell>(cells.Count);
        for (int i = 0; i < cells.Count; i++)
        {
            var (from, to) = cells[i];
            var state = from >= r.BuiltAt ? NetLinkState.ToCome
                : drops.Any(d => d.From < to && d.To > from) ? NetLinkState.Dropped
                : !on.Contains(i) ? NetLinkState.Off
                : r.Unrecorded.Any(u => u.Start <= from && u.End >= (to < r.BuiltAt ? to : r.BuiltAt)) ? NetLinkState.NotRecorded
                : NetLinkState.Up;
            list.Add(new NetLinkCell(from, to, state));
        }
        return list;
    }

    /// <summary>
    /// The bars of a stretch the PC wasn't recorded at all (no minute of it), each with why, from Windows' log: the
    /// stretch taken whole, from the last minute recorded before it to the first after (see <see cref="WhyEmpty"/>).
    /// </summary>
    private static List<NetBin> OffReasons(RigsightDb db, NetReport r, long f, long t, Func<DateTime, IReadOnlyList<Stability.PowerEvent>?> readPower)
    {
        var bins = r.Bins;
        TimeSpan length = r.Hourly ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        var on = db.GetMinuteTimes(f, Math.Min(t, TimeUtil.ToUnix(r.BuiltAt)));
        bool Off(NetBin b) => b.Start + length <= r.BuiltAt && b.Total == 0 && !on.Any(ts => ts >= TimeUtil.ToUnix(b.Start) && ts < TimeUtil.ToUnix(b.Start + length));
        var off = bins.Select(Off).ToArray();
        if (!off.Any(x => x)) return bins;
        IReadOnlyList<Stability.PowerEvent>? power = (r.BuiltAt - r.From).TotalDays <= WhyEmpty.PowerLogDays ? readPower(r.From.AddDays(-1)) : null;
        var result = bins.ToList();
        for (int i = 0; i < bins.Count;)
        {
            if (!off[i]) { i++; continue; }
            int start = i;
            while (i < bins.Count && off[i]) i++;
            long a = TimeUtil.ToUnix(bins[start].Start), b = TimeUtil.ToUnix(bins[i - 1].Start + length);
            var gapFrom = db.LastMinuteBefore(a) is long last ? TimeUtil.FromUnix(last + 60) : bins[start].Start;
            var gapTo = db.FirstMinuteFrom(b) is long next ? TimeUtil.FromUnix(next) : r.BuiltAt;
            string? why = Stability.PowerLog.Words(Stability.PowerLog.Reason(gapFrom, gapTo, power, TimeSpan.FromMinutes(3)));
            if (why is null) continue;
            for (int k = start; k < i; k++) result[k] = bins[k] with { OffWhy = why };
        }
        return result;
    }

    /// <summary>The games played (a minute or more in front) from <paramref name="from"/> to <paramref name="to"/>, by name.</summary>
    public static List<string> GamesPlayed(RigsightDb db, DateTime from, DateTime to, IReadOnlyDictionary<long, AppRow> apps, RigsightSettings settings) =>
        [.. db.GetAppTotals(TimeUtil.ToUnix(from), TimeUtil.ToUnix(to))
            .Where(h => h.FgSec >= 60 && apps.TryGetValue(h.AppId, out var a) && (settings.AppCategories.TryGetValue(a.Exe, out var c) ? c : a.Category) == AppCategory.Game)
            .Select(h => NameOf(apps, settings, h.AppId))];

    /// <summary>The apps with a colour of their own on the chart (see <see cref="NetReport.ColorApps"/>).</summary>
    public const int ColoredApps = 4;

    /// <summary>
    /// The colours: the period's biggest users get them (the user, 2026-11-05: OBS Studio, the day's biggest by far, was
    /// grey because the colours went to the last month's biggest), so the biggest thing on the graph always has one.
    /// Each keeps the colour it had before where it can (<see cref="RigsightSettings.NetColorApps"/> remembers who had
    /// which), so an app is the same colour on every period it's coloured in, and two apps never share one in a view.
    /// One with no colour yet, or whose colour another has here, takes a free one, and that's remembered for it.
    /// </summary>
    internal static void Colour(NetReport r, RigsightSettings settings)
    {
        var remembered = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in settings.NetColorApps)
            if (RigsightSettings.ParseNetColor(entry) is { } e) remembered.TryAdd(e.Exe, e.Slot);
        var top = r.Apps.Where(a => a.Use.Total >= 1 << 20).Take(ColoredApps).ToList();
        var slots = new long[ColoredApps]; // 0: the colour is free (app ids start at 1)
        var waiting = new List<NetAppStat>();
        foreach (var a in top)
            if (remembered.TryGetValue(a.Exe, out int slot) && slot < ColoredApps && slots[slot] == 0) slots[slot] = a.Id;
            else waiting.Add(a);
        foreach (var a in waiting)
        {
            // A free colour no other app remembers first, else any free one.
            int free = Enumerable.Range(0, ColoredApps).Where(i => slots[i] == 0).OrderBy(i => remembered.ContainsValue(i) ? 1 : 0).First();
            slots[free] = a.Id;
        }
        r.ColorApps = [.. slots];
        // Newest first: this view's apps, then everyone remembered before, so the memory keeps the apps seen lately.
        var seen = top.Select(a => $"{Array.IndexOf(slots, a.Id)}:{a.Exe}").ToList();
        r.ColorExes = [.. seen.Concat(settings.NetColorApps.Where(e => RigsightSettings.ParseNetColor(e) is { } x && !top.Any(a => a.Exe.Equals(x.Exe, StringComparison.OrdinalIgnoreCase))))
            .Take(RigsightSettings.MaxNetColorApps)];
    }
    /// <summary>The days of history there must be before the colours are remembered.</summary>
    public const int ColorSettleDays = 7;

    /// <summary>A year's days with any use, as bars of a day each with their apps (the year's chart has months).</summary>
    private static IEnumerable<NetBin> YearDays(RigsightDb db, List<NetDay> days, long from, long to)
    {
        var appDays = db.GetNetAppDays(from, to).ToLookup(u => TimeUtil.FromUnix(u.Ts).Date);
        return days.GroupBy(d => TimeUtil.FromUnix(d.Day).Date)
            .Select(g => new NetBin(g.Key, g.Sum(d => d.Down), 0, g.Sum(d => d.Up)) { Apps = Biggest(appDays[g.Key]) });
    }

    /// <summary>
    /// Each day from <paramref name="from"/> to <paramref name="to"/> for the connection strip: still to come after
    /// today; dropped when a drop began in it; up when any internet use was recorded in it; else not recorded (the PC
    /// off, or nothing being read). The minutes recorded are the day's minutes with use written, already added up in
    /// net_day: nothing more is read for them. A drop belongs to the day it began in.
    /// </summary>
    internal static List<NetDayLink> DayLinks(DateTime from, DateTime to, DateTime now, IEnumerable<NetDay> days, IEnumerable<NetDrop> drops)
    {
        // A date has two rows only where the PC's time zone changed during it: added up.
        var minutes = days.GroupBy(d => TimeUtil.FromUnix(d.Day).Date).ToDictionary(g => g.Key, g => g.Sum(d => d.Minutes));
        var dropsOn = drops.ToLookup(d => TimeUtil.FromUnix(d.Start).Date);
        var list = new List<NetDayLink>();
        for (var day = from.Date; day < to; day = day.AddDays(1))
        {
            int recorded = minutes.GetValueOrDefault(day);
            var dropped = dropsOn[day].OrderBy(d => d.Start).ToList();
            var state = day > now.Date ? NetDayState.ToCome
                : dropped.Count > 0 ? NetDayState.Dropped
                : recorded > 0 ? NetDayState.Up
                : NetDayState.NotRecorded;
            list.Add(new NetDayLink(day, state, state == NetDayState.ToCome ? 0 : recorded, dropped));
        }
        return list;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    /// <summary>A bar's apps, biggest first, without the ones that moved next to nothing.</summary>
    // Every app with any use, biggest first: the ones under 1 MB are listed too, together under one row (the user,
    // 2026-11-05: "30 other apps" could be seen nowhere).
    private static List<NetAppUse> Biggest(IEnumerable<NetAppUse> uses) => [.. uses.Where(u => u.Total > 0).OrderByDescending(u => u.Total)];

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
        if (!apps.TryGetValue(id, out var a)) return Lang.T("Unknown");
        return settings.AppNames.TryGetValue(a.Exe, out var alias) ? alias : AppCatalog.KnownName(a.Exe) ?? a.Name;
    }
}
