using System.Globalization;
using System.Text.RegularExpressions;
using Rigsight.Core.Data;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Parts;

/// <summary>A graphics driver update of the page's GPU, as the inventory's line has it (the Timeline's driver line).</summary>
/// <param name="Title">The line's title in the language in use ("NVIDIA graphics driver 621.24").</param>
/// <param name="DeviceKey">The driver's device in the driver history (its versions open from it); null where none is followed.</param>
public sealed record PartDriverUpdate(DateTime At, string Title, string Version, string? Was, string? DeviceKey);

/// <summary>A part taken out for another: the old one's full name (null: not known, a GPU put in beside another) and the new one's.</summary>
public sealed record PartSwap(DateTime At, string? OldName, string NewName);

/// <summary>
/// What the GPU and CPU pages' history marks (1.23.0, approved mock-up "after-test" section 2): the GPU's graphics driver
/// updates (the inventory's lines, the ones the Timeline and the game chart show) and the part swaps (the inventory's
/// hardware lines: a GPU taken out and another put in, "CPU changed to"). Read from the changes already recorded; nothing new is kept.
/// </summary>
public sealed record PartEvents(IReadOnlyList<PartDriverUpdate> Drivers, IReadOnlyList<PartSwap> Swaps)
{
    public static readonly PartEvents None = new([], []);

    /// <summary>What was found and what was left out, for the log (null: nothing to say).</summary>
    public string? Said { get; init; }

    /// <summary>A GPU taken out and one put in within this long of each other are a swap.</summary>
    public static readonly TimeSpan PairWithin = TimeSpan.FromDays(1);

    /// <param name="changes">The Timeline's changes, oldest first or in any order.</param>
    /// <param name="drivers">The driver history (GPU page): which device each graphics driver line was for.</param>
    /// <param name="current">The part the page shows now (the sensors' name): the last swap must have put it in.</param>
    public static PartEvents Of(Part part, IReadOnlyList<SystemChange> changes, DriverHistory? drivers, string? current)
    {
        static string Subject(SystemChange c) =>
            c.Subject.StartsWith(Inventory.ExactPrefix, StringComparison.Ordinal) ? c.Subject[Inventory.ExactPrefix.Length..] : c.Subject;
        var hardware = changes.Where(c => c.Kind == ChangeKind.Hardware).OrderBy(c => c.Time).ToList();
        var found = new List<PartSwap>();
        if (part == Part.Cpu)
        {
            foreach (var c in hardware.Where(c => Subject(c).StartsWith(Inventory.Cpu + ":", StringComparison.Ordinal)))
                if (c.Was is { Length: > 0 } was && c.Now is { Length: > 0 } now && !PartMarks.SameName(part, was, now))
                    found.Add(new PartSwap(c.Time, was, now));
        }
        else
        {
            var gpus = hardware.Where(c => Subject(c).StartsWith(Inventory.Gpu + ":", StringComparison.Ordinal)).ToList();
            var removed = gpus.Where(c => c.Title.StartsWith(Inventory.CardRemoved, StringComparison.Ordinal)).ToList();
            foreach (var added in gpus.Where(c => c.Title.StartsWith(Inventory.CardAdded, StringComparison.Ordinal)))
            {
                string name = added.Title[Inventory.CardAdded.Length..].Trim();
                var gone = removed.Where(r => (r.Time - added.Time).Duration() <= PairWithin).MinBy(r => (r.Time - added.Time).Duration());
                string? old = gone?.Title[Inventory.CardRemoved.Length..].Trim();
                // The same card back (put in another slot, seated again) is no new part.
                if (old is not null && PartMarks.SameName(part, old, name)) continue;
                if (name.Length > 0) found.Add(new PartSwap(added.Time, old, name));
            }
        }

        // Only the swaps that lead to the part shown now: the last one put it in, each one before put in the part the next
        // one took out. A card put in beside the one the page follows (a second GPU) is no swap of it.
        var swaps = found;
        string? said = null;
        if (current is { Length: > 0 } && found.Count > 0)
        {
            var chain = new List<PartSwap>();
            string want = current;
            for (int i = found.Count - 1; i >= 0; i--)
            {
                var s = found[i];
                if (!PartMarks.SameName(part, s.NewName, want)) continue;
                chain.Insert(0, s);
                if (s.OldName is not { Length: > 0 } before) break;
                want = before;
            }
            if (chain.Count != found.Count)
                said = $"Expected each {part} change on record to lead to {current}; kept {chain.Count} of {found.Count} ({string.Join("; ", found.Select(s => $"{s.At:d MMM HH:mm} {s.OldName ?? "?"} to {s.NewName}"))})";
            swaps = chain;
        }

        var updates = new List<PartDriverUpdate>();
        if (part == Part.Gpu)
        {
            var lines = drivers?.WithChanges(changes).Lines;
            var present = drivers?.Devices.Where(d => d.IsGraphicsCard && !d.Gone).ToList() ?? [];
            var mine = present.Count > 1 && current is { Length: > 0 }
                ? present.FirstOrDefault(d => PartMarks.SameName(part, d.Name, current)) ?? drivers!.GraphicsCard
                : drivers?.GraphicsCard;
            int other = 0;
            foreach (var c in changes.Where(c => c.IsGraphicsDriver && c.Now is { Length: > 0 }).OrderBy(c => c.Time))
            {
                string? key = lines is not null && lines.TryGetValue(c, out var line) ? line.DeviceKey : null;
                // Another GPU still in the PC (a laptop's built-in graphics beside its own GPU) isn't this page's.
                if (key is not null && mine is not null && !key.Equals(mine.Key, StringComparison.OrdinalIgnoreCase)
                    && present.Any(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) { other++; continue; }
                updates.Add(new PartDriverUpdate(c.Time, c.Shown, c.Now!, c.Was, key ?? mine?.Key));
            }
            if (other > 0) said = (said is null ? "" : said + ". ") + $"Left out {other} graphics driver update(s) of another GPU still in the PC";
        }
        return new PartEvents(updates, swaps) { Said = said };
    }
}

/// <summary>Which of the two a mark is.</summary>
public enum PartMarkKind { Driver, NewPart }

/// <summary>
/// One side of a mark: its time (cut at the change before or after it, and at <see cref="PartMarks.WindowDays"/> days), and
/// the part with a game in front in it, all games together and each game.
/// </summary>
/// <param name="Since">The side after a change still the current one, running to now ("since").</param>
public sealed record PartSpan(DateTime From, DateTime To, bool Since, PartSide? Games, IReadOnlyDictionary<long, PartSide> ByGame)
{
    public static PartSpan Empty(DateTime at) => new(at, at, false, null, new Dictionary<long, PartSide>());

    /// <summary>Its length in whole days, at least one.</summary>
    public int Days => Math.Max(1, (int)Math.Round((To - From).TotalDays));
}

/// <summary>
/// A mark on the GPU or CPU history: a graphics driver update (a dashed line) or a new part (a solid line), at the left
/// edge of the stretch <see cref="Slot"/> it came in, with the part with a game in front before and after it.
/// </summary>
/// <param name="Title">A driver: the line's title ("NVIDIA graphics driver 621.24"); a new part: its full name.</param>
/// <param name="Now">A driver: its version; a new part: its full name.</param>
/// <param name="Was">The driver's version before, or the part before (its full name); null where not known.</param>
/// <param name="DeviceKey">A driver's device in the driver history, to open its versions.</param>
/// <param name="WasSince">A new part: since when the one before it was on record.</param>
public sealed record PartMark(PartMarkKind Kind, DateTime At, int Slot, string Title, string Now, string? Was, string? DeviceKey, DateTime? WasSince,
    PartSpan Before, PartSpan After)
{
    /// <summary>The games played on both sides (each <see cref="PartReport.MinAppMinutes"/> minutes or more), the most played first.</summary>
    public IReadOnlyList<long> BothSides(int most = 3) =>
    [
        .. Before.ByGame.Where(kv => kv.Value.Minutes >= PartReport.MinAppMinutes && After.ByGame.TryGetValue(kv.Key, out var a) && a.Minutes >= PartReport.MinAppMinutes)
            .OrderByDescending(kv => kv.Value.Minutes + After.ByGame[kv.Key].Minutes).Take(most).Select(kv => kv.Key),
    ];
}

/// <summary>The marks' facts and words (built once here for the chart, its hovers and the tests).</summary>
public static partial class PartMarks
{
    /// <summary>Days looked at on each side of a mark (fewer when another change is nearer), as a big change's before and after.</summary>
    public const int WindowDays = Reports.ChangeEffects.WindowDays;

    /// <summary>Driver marks a period shows at most (a year of monthly drivers fits): the newest.</summary>
    public const int MostDrivers = 24;

    /// <summary>The stretch a time falls in (-1: none).</summary>
    public static int SlotOf(IReadOnlyList<PartSlot> slots, DateTime t)
    {
        for (int i = 0; i < slots.Count; i++) if (t >= slots[i].From && t < slots[i].To) return i;
        return -1;
    }

    /// <summary>
    /// The period's marks: each driver update and each new part in a stretch shown. Two of one kind in one stretch are one
    /// mark (the last, after the version or part before the first); its sides run from before the first to after the last.
    /// </summary>
    internal static List<PartMark> Build(RigsightDb db, Part part, PartEvents events, IReadOnlyList<PartSlot> slots, IReadOnlyCollection<long> games,
        long? firstMinute, DateTime now)
    {
        var marks = new List<PartMark>();
        if (slots.Count == 0 || events.Drivers.Count + events.Swaps.Count == 0) return marks;
        bool gpu = part == Part.Gpu;
        DateTime start = firstMinute is long fm ? TimeUtil.FromUnix(fm) : DateTime.MinValue;
        var swapTimes = events.Swaps.Select(s => s.At).ToList();
        var allTimes = swapTimes.Concat(events.Drivers.Select(d => d.At)).ToList();

        PartSpan Side(DateTime a, DateTime b, bool since)
        {
            if (b <= a) return PartSpan.Empty(a);
            var byGame = db.GetPartGameSums(TimeUtil.ToUnix(a), TimeUtil.ToUnix(b), gpu, games)
                .ToDictionary(g => g.App, g => new PartSide(g.Minutes, g.Temp, g.Load, g.Power, g.Clock));
            var all = byGame.Values.Aggregate((PartSide?)null, (acc, s) => acc is null ? s : acc + s);
            return new PartSpan(a, b, since, all, byGame);
        }

        (PartSpan Before, PartSpan After) Sides(DateTime first, DateTime last, List<DateTime> bounds)
        {
            var window = TimeSpan.FromDays(WindowDays);
            var prev = bounds.Where(x => x < first).DefaultIfEmpty(DateTime.MinValue).Max();
            var from = new[] { first - window, prev, start }.Max();
            var next = bounds.Where(x => x > last).DefaultIfEmpty(DateTime.MaxValue).Min();
            var to = new[] { last + window, next, now }.Min();
            // "Since" while it is still the current one and its days reach now; "in the N days after" once cut.
            bool since = next == DateTime.MaxValue && last + window >= now;
            return (Side(from, first, false), Side(last, to, since));
        }

        foreach (var group in events.Drivers.Where(d => d.At <= now).Select(d => (Mark: d, Slot: SlotOf(slots, d.At))).Where(x => x.Slot >= 0)
                     .GroupBy(x => x.Slot).OrderBy(g => g.Key).TakeLast(MostDrivers))
        {
            var first = group.MinBy(x => x.Mark.At).Mark;
            var last = group.MaxBy(x => x.Mark.At).Mark;
            var (before, after) = Sides(first.At, last.At, allTimes);
            marks.Add(new PartMark(PartMarkKind.Driver, last.At, group.Key, last.Title, last.Version, first.Was, last.DeviceKey, null, before, after));
        }
        foreach (var group in events.Swaps.Where(s => s.At <= now).Select(s => (Swap: s, Slot: SlotOf(slots, s.At))).Where(x => x.Slot >= 0).GroupBy(x => x.Slot))
        {
            var first = group.MinBy(x => x.Swap.At).Swap;
            var last = group.MaxBy(x => x.Swap.At).Swap;
            var (before, after) = Sides(first.At, last.At, swapTimes);
            var wasSince = swapTimes.Where(x => x < first.At).Select(x => (DateTime?)x).DefaultIfEmpty(start == DateTime.MinValue ? null : start).Max();
            marks.Add(new PartMark(PartMarkKind.NewPart, last.At, group.Key, last.NewName, last.NewName, first.OldName, null, wasSince, before, after));
        }
        return marks;
    }

    // ── Names ──────────────────────────────────────────────────────────────

    [GeneratedRegex(@"\((R|TM)\)|\bNVIDIA\b|\bGeForce\b|\bAMD\b|\bIntel\b", RegexOptions.IgnoreCase)]
    private static partial Regex GpuWords();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>A part's name as the chart's marks say it: "RTX 5080" (NVIDIA GeForce RTX 5080), "Core i7-12700K".</summary>
    public static string Short(Part part, string name)
    {
        if (part == Part.Cpu) return Ai.AiTools.ShortCpu(name);
        string n = Spaces().Replace(GpuWords().Replace(name, " "), " ").Trim();
        return n.Length > 0 ? n : name.Trim();
    }

    /// <summary>Two names of one part, as the sensors and Windows' list each write it ("12th Gen Intel(R) Core(TM) i7-12700K" is "Intel Core i7-12700K").</summary>
    public static bool SameName(Part part, string a, string b)
    {
        static string Key(string s) => new([.. s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
        string x = Key(Short(part, a)), y = Key(Short(part, b));
        return x.Length > 0 && y.Length > 0 && (x == y || x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal));
    }

    // ── Words ──────────────────────────────────────────────────────────────

    private static string Day(DateTime t) => t.ToString("ddd d MMM", CultureInfo.CurrentCulture);
    private static string Clock(DateTime t) => TimeText.At(t, "h:mm tt");

    /// <summary>A driver mark's name on the chart: "Driver 621.24", or shorter where that doesn't fit: "621.24".</summary>
    public static (string Long, string Short) DriverLabel(PartMark m) => (Lang.T($"Driver {m.Now}"), m.Now);

    /// <summary>A new part's names on the chart: the one before (faint, before the line) and "RTX 5080 since Mon 23", or shorter "RTX 5080".</summary>
    public static (string? Before, string Long, string Short) PartLabels(Part part, PartMark m, PartStep step)
    {
        string now = Short(part, m.Now);
        string when = step switch
        {
            PartStep.Hour => Clock(m.At),
            PartStep.Week => m.At.ToString("d MMM", CultureInfo.CurrentCulture),
            _ => m.At.ToString("ddd d", CultureInfo.CurrentCulture),
        };
        return (m.Was is { Length: > 0 } was ? Short(part, was) : null, Lang.T($"{now} since {when}"), now);
    }

    /// <summary>The day hover's blue line on a driver's stretch: "◆ Driver 621.24 installed this day, at 4:57 PM".</summary>
    public static string DayLine(PartMark m, PartStep step) => step switch
    {
        PartStep.Hour => Lang.T($"◆ Driver {m.Now} installed at {Clock(m.At)}"),
        PartStep.Week => Lang.T($"◆ Driver {m.Now} installed {Day(m.At)}, at {Clock(m.At)}"),
        _ => Lang.T($"◆ Driver {m.Now} installed this day, at {Clock(m.At)}"),
    };

    /// <summary>The day hover's faint line on an old part's stretch: "On the RTX 3080 Ti, your GPU before. Not in the usual."</summary>
    public static string OldLine(Part part, string? oldName) => (part, oldName) switch
    {
        (Part.Gpu, { Length: > 0 } n) => Lang.T($"On the {Short(part, n)}, your GPU before. Not in the usual."),
        (Part.Gpu, _) => Lang.T("On the GPU you had before. Not in the usual."),
        (_, { Length: > 0 } n) => Lang.T($"On the {Short(part, n)}, your CPU before. Not in the usual."),
        _ => Lang.T("On the CPU you had before. Not in the usual."),
    };

    /// <summary>A driver mark's hover line under its title: "Installed Sat 21 Nov, 4:57 PM, after 621.01".</summary>
    public static string DriverSub(PartMark m) => m.Was is { Length: > 0 } was
        ? Lang.T($"Installed {Day(m.At)}, {Clock(m.At)}, after {was}")
        : Lang.T($"Installed {Day(m.At)}, {Clock(m.At)}");

    /// <summary>The side before a mark: "With a game in front, 6 days before".</summary>
    public static string BeforeLabel(PartSpan s) => s.Days == 1
        ? Lang.T("With a game in front, the day before")
        : Lang.T($"With a game in front, {s.Days} days before");

    /// <summary>The side after a mark: "With a game in front, since", or once cut "With a game in front, in the 14 days after".</summary>
    public static string AfterLabel(PartSpan s) => s.Since
        ? Lang.T("With a game in front, since")
        : s.Days == 1 ? Lang.T("With a game in front, in the day after") : Lang.T($"With a game in front, in the {s.Days} days after");

    /// <summary>A game on both sides of a mark: "69° then 68°" (null: no reading of the metric on a side).</summary>
    public static string? Then(PartMetric metric, PartSide before, PartSide after) =>
        before.Of(metric).Average is double a && after.Of(metric).Average is double b
            ? Lang.T($"{PartReport.Figure(metric, a)} then {PartReport.Figure(metric, b)}")
            : null;

    /// <summary>A new part's hover title: "A new GPU", "A new CPU".</summary>
    public static string PartTitle(Part part) => part == Part.Gpu ? Lang.T("A new GPU") : Lang.T("A new CPU");

    /// <summary>A new part's hover line under its title: "NVIDIA GeForce RTX 5080 since Mon 23 Nov, 11:02 AM".</summary>
    public static string PartSub(PartMark m) => Lang.T($"{m.Now} since {Day(m.At)}, {Clock(m.At)}");

    /// <summary>The part before, the new part's first fact: "NVIDIA GeForce RTX 3080 Ti, from 2 Sep" (null: not known).</summary>
    public static string? BeforeIt(PartMark m) => m.Was is not { Length: > 0 } was ? null
        : m.WasSince is { } since ? Lang.T($"{was}, from {since.ToString("d MMM", CultureInfo.CurrentCulture)}") : was;

    /// <summary>A side's label in the new part's hover: "With a game, RTX 3080 Ti".</summary>
    public static string WithAGame(Part part, string name) => Lang.T($"With a game, {Short(part, name)}");

    /// <summary>The new part's hover foot: "The page's usual and What works it hardest count the RTX 5080 only".</summary>
    public static string PartFoot(Part part, PartMark m) => Lang.T($"The page's usual and What works it hardest count the {Short(part, m.Now)} only");
}
