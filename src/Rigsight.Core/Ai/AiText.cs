using System.Globalization;
using Rigsight.Core.Settings;

namespace Rigsight.Core.Ai;

/// <summary>
/// The one way look-up text writes dates, times and numbers for the model, whatever Windows' region and the clock picked
/// in Settings: "Wed 4 Nov 9:59 PM", "10:13:00 PM", "2.4 GB", "1,234". A Spanish PC sent "mié. 4 nov. 9:59 p. m." and
/// another "2,4 GB" and "5 nov, 14:08" (live answers, 3 to 6 Nov 2026): the prompt and the rest of the records are in
/// this English form, and a model reading "2,4" as two thousand four, or a 24-hour time beside 12-hour ones, answers wrong.
/// Only the formats are fixed here; the words a language pack translates stay as they are.
/// </summary>
public static class AiText
{
    /// <summary>English names, a 12-hour clock, a point for decimals and a comma between thousands.</summary>
    public static readonly CultureInfo Culture = Make();

    private static CultureInfo Make()
    {
        var c = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        var f = c.DateTimeFormat;
        f.ShortTimePattern = "h:mm tt";
        f.LongTimePattern = "h:mm:ss tt";
        f.ShortDatePattern = "d MMM yyyy";
        f.LongDatePattern = "dddd d MMMM yyyy";
        f.FullDateTimePattern = "dddd d MMMM yyyy h:mm:ss tt";
        f.MonthDayPattern = "d MMMM";
        f.YearMonthPattern = "MMMM yyyy";
        return CultureInfo.ReadOnly(c);
    }

    /// <summary>
    /// <paramref name="read"/> run with the fixed formats (and the 12-hour clock), then the thread's own back: every
    /// look-up, and the app's readers it calls on its own thread (live sensors, drives).
    /// </summary>
    public static T Fixed<T>(Func<T> read)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = Culture;
        try
        {
            using (TimeText.UseHere(ClockStyle.H12)) return read();
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }
}
