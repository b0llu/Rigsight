using Rigsight.Core.Data;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Reports;

public enum EffectTone { Same, Better, Worse }

/// <summary>What was different after a change, in a line or two ("In Cyberpunk 2077, the GPU runs 6° hotter since…").</summary>
public sealed record ChangeEffect(List<string> Lines, EffectTone Tone);

/// <summary>
/// How the PC ran after a big change (a graphics driver, a Windows update, the BIOS, new hardware) against before it:
/// the same game's temperatures at the same load with the room taken out (as the drift insight compares months), and
/// the PC's own problems against the hours it was used. The days between two such changes belong to the earlier one,
/// so nothing is put down to the wrong change; with too little on either side, nothing is said.
/// </summary>
public static class ChangeEffects
{
    /// <summary>Days looked at on each side of a change (fewer when another change is nearer).</summary>
    public const int WindowDays = 14;

    // The drift insight's bar: an hour of steady play and of rest on each side, 5° apart and 3° beyond what the room did,
    // at a GPU power within 15% (else it isn't the same load).
    private const int MinSteadyMinutes = 60, MinRestMinutes = 60;
    private const double MinDiff = 5, MinBeyondRoom = 3, PowerShare = 0.15;

    /// <summary>Within this many degrees it runs "as warm as before". In between, nothing is said either way.</summary>
    private const double SameWithin = 2;

    /// <summary>Hours of use on each side before problems are counted against each other.</summary>
    private const double MinUseHours = 5;

    /// <summary>The changes worth a before and after: what every game and app runs on.</summary>
    public static bool IsMajor(SystemChange c) =>
        c.IsGraphicsDriver || c.Kind is ChangeKind.Firmware or ChangeKind.Hardware or ChangeKind.Windows or ChangeKind.WindowsUpdate;

    /// <summary>The effect of each day's big changes, by day; days with nothing to say are left out.</summary>
    public static Dictionary<DateTime, ChangeEffect> Of(IEnumerable<SystemChange> changes, IReadOnlyList<HeatDay> heat, IReadOnlyList<SystemDay> use,
        IReadOnlyList<(DateTime Time, CrashKind Kind)> problems, Func<long, string> nameOf, DateTime now)
    {
        var effects = new Dictionary<DateTime, ChangeEffect>();
        var days = changes.Where(IsMajor).GroupBy(c => c.Time.Date).OrderBy(g => g.Key).Select(g => (Day: g.Key, At: g.Min(c => c.Earliest))).ToList(); // a problem in the minutes before it was noticed may be its doing
        var tomorrow = now.Date.AddDays(1);
        for (int i = 0; i < days.Count; i++)
        {
            var (day, at) = days[i];
            var from = day.AddDays(-WindowDays);
            if (i > 0 && days[i - 1].Day.AddDays(1) > from) from = days[i - 1].Day.AddDays(1);
            var to = day.AddDays(WindowDays + 1);
            if (i + 1 < days.Count && days[i + 1].Day < to) to = days[i + 1].Day;
            bool open = to >= tomorrow; // still running: "since", not "in the days after"
            if (open) to = tomorrow;
            if (Effect(day, at, from, to, open) is { } effect) effects[day] = effect;
        }
        return effects;

        ChangeEffect? Effect(DateTime day, DateTime at, DateTime from, DateTime to, bool open)
        {
            // Whole days on each side; the change's own day is neither.
            int daysBefore = (int)(day - from).TotalDays, daysAfter = (int)(to - day).TotalDays - 1;
            if (daysBefore < 1 || daysAfter < 2) return null;
            string after = open ? "since" : $"in the {daysAfter} days after";
            var lines = new List<string>();
            var tone = EffectTone.Same;
            void Say(string line, EffectTone t)
            {
                lines.Add(line);
                if (t == EffectTone.Worse || tone == EffectTone.Same) tone = t;
            }

            // The change's own day is half before, half after: its heat counts on neither side.
            var (was, wasRest) = Heat(from, day);
            var (isNow, nowRest) = Heat(day.AddDays(1), to);
            var game = isNow.Select(n => (Now: n, Was: was.FirstOrDefault(w => w.AppId == n.AppId)))
                .Where(p => p.Was is not null && p.Now.Minutes >= MinSteadyMinutes && p.Was.Minutes >= MinSteadyMinutes && SamePower(p.Now, p.Was))
                .OrderByDescending(p => Math.Min(p.Now.Minutes, p.Was!.Minutes)).FirstOrDefault();
            string? sameHeat = null;
            if (game.Now is { } n1 && game.Was is { } w1 && nowRest is { Minutes: >= MinRestMinutes } && wasRest is { Minutes: >= MinRestMinutes })
            {
                bool any = false, same = true;
                void Part(string part, double? a, double? b, double? restNow, double? restWas)
                {
                    if (a is not double x || b is not double y || restNow is not double rn || restWas is not double rw) return;
                    double diff = x - y, beyondRoom = diff - (rn - rw);
                    same &= Math.Abs(diff) <= SameWithin;
                    if (Math.Abs(diff) < MinDiff || Math.Abs(beyondRoom) < MinBeyondRoom || Math.Sign(diff) != Math.Sign(beyondRoom)) return;
                    any = true;
                    Say($"In {n1.App}, the {part} {(open ? "runs" : "ran")} {Degrees(x, y)} {(diff > 0 ? "hotter" : "cooler")} {after}: "
                        + $"{Units.TempShort(x)} against {Units.TempShort(y)} before, at the same load and not because of the room.", diff > 0 ? EffectTone.Worse : EffectTone.Better);
                }
                Part("GPU", n1.Gpu, w1.Gpu, nowRest.Gpu, wasRest.Gpu);
                Part("CPU", n1.Cpu, w1.Cpu, nowRest.Cpu, wasRest.Cpu);
                if (!any && same) sameHeat = n1.App;
            }

            // Problems, counted from the moment of the change, against the hours the PC was used on each side.
            var before = problems.Where(p => p.Time >= from && p.Time < at).ToList();
            var since = problems.Where(p => p.Time >= at && p.Time < to).ToList();
            // The change's own day is known only as a whole: its hours go with "after", where its problems from the
            // change on are counted. (Left out, three resets that evening were set against hours that didn't include
            // the evening; counted this way a rate after can only come out lower, never falsely worse.)
            double hoursBefore = Hours(from, day), hoursAfter = Hours(day, to);
            bool sameProblems = false;
            if (hoursBefore >= MinUseHours && hoursAfter >= MinUseHours)
            {
                double rateBefore = before.Count / hoursBefore, rateAfter = since.Count / hoursAfter;
                var t = since.Count >= 2 && rateAfter >= 2 * rateBefore ? EffectTone.Worse
                    : before.Count >= 2 && rateBefore >= 2 * rateAfter ? EffectTone.Better : EffectTone.Same;
                if (since.Count > 0 && before.Count == 0) Say($"{Capital(Words(since))} {after}, none in the {Days(daysBefore)} before.", t);
                else if (since.Count == 0 && before.Count > 0) Say($"No problems {after}; {Words(before)} in the {Days(daysBefore)} before.", t);
                else if (since.Count > 0) Say($"{Capital(Words(since))} {after} over {hoursAfter:0} h of use; {Words(before)} over {hoursBefore:0} h before.", t);
                else sameProblems = true;
            }

            // Nothing different, where there was enough to tell: worth knowing after an update too.
            if (lines.Count == 0)
            {
                string start = open ? "Since then" : $"In the {daysAfter} days after";
                if (sameHeat is not null)
                    lines.Add($"{start}, {sameHeat} {(open ? "runs" : "ran")} as warm as before{(sameProblems ? ", with no problems" : "")}.");
                else if (sameProblems)
                    lines.Add(open ? $"No problems since, as in the {Days(daysBefore)} before." : $"No problems in the {daysAfter} days after, as in the {Days(daysBefore)} before.");
            }
            return lines.Count == 0 ? null : new ChangeEffect(lines, tone);
        }

        // Each app's steady load, and the time at rest (app 0), over the days from one midnight to another.
        (List<SteadyLoad> Steady, LoadTemps? AtRest) Heat(DateTime from, DateTime to)
        {
            long f = TimeUtil.ToUnix(from), t = TimeUtil.ToUnix(to);
            var rows = heat.Where(d => d.Day >= f && d.Day < t).ToList();
            static double? Ratio(double sum, int n) => n > 0 ? sum / n : null;
            var steady = rows.Where(d => d.App != 0).GroupBy(d => d.App).Where(g => g.Sum(x => x.GpuN) > 0)
                .Select(g => new SteadyLoad(g.Key, nameOf(g.Key), g.Sum(x => x.N), g.Count(), g.Sum(x => x.GpuSum) / g.Sum(x => x.GpuN),
                    Ratio(g.Sum(x => x.CpuSum), g.Sum(x => x.CpuN)), Ratio(g.Sum(x => x.PowerSum), g.Sum(x => x.PowerN)))).ToList();
            var rest = rows.Where(d => d.App == 0).ToList();
            int minutes = rest.Sum(x => x.N);
            return (steady, minutes > 0 ? new LoadTemps(Ratio(rest.Sum(x => x.CpuSum), rest.Sum(x => x.CpuN)), Ratio(rest.Sum(x => x.GpuSum), rest.Sum(x => x.GpuN)), minutes) : null);
        }

        double Hours(DateTime from, DateTime to)
        {
            long f = TimeUtil.ToUnix(from), t = TimeUtil.ToUnix(to);
            return use.Where(d => d.Day >= f && d.Day < t).Sum(d => d.ActiveSec) / 3600;
        }
    }

    private static bool SamePower(SteadyLoad a, SteadyLoad b) =>
        a.GpuPower is not double pa || b.GpuPower is not double pb || pb <= 0 || Math.Abs(pa - pb) / pb <= PowerShare;

    /// <summary>"3 graphics driver resets and 1 blue screen".</summary>
    private static string Words(IEnumerable<(DateTime Time, CrashKind Kind)> problems) =>
        string.Join(" and ", problems.GroupBy(p => p.Kind).OrderByDescending(g => g.Count()).Select(g => CrashWords.Count(g.Key, g.Count())));

    private static string Days(int n) => n == 1 ? "day" : $"{n} days";

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    /// <summary>The difference as shown: between the two rounded readings, in the user's unit.</summary>
    private static string Degrees(double a, double b)
    {
        static double Shown(double c) => Math.Round(Units.Fahrenheit ? c * 9 / 5 + 32 : c);
        return $"{Math.Abs(Shown(a) - Shown(b)):0}°";
    }
}
