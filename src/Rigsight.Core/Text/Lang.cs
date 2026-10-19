using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Rigsight.Core;

/// <summary>
/// The app's words in the language the person picked. English is built in and is the key: <c>Lang.T("Crashes")</c>,
/// <c>Lang.T($"{app} crashed at {time}")</c>. A language pack (see <see cref="LanguagePack"/>) maps each English text
/// to its translation; anything the pack doesn't have, or has in a form that wouldn't fit, stays English. With no
/// pack loaded the text comes out exactly as the plain string would.
/// </summary>
/// <remarks>
/// An interpolated text is looked up by its template, the holes numbered in order: <c>$"{app} crashed at {time:t}"</c>
/// is the entry <c>"{0} crashed at {1:t}"</c>. A sentence is always translated whole, never put together from
/// translated words, since another language orders and agrees its words differently. Numbers and dates are formatted
/// by Windows' own region settings, as before; only the words change.
/// </remarks>
public static class Lang
{
    private static volatile LanguagePack? _pack;

    /// <summary>A pack for one flow of work only (tests run side by side, each in its own language).</summary>
    private static readonly AsyncLocal<Override?> Local = new();

    private sealed record Override(LanguagePack? Pack);

    /// <summary>The pack in use, or null for English.</summary>
    public static LanguagePack? Pack => Local.Value is { } here ? here.Pack : _pack;

    /// <summary>The language in use: "en" or a pack's code.</summary>
    public static string Code => Pack?.Code ?? LanguagePack.English;

    /// <summary>Whether a pack is in use (a cheap check for code that only needs to know the text isn't English).</summary>
    public static bool Translating => Pack is not null;

    /// <summary>Puts a pack in use; null goes back to English.</summary>
    public static void Use(LanguagePack? pack) => _pack = pack;

    /// <summary>Uses <paramref name="pack"/> (null: English) in this flow of work until disposed, leaving every other alone.</summary>
    internal static IDisposable UseHere(LanguagePack? pack)
    {
        var before = Local.Value;
        Local.Value = new Override(pack);
        return new Restore(() => Local.Value = before);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>A text with nothing in it to fill.</summary>
    public static string T(string english)
    {
        var pack = Pack;
        return pack is not null && pack.Strings.TryGetValue(english, out var text) ? text : english;
    }

    /// <summary>
    /// The same English with another meaning in another place ("Up" for a connection, not an upload): the pack holds it
    /// as "‹context›English" and may say it differently; without that entry it's translated like the plain text.
    /// </summary>
    public static string C(string context, string english)
    {
        var pack = Pack;
        if (pack is null) return english;
        return pack.Strings.TryGetValue(Context(context, english), out var text) ? text : T(english);
    }

    /// <summary>A text with holes and a meaning of its own here (see <see cref="C(string, string)"/>).</summary>
    public static string C(string context, [InterpolatedStringHandlerArgument("context")] ref LangText text) => text.ToStringAndClear();

    /// <summary>The pack's key for a text with a context: "‹time›At {0}".</summary>
    public static string Context(string context, string english) => $"‹{context}›{english}";

    /// <summary>
    /// Marks an English text that's kept in a table and translated later with <see cref="T(string)"/>, so the language
    /// tool lists it. Returns it unchanged.
    /// </summary>
    public static string Key(string english) => english;

    /// <summary>A text with holes: translated by its template, then filled.</summary>
    public static string T(ref LangText text) => text.ToStringAndClear();

    /// <summary>
    /// A template held in a variable or table, filled with <paramref name="args"/>: <c>Lang.F("{0} crashed", app)</c>.
    /// Prefer <see cref="T(ref LangText)"/>; this is for templates that are data rather than code.
    /// </summary>
    public static string F(string template, params object?[] args)
    {
        var pack = Pack;
        if (pack is not null && pack.Strings.TryGetValue(template, out var text))
        {
            try { return string.Format(CultureInfo.CurrentCulture, text, args); }
            catch (FormatException) { /* checked when the pack loads; English below if it still doesn't fit */ }
        }
        return string.Format(CultureInfo.CurrentCulture, template, args);
    }

    /// <summary>
    /// A template held as data, written with its context ("‹stretch›{0} to {1}", as <see cref="Context"/> makes it):
    /// the pack's entry for it if there is one, else translated like the plain template. No context: as <see cref="F"/>.
    /// </summary>
    public static string FC(string template, params object?[] args)
    {
        if (!template.StartsWith('‹') || template.IndexOf('›') is not (> 1 and var end)) return F(template, args);
        string english = template[(end + 1)..];
        var pack = Pack;
        if (pack is not null && pack.Strings.TryGetValue(template, out var text))
        {
            try { return string.Format(CultureInfo.CurrentCulture, text, args); }
            catch (FormatException) { /* the plain template below */ }
        }
        return F(english, args);
    }

    /// <summary>The holes of a template, as their numbers: "{0} at {1:t}" → 0, 1.</summary>
    internal static readonly Regex Hole = new(@"(?<!\{)\{(\d+)(?:,-?\d+)?(?::[^{}]*)?\}(?!\})", RegexOptions.Compiled);

    /// <summary>The numbers of a template's holes, sorted with repeats removed.</summary>
    public static int[] Holes(string template) =>
        [.. Hole.Matches(template).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().Order()];
}

/// <summary>
/// The interpolated text behind <see cref="Lang.T(ref LangText)"/>. With no pack in use it builds the text just as a
/// plain interpolated string would; with one, it collects the template and the values, then fills the translation.
/// </summary>
[InterpolatedStringHandler]
public ref struct LangText
{
    private DefaultInterpolatedStringHandler _english;
    private readonly LanguagePack? _pack;
    private StringBuilder? _template;
    private List<object?>? _args;

    private readonly string? _context;

    public LangText(int literalLength, int formattedCount, string context) : this(literalLength, formattedCount) => _context = context;

    public LangText(int literalLength, int formattedCount)
    {
        _pack = Lang.Pack;
        if (_pack is null)
        {
            _english = new DefaultInterpolatedStringHandler(literalLength, formattedCount, CultureInfo.CurrentCulture);
        }
        else
        {
            _template = new StringBuilder(literalLength + formattedCount * 4);
            _args = new List<object?>(formattedCount);
        }
    }

    public void AppendLiteral(string s)
    {
        if (_template is null) _english.AppendLiteral(s);
        else _template.Append(s.Replace("{", "{{").Replace("}", "}}"));
    }

    public void AppendFormatted<T>(T value) => AppendFormatted(value, 0, null);

    public void AppendFormatted<T>(T value, string? format) => AppendFormatted(value, 0, format);

    public void AppendFormatted<T>(T value, int alignment) => AppendFormatted(value, alignment, null);

    public void AppendFormatted<T>(T value, int alignment, string? format)
    {
        if (_template is null)
        {
            _english.AppendFormatted(value, alignment, format);
            return;
        }
        _template.Append('{').Append(_args!.Count.ToString(CultureInfo.InvariantCulture));
        if (alignment != 0) _template.Append(',').Append(alignment.ToString(CultureInfo.InvariantCulture));
        if (format is not null) _template.Append(':').Append(format);
        _template.Append('}');
        _args.Add(value);
    }

    public void AppendFormatted(string? value) => AppendFormatted<string?>(value, 0, null);

    internal string ToStringAndClear()
    {
        if (_template is null) return _english.ToStringAndClear();
        string template = _template.ToString();
        object?[] args = [.. _args!];
        if (_context is not null && _pack!.Strings.TryGetValue(Lang.Context(_context, template), out var inContext))
        {
            try { return string.Format(CultureInfo.CurrentCulture, inContext, args); }
            catch (FormatException) { /* the plain text below */ }
        }
        if (_pack!.Strings.TryGetValue(template, out var text))
        {
            try { return string.Format(CultureInfo.CurrentCulture, text, args); }
            catch (FormatException) { /* falls back to English below */ }
        }
        return string.Format(CultureInfo.CurrentCulture, template, args);
    }
}
