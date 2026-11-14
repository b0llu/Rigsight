using System.Windows;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;

namespace Rigsight.Models;

/// <summary>
/// A line of the "Over the months" tile (approved mock-up 2026-11-14), the same on every page that shows wear: an icon,
/// one statement (amber only when the part moved, grey while it's too soon to say), a grey detail under it, and a small
/// chart a bar a week with that week's text on hover.
/// </summary>
public sealed record WearTileRow(string Glyph, Brush IconBrush, string Statement, WearState State, string Detail,
    IReadOnlyList<WearWeek>? Weeks = null, double? Base = null, double MinSpan = 4, Brush? Fill = null, IReadOnlyList<string>? Tips = null)
{
    public bool IsFan => Glyph == "fan";
    public bool HasGlyph => !IsFan;
    /// <summary>Two weeks or more to draw: a single week as one bar says nothing yet.</summary>
    public bool HasChart => Weeks is { Count: >= 2 };
    public bool HasDetail => Detail.Length > 0;
    public Brush StatementBrush => (Brush)Application.Current.FindResource(State switch
    {
        WearState.Moved => "WarmBrush",
        WearState.TooSoon or WearState.NoReading => "MutedBrush",
        _ => "TextBrush",
    });
    public Brush LastFill => State == WearState.Moved ? (Brush)Application.Current.FindResource("WarmBrush") : Fill ?? Brushes.Gray;
}

/// <summary>The tile's lines for each kind of part, worded once (WORDING.md: both figures, plain words, no verdict).</summary>
public static class WearTiles
{
    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    /// <summary>"Against late September", the tile's corner; empty while there's nothing to compare against.</summary>
    public static string Against(DateTime? then) => then is DateTime t ? Lang.T($"Against {Wear.WhenSaid(t)}") : "";

    private static List<string> WeekTips(IReadOnlyList<WearWeek> weeks, Func<double, string> say) =>
        [.. weeks.Select(w => Lang.T($"Week of {w.Start:d MMM}") + "\n" + say(w.Value))];

    /// <summary>A fan row of the Fans page, with its weeks: its title, whether it's several fans (a GPU's), and its colour.</summary>
    public sealed record FanWear(string Title, bool Many, WearRow Wear, Brush Brush);

    /// <summary>"It needs 4 weeks of games or other heavy use, and has 2 so far.": what a part too new to judge still needs.</summary>
    public static string NeedsWeeks(int has) => has == 0
        ? Lang.T($"It needs {Wear.MinWeeks} weeks of games or other heavy use.")
        : Lang.T($"It needs {Wear.MinWeeks} weeks of games or other heavy use, and has {has} so far.");

    /// <summary>The most lines the Fans tile has before the fans that kept their speed are said together.</summary>
    public const int MaxFanLines = 4;

    /// <summary>
    /// The Fans page's lines (mock-up 2026-11-14): a line a fan row, any that moved first. Past <see cref="MaxFanLines"/>,
    /// the fans after the first steady ones are said in one line ("Fan #2 and Fan #5 as they did"), and those too new to
    /// judge in another.
    /// </summary>
    public static List<WearTileRow> Fans(IReadOnlyList<FanWear> fans)
    {
        static string Rpm(double v) => Units.Format(SensorKind.Fan, v);
        static string Heat(WearRow w) => w.RangeLow is double lo && w.RangeHigh is double hi
            ? Lang.T($"at {Units.TempShort(lo)} to {Units.TempShort(hi + FanCurves.BinDegrees)}") : "";
        static int Order(WearState s) => s switch { WearState.Moved => 0, WearState.Steady => 1, _ => 2 };
        var sorted = fans.OrderBy(f => Order(f.Wear.State)).ToList();

        WearTileRow One(FanWear f)
        {
            var w = f.Wear;
            if (w.State is WearState.TooSoon or WearState.NoReading || w.Now is not double now || w.Then is not double then)
                return new WearTileRow("fan", f.Brush, Lang.T($"Too soon to say for {f.Title}"), WearState.TooSoon, NeedsWeeks(w.History.Count),
                    w.Weeks, null, 200, f.Brush, WeekTips(w.Weeks, v => Lang.T($"{Rpm(v)} at the same heat")));
            string statement = w.State == WearState.Moved
                ? now > then
                    ? f.Many ? Lang.T($"{f.Title} spin {now - then:0} RPM faster for the same heat") : Lang.T($"{f.Title} spins {now - then:0} RPM faster for the same heat")
                    : f.Many ? Lang.T($"{f.Title} spin {then - now:0} RPM slower for the same heat") : Lang.T($"{f.Title} spins {then - now:0} RPM slower for the same heat")
                : f.Many ? Lang.T($"{f.Title} spin as fast for the heat as they did") : Lang.T($"{f.Title} spins as fast for the heat as it did");
            return new WearTileRow("fan", f.Brush, statement, w.State, Lang.T($"{Rpm(now)} {Heat(w)}, {then:0} then"),
                w.Weeks, then, 200, f.Brush, WeekTips(w.Weeks, v => Lang.T($"{Rpm(v)} at the same heat")));
        }

        if (sorted.Count <= MaxFanLines) return [.. sorted.Select(One)];
        var moved = sorted.Where(f => f.Wear.State == WearState.Moved).ToList();
        var steady = sorted.Where(f => f.Wear.State == WearState.Steady).ToList();
        var soon = sorted.Where(f => f.Wear.State is not (WearState.Moved or WearState.Steady)).ToList();
        int room = Math.Max(1, MaxFanLines - moved.Count - (soon.Count > 0 ? 1 : 0) - 1);
        var lines = moved.Select(One).Concat(steady.Take(room).Select(One)).ToList();
        var together = steady.Skip(room).ToList();
        if (together.Count == 1) lines.Add(One(together[0]));
        else if (together.Count > 1)
        {
            double most = together.Max(f => f.Wear.Then is > 0 and double t && f.Wear.Now is double n ? Math.Abs(n - t) / t * 100 : 0);
            lines.Add(new WearTileRow("fan", together[0].Brush, Lang.T($"{Wording.JoinAnd(together.Select(f => f.Title))} as they did"), WearState.Steady,
                Lang.T($"Each within {Math.Max(1, Math.Ceiling(most)):0}% at the same heat")));
        }
        if (soon.Count == 1) lines.Add(One(soon[0]));
        else if (soon.Count > 1)
            lines.Add(new WearTileRow("fan", soon[0].Brush, Lang.T($"Too soon to say for {Wording.JoinAnd(soon.Select(f => f.Title))}"), WearState.TooSoon,
                NeedsWeeks(soon.Min(f => f.Wear.History.Count))));
        return lines;
    }

    /// <summary>A drive's lines: its life left and pace (SSDs), its bad sectors, how warm it runs.</summary>
    public static List<WearTileRow> Drive(WearRow wear, DriveHealthInfo h)
    {
        var rows = new List<WearTileRow>();
        var pink = Res("PinkBrush");
        string? when = wear.ThenDate is DateTime t ? Wear.WhenSaid(t) : null;
        bool soon = wear.State == WearState.TooSoon;

        if (h.Life is double life)
        {
            string statement = wear.LifePerYear is double perYear && perYear >= 0.5
                ? Lang.T($"{life:0}% life left, about {perYear:0}% used a year")
                : Lang.T($"{life:0}% life left");
            string detail = soon || wear.Then is null || when is null ? Lang.T("Too soon to say how fast it wears") : Lang.T($"{wear.Then:0}% in {when}");
            rows.Add(new WearTileRow("", pink, statement, WearState.Steady, detail,
                wear.Weeks, wear.Then, 5, pink, WeekTips(wear.Weeks, v => Lang.T($"{v:0}% life left"))));
        }

        if (wear.SectorsNow is long now)
        {
            long then = wear.SectorsThen ?? now;
            if (now > then && when is not null)
                rows.Add(new WearTileRow("", Res("WarmBrush"), now == 1 ? Lang.T($"1 bad sector, up from {then:N0} in {when}") : Lang.T($"{now:N0} bad sectors, up from {then:N0} in {when}"),
                    WearState.Moved, Lang.T("Swapped out, waiting and unreadable together")));
            else if (now == 0)
                rows.Add(new WearTileRow("", Res("GoodBrush"), when is null || soon ? Lang.T("No bad sectors") : Lang.T($"No bad sectors, as in {when}"),
                    WearState.Steady, Lang.T("Swapped out, waiting and unreadable all at 0")));
            else
                rows.Add(new WearTileRow("", Res("WarmBrush"), now == 1 ? Lang.T("1 bad sector") : Lang.T($"{now:N0} bad sectors"), WearState.Moved,
                    Lang.T("Swapped out, waiting and unreadable together")));
        }

        if (wear.TempWeeks is { Count: >= 1 } temps)
        {
            double first = temps[0].Value, last = temps[^1].Value;
            bool compare = temps.Count >= 2 && when is not null;
            string statement = !compare ? Lang.T($"Runs at {last:0}° on average") : Math.Abs(last - first) < 3 ? Lang.T("Runs as warm as it did")
                : last > first ? Lang.T($"Runs {last - first:0}° warmer than in {when}") : Lang.T($"Runs {first - last:0}° cooler than in {when}");
            string detail = compare ? Lang.T($"{last:0}° on average, {first:0}° then") : "";
            rows.Add(new WearTileRow("", pink, statement, WearState.Steady, detail, temps, first, 4, pink, WeekTips(temps, v => Lang.T($"{v:0}° on average"))));
        }
        return rows;
    }
}
