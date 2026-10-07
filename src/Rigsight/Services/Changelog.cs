using CommunityToolkit.Mvvm.ComponentModel;

namespace Rigsight.Services;

/// <summary>What one version brought, in plain words: new things, things that work better, and fixes.</summary>
public sealed record ReleaseNotes(Version Version, DateOnly Date, string[] New, string[] Better, string[] Fixed)
{
    /// <summary>"v0.6.1": the version as the card heads it.</summary>
    public string Short => $"v{Version.Major}.{Version.Minor}.{Version.Build}";
    public string DateText => Date.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The non-empty groups, in order, for the What's new card.</summary>
    /// <summary>The release's headline feature, shown big at the top of its What's new (most releases have none).</summary>
    public ReleaseFeature? Feature { get; init; }

    public IReadOnlyList<ReleaseSection> Sections =>
    [
        .. new[] { new ReleaseSection("NEW", "new", New), new ReleaseSection("BETTER", "better", Better), new ReleaseSection("FIXED", "fixed", Fixed) }
            .Where(s => s.Items.Length > 0),
    ];
}

public sealed record ReleaseSection(string Title, string Kind, string[] Items);

/// <summary>
/// A release's headline feature, for the top of its What's new: its name, one line on what it is, a few things it can
/// do, and the page that opens it, so people see there is something new and go and try it.
/// </summary>
/// <param name="icon">An icon-font character, or "fan" / "network" for the drawn ones (as in the sidebar).</param>
/// <param name="page">The page's navigation key ("timeline"), or "ask" for Riggy's chat, which opens over whatever page is showing.</param>
public sealed partial class ReleaseFeature(string name, string pitch, string[] points, string page, string icon, bool isPage = true) : ObservableObject
{
    public string Name { get; } = name;
    public string Pitch { get; } = pitch;
    public string[] Points { get; } = points;
    public string Page { get; } = page;
    public string Icon { get; } = icon;

    /// <summary>A page of its own ("NEW PAGE"), or something new on a page there already was ("NEW FEATURE").</summary>
    public string Tag { get; init; } = isPage ? "NEW PAGE" : "NEW FEATURE";

    /// <summary>The button names the page it opens, which for a feature isn't always the feature's own name.</summary>
    public string OpenText { get; init; } = $"Open {name}";

    /// <summary>Something true about this PC from the feature's own data ("31 changes already found…"), once known.</summary>
    [ObservableProperty] private string? _fact;
}

/// <summary>
/// Every version's "What's new", newest first, written for the people using Rigsight (not the release notes): one short
/// headline per change, enough to make them go and look or to know it's fixed. No examples, menu paths or internals. A new version gets an entry here before it ships (a test
/// checks); one with nothing a person would notice gets none, and then no What's new either.
/// </summary>
public static class Changelog
{
    private static ReleaseNotes V(string version, int month, int day, string[]? added = null, string[]? better = null, string[]? fixedBugs = null,
        ReleaseFeature? feature = null) =>
        new(Version.Parse(version), new DateOnly(2026, month, day), added ?? [], better ?? [], fixedBugs ?? []) { Feature = feature };

    public static IReadOnlyList<ReleaseNotes> Releases { get; } =
    [
        V("0.17.0", 10, 7,
            added: ["Week, month and year on the temperature graph: pick any one."],
            better: ["New GPU line colours: green, amber and red now only mean good, warm and hot.",
                "The GPU hot spot and memory lines show the average, like the CPU and GPU lines."]),
        V("0.16.1", 10, 6,
            better: ["Hover the temperature graph for each minute's average, highest and lowest."],
            fixedBugs: ["Temperature spikes no longer shift around as the graph moves."]),
        V("0.16.0", 10, 6,
            feature: new("Riggy", "A helper in the corner you can ask about your PC.",
                ["Ask why it crashed, how hot it got, what changed or where your time went.",
                    "Answers come from your PC's own records. Nothing you type leaves it.",
                    "Experimental and still learning: tell it when it reads you wrong.",
                    "Not for you? Turn Riggy off in Settings."],
                "ask", "\uE8BD", isPage: false) { Tag = "EXPERIMENTAL", OpenText = "Open Riggy" },
            added: ["The licences of what Rigsight is built with, in Settings."]),
        V("0.15.5", 10, 6,
            better: ["Rigsight says so when its background agent is another version, and restarts it.",
                "The Today and Now playing widgets use less CPU."],
            fixedBugs: ["Custom ranges over three months add up the same in every part of the report.",
                "A graphics driver install is no longer mistaken for the card being removed.",
                "The frame rate no longer freezes on screen when RivaTuner is closed."]),
        V("0.15.4", 10, 6,
            better: ["Small charts show the reading under your pointer.",
                "The Crashes tile on a dashboard always covers the last 30 days.",
                "Safer installing of RivaTuner and the sensor driver."],
            fixedBugs: ["Pages left open overnight move on to the new day.",
                "A game session is kept when the PC crashes or loses power mid-game.",
                "Settings can no longer reset themselves after a bad shutdown.",
                "A setting changed just before closing the window is kept.",
                "The tray icon comes back after the taskbar restarts.",
                "Comparisons leave out a week or month that was only partly recorded.",
                "Fan history goes back further than three months.",
                "Plugging in a USB drive is no longer listed as a hardware change.",
                "Reports no longer go blank after a time zone change.",
                "Time asleep isn't counted as apps being open."]),
        V("0.15.3", 10, 5,
            fixedBugs: ["Network use no longer stops being recorded without a sign."]),
        V("0.15.2", 10, 5,
            better: ["Group the Timeline by month to see each month in one row.",
                "When you change a fan's speed setting, the Fans page shows the new setting on its own."],
            fixedBugs: ["Scrolling past the end of a list carries on down the page."]),
        V("0.15.1", 10, 5,
            better: ["Group a month's app updates into one line on the Timeline.",
                "Home shows the last day you used your PC when yesterday was a day off.",
                "A ~ marks Timeline times that are when a change was noticed, not the exact moment."],
            fixedBugs: ["The Timeline list always loads, also when you come back to the page.",
                "Every line on the Timeline says when it happened."]),
        V("0.15.0", 10, 5,
            feature: new("Timeline", "Everything that changed on your PC, day by day.",
                ["See when a driver, a Windows update, an app or a setting changed.",
                    "Find out if your PC ran hotter or less stable after a change.",
                    "Jump to any day, or show just one kind of change."],
                "timeline", "\uE81C"),
            better: ["Crashes also show hardware and BIOS changes from the week before."]),
        V("0.14.4", 10, 2,
            better: ["The support button in Settings is easier to spot."]),
        V("0.14.3", 10, 2,
            added: ["A way to support Rigsight, in Settings."]),
        V("0.14.2", 10, 1,
            better: ["Hover the live network chart and the connection strip for the details.",
                "Long app lists on Network and Reports scroll inside their own box."],
            fixedBugs: ["A shutdown that didn't finish no longer shows as a power loss while asleep."]),
        V("0.14.1", 10, 1,
            fixedBugs: ["Every app on the Network page shows its proper name.",
                "An app left open but never used no longer reads as used for 0s.",
                "Widget pictures on the Widgets page always refresh."]),
        V("0.14.0", 10, 1,
            feature: new("Network", "What each app downloads and uploads.",
                ["By day, week, month or year, with what ran in the background or while you were away.",
                    "Your biggest downloads, your top speed, and when the internet dropped.",
                    "A heads-up when downloads get slower, or an app uploads far more than usual."],
                "network", "network"),
            better: ["A tidier storage map: the smallest folders sit together in one block."],
            fixedBugs: ["Every word on the taskbar strip is as clear as the part's name.",
                "Storage scans count what files really take, never more than your drive."]),
        V("0.13.2", 9, 30,
            fixedBugs: ["The Fans list only scrolls when you have more fans than fit.",
                "Scrollbars no longer squeeze the Fans and Apps lists."]),
        V("0.13.1", 9, 30,
            better: ["A neater Apps page: pick a category from a list next to the sort."],
            fixedBugs: ["Dropdown lists react anywhere on a row, not just over the text."]),
        V("0.13.0", 9, 30,
            added: ["Dashboard presets, made to fit your PC's sensors.",
                "Accent colours, or your Windows accent.",
                "Filter the Apps page by category.",
                "Place the overlay freely, to the pixel, even off the screen."],
            better: ["A tidier sidebar: fold its sections, hide or reorder pages.",
                "Widgets, overlay and taskbar together under On screen.",
                "Settings sorted into clearer sections."]),
        V("0.12.1", 9, 30,
            added: ["Fans: pick a day, week, month or year, and the whole page follows.",
                "A year view: see if a fan got slower over the months."],
            better: ["Hover the fan charts to see each reading.", "Six facts for each fan, and a list that scrolls when you have many.",
                "A new fan icon."]),
        V("0.12.0", 9, 30,
            feature: new("Fans", "How your fans are doing, and what makes them spin.",
                ["Every fan's speed through the day, and the app or heat behind it.",
                    "A heads-up when a fan turns slower than it used to at the same heat."],
                "fans", "fan"),
            added: ["Temperatures at rest, next to your usual, on the Temperatures page."],
            better: ["Peaks say what you were doing: playing, watching or working."],
            fixedBugs: ["A tidier menu on the Apps page."]),
        V("0.11.3", 9, 29,
            better: ["Apps opens on today, like Home."],
            fixedBugs: ["Today's temperature peaks now match on every page.", "Clearer wording for which app was working hardest at a peak."]),
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
            feature: new("Taskbar readings", "Live readings right in the taskbar, beside the clock.",
                ["Temperatures, load and more, always in view.",
                    "Each part in its own colour: CPU, GPU, memory."],
                "taskbar", "\uE75B", isPage: false) { OpenText = "Open Taskbar" },
            better:
            [
                "Taskbar icons are colour-coded by part and stay where you drag them.",
                "Temperature peaks name the app doing the work.",
            ]),
        V("0.8.0", 9, 28,
            feature: new("Your own widgets", "Build widgets from any reading, or change the built-in ones.",
                ["Pick the readings, the size and the look.",
                    "An FPS widget, and new Grey and Grayscale looks."],
                "widgets", "\uF246", isPage: false) { OpenText = "Open Widgets" },
            added: ["Place the in-game overlay anywhere on the screen."],
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
