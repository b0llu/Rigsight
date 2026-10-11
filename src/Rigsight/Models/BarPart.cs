using Rigsight.Core;

namespace Rigsight.Models;

/// <summary>A piece of a <see cref="Controls.PartsBar"/>: how much of the whole it is, and its colour (a theme brush's name, or a colour written out).</summary>
public interface IBarPart
{
    double Weight { get; }
    string Brush { get; }
}

/// <summary>A kind of app's share of a period's time in use, on the Reports page ("Games 4h 09m").</summary>
public sealed record TimePart(string Name, double Seconds, string Brush) : IBarPart
{
    public double Weight => Seconds;
    public string Text => Units.Duration(Seconds);
}
