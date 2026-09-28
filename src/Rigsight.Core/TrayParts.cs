namespace Rigsight.Core;

/// <summary>The part of the PC a taskbar reading is about, which its colour mark shows.</summary>
public enum TrayPart { Cpu, Gpu, Ram, Drive, Board, Network, Other }

/// <summary>
/// What a taskbar reading is about, and how readings are grouped when they share icons. Every icon carries a colour
/// mark for its part (the colours Rigsight uses everywhere: CPU blue, GPU green, RAM purple), so a bare number in the
/// taskbar still says whose it is. Grouped, each icon holds one part only (a GPU or drive each, being separate
/// devices), up to two readings stacked, the temperature on top. Shared by the agent, which draws the icons, and the
/// Taskbar page, which previews them.
/// </summary>
public static class TrayParts
{
    /// <summary>Readings an icon holds at most, stacked (a third wouldn't be legible at 16 pixels).</summary>
    public const int PerIcon = 2;

    /// <summary>The part, from LibreHardwareMonitor's hardware type ("Cpu", "GpuNvidia", "SuperIO"…).</summary>
    public static TrayPart PartOf(string? hardwareType) => hardwareType switch
    {
        "Cpu" => TrayPart.Cpu,
        "GpuNvidia" or "GpuAmd" or "GpuIntel" => TrayPart.Gpu,
        "Memory" => TrayPart.Ram,
        "Storage" => TrayPart.Drive,
        "Motherboard" or "SuperIO" or "EmbeddedController" => TrayPart.Board,
        "Network" => TrayPart.Network,
        _ => TrayPart.Other,
    };

    public static string Label(TrayPart part) => part switch
    {
        TrayPart.Cpu => "CPU",
        TrayPart.Gpu => "GPU",
        TrayPart.Ram => "RAM",
        TrayPart.Drive => "Drive",
        TrayPart.Board => "Board",
        TrayPart.Network => "Network",
        _ => "Other",
    };

    /// <summary>
    /// The part's colour (red, green, blue), as on every Rigsight page, a shade deeper on a light taskbar so it holds
    /// its own there; other parts are grey (drives too: a blue would pass for a cool temperature's colour).
    /// </summary>
    public static (byte R, byte G, byte B) Color(TrayPart part, bool light = false) => part switch
    {
        TrayPart.Cpu => light ? ((byte)59, (byte)110, (byte)235) : ((byte)91, (byte)140, (byte)255),
        TrayPart.Gpu => light ? ((byte)16, (byte)165, (byte)105) : ((byte)61, (byte)220, (byte)151),
        TrayPart.Ram => light ? ((byte)140, (byte)95, (byte)230) : ((byte)177, (byte)140, (byte)255),
        _ => light ? ((byte)128, (byte)128, (byte)128) : ((byte)148, (byte)148, (byte)148),
    };

    /// <summary>
    /// The device a sensor belongs to, from its ID ("/gpu-nvidia/0/temperature/0" → "/gpu-nvidia/0"): two GPUs or
    /// drives are grouped apart.
    /// </summary>
    public static string DeviceOf(string sensorId)
    {
        int last = sensorId.LastIndexOf('/');
        int before = last > 0 ? sensorId.LastIndexOf('/', last - 1) : -1;
        return before > 0 ? sensorId[..before] : sensorId;
    }

    /// <summary>
    /// What groups readings: the part, and for GPUs, drives and the rest also the device (a laptop's two GPUs are two
    /// icons). The CPU, RAM and board are one each (RAM's total and its sticks' temperatures come as separate devices).
    /// </summary>
    public static string GroupOf(TrayPart part, string sensorId) =>
        part is TrayPart.Cpu or TrayPart.Ram or TrayPart.Board ? part.ToString() : $"{part}:{DeviceOf(sensorId)}";

    /// <summary>
    /// Readings grouped into icons: by part (see <see cref="GroupOf"/>) in the order each part first appears, within
    /// one the temperature first, then the load, then the rest as listed, <paramref name="perIcon"/> to an icon (the
    /// strip has room for all of a part's).
    /// </summary>
    public static List<List<T>> Group<T>(IReadOnlyList<T> readings, Func<T, (string Group, SensorKind Kind)> describe, int perIcon = PerIcon)
    {
        var groups = new List<(string Key, List<T> Items)>();
        foreach (var r in readings)
        {
            string key = describe(r).Group;
            int i = groups.FindIndex(g => g.Key == key);
            if (i < 0) groups.Add((key, [r]));
            else groups[i].Items.Add(r);
        }
        var icons = new List<List<T>>();
        foreach (var (_, items) in groups)
        {
            var ordered = items.Select((r, i) => (r, i)).OrderBy(x => Rank(describe(x.r).Kind)).ThenBy(x => x.i).Select(x => x.r).ToList();
            for (int i = 0; i < ordered.Count; i += perIcon) icons.Add(ordered.Skip(i).Take(perIcon).ToList());
        }
        return icons;
    }

    /// <summary>
    /// The names under the strip's groups: the part's, numbered where it comes more than once ("GPU 1", "GPU 2": a
    /// laptop's two GPUs).
    /// </summary>
    public static List<string> Names(IReadOnlyList<TrayPart> parts)
    {
        var names = new List<string>(parts.Count);
        var seen = new Dictionary<TrayPart, int>();
        foreach (var part in parts)
        {
            int n = seen.GetValueOrDefault(part) + 1;
            seen[part] = n;
            bool several = parts.Count(p => p == part) > 1;
            names.Add(several ? $"{Label(part)} {n}" : Label(part));
        }
        return names;
    }

    /// <summary>A name given on All sensors is used under a reading on the strip when it's at most this long.</summary>
    public const int MaxShortLabel = 10;

    /// <summary>
    /// The word under a reading on the strip (after its part's first, which has the part's name, see
    /// <see cref="StripLabels"/>). The number's unit already says what sort of reading it is, so the word tells
    /// readings of one part apart: "Load", "Clock", "Power", the GPU's "Hot" spot and "VRAM". A short name the user
    /// gave the sensor wins.
    /// </summary>
    public static string ShortLabel(SensorKind kind, string sensorName, TrayPart part, string? custom = null)
    {
        if (custom?.Trim() is { Length: > 0 and <= MaxShortLabel } own) return own;
        string n = sensorName.ToLowerInvariant();
        bool Has(string s) => n.Contains(s, StringComparison.Ordinal);
        switch (kind)
        {
            case SensorKind.Temperature:
                if (Has("hot spot") || Has("hotspot")) return "Hot";
                if (part == TrayPart.Gpu && Has("memory")) return "VRAM";
                if (System.Text.RegularExpressions.Regex.Match(sensorName, @"CCD\s*#?(\d+)") is { Success: true } ccd) return "CCD" + ccd.Groups[1].Value;
                return part switch { TrayPart.Ram => "DIMM", TrayPart.Drive => "Drive", _ => "Temp" };
            case SensorKind.Load:
                if (part == TrayPart.Gpu && Has("memory")) return "VRAM";
                if (Has("video")) return "Video";
                if (part == TrayPart.Ram) return Has("virtual") ? "Virtual" : "Used";
                return "Load";
            case SensorKind.Power: return "Power";
            case SensorKind.Clock: return Has("memory") ? "Mem" : "Clock";
            case SensorKind.Fan or SensorKind.Control: return "Fan";
            case SensorKind.Voltage: return "Volt";
            case SensorKind.Current: return "Amps";
            case SensorKind.Data or SensorKind.SmallData: return Has("available") || Has("free") ? "Free" : "Used";
            case SensorKind.Throughput: return Has("upload") ? "Up" : Has("download") ? "Down" : "Rate";
            case SensorKind.Flow: return "Flow";
        }
        string first = sensorName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first.Length <= 6 ? first : first[..6];
    }

    /// <summary>
    /// The words under a part's readings on the strip: the part's name under the first (so the group is named),
    /// each reading's own under the rest.
    /// </summary>
    public static List<string> StripLabels(IReadOnlyList<string> shortLabels, string partName) =>
        [.. shortLabels.Select((label, i) => i == 0 ? partName : label)];

    private static int Rank(SensorKind kind) => kind switch { SensorKind.Temperature => 0, SensorKind.Load => 1, _ => 2 };
}
