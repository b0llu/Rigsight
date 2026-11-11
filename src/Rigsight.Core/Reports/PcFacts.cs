using Rigsight.Core.Data;
using Rigsight.Core.Games;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Reports;

/// <summary>Which of RigSight's records a widget's tiles show (so only those are read).</summary>
[Flags]
public enum PcNeeds
{
    None = 0,
    Today = 1,
    Yesterday = 2,
    Week = 4,
    Crashes = 8,
    Health = 16,
    Changes = 32,
    LastGame = 64,
    Internet = 128,
}

/// <summary>An app on a tile's list: its name and program (for its icon), its figure as written, and its share of the first one's (for a bar).</summary>
public sealed record AppLine(string Name, string? Path, string Amount, double Share);

/// <summary>A line of PC health: its icon (Windows' icon font), the statement, and whether it needs a look (said in amber).</summary>
public sealed record HealthLine(string Icon, string Text, bool NeedsLook);

/// <summary>The last game played: its name and program, when its play ended, how long it was in front, and its average frame rate.</summary>
public sealed record LastPlayLine(string Name, string? Path, DateTime Ended, double ActiveSec, double? Fps);

/// <summary>
/// What the widgets' tiles about the PC's day and its internet show from RigSight's records (the PC tiles mock-up,
/// 2026-11-11), each read with the reader its page uses: Home's (today, PC health, what changed), Reports' (yesterday,
/// the last 7 days), Crashes', Games' and the Network page's. Read off the drawing thread, about once a minute, and only
/// what a widget that is on shows (<see cref="NeedsOf"/>).
/// </summary>
public sealed class PcFacts
{
    /// <summary>When it was read, and what it holds.</summary>
    public DateTime ReadAt { get; set; }
    public PcNeeds Holds { get; set; }

    // Today, as Home has it.
    public List<AppLine> MostUsed { get; set; } = [];
    public List<string> StandOut { get; set; } = [];

    // Yesterday, as Home's recap has it; why there's nothing when there's nothing ("Your PC was off yesterday.").
    public bool YesterdayHasData { get; set; }
    public double YesterdayActiveSec { get; set; }
    public List<AppLine> YesterdayApps { get; set; } = [];
    public string? YesterdayWhy { get; set; }

    /// <summary>The screen time of each of the 6 days before today, oldest first (today's comes live).</summary>
    public List<(DateTime Day, double ActiveSec)> Days { get; set; } = [];

    // The last 30 days' crashes, as the Crashes page lists them, and the latest in a sentence.
    public int Crashes { get; set; }
    public string? LatestCrash { get; set; }

    public List<HealthLine> Health { get; set; } = [];
    public List<ChangeCardLine> Changes { get; set; } = [];

    public LastPlayLine? LastGame { get; set; }

    // Today's internet, as the Network page has it.
    public bool NetHasData { get; set; }
    public long NetDown { get; set; }
    public long NetUp { get; set; }
    public long NetBackground { get; set; }
    public List<AppLine> NetApps { get; set; } = [];
    /// <summary>Each hour of today (24): what its biggest app used, and the rest of the hour's use.</summary>
    public long[] HourTop { get; set; } = new long[24];
    public long[] HourRest { get; set; } = new long[24];
    /// <summary>Today's quarter hours (96), each true when the PC was on and online in it (the Network page's strip).</summary>
    public bool[] Quarters { get; set; } = [];
    public List<(DateTime Start, DateTime End)> Drops { get; set; } = [];

    // Kept from one reading to the next: what's slow to work out and changes rarely.
    internal Dictionary<DateTime, ChangeResult>? Results { get; set; }
    internal DateTime ResultsAt { get; set; }
    internal DateTime PastAt { get; set; }
    internal (string Key, DateTime? Played)? GameKey { get; set; }
    internal Report? TodayReport { get; set; }
    internal DateTime TodayAt { get; set; }

    /// <summary>The most apps a list keeps (more than any tile shows).</summary>
    public const int ListMost = 8;

    /// <summary>How often the records are read while a tile needs them.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    /// <summary>How long the slow parts are kept: big changes' results, and the days before today.</summary>
    private static readonly TimeSpan ResultsFor = TimeSpan.FromMinutes(10), PastFor = TimeSpan.FromMinutes(30);

    /// <summary>How long today's report is kept (Most used, What stands out and PC health come from it).</summary>
    public static readonly TimeSpan TodayFor = TimeSpan.FromMinutes(5);

    /// <summary>What these tiles read from the records (None for every other kind).</summary>
    public static PcNeeds NeedsOf(string kind) => kind switch
    {
        WidgetTiles.MostUsed or WidgetTiles.StandOut => PcNeeds.Today,
        WidgetTiles.Health => PcNeeds.Today | PcNeeds.Health,
        WidgetTiles.Changed => PcNeeds.Changes,
        WidgetTiles.Yesterday => PcNeeds.Yesterday,
        WidgetTiles.Week => PcNeeds.Week,
        WidgetTiles.Crashes => PcNeeds.Crashes,
        WidgetTiles.LastGame => PcNeeds.LastGame,
        WidgetTiles.NetToday or WidgetTiles.NetApps or WidgetTiles.NetHours or WidgetTiles.NetDrops => PcNeeds.Internet,
        _ => PcNeeds.None,
    };

    public static PcNeeds NeedsOf(IEnumerable<WidgetConfig> widgets)
    {
        var needs = PcNeeds.None;
        foreach (var w in widgets)
            foreach (var t in w.Tiles ?? [])
                needs |= NeedsOf(t.Kind);
        return needs;
    }

    /// <summary>
    /// Reads what <paramref name="needs"/> asks for. <paramref name="before"/> (the last reading) lends what's slow and
    /// still fresh. <paramref name="drives"/> and <paramref name="fullest"/> are the drives as PC health judges them.
    /// </summary>
    public static PcFacts Read(RigsightDb db, RigsightSettings s, DateTime now, PcNeeds needs, IReadOnlyCollection<DriveCheck>? drives = null,
        string? fullest = null, PcFacts? before = null)
    {
        var f = new PcFacts { ReadAt = now, Holds = needs };
        var apps = db.LoadApps().ToDictionary(a => a.Id);
        var today = now.Date;

        // Today's report is the dearest part (about half a second of CPU on a PC with weeks of history): read again every
        // few minutes, and kept in between.
        Report? report = null;
        if ((needs & (PcNeeds.Today | PcNeeds.Health)) != 0)
        {
            if (before?.TodayReport is { } kept && before.TodayAt.Date == today && now - before.TodayAt < TodayFor)
                (report, f.TodayAt) = (kept, before.TodayAt);
            else
                (report, f.TodayAt) = (ReportBuilder.Build(db, ReportRange.Day, today, s), now);
            f.TodayReport = report;
        }
        if (report is not null)
        {
            var top = HomeCards.TopToday(report);
            double most = Math.Max(1, top.FirstOrDefault()?.ActiveSec ?? 1);
            f.MostUsed = [.. top.Select(a => new AppLine(a.Name, a.Path, Units.Duration(a.ActiveSec), a.ActiveSec / most))];
            f.StandOut = [.. HomeCards.StandOut(report).Select(i => i.Line)];
        }

        HomeCards.Records? records = (needs & (PcNeeds.Health | PcNeeds.Changes)) != 0 ? HomeCards.Read(db, s, now) : null;
        if (records is not null)
        {
            if (before?.Results is { } kept && now - before.ResultsAt < ResultsFor && before.ResultsAt.Date == today)
                (f.Results, f.ResultsAt) = (kept, before.ResultsAt);
            else
                (f.Results, f.ResultsAt) = (ChangeEffects.Read(db, s), now); // as the pages have them (ReportService.ChangeResultsAsync)
            // As Home: the changes card's lines, and PC health without a change the changes card already says.
            var lines = HomeCards.Changes(records.Changes, today, f.Results, ListMost);
            f.Changes = lines;
            if ((needs & PcNeeds.Health) != 0)
            {
                var worse = HomeCards.Worse(f.Results, today);
                if (worse is not null && HomeCards.Changes(records.Changes, today, f.Results).Any(l => l.ChangeDay == worse.Day)) worse = null;
                var parts = HomeCards.Health(report, records.Problems, records.Mains, drives ?? [], fullest, records.MainsLikely);
                var health = parts.Select(i => new HealthLine(i.Icon, i.Line, HomeCards.NeedsLook(i))).ToList();
                f.Health = HomeCards.HealthOrder(health, worse is null ? null : new HealthLine("", worse.Said, true), HomeCards.HeatSaid(parts), l => l.NeedsLook);
            }
        }

        // Yesterday and the days before today change only as the day turns: kept for half an hour.
        bool pastKept = before is not null && before.PastAt.Date == today && now - before.PastAt < PastFor;
        if ((needs & PcNeeds.Yesterday) != 0)
        {
            if (pastKept && (before!.Holds & PcNeeds.Yesterday) != 0)
                (f.YesterdayHasData, f.YesterdayActiveSec, f.YesterdayApps, f.YesterdayWhy) = (before.YesterdayHasData, before.YesterdayActiveSec, before.YesterdayApps, before.YesterdayWhy);
            else
            {
                var y = ReportBuilder.BuildRaw(db, ReportRange.Day, today.AddDays(-1), today, apps, s);
                f.YesterdayHasData = y.HasData;
                f.YesterdayActiveSec = y.ActiveSec;
                f.YesterdayApps = [.. HomeCards.TopApps(y, ListMost).Select(a => new AppLine(a.Name, a.Path, Units.Duration(a.ActiveSec), 0))];
                if (!y.HasData)
                {
                    string when = Lang.T("yesterday");
                    var (kind, began) = WhyEmpty.Of(db, today.AddDays(-1), today, now);
                    f.YesterdayWhy = (kind == WhyEmpty.Kind.PcWasOn ? null : WhyEmpty.Sentence(kind, began, when)) ?? Lang.T($"Not recorded {when}");
                }
            }
        }
        if ((needs & PcNeeds.Week) != 0)
        {
            if (pastKept && (before!.Holds & PcNeeds.Week) != 0) f.Days = before.Days;
            else
            {
                var week = ReportBuilder.BuildRaw(db, ReportRange.Week, today.AddDays(-6), today, apps, s);
                f.Days = [.. Enumerable.Range(0, 6).Select(i => today.AddDays(i - 6)).Select(d => (d, week.Days.FirstOrDefault(b => b.Day == d)?.ActiveSec ?? 0))];
            }
        }
        f.PastAt = pastKept ? before!.PastAt : now;

        if ((needs & PcNeeds.Crashes) != 0) ReadCrashes(db, s, now, f);
        if ((needs & PcNeeds.LastGame) != 0) ReadLastGame(db, s, now, f, before);
        if ((needs & PcNeeds.Internet) != 0)
            ReadInternet(report is not null && f.TodayAt == now && report.Net is { } fresh ? fresh : NetReportBuilder.Build(db, ReportRange.Day, today, apps, s), f);
        return f;
    }

    /// <summary>The Crashes page's last 30 days (muted apps left out, as there), and the latest one in a sentence.</summary>
    private static void ReadCrashes(RigsightDb db, RigsightSettings s, DateTime now, PcFacts f)
    {
        const int Days = 30;
        var events = db.GetCrashes(TimeUtil.ToUnix(now.Date.AddDays(-(Days - 1))), TimeUtil.ToUnix(now.AddMinutes(1)))
            .Where(e => !s.IsCrashMuted(e.AppExe)).OrderByDescending(e => e.Ts).ToList();
        f.Crashes = events.Count;
        if (events.FirstOrDefault() is not { } latest) return;
        var byExe = db.LoadApps().GroupBy(a => a.Exe, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        string title = CrashExplainer.Explain(latest, CrashExplainer.AppNameOf(latest, byExe, s)).Title;
        f.LatestCrash = When(title, latest.Time, now);
    }

    /// <summary>"Dota 2 crashed today at 9:14 PM", "… yesterday at 9:14 PM", "… on 2 Nov at 9:14 PM".</summary>
    public static string When(string what, DateTime at, DateTime now)
    {
        string time = at.ToString(TimeText.Fit("h:mm tt"));
        return (now.Date - at.Date).Days switch
        {
            <= 0 => Lang.T($"{what} today at {time}"),
            1 => Lang.T($"{what} yesterday at {time}"),
            _ => Lang.T($"{what} on {at:d MMM} at {time}"),
        };
    }

    /// <summary>The last game played, as the Games page has it; its play read again only once another has been played.</summary>
    private static void ReadLastGame(RigsightDb db, RigsightSettings s, DateTime now, PcFacts f, PcFacts? before)
    {
        var last = GameHistory.Library(db, [], s).Where(e => e.LastPlayed is not null).MaxBy(e => e.LastPlayed);
        if (last is null) return;
        f.GameKey = (last.Key, last.LastPlayed);
        if (before is { LastGame: { } kept } && before.GameKey == f.GameKey)
        {
            f.LastGame = kept;
            return;
        }
        if (GameHistory.LastPlay(db, last, s, now) is { } play)
            f.LastGame = new LastPlayLine(last.Name, last.ExePath, play.EndTime, play.ActiveSec, play.Stats.Fps);
    }

    /// <summary>Today's internet from the Network page's report: totals, the apps that used the most, each hour, the drops.</summary>
    private static void ReadInternet(NetReport net, PcFacts f)
    {
        f.NetHasData = net.HasData;
        if (!net.HasData) return;
        (f.NetDown, f.NetUp, f.NetBackground) = (net.Down, net.Up, net.Background);
        var used = net.Apps.Where(a => a.Use.Total >= 1 << 20).Take(ListMost).ToList();
        double most = Math.Max(1, used.FirstOrDefault()?.Use.Total ?? 1);
        f.NetApps = [.. used.Select(a => new AppLine(a.Name, a.Path, Units.Data(a.Use.Total), a.Use.Total / most))];
        for (int h = 0; h < Math.Min(24, net.Bins.Count); h++)
        {
            var bin = net.Bins[h];
            long top = bin.Apps.FirstOrDefault()?.Total ?? 0, all = Math.Max(bin.Total, top);
            (f.HourTop[h], f.HourRest[h]) = (top, all - top);
        }
        f.Quarters = net.Quarters;
        f.Drops = [.. net.Drops.Select(d => (TimeUtil.FromUnix(d.Start), TimeUtil.FromUnix(d.End)))];
    }
}
