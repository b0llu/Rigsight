using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core;

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
        .. new[] { new ReleaseSection(Lang.T("NEW"), "new", New), new ReleaseSection(Lang.T("BETTER"), "better", Better), new ReleaseSection(Lang.T("FIXED"), "fixed", Fixed) }
            .Where(s => s.Items.Length > 0),
    ];
}

public sealed record ReleaseSection(string Title, string Kind, string[] Items);

/// <summary>
/// A release's headline feature, for the top of its What's new: its name, one line on what it is, a few things it can
/// do, and the page that opens it, so people see there is something new and go and try it.
/// </summary>
/// <param name="icon">An icon-font character, or "fan" / "network" for the drawn ones (as in the sidebar).</param>
/// <param name="page">The page's navigation key ("timeline").</param>
public sealed partial class ReleaseFeature(string name, string pitch, string[] points, string page, string icon, bool isPage = true) : ObservableObject
{
    public string Name { get; } = name;
    public string Pitch { get; } = pitch;
    public string[] Points { get; } = points;
    public string Page { get; } = page;
    public string Icon { get; } = icon;

    /// <summary>
    /// A page of its own ("NEW PAGE"), something new on a page there already was ("NEW FEATURE"), or "REMOVED": set in
    /// English, shown in the person's language.
    /// </summary>
    public string Tag
    {
        get => _tag switch
        {
            "NEW PAGE" => Lang.T("NEW PAGE"),
            "NEW FEATURE" => Lang.T("NEW FEATURE"),
            "REMOVED" => Lang.T("REMOVED"),
            _ => _tag,
        };
        init => _tag = value;
    }

    private readonly string _tag = isPage ? "NEW PAGE" : "NEW FEATURE";

    /// <summary>The card is this page's own arrival (so what the page already holds for this PC belongs on it).</summary>
    public bool IsPage { get; } = isPage;

    /// <summary>
    /// The card says something was taken away, with why. It's drawn plain and grey, with no button: the colours of the
    /// other cards say "something new is here", which this isn't.
    /// </summary>
    public bool IsRemoval => _tag == "REMOVED";

    /// <summary>The button names the page it opens, which for a feature isn't always the feature's own name.</summary>
    public string OpenText { get; init; } = $"Open {name}";

    /// <summary>
    /// Whether the card has a button. Not for something taken away, and not for something that has no page to try it
    /// on: a change in how Rigsight runs (on a standard account, say) is told, not opened.
    /// </summary>
    public bool HasButton { get; init; } = true;

    /// <summary>The button is drawn.</summary>
    public bool ShowsButton => HasButton && !IsRemoval;

    /// <summary>Something true about this PC from the feature's own data ("31 changes already found…"), once known.</summary>
    [ObservableProperty] private string? _fact;
}

/// <summary>
/// Every version's "What's new", newest first, written for the people using Rigsight (not the release notes, and not
/// a list of everything that changed in the code). A new version gets an entry here before it ships (a test checks);
/// one with nothing a person would notice gets none, and then no What's new either.
/// <para>How a line is written:</para>
/// <para>- It says what changed, in one natural sentence that makes sense on its own: what the person will see, can
/// now do, or no longer runs into. "Memory points out apps whose memory use has kept growing through the day", not
/// "Better memory insights"; no "its usual" or "the ones above it" that only the line before explains.</para>
/// <para>- It uses the app's own words: the pages by their names (Home, Reports, Memory, Processes, Temperatures,
/// Network, Timeline…), CPU, GPU and RAM, "app" for what the person runs and "process" for Windows' own unit; a
/// reading by what it is (highest, lowest or average; energy is not power; GPU hot spot is not GPU memory).</para>
/// <para>- It claims only what the change does: no "always" or "never" the fix doesn't earn, no speed or safety
/// claim that wasn't measured or checked.</para>
/// <para>- It is something the person would care about after updating: a new ability, a real improvement, a problem
/// they could have met, or a real gain in speed, reliability, compatibility or privacy. Not tests, refactors, tools,
/// logging that helps nobody outside, or spacing and wording tweaks that change nothing about how the app is read
/// or used. Several small changes behind one benefit are one line.</para>
/// <para>A headline feature's card: a short name, one sentence on what it is for, two to four things it lets the
/// person do (each a different one, none repeating the pitch), and the page its button opens.</para>
/// </summary>
public static class Changelog
{
    private static ReleaseNotes V(string version, int month, int day, string[]? added = null, string[]? better = null, string[]? fixedBugs = null,
        ReleaseFeature? feature = null) =>
        new(Version.Parse(version), new DateOnly(2026, month, day), added ?? [], better ?? [], fixedBugs ?? []) { Feature = feature };

    public static IReadOnlyList<ReleaseNotes> Releases { get; } =
    [
        // Version and date settled at release.
        // This one is about another language, so it's said in it too (older entries stay as they were written).
        V("0.30.0", 10, 19,
            feature: new(Lang.T("RigSight in Spanish"), Lang.T("RigSight can now run fully in Spanish, downloaded only if you pick it."),
                [Lang.T("Pick Español in Settings, General, Language: RigSight downloads it and opens again in Spanish."),
                    Lang.T("Every page, notification, widget and the overlay are in Spanish, and so is your history from before the switch."),
                    Lang.T("Each update brings the newest Spanish with it, so you never download it again by hand.")],
                "settings", "\uE774") { OpenText = Lang.T("Open Settings") },
            added: [Lang.T("In Spanish, dates read the Spanish way and spreadsheet exports come out in Spanish, headings and all.")]),
        V("0.29.1", 10, 19,
            added: ["Search the Games library by a game's name or its launcher, right above the covers."],
            better: ["With only one play recorded, How it runs on the Games page charts that play minute by minute instead of staying empty.",
                "Hovering How it runs shows the average and the 1% low each in a box of its own, next to its line.",
                "A game stays shown as running on the Games page while you're in another window, with Play and uninstall off until it closes."],
            fixedBugs: ["The Games page now follows the accent colour you pick in Settings, from the Play button to the FPS chart.",
                "A game whose folder was deleted or only half uninstalled no longer shows as installed with its old size."]),
        V("0.29.0", 10, 18,
            feature: new("Games", "Every game on your PC in one library, from any launcher, with how it runs on your PC.",
                ["See each game's frame rate and 1% low for every play, and whether its last update or a driver changed them.",
                    "Find out what holds a game back: your graphics card at full load, or a frame cap or the CPU.",
                    "Every play with its temperatures, power and how it ended, and the driver and game updates between plays.",
                    "Play, open the game's folder or uninstall it through its own launcher, and see installed games you haven't played lately."],
                "games", "") { OpenText = "Open Games" },
            better: ["Games from Epic, Riot, GOG, Ubisoft, EA and Battle.net are recognised wherever they're installed, so Rocket League in its own folder counts as a game.",
                "Anti-cheat starters, launchers' own helpers and Wallpaper Engine no longer count as games in Reports and on Home.",
                "The Timeline shows when a game got an update, and a game running hotter than usual on Home opens that game."],
            added: ["If the history file is ever damaged, by a power cut for example, RigSight repairs it, keeps everything that can still be read and says which hours couldn't be.",
                "Settings → History can check the history file and repair it, and keeps the damaged copy until you delete it."],
            fixedBugs: ["A widget dragged onto the taskbar stays over it, even after a game has been open.",
                "Scanning a folder nested hundreds of levels deep on the Storage page no longer closes RigSight.",
                "The crash close-up draws even when several apps opened just before the crash.",
                "The background agent keeps running when one of its start-up or closing steps fails, instead of stopping."]),
        V("0.28.1", 10, 18,
            better: ["A startup program that leaves and comes straight back, as Edge does when you sign in, shows as one line saying so.",
                "When the power went off just after you started a game, the minutes before it say the GPU warmed because the game was loading, not as a warning."]),
        V("0.28.0", 10, 18,
            feature: new("Normal for this PC", "RigSight now knows what is normal for your PC and says when something really isn't.",
                ["Compares each game's temperatures with your last two months at the same power, so it works weeks after setup, not months.",
                    "Weighs blue screens, driver resets and sudden shut-offs against your usual, with likely power cuts counted apart.",
                    "Puts your usual beside each week and month on Reports, coloured only where something moved."],
                "reports", "", isPage: false) { OpenText = "Open Reports" },
            added: ["The Timeline shows when your motherboard fans came back from a restart at another speed, such as after a change in the BIOS.",
                "One app crashing far more than it usually does gets a line of its own, apart from the PC's own problems."],
            better: ["Every line on Home is one short statement, and only a line with more to see opens a page.",
                "Saying a game runs hotter now gives the temperature at rest that rules the room out, instead of \"not because of the room\".",
                "The problems tile on Reports counts the PC's own problems, with app crashes and shut-offs that look like the power said under it."],
            fixedBugs: ["Nothing behind the Updating RigSight screen shows through it, such as the sidebar's scroll bar or an open Settings menu, and its logo is sharp.",
                "A startup program that leaves and comes straight back, as Edge does when you sign in, no longer shows on Home and the Timeline as two changes."]),
        V("0.27.1", 10, 17,
            added: ["On PCs with two graphics adapters, the Timeline says when a GPU disconnected and came back within 10 minutes, outside a driver install."],
            better: ["Zooming into a stretch of a long Temperatures range, such as the last 7 days, shows it in finer detail on its own scale.",
                "The Timeline shows app installs, updates and startup changes at their exact time, or the minute they were found, instead of a ~ time.",
                "A GPU is said to be removed only after 10 minutes missing, and the Timeline dates it to the minute it went missing."],
            fixedBugs: ["A Custom range on Temperatures longer than about a day shows its first hours instead of starting partway in.",
                "The list of readings in a widget's search box is as wide as the box above it."]),
        V("0.27.0", 10, 17,
            feature: new("What changed?", "See when a driver, Windows update, BIOS or power plan change made your PC better or worse.",
                ["Compares the same game at the same load before and since, with the warmth of the room taken out.",
                    "Counts only the problems a change can cause, such as graphics driver resets after a graphics driver.",
                    "Open a change to see every reading, each day on a graph, how sure it is, and the crashes involved.",
                    "Says nothing about a change that made no difference you could measure."],
                "timeline", "\uE81C", isPage: false) { OpenText = "Open Timeline" },
            added: ["The Temperatures graph has one range menu, from the last 5 minutes to the last 12 months, and a Custom range.",
                "Widgets can show today's screen time, PC on time, most used app and CPU and GPU peaks, the date, this session and the app in front."],
            better: ["Adding a reading to a widget is one search box: type to find any reading or sensor, narrow it by part, and a click adds it.",
                "Widget text is sharper, drawn the way Windows draws its own on any widget with a background of 60% or more.",
                "With Update automatically off, RigSight no longer looks for a new version by itself; Check for updates still does when you press it.",
                "An app's Time, Hardware and Internet cards sit side by side at an ordinary window size, instead of one under another.",
                "Week, Month and Year on the Temperatures graph are the last 7 days, 30 days and 12 months, so no empty days lie ahead.",
                "The app is now written RigSight, with a capital S, everywhere its name is shown."],
            fixedBugs: ["A widget moved to a layout that shows fewer readings, and back again, keeps every reading it had.",
                "A crash opened from Home, Reports or the Timeline goes back to that page, without another page flashing up first.",
                "A Timeline line about several days' problems opens all of those days on Crashes, not just the last one.",
                "The Timeline no longer credits a graphics driver update with fewer sudden shut-offs, which a graphics driver doesn't cause."]),
        V("0.26.0", 10, 16,
            added: ["Widgets stay over windowed games, and over borderless ones on screens that can show them without turning off G-Sync.",
                "Drag across a graph on Reports, Temperatures or Fans to zoom into that stretch, and one button zooms back out.",
                "Reports' minute-by-minute graph can show each minute's average, highest or lowest.",
                "Apps can be sorted by most internet use, and an app's card shows what it downloaded and uploaded.",
                "Temperatures shows drives in a card of their own, each against its own safe limit."],
            better: ["A crash's cause reads Confirmed, Likely, Possibly or Not enough to say, the same in the list and when opened.",
                "Crash notifications say what happened in the title, such as \"Dota 2 crashed\" or \"Chrome froze\".",
                "Network's biggest days add up each whole day with the apps that used it, and its connection card covers a week, month or year.",
                "Temperatures groups the motherboard's sensors by the chip they are read from.",
                "Storage shows a loader while it looks for space you could free, instead of an empty card.",
                "The menu beside each sensor in All sensors shows an icon for every place it can be added."],
            fixedBugs: ["An app no longer shows up twice in the Memory and Processes lists after switching pages.",
                "Home's yesterday card shows one loader while it loads, instead of two."]),
        V("0.25.0", 10, 15,
            feature: new("Crash close-ups", "An opened crash shows the seconds around it and what came after, and graphics driver resets are counted.",
                ["An app or game crash keeps its last five minutes and the two after, a reading every 2 to 3 seconds.",
                    "Every opened crash shows the 10 minutes after it, such as the app opening again or the PC coming back on.",
                    "Graphics driver resets that Windows records as hang reports are listed and counted, each opening up close.",
                    "A shut-off that came right after the graphics card hung says so, with the second it happened."],
                "crashes", "", isPage: false) { OpenText = "Open Crashes" },
            better: ["The top level of how sure a crash's cause is now reads Confirmed, for what Windows' own records say.",
                "An opened game crash points out when graphics memory was well above that game's usual on this PC.",
                "Copy report and the crashes export include what stood out in a crash's last minutes.",
                "\"What they have in common\" says plainly when a graphics driver update didn't cause the shut-offs."]),
        V("0.24.1", 10, 14,
            added: ["Pressing Update shows an \"Updating RigSight\" screen with a progress bar until the new version opens, instead of the window just disappearing."],
            better: ["The update button says Update instead of Restart, and the sidebar line says \"Update ready\".",
                "Lines on an opened crash for apps that opened before it are labelled, and the readings box names the app for that minute.",
                "Right after an update, the Crashes page waits for Windows' records instead of showing shut-offs as serious for a few seconds.",
                "On a standard account, an opened crash says why the CPU's temperature and power and the motherboard's fans aren't shown.",
                "Minutes with nothing recorded are left empty on an opened crash, instead of looking like readings at zero.",
                "\"What they have in common\" counts unfinished shutdowns apart, so it matches the shut-offs tile."]),
        V("0.24.0", 10, 14,
            feature: new("Crash causes", "Every crash now says what most likely caused it, how sure that is, and what it rests on.",
                ["Open a crash to see what points to the cause, what it wasn't, and what is still unknown.",
                    "The 45 minutes before a crash show temperatures, load, power, memory, fans, drives and network, with the apps doing the work.",
                    "Point at any minute to see every reading from it, including each app's share of the CPU, GPU and memory.",
                    "Copy a crash as a report for a forum post, or search the web for it."],
                "crashes", "\uE7BA", isPage: false) { OpenText = "Open Crashes" },
            added: ["Crash notifications are on and say what most likely happened, and an app that keeps crashing is announced once.",
                "Reports lists the period's crashes with their likely cause, and each one opens straight into its details.",
                "Each minute now records drive activity, GPU memory in use, memory promised to apps and battery charge."],
            better: ["Home, Reports, the Timeline and exports give the same cause for a crash as the Crashes page.",
                "The Crashes page leads with the most serious problem of the period and says which shut-offs were likely power cuts.",
                "Today and yesterday are called that across the app, instead of \"that day\".",
                "Dropdowns and right-click menus take the colours of your theme and accent."],
            fixedBugs: ["Storage's list no longer comes up empty after switching from Largest files and going back a folder.",
                "Emptying the Recycle Bin from Storage no longer freezes the window while a drive is being scanned.",
                "While another drive is scanned, Storage shows a loader instead of the last drive's folders."]),
        V("0.23.0", 10, 13,
            feature: new("Standard accounts", "RigSight runs on a standard Windows account by itself, without asking for an admin password.",
                ["Each person on a PC has their own history, saved in their own Windows account.",
                    "Setup can install RigSight for everyone on the PC and keep it updated for them.",
                    "Settings lists the readings that need admin rights, and each page shows what it can read instead."],
                "settings", "\uEA18", isPage: false) { HasButton = false },
            added: ["On a PC that keeps RigSight updated for everyone, updates install as RigSight starts, and Settings has Update now for anyone who doesn't want to wait."],
            better: ["On a PC with several admin accounts, RigSight starts for each of them without a prompt once each has accepted it.",
                "After an update, RigSight comes back only for people who had it running."]),
        V("0.22.0", 10, 12,
            feature: new("Themes", "Give the whole window a colour of its own, with new themes and an accent that reaches every page.",
                ["Three new themes, Midnight, Forest and Ember, beside Dark, Grey and Light.",
                    "A picked accent colours the charts, bars and icons on every page, not only the buttons.",
                    "Choose whether temperatures keep green, amber and red or take the accent too.",
                    "Warnings stay amber and red in every theme, so a problem still stands out."],
                "settings", "\uE790", isPage: false) { OpenText = "Open Settings" },
            added: ["Settings is split into tabs, so each group of settings is one click away instead of a long scroll.",
                "Settings stays at the foot of the sidebar and opens a menu with updates, What's new, bug reports and logs."],
            better: ["An accent you picked earlier now colours the whole app, temperatures included, and Appearance in Settings can bring green, amber and red back.",
                "Small labels such as LOAD and POWER are a lighter grey, so they are easier to read on every page.",
                "Drive bars on Storage and the bars on Memory are one flat colour instead of a blue-to-purple fade.",
                "Home shows a loader while its recap card reads another day, week or month.",
                "Storage uses much less memory and CPU time while it adds up the space you could free.",
                "The CPU, GPU and RAM readout under the sidebar is removed, as Home and Temperatures already show those readings."]),
        V("0.21.1", 10, 11,
            better: ["App icons are saved between runs, so Home and Reports no longer wait to read them each time RigSight opens.",
                "Reports shows a loader while it reads another day, week or month, instead of leaving the old figures on screen.",
                "Reports lists every highlight and every peak, including voltages and CPU power, without extra buttons.",
                "Home's cards show a loader until their data has arrived."],
            fixedBugs: ["A Steam game is listed on the Timeline as installed once its download has finished, not when it starts.",
                "A game whose program is named after its publisher's launcher appears under the game's own name.",
                "Home no longer says there is no history, or that nothing has changed, while it is still loading."]),
        V("0.21.0", 10, 11,
            feature: new("Export", "Save your PC's history as files you can read, open in a spreadsheet or share.",
                ["Nine tables, including app usage, temperatures, fans, internet use, memory and crashes.",
                    "A report for any period, and a text snapshot of this PC.",
                    "A full copy of the history, which also works as a backup.",
                    "Choose text laid out for reading, or CSV files for a spreadsheet."],
                "settings", "\uE896", isPage: false) { OpenText = "Open Settings" },
            added: ["Reports shows how your time was split between kinds of apps, such as games and browsing.",
                "Reports adds internet use, time in games and the energy used by the CPU and graphics card.",
                "Reports lists what changed on the PC during the period.",
                "Home adds two cards: recent changes on the PC, and PC health covering crashes, drives and fans.",
                "Temperatures shows the warmest hours of the past week, hour by hour.",
                "Opening an app on Processes shows how its memory is split between tabs, extensions and the app itself."],
            better: ["Figures on Reports are compared with your usual or with the previous period.",
                "The apps list on Reports shows each app's internet use.",
                "Highlight icons on Home and Reports are coloured by what each line is about.",
                "Home's recap card shows as many apps and highlights as fit, instead of a fixed few.",
                "Home shows how much has been downloaded today.",
                "Temperatures charts each part's resting temperature over the last 30 days.",
                "Motherboard and drive temperatures appear as tiles directly under the temperature graph.",
                "Opening an app on Processes shows its recorded history right away, without an extra click.",
                "Games whose file name runs words together, like \"Grounded2\", are shown with proper spacing.",
                "The Overlay and Taskbar pages are centred in a wide window instead of sitting at the left edge."],
            fixedBugs: ["Temperature and fan highlights name the game you were playing instead of its launcher.",
                "\"Worth a look\" notes on Memory no longer flicker when an app's memory hovers around the threshold.",
                "Flat chart lines in the Processes tiles no longer spill past the tile's rounded corners.",
                "Drive space changes on the Timeline say how much was added and name the drive in plain words."]),
        V("0.20.0", 10, 10,
            feature: new("Processes", "See everything running on your PC right now, in one sortable list.",
                ["Memory, CPU, internet and disk use for every app, sorted by the column you choose.",
                    "Open an app to see its processes and its recorded history.",
                    "End an app or a single process, with a warning before ending a part of Windows."],
                "processes", ""),
            added: ["Edit the sidebar from Settings: drag pages to reorder them, rename them or move them between groups.",
                "Sidebar group headings can be renamed or switched off."],
            better: ["Memory lists each app's current memory use beside its usual amount, and marks apps well above their usual.",
                "Memory points out apps whose memory use has kept growing through the day.",
                "The memory gauge turns amber, then red, as RAM fills up.",
                "The temperature graph's legend shows the average, highest or lowest reading on every time range.",
                "The lowest GPU hot spot and GPU memory temperatures are recorded from this version on."],
            fixedBugs: ["The live list of running apps on Memory no longer falls out of order as readings change.",
                "The temperature legend shows a week's lowest readings instead of dashes."]),
        V("0.19.3", 10, 10,
            better: ["Settings tells you when the driver needed for CPU temperature and power readings is missing or not running.",
                "A missing sensor driver can be installed from Settings.",
                "Copied logs list which sensors aren't being read, and why."]),
        V("0.19.2", 10, 9,
            better: ["Sensor readings and the list of running apps appear right after the window opens.",
                "Areas that are still loading show a loader instead of an empty space."],
            fixedBugs: ["History pages no longer say nothing was recorded while they are still loading.",
                "After a loader disappears, the window stops redrawing right away instead of staying busy for a few seconds."]),
        V("0.19.1", 10, 9,
            better: ["An unexpected shutdown is timed by the last minute RigSight recorded, not the earlier time Windows reports.",
                "Past shutdowns are moved to that time too, and show what was running just before.",
                "Temperatures shows each part's average, highest or lowest reading for the selected day, week, month or year.",
                "Hovering a red mark on the temperature graph explains why it is there.",
                "The temperature graph's hover box lists the hottest part first.",
                "All sensors fits a small window by dropping columns instead of cutting them off.",
                "In a large maximised window, pages keep a readable width and charts grow taller."],
            fixedBugs: ["Readings on Temperatures and Memory are no longer cut off in a small window.",
                "Content on Crashes, Reports, Apps, Timeline, Network and Widgets fits better in a small window.",
                "Report a bug now only opens the form; the separate Copy button copies the logs.",
                "The Right now pane on All sensors no longer extends past the bottom of the window.",
                "Scrollbars on All sensors no longer cover the readings.",
                "The menu button at the end of a sensor's row is no longer cut off."]),
        V("0.19.0", 10, 9,
            added: ["Network now focuses on usage: which apps used your internet, hour by hour.",
                "Click a bar on the Network chart to see which apps used the internet in that hour.",
                "Sort apps on Network by total, download, upload or background use.",
                "Dashboards can show internet use, with six new tiles and a Network template.",
                "All sensors points out what stands out right now: the warmest parts, the hardest-working parts and the fastest fans.",
                "Add any sensor to the taskbar, the overlay, a widget or a dashboard from its row on All sensors.",
                "Each part on All sensors shows its key readings in its header, whether folded or open.",
                "Report a bug from Settings: RigSight copies the logs and opens a form."],
            better: ["Live download and upload speeds on Network are a single line that expands when clicked.",
                "Biggest hours on Network count all internet use, including uploads and calls, not only downloads.",
                "Network marks hours it couldn't record instead of showing them as no usage.",
                "Each temperature on All sensors is coloured according to its own part's limits.",
                "Search on All sensors matches words in any order.",
                "Drive health explains why a drive is marked Caution."],
            fixedBugs: ["An internet drop that ends while the PC is asleep or off is no longer lost.",
                "Restarting RigSight no longer loses part of that minute's internet use.",
                "The last 15 minutes of an internet drop no longer show as online.",
                "Hidden sensors are no longer offered for widgets, dashboards or the overlay.",
                "An SSD reporting almost no remaining life is no longer shown in green."]),
        V("0.18.1", 10, 8,
            better: ["Returning to Network, Memory, Reports or Timeline no longer freezes the window for a moment.",
                "The Timeline dates a change found right after startup from when the PC came on, not from the night before."],
            fixedBugs: ["Check for updates no longer downloads an update that was only being offered."]),
        V("0.18.0", 10, 8,
            feature: new("Riggy is gone", "It looked like an AI assistant, but it wasn't one.",
                ["Its answers were written in advance for about 17 kinds of question, not generated.",
                    "A real AI would need a download of about 1 GB, or your PC's history sent to a server.",
                    "RigSight never sends your data anywhere, and a cloud AI would break that promise.",
                    "With Riggy removed, the download is 20 MB smaller and three pages gain a search box."],
                "timeline", "\uE711", isPage: false) { Tag = "REMOVED", OpenText = "Open Timeline" },
            added: ["Search boxes on Memory, Timeline and Crashes.",
                "The temperature graph can be drawn from each minute's average, highest or lowest reading.",
                "A Refresh button on Timeline."],
            better: ["The temperature graph marks gaps where nothing was recorded and says why: the PC was off, asleep or had crashed.",
                "Actions that take a moment, such as scans and checks, show a loader while they run.",
                "The live list on Memory keeps a usable height in a small window.",
                "An app update appears on the Timeline when the app starts running the new version, not when it was downloaded."],
            fixedBugs: ["Clear all history reports that it is done only once the history has actually been cleared."]),
        V("0.17.1", 10, 8,
            added: ["The Timeline records your Windows power plan and every time it changes.",
                "2XL and 3XL sizes for widgets and the overlay."],
            better: ["A drive scan shows its progress on the drive's card.",
                "Crashes notes when a graphics driver was being installed at the time of a crash.",
                "The sidebar no longer shows a warning while the background agent is still starting."],
            fixedBugs: ["RigSight keeps running while a graphics driver is being updated.",
                "The sidebar's scrollbar no longer overlaps the menu."]),
        V("0.17.0", 10, 7,
            added: ["The temperature graph can show a week, a month or a year."],
            better: ["GPU lines on the temperature graph have new colours, so green, amber and red only mean good, warm and hot.",
                "The GPU hot spot and GPU memory temperature lines show averages, like the CPU and GPU lines."]),
        V("0.16.1", 10, 6,
            better: ["Hover the temperature graph to see each minute's average, highest and lowest reading."],
            fixedBugs: ["Temperature spikes no longer shift position as the graph scrolls."]),
        V("0.16.0", 10, 6,
            added: ["Riggy, an experimental helper you could ask about your PC (removed in 0.18.0).",
                "Settings lists the licences of the software RigSight is built with."]),
        V("0.15.5", 10, 6,
            better: ["RigSight tells you when its background agent is a different version, and restarts it.",
                "The Today and Now playing widgets use less CPU."],
            fixedBugs: ["Custom ranges longer than three months show the same totals in every part of the report.",
                "Installing a graphics driver is no longer mistaken for the graphics card being removed.",
                "The frame rate reading no longer stays frozen on screen after RivaTuner is closed."]),
        V("0.15.4", 10, 6,
            better: ["Small charts show the reading under your pointer.",
                "The Crashes tile on a dashboard always covers the last 30 days.",
                "RigSight installs RivaTuner and the sensor driver only through Windows' own App Installer."],
            fixedBugs: ["Pages left open overnight switch to the new day at midnight.",
                "A game session is kept if the PC crashes or loses power mid-game.",
                "If a bad shutdown damages the settings file, RigSight restores the previous save instead of resetting to defaults.",
                "A setting changed just before closing the window is kept.",
                "The tray icon returns after the Windows taskbar restarts.",
                "Comparisons skip a week or month that was only partly recorded.",
                "Fan history goes back further than three months.",
                "Plugging in a USB drive is no longer listed as a hardware change.",
                "Reports no longer go blank after a time zone change.",
                "Time the PC spent asleep is no longer counted as time an app was open."]),
        V("0.15.3", 10, 5,
            fixedBugs: ["Network recording no longer stops silently: RigSight notices when it stalls and restarts it."]),
        V("0.15.2", 10, 5,
            better: ["Grouping the Timeline by month shows one row per month, with a line for each kind of change.",
                "When a fan's speed setting changes, Fans notes the change and compares the fan with its speeds since then."],
            fixedBugs: ["Scrolling past the end of a list continues scrolling the page."]),
        V("0.15.1", 10, 5,
            better: ["App updates on the Timeline can be grouped by day or by month.",
                "Home shows the last day you used your PC when it wasn't used yesterday.",
                "A ~ before a Timeline time means the change was noticed then, not that it happened at that exact moment."],
            fixedBugs: ["The Timeline list fills completely, including when you return to the page.",
                "Every line on the Timeline says when it happened."]),
        V("0.15.0", 10, 5,
            feature: new("Timeline", "Everything that changed on your PC, day by day.",
                ["See when a driver, a Windows update, an app or a setting changed.",
                    "Find out whether your PC ran hotter or less stable after a change.",
                    "Jump to any day, or show only one kind of change."],
                "timeline", "\uE81C"),
            better: ["Crashes also lists hardware and BIOS changes from the week before a crash."]),
        V("0.14.4", 10, 2,
            better: ["The support button in Settings is easier to find."]),
        V("0.14.3", 10, 2,
            added: ["Settings has a way to support RigSight."]),
        V("0.14.2", 10, 1,
            better: ["Hover the live network chart or the connection strip to see details.",
                "Long app lists on Network and Reports scroll inside their own box."],
            fixedBugs: ["A shutdown that didn't finish is no longer shown as a power loss while asleep."]),
        V("0.14.1", 10, 1,
            fixedBugs: ["Apps on Network that showed a file's description, such as curl, now show their proper name.",
                "An app that was open but never used no longer shows as used for 0s.",
                "Widget previews on the Widgets page no longer occasionally fail to refresh."]),
        V("0.14.0", 10, 1,
            feature: new("Network", "See what each app downloads and uploads.",
                ["View usage by day, week, month or year, including what ran in the background or while you were away.",
                    "See your biggest downloads, your top speed and when the internet dropped.",
                    "Get a heads-up when downloads slow down or an app uploads far more than usual."],
                "network", "network"),
            better: ["The storage map groups the smallest folders into one block."],
            fixedBugs: ["All text on the taskbar strip is bold, so values are as easy to read as the part names.",
                "Storage scans count the space files actually take on disk, so totals no longer exceed the drive's size."]),
        V("0.13.2", 9, 30,
            fixedBugs: ["The Fans list scrolls only when there are more fans than fit.",
                "Scrollbars no longer squeeze the Fans and Apps lists."]),
        V("0.13.1", 9, 30,
            better: ["Apps has a category list next to the sort control."],
            fixedBugs: ["Dropdown rows respond to clicks anywhere on the row, not just on the text."]),
        V("0.13.0", 9, 30,
            added: ["Dashboard presets, built from the sensors your PC has.",
                "Accent colours, including your Windows accent colour.",
                "Filter Apps by category.",
                "Place the overlay anywhere, to the pixel, even off the screen."],
            better: ["The sidebar's sections can be folded, and pages can be hidden or reordered.",
                "Widgets, Overlay and Taskbar are grouped under On screen in the sidebar.",
                "Settings are grouped into clearer sections."]),
        V("0.12.1", 9, 30,
            added: ["Fans has a day, week, month or year picker, and the whole page follows it.",
                "A year view on Fans shows whether a fan has slowed down over the months."],
            better: ["Hover the fan charts to see each reading.",
                "Each fan shows six facts, and the fan list scrolls when there are many."]),
        V("0.12.0", 9, 30,
            feature: new("Fans", "How your fans are doing, and what makes them spin.",
                ["Every fan's speed through the day, and the app or heat behind it.",
                    "A heads-up when a fan runs slower than it used to at the same temperature."],
                "fans", "fan"),
            added: ["Temperatures shows how warm each part is at rest, next to its usual resting temperature."],
            better: ["Temperature peaks say what you were doing at the time: playing, watching or working."]),
        V("0.11.3", 9, 29,
            better: ["Apps opens on today's usage, like Home."],
            fixedBugs: ["Today's temperature peaks now match on every page.",
                "Peak lines say which app used the CPU or GPU most, instead of calling it the busiest."]),
        V("0.11.2", 9, 29,
            better: ["Pages open instantly again, without the fade animation.",
                "Crashes opens without stuttering, even with a long history."]),
        V("0.11.1", 9, 29,
            added: ["Animations across the app: pages, buttons, switches and the sidebar."]),
        V("0.11.0", 9, 29,
            added: ["Taskbar readings can be shown in grayscale."],
            better:
            [
                "Highlights raise fewer false alarms: heat is only compared for the same game at a steady load.",
                "A warning when a fan stops while it should be spinning.",
                "RigSight's background agent uses about a quarter less memory.",
            ],
            fixedBugs:
            [
                "Fan speeds are recorded correctly while the RigSight window is closed.",
                "Late-night gaming sessions count toward the correct day.",
            ]),
        V("0.10.1", 9, 28,
            fixedBugs: ["Highlights about a crash name its likely cause instead of repeating their own title."]),
        V("0.10.0", 9, 28,
            added:
            [
                "Home has recaps for last week, last month and last year.",
                "Highlights say which app caused the heat, and compare how your PC runs now with months ago.",
                "Records and streaks: your longest session, hottest peak and busiest day.",
            ],
            better:
            [
                "Days are compared with your usual for the same weekday.",
                "Highlights leave out what isn't news. Hover one to see the numbers behind it.",
            ]),
        V("0.9.0", 9, 28,
            feature: new("Taskbar readings", "Live readings right in the taskbar, beside the clock.",
                ["Temperatures, load and more, always in view.",
                    "Each part has its own colour: CPU, GPU and memory."],
                "taskbar", "\uE75B", isPage: false) { OpenText = "Open Taskbar" },
            better:
            [
                "Tray icons are colour-coded by part and stay where you drag them.",
                "Temperature peaks name the app that was doing the work.",
            ]),
        V("0.8.0", 9, 28,
            feature: new("Your own widgets", "Build widgets from any reading, or change the built-in ones.",
                ["Pick the readings, the size and the look.",
                    "An FPS widget, and new Grey and Grayscale looks."],
                "widgets", "\uF246", isPage: false) { OpenText = "Open Widgets" },
            added: ["Place the in-game overlay anywhere on the screen."],
            better:
            [
                "Widgets step aside while you play, so they don't add lag.",
                "Notifications match the Grey theme.",
            ],
            fixedBugs: ["Games whose program has a generic file name are shown under their real name."]),
        V("0.7.1", 9, 27,
            added: ["A Grey theme, easier on the eyes on OLED screens."]),
        V("0.7.0", 9, 27,
            added:
            [
                "Live readings can be shown in your taskbar.",
                "This card, which shows what's new after each update.",
            ],
            better: ["Readings appear seconds sooner after your PC starts."]),
        V("0.6.3", 9, 27,
            better:
            [
                "Storage has a Refresh button.",
                "A dedicated graphics card is listed before integrated graphics.",
            ],
            fixedBugs:
            [
                "Home's temperature peak names the app that was in front for most of that minute.",
                "Opening a folder no longer makes RigSight think you just signed in.",
            ]),
        V("0.6.2", 9, 27,
            better:
            [
                "The in-game overlay no longer adds lag or turns off G-Sync.",
                "The in-game overlay looks the same whether RigSight or RivaTuner draws it.",
                "The RAM reading in the sidebar is coloured by how full memory is.",
            ]),
        V("0.6.1", 9, 27,
            added: ["Dashboard tiles stay exactly where you put them, gaps included."],
            fixedBugs: ["NVIDIA GPU Bus and GPU Memory readings are separate again."]),
        V("0.6.0", 9, 27,
            added:
            [
                "RigSight works alongside iCUE, Armoury Crate and other RGB and fan apps by not reading the hardware they control.",
                "A problem report you can copy and share when you need help.",
            ],
            better:
            [
                "RigSight recovers on its own if a hardware scan gets stuck.",
                "Settings points out anything that needs your attention.",
            ],
            fixedBugs: ["The message shown when RigSight's background agent doesn't start says whether it never started or isn't answering."]),
    ];

    /// <summary>The entry for exactly this version, if there is one.</summary>
    public static ReleaseNotes? For(Version version) =>
        Releases.FirstOrDefault(r => r.Version == Plain(version));

    /// <summary>What came after <paramref name="seen"/>, up to and including <paramref name="current"/>, newest first.</summary>
    public static IReadOnlyList<ReleaseNotes> Since(Version seen, Version current) =>
        [.. Releases.Where(r => r.Version > Plain(seen) && r.Version <= Plain(current))];

    private static Version Plain(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
