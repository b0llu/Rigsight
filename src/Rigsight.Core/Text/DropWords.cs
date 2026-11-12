using Rigsight.Core.Data;

namespace Rigsight.Core;

/// <summary>
/// What RigSight says about the internet dropping, in the notification and on the Network page, in one place. The
/// words are the user's own (2026-11-05): "Your internet is down / Your router is reachable, but there's no internet
/// connection.", "Your Wi-Fi disconnected / Your PC lost its connection to the Wi-Fi network.", "Your internet keeps
/// dropping / Your Wi-Fi disconnected 3 times in the last 10 minutes.", "Your internet is back / Your connection was
/// restored after 50 seconds of downtime." The page says the same in the past tense.
/// </summary>
public static class DropWords
{
    /// <param name="kind">The PC lost its network (<see cref="NetDropKind.Link"/>) or kept it with no internet.</param>
    /// <param name="card">"Wi-Fi", "Ethernet" or another adapter's kind (null: not known).</param>
    /// <param name="router">Whether the router answered the line check meanwhile (null: not checked).</param>
    public static (string Title, string Body) Down(NetDropKind kind, string? card, bool? router) => kind == NetDropKind.Link
        ? card == "Ethernet"
            ? (Lang.T("Your Ethernet disconnected"), Lang.T("Your PC lost its wired connection to the network."))
            : card == "Wi-Fi"
                ? (Lang.T("Your Wi-Fi disconnected"), Lang.T("Your PC lost its connection to the Wi-Fi network."))
                : (Lang.T("Your network disconnected"), Lang.T("Your PC lost its connection to the network."))
        : (Lang.T("Your internet is down"), router == true ? Lang.T("Your router is reachable, but there's no internet connection.")
            : Lang.T("Your PC is connected to the network, but there's no internet connection."));

    public static (string Title, string Body) Back(long seconds) =>
        (Lang.T("Your internet is back"), Lang.T($"Your connection was restored after {Long(seconds)} of downtime."));

    /// <param name="kind">The kind most of the drops were.</param>
    public static (string Title, string Body) Keeps(int times, NetDropKind kind, string? card) =>
        (Lang.T("Your internet keeps dropping"), kind == NetDropKind.Link
            ? card == "Ethernet" ? Lang.T($"Your Ethernet disconnected {times} times in the last 10 minutes.")
                : card == "Wi-Fi" ? Lang.T($"Your Wi-Fi disconnected {times} times in the last 10 minutes.")
                : Lang.T($"Your network disconnected {times} times in the last 10 minutes.")
            : Lang.T($"Your internet dropped {times} times in the last 10 minutes."));

    /// <summary>
    /// How often a period's internet dropped, as the Network page's Connection card and the widgets' Connection today
    /// say it: "No drops", never "online all day" (a day still going on, or one the PC was off for half of, wasn't that).
    /// </summary>
    public static string Count(int drops) => drops switch
    {
        0 => Lang.T("No drops"),
        1 => Lang.T("Dropped once"),
        2 => Lang.T("Dropped twice"),
        var n => Lang.T($"Dropped {n} times"),
    };

    /// <summary>A drop on the Network page ("Your internet was down for 50 seconds"), and what broke, in the past tense.</summary>
    public static string WasDown(long seconds) => Lang.T($"Your internet was down for {Long(seconds)}");

    public static string WhatBroke(NetDropKind kind, string? card, bool? router) => kind == NetDropKind.Link
        ? card == "Ethernet" ? Lang.T("Your PC lost its wired connection to the network.")
            : card == "Wi-Fi" ? Lang.T("Your PC lost its connection to the Wi-Fi network.")
            : Lang.T("Your PC lost its connection to the network.")
        : router == true ? Lang.T("Your router was reachable, but there was no internet connection.")
            : Lang.T("Your PC was connected to the network, but there was no internet connection.");

    /// <summary>
    /// A length in whole words, joined like every notification's ("2 hours and 14 minutes"): "50 seconds", "1 minute",
    /// "2 minutes and 10 seconds", "1 hour and 5 minutes". Seconds stay past a minute: a drop's length is told exactly.
    /// </summary>
    public static string Long(long seconds)
    {
        seconds = Math.Max(1, seconds);
        long h = seconds / 3600, m = seconds % 3600 / 60, s = seconds % 60;
        string Hours(long n) => n == 1 ? Lang.T("1 hour") : Lang.T($"{n} hours");
        string Minutes(long n) => n == 1 ? Lang.T("1 minute") : Lang.T($"{n} minutes");
        string Seconds(long n) => n == 1 ? Lang.T("1 second") : Lang.T($"{n} seconds");
        string Both(string a, string b) => Lang.T($"{a} and {b}");
        if (h > 0) return m > 0 ? Both(Hours(h), Minutes(m)) : Hours(h);
        if (m > 0) return s > 0 ? Both(Minutes(m), Seconds(s)) : Minutes(m);
        return Seconds(s);
    }
}
