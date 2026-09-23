using Rigsight.Core.Settings;

namespace Rigsight.Core.Apps;

/// <summary>Describes what you were doing in an app, in words that fit the kind of app.</summary>
public static class ActivityWords
{
    /// <summary>"while playing Dota 2", "while browsing in Chrome", "while on Discord"…</summary>
    public static string While(string app, AppCategory category) => category switch
    {
        AppCategory.Game => $"while playing {app}",
        AppCategory.Browser => $"while browsing in {app}",
        // Music and video alike: "while Spotify was playing", "while VLC was playing".
        AppCategory.Media => $"while {app} was playing",
        // Chats and calls: "while on Discord", "while on Zoom".
        AppCategory.Communication => $"while on {app}",
        AppCategory.Development or AppCategory.Productivity => $"while working in {app}",
        AppCategory.Launcher or AppCategory.System => $"while in {app}",
        _ => $"while using {app}",
    };
}
