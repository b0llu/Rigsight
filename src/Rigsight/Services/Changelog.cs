namespace Rigsight.Services;

/// <summary>What one version brought, in plain words: new things, things that work better, and fixes.</summary>
public sealed record ReleaseNotes(Version Version, DateOnly Date, string[] New, string[] Better, string[] Fixed)
{
    /// <summary>"v0.6.1": the version as the card heads it.</summary>
    public string Short => $"v{Version.Major}.{Version.Minor}.{Version.Build}";
    public string DateText => Date.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The non-empty groups, in order, for the What's new card.</summary>
    public IReadOnlyList<ReleaseSection> Sections =>
    [
        .. new[] { new ReleaseSection("NEW", "new", New), new ReleaseSection("BETTER", "better", Better), new ReleaseSection("FIXED", "fixed", Fixed) }
            .Where(s => s.Items.Length > 0),
    ];
}

public sealed record ReleaseSection(string Title, string Kind, string[] Items);

/// <summary>
/// Every version's "What's new", newest first, written for the people using Rigsight (not the release notes): one short
/// line per change, what it means for them, no internals. A new version gets an entry here before it ships (a test
/// checks); one with nothing a person would notice gets none, and then no What's new either.
/// </summary>
public static class Changelog
{
    private static ReleaseNotes V(string version, int month, int day, string[]? added = null, string[]? better = null, string[]? fixedBugs = null) =>
        new(Version.Parse(version), new DateOnly(2026, month, day), added ?? [], better ?? [], fixedBugs ?? []);

    public static IReadOnlyList<ReleaseNotes> Releases { get; } =
    [
        V("0.7.0", 9, 27,
            added:
            [
                "Readings in your taskbar: put any sensor next to the clock, as its own icon or all in one (Customize → Taskbar).",
                "This card: after an update, a short look at what changed. Every update is in Settings → About.",
            ],
            better:
            [
                "Readings show up about a second after your PC starts, instead of five or six.",
            ]),
        V("0.6.3", 9, 27,
            better:
            [
                "Storage has a Refresh button: delete something, refresh, and see the space come back.",
                "Your graphics card is always shown before your processor's built-in graphics.",
            ],
            fixedBugs:
            [
                "The temperature peak on Home names the app you were actually using.",
                "Opening a folder no longer made Rigsight think you had just signed in.",
            ]),
        V("0.6.2", 9, 27,
            better:
            [
                "The in-game overlay no longer adds lag or turns off G-Sync and FreeSync.",
                "The in-game overlay looks more like Rigsight's own, and a 0% background really is see-through.",
                "RAM in the sidebar is coloured like CPU and GPU.",
            ]),
        V("0.6.1", 9, 27,
            added:
            [
                "Dashboards can have empty spaces: tiles stay exactly where you put them.",
            ],
            fixedBugs:
            [
                "On NVIDIA cards, GPU Bus and GPU Memory are two separate readings again.",
            ]),
        V("0.6.0", 9, 27,
            added:
            [
                "Works alongside RGB and fan apps like iCUE and Armoury Crate, without both reaching for the same hardware.",
                "A problem report in Settings → About, to paste wherever you ask for help.",
            ],
            better:
            [
                "If reading your hardware ever gets stuck, Rigsight recovers on its own next time.",
                "Settings shows a dot, and opens right where something needs a look.",
            ],
            fixedBugs:
            [
                "A clearer message when the background part of Rigsight doesn't start.",
            ]),
    ];

    /// <summary>The entry for exactly this version, if there is one.</summary>
    public static ReleaseNotes? For(Version version) =>
        Releases.FirstOrDefault(r => r.Version == Plain(version));

    /// <summary>What came after <paramref name="seen"/>, up to and including <paramref name="current"/>, newest first.</summary>
    public static IReadOnlyList<ReleaseNotes> Since(Version seen, Version current) =>
        [.. Releases.Where(r => r.Version > Plain(seen) && r.Version <= Plain(current))];

    private static Version Plain(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
