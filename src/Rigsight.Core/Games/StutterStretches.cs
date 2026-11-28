using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Drivers;
using Rigsight.Core.Settings;

namespace Rigsight.Core.Games;

/// <summary>What an observation is about, for its icon and colour on the page.</summary>
public enum StutterSeenKind { Spread, Nothing, GameDisk, PcDisk, GpuMemory, GpuBusy, CpuBusy, OtherApp, Memory, Device }

/// <summary>One thing seen around a group of stutters, as a count (never a cause). <see cref="Text"/> marks its figures **like this**.</summary>
public sealed record StutterSeen(StutterSeenKind Kind, string Text, long? App = null);

/// <summary>
/// A stretch of a play where its stutters bunched up (or, <see cref="Spread"/>, the rest of them), with up to two things
/// seen around them. <see cref="Title"/>: "8:42 to 8:57 PM", or "Spread through the play".
/// </summary>
public sealed record StutterStretch(long FromMs, long ToMs, int Count, bool Spread, string Title, string CountText, IReadOnlyList<StutterSeen> Seen)
{
    /// <summary>How far into the play it came: "22 to 37 minutes in" (empty for the spread stutters).</summary>
    public string Into { get; init; } = "";

    /// <summary>Its stutters one by one, the longest first (1.23.0, the stretch opened).</summary>
    public IReadOnlyList<StutterOne> Each { get; init; } = [];
}

/// <summary>A reading that stood out around one stutter, for its chip: "Crimson Desert using the disk" with **212 MB/s**.</summary>
public sealed record StutterChip(StutterSeenKind Kind, string Label, string Value, long? App = null);

/// <summary>
/// One stutter of a stretch (1.23.0, approved mock-up "1 · Games, each stutter in a stretch"): when, how long its frame
/// took against the game's usual frame, and the readings around it that stood out, by the same figures the stretch's
/// own lines use. <see cref="Around"/> is null for a play read by the minute (nothing was kept around each stutter);
/// empty where nothing stood out, or (<see cref="Read"/> false) where nothing was read near this one.
/// </summary>
public sealed record StutterOne(long AtMs, double Ms, double UsualMs, IReadOnlyList<StutterChip>? Around, bool Read = true)
{
    /// <summary>
    /// A play read by the minute (1.23.0, the strip's hover): what stood out in the whole minute this stutter came in, by the
    /// same figures the stretch's minute lines use; empty where nothing did, null on a play read around each stutter.
    /// </summary>
    public IReadOnlyList<StutterChip>? InMinute { get; init; }
}

/// <summary>
/// A minute of the play for its hover: around its stutters (<see cref="AtStutters"/>, the highest of the seconds around
/// each) or the minute as a whole. <see cref="Disk"/> is the game's own disk use when <see cref="DiskIsGame"/>, else every
/// app's; <see cref="BusyApp"/> another app well past its usual share of the CPU in the play, if any.
/// </summary>
public sealed record StutterMinuteRead(bool AtStutters, double? Disk, bool DiskIsGame, double? VramMb, double? VramTotalMb, double? Gpu,
    double? GpuTemp, double? Cpu, long? BusyApp, double? BusyCpu);

/// <summary>Where a play stuttered (see <see cref="StutterStretches"/>).</summary>
/// <param name="Headline">"41 stutters, 32 of them in two stretches."</param>
/// <param name="BySeconds">Read from the seconds around each stutter; false: from the minutes, for a play from before they were kept.</param>
/// <param name="OldNote">Why only minutes, when <paramref name="BySeconds"/> is false.</param>
public sealed record StutterReport(string Headline, bool BySeconds, string? OldNote, IReadOnlyList<StutterStretch> Stretches,
    IReadOnlyDictionary<long, StutterMinuteRead> Minutes, IReadOnlyDictionary<long, (string Name, string? Path)> Apps)
{
    /// <summary>The small caps under a picked stretch: which readings it was worked out from.</summary>
    public string Grain => BySeconds ? Lang.T("READ AROUND EACH STUTTER") : Lang.T("READ BY THE MINUTE");

    /// <summary>Beside the panel's heading (approved mock-up "1 · Games, each stutter in a stretch"): which readings these are.</summary>
    public string GrainLine => BySeconds ? Lang.T("Read in the seconds around each stutter") : Lang.T("Read by the minute");

    /// <summary>The stretch a minute (Unix seconds) falls in, or null (spread stutters are no stretch).</summary>
    public StutterStretch? StretchAt(long ts) => Stretches.FirstOrDefault(s => !s.Spread && ts * 1000 + 59_999 >= s.FromMs && ts * 1000 <= s.ToMs);
}

/// <summary>
/// Where a play stuttered (1.23.0, issue #18, approved mock-up "2 + 1 · Games"): its stutters grouped into stretches where
/// they came far closer together than in the rest of the play, and the rest as "Spread through the play"; for each, up to
/// two things seen around its stutters, as counts against the rest of the play: the game itself (its disk use, the GPU's
/// memory nearly full, the GPU or CPU flat out), another app far busier than usual, Windows short of memory, and a part
/// whose driver ran long at them (issue #15). Never a cause: what was measured, and how often.
/// <para>
/// Stretches (tuned on simulated plays, 2026-11-28): stutters within <see cref="StutterSnaps.ShareMs"/> of each other are
/// one moment (a hitch is often several frames), and a stretch is a run of at least <see cref="AtLeast"/> moments, closer
/// together than a few gaps (half, one and two of the rest's own gap, and 30 s to 4 min), whose count the rest's rate
/// would give less than <see cref="Unlikely"/> of the time, at least <see cref="Denser"/> times as dense as the rest, and
/// no more than <see cref="MostOfPlay"/> of the play. Up to <see cref="MostStretches"/>, the clearest first. On random
/// plays of 30 minutes to 2.5 hours, one to six stutters a minute, under 3% showed a stretch that wasn't there; a burst
/// like the mock-up's (23 in 16 minutes) was found 94% of the time, its smaller one (9 in 6 minutes) 81%.
/// </para>
/// </summary>
public static class StutterStretches
{
    public const int AtLeast = 5, MostStretches = 3;
    public const double Unlikely = 1e-3, Denser = 3, MostOfPlay = 0.6;

    /// <summary>The figure "at" a stutter must reach to be counted for each check.</summary>
    internal const double DiskAtLeast = 50e6, VramFull = 0.95, GpuFlat = 97, CpuFlat = 90, CommitFull = 90, OtherCpu = 10, Times = 3;

    /// <summary>The CPU or GPU flat out counts only where the rest of the play averaged under these, %.</summary>
    internal const double CpuRestUnder = 75, GpuRestUnder = 90;

    /// <summary>An opened stretch on a play read around each stutter shows its longest this many, then "And 9 more, 36 to 40 ms".</summary>
    public const int ShownFirst = 8;

    /// <summary>On a play read by the minute, whose short rows stand three across (1.23.0, "1b"): three full rows of three.</summary>
    public const int ShownFirstByMinute = 9;

    /// <summary>How many stutters an opened stretch shows before "And N more": <see cref="ShownFirst"/>, or <see cref="ShownFirstByMinute"/> by the minute.</summary>
    public static int First(bool around) => around ? ShownFirst : ShownFirstByMinute;

    /// <summary>The play's stutters (from <paramref name="playFrom"/> to <paramref name="playTo"/>, Unix seconds) by stretch, and each minute's readings for the chart's hover. A play without stutters has no stretches and an empty headline.</summary>
    /// <param name="game">The game's app number (its stutters' and its own minutes').</param>
    /// <param name="device">The part found by <see cref="StutterParts"/> for the play, if any: said where its long bursts met the stutters.</param>
    public static StutterReport Of(RigsightDb db, long game, string gameName, IReadOnlyList<Stutter> stutters, long playFrom, long playTo,
        RigsightSettings settings, StutterParts.Cause? device)
    {
        var sorted = stutters.OrderBy(s => s.AtMs).ToList();
        long first = playFrom / 60 * 60;
        var minutes = db.GetMinutes(first, playTo).ToDictionary(m => m.Ts);
        var appMinutes = db.GetAppMinutes(first, playTo);
        bool bySeconds = sorted.Count(s => s.Snap is not null) * 2 >= sorted.Count;
        string? note = bySeconds ? null
            : db.FirstStutterSnap() is long since && since <= playFrom * 1000
                ? Lang.T("The seconds around these stutters weren't read, so these are what the whole minutes held.")
                : Lang.T("This play was recorded before RigSight kept the seconds around each stutter, so these are what the whole minutes held.");
        var play = new Play(game, gameName, sorted, minutes, appMinutes, playFrom, playTo, device);
        var report = Build(play, bySeconds);
        var ids = report.Stretches.SelectMany(s => s.Seen).Select(s => s.App).Concat(report.Minutes.Values.Select(m => m.BusyApp))
            .Concat(report.Stretches.SelectMany(s => s.Each).SelectMany(o => (o.Around ?? []).Concat(o.InMinute ?? [])).Select(c => c.App)).OfType<long>().ToHashSet();
        var apps = ids.Count == 0 ? new Dictionary<long, (string, string?)>()
            : db.LoadApps().Where(a => ids.Contains(a.Id)).ToDictionary(a => a.Id, a => (NameOf(a, settings), a.Path));
        // The names in the words, now they're known.
        string Named(StutterSeen x) => x.App is long id && apps.TryGetValue(id, out var a) ? x.Text.Replace(AppMark, a.Item1)
            : x.Text.Replace(AppMark, x.Text.StartsWith(AppMark, StringComparison.Ordinal) ? Lang.T("An app") : Lang.T("an app"));
        string Chip(StutterChip c) => c.App is long id && apps.TryGetValue(id, out var a) ? c.Label.Replace(AppMark, a.Item1)
            : c.Label.Replace(AppMark, c.Label.StartsWith(AppMark, StringComparison.Ordinal) ? Lang.T("An app") : Lang.T("an app"));
        var stretches = report.Stretches.Select(s => s with
        {
            Seen = [.. s.Seen.Select(x => x with { Text = Named(x) })],
            Each = [.. s.Each.Select(o => Chips(o, Chip))],
        }).ToList();
        return report with { OldNote = note, Stretches = stretches, Apps = apps };
    }

    /// <summary>A stutter with the app names put into its chips (around it, or in its minute).</summary>
    private static StutterOne Chips(StutterOne o, Func<StutterChip, string> name)
    {
        static bool Has(IReadOnlyList<StutterChip>? chips) => chips is { Count: > 0 } && chips.Any(c => c.App is not null);
        if (Has(o.Around)) o = o with { Around = [.. o.Around!.Select(c => c with { Label = name(c) })] };
        if (Has(o.InMinute)) o = o with { InMinute = [.. o.InMinute!.Select(c => c with { Label = name(c) })] };
        return o;
    }

    /// <summary>The user's name for an app, else the known one, else what Windows says.</summary>
    internal static string NameOf(AppRow a, RigsightSettings s) => s.AppNames.TryGetValue(a.Exe, out var alias) ? alias : AppCatalog.KnownName(a.Exe) ?? a.Name;

    // Where another app's name goes in a line, filled in once the names are read.
    private const string AppMark = "\u0001app\u0001";

    /// <summary>Everything about the play the checks read.</summary>
    internal sealed record Play(long Game, string GameName, IReadOnlyList<Stutter> Stutters, IReadOnlyDictionary<long, SystemMinute> Minutes,
        IReadOnlyList<AppMinute> AppMinutes, long From, long To, StutterParts.Cause? Device);

    /// <summary>The report from what was read (no names yet: see <see cref="Of"/>).</summary>
    internal static StutterReport Build(Play play, bool bySeconds)
    {
        var s = play.Stutters;
        var times = s.Select(x => x.AtMs).ToList();
        long playMs = Math.Max(60_000, (play.To - play.From) * 1000);
        var groups = Find(times, playMs);
        var stretches = new List<StutterStretch>();
        var inStretch = new bool[s.Count];
        foreach (var (a, b) in groups)
        {
            for (int i = a; i <= b; i++) inStretch[i] = true;
            var group = s.Skip(a).Take(b - a + 1).ToList();
            long fromTs = times[a] / 60_000 * 60, toTs = times[b] / 60_000 * 60;
            var rest = play.Minutes.Keys.Where(t => t < fromTs || t > toTs).ToList();
            var inside = play.Minutes.Keys.Where(t => t >= fromTs && t <= toTs).ToList();
            stretches.Add(new StutterStretch(times[a], times[b], group.Count, false, Span(times[a], times[b]), Count(group.Count),
                Seen(play, group, inside, rest, bySeconds, stretch: true))
            {
                Into = MinutesIn(times[a], times[b], play.From), Each = Ones(play, group, rest, bySeconds),
            });
        }
        var spread = s.Where((_, i) => !inStretch[i]).ToList();
        if (spread.Count > 0)
        {
            var spreadMinutes = spread.Select(x => x.AtMs / 60_000 * 60).ToHashSet();
            var rest = play.Minutes.Keys.Where(t => !spreadMinutes.Contains(t) && !stretches.Any(x => t * 1000 + 59_999 >= x.FromMs && t * 1000 <= x.ToMs)).ToList();
            var seen = Seen(play, spread, [.. spreadMinutes.Where(play.Minutes.ContainsKey)], rest, bySeconds, stretch: false);
            double restMin = Math.Max(1, playMs / 60_000.0 - stretches.Sum(x => (x.ToMs - x.FromMs) / 60_000.0));
            if (spread.Count >= 2)
            {
                string every = StutterCauses.Every(restMin / spread.Count);
                string lead = char.ToUpper(every[0], System.Globalization.CultureInfo.CurrentCulture) + every[1..] + ".";
                // With nothing seen, the one line says both (approved mock-up: "About one every 9 minutes. Nothing measured stood out around them.").
                seen = seen is [{ Kind: StutterSeenKind.Nothing } nothing] ? [new(StutterSeenKind.Spread, lead + " " + nothing.Text)] : [new(StutterSeenKind.Spread, lead), .. seen];
            }
            stretches.Add(new StutterStretch(spread[0].AtMs, spread[^1].AtMs, spread.Count, true,
                Lang.T("Spread through the play"), Count(spread.Count), seen) { Each = Ones(play, spread, rest, bySeconds) });
        }
        return new StutterReport(Headline(s.Count, groups.Count, groups.Sum(g => g.To - g.From + 1)), bySeconds, null, stretches,
            MinuteReads(play, bySeconds), new Dictionary<long, (string, string?)>());
    }

    private static string Count(int n) => n == 1 ? Lang.T("1 stutter") : Lang.T($"{n:N0} stutters");

    /// <summary>"41 stutters, 32 of them in two stretches."</summary>
    internal static string Headline(int all, int stretches, int inThem)
    {
        if (all == 0) return "";
        if (stretches == 0) return all == 1 ? Lang.T("1 stutter in this play.") : Lang.T($"{all:N0} stutters, spread through the play.");
        string how = stretches switch { 1 => Lang.T("one stretch"), 2 => Lang.T("two stretches"), _ => Lang.T("three stretches") };
        return inThem >= all ? Lang.T($"{all:N0} stutters, all in {how}.") : Lang.T($"{all:N0} stutters, {inThem:N0} of them in {how}.");
    }

    /// <summary>"8:42 to 8:57 PM", or one time when both are in the same minute.</summary>
    private static string Span(long fromMs, long toMs)
    {
        var a = TimeUtil.FromUnix(fromMs / 1000);
        var b = TimeUtil.FromUnix(toMs / 1000);
        if (a.Date == b.Date && a.Hour == b.Hour && a.Minute == b.Minute) return TimeText.At(a, "h:mm tt");
        bool sameHalf = a.Date == b.Date && a.Hour / 12 == b.Hour / 12;
        return Lang.T($"{TimeText.At(a, sameHalf ? "h:mm" : "h:mm tt")} to {TimeText.At(b, "h:mm tt")}");
    }

    // ── Stretches ───────────────────────────────────────────────────────

    /// <summary>
    /// The stretches among stutter times (Unix ms, oldest first) in a play <paramref name="playMs"/> long: index ranges into
    /// the times, oldest first. See the class for the rule.
    /// </summary>
    internal static List<(int From, int To)> Find(IReadOnlyList<long> times, long playMs)
    {
        // Moments: a stutter and the ones within ShareMs of it.
        var moments = new List<(int First, int Last)>();
        for (int i = 0; i < times.Count;)
        {
            int j = i;
            while (j + 1 < times.Count && times[j + 1] - times[i] <= StutterSnaps.ShareMs) j++;
            moments.Add((i, j));
            i = j + 1;
        }
        var found = new List<(int From, int To)>();
        int n = moments.Count;
        if (n < AtLeast) return [];
        double playMin = playMs / 60_000.0;
        var taken = new bool[n];
        double Start(int m) => times[moments[m].First] / 60_000.0;
        double End(int m) => times[moments[m].Last] / 60_000.0;
        for (int round = 0; round < MostStretches; round++)
        {
            int restN = n - found.Sum(f => f.To - f.From + 1);
            double restMin = Math.Max(1, playMin - found.Sum(f => End(f.To) - Start(f.From)));
            if (restN < AtLeast) break;
            double rate = restN / restMin, gap = restMin / restN;
            var gaps = new[] { gap / 2, gap, gap * 2, 0.5, 1, 2, 4 }.Select(g => Math.Clamp(g, 0.5, 5)).Distinct().Order();
            (int From, int To, double P)? best = null;
            foreach (double g in gaps)
            {
                for (int i = 0; i < n;)
                {
                    if (taken[i]) { i++; continue; }
                    int j = i;
                    while (j + 1 < n && !taken[j + 1] && Start(j + 1) - End(j) <= g) j++;
                    int k = j - i + 1;
                    double span = End(j) - Start(i), counted = Math.Max(1, span);
                    if (k >= AtLeast && span <= MostOfPlay * playMin && k / counted >= Denser * rate)
                    {
                        double p = Tail(k, rate * counted + 0.5);
                        if (p < Unlikely && (best is null || p < best.Value.P)) best = (i, j, p);
                    }
                    i = j + 1;
                }
            }
            if (best is not { } b) break;
            found.Add((b.From, b.To));
            for (int x = b.From; x <= b.To; x++) taken[x] = true;
        }
        return [.. found.OrderBy(f => f.From).Select(f => (moments[f.From].First, moments[f.To].Last))];
    }

    /// <summary>How often a count of <paramref name="k"/> or more comes by chance where <paramref name="mu"/> are expected (Poisson).</summary>
    internal static double Tail(int k, double mu)
    {
        if (mu <= 0) return k <= 0 ? 1 : 0;
        if (k <= mu) return 1;
        double lp = -mu + k * Math.Log(mu) - LogFactorial(k), sum = 0;
        for (int i = k; i < k + 10_000; i++)
        {
            double term = Math.Exp(lp);
            sum += term;
            if (term < sum * 1e-12) break;
            lp += Math.Log(mu) - Math.Log(i + 1);
        }
        return Math.Min(1, sum);
    }

    private static double LogFactorial(int k)
    {
        if (k < 2) return 0;
        if (k > 30) return k * Math.Log(k) - k + 0.5 * Math.Log(2 * Math.PI * k) + 1.0 / (12 * k);
        double sum = 0;
        for (int i = 2; i <= k; i++) sum += Math.Log(i);
        return sum;
    }

    // ── What was seen ───────────────────────────────────────────────────

    /// <summary>
    /// Up to two things seen around a group of stutters, against the rest of the play (<paramref name="rest"/>, its minutes):
    /// from the seconds around each, or from <paramref name="inside"/>, the group's own minutes. "Nothing … stood out" when none.
    /// </summary>
    private static List<StutterSeen> Seen(Play play, IReadOnlyList<Stutter> group, IReadOnlyList<long> inside, IReadOnlyList<long> rest, bool bySeconds, bool stretch)
    {
        var seen = new List<StutterSeen>();
        if (DeviceSeen(play, group) is { } device) seen.Add(device);
        var checks = bySeconds ? SecondChecks(play, group, rest) : MinuteChecks(play, inside, rest, stretch);
        foreach (var check in checks)
        {
            if (seen.Count >= 2) break;
            if (check() is { } found) seen.Add(found);
        }
        if (seen.Count == 0)
            seen.Add(new(StutterSeenKind.Nothing, bySeconds
                ? group.Count == 1 ? Lang.T("Nothing measured stood out around it.") : Lang.T("Nothing measured stood out around them.")
                : Lang.T("Nothing stood out in those minutes.")));
        return seen;
    }

    /// <summary>"At **17 of these 23** stutters", "At **all 23** of these stutters", "At this stutter".</summary>
    internal static string At(int hits, int of) =>
        of == 1 ? Lang.T("At this stutter")
        : hits >= of ? (of == 2 ? Lang.T("At **both** of these stutters") : Lang.T($"At **all {of}** of these stutters"))
        : Lang.T($"At **{hits} of these {of}** stutters");

    /// <summary>"around **all 23**", "around **19 of these 23**", "around it".</summary>
    private static string Around(int hits, int of) =>
        of == 1 ? Lang.T("around it") : hits >= of ? Lang.T($"around **all {of}**") : Lang.T($"around **{hits} of these {of}**");

    /// <summary>A range of figures: "**180 to 320**", or one figure where they round the same.</summary>
    private static string Range(IEnumerable<double> values, string format)
    {
        var list = values.ToList();
        string lo = list.Min().ToString(format, System.Globalization.CultureInfo.CurrentCulture), hi = list.Max().ToString(format, System.Globalization.CultureInfo.CurrentCulture);
        return lo == hi ? lo : Lang.T($"{lo} to {hi}");
    }

    private static string Mbs(double bytes) => bytes < 1e6 ? Lang.T("under 1 MB/s") : $"{bytes / 1e6:0} MB/s";
    private static string Share(double pct) => pct < 2 ? Lang.T("under 2%") : $"{pct:0}%";

    /// <summary>Enough of the readings for a group to be said: most of them, and at least a few where there are a few.</summary>
    private static bool Enough(int hits, int read) => read > 0 && hits >= Math.Min(3, read) && hits * 2 >= read;

    /// <summary>The game's own disk use in each of these minutes (0 in a minute it wasn't among the busiest apps), or null without app minutes.</summary>
    private static double? GameDisk(Play play, IReadOnlyList<long> minutes)
    {
        if (play.AppMinutes.Count == 0 || minutes.Count == 0) return null;
        var set = minutes.ToHashSet();
        return play.AppMinutes.Where(a => a.App == play.Game && set.Contains(a.Ts)).Sum(a => a.Disk) / minutes.Count;
    }

    /// <summary>An app's average share of the CPU over these minutes (0 in a minute it wasn't among the busiest).</summary>
    private static double AppCpu(Play play, long app, IReadOnlyList<long> minutes)
    {
        if (minutes.Count == 0) return 0;
        var set = minutes.ToHashSet();
        return play.AppMinutes.Where(a => a.App == app && set.Contains(a.Ts)).Sum(a => a.Cpu) / minutes.Count;
    }

    private static double? Average(Play play, IReadOnlyList<long> minutes, Func<SystemMinute, double?> value)
    {
        var values = minutes.Select(t => play.Minutes.TryGetValue(t, out var m) ? value(m) : null).OfType<double>().ToList();
        return values.Count == 0 ? null : values.Average();
    }

    private static string Against(string figure) => Lang.T($", against {figure} in the rest of the play");

    /// <summary>The checks on the seconds around each stutter, the game's own first.</summary>
    private static IEnumerable<Func<StutterSeen?>> SecondChecks(Play play, IReadOnlyList<Stutter> group, IReadOnlyList<long> rest)
    {
        var snaps = group.Where(s => s.Snap is not null).Select(s => s.Snap!).ToList();
        int k = group.Count;

        // The disk: the game's own use where it was read (it was in front), else every app's together.
        yield return () =>
        {
            var withDisk = snaps.Where(s => s.Disk is not null).ToList();
            if (withDisk.Count == 0) return null;
            bool own = withDisk.Count(s => s.DiskIsGame) * 2 >= withDisk.Count;
            var read = withDisk.Where(s => s.DiskIsGame == own).ToList();
            double? usual = own ? GameDisk(play, rest) : Average(play, rest, m => m.DiskRate);
            double floor = Math.Max(DiskAtLeast, Times * (usual ?? 0));
            var hits = read.Where(s => s.Disk >= floor).Select(s => s.Disk!.Value).ToList();
            if (!Enough(hits.Count, read.Count)) return null;
            string range = Range(hits.Select(d => d / 1e6), "0");
            string tail = usual is double u ? Against(Mbs(u)) : "";
            return own
                ? new(StutterSeenKind.GameDisk, Lang.T($"{At(hits.Count, k)}, {play.GameName} was using the disk at **{range} MB/s**{tail}."))
                : new(StutterSeenKind.PcDisk, Lang.T($"{At(hits.Count, k)}, apps were using the disk at **{range} MB/s** all together{tail}."));
        };

        // The GPU's memory all but full.
        yield return () =>
        {
            var read = snaps.Where(s => s.VramMb is not null && s.VramTotalMb is > 0).ToList();
            var hits = read.Where(s => s.VramMb!.Value / s.VramTotalMb!.Value >= VramFull).ToList();
            if (!Enough(hits.Count, read.Count)) return null;
            double total = hits.Max(s => s.VramTotalMb!.Value) / 1024;
            string range = Range(hits.Select(s => s.VramMb!.Value / 1024), "0.0");
            double? usual = Average(play, rest, m => m.VramUsed is double v && m.VramTotal is > 0 ? v / m.VramTotal.Value : null);
            string tail = usual is not double u ? "" : u >= 0.9 ? Lang.T(", much as in the rest of the play") : Against(Lang.T($"{u * total:0.0} GB"));
            return new(StutterSeenKind.GpuMemory, Lang.T($"GPU memory was at **{range} GB of {total:0} GB** {Around(hits.Count, k)}{tail}."));
        };

        // Another app far busier on the CPU than it was in the rest of the play.
        yield return () =>
        {
            var read = snaps.Where(s => s.OtherApp is not null && s.OtherCpu is not null).ToList();
            var best = read.Where(s => s.OtherCpu >= Math.Max(OtherCpu, Times * AppCpu(play, s.OtherApp!.Value, rest)))
                .GroupBy(s => s.OtherApp!.Value).MaxBy(g => g.Count());
            if (best is null || !Enough(best.Count(), snaps.Count)) return null;
            string range = Range(best.Select(s => s.OtherCpu!.Value), "0");
            return new(StutterSeenKind.OtherApp, Lang.T($"{At(best.Count(), k)}, {AppMark} was using **{range}%** of the CPU{Against(Share(AppCpu(play, best.Key, rest)))}."), best.Key);
        };

        // The CPU flat out, where it wasn't in the rest of the play.
        yield return () => Flat(snaps, k, s => s.Cpu, CpuFlat, Average(play, rest, m => m.CpuLoad), CpuRestUnder, StutterSeenKind.CpuBusy, "CPU");
        // The GPU flat out: in most games it is, so only where it wasn't in the rest of the play.
        yield return () => Flat(snaps, k, s => s.Gpu, GpuFlat, Average(play, rest, m => m.GpuLoad), GpuRestUnder, StutterSeenKind.GpuBusy, "GPU");

        // Windows nearly out of memory to promise.
        yield return () =>
        {
            var read = snaps.Where(s => s.Commit is not null).ToList();
            var hits = read.Where(s => s.Commit >= CommitFull).Select(s => s.Commit!.Value).ToList();
            if (!Enough(hits.Count, read.Count)) return null;
            if (rest.Any(t => play.Minutes.TryGetValue(t, out var m) && m.CommitPct >= CommitFull)) return null;
            return new(StutterSeenKind.Memory, Lang.T($"{At(hits.Count, k)}, Windows had already promised **{Range(hits, "0")}%** of its memory and page file to apps."));
        };
    }

    private static StutterSeen? Flat(List<StutterSnap> snaps, int k, Func<StutterSnap, double?> value, double flat, double? usual, double under, StutterSeenKind kind, string part)
    {
        if (usual is not double u || u >= under) return null;
        var read = snaps.Select(value).OfType<double>().ToList();
        var hits = read.Where(v => v >= flat).ToList();
        if (!Enough(hits.Count, read.Count)) return null;
        return new(kind, Lang.T($"{At(hits.Count, k)}, the {part} was **{Range(hits, "0")}%** busy{Against(Lang.T($"{u:0}% on average"))}."));
    }

    /// <summary>The checks on whole minutes (a play from before the seconds were kept), the game's own first.</summary>
    private static IEnumerable<Func<StutterSeen?>> MinuteChecks(Play play, IReadOnlyList<long> inside, IReadOnlyList<long> rest, bool stretch)
    {
        string where = stretch ? Lang.T("in these minutes") : Lang.T("in the minutes they came in");
        if (inside.Count == 0) yield break;

        yield return () =>
        {
            if (GameDisk(play, inside) is not double d || d < 20e6) return null;
            double? usual = GameDisk(play, rest);
            if (usual is double u && d < Times * u) return null;
            return new(StutterSeenKind.GameDisk, Lang.T($"{play.GameName} used the disk at **{d / 1e6:0} MB/s** on average {where}{(usual is double u2 ? Against(Mbs(u2)) : "")}."));
        };

        yield return () =>
        {
            var full = inside.Select(t => play.Minutes.TryGetValue(t, out var m) && m.VramUsed is double v && m.VramTotal is > 0 ? (v, m.VramTotal.Value) : ((double, double)?)null)
                .OfType<(double Used, double Total)>().ToList();
            if (full.Count == 0 || full.Average(f => f.Used / f.Total) < 0.93) return null;
            return new(StutterSeenKind.GpuMemory, Lang.T($"GPU memory averaged **{full.Average(f => f.Used) / 1024:0.0} GB of {full.Max(f => f.Total) / 1024:0} GB** {where}."));
        };

        yield return () =>
        {
            var set = inside.ToHashSet();
            var busiest = play.AppMinutes.Where(a => a.App != play.Game && set.Contains(a.Ts)).GroupBy(a => a.App)
                .Select(g => (App: g.Key, Cpu: g.Sum(a => a.Cpu) / inside.Count)).OrderByDescending(x => x.Cpu).FirstOrDefault();
            if (busiest.Cpu < OtherCpu) return null;
            double usual = AppCpu(play, busiest.App, rest);
            if (busiest.Cpu < Times * usual) return null;
            return new(StutterSeenKind.OtherApp, Lang.T($"{AppMark} used **{busiest.Cpu:0}%** of the CPU on average {where}{Against(Share(usual))}."), busiest.App);
        };

        yield return () => FlatMinutes(play, inside, rest, m => m.CpuLoad, CpuFlat, CpuRestUnder, StutterSeenKind.CpuBusy, "CPU", where);
        yield return () => FlatMinutes(play, inside, rest, m => m.GpuLoad, GpuFlat, GpuRestUnder, StutterSeenKind.GpuBusy, "GPU", where);

        yield return () =>
        {
            var high = inside.Select(t => play.Minutes.TryGetValue(t, out var m) ? m.CommitPct : null).OfType<double>().DefaultIfEmpty(0).Max();
            if (high < CommitFull || rest.Any(t => play.Minutes.TryGetValue(t, out var m) && m.CommitPct >= CommitFull)) return null;
            return new(StutterSeenKind.Memory, Lang.T($"Windows had promised up to **{high:0}%** of its memory and page file to apps {where}."));
        };
    }

    private static StutterSeen? FlatMinutes(Play play, IReadOnlyList<long> inside, IReadOnlyList<long> rest, Func<SystemMinute, double?> value, double flat, double under,
        StutterSeenKind kind, string part, string where)
    {
        if (Average(play, inside, value) is not double v || v < flat || Average(play, rest, value) is not double u || u >= under) return null;
        return new(kind, Lang.T($"The {part} was **{v:0}%** busy on average {where}{Against(Lang.T($"{u:0}%"))}."));
    }

    /// <summary>
    /// The part found for the whole play (issue #15), said for a group where its long bursts met at least two of its
    /// stutters and a fifth of them: "At **12 of these 23** stutters, your Ethernet adapter was using the CPU for much longer than usual."
    /// </summary>
    private static StutterSeen? DeviceSeen(Play play, IReadOnlyList<Stutter> group)
    {
        if (play.Device is not { Part.Say: { } say } device) return null;
        int floor = (int)Math.Max(DriverRules.LongUs, DriverRules.MuchLonger * device.UsualUs);
        int hits = group.Count(s => string.Equals(s.Drv, device.Part.Driver, StringComparison.OrdinalIgnoreCase) && s.DrvUs >= floor);
        if (hits < 2 || hits < StutterParts.Share * group.Count) return null;
        return new(StutterSeenKind.Device, Lang.T($"{At(hits, group.Count)}, your {say} was using the CPU for much longer than usual."));
    }

    // ── Each stutter, the stretch opened (1.23.0) ───────────────────────

    /// <summary>
    /// A group's stutters one by one, the longest first, each with the readings around it that stood out: the same
    /// figures and the same rest of the play the group's own lines are checked against (<see cref="SecondChecks"/>,
    /// <see cref="DeviceSeen"/>), for one stutter instead of a count. A play read by the minute has nothing around each.
    /// </summary>
    private static List<StutterOne> Ones(Play play, IReadOnlyList<Stutter> group, IReadOnlyList<long> rest, bool bySeconds)
    {
        var longest = group.OrderByDescending(s => s.Ms).ThenBy(s => s.AtMs).ToList();
        if (!bySeconds)
        {
            var minute = MinuteFinder(play, rest);
            return [.. longest.Select(s => new StutterOne(s.AtMs, s.Ms, s.UsualMs, null) { InMinute = minute(s.AtMs / 60_000 * 60) })];
        }
        // The rest of the play, read once for the group.
        double? gameDisk = GameDisk(play, rest), allDisk = Average(play, rest, m => m.DiskRate);
        double? cpu = Average(play, rest, m => m.CpuLoad), gpu = Average(play, rest, m => m.GpuLoad);
        bool commitFull = rest.Any(t => play.Minutes.TryGetValue(t, out var m) && m.CommitPct >= CommitFull);
        var usual = new Dictionary<long, double>();
        double Usual(long app) => usual.TryGetValue(app, out var u) ? u : usual[app] = AppCpu(play, app, rest);
        var device = play.Device is { Part.Say: { } say } d ? (d.Part.Driver, Say: say, Floor: (int)Math.Max(DriverRules.LongUs, DriverRules.MuchLonger * d.UsualUs)) : default;

        var list = new List<StutterOne>(longest.Count);
        foreach (var s in longest)
        {
            var chips = new List<StutterChip>();
            if (device.Say is { } part && string.Equals(s.Drv, device.Driver, StringComparison.OrdinalIgnoreCase) && s.DrvUs >= device.Floor)
                chips.Add(new(StutterSeenKind.Device, Lang.T($"Your {part} on the CPU"), LatencyWords.Ms(s.DrvUs)));
            if (s.Snap is { } n)
            {
                if (n.Disk is double disk && disk >= Math.Max(DiskAtLeast, Times * ((n.DiskIsGame ? gameDisk : allDisk) ?? 0)))
                    chips.Add(n.DiskIsGame
                        ? new(StutterSeenKind.GameDisk, Lang.T($"{play.GameName} using the disk"), Mbs(disk))
                        : new(StutterSeenKind.PcDisk, Lang.T("All apps using the disk"), Mbs(disk)));
                if (n.VramMb is double v && n.VramTotalMb is double total && total > 0 && v / total >= VramFull)
                    chips.Add(new(StutterSeenKind.GpuMemory, Lang.T("GPU memory"), Lang.T($"{v / 1024:0.0} of {total / 1024:0} GB")));
                if (n.OtherApp is long app && n.OtherCpu is double share && share >= Math.Max(OtherCpu, Times * Usual(app)))
                    chips.Add(new(StutterSeenKind.OtherApp, Lang.T($"{AppMark} using the CPU"), $"{share:0}%", app));
                if (cpu is < CpuRestUnder && n.Cpu is double nc && nc >= CpuFlat)
                    chips.Add(new(StutterSeenKind.CpuBusy, "CPU", Lang.T($"{nc:0}% busy")));
                if (gpu is < GpuRestUnder && n.Gpu is double ng && ng >= GpuFlat)
                    chips.Add(new(StutterSeenKind.GpuBusy, "GPU", Lang.T($"{ng:0}% busy")));
                if (!commitFull && n.Commit is double promised && promised >= CommitFull)
                    chips.Add(new(StutterSeenKind.Memory, Lang.T("Memory promised to apps"), $"{promised:0}%"));
            }
            list.Add(new StutterOne(s.AtMs, s.Ms, s.UsualMs, chips, s.Snap is not null || chips.Count > 0));
        }
        return list;
    }

    /// <summary>
    /// On a play read by the minute, what stood out in one minute (Unix seconds) against the rest of the play
    /// (<paramref name="rest"/>, its minutes), by the figures of <see cref="MinuteChecks"/>: the game's own disk use, the
    /// GPU's memory nearly full, another app far busier than usual, the CPU or GPU flat out where they weren't, Windows
    /// short of memory. Each minute is worked out once, however many stutters it held.
    /// </summary>
    private static Func<long, IReadOnlyList<StutterChip>> MinuteFinder(Play play, IReadOnlyList<long> rest)
    {
        double? gameDisk = GameDisk(play, rest);
        double? cpu = Average(play, rest, m => m.CpuLoad), gpu = Average(play, rest, m => m.GpuLoad);
        bool commitFull = rest.Any(t => play.Minutes.TryGetValue(t, out var m) && m.CommitPct >= CommitFull);
        var appsByMinute = play.AppMinutes.ToLookup(a => a.Ts);
        var usual = new Dictionary<long, double>();
        double Usual(long app) => usual.TryGetValue(app, out var u) ? u : usual[app] = AppCpu(play, app, rest);
        var found = new Dictionary<long, IReadOnlyList<StutterChip>>();
        return ts =>
        {
            if (found.TryGetValue(ts, out var known)) return known;
            var chips = new List<StutterChip>();
            if (play.Minutes.TryGetValue(ts, out var m))
            {
                var apps = appsByMinute[ts].ToList();
                if (play.AppMinutes.Count > 0 && apps.Where(a => a.App == play.Game).Sum(a => a.Disk) is var disk && disk >= 20e6 && (gameDisk is not double gd || disk >= Times * gd))
                    chips.Add(new(StutterSeenKind.GameDisk, Lang.T($"{play.GameName} using the disk"), Mbs(disk)));
                if (m.VramUsed is double v && m.VramTotal is double total && total > 0 && v / total >= 0.93)
                    chips.Add(new(StutterSeenKind.GpuMemory, Lang.T("GPU memory"), Lang.T($"{v / 1024:0.0} of {total / 1024:0} GB")));
                if (apps.Where(a => a.App != play.Game && a.Cpu >= Math.Max(OtherCpu, Times * Usual(a.App))).MaxBy(a => a.Cpu) is { } other)
                    chips.Add(new(StutterSeenKind.OtherApp, Lang.T($"{AppMark} using the CPU"), $"{other.Cpu:0}%", other.App));
                if (cpu is < CpuRestUnder && m.CpuLoad is double c && c >= CpuFlat)
                    chips.Add(new(StutterSeenKind.CpuBusy, "CPU", Lang.T($"{c:0}% busy")));
                if (gpu is < GpuRestUnder && m.GpuLoad is double g && g >= GpuFlat)
                    chips.Add(new(StutterSeenKind.GpuBusy, "GPU", Lang.T($"{g:0}% busy")));
                if (!commitFull && m.CommitPct is double promised && promised >= CommitFull)
                    chips.Add(new(StutterSeenKind.Memory, Lang.T("Memory promised to apps"), $"{promised:0}%"));
            }
            return found[ts] = chips;
        };
    }

    /// <summary>"22 to 37 minutes in", "22 minutes in": how far into the play (from <paramref name="playFrom"/>, Unix seconds) a stretch came.</summary>
    internal static string MinutesIn(long fromMs, long toMs, long playFrom)
    {
        long a = Math.Max(0, fromMs / 1000 - playFrom) / 60, b = Math.Max(0, toMs / 1000 - playFrom) / 60;
        return a == b ? (a == 1 ? Lang.T("1 minute in") : Lang.T($"{a} minutes in")) : Lang.T($"{a} to {b} minutes in");
    }

    /// <summary>"22m in", "1h 05m in": how far into the play (from <paramref name="playFrom"/>, Unix seconds) a stutter came.</summary>
    public static string In(long atMs, long playFrom) => Lang.T($"{Units.Duration(Math.Max(0, atMs / 1000 - playFrom))} in");

    /// <summary>Under an opened stretch's first stutters, the rest: "And 9 more, 36 to 40 ms"; empty with none.</summary>
    public static string More(IReadOnlyList<StutterOne> rest)
    {
        if (rest.Count == 0) return "";
        string lo = $"{rest.Min(o => o.Ms):0}", hi = $"{rest.Max(o => o.Ms):0}";
        return lo == hi ? Lang.T($"And {rest.Count} more, {lo} ms") : Lang.T($"And {rest.Count} more, {lo} to {hi} ms");
    }

    // ── The minutes, for the chart's hover ─────────────────────────────

    private static Dictionary<long, StutterMinuteRead> MinuteReads(Play play, bool bySeconds)
    {
        var reads = new Dictionary<long, StutterMinuteRead>();
        var snapsByMinute = play.Stutters.Where(s => s.Snap is not null).GroupBy(s => s.AtMs / 60_000 * 60).ToDictionary(g => g.Key, g => g.Select(s => s.Snap!).ToList());
        var appsByMinute = play.AppMinutes.ToLookup(a => a.Ts);
        var all = play.Minutes.Keys.ToList();
        var usual = new Dictionary<long, double>();
        double Usual(long app) => usual.TryGetValue(app, out var u) ? u : usual[app] = AppCpu(play, app, all);
        bool hasApps = play.AppMinutes.Count > 0;
        foreach (var (ts, m) in play.Minutes)
        {
            if (bySeconds && snapsByMinute.TryGetValue(ts, out var snaps))
            {
                var own = snaps.Where(s => s.DiskIsGame && s.Disk is not null).ToList();
                var vram = snaps.Where(s => s.VramMb is not null && s.VramTotalMb is > 0).MaxBy(s => s.VramMb!.Value / s.VramTotalMb!.Value);
                var busy = snaps.Where(s => s.OtherApp is long a && s.OtherCpu >= Math.Max(OtherCpu, Times * Usual(a))).MaxBy(s => s.OtherCpu);
                reads[ts] = new StutterMinuteRead(true, own.Count > 0 ? own.Max(s => s.Disk) : snaps.Max(s => s.Disk), own.Count > 0,
                    vram?.VramMb, vram?.VramTotalMb, snaps.Max(s => s.Gpu), snaps.Max(s => s.GpuTemp), snaps.Max(s => s.Cpu), busy?.OtherApp, busy?.OtherCpu);
                continue;
            }
            var apps = appsByMinute[ts].ToList();
            double? gameDisk = hasApps ? apps.Where(a => a.App == play.Game).Sum(a => a.Disk) : null;
            var other = apps.Where(a => a.App != play.Game && a.Cpu >= Math.Max(OtherCpu, Times * Usual(a.App))).MaxBy(a => a.Cpu);
            reads[ts] = new StutterMinuteRead(false, gameDisk ?? m.DiskRate, gameDisk is not null, m.VramUsed, m.VramTotal, m.GpuLoad, m.GpuTemp, m.CpuLoad,
                other?.App, other?.Cpu);
        }
        return reads;
    }
}
