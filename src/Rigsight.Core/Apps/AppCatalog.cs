using System.Globalization;
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
        ["control.exe"] = AppCategory.System, ["rigsight.exe"] = AppCategory.System, ["rigsight.agent.exe"] = AppCategory.System,
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

    // Descriptions shared by many unrelated apps (framework hosts), which say nothing about the app itself.
    private static readonly HashSet<string> GenericDescriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Application", "Electron", "Chromium", "Node.js", "Java(TM) Platform SE binary", "OpenJDK Platform binary",
        "Python", "Qt", "CEF", "Unity", "Launcher",
    };

    /// <summary>Reads the product description from the exe (e.g. "Google Chrome").</summary>
    public static string ResolveName(string exe, string? path)
    {
        if (path is not null)
        {
            try
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                var desc = info.FileDescription?.Trim();
                if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 48 && !GenericDescriptions.Contains(desc))
                    return desc;
                var product = info.ProductName?.Trim();
                if (!string.IsNullOrWhiteSpace(product) && product.Length <= 48 && !GenericDescriptions.Contains(product))
                    return product;
            }
            catch
            {
                // Access denied or not a PE file: fall through.
            }
        }
        return FallbackName(exe);
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
