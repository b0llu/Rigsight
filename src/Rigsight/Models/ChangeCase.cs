using System.Text;
using Rigsight.Core;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;

namespace Rigsight.Models;

/// <summary>A line of the opened change's "How sure" card: what it rests on, or what makes it less sure.</summary>
public sealed record ChangeCheckLine(string Icon, string Brush, string Title, string Detail);

/// <summary>A problem counted on one side of a change, to open as its own crash.</summary>
public sealed record ChangeProblemLine(DateTime Time, string When, string Title, string Side);

/// <summary>One graphics driver this PC had, and what it did where that's known: "617.42 · 7 Oct to now · Hotter".</summary>
public sealed record DriverLine(string Version, string Dates, string? Result, string Brush, DateTime Day, bool IsThis, bool CanOpen);

/// <summary>
/// A change opened from its result (the Timeline, Home, Reports, Crashes): what it made better or worse, every reading
/// before and since, day by day, how sure, and the problems counted. Built from <see cref="ChangeResult"/>, so it says
/// what every other page says.
/// </summary>
public sealed class ChangeCase(ChangeResult result, IReadOnlyList<DriverLine> drivers)
{
    public ChangeResult Result { get; } = result;
    public DateTime Day => Result.Day;

    public string Headline => Result.Headline;

    /// <summary>"NVIDIA graphics driver 617.42 · was 616.92 · Wed 7 Oct, 5:40 PM"; several changes: each of them.</summary>
    public string Under => string.Join("  ·  ", Result.Changes.Select(c => c.Shown + (c.Was is { Length: > 0 } was ? Lang.T($", was {was}") : "")))
        + $"  ·  {Result.Main.Time:ddd d MMM}, {(Result.Main.IsApproximate ? "~" : "")}{Result.Main.Time:h:mm tt}";

    public string Context => Result.Context;
    public string Chip => Result.Chip;
    /// <summary>For the chip's colours (see SureBrushConverter).</summary>
    public string Sure => Result.Sure.ToString();
    public string SureHint => Result.Sure == Confidence.Likely
        ? Lang.T("Like for like on both sides, with enough to go on")
        : Lang.T("Something stands in the way of more: see How sure");

    public string ToneBrush => Result.Tone == EffectTone.Worse ? "WarmBrush" : "GoodBrush";
    public string Icon => ResultIcon(Result);

    public string? Advice => Result.Advice;
    public bool HasAdvice => Advice is not null;

    public string RangeText => Result.Open
        ? Lang.T($"{Result.From:d MMM} to {Result.Day.AddDays(-1):d MMM} · {Result.Day.AddDays(1):d MMM} to now")
        : Lang.T($"{Result.From:d MMM} to {Result.Day.AddDays(-1):d MMM} · {Result.Day.AddDays(1):d MMM} to {Result.To.AddDays(-1):d MMM}");
    public string SinceHead => Result.Open ? Lang.T("SINCE") : Lang.T("AFTER");
    public List<ChangeFigure> Rows => Result.Rows;

    public List<ChangeCheckLine> Checks { get; } = [.. result.Checks.Select(c => c.Ok
        ? new ChangeCheckLine("", "GoodBrush", c.Title, c.Detail)
        : new ChangeCheckLine("", "WarmBrush", c.Title, c.Detail))];

    public List<ChangeProblemLine> Problems { get; } = [.. result.Before.Select(p => Line(p, Lang.T("Before the change"))).Concat(result.After.Select(p => Line(p, result.Open ? Lang.T("Since the change") : Lang.T("After the change"))))
        .OrderByDescending(p => p.Time)];
    public bool HasProblems => Problems.Count > 0;
    public string ProblemsCount => Result.Open ? Lang.T($"{Result.Before.Count} before, {Result.After.Count} since") : Lang.T($"{Result.Before.Count} before, {Result.After.Count} after");

    public List<DriverLine> Drivers { get; } = [.. drivers];
    /// <summary>Only when another driver made a difference too: a list of dates alone isn't worth a card.</summary>
    public bool HasDrivers => Drivers.Any(d => !d.IsThis && d.Result is not null);

    public List<ChangeDay> ChartDays => Result.Days;

    // "Each time you played": only where a temperature moved.
    public bool HasPlayChart => Result is { TempLabel: not null, TempBefore: not null, TempAfter: not null };
    public bool PlayHotter => Result.TempAfter > Result.TempBefore;
    public string PlayBrush => PlayHotter ? "WarmBrush" : "GoodBrush";
    public string AfterName => Result.Open ? Lang.T("Since") : Lang.T("After");
    public string BeforeTempText => Core.Units.TempShort(Result.TempBefore);
    public string AfterTempText => Core.Units.TempShort(Result.TempAfter);
    public string BeforeDaysText => Lang.T($"Before · {Played(Result.Days.Where(d => d.Day < Day))}");
    public string AfterDaysText => $"{AfterName} · {Played(Result.Days.Where(d => d.Day > Day))}";
    /// <summary>"10° hotter": between the two averages as shown.</summary>
    public string DiffText => Result.Pairs.FirstOrDefault(p => p.Label == Result.TempLabel)?.Meaning.Split(',')[0] ?? "";

    private static string Played(IEnumerable<ChangeDay> days) => days.Count(d => d.Temp is not null) is var n && n == 1 ? Lang.T("1 day") : Lang.T($"{n} days");

    private static ChangeProblemLine Line(ChangeProblem p, string side) =>
        new(p.Time, p.Time.ToString("ddd d MMM, h:mm tt"), p.Title.Length > 0 ? p.Title : Wording.Capital(Wording.Count(p.Kind, 1)), side);

    /// <summary>An icon for what moved: heat, or problems.</summary>
    public static string ResultIcon(ChangeResult r) => r.TempLabel is not null && r.Pairs.FirstOrDefault()?.Label == r.TempLabel ? ""
        : r.Tone == EffectTone.Worse ? "" : "";

    /// <summary>The whole result as text, for a forum post or a support request.</summary>
    public string Report()
    {
        var b = new StringBuilder();
        foreach (var c in Result.Changes)
            b.AppendLine($"{c.Shown}{(c.Was is { Length: > 0 } was ? Lang.T($" (was {was})") : "")} · {c.Time:ddd d MMM yyyy, h:mm tt}");
        b.AppendLine($"{Result.Headline}. {Result.HowSure}.");
        b.AppendLine(Result.Context);
        b.AppendLine();
        foreach (var r in Result.Rows)
        {
            string figures = Result.Open ? Lang.T($"{r.Label}: {r.Before} before, {r.After} since") : Lang.T($"{r.Label}: {r.Before} before, {r.After} after");
            b.AppendLine($"{figures}{(r.Meaning.Length > 0 ? $" ({r.Meaning})" : "")}");
        }
        b.AppendLine();
        b.AppendLine(Lang.T($"Compared: {RangeText}. {Result.Why}"));
        foreach (var c in Result.Checks.Where(c => !c.Ok)) b.AppendLine($"{c.Title}: {c.Detail}");
        if (Advice is not null) b.AppendLine(Lang.T($"What to try: {Advice}"));
        b.AppendLine();
        b.Append(Services.PcInfo.Text);
        return b.ToString();
    }
}
