using System.Globalization;
using System.Text.RegularExpressions;
using Rigsight.Core.Settings;

namespace Rigsight.Core.Apps;

/// <summary>Guesses what kind of app an executable is, and gives it a readable fallback name.</summary>
public static class AppCatalog
{
    private static readonly Dictionary<string, AppCategory> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // Browsers
        ["chrome.exe"] = AppCategory.Browser, ["msedge.exe"] = AppCategory.Browser, ["firefox.exe"] = AppCategory.Browser,
        ["brave.exe"] = AppCategory.Browser, ["opera.exe"] = AppCategory.Browser, ["opera_gx.exe"] = AppCategory.Browser,
        ["vivaldi.exe"] = AppCategory.Browser, ["arc.exe"] = AppCategory.Browser, ["zen.exe"] = AppCategory.Browser,
        ["iexplore.exe"] = AppCategory.Browser, ["waterfox.exe"] = AppCategory.Browser, ["librewolf.exe"] = AppCategory.Browser,
        ["floorp.exe"] = AppCategory.Browser, ["thorium.exe"] = AppCategory.Browser,
        // Development
        ["code.exe"] = AppCategory.Development, ["devenv.exe"] = AppCategory.Development, ["rider64.exe"] = AppCategory.Development,
        ["idea64.exe"] = AppCategory.Development, ["pycharm64.exe"] = AppCategory.Development, ["webstorm64.exe"] = AppCategory.Development,
        ["windowsterminal.exe"] = AppCategory.Development, ["wt.exe"] = AppCategory.Development, ["powershell.exe"] = AppCategory.Development,
        ["pwsh.exe"] = AppCategory.Development, ["cmd.exe"] = AppCategory.Development, ["cursor.exe"] = AppCategory.Development,
        ["antigravity.exe"] = AppCategory.Development, ["postman.exe"] = AppCategory.Development, ["docker desktop.exe"] = AppCategory.Development,
        ["github desktop.exe"] = AppCategory.Development, ["sublime_text.exe"] = AppCategory.Development, ["notepad++.exe"] = AppCategory.Development,
        ["claude.exe"] = AppCategory.Development, ["code - insiders.exe"] = AppCategory.Development, ["vscodium.exe"] = AppCategory.Development,
        ["windsurf.exe"] = AppCategory.Development, ["zed.exe"] = AppCategory.Development, ["clion64.exe"] = AppCategory.Development,
        ["goland64.exe"] = AppCategory.Development, ["phpstorm64.exe"] = AppCategory.Development, ["datagrip64.exe"] = AppCategory.Development,
        ["rubymine64.exe"] = AppCategory.Development, ["studio64.exe"] = AppCategory.Development, ["eclipse.exe"] = AppCategory.Development,
        ["openconsole.exe"] = AppCategory.Development, ["git-bash.exe"] = AppCategory.Development, ["mintty.exe"] = AppCategory.Development,
        ["wsl.exe"] = AppCategory.Development, ["gitkraken.exe"] = AppCategory.Development, ["fork.exe"] = AppCategory.Development,
        ["sourcetree.exe"] = AppCategory.Development, ["dbeaver.exe"] = AppCategory.Development, ["ssms.exe"] = AppCategory.Development,
        ["insomnia.exe"] = AppCategory.Development, ["unity hub.exe"] = AppCategory.Development, ["unity.exe"] = AppCategory.Development,
        ["unrealeditor.exe"] = AppCategory.Development, ["putty.exe"] = AppCategory.Development, ["winscp.exe"] = AppCategory.Development,
        ["vbcscompiler.exe"] = AppCategory.Development, ["msbuild.exe"] = AppCategory.Development, ["git.exe"] = AppCategory.Development,
        ["lm studio.exe"] = AppCategory.Development,
        // Communication
        ["discord.exe"] = AppCategory.Communication, ["slack.exe"] = AppCategory.Communication, ["ms-teams.exe"] = AppCategory.Communication,
        ["teams.exe"] = AppCategory.Communication, ["whatsapp.exe"] = AppCategory.Communication, ["telegram.exe"] = AppCategory.Communication,
        ["zoom.exe"] = AppCategory.Communication, ["signal.exe"] = AppCategory.Communication, ["outlook.exe"] = AppCategory.Communication,
        ["olk.exe"] = AppCategory.Communication, ["thunderbird.exe"] = AppCategory.Communication,
        ["discordptb.exe"] = AppCategory.Communication, ["discordcanary.exe"] = AppCategory.Communication, ["vesktop.exe"] = AppCategory.Communication,
        ["skype.exe"] = AppCategory.Communication, ["element.exe"] = AppCategory.Communication, ["viber.exe"] = AppCategory.Communication,
        ["wechat.exe"] = AppCategory.Communication, ["weixin.exe"] = AppCategory.Communication, ["line.exe"] = AppCategory.Communication,
        ["kakaotalk.exe"] = AppCategory.Communication, ["ts3client_win64.exe"] = AppCategory.Communication, ["teamspeak.exe"] = AppCategory.Communication,
        ["mumble.exe"] = AppCategory.Communication, ["guilded.exe"] = AppCategory.Communication, ["mailspring.exe"] = AppCategory.Communication,
        ["messenger.exe"] = AppCategory.Communication, ["whatsapp.root.exe"] = AppCategory.Communication, ["phoneexperiencehost.exe"] = AppCategory.Communication,
        // Media
        ["spotify.exe"] = AppCategory.Media, ["vlc.exe"] = AppCategory.Media, ["obs64.exe"] = AppCategory.Media,
        ["mpc-hc64.exe"] = AppCategory.Media, ["mpv.exe"] = AppCategory.Media, ["stremio.exe"] = AppCategory.Media,
        ["potplayermini64.exe"] = AppCategory.Media, ["music.ui.exe"] = AppCategory.Media, ["netflix.exe"] = AppCategory.Media,
        ["photoshop.exe"] = AppCategory.Media, ["premiere pro.exe"] = AppCategory.Media, ["resolve.exe"] = AppCategory.Media,
        ["blender.exe"] = AppCategory.Media, ["audacity.exe"] = AppCategory.Media,
        ["obs32.exe"] = AppCategory.Media, ["mpc-hc.exe"] = AppCategory.Media, ["mpc-be64.exe"] = AppCategory.Media,
        ["potplayer64.exe"] = AppCategory.Media, ["potplayermini.exe"] = AppCategory.Media, ["foobar2000.exe"] = AppCategory.Media,
        ["aimp.exe"] = AppCategory.Media, ["musicbee.exe"] = AppCategory.Media, ["itunes.exe"] = AppCategory.Media,
        ["applemusic.exe"] = AppCategory.Media, ["tidal.exe"] = AppCategory.Media, ["deezer.exe"] = AppCategory.Media,
        ["plex.exe"] = AppCategory.Media, ["plexamp.exe"] = AppCategory.Media, ["kodi.exe"] = AppCategory.Media,
        ["streamlabs obs.exe"] = AppCategory.Media, ["streamlabs desktop.exe"] = AppCategory.Media, ["medal.exe"] = AppCategory.Media,
        ["krita.exe"] = AppCategory.Media, ["inkscape.exe"] = AppCategory.Media, ["gimp-2.10.exe"] = AppCategory.Media,
        ["gimp-3.0.exe"] = AppCategory.Media, ["afterfx.exe"] = AppCategory.Media, ["adobe premiere pro.exe"] = AppCategory.Media,
        ["illustrator.exe"] = AppCategory.Media, ["lightroom.exe"] = AppCategory.Media, ["audition.exe"] = AppCategory.Media,
        ["capcut.exe"] = AppCategory.Media, ["clipchamp.exe"] = AppCategory.Media, ["shotcut.exe"] = AppCategory.Media,
        ["kdenlive.exe"] = AppCategory.Media, ["handbrake.exe"] = AppCategory.Media, ["fl64.exe"] = AppCategory.Media,
        ["reaper.exe"] = AppCategory.Media, ["paintdotnet.exe"] = AppCategory.Media, ["mspaint.exe"] = AppCategory.Media,
        ["photos.exe"] = AppCategory.Media, ["microsoft.photos.exe"] = AppCategory.Media, ["microsoft.media.player.exe"] = AppCategory.Media,
        ["wmplayer.exe"] = AppCategory.Media, ["video.ui.exe"] = AppCategory.Media, ["windowscamera.exe"] = AppCategory.Media,
        // Game launchers, and the programs they run beside them
        ["steam.exe"] = AppCategory.Launcher, ["steamwebhelper.exe"] = AppCategory.Launcher, ["epicgameslauncher.exe"] = AppCategory.Launcher,
        ["battle.net.exe"] = AppCategory.Launcher, ["eadesktop.exe"] = AppCategory.Launcher, ["upc.exe"] = AppCategory.Launcher,
        ["ubisoftconnect.exe"] = AppCategory.Launcher, ["galaxyclient.exe"] = AppCategory.Launcher, ["riotclientservices.exe"] = AppCategory.Launcher,
        ["xboxpcapp.exe"] = AppCategory.Launcher, ["playnite.desktopapp.exe"] = AppCategory.Launcher, ["rockstarlauncher.exe"] = AppCategory.Launcher,
        ["riot client.exe"] = AppCategory.Launcher, ["riotclientux.exe"] = AppCategory.Launcher, ["eawebhelper.exe"] = AppCategory.Launcher,
        ["epicwebhelper.exe"] = AppCategory.Launcher, ["upcwebcore.exe"] = AppCategory.Launcher, ["uplaywebcore.exe"] = AppCategory.Launcher,
        ["steamservice.exe"] = AppCategory.Launcher, ["gameoverlayui.exe"] = AppCategory.Launcher, ["gameoverlayui64.exe"] = AppCategory.Launcher,
        ["steamerrorreporter.exe"] = AppCategory.Launcher, ["epiconlineservicesuserhelper.exe"] = AppCategory.Launcher,
        ["epiconlineserviceshost.exe"] = AppCategory.Launcher, ["eabackgroundservice.exe"] = AppCategory.Launcher, ["ealocalhostsvc.exe"] = AppCategory.Launcher,
        ["eaconnect_microsoft.exe"] = AppCategory.Launcher, ["eacefsubprocess.exe"] = AppCategory.Launcher, ["link2ea.exe"] = AppCategory.Launcher,
        ["origin.exe"] = AppCategory.Launcher, ["originwebhelperservice.exe"] = AppCategory.Launcher, ["ubisoftgamelauncher.exe"] = AppCategory.Launcher,
        ["ubisoftgamelauncher64.exe"] = AppCategory.Launcher, ["uplayservice.exe"] = AppCategory.Launcher, ["galaxyclient helper.exe"] = AppCategory.Launcher,
        ["gog galaxy notifications renderer.exe"] = AppCategory.Launcher, ["galaxycommunication.exe"] = AppCategory.Launcher,
        ["galaxyclientservice.exe"] = AppCategory.Launcher, ["xboxpcappft.exe"] = AppCategory.Launcher, ["xboxappservices.exe"] = AppCategory.Launcher,
        ["xboxpctray.exe"] = AppCategory.Launcher, ["gamingservices.exe"] = AppCategory.Launcher, ["gamingservicesnet.exe"] = AppCategory.Launcher,
        ["rockstarservice.exe"] = AppCategory.Launcher, ["socialclubhelper.exe"] = AppCategory.Launcher, ["playgtav.exe"] = AppCategory.Launcher,
        ["playnite.fullscreenapp.exe"] = AppCategory.Launcher, ["heroic.exe"] = AppCategory.Launcher, ["itch.exe"] = AppCategory.Launcher,
        ["amazon games ui.exe"] = AppCategory.Launcher, ["wgc.exe"] = AppCategory.Launcher, ["hoyoplay.exe"] = AppCategory.Launcher,
        // Anti-cheat starters sit in a game's folder and open the game; their seconds aren't play.
        ["start_protected_game.exe"] = AppCategory.Launcher, ["eaanticheat.gameservicelauncher.exe"] = AppCategory.Launcher,
        ["easyanticheat_eos_setup.exe"] = AppCategory.Launcher,
        // Installed through Steam like games, but they aren't.
        ["wallpaper32.exe"] = AppCategory.Other, ["wallpaper64.exe"] = AppCategory.Other, ["stremio-shell-ng.exe"] = AppCategory.Media,
        // Productivity
        ["winword.exe"] = AppCategory.Productivity, ["excel.exe"] = AppCategory.Productivity, ["powerpnt.exe"] = AppCategory.Productivity,
        ["onenote.exe"] = AppCategory.Productivity, ["notion.exe"] = AppCategory.Productivity, ["obsidian.exe"] = AppCategory.Productivity,
        ["acrobat.exe"] = AppCategory.Productivity, ["notepad.exe"] = AppCategory.Productivity, ["figma.exe"] = AppCategory.Productivity,
        ["acrord32.exe"] = AppCategory.Productivity, ["sumatrapdf.exe"] = AppCategory.Productivity, ["foxitpdfreader.exe"] = AppCategory.Productivity,
        ["msaccess.exe"] = AppCategory.Productivity, ["mspub.exe"] = AppCategory.Productivity, ["visio.exe"] = AppCategory.Productivity,
        ["soffice.exe"] = AppCategory.Productivity, ["soffice.bin"] = AppCategory.Productivity, ["wps.exe"] = AppCategory.Productivity,
        ["evernote.exe"] = AppCategory.Productivity, ["todoist.exe"] = AppCategory.Productivity, ["joplin.exe"] = AppCategory.Productivity,
        ["logseq.exe"] = AppCategory.Productivity, ["typora.exe"] = AppCategory.Productivity, ["calculatorapp.exe"] = AppCategory.Productivity,
        ["calculator.exe"] = AppCategory.Productivity, ["screenclippinghost.exe"] = AppCategory.Productivity, ["snippingtool.exe"] = AppCategory.Productivity,
        ["stickynotes.exe"] = AppCategory.Productivity, ["todo.exe"] = AppCategory.Productivity, ["miro.exe"] = AppCategory.Productivity,
        ["clickup.exe"] = AppCategory.Productivity, ["linear.exe"] = AppCategory.Productivity,
        // System
        ["explorer.exe"] = AppCategory.System, ["taskmgr.exe"] = AppCategory.System, ["systemsettings.exe"] = AppCategory.System,
        ["applicationframehost.exe"] = AppCategory.System, ["searchhost.exe"] = AppCategory.System, ["startmenuexperiencehost.exe"] = AppCategory.System,
        ["shellexperiencehost.exe"] = AppCategory.System, ["lockapp.exe"] = AppCategory.System, ["mmc.exe"] = AppCategory.System,
        ["control.exe"] = AppCategory.System, ["system"] = AppCategory.System, ["rigsight.exe"] = AppCategory.System, ["rigsight.agent.exe"] = AppCategory.System,
        ["msmpeng.exe"] = AppCategory.System, ["mpdefendercoreservice.exe"] = AppCategory.System, ["securityhealthsystray.exe"] = AppCategory.System,
        ["securityhealthservice.exe"] = AppCategory.System, ["regedit.exe"] = AppCategory.System, ["perfmon.exe"] = AppCategory.System,
        ["resmon.exe"] = AppCategory.System, ["eventvwr.exe"] = AppCategory.System, ["windowspackagemanagerserver.exe"] = AppCategory.System,
        ["textinputhost.exe"] = AppCategory.System, ["shellhost.exe"] = AppCategory.System, ["widgets.exe"] = AppCategory.System,
        ["vmmem"] = AppCategory.System, ["vmmemwsl"] = AppCategory.System,
    };

    private static readonly string[] GameFolders =
    [
        @"\steamapps\common\", @"\epic games\", @"\gog galaxy\games\", @"\riot games\", @"\xboxgames\",
        @"\ea games\", @"\games\", @"\ubisoft game launcher\games\", @"\battle.net\games\", @"\rockstar games\",
        @"\windowsapps\microsoft.minecraft", @"\zenlesszonezero", @"\genshin impact",
    ];

    private static readonly string[] NotGamesInGameFolders = ["unitycrashhandler64.exe", "crashreportclient.exe", "easyanticheat.exe", "setup.exe"];

    // Apps sold on Steam that aren't games, by their folder in steamapps\common: the folder rule would call anything there
    // a game (Wallpaper Engine's ui32.exe showed on Games as "Wallpaper Engine User Interface", 2026-11-28).
    private static readonly (string Folder, AppCategory Category)[] SoftwareInGameFolders =
    [
        (@"\steamapps\common\wallpaper_engine\", AppCategory.Other), (@"\steamapps\common\lossless scaling\", AppCategory.Other),
        (@"\steamapps\common\steamvr\", AppCategory.Other), (@"\steamapps\common\steamworks shared\", AppCategory.Other),
        (@"\steamapps\common\obs studio\", AppCategory.Media), (@"\steamapps\common\blender\", AppCategory.Productivity),
        (@"\steamapps\common\aseprite\", AppCategory.Productivity), (@"\steamapps\common\soundpad\", AppCategory.Media),
        (@"\steamapps\common\3dmark\", AppCategory.Other), (@"\steamapps\common\voicemod\", AppCategory.Media),
    ];

    /// <summary>The category of an app sold on Steam that isn't a game, from its folder; null for anything else.</summary>
    internal static AppCategory? SoftwareOnSteam(string? path)
    {
        if (path is null) return null;
        var p = path.Replace('/', '\\').ToLowerInvariant();
        foreach (var (folder, category) in SoftwareInGameFolders)
            if (p.Contains(folder)) return category;
        return null;
    }

    // Parts of a file name that make it a game's helper, never the game: crash reporters, installers, the runtimes a
    // game installs, its uninstaller, an anti-cheat's service.
    private static readonly string[] HelperParts =
    [
        "crashhandler", "crashreport", "crashpad", "crash_reporter", "crashsender", "bugsplat", "bssndrpt", "setup", "installer",
        "redist", "unins", "dotnetfx", "prereq", "beservice",
    ];

    /// <summary>A game's helper (a crash reporter, an installer, a runtime, an anti-cheat's service), never the game itself.</summary>
    internal static bool IsHelper(string exe)
    {
        if (NotGamesInGameFolders.Contains(exe, StringComparer.OrdinalIgnoreCase)) return true;
        var name = Path.GetFileNameWithoutExtension(exe);
        return HelperParts.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    // The launchers' own programs under a game-library folder name ("Epic Games\Launcher", "Riot Games\Riot Client"):
    // they aren't games, though the folder rule would call them one.
    private static readonly string[] LauncherFolders =
    [
        @"\epic games\launcher\", @"\epic games\epic online services\", @"\riot games\riot client\",
        @"\ubisoft game launcher\", @"\ea desktop\", @"\battle.net\", @"\gog galaxy\",
        @"\rockstar games\launcher\", @"\rockstar games\social club\", @"\origin\", @"\hoyoplay\", @"\amazon games\app\",
        @"\windowsapps\microsoft.gamingapp_", @"\windowsapps\microsoft.gamingservices_", @"\windowsapps\microsoft.xboxapp_",
    ];

    // A launcher's games folder ("Ubisoft Game Launcher\games\", "GOG Galaxy\Games\") holds games, not the launcher.
    private static readonly string[] GamesInLauncherFolders = [@"game launcher\games\", @"gog galaxy\games\", @"battle.net\games\", @"hoyoplay\games\"];

    private static bool InLauncherFolder(string path)
    {
        var p = path.Replace('/', '\\').ToLowerInvariant();
        return LauncherFolders.Any(p.Contains) && !GamesInLauncherFolders.Any(p.Contains);
    }

    // Rockstar's launcher and its helpers have plain names ("Launcher.exe"): only theirs inside Rockstar's folder.
    private static readonly string[] RockstarHelpers = ["launcher.exe", "launcherpatcher.exe", "rockstarservice.exe", "socialclubhelper.exe"];

    /// <summary>
    /// Why this program is a game launcher or one of its helpers, or null when it isn't one. Checked before any store's
    /// listing: a launcher installed beside a game ("Overwatch Launcher.exe", Rockstar's Launcher.exe) is in the game's
    /// folder and would otherwise be taken for the game. By name and by where it sits.
    /// </summary>
    internal static string? LauncherEvidence(string exe, string? path)
    {
        if (IsAntiCheatStarter(exe) && !NotGamesInGameFolders.Contains(exe, StringComparer.OrdinalIgnoreCase)) return "an anti-cheat starter";
        var name = Path.GetFileNameWithoutExtension(exe);
        if (name.StartsWith("RiotClient", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LeagueClient", StringComparison.OrdinalIgnoreCase))
            return "Riot's client";
        // "Overwatch Launcher.exe", "Paradox Launcher.exe": a game's own launcher, named after it (Battle.net's way).
        if (name.EndsWith(" Launcher", StringComparison.OrdinalIgnoreCase)) return "a launcher by its name";
        if (path is null) return null;
        var p = path.Replace('/', '\\').ToLowerInvariant();
        if (p.Contains(@"\rockstar games\") && RockstarHelpers.Contains(exe, StringComparer.OrdinalIgnoreCase)) return "Rockstar's launcher";
        if (InLauncherFolder(path)) return "in a launcher's own folder";
        // "FortniteLauncher.exe", "GTAVLauncher.exe": only in a game's folder, where nothing else would say so (elsewhere
        // "…Launcher.exe" starts all sorts, like PowerToys).
        if ((name.EndsWith("Launcher", StringComparison.OrdinalIgnoreCase) || name.Equals("LauncherPatcher", StringComparison.OrdinalIgnoreCase))
            && (GameFolders.Any(p.Contains) || Games.GameLibrary.Find(InstalledGames, path) is not null))
            return "a launcher by its name, in a game's folder";
        return null;
    }

    /// <summary>
    /// The games the launchers say are installed (see <see cref="Games.GameLibrary"/>), set by whoever scanned them.
    /// A program inside one of their folders is a game wherever that folder is (Rocket League in E:\rocketleague).
    /// </summary>
    public static IReadOnlyList<Games.InstalledGame> InstalledGames { get; set; } = [];

    /// <summary>
    /// Category for an app. <paramref name="overrides"/> (the user's choices) win, then the known list,
    /// then the install folder (Steam/Epic/… libraries mean it's a game). See <see cref="Decide"/>.
    /// </summary>
    public static AppCategory Classify(string exe, string? path, IReadOnlyDictionary<string, AppCategory>? overrides = null) =>
        Decide(exe, path, overrides).Category;

    /// <summary>An app's category and what decided it, for the log.</summary>
    public readonly record struct Verdict(AppCategory Category, string Why);

    /// <summary>
    /// An app's category and the evidence for it, the first that fits deciding: the person's choice; the known list; a
    /// game launcher or its helper (by name and folder, before any store's listing); a store's listing (only the program
    /// the store says starts the game, where it says, and anything in its folder made with a game engine); a game
    /// library folder or engine; then what the program says of itself (its package, its maker and description) and
    /// where it sits. With weak or mixed evidence, Other: a game seen played is learned later (Tracker.LearnGame).
    /// <paramref name="readFile"/> false leaves out the program's own details (no disk reads, for a check of many apps).
    /// </summary>
    public static Verdict Decide(string exe, string? path, IReadOnlyDictionary<string, AppCategory>? overrides = null, bool readFile = true)
    {
        if (overrides is not null && overrides.TryGetValue(exe, out var chosen)) return new(chosen, "the person's choice");
        if (Known.TryGetValue(exe, out var known)) return new(known, "a known app");
        if (LauncherEvidence(exe, path) is { } launcher) return new(AppCategory.Launcher, launcher);
        if (SoftwareOnSteam(path) is { } software) return new(software, "an app on Steam that isn't a game");
        if (path is not null && !IsHelper(exe))
        {
            if (Games.GameLibrary.Find(InstalledGames, path) is { } listed)
            {
                if (listed.LaunchExe is null) return new(AppCategory.Game, $"{listed.Launcher} lists its folder ({listed.Name})");
                if (listed.Starts(path)) return new(AppCategory.Game, $"{listed.Launcher} starts {listed.Name} with it");
                if (EngineOf(exe, path) is { } made) return new(AppCategory.Game, $"made with {made}, in {listed.Name}'s folder");
                // Another program in a listed game's folder: a game only once it's seen played.
                var other = ByWhatItSays(exe, path, readFile);
                return other with { Why = $"in {listed.Name}'s folder but not what {listed.Launcher} starts; {other.Why}" };
            }
            if (GameFolders.Any(path.ToLowerInvariant().Contains)) return new(AppCategory.Game, "in a game library folder");
            if (EngineOf(exe, path) is { } engine) return new(AppCategory.Game, $"made with {engine}");
        }
        return ByWhatItSays(exe, path, readFile);
    }

    // Store apps by their package's family name (…\WindowsApps\Microsoft.WindowsCalculator_11.2405…\CalculatorApp.exe).
    private static readonly (string Family, AppCategory Category)[] Packages =
    [
        ("Microsoft.WindowsCalculator", AppCategory.Productivity), ("Microsoft.WindowsNotepad", AppCategory.Productivity),
        ("Microsoft.ScreenSketch", AppCategory.Productivity), ("Microsoft.MicrosoftStickyNotes", AppCategory.Productivity),
        ("Microsoft.Todos", AppCategory.Productivity), ("Microsoft.WindowsAlarms", AppCategory.Productivity), ("Microsoft.Office", AppCategory.Productivity),
        ("Microsoft.Windows.Photos", AppCategory.Media), ("Microsoft.ZuneMusic", AppCategory.Media), ("Microsoft.ZuneVideo", AppCategory.Media),
        ("Microsoft.Paint", AppCategory.Media), ("Microsoft.WindowsCamera", AppCategory.Media), ("Microsoft.WindowsSoundRecorder", AppCategory.Media),
        ("Clipchamp.Clipchamp", AppCategory.Media), ("SpotifyAB.SpotifyMusic", AppCategory.Media), ("4DF9E0F8.Netflix", AppCategory.Media),
        ("AppleInc.AppleMusic", AppCategory.Media), ("AppleInc.AppleTV", AppCategory.Media), ("Microsoft.Windows.DevHome", AppCategory.Development),
        ("Microsoft.WindowsTerminal", AppCategory.Development), ("Microsoft.PowerShell", AppCategory.Development),
        ("MSTeams", AppCategory.Communication), ("Microsoft.OutlookForWindows", AppCategory.Communication), ("5319275A.WhatsAppDesktop", AppCategory.Communication),
        ("Microsoft.YourPhone", AppCategory.Communication), ("Microsoft.SkypeApp", AppCategory.Communication), ("TelegramMessengerLLP.TelegramDesktop", AppCategory.Communication),
        ("Microsoft.MicrosoftEdge", AppCategory.Browser), ("Microsoft.MinecraftUWP", AppCategory.Game), ("Microsoft.MicrosoftSolitaireCollection", AppCategory.Game),
        ("Microsoft.WindowsStore", AppCategory.System), ("Microsoft.Windows.", AppCategory.System), ("MicrosoftWindows.", AppCategory.System),
        ("Microsoft.XboxGamingOverlay", AppCategory.System), ("Microsoft.WindowsFeedbackHub", AppCategory.System), ("Microsoft.GetHelp", AppCategory.System),
    ];

    /// <summary>The package's family name for a Store app ("Microsoft.WindowsCalculator"), from its folder; null for any other program.</summary>
    internal static string? PackageFamily(string? path)
    {
        if (path is null) return null;
        int i = path.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        int start = i + @"\WindowsApps\".Length, end = path.IndexOf('\\', start);
        if (end <= start) return null;
        var folder = path[start..end];
        int cut = folder.IndexOf('_');
        return cut > 0 ? folder[..cut] : folder;
    }

    // What a program's maker, product or description says it is: one kind of evidence each, and only one kind may come
    // out of it all (Adobe makes both Photoshop and Acrobat: the product decides there).
    private static readonly (string Text, AppCategory Category)[] Makers =
    [
        ("JetBrains", AppCategory.Development), ("GitHub", AppCategory.Development), ("Docker Inc", AppCategory.Development),
        ("Postman", AppCategory.Development), ("Sublime HQ", AppCategory.Development), ("Anysphere", AppCategory.Development),
        ("Python Software Foundation", AppCategory.Development), ("Git Development Community", AppCategory.Development),
        ("Unity Technologies", AppCategory.Development), ("Discord", AppCategory.Communication), ("Slack Technologies", AppCategory.Communication),
        ("Zoom Video Communications", AppCategory.Communication), ("Zoom Communications", AppCategory.Communication),
        ("Telegram", AppCategory.Communication), ("WhatsApp", AppCategory.Communication), ("Signal Messenger", AppCategory.Communication),
        ("TeamSpeak", AppCategory.Communication), ("Viber Media", AppCategory.Communication), ("Spotify", AppCategory.Media),
        ("VideoLAN", AppCategory.Media), ("OBS Project", AppCategory.Media), ("Blackmagic Design", AppCategory.Media),
        ("Blender Foundation", AppCategory.Media), ("Audacity", AppCategory.Media), ("Plex", AppCategory.Media), ("Streamlabs", AppCategory.Media),
        ("Image-Line", AppCategory.Media), ("Cockos", AppCategory.Media), ("Steinberg", AppCategory.Media), ("Ableton", AppCategory.Media),
        ("Brave Software", AppCategory.Browser), ("Vivaldi Technologies", AppCategory.Browser), ("Opera Norway", AppCategory.Browser),
        ("The Browser Company", AppCategory.Browser), ("Notion Labs", AppCategory.Productivity), ("Obsidian", AppCategory.Productivity),
        ("Evernote", AppCategory.Productivity), ("Doist", AppCategory.Productivity), ("The Document Foundation", AppCategory.Productivity),
        ("Foxit", AppCategory.Productivity),
    ];

    private static readonly (string Text, AppCategory Category)[] Products =
    [
        ("Google Chrome", AppCategory.Browser), ("Firefox", AppCategory.Browser), ("Waterfox", AppCategory.Browser), ("LibreWolf", AppCategory.Browser),
        ("Yandex Browser", AppCategory.Browser), ("Photoshop", AppCategory.Media), ("Premiere", AppCategory.Media), ("After Effects", AppCategory.Media),
        ("Lightroom", AppCategory.Media), ("Illustrator", AppCategory.Media), ("Audition", AppCategory.Media), ("Media Encoder", AppCategory.Media),
        ("Acrobat", AppCategory.Productivity), ("Microsoft Office", AppCategory.Productivity), ("LibreOffice", AppCategory.Productivity),
        ("Visual Studio", AppCategory.Development), ("Android Studio", AppCategory.Development),
    ];

    private static readonly (System.Text.RegularExpressions.Regex Words, AppCategory Category)[] Descriptions =
    [
        (Words("web browser", "internet browser"), AppCategory.Browser),
        (Words("media player", "video player", "music player", "audio player", "video editor", "photo editor", "image editor", "audio editor",
            "screen recorder", "sound recorder", "voice recorder", "image viewer", "photo viewer", "streaming software"), AppCategory.Media),
        (Words("messenger", "email client", "e-mail client", "mail client", "video conferencing", "video calls", "voice chat", "chat client"), AppCategory.Communication),
        (Words("integrated development environment", "code editor", "source code editor", "terminal", "debugger", "compiler", "version control"), AppCategory.Development),
        (Words("pdf reader", "pdf editor", "pdf viewer", "word processor", "spreadsheet", "note-taking", "calculator", "office suite", "to-do"), AppCategory.Productivity),
    ];

    private static System.Text.RegularExpressions.Regex Words(params string[] words) =>
        new($@"\b({string.Join("|", words.Select(System.Text.RegularExpressions.Regex.Escape))})\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The category from what the program says of itself: its package, then (outside Windows' folder) its maker,
    /// product and description; a program in Windows' folder is a part of Windows when Microsoft made it (or nothing
    /// says otherwise). Mixed or no evidence: Other.
    /// </summary>
    private static Verdict ByWhatItSays(string exe, string? path, bool readFile)
    {
        if (path is null) return new(AppCategory.Other, "nothing to go on");
        if (PackageFamily(path) is { } family)
            foreach (var (prefix, category) in Packages)
                if (family.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return new(category, $"the Store package {family}");
        var facts = readFile ? FactsOf(path) : null;
        if (InWindowsFolder(path))
        {
            // Microsoft's own, or a file whose maker can't be read (as before): a part of Windows. Another maker's file
            // that only sits there (a driver's helper in System32) is not.
            if (facts?.Company is not { Length: > 0 } company) return new(AppCategory.System, "in Windows' folder");
            if (company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return new(AppCategory.System, "Microsoft's, in Windows' folder");
        }
        if (facts is null) return new(AppCategory.Other, readFile ? "its details can't be read" : "its details weren't read");
        return FromFacts(facts);
    }

    /// <summary>The category from a program's maker, product and description alone: one kind, or Other when none or several.</summary>
    internal static Verdict FromFacts(FileFacts facts)
    {
        var found = new List<(AppCategory Category, string Why)>();
        foreach (var (text, category) in Makers)
            if (facts.Company?.Contains(text, StringComparison.OrdinalIgnoreCase) == true) found.Add((category, $"made by {facts.Company}"));
        foreach (var (text, category) in Products)
            if (facts.Product?.Contains(text, StringComparison.OrdinalIgnoreCase) == true && facts.Product.IndexOf("WebView", StringComparison.OrdinalIgnoreCase) < 0)
                found.Add((category, $"part of {facts.Product}"));
        foreach (var (words, category) in Descriptions)
            if (words.IsMatch(facts.Description ?? "") || words.IsMatch(facts.Product ?? "")) found.Add((category, $"describes itself as \"{facts.Description ?? facts.Product}\""));
        var kinds = found.Select(f => f.Category).Distinct().ToList();
        return kinds.Count switch
        {
            1 => new(kinds[0], found[0].Why),
            0 => new(AppCategory.Other, "nothing it says names a kind"),
            _ => new(AppCategory.Other, $"mixed: {string.Join(", ", found.Select(f => f.Why))}"),
        };
    }

    private static bool InWindowsFolder(string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return windows.Length > 0 && path.StartsWith(windows.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What a program's file says of itself (from its version resource).</summary>
    internal sealed record FileFacts(string? Description, string? Product, string? Company);

    // Read once a program: the name and the category both use them. Kept small: a long-running agent sees many programs.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, FileFacts?> Facts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The program's description, product and maker, or null when its file can't be read (or isn't there).</summary>
    internal static FileFacts? FactsOf(string path)
    {
        if (Facts.TryGetValue(path, out var known)) return known;
        FileFacts? facts = null;
        try
        {
            // A network path or a drive that isn't there isn't asked (Windows can wait seconds on each).
            if (ReadyDisk.Holds(path) && File.Exists(path))
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
                facts = new FileFacts(Clean(info.FileDescription), Clean(info.ProductName), Clean(info.CompanyName));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        if (Facts.Count >= 512) Facts.Clear();
        Facts[path] = facts;
        return facts;
    }

    /// <summary>
    /// The category an app saved by an older version should change to now, or null to keep it. Only where something is
    /// certain: a launcher lists the app's folder (Other → Game), the app is a game launcher or its helper (now checked
    /// before any store's listing: Rockstar's Launcher.exe, "Overwatch Launcher.exe"), or the app is known not to be a
    /// game (an anti-cheat starter, a crash reporter, Wallpaper Engine). A game learned from play keeps Game. Reads
    /// nothing from the programs' files: it runs over every app as the agent starts.
    /// </summary>
    public static AppCategory? Reclassified(string exe, string? path, AppCategory saved)
    {
        var now = Decide(exe, path, readFile: false).Category;
        if (now == saved) return null;
        if (saved == AppCategory.Other && now is AppCategory.Game or AppCategory.Launcher) return now;
        // A known app first saved before the list named it (VBCSCompiler as Other until 1.23.0). The person's own pick is kept
        // apart (the overrides) and never comes here.
        if (saved == AppCategory.Other && Known.ContainsKey(exe)) return now;
        if (saved == AppCategory.Game && (now == AppCategory.Launcher || Known.ContainsKey(exe) || IsHelper(exe) || path is not null && InLauncherFolder(path)
            || SoftwareOnSteam(path) is not null)) return now;
        return null;
    }

    /// <summary>
    /// The game engine a program was made with, from the files every game made with it carries beside it, or null.
    /// This finds a game wherever it came from and wherever it sits (a folder in Downloads, a standalone install),
    /// including light games that never work the graphics card hard enough for the play rule (Super Battle Golf, a
    /// Unity game, kept it at 35-39%, 2026-10-22). Read from the disk once a folder, then remembered.
    /// </summary>
    public static string? EngineOf(string exe, string path)
    {
        var name = Path.GetFileNameWithoutExtension(exe);
        // The engines' own editors and tools sit among the same files: not games.
        if (name.StartsWith("UnrealEditor", StringComparison.OrdinalIgnoreCase) || name.StartsWith("UE4Editor", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Unity", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Unity Hub", StringComparison.OrdinalIgnoreCase)
            || name.Contains("CrashHandler", StringComparison.OrdinalIgnoreCase) || name.Contains("CrashReport", StringComparison.OrdinalIgnoreCase))
            return null;
        string key = path.ToLowerInvariant();
        if (Engines.TryGetValue(key, out var known)) return known;
        // A network path or a drive that isn't there: not looked at (Windows can wait seconds on each), and not
        // remembered either, so the program is looked at again once its drive is back.
        if (!ReadyDisk.Holds(path)) return null;
        string? engine = null;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null)
            {
                if (File.Exists(Path.Combine(dir, "UnityPlayer.dll")) || Directory.Exists(Path.Combine(dir, name + "_Data"))) engine = "Unity";
                // Unreal: <Game>\Binaries\Win64\<Game>-Win64-Shipping.exe, its packed content in <Game>\Content\Paks.
                else if (key.Contains(@"\binaries\win64\") || key.Contains(@"\binaries\wingdk\"))
                {
                    if (Directory.Exists(Path.GetFullPath(Path.Combine(dir, "..", "..", "Content", "Paks")))) engine = "Unreal";
                }
                else if (File.Exists(Path.ChangeExtension(path, ".pck"))) engine = "Godot";
                else if (File.Exists(Path.Combine(dir, "data.win"))) engine = "GameMaker";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        Engines[key] = engine;
        return engine;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> Engines = new();

    /// <summary>
    /// A game's anti-cheat starter, which sits in the game's own folder and only starts it: "RocketLeague_EAC.exe"
    /// (Easy Anti-Cheat), "TslGame_BE.exe" (BattlEye). Not the game: its time isn't play time.
    /// </summary>
    internal static bool IsAntiCheatStarter(string exe)
    {
        var name = Path.GetFileNameWithoutExtension(exe);
        return name.EndsWith("_eac", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_be", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_eac_launcher", StringComparison.OrdinalIgnoreCase)
            || name.Contains("easyanticheat", StringComparison.OrdinalIgnoreCase) || name.Contains("battleye", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"eldenring.exe" → "Eldenring". Used when an exe has no description.</summary>
    public static string FallbackName(string exe)
    {
        var name = Path.GetFileNameWithoutExtension(exe).Replace('_', ' ').Replace('-', ' ');
        if (name.Length == 0) return exe;
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name);
    }

    // Windows helpers whose exe has no useful description (a screenshot shows up as "Microsoft Windows Operating System").
    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["screenclippinghost.exe"] = "Snipping Tool", ["snippingtool.exe"] = "Snipping Tool",
        ["searchhost.exe"] = "Windows Search", ["startmenuexperiencehost.exe"] = "Start menu",
        ["shellexperiencehost.exe"] = "Windows taskbar", ["shellhost.exe"] = "Windows taskbar",
        ["textinputhost.exe"] = "Emoji & clipboard panel", ["lockapp.exe"] = "Lock screen",
        ["openwith.exe"] = "Open with", ["pickerhost.exe"] = "File picker", ["consent.exe"] = "Admin prompt",
        ["dwm.exe"] = "Windows display (DWM)", ["svchost.exe"] = "Windows service", ["systemsettings.exe"] = "Settings",
        ["wallpaper32.exe"] = "Wallpaper Engine", ["wallpaper64.exe"] = "Wallpaper Engine", ["wallpaperservice32.exe"] = "Wallpaper Engine service",
        ["wallpaperservice64.exe"] = "Wallpaper Engine service",
        // Anti-cheat launchers sit in the game's folder; named after the game they'd look like a second copy of it.
        ["start_protected_game.exe"] = "Easy Anti-Cheat", ["easyanticheat.exe"] = "Easy Anti-Cheat",
        ["easyanticheat_eos_setup.exe"] = "Easy Anti-Cheat setup",
        // Work Windows and launchers do in the background, which a temperature high can be put down to.
        ["msmpeng.exe"] = "Microsoft Defender", ["mpdefendercoreservice.exe"] = "Microsoft Defender",
        ["tiworker.exe"] = "Windows Update", ["trustedinstaller.exe"] = "Windows Update", ["mousocoreworker.exe"] = "Windows Update",
        ["searchindexer.exe"] = "Windows Search indexing", ["compattelrunner.exe"] = "Windows compatibility check",
        ["wmiprvse.exe"] = "Windows management (WMI)", ["audiodg.exe"] = "Windows audio",
        ["fossilize_replay.exe"] = "Steam shader pre-caching",
        // Tools whose files describe themselves badly ("The curl executable") or not at all.
        ["rigsight.exe"] = "RigSight", ["rigsight.agent.exe"] = "RigSight background agent",
        ["curl.exe"] = "curl",["warp-svc.exe"] = "Cloudflare WARP", ["warp-taskbar.exe"] = "Cloudflare WARP",
        // Windows services that use the network, each in its own service host (see the agent's NetApps), and Windows'
        // own kernel traffic (file sharing, some VPNs).
        ["svchost.exe:dosvc"] = "Delivery Optimization", ["svchost.exe:wuauserv"] = "Windows Update", ["svchost.exe:usosvc"] = "Windows Update",
        ["svchost.exe:bits"] = "Windows background downloads", ["svchost.exe:installservice"] = "Microsoft Store installs",
        ["svchost.exe:dnscache"] = "Windows name lookups (DNS)", ["system"] = "Windows system",
    };

    /// <summary>A built-in name for this exe, if it has one.</summary>
    public static string? KnownName(string exe) => KnownNames.GetValueOrDefault(exe);

    /// <summary>Names that say nothing about the app ("Microsoft® Windows® Operating System" is on hundreds of Windows exes).</summary>
    public static bool IsGenericName(string name) =>
        GenericDescriptions.Contains(name) || name.Contains("Operating System", StringComparison.OrdinalIgnoreCase);

    // Descriptions shared by many unrelated apps (framework hosts), which say nothing about the app itself.
    private static readonly HashSet<string> GenericDescriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Application", "Electron", "Chromium", "Node.js", "Java(TM) Platform SE binary", "OpenJDK Platform binary",
        "Python", "Qt", "CEF", "Unity", "Launcher",
    };

    /// <summary>Reads the product description from the exe (e.g. "Google Chrome").</summary>
    public static string ResolveName(string exe, string? path)
    {
        if (KnownName(exe) is { } known) return known;
        if (path is not null)
        {
            try
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                var desc = info.FileDescription?.Trim();
                if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 48 && !IsGenericName(desc))
                    return GameBehindLauncher(exe, desc, path) ?? Spaced(desc, path);
                var product = info.ProductName?.Trim();
                if (!string.IsNullOrWhiteSpace(product) && product.Length <= 48 && !IsGenericName(product))
                    return GameBehindLauncher(exe, product, path) ?? Spaced(product, path);
            }
            catch
            {
                // Access denied or not a PE file: fall through.
            }
        }
        if (!NotTheGame(exe, path) && GameName(path) is { Length: <= 48 } game) return game;
        return FallbackName(exe);
    }

    /// <summary>
    /// A program that is never the game whose folder it sits in: a helper, a launcher or anything on the known list. It
    /// isn't named after the game either (Rockstar's RockstarService.exe was "Launcher", its folder's name).
    /// </summary>
    private static bool NotTheGame(string exe, string? path) => IsHelper(exe) || Known.ContainsKey(exe) || LauncherEvidence(exe, path) is not null;

    /// <summary>
    /// A name an older version gave a launcher or a helper from the folder it sits in, as if it were a game there
    /// ("Launcher" for Rockstar's service): named again from the program itself.
    /// </summary>
    public static bool DressedAsGame(string exe, string name, string? path) =>
        NotTheGame(exe, path) && GameName(path) is { } folder && folder.Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The game's name for a game whose file is named after its maker's launcher: ARC Raiders' PioneerGame.exe calls
    /// itself "Embark Game Launcher". Only in a game library, and only where the file's own name doesn't say launcher
    /// (a real one, "RockstarLauncher.exe" beside a game, keeps its name), and never for a launcher or a helper by the
    /// launcher rule (RockstarService.exe calls itself a launcher's service). Null where that isn't so.
    /// </summary>
    public static string? GameBehindLauncher(string exe, string name, string? path) =>
        name.Contains("Launcher", StringComparison.OrdinalIgnoreCase) && !exe.Contains("launcher", StringComparison.OrdinalIgnoreCase)
        && !NotTheGame(exe, path) && GameName(path) is { Length: <= 48 } game ? game : null;

    /// <summary>
    /// The name as a folder above the exe spells it, when that is the same name with its words apart: a game whose
    /// file calls itself "Grounded2" in the folder "Grounded 2" is "Grounded 2".
    /// </summary>
    public static string Spaced(string name, string? path)
    {
        if (path is null || name.Any(char.IsWhiteSpace)) return name;
        try
        {
            for (var dir = Path.GetDirectoryName(path); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                var folder = Path.GetFileName(dir).Trim();
                if (folder.Length > name.Length && folder.Any(char.IsWhiteSpace)
                    && string.Concat(folder.Where(c => !char.IsWhiteSpace(c))).Equals(name, StringComparison.OrdinalIgnoreCase))
                    return folder;
            }
        }
        catch (ArgumentException)
        {
            // Not a path that can be walked: the name as it is.
        }
        return name;
    }

    // Game libraries whose next folder is the game's own (…\steamapps\common\Rematch\…), most specific first.
    private static readonly string[] Libraries =
    [
        @"\steamapps\common\", @"\epic games\", @"\gog galaxy\games\", @"\ubisoft game launcher\games\", @"\riot games\",
        @"\xboxgames\", @"\ea games\", @"\rockstar games\", @"\games\",
    ];

    /// <summary>
    /// The game's name from where it's installed, for exes that don't name themselves (Unreal games are all
    /// "&lt;Project&gt;-Win64-Shipping.exe" with no description): Steam's own name for it, or else its folder.
    /// </summary>
    public static string? GameName(string? path)
    {
        if (path is null) return null;
        foreach (var library in Libraries)
        {
            int i = path.IndexOf(library, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            int start = i + library.Length, end = path.IndexOf('\\', start);
            if (end <= start) return null; // the exe sits in the library folder itself
            var folder = path[start..end];
            if (library == Libraries[0] && SteamName(path[..(i + @"\steamapps".Length)], folder) is { } steam) return steam;
            return folder;
        }
        return null;
    }

    private static readonly Regex AcfLine = new(@"^\s*""(name|installdir)""\s+""(.*)""\s*$", RegexOptions.IgnoreCase);

    /// <summary>The name Steam shows for the game installed in steamapps\common\<paramref name="installDir"/>.</summary>
    private static string? SteamName(string steamapps, string installDir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                string? name = null, dir = null;
                foreach (var line in File.ReadLines(file))
                {
                    var m = AcfLine.Match(line);
                    if (!m.Success) continue;
                    if (m.Groups[1].Value.Equals("name", StringComparison.OrdinalIgnoreCase)) name ??= m.Groups[2].Value.Trim();
                    else dir ??= m.Groups[2].Value.Trim();
                    if (name is not null && dir is not null) break;
                }
                if (installDir.Equals(dir, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(name)) return name;
            }
        }
        catch
        {
            // No access, or the library moved: the folder name will do.
        }
        return null;
    }

    /// <summary>The category's name in the person's language, as pages show it.</summary>
    public static string Label(AppCategory c) => Lang.T(EnglishLabel(c));

    /// <summary>The category's name in English, as the spreadsheet export writes it (people's sheets rely on it).</summary>
    public static string EnglishLabel(AppCategory c) => c switch
    {
        AppCategory.Game => Lang.Key("Games"),
        AppCategory.Browser => Lang.Key("Browsing"),
        AppCategory.Development => Lang.Key("Development"),
        AppCategory.Communication => Lang.Key("Chat & calls"),
        AppCategory.Media => Lang.Key("Media & creative"),
        AppCategory.Launcher => Lang.Key("Launchers"),
        AppCategory.Productivity => Lang.Key("Productivity"),
        AppCategory.System => Lang.Key("System"),
        _ => Lang.Key("Other"),
    };

    /// <summary>Consistent color per category (hex), used in timelines and charts.</summary>
    public static string Color(AppCategory c) => c switch
    {
        AppCategory.Game => "#3DDC97",
        AppCategory.Browser => "#5B8CFF",
        AppCategory.Development => "#B18CFF",
        AppCategory.Communication => "#7C83FD",
        AppCategory.Media => "#F472B6",
        AppCategory.Launcher => "#22D3EE",
        AppCategory.Productivity => "#FBBF24",
        AppCategory.System => "#64748B",
        _ => "#94A3B8",
    };
}
