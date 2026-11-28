using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Rigsight.Core;

namespace Rigsight.Controls;

/// <summary>
/// The small kind tags ("TEMPERATURE", "BATTERY") on All sensors' rows and in the taskbar's reading picker: one width for
/// every tag, so the names beside them start in one line. As wide as the widest single word of any tag in the language
/// in use; a tag of several words ("FAN CONTROL", "RETRASO DE CONTROLADORES") wraps onto a second line. At a fixed 44
/// and 58 they cut off "VOLTAGE", "BATTERY", "TEMPERATURE" and "FAN CONTROL".
/// </summary>
public static class KindTag
{
    /// <summary>The tag's padding, 6 each side.</summary>
    public static readonly Thickness Padding = new(6, 2, 6, 2);

    private static double? _width;

    /// <summary>The tag's width, its padding included (worked out once: the language is set before any page is built).</summary>
    public static double Width => _width ??= Measure();

    /// <summary>All sensors' first column: the tag, its 6 from the row's edge and a little room before the name.</summary>
    public static GridLength Column => new(Width + 10);

    /// <summary>
    /// All sensors' name column at its narrowest: what the tag leaves of the 188 the two columns had (58 and 130), so a
    /// wider tag narrows the names, which end in "…", rather than pushing a column off a small window.
    /// </summary>
    public static double NameMinWidth => Math.Max(80, 188 - Column.Value);

    private static double Measure()
    {
        var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        double widest = Enum.GetValues<SensorKind>()
            .SelectMany(k => Units.TypeLabel(k).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Max(word => new FormattedText(word, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, TextSize.Tiny, Brushes.Black, 1.0)
                .WidthIncludingTrailingWhitespace);
        return Math.Ceiling(widest) + Padding.Left + Padding.Right + 1;
    }
}
