namespace Rigsight.Core.Stability;

/// <summary>Something Windows logged about the PC's power: it started, it had gone down without shutting down, it went to sleep or woke.</summary>
public enum PowerEventKind
{
    /// <summary>Windows started (after a shutdown, a restart, or going down some other way).</summary>
    Started,
    /// <summary>Logged at a start: the PC had gone down without shutting down (power lost, held power button, a hang).</summary>
    ShutOff,
    /// <summary>Logged at a start: Windows had crashed (a blue screen).</summary>
    Crashed,
    Sleep,
    Wake,
}

public sealed record PowerEvent(DateTime Time, PowerEventKind Kind);

/// <summary>Why a stretch of history has nothing recorded, as far as Windows' own log says.</summary>
public enum GapReason
{
    /// <summary>Windows' log doesn't say (it doesn't reach back that far, or couldn't be read).</summary>
    Unknown,
    /// <summary>The PC was shut down (or restarted) and started again.</summary>
    Off,
    /// <summary>The PC went down without shutting down, and was off until it was started again.</summary>
    ShutOff,
    /// <summary>Windows crashed (a blue screen), and the PC was down until it started again.</summary>
    Crashed,
    Asleep,
    /// <summary>The PC was on the whole time: nothing was recording.</summary>
    NotRunning,
}

public static class PowerLog
{
    /// <summary>
    /// Why nothing was recorded from <paramref name="from"/> to <paramref name="to"/>, from what Windows logged in and
    /// around that time (<paramref name="events"/>, oldest first). A start with "it had gone down without shutting
    /// down" beside it is that; a plain start is the PC having been off; sleep or a wake with no start is sleep; and
    /// with none of those, on a log that reaches back before the gap, the PC ran throughout and nothing was recording.
    /// </summary>
    /// <param name="margin">How far outside the gap an event still belongs to it: the gap's ends are whole minutes (or
    /// hours), and Windows writes "it had gone down" some seconds after the start.</param>
    public static GapReason Reason(DateTime from, DateTime to, IReadOnlyList<PowerEvent>? events, TimeSpan margin)
    {
        if (events is null || events.Count == 0) return GapReason.Unknown;
        var inside = events.Where(e => e.Time >= from - margin && e.Time <= to + margin).ToList();
        if (inside.Any(e => e.Kind == PowerEventKind.Crashed)) return GapReason.Crashed;
        if (inside.Any(e => e.Kind == PowerEventKind.ShutOff)) return GapReason.ShutOff;
        if (inside.Any(e => e.Kind == PowerEventKind.Started)) return GapReason.Off;
        if (inside.Any(e => e.Kind is PowerEventKind.Sleep or PowerEventKind.Wake)) return GapReason.Asleep;
        // Nothing about power in it. That means "on throughout" only if the log goes back past it.
        return events[0].Time < from - margin ? GapReason.NotRunning : GapReason.Unknown;
    }

    /// <summary>The reason in the words the chart shows (null: not known, nothing to add to "Not recorded").</summary>
    public static string? Words(GapReason reason) => reason switch
    {
        GapReason.Off => "PC was off",
        GapReason.ShutOff => "PC shut off unexpectedly",
        GapReason.Crashed => "Windows crashed",
        GapReason.Asleep => "PC was asleep",
        GapReason.NotRunning => "Rigsight wasn't running",
        _ => null,
    };
}
