using Rigsight.Core.Protocol;

namespace Rigsight.Core.Net;

/// <summary>
/// What's said about the ping to an app's server, the same on the Network page, the Ping tile and the taskbar: what the
/// app is, its figure, its usual as a range, and its checks lost as a count (approved mock-up "Ping for your games and
/// calls", 2026-11-16).
/// </summary>
public static class ServerWords
{
    /// <summary>A figure that needs a look: half again above the top of its usual and at least 10 ms more.</summary>
    public static bool AboveUsual(int ms, int? usualHigh) => usualHigh is int high && ms > high * 1.5 && ms - high >= 10;

    /// <summary>Lost checks that need a look: more than 2% of the last 10 minutes' (and more than one).</summary>
    public static bool Losing(ServerLive s) => s.Lost > 1 && s.Lost * 50 > s.Sent;

    public static bool NeedsLook(ServerLive s) => s.Ms is int ms && AboveUsual(ms, s.UsualHigh) || Losing(s);

    /// <summary>"Call", "Game", "Remote play", "Steady stream"; nothing for an app picked by hand.</summary>
    public static string? KindWord(string kind) => kind switch
    {
        "game" => Lang.T("Game"),
        "call" => Lang.T("Call"),
        "remote" => Lang.T("Remote play"),
        "live" => Lang.T("Steady stream"),
        _ => null,
    };

    /// <summary>The line under the app's name: "Call, Singapore", "Game", "Singapore" (picked), or nothing.</summary>
    public static string? Under(ServerLive s) =>
        (KindWord(s.Kind), s.Place) switch
        {
            ({ } k, { } p) => Lang.T($"{k}, {p}"),
            ({ } k, null) => k,
            (null, { } p) => p,
            _ => null,
        };

    /// <summary>The figure in the Ping column: "97 ms", "–" for a server that doesn't answer, empty before 30 s of checks.</summary>
    public static string Figure(ServerLive s) => s.NoAnswer ? "–" : s.Ms is int ms ? $"{ms} ms" : "";

    /// <summary>"Discord's server in Singapore", "Dota 2's server".</summary>
    public static string ServerOf(string app, string? place) =>
        place is null ? Lang.T($"{app}'s server") : Lang.T($"{app}'s server in {place}");

    /// <summary>"Usual 90 to 100 ms." or what it still needs.</summary>
    public static string Usual(ServerLive s) =>
        s.UsualLow is int lo && s.UsualHigh is int hi ? Lang.T($"Usual {lo} to {hi} ms.")
            : Lang.T("Too soon to say what's usual. It needs an hour of checks to this server.");

    /// <summary>"None of the last 120 checks lost." / "3 of the last 120 checks lost."</summary>
    public static string Lost(ServerLive s) =>
        s.Lost == 0 ? Lang.T($"None of the last {s.Sent} checks lost.") : Lang.T($"{s.Lost} of the last {s.Sent} checks lost.");

    /// <summary>The hover on an app's ping: whose server, the figure, its usual and the checks lost.</summary>
    public static string Tip(string app, ServerLive s)
    {
        if (s.NoAnswer) return Lang.T($"{ServerOf(app, s.Place)} doesn't answer checks, so its ping can't be shown.");
        if (s.Ms is not int ms) return Lang.T($"Checking {ServerOf(app, s.Place)}. Its ping shows after 30 seconds of checks.");
        return $"{ServerOf(app, s.Place)}, {ms} ms\n{Usual(s)} {Lost(s)}";
    }

    /// <summary>The app's name with what it is where that reads naturally: "Discord call", "Dota 2".</summary>
    public static string Named(string app, string kind) => kind == "call" ? Lang.T($"{app} call") : app;

    /// <summary>The rest of the summary line after the figure: ", usual 90 to 100 ms, none of the last 120 checks lost".</summary>
    public static string SummaryRest(ServerLive s)
    {
        string usual = s.UsualLow is int lo && s.UsualHigh is int hi ? Lang.T($", usual {lo} to {hi} ms") : "";
        string lost = s.Lost == 0 ? Lang.T($"none of the last {s.Sent} checks lost") : Lang.T($"{s.Lost} of the last {s.Sent} checks lost");
        return $"{usual}, {lost}";
    }

    /// <summary>
    /// The server the summary and the taskbar speak of: one that needs a look first, then a game, a call, the rest; the
    /// one checked longest among equals. Null with none being checked (or none with a figure yet).
    /// </summary>
    public static ServerLive? Lead(IReadOnlyList<ServerLive>? servers) =>
        servers?.Where(s => s.Ms is not null || s.NoAnswer)
            .OrderByDescending(NeedsLook)
            .ThenBy(s => s.Kind switch { "game" => 0, "call" => 1, "remote" => 2, "picked" => 3, _ => 4 })
            .ThenByDescending(s => s.Sent)
            .FirstOrDefault();
}
