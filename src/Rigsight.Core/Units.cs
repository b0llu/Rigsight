namespace Rigsight.Core;

/// <summary>Sensor kinds (mirrors LibreHardwareMonitor's SensorType so the app doesn't need that library).</summary>
public enum SensorKind
{
    Voltage, Current, Power, Clock, Temperature, Load, Frequency, Fan, Flow, Control, Level,
    Factor, Data, SmallData, Throughput, TimeSpan, Timing, Energy, Noise, Conductivity, Humidity,
}

/// <summary>Formatting of raw values. Temperatures are always stored in °C and converted for display.</summary>
public static class Units
{
    public static bool Fahrenheit { get; set; }

    public static string TempUnit => Fahrenheit ? "°F" : "°C";

    public static double Temp(double celsius) => Fahrenheit ? celsius * 9 / 5 + 32 : celsius;

    public static string Format(SensorKind kind, double? value)
    {
        if (value is not double v || double.IsNaN(v))
            return "—";

        return kind switch
        {
            SensorKind.Temperature => $"{Temp(v):0.0} {TempUnit}",
            SensorKind.Load or SensorKind.Control or SensorKind.Level or SensorKind.Humidity => $"{v:0.0} %",
            SensorKind.Clock => v >= 1000 ? $"{v / 1000:0.00} GHz" : $"{v:0} MHz",
            SensorKind.Power => $"{v:0.0} W",
            SensorKind.Voltage => $"{v:0.000} V",
            SensorKind.Current => $"{v:0.00} A",
            SensorKind.Fan => $"{v:0} RPM",
            SensorKind.Flow => $"{v:0.0} L/h",
            SensorKind.Data => $"{v:0.0} GB",
            SensorKind.SmallData => $"{v:0} MB",
            SensorKind.Throughput => Rate(v),
            SensorKind.Frequency => $"{v:0} Hz",
            SensorKind.Energy => $"{v:0} mWh",
            SensorKind.Noise => $"{v:0} dBA",
            SensorKind.Timing => $"{v:0.00} ns",
            _ => $"{v:0.##}",
        };
    }

    /// <summary>Compact form, e.g. "46°" for temperatures or "37%" for load.</summary>
    public static string Short(SensorKind kind, double? value)
    {
        if (value is not double v || double.IsNaN(v))
            return "—";

        return kind switch
        {
            SensorKind.Temperature => $"{Temp(v):0}°",
            SensorKind.Load or SensorKind.Control or SensorKind.Level => $"{v:0}%",
            SensorKind.Power => $"{v:0} W",
            _ => Format(kind, v),
        };
    }

    /// <summary>Number only, for tight spaces next to a value that already shows the unit.</summary>
    public static string Compact(SensorKind kind, double? value)
    {
        if (value is not double v || double.IsNaN(v))
            return "—";

        return kind switch
        {
            SensorKind.Temperature => $"{Temp(v):0}°",
            SensorKind.Clock when v >= 1000 => $"{v / 1000:0.00}",
            SensorKind.Voltage => $"{v:0.00}",
            _ => $"{v:0}",
        };
    }

    public static string TempShort(double? celsius) => Short(SensorKind.Temperature, celsius);

    public static string TypeLabel(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "TEMP",
        SensorKind.Voltage => "VOLT",
        SensorKind.Current => "AMPS",
        SensorKind.Control => "CTRL",
        SensorKind.Data or SensorKind.SmallData => "DATA",
        SensorKind.Throughput => "RATE",
        SensorKind.Factor => "INFO",
        _ => kind.ToString().ToUpperInvariant(),
    };

    /// <summary>"4h 05m", "45m", "30s".</summary>
    public static string Duration(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m";
        return $"{t.Seconds}s";
    }

    public static string Bytes(double bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (1L << 40):0.00} TB",
        >= 1L << 30 => $"{bytes / (1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (1L << 20):0} MB",
        >= 1L << 10 => $"{bytes / (1L << 10):0} KB",
        _ => $"{bytes:0} B",
    };

    public static string Megabytes(double mb) => mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";

    private static string Rate(double bytesPerSec) => Bytes(bytesPerSec) + "/s";
}
