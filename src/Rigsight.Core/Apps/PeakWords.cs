using Rigsight.Core.Data;

namespace Rigsight.Core.Apps;

/// <summary>
/// How a temperature high is put down to an app: the one that was doing the work just before it, never the window in
/// front, which often isn't what made the heat (a game left running, an update, shaders compiling in the background).
/// </summary>
public static class PeakWords
{
    /// <summary>Under a peak's value: "Rematch was busiest", or when it was if no app clearly was (null: no peak yet).</summary>
    public static string? Line(double? peak, string? app, long? time) =>
        peak is null ? null
        : app is not null ? $"{app} was busiest"
        : time is long t ? $"At {TimeUtil.FromUnix(t):h:mm tt}"
        : "";

    /// <summary>After "CPU peaked at 88° at 3:12 PM": ", with Rematch working it hardest" (nothing when no app clearly was).</summary>
    public static string With(string? app) => app is null ? "" : $", with {app} working it hardest";
}
