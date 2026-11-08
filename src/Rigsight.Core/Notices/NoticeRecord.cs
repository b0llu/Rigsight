namespace Rigsight.Core.Notices;

/// <summary>
/// When a notification may interrupt (the notifications round, 2026-11-07, from the research: interrupt only where missing
/// it costs something). <see cref="Problem"/>: shown at once, even in games, never held back by the daily limit.
/// <see cref="Worth"/>: worth knowing, shown when no game is in front. <see cref="Recap"/>: recaps and news.
/// <see cref="App"/>: RigSight's own (a new version, the overlay's notes): outside the limit, and the overlay's aren't kept.
/// </summary>
public enum NoticeLevel { Problem, Worth, Recap, App }

/// <summary>
/// A notification as the recent list keeps it (30 days). <see cref="Shown"/> false: held back (the daily limit, Do not
/// disturb), so it's only in the list. <see cref="Key"/> is what it was about ("fan-stopped|CPU Fan|2026-11-07"), so the
/// same thing isn't said twice.
/// </summary>
public sealed record NoticeRecord(DateTime Time, string Kind, NoticeLevel Level, string Title, string Body, string? Page, string? Arg,
    string? Icon, string? Key, bool Shown)
{
    public long Id { get; init; }
}
