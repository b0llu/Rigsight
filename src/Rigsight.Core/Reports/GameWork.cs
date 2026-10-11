using Rigsight.Core.Data;
using Rigsight.Core.Settings;

namespace Rigsight.Core.Reports;

/// <summary>
/// Whose work the GPU's was while a game was in front. A game guarded by anti-cheat hides its own GPU use, and what
/// can still be read then is its launcher's (Steam draws its overlay inside the game): the launcher came out as the
/// app working the GPU hardest for the whole match, and a fan's fastest moment read "While Steam was running".
/// </summary>
public static class GameWork
{
    /// <summary>The app to name for the GPU's work: the game in front when the one found was only a launcher.</summary>
    public static T? GpuApp<T>(T? found, T? inFront, Func<T, AppCategory?> categoryOf) where T : class =>
        found is not null && inFront is not null && !ReferenceEquals(found, inFront)
        && categoryOf(found) == AppCategory.Launcher && categoryOf(inFront) == AppCategory.Game ? inFront : found;

    /// <summary>The same for minutes already recorded, which name the launcher: each gets the game that was in front.</summary>
    public static void Settle(IEnumerable<SystemMinute> minutes, Func<long?, AppCategory> categoryOf)
    {
        foreach (var m in minutes)
            if (m.GpuApp is not null && m.FgApp is not null && m.GpuApp != m.FgApp
                && categoryOf(m.GpuApp) == AppCategory.Launcher && categoryOf(m.FgApp) == AppCategory.Game)
                m.GpuApp = m.FgApp;
    }
}
