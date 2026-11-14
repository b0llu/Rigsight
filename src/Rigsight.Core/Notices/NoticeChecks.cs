using Rigsight.Core.Data;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Notices;

/// <summary>
/// A notification the checks would send: its words, where a click opens, and what it's about (<see cref="Key"/>, with the
/// day in it where it may be said again the next day). <see cref="Value"/>: what's kept with the key to compare next time
/// (a drive's bad sector count).
/// </summary>
/// <paramref name="Quiet"/>: only noted as said, not sent (a drive's first life reading sets where it stands).
public sealed record NoticeCandidate(string Kind, NoticeLevel Level, NoticeText Text, string? Page, string? Arg, string Key, string? Value = null, bool Quiet = false);

/// <summary>
/// The notifications round's checks (2026-11-07, mock-up LWycKU9ZC8jzeVH8bVjPWm): each turns something RigSight already
/// works out for its pages into a notification, in that line's own words (<see cref="Insight.Notice"/>), once. Pure: the
/// agent gives them what it read and what was said before (<see cref="RigsightDb.Said"/>), and sends what comes back.
/// Every check says in the log what it expected and what it found, also when it stays quiet.
/// </summary>
public static class NoticeChecks
{
    /// <summary>A drive counts as almost full under both: 5% of it, and 20 GB.</summary>
    public const double FullShare = 0.05;
    public const long FullBytes = 20L << 30;

    /// <summary>An app's memory counts as growing when it rose for this long, to at least <see cref="GrowthMinBytes"/> and <see cref="GrowthFactor"/> times where it started.</summary>
    public static readonly TimeSpan GrowthSpan = TimeSpan.FromHours(3);
    public const long GrowthMinBytes = 2L << 30;
    public const double GrowthFactor = 2;

    /// <summary>How often "more PC problems than usual" may be said.</summary>
    public static readonly TimeSpan ProblemsAgain = TimeSpan.FromDays(7);

    private static string Day(DateTime d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Today's report's lines that become notifications while the PC is in use: a fan that stopped (a problem), a fan
    /// slower than usual or than months ago, and the PC's problems past their usual (worth knowing). A game hotter than
    /// usual and the GPU slowing itself are said when the game ends instead (<see cref="AfterGame"/>), so not here.
    /// </summary>
    public static List<NoticeCandidate> FromReport(Report today, AlertSettings s, Func<string, (DateTime At, string? Value)?> said, DateTime now)
    {
        var list = new List<NoticeCandidate>();
        foreach (var i in today.Insights)
        {
            if (i.Notice is not { } text) continue;
            switch (i.Key)
            {
                case "fan-stopped" when s.FanStops:
                    Add(new("fan", NoticeLevel.Problem, text, "fans", null, $"fan-stopped|{i.FanName}|{Day(now)}"));
                    break;
                case "fan-slower" or "fan-older" when s.FansSlower:
                    Add(new("fan", NoticeLevel.Worth, text, "fans", null, $"{i.Key}|{i.FanName}|{Day(now)}"));
                    break;
                case "usual-problems" when s.MoreProblems && i.Tone == InsightTone.Warn:
                    // Once a week at most: the count stays past its usual for days once it is.
                    if (said("usual-problems") is { } last && now - last.At < ProblemsAgain) break;
                    list.Add(new("problems", NoticeLevel.Worth, text, "crashes", null, "usual-problems"));
                    break;
            }
        }
        return list;

        void Add(NoticeCandidate c) { if (said(c.Key) is null) list.Add(c); }
    }

    /// <summary>
    /// When a game ends: what today's report says about it that's worth knowing, as sentences for its summary (when the
    /// summary is shown) and as notifications of their own (when it isn't). Hotter than usual is about this game only;
    /// the GPU slowing itself is the day's, said once a day.
    /// </summary>
    /// <param name="usual">The last 7 days against the weeks before, for this game (<see cref="PcUsual.Read"/> with its id).</param>
    /// <param name="throttle">Today's GPU slowing itself, if it did.</param>
    public static (List<string> Sentences, List<NoticeCandidate> Cards) AfterGame(PcUsual? usual, Throttling? throttle, string game, string exe, AlertSettings s,
        Func<string, (DateTime At, string? Value)?> said, DateTime now)
    {
        var sentences = new List<string>();
        var cards = new List<NoticeCandidate>();
        // The GPU unless only the CPU moved, as the page says it.
        if (s.HotterThanUsual && usual?.Parts.FirstOrDefault(p => p.Tone == EffectTone.Worse && p.Hotter) is { } hot)
        {
            string key = $"usual-heat|{game}|{Day(now)}";
            if (said(key) is null)
            {
                sentences.Add(NoticeWords.HotterInSummary(hot));
                cards.Add(new("hotter", NoticeLevel.Worth, NoticeWords.Hotter(hot), "games", exe, key));
            }
        }
        if (s.GpuSlowing && throttle is { } t)
        {
            string key = $"throttle|{Day(now)}";
            if (said(key) is null)
            {
                sentences.Add(NoticeWords.SlowedInSummary(t));
                cards.Add(new("slowed", NoticeLevel.Worth, NoticeWords.Slowed(t), "temperatures", null, key));
            }
        }
        return (sentences, cards);
    }

    /// <summary>
    /// A drive's own health check: a failure (a problem), early warning signs, new or more bad sectors, or almost all of its
    /// rated life used (worth knowing). A drive is said when it changes for the worse: its first reading only sets where it
    /// stands, unless that is already a failure or bad sectors (then it's said once).
    /// </summary>
    /// <param name="paceOf">A drive's share of rated life used a year at the pace seen (by its key), asked only when a life card is sent.</param>
    public static List<NoticeCandidate> Drives(IEnumerable<DriveHealthInfo> drives, IReadOnlyDictionary<string, double> lifeLeft, AlertSettings s,
        Func<string, (DateTime At, string? Value)?> said, DateTime? now = null, Func<string, double?>? paceOf = null)
    {
        var list = new List<NoticeCandidate>();
        var today = (now ?? DateTime.Now).Date;
        foreach (var d in drives.Where(d => d.Name.Length > 0))
        {
            string name = d.Name;
            // A click opens the drive itself on Storage (by its key), or Storage when it has none.
            string? arg = d.Key is { Length: > 0 } k ? DriveArg + k : null;
            double? left = d.Life ?? (lifeLeft.TryGetValue(name, out double l) ? l : null);

            // Its own warning temperature reached: a problem, said at once, once a day (option A, 2026-11-14).
            if (s.DriveTemperature && d.Temperature is double temp && d.TemperatureWarning is double warning && warning > 0 && temp >= warning
                && said($"drive-hot|{name}|{Day(today)}") is null)
                list.Add(new("drive", NoticeLevel.Problem, NoticeWords.DriveHot(name, temp, warning), "storage", arg, $"drive-hot|{name}|{Day(today)}"));

            // Half and a quarter of its rated life left (10% is the health card's "most of its rated life used"). The first
            // reading only sets where it stands: a drive already at 40% isn't said to have just reached half.
            if (s.DriveLifeMilestones && left is double life)
            {
                int mark = life <= 25 ? 25 : life <= 50 ? 50 : 100;
                var before = said($"drive-life|{name}");
                int? was = before?.Value is { } v && int.TryParse(v, out int m) ? m : null;
                string value = mark.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var quiet = new NoticeText("", "");
                if (before is null || was is null || mark > was)
                    list.Add(new("drive", NoticeLevel.Worth, quiet, "storage", arg, $"drive-life|{name}", value, Quiet: true)); // where it stands, or a newer drive
                else if (mark < was && life > WornOut)
                    list.Add(new("drive", NoticeLevel.Worth, NoticeWords.DriveLife(name, mark, life, d.Key is { } key ? paceOf?.Invoke(key) : null), "storage", arg, $"drive-life|{name}", value));
            }

            if (!s.DriveHealth) continue;
            if (d.Status == "Bad")
            {
                if (said($"drive-bad|{name}") is null) list.Add(new("drive", NoticeLevel.Problem, NoticeWords.DriveFailing(name), "storage", arg, $"drive-bad|{name}"));
                continue;
            }
            long sectors = (d.ReallocatedSectors ?? 0) + (d.PendingSectors ?? 0) + (d.UncorrectableSectors ?? 0);
            if (sectors > 0)
            {
                var before = said($"drive-sectors|{name}");
                long? was = before?.Value is { } v && long.TryParse(v, out long n) ? n : null;
                if (before is null || sectors > (was ?? 0))
                    list.Add(new("drive", NoticeLevel.Worth, NoticeWords.DriveSectors(name, SectorWords(d), before is null ? null : was ?? 0), "storage", arg,
                        $"drive-sectors|{name}", sectors.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            else if (d.Status == "Caution" && said($"drive-caution|{name}") is null)
                list.Add(new("drive", NoticeLevel.Worth, NoticeWords.DriveCaution(name), "storage", arg, $"drive-caution|{name}"));
            if (left is double worn && worn <= WornOut && said($"drive-worn|{name}") is null)
                list.Add(new("drive", NoticeLevel.Worth, NoticeWords.DriveWorn(name, worn), "storage", arg, $"drive-worn|{name}"));
        }
        return list;
    }

    /// <summary>A drive card's argument before the drive's key: Storage opens that drive.</summary>
    public const string DriveArg = "drive:";

    /// <summary>How long before a part that runs hotter than it used to may be said again (and only further up).</summary>
    public static readonly TimeSpan WearAgain = TimeSpan.FromDays(30);

    /// <summary>
    /// A chip that sits further above its rest under the same load than in its first weeks, for two weeks running (Wear):
    /// worth knowing, at most once a month each, and again only when it climbed another Wear.CoolingMoved degrees.
    /// </summary>
    public static List<NoticeCandidate> Cooling(IEnumerable<WearRow> parts, AlertSettings s, Func<string, (DateTime At, string? Value)?> said, DateTime now)
    {
        var list = new List<NoticeCandidate>();
        if (!s.PartsChanged) return list;
        foreach (var r in parts)
        {
            if (r is not { State: WearState.Moved, Now: double gap, Then: double then, ThenDate: DateTime since } || gap <= then) continue;
            string key = $"wear|{r.Part}";
            var before = said(key);
            double? was = before?.Value is { } v && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double x) ? x : null;
            if (before is { } b && (now - b.At < WearAgain || was is null || gap < was + Wear.CoolingMoved)) continue;
            list.Add(new("wear", NoticeLevel.Worth, NoticeWords.RunsHotter(r.Name, gap, then, Wear.WhenSaid(since)), "temperatures", null, key,
                gap.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)));
        }
        return list;
    }

    /// <summary>An SSD's life left at or under which it's said (the Storage card's "worn out" line).</summary>
    public const double WornOut = 10;

    /// <summary>"3 reallocated", "3 reallocated, 1 pending": a drive's bad sectors, as its Storage card lists them.</summary>
    public static string SectorWords(DriveHealthInfo h)
    {
        var parts = new List<string>();
        if (h.ReallocatedSectors > 0) parts.Add(Lang.T($"{h.ReallocatedSectors:N0} reallocated"));
        if (h.PendingSectors > 0) parts.Add(Lang.T($"{h.PendingSectors:N0} pending"));
        if (h.UncorrectableSectors > 0) parts.Add(Lang.T($"{h.UncorrectableSectors:N0} unreadable"));
        return string.Join(", ", parts);
    }

    /// <summary>A fixed drive almost full (under 5% and under 20 GB left), once a day each.</summary>
    public static List<NoticeCandidate> Space(IEnumerable<(char Letter, long Free, long Total)> drives, char windowsDrive, AlertSettings s,
        Func<string, (DateTime At, string? Value)?> said, DateTime now)
    {
        var list = new List<NoticeCandidate>();
        if (!s.DriveFull) return list;
        foreach (var (letter, free, total) in drives)
        {
            if (total <= 0 || free >= FullBytes || free >= total * FullShare) continue;
            string key = $"drive-full|{letter}|{Day(now)}";
            if (said(key) is null)
                list.Add(new("storage", NoticeLevel.Worth, NoticeWords.DriveFull(letter, free, total, char.ToUpperInvariant(letter) == char.ToUpperInvariant(windowsDrive)),
                    "storage", letter.ToString(), key));
        }
        return list;
    }

    /// <summary>
    /// A driver or Windows update after which a game ran worse, as Timeline found it (only results it shows, on its
    /// days), once per change.
    /// </summary>
    public static List<NoticeCandidate> Changes(IReadOnlyDictionary<DateTime, ChangeResult> results, bool on,
        Func<string, (DateTime At, string? Value)?> said, DateTime now)
    {
        var list = new List<NoticeCandidate>();
        if (!on) return list;
        foreach (var (day, r) in results.OrderBy(x => x.Key))
        {
            if (r.Tone != EffectTone.Worse || now.Date - day.Date > TimeSpan.FromDays(ChangeEffects.WindowDays)) continue;
            string key = $"change|{Day(day)}";
            if (said(key) is null) list.Add(new("change", NoticeLevel.Worth, NoticeWords.WorseChange(r), "timeline", Day(day), key));
        }
        return list;
    }

    /// <summary>
    /// An app whose memory rose through the last <see cref="GrowthSpan"/> without coming back down: each hour's
    /// highest at least the hour before's, ending at <see cref="GrowthMinBytes"/> or more and <see cref="GrowthFactor"/>
    /// times where it started. <paramref name="minutes"/>: each app's memory a minute over that span, oldest first.
    /// </summary>
    public static List<NoticeCandidate> Memory(IReadOnlyDictionary<(string Exe, string Name), List<(DateTime At, long Bytes)>> minutes, AlertSettings s,
        Func<string, (DateTime At, string? Value)?> said, DateTime now)
    {
        var list = new List<NoticeCandidate>();
        if (!s.MemoryGrowth) return list;
        foreach (var ((exe, name), rows) in minutes)
        {
            if (rows.Count < 2 || rows[^1].At - rows[0].At < GrowthSpan - TimeSpan.FromMinutes(10)) continue;
            var hours = rows.GroupBy(r => (int)((r.At - rows[0].At).TotalMinutes / 60)).OrderBy(g => g.Key).Select(g => g.Max(r => r.Bytes)).ToList();
            if (hours.Count < 3) continue;
            bool rising = hours.Zip(hours.Skip(1)).All(p => p.Second >= p.First * 0.97);
            long from = rows.Take(10).Min(r => r.Bytes), to = rows[^1].Bytes;
            if (!rising || to < GrowthMinBytes || to < from * GrowthFactor) continue;
            string key = $"memory|{exe}|{Day(now)}";
            if (said(key) is null)
                list.Add(new("memory", NoticeLevel.Recap, NoticeWords.MemoryGrowing(name, from, to, (rows[^1].At - rows[0].At).TotalSeconds), "processes", exe, key));
        }
        return list;
    }

    /// <summary>A new app that starts with Windows, as the Timeline recorded it, once each.</summary>
    public static List<NoticeCandidate> Startup(IEnumerable<SystemChange> changes, bool on, Func<string, (DateTime At, string? Value)?> said, DateTime now)
    {
        var list = new List<NoticeCandidate>();
        if (!on) return list;
        foreach (var c in changes.Where(c => c.Kind == ChangeKind.Startup && c.Now == Inventory.On && c.Time > now.AddDays(-2)))
        {
            string key = $"startup|{c.Subject}|{c.Time.Ticks}";
            if (said(key) is null) list.Add(new("startup", NoticeLevel.Recap, NoticeWords.StartsWithWindows(c.Shown, c.Time, now), "timeline", Day(c.Time), key));
        }
        return list;
    }
}
