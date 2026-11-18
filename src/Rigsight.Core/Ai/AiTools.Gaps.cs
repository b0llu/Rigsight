using System.Globalization;
using System.Text;
using Rigsight.Core.Data;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Ai;

/// <summary>
/// Stretches inside an asked period that RigSight recorded nothing in, said by every look-up over a period. A PC's records
/// had nothing from 15 Oct to 7 Nov, which held every shut-off asked about, and only get_moment's "last minute recorded
/// before" hinted at it, so the answer said "nothing was recorded at 4:48 PM" and never that three weeks were missing
/// (audit Q062, 2026-11-17).
/// </summary>
public sealed partial class AiTools
{
    /// <summary>The shortest stretch without records that is said: a night off is shorter, a day or more is worth a line.</summary>
    internal const long GapSeconds = 24 * 3600;

    /// <summary>Stretches said one by one; the rest are counted.</summary>
    private const int MostGaps = 6;

    /// <summary>
    /// When our records on this PC begin (Unix seconds): the first minute, when it's in the first hour of the app
    /// totals (those are by the hour), as <see cref="Began"/> and <see cref="RecordsFrom"/> read it. Null: nothing yet.
    /// </summary>
    internal static long? RecordingStart(RigsightDb db) =>
        db.FirstDataTime() is not long data ? null : db.FirstMinuteTime() is long m && m - data < 3600 ? m : data;

    /// <summary>
    /// The "NOT RECORDED" line for a period from one local day to another (both included): each stretch of a day or more
    /// in it with no records, said whole, with what can be known of why. "" when the records cover it.
    /// </summary>
    /// <param name="began">Whether to say that recording began inside or after the period (get_report and get_crashes say it themselves).</param>
    /// <param name="crashes">For get_crashes: the crashes in a stretch come from Windows' logs, without our readings around them.</param>
    private string NotRecorded(RigsightDb db, DateTime from, DateTime to, bool began = true, bool crashes = false)
    {
        if (to < from) (from, to) = (to, from);
        long f = TimeUtil.ToUnix(from.Date), t = TimeUtil.ToUnix(to.Date.AddDays(1)), now = TimeUtil.ToUnix(DateTime.Now);
        if (f >= now || RecordingStart(db) is not long start) return "";
        var sb = new StringBuilder();
        if (began && start > f)
        {
            var at = TimeUtil.FromUnix(start);
            sb.AppendLine(start >= t
                ? $"NOT RECORDED: Our records on this PC begin {GapTime(at)}, after this period, so nothing in it was recorded. Say that, not that nothing happened."
                : $"NOT RECORDED: Our records on this PC begin {GapTime(at)}; nothing before then in this period was recorded.");
        }
        long end = Math.Min(t, now);
        var gaps = db.GetMinuteGaps(f, t, GapSeconds, now)
            .Where(g => g.Start is not null)
            // Inside the period for an hour or more (or all of it): a stretch that only grazes its edge is another period's.
            .Where(g => Math.Min(g.End ?? now, end) - Math.Max(g.Start!.Value, f) >= Math.Min(3600, end - f))
            .Select(g => (Start: g.Start!.Value, End: g.End))
            .ToList();
        if (gaps.Count == 0) return sb.ToString();
        var shown = gaps.OrderByDescending(g => (g.End ?? now) - g.Start).Take(MostGaps).OrderBy(g => g.Start).ToList();
        foreach (var g in shown) sb.AppendLine("NOT RECORDED: " + GapWords(db, g.Start, g.End, now, f, end, crashes));
        if (gaps.Count > shown.Count)
            sb.AppendLine($"NOT RECORDED: {gaps.Count - shown.Count} more stretches of a day or more with no records in this period (the {shown.Count} longest are above); get_on_times has the days recorded.");
        return sb.ToString();
    }

    /// <summary>
    /// One stretch without records, in words: when, how long, and why where the records can say. RigSight records every
    /// minute the PC is on while its agent runs, so a stretch is the PC off or asleep, or RigSight not recording. Only
    /// Windows' own logs can tell those apart: an app crash or an update installed inside the stretch means the PC was on.
    /// </summary>
    /// <param name="end">Null: nothing recorded since, up to <paramref name="now"/>.</param>
    internal string GapWords(RigsightDb db, long start, long? end, long now, long? periodFrom = null, long? periodTo = null, bool crashes = false)
    {
        long stop = end ?? now;
        string span = Length(stop - start);
        var sb = new StringBuilder();
        sb.Append(end is long e
            ? $"We have no records from {GapTime(TimeUtil.FromUnix(start))} to {GapTime(TimeUtil.FromUnix(e))} ({span})"
            : $"We have no records since {GapTime(TimeUtil.FromUnix(start))} ({span} up to now)");
        if (periodFrom is long pf && periodTo is long pt) sb.Append(start <= pf && stop >= pt ? ", which is all of this period" : ", which covers part of this period");
        sb.Append(". ");

        // Windows' own logs, read whether or not RigSight was recording: something they logged inside the stretch (well
        // clear of its edges, where RigSight's own checks date what they find) means the PC was on then.
        long inFrom = start + 600, inTo = stop - 600;
        var on = new List<string>();
        if (inTo > inFrom)
        {
            if (On(Switch.Crashes))
            {
                var apps = db.GetCrashes(inFrom, inTo).Where(c => c.Kind is CrashKind.AppCrash or CrashKind.AppHang or CrashKind.GpuDriverReset).ToList();
                if (apps.Count > 0) on.Add($"{apps.Count} app {(apps.Count == 1 ? "crash or freeze" : "crashes or freezes")}, the first on {TimeUtil.FromUnix(apps.Min(c => c.Ts)):d MMM}");
            }
            if (On(Switch.Changes))
            {
                var updates = db.GetChanges(inFrom, inTo).Where(c => c.Kind is ChangeKind.Driver or ChangeKind.WindowsUpdate && c.NoticedFrom is null).ToList();
                if (updates.Count > 0) on.Add($"{updates.Count} {(updates.Count == 1 ? "driver or Windows update" : "drivers or Windows updates")} installed, the first on {updates.Min(c => c.Time):d MMM}");
            }
        }
        if (on.Count > 0)
            sb.Append($"Windows' own logs show the PC was on during it ({string.Join("; ", on)}), so we weren't recording for at least part of it. ");
        else if (end is null && Recording?.Invoke() == false)
            sb.Append("RigSight's background agent, which records, isn't running now, so nothing is being recorded. ");
        else
            sb.Append("The PC was off or asleep, or we weren't recording; its records can't tell which, so don't say which. ");
        sb.Append(crashes
            ? "Crashes in that time come from Windows' own logs, without our readings around them (temperatures, load, the app in front), so those can't be checked for them."
            : "Temperatures, loads and apps in that time can't be checked: say so when the question touches it, and never read the missing time as nothing happening.");
        return sb.ToString();
    }

    /// <summary>"22 days 13 hours", "1 day 2 hours", "9h 30m": a stretch of days said in days, not "541h 21m".</summary>
    internal static string Length(long seconds)
    {
        if (seconds < 86400) return Units.Duration(seconds);
        long days = seconds / 86400, hours = seconds % 86400 / 3600;
        return $"{days} {(days == 1 ? "day" : "days")}" + (hours > 0 ? $" {hours} {(hours == 1 ? "hour" : "hours")}" : "");
    }

    private static string GapTime(DateTime at) => at.ToString("ddd d MMM yyyy h:mm tt", CultureInfo.InvariantCulture);
}
