namespace Rigsight.Core.Settings;

/// <summary>
/// A timer tile's arithmetic, shared by the agent (which draws it, runs it and says when a timer is up) and the app's
/// editor: a timer counts down from its length, a stopwatch counts up, a countdown counts the days to a day. Its state
/// is kept on the tile (<see cref="WidgetTile.RunningSince"/>, <see cref="WidgetTile.RunMs"/>), so it carries on
/// through a restart and reads the same in the app and on the desktop.
/// </summary>
public static class WidgetTimer
{
    public static string KindOf(WidgetTile t) => t.TimerKind ?? WidgetTiles.TimerKinds[0];
    public static bool IsStopwatch(WidgetTile t) => KindOf(t) == WidgetTiles.TimerKinds[1];
    public static bool IsCountdown(WidgetTile t) => KindOf(t) == WidgetTiles.TimerKinds[2];
    public static bool IsTimer(WidgetTile t) => !IsStopwatch(t) && !IsCountdown(t);

    public static bool Running(WidgetTile t) => t.RunningSince is not null;

    public static long LengthMs(WidgetTile t) => Math.Max(1, t.Minutes) * 60_000L;

    /// <summary>How long it has run, pauses left out.</summary>
    public static long ElapsedMs(WidgetTile t, long nowMs) => t.RunMs + (t.RunningSince is long since ? Math.Max(0, nowMs - since) : 0);

    /// <summary>A timer's time left.</summary>
    public static long LeftMs(WidgetTile t, long nowMs) => Math.Max(0, LengthMs(t) - ElapsedMs(t, nowMs));

    /// <summary>A timer that has run its whole length.</summary>
    public static bool IsUp(WidgetTile t, long nowMs) => IsTimer(t) && ElapsedMs(t, nowMs) >= LengthMs(t);

    /// <summary>Start, or pause, or (a timer that's up) start again from its length.</summary>
    public static void StartOrPause(WidgetTile t, long nowMs)
    {
        if (Running(t))
        {
            t.RunMs = ElapsedMs(t, nowMs);
            t.RunningSince = null;
            return;
        }
        if (IsUp(t, nowMs)) t.RunMs = 0;
        t.RunningSince = nowMs;
    }

    public static void Reset(WidgetTile t)
    {
        t.RunMs = 0;
        t.RunningSince = null;
    }

    /// <summary>A timer that has just run out: stopped at its length (so it's said once).</summary>
    public static void Stop(WidgetTile t)
    {
        t.RunMs = LengthMs(t);
        t.RunningSince = null;
    }

    /// <summary>
    /// What the tile shows big: a timer's time left (counted up to the next whole second, so it reads 25:00 at the
    /// start and 00:00 only when it's up), a stopwatch's time so far. Hours only when there are any.
    /// </summary>
    public static string Shown(WidgetTile t, long nowMs)
    {
        long ms = IsStopwatch(t) ? ElapsedMs(t, nowMs) : LeftMs(t, nowMs);
        long s = IsStopwatch(t) ? ms / 1000 : (ms + 999) / 1000;
        long h = s / 3600, m = s / 60 % 60, sec = s % 60;
        return h > 0 ? $"{h}:{m:00}:{sec:00}" : $"{m:00}:{sec:00}";
    }

    /// <summary>A countdown's days left to its day (negative once it's passed); none without a day.</summary>
    public static int? DaysLeft(WidgetTile t, DateTime today) => t.Until is { } until ? (until.Date - today.Date).Days : null;
}
