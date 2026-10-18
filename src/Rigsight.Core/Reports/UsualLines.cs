using Rigsight.Core.Stability;

namespace Rigsight.Core.Reports;

/// <summary>
/// The statements about the PC against its usual (see <see cref="PcUsual"/>), each written here once: its full
/// sentence (Reports, the recap, Export) and its one statement (Home), under one key, so where it opens comes from
/// <see cref="InsightLinks"/> alone. The insight list and Home's PC health both take them from here and say them
/// the same way: neither builds a sentence of its own.
/// </summary>
public static class UsualLines
{
    // Icons (Segoe Fluent), as the insight list has them.
    private const string IconCompare = "", IconTemp = "", IconWarn = "", IconGood = "", IconFan = "", IconPower = "";

    /// <summary>
    /// The span the PC's problems are counted over, after a statement: a day is read with the 6 days before it ("in the
    /// last 7 days", "in the 7 days to 16 Oct"); a longer period as itself ("this week", "in September").
    /// </summary>
    public static string ProblemsSpan(Report r, DateTime today) =>
        ReportBuilder.IsDayLike(r.Range, r.From, r.To) ? ChartRange.Words(ChartRange.Week, r.To.AddTicks(-1).Date, today) : PeriodWords.Said(r.Range, r.From, today);

    /// <summary>
    /// A game hotter or cooler than usual: the part that moved (the GPU unless only the CPU did), the other said with it
    /// when it moved too. Its one statement follows the last day the game was played, with that day's figure, and names
    /// the day when it wasn't the period's last ("ran 11° hotter than usual on Wed 14 Oct", then "runs 5° hotter").
    /// </summary>
    public static Insight? Heat(PcUsual usual, bool inProgress, DateTime lastDay)
    {
        var moved = usual.Parts.Where(p => p.Tone != EffectTone.Same).ToList();
        if (moved.FirstOrDefault() is not { } part) return null;
        var also = moved.Skip(1).FirstOrDefault(p => p.Hotter == part.Hotter);
        bool hotter = part.Hotter;
        string how = hotter ? "hotter" : "cooler", verb = inProgress ? "runs" : "ran", sits = inProgress ? "sits" : "sat";
        // What rules the room out is said as the figure that does: the part with nothing running.
        string rest = part.RestNow is double rn && part.RestUsual is double ru
            ? Math.Abs(rn - ru) < 1.5 ? $" With nothing running it {sits} at its usual {Units.TempShort(rn)}"
                : $" With nothing running it's only {Units.Degrees(Math.Abs(rn - ru))} {(rn > ru ? "warmer" : "cooler")} than usual"
            : "";
        string text = $"In {part.App}, your {part.Part} {verb} {part.Diff} {how} than usual: {Units.TempShort(part.Now)} against {PcUsual.Span(part.Low, part.High)}, at the same power"
            + (also is null ? "." : $"; your {also.Part} {also.Diff} {how} too ({Units.TempShort(also.Now)} against {PcUsual.Span(also.Low, also.High)}).")
            + (hotter ? $"{rest}{(rest.Length > 0 ? ", so dust" : " Dust")} or old thermal paste are the likely causes." : rest.Length > 0 ? $"{rest}." : "");
        string cpu = part.Part == "CPU" ? " on the CPU" : "";
        string line = part.LastDay is { } last && last.Date != lastDay.Date
            ? $"{part.App} ran {part.LastDiff} {how} than usual{cpu} on {last:ddd d MMM}"
            : $"{part.App} {verb} {part.LastDiff} {how} than usual{cpu}";
        return new Insight(IconCompare, text, hotter ? InsightTone.Warn : InsightTone.Good, "usual-heat", hotter ? 74 : 41,
            $"{part.NowMinutes} minutes of steady play now; usual from {part.UsualMinutes} minutes over {part.UsualDays} days") { Short = line, Game = part.App };
    }

    /// <summary>The PC's own problems when they were more or fewer than usual; null when as usual.</summary>
    public static Insight? Problems(UsualProblems pr, string span) => pr.Tone switch
    {
        EffectTone.Worse => new Insight(IconWarn, $"{Wording.Capital(pr.Words)} {span}; {pr.UsualWords} on this PC. Each one is on the Crashes page.", InsightTone.Warn, "usual-problems", 92,
            Detail(pr)) { Short = $"{Wording.Capital(pr.Words)} {span}" },
        EffectTone.Better => new Insight(IconGood, $"Fewer PC problems than usual: {(pr.Now.Count == 0 ? "none" : pr.Now.Count.ToString())} {span}, {pr.UsualWords}.", InsightTone.Good, "usual-problems", 45,
            Detail(pr)) { Short = "Fewer PC problems than usual" },
        _ => null,
    };

    /// <summary>The shut-offs that look like the power, only when they jumped far past their own usual; null otherwise.</summary>
    public static Insight? Power(UsualProblems pr, string span) => pr.PowerTone != EffectTone.Worse ? null
        : new Insight(IconWarn, $"More sudden shut-offs than usual: {pr.PowerNow} {span}, {pr.PowerUsualWords}. Each looked like the power going off; if the mains was steady, check the power supply.",
            InsightTone.Warn, "usual-power", 88, $"{pr.PowerNow} in {pr.NowHours:0} h of use; {pr.PowerUsual} in {pr.UsualHours:0} h in the weeks before")
            { Short = $"More sudden shut-offs than usual {span}" };

    private static string Detail(UsualProblems pr) => $"{pr.Now.Count} in {pr.NowHours:0} h of use; {pr.UsualCount} in {pr.UsualHours:0} h in the weeks before";

    /// <summary>
    /// Home's PC health for the PC's own parts, a statement each, against the usual of <paramref name="today"/> (the
    /// last 7 days against the weeks before): the temperatures, the fans, the PC's problems and the likely power cuts.
    /// What moved is the insight itself (the same words as the list on Reports); what didn't is said plainly. Without
    /// enough history for a usual, the last 7 days' problems are counted as they are (<paramref name="problems"/>: the
    /// kind of each of the PC's own problems; <paramref name="cuts"/>: shut-offs that look like the power, of them
    /// <paramref name="cutsLikely"/> likely a power cut, null: all of them).
    /// Where each opens is <see cref="InsightLinks"/>'s to say.
    /// </summary>
    public static List<Insight> Health(Report? today, IReadOnlyCollection<CrashKind> problems, int cuts, int? cutsLikely = null)
    {
        var usual = today?.Usual;
        var insights = today?.Insights ?? [];
        string week = today is null ? "in the last 7 days" : ProblemsSpan(today, today.From.Date);
        var list = new List<Insight>();

        if (insights.FirstOrDefault(i => i.Key == "usual-heat") is { } heat) list.Add(heat);
        else if (usual?.HasHeat == true) list.Add(new Insight(IconTemp, "Temperatures as usual", InsightTone.Neutral, "temps-usual"));

        if (insights.FirstOrDefault(i => i.Key is "fan-stopped" or "fan-slower" or "fan-older") is { } fan) list.Add(fan);
        else if (usual?.Fans.Count > 0) list.Add(new Insight(IconFan, "Fans at their usual speeds", InsightTone.Neutral, "fans-usual"));

        if (insights.FirstOrDefault(i => i.Key == "usual-problems") is { } moved) list.Add(moved);
        else
        {
            string? words = usual?.Problems is { Now.Count: > 0 } pr ? pr.Words
                : problems.Count > 0 && usual?.Problems is null ? string.Join(" and ", problems.GroupBy(k => k).OrderByDescending(g => g.Count()).Select(g => Wording.Count(g.Key, g.Count())))
                : null;
            list.Add(words is null
                ? new Insight(IconGood, $"No PC problems {week}", InsightTone.Good, "no-problems")
                : new Insight(IconWarn, $"{Wording.Capital(words)} {week}", InsightTone.Neutral, "pc-problems"));
        }
        // The power's shut-offs: said in grey, unless they jumped far past their usual (then the insight, in amber).
        if (insights.FirstOrDefault(i => i.Key == "usual-power") is { } power) list.Add(power);
        else if (usual?.Problems is { PowerNow: > 0 } p) list.Add(new Insight(IconPower, $"{Wording.Capital(p.PowerWords)} {week}", InsightTone.Neutral, "power-cuts"));
        else if (usual?.Problems is null && cuts > 0) list.Add(new Insight(IconPower, $"{Wording.Capital(Wording.PowerOffs(cutsLikely ?? cuts, cuts))} {week}", InsightTone.Neutral, "power-cuts"));
        return InsightLinks.Apply(list);
    }
}
