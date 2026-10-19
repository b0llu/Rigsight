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
            SensorKind.Data => DataGb(v),
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
            SensorKind.Data when v >= 1000 => DataGb(v), // a value read as GB would be off by a thousand
            SensorKind.Data when v < 100 => $"{v:0.0}", // "↓ 9.6 ↑ 10.2" GB, not "↓ 10 ↑ 10"
            _ => $"{v:0}",
        };
    }

    /// <summary>A Data reading (decimal GB, as the drives report it): TB from 1,000 GB, so "97.8 TB" and not "97771.0 GB".</summary>
    public static string DataGb(double gb) => Math.Abs(gb) >= 1000 ? $"{gb / 1000:0.0} TB" : $"{gb:0.0} GB";

    public static string TempShort(double? celsius) => Short(SensorKind.Temperature, celsius);

    /// <summary>
    /// A reading as short as it can be and still say the number: at most about three characters, since a tray icon is
    /// 16 px square at normal scaling. Units are left out (the tooltip has them): 62 (°), 45 (%), 1.2k (RPM), 4.4 (GHz).
    /// </summary>
    public static string TrayText(SensorKind kind, double? value)
    {
        if (value is not double v || !double.IsFinite(v)) return "–";
        string Whole(double x) => Math.Round(x).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        string OneDecimal(double x) => x.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        string Short(double x) => Math.Abs(x) < 10 ? OneDecimal(x) : Whole(x);
        return kind switch
        {
            SensorKind.Temperature => Whole(Temp(v)),
            SensorKind.Load or SensorKind.Control or SensorKind.Level or SensorKind.Humidity or SensorKind.Power => Whole(v),
            SensorKind.Fan or SensorKind.Flow => v >= 1000 ? OneDecimal(v / 1000) + "k" : Whole(v),
            SensorKind.Clock => v >= 1000 ? OneDecimal(v / 1000) : Whole(v),    // MHz; GHz from 1000
            SensorKind.Voltage => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            SensorKind.SmallData => Short(v >= 1024 ? v / 1024 : v),             // MB; GB from 1024
            SensorKind.Throughput => Short(v / (1024 * 1024)),                   // bytes/s as MB/s
            _ => Short(v),
        };
    }

    /// <summary>
    /// A reading as the taskbar strip shows it: <see cref="TrayText"/> with a short unit, since the strip has the room
    /// ("48°", "7%", "88 W", "4.5 GHz", "1.2k rpm").
    /// </summary>
    public static string StripText(SensorKind kind, double? value)
    {
        string text = TrayText(kind, value);
        if (value is not double v || !double.IsFinite(v)) return text;
        return kind switch
        {
            SensorKind.Temperature => text + "°",
            SensorKind.Load or SensorKind.Control or SensorKind.Level or SensorKind.Humidity => text + "%",
            SensorKind.Power => text + " W",
            SensorKind.Fan => text + " rpm",
            SensorKind.Flow => text + " L/h",
            SensorKind.Clock => text + (v >= 1000 ? " GHz" : " MHz"),
            SensorKind.Voltage => text + " V",
            SensorKind.Current => text + " A",
            SensorKind.SmallData => text + (v >= 1024 ? " GB" : " MB"),
            SensorKind.Data => v >= 1000 ? TrayText(kind, v / 1000) + " TB" : text + " GB",
            SensorKind.Throughput => text + " MB/s",
            _ => text,
        };
    }

    public static string TypeLabel(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => Lang.T("TEMP"),
        SensorKind.Voltage => Lang.T("VOLT"),
        SensorKind.Current => Lang.T("AMPS"),
        SensorKind.Control => Lang.T("CTRL"),
        SensorKind.Data or SensorKind.SmallData => Lang.T("DATA"),
        SensorKind.Throughput => Lang.T("RATE"),
        SensorKind.Factor => Lang.T("INFO"),
        SensorKind.Power => Lang.T("POWER"),
        SensorKind.Clock => Lang.T("CLOCK"),
        SensorKind.Load => Lang.T("LOAD"),
        SensorKind.Fan => Lang.T("FAN"),
        SensorKind.Flow => Lang.T("FLOW"),
        SensorKind.Level => Lang.T("LEVEL"),
        SensorKind.Frequency => Lang.T("FREQUENCY"),
        SensorKind.Energy => Lang.T("ENERGY"),
        SensorKind.Noise => Lang.T("NOISE"),
        SensorKind.Humidity => Lang.T("HUMIDITY"),
        SensorKind.Timing => Lang.T("TIMING"),
        _ => kind.ToString().ToUpperInvariant(),
    };

    /// <summary>"4h 05m", "45m", "30s".</summary>
    public static string Duration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        int hours = (int)t.TotalHours, minutes = t.Minutes, secs = t.Seconds;
        if (t.TotalHours >= 1) return Lang.T($"{hours}h {minutes:00}m");
        if (t.TotalMinutes >= 1) return Lang.T($"{minutes}m");
        return Lang.T($"{secs}s");
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

    /// <summary>
    /// Data moved, as Windows counts it (1 GB = 1,024 MB): "640 MB", "3.4 GB", "118 GB" (a decimal only where it says
    /// something), "1.25 TB".
    /// </summary>
    public static string Data(double bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (1L << 40):0.00} TB",
        >= 10L << 30 => $"{bytes / (1L << 30):0} GB",
        >= 1L << 30 => $"{bytes / (1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (1L << 20):0} MB",
        >= 1L << 10 => $"{bytes / (1L << 10):0} KB",
        _ => $"{bytes:0} B",
    };

    /// <summary>A speed in bytes a second: "11.8 MB/s", "312 KB/s".</summary>
    public static string Speed(double bytesPerSec) => bytesPerSec switch
    {
        >= 1L << 30 => $"{bytesPerSec / (1L << 30):0.0} GB/s",
        >= 1L << 20 => $"{bytesPerSec / (1L << 20):0.0} MB/s",
        >= 1L << 10 => $"{bytesPerSec / (1L << 10):0} KB/s",
        _ => $"{bytesPerSec:0} B/s",
    };

    /// <summary>A speed as internet plans state it, in megabits a second: "99 Mbps", "1.2 Gbps".</summary>
    public static string Mbps(double bytesPerSec)
    {
        double mbps = bytesPerSec * 8 / 1_000_000;
        return mbps >= 1000 ? $"{mbps / 1000:0.0} Gbps" : mbps >= 10 ? $"{mbps:0} Mbps" : $"{mbps:0.0} Mbps";
    }

    /// <summary>A temperature difference in the user's unit, rounded ("5°"), its sign dropped.</summary>
    public static string Degrees(double celsiusDiff) => $"{Math.Round(Math.Abs(celsiusDiff) * (Fahrenheit ? 9.0 / 5 : 1)):0}°";

    /// <summary>The difference between two temperatures as they're shown (each rounded), so "3° hotter (63° vs 60°)" adds up.</summary>
    public static string ShownDiff(double a, double b)
    {
        static double Shown(double c) => Math.Round(Fahrenheit ? c * 9 / 5 + 32 : c);
        return $"{Math.Abs(Shown(a) - Shown(b)):0}°";
    }
}
