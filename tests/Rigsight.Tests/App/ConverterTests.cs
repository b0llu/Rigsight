using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Rigsight.Controls;
using Rigsight.Converters;
using DurationConverter = Rigsight.Converters.DurationConverter;
using Rigsight.Core;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Models;
using Rigsight.Services;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.App;

/// <summary>Every converter the pages use: normal values, limits, nothing, NaN and the wrong kind of value.</summary>
[Collection("UI")]
public sealed class ConverterTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    private static object? Convert(IValueConverter c, object? value, object? parameter = null, Type? target = null) =>
        Ui.Run(() => c.Convert(value!, target ?? typeof(object), parameter!, Culture));

    private static Color ThemeColor(string key) => Ui.Run(() => (Color)Application.Current.FindResource(key));

    /// <summary>Runs <paramref name="check"/> in the dark and in the light theme.</summary>
    private static void InBothThemes(Action<string> check)
    {
        try
        {
            foreach (var theme in new[] { ThemeManager.Dark, ThemeManager.Light })
            {
                Ui.Run(() => ThemeManager.Apply(theme));
                check(theme);
            }
        }
        finally
        {
            Ui.Run(() => ThemeManager.Apply(ThemeManager.Dark));
        }
    }

    // ── Temperatures to colours ──────────────────────────────────────────

    [Theory]
    [InlineData(-10.0, "CoolColor")]
    [InlineData(0.0, "CoolColor")]
    [InlineData(44.9, "CoolColor")]
    [InlineData(45.0, "GoodColor")]
    [InlineData(69.9, "GoodColor")]
    [InlineData(70.0, "WarmColor")]
    [InlineData(84.9, "WarmColor")]
    [InlineData(85.0, "HotColor")]
    [InlineData(150.0, "HotColor")]
    [InlineData(double.PositiveInfinity, "HotColor")]
    [InlineData(double.NegativeInfinity, "CoolColor")]
    public void Temperature_colour_thresholds_in_both_themes(double celsius, string colorKey)
    {
        var c = new TempToBrushConverter();
        InBothThemes(_ =>
        {
            var brush = Assert.IsType<SolidColorBrush>(Convert(c, celsius));
            Assert.Equal(ThemeColor(colorKey), brush.Color);
            Assert.True(brush.IsFrozen);
        });
    }

    [Fact]
    public void Temperature_colours_differ_between_themes()
    {
        var c = new TempToBrushConverter();
        var colors = new Dictionary<string, Color>();
        InBothThemes(theme => colors[theme] = ((SolidColorBrush)Convert(c, 90.0)!).Color);
        Assert.NotEqual(colors[ThemeManager.Dark], colors[ThemeManager.Light]);
    }

    [Theory]
    [InlineData(29.0, "CoolColor")]
    [InlineData(30.0, "GoodColor")]
    [InlineData(50.0, "WarmColor")]
    [InlineData(60.0, "HotColor")]
    public void Temperature_thresholds_can_be_given(double celsius, string colorKey) =>
        Assert.Equal(ThemeColor(colorKey), ((SolidColorBrush)Convert(new TempToBrushConverter(), celsius, "30,50,60")!).Color);

    [Theory]
    [InlineData("30,50")]
    [InlineData("")]
    [InlineData(5)]
    public void Incomplete_thresholds_use_the_defaults(object parameter) =>
        Assert.Equal(ThemeColor("GoodColor"), ((SolidColorBrush)Convert(new TempToBrushConverter(), 50.0, parameter)!).Color);

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData("80")]
    [InlineData(80)]    // an int isn't a reading
    [InlineData(80f)]
    public void No_temperature_is_faint(object? value) =>
        Assert.Same(Ui.Run(() => TempToBrushConverter.None), Convert(new TempToBrushConverter(), value));

    // ── Equality (radio buttons) ─────────────────────────────────────────

    [Theory]
    [InlineData("Light", "light", true)]
    [InlineData(ReportRange.Week, "Week", true)]
    [InlineData(300, "300", true)]
    [InlineData(300, "3600", false)]
    [InlineData(null, "x", false)]
    [InlineData(null, null, true)]
    [InlineData(true, "True", true)]
    public void Equals_compares_as_text_ignoring_case(object? value, object? parameter, bool expected)
    {
        Assert.Equal(expected, Convert(new EqualsConverter(), value, parameter));
        Assert.Equal(expected ? Visibility.Visible : Visibility.Collapsed, Convert(new EqualsToVisibilityConverter(), value, parameter));
    }

    [Fact]
    public void Equals_back_gives_the_choice_in_the_bound_type()
    {
        var c = new EqualsConverter();
        Assert.Equal("Light", c.ConvertBack(true, typeof(string), "Light", Culture));
        Assert.Equal(ReportRange.Month, c.ConvertBack(true, typeof(ReportRange), "Month", Culture));
        Assert.Equal(NotificationStyle.Windows, c.ConvertBack(true, typeof(NotificationStyle?), "Windows", Culture));
        Assert.Equal(3600, c.ConvertBack(true, typeof(int), "3600", Culture));
        Assert.Equal(1.25, c.ConvertBack(true, typeof(double), "1.25", Culture));
        Assert.Equal(90, c.ConvertBack(true, typeof(int?), "90", Culture));
        // Unchecking a radio button isn't a choice.
        Assert.Same(Binding.DoNothing, c.ConvertBack(false, typeof(string), "Light", Culture));
        Assert.Same(Binding.DoNothing, c.ConvertBack(null, typeof(string), "Light", Culture));
        Assert.Same(Binding.DoNothing, c.ConvertBack(true, typeof(string), null, Culture));
    }

    // ── Visibility ───────────────────────────────────────────────────────

    public static TheoryData<object?, bool> VisibleValues => new()
    {
        { true, true }, { false, false },
        { "text", true }, { "", false },
        { 1, true }, { 0, false }, { -1, true },
        { 0.5, true }, { 0.0, false }, { double.NaN, false },
        { new List<int> { 1 }, true }, { new List<int>(), false }, { new int[0], false },
        { null, false },
        { new object(), true },
        { 5L, true }, // other numbers: something is there
    };

    [Theory]
    [MemberData(nameof(VisibleValues))]
    public void Visibility_from_any_value(object? value, bool visible)
    {
        var c = new VisibilityConverter();
        Assert.Equal(visible ? Visibility.Visible : Visibility.Collapsed, Convert(c, value));
        Assert.Equal(visible ? Visibility.Collapsed : Visibility.Visible, Convert(c, value, "Invert"));
        Assert.Equal(visible ? Visibility.Visible : Visibility.Collapsed, Convert(c, value, "invert")); // only "Invert" inverts
    }

    // ── Numbers ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(50.0, "0.4", 20.0)]
    [InlineData(50.0, null, 50.0)]
    [InlineData(-5.0, "2", 0.0)]
    [InlineData(double.NaN, "2", 0.0)]
    [InlineData(3f, "2", 6.0)]
    [InlineData(7, "0.5", 3.5)]
    [InlineData("7", "2", 0.0)]
    [InlineData(null, "2", 0.0)]
    [InlineData(100.0, "0", 0.0)]
    public void Scale(object? value, string? factor, double expected) =>
        Assert.Equal(expected, (double)Convert(new ScaleConverter(), value, factor)!, 9);

    [Theory]
    [InlineData(0.0, null, "0s")]
    [InlineData(0.0, "Dash", "—")]
    [InlineData(-3.0, "Dash", "—")]
    [InlineData(-3.0, null, "0s")]
    [InlineData(45.0, null, "45s")]
    [InlineData(60.0, "Dash", "1m")]
    [InlineData(3599.0, null, "59m")]
    [InlineData(3600.0, null, "1h 00m")]
    [InlineData(14_700.0, null, "4h 05m")]
    [InlineData(360_000.0, null, "100h 00m")]
    [InlineData(double.NaN, null, "0s")]
    [InlineData(45, null, "—")]
    [InlineData(null, null, "—")]
    public void Duration(object? seconds, string? parameter, string expected) =>
        Assert.Equal(expected, Convert(new DurationConverter(), seconds, parameter));

    [Theory]
    [InlineData(512.0, "512 MB")]
    [InlineData(1023.4, "1023 MB")]
    [InlineData(1024.0, "1.0 GB")]
    [InlineData(20_480.0, "20.0 GB")]
    [InlineData(0.0, "0 MB")]
    [InlineData(512, "—")]
    [InlineData(null, "—")]
    public void Megabytes(object? mb, string expected) => Assert.Equal(expected, Convert(new MegabytesConverter(), mb));

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(5L << 20, "5 MB")]
    [InlineData(3L << 30, "3.0 GB")]
    [InlineData(2L << 40, "2.00 TB")]
    [InlineData(1536.0, "2 KB")]
    [InlineData(5, "—")]
    [InlineData("5", "—")]
    [InlineData(null, "—")]
    public void Bytes(object? bytes, string expected) => Assert.Equal(expected, Convert(new BytesConverter(), bytes));

    [Fact]
    public void Temperatures_in_the_users_unit()
    {
        try
        {
            Assert.Equal("46°", Convert(new TempShortConverter(), 46.4));
            Assert.Equal("—", Convert(new TempShortConverter(), null));
            Assert.Equal("—", Convert(new TempShortConverter(), "46"));
            Assert.Equal("—", Convert(new TempShortConverter(), double.NaN));
            Assert.Equal("50°C", Convert(new TempDisplayConverter(), 50.0));
            Assert.Equal("50.4°C", Convert(new TempDisplayConverter(), 50.4, "0.0"));
            Assert.Equal("—", Convert(new TempDisplayConverter(), null));
            Assert.Equal("—", Convert(new TempDisplayConverter(), 50));
            Ui.Run(() => Units.Fahrenheit = true);
            Assert.Equal("115°", Convert(new TempShortConverter(), 46.0));
            Assert.Equal("122°F", Convert(new TempDisplayConverter(), 50.0));
            Assert.Equal("-40°F", Convert(new TempDisplayConverter(), -40.0));
        }
        finally
        {
            Ui.Run(() => Units.Fahrenheit = false);
        }
    }

    [Theory]
    [InlineData(50.0, 200.0, 25.0)]
    [InlineData(300.0, 200.0, 100.0)]
    [InlineData(-5.0, 200.0, 0.0)]
    [InlineData(5.0, 0.0, 0.0)]
    [InlineData(5.0, -1.0, 0.0)]
    public void Fraction(double value, double max, double expected) =>
        Assert.Equal(expected, (double)new FractionConverter().Convert([value, max], typeof(double), null, Culture), 9);

    [Fact]
    public void Fraction_of_something_else_is_nothing()
    {
        var c = new FractionConverter();
        Assert.Equal(0.0, c.Convert([5, 10.0], typeof(double), null, Culture));
        Assert.Equal(0.0, c.Convert([5.0], typeof(double), null, Culture));
        Assert.Equal(0.0, c.Convert([], typeof(double), null, Culture));
        Assert.Equal(0.0, c.Convert([DependencyProperty.UnsetValue, 10.0], typeof(double), null, Culture));
        Assert.Empty(c.ConvertBack(5.0, [typeof(double)], null, Culture));
    }

    // ── Apps ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("chrome", "C")]
    [InlineData("  spotify", "S")]
    [InlineData("élan", "É")]
    [InlineData("7-Zip", "7")]
    [InlineData("", "?")]
    [InlineData("   ", "?")]
    [InlineData(null, "?")]
    [InlineData(42, "?")]
    public void Initial_of_an_app(object? name, string expected) => Assert.Equal(expected, Convert(new InitialConverter(), name));

    [Fact]
    public void Category_colours_and_labels()
    {
        var brushes = new CategoryBrushConverter();
        var labels = new CategoryLabelConverter();
        foreach (var category in Enum.GetValues<AppCategory>())
        {
            var brush = (SolidColorBrush)Convert(brushes, category)!;
            Assert.Equal((Color)ColorConverter.ConvertFromString(Rigsight.Core.Apps.AppCatalog.Color(category)), brush.Color);
            Assert.Equal(1, brush.Opacity);
            Assert.True(brush.IsFrozen);
            Assert.Same(brush, Convert(brushes, category)); // made once
            var soft = (SolidColorBrush)Convert(brushes, category, "Soft")!;
            Assert.Equal(brush.Color, soft.Color);
            Assert.Equal(0.16, soft.Opacity, 6);
            Assert.Equal(Rigsight.Core.Apps.AppCatalog.Label(category), Convert(labels, category));
            Assert.Equal(CategoryLabelConverter.Label(category), Convert(labels, category));
        }
        Assert.Same(Convert(brushes, AppCategory.Other), Convert(brushes, "Games"));
        Assert.Same(Convert(brushes, AppCategory.Other), Convert(brushes, null));
        Assert.Equal("", Convert(labels, "Game"));
        Assert.Equal("Games", Convert(labels, AppCategory.Game));
        Assert.Equal("Chat & calls", Convert(labels, AppCategory.Communication));
    }

    [Fact]
    public void Icons_for_paths()
    {
        var c = new IconConverter();
        Assert.Null(Convert(c, null));
        Assert.Null(Convert(c, 5));
        Assert.Null(Convert(c, @"C:\no\such\app.exe"));
        Assert.IsAssignableFrom<ImageSource>(Convert(c, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")));
    }

    // ── Palette brushes ──────────────────────────────────────────────────

    [Theory]
    [InlineData(InsightTone.Good, "GoodColor")]
    [InlineData(InsightTone.Warn, "WarmColor")]
    [InlineData(InsightTone.Hot, "HotColor")]
    [InlineData(InsightTone.Neutral, "CpuColor")]
    [InlineData(null, "CpuColor")]
    [InlineData("Hot", "CpuColor")]
    public void Insight_tone_colours(object? tone, string colorKey)
    {
        InBothThemes(_ => Assert.Equal(ThemeColor(colorKey), ((SolidColorBrush)Convert(new ToneBrushConverter(), tone)!).Color));
    }

    [Theory]
    [InlineData(CrashSeverity.Critical, "HotColor")]
    [InlineData(CrashSeverity.Serious, "OrangeColor")]
    [InlineData(CrashSeverity.Minor, "WarmColor")]
    [InlineData(CrashSeverity.Info, "FaintColor")]
    [InlineData(null, "FaintColor")]
    public void Severity_colours_and_their_soft_backgrounds(object? severity, string colorKey)
    {
        var c = new SeverityBrushConverter();
        InBothThemes(_ =>
        {
            var solid = (SolidColorBrush)Convert(c, severity)!;
            Assert.Equal(ThemeColor(colorKey), solid.Color);
            var soft = (SolidColorBrush)Convert(c, severity, "soft")!;
            Assert.Equal(solid.Color, soft.Color);
            Assert.Equal(0.14, soft.Opacity, 6);
            Assert.True(soft.IsFrozen);
            Assert.Same(solid, Convert(c, severity, "Soft")); // only "soft" is soft
        });
    }

    public static TheoryData<object?, string> Kinds => new()
    {
        { SensorKind.Temperature, "OrangeColor" }, { SensorKind.Load, "CpuColor" }, { SensorKind.Clock, "PurpleColor" },
        { SensorKind.Frequency, "PurpleColor" }, { SensorKind.Timing, "PurpleColor" }, { SensorKind.Power, "WarmColor" },
        { SensorKind.Energy, "WarmColor" }, { SensorKind.Current, "WarmColor" }, { SensorKind.Voltage, "CoolColor" },
        { SensorKind.Fan, "GoodColor" }, { SensorKind.Flow, "GoodColor" }, { SensorKind.Control, "GoodColor" },
        { SensorKind.Data, "PinkColor" }, { SensorKind.SmallData, "PinkColor" }, { SensorKind.Throughput, "PinkColor" },
        { SensorKind.Level, "MutedColor" }, { SensorKind.Factor, "MutedColor" }, { SensorKind.Noise, "MutedColor" },
        { "Cpu", "CpuColor" }, { "GpuNvidia", "GpuColor" }, { "GpuAmd", "GpuColor" }, { "GpuIntel", "GpuColor" },
        { "Memory", "PurpleColor" }, { "Storage", "CoolColor" }, { "Motherboard", "OrangeColor" }, { "SuperIO", "OrangeColor" },
        { "EmbeddedController", "OrangeColor" }, { "Cooler", "GoodColor" }, { "Psu", "WarmColor" }, { "Battery", "WarmColor" },
        { "Network", "PinkColor" }, { "Toaster", "MutedColor" }, { null, "MutedColor" }, { 3, "MutedColor" },
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Kind_and_hardware_colours_follow_the_theme(object? value, string colorKey)
    {
        Assert.Equal(colorKey, KindBrushConverter.ColorKey(value));
        var c = new KindBrushConverter();
        InBothThemes(_ =>
        {
            var brush = (SolidColorBrush)Convert(c, value)!;
            Assert.Equal(ThemeColor(colorKey), brush.Color);
            Assert.Same(brush, Convert(c, value)); // cached per theme
            Assert.Equal(0.14, ((SolidColorBrush)Convert(c, value, "soft")!).Opacity, 6);
        });
    }

    [Fact]
    public void Kind_colours_are_kept_for_the_current_theme_only()
    {
        var c = new KindBrushConverter();
        var cache = (System.Collections.IDictionary)typeof(KindBrushConverter).GetField("Cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        try
        {
            for (int i = 0; i < 20; i++)
            {
                Ui.Run(() => ThemeManager.Apply(i % 2 == 0 ? ThemeManager.Light : ThemeManager.Dark));
                foreach (var kind in Enum.GetValues<SensorKind>()) { Convert(c, kind); Convert(c, kind, "soft"); }
            }
            // Eight colours, solid and soft: not eight more for every theme change.
            Assert.InRange(Ui.Run(() => cache.Count), 1, 16);
        }
        finally
        {
            Ui.Run(() => ThemeManager.Apply(ThemeManager.Dark));
        }
    }

    [Fact]
    public void Every_sensor_kind_has_a_colour()
    {
        foreach (var kind in Enum.GetValues<SensorKind>())
            Assert.True(Ui.Run(() => Application.Current.TryFindResource(KindBrushConverter.ColorKey(kind))) is Color, kind.ToString());
    }

    [Fact]
    public void Palette_brush_by_key()
    {
        var c = new ResourceBrushConverter();
        InBothThemes(_ =>
        {
            Assert.Same(Ui.Run(() => Application.Current.FindResource("HotBrush")), Convert(c, "HotBrush"));
            Assert.Null(Convert(c, "NoSuchBrush"));
            Assert.Null(Convert(c, "CoolColor")); // a colour, not a brush
            Assert.Null(Convert(c, null));
            Assert.Null(Convert(c, 7));
        });
    }

    [Fact]
    public void Nothing_converts_back()
    {
        IValueConverter[] oneWay =
        [
            new TempToBrushConverter(), new EqualsToVisibilityConverter(), new VisibilityConverter(), new ScaleConverter(), new DurationConverter(),
            new TempShortConverter(), new MegabytesConverter(), new BytesConverter(), new IconConverter(), new CategoryBrushConverter(),
            new InitialConverter(), new CategoryLabelConverter(), new ToneBrushConverter(), new SeverityBrushConverter(), new KindBrushConverter(),
            new ResourceBrushConverter(), new TempDisplayConverter(),
        ];
        foreach (var c in oneWay) Assert.Same(Binding.DoNothing, c.ConvertBack(Visibility.Visible, typeof(object), null!, Culture));
    }

    [Fact]
    public void Every_converter_the_theme_declares_is_tested_here()
    {
        Type[] tested =
        [
            typeof(TempToBrushConverter), typeof(EqualsConverter), typeof(EqualsToVisibilityConverter), typeof(VisibilityConverter), typeof(ScaleConverter),
            typeof(DurationConverter), typeof(TempShortConverter), typeof(MegabytesConverter), typeof(BytesConverter), typeof(IconConverter),
            typeof(CategoryBrushConverter), typeof(InitialConverter), typeof(CategoryLabelConverter), typeof(ToneBrushConverter),
            typeof(SeverityBrushConverter), typeof(KindBrushConverter), typeof(ResourceBrushConverter), typeof(TempDisplayConverter), typeof(FractionConverter),
        ];
        var declared = Ui.Run(() => Application.Current.Resources.MergedDictionaries
            .Where(d => d.Source?.OriginalString.EndsWith("Themes/Theme.xaml") == true)
            .SelectMany(d => d.Values.OfType<object>()).Where(v => v is IValueConverter or IMultiValueConverter).Select(v => v.GetType()).Distinct().ToList());
        Assert.NotEmpty(declared);
        Assert.All(declared, t => Assert.Contains(t, tested));
        // And nothing in the app's converter file goes untested.
        var all = typeof(TempToBrushConverter).Assembly.GetTypes().Where(t => t.Namespace == "Rigsight.Converters" && t.IsPublic).ToList();
        Assert.All(all, t => Assert.Contains(t, tested));
    }
}
