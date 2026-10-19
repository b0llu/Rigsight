using System.Globalization;

namespace Rigsight.Core.Rewind;

/// <summary>A line under a card's sentence: a ranked game, an app and what it took in.</summary>
public sealed record RewindRow(string Name, string Value, double Bar = 0, int Rank = 0, string? Exe = null, string? Path = null);

/// <summary>A small figure with its label above it, the two reading as one sentence ("Your PC was on for" / "220h").</summary>
public sealed record RewindTile(string Label, string Value, string? Hue = null);

/// <summary>A tall bar with its figure and what it counts ("11" / "installed").</summary>
public sealed record RewindBar(string Value, string Label, double Fraction, string Hue);

/// <summary>
/// One card, as the design approved on 2026-10-19 draws it: a small label and a big figure that read as one plain
/// sentence ("Your PC was on for" / "220" "hours"), the line under them saying more without repeating the figure, and
/// what the card shows besides (games, tiles, bars). The picture itself (calendar, clock, art) comes from the facts.
/// </summary>
public sealed record RewindCard(string Id, string Name, string Label, string Big, string? Sub)
{
    /// <summary>Set smaller beside the big figure ("hours", "42m", "GB").</summary>
    public string? Unit { get; init; }
    /// <summary>A name or a phrase rather than a number: drawn a size smaller.</summary>
    public bool Wordy { get; init; }
    public IReadOnlyList<RewindRow> Rows { get; init; } = [];
    public IReadOnlyList<RewindTile> Tiles { get; init; } = [];
    public IReadOnlyList<RewindBar> Bars { get; init; } = [];
    public IReadOnlyList<string> Chips { get; init; } = [];
    /// <summary>A program the card's picture is drawn from (the most used app, for its icon).</summary>
    public string? Tag { get; init; }
    /// <summary>The longest sitting: from and to, as times of day.</summary>
    public (string From, string To, double Start, double End)? Span { get; init; }
}

/// <summary>
/// A rewind's cards, as RigSight shows them: put together from the saved facts in the language in use. A card with
/// nothing worth saying is left out rather than shown with a zero.
/// </summary>
public static class RewindCards
{
    // The palette's own colours (Cpu, Gpu, Purple, Pink, Sky): never green, amber or red, which mean good and bad.
    public const string Blue = "#5B8CFF", Cyan = "#22D3EE", Purple = "#B18CFF", Pink = "#F472B6", Sky = "#38BDF8", Slate = "#A3B3CC";

    public static string KindHue(RewindKind kind) => kind switch
    {
        RewindKind.Month => Blue,
        RewindKind.Quarter => Cyan,
        RewindKind.Half => Purple,
        _ => Pink,
    };

    /// <summary>"October", "January to March", "January to June", "2026".</summary>
    public static string Title(RewindFacts f) => f.Kind switch
    {
        RewindKind.Month => Month(f.From),
        RewindKind.Year => f.From.Year.ToString(CultureInfo.InvariantCulture),
        _ => Lang.T($"{Month(f.From)} to {Month(f.To.AddDays(-1))}"),
    };

    /// <summary>The big word on the cover: short enough to fill it ("October", "Jan – Mar", "2026").</summary>
    public static string CoverWord(RewindFacts f) => f.Kind switch
    {
        RewindKind.Month => Month(f.From),
        RewindKind.Year => f.From.Year.ToString(CultureInfo.InvariantCulture),
        _ => $"{Short(f.From)} – {Short(f.To.AddDays(-1))}",
    };

    /// <summary>With the year, for the list, the cards' corner and the folder: "October 2026", "January to March 2027".</summary>
    public static string TitleWithYear(RewindFacts f) => f.Kind == RewindKind.Year ? Title(f) : $"{Title(f)} {f.From.Year}";

    public static string KindName(RewindKind kind) => kind switch
    {
        RewindKind.Month => Lang.T("Month"),
        RewindKind.Quarter => Lang.T("3 months"),
        RewindKind.Half => Lang.T("6 months"),
        _ => Lang.T("Year"),
    };

    private static string Month(DateTime d) => d.ToString("MMMM", CultureInfo.CurrentCulture);
    private static string Short(DateTime d) => d.ToString("MMM", CultureInfo.CurrentCulture);
    private static string Day(DateTime d) => d.ToString("dddd d MMMM", CultureInfo.CurrentCulture);
    private static string Clock(DateTime d) => d.ToString("h:mm tt", CultureInfo.CurrentCulture);
    private static string HourOfDay(int h) => DateTime.Today.AddHours(h % 24).ToString("h tt", CultureInfo.CurrentCulture);
    private static string Hours(double sec) => Lang.T($"{Math.Round(sec / 3600):0} hours");
    private static string Whole(double v) => Math.Round(v).ToString("0", CultureInfo.CurrentCulture);

    /// <summary>A duration split for the big figure: "62h" and "42m"; under an hour "45m" alone.</summary>
    private static (string Big, string? Unit) Split(double sec)
    {
        string d = Units.Duration(sec);
        int space = d.IndexOf(' ');
        return space > 0 ? (d[..space], d[(space + 1)..]) : (d, null);
    }

    /// <summary>The two hours in a row with the most play, as "from 9 PM to 11 PM".</summary>
    private static (int From, int To)? PeakHours(double[] hours)
    {
        if (hours.Length != 24 || hours.Sum() <= 0) return null;
        int best = Enumerable.Range(0, 24).MaxBy(h => hours[h] + hours[(h + 1) % 24]);
        return (best, best + 2);
    }

    public static List<RewindCard> Of(RewindFacts f)
    {
        var cards = new List<RewindCard>();
        string title = Title(f);
        bool games = f.Games && f.GamingSec >= 1800;
        // A little play (a couple of games, a few hours): the games still get their cards, but what kind of user the
        // period showed comes from the whole PC, not from a handful of sittings.
        bool light = games && (f.GamingSec < 5 * 3600 || f.GameCount <= 2);

        // Cover
        string coverSub = games
            ? Lang.T($"{Hours(f.OnSec)} on your PC. {Whole(f.GamingSec / 3600)} of them in {f.GameCount} games.")
            : Lang.T($"{Hours(f.OnSec)} on your PC, on {f.DaysOn} of its {f.Days} days.");
        cards.Add(new("cover", title, Lang.T("Your rewind"), CoverWord(f), coverSub));

        if (f.Time && f.OnSec > 0)
        {
            string sub = f.BusiestDay is { } busiest
                ? Lang.T($"On {f.DaysOn} of {f.Days} days. Your biggest day was {Day(busiest)}: {Hours(f.BusiestDaySec)} on.")
                : Lang.T($"On {f.DaysOn} of {f.Days} days.");
            cards.Add(new("time", Lang.T("PC on for"), Lang.T("Your PC was on for"), Whole(f.OnSec / 3600), sub) { Unit = Lang.T("hours") });
        }

        // The apps used most: for every PC, and the heart of one that never plays games.
        if (f.Time && f.TopApps.Count > 0)
        {
            var first = f.TopApps[0];
            var rows = f.TopApps.Skip(1).Select((a, i) => new RewindRow(a.Name, Units.Duration(a.Value), 0, i + 2, a.Exe, a.Path)).ToList();
            cards.Add(new("apps", Lang.T("Most used"), Lang.T("Your most used app"), first.Name, Lang.T($"{Units.Duration(first.Value)} in front of you."))
            { Wordy = true, Rows = rows, Tag = first.Path });
        }
        if (f.Time && (!games || light) && PeakHours(f.UseHours) is var (useFrom, useTo))
            cards.Add(new("hours", Lang.T("When you used it"), Lang.T("You used your PC most between"), $"{HourOfDay(useFrom)} – {HourOfDay(useTo)}",
                Lang.T($"{Hours(f.ActiveSec)} in use in all.")) { Wordy = true });

        if (games)
        {
            var (big, unit) = Split(f.GamingSec);
            string sub = PeakHours(f.PlayHours) is var (from, to)
                ? Lang.T($"You played most between {HourOfDay(from)} and {HourOfDay(to)}.")
                : f.GameCount == 1 ? Lang.T("You played 1 game.") : Lang.T($"You played {f.GameCount} different games.");
            cards.Add(new("games", Lang.T("Time in games"), Lang.T("Time in games"), big, sub) { Unit = unit });

            if (f.TopGames.Count > 0)
            {
                var first = f.TopGames[0];
                string played = first.Count == 1
                    ? Lang.T($"{Units.Duration(first.Value)}, played once.")
                    : Lang.T($"{Units.Duration(first.Value)}, played {first.Count} times.");
                var rows = f.TopGames.Skip(1).Select((g, i) => new RewindRow(g.Name, Units.Duration(g.Value), 0, i + 2, g.Exe, g.Path)).ToList();
                cards.Add(new("top", Lang.T("Most played"), Lang.T("Your most played"), first.Name, played) { Wordy = true, Rows = rows, Tag = first.Path });
            }

            if (f.LongestGame is { } longest && f.LongestStart is { } start && longest.Value >= 1200)
            {
                var (big2, unit2) = Split(longest.Value);
                var end = f.LongestEnd ?? start.AddSeconds(longest.Value);
                cards.Add(new("longest", Lang.T("Longest sitting"), Lang.T("Your longest sitting"), big2,
                    Lang.T($"{longest.Name} without a break, on {Day(start)}."))
                {
                    Unit = unit2,
                    Tag = longest.Path,
                    Span = (Clock(start), Clock(end), start.TimeOfDay.TotalHours / 24, Math.Min(1, (end - start.Date).TotalHours / 24)),
                });
            }
        }

        if (f.Internet && f.Down > 0)
        {
            string data = Units.Data(f.Down);
            int space = data.LastIndexOf(' ');
            double top = f.TopInternet.Count > 0 ? f.TopInternet[0].Value : 0;
            var rows = f.TopInternet.Select(a => new RewindRow(a.Name, Units.Data(a.Value), top > 0 ? a.Value / top : 0, 0, a.Exe, a.Path)).ToList();
            string sub = rows.Count > 0 ? Lang.T($"And uploaded {Units.Data(f.Up)}. Most of it came through these:") : Lang.T($"And uploaded {Units.Data(f.Up)}.");
            cards.Add(new("internet", Lang.T("You downloaded"), Lang.T("You downloaded"), space > 0 ? data[..space] : data, sub)
            { Unit = space > 0 ? data[(space + 1)..] : null, Rows = rows });
        }

        if (f.Heat && (f.HotSpot ?? f.GpuHot ?? f.CpuHot) is not null)
        {
            var parts = new[] { (f.HotSpot, Lang.T("Your graphics card’s hot spot"), Lang.T("Graphics card hot spot"), Purple),
                                (f.GpuHot, Lang.T("Your graphics card"), Lang.T("Graphics card"), Cyan),
                                (f.CpuHot, Lang.T("Your processor"), Lang.T("Processor"), Blue) }
                .Where(p => p.Item1 is not null).Select(p => (Heat: p.Item1!, Whole: p.Item2, Part: p.Item3, Hue: p.Item4)).ToList();
            var max = parts.MaxBy(p => p.Heat.Value);
            string When(RewindHeat h) => h.App is { } app
                ? (h.InGame ? Lang.T($"while you played {app} on {Day(h.At)}") : Lang.T($"while {app} was in front on {Day(h.At)}"))
                : Lang.T($"on {Day(h.At)}");
            string Where(RewindHeat h) => h.App is { } app ? Lang.T($"{h.At:d MMMM}, in {app}") : h.At.ToString("dddd d MMMM", CultureInfo.CurrentCulture);
            var tiles = parts.Where(p => p.Heat != max.Heat).Select(p => new RewindTile($"{p.Part}, {Where(p.Heat)}", Units.TempShort(p.Heat.Value), p.Hue)).ToList();
            // A cool period says so: the highest was still well under what any part minds.
            string sub = max.Heat.Value < 70
                ? Lang.T($"{max.Whole}, {When(max.Heat)}. It stayed cool all along.")
                : Lang.T($"{max.Whole}, {When(max.Heat)}.");
            cards.Add(new("heat", Lang.T("Hottest it got"), Lang.T("The hottest it got"), Units.TempShort(max.Heat.Value), sub) { Tiles = tiles });
        }

        if (f.Changes && (f.NewDriver is not null || f.Installed + f.Updated + f.Removed > 0))
        {
            int most = Math.Max(1, Math.Max(f.Installed, Math.Max(f.Updated, f.Removed)));
            var bars = new List<RewindBar>
            {
                new(f.Installed.ToString(CultureInfo.CurrentCulture), Lang.T("installed"), (double)f.Installed / most, Cyan),
                new(f.Updated.ToString(CultureInfo.CurrentCulture), Lang.T("updated"), (double)f.Updated / most, Sky),
                new(f.Removed.ToString(CultureInfo.CurrentCulture), Lang.T("removed"), (double)f.Removed / most, Blue),
            };
            if (f.NewDriver is { } driver && f.NewDriverAt is { } at)
                cards.Add(new("changes", Lang.T("What changed"), Lang.T("What changed on your PC"), Lang.T("A new graphics driver"), null)
                { Wordy = true, Bars = bars, Chips = [Lang.T($"Version {driver}"), Day(at)] });
            else
            {
                int n = f.Installed + f.Updated + f.Removed;
                cards.Add(new("changes", Lang.T("What changed"), Lang.T("What changed on your PC"), n == 1 ? Lang.T("1 app") : Lang.T($"{n} apps"),
                    Lang.T("Installed, updated or removed.")) { Wordy = true, Bars = bars });
            }
        }

        // Nothing went wrong: good news is a card too.
        if (f.Problems && f.AppCrashes + f.ShutOffs == 0 && f.DaysOn > 0)
            cards.Add(new("problems", Lang.T("What went wrong"), Lang.T("What went wrong"), Lang.T("Nothing"),
                Lang.T("Not one app or game crashed, and your PC never shut down without warning.")) { Wordy = true });
        if (f.Problems && f.AppCrashes + f.ShutOffs > 0)
        {
            int worst = f.CrashesByDay.Count > 0 ? f.CrashesByDay.Max() : 0;
            string? sub = worst > 1
                ? Lang.T($"The worst day was {Day(f.From.AddDays(f.CrashesByDay.IndexOf(worst)))}, with {worst} crashes.")
                : null;
            cards.Add(new("problems", Lang.T("What went wrong"), Lang.T("What went wrong"), "", sub)
            {
                Tiles =
                [
                    new(f.AppCrashes == 1 ? Lang.T("time an app or game crashed") : Lang.T("times an app or game crashed"), f.AppCrashes.ToString(CultureInfo.CurrentCulture), Purple),
                    new(f.ShutOffs == 1 ? Lang.T("time your PC shut down without warning") : Lang.T("times your PC shut down without warning"), f.ShutOffs.ToString(CultureInfo.CurrentCulture), Blue),
                ],
            });
        }

        if (games && !light && Player(f) is { } player)
        {
            var tiles = new List<RewindTile> { new(Lang.T("games"), f.GameCount.ToString(CultureInfo.CurrentCulture)), new(Lang.T("times played"), f.Plays.ToString(CultureInfo.CurrentCulture)) };
            if (f.TopGames.Count > 0 && f.TopGames[0].Count > 1) tiles.Add(new(f.TopGames[0].Name, $"{f.TopGames[0].Count}×"));
            cards.Add(new("type", Lang.T("Kind of player"), Lang.T("What kind of player you were"), player.Name, player.Why)
            { Wordy = true, Tiles = tiles, Chips = [f.GameCount == 1 ? Lang.T("1 game") : Lang.T($"{f.GameCount} games")] });
        }

        if ((!games || light) && f.Time && User(f) is { } user)
            cards.Add(new("type", Lang.T("Kind of PC user"), Lang.T("What kind of PC user you were"), user.Name, user.Why)
            {
                Wordy = true,
                Chips = [f.DaysOn == 1 ? Lang.T("1 day on") : Lang.T($"{f.DaysOn} days on")],
                Tiles = [new(Lang.T("days on"), f.DaysOn.ToString(CultureInfo.CurrentCulture)), new(Lang.T("a day"), $"{Whole(f.ActiveSec / 3600 / Math.Max(1, f.DaysOn))}h"),
                         new(Lang.T("in all"), $"{Whole(f.ActiveSec / 3600)}h")],
            });

        // In short: the top five and the four biggest facts of the topics shown.
        var four = new List<RewindTile>();
        if (f.Time && f.OnSec > 0) four.Add(new(Lang.T("Your PC was on for"), $"{Whole(f.OnSec / 3600)}h"));
        if (games) four.Add(new(Lang.T("You played games for"), $"{Whole(f.GamingSec / 3600)}h"));
        if (f.Internet && f.Down > 0) four.Add(new(Lang.T("You downloaded"), Units.Data(f.Down)));
        if (f.Heat && (f.HotSpot ?? f.GpuHot ?? f.CpuHot) is not null)
            four.Add(new(Lang.T("The hottest it got"), Units.TempShort(new[] { f.HotSpot, f.GpuHot, f.CpuHot }.Where(h => h is not null).Max(h => h!.Value))));
        if (four.Count < 4 && f.Time && f.DaysOn > 0) four.Add(new(Lang.T("Days you switched it on"), f.DaysOn.ToString(CultureInfo.CurrentCulture)));
        if (four.Count >= 2)
        {
            string name = f.Kind switch
            {
                RewindKind.Month => Lang.T("Your month"),
                RewindKind.Quarter => Lang.T("Your 3 months"),
                RewindKind.Half => Lang.T("Your 6 months"),
                _ => Lang.T("Your year"),
            };
            var rows = (games ? f.TopGames : f.Time ? f.TopApps : []).Select((g, i) => new RewindRow(g.Name, Units.Duration(g.Value), 0, i + 1, g.Exe, g.Path)).ToList();
            cards.Add(new("summary", name, Lang.T($"{title} in short"), "", null) { Tiles = [.. four.Take(4)], Rows = rows });
        }
        return cards;
    }

    /// <summary>
    /// What kind of player the period showed, by plain rules on its own figures (never against other people): one game
    /// above all, late nights, long sittings, many games, or steady play. Null with too little play to say.
    /// </summary>
    public static (string Name, string Why)? Player(RewindFacts f)
    {
        if (f.Plays < 3 || f.TopGames.Count == 0) return null;
        var first = f.TopGames[0];
        double share = f.GamingSec > 0 ? first.Value / f.GamingSec : 0;
        double perPlay = f.GamingSec / Math.Max(1, f.Plays);
        if (share >= 0.6)
            return (Lang.T("One-game player"), Lang.T($"{first.Name} took {Math.Round(share * 100):0}% of your time in games."));
        if (f.NightShare >= 0.5)
            return (Lang.T("Night player"), Lang.T($"More than half of your playing was after 9 PM, {first.Name} most of all."));
        if (perPlay >= 5400)
            return (Lang.T("Marathon player"), Lang.T($"You played for {Units.Duration(perPlay)} at a time, on average."));
        if (f.GameCount >= 8)
            return (Lang.T("Variety player"), Lang.T($"You tried something new all month, but {first.Name} kept pulling you back."));
        return (Lang.T("Steady player"), Lang.T($"About {Units.Duration(perPlay)} each time you played."));
    }

    /// <summary>
    /// For a PC without games, what kind of user the period showed, from when in the day it was used and how much:
    /// late nights, early mornings, long days, or steady use.
    /// </summary>
    public static (string Name, string Why)? User(RewindFacts f)
    {
        double all = f.UseHours.Sum();
        if (all <= 0 || f.DaysOn < 3) return null;
        double night = Enumerable.Range(0, 24).Where(h => h >= 21 || h < 5).Sum(h => f.UseHours[h]) / all;
        double morning = Enumerable.Range(5, 7).Sum(h => f.UseHours[h]) / all;
        double perDay = f.ActiveSec / 3600 / f.DaysOn;
        if (night >= 0.35) return (Lang.T("Night owl"), Lang.T("More than a third of your time on the PC was after 9 PM."));
        if (morning >= 0.35) return (Lang.T("Early bird"), Lang.T("More than a third of your time on the PC was before noon."));
        if (perDay >= 8) return (Lang.T("All-day user"), Lang.T($"About {Whole(perDay)} hours a day, on the days it was on."));
        return (Lang.T("Steady user"), Lang.T($"About {Whole(perDay)} hours a day, on {f.DaysOn} days."));
    }

    /// <summary>The notification's line: "You played games for 62 hours, Dota 2 the most. Click to watch it."</summary>
    public static string Teaser(RewindFacts f) =>
        f.Games && f.GamingSec >= 1800 && f.TopGames.Count > 0
            ? Lang.T($"You played games for {Hours(f.GamingSec)}, {f.TopGames[0].Name} the most. Click to watch it.")
            : Lang.T($"Your PC was on for {Hours(f.OnSec)}. Click to watch it.");

    /// <summary>"Your October rewind is ready", "Your January to March rewind is ready".</summary>
    public static string Ready(RewindFacts f) => Lang.T($"Your {Title(f)} rewind is ready");
}
