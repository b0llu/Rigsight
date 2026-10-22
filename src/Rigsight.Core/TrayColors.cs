using System.Globalization;
using Rigsight.Core.Settings;

namespace Rigsight.Core;

/// <summary>
/// The colours a taskbar reading can be given of its own (Taskbar page): its number's, and its name's, which also colours
/// its mark and its graph. Unpicked, a reading looks as it always has: a temperature by how warm it is, any other number
/// in the taskbar's text colour, the name in its part's colour (see <see cref="TrayParts.Color"/>). Shared by the agent,
/// which draws the readings, and the page, which previews them.
/// </summary>
public static class TrayColors
{
    /// <summary>A number coloured by how warm it is (temperatures only), as RigSight colours temperatures everywhere.</summary>
    public const string Heat = "heat";

    /// <summary>
    /// The quick picks, in the picker's order. None is green, amber or red, so a reading never looks like a warning by
    /// a quick pick; any colour at all can still be set as a custom one.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Hex)> Palette =
    [
        (Lang.Key("Blue"), "#5B8CFF"), (Lang.Key("Indigo"), "#818CF8"), (Lang.Key("Cyan"), "#22D3EE"), (Lang.Key("Violet"), "#B18CFF"), (Lang.Key("Lilac"), "#C4B5FD"),
        (Lang.Key("Magenta"), "#E879F9"), (Lang.Key("Pink"), "#F472B6"), (Lang.Key("White"), "#FFFFFF"), (Lang.Key("Silver"), "#C8C8C8"), (Lang.Key("Grey"), "#949494"),
    ];

    /// <summary>"#5B8CFF" (or "5b8cff") as red, green, blue; null for anything else.</summary>
    public static (byte R, byte G, byte B)? Parse(string? hex)
    {
        if (hex is null) return null;
        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int n)) return null;
        return ((byte)(n >> 16), (byte)(n >> 8 & 255), (byte)(n & 255));
    }

    /// <summary>A colour as its code: "#5B8CFF".</summary>
    public static string Hex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

    /// <summary>
    /// A picked colour, made readable on the taskbar: on a light one, deepened until it stands out from it (white
    /// would vanish); on a dark one, lifted until it does (a near-black would). Colours that already read well are kept.
    /// </summary>
    public static (byte R, byte G, byte B) ForTaskbar((byte R, byte G, byte B) c, bool light)
    {
        // The taskbar's own colour: Windows 11's light and dark.
        double back = light ? Luminance(238, 238, 238) : Luminance(28, 28, 28);
        var now = c;
        for (int step = 1; step <= 20 && Contrast(Luminance(now.R, now.G, now.B), back) < 3; step++)
        {
            double k = step / 20.0;
            now = light
                ? ((byte)(c.R * (1 - k)), (byte)(c.G * (1 - k)), (byte)(c.B * (1 - k)))
                : ((byte)(c.R + (255 - c.R) * k), (byte)(c.G + (255 - c.G) * k), (byte)(c.B + (255 - c.B) * k));
        }
        return now;
    }

    /// <summary>The number's colour picked for a reading: null (as RigSight colours it), <see cref="Heat"/> or a code.</summary>
    public static string? NumberOf(RigsightSettings s, string id) => s.TrayColors.GetValueOrDefault(id)?.Number;

    /// <summary>The name's colour picked for a reading (a code), or null for its part's.</summary>
    public static string? NameOf(RigsightSettings s, string id) => s.TrayColors.GetValueOrDefault(id)?.Name;

    private static double Luminance(byte r, byte g, byte b)
    {
        static double L(byte v) { double c = v / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        return 0.2126 * L(r) + 0.7152 * L(g) + 0.0722 * L(b);
    }

    private static double Contrast(double a, double b) => (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
}
