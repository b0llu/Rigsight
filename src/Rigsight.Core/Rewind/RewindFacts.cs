namespace Rigsight.Core.Rewind;

/// <summary>How long a rewind looks back: a month, three months, six months or a year (never longer).</summary>
public enum RewindKind { Month, Quarter, Half, Year }

/// <summary>A game or app on a card: its name, its program (for its cover or icon) and its figure.</summary>
public sealed record RewindItem(string Name, string Exe, string? Path, double Value, int Count = 0);

/// <summary>One month of a longer rewind: its first day, its hours of use, and the game (or app) used most in it.</summary>
public sealed record RewindMonth(DateTime Start, double Hours, RewindItem? Top);

/// <summary>A hottest moment: the reading, when, and the game or app in front then.</summary>
public sealed record RewindHeat(double Value, DateTime At, string? App, bool InGame);

/// <summary>
/// What a rewind found, saved when it is made: numbers, names and times only, no sentences. The cards' words are put
/// together when shown, in the language in use (as every saved line of history is, see SavedText). A rewind recreated
/// is these facts read again with the settings of the day.
/// </summary>
public sealed class RewindFacts
{
    public string Id { get; set; } = "";
    public RewindKind Kind { get; set; }
    /// <summary>The first day, and the day after the last (midnight to midnight).</summary>
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public DateTime Made { get; set; }

    /// <summary>A test copy's sample made from the same history (No games, Two games, Quiet PC); null for a real rewind.</summary>
    public string? Sample { get; set; }

    /// <summary>Watched to the end or opened from the list: no "new" dot, no full screen.</summary>
    public bool Seen { get; set; }
    /// <summary>Already shown full screen, or put in the list because a bigger one took the screen that day.</summary>
    public bool Announced { get; set; }

    /// <summary>The topics on when it was made (what goes in): the cards follow these, not today's settings.</summary>
    public bool Time { get; set; }
    public bool Games { get; set; }
    public bool Internet { get; set; }
    public bool Heat { get; set; }
    public bool Changes { get; set; }
    public bool Problems { get; set; }

    // Time on the PC
    public double OnSec { get; set; }
    public double ActiveSec { get; set; }
    public int DaysOn { get; set; }
    public int Days { get; set; }
    public DateTime? BusiestDay { get; set; }
    public double BusiestDaySec { get; set; }
    /// <summary>Hours the PC was in use on each day of the period, from its first day (for the calendar and the day strip).</summary>
    public List<double> DayHours { get; set; } = [];
    /// <summary>A longer rewind's months, in order (empty for a month's own).</summary>
    public List<RewindMonth> Months { get; set; } = [];
    /// <summary>The apps (not games) used the most, by their time in front.</summary>
    public List<RewindItem> TopApps { get; set; } = [];
    /// <summary>Hours of use in each hour of the day, every app and game, over the whole period.</summary>
    public double[] UseHours { get; set; } = new double[24];

    // Games
    public double GamingSec { get; set; }
    public int GameCount { get; set; }
    public int Plays { get; set; }
    public List<RewindItem> TopGames { get; set; } = [];
    public RewindItem? LongestGame { get; set; }
    public DateTime? LongestStart { get; set; }
    public DateTime? LongestEnd { get; set; }
    /// <summary>Hours of play in each hour of the day (0 = midnight to 1 AM), over the whole period.</summary>
    public double[] PlayHours { get; set; } = new double[24];
    /// <summary>The games' share of evening and night play (after 9 PM), for the kind of player.</summary>
    public double NightShare { get; set; }

    // Heat
    public RewindHeat? CpuHot { get; set; }
    public RewindHeat? GpuHot { get; set; }
    public RewindHeat? HotSpot { get; set; }

    // Changes
    public string? NewDriver { get; set; }
    public DateTime? NewDriverAt { get; set; }
    public int Installed { get; set; }
    public int Updated { get; set; }
    public int Removed { get; set; }

    // Internet
    public long Down { get; set; }
    public long Up { get; set; }
    public List<RewindItem> TopInternet { get; set; } = [];

    // Problems
    public int AppCrashes { get; set; }
    public int ShutOffs { get; set; }
    /// <summary>App crashes and sudden shut-offs on each day of the period, from its first day.</summary>
    public List<int> CrashesByDay { get; set; } = [];
    public List<int> ShutOffsByDay { get; set; } = [];
}
