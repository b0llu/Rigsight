using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Rigsight.Core.Settings;

namespace Rigsight.Core;

/// <summary>
/// How the app writes the time of day: as Windows does (its region's clock), or always 12-hour ("6:41 PM") or 24-hour
/// ("18:41") as picked in Settings. Times are written with 12-hour formats in the code ("h:mm tt", "ddd d MMM, h tt");
/// <see cref="Fit"/> turns those into the 24-hour form when that's the clock in use. Stored history is never text, so
/// every day, old or new, shows in the clock picked.
/// </summary>
public static partial class TimeText
{
    private static volatile ClockStyle _style;

    /// <summary>A clock for one flow of work only (tests run side by side, each with its own).</summary>
    private static readonly AsyncLocal<ClockStyle?> Local = new();

    /// <summary>The clock picked in Settings (the app and the agent each set it from their settings).</summary>
    public static ClockStyle Style { get => Local.Value ?? _style; set => _style = value; }

    /// <summary>Uses <paramref name="style"/> in this flow of work until disposed, leaving every other alone.</summary>
    internal static IDisposable UseHere(ClockStyle style)
    {
        var before = Local.Value;
        Local.Value = style;
        return new Restore(() => Local.Value = before);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>Whether times are written 24-hour: picked, or Windows' region writes its own short time that way.</summary>
    public static bool Is24 => Style switch
    {
        ClockStyle.H24 => true,
        ClockStyle.H12 => false,
        _ => CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('H'),
    };

    private static readonly ConcurrentDictionary<string, string> To24Cache = new();

    /// <summary>
    /// A 12-hour date and time format as the clock in use writes it: unchanged for 12-hour, else "h:mm tt" → "H:mm",
    /// "h:mm:ss tt" → "H:mm:ss" and an hour alone ("h tt", for chart hours) → "H:00". "t" and "T" follow the clock too.
    /// </summary>
    public static string Fit(string format)
    {
        if (format is "t" or "T") return Style == ClockStyle.Windows ? format : Is24 ? (format == "t" ? "H:mm" : "H:mm:ss") : (format == "t" ? "h:mm tt" : "h:mm:ss tt");
        if (!Is24 || !HasHour(format)) return format;
        return To24Cache.GetOrAdd(format, To24);
    }

    /// <summary>
    /// The word a language puts between a month's name and its year, empty for none: Spanish writes "noviembre de 2026"
    /// and "27 de noviembre de 2026" ("de" before the month comes from the month names, see LanguageStore).
    /// <see cref="At(DateTime, string, IFormatProvider?)"/> and filled templates put it into every "MMMM yyyy", so no date
    /// pattern needs translating. It can't go into the pattern itself: .NET reads any "d" in a pattern, even inside a
    /// quoted word, as a day and would then write "de noviembre" with no day before it.
    /// </summary>
    public static string YearJoin { get => LocalJoin.Value ?? _yearJoin; set => _yearJoin = value ?? ""; }

    private static volatile string _yearJoin = "";
    private static readonly AsyncLocal<string?> LocalJoin = new();

    /// <summary>Uses <paramref name="join"/> in this flow of work until disposed (for tests side by side).</summary>
    internal static IDisposable JoinHere(string join)
    {
        var before = LocalJoin.Value;
        LocalJoin.Value = join;
        return new Restore(() => LocalJoin.Value = before);
    }

    [GeneratedRegex(@"(?<!M)MMMM(?!M)\s+(?=yy)")]
    private static partial Regex MonthThenYear();

    /// <summary><paramref name="t"/> in <paramref name="format"/> (a 12-hour one, see <see cref="Fit"/>), as the clock in use writes it.</summary>
    public static string At(DateTime t, string format, IFormatProvider? culture = null) => Write(t, Fit(format), culture ?? CultureInfo.CurrentCulture);

    /// <inheritdoc cref="At(DateTime, string, IFormatProvider?)"/>
    public static string At(DateTimeOffset t, string format, IFormatProvider? culture = null) => Write(t.DateTime, Fit(format), culture ?? CultureInfo.CurrentCulture);

    /// <summary><paramref name="t"/> in a fitted <paramref name="format"/>, with the language's word between a month's name and its year.</summary>
    private static string Write(DateTime t, string format, IFormatProvider? culture)
    {
        string join = YearJoin;
        if (join.Length == 0 || !format.Contains("MMMM", StringComparison.Ordinal) || MonthThenYear().Match(format) is not { Success: true } m)
            return t.ToString(format, culture);
        // The month part and the rest apart, so the month part alone decides whether "de" goes before the month's name.
        int cut = m.Index + 4;
        return t.ToString(format[..cut], culture) + " " + join + " " + t.ToString(format[(m.Index + m.Length)..], culture);
    }

    /// <summary>The time of day: "6:41 PM" or "18:41".</summary>
    public static string Time(DateTime t) => At(t, "h:mm tt");

    /// <summary>A date or time value made to follow the clock when a template is filled (see <see cref="Lang"/>); anything else as it is.</summary>
    internal static object? Fitted(object? value) => value switch
    {
        DateTime d => new FittedTime(d),
        DateTimeOffset o => new FittedTime(o.LocalDateTime),
        _ => value,
    };

    private static bool HasHour(string format) => format.Contains('h') || format.Contains("tt", StringComparison.Ordinal);

    private static string To24(string format)
    {
        string f = Designator().Replace(format, "");
        // An hour with minutes after it keeps them; an hour alone ("6 PM") gets ":00" ("18:00"), as 24-hour clocks write it.
        f = HourBeforeMinutes().Replace(f, m => m.Value.ToUpperInvariant());
        f = HourAlone().Replace(f, m => m.Value.ToUpperInvariant() + ":00");
        return f.Trim();
    }

    [GeneratedRegex(@"\s*tt")]
    private static partial Regex Designator();

    [GeneratedRegex(@"(?<!h)hh?(?!h)(?=:)")]
    private static partial Regex HourBeforeMinutes();

    [GeneratedRegex(@"(?<!h)hh?(?![h:])")]
    private static partial Regex HourAlone();

    /// <summary>A date and time that writes itself through <see cref="Fit"/>, for templates filled by <c>string.Format</c>.</summary>
    private readonly record struct FittedTime(DateTime Value) : IFormattable
    {
        public string ToString(string? format, IFormatProvider? provider) => format is null ? Value.ToString(provider) : Write(Value, Fit(format), provider);
        public override string ToString() => Value.ToString();
    }
}
