using Rigsight.Core;

namespace Rigsight.Tests.Core;

[Collection("Core statics")]
public sealed class UnitsTests : IDisposable
{
    private readonly bool _fahrenheit = Units.Fahrenheit;
    private readonly CultureScope _culture = new("en-US");

    public UnitsTests() => Units.Fahrenheit = false;

    public void Dispose()
    {
        Units.Fahrenheit = _fahrenheit;
        _culture.Dispose();
    }

    [Theory]
    [InlineData(SensorKind.Temperature, 46.54, "46.5 °C")]
    [InlineData(SensorKind.Temperature, -5, "-5.0 °C")]
    [InlineData(SensorKind.Temperature, 0, "0.0 °C")]
    [InlineData(SensorKind.Load, 37.26, "37.3 %")]
    [InlineData(SensorKind.Load, 100, "100.0 %")]
    [InlineData(SensorKind.Control, 55, "55.0 %")]
    [InlineData(SensorKind.Level, 12.34, "12.3 %")]
    [InlineData(SensorKind.Humidity, 40.04, "40.0 %")]
    [InlineData(SensorKind.Clock, 4650, "4.65 GHz")]
    [InlineData(SensorKind.Clock, 1000, "1.00 GHz")]
    [InlineData(SensorKind.Clock, 999.4, "999 MHz")]
    [InlineData(SensorKind.Clock, 800, "800 MHz")]
    [InlineData(SensorKind.Clock, 0, "0 MHz")]
    [InlineData(SensorKind.Power, 120.44, "120.4 W")]
    [InlineData(SensorKind.Voltage, 1.25, "1.250 V")]
    [InlineData(SensorKind.Voltage, 12.1, "12.100 V")]
    [InlineData(SensorKind.Current, 3.5, "3.50 A")]
    [InlineData(SensorKind.Fan, 1200.4, "1200 RPM")]
    [InlineData(SensorKind.Fan, 0, "0 RPM")]
    [InlineData(SensorKind.Flow, 120, "120.0 L/h")]
    [InlineData(SensorKind.Data, 12.34, "12.3 GB")]
    [InlineData(SensorKind.SmallData, 512, "512 MB")]
    [InlineData(SensorKind.Throughput, 0, "0 B/s")]
    [InlineData(SensorKind.Throughput, 1536, "2 KB/s")]
    [InlineData(SensorKind.Throughput, 5 * 1048576.0, "5 MB/s")]
    [InlineData(SensorKind.Frequency, 60, "60 Hz")]
    [InlineData(SensorKind.Energy, 5000, "5000 mWh")]
    [InlineData(SensorKind.Noise, 35.2, "35 dBA")]
    [InlineData(SensorKind.Timing, 1.5, "1.50 ns")]
    [InlineData(SensorKind.Factor, 1.234, "1.23")]
    [InlineData(SensorKind.Factor, 2, "2")]
    [InlineData(SensorKind.TimeSpan, 12, "12")]
    [InlineData(SensorKind.Conductivity, 0.5, "0.5")]
    public void Format_shows_each_kind_with_its_unit(SensorKind kind, double value, string expected) =>
        Assert.Equal(expected, Units.Format(kind, value));

    public static TheoryData<SensorKind> AllKinds => [.. Enum.GetValues<SensorKind>()];

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Missing_or_NaN_readings_show_a_dash_in_every_form(SensorKind kind)
    {
        Assert.Equal("—", Units.Format(kind, null));
        Assert.Equal("—", Units.Format(kind, double.NaN));
        Assert.Equal("—", Units.Short(kind, null));
        Assert.Equal("—", Units.Short(kind, double.NaN));
        Assert.Equal("—", Units.Compact(kind, null));
        Assert.Equal("—", Units.Compact(kind, double.NaN));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Every_kind_formats_extreme_values_without_throwing(SensorKind kind)
    {
        foreach (var v in new[] { double.PositiveInfinity, double.NegativeInfinity, double.MaxValue, -double.MaxValue, 1e-300, -0.0 })
        {
            Assert.False(string.IsNullOrEmpty(Units.Format(kind, v)));
            Assert.False(string.IsNullOrEmpty(Units.Short(kind, v)));
            Assert.False(string.IsNullOrEmpty(Units.Compact(kind, v)));
        }
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Every_kind_has_a_type_label(SensorKind kind)
    {
        var label = Units.TypeLabel(kind);
        Assert.False(string.IsNullOrWhiteSpace(label));
        Assert.Equal(label.ToUpperInvariant(), label);
    }

    [Theory]
    [InlineData(SensorKind.Temperature, "TEMP")]
    [InlineData(SensorKind.Voltage, "VOLT")]
    [InlineData(SensorKind.Current, "AMPS")]
    [InlineData(SensorKind.Control, "CTRL")]
    [InlineData(SensorKind.Data, "DATA")]
    [InlineData(SensorKind.SmallData, "DATA")]
    [InlineData(SensorKind.Throughput, "RATE")]
    [InlineData(SensorKind.Factor, "INFO")]
    [InlineData(SensorKind.Load, "LOAD")]
    [InlineData(SensorKind.Fan, "FAN")]
    [InlineData(SensorKind.TimeSpan, "TIMESPAN")]
    public void Type_labels(SensorKind kind, string expected) => Assert.Equal(expected, Units.TypeLabel(kind));

    [Theory]
    [InlineData(SensorKind.Temperature, 46.6, "47°")]
    [InlineData(SensorKind.Temperature, 46.4, "46°")]
    [InlineData(SensorKind.Load, 37.4, "37%")]
    [InlineData(SensorKind.Control, 99.6, "100%")]
    [InlineData(SensorKind.Level, 5, "5%")]
    [InlineData(SensorKind.Power, 120.6, "121 W")]
    // Humidity isn't in Short's list: it falls back to the full form.
    [InlineData(SensorKind.Humidity, 37.4, "37.4 %")]
    [InlineData(SensorKind.Clock, 4650, "4.65 GHz")]
    [InlineData(SensorKind.Fan, 900, "900 RPM")]
    [InlineData(SensorKind.Voltage, 1.25, "1.250 V")]
    public void Short_forms(SensorKind kind, double value, string expected) => Assert.Equal(expected, Units.Short(kind, value));

    [Theory]
    [InlineData(SensorKind.Temperature, 46.4, "46°")]
    [InlineData(SensorKind.Clock, 4650, "4.65")]
    [InlineData(SensorKind.Clock, 1000, "1.00")]
    [InlineData(SensorKind.Clock, 999, "999")]
    [InlineData(SensorKind.Voltage, 1.256, "1.26")]
    [InlineData(SensorKind.Data, 9.6, "9.6")]
    [InlineData(SensorKind.Data, 99.94, "99.9")]
    [InlineData(SensorKind.Data, 100, "100")]
    [InlineData(SensorKind.Data, 150.4, "150")]
    [InlineData(SensorKind.Load, 37.6, "38")]
    [InlineData(SensorKind.Power, 120.6, "121")]
    [InlineData(SensorKind.Fan, 1234.4, "1234")]
    public void Compact_forms_are_numbers_only(SensorKind kind, double value, string expected) =>
        Assert.Equal(expected, Units.Compact(kind, value));

    [Fact]
    public void TempShort_is_Short_for_temperatures()
    {
        Assert.Equal("46°", Units.TempShort(46.2));
        Assert.Equal("—", Units.TempShort(null));
        Assert.Equal("—", Units.TempShort(double.NaN));
    }

    [Fact]
    public void Celsius_is_the_default_and_values_pass_through()
    {
        Assert.Equal("°C", Units.TempUnit);
        Assert.Equal(46.5, Units.Temp(46.5));
    }

    [Theory]
    [InlineData(0, 32)]
    [InlineData(100, 212)]
    [InlineData(-40, -40)]
    [InlineData(37, 98.6)]
    public void Fahrenheit_converts_temperatures(double celsius, double fahrenheit)
    {
        Units.Fahrenheit = true;
        Assert.Equal(fahrenheit, Units.Temp(celsius), 9);
        Assert.Equal("°F", Units.TempUnit);
    }

    [Fact]
    public void Fahrenheit_changes_every_temperature_form_and_nothing_else()
    {
        Units.Fahrenheit = true;
        Assert.Equal("122.0 °F", Units.Format(SensorKind.Temperature, 50));
        Assert.Equal("122°", Units.Short(SensorKind.Temperature, 50));
        Assert.Equal("122°", Units.Compact(SensorKind.Temperature, 50));
        Assert.Equal("122°", Units.TempShort(50));
        Assert.Equal("50.0 %", Units.Format(SensorKind.Load, 50));
        Assert.Equal("50 W", Units.Short(SensorKind.Power, 50));
        Assert.Equal("—", Units.Format(SensorKind.Temperature, null));

        Units.Fahrenheit = false;
        Assert.Equal("50.0 °C", Units.Format(SensorKind.Temperature, 50));
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(30, "30s")]
    [InlineData(59.9, "59s")]
    [InlineData(60, "1m")]
    [InlineData(119, "1m")]
    [InlineData(45 * 60, "45m")]
    [InlineData(3599, "59m")]
    [InlineData(3599.9, "59m")]
    [InlineData(3600, "1h 00m")]
    [InlineData(4 * 3600 + 5 * 60, "4h 05m")]
    [InlineData(10 * 3600 + 59 * 60 + 59, "10h 59m")]
    [InlineData(25 * 3600, "25h 00m")]
    [InlineData(3 * 86400 + 90, "72h 01m")]
    [InlineData(1e9, "277777h 46m")]
    [InlineData(-1, "0s")]
    [InlineData(-1e12, "0s")]
    [InlineData(double.NaN, "0s")]
    [InlineData(double.NegativeInfinity, "0s")]
    public void Duration(double seconds, string expected) => Assert.Equal(expected, Units.Duration(seconds));

    [Fact]
    public void Duration_of_infinity_does_not_throw() =>
        Assert.False(string.IsNullOrEmpty(Units.Duration(double.PositiveInfinity)));

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "2 KB")]
    [InlineData(1048575, "1024 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741823, "1024 MB")]
    [InlineData(1073741824, "1.0 GB")]
    [InlineData(1.5 * 1073741824, "1.5 GB")]
    [InlineData(1099511627775, "1024.0 GB")]
    [InlineData(1099511627776, "1.00 TB")]
    [InlineData(2.5 * 1099511627776, "2.50 TB")]
    [InlineData(1125899906842624, "1024.00 TB")]
    [InlineData(-5, "-5 B")]
    public void Bytes_picks_the_largest_unit_under_1024(double bytes, string expected) => Assert.Equal(expected, Units.Bytes(bytes));

    [Theory]
    [InlineData(0, "0 MB")]
    [InlineData(512, "512 MB")]
    [InlineData(1023.4, "1023 MB")]
    [InlineData(1023.6, "1024 MB")]
    [InlineData(1024, "1.0 GB")]
    [InlineData(16384, "16.0 GB")]
    [InlineData(1536, "1.5 GB")]
    [InlineData(1048576, "1024.0 GB")]
    public void Megabytes(double mb, string expected) => Assert.Equal(expected, Units.Megabytes(mb));

    [Fact]
    public void Numbers_follow_the_current_culture()
    {
        using var _ = new CultureScope("de-DE");
        Assert.Equal("46,5 °C", Units.Format(SensorKind.Temperature, 46.5));
        Assert.Equal("4,65 GHz", Units.Format(SensorKind.Clock, 4650));
        Assert.Equal("1,5 GB", Units.Bytes(1.5 * 1073741824));
    }

    [Fact]
    public void Integer_forms_are_the_same_in_every_culture()
    {
        foreach (var culture in new[] { "en-US", "de-DE", "fr-FR", "ja-JP", "ar-SA", "hi-IN" })
        {
            using var _ = new CultureScope(culture);
            Assert.Equal("47°", Units.Short(SensorKind.Temperature, 46.6));
            Assert.Equal("37%", Units.Short(SensorKind.Load, 37.4));
            Assert.Equal("1h 00m", Units.Duration(3600));
        }
    }
}
