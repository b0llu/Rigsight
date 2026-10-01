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

    /// <summary>
    /// Category for an app. <paramref name="overrides"/> (the user's choices) win, then the known list,
    /// then the install folder (Steam/Epic/… libraries mean it's a game).
    /// </summary>
    public static AppCategory Classify(string exe, string? path, IReadOnlyDictionary<string, AppCategory>? overrides = null)
    {
        if (overrides is not null && overrides.TryGetValue(exe, out var chosen)) return chosen;
        if (Known.TryGetValue(exe, out var known)) return known;

        if (path is not null && !NotGamesInGameFolders.Contains(exe, StringComparer.OrdinalIgnoreCase))
        {
            var p = path.ToLowerInvariant();
            if (GameFolders.Any(p.Contains)) return AppCategory.Game;
        }
        if (path is not null && path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
            return AppCategory.System;
        return AppCategory.Other;
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
                    return desc;
                var product = info.ProductName?.Trim();
                if (!string.IsNullOrWhiteSpace(product) && product.Length <= 48 && !IsGenericName(product))
                    return product;
            }
            catch
            {
                // Access denied or not a PE file: fall through.
            }
        }
        if (!NotGamesInGameFolders.Contains(exe, StringComparer.OrdinalIgnoreCase) && GameName(path) is { Length: <= 48 } game) return game;
        return FallbackName(exe);
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

    public static string Label(AppCategory c) => c switch
    {
        AppCategory.Game => "Games",
        AppCategory.Browser => "Browsing",
        AppCategory.Development => "Development",
        AppCategory.Communication => "Chat & calls",
        AppCategory.Media => "Media & creative",
        AppCategory.Launcher => "Launchers",
        AppCategory.Productivity => "Productivity",
        AppCategory.System => "System",
        _ => "Other",
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
