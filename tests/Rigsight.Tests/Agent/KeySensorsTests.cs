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
        int index = 0, device = 0;
        foreach (var hw in hardware)
        {
            foreach (var s in hw.Sensors)
                list.Add(new Candidate(index++, hw.Type, hw.Name, s.Name, s.Kind, device));
            device++;
        }
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

        /// <summary>The adapters as Windows would list them for games (none: ranked by name).</summary>
        public List<PreferredGpu> Windows { get; } = [];

        public Pc Prefers(string type, string name)
        {
            Windows.Add(new PreferredGpu(name, type));
            return this;
        }

        public Dictionary<string, int> Pick() => KeySensors.Pick(CandidatesOf(Hardware), Windows);

        public List<Gpu> Gpus() => KeySensors.Gpus(CandidatesOf(Hardware), Windows);

        /// <summary>The device the sensor picked for a key belongs to ("#2 AMD Radeon(TM) Graphics").</summary>
        public string? DeviceOf(string key)
        {
            if (!Pick().TryGetValue(key, out int i)) return null;
            var all = Hardware.SelectMany((h, n) => h.Sensors.Select(_ => $"#{n} {h.Name}")).ToList();
            return all[i];
        }

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

    // ── Several graphics processors ──

    private static readonly (SensorKind, string)[] NvidiaCard =
        [(SensorKind.Temperature, "GPU Core"), (SensorKind.Temperature, "GPU Hot Spot"), (SensorKind.Load, "GPU Core"), (SensorKind.Power, "GPU Package"),
         (SensorKind.Clock, "GPU Core"), (SensorKind.Fan, "GPU Fan"), (SensorKind.Load, "GPU Memory"), (SensorKind.SmallData, "GPU Memory Used"), (SensorKind.SmallData, "GPU Memory Total")];

    private static readonly (SensorKind, string)[] AmdGpu =
        [(SensorKind.Temperature, "GPU Core"), (SensorKind.Temperature, "GPU Memory"), (SensorKind.Load, "GPU Core"), (SensorKind.Power, "GPU Package"),
         (SensorKind.Clock, "GPU Core"), (SensorKind.Fan, "GPU Fan"), (SensorKind.SmallData, "D3D Dedicated Memory Used"), (SensorKind.SmallData, "D3D Dedicated Memory Total")];

    private static readonly (SensorKind, string)[] IntelGpu =
        [(SensorKind.Temperature, "GPU Core"), (SensorKind.Load, "D3D 3D"), (SensorKind.Power, "GPU Power"), (SensorKind.SmallData, "D3D Dedicated Memory Used")];

    [Fact]
    public void A_card_wins_over_the_processors_amd_graphics_listed_before_it()
    {
        // The user's PC: a Ryzen with graphics (listed first) and a dedicated card.
        var pc = new Pc()
            .Add("Cpu", "AMD Ryzen 7 7700X", (SensorKind.Temperature, "Core (Tctl/Tdie)"))
            .Add("GpuAmd", "AMD Radeon(TM) Graphics", AmdGpu)
            .Add("GpuNvidia", "NVIDIA GeForce RTX 4070", NvidiaCard);
        Assert.All(new[] { GpuTemp, GpuLoad, GpuPower, GpuClock, GpuFan, GpuVramUsed, GpuVramTotal }, key => Assert.Equal("#2 NVIDIA GeForce RTX 4070", pc.DeviceOf(key)));
        Assert.Equal(["NVIDIA GeForce RTX 4070", "AMD Radeon(TM) Graphics"], pc.Gpus().Select(g => g.Name));
        Assert.True(pc.Gpus()[1].Integrated);
    }

    [Theory]
    [InlineData("GpuIntel", "Intel(R) UHD Graphics 770", "GpuAmd", "AMD Radeon RX 7800 XT")]
    [InlineData("GpuAmd", "AMD Radeon(TM) 780M Graphics", "GpuAmd", "AMD Radeon RX 7600S")]
    [InlineData("GpuAmd", "AMD Radeon(TM) Vega 8 Graphics", "GpuNvidia", "NVIDIA GeForce GTX 1650")]
    [InlineData("GpuIntel", "Intel(R) Iris(R) Xe Graphics", "GpuIntel", "Intel(R) Arc(TM) A770 Graphics")]
    [InlineData("GpuAmd", "AMD Radeon 890M Graphics", "GpuAmd", "AMD Radeon Pro W7600")]
    public void The_dedicated_card_is_the_main_gpu_whichever_is_listed_first(string igpuType, string igpu, string cardType, string card)
    {
        var sensors = (string type) => type switch { "GpuNvidia" => NvidiaCard, "GpuAmd" => AmdGpu, _ => IntelGpu };
        var pc = new Pc().Add(igpuType, igpu, sensors(igpuType)).Add(cardType, card, sensors(cardType));
        Assert.Equal($"#1 {card}", pc.DeviceOf(GpuTemp));
        Assert.Equal([card, igpu], pc.Gpus().Select(g => g.Name));
        Assert.Equal([false, true], pc.Gpus().Select(g => g.Integrated));
    }

    [Fact]
    public void Two_identical_cards_are_two_gpus_in_the_order_theyre_listed()
    {
        var pc = new Pc()
            .Add("GpuNvidia", "NVIDIA GeForce RTX 3090", NvidiaCard)
            .Add("GpuNvidia", "NVIDIA GeForce RTX 3090", NvidiaCard);
        var gpus = pc.Gpus();
        Assert.Equal(2, gpus.Count);
        Assert.Equal("#0 NVIDIA GeForce RTX 3090", pc.DeviceOf(GpuTemp));
        Assert.NotEqual(gpus[0].Keys[GpuTemp], gpus[1].Keys[GpuTemp]);
        Assert.Equal(gpus[1].Keys[GpuTemp], gpus[0].Keys[GpuTemp] + NvidiaCard.Length);
    }

    [Fact]
    public void Only_integrated_graphics_are_still_the_gpu()
    {
        var pc = new Pc().Add("Cpu", "AMD Ryzen 5 5600G", (SensorKind.Temperature, "Core (Tctl/Tdie)")).Add("GpuAmd", "AMD Radeon(TM) Graphics", AmdGpu);
        Assert.Equal("#1 AMD Radeon(TM) Graphics", pc.DeviceOf(GpuTemp));
        Assert.Single(pc.Gpus());
    }

    [Fact]
    public void Each_gpu_has_its_own_readings()
    {
        var pc = new Pc().Add("GpuAmd", "AMD Radeon(TM) Graphics", AmdGpu).Add("GpuNvidia", "NVIDIA GeForce RTX 4070", NvidiaCard);
        var (card, igpu) = (pc.Gpus()[0], pc.Gpus()[1]);
        Assert.Contains(GpuHotSpot, card.Keys.Keys);
        Assert.DoesNotContain(GpuHotSpot, igpu.Keys.Keys);           // integrated graphics have no hot spot sensor
        Assert.Contains(GpuMemJunction, igpu.Keys.Keys);             // "GPU Memory" temperature
        Assert.True(igpu.Keys.Values.All(i => i < AmdGpu.Length));  // all its own sensors
        Assert.True(card.Keys.Values.All(i => i >= AmdGpu.Length));
    }

    [Fact]
    public void The_captured_pc_has_one_gpu()
    {
        var gpus = KeySensors.Gpus(CandidatesOf(Fixtures.Hello().Hardware!));
        Assert.Equal("NVIDIA GeForce RTX 3080 Ti", Assert.Single(gpus).Name);
    }

    [Theory]
    [InlineData("GpuAmd", "AMD Radeon(TM) Graphics", true)]
    [InlineData("GpuAmd", "AMD Radeon(TM) 780M Graphics", true)]
    [InlineData("GpuAmd", "AMD Radeon 890M Graphics", true)]
    [InlineData("GpuAmd", "AMD Radeon(TM) Vega 8 Graphics", true)]
    [InlineData("GpuAmd", "Radeon Vega 11", true)]
    [InlineData("GpuAmd", "AMD Radeon RX 7900 XTX", false)]
    [InlineData("GpuAmd", "AMD Radeon RX Vega 64", false)]
    [InlineData("GpuAmd", "AMD Radeon Pro W7900", false)]
    [InlineData("GpuAmd", "AMD Radeon VII", false)]
    [InlineData("GpuIntel", "Intel(R) UHD Graphics 630", true)]
    [InlineData("GpuIntel", "Intel(R) Iris(R) Xe Graphics", true)]
    [InlineData("GpuIntel", "Intel(R) Arc(TM) A770 Graphics", false)]
    [InlineData("GpuIntel", "Intel(R) Arc(TM) B580 Graphics", false)]
    [InlineData("GpuIntel", "Intel(R) Arc(TM) Pro A60 Graphics", false)]
    [InlineData("GpuIntel", "Intel(R) Arc(TM) Graphics", true)]    // inside Core Ultra processors
    [InlineData("GpuIntel", "Intel(R) Arc(TM) 140V GPU", true)]    // Lunar Lake's
    [InlineData("GpuAmd", "AMD Radeon 680M", true)]
    [InlineData("GpuAmd", "AMD Radeon(TM) 610M", true)]
    [InlineData("GpuNvidia", "NVIDIA GeForce RTX 4090", false)]
    [InlineData("GpuNvidia", "NVIDIA GeForce MX450", false)]
    public void Integrated_graphics_are_told_from_cards_by_name(string type, string name, bool integrated) =>
        Assert.Equal(integrated, KeySensors.LooksIntegrated(type, name));

    // ── Windows' choice ─────────────────────────────────────────────────

    [Fact]
    public void The_gpu_windows_gives_games_is_the_main_one_whatever_its_maker()
    {
        // Two cards: by name NVIDIA would win, but Windows is set to give games the AMD card.
        var pc = new Pc().Add("GpuNvidia", "NVIDIA GeForce RTX 3060", NvidiaCard).Add("GpuAmd", "AMD Radeon RX 7900 XTX", AmdGpu)
            .Prefers("GpuAmd", "AMD Radeon RX 7900 XTX").Prefers("GpuNvidia", "NVIDIA GeForce RTX 3060");
        Assert.Equal(["AMD Radeon RX 7900 XTX", "NVIDIA GeForce RTX 3060"], pc.Gpus().Select(g => g.Name));
        Assert.Equal("#1 AMD Radeon RX 7900 XTX", pc.DeviceOf(GpuTemp));
    }

    [Fact]
    public void Windows_can_even_put_integrated_graphics_first()
    {
        // Set to power saving for everything: Windows' first is the main GPU, as games will run there.
        var pc = new Pc().Add("GpuIntel", "Intel(R) Iris(R) Xe Graphics", IntelGpu).Add("GpuNvidia", "NVIDIA GeForce RTX 4060 Laptop GPU", NvidiaCard)
            .Prefers("GpuIntel", "Intel(R) Iris(R) Xe Graphics").Prefers("GpuNvidia", "NVIDIA GeForce RTX 4060 Laptop GPU");
        Assert.Equal("Intel(R) Iris(R) Xe Graphics", pc.Gpus()[0].Name);
        Assert.True(pc.Gpus()[0].Integrated); // still named for what it is
    }

    [Fact]
    public void Names_match_without_trademark_signs_case_or_spacing()
    {
        var pc = new Pc().Add("GpuNvidia", "NVIDIA GeForce RTX 3060", NvidiaCard).Add("GpuAmd", "AMD Radeon(TM) RX 7900 XTX", AmdGpu)
            .Prefers("GpuAmd", "amd radeon™  rx 7900 xtx");
        Assert.Equal("AMD Radeon(TM) RX 7900 XTX", pc.Gpus()[0].Name);
    }

    [Fact]
    public void An_adapter_named_differently_matches_the_only_gpu_of_its_maker()
    {
        var pc = new Pc().Add("GpuNvidia", "NVIDIA GeForce RTX 3060", NvidiaCard).Add("GpuAmd", "AMD Radeon RX 7900 XTX", AmdGpu)
            .Prefers("GpuAmd", "Radeon RX 7900 XTX (driver name)");
        Assert.Equal("AMD Radeon RX 7900 XTX", pc.Gpus()[0].Name);
    }

    [Fact]
    public void Two_gpus_of_the_same_maker_need_the_name_to_match()
    {
        // Two AMD GPUs and a name that fits neither: no guessing which, the names decide (card before integrated).
        var pc = new Pc().Add("GpuAmd", "AMD Radeon(TM) Graphics", AmdGpu).Add("GpuAmd", "AMD Radeon RX 7800 XT", AmdGpu)
            .Prefers("GpuAmd", "Some other AMD name");
        Assert.Equal(["AMD Radeon RX 7800 XT", "AMD Radeon(TM) Graphics"], pc.Gpus().Select(g => g.Name));
    }

    [Fact]
    public void Identical_cards_take_windows_places_in_turn()
    {
        var pc = new Pc().Add("GpuNvidia", "NVIDIA GeForce RTX 3090", NvidiaCard).Add("GpuNvidia", "NVIDIA GeForce RTX 3090", NvidiaCard)
            .Prefers("GpuNvidia", "NVIDIA GeForce RTX 3090").Prefers("GpuNvidia", "NVIDIA GeForce RTX 3090");
        Assert.Equal(2, pc.Gpus().Count);
        Assert.Equal("#0 NVIDIA GeForce RTX 3090", pc.DeviceOf(GpuTemp));
    }

    [Fact]
    public void Gpus_windows_doesnt_list_come_after_by_name()
    {
        // Windows names only the card (the processor's graphics off in the BIOS, say, but still seen by the sensors).
        var pc = new Pc().Add("GpuAmd", "AMD Radeon(TM) Graphics", AmdGpu).Add("GpuIntel", "Intel(R) Arc(TM) A770 Graphics", IntelGpu)
            .Add("GpuNvidia", "NVIDIA GeForce RTX 4070", NvidiaCard).Prefers("GpuIntel", "Intel(R) Arc(TM) A770 Graphics");
        Assert.Equal(["Intel(R) Arc(TM) A770 Graphics", "NVIDIA GeForce RTX 4070", "AMD Radeon(TM) Graphics"], pc.Gpus().Select(g => g.Name));
    }

    [Fact]
    public void Adapters_without_sensors_change_nothing()
    {
        // A remote-desktop or virtual adapter first in Windows' list: no GPU of that name or maker here.
        var pc = new Pc().Add("GpuAmd", "AMD Radeon(TM) Graphics", AmdGpu).Add("GpuNvidia", "NVIDIA GeForce RTX 4070", NvidiaCard)
            .Prefers("", "Parsec Virtual Display Adapter");
        Assert.Equal("NVIDIA GeForce RTX 4070", pc.Gpus()[0].Name);
    }

    [Fact]
    public void Windows_lists_this_pcs_adapters_high_performance_first()
    {
        // On the PC running the tests: every adapter it has, none a software one, each named, the first a real GPU.
        var list = Rigsight.Agent.Sensors.GpuPreference.Read();
        Assert.SkipWhen(list.Count == 0, "no graphics adapter Windows can rank (a VM?)");
        Assert.All(list, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
        Assert.DoesNotContain(list, a => a.Name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual("", list[0].Type);
        Assert.Equal(list.Count, Rigsight.Agent.Sensors.GpuPreference.Read().Count); // asked again: the same
    }

    [Theory]
    [InlineData(0x10DEu, "GpuNvidia")]
    [InlineData(0x1002u, "GpuAmd")]
    [InlineData(0x8086u, "GpuIntel")]
    [InlineData(0x1414u, "")] // Microsoft's software adapter
    public void Adapters_makers_by_vendor_id(uint vendor, string type) =>
        Assert.Equal(type, Rigsight.Agent.Sensors.GpuPreference.Maker(vendor));
}
