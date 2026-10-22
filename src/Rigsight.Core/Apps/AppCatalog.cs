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
        // Development
        ["code.exe"] = AppCategory.Development, ["devenv.exe"] = AppCategory.Development, ["rider64.exe"] = AppCategory.Development,
        ["idea64.exe"] = AppCategory.Development, ["pycharm64.exe"] = AppCategory.Development, ["webstorm64.exe"] = AppCategory.Development,
        ["windowsterminal.exe"] = AppCategory.Development, ["wt.exe"] = AppCategory.Development, ["powershell.exe"] = AppCategory.Development,
        ["pwsh.exe"] = AppCategory.Development, ["cmd.exe"] = AppCategory.Development, ["cursor.exe"] = AppCategory.Development,
        ["antigravity.exe"] = AppCategory.Development, ["postman.exe"] = AppCategory.Development, ["docker desktop.exe"] = AppCategory.Development,
        ["github desktop.exe"] = AppCategory.Development, ["sublime_text.exe"] = AppCategory.Development, ["notepad++.exe"] = AppCategory.Development,
        ["claude.exe"] = AppCategory.Development,
        // Communication
        ["discord.exe"] = AppCategory.Communication, ["slack.exe"] = AppCategory.Communication, ["ms-teams.exe"] = AppCategory.Communication,
        ["teams.exe"] = AppCategory.Communication, ["whatsapp.exe"] = AppCategory.Communication, ["telegram.exe"] = AppCategory.Communication,
        ["zoom.exe"] = AppCategory.Communication, ["signal.exe"] = AppCategory.Communication, ["outlook.exe"] = AppCategory.Communication,
        ["olk.exe"] = AppCategory.Communication, ["thunderbird.exe"] = AppCategory.Communication,
        // Media
        ["spotify.exe"] = AppCategory.Media, ["vlc.exe"] = AppCategory.Media, ["obs64.exe"] = AppCategory.Media,
        ["mpc-hc64.exe"] = AppCategory.Media, ["mpv.exe"] = AppCategory.Media, ["stremio.exe"] = AppCategory.Media,
        ["potplayermini64.exe"] = AppCategory.Media, ["music.ui.exe"] = AppCategory.Media, ["netflix.exe"] = AppCategory.Media,
        ["photoshop.exe"] = AppCategory.Media, ["premiere pro.exe"] = AppCategory.Media, ["resolve.exe"] = AppCategory.Media,
        ["blender.exe"] = AppCategory.Media, ["audacity.exe"] = AppCategory.Media,
        // Game launchers
        ["steam.exe"] = AppCategory.Launcher, ["steamwebhelper.exe"] = AppCategory.Launcher, ["epicgameslauncher.exe"] = AppCategory.Launcher,
        ["battle.net.exe"] = AppCategory.Launcher, ["eadesktop.exe"] = AppCategory.Launcher, ["upc.exe"] = AppCategory.Launcher,
        ["ubisoftconnect.exe"] = AppCategory.Launcher, ["galaxyclient.exe"] = AppCategory.Launcher, ["riotclientservices.exe"] = AppCategory.Launcher,
        ["xboxpcapp.exe"] = AppCategory.Launcher, ["playnite.desktopapp.exe"] = AppCategory.Launcher, ["rockstarlauncher.exe"] = AppCategory.Launcher,
        ["riot client.exe"] = AppCategory.Launcher, ["riotclientux.exe"] = AppCategory.Launcher, ["eawebhelper.exe"] = AppCategory.Launcher,
        ["epicwebhelper.exe"] = AppCategory.Launcher, ["upcwebcore.exe"] = AppCategory.Launcher, ["uplaywebcore.exe"] = AppCategory.Launcher,
        // Anti-cheat starters sit in a game's folder and open the game; their seconds aren't play.
        ["start_protected_game.exe"] = AppCategory.Launcher, ["eaanticheat.gameservicelauncher.exe"] = AppCategory.Launcher,
        ["easyanticheat_eos_setup.exe"] = AppCategory.Launcher,
        // Installed through Steam like games, but they aren't.
        ["wallpaper32.exe"] = AppCategory.Other, ["wallpaper64.exe"] = AppCategory.Other, ["stremio-shell-ng.exe"] = AppCategory.Media,
        // Productivity
        ["winword.exe"] = AppCategory.Productivity, ["excel.exe"] = AppCategory.Productivity, ["powerpnt.exe"] = AppCategory.Productivity,
        ["onenote.exe"] = AppCategory.Productivity, ["notion.exe"] = AppCategory.Productivity, ["obsidian.exe"] = AppCategory.Productivity,
        ["acrobat.exe"] = AppCategory.Productivity, ["notepad.exe"] = AppCategory.Productivity, ["figma.exe"] = AppCategory.Productivity,
        // System
        ["explorer.exe"] = AppCategory.System, ["taskmgr.exe"] = AppCategory.System, ["systemsettings.exe"] = AppCategory.System,
        ["applicationframehost.exe"] = AppCategory.System, ["searchhost.exe"] = AppCategory.System, ["startmenuexperiencehost.exe"] = AppCategory.System,
        ["shellexperiencehost.exe"] = AppCategory.System, ["lockapp.exe"] = AppCategory.System, ["mmc.exe"] = AppCategory.System,
        ["control.exe"] = AppCategory.System, ["system"] = AppCategory.System, ["rigsight.exe"] = AppCategory.System, ["rigsight.agent.exe"] = AppCategory.System,
    };

    private static readonly string[] GameFolders =
    [
        @"\steamapps\common\", @"\epic games\", @"\gog galaxy\games\", @"\riot games\", @"\xboxgames\",
        @"\ea games\", @"\games\", @"\ubisoft game launcher\games\", @"\battle.net\games\", @"\rockstar games\",
        @"\windowsapps\microsoft.minecraft", @"\zenlesszonezero", @"\genshin impact",
    ];

    private static readonly string[] NotGamesInGameFolders = ["unitycrashhandler64.exe", "crashreportclient.exe", "easyanticheat.exe", "setup.exe"];

    // The launchers' own programs under a game-library folder name ("Epic Games\Launcher", "Riot Games\Riot Client"):
    // they aren't games, though the folder rule would call them one.
    private static readonly string[] LauncherFolders =
    [
        @"\epic games\launcher\", @"\epic games\epic online services\", @"\riot games\riot client\",
        @"\ubisoft game launcher\", @"\ea desktop\", @"\battle.net\", @"\gog galaxy\",
    ];

    private static bool InLauncherFolder(string path)
    {
        var p = path.ToLowerInvariant();
        // A launcher's games folder ("Ubisoft Game Launcher\games\", "GOG Galaxy\Games\") holds games, not the launcher.
        return LauncherFolders.Any(p.Contains) && !p.Contains(@"game launcher\games\") && !p.Contains(@"gog galaxy\games\") && !p.Contains(@"battle.net\games\");
    }

    /// <summary>
    /// The games the launchers say are installed (see <see cref="Games.GameLibrary"/>), set by whoever scanned them.
    /// A program inside one of their folders is a game wherever that folder is (Rocket League in E:\rocketleague).
    /// </summary>
    public static IReadOnlyList<Games.InstalledGame> InstalledGames { get; set; } = [];

    /// <summary>
    /// Category for an app. <paramref name="overrides"/> (the user's choices) win, then the known list,
    /// then the install folder (Steam/Epic/… libraries mean it's a game).
    /// </summary>
    public static AppCategory Classify(string exe, string? path, IReadOnlyDictionary<string, AppCategory>? overrides = null)
    {
        if (overrides is not null && overrides.TryGetValue(exe, out var chosen)) return chosen;
        if (Known.TryGetValue(exe, out var known)) return known;
        if (IsAntiCheatStarter(exe) && !NotGamesInGameFolders.Contains(exe, StringComparer.OrdinalIgnoreCase)) return AppCategory.Launcher;

        if (path is not null && !NotGamesInGameFolders.Contains(exe, StringComparer.OrdinalIgnoreCase))
        {
            if (Games.GameLibrary.Find(InstalledGames, path) is not null) return AppCategory.Game;
            if (InLauncherFolder(path)) return AppCategory.Launcher;
            if (GameFolders.Any(path.ToLowerInvariant().Contains)) return AppCategory.Game;
            if (EngineOf(exe, path) is not null) return AppCategory.Game;
        }
        if (path is not null && path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
            return AppCategory.System;
        return AppCategory.Other;
    }

    /// <summary>
    /// The category an app saved by an older version should change to now, or null to keep it. Only where something is
    /// certain: a launcher lists the app's folder (Other → Game), or the app is known not to be a game (a launcher's
    /// own program, an anti-cheat starter, Wallpaper Engine). A game learned from play keeps Game.
    /// </summary>
    public static AppCategory? Reclassified(string exe, string? path, AppCategory saved)
    {
        var now = Classify(exe, path);
        if (now == saved) return null;
        if (now == AppCategory.Game && saved == AppCategory.Other) return now;
        if (saved == AppCategory.Game && (Known.ContainsKey(exe) || IsAntiCheatStarter(exe) || path is not null && InLauncherFolder(path))) return now;
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
        ["curl.exe"] = "curl", ["warp-svc.exe"] = "Cloudflare WARP", ["warp-taskbar.exe"] = "Cloudflare WARP",
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
        if (!NotGamesInGameFolders.Contains(exe, StringComparer.OrdinalIgnoreCase) && GameName(path) is { Length: <= 48 } game) return game;
        return FallbackName(exe);
    }

    /// <summary>
    /// The game's name for a game whose file is named after its maker's launcher: ARC Raiders' PioneerGame.exe calls
    /// itself "Embark Game Launcher". Only in a game library, and only where the file's own name doesn't say launcher
    /// (a real one, "RockstarLauncher.exe" beside a game, keeps its name). Null where that isn't so.
    /// </summary>
    public static string? GameBehindLauncher(string exe, string name, string? path) =>
        name.Contains("Launcher", StringComparison.OrdinalIgnoreCase) && !exe.Contains("launcher", StringComparison.OrdinalIgnoreCase)
        && !NotGamesInGameFolders.Contains(exe, StringComparer.OrdinalIgnoreCase) && GameName(path) is { Length: <= 48 } game ? game : null;

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
