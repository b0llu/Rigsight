using Rigsight.Core.Data;
using Rigsight.Core.Protocol;

namespace Rigsight.Core.Storage;

/// <summary>A watched reading's dot: none (a reading of wear and use), green as usual, amber not.</summary>
public enum ReadingDot { None, Good, Warm }

/// <summary>
/// One line of Every reading in plain words (approved mock-up "Every reading, made clearer", 2026-11-14): its icon and
/// colour (a palette brush's key), its plain name, what the reading is, the figure now, what's usual under a watched one,
/// what changed since the day it's set against, and the hover's calm sentence with the drive's own name and number.
/// </summary>
public sealed record DriveReading(
    bool Watched, string Name, string What, string Now, string Usual, ReadingDot Dot, string Change, bool ChangeWarm,
    string Tip, string Glyph, string Colour, bool ValueWarm, int? AttributeId, string Maker)
{
    /// <summary>"UltraDMA CRC Error Count, 199 (C7)": the drive's own name and number for it, or nothing when it gave none.</summary>
    public string MakerLine => AttributeId is int id && Maker.Length > 0 ? $"{Maker}, {id} ({id:X2})" : "";
}

/// <summary>One attribute as the drive reports it: "199 · C7", the maker's name, the plain name RigSight says it in (if any), its scores and count.</summary>
public sealed record DriveAttributeLine(string IdText, string Name, string Plain, string Score, string Lowest, string Limit, string Count, ReadingDot Dot, string Tip)
{
    public bool Watched => Dot != ReadingDot.None;
}

/// <summary>The two groups of the plain list, the day they're set against, and how many watched readings aren't as usual.</summary>
public sealed record DriveReadingList(IReadOnlyList<DriveReading> Watch, IReadOnlyList<DriveReading> Wear, DateTime? Since)
{
    public int NotUsual => Watch.Count(r => r.Dot == ReadingDot.Warm);

    /// <summary>The summary beside WHAT TO WATCH: "All as usual", "1 isn't as usual", "2 aren't as usual".</summary>
    public string Summary => NotUsual switch
    {
        0 => Lang.T("All as usual"),
        1 => Lang.T("1 isn't as usual"),
        var n => Lang.T($"{n} aren't as usual"),
    };

    /// <summary>"SINCE 14 OCT" over the change column; empty while there's no earlier day at all.</summary>
    public string SinceTitle => Since is DateTime d ? Lang.T($"SINCE {d:d MMM}").ToUpperInvariant() : "";
}

/// <summary>
/// The counts a drive gives that RigSight watches or keeps a day at a time, read once here for the page, the daily row
/// (RigsightDb.UpsertDriveHealth) and the tests. SATA attributes by their numbers; an NVMe drive's health log comes as
/// attributes 1 to 15 (1 Critical Warning, 3 Available Spare, 4 its threshold, 13 Unsafe Shutdowns, 14 Media Errors).
/// </summary>
public static class DriveCounts
{
    public static ulong? Raw(DriveHealthInfo h, int id) => h.Attributes?.FirstOrDefault(a => a.Id == id) is { } a ? a.Raw : null;

    private static long? Long(ulong? v) => v is ulong u ? (long)Math.Min(u, long.MaxValue) : null;

    /// <summary>199, UltraDMA CRC errors (SATA).</summary>
    public static long? CableErrors(DriveHealthInfo h) => h.IsNvme ? null : Long(Raw(h, 199));

    /// <summary>187, Reported Uncorrectable Errors (SATA).</summary>
    public static long? ReportedUncorrectable(DriveHealthInfo h) => h.IsNvme ? null : Long(Raw(h, 187));

    /// <summary>
    /// 188, Command Timeout (SATA). Some makers pack three 16-bit counters into it (Seagate), which would read as billions,
    /// so past 16 bits only the lowest counter is taken.
    /// </summary>
    public static long? Timeouts(DriveHealthInfo h) => h.IsNvme ? null : Raw(h, 188) is ulong v ? (long)(v > 0xFFFF ? v & 0xFFFF : v) : null;

    /// <summary>NVMe 14, Media and Data Integrity Errors.</summary>
    public static long? MediaErrors(DriveHealthInfo h) => h.IsNvme ? Long(Raw(h, 14)) : null;

    /// <summary>NVMe 3, Available Spare (percent).</summary>
    public static long? Spare(DriveHealthInfo h) => h.IsNvme ? Long(Raw(h, 3)) : null;

    /// <summary>NVMe 4, the Available Spare it warns under (10 when it doesn't say).</summary>
    public static long SpareLimit(DriveHealthInfo h) => Long(Raw(h, 4)) is long l and > 0 ? l : 10;

    /// <summary>NVMe 1, Critical Warning (its bits).</summary>
    public static long? Warning(DriveHealthInfo h) => h.IsNvme ? Long(Raw(h, 1)) : null;

    /// <summary>The attribute counting sudden shut-offs: NVMe 13 Unsafe Shutdowns; SATA 192 Power-off Retract Count, else 174.</summary>
    public static int? ShutOffsId(DriveHealthInfo h) => h.IsNvme ? Raw(h, 13) is null ? null : 13 : Raw(h, 192) is not null ? 192 : Raw(h, 174) is not null ? 174 : null;

    public static long? ShutOffs(DriveHealthInfo h) => ShutOffsId(h) is int id ? Long(Raw(h, id)) : null;

    /// <summary>
    /// What an NVMe drive's warning flag warns of, from its bits: "low spare blocks and heat". Empty when none of the
    /// five known bits is set.
    /// </summary>
    public static string WarningWords(long bits)
    {
        var parts = new List<string>();
        if ((bits & 1) != 0) parts.Add(Lang.T("low spare blocks"));
        if ((bits & 2) != 0) parts.Add(Lang.T("heat"));
        if ((bits & 4) != 0) parts.Add(Lang.T("errors lowering its reliability"));
        if ((bits & 8) != 0) parts.Add(Lang.T("a switch to read-only"));
        if ((bits & 16) != 0) parts.Add(Lang.T("a failed power-loss backup"));
        return parts.Count switch
        {
            0 => "",
            1 => parts[0],
            _ => Lang.T($"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}"),
        };
    }
}

/// <summary>A drive's readings in plain words and as the drive reports them (see <see cref="DriveReading"/>).</summary>
public static class DriveReadings
{
    // The SATA attributes the library's life, written, read and temperature most likely come from (for the hover's
    // name and number only, so a guess is harmless): the first of these the drive gives.
    private static readonly int[] LifeIds = [231, 169, 202, 177, 233];
    private static readonly int[] WrittenIds = [241, 246];
    private static readonly int[] ReadIds = [242];
    private static readonly int[] TempIds = [194, 190];

    /// <summary>
    /// The day the change column is set against: the latest kept day a month or more ago, or the oldest kept day before
    /// today while the drive has less than a month (the mock-up's note). Null on its first day.
    /// </summary>
    public static DriveHealthDay? Reference(IReadOnlyList<DriveHealthDay> days, DateTime today)
    {
        long monthAgo = TimeUtil.ToUnix(today.AddMonths(-1)), start = TimeUtil.ToUnix(today.Date);
        return days.Where(d => d.Day <= monthAgo).MaxBy(d => d.Day) ?? days.Where(d => d.Day < start).MinBy(d => d.Day);
    }

    /// <param name="days">The drive's own daily rows, at least back to a month ago (oldest first or not).</param>
    public static DriveReadingList Plain(DriveHealthInfo h, IReadOnlyList<DriveHealthDay> days, DateTime today)
    {
        var then = Reference(days, today);
        DateTime? since = then is null ? null : TimeUtil.FromUnix(then.Day);
        var watch = new List<DriveReading>();
        var wear = new List<DriveReading>();
        DriveAttribute? Attr(int id) => h.Attributes?.FirstOrDefault(a => a.Id == id);
        DriveAttribute? First(int[] ids) => ids.Select(Attr).FirstOrDefault(a => a is not null);
        string tooSoon = Lang.T("Too soon to say");

        // A count where a rise is the wrong way: "Up 16 since 14 Oct" in amber, "Same as on 14 Oct", "Down 5 since 14 Oct".
        (string Text, bool Warm) Count(long now, long? was, bool upIsBad = true)
        {
            if (since is not DateTime d || was is not long w) return (tooSoon, false);
            long diff = now - w;
            return diff > 0 ? (Lang.T($"Up {diff:N0} since {d:d MMM}"), upIsBad)
                : diff < 0 ? (Lang.T($"Down {-diff:N0} since {d:d MMM}"), false)
                : (Lang.T($"Same as on {d:d MMM}"), false);
        }
        (string Text, bool Warm) Percent(double now, double? was, bool downIsBad)
        {
            if (since is not DateTime d || was is not double w) return (tooSoon, false);
            long diff = (long)Math.Round(now) - (long)Math.Round(w);
            return diff > 0 ? (Lang.T($"Up {diff}% since {d:d MMM}"), false)
                : diff < 0 ? (Lang.T($"Down {-diff}% since {d:d MMM}"), downIsBad)
                : (Lang.T($"Same as on {d:d MMM}"), false);
        }
        string Bytes(double nowGb, double? wasGb)
        {
            if (since is not DateTime d || wasGb is not double w) return tooSoon;
            double diff = nowGb - w;
            return diff >= 0.001 ? Lang.T($"Up {Units.DiskBytes(diff * 1e9)} since {d:d MMM}")
                : diff <= -0.001 ? Lang.T($"Down {Units.DiskBytes(-diff * 1e9)} since {d:d MMM}")
                : Lang.T($"Same as on {d:d MMM}");
        }

        string usual0 = Lang.T("Usually 0");
        void Watched(long? now, long? was, string name, string what, string tip, string glyph, string colour, int id)
        {
            if (now is not long v) return;
            var (change, warm) = Count(v, was);
            watch.Add(new(true, name, what, $"{v:N0}", usual0, v == 0 ? ReadingDot.Good : ReadingDot.Warm, change, warm, tip, glyph, colour, v > 0, id, Attr(id)?.Name ?? ""));
        }

        if (h.IsNvme)
        {
            if (DriveCounts.Warning(h) is long bits)
            {
                string words = DriveCounts.WarningWords(bits);
                string what = bits != 0 && words.Length > 0 ? Lang.T($"It warns of {words}") : Lang.T("Its own flag for low spare blocks, heat or errors");
                (string change, bool warm) = since is not DateTime d || then?.Warning is not long was ? (tooSoon, false)
                    : was == bits ? (Lang.T($"Same as on {d:d MMM}"), false)
                    : was == 0 ? (Lang.T($"New since {d:d MMM}"), true)
                    : bits == 0 ? (Lang.T($"Cleared since {d:d MMM}"), false)
                    : (Lang.T($"Changed since {d:d MMM}"), true);
                watch.Add(new(true, Lang.T("Warnings from the drive"), what, bits == 0 ? Lang.T("None") : Lang.T("Yes"), Lang.T("Usually none"),
                    bits == 0 ? ReadingDot.Good : ReadingDot.Warm, change, warm,
                    Lang.T("The drive sets this itself when its spare blocks run low, it gets too hot, or errors build up."),
                    "", "PinkBrush", bits != 0, 1, Attr(1)?.Name ?? ""));
            }
            if (DriveCounts.Spare(h) is long spare)
            {
                long limit = DriveCounts.SpareLimit(h);
                var (change, warm) = Percent(spare, then?.Spare, downIsBad: true);
                bool low = spare < limit;
                watch.Add(new(true, Lang.T("Spare blocks left"), Lang.T("Room it keeps inside to replace worn blocks"), $"{spare}%", Lang.T($"Warns under {limit}%"),
                    low ? ReadingDot.Warm : ReadingDot.Good, change, warm,
                    Lang.T($"This is spare room inside the drive, not free space for your files. The drive warns under {limit}%."),
                    "", "PurpleBrush", low, 3, Attr(3)?.Name ?? ""));
            }
            Watched(DriveCounts.MediaErrors(h), then?.MediaErrors, Lang.T("Data it couldn't read back"), Lang.T("Reads where it couldn't recover the data"),
                Lang.T("Any count means some data didn't read back as it was written."), "", "PurpleBrush", 14);
        }
        else
        {
            string fromBefore = Lang.T("The count never goes down, so a figure that stays the same is from before.");
            Watched(h.ReallocatedSectors, then?.Reallocated, Lang.T("Bad sectors swapped out"), Lang.T("Sectors it stopped using and replaced with spares"),
                Lang.T("A small count can stay the same for years. What matters is whether it keeps rising."), "", "PurpleBrush", 5);
            Watched(h.PendingSectors, then?.Pending, Lang.T("Sectors waiting to be swapped"), Lang.T("Sectors it had trouble reading, checked again when next written"),
                Lang.T("This count can fall back to 0 once the drive rewrites those sectors."), "", "PurpleBrush", 197);
            Watched(h.UncorrectableSectors, then?.Uncorrectable, Lang.T("Sectors it couldn't read"), Lang.T("Sectors with read errors it couldn't correct"),
                Lang.T("Data on these sectors couldn't be read back as it was written."), "", "PurpleBrush", 198);
            // 187 and 188, which Backblaze counts with the sector counts (the mock-up's suggestion, taken).
            Watched(DriveCounts.ReportedUncorrectable(h), then?.ReportedUncorrectable, Lang.T("Data it couldn't read back"), Lang.T("Reads where it couldn't recover the data"),
                Lang.T("Any count means some data didn't read back as it was written."), "", "PurpleBrush", 187);
            Watched(DriveCounts.CableErrors(h), then?.CableErrors, Lang.T("Cable or connection errors"), Lang.T("Transfers that failed a check between the drive and the PC"),
                fromBefore, "", "SkyBrush", 199);
            Watched(DriveCounts.Timeouts(h), then?.Timeouts, Lang.T("Commands that timed out"), Lang.T("Requests from the PC it didn't answer in time"),
                fromBefore, "", "SkyBrush", 188);
        }

        bool nvme = h.IsNvme;
        string madeCount = Lang.T("The drive counts this itself, so it includes any PC it was in before this one.");
        void Use(string now, string change, string name, string what, string tip, string glyph, string colour, DriveAttribute? attr, string? maker = null, bool warm = false) =>
            wear.Add(new(false, name, what, now, "", ReadingDot.None, change, false, tip, glyph, colour, warm, attr?.Id, maker ?? attr?.Name ?? ""));

        if (h.Life is double life)
            Use($"{life:0}%", Percent(life, then?.Life, downIsBad: false).Text, Lang.T("Life left"), Lang.T("The maker's estimate of the wear its flash can still take"),
                nvme ? Lang.T("The drive works this out itself from how worn its flash is. RigSight shows it as 100 minus Percentage Used.")
                     : Lang.T("The drive works this out itself from how worn its flash is."),
                "", "PinkBrush", nvme ? Attr(5) : First(LifeIds));
        if (h.WrittenGb is double w)
            Use(Units.DiskBytes(w * 1e9), Bytes(w, then?.WrittenGb), Lang.T("Data written"), Lang.T("Everything written to it since it was made"), madeCount,
                "", "PinkBrush", nvme ? Attr(7) : First(WrittenIds));
        if (h.ReadGb is double r)
            Use(Units.DiskBytes(r * 1e9), Bytes(r, then?.ReadGb), Lang.T("Data read"), Lang.T("Everything read from it since it was made"), madeCount,
                "", "PurpleBrush", nvme ? Attr(6) : First(ReadIds));
        if (h.PowerOnHours is long hrs)
            Use($"{hrs:N0}", Count(hrs, then?.Hours, upIsBad: false).Text, Lang.T("Hours on"), Lang.T("Every hour it has had power since it was made"), madeCount,
                "", "SkyBrush", nvme ? Attr(12) : Attr(9));
        if (h.PowerOnCount is long ons)
            Use($"{ons:N0}", Count(ons, then?.PowerOns, upIsBad: false).Text, Lang.T("Times switched on"), Lang.T("Each time it was powered up"),
                Lang.T("The drive counts this itself, from the day it was made."), "", "SkyBrush", nvme ? Attr(11) : Attr(12));
        if (DriveCounts.ShutOffs(h) is long cuts)
            Use($"{cuts:N0}", Count(cuts, then?.ShutOffs, upIsBad: false).Text, Lang.T("Sudden shut-offs"), Lang.T("Times it lost power without a normal shutdown"),
                Lang.T("The drive counts each time the power went before it was told to stop."), "", "SkyBrush", Attr(DriveCounts.ShutOffsId(h)!.Value));
        if (h.Temperature is double temp)
        {
            bool hot = h.TemperatureWarning is double warn && warn > 0 && temp >= warn;
            List<double> average = then is null ? [] : days.Where(d => d.Day >= then.Day && d.TempAvg is not null).Select(d => d.TempAvg!.Value).ToList();
            string change = since is DateTime d && average.Count > 0 ? Lang.T($"Average {average.Average():0}° since {d:d MMM}") : tooSoon;
            string tip = h.TemperatureWarning is double limit && limit > 0 ? Lang.T($"The drive warns at {limit:0}°.") : Lang.T("The drive doesn't give a temperature it warns at.");
            var attr = nvme ? Attr(2) : First(TempIds);
            Use($"{temp:0}°", change, Lang.T("Temperature"), Lang.T("From its own sensor, read just now"), tip, "", hot ? "WarmBrush" : "SkyBrush", attr, warm: hot);
        }
        return new DriveReadingList(watch, wear, since);
    }

    /// <summary>
    /// Every attribute as the drive names it: "199 · C7", its name, the plain name of a reading shown in plain words, a
    /// dot on a watched one, and its scores (none on an NVMe drive, which gives counts only).
    /// </summary>
    public static List<DriveAttributeLine> Own(DriveHealthInfo h, DriveReadingList plain)
    {
        var byId = plain.Watch.Concat(plain.Wear).Where(r => r.AttributeId is not null).GroupBy(r => r.AttributeId!.Value).ToDictionary(g => g.Key, g => g.First());
        var lines = new List<DriveAttributeLine>();
        foreach (var a in h.Attributes ?? [])
        {
            var shown = byId.GetValueOrDefault(a.Id);
            string plainName = shown?.Name ?? "";
            var dot = shown is { Watched: true } ? shown.Dot : ReadingDot.None;
            string tip;
            if (h.IsNvme) tip = shown is not null ? Lang.T($"Shown in plain words as {plainName}.") : Lang.T("Not in the plain-words list.");
            else
            {
                tip = a.Threshold > 0
                    ? Lang.T($"Score {a.Current} now and {a.Worst} at its lowest. The drive flags it once the score falls to {a.Threshold}.")
                    : Lang.T($"Score {a.Current} now and {a.Worst} at its lowest. The drive sets no limit for it, so it never flags it.");
                if (shown is not null) tip += "\n" + Lang.T($"Shown in plain words as {plainName}.");
            }
            lines.Add(new($"{a.Id} · {a.Id:X2}", a.Name, plainName, h.IsNvme ? "" : $"{a.Current}", h.IsNvme ? "" : $"{a.Worst}", h.IsNvme ? "" : $"{a.Threshold}",
                $"{a.Raw:N0}", dot, tip));
        }
        return lines;
    }

    /// <summary>The line under the drive's own list on how to read it.</summary>
    public static string OwnFoot(bool nvme) => nvme
        ? Lang.T("NVMe drives report counts only, with no scores. A dot marks a reading RigSight watches, and the grey name is how it's said in plain words.")
        : Lang.T("Scores are the maker's own scale, where lower is worse. The drive flags a reading once its score falls to its limit. A dot marks a reading RigSight watches, and the grey name is how it's said in plain words.");
}
