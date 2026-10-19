using System.Text.RegularExpressions;

namespace Rigsight.Core;

/// <summary>
/// A sensor's own name, as the hardware library gives it in English ("GPU Hot Spot", "Fan #2", "Core #3 (SMU)"), in the
/// language in use, for showing only: ids, settings and every comparison keep the English. Names it doesn't know (and
/// names people gave sensors themselves) show as they are; with no pack in use, every name comes back unchanged.
/// </summary>
/// <remarks>Each is a "sensor" text in the pack (‹sensor›…), so "Memory" as a sensor can read differently from elsewhere.</remarks>
public static partial class SensorWords
{
    [GeneratedRegex(@"^(?<a>.+?) #(?<n>\d+)(?<b>.*)$")]
    private static partial Regex Numbered();

    [GeneratedRegex(@"^CPU Core #(?<n>\d+) Thread #(?<t>\d+)$")]
    private static partial Regex Thread();

    [GeneratedRegex(@"^GPU Fan (?<n>\d+)$")]
    private static partial Regex GpuFan();

    [GeneratedRegex(@"^Temperature (?<n>\d+)$")]
    private static partial Regex DriveTemp();

    public static string Show(string? name)
    {
        if (string.IsNullOrEmpty(name) || !Lang.Translating) return name ?? "";
        if (Exact(name) is { } exact) return exact;

        if (Thread().Match(name) is { Success: true } t)
        {
            string core = t.Groups["n"].Value, thread = t.Groups["t"].Value;
            return Lang.C("sensor", $"CPU Core #{core} Thread #{thread}");
        }
        if (GpuFan().Match(name) is { Success: true } g)
        {
            string n = g.Groups["n"].Value;
            return Lang.C("sensor", $"GPU Fan {n}");
        }
        if (DriveTemp().Match(name) is { Success: true } d)
        {
            string n = d.Groups["n"].Value;
            return Lang.C("sensor", $"Temperature {n}");
        }
        if (Numbered().Match(name) is { Success: true } m)
        {
            string n = m.Groups["n"].Value;
            string? said = (m.Groups["a"].Value, m.Groups["b"].Value) switch
            {
                ("Fan", "") => Lang.C("sensor", $"Fan #{n}"),
                ("System Fan", "") => Lang.C("sensor", $"System Fan #{n}"),
                ("Temperature", "") => Lang.C("sensor", $"Temperature #{n}"),
                ("Voltage", "") => Lang.C("sensor", $"Voltage #{n}"),
                ("Core", "") => Lang.C("sensor", $"Core #{n}"),
                ("CPU Core", "") => Lang.C("sensor", $"CPU Core #{n}"),
                ("Core", " (SMU)") => Lang.C("sensor", $"Core #{n} (SMU)"),
                ("Core", " (Effective)") => Lang.C("sensor", $"Core #{n} (Effective)"),
                ("Core", " VID") => Lang.C("sensor", $"Core #{n} VID"),
                ("CPU Core", " Clock") => Lang.C("sensor", $"CPU Core #{n} Clock"),
                _ => null,
            };
            if (said is not null) return said;
        }
        return name;
    }

    /// <summary>A name the library gives as it is, in the language in use; null when it isn't one of them.</summary>
    private static string? Exact(string name) => name switch
    {
        // Processor
        "CPU Total" => Lang.C("sensor", "CPU Total"),
        "CPU Core Max" => Lang.C("sensor", "CPU Core Max"),
        "Core (Tctl/Tdie)" => Lang.C("sensor", "Core (Tctl/Tdie)"),
        "Core (Tctl)" => Lang.C("sensor", "Core (Tctl)"),
        "Core (Tdie)" => Lang.C("sensor", "Core (Tdie)"),
        "Package" => Lang.C("sensor", "Package"),
        "Core Max" => Lang.C("sensor", "Core Max"),
        "Core Average" => Lang.C("sensor", "Core Average"),
        "Cores (Average)" => Lang.C("sensor", "Cores (Average)"),
        "Cores (Average Effective)" => Lang.C("sensor", "Cores (Average Effective)"),
        "Bus Speed" => Lang.C("sensor", "Bus Speed"),
        "Core (SVI2 TFN)" => Lang.C("sensor", "Core (SVI2 TFN)"),
        "CPU Cores" => Lang.C("sensor", "CPU Cores"),
        "CPU Memory" => Lang.C("sensor", "CPU Memory"),
        "CPU Graphics" => Lang.C("sensor", "CPU Graphics"),
        "CPU System Agent" => Lang.C("sensor", "CPU System Agent"),
        "Core Voltage" => Lang.C("sensor", "Core Voltage"),
        "CCDs Max (Tdie)" => Lang.C("sensor", "CCDs Max (Tdie)"),
        "CCDs Average (Tdie)" => Lang.C("sensor", "CCDs Average (Tdie)"),
        // Graphics
        "GPU Core" => Lang.C("sensor", "GPU Core"),
        "GPU Memory" => Lang.C("sensor", "GPU Memory"),
        "GPU Hot Spot" => Lang.C("sensor", "GPU Hot Spot"),
        "GPU Memory Junction" => Lang.C("sensor", "GPU Memory Junction"),
        "GPU Package" => Lang.C("sensor", "GPU Package"),
        "GPU Power" => Lang.C("sensor", "GPU Power"),
        "GPU Board Power" => Lang.C("sensor", "GPU Board Power"),
        "GPU Core Voltage" => Lang.C("sensor", "GPU Core Voltage"),
        "GPU Memory Controller" => Lang.C("sensor", "GPU Memory Controller"),
        "GPU Video Engine" => Lang.C("sensor", "GPU Video Engine"),
        "GPU Bus" => Lang.C("sensor", "GPU Bus"),
        "GPU Memory Free" => Lang.C("sensor", "GPU Memory Free"),
        "GPU Memory Used" => Lang.C("sensor", "GPU Memory Used"),
        "GPU Memory Total" => Lang.C("sensor", "GPU Memory Total"),
        "GPU PCIe Rx" => Lang.C("sensor", "GPU PCIe Rx"),
        "GPU PCIe Tx" => Lang.C("sensor", "GPU PCIe Tx"),
        "GPU Shader" => Lang.C("sensor", "GPU Shader"),
        "GPU SoC" => Lang.C("sensor", "GPU SoC"),
        "GPU Fan" => Lang.C("sensor", "GPU Fan"),
        "D3D Dedicated Memory Used" => Lang.C("sensor", "D3D Dedicated Memory Used"),
        "D3D Shared Memory Used" => Lang.C("sensor", "D3D Shared Memory Used"),
        "D3D Video Decode" => Lang.C("sensor", "D3D Video Decode"),
        "D3D Video Encode" => Lang.C("sensor", "D3D Video Encode"),
        "D3D Copy" => Lang.C("sensor", "D3D Copy"),
        // Memory
        "Memory" => Lang.C("sensor", "Memory"),
        "Memory Used" => Lang.C("sensor", "Memory Used"),
        "Memory Available" => Lang.C("sensor", "Memory Available"),
        "Virtual Memory" => Lang.C("sensor", "Virtual Memory"),
        "Total Memory" => Lang.C("sensor", "Total Memory"),
        "Virtual Memory Used" => Lang.C("sensor", "Virtual Memory Used"),
        "Virtual Memory Available" => Lang.C("sensor", "Virtual Memory Available"),
        // Drives
        "Temperature" => Lang.C("sensor", "Temperature"),
        "Composite Temperature" => Lang.C("sensor", "Composite Temperature"),
        "Warning Temperature" => Lang.C("sensor", "Warning Temperature"),
        "Critical Temperature" => Lang.C("sensor", "Critical Temperature"),
        "Used Space" => Lang.C("sensor", "Used Space"),
        "Free Space" => Lang.C("sensor", "Free Space"),
        "Total Space" => Lang.C("sensor", "Total Space"),
        "Capacity" => Lang.C("sensor", "Capacity"),
        "Data Read" => Lang.C("sensor", "Data Read"),
        "Data Written" => Lang.C("sensor", "Data Written"),
        "Read Rate" => Lang.C("sensor", "Read Rate"),
        "Write Rate" => Lang.C("sensor", "Write Rate"),
        "Read Activity" => Lang.C("sensor", "Read Activity"),
        "Write Activity" => Lang.C("sensor", "Write Activity"),
        "Total Activity" => Lang.C("sensor", "Total Activity"),
        "Available Spare" => Lang.C("sensor", "Available Spare"),
        "Available Spare Threshold" => Lang.C("sensor", "Available Spare Threshold"),
        "Percentage Used" => Lang.C("sensor", "Percentage Used"),
        "Power On Count" => Lang.C("sensor", "Power On Count"),
        "Power On Hours" => Lang.C("sensor", "Power On Hours"),
        "Life" => Lang.C("sensor", "Life"),
        "Remaining Life" => Lang.C("sensor", "Remaining Life"),
        // Motherboard
        "+3V Standby" => Lang.C("sensor", "+3V Standby"),
        "CMOS Battery" => Lang.C("sensor", "CMOS Battery"),
        "CPU Fan" => Lang.C("sensor", "CPU Fan"),
        "Pump" => Lang.C("sensor", "Pump"),
        "System" => Lang.C("sensor", "System"),
        // Network
        "Data Uploaded" => Lang.C("sensor", "Data Uploaded"),
        "Data Downloaded" => Lang.C("sensor", "Data Downloaded"),
        "Upload Speed" => Lang.C("sensor", "Upload Speed"),
        "Download Speed" => Lang.C("sensor", "Download Speed"),
        "Network Utilization" => Lang.C("sensor", "Network Utilization"),
        _ => null,
    };
}
