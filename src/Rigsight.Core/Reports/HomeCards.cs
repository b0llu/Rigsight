using Rigsight.Core.Data;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Reports;

/// <summary>A line of "What changed lately": the change in a few words, when, and what it made better or worse (a big change's result only).</summary>
public sealed record ChangeCardLine(SystemChange Change, string Text, string When, EffectTone? Tone = null, DateTime? ChangeDay = null);

/// <summary>A drive as PC health judges it: whether its health is known, and whether it needs a look.</summary>
public sealed record DriveCheck(string Name, bool HasHealth, bool NeedsLook);

/// <summary>
/// Home's two small cards, "What changed lately" and "PC health", worked out once here: Home shows them, and so do the
/// widgets' tiles of the same name, from the same records and in the same words (so the two never disagree).
/// </summary>
public static class HomeCards
{
    /// <summary>How many lines each small card holds on Home.</summary>
    public const int Lines = 4;

    /// <summary>How long the latest change that made a difference stays on top of "What changed lately".</summary>
    public const int PinDays = ChangeEffects.WindowDays;

    /// <summary>What the cards read from the records: the last 30 days' changes, and the last 7 days' problems.</summary>
    public sealed record Records(List<SystemChange> Changes, List<CrashKind> Problems, int Mains, int MainsLikely);

    public static Records Read(RigsightDb db, RigsightSettings s, DateTime now)
    {
        long from = TimeUtil.ToUnix(now.Date.AddDays(-30)), to = TimeUtil.ToUnix(now.AddDays(1));
        var changes = db.GetChanges(from, to);
        changes.AddRange(StorageChanges.From(db.GetDriveDays(from - 86400)).Where(c => c.Time >= now.Date.AddDays(-30)));
        var crashes = db.GetCrashes(TimeUtil.ToUnix(now.Date.AddDays(-6)), to).Where(e => !s.IsCrashMuted(e.AppExe)).ToList();
        // A shut-off that looks like the power is no fault of the PC's: counted, and said, apart (PcUsual.ShutOffOf).
        var found = CrashCases.Of(db, crashes, s);
        return new Records(changes, [.. crashes.Where(e => PcUsual.CountsAsPcProblem(e, found.GetValueOrDefault(e.Id))).Select(e => e.Kind)],
            crashes.Count(e => PcUsual.LooksLikePower(e, found.GetValueOrDefault(e.Id))),
            crashes.Count(e => PcUsual.ShutOffOf(e, found.GetValueOrDefault(e.Id)) == ShutOff.PowerLikely));
    }

    /// <summary>"Today", "Yesterday", the day's name within a week, else its date.</summary>
    public static string DayWord(DateTime day, DateTime today) => (today.Date - day.Date).Days switch
    {
        <= 0 => Lang.T("Today"),
        1 => Lang.T("Yesterday"),
        < 7 => day.ToString("dddd"),
        _ => day.ToString("d MMM"),
    };

    /// <summary>A change in a few words: an app update without its version ("Google Chrome updated").</summary>
    public static string Brief(string title) =>
        title.IndexOf(" updated to ", StringComparison.Ordinal) is > 0 and var at ? title[..(at + " updated".Length)] : title;

    /// <summary>
    /// "What changed lately": each change in a few words with when, newest first; the latest big change that made
    /// something better or worse stays on top for two weeks as what it did ("Crimson Desert runs 10° hotter on the GPU
    /// since driver 618.10").
    /// </summary>
    public static List<ChangeCardLine> Changes(IEnumerable<SystemChange> changes, DateTime today, IReadOnlyDictionary<DateTime, ChangeResult> results, int count = Lines)
    {
        var list = changes.ToList();
        var pinned = results.Values.Where(r => r.Day >= today.AddDays(-PinDays) && list.Contains(r.Main)).MaxBy(r => r.Day);
        // Shortened on the saved English, then shown in the language in use.
        var rest = list.Where(c => c != pinned?.Main).OrderByDescending(c => c.Time).Take(pinned is null ? count : count - 1)
            .Select(c => new ChangeCardLine(c, SavedText.Show(Brief(c.Title)), DayWord(c.Time.Date, today))).ToList();
        if (pinned is null) return rest;
        return [new ChangeCardLine(pinned.Main, pinned.Said, DayWord(pinned.Day, today), pinned.Tone, pinned.Day), .. rest];
    }

    /// <summary>A change that made things worse, when it's likely the change and still recent: the only result PC health calls out.</summary>
    public static ChangeResult? Worse(IReadOnlyDictionary<DateTime, ChangeResult> results, DateTime today) =>
        results.Values.Where(r => r is { Tone: EffectTone.Worse, Sure: Confidence.Likely } && r.Day >= today.AddDays(-PinDays)).MaxBy(r => r.Day);

    /// <summary>
    /// PC health, a statement a line: the PC's own parts against its usual (UsualLines.Health, the same words as the
    /// insight list), then the drives: one that needs a look, else healthy and the fullest. Where a line opens comes from
    /// InsightLinks alone.
    /// </summary>
    /// <summary>A drive's icon in Windows' icon font, for PC health's line about the drives.</summary>
    public const string DriveIcon = "";

    /// <param name="mainsLikely">Of <paramref name="mains"/>, the likely power cuts (the rest were possibly the power); null: all of them.</param>
    public static List<Insight> Health(Report? today, IReadOnlyCollection<CrashKind> problems, int mains, IReadOnlyCollection<DriveCheck> drives, string? fullest, int? mainsLikely = null)
    {
        var parts = UsualLines.Health(today, problems, mains, mainsLikely);
        var known = drives.Where(d => d.HasHealth).ToList();
        if (known.FirstOrDefault(d => d.NeedsLook) is { } unwell)
            parts.Add(new Insight(DriveIcon, Lang.T($"{unwell.Name} needs a look"), InsightTone.Warn, "drive-unwell"));
        else if (known.Count > 0)
            parts.Add(new Insight(DriveIcon, DrivesHealthy(known.Count, fullest), InsightTone.Good, "drives"));
        else if (fullest is not null)
            parts.Add(new Insight(DriveIcon, Wording.Capital(fullest), InsightTone.Neutral, "drives"));
        return InsightLinks.Apply(parts);
    }

    /// <summary>Whether a line of PC health needs a look (said in amber).</summary>
    public static bool NeedsLook(Insight i) => i.Tone is InsightTone.Warn or InsightTone.Hot;

    /// <summary>
    /// PC health's lines in their order: a change that made things worse first, unless the heat line already says what
    /// it did (a game hotter than usual, when the change itself is in "What changed lately"); then what needs a look.
    /// </summary>
    public static List<T> HealthOrder<T>(List<T> lines, T? worse, bool heatSaid, Func<T, bool> needsLook) where T : class
    {
        if (worse is not null && !heatSaid) lines.Insert(0, worse);
        return [.. lines.Where(needsLook), .. lines.Where(l => !needsLook(l))];
    }

    /// <summary>Whether PC health already says a game runs hotter than usual (then a worse change isn't said again).</summary>
    public static bool HeatSaid(IEnumerable<Insight> health) => health.Any(i => i is { Key: "usual-heat", Tone: InsightTone.Warn });

    /// <summary>"The drive is healthy" / "Drives healthy", with the fullest drive after it when it's known.</summary>
    public static string DrivesHealthy(int count, string? fullest) => (count == 1, fullest) switch
    {
        (true, null) => Lang.T("The drive is healthy"),
        (true, _) => Lang.T($"The drive is healthy; {fullest}"),
        (false, null) => Lang.T("Drives healthy"),
        _ => Lang.T($"Drives healthy; {fullest}"),
    };

    /// <summary>
    /// Whether a drive needs a look: SMART reports a failure or early warning signs, or an SSD reports
    /// <see cref="WornOut"/>% of its rated life left or less (the life figure is the drive's own word, a warning only).
    /// </summary>
    public static bool DriveNeedsLook(string? status, double? lifeLeft) => status is "Bad" or "Caution" || lifeLeft is double left && left <= WornOut;

    /// <summary>An SSD that reports this much of its rated life left (percent) or less gets a warning.</summary>
    public const double WornOut = 10;

    /// <summary>The fixed drive that is fullest by share, in words ("the C drive is 58% full"); null if none can be read.</summary>
    public static string? Fullest()
    {
        try
        {
            var fullest = DriveInfo.GetDrives().Where(d => d is { DriveType: DriveType.Fixed, IsReady: true, TotalSize: > 0 })
                .MaxBy(d => 1 - (double)d.TotalFreeSpace / d.TotalSize);
            return fullest is null ? null : FullText(fullest.Name, fullest.TotalSize - fullest.TotalFreeSpace, fullest.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string FullText(string root, long usedBytes, long totalBytes)
    {
        string letter = root.TrimEnd('\\').TrimEnd(':');
        double share = Math.Round(100.0 * usedBytes / totalBytes);
        return Lang.T($"the {letter} drive is {share:0}% full");
    }

    // Home already shows the time totals and the most-used apps, so those lines are left out of its own list.
    private static readonly HashSet<string> ShownElsewhere = ["screen", "top-app"];

    /// <summary>
    /// "What stands out" away from Home (a dashboard, a widget): today's lines with what PC health says on Home, since
    /// neither has PC health beside it; today's internet in a line is for looking back (the recap), not news.
    /// </summary>
    public static List<Insight> StandOut(Report? today) => today?.Insights.Where(i => !ShownElsewhere.Contains(i.Key) && i.Key != "net-recap").Take(5).ToList() ?? [];

    /// <summary>Home's own "What stands out today": as <see cref="StandOut"/>, less what PC health says beside it.</summary>
    public static List<Insight> HomeStandOut(Report? today, IReadOnlySet<string> inHealth) =>
        today?.Insights.Where(i => !ShownElsewhere.Contains(i.Key) && !inHealth.Contains(i.Key) && i.Key != "net-recap").Take(5).ToList() ?? [];

    /// <summary>The apps used most today, as Home lists them: half a minute or more, Windows' own parts left out.</summary>
    public static List<AppStat> TopToday(Report? today, int count = 6) =>
        today?.Apps.Where(a => a.ActiveSec >= 30 && a.Category != AppCategory.System).Take(count).ToList() ?? [];

    /// <summary>A past period's apps, as Home's recap lists them: a minute or more, Windows' own parts left out.</summary>
    public static List<AppStat> TopApps(Report? r, int count = 3) =>
        r?.Apps.Where(a => a.ActiveSec >= 60 && a.Category != AppCategory.System).Take(count).ToList() ?? [];
}
