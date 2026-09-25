using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Tests.Support;
using static Rigsight.Core.KeySensors;

namespace Rigsight.Tests.Agent;

/// <summary>Finding the handful of key sensors among the ~150 a PC has (names differ between AMD, Intel and NVIDIA).</summary>
public class KeySensorsTests
{
    /// <summary>Candidates numbered exactly as SensorHost numbers them: hardware in order, sensors in order.</summary>
    private static List<Candidate> CandidatesOf(IEnumerable<HardwareMeta> hardware)
    {
        var list = new List<Candidate>();
        int index = 0;
        foreach (var hw in hardware)
            foreach (var s in hw.Sensors)
                list.Add(new Candidate(index++, hw.Type, hw.Name, s.Name, s.Kind));
        return list;
    }

    private sealed class Pc
    {
        public List<HardwareMeta> Hardware { get; } = [];

        public Pc Add(string type, string name, params (SensorKind Kind, string Name)[] sensors)
        {
            var hw = new HardwareMeta { Type = type, Name = name };
            foreach (var (kind, sensor) in sensors)
                hw.Sensors.Add(new SensorMeta { Id = $"/{type}/{Hardware.Count}/{hw.Sensors.Count}", Name = sensor, Kind = kind });
            Hardware.Add(hw);
            return this;
        }

        public Dictionary<string, int> Pick() => KeySensors.Pick(CandidatesOf(Hardware));

        /// <summary>The name of the sensor picked for a key (null: none).</summary>
        public string? Named(string key)
        {
            if (!Pick().TryGetValue(key, out int i)) return null;
            var all = Hardware.SelectMany(h => h.Sensors.Select(s => (h, s))).ToList();
            return $"{all[i].h.Type}:{all[i].s.Name}:{all[i].s.Kind}";
        }
    }

    private static readonly (SensorKind, string)[] Ram =
        [(SensorKind.Load, "Memory"), (SensorKind.Data, "Memory Used"), (SensorKind.Data, "Memory Available")];

    [Fact]
    public void The_captured_Ryzen_and_RTX_PC_picks_the_same_keys_as_the_real_agent()
    {
        var hello = Fixtures.Hello();
        var keys = KeySensors.Pick(CandidatesOf(hello.Hardware!));
        Assert.Equal(hello.Keys!.OrderBy(k => k.Key), keys.OrderBy(k => k.Key));
    }

    [Fact]
    public void The_captured_PC_has_every_key()
    {
        var keys = KeySensors.Pick(CandidatesOf(Fixtures.Hello().Hardware!));
        string[] all = [CpuTemp, CpuDieTemp, CpuLoad, CpuPower, CpuClock, CpuVoltage, GpuTemp, GpuHotSpot, GpuMemJunction, GpuLoad,
            GpuPower, GpuClock, GpuFan, GpuVoltage, GpuVramLoad, GpuVramUsed, GpuVramTotal, RamLoad, RamUsed, RamAvailable];
        Assert.Equal(all.Order(), keys.Keys.Order());
        Assert.All(HistoryKeys, k => Assert.Contains(k, keys.Keys));
    }

    [Fact]
    public void Each_captured_key_points_at_a_sensor_of_the_right_kind()
    {
        var hello = Fixtures.Hello();
        var sensors = hello.Hardware!.SelectMany(h => h.Sensors.Select(s => (Hw: h, S: s))).ToList();
        var keys = KeySensors.Pick(CandidatesOf(hello.Hardware!));
        Assert.Equal(SensorKind.Temperature, sensors[keys[CpuTemp]].S.Kind);
        Assert.Equal("Core (Tctl/Tdie)", sensors[keys[CpuTemp]].S.Name);
        Assert.Equal("CCD1 (Tdie)", sensors[keys[CpuDieTemp]].S.Name);
        Assert.Equal(SensorKind.Load, sensors[keys[GpuLoad]].S.Kind);
        Assert.Equal(SensorKind.Clock, sensors[keys[GpuClock]].S.Kind);
        Assert.Equal("GPU Core", sensors[keys[GpuClock]].S.Name);
        Assert.Equal("Total Memory", sensors[keys[RamLoad]].Hw.Name);
        Assert.Equal("Total Memory", sensors[keys[RamUsed]].Hw.Name);
        Assert.Equal("Total Memory", sensors[keys[RamAvailable]].Hw.Name);
    }

    [Fact]
    public void Virtual_memory_never_counts_as_RAM()
    {
        // Listed first, with the same sensor names as the real RAM.
        var pc = new Pc().Add("Memory", "Virtual Memory", Ram).Add("Memory", "Total Memory", Ram);
        Assert.Equal("Memory:Memory:Load", pc.Named(RamLoad));
        Assert.Equal(3, pc.Pick()[RamLoad]);
        Assert.Equal(4, pc.Pick()[RamUsed]);
        Assert.Equal(5, pc.Pick()[RamAvailable]);

        var onlyVirtual = new Pc().Add("Memory", "virtual memory", Ram);
        Assert.Empty(onlyVirtual.Pick());
    }

    [Fact]
    public void An_Intel_CPU_with_an_AMD_GPU()
    {
        var pc = new Pc()
            .Add("Cpu", "Intel Core i7-13700K",
                (SensorKind.Temperature, "Core #1"), (SensorKind.Temperature, "Core Max"), (SensorKind.Temperature, "CPU Package"),
                (SensorKind.Temperature, "Core Average"), (SensorKind.Load, "CPU Core #1"), (SensorKind.Load, "CPU Total"),
                (SensorKind.Clock, "Bus Speed"), (SensorKind.Clock, "P-Core #1"), (SensorKind.Clock, "Core #1"),
                (SensorKind.Power, "CPU Cores"), (SensorKind.Power, "CPU Package"), (SensorKind.Voltage, "CPU Core"), (SensorKind.Voltage, "Core #1 VID"))
            .Add("GpuAmd", "AMD Radeon RX 7900 XTX",
                (SensorKind.Temperature, "GPU Core"), (SensorKind.Temperature, "GPU Hot Spot"), (SensorKind.Temperature, "GPU Memory"),
                (SensorKind.Load, "GPU Core"), (SensorKind.Load, "GPU Memory"), (SensorKind.Clock, "GPU Core"), (SensorKind.Clock, "GPU Memory"),
                (SensorKind.Power, "GPU Package"), (SensorKind.Voltage, "GPU Core"), (SensorKind.Fan, "GPU Fan"),
                (SensorKind.SmallData, "GPU Memory Used"), (SensorKind.SmallData, "GPU Memory Total"))
            .Add("Memory", "Total Memory", Ram);

        Assert.Equal("Cpu:CPU Package:Temperature", pc.Named(CpuTemp));
        Assert.Equal("Cpu:Core Max:Temperature", pc.Named(CpuDieTemp));
        Assert.Equal("Cpu:CPU Total:Load", pc.Named(CpuLoad));
        Assert.Equal("Cpu:CPU Package:Power", pc.Named(CpuPower));
        Assert.Equal("Cpu:Core #1:Clock", pc.Named(CpuClock));
        Assert.Equal("Cpu:CPU Core:Voltage", pc.Named(CpuVoltage));
        Assert.Equal("GpuAmd:GPU Core:Temperature", pc.Named(GpuTemp));
        Assert.Equal("GpuAmd:GPU Hot Spot:Temperature", pc.Named(GpuHotSpot));
        Assert.Equal("GpuAmd:GPU Memory:Temperature", pc.Named(GpuMemJunction));
        Assert.Equal("GpuAmd:GPU Core:Load", pc.Named(GpuLoad));
        Assert.Equal("GpuAmd:GPU Package:Power", pc.Named(GpuPower));
        Assert.Equal("GpuAmd:GPU Core:Clock", pc.Named(GpuClock));
        Assert.Equal("GpuAmd:GPU Core:Voltage", pc.Named(GpuVoltage));
        Assert.Equal("GpuAmd:GPU Memory:Load", pc.Named(GpuVramLoad));
        Assert.Equal("GpuAmd:GPU Memory Used:SmallData", pc.Named(GpuVramUsed));
        Assert.Equal("GpuAmd:GPU Memory Total:SmallData", pc.Named(GpuVramTotal));
        Assert.Equal("GpuAmd:GPU Fan:Fan", pc.Named(GpuFan));
        Assert.Equal("Memory:Memory:Load", pc.Named(RamLoad));
    }

    [Fact]
    public void An_Intel_integrated_GPU_when_its_the_only_one()
    {
        var pc = new Pc()
            .Add("Cpu", "Intel Core i5-1235U", (SensorKind.Temperature, "CPU Package"), (SensorKind.Load, "CPU Total"))
            .Add("GpuIntel", "Intel Iris Xe Graphics",
                (SensorKind.Load, "D3D 3D"), (SensorKind.Load, "D3D Video Decode"), (SensorKind.Power, "GPU Power"),
                (SensorKind.SmallData, "D3D Dedicated Memory Used"), (SensorKind.SmallData, "D3D Shared Memory Used"))
            .Add("Memory", "Total Memory", Ram);

        Assert.Equal("GpuIntel:D3D 3D:Load", pc.Named(GpuLoad));
        Assert.Equal("GpuIntel:GPU Power:Power", pc.Named(GpuPower));
        Assert.Equal("GpuIntel:D3D Dedicated Memory Used:SmallData", pc.Named(GpuVramUsed));
        Assert.Null(pc.Named(GpuTemp));        // integrated graphics report no temperature
        Assert.Null(pc.Named(GpuVramTotal));
        Assert.Null(pc.Named(GpuFan));
        Assert.Null(pc.Named(CpuDieTemp));
    }

    [Fact]
    public void A_discrete_GPU_wins_over_the_integrated_one_listed_first()
    {
        var pc = new Pc()
            .Add("GpuIntel", "Intel UHD Graphics 770", (SensorKind.Load, "D3D 3D"), (SensorKind.Temperature, "GPU Core"))
            .Add("GpuNvidia", "NVIDIA GeForce RTX 4070", (SensorKind.Temperature, "GPU Core"), (SensorKind.Load, "GPU Core"), (SensorKind.Load, "D3D 3D"));
        Assert.Equal(2, pc.Pick()[GpuTemp]);
        Assert.Equal(3, pc.Pick()[GpuLoad]);
    }

    [Fact]
    public void A_laptop_without_a_discrete_GPU_or_temperatures()
    {
        // Without admin rights (or on some laptops) the CPU reports loads but no temperatures.
        var pc = new Pc()
            .Add("Cpu", "AMD Ryzen 5 7530U", (SensorKind.Load, "CPU Total"), (SensorKind.Clock, "Cores (Average)"))
            .Add("GpuAmd", "AMD Radeon Graphics", (SensorKind.Load, "D3D 3D"))
            .Add("Battery", "Battery", (SensorKind.Level, "Charge Level"))
            .Add("Memory", "Total Memory", Ram);
        var keys = pc.Pick();
        Assert.False(keys.ContainsKey(CpuTemp));
        Assert.False(keys.ContainsKey(GpuTemp));
        Assert.Equal("GpuAmd:D3D 3D:Load", pc.Named(GpuLoad));
        Assert.Equal("Cpu:Cores (Average):Clock", pc.Named(CpuClock));
        Assert.Equal([CpuClock, CpuLoad, GpuLoad, RamAvailable, RamLoad, RamUsed], keys.Keys.Order());
    }

    [Fact]
    public void A_CPU_temperature_of_another_name_is_better_than_none()
    {
        var pc = new Pc().Add("Cpu", "Some CPU", (SensorKind.Load, "CPU Total"), (SensorKind.Temperature, "Tdie"), (SensorKind.Temperature, "Other"));
        Assert.Equal("Cpu:Tdie:Temperature", pc.Named(CpuTemp));
    }

    [Fact]
    public void A_GPU_temperature_of_another_name_is_better_than_none()
    {
        var pc = new Pc().Add("GpuNvidia", "Some GPU", (SensorKind.Temperature, "GPU Die"), (SensorKind.Temperature, "GPU Hot Spot"));
        Assert.Equal("GpuNvidia:GPU Die:Temperature", pc.Named(GpuTemp));
        Assert.Equal("GpuNvidia:GPU Hot Spot:Temperature", pc.Named(GpuHotSpot));
    }

    [Fact]
    public void The_die_temperature_is_dropped_when_it_is_the_main_one()
    {
        var pc = new Pc().Add("Cpu", "Old CPU", (SensorKind.Temperature, "Core Max"));
        Assert.Equal("Cpu:Core Max:Temperature", pc.Named(CpuTemp));
        Assert.False(pc.Pick().ContainsKey(CpuDieTemp));
    }

    [Fact]
    public void Earlier_names_in_the_preference_list_win_over_earlier_sensors()
    {
        var pc = new Pc().Add("Cpu", "AMD", (SensorKind.Temperature, "Core (Tdie)"), (SensorKind.Temperature, "Core (Tctl)"),
            (SensorKind.Temperature, "Core (Tctl/Tdie)"));
        Assert.Equal("Cpu:Core (Tctl/Tdie):Temperature", pc.Named(CpuTemp));
    }

    [Fact]
    public void Names_match_whatever_their_case_but_kinds_must_match()
    {
        var pc = new Pc().Add("Cpu", "CPU", (SensorKind.Load, "cpu total"), (SensorKind.Temperature, "CPU TOTAL"), (SensorKind.Power, "package"));
        Assert.Equal(0, pc.Pick()[CpuLoad]);
        Assert.Equal(2, pc.Pick()[CpuPower]);
        Assert.Equal(1, pc.Pick()[CpuTemp]); // only as "any CPU temperature"
    }

    [Fact]
    public void Sensors_of_other_hardware_never_count()
    {
        // A motherboard chip with CPU-like sensor names.
        var pc = new Pc().Add("SuperIO", "ITE IT8686E", (SensorKind.Temperature, "CPU Package"), (SensorKind.Temperature, "GPU Core"),
            (SensorKind.Load, "CPU Total"), (SensorKind.Load, "Memory"));
        Assert.Empty(pc.Pick());
    }

    [Fact]
    public void Nothing_to_pick_from()
    {
        Assert.Empty(KeySensors.Pick([]));
    }

    [Fact]
    public void Two_discrete_GPUs_use_the_first()
    {
        var pc = new Pc()
            .Add("GpuNvidia", "RTX A", (SensorKind.Temperature, "GPU Core"))
            .Add("GpuAmd", "RX B", (SensorKind.Temperature, "GPU Core"));
        Assert.Equal(0, pc.Pick()[GpuTemp]);
    }
}
