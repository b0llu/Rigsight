using Rigsight.Core.Data;
using Rigsight.Core.Settings;

namespace Rigsight.Core.Apps;

/// <summary>
/// How a temperature high is put down to an app: the one that was doing the work just before it, never the window in
/// front, which often isn't what made the heat (a game left running, an update, shaders compiling in the background).
/// </summary>
public static class PeakWords
{
    /// <summary>
    /// Under a peak's value: what was going on, in words that suit the kind of app doing most of that part's work just
    /// before it ("While playing Rematch", "While Spotify was playing"), or when it was if no app clearly was (null: no
    /// peak yet). ("Rematch was busiest" and "Rematch used the CPU most" read as a share of use, not as where the heat
    /// came from.)
    /// </summary>
    public static string? Line(double? peak, string? app, long? time, AppCategory? category = null) =>
        peak is null ? null
        : app is not null ? category switch
        {
            AppCategory.Game => $"While playing {app}",
            AppCategory.Media => $"While {app} was playing",
            AppCategory.Browser or AppCategory.Communication or AppCategory.Productivity or AppCategory.Development => $"While using {app}",
            // Launchers (downloads, updates), Windows' own work and anything unknown: it was running, maybe unseen.
            _ => $"While {app} was running",
        }
        : time is long t ? $"At {TimeUtil.FromUnix(t):h:mm tt}"
        : "";

    /// <summary>After "CPU peaked at 88° at 3:12 PM": ", with Rematch working it hardest" (nothing when no app clearly was).</summary>
    public static string With(string? app) => app is null ? "" : $", with {app} working it hardest";
}
