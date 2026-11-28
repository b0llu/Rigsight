namespace Rigsight.Core.Notices;

/// <summary>
/// One figure against its usual, drawn on a notification card under its words (1.23.0, mock-up 1.23.0 section 9): the
/// reading in big ("83°C"), a scale with the usual as a grey band ("usual 74 to 77°") and an amber stretch from the top of
/// the usual to the reading, and how far above that it is ("6° above"). Only for a notice that says one figure against a
/// usual, from the figures its words already state: hotter than usual (<see cref="NoticeWords.Hotter"/>) and the
/// temperature alert in a game whose usual is known. Temperatures in °C, shown in the unit picked.
/// </summary>
public sealed record NoticeFigure(double Value, double Low, double High)
{
    /// <summary>
    /// The figure for a reading over its usual, or null where it isn't above the top of it as shown (each side rounded,
    /// as the words round them): a reading inside its usual has no stretch to draw.
    /// </summary>
    public static NoticeFigure? Above(double value, double low, double high)
    {
        if (!double.IsFinite(value) || !double.IsFinite(low) || !double.IsFinite(high)) return null;
        if (low > high) (low, high) = (high, low);
        return Shown(value) > Shown(high) ? new NoticeFigure(value, low, high) : null;
    }

    /// <summary>A temperature as the card shows it, in the unit picked, rounded.</summary>
    private static double Shown(double celsius) => Math.Round(Units.Fahrenheit ? celsius * 9 / 5 + 32 : celsius);

    /// <summary>The reading, with its unit: "83°C".</summary>
    public string ValueText => NoticeWords.Temp(Value);

    /// <summary>
    /// Under the band: "usual 74 to 77°C" (WORDING: a usual that is a spread), or "usual 75°C" when both ends show the same.
    /// With its unit, since a notification is read away from the app (WORDING: a temperature in a notification).
    /// </summary>
    public string UsualText => Shown(Low) == Shown(High)
        ? Lang.T($"usual {NoticeWords.Temp(High)}")
        : Lang.T($"usual {Units.TempShort(Low).TrimEnd('°')} to {NoticeWords.Temp(High)}");

    /// <summary>How far the reading is past the top of its usual, as shown, with its unit: "6°C above".</summary>
    public string AboveText => Lang.T($"{Units.ShownDiff(Value, High)}{(Units.Fahrenheit ? "F" : "C")} above");

    /// <summary>
    /// Where the scale starts and ends, in °C: the usual a little left of the middle and the reading about three quarters
    /// along, as the mock-up draws it (74 to 77 and 83 on a scale from 60 to 90).
    /// </summary>
    public (double Min, double Max) Scale
    {
        get
        {
            double reach = Math.Max(Value - Low, 3);
            return (Low - reach * 1.55, Value + reach * 0.78);
        }
    }

    /// <summary>Where <paramref name="celsius"/> falls along the scale, from 0 (its start) to 1 (its end).</summary>
    public double At(double celsius)
    {
        var (min, max) = Scale;
        return Math.Clamp((celsius - min) / (max - min), 0, 1);
    }
}
