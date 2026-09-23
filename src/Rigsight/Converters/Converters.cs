using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Core.Apps;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Services;

namespace Rigsight.Converters;

internal static class Brushes2
{
    public static SolidColorBrush Frozen(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush FromHex(string hex, double opacity = 1)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Maps a temperature in °C to a color. ConverterParameter is "cool,good,warm" thresholds
/// (default "45,70,85"): below cool = blue, below good = green, below warm = amber, else red.
/// </summary>
public sealed class TempToBrushConverter : IValueConverter
{
    public static readonly SolidColorBrush Cool = Brushes2.Frozen(0x38, 0xBD, 0xF8);
    public static readonly SolidColorBrush Good = Brushes2.Frozen(0x34, 0xD3, 0x99);
    public static readonly SolidColorBrush Warm = Brushes2.Frozen(0xFB, 0xBF, 0x24);
    public static readonly SolidColorBrush Hot = Brushes2.Frozen(0xF8, 0x71, 0x71);
    public static readonly SolidColorBrush None = Brushes2.Frozen(0x5B, 0x64, 0x7A);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double c || double.IsNaN(c)) return None;

        double cool = 45, good = 70, warm = 85;
        if (parameter is string p && p.Split(',') is { Length: 3 } parts)
        {
            cool = double.Parse(parts[0], CultureInfo.InvariantCulture);
            good = double.Parse(parts[1], CultureInfo.InvariantCulture);
            warm = double.Parse(parts[2], CultureInfo.InvariantCulture);
        }
        return c < cool ? Cool : c < good ? Good : c < warm ? Warm : Hot;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>True when the bound value's string form equals ConverterParameter. Used for radio-button groups.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null) return Binding.DoNothing;
        var s = parameter.ToString()!;
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (type == typeof(string)) return s;
        if (type.IsEnum) return Enum.Parse(type, s);
        return System.Convert.ChangeType(s, type, CultureInfo.InvariantCulture);
    }
}

/// <summary>Visible when the value's string form equals ConverterParameter.</summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Bool (or non-null / non-empty / non-zero) to Visibility. ConverterParameter "Invert" flips it.</summary>
public sealed class VisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value switch
        {
            bool b => b,
            string s => !string.IsNullOrEmpty(s),
            int i => i != 0,
            double d => d != 0 && !double.IsNaN(d),
            System.Collections.ICollection c => c.Count > 0,
            null => false,
            _ => true,
        };
        if (parameter as string == "Invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Scales a numeric value by ConverterParameter (e.g. percentage → pixel height).</summary>
public sealed class ScaleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double v = value switch { double d when !double.IsNaN(d) => d, float f => f, int i => i, _ => 0 };
        double factor = parameter is string p ? double.Parse(p, CultureInfo.InvariantCulture) : 1;
        return Math.Max(0, v * factor);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Seconds → "4h 05m".</summary>
public sealed class DurationConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double s ? (s <= 0 && parameter as string == "Dash" ? "—" : Units.Duration(s)) : "—";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>°C → "46°" in the user's unit.</summary>
public sealed class TempShortConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Units.TempShort(value as double?);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>MB → "1.2 GB".</summary>
public sealed class MegabytesConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double mb ? Units.Megabytes(mb) : "—";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Bytes (long) → "12.3 GB".</summary>
public sealed class BytesConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        long l => Units.Bytes(l),
        double d => Units.Bytes(d),
        _ => "—",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Exe path → app icon.</summary>
public sealed class IconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => IconCache.Get(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>AppCategory → its color (ConverterParameter "Soft" for a translucent version).</summary>
public sealed class CategoryBrushConverter : IValueConverter
{
    private static readonly Dictionary<(AppCategory, bool), SolidColorBrush> Cache = [];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var category = value is AppCategory c ? c : AppCategory.Other;
        bool soft = parameter as string == "Soft";
        if (!Cache.TryGetValue((category, soft), out var brush))
            Cache[(category, soft)] = brush = Brushes2.FromHex(AppCatalog.Color(category), soft ? 0.16 : 1);
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>AppCategory → "Games", "Browsing"…</summary>
public sealed class CategoryLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is AppCategory c ? AppCatalog.Label(c) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Insight tone → accent color.</summary>
public sealed class ToneBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Neutral = Brushes2.Frozen(0x5B, 0x8C, 0xFF);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        InsightTone.Good => TempToBrushConverter.Good,
        InsightTone.Warn => TempToBrushConverter.Warm,
        InsightTone.Hot => TempToBrushConverter.Hot,
        _ => Neutral,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Converts a temperature in °C to the display unit, formatted with ConverterParameter (default "0").</summary>
public sealed class TempDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double c ? Units.Temp(c).ToString(parameter as string ?? "0", culture) + Units.TempUnit : "—";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Percent (0–100) of a value relative to ConverterParameter maximum, used for simple bars.</summary>
public sealed class FractionConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double v || values[1] is not double max || max <= 0) return 0.0;
        return Math.Clamp(v / max * 100, 0, 100);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}
