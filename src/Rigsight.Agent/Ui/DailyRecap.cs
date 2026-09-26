using System.Diagnostics;
using System.Globalization;
using Rigsight.Core.Settings;

namespace Rigsight.Agent.Ui;

/// <summary>
/// The daily recap: shown when the PC is turned on (or wakes from sleep), for the last day it was used whose recap
/// hasn't been shown yet, once that day is over: at 5 AM the next morning, as for the "night before ran late"
/// insight (waking a laptop at 12:10 AM doesn't recap the evening still going on). A late session running past
/// midnight doesn't bring it; nor does a day away: the next time the PC is turned on, the recap is of the last day
/// it was used, however long ago.
/// </summary>
internal static class DailyRecap
{
    /// <summary>Signed in this recently: the PC was just turned on (or someone just signed in).</summary>
    public static readonly TimeSpan JustStartedWithin = TimeSpan.FromMinutes(10);

    /// <summary>A day is over at this hour of the next morning (use before it belongs to the night before).</summary>
    public const int DayOverAtHour = 5;

    /// <summary>Days before this one are over at <paramref name="now"/>: today after 5 AM, yesterday before.</summary>
    public static DateTime OverBefore(DateTime now) => now.Hour < DayOverAtHour ? now.Date.AddDays(-1) : now.Date;

    /// <summary>
    /// The day to recap: the last day the PC was used before <paramref name="overBefore"/> (see <see cref="OverBefore"/>),
    /// unless its recap was shown already.
    /// </summary>
    public static DateTime? DayToRecap(DateTime overBefore, DateTime? lastUsedDay, DateTime? recapped) =>
        lastUsedDay is { } day && day < overBefore.Date && (recapped is null || day > recapped) ? day : null;

    /// <summary>The last day recapped: <see cref="RigsightSettings.RecappedDay"/>, or before 0.5.16 the day before a recap was shown.</summary>
    public static DateTime? Recapped(RigsightSettings s) => Day(s.RecappedDay) ?? Day(s.LastRecapDay)?.AddDays(-1);

    /// <summary>"Yesterday on your PC", "Tuesday on your PC", "12 September on your PC".</summary>
    public static string Title(DateTime day, DateTime today) => (today.Date - day.Date).Days switch
    {
        1 => "Yesterday on your PC",
        < 7 => $"{day.ToString("dddd", CultureInfo.InvariantCulture)} on your PC",
        _ => $"{day.ToString("d MMMM", CultureInfo.InvariantCulture)} on your PC",
    };

    public static string Key(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether the user signed in just now (the PC was turned on): their desktop (Explorer) started within
    /// <see cref="JustStartedWithin"/>. Not the time since Windows started, which Windows' fast startup carries over a
    /// shutdown.
    /// </summary>
    public static bool JustSignedIn()
    {
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try
            {
                if (p.SessionId == session && DateTime.Now - p.StartTime < JustStartedWithin) return true;
            }
            catch
            {
                // Gone or unreadable: the others still count.
            }
            finally
            {
                p.Dispose();
            }
        }
        return false;
    }

    private static DateTime? Day(string? text) =>
        DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
