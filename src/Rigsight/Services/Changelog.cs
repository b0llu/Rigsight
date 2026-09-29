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
/// headline per change, enough to make them go and look or to know it's fixed. No examples, menu paths or internals. A new version gets an entry here before it ships (a test
/// checks); one with nothing a person would notice gets none, and then no What's new either.
/// </summary>
public static class Changelog
{
    private static ReleaseNotes V(string version, int month, int day, string[]? added = null, string[]? better = null, string[]? fixedBugs = null) =>
        new(Version.Parse(version), new DateOnly(2026, month, day), added ?? [], better ?? [], fixedBugs ?? []);

    public static IReadOnlyList<ReleaseNotes> Releases { get; } =
    [
        V("0.11.2", 9, 29,
            better: ["Pages open instantly again, without a fade.", "The Crashes page opens without a stutter, even with a long history."]),
        V("0.11.1", 9, 29,
            added: ["Smooth animations across the app: pages, buttons, switches and the sidebar."]),
        V("0.11.0", 9, 29,
            added: ["Taskbar readings in grayscale, for a calmer taskbar."],
            better:
            [
                "Smarter highlights: fewer false alarms, and heat compared like for like.",
                "A warning if a fan stops when it should be spinning.",
                "Rigsight uses less memory in the background.",
            ],
            fixedBugs:
            [
                "Fan speeds are recorded correctly.",
                "Late-night gaming counts toward the right day.",
            ]),
        V("0.10.1", 9, 28,
            fixedBugs: ["Crash highlights read more clearly."]),
        V("0.10.0", 9, 28,
            added:
            [
                "Recaps for last week, last month and last year on Home.",
                "Smarter highlights: who made the heat, and how your PC runs compared with months ago.",
                "Records and streaks: your longest session, hottest peak, busiest day.",
            ],
            better:
            [
                "Days are compared with your usual for that weekday.",
                "Highlights skip what isn't news. Hover one to see the numbers behind it.",
            ]),
        V("0.9.0", 9, 28,
            added: ["Live readings right in the taskbar, beside the clock."],
            better:
            [
                "Taskbar icons are colour-coded by part and stay where you drag them.",
                "Temperature peaks name the app doing the work.",
            ]),
        V("0.8.0", 9, 28,
            added:
            [
                "Place the in-game overlay anywhere on the screen.",
                "Build your own widgets, or change the built-in ones.",
                "An FPS widget, and new Grey and Grayscale looks for widgets.",
            ],
            better:
            [
                "Widgets step aside while you play, so they never add lag.",
                "Notifications match the Grey theme.",
            ],
            fixedBugs: ["Better game name recognition."]),
        V("0.7.1", 9, 27,
            added: ["A Grey theme, easier on the eyes on OLED screens."]),
        V("0.7.0", 9, 27,
            added:
            [
                "Readings in your taskbar.",
                "This card: what's new after each update.",
            ],
            better: ["Readings show up seconds sooner after your PC starts."]),
        V("0.6.3", 9, 27,
            better:
            [
                "Storage has a Refresh button.",
                "Your graphics card is listed before built-in graphics.",
            ],
            fixedBugs:
            [
                "Home's temperature peak names the right app.",
                "Rigsight no longer thinks you just signed in when you open a folder.",
            ]),
        V("0.6.2", 9, 27,
            better:
            [
                "The in-game overlay no longer adds lag or turns off G-Sync.",
                "A cleaner in-game overlay.",
                "RAM gets its own colour in the sidebar.",
            ]),
        V("0.6.1", 9, 27,
            added: ["Dashboard tiles stay exactly where you put them."],
            fixedBugs: ["NVIDIA GPU Bus and GPU Memory readings are separate again."]),
        V("0.6.0", 9, 27,
            added:
            [
                "Works alongside iCUE, Armoury Crate and other RGB apps.",
                "A problem report to share when you need help.",
            ],
            better:
            [
                "Rigsight recovers on its own if reading your hardware gets stuck.",
                "Settings points you to anything that needs a look.",
            ],
            fixedBugs: ["A clearer message when Rigsight's background part doesn't start."]),
    ];

    /// <summary>The entry for exactly this version, if there is one.</summary>
    public static ReleaseNotes? For(Version version) =>
        Releases.FirstOrDefault(r => r.Version == Plain(version));

    /// <summary>What came after <paramref name="seen"/>, up to and including <paramref name="current"/>, newest first.</summary>
    public static IReadOnlyList<ReleaseNotes> Since(Version seen, Version current) =>
        [.. Releases.Where(r => r.Version > Plain(seen) && r.Version <= Plain(current))];

    private static Version Plain(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
