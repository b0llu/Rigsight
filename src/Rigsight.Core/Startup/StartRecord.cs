namespace Rigsight.Core.Startup;

/// <summary>
/// How a start began, from Windows' own record of it (Kernel-Boot event 27): a full start or restart, Fast Startup, or
/// waking from hibernation. Each kind has its own usual, and a start is only ever set against its own kind.
/// </summary>
public enum StartKind
{
    Full = 0,
    Fast = 1,
    Hibernate = 2,
}

/// <summary>
/// One start as RigSight keeps it (the startup table), each part in milliseconds and null where Windows' logs gave none:
/// <see cref="BiosMs"/> before Windows (the firmware's own figure), <see cref="MainMs"/> Windows starting (from Windows' own
/// start to the sign-in screen being ready), <see cref="PostMs"/> getting ready after sign-in (from signing in to the apps
/// that start with Windows having been launched). The time spent at the sign-in screen is in neither.
/// </summary>
/// <param name="Time">When Windows started (its record of the start, to the second).</param>
/// <param name="AfterUpdate">Windows' record says it was a restart to finish installing updates (null: not known).</param>
/// <param name="Read">
/// Windows' logs were looked in for this start, still reached back to it and can't get more for it: a part missing then
/// isn't in them. False while it can still come (minutes after the start) or where the logs no longer reached back.
/// </param>
public sealed record StartRecord(DateTime Time, StartKind Kind, int? BiosMs, int? MainMs, int? PostMs, bool? AfterUpdate, bool Read)
{
    /// <summary>What Windows named as slower than its usual on this start (events 101 to 110), longest first.</summary>
    public IReadOnlyList<SlowItem> Slow { get; init; } = [];

    /// <summary>Each app that started with Windows on this start, in the order Windows launched them (Shell-Core 9707/9708, 62408/62409).</summary>
    public IReadOnlyList<AppLaunch> Apps { get; init; } = [];
}

/// <summary>
/// An app Windows launched as it started (one of the apps that start with Windows), and how long its launch took: from
/// Windows starting it to Windows going on to the next (Shell-Core's own "Started/Finished execution of command" pair).
/// </summary>
/// <param name="Name">Its name as the Start-up page names the apps that start with Windows ("Steam", "Discord").</param>
/// <param name="Exe">The program's file ("steam.exe"), for its icon.</param>
/// <param name="AtMs">When Windows started it, after sign-in.</param>
/// <param name="Ms">How long its launch took.</param>
public sealed record AppLaunch(string Name, string Exe, int AtMs, int Ms)
{
    /// <summary>The command as Windows logged it (the program's file and what follows it), kept for the log and the name.</summary>
    public string? Command { get; init; }
}

/// <summary>
/// Something Windows itself named as slower than its usual on a start (Diagnostics-Performance events 101 to 110): an
/// app, a driver, a service, a device, or a part of Windows' own start. Windows' own figures, in milliseconds.
/// </summary>
/// <param name="Event">Which event: 101 app, 102 driver, 103 service, 109 device, the others a part of Windows' start.</param>
/// <param name="Name">The name Windows gave it (its friendly name where it has one), or "" for a part of Windows' own start.</param>
/// <param name="File">The file Windows named ("Hex.exe", "nvlddmkm.sys"), where it named one.</param>
public sealed record SlowItem(int Event, string Name, string? File, int TotalMs, int SlowerMs);
