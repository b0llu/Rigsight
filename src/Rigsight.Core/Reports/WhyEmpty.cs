using Rigsight.Core.Data;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Reports;

/// <summary>
/// Why a period has nothing recorded, said plainly where it's known: the history hadn't started yet, the PC was off or
/// asleep (Windows' own log of starts and sleeps), or the PC was on and RigSight wasn't running. A page with nothing to
/// show says this instead of a bare "not recorded", which read as RigSight having failed when the PC was simply off
/// (the user, 2026-11-04: three days of a PC not started showed "Internet use is recorded while the agent runs as admin").
/// </summary>
public static class WhyEmpty
{
    public enum Kind
    {
        /// <summary>The period is before this PC's history begins.</summary>
        BeforeHistory,
        Off,
        Asleep,
        /// <summary>The PC was on (Windows logged nothing about power in the stretch) and nothing was recording.</summary>
        NotRunning,
        /// <summary>Windows' log doesn't reach back that far (or couldn't be read): off, or RigSight not running.</summary>
        Unknown,
        /// <summary>RigSight was recording the PC: what the page shows wasn't (it's the page's to say why).</summary>
        PcWasOn,
    }

    /// <summary>How far back Windows' log is read for a reason: older periods say "off, or not running".</summary>
    public const int PowerLogDays = 62;

    /// <summary>
    /// Why nothing was recorded from <paramref name="from"/> to <paramref name="to"/>. The empty stretch is taken whole,
    /// from the last minute recorded before it to the first after (a PC off for three days started again on the fourth:
    /// that start is what says it was off), and set against what Windows logged in it.
    /// </summary>
    public static (Kind Kind, DateTime? Began) Of(RigsightDb db, DateTime from, DateTime to, DateTime now,
        Func<DateTime, IReadOnlyList<PowerEvent>?>? readPower = null)
    {
        if (db.FirstDataTime() is not long first) return (Kind.BeforeHistory, null);
        var began = TimeUtil.FromUnix(first);
        if (to <= began) return (Kind.BeforeHistory, began);
        var end = to < now ? to : now;
        long f = TimeUtil.ToUnix(from), e = TimeUtil.ToUnix(end);
        if (db.HasMinutes(f, e)) return (Kind.PcWasOn, began);
        var gapFrom = db.LastMinuteBefore(f) is long last ? TimeUtil.FromUnix(last + 60) : from;
        var gapTo = db.FirstMinuteFrom(e) is long next ? TimeUtil.FromUnix(next) : now;
        if ((now - gapFrom).TotalDays > PowerLogDays) return (Kind.Unknown, began);
        var power = (readPower ?? CrashLogReader.ReadPower)(gapFrom.AddDays(-1));
        return (PowerLog.Reason(gapFrom, gapTo, power, TimeSpan.FromMinutes(3)) switch
        {
            GapReason.Off or GapReason.ShutOff or GapReason.Crashed => Kind.Off,
            GapReason.Asleep => Kind.Asleep,
            GapReason.NotRunning => Kind.NotRunning,
            _ => Kind.Unknown,
        }, began);
    }

    /// <summary>
    /// The sentence for an empty page, the period said as <see cref="PeriodWords"/> and the period picker say it ("on
    /// Sun 1 Nov", "last week"). Null for <see cref="Kind.PcWasOn"/>: the page says what of its own wasn't recorded.
    /// </summary>
    public static string? Sentence(Kind kind, DateTime? began, string when) => kind switch
    {
        Kind.BeforeHistory when began is { } b => Lang.T($"Nothing was recorded {when}. This PC's history starts on {b:d MMM yyyy}."),
        Kind.BeforeHistory => Lang.T($"Nothing was recorded {when}. This PC's history hasn't started yet."),
        Kind.Off => Lang.T($"Your PC was off {when}."),
        Kind.Asleep => Lang.T($"Your PC was asleep {when}."),
        Kind.NotRunning => Lang.T($"Your PC was on {when}, but RigSight wasn't running."),
        Kind.Unknown => Lang.T($"Nothing was recorded {when}. Your PC was off, or RigSight wasn't running."),
        _ => null,
    };
}
