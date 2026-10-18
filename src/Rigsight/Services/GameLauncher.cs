using System.Diagnostics;
using System.IO;
using Rigsight.Core.Games;

namespace Rigsight.Services;

/// <summary>
/// The Games page's Play, Open game folder and Uninstall. Play and Uninstall only hand over to the game's launcher (its
/// link, or its own uninstaller) exactly as a shortcut would: the launcher signs in, updates and asks to confirm.
/// RigSight never removes a file itself.
/// </summary>
public static class GameLauncher
{
    /// <summary>Replaced by tests, so nothing is started.</summary>
    internal static Action<ProcessStartInfo> Start { get; set; } = info => Process.Start(info)?.Dispose();

    /// <summary>Who will open the game or remove it, in words: "Steam", "Epic Games", "the Riot Client".</summary>
    public static string Who(Launcher? launcher) => launcher switch
    {
        Launcher.Steam => "Steam",
        Launcher.Epic => "Epic Games",
        Launcher.Riot => "the Riot Client",
        Launcher.Gog => "GOG Galaxy",
        Launcher.Ubisoft => "Ubisoft Connect",
        Launcher.Ea => "the EA app",
        Launcher.BattleNet => "Battle.net",
        _ => "Windows",
    };

    /// <summary>The launcher's name as on a cover's badge.</summary>
    public static string Badge(Launcher? launcher) => launcher switch
    {
        Launcher.Steam => "STEAM",
        Launcher.Epic => "EPIC",
        Launcher.Riot => "RIOT",
        Launcher.Gog => "GOG",
        Launcher.Ubisoft => "UBISOFT",
        Launcher.Ea => "EA",
        Launcher.BattleNet => "BATTLE.NET",
        _ => "",
    };

    /// <summary>Starts the game through its launcher, or its own program when no launcher lists it.</summary>
    public static bool Play(GameEntry game)
    {
        if (game.Installed?.LaunchTarget is { } target) return Run(target, game.Installed.LaunchArgs, "play " + game.Name);
        if (game.ExePath is { } exe && File.Exists(exe)) return Run(exe, null, "play " + game.Name, Path.GetDirectoryName(exe));
        return false;
    }

    /// <summary>The game's folder in File Explorer.</summary>
    public static bool OpenFolder(GameEntry game)
    {
        var dir = FolderOf(game);
        return dir is not null && Run(Core.RigsightPaths.Explorer, $"\"{dir}\"", "open the folder of " + game.Name, shell: false);
    }

    public static string? FolderOf(GameEntry game) =>
        game.Installed?.InstallDir is { } d && Directory.Exists(d) ? d
        : game.ExePath is { } exe && Path.GetDirectoryName(exe) is { } e && Directory.Exists(e) ? e : null;

    /// <summary>The launcher's own uninstall for the game (it asks to confirm), else Windows' Installed apps.</summary>
    public static bool Uninstall(GameEntry game)
    {
        if (game.Installed?.UninstallTarget is { } target) return Run(target, game.Installed.UninstallArgs, "uninstall " + game.Name);
        return Run("ms-settings:appsfeatures", null, "uninstall " + game.Name);
    }

    private static bool Run(string target, string? args, string what, string? workingDir = null, bool shell = true)
    {
        try
        {
            var info = new ProcessStartInfo(target) { UseShellExecute = shell };
            if (args is not null) info.Arguments = args;
            if (workingDir is not null) info.WorkingDirectory = workingDir;
            Start(info);
            Core.Log.Write("games", $"{what}: {target} {args}".TrimEnd());
            return true;
        }
        catch (Exception ex)
        {
            Core.Log.Write("games", $"couldn't {what} ({target} {args}): {ex.Message}");
            return false;
        }
    }
}
