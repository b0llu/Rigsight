namespace Rigsight.Core;

/// <summary>
/// What a wireless device says about charging (1.22.0): only a device that says it is ever shown as charging or full.
/// <see cref="Unknown"/> where it can't say (Bluetooth, Xbox controllers): nothing is shown for it, no bolt and no question mark.
/// </summary>
public enum ChargeState { Unknown, NotCharging, Charging, Full }

/// <summary>
/// A device's charging, from its own word to what every place that shows its battery draws: a bolt after the number
/// while it charges, "Full" in grey after 100% when it says it's full on power, nothing where it can't say. The number's
/// colour keeps following the level (green, amber at 15% or less, red at 5% or less), charging or not.
/// </summary>
public static class DeviceCharge
{
    /// <summary>
    /// A Logitech device's Unified Battery state (0 discharging, 1 charging, 2 charging slowly, 3 full on power, 4 a
    /// charging fault) and whether outside power is there.
    /// </summary>
    public static ChargeState FromUnified(byte state, byte power) => state switch
    {
        3 => ChargeState.Full,
        1 or 2 => ChargeState.Charging,
        _ when power != 0 => ChargeState.Charging,
        _ => ChargeState.NotCharging,
    };

    /// <summary>A Logitech device's older Battery Status state (0 discharging, 1 charging, 2 almost full, 3 full, 4 charging slowly, 5-6 faults).</summary>
    public static ChargeState FromStatus(byte state) => state switch
    {
        3 => ChargeState.Full,
        1 or 2 or 4 => ChargeState.Charging,
        _ => ChargeState.NotCharging,
    };

    /// <summary>The state as it travels to the app with each tick ("charging", "full"); null when there's nothing to show.</summary>
    public static string? Wire(ChargeState state) => state switch
    {
        ChargeState.Charging => "charging",
        ChargeState.Full => "full",
        _ => null,
    };

    /// <summary>The state back from <see cref="Wire"/>: anything else is nothing to show.</summary>
    public static ChargeState FromWire(string? word) => word switch
    {
        "charging" => ChargeState.Charging,
        "full" => ChargeState.Full,
        _ => ChargeState.Unknown,
    };

    /// <summary>What goes after the number in a list or a group's header.</summary>
    public enum Mark { None, Bolt, Full }

    /// <summary>
    /// After the number in All sensors and its group's header: a bolt while charging; "Full" when the device says it's
    /// full and the number says 100% (a device that says full a little under 100% is still topping up: a bolt); nothing
    /// otherwise. A device that gives only a level says "Full" already, so nothing more goes after it.
    /// </summary>
    public static Mark MarkOf(ChargeState state, double? value) => state switch
    {
        ChargeState.Charging => Mark.Bolt,
        ChargeState.Full when value is double v && v < 0 => Math.Round(-v) >= 4 ? Mark.None : Mark.Bolt,
        ChargeState.Full when value is >= 99.5 => Mark.Full,
        ChargeState.Full => Mark.Bolt,
        _ => Mark.None,
    };

    /// <summary>On a taskbar tile or a widget, where "Full" is too long: the bolt while charging and while full on power.</summary>
    public static bool TileBolt(ChargeState state) => state is ChargeState.Charging or ChargeState.Full;

    /// <summary>
    /// What a hover says: "Charging, 45% now" while charging, "Full, 100%" when full on power; null when the device
    /// can't say or isn't charging (the reading alone is shown).
    /// </summary>
    public static string? Tip(ChargeState state, double? value)
    {
        if (state is not (ChargeState.Charging or ChargeState.Full)) return null;
        string reading = Units.Short(SensorKind.Battery, value);
        // Full a little under 100% shows a bolt, and says so as charging; a level-only device's "Full" is its own word.
        if (MarkOf(state, value) == Mark.Bolt) return Lang.T($"Charging, {reading} now");
        return value is double v && v < 0 ? reading : Lang.T($"Full, {reading}");
    }

    /// <summary>How far back <see cref="Trend"/> looks.</summary>
    public const int TrendMinutes = 10;

    /// <summary>
    /// What a device's battery did lately, for its card on All sensors (1.22.0): "Steady", "Up 3% in 10m" or
    /// "Down 2% in 10m", over the last 10 minutes or as much of them as there is since a break (the device or the PC was
    /// off). A 1% wobble back and forth is Steady: a level read as whole percents flips between two. Null with under a
    /// minute to go on, or for a device that gives a level word ("Medium") rather than a number.
    /// </summary>
    /// <param name="readings">The battery's readings, oldest first; NaN marks a break.</param>
    public static string? Trend(IEnumerable<(long At, double Value)> readings, long now)
    {
        long from = now - TrendMinutes * 60_000L;
        var points = new List<(long At, int Value)>();
        foreach (var (at, value) in readings)
        {
            if (at < from) continue;
            if (double.IsNaN(value)) { points.Clear(); continue; }
            if (value < 0) return null; // a level word
            points.Add((at, (int)Math.Round(value)));
        }
        if (points.Count < 2 || now - points[0].At < 60_000) return null;
        int delta = points[^1].Value - points[0].Value;
        if (delta == 0) return Lang.T("Steady");
        if (Math.Abs(delta) == 1)
        {
            // Did it ever step the other way? Then it's the reading flipping between two, not a change.
            for (int i = 1; i < points.Count; i++)
                if (Math.Sign(points[i].Value - points[i - 1].Value) == -Math.Sign(delta)) return Lang.T("Steady");
        }
        int minutes = (int)Math.Clamp(Math.Round((now - points[0].At) / 60_000.0), 1, TrendMinutes);
        string span = Units.Duration(minutes * 60);
        int by = Math.Abs(delta);
        return delta > 0 ? Lang.T($"Up {by}% in {span}") : Lang.T($"Down {by}% in {span}");
    }
}
