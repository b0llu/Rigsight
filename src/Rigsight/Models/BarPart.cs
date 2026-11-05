using Rigsight.Core;

namespace Rigsight.Models;

/// <summary>A piece of a <see cref="Controls.PartsBar"/>: how much of the whole it is, and its colour (a theme brush's name, or a colour written out).</summary>
public interface IBarPart
{
    double Weight { get; }
    string Brush { get; }
}

/// <summary>
/// A kind of app's share of a period's time in use, on the Reports page ("Games 4h 09m"): its row in Where the time went
/// (issue #9 round, mock-up FRqgRz6oR4PjbazDL4wt3x option A, 2026-11-05) with its share of the time, its bar against the
/// biggest kind's, the apps that made it up, and the kinds it covers ("Everything else" is several).
/// </summary>
public sealed record TimePart(string Name, double Seconds, string Brush) : IBarPart
{
    public double Weight => Seconds;
    public string Text => Units.Duration(Seconds);
    public IReadOnlyList<Core.Settings.AppCategory> Kinds { get; init; } = [];
    /// <summary>Its share of all the time in use (0 to 1), and its length against the biggest kind's (0 to 1).</summary>
    public double Share { get; init; }
    public double OfLargest { get; init; }
    public string ShareText => $"{Math.Round(Share * 100):0}%";
    /// <summary>The apps that made it up, the biggest first ("Windows Terminal 2h 40m · Claude Code 1h 55m · 9 more").</summary>
    public string Apps { get; init; } = "";

    public bool Equals(TimePart? other) => other is not null && Name == other.Name && Seconds == other.Seconds && Brush == other.Brush && Apps == other.Apps
        && Share == other.Share && OfLargest == other.OfLargest && Kinds.SequenceEqual(other.Kinds);
    public override int GetHashCode() => HashCode.Combine(Name, Seconds, Brush, Apps);
}

/// <summary>A row of Where the time went: its kind, and whether another kind is picked (it steps back then).</summary>
public sealed record TimeRow(TimePart Part, bool Dim, bool Picked);
