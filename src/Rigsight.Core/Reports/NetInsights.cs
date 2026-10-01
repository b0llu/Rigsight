using Rigsight.Core.Data;

namespace Rigsight.Core.Reports;

/// <summary>
/// What's worth saying about a period's internet use. Like the rest of the insights: like for like or not at all, enough
/// behind each line, said once where it would otherwise repeat, and nothing on an ordinary day.
/// </summary>
public static class NetInsights
{
    private const long MB = 1L << 20, GB = 1L << 30;

    // Icons (Segoe Fluent): download, upload, speed, no connection, a trophy for records, the recap's chart.
    private const string IconDown = "", IconUp = "", IconSpeed = "", IconDrop = "", IconRecord = "", IconRecap = "";

    /// <summary>An app downloading this much in the background in a day (scaled for a week or month) is worth a line…</summary>
    internal const long BackgroundDayBytes = 2 * GB;
    /// <summary>…when at least this share of its download was in the background.</summary>
    internal const double BackgroundShare = 0.6;

    /// <summary>Windows sharing updates with PCs on the internet: worth a line from this much in a day (2 GB longer).</summary>
    internal const long SharingDayBytes = 500 * MB;
    internal const string DeliveryOptimization = "svchost.exe:DoSvc";

    /// <summary>An app uploading at least this much, and this many times its usual day, is worth a line.</summary>
    internal const long UploadBytes = GB;
    internal const double UploadTimes = 4;

    /// <summary>The speed check: the best steady download of the last 7 days against the 30 days before…</summary>
    internal const int SpeedDays = 7, SpeedUsualDays = 30;
    /// <summary>…with at least this many days of big downloads in each…</summary>
    internal const int SpeedMinDays = 3, SpeedMinUsualDays = 5;
    /// <summary>…and the week's best at most this share of the usual.</summary>
    internal const double SpeedDrop = 0.75;

    /// <summary>Downloading behind a game: worth a line from this much.</summary>
    internal const long GameBytes = 500 * MB;

    /// <summary>A download record needs a day of at least this much.</summary>
    internal const long RecordBytes = 5 * GB;
    private static readonly (int Days, int MinDays)[] RecordWindows = [(30, 15), (90, 40), (365, 120)];

    /// <summary>Drops: at least this many in a day, and more than twice the usual day's (the 14 days before).</summary>
    internal const int MinDrops = 3, DropUsualDays = 14;
    /// <summary>Without 7 days to know the usual by, this many.</summary>
    internal const int MinDropsNoUsual = 5;

    public static List<Insight> Generate(RigsightDb db, NetReport r, IReadOnlyList<string> gamesPlayed)
    {
        var list = new List<Insight>();
        if (!r.HasData) return list;
        bool inProgress = r.From <= r.BuiltAt && r.BuiltAt < r.To;
        bool isDay = r.Range == ReportRange.Day;
        string period = PeriodWord(r.Range, inProgress);
        double scale = r.Range switch { ReportRange.Day => 1, ReportRange.Week => 5, ReportRange.Month => 15, _ => double.PositiveInfinity };

        // 1. An app downloading a lot while you weren't using it, or weren't even there.
        if (r.Apps.Where(a => !a.Exe.Equals(DeliveryOptimization, StringComparison.OrdinalIgnoreCase)).MaxBy(a => a.Use.BgDown + a.Use.AwayDown) is { } bg)
        {
            long behind = bg.Use.BgDown + bg.Use.AwayDown;
            if (behind >= BackgroundDayBytes * scale && behind >= bg.Use.Down * BackgroundShare)
            {
                bool away = bg.Use.AwayDown * 2 >= bg.Use.Down;
                list.Add(new Insight(IconDown, away
                        ? $"{bg.Name} downloaded {Units.Data(bg.Use.Down)} {period}, {Units.Data(bg.Use.AwayDown)} of it while you were away."
                        : $"{bg.Name} downloaded {Units.Data(bg.Use.Down)} {period}, {Units.Data(behind)} of it in the background.",
                    InsightTone.Neutral, "net-background", 57,
                    $"In the background {Units.Data(bg.Use.BgDown)} · while you were away {Units.Data(bg.Use.AwayDown)}"));
            }
        }

        // 2. Windows handing out updates to other PCs on the internet (to PCs at home is local, and not counted).
        if (r.Apps.FirstOrDefault(a => a.Exe.Equals(DeliveryOptimization, StringComparison.OrdinalIgnoreCase)) is { } sharing
            && sharing.Use.Up >= (isDay ? SharingDayBytes : 4 * SharingDayBytes))
            list.Add(new Insight(IconUp,
                $"Windows uploaded {Units.Data(sharing.Use.Up)} of updates to other PCs on the internet {period}. You can turn this off in Windows Update settings, under Delivery Optimization.",
                InsightTone.Neutral, "net-sharing", 59));

        // 3. An app uploading far more than it usually does: a backup or sync running wild, or something sending files out.
        if (isDay) UnusualUpload(db, r, list, period);

        // 4. The connection slower than it was, by the best big download of the week against the month before.
        if (isDay && SpeedDropped(db, r.From) is { } drop && SpeedDropped(db, r.From.AddDays(-1)) is null)
            list.Add(new Insight(IconSpeed,
                $"Your downloads topped out at {Units.Speed(drop.Week)} ({Units.Mbps(drop.Week)}) over the last 7 days, down from your usual {Units.Speed(drop.Usual)} ({Units.Mbps(drop.Usual)}).",
                InsightTone.Warn, "net-speed", 75,
                $"The fastest steady download each day, all apps together: {drop.WeekDays} days lately, {drop.UsualDays} in the 30 before"));

        // 5. Downloading behind a game.
        if (isDay && r.Apps.MaxBy(a => a.Use.GameDown) is { } behindGame && behindGame.Use.GameDown >= GameBytes)
        {
            string game = gamesPlayed.Count == 1 ? $"while you played {gamesPlayed[0]}" : "while you were in a game";
            list.Add(new Insight(IconDown, $"{behindGame.Name} downloaded {Units.Data(behindGame.Use.GameDown)} {game}.", InsightTone.Neutral, "net-game", 54));
        }

        // 6. The biggest download day in a month, three months or a year.
        if (isDay && r.Down >= RecordBytes && RecordDays(db, r) is int days)
            list.Add(new Insight(IconRecord, $"Your biggest download day in {Span(days)}: {Units.Data(r.Down)}{(inProgress ? " so far" : "")}.",
                InsightTone.Neutral, "net-record", 60, $"Beats every day of the {days} before"));

        // 7. The period in a line, against the one before.
        if (r.Down >= 100 * MB)
        {
            string head = Capitalize(period);
            string against = "";
            if (r.PreviousDown is long before && before >= 500 * MB)
            {
                double ratio = (double)r.Down / before;
                string last = r.Range switch { ReportRange.Day => "yesterday", ReportRange.Week => "last week", ReportRange.Month => "last month", _ => "last year" };
                if (inProgress) last += " by now";
                against = ratio >= 1.25 ? $", {ratio:0.0}× {last}" : ratio <= 0.8 ? $", {(1 - ratio) * 100:0}% less than {last}" : $", about the same as {last}";
            }
            var top = r.Apps.FirstOrDefault();
            string mostly = top is not null && top.Use.Down >= r.Down * 0.4 ? $", mostly {top.Name}" : "";
            list.Add(new Insight(IconRecap, $"{head}: {Units.Data(r.Down)} downloaded{against}{mostly}.", InsightTone.Neutral, "net-recap", 45));
        }

        // 8. The internet dropping more than usual.
        if (isDay && r.Drops.Count >= MinDrops && MoreDropsThanUsual(db, r))
        {
            var longest = r.Drops.MaxBy(d => d.Seconds)!;
            int link = r.Drops.Count(d => d.Kind == NetDropKind.Link);
            string why = link == 0 ? " Your PC stayed connected to the router each time, so the trouble was past it: the router or your internet provider."
                : link == r.Drops.Count ? " Each time your PC's own connection to the router (the cable or Wi-Fi) went down."
                : "";
            list.Add(new Insight(IconDrop,
                $"Your internet dropped {r.Drops.Count} times {period}, the longest for {Units.Duration(longest.Seconds)} at {TimeUtil.FromUnix(longest.Start):h:mm tt}.{why}",
                InsightTone.Warn, "net-drops", 74,
                string.Join(" · ", r.Drops.Select(d => $"{TimeUtil.FromUnix(d.Start):h:mm tt} {Units.Duration(d.Seconds)}"))));
        }

        return list;
    }

    private static void UnusualUpload(RigsightDb db, NetReport r, List<Insight> list, string period)
    {
        var candidates = r.Apps.Where(a => a.Use.Up >= UploadBytes && !a.Exe.Equals(DeliveryOptimization, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.Use.Up).ToList();
        if (candidates.Count == 0) return;
        var history = db.GetNetAppDays(TimeUtil.ToUnix(r.From.AddDays(-NetReportBuilder.UsualDays)), TimeUtil.ToUnix(r.From));
        foreach (var app in candidates)
        {
            var days = history.Where(d => d.App == app.Id && d.Total > 0).Select(d => d.Up).ToList();
            if (days.Count < NetReportBuilder.MinUsualDays) continue;
            long usual = NetReportBuilder.Median(days);
            if (app.Use.Up < usual * UploadTimes) continue;
            list.Add(new Insight(IconUp, usual >= MB
                    ? $"{app.Name} uploaded {Units.Data(app.Use.Up)} {period}, against about {Units.Data(usual)} on a usual day."
                    : $"{app.Name} uploaded {Units.Data(app.Use.Up)} {period}. It usually uploads next to nothing.",
                usual >= MB ? InsightTone.Neutral : InsightTone.Warn, "net-upload", 68,
                $"Its usual: the middle of the {days.Count} days it was online in the {NetReportBuilder.UsualDays} before"));
            return;
        }
    }

    /// <summary>
    /// The 7 days up to <paramref name="day"/> against the 30 before them: the fastest steady download of the week (all apps
    /// together, so two downloads sharing the line still count as one full line) at most 75% of the usual day's best.
    /// The week's best, not its average: one slow server doesn't make a slow line.
    /// </summary>
    internal static (long Week, long Usual, int WeekDays, int UsualDays)? SpeedDropped(RigsightDb db, DateTime day)
    {
        var start = day.Date.AddDays(-(SpeedDays - 1));
        var rows = db.GetNetDays(TimeUtil.ToUnix(start.AddDays(-SpeedUsualDays)), TimeUtil.ToUnix(day.Date.AddDays(1)));
        long weekFrom = TimeUtil.ToUnix(start);
        var week = rows.Where(d => d.Day >= weekFrom && d.Best is not null).Select(d => d.Best!.Value).ToList();
        var usual = rows.Where(d => d.Day < weekFrom && d.Best is not null).Select(d => d.Best!.Value).ToList();
        if (week.Count < SpeedMinDays || usual.Count < SpeedMinUsualDays) return null;
        long best = week.Max(), typical = NetReportBuilder.Median(usual);
        return best <= typical * SpeedDrop ? (best, typical, week.Count, usual.Count) : null;
    }

    /// <summary>The longest window (30, 90 or 365 days) whose every day this one beats, with history enough to say so.</summary>
    private static int? RecordDays(RigsightDb db, NetReport r)
    {
        var rows = db.GetNetDays(TimeUtil.ToUnix(r.From.AddDays(-365)), TimeUtil.ToUnix(r.From));
        long? first = db.GetNetDays(0, TimeUtil.ToUnix(r.From)).FirstOrDefault()?.Day;
        if (first is null) return null;
        int? best = null;
        foreach (var (window, minDays) in RecordWindows)
        {
            var inWindow = rows.Where(d => d.Day >= TimeUtil.ToUnix(r.From.AddDays(-window)) && d.Minutes > 0).ToList();
            // "In a year" needs about a year of history, not a year's worth of days scattered over five months.
            if (inWindow.Count < minDays || first > TimeUtil.ToUnix(r.From.AddDays(-window * 0.9))) break;
            if (inWindow.Any(d => d.Down >= r.Down)) break;
            best = window;
        }
        return best;
    }

    private static bool MoreDropsThanUsual(RigsightDb db, NetReport r)
    {
        long from = TimeUtil.ToUnix(r.From.AddDays(-DropUsualDays)), to = TimeUtil.ToUnix(r.From);
        int days = db.GetNetDays(from, to).Count(d => d.Minutes > 0);
        if (days < NetReportBuilder.MinUsualDays) return r.Drops.Count >= MinDropsNoUsual;
        double usual = (double)db.GetNetDrops(from, to).Count / days;
        return r.Drops.Count > usual * 2;
    }

    private static string PeriodWord(ReportRange range, bool inProgress) => (range, inProgress) switch
    {
        (ReportRange.Day, true) => "today",
        (ReportRange.Day, false) => "that day",
        (ReportRange.Week, true) => "this week",
        (ReportRange.Week, false) => "that week",
        (ReportRange.Month, true) => "this month",
        (ReportRange.Month, false) => "that month",
        (_, true) => "this year",
        _ => "that year",
    };

    private static string Span(int days) => days switch { 30 => "a month", 90 => "three months", _ => "a year" };

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
