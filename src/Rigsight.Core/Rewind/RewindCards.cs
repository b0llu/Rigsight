using System.Globalization;

namespace Rigsight.Core.Rewind;

/// <summary>A line under a card's sentence: a ranked game with its bar, a day, a reading.</summary>
public sealed record RewindRow(string Name, string Value, string? Detail = null, double Bar = 0, int Rank = 0, string? Exe = null, string? Path = null);

/// <summary>
/// One card. Its small label and its big figure read as one plain sentence ("Your PC was on for" / "211h 38m"), the
/// line under them says more without saying the figure again. Hues are the card's two glows, from the app's palette.
/// </summary>
public sealed record RewindCard(string Id, string Name, string Label, string Big, string? Sub, IReadOnlyList<RewindRow> Rows, string Hue1, string Hue2)
{
    /// <summary>A name or a phrase rather than a number: drawn a size smaller.</summary>
    public bool Wordy { get; init; }
    /// <summary>The summary's figures, two by two.</summary>
    public IReadOnlyList<(string Value, string Label)> Tiles { get; init; } = [];
    /// <summary>The heat card's readings: degrees, part and when.</summary>
    public IReadOnlyList<(string Deg, string Part, string When, string Hue)> Heat { get; init; } = [];
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
    private static string Hours(double sec) => Lang.T($"{Math.Round(sec / 3600):0} hours");

    public static List<RewindCard> Of(RewindFacts f)
    {
        var cards = new List<RewindCard>();
        string title = Title(f);
        bool games = f.Games && f.GamingSec >= 1800;

        // Cover
        string coverSub = games
            ? Lang.T($"Your PC was on for {Hours(f.OnSec)}, and {Math.Round(f.GamingSec / 3600):0} of them went to games.")
            : Lang.T($"Your PC was on for {Hours(f.OnSec)}, on {f.DaysOn} of its {f.Days} days.");
        cards.Add(new("cover", title, Lang.T("A look back at"), CoverWord(f), coverSub, [], KindHue(f.Kind), Cyan));

        if (f.Time && f.OnSec > 0)
        {
            var rows = new List<RewindRow>();
            if (f.BusiestDay is { } busiest) rows.Add(new(Lang.T("Your busiest day"), Units.Duration(f.BusiestDaySec), Day(busiest)));
            rows.Add(new(Lang.T("Days you switched it on"), "", Lang.T($"{f.DaysOn} out of {f.Days}")));
            cards.Add(new("time", Lang.T("PC on for"), Lang.T("Your PC was on for"), Units.Duration(f.OnSec),
                Lang.T($"You were using it for {Units.Duration(f.ActiveSec)} of that."), rows, Sky, Blue));
        }

        if (games)
        {
            string played = f.GameCount == 1
                ? (f.Plays == 1 ? Lang.T("You played 1 game, once.") : Lang.T($"You played 1 game, {f.Plays} times."))
                : Lang.T($"You played {f.GameCount} different games, {f.Plays} times in all.");
            cards.Add(new("games", Lang.T("Time in games"), Lang.T("Time in games"), Units.Duration(f.GamingSec), played, [], Purple, Blue));

            if (f.TopGames.Count > 0)
            {
                double top = f.TopGames[0].Value;
                var rows = f.TopGames.Select((g, i) => new RewindRow(g.Name, Units.Duration(g.Value), null, top > 0 ? g.Value / top : 0, i + 1, g.Exe, g.Path)).ToList();
                cards.Add(new("top", Lang.T("Most played"), Lang.T("Most played"), f.TopGames[0].Name, null, rows, Blue, Pink) { Wordy = true });
            }

            if (f.LongestGame is { } longest && f.LongestStart is { } start && longest.Value >= 1200)
                cards.Add(new("longest", Lang.T("Longest sitting"), Lang.T("Your longest sitting"), Units.Duration(longest.Value),
                    Lang.T($"{longest.Name} without a break, from {Clock(start)} on {Day(start)}."), [], Pink, Purple));
        }

        if (f.Heat && (f.HotSpot ?? f.GpuHot ?? f.CpuHot) is { } hottest)
        {
            var parts = new[] { (f.HotSpot, Lang.T("Your graphics card’s hot spot"), Purple), (f.GpuHot, Lang.T("Your graphics card"), Cyan), (f.CpuHot, Lang.T("Your processor"), Blue) }
                .Where(p => p.Item1 is not null).Select(p => (Heat: p.Item1!, Part: p.Item2, Hue: p.Item3)).ToList();
            var max = parts.MaxBy(p => p.Heat.Value);
            string When(RewindHeat h) => h.App is { } app
                ? (h.InGame ? Lang.T($"while you played {app} on {Day(h.At)}") : Lang.T($"while {app} was in front on {Day(h.At)}"))
                : Lang.T($"on {Day(h.At)}");
            var rest = parts.Where(p => p.Heat != max.Heat).Select(p => (Units.TempShort(p.Heat.Value), p.Part, Capital(When(p.Heat)), p.Hue)).ToList();
            cards.Add(new("heat", Lang.T("Hottest it got"), Lang.T("The hottest it got"), Units.TempShort(max.Heat.Value),
                Lang.T($"{max.Part}, {When(max.Heat)}."), [], Cyan, Purple) { Heat = rest });
        }

        if (f.Changes && (f.NewDriver is not null || f.Installed + f.Updated + f.Removed > 0))
        {
            var rows = new List<RewindRow>();
            if (f.Installed > 0) rows.Add(new(Lang.T("Apps installed"), f.Installed.ToString(CultureInfo.CurrentCulture)));
            if (f.Updated > 0) rows.Add(new(Lang.T("Apps updated"), f.Updated.ToString(CultureInfo.CurrentCulture)));
            if (f.Removed > 0) rows.Add(new(Lang.T("Apps removed"), f.Removed.ToString(CultureInfo.CurrentCulture)));
            if (f.NewDriver is { } driver && f.NewDriverAt is { } at)
                cards.Add(new("changes", Lang.T("What changed"), Lang.T("What changed"), Lang.T("A new graphics driver"),
                    Lang.T($"Version {driver}, installed on {Day(at)}."), rows, Purple, Cyan) { Wordy = true });
            else
            {
                int n = f.Installed + f.Updated + f.Removed;
                cards.Add(new("changes", Lang.T("What changed"), Lang.T("What changed"), n == 1 ? Lang.T("1 app") : Lang.T($"{n} apps"),
                    Lang.T("Installed, updated or removed on your PC."), rows, Purple, Cyan) { Wordy = true });
            }
        }

        if (f.Internet && f.Down > 0)
        {
            double top = f.TopInternet.Count > 0 ? f.TopInternet[0].Value : 0;
            var rows = f.TopInternet.Select(a => new RewindRow(a.Name, Units.Data(a.Value), null, top > 0 ? a.Value / top : 0, 0, a.Exe, a.Path)).ToList();
            string sub = rows.Count > 0 ? Lang.T($"And uploaded {Units.Data(f.Up)}. Most of it came through these:") : Lang.T($"And uploaded {Units.Data(f.Up)}.");
            cards.Add(new("internet", Lang.T("You downloaded"), Lang.T("You downloaded"), Units.Data(f.Down), sub, rows, Sky, Cyan));
        }

        if (f.Problems && f.AppCrashes + f.ShutOffs > 0)
        {
            string big = f.AppCrashes switch { 0 => Lang.T("No app crashes"), 1 => Lang.T("1 app crash"), _ => Lang.T($"{f.AppCrashes} app crashes") };
            string sub = f.ShutOffs switch
            {
                0 => Lang.T("Your PC never shut down without warning."),
                1 => Lang.T("Your PC also shut down without warning once."),
                _ => Lang.T($"Your PC also shut down without warning {f.ShutOffs} times."),
            };
            cards.Add(new("problems", Lang.T("Problems"), Lang.T("Problems"), big, sub, [], Slate, Blue) { Wordy = true });
        }

        if (games && Player(f) is { } player)
            cards.Add(new("type", Lang.T("Kind of player"), Lang.T("What kind of player you were"), player.Name, player.Why, [], Pink, Blue) { Wordy = true });

        // In short: the four biggest facts of the topics shown.
        var tiles = new List<(string, string)>();
        if (f.Time && f.OnSec > 0) tiles.Add(($"{Math.Round(f.OnSec / 3600):0} h", Lang.T("PC switched on")));
        if (games) tiles.Add(($"{Math.Round(f.GamingSec / 3600):0} h", Lang.T("playing games")));
        if (games && f.TopGames.Count > 0) tiles.Add((f.TopGames[0].Name, Lang.T("most played")));
        if (f.Internet && f.Down > 0) tiles.Add((Units.Data(f.Down), Lang.T("downloaded")));
        if (tiles.Count < 4 && f.Time && f.DaysOn > 0) tiles.Add(($"{f.DaysOn}", Lang.T("days switched on")));
        if (tiles.Count >= 2)
        {
            string name = f.Kind switch
            {
                RewindKind.Month => Lang.T("Your month"),
                RewindKind.Quarter => Lang.T("Your 3 months"),
                RewindKind.Half => Lang.T("Your 6 months"),
                _ => Lang.T("Your year"),
            };
            cards.Add(new("summary", name, Lang.T($"{title} in short"), "", null, [], Blue, Pink) { Tiles = tiles.Take(4).ToList() });
        }
        return cards;
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..];

    /// <summary>
    /// What kind of player the period showed, by plain rules on its own figures (never against other people): one game
    /// above all, many games, long sittings, late nights, or steady play. Null with too little play to say.
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
            return (Lang.T("Variety player"), first.Count > 1
                ? Lang.T($"You tried {f.GameCount} different games, but {first.Name} was the one you kept coming back to: {first.Count} times.")
                : Lang.T($"You tried {f.GameCount} different games."));
        return (Lang.T("Steady player"), Lang.T($"{f.Plays} times in all, about {Units.Duration(perPlay)} each time."));
    }

    /// <summary>The notification's line: "You played games for 62 hours, Dota 2 the most. Click to watch it."</summary>
    public static string Teaser(RewindFacts f) =>
        f.Games && f.GamingSec >= 1800 && f.TopGames.Count > 0
            ? Lang.T($"You played games for {Hours(f.GamingSec)}, {f.TopGames[0].Name} the most. Click to watch it.")
            : Lang.T($"Your PC was on for {Hours(f.OnSec)}. Click to watch it.");

    /// <summary>"Your October rewind is ready", "Your January to March rewind is ready".</summary>
    public static string Ready(RewindFacts f) => Lang.T($"Your {Title(f)} rewind is ready");
}
