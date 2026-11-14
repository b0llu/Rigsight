using System.Globalization;
using Rigsight.Core;
using Rigsight.Core.Reports;

namespace Rigsight.Models;

/// <summary>
/// One part on the Temperatures page's "Over the months" (approved mock-up, 2026-11-14): how far it climbs above its
/// resting temperature under the same load, week by week or month by month, against its usual band from its first weeks.
/// </summary>
public sealed record CoolingPart
{
    public required string Name { get; init; }
    /// <summary>The part's own colour (a theme brush's name).</summary>
    public required string Brush { get; init; }
    public required WearRow Row { get; init; }
    /// <summary>The points drawn: the last weeks, or each month.</summary>
    public IReadOnlyList<WearWeek> Points { get; init; } = [];
    public bool ByMonth { get; init; }
    /// <summary>The scale both charts share (see <see cref="CoolingCard.Of"/>).</summary>
    public double ScaleLow { get; init; } = double.NaN;
    public double ScaleSpan { get; init; } = 8;

    /// <summary>Warmer than its usual band for two weeks running: the figure, the start of the line and the last point go amber.</summary>
    public bool IsWarmer => Row is { State: WearState.Moved, Now: double now, Then: double then } && now > then;

    public string BigText => Row.Now is double now ? Units.TempGap(now) : "—";

    /// <summary>"Up 4° since late September," in amber when warmer; empty when it hasn't moved.</summary>
    public string LeadText => Row is { State: WearState.Moved, Now: double now, Then: double then, ThenDate: DateTime since }
        ? (now > then ? Lang.T($"Up {Units.TempGap(now - then)} since {Wear.WhenSaid(since)},") : Lang.T($"Down {Units.TempGap(then - now)} since {Wear.WhenSaid(since)},")) + " "
        : "";

    public string TailText => Row.State switch
    {
        WearState.Moved => Row.WeeksOutside == 1 ? Lang.T("for a week now") : Lang.T($"for {Row.WeeksOutside} weeks now"),
        WearState.Steady when Row.ThenDate is DateTime since => Lang.T($"Inside its usual band, as in {Wear.WhenSaid(since)}"),
        _ => Lang.T("Too soon to say.") + " " + WearTiles.NeedsWeeks(Row.History.Count),
    };

    /// <summary>The chart's name for screen readers.</summary>
    public string ChartName => Lang.T($"{Name} above rest under load, {(ByMonth ? Lang.T("month by month") : Lang.T("week by week"))}");

    /// <summary>"Under load 65°, was 59°": the two temperatures the gap is made of, now and in the first weeks.</summary>
    public string LoadText => Row.LoadNow is double now
        ? Row.LoadThen is double then && Row.State != WearState.TooSoon ? Lang.T($"Under load {Units.TempShort(now)}, was {Units.TempShort(then)}") : Lang.T($"Under load {Units.TempShort(now)}")
        : "";
    public string RestText => Row.RestNow is double now
        ? Row.RestThen is double then && Row.State != WearState.TooSoon ? Lang.T($"At rest {Units.TempShort(now)}, was {Units.TempShort(then)}") : Lang.T($"At rest {Units.TempShort(now)}")
        : "";
    public bool HasFacts => LoadText.Length > 0;

    public string FirstLabel => Points.Count > 0 ? $"{Units.TempGap(Points[0].Value)} · {When(Points[0].Start)}" : "";
    public string LastLabel => Points.Count > 1 ? $"{Units.TempGap(Points[^1].Value)} · {When(Points[^1].Start)}" : "";

    /// <summary>What the chart says under the pointer for each point: its week or month, the gap, and what it's made of.</summary>
    public IReadOnlyList<string> Tips => [.. Points.Select(p =>
        $"{(ByMonth ? p.Start.ToString("MMMM yyyy", CultureInfo.CurrentCulture) : Lang.T($"Week of {p.Start.ToString("d MMM", CultureInfo.CurrentCulture)}"))}\n"
        + Lang.T($"{Units.TempGap(p.Value, "0.0")} above rest")
        + (p is { Load: double load, Rest: double rest } ? "\n" + Lang.T($"{Units.TempShort(load)} under load, {Units.TempShort(rest)} at rest") : ""))];

    private string When(DateTime start) => start.ToString(ByMonth ? "MMM" : "d MMM", CultureInfo.CurrentCulture);
}

/// <summary>The card's parts, drawn on one scale so a flat line is truly flat.</summary>
public static class CoolingCard
{
    /// <summary>The smallest span the scale shows (°C): a wobble of a degree stays a wobble.</summary>
    public const double MinSpan = 4;

    public static IReadOnlyList<CoolingPart> Of(WearRow cpu, WearRow? gpu, bool byMonth)
    {
        var parts = new List<CoolingPart> { Part("CPU", "CpuBrush", cpu, byMonth) };
        if (gpu is not null) parts.Add(Part("GPU", "GpuBrush", gpu, byMonth));
        var values = parts.SelectMany(p => p.Points.Select(w => w.Value)
            .Concat(p.Row.BandLow is double lo ? [lo] : []).Concat(p.Row.BandHigh is double hi ? [hi] : [])).ToList();
        if (values.Count == 0) return parts;
        double low = values.Min(), high = values.Max();
        double span = Math.Max(MinSpan, high - low) + 1;
        return [.. parts.Select(p => p with { ScaleLow = low - (span - (high - low)) / 2, ScaleSpan = span })];
    }

    private static CoolingPart Part(string name, string brush, WearRow row, bool byMonth) => new()
    {
        Name = name, Brush = brush, Row = row, ByMonth = byMonth,
        Points = byMonth ? Months(row.History) : row.Weeks,
    };

    /// <summary>Each month's average of its weeks (a week counts in the month it starts in), the last twelve.</summary>
    public static IReadOnlyList<WearWeek> Months(IReadOnlyList<WearWeek> weeks) =>
        [.. weeks.GroupBy(w => new DateTime(w.Start.Year, w.Start.Month, 1))
            .OrderBy(g => g.Key)
            .Select(g => new WearWeek(g.Key, g.Average(w => w.Value),
                g.All(w => w.Load is not null) ? g.Average(w => w.Load!.Value) : null,
                g.All(w => w.Rest is not null) ? g.Average(w => w.Rest!.Value) : null))
            .TakeLast(12)];
}
