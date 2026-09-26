using System.Text.RegularExpressions;

namespace Rigsight.Core;

/// <summary>
/// Names of the handful of sensors Rigsight tracks over time, and the rules for finding them
/// among the ~150 sensors a PC exposes (names differ between AMD, Intel and NVIDIA).
/// </summary>
public static class KeySensors
{
    public const string CpuTemp = "cpuTemp";
    public const string CpuDieTemp = "cpuDieTemp";
    public const string CpuLoad = "cpuLoad";
    public const string CpuPower = "cpuPower";
    public const string CpuClock = "cpuClock";
    public const string CpuVoltage = "cpuVoltage";
    public const string GpuTemp = "gpuTemp";
    public const string GpuHotSpot = "gpuHotSpot";
    public const string GpuMemJunction = "gpuMemJunction";
    public const string GpuLoad = "gpuLoad";
    public const string GpuPower = "gpuPower";
    public const string GpuClock = "gpuClock";
    public const string GpuFan = "gpuFan";
    public const string GpuVoltage = "gpuVoltage";
    public const string GpuVramLoad = "gpuVramLoad";
    public const string GpuVramUsed = "gpuVramUsed";
    public const string GpuVramTotal = "gpuVramTotal";
    public const string RamLoad = "ramLoad";
    public const string RamUsed = "ramUsed";
    public const string RamAvailable = "ramAvailable";

    /// <summary>Keys whose recent history is sent to the app when it connects.</summary>
    public static readonly string[] HistoryKeys = [CpuTemp, GpuTemp, GpuHotSpot, GpuMemJunction, CpuLoad, GpuLoad, RamLoad];

    /// <param name="Hardware">Which device it belongs to (two identical cards share a name); -1: tell them apart by name.</param>
    public readonly record struct Candidate(int Index, string HardwareType, string HardwareName, string Name, SensorKind Kind, int Hardware = -1);

    /// <summary>A graphics processor and its key sensors (<see cref="GpuKeys"/> to indexes into the flat sensor list).</summary>
    public sealed record Gpu(string Name, string Type, bool Integrated, Dictionary<string, int> Keys);

    /// <summary>
    /// A graphics adapter as Windows lists it for games (high-performance first): its name and the hardware type of its
    /// maker ("GpuNvidia", "GpuAmd", "GpuIntel"; "" for another).
    /// </summary>
    public sealed record PreferredGpu(string Name, string Type);

    /// <summary>The keys every graphics processor has its own of.</summary>
    public static readonly string[] GpuKeys =
        [GpuTemp, GpuHotSpot, GpuMemJunction, GpuLoad, GpuPower, GpuClock, GpuFan, GpuVoltage, GpuVramLoad, GpuVramUsed, GpuVramTotal];

    /// <summary>Maps key names to indexes into the flat sensor list. The GPU keys are the main graphics card's (see <see cref="Gpus"/>).</summary>
    public static Dictionary<string, int> Pick(IReadOnlyList<Candidate> all, IReadOnlyList<PreferredGpu>? preferred = null)
    {
        var keys = new Dictionary<string, int>();
        bool IsCpu(Candidate c) => c.HardwareType == "Cpu";
        // "Virtual Memory" (RAM plus the page file) uses the same sensor names as the real RAM: skip it.
        bool IsRam(Candidate c) => c.HardwareType == "Memory" && !c.HardwareName.Contains("Virtual", StringComparison.OrdinalIgnoreCase);

        Add(keys, all, CpuTemp, IsCpu, SensorKind.Temperature, "Core (Tctl/Tdie)", "CPU Package", "Core (Tctl)", "Core (Tdie)", "Core Average", "Core Max");
        if (!keys.ContainsKey(CpuTemp))
        {
            var any = all.FirstOrDefault(c => IsCpu(c) && c.Kind == SensorKind.Temperature);
            if (any.Name is not null) keys[CpuTemp] = any.Index;
        }
        Add(keys, all, CpuDieTemp, IsCpu, SensorKind.Temperature, "CCD1 (Tdie)", "CCDs Max (Tdie)", "Core Max");
        if (keys.TryGetValue(CpuDieTemp, out var die) && keys.TryGetValue(CpuTemp, out var main) && die == main)
            keys.Remove(CpuDieTemp);
        Add(keys, all, CpuLoad, IsCpu, SensorKind.Load, "CPU Total");
        Add(keys, all, CpuPower, IsCpu, SensorKind.Power, "Package", "CPU Package", "Core (SMU)");
        Add(keys, all, CpuClock, IsCpu, SensorKind.Clock, "Cores (Average)", "Core #1", "CPU Core #1");
        Add(keys, all, CpuVoltage, IsCpu, SensorKind.Voltage, "Core (SVI2 TFN)", "Core (SVI3 TFN)", "CPU Core", "Core #1 VID", "Core VID");

        if (Gpus(all, preferred).FirstOrDefault() is { } gpu)
            foreach (var (key, index) in gpu.Keys) keys[key] = index;

        Add(keys, all, RamLoad, IsRam, SensorKind.Load, "Memory");
        Add(keys, all, RamUsed, IsRam, SensorKind.Data, "Memory Used");
        Add(keys, all, RamAvailable, IsRam, SensorKind.Data, "Memory Available");
        return keys;
    }

    /// <summary>
    /// Every graphics processor with its key sensors, the main one first. The order is Windows' own when
    /// <paramref name="preferred"/> is given (the adapters as Windows offers them to games, high-performance first, see
    /// the agent's GpuPreference): the GPU it gives games is the main one, whatever its maker. GPUs it doesn't name, or
    /// all of them without it: a dedicated card before integrated graphics (a laptop's, or a desktop processor's, which is
    /// often listed first and would otherwise hide the card), then NVIDIA, AMD, Intel; identical cards (SLI, CrossFire)
    /// in the order they're listed.
    /// </summary>
    public static List<Gpu> Gpus(IReadOnlyList<Candidate> all, IReadOnlyList<PreferredGpu>? preferred = null)
    {
        var found = all
            .Where(c => c.HardwareType is "GpuNvidia" or "GpuAmd" or "GpuIntel")
            .GroupBy(c => c.Hardware >= 0 ? $"#{c.Hardware}" : $"{c.HardwareType}|{c.HardwareName}")
            .Select((group, order) =>
            {
                var sensors = group.ToList();
                var first = sensors[0];
                var keys = new Dictionary<string, int>();
                static bool Any(Candidate _) => true;
                Add(keys, sensors, GpuTemp, Any, SensorKind.Temperature, "GPU Core");
                if (!keys.ContainsKey(GpuTemp) && sensors.FirstOrDefault(c => c.Kind == SensorKind.Temperature) is { Name: not null } t) keys[GpuTemp] = t.Index;
                Add(keys, sensors, GpuHotSpot, Any, SensorKind.Temperature, "GPU Hot Spot");
                Add(keys, sensors, GpuMemJunction, Any, SensorKind.Temperature, "GPU Memory Junction", "GPU Memory");
                Add(keys, sensors, GpuLoad, Any, SensorKind.Load, "GPU Core", "D3D 3D");
                Add(keys, sensors, GpuPower, Any, SensorKind.Power, "GPU Package", "GPU Power", "GPU Core");
                Add(keys, sensors, GpuClock, Any, SensorKind.Clock, "GPU Core");
                Add(keys, sensors, GpuVoltage, Any, SensorKind.Voltage, "GPU Core Voltage", "GPU Core");
                Add(keys, sensors, GpuVramLoad, Any, SensorKind.Load, "GPU Memory");
                Add(keys, sensors, GpuVramUsed, Any, SensorKind.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
                Add(keys, sensors, GpuVramTotal, Any, SensorKind.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");
                if (sensors.FirstOrDefault(c => c.Kind == SensorKind.Fan) is { Name: not null } fan) keys[GpuFan] = fan.Index;
                return (Gpu: new Gpu(first.HardwareName, first.HardwareType, LooksIntegrated(first.HardwareType, first.HardwareName), keys), Order: order);
            });
        var gpus = found.ToList();
        var place = WindowsOrder(gpus.Select(g => g.Gpu).ToList(), preferred ?? []);
        return [.. gpus.OrderBy(g => place[g.Order]).ThenByDescending(g => Rank(g.Gpu)).ThenBy(g => g.Order).Select(g => g.Gpu)];
    }

    /// <summary>
    /// Where each GPU comes in Windows' list (int.MaxValue: not in it). An adapter is matched by name (ignoring ®, ™,
    /// "(R)", "(TM)", case and spacing), else by maker when that maker has just one GPU left; each GPU once, so two
    /// identical cards match two adapters in turn.
    /// </summary>
    private static int[] WindowsOrder(List<Gpu> gpus, IReadOnlyList<PreferredGpu> preferred)
    {
        var place = Enumerable.Repeat(int.MaxValue, gpus.Count).ToArray();
        for (int p = 0; p < preferred.Count; p++)
        {
            var want = preferred[p];
            string name = Plain(want.Name);
            int match = Enumerable.Range(0, gpus.Count).FirstOrDefault(i => place[i] == int.MaxValue && Plain(gpus[i].Name) == name, -1);
            if (match < 0)
            {
                var sameMaker = Enumerable.Range(0, gpus.Count).Where(i => place[i] == int.MaxValue && gpus[i].Type == want.Type && want.Type != "").ToList();
                if (sameMaker.Count == 1) match = sameMaker[0];
            }
            if (match >= 0) place[match] = p;
        }
        return place;
    }

    private static string Plain(string name) =>
        Regex.Replace(Regex.Replace(name, @"\((R|TM|C)\)|[®™©]", "", RegexOptions.IgnoreCase), @"\s+", " ").Trim().ToLowerInvariant();

    private static int Rank(Gpu g) => (g.Integrated ? 0 : 10) + g.Type switch { "GpuNvidia" => 3, "GpuAmd" => 2, _ => 1 };

    /// <summary>
    /// Integrated graphics, by name: Intel's, except Arc cards ("Arc A770", "Arc B580", "Arc Pro"; the "Intel Arc
    /// Graphics" inside Core Ultra processors is integrated), and AMD's inside Ryzen processors ("AMD Radeon(TM)
    /// Graphics", "Radeon 780M Graphics", "Radeon 680M", "Radeon Vega 8 Graphics"). Cards are "Radeon RX ...", "Radeon Pro ...".
    /// </summary>
    public static bool LooksIntegrated(string type, string name) => type switch
    {
        "GpuIntel" => !Regex.IsMatch(name, @"\bArc\s*(\(TM\)|\u2122)?\s*(Pro\b|[AB]\d{3})", RegexOptions.IgnoreCase),
        "GpuAmd" => !Regex.IsMatch(name, @"\b(RX|Pro|FirePro|VII)\b", RegexOptions.IgnoreCase)
            && Regex.IsMatch(name, @"Radeon\s*(\(TM\)|\u2122)?\s*(\d{3,4}M\b|(Vega\s*\d*\s+)?Graphics)|Vega\s*\d+", RegexOptions.IgnoreCase),
        _ => false,
    };

    /// <summary>The first sensor of <paramref name="kind"/> named one of <paramref name="names"/> (in that order of preference).</summary>
    private static void Add(Dictionary<string, int> keys, IReadOnlyList<Candidate> all, string key, Func<Candidate, bool> hw, SensorKind kind, params string[] names)
    {
        foreach (var n in names)
        {
            var match = all.FirstOrDefault(c => hw(c) && c.Kind == kind && c.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (match.Name is not null)
            {
                keys[key] = match.Index;
                return;
            }
        }
    }
}
